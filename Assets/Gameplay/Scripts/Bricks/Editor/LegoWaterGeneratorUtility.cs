#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class LegoWaterGeneratorUtility
{
    const float StudPitch = 1.0f;
    const float PlateTotalHeight = 0.6f;
    const string BasePart = "3024.dat";
    const string Part2x2 = "3022.dat";
    const string Part4x4 = "3031.dat";
    const string OutputFolder = "Assets/Content/Bricks/BrickData/Water/";
    static readonly Collider[] OverlapHits = new Collider[32];

    struct PartMetrics
    {
        public float pitchX;
        public float pitchZ;
        public float height;
        public Vector3 bottomAnchorOffset;
    }

    public static void GenerateWater(LegoWaterGenerator g)
    {
        if (g == null)
            return;

        if (!ValidateInputs(g, out var registry, out var ldrawRootPath))
            return;

        // Feature retired: remove any legacy infinite ocean object.
        RemoveLegacyInfiniteOcean(g);

        if (g.clearBeforeGenerate)
            ClearWater(g, g.deleteGeneratedAssetOnClear);

        g.terrainStructure.EnsureInitialized();
        var terrainModel = g.terrainStructure.model;
        if (terrainModel == null || !terrainModel.HasGpuData || terrainModel.sortedBricks == null || terrainModel.sortedBricks.Length == 0)
        {
            Debug.LogError("LegoWaterGenerator: terrain structure has no GPU data.");
            return;
        }

        var partMetricsCache = new Dictionary<string, PartMetrics>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetPartMetrics(ldrawRootPath, BasePart, partMetricsCache, out var baseMetrics))
        {
            Debug.LogError("LegoWaterGenerator: could not resolve base part metrics for 3024.dat.");
            return;
        }

        Vector3 meshScale = new Vector3(
            StudPitch / Mathf.Max(0.0001f, baseMetrics.pitchX),
            PlateTotalHeight / Mathf.Max(0.0001f, baseMetrics.height),
            StudPitch / Mathf.Max(0.0001f, baseMetrics.pitchZ));

        var topByCell = BuildSurfaceHeightMap(terrainModel, registry, g.terrainStructure.aliveFlags);
        if (topByCell.Count == 0)
        {
            Debug.LogWarning("LegoWaterGenerator: terrain cell map empty.");
            return;
        }

        GetBoundsAndHeight(topByCell, out int minX, out int maxX, out int minZ, out int maxZ, out float minY, out float maxY);
        float seaLevelT = Mathf.Clamp01(GetSeaLevelBias(g) / 100f);
        float seaY = Mathf.Lerp(minY, maxY, seaLevelT) + g.waterYOffset;

        int pad = Mathf.Max(0, g.waterPaddingCells);
        minX -= pad; maxX += pad; minZ -= pad; maxZ += pad;

        int sizeX = maxX - minX + 1;
        int sizeZ = maxZ - minZ + 1;
        var waterColorGrid = new int[sizeX, sizeZ];
        for (int x = 0; x < sizeX; x++)
            for (int z = 0; z < sizeZ; z++)
                waterColorGrid[x, z] = -1;

        // Build water occupancy/colors by excluding cells where terrain is clearly above sea.
        for (int gx = minX; gx <= maxX; gx++)
        {
            for (int gz = minZ; gz <= maxZ; gz++)
            {
                var key = new Vector2Int(gx, gz);
                bool hasTerrain = topByCell.TryGetValue(key, out float terrainTopY);
                bool isLandAboveSea = hasTerrain && terrainTopY > seaY + 0.05f;
                if (isLandAboveSea)
                    continue;

                int lx = gx - minX;
                int lz = gz - minZ;
                int color = PickWaterColor(g, topByCell, gx, gz, seaY, hasTerrain ? terrainTopY : minY, seaLevelT);
                waterColorGrid[lx, lz] = color;
            }
        }

        if (g.enableFoam)
            ApplyFoamMask(g, topByCell, waterColorGrid, minX, minZ, seaY);

        var bricks = new List<LDrawModelAsset.Brick>(sizeX * sizeZ / 2);
        var uniqueParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (g.mixedTiles)
            EmitMixedTiles(g, waterColorGrid, minX, minZ, seaY, meshScale, partMetricsCache, ldrawRootPath, bricks, uniqueParts);
        else
            EmitSingleTiles(waterColorGrid, minX, minZ, seaY, meshScale, partMetricsCache, ldrawRootPath, bricks, uniqueParts);

        if (bricks.Count == 0)
        {
            Debug.LogWarning("LegoWaterGenerator: no water bricks generated.");
            return;
        }

        var waterModel = LDrawModelImporter.BuildModelFromBrickList(bricks, uniqueParts, ldrawRootPath);
        if (waterModel == null)
        {
            Debug.LogError("LegoWaterGenerator: failed to build water model asset.");
            return;
        }

        Directory.CreateDirectory(OutputFolder);
        string terrainName = g.terrainStructure != null ? g.terrainStructure.name : "Terrain";
        string path = OutputFolder + SanitizeFileName(terrainName + "_Water.asset");

        if (!string.IsNullOrWhiteSpace(g.generatedAssetPath) && AssetDatabase.LoadAssetAtPath<LDrawModelAsset>(g.generatedAssetPath) != null)
            AssetDatabase.DeleteAsset(g.generatedAssetPath);
        else if (AssetDatabase.LoadAssetAtPath<LDrawModelAsset>(path) != null)
            AssetDatabase.DeleteAsset(path);

        AssetDatabase.CreateAsset(waterModel, path);
        g.generatedAssetPath = path;

        var go = GetOrCreateWaterObject(g);
        var renderer = go.GetComponent<LDrawModelRenderer>();
        if (renderer == null)
            renderer = go.AddComponent<LDrawModelRenderer>();

        renderer.model = waterModel;
        renderer.parts = registry;
        renderer.brickMaterial = ResolveWaterMaterial(g);
        renderer.paletteTexture = ResolvePaletteTexture(g);

        var animator = go.GetComponent<LegoWaterAnimator>();
        if (animator == null)
            animator = go.AddComponent<LegoWaterAnimator>();
        CopyAnimationSettings(g, animator);

        EditorUtility.SetDirty(g);
        EditorUtility.SetDirty(renderer);
        EditorUtility.SetDirty(animator);
        EditorUtility.SetDirty(waterModel);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(g.gameObject.scene);

        // Auto-configure underwater culling on BrickRenderSystem to match sea level
        var renderSystem = UnityEngine.Object.FindFirstObjectByType<BrickRenderSystem>();
        if (renderSystem != null)
        {
            renderSystem.enableUnderwaterCull = true;
            renderSystem.underwaterCullY = seaY;
            EditorUtility.SetDirty(renderSystem);
        }

        Debug.Log($"LegoWaterGenerator: generated {bricks.Count} bricks at seaY={seaY:F2} -> {path}");
    }

    public static void ClearWater(LegoWaterGenerator g, bool deleteAsset)
    {
        if (g == null)
            return;

        RemoveLegacyInfiniteOcean(g);

        Transform root = g.environmentRoot != null ? g.environmentRoot : g.transform;
        if (g.waterObject == null)
            g.waterObject = FindChildByName(root, g.waterObjectName);

        if (g.waterObject != null)
        {
            Undo.DestroyObjectImmediate(g.waterObject);
            g.waterObject = null;
        }

        if (deleteAsset && !string.IsNullOrWhiteSpace(g.generatedAssetPath))
        {
            if (AssetDatabase.LoadAssetAtPath<LDrawModelAsset>(g.generatedAssetPath) != null)
                AssetDatabase.DeleteAsset(g.generatedAssetPath);
            g.generatedAssetPath = string.Empty;
        }

        EditorUtility.SetDirty(g);
        EditorSceneManager.MarkSceneDirty(g.gameObject.scene);
    }

    public static void RegenerateSeedAndGenerate(LegoWaterGenerator g)
    {
        if (g == null)
            return;

        g.seed = unchecked(g.seed * 1664525 + 1013904223);
        if (g.seed == 0)
            g.seed = 24601;
        GenerateWater(g);
    }

    static void EmitMixedTiles(
        LegoWaterGenerator g,
        int[,] colorGrid,
        int minX,
        int minZ,
        float seaY,
        Vector3 meshScale,
        Dictionary<string, PartMetrics> cache,
        string ldrawRootPath,
        List<LDrawModelAsset.Brick> bricks,
        HashSet<string> uniqueParts)
    {
        int sx = colorGrid.GetLength(0);
        int sz = colorGrid.GetLength(1);
        bool[,] used = new bool[sx, sz];

        for (int z = 0; z < sz; z++)
        {
            for (int x = 0; x < sx; x++)
            {
                if (used[x, z] || colorGrid[x, z] < 0)
                    continue;

                int color = colorGrid[x, z];
                int size = 1;

                if (CanPlaceSquare(colorGrid, used, x, z, 4, color) && Hash01(HashCell(x + minX, z + minZ, g.seed ^ 0x13579BDF)) <= g.tile4x4Chance)
                    size = 4;
                else if (CanPlaceSquare(colorGrid, used, x, z, 2, color) && Hash01(HashCell(x + minX, z + minZ, g.seed ^ 0x2468ACE1)) <= g.tile2x2Chance)
                    size = 2;

                string part = size == 4 ? Part4x4 : (size == 2 ? Part2x2 : BasePart);
                if (!TryGetPartMetrics(ldrawRootPath, part, cache, out var metrics))
                    metrics = cache[BasePart];

                for (int dz = 0; dz < size; dz++)
                    for (int dx = 0; dx < size; dx++)
                        used[x + dx, z + dz] = true;

                float px = (minX + x) + size * 0.5f;
                float pz = (minZ + z) + size * 0.5f;
                var local = Matrix4x4.TRS(new Vector3(px, seaY, pz), Quaternion.identity, Vector3.one)
                          * Matrix4x4.Scale(meshScale)
                          * Matrix4x4.Translate(metrics.bottomAnchorOffset);

                bricks.Add(new LDrawModelAsset.Brick { part = part, colorCode = color, local = local });
                uniqueParts.Add(part);
            }
        }
    }

    static void EmitSingleTiles(
        int[,] colorGrid,
        int minX,
        int minZ,
        float seaY,
        Vector3 meshScale,
        Dictionary<string, PartMetrics> cache,
        string ldrawRootPath,
        List<LDrawModelAsset.Brick> bricks,
        HashSet<string> uniqueParts)
    {
        if (!TryGetPartMetrics(ldrawRootPath, BasePart, cache, out var baseMetrics))
            return;

        int sx = colorGrid.GetLength(0);
        int sz = colorGrid.GetLength(1);
        for (int z = 0; z < sz; z++)
        {
            for (int x = 0; x < sx; x++)
            {
                int color = colorGrid[x, z];
                if (color < 0)
                    continue;

                float px = (minX + x) + 0.5f;
                float pz = (minZ + z) + 0.5f;
                var local = Matrix4x4.TRS(new Vector3(px, seaY, pz), Quaternion.identity, Vector3.one)
                          * Matrix4x4.Scale(meshScale)
                          * Matrix4x4.Translate(baseMetrics.bottomAnchorOffset);

                bricks.Add(new LDrawModelAsset.Brick { part = BasePart, colorCode = color, local = local });
                uniqueParts.Add(BasePart);
            }
        }
    }

    static bool CanPlaceSquare(int[,] colors, bool[,] used, int x, int z, int size, int color)
    {
        int sx = colors.GetLength(0);
        int sz = colors.GetLength(1);
        if (x + size > sx || z + size > sz)
            return false;

        for (int dz = 0; dz < size; dz++)
        {
            for (int dx = 0; dx < size; dx++)
            {
                if (used[x + dx, z + dz]) return false;
                if (colors[x + dx, z + dz] != color) return false;
            }
        }

        return true;
    }

    static void ApplyFoamMask(LegoWaterGenerator g, Dictionary<Vector2Int, float> topByCell, int[,] colorGrid, int minX, int minZ, float seaY)
    {
        int sx = colorGrid.GetLength(0);
        int sz = colorGrid.GetLength(1);

        for (int z = 0; z < sz; z++)
        {
            for (int x = 0; x < sx; x++)
            {
                if (colorGrid[x, z] < 0)
                    continue;

                int gx = minX + x;
                int gz = minZ + z;
                bool shoreline = IsShorelineCell(topByCell, gx, gz, seaY);

                if (!shoreline && g.overlapFoamRefine)
                    shoreline = IsOverlapFoamCell(g, gx, gz, seaY);

                if (shoreline)
                    colorGrid[x, z] = g.foamColorCode;
            }
        }
    }

    static bool IsShorelineCell(Dictionary<Vector2Int, float> topByCell, int x, int z, float seaY)
    {
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                var n = new Vector2Int(x + dx, z + dz);
                if (topByCell.TryGetValue(n, out float y) && y > seaY + 0.05f)
                    return true;
            }
        }
        return false;
    }

    static bool IsOverlapFoamCell(LegoWaterGenerator g, int cellX, int cellZ, float seaY)
    {
        if (!g.overlapFoamRefine || g.terrainStructure == null)
            return false;

        Vector3 local = new Vector3(cellX + 0.5f, seaY + g.overlapFoamHeight * 0.5f, cellZ + 0.5f);
        Vector3 world = g.terrainStructure.transform.TransformPoint(local);

        int hits = Physics.OverlapBoxNonAlloc(
            world,
            new Vector3(g.overlapFoamRadius, g.overlapFoamHeight * 0.5f, g.overlapFoamRadius),
            OverlapHits,
            Quaternion.identity,
            g.overlapFoamMask,
            QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hits; i++)
        {
            var c = OverlapHits[i];
            if (c == null) continue;
            if (c.transform.IsChildOf(g.terrainStructure.transform))
                continue;
            if (g.waterObject != null && c.transform.IsChildOf(g.waterObject.transform))
                continue;
            return true;
        }

        return false;
    }

    static int PickWaterColor(LegoWaterGenerator g, Dictionary<Vector2Int, float> topByCell, int gx, int gz, float seaY, float terrainTopY, float seaLevelT)
    {
        float depth = Mathf.Max(0f, seaY - terrainTopY);
        if (depth > g.shallowDepthStuds)
            return g.deepWaterColorCode;
        return g.shallowWaterColorCode;
    }

    static Dictionary<Vector2Int, float> BuildSurfaceHeightMap(LDrawModelAsset model, LDrawPartRegistry registry, byte[] aliveFlags)
    {
        var topByCell = new Dictionary<Vector2Int, float>(8192);
        var bricks = model.sortedBricks;

        for (int i = 0; i < bricks.Length; i++)
        {
            if (aliveFlags != null && i < aliveFlags.Length && aliveFlags[i] == 0)
                continue;

            Mesh mesh = registry.GetMeshById(bricks[i].partId);
            if (mesh == null)
                continue;

            GetTransformedBounds(bricks[i].localTransform, mesh.bounds, out Vector3 min, out Vector3 max);
            int minX = Mathf.FloorToInt(min.x + 0.001f);
            int maxX = Mathf.CeilToInt(max.x - 0.001f) - 1;
            int minZ = Mathf.FloorToInt(min.z + 0.001f);
            int maxZ = Mathf.CeilToInt(max.z - 0.001f) - 1;

            if (maxX < minX || maxZ < minZ)
            {
                int cx = Mathf.FloorToInt(bricks[i].localTransform.GetColumn(3).x);
                int cz = Mathf.FloorToInt(bricks[i].localTransform.GetColumn(3).z);
                var k = new Vector2Int(cx, cz);
                if (!topByCell.TryGetValue(k, out float oldY) || max.y > oldY)
                    topByCell[k] = max.y;
                continue;
            }

            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    var k = new Vector2Int(x, z);
                    if (!topByCell.TryGetValue(k, out float oldY) || max.y > oldY)
                        topByCell[k] = max.y;
                }
            }
        }

        return topByCell;
    }

    static void GetBoundsAndHeight(Dictionary<Vector2Int, float> topByCell, out int minX, out int maxX, out int minZ, out int maxZ, out float minY, out float maxY)
    {
        minX = int.MaxValue; maxX = int.MinValue;
        minZ = int.MaxValue; maxZ = int.MinValue;
        minY = float.MaxValue; maxY = float.MinValue;

        foreach (var kv in topByCell)
        {
            minX = Mathf.Min(minX, kv.Key.x);
            maxX = Mathf.Max(maxX, kv.Key.x);
            minZ = Mathf.Min(minZ, kv.Key.y);
            maxZ = Mathf.Max(maxZ, kv.Key.y);
            minY = Mathf.Min(minY, kv.Value);
            maxY = Mathf.Max(maxY, kv.Value);
        }
    }

    static bool ValidateInputs(LegoWaterGenerator g, out LDrawPartRegistry registry, out string ldrawRootPath)
    {
        registry = g.partRegistry;
        ldrawRootPath = FindLDrawRootPath();

        if (g.terrainStructure == null)
            g.terrainStructure = g.GetComponentInParent<BrickStructure>();
        if (g.environmentRoot == null)
            g.environmentRoot = g.transform;

        if (g.terrainStructure == null)
        {
            Debug.LogError("LegoWaterGenerator: assign Terrain Structure.");
            return false;
        }

        if (registry == null)
            registry = g.terrainStructure.parts;
        if (registry == null)
            registry = FindPartRegistry();
        if (registry == null)
        {
            Debug.LogError("LegoWaterGenerator: assign Part Registry.");
            return false;
        }
        g.partRegistry = registry;

        if (string.IsNullOrWhiteSpace(ldrawRootPath))
        {
            Debug.LogError("LegoWaterGenerator: missing LDrawSettings.ldrawRootPath.");
            return false;
        }

        return true;
    }

    static int GetSeaLevelBias(LegoWaterGenerator g)
    {
        if (g.useTerrainSeaLevel && g.terrainStructure != null)
        {
            var adjuster = g.terrainStructure.GetComponent<TerrainColorAdjuster>();
            if (adjuster != null)
                return adjuster.seaLevelBias;
        }
        return g.seaLevelBias;
    }

    static Material ResolveWaterMaterial(LegoWaterGenerator g)
    {
        if (g.waterMaterial != null)
            return g.waterMaterial;

        Shader s = Shader.Find("Bricks/LDrawURP_Opaque");
        if (s != null)
            return new Material(s) { name = "LegoWater_RuntimeMat" };

        return null;
    }

    static Texture2D ResolvePaletteTexture(LegoWaterGenerator g)
    {
        if (g.paletteTexture != null)
            return g.paletteTexture;

        var rs = UnityEngine.Object.FindFirstObjectByType<BrickRenderSystem>();
        if (rs != null)
            return rs.paletteTexture;

        return null;
    }

    static void CopyAnimationSettings(LegoWaterGenerator g, LegoWaterAnimator animator)
    {
        animator.animateWater = g.animateWater;
        animator.globalBobAmplitude = g.globalBobAmplitude;
        animator.globalBobFrequency = g.globalBobFrequency;
        animator.waveAmplitudePrimary = g.waveAmplitudePrimary;
        animator.waveFrequencyPrimary = g.waveFrequencyPrimary;
        animator.waveSpeedPrimary = g.waveSpeedPrimary;
        animator.waveAmplitudeSecondary = g.waveAmplitudeSecondary;
        animator.waveFrequencySecondary = g.waveFrequencySecondary;
        animator.waveSpeedSecondary = g.waveSpeedSecondary;
    }

    static bool TryGetPartMetrics(string ldrawRootPath, string part, Dictionary<string, PartMetrics> cache, out PartMetrics metrics)
    {
        string key = NormalizePart(part);
        if (cache.TryGetValue(key, out metrics))
            return true;

        Mesh mesh = DatMeshBuilder.BuildMeshForFile(ldrawRootPath, key);
        if (mesh == null)
            return false;

        Bounds b = mesh.bounds;
        metrics = new PartMetrics
        {
            pitchX = b.size.x,
            pitchZ = b.size.z,
            height = b.size.y,
            bottomAnchorOffset = new Vector3(-b.center.x, -b.min.y, -b.center.z)
        };
        cache[key] = metrics;
        return true;
    }

    static void GetTransformedBounds(Matrix4x4 xf, Bounds b, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        Vector3 bMin = b.min;
        Vector3 bMax = b.max;

        for (int ix = 0; ix < 2; ix++)
        {
            for (int iy = 0; iy < 2; iy++)
            {
                for (int iz = 0; iz < 2; iz++)
                {
                    Vector3 corner = new Vector3(
                        ix == 0 ? bMin.x : bMax.x,
                        iy == 0 ? bMin.y : bMax.y,
                        iz == 0 ? bMin.z : bMax.z);
                    Vector3 p = xf.MultiplyPoint3x4(corner);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
        }
    }

    static GameObject GetOrCreateWaterObject(LegoWaterGenerator g)
    {
        Transform root = g.environmentRoot != null ? g.environmentRoot : g.transform;
        if (g.waterObject == null)
            g.waterObject = FindChildByName(root, g.waterObjectName);

        if (g.waterObject == null)
        {
            g.waterObject = new GameObject(g.waterObjectName);
            Undo.RegisterCreatedObjectUndo(g.waterObject, "Create Lego Water Object");
        }

        g.waterObject.transform.position = g.terrainStructure.transform.position;
        g.waterObject.transform.rotation = g.terrainStructure.transform.rotation;
        g.waterObject.transform.localScale = g.terrainStructure.transform.lossyScale;
        g.waterObject.transform.SetParent(root, true);

        return g.waterObject;
    }

    static string NormalizePart(string part)
    {
        if (string.IsNullOrWhiteSpace(part))
            return BasePart;
        return part.Replace('\\', '/').Trim();
    }

    static void RemoveLegacyInfiniteOcean(LegoWaterGenerator g)
    {
        if (g == null)
            return;

        Transform root = g.environmentRoot != null ? g.environmentRoot : g.transform;
        if (root == null)
            return;

        var ocean = root.Find("_InfiniteOcean");
        if (ocean != null)
            Undo.DestroyObjectImmediate(ocean.gameObject);
    }

    static GameObject FindChildByName(Transform root, string childName)
    {
        if (root == null)
            return null;

        for (int i = 0; i < root.childCount; i++)
        {
            var c = root.GetChild(i);
            if (string.Equals(c.name, childName, StringComparison.Ordinal))
                return c.gameObject;
        }
        return null;
    }

    static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    static string FindLDrawRootPath()
    {
        string[] guids = AssetDatabase.FindAssets("t:LDrawSettings");
        if (guids.Length == 0)
            return null;

        string assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
        var settingsAsset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
        if (settingsAsset == null)
            return null;

        var so = new SerializedObject(settingsAsset);
        var p = so.FindProperty("ldrawRootPath");
        return p != null ? p.stringValue : null;
    }

    static LDrawPartRegistry FindPartRegistry()
    {
        string[] guids = AssetDatabase.FindAssets("t:LDrawPartRegistry");
        if (guids.Length == 0)
            return null;
        return AssetDatabase.LoadAssetAtPath<LDrawPartRegistry>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    static uint HashCell(int x, int z, int s)
    {
        uint h = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ (uint)(s * 83492791);
        h ^= h >> 16;
        h *= 0x7feb352d;
        h ^= h >> 15;
        h *= 0x846ca68b;
        h ^= h >> 16;
        return h;
    }

    static float Hash01(uint h)
    {
        return (h & 0x00FFFFFF) / 16777215f;
    }
}
#endif
