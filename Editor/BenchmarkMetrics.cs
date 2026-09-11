#define ENABLE_PROFILER
using System;
using System.Collections.Generic;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
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
        public bool gpuReturned;
        public int observedFrame;
        public double realtime;
        public long frameTimeNs;
        public bool frameTimeValid;
        public long forwardNs, shadowNs, cameraNs;
        public int forwardBlocks, shadowBlocks, cameraBlocks;
        public long cpuNs, drawCalls, setPassCalls, triangles;
        public bool gpuValid, cpuValid;
    }

    public sealed class BenchmarkMetrics : IDisposable
    {
        private readonly Camera camera;
        private readonly List<KeyValuePair<CameraEvent, CommandBuffer>> cameraBuffers = new List<KeyValuePair<CameraEvent, CommandBuffer>>();
        private readonly Dictionary<Light, CommandBuffer[]> lightBuffers = new Dictionary<Light, CommandBuffer[]>();
        private ProfilerRecorder cpu, draws, setPasses, triangles;
        private readonly bool wasProfilerEnabled;
        private CommandBuffer beginFrame;
        private IntPtr nativeEvent;
        private BenchmarkCaptureLedger ledger;
        private bool nativeActive, collecting, draining, disposed;
        private static int nextTicket;

        public bool GpuAvailable => nativeActive;
        public int CpuValidFrames => ledger?.CpuValidFrames ?? 0;
        public int GpuValidFrames => ledger?.GpuValidFrames ?? 0;
        public int TotalFrames => ledger?.TotalFrames ?? 0;
        public int PendingGpuFrames => ledger?.PendingGpuFrames ?? 0;
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
        }

        public void EnableGpu()
        {
            if (nativeActive) return;
            nativeEvent = NativeGpuProvider.EventPointer();
            try
            {
                beginFrame = new CommandBuffer { name = "FUKA GPU capture begin" };
                Add(CameraEvent.BeforeForwardOpaque, 4);
                Add(CameraEvent.AfterForwardAlpha, 5);
                Add(CameraEvent.AfterEverything, 9, 10);
                NativeGpuProvider.Clear();
                nativeActive = true;
                Camera.onPreCull += BeforeCamera;
                DiscoverLights();
            }
            catch { RemoveGpuBuffers(); throw; }
        }

        public void BeginSampling(List<BenchmarkSample> frames, int frame, double now)
        {
            ledger = new BenchmarkCaptureLedger(frames);
            collecting = true; draining = false;
            ledger.BeginFrame(frame, now);
        }

        public void BeginFrame(int frame, double now)
        {
            if (collecting) ledger.BeginFrame(frame, now);
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
            if (rendered != camera || !nativeActive) return;
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
                if (collecting && !ledger.BindGpu(Time.frameCount, ticket))
                {
                    beginFrame.IssuePluginEvent(nativeEvent, 11);
                    Graphics.ExecuteCommandBuffer(beginFrame);
                    return;
                }
                // Warmup renders have unique tickets too, but no entry in the sampling ledger.
                // Queued warmup replies can therefore never leak across the interval boundary.
                beginFrame.IssuePluginEvent(nativeEvent, (ticket << 8) | 1);
                beginFrame.IssuePluginEvent(nativeEvent, 8);
            }
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
                    gpuFlags = gpu.flags, forwardNs = gpu.forwardNs, shadowNs = gpu.shadowNs,
                    cameraNs = gpu.cameraNs, forwardBlocks = gpu.forwardBlocks,
                    shadowBlocks = gpu.shadowBlocks, cameraBlocks = gpu.cameraBlocks,
                    gpuValid = gpu.valid != 0 && gpu.flags == 0 && gpu.forwardNs >= 0 && gpu.shadowNs >= 0 &&
                               gpu.cameraNs >= 0 && gpu.forwardBlocks == 1 && gpu.cameraBlocks == 1,
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

        private void Add(CameraEvent position, params int[] events)
        {
            var buffer = new CommandBuffer();
            foreach (int value in events) buffer.IssuePluginEvent(nativeEvent, value);
            camera.AddCommandBuffer(position, buffer);
            cameraBuffers.Add(new KeyValuePair<CameraEvent, CommandBuffer>(position, buffer));
        }

        public void DiscoverLights()
        {
            if (!nativeActive) return;
            foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>(true))
            {
                if (lightBuffers.ContainsKey(light)) continue;
                var begin = new CommandBuffer(); var end = new CommandBuffer();
                begin.IssuePluginEvent(nativeEvent, 6); end.IssuePluginEvent(nativeEvent, 7);
                light.AddCommandBuffer(LightEvent.BeforeShadowMap, begin);
                light.AddCommandBuffer(LightEvent.AfterShadowMap, end);
                lightBuffers.Add(light, new[] { begin, end });
            }
        }

        private void RemoveGpuBuffers()
        {
            Camera.onPreCull -= BeforeCamera;
            foreach (var pair in cameraBuffers)
            {
                if (camera) camera.RemoveCommandBuffer(pair.Key, pair.Value);
                pair.Value.Release();
            }
            cameraBuffers.Clear();
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
            nativeActive = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            RemoveGpuBuffers();
            cpu.Dispose(); draws.Dispose(); setPasses.Dispose(); triangles.Dispose();
            Profiler.enabled = wasProfilerEnabled;
        }
    }
}
