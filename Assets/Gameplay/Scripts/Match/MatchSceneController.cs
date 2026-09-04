using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class MatchSceneController : MonoBehaviour
{
    [SerializeField] Transform mapRoot;
    [SerializeField] string fallbackMapSceneName = "Sandbox";
    [SerializeField] bool reparentLoadedMapRoots = false;
    [Header("Loading Screen")]
    [SerializeField] bool showLoadingScreenAtMatchStart = true;
    [SerializeField] float minimumLoadingScreenSeconds = 1.25f;
    [SerializeField] int loadingTextFontSize = 56;
    [SerializeField] float loadingTextDotSpeed = 3f;
    [SerializeField] float loadingTextPulseSpeed = 2.5f;
    [SerializeField] float loadingFadeOutSeconds = 0.35f;

    [Header("Map Readiness")]
    [SerializeField] bool waitForBrickCollidersBeforeHide = true;
    [SerializeField] float maxBrickColliderWaitSeconds = 20f;
    [SerializeField] float brickColliderPollInterval = 0.1f;
    [SerializeField] bool waitForBrickRenderingBeforeHide = true;
    [SerializeField] float maxBrickRenderWaitSeconds = 20f;
    [SerializeField] int requiredRenderReadyPolls = 3;

    readonly List<Canvas> _hiddenMatchCanvases = new List<Canvas>();
    Canvas _loadingCanvas;
    Image _loadingPanel;
    Text _loadingText;
    float _loadingStartedAt;
    bool _loadingVisible;
    bool _mapLoaded;

    void Update()
    {
        if (!_loadingVisible || _loadingText == null)
            return;

        int dots = ((int)(Time.unscaledTime * Mathf.Max(0.1f, loadingTextDotSpeed))) % 4;
        _loadingText.text = "Loading" + new string('.', dots);

        // Subtle text pulse while loading.
        float pulse = 0.78f + 0.22f * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * Mathf.Max(0.1f, loadingTextPulseSpeed) * Mathf.PI * 2f));
        var c = _loadingText.color;
        c.a = pulse;
        _loadingText.color = c;
    }

    void Start()
    {
        StartCoroutine(BeginMatchLoadRoutine());
    }

    IEnumerator BeginMatchLoadRoutine()
    {
        try
        {
            if (showLoadingScreenAtMatchStart)
                ShowLoadingOverlay();

            var mapScene = GameSession.I != null ? GameSession.I.selectedMapSceneName : fallbackMapSceneName;
            if (!Application.CanStreamedLevelBeLoaded(mapScene) && Application.CanStreamedLevelBeLoaded(fallbackMapSceneName))
            {
                Debug.LogWarning($"[MatchSceneController] Map scene '{mapScene}' is not loadable. Falling back to '{fallbackMapSceneName}'.");
                mapScene = fallbackMapSceneName;
            }

            _mapLoaded = false;
            yield return LoadMapAdditiveUnderRootRoutine(mapScene, mapRoot, reparentLoadedMapRoots);

            if (showLoadingScreenAtMatchStart)
            {
                if (_mapLoaded && waitForBrickCollidersBeforeHide)
                    yield return WaitForBrickColliderManagersReady();

                if (_mapLoaded && waitForBrickRenderingBeforeHide)
                    yield return WaitForBrickRenderingReady();

                float minEndTime = _loadingStartedAt + Mathf.Max(0f, minimumLoadingScreenSeconds);
                while (Time.unscaledTime < minEndTime)
                    yield return null;

                yield return FadeOutLoadingOverlayRoutine();
            }
        }
        finally
        {
            if (showLoadingScreenAtMatchStart)
                HideLoadingOverlayImmediate();
        }
    }

    IEnumerator LoadMapAdditiveUnderRootRoutine(string sceneName, Transform parent, bool reparentRoots)
    {
        var loaded = SceneManager.GetSceneByName(sceneName);
        if (!loaded.isLoaded)
        {
            var op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
            if (op == null)
            {
                Debug.LogError($"[MatchSceneController] Failed to start loading scene '{sceneName}'.");
                yield break;
            }

            while (!op.isDone)
                yield return null;

            loaded = SceneManager.GetSceneByName(sceneName);
        }

        if (!loaded.IsValid() || !loaded.isLoaded)
        {
            Debug.LogError($"[MatchSceneController] Scene '{sceneName}' did not load correctly.");
            yield break;
        }

        SceneManager.SetActiveScene(loaded);

        // Optional: make the loaded scene's root objects children of MapRoot (Unity doesn't do this automatically).
        if (reparentRoots && parent != null)
        {
            var roots = loaded.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].transform != parent && roots[i].transform.parent != parent)
                    roots[i].transform.SetParent(parent, true);
            }
        }

        _mapLoaded = true;
    }

    IEnumerator WaitForBrickColliderManagersReady()
    {
        float deadline = Time.realtimeSinceStartup + Mathf.Max(0f, maxBrickColliderWaitSeconds);
        bool sawManagers = false;
        float poll = Mathf.Max(0.02f, brickColliderPollInterval);

        while (true)
        {
            var managers = FindObjectsByType<BrickColliderManager>(FindObjectsSortMode.None);
            int count = managers != null ? managers.Length : 0;
            if (count > 0)
                sawManagers = true;

            bool allReady = sawManagers;
            for (int i = 0; i < count; i++)
            {
                var manager = managers[i];
                if (manager != null && !manager.BuildComplete)
                {
                    allReady = false;
                    break;
                }
            }

            if (allReady)
                yield break;

            if (Time.realtimeSinceStartup >= deadline)
            {
                int unreadyCount = 0;
                for (int i = 0; i < count; i++)
                {
                    var manager = managers[i];
                    if (manager != null && !manager.BuildComplete)
                        unreadyCount++;
                }

                Debug.LogWarning($"[MatchSceneController] Timed out waiting for BrickColliderManagers. managers={count}, unready={unreadyCount}. Proceeding to hide loading screen.");
                yield break;
            }

            yield return new WaitForSecondsRealtime(poll);
        }
    }

    IEnumerator WaitForBrickRenderingReady()
    {
        float deadline = Time.realtimeSinceStartup + Mathf.Max(0f, maxBrickRenderWaitSeconds);
        float poll = Mathf.Max(0.02f, brickColliderPollInterval);
        int readyStreak = 0;
        bool sawAnyGpuBrickStructure = false;
        bool sawAnyLegacyBrickRenderer = false;
        float nonGpuGraceDeadline = Time.realtimeSinceStartup + 1.0f;

        while (true)
        {
            var structures = FindObjectsByType<BrickStructure>(FindObjectsSortMode.None);
            bool structuresInitialized = true;
            int pendingInitCount = 0;

            for (int i = 0; i < structures.Length; i++)
            {
                var s = structures[i];
                if (s == null || !s.isActiveAndEnabled || s.model == null || !s.model.HasGpuData)
                    continue;

                sawAnyGpuBrickStructure = true;

                if (s.worldTransforms == null || s.aliveFlags == null)
                {
                    structuresInitialized = false;
                    pendingInitCount++;
                }
            }

            if (!sawAnyGpuBrickStructure)
            {
                // Legacy brick renderer path has no incremental GPU rebuild signal.
                var legacyRenderers = FindObjectsByType<LDrawModelRenderer>(FindObjectsSortMode.None);
                if (legacyRenderers != null && legacyRenderers.Length > 0)
                    sawAnyLegacyBrickRenderer = true;
            }

            var renderSystem = BrickRenderSystem.Instance;
            bool renderReady = renderSystem != null && renderSystem.IsRuntimeReady;

            bool readyNow = false;
            if (sawAnyGpuBrickStructure)
            {
                readyNow = structuresInitialized && renderReady;
            }
            else if (sawAnyLegacyBrickRenderer)
            {
                // Give legacy renderers a short settle window, then proceed.
                readyNow = Time.realtimeSinceStartup >= nonGpuGraceDeadline;
            }
            else if (Time.realtimeSinceStartup >= nonGpuGraceDeadline)
            {
                // No brick renderers detected in scene; do not block loading UI.
                readyNow = true;
            }

            if (readyNow)
                readyStreak++;
            else
                readyStreak = 0;

            if (readyStreak >= Mathf.Max(1, requiredRenderReadyPolls))
            {
                // Give one final frame for visible submission after ready-state is reached.
                yield return null;
                yield break;
            }

            if (Time.realtimeSinceStartup >= deadline)
            {
                int totalStructures = structures != null ? structures.Length : 0;
                int gpuStructures = 0;
                for (int i = 0; i < totalStructures; i++)
                {
                    var s = structures[i];
                    if (s != null && s.isActiveAndEnabled && s.model != null && s.model.HasGpuData)
                        gpuStructures++;
                }

                int activeBatches = renderSystem != null ? renderSystem.ActiveBatchCount : -1;
                int registered = renderSystem != null ? renderSystem.RegisteredStructureCount : -1;
                bool rebuilding = renderSystem != null && renderSystem.IsRebuilding;

                Debug.LogWarning($"[MatchSceneController] Timed out waiting for brick rendering readiness. structures={totalStructures}, gpuStructures={gpuStructures}, structuresInitialized={structuresInitialized}, pendingInit={pendingInitCount}, renderSystem={(renderSystem != null)}, renderReady={renderReady}, rebuilding={rebuilding}, activeBatches={activeBatches}, registeredStructures={registered}. Proceeding to hide loading screen.");
                yield break;
            }

            yield return new WaitForSecondsRealtime(poll);
        }
    }

    IEnumerator FadeOutLoadingOverlayRoutine()
    {
        if (_loadingCanvas == null || _loadingPanel == null)
            yield break;

        float duration = Mathf.Max(0f, loadingFadeOutSeconds);
        if (duration <= 0.001f)
            yield break;

        Color panelStart = _loadingPanel.color;
        Color textStart = _loadingText != null ? _loadingText.color : Color.white;
        float start = Time.unscaledTime;

        while (true)
        {
            float t = Mathf.Clamp01((Time.unscaledTime - start) / duration);

            Color panel = panelStart;
            panel.a = Mathf.Lerp(panelStart.a, 0f, t);
            _loadingPanel.color = panel;

            if (_loadingText != null)
            {
                Color tc = textStart;
                tc.a = Mathf.Lerp(textStart.a, 0f, t);
                _loadingText.color = tc;
            }

            if (t >= 1f)
                yield break;

            yield return null;
        }
    }

    void ShowLoadingOverlay()
    {
        _hiddenMatchCanvases.Clear();

        var canvases = FindObjectsByType<Canvas>(FindObjectsSortMode.None);
        for (int i = 0; i < canvases.Length; i++)
        {
            var canvas = canvases[i];
            if (canvas == null || !canvas.enabled)
                continue;

            if (canvas.gameObject.scene != gameObject.scene)
                continue;

            canvas.enabled = false;
            _hiddenMatchCanvases.Add(canvas);
        }

        var canvasGo = new GameObject("MatchLoadingCanvas");
        _loadingCanvas = canvasGo.AddComponent<Canvas>();
        _loadingCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _loadingCanvas.sortingOrder = short.MaxValue;

        canvasGo.AddComponent<CanvasScaler>();
        canvasGo.AddComponent<GraphicRaycaster>();

        var panelGo = new GameObject("BlackPanel");
        panelGo.transform.SetParent(canvasGo.transform, false);
        var panelRect = panelGo.AddComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;

        _loadingPanel = panelGo.AddComponent<Image>();
        _loadingPanel.color = Color.black;

        var textGo = new GameObject("LoadingText");
        textGo.transform.SetParent(panelGo.transform, false);
        var textRect = textGo.AddComponent<RectTransform>();
        textRect.anchorMin = new Vector2(0.5f, 0.5f);
        textRect.anchorMax = new Vector2(0.5f, 0.5f);
        textRect.pivot = new Vector2(0.5f, 0.5f);
        textRect.anchoredPosition = Vector2.zero;
        textRect.sizeDelta = new Vector2(800f, 140f);

        _loadingText = textGo.AddComponent<Text>();
        _loadingText.font = ResolveBuiltinFont();
        _loadingText.fontSize = Mathf.Max(12, loadingTextFontSize);
        _loadingText.fontStyle = FontStyle.Bold;
        _loadingText.alignment = TextAnchor.MiddleCenter;
        _loadingText.color = Color.white;
        _loadingText.raycastTarget = false;
        _loadingText.text = "Loading";

        if (_loadingText.font == null)
            Debug.LogError("[MatchSceneController] Failed to resolve a built-in font for loading text.");

        _loadingStartedAt = Time.unscaledTime;
        _loadingVisible = true;
    }

    void HideLoadingOverlay()
    {
        HideLoadingOverlayImmediate();
    }

    void HideLoadingOverlayImmediate()
    {
        _loadingVisible = false;

        for (int i = 0; i < _hiddenMatchCanvases.Count; i++)
        {
            var canvas = _hiddenMatchCanvases[i];
            if (canvas != null)
                canvas.enabled = true;
        }

        _hiddenMatchCanvases.Clear();

        if (_loadingCanvas != null)
            Destroy(_loadingCanvas.gameObject);

        _loadingCanvas = null;
        _loadingPanel = null;
        _loadingText = null;
    }

    static Font ResolveBuiltinFont()
    {
        // Unity 6+ legacy UI built-in runtime font.
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font != null)
            return font;

        // Fallback for older Unity versions.
        font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }

}
