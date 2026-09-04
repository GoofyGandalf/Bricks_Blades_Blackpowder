#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class DatMeshBuilder
{
    const string PartsCacheFolder  = "Assets/Content/Bricks/PartsCache/";
    const string DecalCacheFolder  = "Assets/Content/Bricks/DecalCache/";

    class LocalGeo
    {
        public Vector3[] verts;
        public int[]     tris;
        /// <summary>
        /// Per-vertex color code, parallel to verts.
        /// -1  = color 16 (inherit from parent/instance).
        /// -2  = color 24 (edge complement — treat as inherit too).
        /// ≥0  = explicit LDraw color code stored in the palette.
        /// </summary>
        public float[]   colorCodes;
        public bool      fileCW;         // true if file declared BFC CW winding
    }

    // Cache local triangles/quads per FULL PATH on disk
    static readonly Dictionary<string, LocalGeo> GeoCache = new(StringComparer.OrdinalIgnoreCase);

    // ─── Decal diagnostics ────────────────────────────────────────────────────

    /// <summary>
    /// Quick check whether a .dat file (or any of its includes one level deep) contains a !TEXMAP directive.
    /// Uses a line-by-line scan — does NOT build or cache geometry.
    /// </summary>
    public static bool HasTexmapDirective(string ldrawRoot, string part)
    {
        string fullPath = ResolveLDrawFile(ldrawRoot, part);
        if (fullPath == null) return false;

        try
        {
            using var sr = new StreamReader(fullPath);
            string line;
            while ((line = sr.ReadLine()) != null)
                if (line.IndexOf("!TEXMAP", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
        }
        catch { /* ignore I/O errors */ }
        return false;
    }

    [MenuItem("Tools/Bricks/Diagnose Decal Pipeline")]
    static void DiagnoseDecalPipelineMenu()
    {
        var settings = FindLDrawSettings();
        if (settings == null || string.IsNullOrEmpty(settings.ldrawRootPath))
        {
            Debug.LogError("[DecalDiag] LDrawSettings not configured or ldrawRootPath empty.");
            return;
        }

        string root     = settings.ldrawRootPath;
        string partsDir = Path.Combine(root, "parts");
        string texDir1  = Path.Combine(root, "textures");
        string texDir2  = Path.Combine(root, "parts", "textures");

        Debug.Log($"[DecalDiag] LDraw root        : {root}");
        Debug.Log($"[DecalDiag] parts/ exists      : {Directory.Exists(partsDir)}");
        Debug.Log($"[DecalDiag] textures/ exists   : {Directory.Exists(texDir1)}");
        Debug.Log($"[DecalDiag] parts/textures/ ex : {Directory.Exists(texDir2)}");

        if (Directory.Exists(texDir1))
            Debug.Log($"[DecalDiag] textures/ PNGs     : {Directory.GetFiles(texDir1, "*.png").Length}");
        if (Directory.Exists(texDir2))
            Debug.Log($"[DecalDiag] parts/textures/ PNGs: {Directory.GetFiles(texDir2, "*.png").Length}");

        if (!Directory.Exists(partsDir))
        {
            Debug.LogError("[DecalDiag] parts/ directory missing — check ldrawRootPath in LDrawSettings.");
            return;
        }

        // Scan parts/ for !TEXMAP
        var datFiles = Directory.GetFiles(partsDir, "*.dat", SearchOption.TopDirectoryOnly);
        int withTexmap = 0;
        string firstPrinted = null;

        foreach (var file in datFiles)
        {
            try
            {
                using var sr = new StreamReader(file);
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.IndexOf("!TEXMAP", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        withTexmap++;
                        firstPrinted ??= file;
                        break;
                    }
                }
            }
            catch { /* skip unreadable files */ }
        }

        Debug.Log($"[DecalDiag] Scanned {datFiles.Length} parts — {withTexmap} have !TEXMAP.");
        if (firstPrinted == null)
        {
            Debug.LogWarning("[DecalDiag] No printed parts found in LDraw library. Your LDraw installation may be incomplete.");
            return;
        }

        Debug.Log($"[DecalDiag] First printed part: {Path.GetFileName(firstPrinted)}");

        // Try to build the first printed part end-to-end
        string partName = Path.GetFileName(firstPrinted);
        Debug.Log($"[DecalDiag] Building mesh for printed part: {partName} ...");
        GeoCache.Clear();
        var mesh = BuildMeshForFile(root, partName);
        if (mesh == null)
            Debug.LogError($"[DecalDiag] BuildMeshForFile returned null for {partName}");
        else
        {
            // Check if UV1 was populated with non-zero (non-inherit) color codes
            var uv1 = new List<Vector2>();
            mesh.GetUVs(1, uv1);
            int nonInherit = 0;
            for (int ii = 0; ii < uv1.Count; ii++)
                if (uv1[ii].x >= 0f) nonInherit++;
            Debug.Log($"[DecalDiag] SUCCESS: mesh={mesh.vertexCount} verts, UV1 color codes: {uv1.Count} verts, {nonInherit} with explicit color (non-inherit)");
        }
    }

    [MenuItem("Tools/Bricks/Validate LDraw Root")]
    public static void ValidateRootMenu()
    {
        var settings = FindLDrawSettings();
        if (settings == null)
        {
            Debug.LogError("No LDrawSettings found.");
            return;
        }

        var root = settings.ldrawRootPath;
        Debug.Log($"LDraw root: {root}");

        string partsDir = Path.Combine(root, "parts");
        string pDir = Path.Combine(root, "p");

        Debug.Log($"parts exists: {Directory.Exists(partsDir)}  ({partsDir})");
        Debug.Log($"p exists: {Directory.Exists(pDir)}  ({pDir})");

        string test = Path.Combine(partsDir, "3004.dat");
        Debug.Log($"3004.dat exists: {File.Exists(test)}  ({test})");
    }

    

    [MenuItem("Tools/Bricks/Rebake ALL Part Meshes (Force)")]
    public static void RebakeAllPartsMenu()
    {
        var settings = FindLDrawSettings();
        if (settings == null || string.IsNullOrWhiteSpace(settings.ldrawRootPath))
        {
            Debug.LogError("Missing LDrawSettings or ldrawRootPath.");
            return;
        }

        string[] regGuids = AssetDatabase.FindAssets("t:LDrawPartRegistry");
        if (regGuids.Length == 0)
        {
            Debug.LogError("No LDrawPartRegistry asset found.");
            return;
        }
        var registry = AssetDatabase.LoadAssetAtPath<LDrawPartRegistry>(
            AssetDatabase.GUIDToAssetPath(regGuids[0]));

        int baked = 0, failed = 0;
        foreach (var entry in registry.entries)
        {
            if (string.IsNullOrWhiteSpace(entry.part)) continue;

            GeoCache.Clear();
            var mesh = BuildMeshForFile(settings.ldrawRootPath, entry.part);
            if (mesh == null)
            {
                Debug.LogWarning($"[RebakeAll] Could not build mesh for: {entry.part}");
                failed++;
                continue;
            }
            mesh.name = Path.GetFileNameWithoutExtension(entry.part);
            SaveMeshAsset(mesh, entry.part);
            baked++;
        }

        registry.Rebuild();
        EditorUtility.SetDirty(registry);
        AssetDatabase.SaveAssets();

        Debug.Log($"[RebakeAll] Done — {baked} rebuilt, {failed} failed.");
    }

    [MenuItem("Tools/Bricks/Bake Mesh For Part...")]
    public static void BakeMeshForPartMenu()
    {
        var settings = FindLDrawSettings();
        if (settings == null)
        {
            Debug.LogError("Missing LDrawSettings asset.");
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.ldrawRootPath))
        {
            Debug.LogError("LDrawSettings.ldrawRootPath is empty. Set it to the folder that contains 'parts' and 'p'.");
            return;
        }

        string part = PromptForPart("3004.dat");
        if (string.IsNullOrWhiteSpace(part))
            return;

        GeoCache.Clear();

        var mesh = BuildMeshForFile(settings.ldrawRootPath, part);
        if (mesh == null)
        {
            Debug.LogError($"Failed to build mesh for {part}");
            return;
        }

        SaveMeshAsset(mesh, part);
        Debug.Log($"Baked mesh for {part} into {PartsCacheFolder}");
    }

    static string PromptForPart(string defaultValue)
    {
        // Unity doesn't have a great built-in input box; use a small EditorWindow-less workaround:
        // We'll use a file panel rooted at parts/ and let you pick a .dat.
        // If you cancel, fallback to defaultValue prompt via dialog.
        return EditorUtility.DisplayDialog("Bake Part", $"Bake default part '{defaultValue}'?\n\n(Click No to pick a .dat file)", "Yes", "No")
            ? defaultValue
            : PickDatFileFromDisk(defaultValue);
    }

    static readonly float LDrawScale = 0.05f;
    static readonly Matrix4x4 ScaleMatrix = Matrix4x4.Scale(Vector3.one * LDrawScale);
    static readonly Matrix4x4 FlipZ = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
    static Vector3 ToUnityPos(Vector3 v) => new Vector3(v.x, v.y, -v.z);
    static Matrix4x4 ToUnityMatrix(Matrix4x4 m) => FlipZ * m * FlipZ;

    static string PickDatFileFromDisk(string fallback)
    {
        var settings = FindLDrawSettings();
        if (settings == null || string.IsNullOrWhiteSpace(settings.ldrawRootPath))
            return fallback;

        string startDir = Path.Combine(settings.ldrawRootPath, "parts");
        string picked = EditorUtility.OpenFilePanel("Select .dat (part)", startDir, "dat");
        if (string.IsNullOrWhiteSpace(picked))
            return null;

        // Convert absolute path back into an LDraw-relative filename if possible
        // so it resolves properly and names assets nicely.
        picked = picked.Replace('\\', '/');
        var root = settings.ldrawRootPath.Replace('\\', '/').TrimEnd('/');
        if (picked.StartsWith(root + "/parts/"))
            return picked.Substring((root + "/parts/").Length);
        if (picked.StartsWith(root + "/p/"))
            return "p/" + picked.Substring((root + "/p/").Length);

        // As a fallback, just return the absolute path; resolver can handle rooted paths.
        return picked;
    }

    static void AddTriWithWinding(List<Vector3> verts, List<int> tris, Vector3 a, Vector3 b, Vector3 c, bool flip)
    {
        int baseIndex = verts.Count;
        verts.Add(a); verts.Add(b); verts.Add(c);

        if (flip)
        {
            tris.Add(baseIndex + 0);
            tris.Add(baseIndex + 2);
            tris.Add(baseIndex + 1);
        }
        else
        {
            tris.Add(baseIndex + 0);
            tris.Add(baseIndex + 1);
            tris.Add(baseIndex + 2);
        }
    }

    public static Mesh BuildMeshForFile(string ldrawRoot, string filename)
    {
        var verts      = new List<Vector3>(20000);
        var tris       = new List<int>(40000);
        var colorCodes = new List<float>(20000);

        // Root transform: LDraw uses Y-up but Y is flipped relative to Unity,
        // and LDraw Z points toward the viewer while Unity Z points away.
        // We handle this by negating Z on every output vertex (see AppendFileRecursive),
        // and rotating 180° on X so the model is right-side up.
        // parentColor = -1 (color 16 = inherit) — at the root level this means "use instance color"
        var rootConv = Matrix4x4.Rotate(Quaternion.Euler(180f, 0f, 0f)) * ScaleMatrix;
        AppendFileRecursive(ldrawRoot, filename, rootConv, false, -1f, verts, tris, colorCodes);

        if (verts.Count == 0 || tris.Count == 0)
        {
            Debug.LogError($"No triangles produced for {filename}");
            return null;
        }

        var mesh = new Mesh();
        mesh.name = Path.GetFileNameWithoutExtension(filename);

        mesh.indexFormat = (verts.Count > 65535)
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;

        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0, true);
        mesh.RecalculateNormals();

        // Pack per-vertex color code into UV1 channel (x = color code, y unused).
        // Sentinel -1 means "use the instance color from the GPU brick buffer".
        // This is read in the shader to look up the correct palette entry per-fragment.
        var uv1 = new Vector2[colorCodes.Count];
        for (int i = 0; i < colorCodes.Count; i++)
            uv1[i] = new Vector2(colorCodes[i], 0f);
        mesh.SetUVs(1, uv1);

        // ── Vertex welding: merge duplicate vertices to reduce GPU memory ──
        WeldVertices(mesh);

        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Merges vertices that share the same position, normal, and color code to reduce GPU memory.
    /// Vertices with different color codes (printed patterns) are never merged.
    /// </summary>
    static void WeldVertices(Mesh mesh)
    {
        var srcVerts   = mesh.vertices;
        var srcNormals = mesh.normals;
        var srcTris    = mesh.triangles;
        // UV1 holds per-vertex color codes packed as (colorCode, 0)
        var uv1List = new List<Vector2>();
        mesh.GetUVs(1, uv1List);
        var srcUvs = uv1List.Count == srcVerts.Length ? uv1List.ToArray() : null;

        if (srcVerts.Length == 0 || srcNormals.Length == 0) return;

        bool hasUvs = srcUvs != null;

        const float posTolerance    = 0.001f;
        const float normalTolerance = 0.01f;

        // Hash vertices into buckets by quantised position
        int quantize(float v) => Mathf.RoundToInt(v / posTolerance);

        var buckets   = new Dictionary<long, List<int>>(srcVerts.Length / 2);
        var remap     = new int[srcVerts.Length];
        var newVerts   = new List<Vector3>(srcVerts.Length / 2);
        var newNormals = new List<Vector3>(srcVerts.Length / 2);
        var newUvs     = hasUvs ? new List<Vector2>(srcVerts.Length / 2) : null;

        for (int i = 0; i < srcVerts.Length; i++)
        {
            var p  = srcVerts[i];
            var n  = srcNormals[i];
            var uv = hasUvs ? srcUvs[i] : Vector2.zero;

            long key = ((long)quantize(p.x) * 73856093L)
                     ^ ((long)quantize(p.y) * 19349663L)
                     ^ ((long)quantize(p.z) * 83492791L);

            int found = -1;

            if (buckets.TryGetValue(key, out var bucket))
            {
                for (int bi = 0; bi < bucket.Count; bi++)
                {
                    int candidate = bucket[bi];
                    bool posOk    = (newVerts[candidate]   - p).sqrMagnitude  <= posTolerance    * posTolerance;
                    bool normOk   = (newNormals[candidate] - n).sqrMagnitude  <= normalTolerance * normalTolerance;
                    // Color codes must match exactly (different colors = different geometry appearance)
                    bool colorOk  = !hasUvs || (newUvs[candidate].x == uv.x);
                    if (posOk && normOk && colorOk) { found = candidate; break; }
                }
            }

            if (found >= 0)
            {
                remap[i] = found;
            }
            else
            {
                int idx = newVerts.Count;
                newVerts.Add(p);
                newNormals.Add(n);
                if (hasUvs) newUvs.Add(uv);
                remap[i] = idx;

                if (bucket == null)
                {
                    bucket = new List<int>(4);
                    buckets[key] = bucket;
                }
                bucket.Add(idx);
            }
        }

        // Remap triangle indices
        var newTris = new int[srcTris.Length];
        for (int i = 0; i < srcTris.Length; i++)
            newTris[i] = remap[srcTris[i]];

        // Apply welded data
        mesh.Clear();
        mesh.indexFormat = (newVerts.Count > 65535)
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(newVerts);
        mesh.SetNormals(newNormals);
        if (hasUvs && newUvs != null) mesh.SetUVs(1, newUvs);  // UV1 = color codes
        mesh.SetTriangles(newTris, 0, true);
    }

    static void AppendFileRecursive(string ldrawRoot, string filename, Matrix4x4 parentXf, bool bfcInvert,
        float parentColor, List<Vector3> outVerts, List<int> outTris, List<float> outColorCodes)
    {
        string fullPath = ResolveLDrawFile(ldrawRoot, filename);
        if (fullPath == null)
        {
            Debug.LogError($"Could not resolve LDraw file: {filename}\nRoot: {ldrawRoot}");
            return;
        }

        // 1) Append THIS file's local triangles/quads (cached — in raw LDraw space)
        var local = ParseLocalGeo(fullPath);
        int baseIndex = outVerts.Count;

        for (int i = 0; i < local.verts.Length; i++)
        {
            // Transform in LDraw space. The root matrix Rotate(180°X) already maps LDraw (x,y,z) → Unity (x,-y,-z),
            // matching the FlipYZ = Scale(1,-1,-1) convention used by the placement matrices in ParseLdr.
            Vector3 lp = parentXf.MultiplyPoint3x4(local.verts[i]);
            outVerts.Add(lp);
        }
        // Copy per-vertex color codes, resolving inherit sentinels (-1=color16, -2=color24) to parentColor.
        // parentColor=-1 at root → sentinel propagates → shader uses the GPU brick's instance color.
        for (int i = 0; i < local.colorCodes.Length; i++)
        {
            float c = local.colorCodes[i];
            outColorCodes.Add(c < 0f ? parentColor : c);
        }
        // Negative det means the transform is a mirror — must flip winding to preserve normals.
        // Rotate(180°X) has det=+1 so it does NOT flip winding.
        // local.fileCW: geometry is stored as declared (not pre-normalised), so CW files need an extra flip.
        float det = Matrix3x3Determinant(parentXf);
        bool needsFlip = (det < 0f) ^ bfcInvert ^ local.fileCW;

        for (int i = 0; i < local.tris.Length; i += 3)
        {
            int i0 = baseIndex + local.tris[i + 0];
            int i1 = baseIndex + local.tris[i + 1];
            int i2 = baseIndex + local.tris[i + 2];

            if (needsFlip)
            {
                outTris.Add(i0);
                outTris.Add(i2);
                outTris.Add(i1);
            }
            else
            {
                outTris.Add(i0);
                outTris.Add(i1);
                outTris.Add(i2);
            }
        }

        // 2) Recurse into type 1 includes
        using var sr = new StreamReader(fullPath);
        string line;
        bool invertNext = false;

        while ((line = sr.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line[0] == '0')
            {
                if (line.IndexOf("BFC", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    line.IndexOf("INVERTNEXT", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    invertNext = true;
                }
                continue;
            }

            var t = SplitTokens(line);
            if (t.Count == 0 || t[0] != "1" || t.Count < 15) continue;

            string childFile = t[^1];

            // Resolve child color: color 16 = inherit parentColor, color 24 = inherit too.
            float childColorRaw = ParseF(t[1]);
            float childColor = (childColorRaw == 16f || childColorRaw == 24f) ? parentColor : childColorRaw;

            float x = ParseF(t[2]), y = ParseF(t[3]), z = ParseF(t[4]);
            float a = ParseF(t[5]), b = ParseF(t[6]), c = ParseF(t[7]);
            float d = ParseF(t[8]), e = ParseF(t[9]), f = ParseF(t[10]);
            float g = ParseF(t[11]), h = ParseF(t[12]), i = ParseF(t[13]);

            // LDraw matrix is column-major: columns are (a,d,g), (b,e,h), (c,f,i), translation (x,y,z)
            var childLocal = new Matrix4x4(
                new Vector4(a, d, g, 0),
                new Vector4(b, e, h, 0),
                new Vector4(c, f, i, 0),
                new Vector4(x, y, z, 1)
            );

            // Keep child matrix in raw LDraw space — Z-flip is applied at vertex output time
            bool childInvert = bfcInvert ^ invertNext;
            invertNext = false;

            AppendFileRecursive(ldrawRoot, childFile, parentXf * childLocal, childInvert,
                childColor, outVerts, outTris, outColorCodes);
        }
    }

    // small helper to compute determinant (3x3) of matrix upper-left
    static float Matrix3x3Determinant(Matrix4x4 m)
    {
        // use columns as vectors
        Vector3 c0 = new Vector3(m.m00, m.m01, m.m02);
        Vector3 c1 = new Vector3(m.m10, m.m11, m.m12);
        Vector3 c2 = new Vector3(m.m20, m.m21, m.m22);
        // det = c0 · (c1 × c2)
        return Vector3.Dot(c0, Vector3.Cross(c1, c2));
    }

    static LocalGeo ParseLocalGeo(string fullPath)
    {
        if (GeoCache.TryGetValue(fullPath, out var cached))
            return cached;

        var verts      = new List<Vector3>(2048);
        var tris       = new List<int>(4096);
        var colorCodes = new List<float>(2048);  // parallel to verts; -1 = color 16 inherit

        bool fileCW = false;

        using var sr = new StreamReader(fullPath);
        string line;
        while ((line = sr.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line[0] == '0')
            {
                if (line.Contains("BFC", StringComparison.OrdinalIgnoreCase))
                {
                    // Check CCW before CW — "CCW" contains "CW" as a substring
                    if (line.IndexOf("CCW", StringComparison.OrdinalIgnoreCase) >= 0)
                        fileCW = false;
                    else if (line.IndexOf("CW", StringComparison.OrdinalIgnoreCase) >= 0)
                        fileCW = true;
                }
                // All other meta-commands (including !TEXMAP) are ignored here.
                // The PNG-!TEXMAP path is handled separately in BuildMeshForFile.
                continue;
            }

            var tok = SplitTokens(line);
            if (tok.Count == 0) continue;
            if (!int.TryParse(tok[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type))
                continue;

            // Parse the color code (token 1): 16 = inherit (-1 sentinel), 24 = edge inherit (-2)
            float rawColor = -1f;
            if (tok.Count >= 2 &&
                float.TryParse(tok[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out float cc))
            {
                rawColor = (cc == 16f) ? -1f : (cc == 24f) ? -2f : cc;
            }

            if (type == 3 && tok.Count >= 11)
            {
                var a = new Vector3(ParseF(tok[2]),  ParseF(tok[3]),  ParseF(tok[4]));
                var b = new Vector3(ParseF(tok[5]),  ParseF(tok[6]),  ParseF(tok[7]));
                var c = new Vector3(ParseF(tok[8]),  ParseF(tok[9]),  ParseF(tok[10]));
                AddTriWithColors(verts, tris, colorCodes, a, b, c, rawColor);
            }
            else if (type == 4 && tok.Count >= 14)
            {
                var p1 = new Vector3(ParseF(tok[2]),  ParseF(tok[3]),  ParseF(tok[4]));
                var p2 = new Vector3(ParseF(tok[5]),  ParseF(tok[6]),  ParseF(tok[7]));
                var p3 = new Vector3(ParseF(tok[8]),  ParseF(tok[9]),  ParseF(tok[10]));
                var p4 = new Vector3(ParseF(tok[11]), ParseF(tok[12]), ParseF(tok[13]));
                AddTriWithColors(verts, tris, colorCodes, p1, p2, p3, rawColor);
                AddTriWithColors(verts, tris, colorCodes, p1, p3, p4, rawColor);
            }
        }

        var g = new LocalGeo
        {
            verts      = verts.ToArray(),
            tris       = tris.ToArray(),
            colorCodes = colorCodes.ToArray(),
            fileCW     = fileCW,
        };
        GeoCache[fullPath] = g;
        return g;
    }

    static void AddTriWithColors(List<Vector3> verts, List<int> tris, List<float> colorCodes,
        Vector3 a, Vector3 b, Vector3 c, float color)
    {
        int baseIndex = verts.Count;
        verts.Add(a); verts.Add(b); verts.Add(c);
        tris.Add(baseIndex);
        tris.Add(baseIndex + 1);
        tris.Add(baseIndex + 2);
        colorCodes.Add(color); colorCodes.Add(color); colorCodes.Add(color);
    }

    static float ParseF(string s) => float.Parse(s, CultureInfo.InvariantCulture);

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

    static string ResolveLDrawFile(string ldrawRoot, string filename)
    {
        filename = filename.Replace('\\', '/').Trim();

        if (Path.IsPathRooted(filename) && File.Exists(filename))
            return filename;

        string Combine(params string[] parts) => Path.Combine(parts).Replace('\\', '/');

        // If already prefixed, try directly under root first (covers "p/..." picks)
        var directUnderRoot = Combine(ldrawRoot, filename);
        if (File.Exists(directUnderRoot)) return directUnderRoot;

        // Try parts first (most common)
        var directParts = Combine(ldrawRoot, "parts", filename);
        if (File.Exists(directParts)) return directParts;

        // If include says s/...
        if (filename.StartsWith("s/"))
        {
            var f = filename.Substring(2);
            var ps = Combine(ldrawRoot, "parts", "s", f);
            if (File.Exists(ps)) return ps;
        }

        // If include says p/48/...
        if (filename.StartsWith("p/48/"))
        {
            var f = filename.Substring("p/48/".Length);
            var pp = Combine(ldrawRoot, "p", "48", f);
            if (File.Exists(pp)) return pp;
        }

        // If include says p/...
        if (filename.StartsWith("p/"))
        {
            var f = filename.Substring(2);
            var pp = Combine(ldrawRoot, "p", f);
            if (File.Exists(pp)) return pp;
        }

        // Common fallback locations
        var subparts = Combine(ldrawRoot, "parts", "s", filename);
        if (File.Exists(subparts)) return subparts;

        var prim = Combine(ldrawRoot, "p", filename);
        if (File.Exists(prim)) return prim;

        var prim48 = Combine(ldrawRoot, "p", "48", filename);
        if (File.Exists(prim48)) return prim48;

        return null;
    }

    /// <summary>
    /// Resolves the texture PNG on disk, copies it to DecalCache/, imports it, and returns the Texture2D asset.
    /// </summary>
    static Texture2D ImportDecalTexture(string ldrawRoot, string textureFile, string partFilename)
    {
        // Build destination path keyed by part name so LDrawPartRegistryEditor.Refresh() can match them.
        Directory.CreateDirectory(DecalCacheFolder);
        string partBase = Path.GetFileNameWithoutExtension(
            partFilename.Replace('\\', '_').Replace('/', '_'));
        string destPath = DecalCacheFolder + partBase + ".png";

        // If already imported, return the existing asset immediately.
        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(destPath);
        if (existing != null) return existing;

        // Resolve source PNG: try standard LDraw textures/ folder, then parts/textures/.
        string srcPath = null;
        foreach (var candidate in new[]
        {
            Path.Combine(ldrawRoot, "textures", textureFile),
            Path.Combine(ldrawRoot, "parts", "textures", textureFile),
        })
        {
            if (File.Exists(candidate)) { srcPath = candidate.Replace('\\', '/'); break; }
        }

        if (srcPath == null)
        {
            Debug.LogWarning($"[DatMeshBuilder] Decal texture not found: '{textureFile}' for part '{partFilename}'.");
            return null;
        }

        File.Copy(srcPath, destPath, overwrite: true);
        AssetDatabase.ImportAsset(destPath, ImportAssetOptions.ForceSynchronousImport);

        var importer = AssetImporter.GetAtPath(destPath) as TextureImporter;
        if (importer != null)
        {
            importer.textureType          = TextureImporterType.Default;
            importer.sRGBTexture          = true;
            importer.alphaIsTransparency  = true;
            importer.mipmapEnabled        = true;
            importer.filterMode           = FilterMode.Bilinear;
            importer.wrapMode             = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(destPath);
    }

    static void SaveDecalAsset(Texture2D decal, string partFilename)
    {
        // ImportDecalTexture already copied and imported the PNG into DecalCache.
        // Nothing to do here — the asset already exists at the expected path.
        // This method is kept as a hook for any post-processing needed in the future.
        _ = decal; // suppress unused warning
    }

    static void SaveMeshAsset(Mesh mesh, string partFilename)
    {
        Directory.CreateDirectory(PartsCacheFolder);

        string clean = partFilename.Replace('\\', '_').Replace('/', '_');
        string assetPath = PartsCacheFolder + Path.GetFileNameWithoutExtension(clean) + ".asset";

        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        if (existing == null)
        {
            AssetDatabase.CreateAsset(mesh, assetPath);
        }
        else
        {
            EditorUtility.CopySerialized(mesh, existing);
            EditorUtility.SetDirty(existing);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static LDrawSettings FindLDrawSettings()
    {
        string[] guids = AssetDatabase.FindAssets("t:LDrawSettings");
        if (guids == null || guids.Length == 0) return null;
        string path = AssetDatabase.GUIDToAssetPath(guids[0]);
        return AssetDatabase.LoadAssetAtPath<LDrawSettings>(path);
    }
}
#endif