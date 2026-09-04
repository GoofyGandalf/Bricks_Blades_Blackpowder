using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using UnityEngine;

public class FusionRuntimeDiagnostics : MonoBehaviour, INetworkRunnerCallbacks
{
    NetworkRunner runner;
    float lastRealtime;
    const float StallWarnSeconds = 1.5f;

    public void Initialize(NetworkRunner activeRunner)
    {
        runner = activeRunner;
    }

    void OnEnable()
    {
        Application.logMessageReceived += OnLogMessage;
        lastRealtime = Time.realtimeSinceStartup;
    }

    void OnDisable()
    {
        Application.logMessageReceived -= OnLogMessage;
    }

    void Update()
    {
        if (runner == null || !runner.IsRunning)
        {
            lastRealtime = Time.realtimeSinceStartup;
            return;
        }

        float now = Time.realtimeSinceStartup;
        float gap = now - lastRealtime;
        lastRealtime = now;

        if (gap >= StallWarnSeconds)
            Debug.LogWarning($"[FusionDiag] Main thread stall detected: {gap:F2}s (runInBackground={Application.runInBackground}, focused={Application.isFocused})");
    }

    void OnLogMessage(string condition, string stackTrace, UnityEngine.LogType type)
    {
        if (type != UnityEngine.LogType.Exception && type != UnityEngine.LogType.Error)
            return;

        if (runner == null || !runner.IsRunning)
            return;

        Debug.LogWarning($"[FusionDiag] Runtime {type} while runner active. condition={condition}\n{stackTrace}");
    }

    public void OnConnectedToServer(NetworkRunner r)
    {
        Debug.Log($"[FusionDiag] ConnectedToServer. IsServer={r.IsServer}, IsClient={r.IsClient}, Session={r.SessionInfo.Name}");
    }

    public void OnDisconnectedFromServer(NetworkRunner r, NetDisconnectReason reason)
    {
        Debug.LogWarning($"[FusionDiag] DisconnectedFromServer. reason={reason}, IsServer={r.IsServer}, IsClient={r.IsClient}");
    }

    public void OnConnectFailed(NetworkRunner r, NetAddress remoteAddress, NetConnectFailedReason reason)
    {
        Debug.LogError($"[FusionDiag] ConnectFailed. remote={remoteAddress}, reason={reason}");
    }

    public void OnShutdown(NetworkRunner r, ShutdownReason s)
    {
        Debug.LogWarning($"[FusionDiag] Runner shutdown. reason={s}");
    }

    public void OnPlayerJoined(NetworkRunner r, PlayerRef p)
    {
        Debug.Log($"[FusionDiag] PlayerJoined {p}. IsServer={r.IsServer}");
    }

    public void OnPlayerLeft(NetworkRunner r, PlayerRef p)
    {
        Debug.LogWarning($"[FusionDiag] PlayerLeft {p}. IsServer={r.IsServer}");
    }

    public void OnSceneLoadStart(NetworkRunner r)
    {
        Debug.Log("[FusionDiag] SceneLoadStart");
    }

    public void OnSceneLoadDone(NetworkRunner r)
    {
        Debug.Log("[FusionDiag] SceneLoadDone");
    }

    public void OnInput(NetworkRunner r, NetworkInput input) { }
    public void OnInputMissing(NetworkRunner r, PlayerRef p, NetworkInput input) { }
    public void OnConnectRequest(NetworkRunner r, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
    public void OnUserSimulationMessage(NetworkRunner r, SimulationMessagePtr message) { }
    public void OnSessionListUpdated(NetworkRunner r, List<SessionInfo> sessionList) { }
    public void OnCustomAuthenticationResponse(NetworkRunner r, Dictionary<string, object> data) { }
    public void OnHostMigration(NetworkRunner r, HostMigrationToken hostMigrationToken) { }
    public void OnObjectExitAOI(NetworkRunner r, NetworkObject obj, PlayerRef player) { }
    public void OnObjectEnterAOI(NetworkRunner r, NetworkObject obj, PlayerRef player) { }
    public void OnReliableDataReceived(NetworkRunner r, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
    public void OnReliableDataProgress(NetworkRunner r, PlayerRef player, ReliableKey key, float progress) { }
}
