using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

/// <summary>
/// Global singleton that manages LEGO-style connections between bricks,
/// both within a single model (via LDrawModelAsset adjacency) and across
/// different BrickStructure models placed near each other.
///
/// Cross-model connections form automatically when bricks from separate
/// structures are close enough (e.g. placing a tree model on a hill model).
///
/// Rebuilds are performed incrementally via coroutine to avoid blocking
/// Fusion's network tick loop during heavy scene loads.
/// </summary>
public class BrickConnectionSystem : MonoBehaviour
{
    public static BrickConnectionSystem Instance { get; private set; }

    [Tooltip("Max distance between brick centres to form a cross-model connection.")]
    public float connectionDistance = 1.5f;

    [Tooltip("Max milliseconds to spend on connection rebuilds per frame. Lower keeps Fusion heartbeat alive.")]
    [Range(1f, 30f)] public float connectionBudgetMs = 5f;

    [Tooltip("How often to scan all structures for changes in Play Mode.")]
    [Min(0.02f)] public float changeScanInterval = 0.1f;

    [Tooltip("Delay cross-connection rebuilds briefly after scene start to reduce load hitches.")]
    [Min(0f)] public float startupRebuildDelay = 0.6f;

    [Tooltip("If enabled, nearby structures are prioritized during connection warmup rebuild.")]
    public bool enableWarmupPriority = true;
    [Tooltip("Optional warmup target. Defaults to Camera.main when unset.")]
    public Transform warmupTarget;

    // ── Registered structures ──
    readonly List<BrickStructure> _structures = new();

    // ── Cross-model connections (bidirectional) ──
    // Key = (structure, brickIndex), Value = list of connected bricks in other structures
    readonly Dictionary<(BrickStructure, int), List<(BrickStructure, int)>> _crossConnections = new();

    // ── Global spatial grid for cross-model proximity queries ──
    const float CellSize = 1.0f;
    readonly Dictionary<Vector3Int, List<(BrickStructure s, int i)>> _globalGrid = new();

    // ── Incremental rebuild state ──
    bool _dirty;
    Coroutine _rebuildCoroutine;
    float _nextScanTime;
    float _startupReadyTime;
    readonly List<BrickStructure> _rebuildOrder = new(256);

    // ─── Lifecycle ───

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        if (Application.isPlaying)
            _startupReadyTime = Time.time + Mathf.Max(0f, startupRebuildDelay);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void LateUpdate()
    {
        if (Application.isPlaying && Time.time < _startupReadyTime)
            return;

        if (Application.isPlaying && !_dirty && _rebuildCoroutine == null && Time.time < _nextScanTime)
            return;

        if (Application.isPlaying)
            _nextScanTime = Time.time + Mathf.Max(0.02f, changeScanInterval);

        // Cull nulls lazily when we are about to rebuild.
        if (_dirty)
        {
            _dirty = false;
            for (int i = _structures.Count - 1; i >= 0; i--)
            {
                if (_structures[i] != null) continue;
                _structures.RemoveAt(i);
            }

            if (_rebuildCoroutine != null)
                StopCoroutine(_rebuildCoroutine);

            if (Application.isPlaying)
                _rebuildCoroutine = StartCoroutine(RebuildIncremental());
            else
            {
                RebuildGlobalGrid();
                RebuildCrossConnections();
            }
        }
    }

    // ─── Incremental rebuild (Play mode) ───

    IEnumerator RebuildIncremental()
    {
        BuildPrioritizedStructureOrder();

        // Phase 1: Rebuild global grid incrementally.
        yield return RebuildGlobalGridIncremental();

        // Phase 2: Rebuild cross connections incrementally (time-budgeted)
        _crossConnections.Clear();
        float distSq = connectionDistance * connectionDistance;
        var sw2 = Stopwatch.StartNew();
        double budgetTicks2 = (double)connectionBudgetMs / 1000.0 * Stopwatch.Frequency;

        for (int si = 0; si < _rebuildOrder.Count; si++)
        {
            var structure = _rebuildOrder[si];
            if (structure == null || structure.worldTransforms == null) continue;

            for (int i = 0; i < structure.worldTransforms.Length; i++)
            {
                if (structure.aliveFlags[i] == 0) continue;

                Vector3 posA = structure.worldTransforms[i].GetColumn(3);
                var center = ToCell(posA);

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    var cell = center + new Vector3Int(dx, dy, dz);
                    if (!_globalGrid.TryGetValue(cell, out var bucket)) continue;

                    foreach (var (otherStructure, otherIdx) in bucket)
                    {
                        if (otherStructure == structure) continue;
                        Vector3 posB = otherStructure.worldTransforms[otherIdx].GetColumn(3);
                        if ((posA - posB).sqrMagnitude <= distSq)
                            Link(structure, i, otherStructure, otherIdx);
                    }
                }

                if (sw2.ElapsedTicks >= budgetTicks2)
                {
                    sw2.Restart();
                    yield return null;
                }
            }
        }

        _rebuildCoroutine = null;
    }

    IEnumerator RebuildGlobalGridIncremental()
    {
        _globalGrid.Clear();
        var sw = Stopwatch.StartNew();
        double budgetTicks = (double)connectionBudgetMs / 1000.0 * Stopwatch.Frequency;

        for (int si = 0; si < _rebuildOrder.Count; si++)
        {
            var structure = _rebuildOrder[si];
            if (structure == null) continue;

            structure.EnsureInitialized();
            if (structure.worldTransforms == null || structure.aliveFlags == null) continue;

            for (int i = 0; i < structure.worldTransforms.Length; i++)
            {
                if (structure.aliveFlags[i] == 0) continue;

                Vector3 pos = structure.worldTransforms[i].GetColumn(3);
                var cell = ToCell(pos);

                if (!_globalGrid.TryGetValue(cell, out var list))
                {
                    list = new List<(BrickStructure, int)>(4);
                    _globalGrid[cell] = list;
                }
                list.Add((structure, i));

                if (sw.ElapsedTicks >= budgetTicks)
                {
                    sw.Restart();
                    yield return null;
                }
            }
        }
    }

    // ─── Registration ───

    public void Register(BrickStructure structure)
    {
        if (_structures.Contains(structure)) return;

        _structures.Add(structure);
        _dirty = true;
    }

    public void Unregister(BrickStructure structure)
    {
        _structures.Remove(structure);
        // Don't purge cross-connections inline — the full rebuild (triggered by _dirty)
        // clears and rebuilds _crossConnections from scratch, so a synchronous O(n×m)
        // dictionary scan here would just be thrown away. Clear eagerly instead.
        _crossConnections.Clear();
        _dirty = true;
    }

    public static void NotifyStructureChanged()
    {
        if (Instance == null) return;
        Instance._dirty = true;
    }

    // ─── Grid / Connection rebuild ───

    void RebuildGlobalGrid()
    {
        BuildPrioritizedStructureOrder();
        _globalGrid.Clear();

        foreach (var structure in _rebuildOrder)
        {
            if (structure == null || structure.worldTransforms == null) continue;

            for (int i = 0; i < structure.worldTransforms.Length; i++)
            {
                if (structure.aliveFlags[i] == 0) continue;

                Vector3 pos = structure.worldTransforms[i].GetColumn(3);
                var cell = ToCell(pos);

                if (!_globalGrid.TryGetValue(cell, out var list))
                {
                    list = new List<(BrickStructure, int)>(4);
                    _globalGrid[cell] = list;
                }
                list.Add((structure, i));
            }
        }
    }

    void RebuildCrossConnections()
    {
        _crossConnections.Clear();
        float distSq = connectionDistance * connectionDistance;

        BuildPrioritizedStructureOrder();

        foreach (var structure in _rebuildOrder)
        {
            if (structure == null || structure.worldTransforms == null) continue;

            for (int i = 0; i < structure.worldTransforms.Length; i++)
            {
                if (structure.aliveFlags[i] == 0) continue;

                Vector3 posA = structure.worldTransforms[i].GetColumn(3);
                var center = ToCell(posA);

                // Check 3x3x3 neighbourhood
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    var cell = center + new Vector3Int(dx, dy, dz);
                    if (!_globalGrid.TryGetValue(cell, out var bucket)) continue;

                    foreach (var (otherStructure, otherIdx) in bucket)
                    {
                        if (otherStructure == structure) continue; // same model → internal adjacency

                        Vector3 posB = otherStructure.worldTransforms[otherIdx].GetColumn(3);
                        if ((posA - posB).sqrMagnitude <= distSq)
                            Link(structure, i, otherStructure, otherIdx);
                    }
                }
            }
        }
    }

    void Link(BrickStructure sA, int iA, BrickStructure sB, int iB)
    {
        AddHalf(sA, iA, sB, iB);
        AddHalf(sB, iB, sA, iA);
    }

    void AddHalf(BrickStructure sFrom, int iFrom, BrickStructure sTo, int iTo)
    {
        var key = (sFrom, iFrom);

        if (!_crossConnections.TryGetValue(key, out var list))
        {
            list = new List<(BrickStructure, int)>(4);
            _crossConnections[key] = list;
        }

        // Skip duplicate
        for (int i = 0; i < list.Count; i++)
            if (list[i].Item1 == sTo && list[i].Item2 == iTo) return;

        list.Add((sTo, iTo));
    }

    void PurgeConnectionsFor(BrickStructure structure)
    {
        var keysToRemove = new List<(BrickStructure, int)>();
        foreach (var kv in _crossConnections)
        {
            if (kv.Key.Item1 == structure)
                keysToRemove.Add(kv.Key);
        }
        foreach (var key in keysToRemove)
            _crossConnections.Remove(key);

        // Remove back-references from other structures
        foreach (var kv in _crossConnections)
            kv.Value.RemoveAll(x => x.Item1 == structure);
    }

    // ─── Public API ───

    /// <summary>Get cross-model connections for a brick. Null if none.</summary>
    public List<(BrickStructure structure, int brickIndex)> GetCrossConnections(BrickStructure structure, int brickIndex)
    {
        return _crossConnections.TryGetValue((structure, brickIndex), out var list) ? list : null;
    }

    /// <summary>True when a brick has at least one cross-model connection.</summary>
    public bool HasCrossConnections(BrickStructure structure, int brickIndex)
    {
        return _crossConnections.ContainsKey((structure, brickIndex));
    }

    /// <summary>All registered structures (read-only).</summary>
    public IReadOnlyList<BrickStructure> RegisteredStructures => _structures;

    /// <summary>Total cross-model connection count (each pair counted once).</summary>
    public int CrossConnectionCount
    {
        get
        {
            int total = 0;
            foreach (var kv in _crossConnections) total += kv.Value.Count;
            return total / 2; // bidirectional
        }
    }

    Vector3 GetWarmupTargetPosition()
    {
        if (warmupTarget != null)
            return warmupTarget.position;

        var cam = Camera.main;
        return cam != null ? cam.transform.position : Vector3.zero;
    }

    void BuildPrioritizedStructureOrder()
    {
        _rebuildOrder.Clear();
        for (int i = 0; i < _structures.Count; i++)
        {
            var s = _structures[i];
            if (s == null || !s.isActiveAndEnabled) continue;
            _rebuildOrder.Add(s);
        }

        if (!enableWarmupPriority || !Application.isPlaying || _rebuildOrder.Count <= 1)
            return;

        Vector3 focus = GetWarmupTargetPosition();
        _rebuildOrder.Sort((a, b) =>
        {
            float da = (a.transform.position - focus).sqrMagnitude;
            float db = (b.transform.position - focus).sqrMagnitude;
            return da.CompareTo(db);
        });
    }

    // Kept as a no-op stub — budget is now purely time-based via connectionBudgetMs.

    // ─── Helpers ───

    static Vector3Int ToCell(Vector3 pos) => new Vector3Int(
        Mathf.FloorToInt(pos.x / CellSize),
        Mathf.FloorToInt(pos.y / CellSize),
        Mathf.FloorToInt(pos.z / CellSize)
    );
}
