using UnityEngine;

/// <summary>
/// Lightweight collision proxy for BrickStructure hit detection.
/// Uses a single BoxCollider covering alive brick positions.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(BrickStructure))]
public class BrickStructureHitProxy : MonoBehaviour
{
    [Tooltip("Extra padding around computed brick bounds.")]
    public Vector3 padding = new Vector3(0.12f, 0.12f, 0.12f);

    BrickStructure _structure;
    BoxCollider _box;

    int _lastDamageVersion = int.MinValue;
    Vector3 _lastPos;
    Quaternion _lastRot;
    Vector3 _lastScale;

    void OnEnable()
    {
        _structure = GetComponent<BrickStructure>();
        _box = GetComponent<BoxCollider>();
        if (_box == null)
            _box = gameObject.AddComponent<BoxCollider>();

        _box.isTrigger = false;
        RebuildBounds();
    }

    void LateUpdate()
    {
        if (_structure == null)
            _structure = GetComponent<BrickStructure>();

        if (_structure == null) return;

        bool transformChanged = transform.position != _lastPos || transform.rotation != _lastRot || transform.lossyScale != _lastScale;
        if (transformChanged || _structure.damageVersion != _lastDamageVersion)
            RebuildBounds();
    }

    void RebuildBounds()
    {
        if (_structure == null)
            return;

        _structure.EnsureInitialized();

        var world = _structure.worldTransforms;
        var alive = _structure.aliveFlags;

        _lastPos = transform.position;
        _lastRot = transform.rotation;
        _lastScale = transform.lossyScale;
        _lastDamageVersion = _structure.damageVersion;

        if (world == null || alive == null || world.Length == 0)
        {
            _box.enabled = false;
            return;
        }

        bool hasAny = false;
        Bounds bounds = default;

        for (int i = 0; i < world.Length; i++)
        {
            if (alive[i] == 0) continue;

            Vector3 p = world[i].GetColumn(3);
            if (!hasAny)
            {
                bounds = new Bounds(p, Vector3.zero);
                hasAny = true;
            }
            else
            {
                bounds.Encapsulate(p);
            }
        }

        if (!hasAny)
        {
            _box.enabled = false;
            return;
        }

        _box.enabled = true;

        bounds.Expand(padding * 2f);

        // Convert world bounds to local collider values.
        _box.center = transform.InverseTransformPoint(bounds.center);

        Vector3 lossy = transform.lossyScale;
        float sx = Mathf.Abs(lossy.x) > 1e-6f ? Mathf.Abs(lossy.x) : 1e-6f;
        float sy = Mathf.Abs(lossy.y) > 1e-6f ? Mathf.Abs(lossy.y) : 1e-6f;
        float sz = Mathf.Abs(lossy.z) > 1e-6f ? Mathf.Abs(lossy.z) : 1e-6f;

        _box.size = new Vector3(bounds.size.x / sx, bounds.size.y / sy, bounds.size.z / sz);
    }
}
