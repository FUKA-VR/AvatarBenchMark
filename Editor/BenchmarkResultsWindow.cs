using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FUKA.AvatarBenchmark.Editor
{
    public sealed class BenchmarkResultsWindow : EditorWindow
    {
        [SerializeField] private string directory;
        private BenchmarkReport report;
        private BenchmarkProfile profile;
        private Vector2 scroll;
        private Vector2 errorScroll;
        private string loggedErrorDetails;
        private string error;
        private bool showUnitySettings;

        public static void Open(string output)
        {
            var window = GetWindow<BenchmarkResultsWindow>("ギミック負荷検証 - 計測結果");
            window.minSize = new Vector2(1160, 550);
            window.directory = output;
            window.Reload();
        }

        private void OnEnable() { if (!string.IsNullOrEmpty(directory)) Reload(); }
        private void OnDisable()
        {
            if (profile) DestroyImmediate(profile);
        }

        private void Reload()
        {
            try
            {
                if (profile) DestroyImmediate(profile);
                string json = File.ReadAllText(Path.Combine(directory, "report.json"));
                report = BenchmarkReport.Parse(json);
                loggedErrorDetails = report.LoggedErrorDetails();
                profile = CreateInstance<BenchmarkProfile>();
                JsonUtility.FromJsonOverwrite(report.profileJson, profile);
                error = null;
            }
            catch (Exception e) { error = e.Message; report = null; }
            Repaint();
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("結果フォルダを開く", GUILayout.Width(130))) EditorUtility.RevealInFinder(directory);
                if (GUILayout.Button("再読み込み", GUILayout.Width(85))) Reload();
                if (GUILayout.Button("HTMLレポートを開く", GUILayout.Width(145)) && report != null && profile) OpenHtmlReport();
                EditorGUILayout.LabelField(directory ?? "");
            }
            if (error != null) { EditorGUILayout.HelpBox(error, MessageType.Error); return; }
            if (report == null) return;
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("ツールバージョン", report.ToolVersionText, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(report.status + " / " + report.unityVersion + " / " + report.gpu + " / " + report.graphicsApi);
            EditorGUILayout.LabelField("所要時間", report.DurationText, EditorStyles.boldLabel);
            if (report.HasLoggedErrors)
            {
                EditorGUILayout.HelpBox(report.LoggedErrorWarningText(), MessageType.Warning);
                errorScroll = EditorGUILayout.BeginScrollView(errorScroll, GUILayout.MaxHeight(160));
                float height = EditorStyles.wordWrappedLabel.CalcHeight(new GUIContent(loggedErrorDetails), Mathf.Max(200, position.width - 64));
                EditorGUILayout.SelectableLabel(loggedErrorDetails, EditorStyles.wordWrappedLabel, GUILayout.Height(height));
                EditorGUILayout.EndScrollView();
            }
            EditorGUILayout.HelpBox("【計測結果の見方と注意点】\n" +
                "・本ツールはUnity Editor環境での負荷計測であり、VRChatクライアント内での実負荷・実FPSと完全に一致するものではありません。\n" +
                "・数値は同一セッション内での「元アバター」との相対差分を比較するためのものです（他PCの数値との比較や、別タイミングでの計測値とは直接比較できません）。\n" +
                "・PC環境（CPU/GPUのボトルネック等）によって影響の出方が異なる場合があります。\n" +
                "・なるべく他のPC処理の影響を減らすため、計測時はPCを再起動して、計測するUnityだけ立ち上がってる状態にしてください。", MessageType.Info);
            if (!string.IsNullOrEmpty(report.error)) EditorGUILayout.HelpBox(report.error, MessageType.Error);
            foreach (var group in report.cases.GroupBy(x => new { x.environment, x.view, x.population }))
            {
                var baseline = group.Where(x => x.role == "baseline").ToArray();
                var control = group.Where(x => x.role == "control").ToArray();
                double gpuNoise = BenchmarkReport.ControlNoise(baseline, control, true, profile);
                double cpuNoise = BenchmarkReport.ControlNoise(baseline, control, false, profile);
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField(Path.GetFileNameWithoutExtension(group.Key.environment) + " / " + group.Key.view + " / " + group.Key.population + "体", EditorStyles.boldLabel);
                DrawControl("GPU", gpuNoise, BenchmarkReport.ControlDeviationPercent(baseline, control, true, profile));
                DrawControl("CPU", cpuNoise, BenchmarkReport.ControlDeviationPercent(baseline, control, false, profile));
                Row("対象", "フレーム (ms)", "GPU (ms)", "差分 (ms)", "差分 (%)", "GPU判定", "CPU (ms)", "差分 (ms)", "CPU判定");
                foreach (var entry in group.Where(x => x.role != "control").GroupBy(x => new { x.entryId, x.label, x.role }))
                {
                    bool target = entry.Key.role == "target";
                    double gpu = BenchmarkReport.Aggregate(entry, true);
                    double cpu = BenchmarkReport.Aggregate(entry, false);
                    Row(entry.Key.label, N(BenchmarkReport.FrameTime(entry)), N(gpu),
                        target ? N(BenchmarkReport.Difference(entry, baseline, true)) : "—",
                        target ? N(BenchmarkReport.DifferencePercent(entry, baseline, true)) : "—",
                        target ? BenchmarkReport.Assessment(entry, baseline, true, profile.rounds, gpuNoise) : "基準", N(cpu),
                        target ? N(BenchmarkReport.Difference(entry, baseline, false)) : "—",
                        target ? BenchmarkReport.Assessment(entry, baseline, false, profile.rounds, cpuNoise) : "基準");
                    EditorGUILayout.LabelField("    GPU: " + BenchmarkReport.AggregateStatus(entry, true, profile.rounds) + " / CPU: " + BenchmarkReport.AggregateStatus(entry, false, profile.rounds), EditorStyles.miniLabel);
                    string gpuDetails = BenchmarkReport.ReferenceDetails(entry, true);
                    string cpuDetails = BenchmarkReport.ReferenceDetails(entry, false);
                    if (!string.IsNullOrEmpty(gpuDetails))
                        EditorGUILayout.HelpBox("GPUの判定理由\n" + gpuDetails, MessageType.Warning);
                    if (!string.IsNullOrEmpty(cpuDetails))
                        EditorGUILayout.HelpBox("CPUの判定理由\n" + cpuDetails, MessageType.Warning);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawBars(group, true); DrawBars(group, false);
                }
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("描画回数（回 / フレーム）", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(BenchmarkReport.CounterDescription, EditorStyles.wordWrappedMiniLabel);
                CounterRow("対象", "Draw Calls (中央値)", "元との差分", "有効試行", "SetPass Calls (中央値)", "元との差分", "有効試行");
                foreach (var entry in group.Where(x => x.role != "control").GroupBy(x => new { x.entryId, x.label, x.role }))
                {
                    bool target = entry.Key.role == "target";
                    CounterRow(entry.Key.label, BenchmarkReport.CounterText(BenchmarkReport.CounterMedian(entry, false)),
                        target ? BenchmarkReport.CounterText(BenchmarkReport.CounterDifference(entry, baseline, false, profile.rounds), true) : "—",
                        BenchmarkReport.CounterRounds(entry, false) + " / " + profile.rounds,
                        BenchmarkReport.CounterText(BenchmarkReport.CounterMedian(entry, true)),
                        target ? BenchmarkReport.CounterText(BenchmarkReport.CounterDifference(entry, baseline, true, profile.rounds), true) : "—",
                        BenchmarkReport.CounterRounds(entry, true) + " / " + profile.rounds);
                }
            }
            EditorGUILayout.Space(15);
            showUnitySettings = EditorGUILayout.Foldout(showUnitySettings, BenchmarkUnitySettings.Title, true);
            if (showUnitySettings)
            {
                if (report.unitySettings == null || report.unitySettings.Count == 0)
                    EditorGUILayout.LabelField(BenchmarkUnitySettings.NotRecorded, EditorStyles.wordWrappedLabel);
                else
                {
                    EditorGUILayout.LabelField(BenchmarkUnitySettings.Description, EditorStyles.wordWrappedLabel);
                    foreach (var setting in report.unitySettings)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(setting.name, GUILayout.Width(300));
                        EditorGUILayout.SelectableLabel(setting.value, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private static void DrawControl(string metric, double noise, double percent)
        {
            var warning = BenchmarkStatistics.ControlWarning(percent);
            string text = metric + ": " + BenchmarkReport.NoiseText(noise, percent);
            if (warning == BenchmarkControlWarning.None)
            {
                EditorGUILayout.LabelField(text, EditorStyles.wordWrappedLabel);
                return;
            }
            var style = new GUIStyle(EditorStyles.wordWrappedLabel) { padding = new RectOffset(8, 8, 5, 5) };
            style.normal.textColor = warning == BenchmarkControlWarning.Notice ? Color.white : new Color(1f, .4f, .4f);
            Rect rect = GUILayoutUtility.GetRect(new GUIContent(text), style, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(.15f, .20f, .26f));
            GUI.Label(rect, text, style);
        }

        private void OpenHtmlReport()
        {
            try
            {
                report.SaveViews(directory, profile);
                // Pass a file path; Windows may fail to resolve a percent-escaped file URL.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.GetFullPath(Path.Combine(directory, "report.html")),
                    UseShellExecute = true
                })?.Dispose();
                error = null;
            }
            catch (Exception e) { error = "HTMLレポートを開けませんでした。\n" + e.Message; }
        }

        private static string N(double value) => BenchmarkReport.N(value);
        private void DrawBars(System.Collections.Generic.IEnumerable<BenchmarkCaseResult> cases, bool gpu)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField((gpu ? "GPU時間" : "CPU時間") + " 中央値とばらつき（標準偏差）", EditorStyles.boldLabel);
                var rows = cases.Where(x => x.role != "control").GroupBy(x => new { x.entryId, x.role }).ToArray();
                double max = rows.Select(x => BenchmarkReport.Aggregate(x, gpu)).Where(BenchmarkStatistics.IsFinite).DefaultIfEmpty(0).Max();
                foreach (var row in rows)
                {
                    double value = BenchmarkReport.Aggregate(row, gpu);
                    double deviation = BenchmarkReport.Variability(row, gpu);
                    EditorGUILayout.LabelField(row.First().label + " / " + BenchmarkReport.AggregateStatus(row, gpu, profile.rounds), EditorStyles.miniLabel);
                    Rect area = GUILayoutUtility.GetRect(150, 24, GUILayout.ExpandWidth(true));
                    EditorGUI.DrawRect(new Rect(area.x, area.y + 6, Math.Max(0, area.width - 195), 9), new Color(.2f, .2f, .2f, .12f));
                    if (BenchmarkStatistics.IsFinite(value))
                        EditorGUI.DrawRect(new Rect(area.x, area.y + 6, (float)(max > 0 ? Math.Max(0, area.width - 195) * value / max : 0), 9), gpu ? new Color(.05f, .65f, .65f) : new Color(.55f, .45f, .85f));
                    GUI.Label(new Rect(area.xMax - 185, area.y, 185, 24), N(value) + " / " + N(deviation) + " ms", EditorStyles.miniLabel);
                }
                EditorGUILayout.LabelField("※バーは各試行の中央値を表しています。右側の数値は「中央値 / 標準偏差」です。", EditorStyles.wordWrappedMiniLabel);
            }
        }
        private static void CounterRow(params string[] values)
        {
            using (new EditorGUILayout.HorizontalScope())
                for (int i = 0; i < values.Length; i++)
                    GUILayout.Label(new GUIContent(values[i], values[i]), GUILayout.Width(i == 0 ? 200 : 125));
        }

        private static void Row(params string[] values)
        {
            using (new EditorGUILayout.HorizontalScope())
                for (int i = 0; i < values.Length; i++)
                    GUILayout.Label(new GUIContent(values[i], values[i]), GUILayout.Width(i == 0 ? 200 : i == 5 || i == 8 ? 135 : i == 1 ? 100 : 85));
        }
    }
}
