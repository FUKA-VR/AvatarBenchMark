using System.Collections.Generic;
using UnityEngine;

namespace FUKA.AvatarBenchmark.Editor
{
    public sealed class BenchmarkBuildManifest : ScriptableObject
    {
        public string buildId;
        public string assetDirectory;
        public string builtAtUtc;
        public List<BuiltAvatar> avatars = new List<BuiltAvatar>();
    }
}
