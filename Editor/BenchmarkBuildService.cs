using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using nadena.dev.ndmf;
using nadena.dev.ndmf.config;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;
using Object = UnityEngine.Object;

namespace FUKA.AvatarBenchmark.Editor
{
    public static class BenchmarkBuildService
    {
        public const string GeneratedRoot = "Assets/_FukaAvatarBenchmark";

        public static BenchmarkBuildManifest Build(BenchmarkProfile profile, string[] entryIds = null)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("Editモードでコンパイル完了後に実行してください。");
            ValidateSources(profile, entryIds);
            var entries = profile.Entries().Where(x => entryIds == null || entryIds.Contains(x.id)).ToArray();
            var previous = AssetDatabase.LoadAssetAtPath<BenchmarkBuildManifest>(profile.buildManifestPath);
            string id = Guid.NewGuid().ToString("N");
            string directory = GeneratedRoot + "/" + id;
            Directory.CreateDirectory(directory);
            AssetDatabase.Refresh();
            var manifest = ScriptableObject.CreateInstance<BenchmarkBuildManifest>();
            manifest.buildId = id;
            manifest.assetDirectory = directory;
            manifest.builtAtUtc = DateTime.UtcNow.ToString("o");
            if (previous)
            {
                var retainedIds = profile.Entries().Select(x => x.id).Except(entries.Select(x => x.id)).ToHashSet();
                foreach (var retained in previous.avatars.Where(x => retainedIds.Contains(x.entryId)))
                    manifest.avatars.Add(new BuiltAvatar
                    {
                        entryId = retained.entryId, label = retained.label, prefab = retained.prefab,
                        buildId = retained.buildId,
                        statistics = retained.statistics,
                        parameters = new List<ParameterInfo>(retained.parameters)
                    });
            }
            var preview = EditorSceneManager.NewPreviewScene();
            bool applyBuild = Config.ApplyOnBuild;
            var errors = new List<string>();
            Application.LogCallback capture = (message, stack, type) =>
            {
                if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert) errors.Add(message);
            };
            try
            {
                Config.ApplyOnBuild = true;
                int index = 0;
                foreach (var entry in entries)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("負荷検証用ビルド", entry.label,
                            (float)index / entries.Length))
                        throw new OperationCanceledException("ビルドをキャンセルしました。");
                    var source = entry.source.Resolve();
                    string fingerprint = Fingerprint(source);
                    using var protection = new BenchmarkSourceGuard(source, id, index);
                    var clone = Object.Instantiate(source);
                    clone.name = source.name + "__FukaBenchmark_" + id.Substring(0, 8) + "_" + index;
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, preview);
                    string avatarDirectory = directory + "/avatar_" + index++;
                    Directory.CreateDirectory(avatarDirectory);
                    AssetDatabase.Refresh();
                    try
                    {
                        // Assets are copied before hooks run: shared source assets are never the hook's edit surface.
                        IsolateMutableAssets(clone, avatarDirectory + "/inputs");
                        clone.SetActive(true);
                        errors.Clear();
                        Application.logMessageReceived += capture;
                        bool success;
                        try
                        {
                            using (new OverrideTemporaryDirectoryScope(avatarDirectory + "/build"))
                                success = VRCBuildPipelineCallbacks.OnPreprocessAvatar(clone);
                        }
                        finally { Application.logMessageReceived -= capture; }
                        if (!success || errors.Count > 0)
                            throw new InvalidOperationException(entry.label + " のビルドに失敗しました:\n" + string.Join("\n", errors));
                        if (Fingerprint(source) != fingerprint)
                            throw new InvalidOperationException("ビルド処理によって元アバターが変更されたことを検出したため、安全のため処理を中止しました: " + entry.label);
                        // Serialize transient assets made by non-NDMF hooks as well.
                        PersistTransientAssets(clone, avatarDirectory + "/outputs");
                        var item = new BuiltAvatar
                        {
                            entryId = entry.id, label = entry.label, buildId = id,
                            parameters = ReadParameters(clone)
                        };
                        clone.SetActive(false);
                        item.prefab = PrefabUtility.SaveAsPrefabAsset(clone, avatarDirectory + "/avatar.prefab");
                        if (!item.prefab) throw new InvalidOperationException("計測用の一時Prefabを保存できませんでした。");
                        item.statistics = BenchmarkAssetStatistics.Capture(item.prefab);
                        manifest.avatars.Add(item);
                    }
                    finally { if (clone) Object.DestroyImmediate(clone); }
                }
                string path = directory + "/manifest.asset";
                AssetDatabase.CreateAsset(manifest, path);
                AssetDatabase.SaveAssetIfDirty(manifest);
                Undo.RecordObject(profile, "負荷検証用ビルドを更新");
                profile.buildManifestPath = path;
                EditorUtility.SetDirty(profile);
                AssetDatabase.SaveAssetIfDirty(profile);
                return manifest;
            }
            catch
            {
                if (manifest && !AssetDatabase.Contains(manifest)) Object.DestroyImmediate(manifest);
                DeleteGeneratedDirectory(directory);
                throw;
            }
            finally
            {
                Config.ApplyOnBuild = applyBuild;
                EditorSceneManager.ClosePreviewScene(preview);
                EditorUtility.ClearProgressBar();
            }
        }

        public static void ValidateSources(BenchmarkProfile profile, string[] entryIds = null)
        {
            string error = GetSourceValidationError(profile, entryIds);
            if (error != null) throw new ArgumentException(error);
            foreach (var entry in profile.Entries().Where(x => entryIds == null || entryIds.Contains(x.id)))
                if (entry.source.Resolve().GetComponentsInChildren<Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime>(true).Length != 0)
                    throw new ArgumentException(entry.label + ": Av3Emulatorで再生中のオブジェクトではなく、ヒエラルキー上の元アバターを指定してください。");
        }

        // Profile structure is required for both builds and playback; scene sources are only required for builds.
        public static string GetEntryValidationError(BenchmarkProfile profile, string[] entryIds = null)
        {
            if (!profile || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(profile)))
                return "計測プロファイルをプロジェクトに保存してください。";
            if (profile.baseline == null || profile.targets == null || profile.targets.Count == 0)
                return "基準となる「元アバター」と、1つ以上の「計測対象アバター」を設定してください。";
            var ids = new HashSet<string>();
            foreach (var entry in profile.Entries())
            {
                if (entry == null || string.IsNullOrEmpty(entry.id) || !ids.Add(entry.id))
                    return "アバターの登録情報に不整合または重複があります。リストから登録し直してください。";
            }
            if (entryIds != null && (entryIds.Length == 0 || entryIds.Distinct().Count() != entryIds.Length || entryIds.Any(x => !ids.Contains(x))))
                return "指定されたアバター情報が見つかりません。登録内容をご確認ください。";
            return null;
        }

        // Check only the requested build sources, so other avatars can keep using saved builds.
        public static string GetSourceValidationError(BenchmarkProfile profile, string[] entryIds = null)
        {
            string error = GetEntryValidationError(profile, entryIds);
            if (error != null) return error;
            foreach (var entry in profile.Entries().Where(x => entryIds == null || entryIds.Contains(x.id)))
            {
                var source = entry.source?.Resolve();
                if (!source && !string.IsNullOrEmpty(entry.source?.displayPath))
                    return entry.label + ": アバターが見つかりません。アバターが含まれるシーンを開くか、「手順1」で再登録してください。";
                if (!source || !source.GetComponent<VRCAvatarDescriptor>() || EditorUtility.IsPersistent(source))
                    return entry.label + ": ヒエラルキー上の VRCAvatarDescriptor が付いたルートオブジェクトを指定してください。";
            }
            return null;
        }

        public static ParameterInfo FindBinding(ParameterValue value, BuiltAvatar avatar)
            => string.IsNullOrEmpty(value.name) ? null : avatar.parameters.SingleOrDefault(x => x.name == value.name);

        public static string GetBindingError(ParameterValue value, BuiltAvatar avatar)
        {
            var match = FindBinding(value, avatar);
            if (match == null)
                return string.IsNullOrEmpty(value.name) ? "パラメータを選択してください。" :
                    avatar.label + ": ビルドされたアバター内にパラメータ「" + value.name + "」が見つかりませんでした。リストから選択し直すか、設定を削除してください。";
            if (match.conflict) return avatar.label + ": パラメータ「" + value.name + "」の型定義が複数のレイヤーで競合しています。";
            if (!BenchmarkStatistics.IsFinite(value.value)) return "パラメータ「" + value.name + "」に無効な値が設定されています。";
            if (match.type == AnimatorControllerParameterType.Trigger)
                return "定常負荷計測のため、Trigger型のパラメータはサポートされていません（Bool / Int / Float をご使用ください）: " + value.name;
            return null;
        }

        public static void ValidateBinding(ParameterValue value, BuiltAvatar avatar)
        {
            string error = GetBindingError(value, avatar);
            if (error != null) throw new ArgumentException(error);
            // Resolve the saved name against the current build; retain the configured value.
            value.type = FindBinding(value, avatar).type;
        }

        public static List<ParameterInfo> ReadParameters(GameObject root)
        {
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (!descriptor) throw new InvalidOperationException("ビルド後のAvatar Descriptorがありません。");
            var result = new Dictionary<string, ParameterInfo>(StringComparer.Ordinal);
            var expression = descriptor.expressionParameters;
            if (expression)
            {
                foreach (var p in expression.parameters.Where(p => !string.IsNullOrEmpty(p.name)))
                {
                    var type = p.valueType == VRCExpressionParameters.ValueType.Bool ? AnimatorControllerParameterType.Bool :
                        p.valueType == VRCExpressionParameters.ValueType.Int ? AnimatorControllerParameterType.Int : AnimatorControllerParameterType.Float;
                    if (result.TryGetValue(p.name, out var prior)) { prior.conflict |= prior.type != type; continue; }
                    result.Add(p.name, new ParameterInfo { name = p.name, type = type, defaultValue = p.defaultValue, expression = true, layers = "Expressions" });
                }
            }
            foreach (var layer in descriptor.baseAnimationLayers.Concat(descriptor.specialAnimationLayers))
            {
                var runtimeController = layer.animatorController;
                while (runtimeController is AnimatorOverrideController overrides) runtimeController = overrides.runtimeAnimatorController;
                var controller = runtimeController as AnimatorController;
                if (!controller) continue;
                foreach (var p in controller.parameters)
                {
                    if (!result.TryGetValue(p.name, out var info))
                    {
                        info = new ParameterInfo { name = p.name, type = p.type, defaultValue =
                            p.type == AnimatorControllerParameterType.Bool ? (p.defaultBool ? 1 : 0) :
                            p.type == AnimatorControllerParameterType.Int ? p.defaultInt : p.defaultFloat, layers = "" };
                        result.Add(p.name, info);
                    }
                    info.conflict |= info.type != p.type;
                    info.layers += (info.layers.Length > 0 ? ", " : "") + layer.type;
                }
            }
            return result.Values.OrderBy(x => x.name, StringComparer.Ordinal).ToList();
        }

        public static string Fingerprint(GameObject root)
        {
            using var scan = FingerprintSteps(root);
            while (scan.MoveNext())
                if (scan.Current != null) return scan.Current;
            throw new InvalidOperationException("アバターの変更確認が完了しませんでした。");
        }

        // Used only inside an explicit build to detect source changes made by build hooks.
        private static IEnumerator<string> FingerprintSteps(GameObject root)
        {
            using var sha = SHA256.Create();
            void Append(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
            }
            // Unity lazily materializes editor curve caches even during read-only inspection.
            foreach (var clip in Dependencies(root).OfType<AnimationClip>())
            {
                PrepareClipForInspection(clip);
                yield return null;
            }
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                Append(StableJson(transform.gameObject));
                yield return null;
                Append(StableJson(transform));
                yield return null;
                foreach (var component in transform.GetComponents<Component>())
                    if (component && !(component is Transform))
                    {
                        Append(StableJson(component));
                        yield return null;
                    }
            }
            foreach (string path in Dependencies(root)
                         .Where(EditorUtility.IsPersistent).Select(AssetDatabase.GetAssetPath)
                         .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).Distinct().OrderBy(p => p))
            {
                var info = new FileInfo(path);
                Append(path + info.Length + info.LastWriteTimeUtc.Ticks);
                yield return null;
            }
            foreach (var asset in Dependencies(root).Where(x => x && Mutable(x))
                         .Distinct().OrderBy(StableObjectId, StringComparer.Ordinal))
            {
                Append(StableJson(asset));
                yield return null;
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            yield return BitConverter.ToString(sha.Hash).Replace("-", "");
        }

        private static string StableJson(Object value)
        {
            string json = EditorJsonUtility.ToJson(value);
            if (value is Animator) json = RemoveArrayCache(json, "m_TOS");
            if (value.GetType().Namespace == "VRC.SDK3.Dynamics.Constraint.Components")
                json = Regex.Replace(json, "\"(cachedExecutionGroupIndex|latestValidExecutionGroupIndex)\":-?[0-9]+", "\"$1\":0");
            return Regex.Replace(json, "\"instanceID\":(-?[0-9]+)", match =>
            {
                int id = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                return "\"reference\":\"" + Hash(StableObjectId(EditorUtility.InstanceIDToObject(id))) + "\"";
            });
        }

        private static string StableObjectId(Object value)
        {
            if (!value) return "null";
            if (EditorUtility.IsPersistent(value)) return GlobalObjectId.GetGlobalObjectIdSlow(value).ToString();
            var transform = value is GameObject go ? go.transform : (value as Component)?.transform;
            if (transform)
            {
                string path = "";
                for (var t = transform; t; t = t.parent) path = t.GetSiblingIndex() + "/" + t.name + "/" + path;
                int componentIndex = value is Component component ? Array.IndexOf(transform.GetComponents<Component>(), component) : -1;
                return transform.gameObject.scene.path + ":" + path + ":" + componentIndex;
            }
            return value.GetType().FullName + ":" + value.name + ":" + value.GetInstanceID();
        }

        private static string RemoveArrayCache(string json, string name)
        {
            int start = json.IndexOf("\"" + name + "\":[", StringComparison.Ordinal);
            if (start < 0) return json;
            start = json.IndexOf('[', start);
            int depth = 0; bool quoted = false, escaped = false;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (quoted) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; continue; }
                if (c == '"') quoted = true;
                else if (c == '[') depth++;
                else if (c == ']' && --depth == 0) return json.Substring(0, start) + "[]" + json.Substring(i + 1);
            }
            return json;
        }

        internal static void PrepareClipForInspection(AnimationClip clip)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip)) AnimationUtility.GetEditorCurve(clip, binding);
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip)) AnimationUtility.GetObjectReferenceCurve(clip, binding);
        }

        public static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }

        internal static bool Mutable(Object value) => value is Material || value is Mesh || value is Motion ||
            value is RuntimeAnimatorController || value is AnimatorState || value is AnimatorStateMachine ||
            value is AnimatorTransitionBase || value is AvatarMask || value is Avatar || value is ScriptableObject && !(value is MonoScript) ||
            value is Texture && AssetDatabase.GetAssetPath(value).EndsWith(".asset", StringComparison.OrdinalIgnoreCase);

        internal static Object[] Dependencies(Object root)
        {
            // CollectDependencies omits some unsaved native Animator graph objects.
            var found = new HashSet<Object>(EditorUtility.CollectDependencies(new[] { root }).Where(x => x));
            var queue = new Queue<Object>(found);
            while (queue.Count > 0)
            {
                var value = queue.Dequeue();
                if (!(value is RuntimeAnimatorController || value is AnimatorState || value is AnimatorStateMachine ||
                      value is AnimatorTransitionBase || value is BlendTree)) continue;
                using var serialized = new SerializedObject(value);
                var property = serialized.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var reference = property.objectReferenceValue;
                    if (reference && found.Add(reference)) queue.Enqueue(reference);
                }
            }
            return found.ToArray();
        }

        private static void IsolateMutableAssets(GameObject clone, string directory)
        {
            Directory.CreateDirectory(directory);
            AssetDatabase.Refresh();
            var dependencies = Dependencies(clone);
            var map = new Dictionary<Object, Object>();
            int index = 0;
            foreach (var asset in dependencies.Where(x => x && Mutable(x)).Distinct())
            {
                var copy = Object.Instantiate(asset);
                copy.name = asset.name;
                copy.hideFlags = HideFlags.None;
                string extension = copy is Material ? ".mat" : copy is AnimationClip ? ".anim" :
                    copy is AnimatorController ? ".controller" : ".asset";
                AssetDatabase.CreateAsset(copy, directory + "/" + index++ + extension);
                map.Add(asset, copy);
            }
            foreach (var component in clone.GetComponentsInChildren<Component>(true).Where(x => x)) Remap(component, map);
            foreach (var copy in map.Values) { Remap(copy, map); AssetDatabase.SaveAssetIfDirty(copy); }
        }

        private static void Remap(Object target, Dictionary<Object, Object> map)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.GetIterator();
            while (property.Next(true))
                if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue &&
                    map.TryGetValue(property.objectReferenceValue, out var replacement)) property.objectReferenceValue = replacement;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void PersistTransientAssets(GameObject clone, string directory)
        {
            Directory.CreateDirectory(directory);
            AssetDatabase.Refresh();
            int index = 0;
            foreach (var asset in Dependencies(clone).Where(x => x && Mutable(x)).Distinct())
            {
                if (EditorUtility.IsPersistent(asset)) continue;
                asset.hideFlags = HideFlags.None;
                string extension = asset is Material ? ".mat" : asset is AnimationClip ? ".anim" : asset is AnimatorController ? ".controller" : ".asset";
                AssetDatabase.CreateAsset(asset, directory + "/" + index++ + extension);
            }
        }

        public static void DeleteGeneratedDirectory(string directory)
        {
            string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            string parent = Path.GetFullPath(GeneratedRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (!full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Path.GetDirectoryName(full) != parent || !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
                throw new ArgumentException("削除対象が計測ツールの生成フォルダではありません。");
            AssetDatabase.DeleteAsset(directory);
        }
    }
}
