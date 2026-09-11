using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FUKA.AvatarBenchmark.Editor
{
    [Serializable]
    public sealed class AvatarReference
    {
        public string globalId = "";
        public string displayPath = "";
        [NonSerialized] private GameObject cached;

        public GameObject Resolve()
        {
            if (cached) return cached;
            // Unity 2022.3 TryParse throws for null on a newly created profile.
            if (!string.IsNullOrEmpty(globalId) && GlobalObjectId.TryParse(globalId, out var id))
                cached = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as GameObject;
            return cached;
        }

        public void Assign(GameObject value)
        {
            cached = value;
            globalId = value ? GlobalObjectId.GetGlobalObjectIdSlow(value).ToString() : "";
            displayPath = value ? value.scene.path + "/" + value.name : "";
        }
    }

    [Serializable]
    public sealed class ParameterValue
    {
        public string name;
        public AnimatorControllerParameterType type;
        public float value;
    }

    [Serializable]
    public sealed class AvatarEntry
    {
        public string id = Guid.NewGuid().ToString("N");
        public string label = "計測対象";
        public AvatarReference source = new AvatarReference();
        public List<ParameterValue> parameters = new List<ParameterValue>();

        public bool TryChangeSource(GameObject value, out string message)
        {
            message = null;
            if (value && EditorUtility.IsPersistent(value))
            {
                message = "Project内のPrefab/アセットは直接登録できません。一度ヒエラルキーに配置してから登録してください。";
                return false;
            }
            source ??= new AvatarReference();
            if (value == source.Resolve()) return false;
            source.Assign(value);
            // A different source must not reuse the previous avatar's cached build.
            id = Guid.NewGuid().ToString("N");
            if (value) label = value.name;
            return true;
        }
    }

    [CreateAssetMenu(menuName = "FUKA/ギミック負荷検証/計測プロファイル", fileName = "AvatarBenchmark")]
    public sealed class BenchmarkProfile : ScriptableObject
    {
        public AvatarEntry baseline = new AvatarEntry { label = "元アバター" };
        public List<AvatarEntry> targets = new List<AvatarEntry> { new AvatarEntry() };
        public List<SceneAsset> environments = new List<SceneAsset>();
        public List<BenchmarkCameraSelection> cameraSelections = new List<BenchmarkCameraSelection>();
        public List<int> populations = new List<int> { 1 };
        // Measurement conditions are shared by every profile and are not serialized.
        public int resolution => 2560;
        public int msaa => 1;
        public float measureSeconds => 5;
        public float maximumMeasureSeconds => 30;
        public int rounds => 3;
        [HideInInspector] public string buildManifestPath;
        [HideInInspector] public string lastReportDirectory;

        public IEnumerable<AvatarEntry> Entries()
        {
            yield return baseline;
            foreach (var target in targets) yield return target;
        }
    }

    [Serializable]
    public sealed class BenchmarkCameraSelection
    {
        public SceneAsset environment;
        public List<string> cameraIds = new List<string>();
    }

    [Serializable]
    public sealed class ParameterInfo
    {
        public string name;
        public AnimatorControllerParameterType type;
        public float defaultValue;
        public string layers;
        public bool expression;
        public bool conflict;
        public bool builtIn;
    }

    [Serializable]
    public sealed class BuiltAvatar
    {
        public string entryId;
        public string label;
        public GameObject prefab;
        public string buildId;
        public AvatarBuildStatistics statistics;
        public List<ParameterInfo> parameters = new List<ParameterInfo>();
    }

}
