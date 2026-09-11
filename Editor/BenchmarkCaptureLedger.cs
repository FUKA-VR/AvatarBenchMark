using System;
using System.Collections.Generic;

namespace FUKA.AvatarBenchmark.Editor
{
    // Keep one row per expected source frame, even when a query never returns.
    public sealed class BenchmarkCaptureLedger
    {
        public const int DuplicateRenderFlag = 1 << 16;
        private readonly List<BenchmarkSample> frames;
        private readonly Dictionary<int, int> pending = new Dictionary<int, int>();
        private int lastCpuFrame = -1;
        public int TotalFrames => frames.Count;
        public int CpuValidFrames { get; private set; }
        public int GpuValidFrames { get; private set; }
        public int PendingGpuFrames => pending.Count;
        public double MaximumFrameSeconds { get; private set; }

        public BenchmarkCaptureLedger(List<BenchmarkSample> frames)
        {
            this.frames = frames ?? throw new ArgumentNullException(nameof(frames));
            if (frames.Count != 0) throw new ArgumentException("The capture ledger requires an empty frame list.", nameof(frames));
        }

        public void BeginFrame(int frame, double now)
        {
            if (frames.Count >= 300000) throw new InvalidOperationException("1試行あたりの記録フレーム数が上限（30万フレーム）に達しました。異常な高フレームレートまたは計測ループが発生した可能性があります。");
            if (frames.Count > 0 && frame <= frames[frames.Count - 1].observedFrame)
                throw new InvalidOperationException("Source frames must be recorded once in ascending order.");
            frames.Add(new BenchmarkSample
            {
                observedFrame = frame, realtime = now,
                cpuNs = -1, frameTimeNs = -1, forwardNs = -1, shadowNs = -1, cameraNs = -1,
                drawCalls = -1, setPassCalls = -1, triangles = -1
            });
        }

        public void CompleteCpu(int frame, double nextFrameTime, long cpuNs, bool valid, long draws, long sets, long triangles)
        {
            if (frames.Count == 0 || frame == lastCpuFrame) return;
            int index = frames.Count - 1;
            var row = frames[index];
            if (row.observedFrame != frame) return;
            lastCpuFrame = frame;
            row.cpuNs = cpuNs; row.cpuValid = valid && cpuNs >= 0;
            row.drawCalls = draws; row.setPassCalls = sets; row.triangles = triangles;
            double duration = nextFrameTime - row.realtime;
            row.frameTimeValid = BenchmarkStatistics.IsFinite(duration) && duration > 0 && duration < long.MaxValue / 1e9;
            row.frameTimeNs = row.frameTimeValid ? (long)Math.Round(duration * 1e9) : -1;
            if (row.frameTimeValid) MaximumFrameSeconds = Math.Max(MaximumFrameSeconds, duration);
            if (row.cpuValid) CpuValidFrames++;
            frames[index] = row;
        }

        public bool BindGpu(int frame, int ticket)
        {
            if (frames.Count == 0) return false;
            int index = frames.Count - 1;
            var row = frames[index];
            if (row.observedFrame != frame) return false;
            if (row.gpuTicket != 0)
            {
                if (row.gpuValid) GpuValidFrames--;
                row.gpuValid = false; row.gpuFlags |= DuplicateRenderFlag;
                frames[index] = row;
                return false;
            }
            if (ticket <= 0 || pending.ContainsKey(ticket)) throw new ArgumentException("Render tickets must be positive and unique.", nameof(ticket));
            row.gpuTicket = ticket; frames[index] = row;
            pending.Add(ticket, index);
            return true;
        }

        public void AcceptGpu(BenchmarkSample reply)
        {
            if (!pending.TryGetValue(reply.gpuTicket, out int index)) return;
            pending.Remove(reply.gpuTicket);
            var row = frames[index];
            row.gpuReturned = true; row.gpuSequence = reply.gpuSequence;
            row.gpuDropped = reply.gpuDropped; row.gpuFlags |= reply.gpuFlags;
            row.forwardNs = reply.forwardNs; row.shadowNs = reply.shadowNs; row.cameraNs = reply.cameraNs;
            row.forwardBlocks = reply.forwardBlocks; row.shadowBlocks = reply.shadowBlocks; row.cameraBlocks = reply.cameraBlocks;
            row.gpuValid = reply.gpuValid && row.gpuFlags == 0;
            if (row.gpuValid) GpuValidFrames++;
            frames[index] = row;
        }
    }
}
