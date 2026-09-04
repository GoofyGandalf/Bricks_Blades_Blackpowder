using Fusion;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public class BBB_FusionBootstrap : MonoBehaviour
{
    NetworkRunner runner;

    void Awake()
    {
        // Prevent host/client timeouts when this window loses focus during local testing.
        Application.runInBackground = true;
        Debug.Log($"[FusionBoot] runInBackground={Application.runInBackground}");
    }

    /// <summary>The currently active NetworkRunner, or null.</summary>
    public NetworkRunner Runner => runner;

    /// <summary>Fired when the session list is updated (for the browser).</summary>
    public event Action<List<SessionInfo>> OnSessionListUpdated;

    // ── Session list query runner (separate from the game runner) ──
    NetworkRunner _lobbyRunner;

    /// <summary>Host a session with the given name. Password stored in session properties.</summary>
    public async Task<bool> HostAsync(string sessionName, string password = "")
    {
        var session = AppRoot.Instance.GameSession;
        session.sessionName = sessionName;
        session.sessionPassword = password;

        return await StartSessionAsync(GameMode.Host, sessionName, password);
    }

    /// <summary>Join a specific session by name. Returns false if the password doesn't match.</summary>
    public async Task<bool> JoinAsync(string sessionName)
    {
        var session = AppRoot.Instance.GameSession;
        session.sessionName = sessionName;

        return await StartSessionAsync(GameMode.Client, sessionName, "");
    }

    /// <summary>
    /// Start querying the Fusion lobby for available sessions.
    /// Results arrive via OnSessionListUpdated.
    /// </summary>
    public async Task StartSessionBrowser()
    {
        await StopSessionBrowser();

        var go = new GameObject("FusionLobbyBrowser");
        DontDestroyOnLoad(go);

        _lobbyRunner = go.AddComponent<NetworkRunner>();

        var callbacks = go.AddComponent<SessionListCallbackRelay>();
        callbacks.Owner = this;
        _lobbyRunner.AddCallbacks(callbacks);

        var result = await _lobbyRunner.JoinSessionLobby(SessionLobby.ClientServer);

        if (!result.Ok)
        {
            Debug.LogWarning($"[FusionBoot] JoinSessionLobby failed: {result.ErrorMessage}");
            Destroy(go);
            _lobbyRunner = null;
        }
    }

    /// <summary>Stop the session browser runner.</summary>
    public async Task StopSessionBrowser()
    {
        if (_lobbyRunner != null)
        {
            await _lobbyRunner.Shutdown();

            if (_lobbyRunner != null && _lobbyRunner.gameObject != null)
                Destroy(_lobbyRunner.gameObject);

            _lobbyRunner = null;
        }
    }

    internal void RaiseSessionListUpdated(List<SessionInfo> sessions)
    {
        OnSessionListUpdated?.Invoke(sessions);
    }

    /// <summary>
    /// Shuts down and destroys the current runner, if any.
    /// </summary>
    public async Task ShutdownAsync()
    {
        await StopSessionBrowser();

        if (runner != null)
        {
            await runner.Shutdown();

            if (runner != null && runner.gameObject != null)
                Destroy(runner.gameObject);

            runner = null;
        }
    }

    async Task<bool> StartSessionAsync(GameMode mode, string sessionName, string password)
    {
        // Fusion runners are single-use; tear down any previous one.
        await ShutdownAsync();

        // Create a fresh runner on its own GameObject.
        var runnerGO = new GameObject("FusionRunner");
        DontDestroyOnLoad(runnerGO);

        runner = runnerGO.AddComponent<NetworkRunner>();
        runner.ProvideInput = true;

        var diagnostics = runnerGO.AddComponent<FusionRuntimeDiagnostics>();
        diagnostics.Initialize(runner);
        runner.AddCallbacks(diagnostics);

        var sceneManager = runnerGO.AddComponent<NetworkSceneManagerDefault>();
        sceneManager.IsSceneTakeOverEnabled = false;

        // Store password in session properties so clients can read it
        var properties = new Dictionary<string, SessionProperty>();
        if (!string.IsNullOrEmpty(password))
            properties["pwd"] = password;

        var args = new StartGameArgs
        {
            GameMode = mode,
            SessionName = sessionName,
            SceneManager = sceneManager,
            SessionProperties = properties.Count > 0 ? properties : null
        };

        Debug.Log($"[FusionBoot] Starting Fusion – Mode={mode}, Session=\"{sessionName}\", runInBackground={Application.runInBackground}");

        var result = await runner.StartGame(args);

        if (!result.Ok)
        {
            Debug.LogError($"[FusionBoot] StartGame FAILED – Reason={result.ShutdownReason}, ErrorMessage={result.ErrorMessage}");
            Destroy(runnerGO);
            runner = null;
            return false;
        }

        Debug.Log($"[FusionBoot] StartGame OK – Region={runner.SessionInfo.Region}, Session=\"{runner.SessionInfo.Name}\"");
        return true;
    }

    /// <summary>
    /// Host-only: load a scene through Fusion so all clients sync automatically.
    /// </summary>
    public void LoadFusionScene(int buildIndex)
    {
        if (runner == null || !runner.IsRunning) return;
        runner.LoadScene(SceneRef.FromIndex(buildIndex), LoadSceneMode.Single);
    }

    /// <summary>Check if a session has a password set.</summary>
    public static bool SessionHasPassword(SessionInfo session)
    {
        return session.Properties != null
            && session.Properties.TryGetValue("pwd", out _);
    }

    /// <summary>Check if a password matches a session's stored password.</summary>
    public static bool CheckPassword(SessionInfo session, string password)
    {
        if (session.Properties == null) return true;
        if (!session.Properties.TryGetValue("pwd", out var stored)) return true;
        return (string)stored == password;
    }
}
