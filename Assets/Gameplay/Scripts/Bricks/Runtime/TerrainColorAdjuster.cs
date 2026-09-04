using UnityEngine;

/// <summary>
/// Runtime component to adjust terrain brick colors via Sea Level Bias slider.
/// Recolors bricks based on their world height without regenerating the asset.
/// Attach to a GameObject with a BrickStructure component.
/// </summary>
[ExecuteAlways]
public class TerrainColorAdjuster : MonoBehaviour
{
    [Range(0, 100)]
    public int seaLevelBias = 50;

    public bool autoUpdate = true;

    [Header("Grass Variation")]
    [Range(0.01f, 0.5f)]
    public float grassNoiseScale = 0.09f;
    [Range(0f, 1f)]
    public float grassNoiseStrength = 0.33f;
    [Range(1, 16)]
    public int grassNoiseQuantBlock = 4;

    [Header("Coastline")]
    [Range(0f, 0.25f)]
    public float beachBand = 0.06f;

    BrickStructure _structure;
    LDrawModelAsset _model;
    int[] _originalColors;
    float[] _brickHeights;
    Vector2[] _brickXZ;
    int _lastSeaLevelBias = -1;
    float _lastGrassNoiseScale = -1f;
    float _lastGrassNoiseStrength = -1f;
    int _lastGrassNoiseQuantBlock = -1;
    float _lastBeachBand = -1f;
    bool _needsRecalc = true;

    // Shoreline to deep-water palette: light sand near coast -> dark blue/black at depth.
    static readonly int[] WaterDepthPalette = { 19, 25, 1, 0 };
    // Land palette with multiple greens for patch variation.
    static readonly int[] GrassPalette = { 2, 10, 17, 288 };
    static readonly int[] CoastalBlendPalette = { 25, 19, 2 };

    void OnEnable()
    {
        _structure = GetComponent<BrickStructure>();
        if (_structure != null)
        {
            _structure.EnsureInitialized();
            _model = _structure.model;
            if (_model != null && _model.HasGpuData)
            {
                CacheOriginalColors();
                CacheBrickWorldData();
                _needsRecalc = true;
            }
        }
    }

    void LateUpdate()
    {
        if (!autoUpdate || _structure == null)
            return;

        // Handle runtime model swaps/regeneration and lazy init.
        if (_model != _structure.model)
        {
            _model = _structure.model;
            _originalColors = null;
            _brickHeights = null;
            _brickXZ = null;
            _needsRecalc = true;
        }

        if (_model == null || !_model.HasGpuData)
            return;

        if (_structure.worldTransforms == null || _structure.worldTransforms.Length != _model.sortedBricks.Length)
            _structure.EnsureInitialized();

        // Refresh caches when missing or stale.
        if (_originalColors == null || _originalColors.Length != _model.sortedBricks.Length)
            CacheOriginalColors();
        if (_brickHeights == null || _brickHeights.Length != _model.sortedBricks.Length ||
            _brickXZ == null || _brickXZ.Length != _model.sortedBricks.Length)
            CacheBrickWorldData();

        if (_brickHeights == null || _brickXZ == null)
            return;

        if (seaLevelBias != _lastSeaLevelBias)
        {
            _needsRecalc = true;
            _lastSeaLevelBias = seaLevelBias;
        }

        if (!Mathf.Approximately(grassNoiseScale, _lastGrassNoiseScale) ||
            !Mathf.Approximately(grassNoiseStrength, _lastGrassNoiseStrength) ||
            grassNoiseQuantBlock != _lastGrassNoiseQuantBlock ||
            !Mathf.Approximately(beachBand, _lastBeachBand))
        {
            _needsRecalc = true;
            _lastGrassNoiseScale = grassNoiseScale;
            _lastGrassNoiseStrength = grassNoiseStrength;
            _lastGrassNoiseQuantBlock = grassNoiseQuantBlock;
            _lastBeachBand = beachBand;
        }

        if (_needsRecalc)
        {
            RecalculateColors();
            _needsRecalc = false;
        }
    }

    void CacheOriginalColors()
    {
        if (_model.sortedBricks == null)
            return;

        _originalColors = new int[_model.sortedBricks.Length];
        for (int i = 0; i < _model.sortedBricks.Length; i++)
            _originalColors[i] = _model.sortedBricks[i].colorCode;
    }

    void CacheBrickWorldData()
    {
        if (_structure == null)
            return;

        if (_structure.worldTransforms == null || _structure.worldTransforms.Length == 0)
            return;

        _brickHeights = new float[_structure.worldTransforms.Length];
        _brickXZ = new Vector2[_structure.worldTransforms.Length];
        for (int i = 0; i < _structure.worldTransforms.Length; i++)
        {
            var matrix = _structure.worldTransforms[i];
            Vector3 p = matrix.GetColumn(3);
            _brickHeights[i] = p.y;
            _brickXZ[i] = new Vector2(p.x, p.z);
        }
    }

    void RecalculateColors()
    {
        if (_originalColors == null || _brickHeights == null || _brickXZ == null)
            return;

        // Find min/max heights to normalize to [0,1]
        float minHeight = float.MaxValue;
        float maxHeight = float.MinValue;
        foreach (float h in _brickHeights)
        {
            if (h < minHeight) minHeight = h;
            if (h > maxHeight) maxHeight = h;
        }

        if (maxHeight <= minHeight)
            maxHeight = minHeight + 1f;

        float heightRange = maxHeight - minHeight;
        float seaLevelT = Mathf.Clamp01(seaLevelBias / 100f);
        float beachTop = Mathf.Clamp01(seaLevelT + beachBand);

        // Recalculate colors with shore/depth gradient and quantized grassy noise patches.
        for (int i = 0; i < _model.sortedBricks.Length; i++)
        {
            float normalizedHeight = (_brickHeights[i] - minHeight) / heightRange;
            int color;

            if (normalizedHeight <= seaLevelT)
            {
                // 0 at shore, 1 at deepest point.
                float depthT = Mathf.InverseLerp(seaLevelT, 0f, normalizedHeight);
                color = SamplePalette(WaterDepthPalette, depthT);
            }
            else if (normalizedHeight <= beachTop)
            {
                float coastT = Mathf.InverseLerp(seaLevelT, beachTop, normalizedHeight);
                color = SamplePalette(CoastalBlendPalette, coastT);
            }
            else
            {
                float landT = Mathf.InverseLerp(beachTop, 1f, normalizedHeight);
                Vector2 p = _brickXZ[i];
                float noise = QuantizedPerlin(p.x, p.y, grassNoiseScale, grassNoiseQuantBlock);
                float combined = Mathf.Clamp01(landT + (noise * 2f - 1f) * grassNoiseStrength);
                color = SamplePalette(GrassPalette, combined);
            }

            var brick = _model.sortedBricks[i];
            brick.colorCode = color;
            _model.sortedBricks[i] = brick;
        }

        // Notify render system that colors changed
        if (_structure != null)
        {
            _structure.damageVersion++;
            BrickRenderSystem.NotifyStructureChanged();
        }
    }

#if UNITY_EDITOR
    [ContextMenu("Reset Colors")]
    void ResetColors()
    {
        if (_originalColors != null && _model != null && _model.HasGpuData)
        {
            for (int i = 0; i < _originalColors.Length && i < _model.sortedBricks.Length; i++)
                _model.sortedBricks[i].colorCode = _originalColors[i];

            seaLevelBias = 50;
            _lastSeaLevelBias = 50;

            if (_structure != null)
            {
                _structure.damageVersion++;
                BrickRenderSystem.NotifyStructureChanged();
            }
        }
    }
#endif

    static int SamplePalette(int[] palette, float t)
    {
        if (palette == null || palette.Length == 0)
            return 2;

        int idx = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(t) * palette.Length), 0, palette.Length - 1);
        return palette[idx];
    }

    static float QuantizedPerlin(float worldX, float worldZ, float scale, int quantBlock)
    {
        int block = Mathf.Max(1, quantBlock);
        float qx = Mathf.Floor(worldX / block);
        float qz = Mathf.Floor(worldZ / block);
        return Mathf.PerlinNoise(qx * scale + 17.13f, qz * scale + 49.91f);
    }
}
