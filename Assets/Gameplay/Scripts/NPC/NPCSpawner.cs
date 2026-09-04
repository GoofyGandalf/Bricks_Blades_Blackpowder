using Fusion;
using Fusion.Sockets;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using BricksBladesBlackpowder.Teams;

namespace BricksBladesBlackpowder.NPC
{
    public class NPCSpawner : MonoBehaviour, INetworkRunnerCallbacks
    {
        public List<NPCProfile> npcProfiles;
        public NetworkPrefabRef npcPrefab;
        [Header("Initial Team Wave")]
        [Min(0)] public int sharedAndNpcSpawnCountPerTeam = 3;
        [Min(0)] public int npcOnlySpawnCountPerTeam = 2;
        [Header("NavMesh Spawn Snap")]
        [SerializeField, Min(0.5f)] float navMeshSampleDistance = 30f;
        [Tooltip("Legacy fallback team only used when SpawnNPC(int) is called directly. Initial team-wave spawning ignores this.")]
        public Team defaultTeam = Team.Pirates;
        [SerializeField] bool randomizeTeamSpawnSelection = true;
        [Header("Performance")]
        [SerializeField, Min(1)] int initialSpawnBatchSize = 1;

        private readonly List<NPCController> npcs = new List<NPCController>();
        static readonly Team[] AllTeams = { Team.Samurai, Team.Knights, Team.Pirates };
        private NetworkRunner runner;
        private bool callbacksRegistered;
        private int spawnSequence;

        System.Collections.IEnumerator Start()
        {
            while (runner == null || !runner.IsRunning)
            {
                var bootstrap = AppRoot.Instance ? AppRoot.Instance.GetComponent<BBB_FusionBootstrap>() : null;
                runner = bootstrap != null ? bootstrap.Runner : null;
                yield return null;
            }

            runner.AddCallbacks(this);
            callbacksRegistered = true;

            if (!runner.IsServer)
                yield break;

            while (SpawnHandler.Instance == null)
                yield return null;

            yield return SpawnInitialTeamWaveRoutine();
        }

        void OnDestroy()
        {
            if (callbacksRegistered && runner != null)
                runner.RemoveCallbacks(this);
        }

        public NPCController SpawnNPC(int spawnIndex)
        {
            // Backwards-compatible API used by NPCSpawnGroup.
            Team fallbackTeam = defaultTeam;
            if (fallbackTeam == Team.None)
            {
                fallbackTeam = Team.Samurai;
                Debug.LogWarning("[NPCSpawner] SpawnNPC(int) called with Default Team=None. Falling back to Samurai.", this);
            }

            return SpawnNPCForTeam(fallbackTeam, spawnIndex, npcOnlyOnly: false);
        }

        public NPCController SpawnNPCForTeam(Team team, int spawnIndex, bool npcOnlyOnly)
        {
            if (runner == null || !runner.IsRunning || !runner.IsServer)
                return null;

            if (!npcPrefab.IsValid)
                return null;

            if (!TryGetNpcSpawnPose(team, spawnIndex, npcOnlyOnly, out var baseSpawnPos, out var baseSpawnRot))
            {
                string mode = npcOnlyOnly ? "NPC-only" : "combined team+NPC";
                Debug.LogWarning($"[NPCSpawner] No {mode} spawn points found for team {team}.", this);
                return null;
            }

            Vector3 spawnPos = baseSpawnPos;
            if (NavMesh.SamplePosition(baseSpawnPos, out NavMeshHit hit, navMeshSampleDistance, NavMesh.AllAreas))
                spawnPos = hit.position;
            else
                Debug.LogWarning($"[NPCSpawner] Could not snap spawn to NavMesh within {navMeshSampleDistance}m from {baseSpawnPos}. Spawning at raw point.", this);
            var obj = runner.Spawn(npcPrefab, spawnPos, baseSpawnRot);
            if (!obj)
                return null;

            var npc = obj.GetComponent<NPCController>();
            if (npc == null) return null;

            // Assign profile
            if (npcProfiles != null && npcProfiles.Count > 0)
                npc.profile = npcProfiles[spawnIndex % npcProfiles.Count];

            // Assign team
            var teamId = obj.GetComponentInChildren<TeamIdentity>(true)
                ?? obj.GetComponentInParent<TeamIdentity>();
            if (!teamId)
            {
                teamId = obj.gameObject.AddComponent<TeamIdentity>();
                Debug.LogWarning($"[NPCSpawner] Spawned NPC had no TeamIdentity in hierarchy. Added TeamIdentity to root for team {team}.", this);
            }

            teamId.team = team;
            var colorApplicator = obj.GetComponentInChildren<BricksBladesBlackpowder.Teams.TeamColorApplicator>(true)
                ?? obj.GetComponentInParent<BricksBladesBlackpowder.Teams.TeamColorApplicator>();
            if (colorApplicator != null)
                colorApplicator.SetTeam(teamId.team);

            npcs.Add(npc);
            return npc;
        }

        System.Collections.IEnumerator SpawnInitialTeamWaveRoutine()
        {
            int created = 0;
            int spawnedThisFrame = 0;

            int SpawnAndCount(Team team, bool npcOnly)
            {
                return SpawnNPCForTeam(team, spawnSequence++, npcOnlyOnly: npcOnly) != null ? 1 : 0;
            }

            foreach (var team in AllTeams)
            {
                for (int i = 0; i < sharedAndNpcSpawnCountPerTeam; i++)
                {
                    created += SpawnAndCount(team, npcOnly: false);
                    spawnedThisFrame++;

                    if (spawnedThisFrame >= initialSpawnBatchSize)
                    {
                        spawnedThisFrame = 0;
                        yield return null;
                    }
                }

                for (int i = 0; i < npcOnlySpawnCountPerTeam; i++)
                {
                    created += SpawnAndCount(team, npcOnly: true);
                    spawnedThisFrame++;

                    if (spawnedThisFrame >= initialSpawnBatchSize)
                    {
                        spawnedThisFrame = 0;
                        yield return null;
                    }
                }
            }

            Debug.Log($"[NPCSpawner] Spawned initial team wave NPC count={created} (per team: combined={sharedAndNpcSpawnCountPerTeam}, npcOnly={npcOnlySpawnCountPerTeam}).", this);
        }

        bool TryGetNpcSpawnPose(Team team, int spawnIndex, bool npcOnlyOnly, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;

            var handler = SpawnHandler.Instance;
            if (handler == null)
                return false;

            int seed = randomizeTeamSpawnSelection
                ? (spawnIndex * 1103515245) ^ Environment.TickCount
                : spawnIndex;

            if (npcOnlyOnly)
            {
                return handler.TryGetRandomNpcOnlySpawnPose(team, seed, out position, out rotation);
            }

            return handler.TryGetRandomCombinedSpawnPose(team, seed, out position, out rotation);
        }

        public void DespawnNPC(NPCController npc)
        {
            if (npc == null)
                return;

            if (npcs.Contains(npc))
            {
                npcs.Remove(npc);
                if (runner != null && runner.IsRunning && runner.IsServer)
                {
                    var obj = npc.GetComponent<NetworkObject>();
                    if (obj)
                        runner.Despawn(obj);
                }
            }
        }

        public List<NPCController> GetAllNPCs() => npcs;

        public void OnPlayerJoined(NetworkRunner r, PlayerRef p) { }
        public void OnPlayerLeft(NetworkRunner r, PlayerRef p) { }
        public void OnInput(NetworkRunner r, NetworkInput input) { }
        public void OnInputMissing(NetworkRunner r, PlayerRef p, NetworkInput input) { }
        public void OnShutdown(NetworkRunner r, ShutdownReason s) { }
        public void OnConnectedToServer(NetworkRunner r) { }
        public void OnDisconnectedFromServer(NetworkRunner r, NetDisconnectReason reason) { }
        public void OnConnectRequest(NetworkRunner r, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        public void OnConnectFailed(NetworkRunner r, NetAddress remoteAddress, NetConnectFailedReason reason) { }
        public void OnUserSimulationMessage(NetworkRunner r, SimulationMessagePtr message) { }
        public void OnSessionListUpdated(NetworkRunner r, List<SessionInfo> sessionList) { }
        public void OnCustomAuthenticationResponse(NetworkRunner r, Dictionary<string, object> data) { }
        public void OnHostMigration(NetworkRunner r, HostMigrationToken hostMigrationToken) { }
        public void OnSceneLoadStart(NetworkRunner r) { }
        public void OnSceneLoadDone(NetworkRunner r) { }
        public void OnObjectExitAOI(NetworkRunner r, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner r, NetworkObject obj, PlayerRef player) { }
        public void OnReliableDataReceived(NetworkRunner r, PlayerRef player, ReliableKey key, System.ArraySegment<byte> data) { }
        public void OnReliableDataProgress(NetworkRunner r, PlayerRef player, ReliableKey key, float progress) { }
    }
}
