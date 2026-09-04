using UnityEngine;
using System.Collections;
using System.Diagnostics;

/// <summary>
/// Generates per-brick BoxColliders as child GameObjects of a BrickStructure.
/// Each collider is sized to the brick's mesh bounds and positioned at the brick's world transform.
/// Colliders are created incrementally across frames to avoid blocking the main thread
/// (which would cause Fusion network timeouts during scene loads).
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(BrickStructure))]
public class BrickColliderManager : MonoBehaviour
{
    const string ContainerName = "_BrickColliders";

    [Tooltip("Max milliseconds to spend building colliders per frame in Play mode. Lower = smoother networking but slower init.")]
    [SerializeField] float colliderBudgetMs = 6f;

    [Tooltip("Use MeshCollider matching the exact part shape (recommended for complex parts like arches and doorways). " +
             "Disable to use BoxColliders sized to mesh bounds instead (faster but inaccurate for non-rectangular parts).")]
    [SerializeField] bool useMeshColliders = true;

    [Tooltip("Spreads initial collider work across structures so they do not all start heavy mesh cooking in the same frame.")]
    [Range(0, 120)]
    [SerializeField] int startupStaggerFrames = 48;

    [Tooltip("Logs when a single collider creation call exceeds this threshold (ms). Helps identify stall-causing part meshes.")]
    [Min(10f)]
    [SerializeField] float slowColliderWarnMs = 200f;

    BrickStructure _structure;
    Transform _container;
    GameObject[] _colliderObjects;
    int _lastDamageVersion = int.MinValue;
    int _lastBrickCount;
    bool _buildComplete;
    Coroutine _buildCoroutine;

    /// <summary>True once the initial collider build is complete for this structure.</summary>
    public bool BuildComplete => _buildComplete;

    void OnEnable()
    {
        _structure = GetComponent<BrickStructure>();

        if (Application.isPlaying)
            _buildCoroutine = StartCoroutine(RebuildCollidersIncremental());
        else
            RebuildCollidersImmediate();
    }

    void OnDisable()
    {
        if (_buildCoroutine != null)
        {
            StopCoroutine(_buildCoroutine);
            _buildCoroutine = null;
        }
        DestroyColliders();
    }

    void LateUpdate()
    {
        if (_structure == null) return;
        if (!_buildComplete) return; // still building

        if (_structure.damageVersion != _lastDamageVersion)
        {
            SyncColliders();
            _lastDamageVersion = _structure.damageVersion;
        }
    }

    // ─── Build / Sync ───

    /// <summary>Incremental build for Play mode.
    /// Creates collider GameObjects time-budgeted to avoid blocking the main thread.
    /// </summary>
    IEnumerator RebuildCollidersIncremental()
    {
        _buildComplete = false;
        DestroyColliders();

        if (_structure == null) _structure = GetComponent<BrickStructure>();
        _structure.EnsureInitialized();

        if (_structure.model == null || !_structure.model.HasGpuData || _structure.worldTransforms == null)
        {
            _buildComplete = true;
            yield break;
        }

        // Stagger startup so dozens of structures do not perform first MeshCollider cook on the same frame.
        int stagger = Mathf.Clamp(startupStaggerFrames, 0, 120);
        if (stagger > 0)
        {
            int framesToWait = Mathf.Abs(GetInstanceID()) % (stagger + 1);
            for (int f = 0; f < framesToWait; f++)
                yield return null;
        }

        int count = _structure.worldTransforms.Length;
        _colliderObjects = new GameObject[count];
        _lastBrickCount = count;

        var containerGo = new GameObject(ContainerName);
        containerGo.transform.SetParent(transform, false);
        containerGo.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
        _container = containerGo.transform;

        // Create collider GameObjects (time-budgeted)
        var sw = Stopwatch.StartNew();
        double budgetTicks = (double)colliderBudgetMs / 1000.0 * Stopwatch.Frequency;

        for (int i = 0; i < count; i++)
        {
            if (_structure.aliveFlags[i] == 0) continue;

            var createSw = Stopwatch.StartNew();
            CreateBrickCollider(i);
            float createMs = (float)(createSw.ElapsedTicks * 1000.0 / Stopwatch.Frequency);
            if (createMs >= slowColliderWarnMs)
            {
                int partId = _structure.model.sortedBricks != null && i < _structure.model.sortedBricks.Length
                    ? _structure.model.sortedBricks[i].partId
                    : -1;
                UnityEngine.Debug.LogWarning($"[BrickColliderManager] Slow collider create: {createMs:F1}ms structure={name} brickIndex={i} partId={partId} meshColliders={useMeshColliders}");
            }

            if (sw.ElapsedTicks >= budgetTicks)
            {
                sw.Restart();
                yield return null;
            }
        }

        _lastDamageVersion = _structure.damageVersion;
        _buildComplete = true;
        _buildCoroutine = null;
    }

    /// <summary>Immediate build for Editor mode (no coroutines).</summary>
    void RebuildCollidersImmediate()
    {
        DestroyColliders();

        if (_structure == null) _structure = GetComponent<BrickStructure>();
        _structure.EnsureInitialized();

        if (_structure.model == null || !_structure.model.HasGpuData) return;
        if (_structure.worldTransforms == null) return;

        int count = _structure.worldTransforms.Length;
        _colliderObjects = new GameObject[count];
        _lastBrickCount = count;

        var containerGo = new GameObject(ContainerName);
        containerGo.transform.SetParent(transform, false);
        containerGo.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
        _container = containerGo.transform;

        for (int i = 0; i < count; i++)
        {
            if (_structure.aliveFlags[i] == 0) continue;
            CreateBrickCollider(i);
        }

        _lastDamageVersion = _structure.damageVersion;
        _buildComplete = true;
    }

    void RebuildColliders()
    {
        if (Application.isPlaying)
        {
            if (_buildCoroutine != null) StopCoroutine(_buildCoroutine);
            _buildCoroutine = StartCoroutine(RebuildCollidersIncremental());
        }
        else
        {
            RebuildCollidersImmediate();
        }
    }

    void CreateBrickCollider(int brickIndex)
    {
        var brick = _structure.model.sortedBricks[brickIndex];
        var xf = _structure.worldTransforms[brickIndex];

        Mesh mesh = _structure.parts != null ? _structure.parts.GetMeshById(brick.partId) : null;

        var go = new GameObject($"BC_{brickIndex}");
        go.transform.SetParent(_container, false);
        go.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
        go.layer = gameObject.layer;

        go.transform.position = xf.GetColumn(3);
        go.transform.rotation = xf.rotation;
        go.transform.localScale = xf.lossyScale;

        if (useMeshColliders && mesh != null && mesh.isReadable)
        {
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = false; // non-convex for accurate complex shapes (arches, doorways, etc.)
        }
        else
        {
            // Fallback: BoxCollider sized to mesh bounds.
            // Also used when mesh is non-readable (GPU-only) to avoid synchronous PhysX cooking.
            Bounds meshBounds = mesh != null ? mesh.bounds : new Bounds(Vector3.zero, Vector3.one * 0.5f);
            var box = go.AddComponent<BoxCollider>();
            box.center = meshBounds.center;
            box.size   = meshBounds.size;
        }

        var tag = go.AddComponent<BrickColliderTag>();
        tag.structure  = _structure;
        tag.brickIndex = brickIndex;

        _colliderObjects[brickIndex] = go;
    }

    /// <summary>
    /// Fast sync: destroy colliders for dead bricks only.
    /// Full rebuild when brick count changes.
    /// </summary>
    void SyncColliders()
    {
        if (_colliderObjects == null || _structure.aliveFlags == null
            || _lastBrickCount != (_structure.worldTransforms?.Length ?? 0))
        {
            RebuildColliders();
            return;
        }

        for (int i = 0; i < _colliderObjects.Length; i++)
        {
            if (_structure.aliveFlags[i] != 0 || _colliderObjects[i] == null) continue;

            // Brick died — remove its collider
            if (Application.isPlaying)
                Destroy(_colliderObjects[i]);
            else
                DestroyImmediate(_colliderObjects[i]);

            _colliderObjects[i] = null;
        }
    }

    void DestroyColliders()
    {
        // Remove container child (and all colliders beneath it)
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i);
            if (child.name != ContainerName) continue;

            if (Application.isPlaying)
                Destroy(child.gameObject);
            else
                DestroyImmediate(child.gameObject);
        }

        _colliderObjects = null;
        _container = null;
    }

    // ─── Static API ───

    /// <summary>
    /// Given a Collider from a physics hit, resolve the BrickStructure and specific brick index.
    /// Returns true if the collider belongs to a brick.
    /// </summary>
    public static bool TryGetBrickFromCollider(Collider col, out BrickStructure structure, out int brickIndex)
    {
        var tag = col.GetComponent<BrickColliderTag>();
        if (tag != null)
        {
            structure = tag.structure;
            brickIndex = tag.brickIndex;
            return structure != null;
        }

        // Fallback: legacy single-collider hit proxy
        structure = col.GetComponentInParent<BrickStructure>();
        brickIndex = -1;
        return structure != null;
    }
}
