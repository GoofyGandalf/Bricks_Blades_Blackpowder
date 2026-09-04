using System;
using System.Collections.Generic;
using BricksBladesBlackpowder.Teams;
using Fusion;
using UnityEngine;

public class SpawnHandler : MonoBehaviour
{
    [Serializable]
    struct TeamSpawnSet
    {
        public Team team;
        public Transform[] points;
    }

    [SerializeField] TeamSpawnSet[] teamSpawns = Array.Empty<TeamSpawnSet>();
    [SerializeField] TeamSpawnSet[] npcOnlyTeamSpawns = Array.Empty<TeamSpawnSet>();

    public static SpawnHandler Instance { get; private set; }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[SpawnHandler] Multiple SpawnHandler instances found. Using the newest one.");
        }

        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    public bool HasSpawnPoints(Team team)
    {
        return TryGetTeamSpawnSet(teamSpawns, team, out var set)
            && set.points != null
            && set.points.Length > 0;
    }

    public bool HasNpcSpawnPoints(Team team, bool allowFallbackToTeamSpawns = true)
    {
        if (TryGetTeamSpawnSet(npcOnlyTeamSpawns, team, out var npcOnly)
            && npcOnly.points != null
            && npcOnly.points.Length > 0)
        {
            return true;
        }

        return allowFallbackToTeamSpawns && HasSpawnPoints(team);
    }

    public bool TryGetSpawnPose(Team team, PlayerRef player, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;

        if (!TryGetTeamSpawnSet(teamSpawns, team, out var set) || set.points == null || set.points.Length == 0)
            return false;

        int idx = GetStableIndex(player.RawEncoded, set.points.Length);
        var point = set.points[idx];
        if (point == null)
            return false;

        position = point.position;
        rotation = point.rotation;
        return true;
    }

    public bool TryGetNpcSpawnPose(
        Team team,
        int spawnIndex,
        out Vector3 position,
        out Quaternion rotation,
        bool allowFallbackToTeamSpawns = true)
    {
        position = default;
        rotation = Quaternion.identity;

        if (TryGetPoseFromSets(npcOnlyTeamSpawns, team, spawnIndex, out position, out rotation))
            return true;

        if (!allowFallbackToTeamSpawns)
            return false;

        return TryGetPoseFromSets(teamSpawns, team, spawnIndex, out position, out rotation);
    }

    public bool TryGetRandomNpcOnlySpawnPose(Team team, int seed, out Vector3 position, out Quaternion rotation)
    {
        return TryGetRandomPoseFromSets(npcOnlyTeamSpawns, team, seed, out position, out rotation);
    }

    public bool TryGetRandomCombinedSpawnPose(Team team, int seed, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;

        var candidates = new List<Transform>(8);
        CollectValidPoints(teamSpawns, team, candidates);
        CollectValidPoints(npcOnlyTeamSpawns, team, candidates);
        if (candidates.Count == 0)
            return false;

        int idx = GetStableIndex(seed, candidates.Count);
        var point = candidates[idx];
        if (point == null)
            return false;

        position = point.position;
        rotation = point.rotation;
        return true;
    }

    static int GetStableIndex(int seed, int length)
    {
        if (length <= 0)
            return 0;

        return (int)((uint)seed % (uint)length);
    }

    bool TryGetPoseFromSets(
        TeamSpawnSet[] source,
        Team team,
        int spawnIndex,
        out Vector3 position,
        out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;

        if (!TryGetTeamSpawnSet(source, team, out var set) || set.points == null || set.points.Length == 0)
            return false;

        int idx = GetStableIndex(spawnIndex, set.points.Length);
        var point = set.points[idx];
        if (point == null)
            return false;

        position = point.position;
        rotation = point.rotation;
        return true;
    }

    bool TryGetRandomPoseFromSets(
        TeamSpawnSet[] source,
        Team team,
        int seed,
        out Vector3 position,
        out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;

        var candidates = new List<Transform>(4);
        CollectValidPoints(source, team, candidates);
        if (candidates.Count == 0)
            return false;

        int idx = GetStableIndex(seed, candidates.Count);
        var point = candidates[idx];
        if (point == null)
            return false;

        position = point.position;
        rotation = point.rotation;
        return true;
    }

    void CollectValidPoints(TeamSpawnSet[] source, Team team, List<Transform> into)
    {
        if (!TryGetTeamSpawnSet(source, team, out var set) || set.points == null || set.points.Length == 0)
            return;

        for (int i = 0; i < set.points.Length; i++)
        {
            if (set.points[i] != null)
                into.Add(set.points[i]);
        }
    }

    bool TryGetTeamSpawnSet(TeamSpawnSet[] source, Team team, out TeamSpawnSet result)
    {
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i].team == team)
            {
                result = source[i];
                return true;
            }
        }

        result = default;
        return false;
    }
}