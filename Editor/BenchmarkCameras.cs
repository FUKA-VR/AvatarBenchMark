using System;
using System.Linq;
using UnityEditor;

namespace FUKA.AvatarBenchmark.Editor
{
    public sealed class BenchmarkCameraOption
    {
        public string id, name;
        public int index;
    }

    public static class BenchmarkCameras
    {
        public static BenchmarkCameraOption[] Options(BenchmarkEnvironment environment)
            => environment.viewpoints.Select((camera, index) => new BenchmarkCameraOption
            {
                id = camera ? GlobalObjectId.GetGlobalObjectIdSlow(camera).ToString() : "",
                name = camera ? camera.name : "未登録のカメラ", index = index
            }).ToArray();

        public static int[] SelectedIndices(BenchmarkProfile profile, SceneAsset scene, BenchmarkCameraOption[] options)
        {
            var selection = profile.cameraSelections?.FirstOrDefault(x => x.environment == scene);
            // An unset selection includes every camera in the environment.
            if (selection == null) return options.Select(x => x.index).ToArray();
            if (selection.cameraIds == null || selection.cameraIds.Count == 0)
                throw new ArgumentException("計測するカメラを1台以上選択してください。");
            if (selection.cameraIds.Any(id => !options.Any(x => x.id == id)))
                throw new ArgumentException("選択したカメラが見つかりません。カメラの選択を更新してください。");
            return options.Where(x => selection.cameraIds.Contains(x.id)).Select(x => x.index).ToArray();
        }
    }
}
