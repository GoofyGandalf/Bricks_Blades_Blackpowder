#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(LegoWaterGenerator))]
public class LegoWaterGeneratorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("Water Actions", EditorStyles.boldLabel);

        var g = (LegoWaterGenerator)target;

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Generate Water", GUILayout.Height(26f)))
                LegoWaterGeneratorUtility.GenerateWater(g);

            if (GUILayout.Button("Clear Water", GUILayout.Height(26f)))
                LegoWaterGeneratorUtility.ClearWater(g, g.deleteGeneratedAssetOnClear);
        }

        if (GUILayout.Button("Regenerate Seed + Generate", GUILayout.Height(24f)))
            LegoWaterGeneratorUtility.RegenerateSeedAndGenerate(g);
    }
}
#endif
