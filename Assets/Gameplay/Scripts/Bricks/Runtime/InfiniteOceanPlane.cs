using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Renders an infinite LEGO ocean with camera-following LOD rings:
/// - Inner ring: 1x1 tiles (detailed)
/// - Mid ring: 2x2 tiles (medium)
/// - Outer ring: 4x4 tiles (distant)
/// All are rendered via a single child LDrawModelRenderer for efficiency.
/// </summary>
[ExecuteAlways]
public class InfiniteOceanPlane : MonoBehaviour
{
    [Header("LOD Radii (studs)")]
    public float lodInnerRadius = 400f;
    public float lodMidRadius = 1200f;
    public float lodOuterRadius = 3000f;

    [Header("Sea Level")]
    public float seaY = 0f;

    [Header("Appearance")]
    [Tooltip("Color code for the ocean tiles (typically 1=dark blue).")]
    public int oceanColorCode = 1;

    [Header("References")]
    public LDrawPartRegistry partRegistry;
    public Material brickMaterial;
    public Texture2D paletteTexture;
    public LDrawModelAsset preGeneratedLodModel;

    [Header("Follow")]
    [Tooltip("If assigned, follows this transform. Defaults to Camera.main.")]
    public Transform followTarget;

    // Cached for follow
    Vector3 _lastCameraPos;
    float _lastSeaY;

    // Child renderer
    Transform _rendererChild;
    LDrawModelRenderer _renderer;

    void OnEnable()
    {
        EnsureRenderer();
        _lastCameraPos = GetCameraPos();
        _lastSeaY = seaY;
    }

    void OnDisable()
    {
        if (_renderer != null)
            DestroyImmediate(_renderer.gameObject);
        _renderer = null;
        _rendererChild = null;
    }

    void OnValidate()
    {
        EnsureRenderer();
    }

    void LateUpdate()
    {
        // Update position if seaY changed
        if (!Mathf.Approximately(_lastSeaY, seaY))
        {
            transform.position = new Vector3(transform.position.x, seaY, transform.position.z);
            _lastSeaY = seaY;
        }

        // Update position when camera moves
        Vector3 camPos = GetCameraPos();
        if ((camPos - _lastCameraPos).sqrMagnitude > 1f) // moved more than 1 stud
        {
            transform.position = new Vector3(camPos.x, seaY, camPos.z);
            _lastCameraPos = camPos;
        }
    }

    Vector3 GetCameraPos()
    {
        Transform cam = followTarget;
        if (cam == null && Camera.main != null)
            cam = Camera.main.transform;
        return cam != null ? cam.position : Vector3.zero;
    }

    void EnsureRenderer()
    {
        if (_renderer != null) return;

        GameObject rendererGo = new GameObject("_Renderer");
        rendererGo.transform.SetParent(transform, false);
        _rendererChild = rendererGo.transform;

        _renderer = rendererGo.AddComponent<LDrawModelRenderer>();
        _renderer.parts = partRegistry;
        _renderer.brickMaterial = brickMaterial;
        _renderer.paletteTexture = paletteTexture;
        _renderer.model = preGeneratedLodModel;
    }

    public void SyncModel()
    {
        if (_renderer != null)
            _renderer.model = preGeneratedLodModel;
    }
}