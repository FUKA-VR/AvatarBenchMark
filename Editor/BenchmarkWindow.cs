using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FUKA.AvatarBenchmark.Editor
{
    public sealed class BenchmarkWindow : EditorWindow
    {
        [SerializeField] private BenchmarkProfile profile;
        [SerializeField] private Vector2 scroll;
        private Vector2 loggedErrorScroll;
        private double lastRepaint;
        private string error;
        private readonly Dictionary<string, string> avatarErrors = new Dictionary<string, string>();
        private readonly Dictionary<string, string> buildErrors = new Dictionary<string, string>();
        private string PreferenceKey => "FUKA.AvatarBenchmark.Profile." + Application.dataPath;

        [MenuItem("Tools/FUKA/ギミックの負荷検証")]
        public static BenchmarkWindow Open()
        {
            var window = GetWindow<BenchmarkWindow>("ギミックの負荷検証");
            window.minSize = new Vector2(440, 360);
            return window;
        }

        private void OnEnable()
        {
            if (!profile) profile = AssetDatabase.LoadAssetAtPath<BenchmarkProfile>(EditorPrefs.GetString(PreferenceKey, ""));
            EditorApplication.update += UpdateProgress;
        }
        private void OnDisable()
        {
            EditorApplication.update -= UpdateProgress;
        }
        private void UpdateProgress()
        {
            if (BenchmarkSession.IsBusy && EditorApplication.timeSinceStartup - lastRepaint > 0.5)
            { lastRepaint = EditorApplication.timeSinceStartup; Repaint(); }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("ギミックの負荷検証 v" + BenchmarkReport.CurrentToolVersion, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(BenchmarkSession.Status);
            if (BenchmarkSession.IsBusy)
            {
                if (GUILayout.Button("計測・プレビューを終了", GUILayout.Height(28))) BenchmarkSession.Stop();
                var run = BenchmarkSession.Current;
                if (run?.HasLoggedErrors == true)
                {
                    EditorGUILayout.HelpBox(run.Report.LoggedErrorTargetsText() + "\nエラーが発生しましたが、計測は継続しています。発生箇所の詳細は以下をご確認ください（エラー内容はUnityのConsoleおよび計測完了後のレポートで確認できます）。", MessageType.Warning);
                    using (var errorView = new EditorGUILayout.ScrollViewScope(loggedErrorScroll, GUILayout.MaxHeight(120)))
                    {
                        loggedErrorScroll = errorView.scrollPosition;
                        foreach (string context in run.Report.loggedErrors.Select(x => x.context).Distinct())
                            EditorGUILayout.LabelField(context, EditorStyles.wordWrappedLabel);
                        if (run.Report.omittedLogErrors > 0)
                            EditorGUILayout.LabelField("エラー件数が上限に達したため、以降の詳細は省略されています。", EditorStyles.wordWrappedMiniLabel);
                    }
                }
                var texture = run?.Preview;
                if (texture)
                {
                    Rect rect = GUILayoutUtility.GetAspectRect((float)texture.width / texture.height, GUILayout.MaxHeight(400));
                    EditorGUI.DrawPreviewTexture(rect, texture, null, ScaleMode.ScaleToFit);
                }
                return;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("計測プロファイル", GUILayout.Width(95));
                var selected = (BenchmarkProfile)EditorGUILayout.ObjectField(profile, typeof(BenchmarkProfile), false);
                if (selected != profile) { UseProfile(selected); GUIUtility.ExitGUI(); }
                if (GUILayout.Button("新規作成", GUILayout.Width(65))) { NewProfile(); GUIUtility.ExitGUI(); }
                using (new EditorGUI.DisabledScope(!profile))
                    if (GUILayout.Button("保存", GUILayout.Width(50))) { EditorUtility.SetDirty(profile); AssetDatabase.SaveAssetIfDirty(profile); }
            }
            if (!profile)
            {
                EditorGUILayout.HelpBox("計測設定プロファイルを作成または選択し、ヒエラルキーから元アバターと計測対象アバターを登録してください。", MessageType.Info);
                return;
            }
            using var scrollView = new EditorGUILayout.ScrollViewScope(scroll);
            scroll = scrollView.scrollPosition;
            Undo.RecordObject(profile, "負荷検証の設定変更");
            using var changes = new EditorGUI.ChangeCheckScope();
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (!string.IsNullOrEmpty(BenchmarkSession.LastError)) EditorGUILayout.HelpBox(BenchmarkSession.LastError, MessageType.Error);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("1. アバターを登録", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("ヒエラルキーから、ギミック追加前の「元アバター」と、ギミックを追加した「計測対象アバター」をドラッグ＆ドロップで登録してください。", MessageType.Info);
            EditorGUILayout.LabelField("元アバター（ギミックなし / 基準）", EditorStyles.boldLabel);
            DrawAvatarRegistration(profile.baseline);
            for (int i = 0; i < profile.targets.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("計測対象 " + (i + 1) + (i == 0 ? "（ギミックあり / 必須）" : "（ギミックあり）"), EditorStyles.boldLabel);
                    using (new EditorGUI.DisabledScope(i == 0))
                        if (GUILayout.Button("↑", GUILayout.Width(28))) { var item = profile.targets[i]; profile.targets[i] = profile.targets[i - 1]; profile.targets[i - 1] = item; GUI.changed = true; }
                    using (new EditorGUI.DisabledScope(profile.targets.Count <= 1))
                        if (GUILayout.Button("削除", GUILayout.Width(50))) { profile.targets.RemoveAt(i--); GUI.changed = true; continue; }
                }
                DrawAvatarRegistration(profile.targets[i]);
            }
            if (GUILayout.Button("＋ 計測対象を追加")) { profile.targets.Add(new AvatarEntry()); GUI.changed = true; }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("2. アバターをビルドしてパラメータを取得", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("ギミックの非破壊改変（NDMF等）を反映し、表情やパラメータ一覧を読み込むためにビルドを行います。\nすべてのアバターを一括でビルドするか、下の一覧から1体ずつビルドしてください。", MessageType.None);
            
            string sourceError = BenchmarkBuildService.GetSourceValidationError(profile);
            string entryError = BenchmarkBuildService.GetEntryValidationError(profile);
            if (entryError != null) EditorGUILayout.HelpBox(entryError, MessageType.Warning);
            bool editorReady = !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating;
            if (!editorReady) EditorGUILayout.HelpBox("コンパイルまたはアセットインポートの完了をお待ちください。", MessageType.Info);
            using (new EditorGUI.DisabledScope(sourceError != null || !editorReady))
                if (GUILayout.Button(new GUIContent("全アバターを一括ビルド", sourceError ?? "登録されているすべてのアバターをビルドし、パラメータを読み込みます。"), GUILayout.Height(30)))
                {
                    Attempt(() => { BenchmarkBuildService.Build(profile); avatarErrors.Clear(); buildErrors.Clear(); });
                    GUIUtility.ExitGUI();
                }
            var manifest = AssetDatabase.LoadAssetAtPath<BenchmarkBuildManifest>(profile.buildManifestPath);
            if (manifest) EditorGUILayout.LabelField("最終ビルド日時: " + manifest.builtAtUtc, EditorStyles.miniLabel);
            bool buildsReady = DrawBuildStatuses(manifest, editorReady);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("3. 計測時のパラメータを設定", EditorStyles.boldLabel);
            if (!manifest) EditorGUILayout.HelpBox("アバターをビルドすると、パラメータの設定欄が表示されます。", MessageType.Info);
            else
            {
                EditorGUILayout.HelpBox("検証したいギミックを起動（ON）にするパラメータと値を設定してください。", MessageType.None);
                bool canPreview = editorReady && HasEnvironments();
                if (!canPreview)
                    EditorGUILayout.HelpBox("プレビューを利用するには: " + (!editorReady
                        ? "コンパイルまたはアセットインポートの完了をお待ちください。"
                        : "「手順4」で計測環境シーンを登録してください。"), MessageType.Info);
                DrawParameters(profile.baseline, true, manifest, canPreview);
                foreach (var entry in profile.targets) DrawParameters(entry, false, manifest, canPreview);
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("4. 計測環境（シーン）を登録", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("計測を行うステージ（背景モデル、配置ピン、カメラが含まれるシーン）を登録します。プリセットのシーンや自作シーンをドラッグ＆ドロップしてください。", MessageType.None);
            for (int i = 0; i < profile.environments.Count; i++)
            using (new EditorGUILayout.HorizontalScope())
            {
                profile.environments[i] = (SceneAsset)EditorGUILayout.ObjectField(profile.environments[i], typeof(SceneAsset), false);
                if (GUILayout.Button("削除", GUILayout.Width(50))) { profile.environments.RemoveAt(i--); GUI.changed = true; }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("＋ シーン枠を追加")) { profile.environments.Add(null); GUI.changed = true; }
                if (GUILayout.Button("計測用環境シーンを新規作成")) Attempt(BenchmarkEnvironmentEditor.CreateWithDialog);
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("5. 計測条件を設定して開始", EditorStyles.boldLabel);
            bool populationsValid = DrawPopulations();
            bool camerasValid = DrawCameras(out int selectedCameraCount);
            if (!HasEnvironments()) EditorGUILayout.HelpBox("「手順4」で計測環境シーンを登録してください。", MessageType.Info);
            if (manifest && !buildsReady) EditorGUILayout.HelpBox("未ビルドのアバターがあります。「手順2」ですべてのアバターをビルドしてください。", MessageType.Info);
            string estimate = "計測条件を設定すると表示されます";
            if (populationsValid && camerasValid)
                estimate = BenchmarkTiming.Estimate(profile.targets.Count, profile.populations.Count, selectedCameraCount, profile.rounds, profile.measureSeconds);
            EditorGUILayout.LabelField("想定所要時間", estimate, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("※所要時間は目安です。PC環境やシェーダーのコンパイル状況等により前後する場合があります。", MessageType.None);
            EditorGUILayout.HelpBox("なるべく他のPC処理の影響を減らすため、計測時はPCを再起動して、計測するUnityだけ立ち上がってる状態にしてください。", MessageType.None);
            using (new EditorGUI.DisabledScope(!buildsReady || entryError != null || !editorReady || !HasEnvironments() || !populationsValid || !camerasValid))
                if (GUILayout.Button("負荷計測を開始", GUILayout.Height(32))) Attempt(() => BenchmarkSession.Start(profile));
            if (changes.changed) EditorUtility.SetDirty(profile);

            if (!string.IsNullOrEmpty(profile.lastReportDirectory))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("結果フォルダを開く")) EditorUtility.RevealInFinder(profile.lastReportDirectory);
                if (GUILayout.Button("計測結果レポートを開く")) Attempt(() => BenchmarkResultsWindow.Open(profile.lastReportDirectory));
            }
        }

        private bool HasEnvironments() => profile.environments.Count > 0 && profile.environments.All(x => x) &&
                                          profile.environments.Distinct().Count() == profile.environments.Count;

        private bool DrawBuildStatuses(BenchmarkBuildManifest manifest, bool editorReady)
        {
            bool ready = true;
            EditorGUILayout.LabelField("ビルド状態", EditorStyles.boldLabel);
            foreach (var entry in profile.Entries())
            {
                var status = BenchmarkBuildStatusService.Get(entry, manifest);
                ready &= status.CanUse;
                string sourceError = BenchmarkBuildService.GetSourceValidationError(profile, new[] { entry.id });
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawBuildStatus(entry, status);
                    using (new EditorGUI.DisabledScope(!editorReady || sourceError != null))
                        if (GUILayout.Button(new GUIContent(status.CanUse ? "再ビルド" : "ビルド",
                                sourceError ?? "このアバターのみをビルドします（他のアバターの設定は保持されます）。"), GUILayout.Width(120)))
                        {
                            AttemptForAvatar(entry, () =>
                            {
                                BenchmarkBuildService.Build(profile, new[] { entry.id });
                                avatarErrors.Remove(entry.id);
                            }, building: true);
                            GUIUtility.ExitGUI();
                        }
                }
                if (!status.CanUse && sourceError != null) EditorGUILayout.HelpBox(sourceError, MessageType.Info);
                if (buildErrors.TryGetValue(entry.id, out string message)) EditorGUILayout.HelpBox(message, MessageType.Warning);
            }
            return ready;
        }

        private static void DrawBuildStatus(AvatarEntry entry, BenchmarkBuildStatus status)
        {
            var type = status.CanUse ? MessageType.None : MessageType.Warning;
            EditorGUILayout.HelpBox(entry.label + ": " + status.Message, type);
        }

        private bool DrawPopulations()
        {
            int limit = 10;
            string capacityError = null;
            if (!HasEnvironments()) { limit = 0; capacityError = "「手順4」で環境シーンを登録すると、計測人数を設定できるようになります。"; }
            else foreach (var environment in profile.environments)
            {
                int count = BenchmarkEnvironmentEditor.PinLimit(environment, Repaint, out string message);
                limit = Math.Min(limit, count);
                if (message != null && capacityError == null) capacityError = environment.name + ": " + message;
            }
            if (BenchmarkStatistics.RemovePopulationsAboveLimit(profile.populations, capacityError == null ? (int?)limit : null))
                GUI.changed = true;
            EditorGUILayout.LabelField("計測人数（複数選択可）", EditorStyles.boldLabel);
            for (int row = 0; row < 2; row++)
            using (new EditorGUILayout.HorizontalScope())
            for (int n = row * 5 + 1; n <= row * 5 + 5; n++)
            {
                bool selected = profile.populations.Contains(n);
                using (new EditorGUI.DisabledScope(n > limit))
                {
                    bool next = EditorGUILayout.ToggleLeft(n + "人", selected, GUILayout.Width(65));
                    if (next != selected)
                    {
                        if (next) profile.populations.Add(n); else profile.populations.Remove(n);
                        profile.populations.Sort(); GUI.changed = true;
                    }
                }
            }
            if (capacityError != null) EditorGUILayout.HelpBox(capacityError, MessageType.Info);
            bool valid = limit > 0 && profile.populations.Count > 0 && profile.populations.All(n => n >= 1 && n <= limit) &&
                         profile.populations.Distinct().Count() == profile.populations.Count;
            if (!valid && capacityError == null) EditorGUILayout.HelpBox("計測人数を1つ以上選択してください。", MessageType.Warning);
            return valid;
        }

        private bool DrawCameras(out int selectedCameraCount)
        {
            selectedCameraCount = 0;
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("計測カメラ（複数選択可）", EditorStyles.boldLabel);
            if (!HasEnvironments()) return false;
            bool valid = true;
            profile.cameraSelections ??= new List<BenchmarkCameraSelection>();
            foreach (var scene in profile.environments)
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(scene.name, EditorStyles.boldLabel);
                var options = BenchmarkEnvironmentEditor.Cameras(scene, Repaint, out string layoutError);
                if (layoutError != null) { EditorGUILayout.HelpBox(layoutError, MessageType.Info); valid = false; continue; }
                var selection = profile.cameraSelections.FirstOrDefault(x => x.environment == scene);
                foreach (var option in options)
                {
                    bool selected = selection == null || selection.cameraIds.Contains(option.id);
                    bool next = EditorGUILayout.ToggleLeft(option.name + "  (" + (option.index + 1) + ")", selected);
                    if (next == selected) continue;
                    if (selection == null)
                    {
                        selection = new BenchmarkCameraSelection { environment = scene, cameraIds = options.Select(x => x.id).ToList() };
                        profile.cameraSelections.Add(selection);
                    }
                    if (next) selection.cameraIds.Add(option.id); else selection.cameraIds.Remove(option.id);
                    GUI.changed = true;
                }
                if (selection != null && selection.cameraIds.Any(id => !options.Any(x => x.id == id)))
                {
                    EditorGUILayout.HelpBox("シーンから削除または変更されたカメラが含まれています。", MessageType.Warning);
                    if (GUILayout.Button("見つからないカメラを選択解除"))
                    { selection.cameraIds.RemoveAll(id => !options.Any(x => x.id == id)); GUI.changed = true; }
                }
                try
                {
                    int count = BenchmarkCameras.SelectedIndices(profile, scene, options).Length;
                    valid &= count > 0;
                    selectedCameraCount += count;
                }
                catch (ArgumentException e) { EditorGUILayout.HelpBox(e.Message, MessageType.Warning); valid = false; }
            }
            return valid;
        }

        private void DrawAvatarRegistration(AvatarEntry entry)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                entry.source ??= new AvatarReference();
                GameObject resolved = entry.source.Resolve();
                bool wasChanged = GUI.changed;
                var next = (GameObject)EditorGUILayout.ObjectField(new GUIContent("アバター", "ヒエラルキーからアバターのルートオブジェクトをドラッグ＆ドロップしてください"),
                    resolved, typeof(GameObject), true, GUILayout.Height(24));
                if (next != resolved)
                {
                    string previousId = entry.id;
                    if (entry.TryChangeSource(next, out string message))
                    {
                        avatarErrors.Remove(previousId);
                        buildErrors.Remove(previousId);
                        error = null;
                        GUI.changed = true;
                    }
                    else
                    {
                        GUI.changed = wasChanged;
                        next = resolved;
                        if (message != null) ShowNotification(new GUIContent(message));
                    }
                }
                entry.label = EditorGUILayout.TextField("表示名", entry.label);
                if (!next && !string.IsNullOrEmpty(entry.source.displayPath))
                    EditorGUILayout.LabelField("参照元オブジェクト: " + entry.source.displayPath, EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void DrawParameters(AvatarEntry entry, bool baseline, BenchmarkBuildManifest manifest, bool canPreview)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField((baseline ? "元アバター: " : "計測対象: ") + entry.label, EditorStyles.boldLabel);
                string entryError = BenchmarkBuildService.GetEntryValidationError(profile, new[] { entry.id });
                var buildStatus = BenchmarkBuildStatusService.Get(entry, manifest);
                if (entryError != null) EditorGUILayout.HelpBox(entryError, MessageType.Info);
                if (avatarErrors.TryGetValue(entry.id, out string message)) EditorGUILayout.HelpBox(message, MessageType.Warning);
                if (!buildStatus.CanUse)
                {
                    EditorGUILayout.HelpBox("「手順2」でこのアバターをビルドすると、パラメータを設定できるようになります。", MessageType.Info);
                    return;
                }
                var built = manifest.avatars.Single(x => x.entryId == entry.id);
                DrawAssetStatistics(built);
                using (new EditorGUI.DisabledScope(!canPreview || entryError != null || !buildStatus.CanUse))
                    if (GUILayout.Button(new GUIContent("プレビュー動作確認", "設定したパラメータを適用した状態でアバターを再生し、ギミックが正しく動作しているか確認します。")))
                        AttemptForAvatar(entry, () => BenchmarkSession.Start(profile, entry.id));
                for (int p = 0; p < entry.parameters.Count; p++)
                using (new EditorGUILayout.HorizontalScope())
                {
                    var value = entry.parameters[p];
                    var match = BenchmarkBuildService.FindBinding(value, built);
                    if (match != null && !match.conflict && value.type != match.type) { value.type = match.type; GUI.changed = true; }
                    if (GUILayout.Button(string.IsNullOrEmpty(value.name) ? "パラメータを選択" : value.name, GUILayout.MinWidth(170)))
                        ParameterMenu(value, built);
                    EditorGUILayout.LabelField(value.type.ToString(), GUILayout.Width(50));
                    if (value.type == AnimatorControllerParameterType.Bool) value.value = EditorGUILayout.Toggle(value.value != 0, GUILayout.Width(45)) ? 1 : 0;
                    else if (value.type == AnimatorControllerParameterType.Int) value.value = EditorGUILayout.IntField((int)value.value, GUILayout.Width(85));
                    else value.value = EditorGUILayout.FloatField(value.value, GUILayout.Width(85));
                    if (GUILayout.Button("×", GUILayout.Width(25))) { entry.parameters.RemoveAt(p--); avatarErrors.Remove(entry.id); error = null; GUI.changed = true; }
                }
                string[] bindingErrors = entry.parameters.Select(value => BenchmarkBuildService.GetBindingError(value, built))
                    .Where(bindingError => bindingError != null).Distinct().ToArray();
                if (bindingErrors.Length > 0) EditorGUILayout.HelpBox(string.Join("\n", bindingErrors), MessageType.Warning);
                if (GUILayout.Button("＋ パラメータを追加")) { entry.parameters.Add(new ParameterValue()); GUI.changed = true; }
            }
        }

        private static void DrawAssetStatistics(BuiltAvatar built)
        {
            EditorGUILayout.LabelField("アバター情報（ビルド結果）", EditorStyles.miniBoldLabel);
            var stats = built.statistics;
            if (stats == null)
            {
                EditorGUILayout.LabelField("統計情報を取得するには、再ビルドしてください。", EditorStyles.miniLabel);
                return;
            }
            EditorGUILayout.LabelField(new GUIContent("ポリゴン数（三角面数）",
                "全MeshRenderer / SkinnedMeshRendererの合計ポリゴン数です（非表示オブジェクトを含みます）。"),
                new GUIContent(string.IsNullOrEmpty(stats.triangleError) ? stats.triangles.ToString("N0") : "取得失敗"));
            string textureSize = (stats.textureBytes / 1_000_000d).ToString("N2") + " MB";
            EditorGUILayout.LabelField(new GUIContent("テクスチャVRAM容量（推定）",
                "アニメーション切り替え用マテリアルやメニュー画像を含めた推定容量です。\n" +
                "対象テクスチャ数: " + stats.textureCount + "枚 (" + textureSize + " / " + stats.texturePlatform + ")"),
                new GUIContent(string.IsNullOrEmpty(stats.textureError) ? textureSize : "取得失敗"));
            if (!string.IsNullOrEmpty(stats.triangleError)) EditorGUILayout.HelpBox("ポリゴン数の集計エラー: " + stats.triangleError, MessageType.Warning);
            if (!string.IsNullOrEmpty(stats.textureError)) EditorGUILayout.HelpBox("テクスチャ容量の集計エラー: " + stats.textureError, MessageType.Warning);
            if (stats.renderTextureCount > 0)
                EditorGUILayout.HelpBox("※RenderTexture（" + stats.renderTextureCount + "枚）はテクスチャ容量に含まれていません。", MessageType.None);
            EditorGUILayout.Space(4);
        }

        private void ParameterMenu(ParameterValue value, BuiltAvatar avatar)
        {
            var menu = new GenericMenu();
            if (avatar == null) menu.AddDisabledItem(new GUIContent("先にビルドを実行してください"));
            else foreach (var item in avatar.parameters)
            {
                var label = new GUIContent(item.name.Replace("/", "／") + "  [" + item.type + "]  初期値=" + item.defaultValue);
                if (item.conflict || item.type == AnimatorControllerParameterType.Trigger) { menu.AddDisabledItem(label); continue; }
                menu.AddItem(label, value.name == item.name, () =>
                {
                    Undo.RecordObject(profile, "計測パラメータを選択");
                    value.name = item.name; value.type = item.type; value.value = item.defaultValue;
                    avatarErrors.Remove(avatar.entryId); error = null;
                    EditorUtility.SetDirty(profile); Repaint();
                });
            }
            menu.ShowAsContext();
        }

        private void NewProfile()
        {
            string path = EditorUtility.SaveFilePanelInProject("計測プロファイルを作成", "AvatarBenchmark", "asset", "計測プロファイルの保存先");
            if (string.IsNullOrEmpty(path)) return;
            var created = CreateInstance<BenchmarkProfile>();
            AssetDatabase.CreateAsset(created, path);
            UseProfile(created);
        }

        public void UseProfile(BenchmarkProfile value) { profile = value; scroll = Vector2.zero; error = null; avatarErrors.Clear(); buildErrors.Clear(); Remember(); Repaint(); }
        private void AttemptForAvatar(AvatarEntry entry, Action action, bool building = false)
        {
            var errors = building ? buildErrors : avatarErrors;
            try { errors.Remove(entry.id); error = null; action(); }
            catch (ExitGUIException) { throw; }
            catch (Exception e)
            {
                if (!IsBuildStatusMessage(e.Message)) errors[entry.id] = e.Message;
                Debug.LogException(e);
            }
        }
        private void Remember() { EditorPrefs.SetString(PreferenceKey, profile ? AssetDatabase.GetAssetPath(profile) : ""); }
        private void Attempt(Action action)
        {
            try { error = null; action(); }
            catch (ExitGUIException) { throw; }
            catch (Exception e) { if (!IsBuildStatusMessage(e.Message)) error = e.Message; Debug.LogException(e); }
        }

        private bool IsBuildStatusMessage(string message)
        {
            if (!profile) return false;
            var manifest = AssetDatabase.LoadAssetAtPath<BenchmarkBuildManifest>(profile.buildManifestPath);
            return profile.Entries().Any(entry =>
            {
                var status = BenchmarkBuildStatusService.Get(entry, manifest);
                return !status.CanUse && message == entry.label + ": " + status.Message;
            });
        }
    }
}
