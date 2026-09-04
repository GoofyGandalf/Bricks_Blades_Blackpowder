#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

[ScriptedImporter(1, new[] { "ldr", "mpd" })]
public class LDrawScriptedImporter : ScriptedImporter
{
    public override void OnImportAsset(AssetImportContext ctx)
    {
        var text = File.ReadAllText(ctx.assetPath);

        // Create a TextAsset so it can be assigned to TextAsset fields.
        var asset = new TextAsset(text)
        {
            name = Path.GetFileNameWithoutExtension(ctx.assetPath)
        };

        ctx.AddObjectToAsset("text", asset);
        ctx.SetMainObject(asset);
    }
}
#endif