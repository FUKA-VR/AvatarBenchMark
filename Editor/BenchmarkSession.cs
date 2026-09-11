using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lyuma.Av3Emulator.Runtime;
using nadena.dev.ndmf.config;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace FUKA.AvatarBenchmark.Editor
{
    [Serializable]
    public sealed class EnvironmentPlan
    {
        public string path;
        public string fingerprint;
        public int[] populations;
        public string[] views;
        public int[] viewIndices;
    }

    [Serializable]
    public sealed class BenchmarkRequest
    {
        public string profilePath;
        public string manifestPath;
        public string profileJson;
        public string output;
        public string temporaryDirectory;
        public string oldPlayScene;
        public bool oldPlayOptionsEnabled;
        public int oldPlayOptions;
        public bool oldApplyOnPlay;
        public int oldVsync;
        public int oldTargetFrameRate;
        public bool oldRunInBackground;
        public float oldTimeScale;
        public bool preview;
        public string previewEntry;
        public List<EnvironmentPlan> environments = new List<EnvironmentPlan>();
    }

    [InitializeOnLoad]
    public static class BenchmarkSession
    {
        private const string RequestKey = "FUKA.AvatarBenchmark.Request";
        private const string FinishKey = "FUKA.AvatarBenchmark.Finished";
        public static BenchmarkRun Current { get; private set; }
        public static bool IsBusy => Current != null || !string.IsNullOrEmpty(SessionState.GetString(RequestKey, ""));
        public static string Status => Current != null ? Current.Status : IsBusy ? "Playモードを起動中..." : "待機中";
        public static string LastError { get; private set; }

        static BenchmarkSession()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.delayCall += Recover;
        }

        private static void Recover()
        {
            if (string.IsNullOrEmpty(SessionState.GetString(RequestKey, ""))) return;
            if (!EditorApplication.isPlayingOrWillChangePlaymode) Restore();
            else if (EditorApplication.isPlaying && Current == null)
            {
                LastError = "計測中にスクリプトのリロードが発生したため中断しました。途中までの結果は保存されています。";
                EditorApplication.isPlaying = false;
            }
        }

        public static void Start(BenchmarkProfile profile, string previewEntry = null)
        {
            if (IsBusy || EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Editモードで開始してください。");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) throw new InvalidOperationException("コンパイルまたはアセットインポートの完了をお待ちください。");
            if (GraphicsSettings.currentRenderPipeline != null) throw new InvalidOperationException("計測にはBuilt-in Render Pipelineが必要です。");
            if (!profile) throw new ArgumentException("計測設定を指定してください。");
            var manifest = AssetDatabase.LoadAssetAtPath<BenchmarkBuildManifest>(profile.buildManifestPath);
            if (!manifest) throw new ArgumentException("先に計測用ビルドを実行してください。");
            ValidateAvatars(profile, manifest, previewEntry);
            if (profile.environments.Count == 0 || profile.environments.Any(x => !x) || profile.environments.Distinct().Count() != profile.environments.Count)
                throw new ArgumentException("環境シーンを1件以上、重複なしで指定してください。");

            var request = new BenchmarkRequest
            {
                profilePath = AssetDatabase.GetAssetPath(profile), manifestPath = profile.buildManifestPath,
                profileJson = JsonUtility.ToJson(profile), preview = previewEntry != null,
                previewEntry = previewEntry,
                temporaryDirectory = BenchmarkBuildService.GeneratedRoot + "/" + Guid.NewGuid().ToString("N"),
                oldPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene),
                oldPlayOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled,
                oldPlayOptions = (int)EditorSettings.enterPlayModeOptions,
                oldApplyOnPlay = Config.ApplyOnPlay,
                oldVsync = QualitySettings.vSyncCount, oldTargetFrameRate = Application.targetFrameRate,
                oldRunInBackground = Application.runInBackground, oldTimeScale = Time.timeScale
            };
            foreach (var sceneAsset in profile.environments)
            {
                string path = AssetDatabase.GetAssetPath(sceneAsset);
                var opened = SceneManager.GetSceneByPath(path);
                if (opened.IsValid() && opened.isDirty) throw new ArgumentException("環境シーンの変更を保存してから開始してください: " + path);
                var previousActive = SceneManager.GetActiveScene();
                bool alreadyOpen = opened.IsValid() && opened.isLoaded;
                var scene = alreadyOpen ? opened : EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                try
                {
                    var environments = scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<BenchmarkEnvironment>(true)).ToArray();
                    if (environments.Length != 1) throw new ArgumentException(path + ": 環境・配置ピンコンポーネントを1個配置してください。");
                    var environment = environments[0];
                    ValidateEnvironment(environment, scene);
                    BenchmarkStatistics.ValidatePopulations(environment.avatarPins.Length, profile.populations);
                    var indices = BenchmarkCameras.SelectedIndices(profile, sceneAsset, BenchmarkCameras.Options(environment));
                    request.environments.Add(new EnvironmentPlan
                    {
                        path = path, fingerprint = BenchmarkBuildService.Hash(File.ReadAllText(path)),
                        populations = profile.populations.ToArray(),
                        views = indices.Select(i => environment.viewpoints[i].name).ToArray(), viewIndices = indices
                    });
                }
                finally
                {
                    if (!alreadyOpen) EditorSceneManager.CloseScene(scene, true);
                    if (previousActive.IsValid()) SceneManager.SetActiveScene(previousActive);
                }
                if (previewEntry != null) break;
            }
            var outputEntries = previewEntry == null ? profile.targets : profile.Entries().Where(x => x.id == previewEntry);
            request.output = BenchmarkOutputDirectory.Create("Logs/AvatarBenchmark", outputEntries.Select(x => x.label), DateTime.Now);
            Directory.CreateDirectory(request.temporaryDirectory);
            AssetDatabase.Refresh();
            var active = SceneManager.GetActiveScene();
            var startScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            string startPath = request.temporaryDirectory + "/Start.unity";
            try { EditorSceneManager.SaveScene(startScene, startPath); }
            finally { EditorSceneManager.CloseScene(startScene, true); if (active.IsValid()) SceneManager.SetActiveScene(active); }
            profile.lastReportDirectory = request.output;
            EditorUtility.SetDirty(profile); AssetDatabase.SaveAssetIfDirty(profile);
            SessionState.SetString(RequestKey, JsonUtility.ToJson(request));
            SessionState.SetBool(FinishKey, false);
            LastError = null;
            // Unity's own Play Mode backup preserves unsaved edits in the user's open scenes.
            EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(startPath);
            EditorSettings.enterPlayModeOptionsEnabled = false;
            Config.ApplyOnPlay = false;
            EditorApplication.isPlaying = true;
        }

        public static void ValidateAvatars(BenchmarkProfile profile, BenchmarkBuildManifest manifest, string previewEntry = null)
        {
            string entryError = BenchmarkBuildService.GetEntryValidationError(profile, previewEntry == null ? null : new[] { previewEntry });
            if (entryError != null) throw new ArgumentException(entryError);
            if (!manifest) throw new ArgumentException("先に計測用ビルドを実行してください。");
            foreach (var entry in profile.Entries().Where(x => previewEntry == null || x.id == previewEntry))
            {
                var buildStatus = BenchmarkBuildStatusService.Get(entry, manifest);
                if (!buildStatus.CanUse) throw new ArgumentException(entry.label + ": " + buildStatus.Message);
                var built = manifest.avatars.Single(x => x.entryId == entry.id);
                if (entry.parameters.Select(x => x.name).Distinct().Count() != entry.parameters.Count)
                    throw new ArgumentException(entry.label + ": 同名パラメータを重複登録できません。");
                foreach (var parameter in entry.parameters)
                    BenchmarkBuildService.ValidateBinding(parameter, built);
            }
        }

        public static void ValidateEnvironment(BenchmarkEnvironment environment, Scene scene)
        {
            if (!environment.gameObject.activeInHierarchy) throw new ArgumentException("環境コンポーネントを有効なオブジェクトに置いてください。");
            BenchmarkStatistics.ValidatePopulations(environment.avatarPins.Length, new[] { 1 });
            if (environment.avatarPins.Any(x => !x || x.gameObject.scene != scene) || environment.avatarPins.Distinct().Count() != environment.avatarPins.Length)
                throw new ArgumentException("配置ピンは同じ環境シーンの別々のTransformを指定してください。");
            if (environment.viewpoints.Length == 0 || environment.viewpoints.Any(x => !x || x.gameObject.scene != scene || !x.gameObject.activeInHierarchy) ||
                environment.viewpoints.Distinct().Count() != environment.viewpoints.Length)
                throw new ArgumentException("同じ環境シーンのカメラを1個以上、重複なしで指定してください。");
            if (scene.GetRootGameObjects().Any(x => x.GetComponentsInChildren<VRCAvatarDescriptor>(true).Length > 0 ||
                                                  x.GetComponentsInChildren<LyumaAv3Emulator>(true).Length > 0))
                throw new ArgumentException("環境シーンには背景・配置ピン・カメラを置き、アバターやAv3Emulatorは計測設定から生成してください。");
        }

        public static void Stop()
        {
            Current?.Finish("中止", null);
            if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = false;
            else Restore();
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            string json = SessionState.GetString(RequestKey, "");
            if (string.IsNullOrEmpty(json)) return;
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                try { Current = new BenchmarkRun(JsonUtility.FromJson<BenchmarkRequest>(json)); }
                catch (Exception e) { LastError = e.ToString(); Debug.LogException(e); EditorApplication.isPlaying = false; }
            }
            else if (change == PlayModeStateChange.ExitingPlayMode)
            {
                Current?.Finish("中止", "Playモードを終了しました。");
                Current = null;
            }
            else if (change == PlayModeStateChange.EnteredEditMode) Restore();
        }

        private static void BeforeReload()
        {
            if (Current != null) { Current.Finish("中断", "スクリプトの再読み込み"); Current = null; }
        }

        internal static void Completed() { SessionState.SetBool(FinishKey, true); EditorApplication.isPlaying = false; }

        private static void Restore()
        {
            string json = SessionState.GetString(RequestKey, "");
            if (string.IsNullOrEmpty(json)) return;
            var r = JsonUtility.FromJson<BenchmarkRequest>(json);
            bool showResults = !r.preview && SessionState.GetBool(FinishKey, false);
            EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(r.oldPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(r.oldPlayScene);
            EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)r.oldPlayOptions;
            EditorSettings.enterPlayModeOptionsEnabled = r.oldPlayOptionsEnabled;
            Config.ApplyOnPlay = r.oldApplyOnPlay;
            QualitySettings.vSyncCount = r.oldVsync; Application.targetFrameRate = r.oldTargetFrameRate;
            Application.runInBackground = r.oldRunInBackground; Time.timeScale = r.oldTimeScale;
            SessionState.EraseString(RequestKey);
            SessionState.EraseBool(FinishKey);
            Current = null;
            try { BenchmarkBuildService.DeleteGeneratedDirectory(r.temporaryDirectory); }
            catch (Exception e) { Debug.LogWarning("一時シーンの削除を完了できませんでした: " + e.Message); }
            if (showResults && File.Exists(Path.Combine(r.output, "report.json")))
                EditorApplication.delayCall += () => BenchmarkResultsWindow.Open(r.output);
        }
    }

    public sealed class BenchmarkRun
    {
        private sealed class Job
        {
            public int environment, view, population, round;
            public string role;
            public AvatarEntry entry;
            public string Label => role == "empty" ? "アバターなし（環境のみ）" : role == "control" ? "元アバター（対照試験）" : entry.label;
        }
        private enum Phase { Next, Unloading, Loading, Initializing, BeforeInput, Settling, Flushing, Sampling, Draining, Preview, Finished }
        private readonly BenchmarkRequest request;
        private readonly BenchmarkProfile profile;
        private readonly BenchmarkBuildManifest manifest;
        private readonly BenchmarkReport report;
        private readonly List<Job> jobs = new List<Job>();
        private readonly List<GameObject> avatars = new List<GameObject>();
        private readonly BenchmarkFramePump pump;
        private readonly HashSet<int> unexpectedCameras = new HashSet<int>();
        private Phase phase;
        private int jobIndex = -1, loadedEnvironment = -1, lastFrame = -1;
        private double phaseStart, caseStart;
        private Scene environmentScene;
        private BenchmarkEnvironment environment;
        private AsyncOperation unloading;
        private LyumaAv3Emulator emulator;
        private Camera camera;
        private RenderTexture target;
        private BenchmarkMetrics metrics;
        private const double GpuDrainLimitSeconds = 15;
        private double gpuDrainBudgetSeconds;
        private double samplingStartedAt = -1, samplingStoppedAt = -1;
        private double lastSamplingStatus;
        private BenchmarkCaseResult result;
        private bool captured;
        private int framesAfterCapture;
        private string logContext = "計測環境を初期化中...";
        private string logTargetLabel = "計測環境を初期化中...";
        public string Status { get; private set; }
        public RenderTexture Preview => target;
        public bool HasLoggedErrors => report.HasLoggedErrors;
        public BenchmarkReport Report => report;
        public string Output => request.output;

        public BenchmarkRun(BenchmarkRequest request)
        {
            this.request = request;
            profile = ScriptableObject.CreateInstance<BenchmarkProfile>();
            JsonUtility.FromJsonOverwrite(request.profileJson, profile);
            manifest = AssetDatabase.LoadAssetAtPath<BenchmarkBuildManifest>(request.manifestPath);
            if (!manifest) throw new InvalidOperationException("計測用ビルドが見つかりません。");
            report = new BenchmarkReport
            {
                measurementMethod = BenchmarkReport.MeasurementMethod,
                gpuTimingMethod = NativeGpuProvider.TimingMethod,
                startedUtc = DateTime.UtcNow.ToString("o"), status = "計測中", unityVersion = Application.unityVersion,
                operatingSystem = SystemInfo.operatingSystem, cpu = SystemInfo.processorType, gpu = SystemInfo.graphicsDeviceName,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(), quality = QualitySettings.names[QualitySettings.GetQualityLevel()],
                colorSpace = QualitySettings.activeColorSpace.ToString(), profileJson = request.profileJson, planJson = JsonUtility.ToJson(request), buildId = manifest.buildId,
                unitySettings = BenchmarkUnitySettings.Capture(),
                packageVersions = string.Join("\n", new[] { "com.vrchat.avatars", "com.vrchat.base", "nadena.dev.ndmf", "nadena.dev.modular-avatar", "com.anatawa12.avatar-optimizer", "lyuma.av3emulator" }
                    .Select(name => "Packages/" + name + "/package.json").Where(File.Exists).Select(File.ReadAllText))
            };
            BuildJobs();
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; Application.runInBackground = true; Time.timeScale = 1;
            pump = new GameObject("FUKA Benchmark Runner").AddComponent<BenchmarkFramePump>();
            pump.tick = Tick;
            Application.logMessageReceived += OnLog;
            Camera.onPreCull += OnCamera;
            phase = Phase.Next;
            Status = "計測環境を初期化中...";
            report.Save(request.output, profile);
        }

        private void BuildJobs()
        {
            for (int e = 0; e < request.environments.Count; e++)
            for (int v = 0; v < request.environments[e].views.Length; v++)
            foreach (int population in request.environments[e].populations)
            for (int round = 0; round < (request.preview ? 1 : profile.rounds); round++)
            {
                var batch = new List<Job>();
                if (!request.preview)
                {
                    batch.Add(new Job { role = "empty" });
                    batch.Add(new Job { role = "baseline", entry = profile.baseline });
                }
                foreach (var entry in request.preview ? profile.Entries().Where(x => x.id == request.previewEntry) : profile.targets)
                    batch.Add(new Job { role = "target", entry = entry });
                if (!request.preview) batch.Add(new Job { role = "control", entry = profile.baseline });
                if (round % 2 != 0) batch.Reverse();
                foreach (var job in batch) { job.environment = e; job.view = v; job.population = population; job.round = round + 1; jobs.Add(job); }
                if (request.preview) return;
            }
        }

        private void Tick()
        {
            if (phase == Phase.Finished || Time.frameCount == lastFrame) return;
            lastFrame = Time.frameCount;
            try
            {
                double now = Time.realtimeSinceStartupAsDouble;
                metrics?.ReadCompletedFrame(Time.frameCount, now);
                metrics?.DrainResults();
                if (phase == Phase.Next) { Next(); return; }
                if (phase == Phase.Unloading)
                {
                    if (unloading != null && !unloading.isDone) return;
                    LoadEnvironment(); return;
                }
                if (phase == Phase.Loading)
                {
                    if (!environmentScene.isLoaded) return;
                    InitializeEnvironment(); SetupCase(); return;
                }
                if (Time.realtimeSinceStartupAsDouble - caseStart > profile.maximumMeasureSeconds + 120 + GpuDrainLimitSeconds && phase != Phase.Preview)
                    throw new TimeoutException("初期化・ウォームアップが完了しませんでした。");
                if (phase == Phase.Initializing)
                {
                    if (emulator.runtimes.Count != avatars.Count || avatars.Any(x => !x.GetComponent<LyumaAv3Runtime>())) return;
                    metrics.DiscoverLights();
                    phase = Phase.BeforeInput; phaseStart = Time.realtimeSinceStartupAsDouble; return;
                }
                if (phase == Phase.BeforeInput)
                {
                    if (EditorApplication.isCompiling || ShaderUtil.anythingCompiling) { phaseStart = Time.realtimeSinceStartupAsDouble; return; }
                    if (Time.realtimeSinceStartupAsDouble - phaseStart < 1) return;
                    foreach (var runtime in emulator.runtimes)
                    foreach (var p in jobs[jobIndex].entry.parameters) ApplyParameter(runtime, p);
                    phase = Phase.Settling; phaseStart = Time.realtimeSinceStartupAsDouble; return;
                }
                if (phase == Phase.Settling)
                {
                    metrics.DiscoverLights();
                    if (EditorApplication.isCompiling || ShaderUtil.anythingCompiling) { phaseStart = Time.realtimeSinceStartupAsDouble; return; }
                    if (!captured && Time.realtimeSinceStartupAsDouble - phaseStart > 0.2) { Capture(); captured = true; framesAfterCapture = 0; }
                    framesAfterCapture++;
                    if (Time.realtimeSinceStartupAsDouble - phaseStart < 1 || framesAfterCapture < 8) return;
                    AuditRuntime();
                    phase = request.preview ? Phase.Preview : Phase.Flushing;
                    phaseStart = Time.realtimeSinceStartupAsDouble;
                    framesAfterCapture = 0;
                    Status = request.preview ? "プレビュー実行中（終了ボタンで戻ります）" : "計測中: " + result.label;
                    return;
                }
                if (phase == Phase.Flushing)
                {
                    if (ShaderUtil.anythingCompiling) { phaseStart = now; return; }
                    if (now - phaseStart < .15) return;
                    samplingStartedAt = phaseStart = now;
                    metrics.BeginSampling(result.frames, Time.frameCount, now);
                    phase = Phase.Sampling;
                    return;
                }
                if (phase == Phase.Preview) return;
                if (phase == Phase.Sampling)
                {
                    if (ShaderUtil.anythingCompiling) throw new InvalidOperationException("計測中にシェーダーのコンパイルが発生しました。再計測してください。");
                    double elapsed = now - samplingStartedAt;
                    bool complete = BenchmarkStatistics.SamplingComplete(elapsed, metrics.CpuValidFrames,
                        metrics.GpuValidFrames, metrics.TotalFrames, metrics.GpuAvailable, profile);
                    if (complete)
                    {
                        samplingStoppedAt = now;
                        gpuDrainBudgetSeconds = Math.Min(GpuDrainLimitSeconds, Math.Max(2, metrics.MaximumFrameSeconds * 8));
                        metrics.StopSampling();
                        phase = Phase.Draining; phaseStart = now;
                        Status = "[" + (jobIndex + 1) + "/" + jobs.Count + "] GPU計測データを回収中: " + result.label;
                        if (!metrics.GpuAvailable || metrics.PendingGpuFrames == 0) CompleteCase();
                        return;
                    }
                    if (elapsed >= profile.measureSeconds) lastSamplingWasExtended = true;
                    if (elapsed - lastSamplingStatus >= 1)
                    {
                        lastSamplingStatus = elapsed;
                        Status = "[" + (jobIndex + 1) + "/" + jobs.Count + "] " + result.label +
                            (lastSamplingWasExtended ? "（サンプル不足のため計測延長中）" : " 計測中: ") +
                            elapsed.ToString("F0") + "秒 / 上限" + profile.maximumMeasureSeconds + "秒";
                    }
                    metrics.BeginFrame(Time.frameCount, now);
                    return;
                }
                if (phase == Phase.Draining)
                {
                    result.gpuDrainSeconds = now - phaseStart;
                    if (metrics.PendingGpuFrames == 0 || result.gpuDrainSeconds >= GpuDrainLimitSeconds ||
                        result.gpuDrainSeconds >= gpuDrainBudgetSeconds && metrics.DrainPolls >= 4)
                        CompleteCase();
                }
            }
            catch (Exception e)
            {
                if (result != null && phase != Phase.Next && phase != Phase.Unloading)
                {
                    result.error = e.Message;
                    CompleteCase();
                }
                else Finish("失敗", e.ToString());
            }
        }

        private void Next()
        {
            CleanupCase();
            jobIndex++;
            if (jobIndex >= jobs.Count)
            {
                Finish(report.cases.All(x => !string.IsNullOrEmpty(x.error)) ? "失敗" :
                    report.cases.Any(x => !string.IsNullOrEmpty(x.error)) ? "一部失敗" : "完了", null);
                return;
            }
            var job = jobs[jobIndex];
            var plan = request.environments[job.environment];
            logTargetLabel = job.entry?.label ?? job.Label;
            logContext = job.Label + " / " + Path.GetFileNameWithoutExtension(plan.path) + " / " + plan.views[job.view] +
                " / " + job.population + "体 / 計測" + job.round + "回目";
            Status = "[" + (jobIndex + 1) + "/" + jobs.Count + "] 計測準備中...";
            if (job.environment != loadedEnvironment)
            {
                if (environmentScene.IsValid())
                {
                    foreach (var root in environmentScene.GetRootGameObjects()) root.SetActive(false);
                    unloading = SceneManager.UnloadSceneAsync(environmentScene);
                    phase = Phase.Unloading;
                }
                else LoadEnvironment();
            }
            else SetupCase();
        }

        private void LoadEnvironment()
        {
            var plan = request.environments[jobs[jobIndex].environment];
            if (BenchmarkBuildService.Hash(File.ReadAllText(plan.path)) != plan.fingerprint)
                throw new InvalidOperationException("環境シーンが計測開始後に変更されました。");
            environmentScene = EditorSceneManager.LoadSceneInPlayMode(plan.path, new LoadSceneParameters(LoadSceneMode.Additive));
            phase = Phase.Loading;
        }

        private void InitializeEnvironment()
        {
            SceneManager.SetActiveScene(environmentScene);
            environment = environmentScene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<BenchmarkEnvironment>(true)).Single();
            BenchmarkSession.ValidateEnvironment(environment, environmentScene);
            foreach (var root in environmentScene.GetRootGameObjects())
            foreach (var c in root.GetComponentsInChildren<Camera>(true)) c.enabled = false;
            LightProbes.Tetrahedralize();
            loadedEnvironment = jobs[jobIndex].environment;
        }

        private void SetupCase()
        {
            var job = jobs[jobIndex];
            unexpectedCameras.Clear(); captured = false;
            ResetSamplingProgress();
            var cameraObject = new GameObject("FUKA Measurement Camera");
            camera = cameraObject.AddComponent<Camera>();
            int cameraIndex = request.environments[job.environment].viewIndices[job.view];
            var viewpoint = environment.viewpoints[cameraIndex];
            camera.CopyFrom(viewpoint);
            camera.transform.SetPositionAndRotation(viewpoint.transform.position, viewpoint.transform.rotation);
            camera.cameraType = CameraType.Game; camera.renderingPath = RenderingPath.Forward;
            camera.aspect = 1;
            target = new RenderTexture(profile.resolution, profile.resolution, 24, RenderTextureFormat.ARGB32) { antiAliasing = profile.msaa, name = "FUKA Benchmark Target" };
            if (!target.Create()) throw new InvalidOperationException("計測用RenderTextureを作成できません。");
            camera.targetTexture = target; camera.enabled = true;
            CreateAvatars();
            result = new BenchmarkCaseResult
            {
                id = "case_" + jobIndex.ToString("D4"), environment = request.environments[job.environment].path,
                view = request.environments[job.environment].views[job.view], population = job.population,
                round = job.round, role = job.role, entryId = job.entry?.id ?? "empty",
                label = job.Label,
                avatarBuildId = job.entry == null ? "" : manifest.avatars.Single(x => x.entryId == job.entry.id).buildId,
                frames = new List<BenchmarkSample>((int)Math.Min(300000, profile.maximumMeasureSeconds * 1500))
            };
            metrics = new BenchmarkMetrics(camera);
            if (!request.preview)
            {
                try { metrics.EnableGpu(); }
                catch (Exception e) { result.gpuError = e.Message; }
            }
            caseStart = Time.realtimeSinceStartupAsDouble;
            phase = Phase.Initializing;
        }

        private void CreateAvatars()
        {
            var job = jobs[jobIndex];
            var host = new GameObject("FUKA Av3Emulator"); host.SetActive(false);
            emulator = host.AddComponent<LyumaAv3Emulator>();
            emulator.RunPreprocessAvatarHook = false;
            emulator.SelectAvatarOnStartup = false; emulator.SelectAssetOnChangeAnimatorToDebug = false;
            emulator.DisableMirrorClone = true; emulator.DisableShadowClone = true;
            emulator.EnableHeadScaling = false; emulator.HaveEyesFollowMouse = false;
            emulator.DisableAvatarDynamicsIntegration = false;
            emulator.DefaultAnimatorToDebug = VRCAvatarDescriptor.AnimLayerType.Base;
            UnityEngine.Random.InitState(1729);
            if (job.entry != null)
            {
                var built = manifest.avatars.Single(x => x.entryId == job.entry.id);
                for (int i = 0; i < job.population; i++)
                {
                    var pin = environment.avatarPins[i];
                    var clone = Object.Instantiate(built.prefab, pin.position, pin.rotation);
                    clone.name = "FUKA Measured Avatar " + i;
                    avatars.Add(clone); clone.SetActive(true);
                }
            }
            host.SetActive(true);
        }

        private bool lastSamplingWasExtended;
        private void ResetSamplingProgress()
        {
            samplingStartedAt = samplingStoppedAt = -1;
            lastSamplingStatus = -1; lastSamplingWasExtended = false;
        }

        private void RecordSampling()
        {
            if (samplingStartedAt < 0 || result.sampling.Count != 0) return;
            metrics.StopSampling();
            double end = samplingStoppedAt >= 0 ? samplingStoppedAt : Time.realtimeSinceStartupAsDouble;
            result.gpuPendingAtEnd = metrics.PendingGpuFrames;
            bool enoughCpu = BenchmarkStatistics.EnoughSamples(metrics.CpuValidFrames, metrics.TotalFrames);
            bool enoughGpu = !metrics.GpuAvailable || BenchmarkStatistics.EnoughSamples(metrics.GpuValidFrames, metrics.TotalFrames);
            result.sampling.Add(new BenchmarkSamplingInterval
            {
                seconds = end - samplingStartedAt, startedRealtime = samplingStartedAt,
                cpuValidFrames = metrics.CpuValidFrames, gpuValidFrames = metrics.GpuValidFrames,
                totalFrames = metrics.TotalFrames, extended = lastSamplingWasExtended,
                insufficientAtLimit = end - samplingStartedAt >= profile.maximumMeasureSeconds && (!enoughCpu || !enoughGpu)
            });
        }

        private static void ApplyParameter(LyumaAv3Runtime runtime, ParameterValue parameter)
        {
            var builtin = LyumaAv3Runtime.BUILTIN_PARAMETERS.FirstOrDefault(x => x.name == parameter.name);
            if (builtin != null)
            {
                object value = parameter.type == AnimatorControllerParameterType.Bool ? (object)(parameter.value != 0) :
                    parameter.type == AnimatorControllerParameterType.Int ? (object)(int)parameter.value : parameter.value;
                builtin.valueSetter(runtime, value); return;
            }
            if (parameter.type == AnimatorControllerParameterType.Bool && runtime.BoolToIndex.TryGetValue(parameter.name, out int b))
            { runtime.Bools[b].value = parameter.value != 0; runtime.Bools[b].lastValue = null; return; }
            if (parameter.type == AnimatorControllerParameterType.Int && runtime.IntToIndex.TryGetValue(parameter.name, out int i))
            { runtime.Ints[i].value = (int)parameter.value; runtime.Ints[i].lastValue = null; return; }
            if (parameter.type == AnimatorControllerParameterType.Float && runtime.FloatToIndex.TryGetValue(parameter.name, out int f))
            { runtime.Floats[f].exportedValue = parameter.value; runtime.Floats[f].lastValue = null; return; }
            throw new InvalidOperationException("Av3Emulatorにパラメータがありません: " + parameter.name);
        }

        private void Capture()
        {
            var previous = RenderTexture.active;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); texture.Apply();
                result.screenshot = result.id + ".png";
                File.WriteAllBytes(Path.Combine(request.output, result.screenshot), texture.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; Object.Destroy(texture); }
        }

        private void AuditRuntime()
        {
            var audit = new System.Text.StringBuilder();
            foreach (var avatar in avatars)
            {
                var runtime = avatar.GetComponent<LyumaAv3Runtime>();
                if (!runtime || !avatar.activeInHierarchy || !avatar.GetComponent<Animator>().hasBoundPlayables)
                    throw new InvalidOperationException("アバターのAnimator再生を確認できません。");
                audit.AppendLine(avatar.name + " / IsLocal=" + runtime.IsLocal);
                foreach (var p in runtime.Bools) audit.AppendLine("Bool " + p.name + "=" + p.value);
                foreach (var p in runtime.Ints) audit.AppendLine("Int " + p.name + "=" + p.value);
                foreach (var p in runtime.Floats) audit.AppendLine("Float " + p.name + "=" + p.exportedValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var component in avatar.GetComponentsInChildren<Component>(true).Where(x => x &&
                             (x.GetType().Name.Contains("PhysBone") || x.GetType().Name.Contains("Constraint"))))
                    audit.AppendLine(component.GetType().Name + " / " + component.name + " active=" + component.gameObject.activeInHierarchy +
                        (component is Behaviour behavior ? " enabled=" + behavior.enabled : ""));
                foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
                    audit.AppendLine(renderer.GetType().Name + " / " + renderer.name + " enabled=" + renderer.enabled + " active=" + renderer.gameObject.activeInHierarchy);
            }
            result.runtimeAudit += "\n計測開始前の動作状態\n" + audit;
        }

        private void OnCamera(Camera rendered)
        {
            if (phase == Phase.Sampling && camera && rendered != camera && rendered.cameraType == CameraType.Game && unexpectedCameras.Add(rendered.GetInstanceID()))
            {
                if (result != null)
                {
                    result.gpuComparisonIssue = "計測中に対象以外のカメラが描画したため、GPUの比較判定を保留します。";
                    result.runtimeAudit += "\n追加カメラ: " + rendered.name;
                }
            }
        }

        private void OnLog(string message, string stack, LogType type)
        {
            report.RecordLog(message, stack, type, logContext, logTargetLabel);
        }

        private void CompleteCase()
        {
            RecordSampling();
            metrics?.Dispose(); metrics = null;
            result.Summarize();
            report.cases.Add(result);
            result = null;
            phase = Phase.Next;
            report.Save(request.output, profile);
        }

        private void CleanupCase()
        {
            metrics?.Dispose(); metrics = null;
            DestroyAvatars();
            if (camera) Object.DestroyImmediate(camera.gameObject);
            if (target) { target.Release(); Object.DestroyImmediate(target); }
            camera = null; target = null;
        }

        private void DestroyAvatars()
        {
            if (emulator) Object.DestroyImmediate(emulator.gameObject);
            emulator = null;
            foreach (var avatar in avatars) if (avatar) Object.DestroyImmediate(avatar);
            avatars.Clear();
        }

        public void Finish(string status, string error)
        {
            if (phase == Phase.Finished) return;
            phase = Phase.Finished;
            if (pump) pump.tick = null;
            Application.logMessageReceived -= OnLog;
            Camera.onPreCull -= OnCamera;
            if (result != null)
            {
                result.error = error ?? "計測完了前に終了しました。";
                RecordSampling();
                result.Summarize(); report.cases.Add(result); result = null;
            }
            try
            {
                CleanupCase();
                report.status = status; report.error = error; report.finishedUtc = DateTime.UtcNow.ToString("o");
                report.Save(request.output, profile);
            }
            finally
            {
                Status = status;
                if (profile) Object.DestroyImmediate(profile);
                BenchmarkSession.Completed();
            }
        }
    }
}
