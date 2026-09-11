using System;
using System.Collections.Generic;
using System.Linq;

namespace FUKA.AvatarBenchmark.Editor
{
    public enum BenchmarkControlWarning { None, Notice, High }

    public static class BenchmarkStatistics
    {
        public const int MinimumValidFrames = 60;
        public const double MaxMissingFraction = .05;
        public const double ControlNoticePercent = 10;
        public const double ControlHighWarningPercent = 20;

        public static bool EnoughSamples(int valid, int total)
            => valid >= MinimumValidFrames && (double)(total - valid) / Math.Max(1, total) <= MaxMissingFraction;

        public static bool SamplingComplete(double elapsedSeconds, int cpuValid, int gpuValid, int total, bool gpuAvailable, BenchmarkProfile profile)
            => elapsedSeconds >= profile.maximumMeasureSeconds || elapsedSeconds >= profile.measureSeconds &&
               EnoughSamples(cpuValid, total) && (!gpuAvailable || EnoughSamples(gpuValid, total));

        public static double Median(IEnumerable<double> source)
        {
            var values = source.Where(IsFinite).OrderBy(x => x).ToArray();
            if (values.Length == 0) return double.NaN;
            int middle = values.Length / 2;
            return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) * 0.5 : values[middle];
        }

        public static double Percentile95(IEnumerable<double> source)
            => Percentile(source, .95);

        public static double Percentile(IEnumerable<double> source, double fraction)
        {
            var values = source.Where(IsFinite).OrderBy(x => x).ToArray();
            return values.Length == 0 ? double.NaN : values[Math.Max(0, Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * fraction) - 1))];
        }

        // Population SD describes all observed frames, around their mean (not their median).
        public static double StandardDeviation(IEnumerable<double> source)
        {
            int count = 0; double mean = 0, m2 = 0;
            foreach (double value in source.Where(IsFinite))
            {
                count++; double delta = value - mean; mean += delta / count; m2 += delta * (value - mean);
            }
            return count == 0 ? double.NaN : Math.Sqrt(Math.Max(0, m2 / count));
        }

        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public static double PercentChange(double baseline, double target) =>
            baseline > 0 && IsFinite(baseline) && IsFinite(target) ? (target / baseline - 1) * 100 : double.NaN;

        public static double ControlDeviationPercent(double baseline, double control)
        {
            if (!IsFinite(baseline) || !IsFinite(control) || baseline < 0 || control < 0) return double.NaN;
            if (baseline == 0) return control == 0 ? 0 : double.PositiveInfinity;
            return Math.Abs(control - baseline) / baseline * 100;
        }

        public static BenchmarkControlWarning ControlWarning(double percent)
        {
            // Include exact 10% and 20% boundaries despite floating-point roundoff.
            if (!IsFinite(percent) || percent < 0 || percent > ControlHighWarningPercent + 1e-9) return BenchmarkControlWarning.High;
            return percent > ControlNoticePercent + 1e-9 ? BenchmarkControlWarning.Notice : BenchmarkControlWarning.None;
        }

        public static string AssessDifference(IEnumerable<double> pairedDifferences, double controlNoise)
        {
            var values = pairedDifferences.Where(IsFinite).ToArray();
            if (values.Length < 3 || !IsFinite(controlNoise)) return "参考値";
            double center = Median(values);
            double spread = values.Max() - values.Min();
            if (Math.Abs(center) <= Math.Max(controlNoise, spread) || values.Min() <= 0 && values.Max() >= 0)
                return "有意差なし";
            return center > 0 ? "増加" : "減少";
        }

        public static bool RemovePopulationsAboveLimit(List<int> populations, int? pinLimit)
        {
            // A missing limit means an environment is unavailable or still loading.
            if (!pinLimit.HasValue || pinLimit.Value < 1 || pinLimit.Value > 10) return false;
            return populations.RemoveAll(count => count > pinLimit.Value) > 0;
        }

        public static void ValidatePopulations(int pinCount, IEnumerable<int> populations)
        {
            var counts = populations?.ToArray() ?? Array.Empty<int>();
            if (pinCount < 1 || pinCount > 10) throw new ArgumentException("配置ピンは1～10個設定する必要があります。");
            if (counts.Length == 0 || counts.Distinct().Count() != counts.Length)
                throw new ArgumentException("計測人数を1つ以上選択してください。");
            if (counts.Any(x => x < 1 || x > 10 || x > pinCount))
                throw new ArgumentException("計測人数は1～10人で、シーン内の配置ピン数以下に設定してください。");
        }
    }
}
