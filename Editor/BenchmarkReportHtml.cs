using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace FUKA.AvatarBenchmark.Editor
{
    // A self-contained report: no CDN, network requests or script execution are needed.
    public static class BenchmarkReportHtml
    {
        private static string H(string value) => WebUtility.HtmlEncode(value ?? "");
        private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static string N(double value) => BenchmarkStatistics.IsFinite(value) && value >= 0 ? value.ToString("0.000", CultureInfo.InvariantCulture) : "—";

        public static string Create(BenchmarkReport report, BenchmarkProfile profile)
        {
            var html = new StringBuilder(@"<!doctype html><html lang='ja'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>VRChat ギミック負荷検証レポート</title><style>
:root{color-scheme:light;--ink:#182a39;--muted:#536977;--line:#dbe3e8;--gpu:#007d82;--cpu:#6653ae}
*{box-sizing:border-box}body{margin:0;background:#f2f5f7;color:var(--ink);font:15px/1.65 'Yu Gothic UI',Meiryo,sans-serif}
main{max-width:1260px;margin:auto;padding:38px 28px 70px}h1{font-size:30px;margin:4px 0 10px}h2{font-size:22px;margin:0 0 12px}h3{margin:0 0 10px;font-size:17px}
p{margin:8px 0}.eyebrow{font-size:12px;letter-spacing:.14em;color:var(--muted)}.muted,small{color:var(--muted)}.card,details{background:white;border:1px solid var(--line);border-radius:12px;padding:22px;margin:18px 0}.grid{display:grid;grid-template-columns:1fr 1fr;gap:22px}.grid .card{margin:0;padding:18px}.note{background:#e9f2f5;padding:12px 16px;border-radius:8px}.warn{color:#8c4b00;background:#fff3dd;padding:8px 12px;border-radius:6px}.ok{color:#006a50}.bad{color:#9b5100}table{width:100%;border-collapse:collapse;font-size:14px}th,td{text-align:right;border-bottom:1px solid var(--line);padding:10px 8px;vertical-align:top}th{font-size:12px;color:var(--muted)}th:first-child,td:first-child{text-align:left}td:first-child{overflow-wrap:anywhere}a{color:#006da0}.bar{height:11px;min-width:1px;border-radius:3px;background:var(--gpu);margin:6px 0 2px}.cpu .bar{background:var(--cpu)}.value{font-variant-numeric:tabular-nums;font-size:18px;font-weight:600}.badge{display:inline-block;font-size:12px;border:1px solid var(--line);border-radius:4px;padding:1px 8px;margin-left:8px}.scroll{overflow-x:auto}summary{cursor:pointer;font-weight:600}details[open]>summary{margin-bottom:20px}svg{width:100%;height:auto;display:block}svg text{font-family:'Yu Gothic UI',Meiryo,sans-serif;fill:#536977;font-size:12px}.stats{font-variant-numeric:tabular-nums}.legend{display:flex;gap:18px;font-size:12px;color:var(--muted)}.swatch{display:inline-block;width:22px;height:8px;background:#cbe4e6;margin-right:5px}.line{height:3px;background:var(--gpu);vertical-align:middle}pre{font-size:12px;white-space:pre-wrap;overflow-wrap:anywhere}.links{display:flex;gap:20px}.detail-note{font-size:12px}.metric-label{font-size:12px;color:var(--muted)}
.control-notice,.control-high{background:#263442;padding:10px 12px;border-radius:6px}.control-notice{color:#fff}.control-high{color:#f66}
@media(max-width:840px){main{padding:20px 14px}.grid{grid-template-columns:1fr}.card,details{padding:16px}}@media print{body{background:white}main{padding:0}details{break-inside:avoid}.card{break-inside:avoid}}
</style><main>");
            html.Append("<div class='eyebrow'>FUKA / AVATAR BENCHMARK</div><h1>VRChat ギミック負荷検証レポート</h1>");
            html.Append("<p><strong>ツールバージョン: ").Append(H(report.ToolVersionText)).Append("</strong></p>");
            html.Append("<p>").Append(H(report.status)).Append(" · ").Append(H(report.gpu)).Append(" · Unity ").Append(H(report.unityVersion)).Append(" / ").Append(H(report.graphicsApi)).Append("</p>");
            html.Append("<p class='muted'>").Append(H(report.cpu)).Append(" · ").Append(H(report.startedUtc)).Append("</p>");
            html.Append("<p><strong>計測所要時間: ").Append(H(report.DurationText)).Append("</strong></p>");
            if (report.HasLoggedErrors)
                html.Append("<div class='warn'><strong>⚠ ").Append(H(report.LoggedErrorWarningText()).Replace("\n", "<br>"))
                    .Append("</strong><pre style='white-space:pre-wrap;overflow-wrap:anywhere;max-height:18em;overflow:auto'>")
                    .Append(H(report.LoggedErrorDetails())).Append("</pre></div>");
            html.Append("<p class='note'>同一の測定区間におけるCPU処理時間・GPU描画時間・Unityフレームレート間隔を記録しています。</p>");
            html.Append("<p class='links'><a href='summary.md'>マークダウン要約</a><a href='report.json'>詳細JSONデータ</a><a href='frames.csv'>全フレームCSV</a></p>");
            html.Append("<div class='note'><strong>【データの見方】</strong><br>・<strong>中央値</strong>: 計測期間中の代表的な負荷値（値が小さいほど軽量）<br>・<strong>標準偏差</strong>: アニメーション等による負荷のばらつき（値が小さいほど安定）<br>※グラフのバーは複数回の試行で得られた中央値を表しています。サンプル数不足や時刻取得の異常などで比較条件を満たさない結果は、理由を添えて参考値として表示します。</div>");
            if (!string.IsNullOrEmpty(report.error)) html.Append("<p class='warn'>").Append(H(report.error)).Append("</p>");
            foreach (var group in report.cases.GroupBy(x => new { x.environment, x.view, x.population }))
            {
                var cases = group.ToArray();
                var baseline = cases.Where(x => x.role == "baseline").ToArray();
                var control = cases.Where(x => x.role == "control").ToArray();
                double gpuNoise = BenchmarkReport.ControlNoise(baseline, control, true, profile);
                double cpuNoise = BenchmarkReport.ControlNoise(baseline, control, false, profile);
                html.Append("<section class='card'><h2>").Append(H(Path.GetFileNameWithoutExtension(group.Key.environment))).Append(" / ").Append(H(group.Key.view)).Append(" / ").Append(group.Key.population).Append("体</h2>");
                html.Append("<div class='grid'>");
                Comparison(html, cases, baseline, true, gpuNoise, BenchmarkReport.ControlDeviationPercent(baseline, control, true, profile), profile);
                Comparison(html, cases, baseline, false, cpuNoise, BenchmarkReport.ControlDeviationPercent(baseline, control, false, profile), profile);
                html.Append("</div><p class='detail-note'>※GPU時間は追加カメラやRenderTextureへの描画・影生成・CustomRenderTexture更新を含む区間時間（描画命令待ち含む）です。重なる区間は1回だけ数えます。CPU時間はメインスレッドのPlayerLoop処理時間です。両者は並行動作するため加算はできません。0.000 msには測定限界以下の極小値も含まれます。</p>");
                DrawCounters(html, cases, baseline, profile.rounds);
                html.Append("<h3>Unity フレーム時間の中央値</h3><p class='detail-note'>※Unity Editor全体のフレーム更新間隔です。内部処理や待機時間を含み、VRChatゲーム内でのFPSとは直接一致しません。</p><table><tr><th>対象</th><th>中央値 (ms)</th><th>有効試行回数</th></tr>");
                foreach (var row in cases.Where(x => x.role != "control").GroupBy(x => new { x.entryId, x.role }))
                    html.Append("<tr><td>").Append(H(row.First().label)).Append("</td><td>").Append(N(BenchmarkReport.FrameTime(row))).Append("</td><td>").Append(row.Count(x => x.frameTimeMedianMs >= 0)).Append(" / ").Append(profile.rounds).Append("</td></tr>");
                html.Append("</table>");
                html.Append("</section>");
            }
            html.Append("<section class='card'><h2>時系列推移と各計測回の詳細</h2><p>アニメーションやパーティクル等によって負荷は時間変動します。P95は「全体の95%のフレームがこの負荷以下に収まる」水準を示し、一時的なスパイク負荷の確認に役立ちます。</p><div class='legend'><span><i class='swatch line'></i>区間中央値</span><span><i class='swatch'></i>区間最小～最大</span></div><p class='detail-note'>横軸: 経過時間（秒） / 縦軸: 処理時間 (ms)。各グラフは自動スケールされます。データ点にカーソルを合わせると詳細が表示されます。</p></section>");
            foreach (var c in report.cases)
            {
                html.Append("<details").Append(c.role == "target" && c.round == 1 ? " open" : "").Append(" id='").Append(H(c.id)).Append("'><summary>").Append(H(c.label)).Append(" · ").Append(H(c.view)).Append(" · ").Append(c.population).Append("体 · 計測").Append(c.round).Append("回目 <span class='badge'>").Append(H(c.id)).Append("</span></summary>");
                if (!string.IsNullOrEmpty(c.error)) html.Append("<p class='warn'>").Append(H(c.error)).Append("</p>");
                html.Append("<div class='scroll'><table class='stats'><tr><th>対象</th><th>中央値 (ms)</th><th>標準偏差 (ms)</th><th>P95 (ms)</th><th>最大 (ms)</th><th>有効フレーム数</th></tr>");
                StatRow(html, "GPU" + (c.gpuUsable ? "" : c.gpuMedianMs >= 0 ? "（参考値）" : "（データなし）"), c.gpuMedianMs, c.gpuStandardDeviationMs, c.gpuP95Ms, c.gpuMaxMs, c.samples - c.gpuMissing, c.samples);
                StatRow(html, "CPU" + (c.cpuUsable ? "" : c.cpuMedianMs >= 0 ? "（参考値）" : "（データなし）"), c.cpuMedianMs, c.cpuStandardDeviationMs, c.cpuP95Ms, c.cpuMaxMs, c.samples - c.cpuMissing, c.samples);
                StatRow(html, "Unity フレーム", c.frameTimeMedianMs, c.frameTimeStandardDeviationMs, c.frameTimeP95Ms, c.frameTimeMaxMs, c.samples - c.frameTimeMissing, c.samples);
                html.Append("</table></div><p>描画回数の中央値: Draw Calls <strong>")
                    .Append(BenchmarkReport.CounterText(c.drawCallsMedian)).Append("</strong> / SetPass <strong>")
                    .Append(BenchmarkReport.CounterText(c.setPassMedian)).Append("</strong></p>");
                html.Append("<div class='grid'><div><h3>GPU</h3>"); Timeline(html, c, 0);
                html.Append("</div><div><h3>CPU</h3>"); Timeline(html, c, 1); html.Append("</div></div>");
                html.Append("<h3>Unity フレーム時間</h3>"); Timeline(html, c, 2);
                html.Append("<p class='detail-note'>GPU: ").Append(H(c.gpuStatus)).Append("<br>CPU: ").Append(H(c.cpuStatus)).Append("</p>");
                html.Append("<details><summary>計測ログ・動作詳細</summary><table><tr><th>実測時間</th><th>有効GPU</th><th>有効CPU</th><th>総フレーム数</th><th>完了状態</th></tr>");
                foreach (var interval in c.sampling)
                    html.Append("<tr><td>").Append(N(interval.seconds)).Append("</td><td>").Append(interval.gpuValidFrames).Append("</td><td>").Append(interval.cpuValidFrames).Append("</td><td>").Append(interval.totalFrames).Append("</td><td>").Append(!string.IsNullOrEmpty(c.error) ? "中断・" + H(c.error) : interval.insufficientAtLimit ? "上限到達・フレーム不足" : interval.extended ? "自動延長" : "完了").Append("</td></tr>");
                html.Append("</table>");
                html.Append("<p>計測終了後のGPU結果待ち: ").Append(N(c.gpuDrainSeconds)).Append("秒 / 待機終了時の未取得 ").Append(c.gpuPendingAtEnd).Append("フレーム。この待機時間は計測時間に含めません。</p>");
                html.Append("<pre>").Append(H(c.runtimeAudit)).Append("</pre></details>");
                if (!string.IsNullOrEmpty(c.screenshot)) html.Append("<p><a href='").Append(H(c.screenshot)).Append("'>この条件の計測画像</a></p>");
                html.Append("</details>");
            }
            html.Append("<details><summary>").Append(H(BenchmarkUnitySettings.Title)).Append("</summary>");
            if (report.unitySettings == null || report.unitySettings.Count == 0)
                html.Append("<p>").Append(H(BenchmarkUnitySettings.NotRecorded)).Append("</p>");
            else
            {
                html.Append("<p>").Append(H(BenchmarkUnitySettings.Description)).Append("</p><table><tr><th>設定項目</th><th>計測開始時の値</th></tr>");
                foreach (var setting in report.unitySettings)
                    html.Append("<tr><td>").Append(H(setting.name)).Append("</td><td>").Append(H(setting.value)).Append("</td></tr>");
                html.Append("</table>");
            }
            html.Append("</details>");
            html.Append("<p class='muted'>【対照試験について】同一セッション内で「元アバター」を再生成して再度計測を行い、PC環境やバックグラウンド処理による負荷のブレ（測定再現性）を検証しています。変動が10%を超えた場合は注意、20%を超えた場合は警告が表示されます。本ツールはUnity Editor上での相対的な負荷差分を検証するものであり、VRChatクライアント内での絶対的な動作パフォーマンスやFPSを保証するものではありません。</p></main></html>");
            return html.ToString();
        }

        private static void DrawCounters(StringBuilder html, BenchmarkCaseResult[] cases, BenchmarkCaseResult[] baseline, int rounds)
        {
            html.Append("<h3>描画回数（回 / フレーム）</h3><p class='detail-note'>").Append(H(BenchmarkReport.CounterDescription))
                .Append("</p><div class='scroll'><table class='stats'><tr><th>対象</th><th>Draw Calls<br>(中央値)</th><th>元との差分</th><th>有効試行</th><th>SetPass Calls<br>(中央値)</th><th>元との差分</th><th>有効試行</th></tr>");
            foreach (var row in cases.Where(x => x.role != "control").GroupBy(x => new { x.entryId, x.role }))
            {
                bool target = row.Key.role == "target";
                html.Append("<tr><td>").Append(H(row.First().label)).Append("</td><td>").Append(BenchmarkReport.CounterText(BenchmarkReport.CounterMedian(row, false)))
                    .Append("</td><td>").Append(target ? BenchmarkReport.CounterText(BenchmarkReport.CounterDifference(row, baseline, false, rounds), true) : "—")
                    .Append("</td><td>").Append(BenchmarkReport.CounterRounds(row, false)).Append(" / ").Append(rounds)
                    .Append("</td><td>").Append(BenchmarkReport.CounterText(BenchmarkReport.CounterMedian(row, true)))
                    .Append("</td><td>").Append(target ? BenchmarkReport.CounterText(BenchmarkReport.CounterDifference(row, baseline, true, rounds), true) : "—")
                    .Append("</td><td>").Append(BenchmarkReport.CounterRounds(row, true)).Append(" / ").Append(rounds).Append("</td></tr>");
            }
            html.Append("</table></div>");
        }

        private static void Comparison(StringBuilder html, BenchmarkCaseResult[] cases, BenchmarkCaseResult[] baseline, bool gpu, double noise, double deviationPercent, BenchmarkProfile profile)
        {
            var rows = cases.Where(x => x.role != "control").GroupBy(x => new { x.entryId, x.role }).ToArray();
            double max = rows.Select(x => BenchmarkReport.Aggregate(x, gpu)).Where(BenchmarkStatistics.IsFinite).DefaultIfEmpty(0).Max();
            var warning = BenchmarkStatistics.ControlWarning(deviationPercent);
            string warningClass = warning == BenchmarkControlWarning.High ? "control-high" : warning == BenchmarkControlWarning.Notice ? "control-notice" : "ok";
            html.Append("<div class='card ").Append(gpu ? "gpu" : "cpu").Append("'><h3>").Append(gpu ? "GPU時間（描画・影生成・描画先の更新）" : "CPU時間（PlayerLoop）").Append("</h3><p class='").Append(warningClass).Append("'>対照試験: ").Append(H(BenchmarkReport.NoiseText(noise, deviationPercent))).Append("</p><table><tr><th>対象</th><th>中央値 (ms)</th><th>標準偏差 (ms)</th></tr>");
            foreach (var row in rows)
            {
                double value = BenchmarkReport.Aggregate(row, gpu);
                double delta = BenchmarkReport.Difference(row, baseline, gpu);
                double percent = BenchmarkReport.DifferencePercent(row, baseline, gpu);
                bool target = row.Key.role == "target";
                html.Append("<tr><td style='width:45%'>").Append(H(row.First().label)).Append("</td><td style='width:33%'><span class='value'>").Append(N(value)).Append("</span>");
                if (BenchmarkStatistics.IsFinite(value)) html.Append("<div class='bar' style='width:").Append(F(max > 0 ? 100 * value / max : 0)).Append("%'></div>");
                html.Append("<br><small>").Append(H(BenchmarkReport.AggregateStatus(row, gpu, profile.rounds))).Append("</small></td><td>").Append(N(BenchmarkReport.Variability(row, gpu))).Append("</td></tr>");
                if (target)
                    html.Append("<tr><td colspan='3'><small>元との差分 ").Append(BenchmarkStatistics.IsFinite(delta) ? Signed(delta) + " ms" : "—")
                        .Append(" / ").Append(BenchmarkStatistics.IsFinite(percent) ? Signed(percent) + "%" : "—")
                        .Append(" · ").Append(H(BenchmarkReport.Assessment(row, baseline, gpu, profile.rounds, noise))).Append("</small></td></tr>");
                string details = BenchmarkReport.ReferenceDetails(row, gpu);
                if (!string.IsNullOrEmpty(details))
                    html.Append("<tr><td colspan='3'><p class='warn'><strong>判定理由</strong><br>")
                        .Append(H(details).Replace("\n", "<br>")).Append("</p></td></tr>");
            }
            html.Append("</table><p class='detail-note'>計測ごとの中央値の最小～最大: ");
            foreach (var row in rows)
            {
                var values = row.Select(x => gpu ? x.gpuMedianMs : x.cpuMedianMs).Where(x => BenchmarkStatistics.IsFinite(x) && x >= 0).ToArray();
                html.Append(H(row.First().label)).Append(" ").Append(values.Length > 0 ? N(values.Min()) + "～" + N(values.Max()) : "取得なし").Append(" ms（").Append(values.Length).Append(" / ").Append(profile.rounds).Append("回） / ");
            }
            html.Append("</p></div>");
        }

        private static string Signed(double value) => (value > 0 ? "+" : "") + value.ToString("0.000", CultureInfo.InvariantCulture);

        private static void StatRow(StringBuilder html, string label, double median, double sd, double p95, double max, int count, int total)
            => html.Append("<tr><td>").Append(label).Append("</td><td>").Append(N(median)).Append("</td><td>").Append(N(sd)).Append("</td><td>").Append(N(p95)).Append("</td><td>").Append(N(max)).Append("</td><td>").Append(count).Append(" / ").Append(total).Append("</td></tr>");

        private static void Timeline(StringBuilder html, BenchmarkCaseResult result, int metric)
        {
            bool gpu = metric == 0, frameTime = metric == 2;
            string label = frameTime ? "Unity フレーム" : gpu ? "GPU" : "CPU";
            var interval = result.sampling.FirstOrDefault();
            var frames = result.frames.Where(x => frameTime ? x.frameTimeValid : gpu ? x.gpuValid : x.cpuValid).ToArray();
            if (interval == null || frames.Length == 0) { html.Append("<p class='warn'>表示できる計測データがありません。</p>"); return; }
            if (!frameTime && !(gpu ? result.gpuUsable : result.cpuUsable))
                html.Append("<p class='warn'><strong>参考値の理由</strong><br>")
                    .Append(H(BenchmarkReport.ReferenceDetails(new[] { result }, gpu)).Replace("\n", "<br>"))
                    .Append("</p>");
            Func<BenchmarkSample, double> value = frame => (frameTime ? frame.frameTimeNs : gpu ? frame.gpuNs : frame.cpuNs) / 1e6;
            double maximum = Math.Max(.001, frames.Max(value) * 1.08), seconds = Math.Max(.001, interval.seconds);
            const int bins = 80;
            var buckets = frames.GroupBy(x => Math.Max(0, Math.Min(bins - 1, (int)((x.realtime - interval.startedRealtime) / seconds * bins)))).OrderBy(x => x.Key).ToArray();
            string color = frameTime ? "#aa691f" : gpu ? "#007d82" : "#6653ae";
            html.Append("<svg viewBox='0 0 560 230' role='img' aria-label='").Append(label).Append("時間の変化'><text x='5' y='16'>ms</text>");
            for (int i = 0; i <= 4; i++)
            {
                double y = 24 + i * 40;
                html.Append("<path d='M54 ").Append(F(y)).Append(" H548' stroke='#dbe3e8'/><text x='48' y='").Append(F(y + 4)).Append("' text-anchor='end'>").Append(N(maximum * (4 - i) / 4)).Append("</text>");
                html.Append("<text x='").Append(F(54 + i * 123.5)).Append("' y='207' text-anchor='").Append(i == 0 ? "start" : i == 4 ? "end" : "middle").Append("'>").Append(F(seconds * i / 4)).Append("s</text>");
            }
            var path = new StringBuilder(); int previous = -2;
            foreach (var bucket in buckets)
            {
                var values = bucket.Select(value).ToArray();
                double x = 54 + (bucket.Key + .5) / bins * 494;
                double lo = 184 - values.Min() / maximum * 160, hi = 184 - values.Max() / maximum * 160;
                double mid = 184 - BenchmarkStatistics.Median(values) / maximum * 160;
                html.Append("<path d='M").Append(F(x)).Append(' ').Append(F(lo)).Append(" V").Append(F(hi)).Append("' stroke='").Append(color).Append("' stroke-opacity='.22' stroke-width='6'/>");
                path.Append(previous + 1 == bucket.Key ? " L" : " M").Append(F(x)).Append(' ').Append(F(mid)); previous = bucket.Key;
                html.Append("<circle cx='").Append(F(x)).Append("' cy='").Append(F(mid)).Append("' r='1.4' fill='").Append(color).Append("'/><rect x='").Append(F(x - 3)).Append("' y='24' width='6' height='160' fill='transparent'><title>").Append(F(bucket.Key * seconds / bins)).Append("～").Append(F((bucket.Key + 1) * seconds / bins)).Append("秒 / ").Append(values.Length).Append("フレーム / 中央値 ").Append(N(BenchmarkStatistics.Median(values))).Append(" ms / 最小 ").Append(N(values.Min())).Append(" / 最大 ").Append(N(values.Max())).Append("</title></rect>");
            }
            html.Append("<path d='").Append(path).Append("' fill='none' stroke='").Append(color).Append("' stroke-width='1.8' pointer-events='none'/></svg>");
        }
    }
}
