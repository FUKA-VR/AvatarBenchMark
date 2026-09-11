using System;
using System.Globalization;

namespace FUKA.AvatarBenchmark.Editor
{
    public static class BenchmarkTiming
    {
        public static string Estimate(int targetCount, int populationCount, int selectedCameraCount, int rounds, double samplingSeconds)
        {
            if (targetCount < 1 || populationCount < 1 || selectedCameraCount < 1 || rounds < 1 ||
                double.IsNaN(samplingSeconds) || double.IsInfinity(samplingSeconds) || samplingSeconds <= 0) return "—";
            // Each condition includes the empty environment, baseline, control and every target.
            long cases = ((long)targetCount + 3) * populationCount * selectedCameraCount * rounds;
            // One simultaneous interval. The allowance covers avatar initialization, settling,
            // captures, asynchronous query draining and transitions; it is not a time limit.
            double minimum = cases * (samplingSeconds + 3);
            double maximum = cases * (samplingSeconds + 8);
            return "約" + FormatEstimate(minimum) + "～" + FormatEstimate(maximum) + "（" + cases + "ケース）";
        }

        public static double ElapsedSeconds(string startedUtc, string finishedUtc)
        {
            if (!DateTimeOffset.TryParse(startedUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
                !DateTimeOffset.TryParse(finishedUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out var finish) ||
                finish < start) return double.NaN;
            return (finish - start).TotalSeconds;
        }

        public static string FormatDuration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds) return "—";
            long total = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
            long hours = total / 3600;
            long minutes = total / 60 % 60;
            long remaining = total % 60;
            string text = hours > 0 ? hours + "時間" : "";
            if (minutes > 0) text += minutes + "分";
            if (remaining > 0 || text.Length == 0) text += remaining + "秒";
            return text;
        }

        private static string FormatEstimate(double seconds)
            => FormatDuration(seconds < 60 ? Math.Ceiling(seconds / 10) * 10 : Math.Ceiling(seconds / 60) * 60);
    }
}
