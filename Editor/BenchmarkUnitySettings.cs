using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace FUKA.AvatarBenchmark.Editor
{
    [Serializable]
    public sealed class BenchmarkUnitySetting
    {
        public string name;
        public string value;

        public BenchmarkUnitySetting(string name, string value)
        {
            this.name = name;
            this.value = value;
        }
    }

    public static class BenchmarkUnitySettings
    {
        public const string Title = "描画負荷に影響するUnity設定";
        public const string Description = "計測開始時のプロジェクト設定値です（Edit > Project Settings > Quality で確認可能）。ライトやマテリアルの個別設定によっても描画負荷が変化します。";
        public const string NotRecorded = "Unity設定の記録データはありません。";

        // Capture once; report generation must never substitute the currently open project's settings.
        public static List<BenchmarkUnitySetting> Capture()
            => new List<BenchmarkUnitySetting>
            {
                new BenchmarkUnitySetting("画質プリセット", QualitySettings.names[QualitySettings.GetQualityLevel()]),
                new BenchmarkUnitySetting("Pixel Light Count", QualitySettings.pixelLightCount.ToString(CultureInfo.InvariantCulture)),
                new BenchmarkUnitySetting("影の描画（Shadows）", QualitySettings.shadows switch
                {
                    ShadowQuality.Disable => "無効",
                    ShadowQuality.HardOnly => "ハードのみ",
                    _ => "ハード＋ソフト"
                }),
                new BenchmarkUnitySetting("影の解像度（Shadow Resolution）", QualitySettings.shadowResolution.ToString()),
                new BenchmarkUnitySetting("影の距離（Shadow Distance）", Number(QualitySettings.shadowDistance) + " m"),
                new BenchmarkUnitySetting("影の分割数（Shadow Cascades）", QualitySettings.shadowCascades.ToString(CultureInfo.InvariantCulture)),
                new BenchmarkUnitySetting("テクスチャのミップ制限", QualitySettings.globalTextureMipmapLimit.ToString(CultureInfo.InvariantCulture)),
                new BenchmarkUnitySetting("異方性フィルタリング", QualitySettings.anisotropicFiltering.ToString()),
                new BenchmarkUnitySetting("テクスチャストリーミング", Enabled(QualitySettings.streamingMipmapsActive)),
                new BenchmarkUnitySetting("LOD Bias", Number(QualitySettings.lodBias)),
                new BenchmarkUnitySetting("最大LODレベル", QualitySettings.maximumLODLevel.ToString(CultureInfo.InvariantCulture)),
                new BenchmarkUnitySetting("スキンウェイト（Skin Weights）", QualitySettings.skinWeights.ToString()),
                new BenchmarkUnitySetting("リアルタイムReflection Probe", Enabled(QualitySettings.realtimeReflectionProbes))
            };

        private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static string Enabled(bool value) => value ? "有効" : "無効";
    }
}
