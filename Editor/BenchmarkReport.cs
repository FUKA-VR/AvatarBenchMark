using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace FUKA.AvatarBenchmark.Editor
{
    [Serializable]
    public sealed class BenchmarkSamplingInterval
    {
        public double seconds;
        public double startedRealtime;
        public int totalFrames;
        public int gpuValidFrames;
        public int cpuValidFrames;
        public bool extended;
        public bool insufficientAtLimit;
    }

    [Serializable]
    public sealed class BenchmarkCaseResult
    {
        public string id, environment, view;
        public int population, round;
        public string role, entryId, avatarBuildId, label, error, screenshot, runtimeAudit;
        public int samples, gpuMissing, cpuMissing, frameTimeMissing;
        public bool gpuUsable, cpuUsable;
        public string gpuError, gpuComparisonIssue;
        public double gpuDrainSeconds;
        public int gpuPendingAtEnd;
        public double gpuMedianMs = -1, gpuP95Ms = -1, gpuMeanMs = -1, gpuStandardDeviationMs = -1, gpuMaxMs = -1;
        public double cpuMedianMs = -1, cpuP95Ms = -1, cpuMeanMs = -1, cpuStandardDeviationMs = -1, cpuMaxMs = -1;
        public double frameTimeMedianMs = -1, frameTimeP95Ms = -1, frameTimeMeanMs = -1, frameTimeStandardDeviationMs = -1, frameTimeMaxMs = -1;
        public string gpuStatus, cpuStatus;
        public double drawCallsMedian = -1, setPassMedian = -1;
        public List<BenchmarkSample> frames = new List<BenchmarkSample>();
        public List<BenchmarkSamplingInterval> sampling = new List<BenchmarkSamplingInterval>();

        public void Summarize()
        {
            samples = frames.Count;
            gpuMissing = frames.Count(x => !x.gpuValid);
            cpuMissing = frames.Count(x => !x.cpuValid);
            frameTimeMissing = frames.Count(x => !x.frameTimeValid);
            gpuUsable = string.IsNullOrEmpty(error) && string.IsNullOrEmpty(gpuError) && string.IsNullOrEmpty(gpuComparisonIssue) &&
                BenchmarkStatistics.EnoughSamples(samples - gpuMissing, samples);
            cpuUsable = string.IsNullOrEmpty(error) && BenchmarkStatistics.EnoughSamples(samples - cpuMissing, samples);
            Describe(frames.Where(x => x.gpuValid).Select(x => x.gpuNs / 1e6).ToArray(),
                out gpuMedianMs, out gpuMeanMs, out gpuStandardDeviationMs, out gpuP95Ms, out gpuMaxMs);
            Describe(frames.Where(x => x.cpuValid).Select(x => x.cpuNs / 1e6).ToArray(),
                out cpuMedianMs, out cpuMeanMs, out cpuStandardDeviationMs, out cpuP95Ms, out cpuMaxMs);
            Describe(frames.Where(x => x.frameTimeValid).Select(x => x.frameTimeNs / 1e6).ToArray(),
                out frameTimeMedianMs, out frameTimeMeanMs, out frameTimeStandardDeviationMs, out frameTimeP95Ms, out frameTimeMaxMs);
            string gpuIssue = !string.IsNullOrEmpty(error) ? error : !string.IsNullOrEmpty(gpuError) ? gpuError : gpuComparisonIssue;
            gpuStatus = Status(gpuUsable, samples - gpuMissing, samples, gpuIssue);
            if (gpuMissing > 0 && string.IsNullOrEmpty(gpuError))
                gpuStatus += "（未回収 " + frames.Count(x => !x.gpuReturned) + "、時計・クエリ異常 " + frames.Count(x => x.gpuFlags != 0) + "）";
            cpuStatus = Status(cpuUsable, samples - cpuMissing, samples, error);
            drawCallsMedian = FiniteOrMissing(BenchmarkStatistics.Median(frames.Where(x => x.drawCalls >= 0).Select(x => (double)x.drawCalls)));
            setPassMedian = FiniteOrMissing(BenchmarkStatistics.Median(frames.Where(x => x.setPassCalls >= 0).Select(x => (double)x.setPassCalls)));
        }

        private static string Status(bool usable, int valid, int total, string issue)
        {
            if (usable) return "正常（" + valid + "/" + total + "フレーム）";
            string reason = !string.IsNullOrEmpty(issue) ? issue :
                "有効フレーム不足（必要: " + BenchmarkStatistics.MinimumValidFrames + "フレーム以上、欠損率: " + (BenchmarkStatistics.MaxMissingFraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%以下）";
            return (valid > 0 ? "参考値" : "データなし") + "（" + valid + "/" + total + "フレーム）: " + reason;
        }

        private static void Describe(double[] values, out double median, out double mean, out double sd, out double p95, out double maximum)
        {
            median = mean = sd = p95 = maximum = -1;
            if (values.Length == 0) return;
            median = BenchmarkStatistics.Median(values);
            mean = values.Average(); sd = BenchmarkStatistics.StandardDeviation(values);
            p95 = BenchmarkStatistics.Percentile95(values); maximum = values.Max();
        }

        private static double FiniteOrMissing(double value) => BenchmarkStatistics.IsFinite(value) ? value : -1;
    }

    [Serializable]
    public sealed class BenchmarkLoggedError
    {
        public string targetLabel, context, message, stackTrace;
        public LogType type;
        public long count = 1;
    }

    [Serializable]
    public sealed class BenchmarkReport
    {
        public const string CurrentToolVersion = "1.10";
        public const string MeasurementMethod = "multi-camera-render-intervals";
        public const int MaximumLoggedErrors = 100;
        public const string LoggedErrorWarning = "計測またはプレビュー中にエラーが発生しました。処理の停止や描画の欠落、エラーログ出力の負荷によって計測数値に影響が出ている可能性があります。";
        public string measurementMethod;
        public string toolVersion;
        public string ToolVersionText => string.IsNullOrWhiteSpace(toolVersion) ? "未記録" : toolVersion;
        public string gpuTimingMethod;
        public string startedUtc, finishedUtc;
        public string DurationText => BenchmarkTiming.FormatDuration(BenchmarkTiming.ElapsedSeconds(startedUtc, finishedUtc));
        public string status, error, unityVersion, operatingSystem, cpu, gpu, graphicsApi, colorSpace, quality, packageVersions;
        public List<BenchmarkUnitySetting> unitySettings;
        public string profileJson, planJson, buildId;
        public string measurementScope = "Unity Play Mode / Built-in / D3D11 timestamp union of game and reflection camera rendering, shadow-map rendering and the CustomRenderTexture update stage per simulation frame. Includes camera RenderTexture output and depth passes. Forward/Deferred camera intervals end at AfterEverything; VertexLit uses pre/post-render callbacks. Overlapping intervals count once; gaps between intervals are excluded. SceneView, preview cameras and repeated automatic GameView repaints are excluded. Standalone GPU work outside these intervals is not captured. CPU is main-thread PlayerLoop, not total worker, render-thread or audio DSP CPU time. GPU elapsed intervals can contain command-submission waits and are not pure GPU active time. Delayed replies retain their source frame. These timings cannot be converted directly to VRChat client FPS.";
        public List<BenchmarkCaseResult> cases = new List<BenchmarkCaseResult>();
        public List<BenchmarkLoggedError> loggedErrors = new List<BenchmarkLoggedError>();
        public long omittedLogErrors;
        public bool HasLoggedErrors => (loggedErrors != null && loggedErrors.Count > 0) || omittedLogErrors > 0;

        public void RecordLog(string message, string stackTrace, LogType type, string context, string targetLabel = null)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            // Repeated render errors must not grow the report on every frame.
            foreach (var entry in loggedErrors)
                if (entry.type == type && entry.context == context && entry.message == message && entry.targetLabel == targetLabel)
                {
                    entry.count++;
                    return;
                }
            if (loggedErrors.Count >= MaximumLoggedErrors) { omittedLogErrors++; return; }
            loggedErrors.Add(new BenchmarkLoggedError { message = message, stackTrace = stackTrace, type = type, context = context, targetLabel = targetLabel });
        }

        public string LoggedErrorTargetsText()
        {
            var targets = loggedErrors?.Select(x => string.IsNullOrEmpty(x.targetLabel) ? x.context : x.targetLabel)
                .Where(x => !string.IsNullOrEmpty(x)).Distinct() ?? Enumerable.Empty<string>();
            return "エラー発生時の計測対象: " + string.Join("、", targets);
        }

        public string LoggedErrorWarningText() => LoggedErrorTargetsText() + "\n" + LoggedErrorWarning;

        public string LoggedErrorDetails()
        {
            var text = new StringBuilder();
            if (loggedErrors != null)
                foreach (var entry in loggedErrors)
                    text.AppendLine(entry.context + " / " + entry.type + "（" + entry.count + "回）")
                        .AppendLine(entry.message).AppendLine();
            if (omittedLogErrors > 0)
                text.AppendLine("エラー上限に達したため、その他のエラー " + omittedLogErrors + "回分の詳細を省略しました。UnityのConsoleをご確認ください。");
            return text.ToString().TrimEnd();
        }

        public static BenchmarkReport Parse(string json)
        {
            var report = JsonUtility.FromJson<BenchmarkReport>(json);
            if (report == null || report.measurementMethod != MeasurementMethod)
                throw new InvalidDataException("この形式の計測結果は開けません。現在のツールで計測してください。");
            return report;
        }

        public void Save(string directory, BenchmarkProfile profile)
        {
            Directory.CreateDirectory(directory);
            WriteAtomic(Path.Combine(directory, "report.json"), JsonUtility.ToJson(this, true));
            var csv = new StringBuilder("case,environment,view,population,round,role,target,observed_frame,realtime,frame_time_ns,frame_time_valid,cpu_ns,cpu_valid,gpu_ticket,gpu_returned,gpu_sequence,gpu_dropped,gpu_flags,gpu_ns,shadow_ns,camera_ns,texture_ns,gpu_blocks,shadow_blocks,camera_blocks,texture_blocks,gpu_valid,draw_calls,setpass_calls,triangles\n");
            foreach (var c in cases)
            foreach (var f in c.frames)
                csv.AppendLine(string.Join(",", new[] { Cell(c.id), Cell(c.environment), Cell(c.view), c.population.ToString(), c.round.ToString(), Cell(c.role), Cell(c.label),
                    f.observedFrame.ToString(), N(f.realtime), f.frameTimeNs.ToString(), f.frameTimeValid.ToString(), f.cpuNs.ToString(), f.cpuValid.ToString(),
                    f.gpuTicket.ToString(), f.gpuReturned.ToString(), f.gpuSequence.ToString(), f.gpuDropped.ToString(), f.gpuFlags.ToString(),
                    f.gpuNs.ToString(), f.shadowNs.ToString(), f.cameraNs.ToString(), f.textureNs.ToString(), f.gpuBlocks.ToString(), f.shadowBlocks.ToString(), f.cameraBlocks.ToString(), f.textureBlocks.ToString(), f.gpuValid.ToString(),
                    f.drawCalls.ToString(), f.setPassCalls.ToString(), f.triangles.ToString() }));
            WriteAtomic(Path.Combine(directory, "frames.csv"), csv.ToString());
            SaveViews(directory, profile);
        }

        public void SaveViews(string directory, BenchmarkProfile profile)
        {
            WriteAtomic(Path.Combine(directory, "summary.md"), Summary(profile));
            WriteAtomic(Path.Combine(directory, "report.html"), BenchmarkReportHtml.Create(this, profile));
        }

        public string Summary(BenchmarkProfile profile)
        {
            var text = new StringBuilder("# VRChat ギミック負荷検証レポート\n\n");
            text.AppendLine("ツールバージョン: " + ToolVersionText + "\n");
            text.AppendLine("ステータス: " + status + "  \nUnity " + unityVersion + " / " + gpu + " / " + graphicsApi + "\n");
            text.AppendLine("計測所要時間: " + DurationText + "\n");
            if (HasLoggedErrors)
            {
                text.AppendLine("> ⚠ " + LoggedErrorWarningText().Replace("\n", "\n> ") + "\n");
                foreach (string line in LoggedErrorDetails().Replace("\r", "").Split('\n'))
                    text.AppendLine("    " + line);
                text.AppendLine();
            }
            text.AppendLine("[HTMLレポートを開く](report.html)\n");
            text.AppendLine("【データの見方】\n" +
                "・代表値: 複数回の計測試行で得られた中央値の中央値を採用しています。\n" +
                "・標準偏差: 計測中の負荷のばらつき度合いを示します（値が小さいほど負荷が安定）。\n" +
                "・P95: 全体の95%のフレームが収まる負荷水準です（一時的なスパイク負荷の指標）。\n" +
                "・GPU時間: 計測カメラ・追加カメラ・影生成・CustomRenderTexture更新の区間を、重なりを二重加算せず集計した時間です（描画命令待ち含む）。\n" +
                "・CPU時間: Av3Emulatorを含むメインスレッドのPlayerLoop処理時間です。\n" +
                "・フレーム時間: Unity全体の1フレーム更新間隔です。\n" +
                "※CPU時間とGPU時間は並行して処理されるため単純加算はできません。また、VRChatクライアント内での実FPSとは直接一致しません。\n");
            text.AppendLine("十分なサンプル数が得られなかった計測値には「参考値」を表示しています。\n");
            if (!string.IsNullOrEmpty(error)) text.AppendLine("中断理由: " + error.Replace("\n", " ") + "\n");
            foreach (var group in cases.GroupBy(x => new { x.environment, x.view, x.population }))
            {
                text.AppendLine("## " + group.Key.environment + " / " + group.Key.view + " / " + group.Key.population + "体\n");
                var baseline = group.Where(x => x.role == "baseline").ToArray();
                var control = group.Where(x => x.role == "control").ToArray();
                double gpuNoise = ControlNoise(baseline, control, true, profile), cpuNoise = ControlNoise(baseline, control, false, profile);
                text.AppendLine("同一アバター対照試験（再現性の確認）: GPU " + NoiseText(gpuNoise, ControlDeviationPercent(baseline, control, true, profile)) +
                    " / CPU " + NoiseText(cpuNoise, ControlDeviationPercent(baseline, control, false, profile)) + "\n");
                text.AppendLine("|対象|フレーム時間中央値 (ms)|GPU中央値 (ms)|GPU標準偏差 (ms)|GPU状態|差分 (ms)|差分 (%)|GPU判定|CPU中央値 (ms)|CPU標準偏差 (ms)|CPU状態|差分 (ms)|CPU判定|\n|---|---:|---:|---:|---|---:|---:|---|---:|---:|---|---:|---|");
                foreach (var entry in group.Where(x => x.role != "control").GroupBy(x => new { x.entryId, x.label, x.role }))
                {
                    bool target = entry.Key.role == "target";
                    text.AppendLine("|" + Md(entry.Key.label) + "|" + N(FrameTime(entry)) + "|" + N(Aggregate(entry, true)) + "|" + N(Variability(entry, true)) + "|" + AggregateStatus(entry, true, profile.rounds) +
                        "|" + N(target ? Difference(entry, baseline, true) : double.NaN) + "|" + N(target ? DifferencePercent(entry, baseline, true) : double.NaN) +
                        "|" + (target ? Assessment(entry, baseline, true, profile.rounds, gpuNoise) : "基準") + "|" + N(Aggregate(entry, false)) + "|" + N(Variability(entry, false)) + "|" + AggregateStatus(entry, false, profile.rounds) +
                        "|" + N(target ? Difference(entry, baseline, false) : double.NaN) + "|" + (target ? Assessment(entry, baseline, false, profile.rounds, cpuNoise) : "基準") + "|");
                }
                text.AppendLine();
                text.AppendLine("### 描画回数（回 / フレーム）\n\n" + CounterDescription + "\n");
                text.AppendLine("|対象|Draw Calls (中央値)|元との差分|有効試行|SetPass Calls (中央値)|元との差分|有効試行|\n|---|---:|---:|---:|---:|---:|---:|");
                foreach (var entry in group.Where(x => x.role != "control").GroupBy(x => new { x.entryId, x.label, x.role }))
                {
                    bool target = entry.Key.role == "target";
                    text.AppendLine("|" + Md(entry.Key.label) + "|" + CounterText(CounterMedian(entry, false)) +
                        "|" + (target ? CounterText(CounterDifference(entry, baseline, false, profile.rounds), true) : "—") + "|" + CounterRounds(entry, false) + "/" + profile.rounds +
                        "|" + CounterText(CounterMedian(entry, true)) + "|" + (target ? CounterText(CounterDifference(entry, baseline, true, profile.rounds), true) : "—") +
                        "|" + CounterRounds(entry, true) + "/" + profile.rounds + "|");
                }
                text.AppendLine();
            }
            text.AppendLine("## 計測状況\n\n|区間|有効GPU / 総数|有効CPU / 総数|状態|画像|\n|---|---:|---:|---|---|");
            foreach (var c in cases)
                text.AppendLine("|" + Md(c.id) + "|" + (c.samples - c.gpuMissing) + "/" + c.samples + "|" + (c.samples - c.cpuMissing) + "/" + c.samples + "|" +
                    Md(!string.IsNullOrEmpty(c.error) ? c.error : "GPU: " + c.gpuStatus + " / CPU: " + c.cpuStatus) + "|" + (string.IsNullOrEmpty(c.screenshot) ? "" : "[画像](" + c.screenshot + ")") + "|");
            text.AppendLine("\n### 区間ごとの計測時間\n\n|区間|実測秒数|有効GPU|有効CPU|総フレーム数|終了条件|\n|---|---:|---:|---:|---:|---|");
            foreach (var c in cases)
            foreach (var interval in c.sampling)
                text.AppendLine("|" + Md(c.id) + "|" + N(interval.seconds) + "|" + interval.gpuValidFrames + "|" + interval.cpuValidFrames + "|" + interval.totalFrames + "|" +
                    (!string.IsNullOrEmpty(c.error) ? "中断・" + Md(c.error) : interval.insufficientAtLimit ? "上限到達・フレーム不足または欠測過多" : interval.extended ? "自動延長して完了" : "完了") + "|");
            text.AppendLine("\n## " + BenchmarkUnitySettings.Title + "\n");
            if (unitySettings == null || unitySettings.Count == 0) text.AppendLine(BenchmarkUnitySettings.NotRecorded + "\n");
            else
            {
                text.AppendLine(BenchmarkUnitySettings.Description + "\n\n|設定|計測開始時の値|\n|---|---|");
                foreach (var setting in unitySettings) text.AppendLine("|" + Md(setting.name) + "|" + Md(setting.value) + "|");
            }
            text.AppendLine("\n詳細設定・動作記録・計測ごとの統計は report.json、全フレームの計測データは frames.csv に保存しています。元の値が0の場合や取得できなかった場合は、差の割合を表示しません。");
            return text.ToString();
        }

        public static double Aggregate(IEnumerable<BenchmarkCaseResult> results, bool gpu)
            => AvailableMedian(results.Select(x => gpu ? x.gpuMedianMs : x.cpuMedianMs));
        public static double Variability(IEnumerable<BenchmarkCaseResult> results, bool gpu)
            => AvailableMedian(results.Select(x => gpu ? x.gpuStandardDeviationMs : x.cpuStandardDeviationMs));
        public static double FrameTime(IEnumerable<BenchmarkCaseResult> results)
            => AvailableMedian(results.Select(x => x.frameTimeMedianMs));
        private static double AvailableMedian(IEnumerable<double> values) => BenchmarkStatistics.Median(values.Where(x => BenchmarkStatistics.IsFinite(x) && x >= 0));

        public const string CounterDescription = "Draw Calls（描画命令回数）および SetPass Calls（シェーダー切り替え回数）の計測値です。\n各計測回の中央値をもとに集計しています。差分は同一試行における元アバターとの比較値です。";
        private static double CounterValue(BenchmarkCaseResult result, bool setPass) => setPass ? result.setPassMedian : result.drawCallsMedian;
        private static bool HasCounter(BenchmarkCaseResult result, bool setPass) => BenchmarkStatistics.IsFinite(CounterValue(result, setPass)) && CounterValue(result, setPass) >= 0;
        public static double CounterMedian(IEnumerable<BenchmarkCaseResult> results, bool setPass) => AvailableMedian(results.Select(x => CounterValue(x, setPass)));
        public static int CounterRounds(IEnumerable<BenchmarkCaseResult> results, bool setPass) => results.Where(x => HasCounter(x, setPass)).Select(x => x.round).Distinct().Count();

        public static double CounterDifference(IEnumerable<BenchmarkCaseResult> results, BenchmarkCaseResult[] baselines, bool setPass, int rounds)
        {
            var rows = results.ToArray();
            if (rounds < 1 || rows.Length != rounds || baselines.Length != rounds ||
                rows.Select(x => x.round).Distinct().Count() != rounds || baselines.Select(x => x.round).Distinct().Count() != rounds ||
                rows.Concat(baselines).Any(x => x.round < 1 || x.round > rounds || !HasCounter(x, setPass) || !string.IsNullOrEmpty(x.error))) return double.NaN;
            var differences = new List<double>();
            foreach (var row in rows)
            {
                var baseline = baselines.FirstOrDefault(x => x.round == row.round && x.environment == row.environment &&
                    x.view == row.view && x.population == row.population);
                if (baseline == null) return double.NaN;
                differences.Add(CounterValue(row, setPass) - CounterValue(baseline, setPass));
            }
            return BenchmarkStatistics.Median(differences);
        }

        public static string CounterText(double value, bool difference = false)
            => BenchmarkStatistics.IsFinite(value) && (difference || value >= 0)
                ? (difference && value > 0 ? "+" : "") + value.ToString("0.##", CultureInfo.InvariantCulture) : "—";

        public static string AggregateStatus(IEnumerable<BenchmarkCaseResult> results, bool gpu, int rounds)
        {
            var rows = results.ToArray();
            int available = rows.Count(x => BenchmarkStatistics.IsFinite(gpu ? x.gpuMedianMs : x.cpuMedianMs) && (gpu ? x.gpuMedianMs : x.cpuMedianMs) >= 0);
            int eligible = rows.Count(x => gpu ? x.gpuUsable : x.cpuUsable);
            return (available == 0 ? "データなし" : Complete(rows, gpu, rounds) ? "正常" : "参考値") + "（有効試行: " + eligible + "/" + rounds + "回）";
        }

        public static string ReferenceDetails(IEnumerable<BenchmarkCaseResult> results, bool gpu)
        {
            var issues = results.Where(x => !(gpu ? x.gpuUsable : x.cpuUsable))
                .Select(x => new
                {
                    x.round,
                    reason = !string.IsNullOrEmpty(x.error) ? x.error :
                        gpu && !string.IsNullOrEmpty(x.gpuError) ? x.gpuError :
                        gpu && !string.IsNullOrEmpty(x.gpuComparisonIssue) ? x.gpuComparisonIssue :
                        gpu ? x.gpuStatus : x.cpuStatus
                }).GroupBy(x => x.reason);
            return string.Join("\n", issues.Select(group =>
                "計測" + string.Join("・", group.Select(x => x.round).Distinct().OrderBy(x => x)) + "回目: " +
                (string.IsNullOrEmpty(group.Key) ? "比較条件を満たしていません。" : group.Key)));
        }

        private static bool Complete(BenchmarkCaseResult[] rows, bool gpu, int rounds)
            => rounds > 0 && rows.Length == rounds && rows.Select(x => x.round).Distinct().Count() == rounds &&
               rows.All(x => x.round >= 1 && x.round <= rounds && (gpu ? x.gpuUsable : x.cpuUsable) &&
                   BenchmarkStatistics.IsFinite(gpu ? x.gpuMedianMs : x.cpuMedianMs) && (gpu ? x.gpuMedianMs : x.cpuMedianMs) >= 0);

        public static List<double> Paired(IEnumerable<BenchmarkCaseResult> values, BenchmarkCaseResult[] baselines, bool gpu)
            => ComparisonPairs(values, baselines, gpu).Select(x => Value(x.target, gpu) - Value(x.baseline, gpu)).ToList();

        private static double Value(BenchmarkCaseResult row, bool gpu) => gpu ? row.gpuMedianMs : row.cpuMedianMs;

        private static List<(BenchmarkCaseResult target, BenchmarkCaseResult baseline)> ComparisonPairs(
            IEnumerable<BenchmarkCaseResult> values, BenchmarkCaseResult[] baselines, bool gpu)
        {
            var result = new List<(BenchmarkCaseResult target, BenchmarkCaseResult baseline)>();
            var rows = values.ToArray();
            if (rows.GroupBy(x => x.round).Any(x => x.Count() != 1) || baselines.GroupBy(x => x.round).Any(x => x.Count() != 1)) return result;
            foreach (var value in rows)
            {
                if (value.round < 1) continue;
                var baseline = baselines.FirstOrDefault(x => x.round == value.round && x.environment == value.environment && x.view == value.view && x.population == value.population);
                if (baseline == null || !BenchmarkStatistics.IsFinite(Value(value, gpu)) || Value(value, gpu) < 0 ||
                    !BenchmarkStatistics.IsFinite(Value(baseline, gpu)) || Value(baseline, gpu) < 0) continue;
                result.Add((value, baseline));
            }
            return result;
        }

        public static double ControlNoise(BenchmarkCaseResult[] baselines, BenchmarkCaseResult[] controls, bool gpu, BenchmarkProfile profile)
        {
            if (profile.rounds < 3 || !Complete(baselines, gpu, profile.rounds) || !Complete(controls, gpu, profile.rounds)) return double.NaN;
            var deltas = Paired(controls, baselines, gpu);
            if (deltas.Count != profile.rounds) return double.NaN;
            foreach (var control in controls)
            {
                var baseline = baselines.First(x => x.round == control.round);
                if (string.IsNullOrEmpty(baseline.avatarBuildId) || baseline.avatarBuildId != control.avatarBuildId) return double.NaN;
            }
            return deltas.Select(Math.Abs).Max();
        }

        public static double ControlDeviationPercent(BenchmarkCaseResult[] baselines, BenchmarkCaseResult[] controls, bool gpu, BenchmarkProfile profile)
        {
            if (!BenchmarkStatistics.IsFinite(ControlNoise(baselines, controls, gpu, profile))) return double.NaN;
            return ComparisonPairs(controls, baselines, gpu)
                .Max(x => BenchmarkStatistics.ControlDeviationPercent(Value(x.baseline, gpu), Value(x.target, gpu)));
        }

        public static double Difference(IEnumerable<BenchmarkCaseResult> values, BenchmarkCaseResult[] baselines, bool gpu)
            => BenchmarkStatistics.Median(Paired(values, baselines, gpu));

        public static double DifferencePercent(IEnumerable<BenchmarkCaseResult> values, BenchmarkCaseResult[] baselines, bool gpu)
        {
            var pairs = ComparisonPairs(values, baselines, gpu);
            return BenchmarkStatistics.PercentChange(BenchmarkStatistics.Median(pairs.Select(x => Value(x.baseline, gpu))),
                BenchmarkStatistics.Median(pairs.Select(x => Value(x.target, gpu))));
        }

        public static string Assessment(IEnumerable<BenchmarkCaseResult> values, BenchmarkCaseResult[] baselines, bool gpu, int rounds, double controlNoise)
        {
            var rows = values.ToArray();
            if (!BenchmarkStatistics.IsFinite(Difference(rows, baselines, gpu))) return "算出不可";
            if (!Complete(rows, gpu, rounds) || !Complete(baselines, gpu, rounds) || !BenchmarkStatistics.IsFinite(controlNoise)) return "参考値";
            return BenchmarkStatistics.AssessDifference(Paired(rows, baselines, gpu), controlNoise);
        }

        public static string NoiseText(double noise, double percent)
        {
            if (!BenchmarkStatistics.IsFinite(noise) || double.IsNaN(percent))
                return "※注意: 対照アバターの再現性を確認できませんでした（差分は参考値となります）。";
            if (double.IsPositiveInfinity(percent))
                return "※注意: 基準値が0msのため変動率を算出できません（最大変動 " + N(noise) + " ms / 差分は参考値）。";
            string value = "対照の変動最大 " + N(percent) + "% (" + N(noise) + " ms)";
            switch (BenchmarkStatistics.ControlWarning(percent))
            {
                case BenchmarkControlWarning.High: return "※警告: " + value + "（変動が20%を超えているため、差分の再現性に注意してください）";
                case BenchmarkControlWarning.Notice: return "※注意: " + value + "（変動が10%を超えています）";
                default: return value;
            }
        }
        public static string Metric(double value) => value >= 0 ? N(value) : "—";
        public static string N(double value) => BenchmarkStatistics.IsFinite(value) ? value.ToString("0.######", CultureInfo.InvariantCulture) : "—";
        private static string Md(string value) => (value ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        private static string Cell(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        private static void WriteAtomic(string path, string value)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, value, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
    }
}
