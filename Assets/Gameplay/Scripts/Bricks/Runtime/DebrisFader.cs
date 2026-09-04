using UnityEngine;

/// <summary>
/// Attach to debris GameObjects. After a delay, smoothly fades all renderers
/// to transparent then destroys the object. Swaps material to Bricks/LDrawURP_Fade
/// which has a proper _Alpha property and transparent blend mode.
/// </summary>
public class DebrisFader : MonoBehaviour
{
    public float delay = 4f;
    public float fadeDuration = 1.5f;

    float _spawnTime;
    bool _fading;
    float _fadeStart;
    Renderer[] _renderers;
    MaterialPropertyBlock _mpb;
    static Shader _fadeShader;

    void Start()
    {
        _spawnTime = Time.time;
        _renderers = GetComponentsInChildren<Renderer>();
        _mpb = new MaterialPropertyBlock();
    }

    void Update()
    {
        float elapsed = Time.time - _spawnTime;

        if (!_fading)
        {
            if (elapsed >= delay)
            {
                _fading = true;
                _fadeStart = Time.time;
                SwapToFadeShader();
            }
            return;
        }

        float fadeElapsed = Time.time - _fadeStart;
        float alpha = 1f - Mathf.Clamp01(fadeElapsed / fadeDuration);

        foreach (var r in _renderers)
        {
            if (r == null) continue;
            r.GetPropertyBlock(_mpb);
            _mpb.SetFloat("_Alpha", alpha);
            r.SetPropertyBlock(_mpb);
        }

        if (alpha <= 0f)
            Destroy(gameObject);
    }

    void SwapToFadeShader()
    {
        if (_fadeShader == null)
            _fadeShader = Shader.Find("Bricks/LDrawURP_Fade");

        if (_fadeShader == null) return;

        foreach (var r in _renderers)
        {
            if (r == null) continue;
            // Clone material so we don't affect shared instances, then swap shader
            var mat = r.material; // this clones automatically
            mat.shader = _fadeShader;
        }
    }
}
