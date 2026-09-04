#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class LDrawModelImporter
{
    const string DefaultOutputFolder = "Assets/Content/Bricks/BrickData/Models/";
    const string PartsCacheFolder = "Assets/Content/Bricks/PartsCache/";

    static readonly Matrix4x4 FlipYZ = Matrix4x4.Scale(new Vector3(1f, -1f, -1f));
    static Matrix4x4 ToUnityMatrix(Matrix4x4 m) => FlipYZ * m * FlipYZ;
    static readonly Matrix4x4 RootConv = Matrix4x4.Rotate(Quaternion.Euler(0f, 180f, 0f));

    // ─── Connectivity bake settings ───
    // AABB-based connectivity: two bricks are neighbors if their bounding boxes
    // (in model space) are within this gap tolerance of each other.
    // At LDraw scale 0.05: stud pitch = 1.0, brick height = 1.2, plate height = 0.4
    const float AabbGapTolerance = 0.35f;  // allow small gaps between touching bricks
    const float AabbCellSize = 2.0f;       // spatial hash cell — cover largest common bricks

    [MenuItem("Tools/Bricks/Import LDR Model from Selected TextAsset")]
    public static void ImportSelected()
    {
        var ta = Selection.activeObject as TextAsset;
        if (ta == null)
        {
            Debug.LogError("Select an .ldr TextAsset in the Project window first.");
            return;
        }

        var settings = FindSettings();
        if (settings == null || string.IsNullOrWhiteSpace(settings.ldrawRootPath))
        {
            Debug.LogError("Missing LDrawSettings or ldrawRootPath. Set LDrawSettings.ldrawRootPath to your LDraw folder (contains parts/ and p/).");
            return;
        }

        ImportTextAssetToModelAndBake(ta, settings.ldrawRootPath);
    }

    /// <summary>
    /// Builds an LDrawModelAsset from a caller-provided brick list, then bakes
    /// GPU batch data and connectivity to match the runtime rendering path.
    /// </summary>
    public static LDrawModelAsset BuildModelFromBrickList(
        List<LDrawModelAsset.Brick> bricks,
        HashSet<string> uniqueParts,
        string ldrawRootPath)
    {
        var model = ScriptableObject.CreateInstance<LDrawModelAsset>();
        model.bricks = bricks != null
            ? new List<LDrawModelAsset.Brick>(bricks)
            : new List<LDrawModelAsset.Brick>();

        if (uniqueParts == null)
        {
            uniqueParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < model.bricks.Count; i++)
            {
                string p = model.bricks[i].part;
                if (!string.IsNullOrWhiteSpace(p))
                    uniqueParts.Add(p.Replace('\\', '/').Trim());
            }
        }

        if (string.IsNullOrWhiteSpace(ldrawRootPath))
        {
            var settings = FindSettings();
            ldrawRootPath = settings != null ? settings.ldrawRootPath : null;
        }

        if (!string.IsNullOrWhiteSpace(ldrawRootPath) && uniqueParts.Count > 0)
            BakeMissingParts(ldrawRootPath, uniqueParts);
        else
            Debug.LogWarning("BuildModelFromBrickList: ldrawRootPath missing or no parts provided; skipping BakeMissingParts.");

        RefreshPartRegistry();
        var registry = FindPartRegistry();
        if (registry == null)
        {
            Debug.LogWarning("LDrawPartRegistry not found - skipping GPU data bake. Run 'Refresh Part Registry' and rebake.");
            return model;
        }

        BakeGpuData(model, registry);
        BakeConnectivity(model, registry);
        return model;
    }

    static void ImportTextAssetToModelAndBake(TextAsset ldrText, string ldrawRootPath)
    {
        var bricks = new List<LDrawModelAsset.Brick>(4096);
        var uniqueParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ParseLdr(ldrText.text, bricks, uniqueParts);

        var model = BuildModelFromBrickList(bricks, uniqueParts, ldrawRootPath);

        Directory.CreateDirectory(DefaultOutputFolder);
        string outPath = AssetDatabase.GenerateUniqueAssetPath(DefaultOutputFolder + ldrText.name + ".asset");
        AssetDatabase.CreateAsset(model, outPath);
        EditorUtility.SetDirty(model);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"Imported {ldrText.name}: {bricks.Count} bricks, {uniqueParts.Count} unique parts, " +
                  $"{model.batchGroups?.Length ?? 0} batch groups -> {outPath}");
        Selection.activeObject = model;
    }

    // ─── GPU Data Bake ───

    /// <summary>
    /// Converts legacy Brick list → sorted BrickInstance[] + BatchGroup[].
    /// Bricks are sorted by (partId, colorCode) so each batch is contiguous.
    /// </summary>
    static void BakeGpuData(LDrawModelAsset model, LDrawPartRegistry registry)
    {
        var legacyBricks = model.bricks;
        if (legacyBricks == null || legacyBricks.Count == 0) return;

        // Convert to temp list with resolved IDs
        var temp = new List<(int partId, int colorCode, Matrix4x4 local)>(legacyBricks.Count);
        int skippedCount = 0;
        var skippedParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in legacyBricks)
        {
            int id = registry.GetPartId(b.part);
            if (id < 0)
            {
                skippedCount++;
                skippedParts.Add(b.part);
                continue; // skip unresolved parts
            }
            temp.Add((id, b.colorCode, b.local));
        }

        if (skippedCount > 0)
        {
            Debug.LogWarning($"BakeGpuData: Skipped {skippedCount} bricks with {skippedParts.Count} unresolved parts: {string.Join(", ", skippedParts)}");
        }

        // Sort by (partId, colorCode) for contiguous batching
        temp.Sort((a, b) =>
        {
            int cmp = a.partId.CompareTo(b.partId);
            return cmp != 0 ? cmp : a.colorCode.CompareTo(b.colorCode);
        });

        // Build sorted brick array and batch groups
        var sorted = new LDrawModelAsset.BrickInstance[temp.Count];
        var groups = new List<LDrawModelAsset.BatchGroup>(64);

        int groupStart = 0;
        for (int i = 0; i < temp.Count; i++)
        {
            sorted[i] = new LDrawModelAsset.BrickInstance
            {
                partId              = temp[i].partId,
                colorCode           = temp[i].colorCode,
                localTransform      = temp[i].local,
                connectivityOffset  = 0,
                neighborCount       = 0,
            };

            // Detect batch group boundaries
            bool isLast = (i == temp.Count - 1);
            bool groupEnd = isLast
                || temp[i + 1].partId != temp[i].partId
                || temp[i + 1].colorCode != temp[i].colorCode;

            if (groupEnd)
            {
                groups.Add(new LDrawModelAsset.BatchGroup
                {
                    partId     = temp[i].partId,
                    colorCode  = temp[i].colorCode,
                    startIndex = groupStart,
                    count      = i - groupStart + 1,
                });
                groupStart = i + 1;
            }
        }

        model.sortedBricks = sorted;
        model.batchGroups  = groups.ToArray();
    }

    // ─── Connectivity Bake ───

    /// <summary>
    /// Builds a flat adjacency list using AABB overlap.
    /// Two bricks are neighbors if their bounding boxes (in model space) are
    /// within AabbGapTolerance of each other. This correctly handles all brick
    /// sizes (1x1, 2x4, 1x6, etc.) unlike center-to-center distance.
    /// </summary>
    static void BakeConnectivity(LDrawModelAsset model, LDrawPartRegistry registry)
    {
        var bricks = model.sortedBricks;
        if (bricks == null || bricks.Length == 0) return;

        // ── Step 1: compute model-space AABB for each brick ──
        var aabbMin = new Vector3[bricks.Length];
        var aabbMax = new Vector3[bricks.Length];
        // Default bounds when mesh not available (approx 1x1 brick)
        var defaultBounds = new Bounds(Vector3.zero, new Vector3(1.0f, 1.2f, 1.0f));

        for (int i = 0; i < bricks.Length; i++)
        {
            Bounds meshBounds = defaultBounds;
            if (registry != null)
            {
                Mesh mesh = registry.GetMeshById(bricks[i].partId);
                if (mesh != null)
                    meshBounds = mesh.bounds;
            }

            // Transform the 8 AABB corners into model space and compute enclosing AABB
            Matrix4x4 xf = bricks[i].localTransform;
            Vector3 bMin = meshBounds.min;
            Vector3 bMax = meshBounds.max;

            Vector3 wMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 wMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int cx = 0; cx < 2; cx++)
            for (int cy = 0; cy < 2; cy++)
            for (int cz = 0; cz < 2; cz++)
            {
                Vector3 corner = new Vector3(
                    cx == 0 ? bMin.x : bMax.x,
                    cy == 0 ? bMin.y : bMax.y,
                    cz == 0 ? bMin.z : bMax.z);
                Vector3 w = xf.MultiplyPoint3x4(corner);
                wMin = Vector3.Min(wMin, w);
                wMax = Vector3.Max(wMax, w);
            }

            aabbMin[i] = wMin;
            aabbMax[i] = wMax;
        }

        // ── Step 2: spatial hash — each brick registers in ALL cells its AABB covers ──
        var grid = new Dictionary<Vector3Int, List<int>>(bricks.Length * 2);

        Vector3Int ToCell(Vector3 pos) => new Vector3Int(
            Mathf.FloorToInt(pos.x / AabbCellSize),
            Mathf.FloorToInt(pos.y / AabbCellSize),
            Mathf.FloorToInt(pos.z / AabbCellSize));

        for (int i = 0; i < bricks.Length; i++)
        {
            // Expand by gap tolerance so near-touching bricks share cells
            Vector3 lo = aabbMin[i] - Vector3.one * AabbGapTolerance;
            Vector3 hi = aabbMax[i] + Vector3.one * AabbGapTolerance;
            Vector3Int cellLo = ToCell(lo);
            Vector3Int cellHi = ToCell(hi);

            for (int x = cellLo.x; x <= cellHi.x; x++)
            for (int y = cellLo.y; y <= cellHi.y; y++)
            for (int z = cellLo.z; z <= cellHi.z; z++)
            {
                var cell = new Vector3Int(x, y, z);
                if (!grid.TryGetValue(cell, out var list))
                {
                    list = new List<int>(8);
                    grid[cell] = list;
                }
                list.Add(i);
            }
        }

        // ── Step 3: build adjacency — two bricks sharing any cell are candidates ──
        var adjacency  = new List<int>(bricks.Length * 6);
        var strengths  = new List<float>(bricks.Length * 6);
        var neighborSet = new HashSet<int>(); // reused per brick

        for (int i = 0; i < bricks.Length; i++)
        {
            neighborSet.Clear();
            int offset = adjacency.Count;

            // Gather all candidate neighbors from cells this brick occupies
            Vector3 lo = aabbMin[i] - Vector3.one * AabbGapTolerance;
            Vector3 hi = aabbMax[i] + Vector3.one * AabbGapTolerance;
            Vector3Int cellLo = ToCell(lo);
            Vector3Int cellHi = ToCell(hi);

            for (int x = cellLo.x; x <= cellHi.x; x++)
            for (int y = cellLo.y; y <= cellHi.y; y++)
            for (int z = cellLo.z; z <= cellHi.z; z++)
            {
                var cell = new Vector3Int(x, y, z);
                if (!grid.TryGetValue(cell, out var list)) continue;

                for (int li = 0; li < list.Count; li++)
                {
                    int j = list[li];
                    if (j == i || neighborSet.Contains(j)) continue;

                    // AABB minimum distance check
                    float gap = AabbMinDistance(aabbMin[i], aabbMax[i], aabbMin[j], aabbMax[j]);
                    if (gap > AabbGapTolerance) continue;

                    neighborSet.Add(j);

                    // Connection strength: vertical (stud) = strong, horizontal (friction) = weak
                    Vector3 centerA = (aabbMin[i] + aabbMax[i]) * 0.5f;
                    Vector3 centerB = (aabbMin[j] + aabbMax[j]) * 0.5f;
                    Vector3 delta = centerB - centerA;
                    float absDy = Mathf.Abs(delta.y);
                    float absHoriz = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z);
                    float verticalRatio = (absDy + 0.001f) / (absDy + absHoriz + 0.001f);
                    float strength = Mathf.Lerp(0.15f, 1.0f, verticalRatio * verticalRatio);

                    adjacency.Add(j);
                    strengths.Add(strength);
                }
            }

            var b = bricks[i];
            b.connectivityOffset = offset;
            b.neighborCount = adjacency.Count - offset;
            bricks[i] = b;
        }

        model.adjacencyList       = adjacency.ToArray();
        model.connectionStrengths = strengths.ToArray();

        // Stats
        int maxNeighbors = 0;
        float avgNeighbors = 0;
        for (int i = 0; i < bricks.Length; i++)
        {
            avgNeighbors += bricks[i].neighborCount;
            if (bricks[i].neighborCount > maxNeighbors)
                maxNeighbors = bricks[i].neighborCount;
        }
        avgNeighbors /= Mathf.Max(1, bricks.Length);
        Debug.Log($"[Connectivity] {bricks.Length} bricks, {adjacency.Count} edges, " +
                  $"avg {avgNeighbors:F1} neighbors/brick, max {maxNeighbors}");
    }

    /// <summary>Minimum distance between two AABBs. 0 if overlapping.</summary>
    static float AabbMinDistance(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax)
    {
        float dx = Mathf.Max(0, Mathf.Max(aMin.x - bMax.x, bMin.x - aMax.x));
        float dy = Mathf.Max(0, Mathf.Max(aMin.y - bMax.y, bMin.y - aMax.y));
        float dz = Mathf.Max(0, Mathf.Max(aMin.z - bMax.z, bMin.z - aMax.z));
        return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // ─── Migration: rebake GPU data on existing assets ───

    [MenuItem("Tools/Bricks/Rebake GPU Data on Selected Model")]
    public static void RebakeSelectedModel()
    {
        var model = Selection.activeObject as LDrawModelAsset;
        if (model == null)
        {
            Debug.LogError("Select an LDrawModelAsset in the Project window.");
            return;
        }

        var registry = FindPartRegistry();
        if (registry == null)
        {
            Debug.LogError("LDrawPartRegistry not found. Run 'Refresh Part Registry' first.");
            return;
        }

        BakeGpuData(model, registry);
        BakeConnectivity(model, registry);
        EditorUtility.SetDirty(model);
        AssetDatabase.SaveAssets();
        Debug.Log($"Rebaked GPU data: {model.sortedBricks?.Length ?? 0} bricks, " +
                  $"{model.batchGroups?.Length ?? 0} batch groups, " +
                  $"{model.adjacencyList?.Length ?? 0} adjacency entries.");
    }

    [MenuItem("Tools/Bricks/Rebake ALL Model Assets")]
    public static void RebakeAllModels()
    {
        var registry = FindPartRegistry();
        if (registry == null)
        {
            Debug.LogError("LDrawPartRegistry not found. Run 'Refresh Part Registry' first.");
            return;
        }

        string[] guids = AssetDatabase.FindAssets("t:LDrawModelAsset");
        int count = 0;
        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var model = AssetDatabase.LoadAssetAtPath<LDrawModelAsset>(path);
            if (model == null || model.bricks == null || model.bricks.Count == 0) continue;

            BakeGpuData(model, registry);
            BakeConnectivity(model, registry);
            EditorUtility.SetDirty(model);
            count++;
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"Rebaked GPU data on {count} model assets.");
    }

    // ─── LDR Parsing (unchanged) ───

    static void ParseLdr(string text, List<LDrawModelAsset.Brick> outBricks, HashSet<string> outParts)
    {
        var files = SplitIntoVirtualFiles(text, out string entryFile);
        if (files.Count == 0) return;

        AppendFile(entryFile, RootConv, false);

        void AppendFile(string file, Matrix4x4 xf, bool bfcInvert)
        {
            if (!files.TryGetValue(Norm(file), out var lines))
                return;

            bool invertNext = false;

            for (int li = 0; li < lines.Count; li++)
            {
                var line = lines[li].Trim();
                if (line.Length == 0) continue;

                if (line[0] == '0')
                {
                    if (line.IndexOf("BFC", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        line.IndexOf("INVERTNEXT", StringComparison.OrdinalIgnoreCase) >= 0)
                        invertNext = true;
                    continue;  // All meta lines are skipped
                }

                var t = SplitTokens(line);
                if (t.Count < 1) continue;

                // Reset invertNext for non-part geometry lines (tri, quad, etc.)
                if (t[0] != "1")
                {
                    invertNext = false;
                    continue;
                }
                if (t.Count < 15) continue;

                int colorCode = ParseI(t[1]);

                const float LDrawScale = 0.05f;
                float x = ParseF(t[2]) * LDrawScale;
                float y = ParseF(t[3]) * LDrawScale;
                float z = ParseF(t[4]) * LDrawScale;
                float a = ParseF(t[5]), b = ParseF(t[6]), c = ParseF(t[7]);
                float d = ParseF(t[8]), e = ParseF(t[9]), f = ParseF(t[10]);
                float g = ParseF(t[11]), h = ParseF(t[12]), i = ParseF(t[13]);

                string childFile = t[^1];

                var childLocal = new Matrix4x4(
                    new Vector4(a, d, g, 0f),   // column 0
                    new Vector4(b, e, h, 0f),   // column 1
                    new Vector4(c, f, i, 0f),   // column 2
                    new Vector4(x, y, z, 1f)    // column 3 (translation, already scaled)
                );

                childLocal = ToUnityMatrix(childLocal);

                bool childInvert = bfcInvert ^ invertNext;
                invertNext = false;

                if (childFile.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                {
                    string part = Norm(childFile);
                    outParts.Add(part);

                    outBricks.Add(new LDrawModelAsset.Brick
                    {
                        part = part,
                        colorCode = colorCode,
                        local = xf * childLocal
                    });
                }
                else
                {
                    AppendFile(childFile, xf * childLocal, childInvert);
                }
            }
        }
    }

    static void BakeMissingParts(string ldrawRootPath, HashSet<string> parts)
    {
        Directory.CreateDirectory(PartsCacheFolder);

        int baked = 0, skipped = 0;

        foreach (var part in parts)
        {
            string meshAssetPath = PartsCacheFolder + Path.GetFileNameWithoutExtension(part) + ".asset";
            bool meshExists = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath) != null;

            if (meshExists) { skipped++; continue; }

            var mesh = DatMeshBuilder.BuildMeshForFile(ldrawRootPath, part);
            if (mesh == null)
            {
                Debug.LogWarning($"Could not bake mesh for {part}");
                continue;
            }

            if (!meshExists)
            {
                mesh.name = Path.GetFileNameWithoutExtension(part);
                AssetDatabase.CreateAsset(mesh, meshAssetPath);
            }
            else
            {
                // Mesh already exists but was missing its decal — update it with UV data.
                var existingMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath);
                if (existingMesh != null)
                {
                    mesh.name = Path.GetFileNameWithoutExtension(part);
                    EditorUtility.CopySerialized(mesh, existingMesh);
                    EditorUtility.SetDirty(existingMesh);
                }
            }
            baked++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"BakeMissingParts: baked {baked}, already had {skipped}");
    }

    static void RefreshPartRegistry()
    {
        var mi = typeof(LDrawPartRegistryEditor).GetMethod("Refresh",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        mi?.Invoke(null, null);
    }

    // ─── Utilities ───

    static LDrawPartRegistry FindPartRegistry()
    {
        string[] guids = AssetDatabase.FindAssets("t:LDrawPartRegistry");
        if (guids == null || guids.Length == 0) return null;
        string path = AssetDatabase.GUIDToAssetPath(guids[0]);
        return AssetDatabase.LoadAssetAtPath<LDrawPartRegistry>(path);
    }

    static Dictionary<string, List<string>> SplitIntoVirtualFiles(string text, out string entryFile)
    {
        var files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        entryFile = null;

        string current = null;
        var currentLines = new List<string>(2048);

        var all = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        for (int i = 0; i < all.Length; i++)
        {
            var line = all[i];

            if (line.StartsWith("0 FILE ", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null)
                    files[Norm(current)] = currentLines;

                current = line.Substring(7).Trim();
                currentLines = new List<string>(2048);

                if (entryFile == null)
                    entryFile = current;

                continue;
            }

            if (line.StartsWith("0 NOFILE", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null)
                    files[Norm(current)] = currentLines;

                current = null;
                currentLines = new List<string>(2048);
                continue;
            }

            if (current != null)
                currentLines.Add(line);
        }

        if (current != null)
            files[Norm(current)] = currentLines;

        if (files.Count == 0)
        {
            entryFile = "main.ldr";
            files[entryFile] = new List<string>(all);
        }

        entryFile = Norm(entryFile);
        return files;
    }

    static string Norm(string p) => p.Replace('\\', '/').Trim();
    static float ParseF(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    static int ParseI(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    static List<string> SplitTokens(string line)
    {
        var tokens = new List<string>(16);
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;
            int start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            tokens.Add(line.Substring(start, i - start));
        }
        return tokens;
    }

    static LDrawSettings FindSettings()
    {
        string[] guids = AssetDatabase.FindAssets("t:LDrawSettings");
        if (guids == null || guids.Length == 0) return null;
        string path = AssetDatabase.GUIDToAssetPath(guids[0]);
        return AssetDatabase.LoadAssetAtPath<LDrawSettings>(path);
    }
}
#endif