using Fusion;
using Fusion.Sockets;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class BasicSpawner : MonoBehaviour, INetworkRunnerCallbacks
{
    [SerializeField] NetworkPrefabRef playerPrefab;

    readonly Dictionary<PlayerRef, NetworkObject> spawned = new Dictionary<PlayerRef, NetworkObject>();
    readonly HashSet<PlayerRef> pendingSpawn = new HashSet<PlayerRef>();
    NetworkRunner runner;

    int attackQueuedCount;
    Transform cachedCamPivot;
    int cachedCamFrame = -1;
    bool callbacksRegistered;
    bool spawnHandlerReady;

    void Update()
    {
        if (Input.GetMouseButtonDown(0))
            attackQueuedCount++;

        // Safety net: detect destroyed player objects and re-spawn on the server.
        if (spawnHandlerReady && callbacksRegistered && runner != null && runner.IsRunning && runner.IsServer)
        {
            foreach (var p in runner.ActivePlayers)
            {
                if (spawned.TryGetValue(p, out var obj))
                {
                    if (obj == null)
                    {
                        Debug.LogWarning($"[BasicSpawner] Player {p} object was DESTROYED – removing stale entry and re-spawning.");
                        spawned.Remove(p);
                        SpawnForPlayer(runner, p);
                    }
                }
                else
                {
                    Debug.Log($"[BasicSpawner] Update: spawning missing player {p}.");
                    SpawnForPlayer(runner, p);
                }
            }
        }
    }

    IEnumerator Start()
    {
        Debug.Log("[BasicSpawner] Start coroutine begun – searching for runner...");

        while (runner == null || !runner.IsRunning)
        {
            var bootstrap = AppRoot.Instance
                ? AppRoot.Instance.GetComponent<BBB_FusionBootstrap>()
                : null;

            if (bootstrap == null)
                Debug.LogWarning("[BasicSpawner] BBB_FusionBootstrap not found on AppRoot.");

            runner = bootstrap != null ? bootstrap.Runner : null;

            if (runner != null && !runner.IsRunning)
                Debug.Log("[BasicSpawner] Runner found but not yet running, waiting...");

            yield return null;
        }

        runner.AddCallbacks(this);
        callbacksRegistered = true;
        Debug.Log($"[BasicSpawner] Callbacks registered. IsServer={runner.IsServer}, ActivePlayers={runner.ActivePlayers.Count()}");

        if (runner.IsServer)
        {
            // Wait for SpawnHandler (may be in an additively-loaded sub-scene)
            while (SpawnHandler.Instance == null)
                yield return null;

            spawnHandlerReady = true;

            SpawnMissingPlayers(runner);
        }
    }

    void OnDestroy()
    {
        if (runner != null)
            runner.RemoveCallbacks(this);
    }

    void SpawnMissingPlayers(NetworkRunner r)
    {
        int count = 0;
        foreach (var p in r.ActivePlayers)
        {
            count++;
            if (!spawned.ContainsKey(p))
                SpawnForPlayer(r, p);
        }
        Debug.Log($"[BasicSpawner] SpawnMissingPlayers checked {count} active player(s), {spawned.Count} already spawned.");
    }

    void SpawnForPlayer(NetworkRunner r, PlayerRef p)
    {
        if (!playerPrefab.IsValid)
        {
            Debug.LogError("[BasicSpawner] playerPrefab is not valid (NetworkPrefabRef not set / not in NetworkProjectConfig).");
            return;
        }

        // Round-robin team assignment: Samurai=1, Knights=2, Pirates=3
        var team = (BricksBladesBlackpowder.Teams.Team)((Math.Abs(p.RawEncoded) % 3) + 1);

        // Get spawn pose from SpawnHandler; fall back to origin if not configured
        var pos = Vector3.zero;
        var rot = Quaternion.identity;
        if (SpawnHandler.Instance != null && SpawnHandler.Instance.TryGetSpawnPose(team, p, out var spawnPos, out var spawnRot))
        {
            pos = spawnPos;
            rot = spawnRot;
        }
        else
        {
            Debug.LogWarning($"[BasicSpawner] No spawn points for team {team} – spawning at origin.");
        }

        var obj = r.Spawn(playerPrefab, pos, rot, p);

        var teamIdentity = obj.GetComponentInChildren<BricksBladesBlackpowder.Teams.TeamIdentity>(true)
            ?? obj.GetComponentInParent<BricksBladesBlackpowder.Teams.TeamIdentity>();
        if (teamIdentity == null)
        {
            teamIdentity = obj.gameObject.AddComponent<BricksBladesBlackpowder.Teams.TeamIdentity>();
            Debug.LogWarning($"[BasicSpawner] Spawned player {p} had no TeamIdentity in hierarchy. Added TeamIdentity to root.");
        }

        teamIdentity.team = team;

        var colorApplicator = obj.GetComponent<BricksBladesBlackpowder.Teams.TeamColorApplicator>();
        if (colorApplicator != null)
            colorApplicator.SetTeam(team);

        spawned[p] = obj;

        Debug.Log($"[BasicSpawner] Spawned player {p} as {team} -> {(obj ? obj.name : "NULL")}");
    }

    public void OnPlayerJoined(NetworkRunner r, PlayerRef p)
    {
        Debug.Log($"[BasicSpawner] OnPlayerJoined: player={p}, IsServer={r.IsServer}");
        if (!r.IsServer) return;
        if (spawned.ContainsKey(p)) return;

        if (!spawnHandlerReady)
        {
            StartCoroutine(SpawnPlayerWhenReady(r, p));
            return;
        }

        SpawnForPlayer(r, p);
    }

    public void OnPlayerLeft(NetworkRunner r, PlayerRef p)
    {
        Debug.LogWarning($"[BasicSpawner] OnPlayerLeft: player={p}, IsServer={r.IsServer}");
        if (!r.IsServer) return;

        if (spawned.TryGetValue(p, out var obj))
        {
            r.Despawn(obj);
            spawned.Remove(p);
        }
    }

    public void OnSceneLoadDone(NetworkRunner r)
    {
        Debug.Log($"[BasicSpawner] OnSceneLoadDone: IsServer={r.IsServer}");
        if (r.IsServer && spawnHandlerReady)
            SpawnMissingPlayers(r);
    }

    IEnumerator SpawnPlayerWhenReady(NetworkRunner r, PlayerRef p)
    {
        if (!pendingSpawn.Add(p))
            yield break;

        while (!spawnHandlerReady)
            yield return null;

        pendingSpawn.Remove(p);

        if (r == null || !r.IsRunning || !r.IsServer)
            yield break;

        if (spawned.ContainsKey(p))
            yield break;

        SpawnForPlayer(r, p);
    }

    public void OnInput(NetworkRunner r, NetworkInput input)
    {
        var data = new NetworkInputData();

        float h = 0f;
        float v = 0f;

        if (Input.GetKey(KeyCode.W)) v += 1f;
        if (Input.GetKey(KeyCode.S)) v -= 1f;
        if (Input.GetKey(KeyCode.D)) h += 1f;
        if (Input.GetKey(KeyCode.A)) h -= 1f;

        Vector3 raw = new Vector3(h, 0f, v);
        if (raw.sqrMagnitude > 1f) raw.Normalize();

        data.running = Input.GetKey(KeyCode.LeftShift);
        data.attack = attackQueuedCount > 0;
        if (data.attack && attackQueuedCount > 0)
            attackQueuedCount--;
        data.combat = Cursor.lockState == CursorLockMode.Locked;
        data.block = Input.GetMouseButton(1);

        // Cache Camera.main lookup — avoid repeated property access every tick
        if (cachedCamFrame != Time.frameCount)
        {
            cachedCamFrame = Time.frameCount;
            var mainCam = Camera.main;
            cachedCamPivot = mainCam ? mainCam.transform : null;
        }
        Transform pivot = cachedCamPivot;
        Vector3 lookDir = Vector3.forward;
        if (pivot)
        {
            Vector3 camForward = pivot.forward; camForward.y = 0f;
            Vector3 camRight   = pivot.right;   camRight.y = 0f;
            raw = camForward.normalized * raw.z + camRight.normalized * raw.x;
            lookDir = camForward;
        }

        if (lookDir.sqrMagnitude > 0.001f)
            lookDir.Normalize();

        data.direction = raw;
        data.lookDirection = lookDir;
        input.Set(data);
    }

    // ---- Required callback stubs ----
    public void OnInputMissing(NetworkRunner r, PlayerRef p, NetworkInput input) { }
    public void OnShutdown(NetworkRunner r, ShutdownReason s) { Debug.LogWarning($"[BasicSpawner] OnShutdown: reason={s}"); }
    public void OnConnectedToServer(NetworkRunner r) { Debug.Log("[BasicSpawner] OnConnectedToServer"); }
    public void OnDisconnectedFromServer(NetworkRunner r, NetDisconnectReason reason) { Debug.LogWarning($"[BasicSpawner] OnDisconnectedFromServer: reason={reason}"); }
    public void OnConnectRequest(NetworkRunner r, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
    public void OnConnectFailed(NetworkRunner r, NetAddress remoteAddress, NetConnectFailedReason reason) { Debug.LogWarning($"[BasicSpawner] OnConnectFailed: reason={reason}"); }
    public void OnUserSimulationMessage(NetworkRunner r, SimulationMessagePtr message) { }
    public void OnSessionListUpdated(NetworkRunner r, List<SessionInfo> sessionList) { }
    public void OnCustomAuthenticationResponse(NetworkRunner r, Dictionary<string, object> data) { }
    public void OnHostMigration(NetworkRunner r, HostMigrationToken hostMigrationToken) { }
    public void OnSceneLoadStart(NetworkRunner r) { Debug.Log("[BasicSpawner] OnSceneLoadStart"); }
    public void OnObjectExitAOI(NetworkRunner r, NetworkObject obj, PlayerRef player) { }
    public void OnObjectEnterAOI(NetworkRunner r, NetworkObject obj, PlayerRef player) { }
    public void OnReliableDataReceived(NetworkRunner r, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
    public void OnReliableDataProgress(NetworkRunner r, PlayerRef player, ReliableKey key, float progress) { }
}
