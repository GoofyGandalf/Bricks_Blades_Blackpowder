#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public static class LDrawPaletteGenerator
{
    // Matches:
    // 0 !COLOUR Lilac CODE 219 VALUE #CDA4DE EDGE #333333 ALPHA 128
    static readonly Regex ColourLine =
        new Regex(@"^0\s+!COLOUR\s+.+?\s+CODE\s+(?<code>\d+)\s+VALUE\s+#(?<hex>[0-9A-Fa-f]{6})(?:.*?\s+ALPHA\s+(?<alpha>\d+))?",
            RegexOptions.Compiled);

    [MenuItem("Tools/Bricks/Generate LDraw Palette")]
    public static void GeneratePaletteMenu()
    {
        var settings = FindSettingsAsset();
        if (settings == null)
        {
            Debug.LogError("No LDrawSettings asset found. Create one via Create -> Bricks -> LDraw Settings.");
            return;
        }

        GeneratePalette(settings);
    }

    public static void GeneratePalette(LDrawSettings settings)
    {
        if (settings == null)
        {
            Debug.LogError("Settings is null.");
            return;
        }

        string ldConfigText = GetLdConfigText(settings);
        if (string.IsNullOrEmpty(ldConfigText))
        {
            Debug.LogError("Could not load LDConfig.ldr. Check LDrawSettings.ldrawRootPath or ldConfigOverride.");
            return;
        }

        int width = Mathf.Max(256, settings.paletteWidth);
        var pixels = new Color32[width];

        // Default: bright magenta so missing codes are obvious.
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color32(255, 0, 255, 255);

        int count = 0;
        using (var reader = new StringReader(ldConfigText))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var m = ColourLine.Match(line);
                if (!m.Success) continue;

                if (!int.TryParse(m.Groups["code"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                    continue;

                if (code < 0 || code >= width)
                    continue;

                string hex = m.Groups["hex"].Value;
                if (!TryParseRgbHex(hex, out var c))
                    continue;

                byte a = 255;
                if (m.Groups["alpha"].Success && int.TryParse(m.Groups["alpha"].Value, out int ai))
                    a = (byte)Mathf.Clamp(ai, 0, 255);

                pixels[code] = new Color32(c.r, c.g, c.b, a);

                pixels[code] = c;
                count++;
            }
        }

        var tex = new Texture2D(width, 1, TextureFormat.RGBA32, mipChain: false, linear: false);
        tex.name = "T_LDrawPalette";
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.SetPixels32(pixels);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);

        WriteTextureAsset(tex, settings.paletteOutputAssetPath);

        settings.paletteTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(settings.paletteOutputAssetPath);
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();

        Debug.Log($"Generated LDraw palette: {settings.paletteOutputAssetPath} (filled {count} color codes, width {width}).");
    }

    static string GetLdConfigText(LDrawSettings settings)
    {
        if (settings.ldConfigOverride == null)
            return null;

        return settings.ldConfigOverride.text;
    }

    static bool TryParseRgbHex(string hex6, out Color32 c)
    {
        c = default;
        if (hex6 == null || hex6.Length != 6) return false;

        byte r = byte.Parse(hex6.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte g = byte.Parse(hex6.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte b = byte.Parse(hex6.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        c = new Color32(r, g, b, 255);
        return true;
    }

    static void WriteTextureAsset(Texture2D tex, string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.StartsWith("Assets/"))
        {
            Debug.LogError($"Invalid output asset path: {assetPath}. Must start with 'Assets/'.");
            return;
        }

        string fullPath = Path.GetFullPath(assetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

        byte[] png = tex.EncodeToPNG();
        File.WriteAllBytes(fullPath, png);

        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

        // Apply importer settings (point sampling, clamp, no mipmaps)
        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Point;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.isReadable = true; // convenient for debugging; you can turn off later
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }
    }

    static LDrawSettings FindSettingsAsset()
    {
        string[] guids = AssetDatabase.FindAssets("t:LDrawSettings");
        if (guids == null || guids.Length == 0) return null;
        string path = AssetDatabase.GUIDToAssetPath(guids[0]);
        return AssetDatabase.LoadAssetAtPath<LDrawSettings>(path);
    }
}
#endif