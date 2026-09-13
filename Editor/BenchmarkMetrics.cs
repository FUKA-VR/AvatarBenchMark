#define ENABLE_PROFILER
using System;
using System.Collections.Generic;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace FUKA.AvatarBenchmark.Editor
{
    [Serializable]
    public struct BenchmarkSample
    {
        public long gpuSequence;
        public int gpuTicket, gpuDropped, gpuFlags;
        public bool gpuReturned, cpuReturned;
        public int observedFrame;
        public double realtime;
        public long frameTimeNs;
        public bool frameTimeValid;
        public long gpuNs, shadowNs, cameraNs, textureNs;
        public int gpuBlocks, shadowBlocks, cameraBlocks, textureBlocks;
        public long cpuNs, drawCalls, setPassCalls, triangles;
        public bool gpuValid, cpuValid;
    }

    public sealed class BenchmarkMetrics : IDisposable
    {
        private readonly Camera camera;
        private static readonly CameraEvent[] CameraStarts = {
            CameraEvent.BeforeDepthTexture, CameraEvent.BeforeDepthNormalsTexture,
            CameraEvent.BeforeGBuffer, CameraEvent.BeforeForwardOpaque,
            CameraEvent.BeforeLighting, CameraEvent.BeforeFinalPass
        };
        private readonly Dictionary<Camera, List<KeyValuePair<CameraEvent, CommandBuffer>>> cameraBuffers = new Dictionary<Camera, List<KeyValuePair<CameraEvent, CommandBuffer>>>();
        private readonly Dictionary<Light, CommandBuffer[]> lightBuffers = new Dictionary<Light, CommandBuffer[]>();
        private readonly Dictionary<Camera, int> cameraIds = new Dictionary<Camera, int>();
        private readonly HashSet<Camera> callbackCameras = new HashSet<Camera>();
        private readonly HashSet<Camera> automaticCameras = new HashSet<Camera>();
        private readonly Stack<bool> cameraScopes = new Stack<bool>();
        private ProfilerRecorder cpu, draws, setPasses, triangles;
        private readonly bool wasProfilerEnabled;
        private readonly BenchmarkFrameHooks frameHooks;
        private CommandBuffer beginFrame;
        private IntPtr nativeEvent;
        private BenchmarkCaptureLedger ledger;
        private bool nativeActive, collecting, draining, disposed;
        private bool frameOpen, mainCameraRendered, inPlayerLoop;
        private int lastStartedFrame = -1, cameraId, lightId;
        private static int nextTicket;

        public bool GpuAvailable => nativeActive;
        public int CpuValidFrames => ledger?.CpuValidFrames ?? 0;
        public int GpuValidFrames => ledger?.GpuValidFrames ?? 0;
        public int TotalFrames => ledger?.TotalFrames ?? 0;
        public int PendingGpuFrames => ledger?.PendingGpuFrames ?? 0;
        public int PendingCpuFrames => ledger?.PendingCpuFrames ?? 0;
        public double MaximumFrameSeconds => ledger?.MaximumFrameSeconds ?? 0;
        public int DrainPolls { get; private set; }

        public BenchmarkMetrics(Camera camera)
        {
            this.camera = camera;
            wasProfilerEnabled = Profiler.enabled;
            Profiler.enabled = false;
            cpu = OpenRecorder("PlayerLoop", true);
            draws = OpenRecorder("Draw Calls Count", false);
            setPasses = OpenRecorder("SetPass Calls Count", false);
            triangles = OpenRecorder("Triangles Count", false);
            frameHooks = new BenchmarkFrameHooks(StartFrame, () => inPlayerLoop = false, () => TextureEvent(14), () => TextureEvent(15));
        }

        public void EnableGpu()
        {
            if (nativeActive) return;
            nativeEvent = NativeGpuProvider.EventPointer();
            try
            {
                beginFrame = new CommandBuffer { name = "FUKA GPU frame capture" };
                NativeGpuProvider.Clear();
                nativeActive = true;
                Camera.onPreCull += BeforeCamera;
                Camera.onPreRender += BeforeLegacyCamera;
                Camera.onPostRender += AfterLegacyCamera;
                foreach (var rendered in UnityEngine.Object.FindObjectsOfType<Camera>(true)) RegisterCamera(rendered);
                DiscoverLights();
            }
            catch { RemoveGpuBuffers(); throw; }
        }

        public void BeginSampling(List<BenchmarkSample> frames)
        {
            ledger = new BenchmarkCaptureLedger(frames);
            collecting = true; draining = false;
            // Capture starts on the next complete frame, including FixedUpdate and manual renders.
        }

        private void StartFrame()
        {
            if (disposed || lastStartedFrame == Time.frameCount) return;
            // Editor GameView rendering happens after PlayerLoop, so close at the next update.
            EndFrame();
            lastStartedFrame = Time.frameCount;
            inPlayerLoop = true;
            automaticCameras.Clear(); cameraScopes.Clear();
            double now = Time.realtimeSinceStartupAsDouble;
            ReadCompletedFrame(Time.frameCount, now);
            DrainResults();
            if (collecting) ledger.BeginFrame(Time.frameCount, now);
            if (!nativeActive) return;
            beginFrame.Clear();
            if (draining)
            {
                DrainPolls++;
                beginFrame.IssuePluginEvent(nativeEvent, 11);
            }
            else
            {
                if (nextTicket >= 0x00ffffff) throw new InvalidOperationException("GPU計測の記録数が上限に達しました。Playモードを終了して再計測してください。");
                int ticket = ++nextTicket;
                if (collecting) ledger.BindGpu(Time.frameCount, ticket);
                beginFrame.IssuePluginEvent(nativeEvent, (ticket << 8) | 1);
                frameOpen = true; mainCameraRendered = false;
            }
            Graphics.ExecuteCommandBuffer(beginFrame);
        }

        private void EndFrame()
        {
            if (!nativeActive || !frameOpen) return;
            beginFrame.Clear();
            if (!mainCameraRendered) beginFrame.IssuePluginEvent(nativeEvent, 12);
            beginFrame.IssuePluginEvent(nativeEvent, 10);
            Graphics.ExecuteCommandBuffer(beginFrame);
            frameOpen = false;
        }

        private void TextureEvent(int id)
        {
            if (!nativeActive || !frameOpen) return;
            beginFrame.Clear(); beginFrame.IssuePluginEvent(nativeEvent, id);
            Graphics.ExecuteCommandBuffer(beginFrame);
        }

        public void ReadCompletedFrame(int currentFrame, double now)
        {
            // PlayerLoop has not ended for the current Update. Its last completed recorder
            // sample belongs to the preceding frame; GPU replies are matched independently.
            ledger?.CompleteCpu(currentFrame - 1, now,
                cpu.Valid && cpu.Count > 0 ? cpu.LastValue : -1,
                cpu.Valid && cpu.Count > 0,
                Counter(draws), Counter(setPasses), Counter(triangles));
        }

        public void StopSampling()
        {
            if (!draining) DrainPolls = 0;
            collecting = false; draining = true;
        }

        private static long Counter(ProfilerRecorder recorder)
            => recorder.Valid && recorder.Count > 0 ? recorder.LastValue : -1;

        private void BeforeCamera(Camera rendered)
        {
            if (!nativeActive || !frameOpen) return;
            RegisterCamera(rendered);
            bool accepted = IsMeasuredCamera(rendered);
            if (cameraScopes.Count > 0) accepted &= cameraScopes.Peek();
            // Repaint can redraw the GameView repeatedly without another simulation update.
            // Explicit renders inside PlayerLoop or an accepted camera keep their full count.
            else if (!inPlayerLoop && accepted) accepted = automaticCameras.Add(rendered);
            cameraScopes.Push(accepted);
            beginFrame.Clear(); beginFrame.IssuePluginEvent(nativeEvent, ((accepted ? 1 : 0) << 8) | 16);
            Graphics.ExecuteCommandBuffer(beginFrame);
            if (rendered == camera && accepted) mainCameraRendered = true;
        }

        public static bool IsMeasuredCamera(Camera rendered)
            => rendered && (rendered.cameraType == CameraType.Game || rendered.cameraType == CameraType.Reflection) &&
               !EditorSceneManager.IsPreviewScene(rendered.gameObject.scene);

        private void RegisterCamera(Camera rendered)
        {
            if (!IsMeasuredCamera(rendered)) return;
            bool callback = rendered.actualRenderingPath == RenderingPath.VertexLit;
            if (cameraBuffers.TryGetValue(rendered, out var previous))
            {
                if (callbackCameras.Contains(rendered) == callback) return;
                foreach (var pair in previous) { rendered.RemoveCommandBuffer(pair.Key, pair.Value); pair.Value.Release(); }
                cameraBuffers.Remove(rendered); callbackCameras.Remove(rendered);
            }
            if (!cameraIds.TryGetValue(rendered, out int id)) cameraIds.Add(rendered, id = ++cameraId);
            var buffers = new List<KeyValuePair<CameraEvent, CommandBuffer>>();
            cameraBuffers.Add(rendered, buffers);
            // VertexLit does not expose the Forward/Deferred command-buffer stages.
            if (callback) { callbackCameras.Add(rendered); return; }
            // Start at the first GPU stage that executes, after CPU culling/preparation.
            foreach (var start in CameraStarts) Add(rendered, buffers, start, (id << 8) | 4, true);
            Add(rendered, buffers, CameraEvent.AfterEverything, (id << 8) | 5, false);
        }

        private void BeforeLegacyCamera(Camera rendered) { LegacyCameraEvent(rendered, 4); }
        private void AfterLegacyCamera(Camera rendered)
        {
            if (!nativeActive || !frameOpen) return;
            LegacyCameraEvent(rendered, 5);
            if (cameraScopes.Count == 0) return;
            cameraScopes.Pop();
            beginFrame.Clear(); beginFrame.IssuePluginEvent(nativeEvent, 17);
            Graphics.ExecuteCommandBuffer(beginFrame);
        }
        private void LegacyCameraEvent(Camera rendered, int kind)
        {
            if (!nativeActive || !frameOpen || !callbackCameras.Contains(rendered)) return;
            beginFrame.Clear(); beginFrame.IssuePluginEvent(nativeEvent, (cameraIds[rendered] << 8) | kind);
            Graphics.ExecuteCommandBuffer(beginFrame);
        }

        public void DrainResults()
        {
            if (!nativeActive) return;
            // Bound main-thread work even if the render thread keeps producing replies.
            for (int i = 0; i < 512 && NativeGpuProvider.Pop(out var gpu); i++)
            {
                ledger?.AcceptGpu(new BenchmarkSample
                {
                    gpuTicket = gpu.ticket, gpuSequence = gpu.sequence, gpuDropped = gpu.dropped,
                    gpuFlags = gpu.flags, gpuNs = gpu.gpuNs, shadowNs = gpu.shadowNs,
                    cameraNs = gpu.cameraNs, textureNs = gpu.textureNs, gpuBlocks = gpu.gpuBlocks, textureBlocks = gpu.textureBlocks,
                    shadowBlocks = gpu.shadowBlocks, cameraBlocks = gpu.cameraBlocks,
                    gpuValid = gpu.valid != 0 && gpu.flags == 0 && gpu.gpuNs >= 0 && gpu.shadowNs >= 0 &&
                               gpu.cameraNs >= 0 && gpu.textureNs >= 0 && gpu.cameraBlocks >= 1,
                    gpuReturned = true
                });
            }
        }

        private static ProfilerRecorder OpenRecorder(string name, bool currentThread)
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach (var handle in handles)
            {
                if (ProfilerRecorderHandle.GetDescription(handle).Name != name) continue;
                var options = ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.WrapAroundWhenCapacityReached | ProfilerRecorderOptions.SumAllSamplesInFrame;
                if (currentThread) options |= ProfilerRecorderOptions.CollectOnlyOnCurrentThread;
                return new ProfilerRecorder(handle, 1, options);
            }
            return default;
        }

        private void Add(Camera rendered, List<KeyValuePair<CameraEvent, CommandBuffer>> buffers, CameraEvent position, int id, bool first)
        {
            var buffer = new CommandBuffer { name = "FUKA GPU camera interval" };
            buffer.IssuePluginEvent(nativeEvent, id);
            var existing = first ? rendered.GetCommandBuffers(position) : Array.Empty<CommandBuffer>();
            foreach (var item in existing) rendered.RemoveCommandBuffer(position, item);
            rendered.AddCommandBuffer(position, buffer);
            foreach (var item in existing) rendered.AddCommandBuffer(position, item);
            buffers.Add(new KeyValuePair<CameraEvent, CommandBuffer>(position, buffer));
        }

        public void DiscoverLights()
        {
            if (!nativeActive) return;
            foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>(true))
            {
                if (lightBuffers.ContainsKey(light) || EditorSceneManager.IsPreviewScene(light.gameObject.scene)) continue;
                int id = ++lightId;
                var begin = new CommandBuffer(); var end = new CommandBuffer();
                begin.IssuePluginEvent(nativeEvent, (id << 8) | 6); end.IssuePluginEvent(nativeEvent, (id << 8) | 7);
                light.AddCommandBuffer(LightEvent.BeforeShadowMap, begin);
                light.AddCommandBuffer(LightEvent.AfterShadowMap, end);
                lightBuffers.Add(light, new[] { begin, end });
            }
        }

        private void RemoveGpuBuffers()
        {
            Camera.onPreCull -= BeforeCamera;
            Camera.onPreRender -= BeforeLegacyCamera;
            Camera.onPostRender -= AfterLegacyCamera;
            foreach (var cameraPair in cameraBuffers)
            foreach (var pair in cameraPair.Value)
            {
                if (cameraPair.Key) cameraPair.Key.RemoveCommandBuffer(pair.Key, pair.Value);
                pair.Value.Release();
            }
            cameraBuffers.Clear();
            cameraIds.Clear(); callbackCameras.Clear();
            automaticCameras.Clear(); cameraScopes.Clear();
            foreach (var pair in lightBuffers)
            {
                if (pair.Key)
                {
                    pair.Key.RemoveCommandBuffer(LightEvent.BeforeShadowMap, pair.Value[0]);
                    pair.Key.RemoveCommandBuffer(LightEvent.AfterShadowMap, pair.Value[1]);
                }
                pair.Value[0].Release(); pair.Value[1].Release();
            }
            lightBuffers.Clear();
            beginFrame?.Release(); beginFrame = null;
            nativeActive = frameOpen = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            EndFrame();
            disposed = true;
            frameHooks.Dispose();
            RemoveGpuBuffers();
            cpu.Dispose(); draws.Dispose(); setPasses.Dispose(); triangles.Dispose();
            Profiler.enabled = wasProfilerEnabled;
        }
    }
}
