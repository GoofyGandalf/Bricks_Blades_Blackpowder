using Fusion;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Photon Fusion networked component for brick structures.
/// Syncs alive/dead state of bricks across all clients.
///
/// Setup: Place on the same GameObject as BrickStructure + NetworkObject.
/// The server is authoritative — damage is applied server-side and the
/// resulting alive-flag deltas are sent to clients, where they drive both
/// the visual BrickStructure update and local debris spawning.
/// </summary>
[RequireComponent(typeof(BrickStructure))]
public class NetworkBrickStructure : NetworkBehaviour
{
    // ─── Networked State ───

    [Networked, Capacity(4096)]
    NetworkArray<byte> AliveFlags => default;

    [Networked] int DamageVersion { get; set; }
    [Networked] int BrickCount { get; set; }

    // ─── Local State ───
    BrickStructure _structure;
    BrickDamageSystem _damageSystem;
    int _localDamageVersion;
    byte[] _prevAlive;   // snapshot to detect what changed on sync

    public override void Spawned()
    {
        _structure = GetComponent<BrickStructure>();
        _damageSystem = FindAnyObjectByType<BrickDamageSystem>();

        if (Object.HasStateAuthority)
        {
            if (_structure.model != null && _structure.model.HasGpuData)
            {
                BrickCount = _structure.model.sortedBricks.Length;
                int count = Mathf.Min(BrickCount, AliveFlags.Length);
                for (int i = 0; i < count; i++)
                    AliveFlags.Set(i, 1);

                DamageVersion = 0;
            }
        }
        else
        {
            SyncFromNetwork();
        }

        _localDamageVersion = DamageVersion;

        // Snapshot current alive state for delta detection
        if (_structure.aliveFlags != null)
        {
            _prevAlive = new byte[_structure.aliveFlags.Length];
            System.Array.Copy(_structure.aliveFlags, _prevAlive, _prevAlive.Length);
        }
    }

    public override void FixedUpdateNetwork()
    {
        if (!Object.HasStateAuthority && DamageVersion != _localDamageVersion)
        {
            SyncFromNetworkWithDebris();
            _localDamageVersion = DamageVersion;
        }
    }

    // ─── Damage API ───

    /// <summary>
    /// Request damage at a world-space point. Always routes to server.
    /// </summary>
    public void RequestDamage(Vector3 worldHitPoint, float radius)
    {
        if (Object.HasStateAuthority)
            ServerApplyDamage(worldHitPoint, radius);
        else
            RPC_ServerApplyDamage(worldHitPoint, radius);
    }

    /// <summary>
    /// Request force-based damage.
    /// </summary>
    public void RequestDamageWithForce(Vector3 worldHitPoint, Vector3 velocity, float mass)
    {
        if (Object.HasStateAuthority)
            ServerApplyDamageWithForce(worldHitPoint, velocity, mass);
        else
            RPC_ServerApplyDamageWithForce(worldHitPoint, velocity, mass);
    }

    // ─── Server RPCs ───

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    void RPC_ServerApplyDamage(Vector3 worldHitPoint, float radius)
    {
        ServerApplyDamage(worldHitPoint, radius);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    void RPC_ServerApplyDamageWithForce(Vector3 worldHitPoint, Vector3 velocity, float mass)
    {
        ServerApplyDamageWithForce(worldHitPoint, velocity, mass);
    }

    void ServerApplyDamage(Vector3 worldHitPoint, float radius)
    {
        if (!Object.HasStateAuthority) return;
        if (_structure == null || _structure.model == null || !_structure.model.HasGpuData) return;

        if (_damageSystem != null)
        {
            _damageSystem.DealDamage(_structure, worldHitPoint, radius);
        }
        else
        {
            var hitBricks = new List<int>(32);
            _structure.FindBricksInRadius(worldHitPoint, radius, hitBricks);
            if (hitBricks.Count == 0) return;
            _structure.ApplyDamage(hitBricks);
        }

        PushAliveFlags();
    }

    void ServerApplyDamageWithForce(Vector3 worldHitPoint, Vector3 velocity, float mass)
    {
        if (!Object.HasStateAuthority) return;
        if (_structure == null || _structure.model == null || !_structure.model.HasGpuData) return;

        if (_damageSystem != null)
        {
            _damageSystem.DealDamageWithForce(_structure, worldHitPoint, velocity, mass);
        }
        else
        {
            float force = velocity.magnitude * mass;
            float normalizedForce = Mathf.Clamp01(force / 60f);
            float radius = 0.5f + normalizedForce * 1.5f;
            var hitBricks = new List<int>(32);
            _structure.FindBricksInRadius(worldHitPoint, radius, hitBricks);
            if (hitBricks.Count > 0) _structure.ApplyDamage(hitBricks);
        }

        PushAliveFlags();
    }

    /// <summary>Copy local alive flags into the networked array and bump version.</summary>
    void PushAliveFlags()
    {
        _structure.EnsureInitialized();
        if (_structure.aliveFlags == null) return;

        int count = Mathf.Min(_structure.aliveFlags.Length, AliveFlags.Length);
        for (int i = 0; i < count; i++)
            AliveFlags.Set(i, _structure.aliveFlags[i]);
        DamageVersion++;

        // Update local snapshot so server doesn't double-process
        if (_prevAlive != null)
            System.Array.Copy(_structure.aliveFlags, _prevAlive, Mathf.Min(_prevAlive.Length, _structure.aliveFlags.Length));
    }

    // ─── Client Sync ───

    void SyncFromNetwork()
    {
        _structure.EnsureInitialized();
        if (_structure == null || _structure.aliveFlags == null) return;
        int count = Mathf.Min(BrickCount, Mathf.Min(AliveFlags.Length, _structure.aliveFlags.Length));

        for (int i = 0; i < count; i++)
            _structure.aliveFlags[i] = AliveFlags[i];

        _structure.damageVersion++;
        BrickRenderSystem.NotifyStructureChanged();
        BrickRenderSystem.ForceImmediateRefresh();
        BrickConnectionSystem.NotifyStructureChanged();
    }

    /// <summary>
    /// Sync from network AND spawn debris for any bricks that just died.
    /// </summary>
    void SyncFromNetworkWithDebris()
    {
        _structure.EnsureInitialized();
        if (_structure == null || _structure.aliveFlags == null) return;
        int count = Mathf.Min(BrickCount, Mathf.Min(AliveFlags.Length, _structure.aliveFlags.Length));

        var newlyDead = new List<int>(16);

        for (int i = 0; i < count; i++)
        {
            byte netVal = AliveFlags[i];
            byte prevVal = (_prevAlive != null && i < _prevAlive.Length) ? _prevAlive[i] : _structure.aliveFlags[i];

            if (prevVal == 1 && netVal == 0)
                newlyDead.Add(i);

            _structure.aliveFlags[i] = netVal;
        }

        _structure.damageVersion++;
        BrickRenderSystem.NotifyStructureChanged();
        BrickRenderSystem.ForceImmediateRefresh();
        BrickConnectionSystem.NotifyStructureChanged();

        // Update snapshot
        if (_prevAlive != null)
            System.Array.Copy(_structure.aliveFlags, _prevAlive, Mathf.Min(_prevAlive.Length, count));

        // Spawn debris for newly-dead bricks on the client
        if (newlyDead.Count > 0 && _damageSystem != null)
            _damageSystem.SpawnDebrisForBricks(_structure, newlyDead);
    }
}
