#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class LDrawPartRegistryEditor
{
    const string PartsCacheFolder = "Assets/Content/Bricks/PartsCache";

    [MenuItem("Tools/Bricks/Refresh Part Registry")]
    public static void Refresh()
    {
        var registry = FindRegistry();
        if (registry == null)
        {
            Debug.LogError("No LDrawPartRegistry asset found. Create one via Create → Bricks → LDraw Part Registry.");
            return;
        }

        // Build index of existing entries so we can update-in-place (preserving stable IDs)
        var existingByPart = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < registry.entries.Count; i++)
        {
            var e = registry.entries[i];
            if (!string.IsNullOrWhiteSpace(e.part))
                existingByPart[NormalizePart(e.part)] = i;
        }

        // ── Pass 1: meshes from PartsCache ──
        string[] guids = AssetDatabase.FindAssets("t:Mesh", new[] { PartsCacheFolder });
        int added = 0, updated = 0;

        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null) continue;

            string part = mesh.name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                ? mesh.name
                : (mesh.name + ".dat");

            string key = NormalizePart(part);

            if (existingByPart.TryGetValue(key, out int idx))
            {
                var e = registry.entries[idx];
                e.mesh = mesh;
                registry.entries[idx] = e;
                updated++;
            }
            else
            {
                registry.entries.Add(new LDrawPartRegistry.Entry { part = part, mesh = mesh });
                existingByPart[key] = registry.entries.Count - 1;
                added++;
            }
        }

        registry.Invalidate();
        registry.Rebuild();

        EditorUtility.SetDirty(registry);
        AssetDatabase.SaveAssets();

        Debug.Log($"Refreshed Part Registry: {registry.entries.Count} total parts ({added} new, {updated} updated) from {PartsCacheFolder}");
    }

    static string NormalizePart(string p) => p.Replace('\\', '/').Trim();

    static LDrawPartRegistry FindRegistry()
    {
        string[] guids = AssetDatabase.FindAssets("t:LDrawPartRegistry");
        if (guids == null || guids.Length == 0) return null;
        string path = AssetDatabase.GUIDToAssetPath(guids[0]);
        return AssetDatabase.LoadAssetAtPath<LDrawPartRegistry>(path);
    }
}
#endif