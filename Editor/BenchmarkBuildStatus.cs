using System.Linq;

namespace FUKA.AvatarBenchmark.Editor
{
    public enum BenchmarkBuildState { Ready, MissingBuild }

    public sealed class BenchmarkBuildStatus
    {
        public BenchmarkBuildState State { get; internal set; }
        public bool CanUse => State == BenchmarkBuildState.Ready;
        public string Message => State switch
        {
            BenchmarkBuildState.Ready => "ビルド完了",
            _ => "未ビルド（「手順2」でビルドを実行してください）"
        };
    }

    public static class BenchmarkBuildStatusService
    {
        // Validate the saved snapshot; source edits take effect on the next explicit build.
        public static BenchmarkBuildStatus Get(AvatarEntry entry, BenchmarkBuildManifest manifest)
        {
            var built = manifest ? manifest.avatars.SingleOrDefault(x => x.entryId == entry.id) : null;
            return new BenchmarkBuildStatus
            {
                State = built?.prefab ? BenchmarkBuildState.Ready : BenchmarkBuildState.MissingBuild
            };
        }
    }
}
