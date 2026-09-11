using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace FUKA.AvatarBenchmark.Editor
{
    [Serializable]
    public sealed class AvatarBuildStatistics
    {
        public long triangles;
        public long textureBytes;
        public int textureCount;
        public int renderTextureCount;
        public string texturePlatform;
        public string triangleError;
        public string textureError;
    }

    public static class BenchmarkAssetStatistics
    {
        public static AvatarBuildStatistics Capture(GameObject prefab)
        {
            if (!prefab) throw new ArgumentException("ビルド済みPrefabがありません。");
            var result = new AvatarBuildStatistics { texturePlatform = EditorUserBuildSettings.activeBuildTarget.ToString() };
            // A statistics failure must not discard a successful avatar build or masquerade as zero.
            try { result.triangles = CountTriangles(prefab); }
            catch (Exception e) { result.triangleError = e.Message; }
            try
            {
                foreach (var texture in CollectTextures(prefab))
                {
                    if (texture is RenderTexture) { result.renderTextureCount++; continue; }
                    result.textureBytes = checked(result.textureBytes + EstimateTextureBytes(texture));
                    result.textureCount++;
                }
            }
            catch (Exception e) { result.textureError = e.Message; }
            return result;
        }

        public static long CountTriangles(GameObject root)
        {
            long count = 0;
            // Count geometry per renderer, including inactive objects; shared meshes can be drawn more than once.
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh :
                    renderer is MeshRenderer ? renderer.GetComponent<MeshFilter>()?.sharedMesh : null;
                if (!mesh) continue;
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    long indices = mesh.GetIndexCount(subMesh);
                    var topology = mesh.GetTopology(subMesh);
                    count = checked(count + (topology == MeshTopology.Triangles ? indices / 3 :
                        topology == MeshTopology.Quads ? indices / 4 * 2 : 0));
                }
            }
            return count;
        }

        public static HashSet<Texture> CollectTextures(GameObject root)
        {
            var textures = new HashSet<Texture>();
            var visited = new HashSet<Object>();
            var queue = new Queue<Object>(root.GetComponentsInChildren<Component>(true).Where(x => x));
            while (queue.Count > 0)
            {
                var value = queue.Dequeue();
                if (!value || !visited.Add(value)) continue;
                if (value is Texture texture) { textures.Add(texture); continue; }
                if (value is Material material)
                {
                    // Ignore obsolete saved properties left over from a different shader.
                    foreach (string property in material.GetTexturePropertyNames()) queue.Enqueue(material.GetTexture(property));
                    continue;
                }
                if (value is RuntimeAnimatorController controller)
                {
                    // animationClips also resolves AnimatorOverrideController replacements.
                    foreach (var clip in controller.animationClips) queue.Enqueue(clip);
                    continue;
                }
                if (value is AnimationClip animation)
                {
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(animation))
                        foreach (var key in AnimationUtility.GetObjectReferenceCurve(animation, binding)) queue.Enqueue(key.value);
                    continue;
                }
                if (!(value is Component || value is ScriptableObject)) continue;
                using var serialized = new SerializedObject(value);
                var iterator = serialized.GetIterator();
                while (iterator.Next(true))
                {
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference || iterator.name == "m_Script") continue;
                    var reference = iterator.objectReferenceValue;
                    // All components within the built hierarchy are already queued. Do not follow other hierarchies,
                    // prefab-source links or scripts/importers (which include editor-only icon textures).
                    if (reference && !(reference is GameObject || reference is Component || reference is MonoScript)) queue.Enqueue(reference);
                }
            }
            return textures;
        }

        public static long EstimateTextureBytes(Texture texture)
        {
            int depth = 1, slices = 1;
            bool volume = false;
            switch (texture)
            {
                case Texture2D _: break;
                case Cubemap _: slices = 6; break;
                case Texture2DArray array: slices = array.depth; break;
                case CubemapArray cubes: slices = checked(cubes.cubemapCount * 6); break;
                case Texture3D texture3D: depth = texture3D.depth; volume = true; break;
                default: throw new NotSupportedException("容量を計算できないテクスチャ: " + texture.name + " (" + texture.GetType().Name + ")");
            }
            if (texture.graphicsFormat == GraphicsFormat.None)
                throw new NotSupportedException("テクスチャの形式を取得できません: " + texture.name);
            long bytes = 0;
            int width = texture.width, height = texture.height;
            for (int mip = 0; mip < texture.mipmapCount; mip++)
            {
                // Unity handles compressed block rounding, including the smallest mip levels.
                bytes = checked(bytes + (long)GraphicsFormatUtility.ComputeMipmapSize(width, height, depth, texture.graphicsFormat) * slices);
                width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
                if (volume) depth = Math.Max(1, depth / 2);
            }
            return bytes;
        }
    }
}
