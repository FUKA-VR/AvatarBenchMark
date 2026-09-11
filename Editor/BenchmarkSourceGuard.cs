using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FUKA.AvatarBenchmark.Editor
{
    // A build callback runs arbitrary package code. Isolated copies are the primary protection;
    // this journal also restores serialized changes made to the source by editor preview hooks.
    internal sealed class BenchmarkSourceGuard : IDisposable
    {
        private sealed class State
        {
            public Object target;
            public string json;
            public bool dirty;
        }
        private readonly List<State> states = new List<State>();
        private readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();
        private readonly string evidence;

        public BenchmarkSourceGuard(GameObject source, string buildId, int index)
        {
            evidence = Path.GetFullPath("Logs/AvatarBenchmarkBuild/" + buildId + "/source_" + index);
            Directory.CreateDirectory(evidence);
            var objects = source.GetComponentsInChildren<Transform>(true).SelectMany(t =>
                new Object[] { t.gameObject }.Concat(t.GetComponents<Component>().Cast<Object>())).Where(x => x).Distinct();
            foreach (var obj in objects) Capture(obj);
            foreach (var asset in BenchmarkBuildService.Dependencies(source).Where(x => x && BenchmarkBuildService.Mutable(x)).Distinct())
            {
                Capture(asset);
                string path = AssetDatabase.GetAssetPath(asset);
                if (!string.IsNullOrEmpty(path) && File.Exists(path) && !files.ContainsKey(path))
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    files.Add(path, bytes);
                    File.WriteAllBytes(Path.Combine(evidence, files.Count.ToString("D5") + ".backup"), bytes);
                }
            }
            File.WriteAllLines(Path.Combine(evidence, "files.txt"), files.Keys);
        }

        private void Capture(Object target)
        {
            if (target is AnimationClip clip) BenchmarkBuildService.PrepareClipForInspection(clip);
            states.Add(new State { target = target, json = EditorJsonUtility.ToJson(target), dirty = EditorUtility.IsDirty(target) });
        }

        public void Dispose()
        {
            var changes = new List<string>();
            foreach (var file in files)
            {
                if (File.Exists(file.Key) && File.ReadAllBytes(file.Key).SequenceEqual(file.Value)) continue;
                File.WriteAllBytes(file.Key, file.Value);
                AssetDatabase.ImportAsset(file.Key, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                changes.Add("Restored file: " + file.Key);
            }
            foreach (var state in states)
            {
                if (!state.target) { changes.Add("Source object was destroyed; manual review required. " + state.json); continue; }
                string currentJson = EditorJsonUtility.ToJson(state.target);
                if (currentJson == state.json) continue;
                string prefix = Path.Combine(evidence, "changed_" + changes.Count);
                File.WriteAllText(prefix + "_before.json", state.json);
                File.WriteAllText(prefix + "_after.json", currentJson);
                EditorJsonUtility.FromJsonOverwrite(state.json, state.target);
                if (state.dirty) EditorUtility.SetDirty(state.target); else EditorUtility.ClearDirty(state.target);
                changes.Add("Restored object: " + state.target.name + " / " + state.target.GetType().FullName);
            }
            File.WriteAllLines(Path.Combine(evidence, "restoration.txt"), changes.Count == 0 ? new[] { "No source changes detected." } : changes);
            if (changes.Any(x => x.StartsWith("Source object was destroyed", StringComparison.Ordinal)))
                throw new InvalidOperationException("ビルド処理中に元アバターのオブジェクトが破棄されました。ログを確認してください: " + evidence);
        }
    }
}
