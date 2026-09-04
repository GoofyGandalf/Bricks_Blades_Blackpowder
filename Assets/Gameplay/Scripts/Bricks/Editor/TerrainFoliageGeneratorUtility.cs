#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class TerrainFoliageGeneratorUtility
{
    const float StudPitch = 1.0f;
    const float PlateTotalHeight = 0.6f;
    const string BasePart = "3024.dat";
    const string OutputFolder = "Assets/Content/Bricks/BrickData/Foliage/";
    static readonly int[] GrassPalette = { 2, 10, 17, 288 };

    struct PartMetrics
    {
        public float pitchX;
        public float pitchZ;
        public float height;
        public Vector3 bottomAnchorOffset;
    }

    static readonly Collider[] BlockageHits = new Collider[64];

    public static void GenerateFoliage(TerrainFoliageGenerator generator)
    {
        if (generator == null)
            return;

        if (!ValidateInputs(generator, out var registry, out var ldrawRootPath))
            return;

        generator.terrainStructure.EnsureInitialized();
        var model = generator.terrainStructure.model;
        if (model == null || !model.HasGpuData || model.sortedBricks == null || model.sortedBricks.Length == 0)
        {
            Debug.LogError("TerrainFoliageGenerator: Terrain structure has no GPU brick data.");
            return;
        }

        if (generator.clearBeforeGenerate)
            ClearFoliage(generator, generator.deleteGeneratedAssetOnClear);

        var partMetricsCache = new Dictionary<string, PartMetrics>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetPartMetrics(ldrawRootPath, BasePart, partMetricsCache, out var baseMetrics))
        {
            Debug.LogError("TerrainFoliageGenerator: failed to read base part metrics (3024.dat).");
            return;
        }

        Vector3 meshScale = new Vector3(
            StudPitch / Mathf.Max(0.0001f, baseMetrics.pitchX),
            PlateTotalHeight / Mathf.Max(0.0001f, baseMetrics.height),
            StudPitch / Mathf.Max(0.0001f, baseMetrics.pitchZ));

        var topByCell = BuildSurfaceHeightMap(model, registry, generator.terrainStructure.aliveFlags);
        if (topByCell.Count == 0)
        {
            Debug.LogWarning("TerrainFoliageGenerator: could not derive terrain surface cells.");
            return;
        }

        float minY = float.MaxValue;
        float maxY = float.MinValue;
        foreach (var kv in topByCell)
        {
            minY = Mathf.Min(minY, kv.Value);
            maxY = Mathf.Max(maxY, kv.Value);
        }
        if (maxY <= minY)
            maxY = minY + 1f;

        float seaLevelT = Mathf.Clamp01(generator.seaLevelBias / 100f);

        var bricks = new List<LDrawModelAsset.Brick>(Mathf.CeilToInt(topByCell.Count * generator.placementDensity));
        var uniqueParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var placedCells = new HashSet<Vector2Int>();
        var placedByPart = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int fallbackMetricsCount = 0;
        int grassPlaced = 0;

        string grassPart = NormalizePart(generator.grassPart);
        bool canGenerateGrassCarpet = generator.generateGrassCarpet
                                   && generator.grassCoverage > 0f
                                   && !string.IsNullOrWhiteSpace(grassPart);

        PartMetrics grassMetrics = baseMetrics;
        if (canGenerateGrassCarpet && !TryGetPartMetrics(ldrawRootPath, grassPart, partMetricsCache, out grassMetrics))
        {
            grassMetrics = baseMetrics;
            Debug.LogWarning($"TerrainFoliageGenerator: grass part '{grassPart}' mesh metrics unavailable; using base metrics for placement.");
        }

        var candidates = new List<(Vector2Int cell, float y, uint orderHash)>(topByCell.Count);
        int orderSeed = generator.seed ^ unchecked((int)0x2C1B3C6D);
        foreach (var kv in topByCell)
        {
            uint h = HashCell(kv.Key.x, kv.Key.y, orderSeed);
            candidates.Add((kv.Key, kv.Value, h));
        }

        candidates.Sort((a, b) => a.orderHash.CompareTo(b.orderHash));

        int chanceSeed = generator.seed ^ unchecked((int)0x59D2F15B);
        int partSeed = generator.seed ^ unchecked((int)0x13FA7D91);
        int rotSeed = generator.seed ^ unchecked((int)0x74BEA2C3);

        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            float normH = Mathf.InverseLerp(minY, maxY, c.y);
            if (normH <= seaLevelT)
                continue;

            if (generator.avoidBlockedAreas && IsBlockedBySceneGeometry(generator, c.cell, c.y))
                continue;

            uint chanceHash = HashCell(c.cell.x, c.cell.y, chanceSeed);
            float chance = Hash01(chanceHash);
            if (chance > generator.placementDensity)
                continue;

            if (!HasSpacing(placedCells, c.cell, Mathf.Max(1, generator.minStudGap)))
                continue;

            uint partHash = HashCell(c.cell.x, c.cell.y, partSeed);
            string part = ChooseFoliagePart(generator.foliageParts, partHash);
            if (string.IsNullOrWhiteSpace(part))
                continue;

            if (!TryGetPartMetrics(ldrawRootPath, part, partMetricsCache, out var metrics))
            {
                // Keep configured part choice, but use base 1x1 metrics so placement still proceeds.
                metrics = baseMetrics;
                fallbackMetricsCount++;
            }

            uint rotHash = HashCell(c.cell.x, c.cell.y, rotSeed);
            int rotStep = (int)((rotHash >> 16) & 0x3u);
            float rotY = rotStep * 90f;

            float px = c.cell.x + 0.5f;
            float pz = c.cell.y + 0.5f;
            float py = c.y + generator.foliageBaseYOffset;

            var local = Matrix4x4.TRS(new Vector3(px, py, pz), Quaternion.Euler(0f, rotY, 0f), Vector3.one)
                      * Matrix4x4.Scale(meshScale)
                      * Matrix4x4.Translate(metrics.bottomAnchorOffset);

            bricks.Add(new LDrawModelAsset.Brick
            {
                part = part,
                colorCode = PickFoliageColor(part, partHash),
                local = local
            });
            uniqueParts.Add(part);
            placedCells.Add(c.cell);
            if (!placedByPart.TryGetValue(part, out int placedCount))
                placedCount = 0;
            placedByPart[part] = placedCount + 1;
        }

        if (canGenerateGrassCarpet)
        {
            int grassChanceSeed = generator.seed ^ unchecked((int)0x31F49A17);
            int grassRotSeed = generator.seed ^ unchecked((int)0x6A09E667);
            int grassColorSeed = generator.seed ^ unchecked((int)0xBB67AE85);

            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                float normH = Mathf.InverseLerp(minY, maxY, c.y);
                if (normH <= seaLevelT)
                    continue;

                if (generator.avoidBlockedAreas && IsBlockedBySceneGeometry(generator, c.cell, c.y))
                    continue;

                if (placedCells.Contains(c.cell))
                    continue;

                uint chanceHash = HashCell(c.cell.x, c.cell.y, grassChanceSeed);
                if (Hash01(chanceHash) > generator.grassCoverage)
                    continue;

                uint rotHash = HashCell(c.cell.x, c.cell.y, grassRotSeed);
                int rotStep = (int)((rotHash >> 16) & 0x3u);
                float rotY = rotStep * 90f;

                float px = c.cell.x + 0.5f;
                float pz = c.cell.y + 0.5f;
                float py = c.y - generator.grassStudSink + generator.grassBaseYOffset;

                uint colorHash = HashCell(c.cell.x, c.cell.y, grassColorSeed);
                int grassColor = PickGrassColor(normH, seaLevelT, colorHash, generator.grassNoiseScale, generator.grassNoiseStrength);

                var local = Matrix4x4.TRS(new Vector3(px, py, pz), Quaternion.Euler(0f, rotY, 0f), Vector3.one)
                          * Matrix4x4.Scale(meshScale)
                          * Matrix4x4.Translate(grassMetrics.bottomAnchorOffset);

                bricks.Add(new LDrawModelAsset.Brick
                {
                    part = grassPart,
                    colorCode = grassColor,
                    local = local
                });
                uniqueParts.Add(grassPart);
                placedCells.Add(c.cell);

                if (!placedByPart.TryGetValue(grassPart, out int grassCountByPart))
                    grassCountByPart = 0;
                placedByPart[grassPart] = grassCountByPart + 1;
                grassPlaced++;
            }
        }

        if (bricks.Count == 0)
        {
            Debug.LogWarning("TerrainFoliageGenerator: no foliage placements were generated. Increase density or lower sea level.");
            return;
        }

        var foliageModel = LDrawModelImporter.BuildModelFromBrickList(bricks, uniqueParts, ldrawRootPath);
        if (foliageModel == null)
        {
            Debug.LogError("TerrainFoliageGenerator: failed to build foliage model.");
            return;
        }

        Directory.CreateDirectory(OutputFolder);
        string terrainName = generator.terrainStructure != null ? generator.terrainStructure.name : "Terrain";
        string fileName = SanitizeFileName(terrainName + "_Foliage.asset");
        string path = OutputFolder + fileName;

        if (!string.IsNullOrWhiteSpace(generator.generatedAssetPath) && AssetDatabase.LoadAssetAtPath<LDrawModelAsset>(generator.generatedAssetPath) != null)
            AssetDatabase.DeleteAsset(generator.generatedAssetPath);
        else if (AssetDatabase.LoadAssetAtPath<LDrawModelAsset>(path) != null)
            AssetDatabase.DeleteAsset(path);

        AssetDatabase.CreateAsset(foliageModel, path);
        generator.generatedAssetPath = path;

        var go = GetOrCreateFoliageObject(generator);

        // Strip BrickColliderManager + its collider container BEFORE touching BrickStructure,
        // so that setting autoAddColliders = false takes effect before any OnEnable fires.
        var oldColliders = go.GetComponent<BrickColliderManager>();
        if (oldColliders != null)
            UnityEngine.Object.DestroyImmediate(oldColliders);
        // Also destroy the "_BrickColliders" child container if it was left behind
        var colContainer = go.transform.Find("_BrickColliders");
        if (colContainer != null)
            UnityEngine.Object.DestroyImmediate(colContainer.gameObject);

        var bs = go.GetComponent<BrickStructure>();
        if (bs == null)
            bs = go.AddComponent<BrickStructure>();

        bs.autoAddColliders = false;   // must be set before model assignment triggers any rebuild
        bs.model = foliageModel;
        bs.parts = registry;

        bs.aliveFlags = null;
        bs.worldTransforms = null;
        bs.damageVersion++;
        bs.EnsureInitialized();

        EditorUtility.SetDirty(generator);
        EditorUtility.SetDirty(bs);
        EditorUtility.SetDirty(foliageModel);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(generator.gameObject.scene);

        string summary = BuildPartSummary(placedByPart);
        Debug.Log($"TerrainFoliageGenerator: part distribution -> {summary}");
        Debug.Log($"TerrainFoliageGenerator: grass carpet placements -> {grassPlaced}");
        if (fallbackMetricsCount > 0)
            Debug.LogWarning($"TerrainFoliageGenerator: used base placement metrics fallback for {fallbackMetricsCount} placements.");
        Debug.Log($"TerrainFoliageGenerator: generated {bricks.Count} foliage bricks -> {path}");
    }

    public static void ClearFoliage(TerrainFoliageGenerator generator, bool deleteAsset)
    {
        if (generator == null)
            return;

        var root = generator.environmentRoot != null ? generator.environmentRoot : generator.transform;

        if (generator.foliageObject == null)
            generator.foliageObject = FindChildByName(root, generator.foliageObjectName);

        if (generator.foliageObject != null)
        {
            Undo.DestroyObjectImmediate(generator.foliageObject);
            generator.foliageObject = null;
        }

        if (deleteAsset && !string.IsNullOrWhiteSpace(generator.generatedAssetPath))
        {
            if (AssetDatabase.LoadAssetAtPath<LDrawModelAsset>(generator.generatedAssetPath) != null)
                AssetDatabase.DeleteAsset(generator.generatedAssetPath);
            generator.generatedAssetPath = string.Empty;
        }

        EditorUtility.SetDirty(generator);
        EditorSceneManager.MarkSceneDirty(generator.gameObject.scene);
    }

    public static void RegenerateSeedAndGenerate(TerrainFoliageGenerator generator)
    {
        if (generator == null)
            return;

        generator.seed = unchecked(generator.seed * 1664525 + 1013904223);
        if (generator.seed == 0)
            generator.seed = 12345;

        GenerateFoliage(generator);
    }

    static bool ValidateInputs(TerrainFoliageGenerator generator, out LDrawPartRegistry registry, out string ldrawRootPath)
    {
        registry = generator.partRegistry;
        ldrawRootPath = FindLDrawRootPath();

        if (generator.terrainStructure == null)
            generator.terrainStructure = generator.GetComponentInParent<BrickStructure>();
        if (generator.environmentRoot == null)
            generator.environmentRoot = generator.transform;

        if (generator.terrainStructure == null)
        {
            Debug.LogError("TerrainFoliageGenerator: assign Terrain Structure.");
            return false;
        }

        if (registry == null)
            registry = generator.terrainStructure.parts;
        if (registry == null)
            registry = FindPartRegistry();
        if (registry == null)
        {
            Debug.LogError("TerrainFoliageGenerator: assign Part Registry.");
            return false;
        }
        generator.partRegistry = registry;

        if (string.IsNullOrWhiteSpace(ldrawRootPath))
        {
            Debug.LogError("TerrainFoliageGenerator: missing LDrawSettings.ldrawRootPath.");
            return false;
        }

        if (generator.foliageParts == null || generator.foliageParts.Length == 0)
        {
            Debug.LogError("TerrainFoliageGenerator: no foliage parts configured.");
            return false;
        }

        Debug.Log("TerrainFoliageGenerator: configured foliage weights -> " + BuildConfiguredWeightsSummary(generator.foliageParts));

        return true;
    }

    static Dictionary<Vector2Int, float> BuildSurfaceHeightMap(
        LDrawModelAsset model,
        LDrawPartRegistry registry,
        byte[] aliveFlags)
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
                    var key = new Vector2Int(x, z);
                    if (!topByCell.TryGetValue(key, out float oldY) || max.y > oldY)
                        topByCell[key] = max.y;
                }
            }
        }

        return topByCell;
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

    static bool TryGetPartMetrics(string ldrawRootPath, string part, Dictionary<string, PartMetrics> cache, out PartMetrics metrics)
    {
        if (cache.TryGetValue(part, out metrics))
            return true;

        Mesh mesh = DatMeshBuilder.BuildMeshForFile(ldrawRootPath, part);
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
        cache[part] = metrics;
        return true;
    }

    static bool HasSpacing(HashSet<Vector2Int> placed, Vector2Int cell, int minGap)
    {
        if (minGap <= 1)
            return true;

        int r = minGap - 1;
        for (int dx = -r; dx <= r; dx++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                if (placed.Contains(new Vector2Int(cell.x + dx, cell.y + dz)))
                    return false;
            }
        }
        return true;
    }

    static string ChooseFoliagePart(TerrainFoliageGenerator.FoliagePart[] parts, uint hash)
    {
        if (parts == null || parts.Length == 0)
            return "6084b.dat";

        float total = 0f;
        for (int i = 0; i < parts.Length; i++)
            total += Mathf.Max(0f, parts[i].weight);

        if (total <= 0f)
            return "6084b.dat";

        float pick = Hash01(hash) * total;
        float running = 0f;
        for (int i = 0; i < parts.Length; i++)
        {
            running += Mathf.Max(0f, parts[i].weight);
            if (pick <= running)
                return NormalizePart(parts[i].part);
        }

        return NormalizePart(parts[parts.Length - 1].part);
    }

    static string BuildPartSummary(Dictionary<string, int> placedByPart)
    {
        if (placedByPart == null || placedByPart.Count == 0)
            return "no placements";

        var entries = new List<KeyValuePair<string, int>>(placedByPart);
        entries.Sort((a, b) => b.Value.CompareTo(a.Value));

        var parts = new List<string>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
            parts.Add(entries[i].Key + "=" + entries[i].Value);

        return string.Join(", ", parts);
    }

    static string BuildConfiguredWeightsSummary(TerrainFoliageGenerator.FoliagePart[] parts)
    {
        if (parts == null || parts.Length == 0)
            return "none";

        var chunks = new List<string>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = NormalizePart(parts[i].part);
            chunks.Add(part + "=" + parts[i].weight.ToString("0.###"));
        }

        return string.Join(", ", chunks);
    }

    static string NormalizePart(string part)
    {
        if (string.IsNullOrWhiteSpace(part))
            return "6084b.dat";
        return part.Replace('\\', '/').Trim();
    }

    static int PickFoliageColor(string part, uint hash)
    {
        if (part.Equals("32607.dat", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("33291.dat", StringComparison.OrdinalIgnoreCase))
        {
            int[] flowers = { 5, 13, 226, 191 };
            return flowers[(int)(hash % (uint)flowers.Length)];
        }

        int[] greens = { 2, 10, 17, 288 };
        return greens[(int)(hash % (uint)greens.Length)];
    }

    static int PickGrassColor(float normalizedHeight, float seaLevelT, uint hash, float noiseScale, float noiseStrength)
    {
        // Height trend: low land slightly darker, high land lighter; add small deterministic noise.
        float landT = Mathf.InverseLerp(seaLevelT, 1f, normalizedHeight);
        float nx = ((hash >> 8) & 0xFF) * noiseScale * 0.03125f;
        float nz = ((hash >> 16) & 0xFF) * noiseScale * 0.03125f;
        float noise = Mathf.PerlinNoise(nx + 19.7f, nz + 73.1f);
        float combined = Mathf.Clamp01(landT + (noise * 2f - 1f) * noiseStrength);

        int idx = Mathf.Clamp(Mathf.FloorToInt(combined * GrassPalette.Length), 0, GrassPalette.Length - 1);
        return GrassPalette[idx];
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

    static GameObject GetOrCreateFoliageObject(TerrainFoliageGenerator generator)
    {
        var root = generator.environmentRoot != null ? generator.environmentRoot : generator.transform;

        if (generator.foliageObject == null)
            generator.foliageObject = FindChildByName(root, generator.foliageObjectName);

        if (generator.foliageObject == null)
        {
            generator.foliageObject = new GameObject(generator.foliageObjectName);
            Undo.RegisterCreatedObjectUndo(generator.foliageObject, "Create Foliage Object");
        }

        generator.foliageObject.transform.position = generator.terrainStructure.transform.position;
        generator.foliageObject.transform.rotation = generator.terrainStructure.transform.rotation;
        generator.foliageObject.transform.localScale = generator.terrainStructure.transform.lossyScale;
        generator.foliageObject.transform.SetParent(root, true);

        return generator.foliageObject;
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

    static bool IsBlockedBySceneGeometry(TerrainFoliageGenerator generator, Vector2Int cell, float localTopY)
    {
        if (generator == null || generator.terrainStructure == null)
            return false;

        float radius = Mathf.Max(0.05f, generator.blockageRadius);
        float start = Mathf.Max(0.0f, generator.blockageStartHeight);
        float height = Mathf.Max(0.1f, generator.blockageCheckHeight);

        Vector3 localBase = new Vector3(cell.x + 0.5f, localTopY + start, cell.y + 0.5f);
        Vector3 worldBase = generator.terrainStructure.transform.TransformPoint(localBase);

        Vector3 center = worldBase + Vector3.up * (height * 0.5f);
        Vector3 halfExtents = new Vector3(radius, height * 0.5f, radius);

        int hitCount = Physics.OverlapBoxNonAlloc(
            center,
            halfExtents,
            BlockageHits,
            Quaternion.identity,
            generator.blockageMask,
            QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hitCount; i++)
        {
            var c = BlockageHits[i];
            if (c == null)
                continue;

            // Ignore terrain's own colliders so we only reject external blockers.
            if (c.transform.IsChildOf(generator.terrainStructure.transform))
                continue;

            if (generator.foliageObject != null && c.transform.IsChildOf(generator.foliageObject.transform))
                continue;

            return true;
        }

        return false;
    }
}
#endif
