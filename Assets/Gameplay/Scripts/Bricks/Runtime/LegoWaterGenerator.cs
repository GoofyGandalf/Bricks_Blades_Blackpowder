using UnityEngine;

[ExecuteAlways]
public class LegoWaterGenerator : MonoBehaviour
{
    [Header("References")]
    public BrickStructure terrainStructure;
    public Transform environmentRoot;
    public LDrawPartRegistry partRegistry;

    [Header("Render")]
    public Material waterMaterial;
    public Texture2D paletteTexture;

    [Header("Output")]
    public string waterObjectName = "LegoWater";

    [Header("Build")]
    [Range(0, 100)] public int seaLevelBias = 4;
    [Tooltip("If true, reads sea level from TerrainColorAdjuster on the terrain object.")]
    public bool useTerrainSeaLevel = true;
    [Min(0)] public int waterPaddingCells = 6;
    [Min(0f)] public float waterYOffset = 0f;

    [Header("Tile Mix")]
    public bool mixedTiles = true;
    [Range(0f, 1f)] public float tile4x4Chance = 0.95f;
    [Range(0f, 1f)] public float tile2x2Chance = 0.85f;

    [Header("Colors")]
    [Tooltip("Opaque foam color code")]
    public int foamColorCode = 15;
    [Tooltip("Shallow water color code")]
    public int shallowWaterColorCode = 1;
    [Tooltip("Deep water color code")]
    public int deepWaterColorCode = 0;
    [Tooltip("Terrain depth (in studs) below sea level before a tile becomes deep water. Increase for a wider shallow band.")]
    [Min(0.1f)] public float shallowDepthStuds = 2f;

    [Header("Foam")]
    public bool enableFoam = true;
    [Tooltip("Additional foam where nearby colliders intersect waterline")]
    public bool overlapFoamRefine = true;
    public LayerMask overlapFoamMask = ~0;
    [Range(0.05f, 2f)] public float overlapFoamRadius = 0.45f;
    [Range(0.05f, 4f)] public float overlapFoamHeight = 1.5f;

    [Header("Animation")]
    public bool animateWater = true;
    public float globalBobAmplitude = 0.55f;
    public float globalBobFrequency = 0.7f;
    public float waveAmplitudePrimary = 0.35f;
    [Tooltip("Keep this very low (0.03-0.08) to avoid height gaps between adjacent tiles.")]
    public float waveFrequencyPrimary = 0.04f;
    public float waveSpeedPrimary = 1.1f;
    public float waveAmplitudeSecondary = 0.18f;
    [Tooltip("Keep this very low (0.03-0.08) to avoid height gaps between adjacent tiles.")]
    public float waveFrequencySecondary = 0.06f;
    public float waveSpeedSecondary = 1.8f;

    [Header("Maintenance")]
    public bool clearBeforeGenerate = true;
    public bool deleteGeneratedAssetOnClear = false;
    public int seed = 24601;

    [SerializeField, HideInInspector] public string generatedAssetPath;
    [SerializeField, HideInInspector] public GameObject waterObject;
}
