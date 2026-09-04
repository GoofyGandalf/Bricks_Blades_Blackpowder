using System;
using UnityEngine;

[ExecuteAlways]
public class TerrainFoliageGenerator : MonoBehaviour
{
    [Serializable]
    public struct FoliagePart
    {
        public string part;
        [Range(0f, 10f)] public float weight;
    }

    [Header("References")]
    public BrickStructure terrainStructure;
    public Transform environmentRoot;
    public LDrawPartRegistry partRegistry;

    [Header("Output")]
    public string foliageObjectName = "Foliage";

    [Header("Placement")]
    [Range(0, 100)] public int seaLevelBias = 4;
    [Range(0f, 1f)] public float placementDensity = 0.08f;
    [Range(1, 6)] public int minStudGap = 2;
    public float foliageBaseYOffset = 0.22f;
    public int seed = 12345;

    [Header("Grass Carpet")]
    public bool generateGrassCarpet = true;
    public string grassPart = "3741a.dat";
    [Range(0f, 1f)] public float grassCoverage = 0.95f;
    public float grassBaseYOffset = -0.02f;
    public float grassStudSink = 0.20f;
    [Range(0.01f, 0.5f)] public float grassNoiseScale = 0.12f;
    [Range(0f, 1f)] public float grassNoiseStrength = 0.18f;

    [Header("Collision Filtering")]
    public bool avoidBlockedAreas = true;
    public LayerMask blockageMask = ~0;
    public float blockageRadius = 0.33f;
    public float blockageStartHeight = 0.28f;
    public float blockageCheckHeight = 3.5f;

    [Header("Foliage Parts")]
    public FoliagePart[] foliageParts =
    {
        new FoliagePart { part = "6084b.dat", weight = 0.72f },
        new FoliagePart { part = "30176.dat", weight = 0.22f },
        new FoliagePart { part = "32607.dat", weight = 0.03f },
        new FoliagePart { part = "33291.dat", weight = 0.03f },
    };

    [Header("Maintenance")]
    public bool clearBeforeGenerate = true;
    public bool deleteGeneratedAssetOnClear = false;

    [SerializeField, HideInInspector] public string generatedAssetPath;
    [SerializeField, HideInInspector] public GameObject foliageObject;
}
