using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Global singleton that renders ALL BrickStructures via GPU-indirect instancing.
/// One draw call per unique (Mesh, colorCode) across all structures.
/// Replaces per-object LDrawModelRenderer.
/// </summary>
[ExecuteAlways]
[DefaultExecutionOrder(100)] // run after BrickStructure.LateUpdate
public class BrickRenderSystem : MonoBehaviour
{
    // ─── Inspector Config ───
    [Header("Rendering")]
    public Material indirectMaterial;       // Uses Bricks/LDrawURP_Indirect shader
    public Texture2D paletteTexture;
    public LDrawPartRegistry partRegistry;

    [Header("Performance")]
    [Tooltip("Maximum brick instances across all structures. Governs GPU buffer size.")]
    public int maxGlobalBricks = 200000;
    [Tooltip("Max brick records processed per frame while rebuilding batches in Play Mode. Higher = faster rebuild, lower = less hitching.")]
    [Min(1000)] public int rebuildBricksPerFrame = 8000;
    [Tooltip("If enabled, structures nearer to the camera/target are rebuilt first during warmup.")]
    public bool enableWarmupPriority = true;
    [Tooltip("Optional warmup target. Defaults to Camera.main when unset.")]
    public Transform warmupTarget;
    [Tooltip("Adaptive budget keeps frame time stable by adjusting rebuild work each frame.")]
    public bool adaptiveRebuildBudget = true;
    [Tooltip("Desired frame time during warmup (ms). Lower value = more conservative rebuild workload.")]
    [Range(8f, 33f)] public float targetWarmupFrameMs = 14f;
    [Min(500)] public int minRebuildBricksPerFrame = 2000;
    [Min(1000)] public int maxRebuildBricksPerFrame = 32000;

    [Header("Runtime Damage Sync")]
    [Tooltip("When true, damage changes trigger an immediate full batch rebuild so broken bricks disappear right away.")]
    public bool rebuildImmediatelyOnDamage = true;

    [Header("Shadows")]
    public ShadowCastingMode shadowMode = ShadowCastingMode.On;
    public bool receiveShadows = true;

    [Header("Underwater Culling")]
    [Tooltip("Skip rendering bricks whose world Y is below this value. Free GPU savings when opaque water covers them.")]
    public bool enableUnderwaterCull = false;
    public float underwaterCullY = 0f;

#if UNITY_EDITOR
    [Header("Editor")]
    [Tooltip("When enabled, the render system refreshes BrickStructure discovery in Edit Mode.")]
    public bool refreshInEditMode = true;
    [Tooltip("How often to scan the scene for BrickStructure changes while in Edit Mode.")]
    [Min(0.05f)] public float editModeRefreshInterval = 0.35f;
    [Tooltip("Continuously repaints SceneView in Edit Mode. Disable to reduce editor overhead.")]
    public bool continuousSceneViewRepaint = false;
#endif

    // ─── Singleton ───
    static BrickRenderSystem _instance;
    public static BrickRenderSystem Instance => _instance;

    /// <summary>
    /// True when the runtime incremental rebuild coroutine is not currently running.
    /// </summary>
    public bool IsRebuilding => _rebuildCoroutine != null;

    /// <summary>
    /// True when rendering is visually settled for gameplay/loading transitions.
    /// A strict revision equality can remain false due to benign late revision bumps,
    /// even when batches are already rendered and visible.
    /// </summary>
    public bool IsRuntimeReady => _rebuildCoroutine == null;
    public int ActiveBatchCount => _drawBatches.Count;
    public int RegisteredStructureCount => _structures.Count;

    static readonly List<BrickStructure> _structures = new();
    static int _globalRevision;

    public static void Register(BrickStructure s)
    {
        if (_structures.Contains(s)) return;
        _structures.Add(s);
        unchecked { _globalRevision++; }
    }

    public static void Unregister(BrickStructure s)
    {
        if (!_structures.Remove(s)) return;
        unchecked { _globalRevision++; }
    }

    public static void NotifyStructureChanged()
    {
        unchecked { _globalRevision++; }
    }

    // ─── GPU Data ───

    /// <summary>Must match the HLSL BrickData struct exactly (80 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct GpuBrickData
    {
        public Matrix4x4 objectToWorld; // 64 bytes
        public float colorCode;         // 4 bytes
        public Vector3 _pad;            // 12 bytes padding to align to 16-byte boundary
    }

    // Per batch-group key: (partId, colorCode) → draw data
    struct DrawBatch
    {
        public Mesh mesh;
        public int colorCode;
        public int startOffset;    // start index in the global GpuBrickData buffer
        public int instanceCount;  // how many instances
    }

    GraphicsBuffer _brickBuffer;
    GraphicsBuffer _argsBuffer;  // reusable indirect args buffer
    GpuBrickData[] _cpuBuffer;
    readonly List<DrawBatch> _drawBatches = new(256);
    readonly List<GraphicsBuffer> _drawArgsBuffers = new(256);

    int _lastPlayRevision = int.MinValue;
    int _lastEditorHash = int.MinValue;
    bool _lastUnderwaterCullEnabled;
    float _lastUnderwaterCullY;
    MaterialPropertyBlock _mpb;
#if UNITY_EDITOR
    double _nextEditorRefreshTime;
#endif

    // Indirect args: [indexCount, instanceCount, startIndex, baseVertex, startInstance]
    readonly uint[] _argsData = new uint[5];
    Coroutine _rebuildCoroutine;
    readonly List<BrickStructure> _rebuildOrder = new(512);
    float _adaptiveBudgetScale = 1f;
    bool _forceImmediateRebuild;

    /// <summary>
    /// Requests an immediate runtime rebuild on the next LateUpdate.
    /// Use this for critical visual sync points (e.g., destruction).
    /// </summary>
    public static void ForceImmediateRefresh()
    {
        if (_instance == null) return;
        _instance._forceImmediateRebuild = true;
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            if (Application.isPlaying)
                Destroy(this);
            else
                DestroyImmediate(this);
            return;
        }
        _instance = this;
    }

    void OnEnable()
    {
        // Re-claim singleton after domain reload (Awake may not re-fire)
        if (_instance == null) _instance = this;
        _mpb = new MaterialPropertyBlock();
        _lastPlayRevision = int.MinValue;
        _lastEditorHash = int.MinValue;
        ReallocBuffers();
    }

    void OnValidate()
    {
        maxGlobalBricks = Mathf.Max(1, maxGlobalBricks);
        if (_instance == null) _instance = this;
        if (_mpb == null) _mpb = new MaterialPropertyBlock();
        _lastPlayRevision = int.MinValue;
        _lastEditorHash = int.MinValue;

        if (_cpuBuffer == null || _cpuBuffer.Length != maxGlobalBricks || _brickBuffer == null)
            ReallocBuffers();
    }

    void OnDisable()
    {
        if (_rebuildCoroutine != null)
        {
            StopCoroutine(_rebuildCoroutine);
            _rebuildCoroutine = null;
        }

        _brickBuffer?.Release();
        _brickBuffer = null;
        _argsBuffer?.Release();
        _argsBuffer = null;
        ReleaseDrawArgsBuffers();
    }

    void OnDestroy()
    {
        if (_instance == this) _instance = null;

        if (_rebuildCoroutine != null)
        {
            StopCoroutine(_rebuildCoroutine);
            _rebuildCoroutine = null;
        }

        _brickBuffer?.Release();
        _brickBuffer = null;
        _argsBuffer?.Release();
        _argsBuffer = null;
        ReleaseDrawArgsBuffers();
    }

    void ReleaseDrawArgsBuffers()
    {
        for (int i = 0; i < _drawArgsBuffers.Count; i++)
            _drawArgsBuffers[i]?.Release();
        _drawArgsBuffers.Clear();
    }

    void ReallocBuffers()
    {
        _brickBuffer?.Release();
        _brickBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxGlobalBricks,
            Marshal.SizeOf<GpuBrickData>());
        _cpuBuffer = new GpuBrickData[maxGlobalBricks];

        _argsBuffer?.Release();
        _argsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, 5 * sizeof(uint));
    }

    // ─── Main render loop ───

    void LateUpdate()
    {
        if (indirectMaterial == null || paletteTexture == null || partRegistry == null) return;

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            if (refreshInEditMode)
            {
                double now = EditorApplication.timeSinceStartup;
                if (now >= _nextEditorRefreshTime)
                {
                    RefreshEditorStructures();
                    _nextEditorRefreshTime = now + Math.Max(0.05, editModeRefreshInterval);

                    if (continuousSceneViewRepaint)
                        SceneView.RepaintAll();
                }
            }
        }
#endif

        if (_structures.Count == 0) return;

        bool cullChanged = _lastUnderwaterCullEnabled != enableUnderwaterCull
                        || !Mathf.Approximately(_lastUnderwaterCullY, underwaterCullY);

        if (Application.isPlaying)
        {
            if (_forceImmediateRebuild && rebuildImmediatelyOnDamage)
            {
                if (_rebuildCoroutine != null)
                {
                    StopCoroutine(_rebuildCoroutine);
                    _rebuildCoroutine = null;
                }

                RebuildBatches();
                _lastPlayRevision = _globalRevision;
                _lastUnderwaterCullEnabled = enableUnderwaterCull;
                _lastUnderwaterCullY = underwaterCullY;
                _forceImmediateRebuild = false;
            }

            bool needsRebuild = (_lastPlayRevision != _globalRevision) || cullChanged;
            if (needsRebuild && _rebuildCoroutine == null)
            {
                int revisionSnapshot = _globalRevision;
                bool cullSnapshot = enableUnderwaterCull;
                float cullYSnapshot = underwaterCullY;
                _rebuildCoroutine = StartCoroutine(RebuildBatchesIncremental(revisionSnapshot, cullSnapshot, cullYSnapshot));
            }
        }
        else
        {
            int currentHash = ComputeDamageHash();
            bool needsRebuild = (_lastEditorHash != currentHash) || cullChanged;
            _lastEditorHash = currentHash;

            if (needsRebuild)
            {
                _lastPlayRevision = _globalRevision;
                _lastUnderwaterCullEnabled = enableUnderwaterCull;
                _lastUnderwaterCullY = underwaterCullY;
                RebuildBatches();

#if UNITY_EDITOR
                SceneView.RepaintAll();
#endif
            }
        }

        SubmitDrawCalls();
    }

    IEnumerator RebuildBatchesIncremental(int revisionSnapshot, bool cullEnabledSnapshot, float cullYSnapshot)
    {
        int budget = GetCurrentRebuildBudget();
        int processed = 0;

        BuildPrioritizedStructureOrder();

        // Build into temporary batch list, then swap at the end.
        var newDrawBatches = new List<DrawBatch>(256);

        // Pass 1: count alive bricks per (partId, colorCode).
        var groupCounts = new Dictionary<(int partId, int colorCode), int>(256);

        for (int si = 0; si < _rebuildOrder.Count; si++)
        {
            var s = _rebuildOrder[si];
            if (s == null || s.model == null || !s.model.HasGpuData) continue;

            var groups = s.model.batchGroups;
            var bricks = s.model.sortedBricks;
            if (groups == null || bricks == null || bricks.Length == 0) continue;

            var alive = s.aliveFlags;
            bool hasAlive = alive != null && alive.Length == bricks.Length;

            var worldXf = s.worldTransforms;
            bool hasWorld = worldXf != null && worldXf.Length == bricks.Length;
            var root = hasWorld ? Matrix4x4.identity : s.transform.localToWorldMatrix;

            for (int gi = 0; gi < groups.Length; gi++)
            {
                var g = groups[gi];
                int aliveInGroup = 0;

                if (!cullEnabledSnapshot)
                {
                    for (int bi = g.startIndex; bi < g.startIndex + g.count; bi++)
                    {
                        if (hasAlive && alive[bi] == 0) continue;
                        aliveInGroup++;

                        if (++processed >= budget)
                        {
                            processed = 0;
                            yield return null;
                            budget = GetCurrentRebuildBudget();
                        }
                    }
                }
                else
                {
                    for (int bi = g.startIndex; bi < g.startIndex + g.count; bi++)
                    {
                        if (hasAlive && alive[bi] == 0) continue;

                        float worldY = hasWorld
                            ? worldXf[bi].m13
                            : (root * bricks[bi].localTransform).m13;
                        if (worldY < cullYSnapshot) continue;

                        aliveInGroup++;

                        if (++processed >= budget)
                        {
                            processed = 0;
                            yield return null;
                            budget = GetCurrentRebuildBudget();
                        }
                    }
                }

                if (aliveInGroup > 0)
                {
                    var key = (g.partId, g.colorCode);
                    groupCounts.TryGetValue(key, out int existing);
                    groupCounts[key] = existing + aliveInGroup;
                }
            }
        }

        // Pass 2: assign offsets and build batches.
        int totalOffset = 0;
        var groupOffsets = new Dictionary<(int, int), int>(groupCounts.Count);

        foreach (var kv in groupCounts)
        {
            if (totalOffset + kv.Value > maxGlobalBricks)
            {
                Debug.LogWarning($"BrickRenderSystem: exceeded maxGlobalBricks ({maxGlobalBricks}). Increase limit.");
                break;
            }

            var mesh = partRegistry.GetMeshById(kv.Key.partId);
            if (mesh == null)
            {
                Debug.LogWarning($"BrickRenderSystem: No mesh for partId={kv.Key.partId} (colorCode={kv.Key.colorCode}), {kv.Value} instances skipped. Try 'Tools > Bricks > Rebake GPU Data on Selected Model'.");
                continue;
            }

            groupOffsets[kv.Key] = totalOffset;
            newDrawBatches.Add(new DrawBatch
            {
                mesh = mesh,
                colorCode = kv.Key.colorCode,
                startOffset = totalOffset,
                instanceCount = kv.Value,
            });

            totalOffset += kv.Value;
        }

        if (totalOffset > maxGlobalBricks)
        {
            maxGlobalBricks = Mathf.NextPowerOfTwo(totalOffset);
            ReallocBuffers();
            _lastPlayRevision = int.MinValue;
            _lastEditorHash = int.MinValue;
            _rebuildCoroutine = null;
            yield break;
        }

        // Pass 3: fill CPU buffer.
        var writeCursors = new Dictionary<(int, int), int>(groupOffsets.Count);
        foreach (var kv in groupOffsets)
            writeCursors[kv.Key] = kv.Value;

        for (int si = 0; si < _rebuildOrder.Count; si++)
        {
            var s = _rebuildOrder[si];
            if (s == null || s.model == null || !s.model.HasGpuData) continue;

            var groups = s.model.batchGroups;
            var bricks = s.model.sortedBricks;
            if (groups == null || bricks == null || bricks.Length == 0) continue;

            var alive = s.aliveFlags;
            bool hasAlive = alive != null && alive.Length == bricks.Length;

            var worldXf = s.worldTransforms;
            bool hasWorld = worldXf != null && worldXf.Length == bricks.Length;
            var root = hasWorld ? Matrix4x4.identity : s.transform.localToWorldMatrix;

            for (int gi = 0; gi < groups.Length; gi++)
            {
                var g = groups[gi];
                var key = (g.partId, g.colorCode);

                if (!writeCursors.TryGetValue(key, out int cursor)) continue;

                if (!cullEnabledSnapshot)
                {
                    for (int bi = g.startIndex; bi < g.startIndex + g.count; bi++)
                    {
                        if (hasAlive && alive[bi] == 0) continue;
                        if (cursor >= _cpuBuffer.Length) break;

                        Matrix4x4 world = hasWorld ? worldXf[bi] : (root * bricks[bi].localTransform);

                        _cpuBuffer[cursor] = new GpuBrickData
                        {
                            objectToWorld = world,
                            colorCode = bricks[bi].colorCode,
                            _pad = Vector3.zero,
                        };
                        cursor++;

                        if (++processed >= budget)
                        {
                            processed = 0;
                            yield return null;
                            budget = GetCurrentRebuildBudget();
                        }
                    }
                }
                else
                {
                    for (int bi = g.startIndex; bi < g.startIndex + g.count; bi++)
                    {
                        if (hasAlive && alive[bi] == 0) continue;

                        Matrix4x4 world = hasWorld ? worldXf[bi] : (root * bricks[bi].localTransform);
                        if (world.m13 < cullYSnapshot) continue;

                        if (cursor >= _cpuBuffer.Length) break;

                        _cpuBuffer[cursor] = new GpuBrickData
                        {
                            objectToWorld = world,
                            colorCode = bricks[bi].colorCode,
                            _pad = Vector3.zero,
                        };
                        cursor++;

                        if (++processed >= budget)
                        {
                            processed = 0;
                            yield return null;
                            budget = GetCurrentRebuildBudget();
                        }
                    }
                }

                writeCursors[key] = cursor;
            }
        }

        if (totalOffset > 0)
            _brickBuffer.SetData(_cpuBuffer, 0, 0, totalOffset);

        _drawArgsBuffers.Capacity = Mathf.Max(_drawArgsBuffers.Capacity, newDrawBatches.Count);
        while (_drawArgsBuffers.Count < newDrawBatches.Count)
            _drawArgsBuffers.Add(new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, 5 * sizeof(uint)));
        while (_drawArgsBuffers.Count > newDrawBatches.Count)
        {
            int last = _drawArgsBuffers.Count - 1;
            _drawArgsBuffers[last]?.Release();
            _drawArgsBuffers.RemoveAt(last);
        }

        for (int i = 0; i < newDrawBatches.Count; i++)
        {
            var batch = newDrawBatches[i];
            _argsData[0] = batch.mesh.GetIndexCount(0);
            _argsData[1] = (uint)batch.instanceCount;
            _argsData[2] = batch.mesh.GetIndexStart(0);
            _argsData[3] = batch.mesh.GetBaseVertex(0);
            _argsData[4] = 0;
            _drawArgsBuffers[i].SetData(_argsData);

            if ((i & 63) == 63)
                yield return null;
        }

        _drawBatches.Clear();
        _drawBatches.AddRange(newDrawBatches);

        _lastPlayRevision = revisionSnapshot;
        _lastUnderwaterCullEnabled = cullEnabledSnapshot;
        _lastUnderwaterCullY = cullYSnapshot;
        _rebuildCoroutine = null;
    }

#if UNITY_EDITOR
    void RefreshEditorStructures()
    {
        _structures.Clear();
        var found = FindObjectsByType<BrickStructure>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
        {
            var s = found[i];
            if (s == null || !s.isActiveAndEnabled) continue;
            s.EnsureInitialized();
            if (s.model != null && s.model.HasGpuData)
                _structures.Add(s);
        }
    }
#endif

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

    int GetCurrentRebuildBudget()
    {
        int minBudget = Mathf.Max(500, minRebuildBricksPerFrame);
        int maxBudget = Mathf.Max(minBudget, maxRebuildBricksPerFrame);
        int baseBudget = Mathf.Clamp(rebuildBricksPerFrame, minBudget, maxBudget);

        if (!adaptiveRebuildBudget || !Application.isPlaying)
            return baseBudget;

        float frameMs = Time.unscaledDeltaTime * 1000f;
        float targetMs = Mathf.Max(8f, targetWarmupFrameMs);

        if (frameMs > targetMs)
            _adaptiveBudgetScale = Mathf.Max(0.35f, _adaptiveBudgetScale * 0.90f);
        else if (frameMs < targetMs * 0.75f)
            _adaptiveBudgetScale = Mathf.Min(2.5f, _adaptiveBudgetScale * 1.06f);

        int adaptiveBudget = Mathf.RoundToInt(baseBudget * _adaptiveBudgetScale);
        return Mathf.Clamp(adaptiveBudget, minBudget, maxBudget);
    }

    int ComputeDamageHash()
    {
        int hash = _structures.Count;
        for (int i = 0; i < _structures.Count; i++)
        {
            var s = _structures[i];
            if (s == null || s.model == null) continue;
            hash = hash * 31 + s.damageVersion;
            hash = hash * 31 + s.GetInstanceID();
        }
        return hash;
    }

    // ─── Batch rebuild (only when dirty) ───

    void RebuildBatches()
    {
        _drawBatches.Clear();

        BuildPrioritizedStructureOrder();

        // Gather: iterate all structures, compact alive bricks into GPU buffer grouped by (partId, colorCode)
        // Strategy: since each structure's sortedBricks are already grouped by (partId, colorCode),
        // we just need to merge across structures.

        // Pass 1: count total alive bricks per (partId, colorCode) across all structures
        var groupCounts = new Dictionary<(int partId, int colorCode), int>(256);

        for (int si = 0; si < _rebuildOrder.Count; si++)
        {
            var s = _rebuildOrder[si];
            if (s == null || s.model == null || !s.model.HasGpuData) continue;
            var groups = s.model.batchGroups;
            var bricks = s.model.sortedBricks;
            if (groups == null || bricks == null || bricks.Length == 0) continue;

            var alive = s.aliveFlags;
            bool hasAlive = alive != null && alive.Length == bricks.Length;

            var worldXf = s.worldTransforms;
            bool hasWorld = worldXf != null && worldXf.Length == bricks.Length;
            var root = hasWorld ? Matrix4x4.identity : s.transform.localToWorldMatrix;

            for (int gi = 0; gi < groups.Length; gi++)
            {
                var g = groups[gi];
                int aliveInGroup = 0;

                if (!enableUnderwaterCull)
                {
                    for (int bi = g.startIndex; bi < g.startIndex + g.count; bi++)
                    {
                        if (hasAlive && alive[bi] == 0) continue;
                        aliveInGroup++;
                    }
                }
                else
                {
                    for (int bi = g.startIndex; bi < g.startIndex + g.count; bi++)
                    {
                        if (hasAlive && alive[bi] == 0) continue;

                        float worldY = hasWorld
                            ? worldXf[bi].m13
                            : (root * bricks[bi].localTransform).m13;
                        if (worldY < underwaterCullY) continue;

                        aliveInGroup++;
                    }
                }

                if (aliveInGroup > 0)
                {
                    var key = (g.partId, g.colorCode);
                    groupCounts.TryGetValue(key, out int existing);
                    groupCounts[key] = existing + aliveInGroup;
                }
            }
        }

        // Pass 2: assign offsets and build draw batches
        int totalOffset = 0;
        var groupOffsets = new Dictionary<(int, int), int>(groupCounts.Count);

        foreach (var kv in groupCounts)
        {
            if (totalOffset + kv.Value > maxGlobalBricks)
            {
                Debug.LogWarning($"BrickRenderSystem: exceeded maxGlobalBricks ({maxGlobalBricks}). Increase limit.");
                break;
            }

            var mesh = partRegistry.GetMeshById(kv.Key.partId);
            if (mesh == null)
            {
                Debug.LogWarning($"BrickRenderSystem: No mesh for partId={kv.Key.partId} (colorCode={kv.Key.colorCode}), {kv.Value} instances skipped. Try 'Tools > Bricks > Rebake GPU Data on Selected Model'.");
                continue;
            }

            groupOffsets[kv.Key] = totalOffset;

            _drawBatches.Add(new DrawBatch
            {
                mesh = mesh,
                colorCode = kv.Key.colorCode,
                startOffset = totalOffset,
                instanceCount = kv.Value,
            });

            totalOffset += kv.Value;
        }

        // Ensure buffer is large enough
        if (totalOffset > maxGlobalBricks)
        {
            maxGlobalBricks = Mathf.NextPowerOfTwo(totalOffset);
            ReallocBuffers();
            // Re-run offset calculation would be needed, but for safety just rebuild next frame
            _lastPlayRevision = int.MinValue;
            _lastEditorHash = int.MinValue;
            return;
        }

        // Pass 3: fill CPU buffer with world transforms of alive bricks
        // Reset write cursors per group
        var writeCursors = new Dictionary<(int, int), int>(groupOffsets.Count);
        foreach (var kv in groupOffsets)
            writeCursors[kv.Key] = kv.Value;

        for (int si = 0; si < _rebuildOrder.Count; si++)
        {
            var s = _rebuildOrder[si];
            if (s == null || s.model == null || !s.model.HasGpuData) continue;

            var groups = s.model.batchGroups;
            var bricks = s.model.sortedBricks;
            if (groups == null || bricks == null || bricks.Length == 0) continue;

            var alive = s.aliveFlags;
            bool hasAlive = alive != null && alive.Length == bricks.Length;

            var worldXf = s.worldTransforms;
            bool hasWorld = worldXf != null && worldXf.Length == bricks.Length;
            var root = hasWorld ? Matrix4x4.identity : s.transform.localToWorldMatrix;

            for (int gi = 0; gi < groups.Length; gi++)
            {
                var g = groups[gi];
                var key = (g.partId, g.colorCode);

                if (!writeCursors.TryGetValue(key, out int cursor)) continue;

                if (!enableUnderwaterCull)
                {
                    for (int bi = g.startIndex; bi < g.startIndex + g.count; bi++)
                    {
                        if (hasAlive && alive[bi] == 0) continue;
                        if (cursor >= _cpuBuffer.Length) break;

                        Matrix4x4 world = hasWorld ? worldXf[bi] : (root * bricks[bi].localTransform);

                        _cpuBuffer[cursor] = new GpuBrickData
                        {
                            objectToWorld = world,
                            colorCode = bricks[bi].colorCode,
                            _pad = Vector3.zero,
                        };
                        cursor++;
                    }
                }
                else
                {
                    for (int bi = g.startIndex; bi < g.startIndex + g.count; bi++)
                    {
                        if (hasAlive && alive[bi] == 0) continue;

                        Matrix4x4 world = hasWorld ? worldXf[bi] : (root * bricks[bi].localTransform);
                        if (world.m13 < underwaterCullY) continue;

                        if (cursor >= _cpuBuffer.Length) break;

                        _cpuBuffer[cursor] = new GpuBrickData
                        {
                            objectToWorld = world,
                            colorCode = bricks[bi].colorCode,
                            _pad = Vector3.zero,
                        };
                        cursor++;
                    }
                }

                writeCursors[key] = cursor;
            }
        }

        // Upload to GPU
        if (totalOffset > 0)
            _brickBuffer.SetData(_cpuBuffer, 0, 0, totalOffset);

        // Build/update dedicated args buffers per batch (reuse existing buffers to avoid allocations).
        _drawArgsBuffers.Capacity = Mathf.Max(_drawArgsBuffers.Capacity, _drawBatches.Count);
        while (_drawArgsBuffers.Count < _drawBatches.Count)
            _drawArgsBuffers.Add(new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, 5 * sizeof(uint)));
        while (_drawArgsBuffers.Count > _drawBatches.Count)
        {
            int last = _drawArgsBuffers.Count - 1;
            _drawArgsBuffers[last]?.Release();
            _drawArgsBuffers.RemoveAt(last);
        }

        for (int i = 0; i < _drawBatches.Count; i++)
        {
            var batch = _drawBatches[i];
            _argsData[0] = batch.mesh.GetIndexCount(0);
            _argsData[1] = (uint)batch.instanceCount;
            _argsData[2] = batch.mesh.GetIndexStart(0);
            _argsData[3] = batch.mesh.GetBaseVertex(0);
            _argsData[4] = 0;
            _drawArgsBuffers[i].SetData(_argsData);
        }
    }

    // ─── Draw call submission ───

    void SubmitDrawCalls()
    {
        if (_brickBuffer == null || _drawBatches.Count == 0 || _drawArgsBuffers.Count < _drawBatches.Count) return;

        float palWidth = paletteTexture.width;
        float smoothness = indirectMaterial.GetFloat("_Smoothness");
        float specularStrength = indirectMaterial.GetFloat("_SpecularStrength");

        _mpb.Clear();
        _mpb.SetBuffer("_BrickBuffer", _brickBuffer);
        _mpb.SetTexture("_PaletteTex", paletteTexture);
        _mpb.SetFloat("_PaletteWidth", palWidth);
        _mpb.SetFloat("_Smoothness", smoothness);
        _mpb.SetFloat("_SpecularStrength", specularStrength);

        var rp = new RenderParams(indirectMaterial)
        {
            worldBounds = new Bounds(Vector3.zero, Vector3.one * 10000f), // conservative
            shadowCastingMode = shadowMode,
            receiveShadows = receiveShadows,
            layer = gameObject.layer,
            matProps = _mpb,
        };

        for (int i = 0; i < _drawBatches.Count; i++)
        {
            var batch = _drawBatches[i];
            if (batch.mesh == null || batch.instanceCount == 0) continue;

            // Per-draw offset into global brick buffer.
            _mpb.SetFloat("_BrickBufferOffset", (float)batch.startOffset);

            Graphics.RenderMeshIndirect(rp, batch.mesh, _drawArgsBuffers[i]);
        }

#if UNITY_EDITOR
        // In edit mode, force the Scene view to repaint so bricks stay visible
        if (!Application.isPlaying)
            SceneView.RepaintAll();
#endif
    }

    // ─── Fallback rendering for legacy (non-GPU-baked) models ───
    // The old LDrawModelRenderer still works for any model that hasn't been re-baked.
    // This system only handles models with HasGpuData == true.
}
