using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;

/// <summary>
/// Per-structure runtime component. Owns alive-state and pre-computed world transforms.
/// Registers itself with BrickRenderSystem on enable.
/// </summary>
[ExecuteAlways]
public class BrickStructure : MonoBehaviour
{
    [Header("Data")]
    public LDrawModelAsset model;
    public LDrawPartRegistry parts;

    [Header("Destruction")]
    [Tooltip("Index of the anchor brick (e.g. bottom-center) for connectivity flood-fill. -1 = auto-pick lowest Y.")]
    public int anchorBrickIndex = -1;
    [Tooltip("Y threshold above the lowest brick to count as 'foundation' (support). All bricks within this band are structural anchors.")]
    public float foundationHeight = 0.5f;

    [Header("Collision")]
    [Tooltip("Automatically add per-brick colliders when this structure initialises.")]
    public bool autoAddColliders = true;

    // ── Runtime state ──
    /// <summary>1 = alive, 0 = dead. Length == model.sortedBricks.Length.</summary>
    [NonSerialized] public byte[] aliveFlags;
    /// <summary>Pre-computed world-space transforms per brick.</summary>
    [NonSerialized] public Matrix4x4[] worldTransforms;
    /// <summary>Incremented each time aliveFlags changes. BrickRenderSystem watches this.</summary>
    [NonSerialized] public int damageVersion;
    /// <summary>Indices of all bricks in the bottom "foundation" layer. These are structural supports.</summary>
    [NonSerialized] public int[] supportBricks;

    bool _transformDirty = true;
    bool _spatialHashDirty = true;
    bool _supportDataDirty = true;
    Vector3 _lastPos;
    Quaternion _lastRot;
    Vector3 _lastScale;

    // ── Static registry for network indexing ──
    static readonly List<BrickStructure> _registry = new();
    public static IReadOnlyList<BrickStructure> Registry => _registry;

    public static int GetIndex(BrickStructure s) => _registry.IndexOf(s);

    static void SortRegistry()
    {
        _registry.Sort((a, b) =>
        {
            int n = string.Compare(a.name, b.name, StringComparison.Ordinal);
            if (n != 0) return n;
            Vector3 pa = a.transform.position, pb = b.transform.position;
            n = pa.x.CompareTo(pb.x); if (n != 0) return n;
            n = pa.y.CompareTo(pb.y); if (n != 0) return n;
            return pa.z.CompareTo(pb.z);
        });
    }

    // ── Spatial hash for hit-point queries ──
    const float SpatialCellSize = 0.5f;
    Dictionary<Vector3Int, List<int>> _spatialHash;

    public void EnsureInitialized()
    {
        if (model == null || !model.HasGpuData) return;

        int count = model.sortedBricks.Length;

        bool needsInit = aliveFlags == null || worldTransforms == null
                      || aliveFlags.Length != count || worldTransforms.Length != count;

        if (!needsInit) return;

        aliveFlags = new byte[count];
        worldTransforms = new Matrix4x4[count];

        for (int i = 0; i < count; i++)
            aliveFlags[i] = 1;

        if (anchorBrickIndex < 0)
            anchorBrickIndex = -1;

        _transformDirty = true;
        _spatialHashDirty = true;
        _supportDataDirty = true;
        damageVersion = 0;

        RefreshWorldTransforms();
    }

    // Called by Unity whenever an Inspector field changes on this component.
    // Reset initialized state so EnsureInitialized() re-runs on the next frame,
    // preventing stale aliveFlags/worldTransforms when model is swapped.
    void OnValidate()
    {
        aliveFlags      = null;
        worldTransforms = null;
        anchorBrickIndex = -1;
        supportBricks = null;
        _spatialHashDirty = true;
        _supportDataDirty = true;

        BrickRenderSystem.NotifyStructureChanged();

        ApplyColliderGenerationSetting();
    }

    void OnEnable()
    {
        if (!Application.isPlaying)
            EnsureInitialized();

        // Cache transform state so first LateUpdate doesn't redundantly
        // recompute transforms and bump damageVersion (which would trigger
        // an extra BrickConnectionSystem rebuild and stall Fusion's tick).
        var t = transform;
        _lastPos = t.position;
        _lastRot = t.rotation;
        _lastScale = t.lossyScale;
        t.hasChanged = false;
        _transformDirty = false;

        ApplyColliderGenerationSetting();

        if (model != null && model.HasGpuData)
        {
            BrickRenderSystem.Register(this);

            if (BrickConnectionSystem.Instance != null)
                BrickConnectionSystem.Instance.Register(this);
        }

        if (!_registry.Contains(this))
        {
            _registry.Add(this);
            SortRegistry();
        }
    }

    void ApplyColliderGenerationSetting()
    {
        var mgr = GetComponent<BrickColliderManager>();

        if (autoAddColliders)
        {
            if (mgr == null)
                gameObject.AddComponent<BrickColliderManager>();
            else
                mgr.enabled = true;
            return;
        }

        if (mgr != null)
        {
            if (Application.isPlaying)
                Destroy(mgr);
            else
                DestroyImmediate(mgr);
        }

        // Cleanup stale collider container if it exists from earlier runs.
        var container = transform.Find("_BrickColliders");
        if (container != null)
        {
            if (Application.isPlaying)
                Destroy(container.gameObject);
            else
                DestroyImmediate(container.gameObject);
        }
    }

    void OnDisable()
    {
        _registry.Remove(this);

        BrickRenderSystem.Unregister(this);

        if (BrickConnectionSystem.Instance != null)
            BrickConnectionSystem.Instance.Unregister(this);
    }

    void LateUpdate()
    {
        var t = transform;
        if (!t.hasChanged)
            return;

        t.hasChanged = false;

        // Check if root transform moved
        if (t.position != _lastPos || t.rotation != _lastRot || t.lossyScale != _lastScale)
        {
            _transformDirty = true;
            _lastPos = t.position;
            _lastRot = t.rotation;
            _lastScale = t.lossyScale;
        }

        if (_transformDirty)
        {
            RefreshWorldTransforms();
            _spatialHashDirty = true;
            _transformDirty = false;
            damageVersion++; // triggers buffer rebuild
            BrickRenderSystem.NotifyStructureChanged();
            BrickConnectionSystem.NotifyStructureChanged();
        }
    }

    // ─── Transform computation ───

    void RefreshWorldTransforms()
    {
        if (model == null || !model.HasGpuData || worldTransforms == null) return;

        var root = transform.localToWorldMatrix;
        var bricks = model.sortedBricks;

        for (int i = 0; i < bricks.Length; i++)
            worldTransforms[i] = root * bricks[i].localTransform;
    }

    // ─── Spatial hash ───

    void BuildSpatialHash()
    {
        if (worldTransforms == null) return;

        if (_spatialHash == null)
            _spatialHash = new Dictionary<Vector3Int, List<int>>(worldTransforms.Length);
        else
            _spatialHash.Clear();

        for (int i = 0; i < worldTransforms.Length; i++)
        {
            if (aliveFlags[i] == 0) continue;
            var cell = WorldToCell(worldTransforms[i].GetColumn(3));
            if (!_spatialHash.TryGetValue(cell, out var list))
            {
                list = new List<int>(8);
                _spatialHash[cell] = list;
            }
            list.Add(i);
        }

        _spatialHashDirty = false;
    }

    static Vector3Int WorldToCell(Vector3 pos) => new Vector3Int(
        Mathf.FloorToInt(pos.x / SpatialCellSize),
        Mathf.FloorToInt(pos.y / SpatialCellSize),
        Mathf.FloorToInt(pos.z / SpatialCellSize)
    );

    // ─── Public queries ───

    /// <summary>Find all alive brick indices within radius of a world-space hit point.</summary>
    public void FindBricksInRadius(Vector3 worldPoint, float radius, List<int> results)
    {
        EnsureInitialized();

        if (_spatialHash == null || _spatialHashDirty)
            BuildSpatialHash();

        if (_spatialHash == null) return;
        results.Clear();

        float radiusSq = radius * radius;
        int cellRadius = Mathf.CeilToInt(radius / SpatialCellSize);
        var centerCell = WorldToCell(worldPoint);

        for (int x = -cellRadius; x <= cellRadius; x++)
        for (int y = -cellRadius; y <= cellRadius; y++)
        for (int z = -cellRadius; z <= cellRadius; z++)
        {
            var cell = centerCell + new Vector3Int(x, y, z);
            if (!_spatialHash.TryGetValue(cell, out var list)) continue;

            for (int li = 0; li < list.Count; li++)
            {
                int idx = list[li];
                if (aliveFlags[idx] == 0) continue;

                Vector3 brickPos = worldTransforms[idx].GetColumn(3);
                if ((brickPos - worldPoint).sqrMagnitude <= radiusSq)
                    results.Add(idx);
            }
        }
    }

    /// <summary>Kill specific bricks and check structural connectivity from supports.</summary>
    public List<List<int>> ApplyDamage(List<int> hitBrickIndices)
    {
        EnsureInitialized();
        EnsureSupportData();

        // Mark hit bricks as dead
        for (int i = 0; i < hitBrickIndices.Count; i++)
        {
            int idx = hitBrickIndices[i];
            if (idx >= 0 && idx < aliveFlags.Length)
                aliveFlags[idx] = 0;
        }

        // Structural check: anything disconnected from foundation falls
        var detachedGroups = FloodFillFromSupports();

        // Rebuild spatial hash after damage
        _spatialHashDirty = true;
        damageVersion++;
        BrickRenderSystem.NotifyStructureChanged();
        BrickRenderSystem.ForceImmediateRefresh();
        BrickConnectionSystem.NotifyStructureChanged();

        return detachedGroups;
    }

    // ─── Teardown-style structural destruction ───

    /// <summary>
    /// Teardown-style damage. Destroys a crater of bricks at the hit point, then checks
    /// structural connectivity from foundation (support) bricks. Any alive bricks no longer
    /// connected to the foundation become detached groups that fall as physics chunks.
    /// </summary>
    /// <param name="hitBrickIndex">The specific brick that was struck.</param>
    /// <param name="impactForce">Normalized force (0-1 range). Controls crater size.</param>
    /// <param name="shatteredBricks">Output: brick indices destroyed by the impact crater.</param>
    /// <returns>List of detached groups (brick indices still alive, ready to become chunks).</returns>
    public List<List<int>> ApplyForceDamage(int hitBrickIndex, float impactForce,
        List<int> shatteredBricks)
    {
        EnsureInitialized();
        EnsureSupportData();

        shatteredBricks.Clear();

        int count = model.sortedBricks.Length;

        // ── Step 1: Destroy the crater ──
        // Kill the hit brick
        if (hitBrickIndex >= 0 && hitBrickIndex < count && aliveFlags[hitBrickIndex] == 1)
        {
            aliveFlags[hitBrickIndex] = 0;
            shatteredBricks.Add(hitBrickIndex);
        }

        // Kill nearby bricks within a force-scaled radius (crater)
        // Low force (~0.3) = just the hit brick + immediate neighbors
        // High force (~1.0) = a fist-sized hole in the wall
        float craterRadius = 0.5f + impactForce * 1.5f; // 0.5 to 2.0 world units
        {
            Vector3 hitPos = worldTransforms[hitBrickIndex].GetColumn(3);
            FindBricksInRadius(hitPos, craterRadius, _craterHits);
            for (int i = 0; i < _craterHits.Count; i++)
            {
                int idx = _craterHits[i];
                if (aliveFlags[idx] == 1)
                {
                    aliveFlags[idx] = 0;
                    shatteredBricks.Add(idx);
                }
            }
        }

        Debug.Log($"[BrickStructure] Crater: {shatteredBricks.Count} bricks destroyed " +
                  $"(force={impactForce:F2}, radius={craterRadius:F2})");

        // ── Step 2: Structural connectivity check ──
        // Flood-fill from all support (foundation) bricks through the adjacency graph.
        // Any alive bricks NOT reachable from foundation → detached → fall.
        var detached = FloodFillFromSupports();

        _spatialHashDirty = true;
        damageVersion++;
        BrickRenderSystem.NotifyStructureChanged();
        BrickRenderSystem.ForceImmediateRefresh();
        BrickConnectionSystem.NotifyStructureChanged();
        return detached;
    }

    readonly List<int> _craterHits = new(32);

    /// <summary>
    /// Flood-fill from ALL support (foundation) bricks through the adjacency graph.
    /// Returns groups of alive bricks not connected to any support brick.
    /// This is the core Teardown-style structural check: connected to ground = stays, disconnected = falls.
    /// </summary>
    List<List<int>> FloodFillFromSupports()
    {
        EnsureSupportData();

        if (model.adjacencyList == null || model.adjacencyList.Length == 0)
            return new List<List<int>>();

        int count = model.sortedBricks.Length;
        var visited = new bool[count];
        var queue = new Queue<int>(count);

        // Seed BFS from ALL alive support bricks (the foundation layer)
        if (supportBricks != null)
        {
            for (int s = 0; s < supportBricks.Length; s++)
            {
                int si = supportBricks[s];
                if (si >= 0 && si < count && aliveFlags[si] == 1 && !visited[si])
                {
                    visited[si] = true;
                    queue.Enqueue(si);
                }
            }
        }

        // Fallback: if no support bricks are alive, use the old single anchor
        if (queue.Count == 0 && anchorBrickIndex >= 0 && anchorBrickIndex < count
            && aliveFlags[anchorBrickIndex] == 1)
        {
            visited[anchorBrickIndex] = true;
            queue.Enqueue(anchorBrickIndex);
        }

        // BFS through adjacency — everything we reach is "supported"
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            var brick = model.sortedBricks[current];

            for (int n = 0; n < brick.neighborCount; n++)
            {
                int neighbor = model.adjacencyList[brick.connectivityOffset + n];
                if (neighbor < 0 || neighbor >= count) continue;
                if (visited[neighbor] || aliveFlags[neighbor] == 0) continue;

                visited[neighbor] = true;
                queue.Enqueue(neighbor);
            }
        }

        // Collect unsupported alive bricks into connected groups (islands)
        var detachedGroups = new List<List<int>>();
        for (int i = 0; i < count; i++)
        {
            if (aliveFlags[i] == 0 || visited[i]) continue;

            // This brick is alive but disconnected from foundation — flood-fill its island
            var island = new List<int>();
            var islandQueue = new Queue<int>();
            islandQueue.Enqueue(i);
            visited[i] = true;

            while (islandQueue.Count > 0)
            {
                int current = islandQueue.Dequeue();
                island.Add(current);

                var brick = model.sortedBricks[current];
                for (int n = 0; n < brick.neighborCount; n++)
                {
                    int neighbor = model.adjacencyList[brick.connectivityOffset + n];
                    if (neighbor < 0 || neighbor >= count) continue;
                    if (visited[neighbor] || aliveFlags[neighbor] == 0) continue;

                    visited[neighbor] = true;
                    islandQueue.Enqueue(neighbor);
                }
            }

            if (island.Count > 0)
                detachedGroups.Add(island);
        }

        if (detachedGroups.Count > 0)
            Debug.Log($"[BrickStructure] Structural check: {detachedGroups.Count} detached groups");

        return detachedGroups;
    }

    void EnsureSupportData()
    {
        if (!_supportDataDirty && supportBricks != null && anchorBrickIndex >= 0)
            return;

        if (anchorBrickIndex < 0)
            anchorBrickIndex = FindLowestBrick();

        ComputeSupportBricks();
        _supportDataDirty = false;
    }

    /// <summary>
    /// Compute the "foundation" set — all bricks whose local Y is within foundationHeight
    /// of the lowest brick. These are the structural supports (ground contact points).
    /// </summary>
    void ComputeSupportBricks()
    {
        if (model == null || !model.HasGpuData) return;

        var bricks = model.sortedBricks;
        float lowestY = float.MaxValue;
        for (int i = 0; i < bricks.Length; i++)
        {
            float y = bricks[i].localTransform.GetColumn(3).y;
            if (y < lowestY) lowestY = y;
        }

        var supports = new List<int>();
        float ceiling = lowestY + foundationHeight;
        for (int i = 0; i < bricks.Length; i++)
        {
            float y = bricks[i].localTransform.GetColumn(3).y;
            if (y <= ceiling)
                supports.Add(i);
        }

        supportBricks = supports.ToArray();
        Debug.Log($"[BrickStructure] Foundation: {supportBricks.Length} support bricks " +
                  $"(lowest Y={lowestY:F2}, ceiling={ceiling:F2})");
    }

    int FindLowestBrick()
    {
        if (model == null || !model.HasGpuData) return 0;
        var bricks = model.sortedBricks;
        int best = 0;
        float bestY = float.MaxValue;
        for (int i = 0; i < bricks.Length; i++)
        {
            float y = bricks[i].localTransform.m13; // column 3, row 1 = Y translation
            if (y < bestY)
            {
                bestY = y;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Count of alive bricks.</summary>
    public int AliveCount
    {
        get
        {
            if (aliveFlags == null) return 0;
            int c = 0;
            for (int i = 0; i < aliveFlags.Length; i++)
                if (aliveFlags[i] != 0) c++;
            return c;
        }
    }
}
