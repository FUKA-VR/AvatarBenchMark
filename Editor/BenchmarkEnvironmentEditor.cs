using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FUKA.AvatarBenchmark.Editor
{
    [CustomEditor(typeof(BenchmarkEnvironment))]
    public sealed class BenchmarkEnvironmentEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("avatarPins"), new GUIContent("アバター配置ピン（最大10箇所）", "アバターをスポーンさせる座標と向きを指定します。"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("viewpoints"), new GUIContent("計測カメラ", "負荷計測時に使用するカメラのリストです。"), true);
            serializedObject.ApplyModifiedProperties();
            var environment = (BenchmarkEnvironment)target;
            using (new EditorGUI.DisabledScope(Application.isPlaying || environment.avatarPins.Length >= 10))
                if (GUILayout.Button("配置ピンを追加"))
                {
                    Undo.RecordObject(environment, "配置ピンを追加");
                    var pin = new GameObject("Avatar Pin " + (environment.avatarPins.Length + 1));
                    Undo.RegisterCreatedObjectUndo(pin, "配置ピンを追加");
                    pin.transform.SetParent(environment.transform, false);
                    pin.transform.localPosition = Vector3.right * environment.avatarPins.Length;
                    environment.avatarPins = environment.avatarPins.Concat(new[] { pin.transform }).ToArray();
                }
            using (new EditorGUI.DisabledScope(Application.isPlaying))
                if (GUILayout.Button("現在のSceneビュー視点からカメラを作成"))
                {
                    var view = SceneView.lastActiveSceneView;
                    if (!view) return;
                    Undo.RecordObject(environment, "計測カメラを追加");
                    var go = new GameObject("View " + (environment.viewpoints.Length + 1));
                    Undo.RegisterCreatedObjectUndo(go, "計測カメラを追加");
                    go.transform.SetParent(environment.transform, false);
                    var camera = go.AddComponent<Camera>(); camera.CopyFrom(view.camera);
                    go.transform.SetPositionAndRotation(view.camera.transform.position, view.camera.transform.rotation);
                    camera.cameraType = CameraType.Game; camera.enabled = false;
                    environment.viewpoints = environment.viewpoints.Concat(new[] { camera }).ToArray();
                }
            EditorGUILayout.HelpBox("【環境シーンの設定】\n" +
                "・背景モデルや照明をこのシーンに配置し、シーンアセットを計測ウィンドウの「手順4」に登録してください。\n" +
                "・ピンの青い線がアバターの正面向きを表します。\n" +
                "・複数人での計測時は、配置ピンリストの先頭から指定した人数分のピンが使用されます。", MessageType.Info);
        }

        private sealed class PinCapacity
        {
            public long stamp;
            public int count;
            public string error;
            public BenchmarkCameraOption[] cameras = Array.Empty<BenchmarkCameraOption>();
        }
        private static readonly Dictionary<string, PinCapacity> capacities = new Dictionary<string, PinCapacity>();

        // Closed scenes are inspected once, outside OnGUI. Open scenes reflect pin edits immediately.
        public static int PinLimit(SceneAsset asset, Action repaint, out string error)
        {
            var layout = Layout(asset, repaint);
            error = layout.error;
            return layout.count;
        }

        public static BenchmarkCameraOption[] Cameras(SceneAsset asset, Action repaint, out string error)
        {
            var layout = Layout(asset, repaint);
            error = layout.error;
            return layout.cameras;
        }

        private static PinCapacity Layout(SceneAsset asset, Action repaint)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path)) return new PinCapacity { error = "環境シーンが指定されていません。" };
            var loaded = SceneManager.GetSceneByPath(path);
            if (loaded.IsValid() && loaded.isLoaded) return ReadLayout(loaded);
            long stamp = File.GetLastWriteTimeUtc(path).Ticks;
            if (!capacities.TryGetValue(path, out var cached) || cached.stamp != stamp)
            {
                cached = new PinCapacity { stamp = stamp, error = "配置ピン情報を読み込み中..." };
                capacities[path] = cached;
                EditorApplication.delayCall += () =>
                {
                    var scene = default(Scene);
                    var previous = SceneManager.GetActiveScene();
                    bool openedHere = false;
                    try
                    {
                        if (EditorApplication.isPlayingOrWillChangePlaymode) { capacities.Remove(path); return; }
                        scene = SceneManager.GetSceneByPath(path);
                        if (!scene.IsValid() || !scene.isLoaded)
                        {
                            scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                            openedHere = true;
                        }
                        var layout = ReadLayout(scene);
                        cached.count = layout.count; cached.error = layout.error; cached.cameras = layout.cameras;
                    }
                    catch (Exception e) { cached.error = e.Message; }
                    finally
                    {
                        if (openedHere && scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
                        if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                        repaint?.Invoke();
                    }
                };
            }
            return cached;
        }

        private static PinCapacity ReadLayout(Scene scene)
        {
            var layout = new PinCapacity();
            layout.count = CountPins(scene, out layout.error);
            if (layout.error != null) return layout;
            var environment = scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<BenchmarkEnvironment>(true)).Single();
            if (environment.viewpoints == null || environment.viewpoints.Length == 0 || environment.viewpoints.Any(x => !x))
                layout.error = "環境シーンに計測カメラが1台も登録されていません。";
            else layout.cameras = BenchmarkCameras.Options(environment);
            return layout;
        }

        public static int CountPins(Scene scene, out string error)
        {
            error = null;
            var environments = scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<BenchmarkEnvironment>(true)).ToArray();
            if (environments.Length != 1) { error = "シーン内にBenchmarkEnvironmentコンポーネントが1つ配置されている必要があります。"; return 0; }
            var pins = environments[0].avatarPins;
            if (pins == null || pins.Length < 1 || pins.Length > 10 || pins.Any(x => !x || x.gameObject.scene != scene) || pins.Distinct().Count() != pins.Length)
            { error = "同一シーン内の配置ピンを1～10箇所、空欄や重複のないように登録してください。"; return 0; }
            return pins.Length;
        }

        [MenuItem("Tools/FUKA/負荷検証の環境シーンを新規作成")]
        public static void CreateWithDialog()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Editモードで実行してください。");
            string path = EditorUtility.SaveFilePanelInProject("環境シーンを新規作成", "BenchmarkEnvironment", "unity", "環境シーンの保存先");
            if (!string.IsNullOrEmpty(path)) CreateScene(path);
        }

        public static Scene CreateScene(string path)
        {
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".unity", StringComparison.Ordinal) || File.Exists(path))
                throw new ArgumentException("未作成のAssets内の.unityパスを指定してください。");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            var root = new GameObject("Benchmark Environment");
            var environment = root.AddComponent<BenchmarkEnvironment>();
            var pin = new GameObject("Avatar Pin 1"); pin.transform.SetParent(root.transform, false);
            var cameraObject = new GameObject("Front"); cameraObject.transform.SetParent(root.transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            cameraObject.transform.position = new Vector3(0, 1, 3);
            cameraObject.transform.rotation = Quaternion.Euler(0, 180, 0);
            camera.fieldOfView = 40; camera.nearClipPlane = 0.05f; camera.farClipPlane = 100;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.12f, 0.12f, 0.12f);
            camera.enabled = false;
            var lightObject = new GameObject("Reference Light"); lightObject.transform.SetParent(root.transform, false);
            var light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 0.8f;
            lightObject.transform.rotation = Quaternion.Euler(35, -25, 0);
            environment.avatarPins = new[] { pin.transform }; environment.viewpoints = new[] { camera };
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat; RenderSettings.ambientLight = new Color(0.2f, 0.2f, 0.2f);
            EditorSceneManager.SaveScene(scene, path);
            Selection.activeGameObject = root;
            return scene;
        }
    }
}
