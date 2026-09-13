using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FUKA.AvatarBenchmark.Editor
{
    // Writable render targets belong to one simulated person. Keep immutable assets shared.
    public sealed class BenchmarkInstanceAssets : IDisposable
    {
        private readonly List<Object> owned = new List<Object>();

        public BenchmarkInstanceAssets(GameObject instance)
        {
            var assets = BenchmarkBuildService.Dependencies(instance).Where(BenchmarkBuildService.Mutable).Distinct().ToArray();
            var required = new HashSet<Object>(assets.Where(x => x is RenderTexture));
            if (required.Count == 0) return;
            try
            {
                // Follow incoming references so materials, animation swaps and CRT update
                // materials still point to the same private target within this avatar.
                var owners = new Dictionary<Object, List<Object>>();
                foreach (var asset in assets)
                {
                    using var serialized = new SerializedObject(asset);
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference || !property.objectReferenceValue) continue;
                        var reference = property.objectReferenceValue;
                        if (!owners.TryGetValue(reference, out var list)) owners.Add(reference, list = new List<Object>());
                        list.Add(asset);
                    }
                }
                var pending = new Queue<Object>(required);
                while (pending.Count > 0)
                {
                    if (!owners.TryGetValue(pending.Dequeue(), out var parents)) continue;
                    foreach (var parent in parents) if (required.Add(parent)) pending.Enqueue(parent);
                }
                var map = new Dictionary<Object, Object>();
                foreach (var source in required)
                {
                    var copy = BenchmarkBuildService.CopyMutableAsset(source);
                    copy.name = source.name; copy.hideFlags = HideFlags.DontSave;
                    owned.Add(copy); map.Add(source, copy);
                }
                foreach (var copy in owned) BenchmarkBuildService.Remap(copy, map);
                foreach (var component in instance.GetComponentsInChildren<Component>(true))
                    if (component) BenchmarkBuildService.Remap(component, map);
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            var targets = new HashSet<RenderTexture>(owned.OfType<RenderTexture>().Where(x => x));
            if (targets.Count > 0)
            {
                // Av3Emulator's inactive source clone is destroyed at the end of the frame.
                // Detach cameras still using our targets before releasing those targets.
                foreach (var camera in Resources.FindObjectsOfTypeAll<Camera>())
                    if (!EditorUtility.IsPersistent(camera) && targets.Contains(camera.targetTexture))
                        camera.targetTexture = null;
            }
            foreach (var asset in owned)
            {
                if (!asset) continue;
                if (asset is RenderTexture texture) texture.Release();
                Object.DestroyImmediate(asset);
            }
            owned.Clear();
        }
    }
}
