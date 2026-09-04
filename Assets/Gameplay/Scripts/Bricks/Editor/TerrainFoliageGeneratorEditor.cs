#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(TerrainFoliageGenerator))]
public class TerrainFoliageGeneratorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("Foliage Actions", EditorStyles.boldLabel);

        var generator = (TerrainFoliageGenerator)target;

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Generate Foliage", GUILayout.Height(26f)))
                TerrainFoliageGeneratorUtility.GenerateFoliage(generator);

            if (GUILayout.Button("Clear Foliage", GUILayout.Height(26f)))
                TerrainFoliageGeneratorUtility.ClearFoliage(generator, generator.deleteGeneratedAssetOnClear);
        }

        if (GUILayout.Button("Regenerate Seed + Generate", GUILayout.Height(24f)))
            TerrainFoliageGeneratorUtility.RegenerateSeedAndGenerate(generator);
    }
}
#endif
