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

using Microsoft::WRL::ComPtr;
struct Sample {
    int64_t sequence, forwardNs, shadowNs, cameraNs;
    int ticket, forwardBlocks, shadowBlocks, cameraBlocks, valid, dropped, flags;
};
static_assert(sizeof(Sample) == 64 && offsetof(Sample, ticket) == 32 && offsetof(Sample, flags) == 56,
    "Sample must match NativeGpuSample's sequential x64 layout.");
struct Span {
    ComPtr<ID3D11Query> begin, end;
    bool started = false, ended = false;
    void Clear() { started = ended = false; }
};
struct Slot {
    ComPtr<ID3D11Query> disjoint;
    Span forward, camera;
    std::vector<Span> shadows;
    Sample sample{};
    int shadowCount = 0, openShadow = -1;
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
static bool ReadSpan(const Span& span, uint64_t frequency, int64_t& ns) {
    ns = -1;
    if (!span.started || !span.ended) return true;
    uint64_t first = 0, last = 0;
    HRESULT a = context->GetData(span.begin.Get(), &first, sizeof(first), D3D11_ASYNC_GETDATA_DONOTFLUSH);
    HRESULT b = context->GetData(span.end.Get(), &last, sizeof(last), D3D11_ASYNC_GETDATA_DONOTFLUSH);
    if (a == S_FALSE || b == S_FALSE) return false;
    if (a != S_OK || b != S_OK || last < first || !frequency) return true;
    ns = static_cast<int64_t>((static_cast<long double>(last - first) * 1000000000.0L) / frequency);
    return true;
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
            if (!ReadSpan(slot.forward, info.Frequency, sample.forwardNs) ||
                !ReadSpan(slot.camera, info.Frequency, sample.cameraNs)) continue;
            int64_t shadows = 0; bool waiting = false;
            for (int i = 0; i < slot.shadowCount; ++i) {
                int64_t ns;
                if (!ReadSpan(slot.shadows[i], info.Frequency, ns)) { waiting = true; break; }
                if (ns < 0) { sample.valid = 0; sample.flags |= 4; } else shadows += ns;
            }
            if (waiting) continue;
            sample.shadowNs = shadows;
            // A completed zero-length span is below timer resolution, not a broken query.
            if (sample.forwardNs < 0 || sample.cameraNs < 0) { sample.valid = 0; sample.flags |= 4; }
            if (sample.forwardBlocks != 1 || sample.cameraBlocks != 1)
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
                slot.sample.forwardNs = slot.sample.shadowNs = slot.sample.cameraNs = -1;
                slot.forward.Clear(); slot.camera.Clear();
                for (auto& span : slot.shadows) span.Clear();
                slot.shadowCount = 0; slot.openShadow = -1; slot.invalid = false;
                active = &slot; context->Begin(slot.disjoint.Get()); return;
            }
            ++dropped; return;
        }
        if (!active) return;
        switch (kind) {
        case 4: Begin(active->forward, true); ++active->sample.forwardBlocks; break;
        case 5: End(active->forward, true); break;
        case 6:
            if (active->openShadow != -1 || active->shadowCount >= 256) { active->invalid = true; break; }
            if (active->shadowCount == static_cast<int>(active->shadows.size())) active->shadows.emplace_back();
            active->openShadow = active->shadowCount++;
            Begin(active->shadows[active->openShadow], true); ++active->sample.shadowBlocks; break;
        case 7:
            if (active->openShadow == -1) { active->invalid = true; break; }
            End(active->shadows[active->openShadow], true); active->openShadow = -1; break;
        // The camera envelope is diagnostic only and includes gaps between draw intervals.
        case 8: Begin(active->camera, false); ++active->sample.cameraBlocks; break;
        case 9: End(active->camera, false); break;
        case 10:
            active->invalid |= active->openShadow != -1;
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
API int Fuka_CaptureApi() { return 1; }
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
