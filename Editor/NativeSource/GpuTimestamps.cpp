#include <d3d11.h>
#include <wrl/client.h>
#include <array>
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <vector>
#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
#include "IntervalUnion.h"

using Microsoft::WRL::ComPtr;
struct Sample {
    int64_t sequence, gpuNs, shadowNs, cameraNs, textureNs;
    int ticket, gpuBlocks, shadowBlocks, cameraBlocks, valid, dropped, flags, textureBlocks;
};
static_assert(sizeof(Sample) == 72 && offsetof(Sample, ticket) == 40 && offsetof(Sample, flags) == 64,
    "Sample must match NativeGpuSample's sequential x64 layout.");
struct Span {
    ComPtr<ID3D11Query> begin, end;
    bool started = false, ended = false;
    int id = 0;
    bool shadow = false;
    void Clear(int key, bool isShadow) { started = ended = false; id = key; shadow = isShadow; }
};
struct Slot {
    ComPtr<ID3D11Query> disjoint;
    std::vector<Span> spans;
    Sample sample{};
    int spanCount = 0;
    bool pending = false, invalid = false;
};
static std::mutex gate;
static IUnityInterfaces* interfaces = nullptr;
static IUnityGraphics* graphics = nullptr;
static ComPtr<ID3D11Device> device;
static ComPtr<ID3D11DeviceContext> context;
static std::array<Slot, 64> slots;
static std::array<Sample, 512> results;
static Slot* active = nullptr;
static int cursor = 0, head = 0, count = 0, dropped = 0;
static int64_t sequence = 0;
static int64_t discardThrough = 0;
static std::vector<bool> cameraScopes;

static bool EnsureDevice() {
    if (device && context) return true;
    if (!graphics || graphics->GetRenderer() != kUnityGfxRendererD3D11) return false;
    auto api = interfaces->Get<IUnityGraphicsD3D11>();
    if (!api || !api->GetDevice()) return false;
    device = api->GetDevice();
    device->GetImmediateContext(context.GetAddressOf());
    return context != nullptr;
}
static bool Query(ComPtr<ID3D11Query>& query, D3D11_QUERY type) {
    if (query) return true;
    D3D11_QUERY_DESC desc{ type, 0 };
    return SUCCEEDED(device->CreateQuery(&desc, query.GetAddressOf()));
}
static void Begin(Span& span, bool flushBoundary) {
    if (span.started || !Query(span.begin, D3D11_QUERY_TIMESTAMP) || !Query(span.end, D3D11_QUERY_TIMESTAMP)) {
        active->invalid = true; return;
    }
    // Start the draw interval in a fresh submission batch, separate from earlier commands.
    if (flushBoundary) context->Flush();
    span.started = true; context->End(span.begin.Get());
}
static void End(Span& span, bool flushBoundary) {
    if (!span.started || span.ended) { active->invalid = true; return; }
    span.ended = true; context->End(span.end.Get());
    // A queued end timestamp can otherwise include the wait for later CPU work.
    // Flush submits asynchronously; query retrieval still uses DONOTFLUSH without waiting.
    if (flushBoundary) context->Flush();
}
static bool ReadSpan(const Span& span, std::pair<uint64_t, uint64_t>& interval, bool& valid) {
    if (!span.started || !span.ended) { valid = false; return true; }
    uint64_t first = 0, last = 0;
    HRESULT a = context->GetData(span.begin.Get(), &first, sizeof(first), D3D11_ASYNC_GETDATA_DONOTFLUSH);
    HRESULT b = context->GetData(span.end.Get(), &last, sizeof(last), D3D11_ASYNC_GETDATA_DONOTFLUSH);
    if (a == S_FALSE || b == S_FALSE) return false;
    if (a != S_OK || b != S_OK || last < first) { valid = false; return true; }
    interval = { first, last };
    return true;
}

static Span* FindOpen(int id, bool shadow) {
    for (int i = active->spanCount - 1; i >= 0; --i) {
        auto& span = active->spans[i];
        if (span.id == id && span.shadow == shadow && !span.ended) return &span;
    }
    return nullptr;
}
static void BeginInterval(int id, bool shadow) {
    if (FindOpen(id, shadow)) {
        // Multiple possible first-stage markers belong to the same camera rendering.
        if (shadow) active->invalid = true;
        return;
    }
    if (active->spanCount >= 2048) { active->invalid = true; return; }
    if (active->spanCount == static_cast<int>(active->spans.size())) active->spans.emplace_back();
    auto& span = active->spans[active->spanCount++]; span.Clear(id, shadow);
    Begin(span, true);
    ++active->sample.gpuBlocks;
    if (shadow) ++active->sample.shadowBlocks;
    else if (id == 0) ++active->sample.textureBlocks;
    else ++active->sample.cameraBlocks;
}
static void EndInterval(int id, bool shadow) {
    auto span = FindOpen(id, shadow);
    if (!span) { active->invalid = true; return; }
    End(*span, true);
}
static int64_t UnionNanoseconds(const std::vector<std::pair<uint64_t, uint64_t>>& spans, uint64_t frequency) {
    return static_cast<int64_t>((static_cast<long double>(IntervalUnionTicks(spans)) * 1000000000.0L) / frequency);
}
static void Poll() {
    std::array<Sample, 64> completed;
    int ready = 0;
    for (auto& slot : slots) {
        if (!slot.pending) continue;
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT info{};
        HRESULT status = context->GetData(slot.disjoint.Get(), &info, sizeof(info), D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (status == S_FALSE) continue;
        Sample sample = slot.sample;
        sample.valid = status == S_OK && !info.Disjoint && info.Frequency && !slot.invalid;
        if (status != S_OK || info.Disjoint || !info.Frequency) sample.flags |= 1;
        if (slot.invalid) sample.flags |= 2;
        if (sample.valid) {
            std::vector<std::pair<uint64_t, uint64_t>> all, cameras, shadows, textures;
            bool waiting = false, valid = true;
            for (int i = 0; i < slot.spanCount; ++i) {
                std::pair<uint64_t, uint64_t> interval{};
                if (!ReadSpan(slot.spans[i], interval, valid)) { waiting = true; break; }
                all.push_back(interval);
                (slot.spans[i].shadow ? shadows : slot.spans[i].id == 0 ? textures : cameras).push_back(interval);
            }
            if (waiting) continue;
            if (!valid) { sample.valid = 0; sample.flags |= 4; }
            else {
                sample.gpuNs = UnionNanoseconds(all, info.Frequency);
                sample.cameraNs = UnionNanoseconds(cameras, info.Frequency);
                sample.shadowNs = UnionNanoseconds(shadows, info.Frequency);
                sample.textureNs = UnionNanoseconds(textures, info.Frequency);
            }
            if (sample.cameraBlocks < 1)
            { sample.valid = 0; sample.flags |= 2; }
        }
        slot.pending = false;
        completed[ready++] = sample;
    }
    std::sort(completed.begin(), completed.begin() + ready, [](const Sample& a, const Sample& b) { return a.sequence < b.sequence; });
    for (int i = 0; i < ready; ++i) {
        if (completed[i].sequence <= discardThrough) continue;
        if (count == static_cast<int>(results.size())) { head = (head + 1) % results.size(); --count; ++dropped; }
        auto sample = completed[i]; sample.dropped = dropped;
        results[(head + count) % results.size()] = sample; ++count;
    }
}
static void UNITY_INTERFACE_API RenderEvent(int eventId) {
    std::lock_guard<std::mutex> lock(gate);
    try {
        if (!EnsureDevice()) return;
        int kind = eventId & 255;
        // Query completion is checked on the render thread, including after submissions stop.
        if (kind == 11) { Poll(); return; }
        if (kind == 1) {
            Poll();
            if (active) { active->invalid = true; context->End(active->disjoint.Get()); active->pending = true; active = nullptr; }
            int ticket = static_cast<unsigned int>(eventId) >> 8;
            if (ticket == 0) { ++dropped; return; }
            for (int i = 0; i < static_cast<int>(slots.size()); ++i) {
                auto& slot = slots[cursor]; cursor = (cursor + 1) % slots.size();
                if (slot.pending) continue;
                if (!Query(slot.disjoint, D3D11_QUERY_TIMESTAMP_DISJOINT)) { ++dropped; return; }
                slot.sample = {}; slot.sample.sequence = ++sequence; slot.sample.ticket = ticket;
                slot.sample.gpuNs = slot.sample.shadowNs = slot.sample.cameraNs = slot.sample.textureNs = -1;
                slot.spanCount = 0; slot.invalid = false;
                cameraScopes.clear();
                active = &slot; context->Begin(slot.disjoint.Get()); return;
            }
            ++dropped; return;
        }
        if (!active) return;
        int id = static_cast<unsigned int>(eventId) >> 8;
        switch (kind) {
        case 16: cameraScopes.push_back(id != 0); break;
        case 17:
            if (cameraScopes.empty()) active->invalid = true;
            else cameraScopes.pop_back();
            break;
        case 4:
            if (!cameraScopes.empty() && cameraScopes.back()) BeginInterval(id, false);
            break;
        case 5:
            if (FindOpen(id, false)) EndInterval(id, false);
            break;
        case 6:
            if (!cameraScopes.empty() && cameraScopes.back()) BeginInterval(id, true);
            break;
        case 7:
            if (FindOpen(id, true)) EndInterval(id, true);
            break;
        case 14: BeginInterval(0, false); break;
        case 15: EndInterval(0, false); break;
        case 12: active->invalid = true; active->sample.flags |= 8; break;
        case 10:
            active->invalid |= !cameraScopes.empty();
            for (int i = 0; i < active->spanCount; ++i) active->invalid |= !active->spans[i].ended;
            context->End(active->disjoint.Get()); active->pending = true; active = nullptr;
            Poll(); break;
        }
    } catch (...) { ++dropped; if (active) { context->End(active->disjoint.Get()); active->pending = true; active->invalid = true; active = nullptr; } }
}
static void UNITY_INTERFACE_API DeviceEvent(UnityGfxDeviceEventType event) {
    if (event != kUnityGfxDeviceEventShutdown && event != kUnityGfxDeviceEventBeforeReset) return;
    std::lock_guard<std::mutex> lock(gate);
    active = nullptr;
    for (auto& slot : slots) slot = Slot{};
    context.Reset(); device.Reset(); head = count = 0;
}
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* value) {
    interfaces = value; graphics = value->Get<IUnityGraphics>(); graphics->RegisterDeviceEventCallback(DeviceEvent);
}
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload() {
    if (graphics) graphics->UnregisterDeviceEventCallback(DeviceEvent);
    DeviceEvent(kUnityGfxDeviceEventShutdown); graphics = nullptr; interfaces = nullptr;
}
#define API extern "C" __declspec(dllexport)
API int Fuka_CaptureApi() { return 2; }
API UnityRenderingEvent Fuka_GetRenderEvent() { return RenderEvent; }
API int Fuka_Supported() { return graphics && graphics->GetRenderer() == kUnityGfxRendererD3D11; }
API int Fuka_Pop(Sample* output) {
    std::lock_guard<std::mutex> lock(gate);
    if (!output) return 0;
    if (!count) return 0;
    *output = results[head]; head = (head + 1) % results.size(); --count;
    return 1;
}
API void Fuka_Clear() {
    std::lock_guard<std::mutex> lock(gate);
    // Main-thread calls only change the result queue and discard boundary, never D3D state.
    head = count = dropped = 0; discardThrough = sequence;
}
