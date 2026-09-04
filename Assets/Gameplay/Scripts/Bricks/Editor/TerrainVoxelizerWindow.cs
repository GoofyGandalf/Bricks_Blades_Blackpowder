#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public class TerrainVoxelizerWindow : EditorWindow
{
    const float StudPitch = 1.0f;
    const float PlateHeight = 0.4f;
    const float StudHeight = 0.2f;
    const float PlateTotalHeight = PlateHeight + StudHeight;
    const string OutputFolder = "Assets/Content/Bricks/BrickData/Terrain/";
    const string BasePart = "3024.dat"; // 1x1 plate; canonical robust mode
    const string PartsCacheFolder = "Assets/Content/Bricks/PartsCache/";

    struct TriSample
    {
        public Vector3 a;
        public Vector3 b;
        public Vector3 c;
        public float minX;
        public float maxX;
        public float minZ;
        public float maxZ;
        public float denom;
    }

    struct BasePartMetrics
    {
        public float pitchX;
        public float pitchZ;
        public float height;
        public Vector3 anchorOffset;
    }

    struct PlacementOption
    {
        public int    w, d;   // stud dims to place: w columns in X, d rows in Z
        public string part;   // LDraw part filename
        public bool   rot90;  // apply 90° Y rotation to orient the part correctly
    }

    struct VerticalOption
    {
        public string part;   // tall 1x1 part
        public int layers;    // number of plate layers this part should replace
    }

    // (minStuds, maxStuds) → part filename (minStuds ≤ maxStuds).
    // meshScale derived from 3024 works universally — all LDraw plates share the same
    // coordinate scale, so the per-stud normalization factor is identical for every size.
    static readonly Dictionary<(int, int), string> PlateCatalog =
        new Dictionary<(int, int), string>
        {
            // Square-only merge set for robust terrain packing (no orientation artifacts).
            {(1,1),"3024.dat"},
            {(2,2),"3022.dat"},
            {(4,4),"3031.dat"},
        };

    // All placement options sorted by area descending (both orientations of every plate).
    static readonly PlacementOption[] SortedPlacements = BuildPlacementOptions();
    static readonly VerticalOption[] SortedVerticalOptions =
    {
        new VerticalOption { part = "2453b.dat", layers = 5 }, // 1x1x5
        new VerticalOption { part = "14716.dat", layers = 3 }, // 1x1x3
    };
    static PlacementOption[] BuildPlacementOptions()
    {
        var list = new List<PlacementOption>(PlateCatalog.Count * 2);
        foreach (var kv in PlateCatalog)
        {
            int a = kv.Key.Item1, b = kv.Key.Item2;
            list.Add(new PlacementOption { w = a, d = b, part = kv.Value, rot90 = false });
            if (a != b)
                list.Add(new PlacementOption { w = b, d = a, part = kv.Value, rot90 = true });
        }
        // Larger area first; wider plate wins ties (better row coverage).
        list.Sort((x, y) =>
        {
            int cmp = (y.w * y.d).CompareTo(x.w * x.d);
            if (cmp != 0) return cmp;
            // Prefer squarer plates first to reduce long-strip visual artifacts.
            int xSkew = Mathf.Abs(x.w - x.d);
            int ySkew = Mathf.Abs(y.w - y.d);
            cmp = xSkew.CompareTo(ySkew);
            return cmp != 0 ? cmp : y.w.CompareTo(x.w);
        });
        return list.ToArray();
    }

    // Island surface palette (low→high): beach sand to lush grass.
    static readonly int[] TerrainSurfacePalette = { 19, 25, 2, 10, 17 };
    // Dark Brown → Reddish Brown → Brown → Tan: deep→shallow dirt shading
    static readonly int[] TerrainDirtPalette  = { 308, 70, 6, 25 };

    MeshFilter sourceMeshFilter;
    int colorCode = 2;
    bool solidFill;
    bool centerToOrigin;
    bool stackSteepSlopes = true;
    int maxSlopeSupportLayers = 0; // 0 = unlimited (recommended for tall mesa walls)
    int sampleHoleFillPasses = 2;
    bool proceduralColor = true;
    float noiseScale = 0.12f;
    float noiseStrength = 0.35f;
    bool keepSourcePosition = true;
    bool mergePlates = true;
    bool mergeVerticalColumns = true;
    int seaLevelBias = 50;  // 0-100: shifts sand/grass palette boundary; 50 = default
    string outputName = "Terrain_Lego";

    static float SnapFloor(float value, float step) => Mathf.Floor(value / step) * step;
    static float SnapNearest(float value, float step) => Mathf.Round(value / step) * step;

    [MenuItem("Tools/Terrain/Voxelize Mesh to LDraw Asset")]
    static void Open()
    {
        GetWindow<TerrainVoxelizerWindow>("Terrain Voxelizer");
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
        sourceMeshFilter = (MeshFilter)EditorGUILayout.ObjectField("Mesh Filter", sourceMeshFilter, typeof(MeshFilter), true);

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        outputName = EditorGUILayout.TextField("Asset Name", outputName);
        colorCode = EditorGUILayout.IntField("LDraw Color Code", colorCode);

        proceduralColor = EditorGUILayout.Toggle(new GUIContent("Procedural Color", "Vary brick color by height and noise for realistic terrain shading."), proceduralColor);
        if (proceduralColor)
        {
            EditorGUI.indentLevel++;
            noiseScale    = EditorGUILayout.Slider(new GUIContent("Noise Scale",    "Spatial frequency of color variation."), noiseScale,    0.01f, 0.5f);
            noiseStrength = EditorGUILayout.Slider(new GUIContent("Noise Strength", "How strongly random noise offsets height-based color."), noiseStrength, 0f, 1f);
            seaLevelBias  = EditorGUILayout.IntSlider(new GUIContent("Sea Level Bias (%)", "Adjusts sand/grass transition height. 0=lowest, 50=default, 100=highest."), seaLevelBias, 0, 100);
            EditorGUI.indentLevel--;
        }

        solidFill = EditorGUILayout.Toggle(new GUIContent("Solid Fill", "Fill all layers under the surface. Disabled = shell only."), solidFill);
        centerToOrigin = EditorGUILayout.Toggle(new GUIContent("Center XZ To Origin", "Centers generated terrain in model space for easier placement."), centerToOrigin);
        keepSourcePosition = EditorGUILayout.Toggle(new GUIContent("Keep Source Position", "If enabled, generated terrain keeps source world-space placement instead of rebasing to origin."), keepSourcePosition);
        stackSteepSlopes = EditorGUILayout.Toggle(new GUIContent("Stack Steep Slopes", "In shell mode, add support plates where neighboring cells drop steeply."), stackSteepSlopes);
        if (stackSteepSlopes)
            maxSlopeSupportLayers = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Max Slope Support Layers (0 = Unlimited)", "Caps extra stacked layers on steep drops. Use 0 for no cap (best for mesas/cliffs)."), maxSlopeSupportLayers));
        sampleHoleFillPasses = EditorGUILayout.IntSlider(new GUIContent("Hole Fill Passes", "Repairs small missed cells after triangle projection."), sampleHoleFillPasses, 0, 4);
        mergeVerticalColumns = EditorGUILayout.Toggle(new GUIContent("Merge Vertical Columns", "Replace stacked 1x1 plate columns with tall 1x1 parts (14716, 2453b)."), mergeVerticalColumns);
        mergePlates = EditorGUILayout.Toggle(new GUIContent("Merge Plates", "Merge adjacent 1x1 cells into larger standard plates (2x4, 4x4 etc.) to reduce brick count."), mergePlates);
        EditorGUILayout.HelpBox(mergePlates
            ? "Plate merging: adjacent same-layer cells are greedily packed into square plates (2×2, 4×4) for robust alignment. Reduces brick count heavily while avoiding stripe/overlap artifacts."
            : "Plate merging disabled — all bricks placed as 1×1 plates.", MessageType.Info);

        EditorGUILayout.Space(10f);
        if (GUILayout.Button("Generate Terrain Asset", GUILayout.Height(28f)))
            GenerateAsset();
    }

    void GenerateAsset()
    {
        if (sourceMeshFilter == null || sourceMeshFilter.sharedMesh == null)
        {
            Debug.LogError("TerrainVoxelizer: assign a MeshFilter with a valid shared mesh.");
            return;
        }

        var settings = FindSettings();
        if (settings == null || string.IsNullOrWhiteSpace(settings.ldrawRootPath))
        {
            Debug.LogError("TerrainVoxelizer: missing LDrawSettings.ldrawRootPath.");
            return;
        }

        var basePart = GetBasePartMetrics(settings.ldrawRootPath);
        float partPitchX = Mathf.Max(0.0001f, basePart.pitchX);
        float partPitchZ = Mathf.Max(0.0001f, basePart.pitchZ);
        float partHeight = Mathf.Max(0.0001f, basePart.height);

        // Canonical LEGO terrain lattice (independent of source mesh bake scale).
        float pitchX = StudPitch;
        float pitchZ = StudPitch;
        float plateStep = PlateHeight;
        Vector3 meshScale = new Vector3(
            pitchX / partPitchX,
            PlateTotalHeight / partHeight,
            pitchZ / partPitchZ);

        var mesh = sourceMeshFilter.sharedMesh;

        // Build sample vertices in world space (respect full transform: translation/rotation/scale)
        // then project them into LEGO sample units.
        Matrix4x4 localToWorld = sourceMeshFilter.transform.localToWorldMatrix;
        var sourceVertices = mesh.vertices;
        var sampledVertices = new Vector3[sourceVertices.Length];
        Vector3 sampledMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 sampledMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int vi = 0; vi < sourceVertices.Length; vi++)
        {
            Vector3 v = localToWorld.MultiplyPoint3x4(sourceVertices[vi]);
            sampledVertices[vi] = v;
            sampledMin = Vector3.Min(sampledMin, v);
            sampledMax = Vector3.Max(sampledMax, v);
        }

        // Work in mesh-local space to avoid scene transform/collider artifacts.
        float gridOriginX = SnapFloor(sampledMin.x, pitchX);
        float gridOriginZ = SnapFloor(sampledMin.z, pitchZ);
        float gridEndX = sampledMax.x;
        float gridEndZ = sampledMax.z;

        int gridX = Mathf.Max(1, Mathf.CeilToInt((gridEndX - gridOriginX) / pitchX));
        int gridZ = Mathf.Max(1, Mathf.CeilToInt((gridEndZ - gridOriginZ) / pitchZ));
        var heightGrid = new int[gridX, gridZ];
        float yBase = SnapFloor(sampledMin.y, plateStep);

        BuildTriangleSampler(mesh, sampledVertices, out var tris, out var bins, out int binCountX, out int binCountZ, out float binSize, gridOriginX, gridOriginZ, gridEndX, gridEndZ, Mathf.Max(pitchX, pitchZ) * 4f);

        if (tris.Count == 0)
        {
            Debug.LogWarning("TerrainVoxelizer: no valid projected triangles found in source mesh.");
            return;
        }

        for (int ix = 0; ix < gridX; ix++)
        {
            for (int iz = 0; iz < gridZ; iz++)
            {
                float wx = gridOriginX + (ix + 0.5f) * pitchX;
                float wz = gridOriginZ + (iz + 0.5f) * pitchZ;

                if (!TrySampleHeight(wx, wz, gridOriginX, gridOriginZ, tris, bins, binCountX, binCountZ, binSize, out float y))
                    continue;

                float relativeY = Mathf.Max(0f, y - yBase);
                int plates = Mathf.Max(1, Mathf.RoundToInt(relativeY / plateStep));
                heightGrid[ix, iz] = plates;
            }
        }

        FillSamplingHoles(heightGrid, sampleHoleFillPasses);

        var bricks = new List<LDrawModelAsset.Brick>(gridX * gridZ);
        var uniqueParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int maxHeight = 0;
        for (int ix = 0; ix < gridX; ix++)
            for (int iz = 0; iz < gridZ; iz++)
                if (heightGrid[ix, iz] > maxHeight)
                    maxHeight = heightGrid[ix, iz];

        if (maxHeight <= 0)
        {
            Debug.LogWarning("TerrainVoxelizer: no terrain cells were sampled from mesh geometry.");
            return;
        }

        float centerOffsetX;
        float centerOffsetZ;
        if (centerToOrigin)
        {
            // Use the generated grid extents (not mesh bounds) so centering stays stud-aligned.
            centerOffsetX = -(gridOriginX + gridX * pitchX * 0.5f);
            centerOffsetZ = -(gridOriginZ + gridZ * pitchZ * 0.5f);
        }
        else if (keepSourcePosition)
        {
            centerOffsetX = 0f;
            centerOffsetZ = 0f;
        }
        else
        {
            // Keep coordinates small/stable in asset-local space while preserving LEGO grid alignment.
            centerOffsetX = -gridOriginX;
            centerOffsetZ = -gridOriginZ;
        }
        float yOffset = keepSourcePosition ? 0f : -yBase;

        // ── Collect per-layer color grids ──
        // colorGrid[ix,iz] = LDraw color code, or -1 if the cell is empty at this layer.
        var layerGrids = new Dictionary<int, int[,]>();
        void OccupyCell(int lx, int lz, int layerIdx, int color)
        {
            if (!layerGrids.TryGetValue(layerIdx, out int[,] grid))
            {
                grid = new int[gridX, gridZ];
                for (int i = 0; i < gridX; i++)
                    for (int j = 0; j < gridZ; j++)
                        grid[i, j] = -1;
                layerGrids[layerIdx] = grid;
            }
            grid[lx, lz] = color;
        }

        if (!solidFill)
        {
            for (int ix = 0; ix < gridX; ix++)
            {
                for (int iz = 0; iz < gridZ; iz++)
                {
                    int h = heightGrid[ix, iz];
                    if (h <= 0)
                        continue;

                    int topLayer = h - 1;
                    int cellColor = ComputeTerrainColor(ix, iz, h, maxHeight, colorCode, proceduralColor, noiseScale, noiseStrength, TerrainSurfacePalette, seaLevelBias);
                    OccupyCell(ix, iz, topLayer, cellColor);

                    if (stackSteepSlopes)
                    {
                        int supportDepth = ComputeSlopeSupportDepth(heightGrid, ix, iz, maxSlopeSupportLayers);
                        for (int d = 1; d <= supportDepth; d++)
                        {
                            int supportLayer = topLayer - d;
                            if (supportLayer < 0)
                                break;
                            OccupyCell(ix, iz, supportLayer, cellColor);
                        }
                    }
                }
            }
        }
        else
        {
            for (int iy = 0; iy < maxHeight; iy++)
            {
                for (int ix = 0; ix < gridX; ix++)
                {
                    for (int iz = 0; iz < gridZ; iz++)
                    {
                        int h = heightGrid[ix, iz];
                        if (h <= iy)
                            continue;

                        bool isSurface = (iy == h - 1);
                        int cellColor = isSurface
                            ? ComputeTerrainColor(ix, iz, h, maxHeight, colorCode, proceduralColor, noiseScale, noiseStrength, TerrainSurfacePalette, seaLevelBias)
                            : ComputeTerrainColor(ix, iz, iy + 1, h - 1, colorCode, proceduralColor, noiseScale, noiseStrength, TerrainDirtPalette, 50);
                        OccupyCell(ix, iz, iy, cellColor);
                    }
                }
            }
        }

        // ── Merge adjacent cells into the largest fitting standard plates ──
        var placementOpts = mergePlates
            ? SortedPlacements
            : new[] { new PlacementOption { w = 1, d = 1, part = BasePart, rot90 = false } };
        var partMetricsCache = new Dictionary<string, BasePartMetrics>(StringComparer.OrdinalIgnoreCase)
        {
            [BasePart] = basePart,
        };

        if (mergeVerticalColumns)
        {
            MergeVerticalColumns(layerGrids, maxHeight,
                gridOriginX, gridOriginZ, yBase, centerOffsetX, centerOffsetZ, yOffset,
                pitchX, pitchZ, plateStep, meshScale,
                settings.ldrawRootPath, partMetricsCache, bricks, uniqueParts);
        }

        foreach (var kv in layerGrids)
        {
            MergeLayer(kv.Key, kv.Value, gridX, gridZ,
                gridOriginX, gridOriginZ, yBase, centerOffsetX, centerOffsetZ, yOffset,
                pitchX, pitchZ, plateStep, meshScale,
                placementOpts, settings.ldrawRootPath, partMetricsCache, bricks, uniqueParts);
        }

        var model = LDrawModelImporter.BuildModelFromBrickList(bricks, uniqueParts, settings.ldrawRootPath);
        if (model == null)
        {
            Debug.LogError("TerrainVoxelizer: failed to build model asset.");
            return;
        }

        Directory.CreateDirectory(OutputFolder);
        string safeName = string.IsNullOrWhiteSpace(outputName) ? "Terrain_Lego" : outputName.Trim();
        string outPath = AssetDatabase.GenerateUniqueAssetPath(OutputFolder + safeName + ".asset");

        AssetDatabase.CreateAsset(model, outPath);
        EditorUtility.SetDirty(model);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"TerrainVoxelizer: generated {bricks.Count} bricks, {model.batchGroups?.Length ?? 0} batches -> {outPath}");
        Selection.activeObject = model;
    }

    static void AddPlate(
        int ix, int iz, int w, int d, int layer,
        string partName, bool rot90, int color,
        float gridOriginX, float gridOriginZ,
        float yBase, float centerOffsetX, float centerOffsetZ, float yOffset,
        float pitchX, float pitchZ, float plateStep,
        Vector3 baseMeshScale, BasePartMetrics partMetrics, float desiredTotalHeight,
        List<LDrawModelAsset.Brick> bricks,
        HashSet<string> uniqueParts)
    {
        float cx = gridOriginX + (ix + w * 0.5f) * pitchX + centerOffsetX;
        float cy = yBase + layer * plateStep + yOffset;
        float cz = gridOriginZ + (iz + d * 0.5f) * pitchZ + centerOffsetZ;

        // Keep X/Z stud normalization from the base plate and scale Y per placed part height.
        Vector3 partScale = new Vector3(
            baseMeshScale.x,
            Mathf.Max(0.0001f, desiredTotalHeight) / Mathf.Max(0.0001f, partMetrics.height),
            baseMeshScale.z);

        var rot = rot90 ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
        bricks.Add(new LDrawModelAsset.Brick
        {
            part      = partName,
            colorCode = color,
            local     = Matrix4x4.TRS(new Vector3(cx, cy, cz), rot, Vector3.one)
                      * Matrix4x4.Scale(partScale)
                      * Matrix4x4.Translate(partMetrics.anchorOffset)
        });
        uniqueParts.Add(partName);
    }

    static bool TryGetLayerCellColor(Dictionary<int, int[,]> layerGrids, int layer, int x, int z, out int color)
    {
        color = -1;
        if (!layerGrids.TryGetValue(layer, out var grid))
            return false;

        color = grid[x, z];
        return color >= 0;
    }

    static void ClearLayerCell(Dictionary<int, int[,]> layerGrids, int layer, int x, int z)
    {
        if (layerGrids.TryGetValue(layer, out var grid))
            grid[x, z] = -1;
    }

    static void MergeVerticalColumns(
        Dictionary<int, int[,]> layerGrids,
        int maxHeight,
        float gridOriginX, float gridOriginZ,
        float yBase, float centerOffsetX, float centerOffsetZ, float yOffset,
        float pitchX, float pitchZ, float plateStep,
        Vector3 meshScale,
        string ldrawRootPath,
        Dictionary<string, BasePartMetrics> partMetricsCache,
        List<LDrawModelAsset.Brick> bricks,
        HashSet<string> uniqueParts)
    {
        if (layerGrids == null || layerGrids.Count == 0 || maxHeight <= 0)
            return;

        int[,] anyGrid = null;
        foreach (var kv in layerGrids)
        {
            anyGrid = kv.Value;
            break;
        }

        if (anyGrid == null)
            return;

        int gridX = anyGrid.GetLength(0);
        int gridZ = anyGrid.GetLength(1);
        int minTallLayers = SortedVerticalOptions[SortedVerticalOptions.Length - 1].layers;

        for (int x = 0; x < gridX; x++)
        {
            for (int z = 0; z < gridZ; z++)
            {
                int layer = 0;
                while (layer < maxHeight)
                {
                    if (!TryGetLayerCellColor(layerGrids, layer, x, z, out int color))
                    {
                        layer++;
                        continue;
                    }

                    int runStart = layer;
                    int runLen = 1;
                    while (runStart + runLen < maxHeight
                           && TryGetLayerCellColor(layerGrids, runStart + runLen, x, z, out int nextColor)
                           && nextColor == color)
                    {
                        runLen++;
                    }

                    if (runLen < minTallLayers)
                    {
                        layer = runStart + runLen;
                        continue;
                    }

                    int cursor = runStart;
                    int remaining = runLen;
                    while (remaining >= minTallLayers)
                    {
                        VerticalOption? chosen = null;
                        for (int i = 0; i < SortedVerticalOptions.Length; i++)
                        {
                            if (SortedVerticalOptions[i].layers <= remaining)
                            {
                                chosen = SortedVerticalOptions[i];
                                break;
                            }
                        }

                        if (!chosen.HasValue)
                            break;

                        var opt = chosen.Value;
                        if (!partMetricsCache.TryGetValue(opt.part, out var partMetrics))
                        {
                            partMetrics = GetPartMetrics(ldrawRootPath, opt.part);
                            partMetricsCache[opt.part] = partMetrics;
                        }

                        float desiredHeight = opt.layers * PlateHeight + StudHeight;
                        int topLayer = cursor + opt.layers - 1;
                        AddPlate(x, z, 1, 1, topLayer, opt.part, false, color,
                            gridOriginX, gridOriginZ, yBase, centerOffsetX, centerOffsetZ, yOffset,
                            pitchX, pitchZ, plateStep, meshScale, partMetrics, desiredHeight, bricks, uniqueParts);

                        for (int dy = 0; dy < opt.layers; dy++)
                            ClearLayerCell(layerGrids, cursor + dy, x, z);

                        cursor += opt.layers;
                        remaining -= opt.layers;
                    }

                    layer = runStart + runLen;
                }
            }
        }
    }

    // Greedy rectangle cover: scans left-to-right / top-to-bottom and places the
    // largest standard plate whose footprint fits entirely in unoccupied cells.
    // The top-left cell's color is used for the whole merged plate.
    static void MergeLayer(
        int layer, int[,] colorGrid, int gridX, int gridZ,
        float gridOriginX, float gridOriginZ,
        float yBase, float centerOffsetX, float centerOffsetZ, float yOffset,
        float pitchX, float pitchZ, float plateStep,
        Vector3 meshScale,
        PlacementOption[] options,
        string ldrawRootPath,
        Dictionary<string, BasePartMetrics> partMetricsCache,
        List<LDrawModelAsset.Brick> bricks,
        HashSet<string> uniqueParts)
    {
        var covered = new bool[gridX, gridZ];

        for (int ix = 0; ix < gridX; ix++)
        {
            for (int iz = 0; iz < gridZ; iz++)
            {
                if (colorGrid[ix, iz] < 0 || covered[ix, iz])
                    continue;

                int color = colorGrid[ix, iz];
                bool placed = false;

                foreach (var opt in options)
                {
                    if (ix + opt.w > gridX || iz + opt.d > gridZ)
                        continue;

                    bool fits = true;
                    for (int dx = 0; dx < opt.w && fits; dx++)
                        for (int dz = 0; dz < opt.d && fits; dz++)
                            if (colorGrid[ix + dx, iz + dz] < 0 || covered[ix + dx, iz + dz] || colorGrid[ix + dx, iz + dz] != color)
                                fits = false;

                    if (!fits)
                        continue;

                    for (int dx = 0; dx < opt.w; dx++)
                        for (int dz = 0; dz < opt.d; dz++)
                            covered[ix + dx, iz + dz] = true;

                    if (!partMetricsCache.TryGetValue(opt.part, out var partMetrics))
                    {
                        partMetrics = GetPartMetrics(ldrawRootPath, opt.part);
                        partMetricsCache[opt.part] = partMetrics;
                    }

                    AddPlate(ix, iz, opt.w, opt.d, layer, opt.part, opt.rot90, color,
                        gridOriginX, gridOriginZ, yBase, centerOffsetX, centerOffsetZ, yOffset,
                        pitchX, pitchZ, plateStep, meshScale, partMetrics, PlateTotalHeight, bricks, uniqueParts);
                    placed = true;
                    break;
                }

                if (!placed)
                {
                    if (!partMetricsCache.TryGetValue(BasePart, out var baseMetrics))
                    {
                        baseMetrics = GetPartMetrics(ldrawRootPath, BasePart);
                        partMetricsCache[BasePart] = baseMetrics;
                    }

                    covered[ix, iz] = true;
                    AddPlate(ix, iz, 1, 1, layer, BasePart, false, color,
                        gridOriginX, gridOriginZ, yBase, centerOffsetX, centerOffsetZ, yOffset,
                        pitchX, pitchZ, plateStep, meshScale, baseMetrics, PlateTotalHeight, bricks, uniqueParts);
                }
            }
        }
    }

    static LDrawSettings FindSettings()
    {
        string[] guids = AssetDatabase.FindAssets("t:LDrawSettings");
        if (guids == null || guids.Length == 0)
            return null;

        string path = AssetDatabase.GUIDToAssetPath(guids[0]);
        return AssetDatabase.LoadAssetAtPath<LDrawSettings>(path);
    }

    static void BuildTriangleSampler(
        Mesh mesh,
        Vector3[] vertices,
        out List<TriSample> tris,
        out List<int>[] bins,
        out int binCountX,
        out int binCountZ,
        out float binSize,
        float gridMinX,
        float gridMinZ,
        float gridMaxX,
        float gridMaxZ,
        float requestedBinSize)
    {
        tris = new List<TriSample>(mesh.triangles.Length / 3);

        float localBinSize = Mathf.Max(StudPitch, requestedBinSize);
        int localBinCountX = Mathf.Max(1, Mathf.CeilToInt((gridMaxX - gridMinX) / localBinSize));
        int localBinCountZ = Mathf.Max(1, Mathf.CeilToInt((gridMaxZ - gridMinZ) / localBinSize));
        bins = new List<int>[localBinCountX * localBinCountZ];

        binSize = localBinSize;
        binCountX = localBinCountX;
        binCountZ = localBinCountZ;

        var verts = vertices ?? mesh.vertices;
        var triIndices = mesh.triangles;
        const float epsilon = 1e-6f;

        int ClampX(int x) => Mathf.Clamp(x, 0, localBinCountX - 1);
        int ClampZ(int z) => Mathf.Clamp(z, 0, localBinCountZ - 1);
        int BinIndex(int x, int z) => z * localBinCountX + x;

        for (int i = 0; i < triIndices.Length; i += 3)
        {
            Vector3 a = verts[triIndices[i + 0]];
            Vector3 b = verts[triIndices[i + 1]];
            Vector3 c = verts[triIndices[i + 2]];

            float ax = a.x, az = a.z;
            float bx = b.x, bz = b.z;
            float cx = c.x, cz = c.z;

            float denom = ((bz - cz) * (ax - cx) + (cx - bx) * (az - cz));
            if (Mathf.Abs(denom) < epsilon)
                continue;

            float triMinX = Mathf.Min(ax, Mathf.Min(bx, cx));
            float triMaxX = Mathf.Max(ax, Mathf.Max(bx, cx));
            float triMinZ = Mathf.Min(az, Mathf.Min(bz, cz));
            float triMaxZ = Mathf.Max(az, Mathf.Max(bz, cz));

            int triId = tris.Count;
            tris.Add(new TriSample
            {
                a = a,
                b = b,
                c = c,
                minX = triMinX,
                maxX = triMaxX,
                minZ = triMinZ,
                maxZ = triMaxZ,
                denom = denom,
            });

            int x0 = ClampX(Mathf.FloorToInt((triMinX - gridMinX) / binSize));
            int x1 = ClampX(Mathf.FloorToInt((triMaxX - gridMinX) / binSize));
            int z0 = ClampZ(Mathf.FloorToInt((triMinZ - gridMinZ) / binSize));
            int z1 = ClampZ(Mathf.FloorToInt((triMaxZ - gridMinZ) / binSize));

            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    int bi = BinIndex(x, z);
                    bins[bi] ??= new List<int>(16);
                    bins[bi].Add(triId);
                }
            }
        }
    }

    static bool TrySampleHeight(
        float x,
        float z,
        float minX,
        float minZ,
        List<TriSample> tris,
        List<int>[] bins,
        int binCountX,
        int binCountZ,
        float binSize,
        out float y)
    {
        y = float.MinValue;
        int binX = Mathf.Clamp(Mathf.FloorToInt((x - minX) / binSize), 0, binCountX - 1);
        int binZ = Mathf.Clamp(Mathf.FloorToInt((z - minZ) / binSize), 0, binCountZ - 1);

        float bestY = float.MinValue;
        const float edgeEpsilon = 1e-4f;

        void TestBin(int bx, int bz)
        {
            if (bx < 0 || bx >= binCountX || bz < 0 || bz >= binCountZ)
                return;

            var list = bins[bz * binCountX + bx];
            if (list == null)
                return;

            for (int i = 0; i < list.Count; i++)
            {
                var t = tris[list[i]];
                if (x < t.minX - edgeEpsilon || x > t.maxX + edgeEpsilon || z < t.minZ - edgeEpsilon || z > t.maxZ + edgeEpsilon)
                    continue;

                float u = ((t.b.z - t.c.z) * (x - t.c.x) + (t.c.x - t.b.x) * (z - t.c.z)) / t.denom;
                float v = ((t.c.z - t.a.z) * (x - t.c.x) + (t.a.x - t.c.x) * (z - t.c.z)) / t.denom;
                float w = 1f - u - v;

                if (u < -edgeEpsilon || v < -edgeEpsilon || w < -edgeEpsilon)
                    continue;

                float ty = u * t.a.y + v * t.b.y + w * t.c.y;
                if (ty > bestY)
                    bestY = ty;
            }
        }

        // Test local bin first, then neighbors for edge continuity.
        TestBin(binX, binZ);
        if (bestY == float.MinValue)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (!(dx == 0 && dz == 0))
                        TestBin(binX + dx, binZ + dz);
        }

        if (bestY == float.MinValue)
            return false;

        y = bestY;
        return true;
    }

    // Maps height → palette color with Perlin noise variation.
    // Noise is quantized to 4-stud tiles so cells within the same tile share the same color code.
    // This lets the greedy rectangle merger produce large same-color plates on flat terrain.
    static int ComputeTerrainColor(int ix, int iz, int h, int maxHeight, int baseColor, bool procedural, float noiseScale, float noiseStrength, int[] palette, int seaLevelBias = 50, int quantBlock = 4)
    {
        if (!procedural || maxHeight <= 0 || palette == null || palette.Length == 0)
            return baseColor;

        float t        = Mathf.Clamp01((float)h / maxHeight);
        // Apply sea level bias: shifts sand/grass transition by (bias - 50) * 0.01 in normalized height space
        float biasShift = (seaLevelBias - 50f) * 0.01f;
        t = Mathf.Clamp01(t + biasShift);
        
        // Snap to quantBlock grid so cells in the same tile share noise → same color code.
        float noiseIx  = (float)(ix / quantBlock);
        float noiseIz  = (float)(iz / quantBlock);
        float noise    = Mathf.PerlinNoise(noiseIx * noiseScale + 73.4f, noiseIz * noiseScale + 31.7f);
        float combined = Mathf.Clamp01(t + (noise * 2f - 1f) * noiseStrength);

        int palIdx = Mathf.Clamp(Mathf.FloorToInt(combined * palette.Length), 0, palette.Length - 1);
        return palette[palIdx];
    }

    static int ComputeSlopeSupportDepth(int[,] heightGrid, int x, int z, int maxSupportLayers)
    {
        int h = heightGrid[x, z];
        if (h <= 1)
            return 0;

        int maxDrop = 0;
        int gridX = heightGrid.GetLength(0);
        int gridZ = heightGrid.GetLength(1);

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0)
                    continue;

                int nx = x + dx;
                int nz = z + dz;
                int nh;
                if (nx < 0 || nx >= gridX || nz < 0 || nz >= gridZ)
                {
                    // Treat mesh boundary as open air/ground so edge cliffs stack fully.
                    nh = 0;
                }
                else
                {
                    nh = heightGrid[nx, nz];
                }

                int drop = h - nh;
                if (drop > maxDrop)
                    maxDrop = drop;
            }
        }

        // top layer already exists, so only fill remaining exposed drop.
        int extra = Mathf.Max(0, maxDrop - 1);
        if (maxSupportLayers <= 0)
            return extra;

        return Mathf.Min(extra, maxSupportLayers);
    }

    static BasePartMetrics GetBasePartMetrics(string ldrawRootPath)
        => GetPartMetrics(ldrawRootPath, BasePart);

    static BasePartMetrics GetPartMetrics(string ldrawRootPath, string partName)
    {
        var result = new BasePartMetrics
        {
            pitchX = StudPitch,
            pitchZ = StudPitch,
            height = PlateHeight,
            anchorOffset = Vector3.zero,
        };

        string meshPath = PartsCacheFolder + Path.GetFileNameWithoutExtension(partName) + ".asset";
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);

        if (mesh == null && !string.IsNullOrWhiteSpace(ldrawRootPath))
        {
            var baked = DatMeshBuilder.BuildMeshForFile(ldrawRootPath, partName);
            if (baked != null)
                mesh = baked;
        }

        if (mesh == null)
            return result;

        Bounds b = mesh.bounds;
        result.pitchX = Mathf.Max(0.0001f, b.size.x);
        result.pitchZ = Mathf.Max(0.0001f, b.size.z);
        result.height = Mathf.Max(0.0001f, b.size.y);
        result.anchorOffset = new Vector3(-b.center.x, -b.max.y, -b.center.z);
        return result;
    }

    static void FillSamplingHoles(int[,] heightGrid, int passes)
    {
        if (heightGrid == null || passes <= 0)
            return;

        int gridX = heightGrid.GetLength(0);
        int gridZ = heightGrid.GetLength(1);
        if (gridX <= 0 || gridZ <= 0)
            return;

        var scratch = new int[gridX, gridZ];

        for (int pass = 0; pass < passes; pass++)
        {
            Array.Copy(heightGrid, scratch, heightGrid.Length);
            bool changed = false;

            for (int x = 0; x < gridX; x++)
            {
                for (int z = 0; z < gridZ; z++)
                {
                    if (heightGrid[x, z] > 0)
                        continue;

                    int sum = 0;
                    int count = 0;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (dx == 0 && dz == 0)
                                continue;
                            int nx = x + dx;
                            int nz = z + dz;
                            if (nx < 0 || nx >= gridX || nz < 0 || nz >= gridZ)
                                continue;

                            int h = heightGrid[nx, nz];
                            if (h <= 0)
                                continue;

                            sum += h;
                            count++;
                        }
                    }

                    // Fill isolated misses only when neighborhood strongly agrees.
                    if (count >= 5)
                    {
                        scratch[x, z] = Mathf.Max(1, Mathf.RoundToInt((float)sum / count));
                        changed = true;
                    }
                }
            }

            Array.Copy(scratch, heightGrid, heightGrid.Length);
            if (!changed)
                break;
        }
    }
}
#endif
