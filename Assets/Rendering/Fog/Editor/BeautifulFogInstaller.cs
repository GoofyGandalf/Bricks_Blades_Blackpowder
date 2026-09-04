#if UNITY_EDITOR
using BBB.Rendering.Fog;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BBB.Rendering.Fog.Editor
{
    public static class BeautifulFogInstaller
    {
        [MenuItem("Tools/Rendering/Beautiful Fog/Install On All URP Renderers")]
        public static void InstallOnAllRenderers()
        {
            var rendererGuids = AssetDatabase.FindAssets("t:UniversalRendererData");
            int addedFeatures = 0;

            foreach (var guid in rendererGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/"))
                {
                    continue;
                }

                var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (rendererData == null)
                {
                    continue;
                }

                if (HasFeature(rendererData))
                {
                    continue;
                }

                var feature = ScriptableObject.CreateInstance<BeautifulFogRendererFeature>();
                feature.name = "Beautiful Fog";
                feature.hideFlags = HideFlags.HideInHierarchy;

                AssetDatabase.AddObjectToAsset(feature, rendererData);
                rendererData.rendererFeatures.Add(feature);

                EditorUtility.SetDirty(feature);
                EditorUtility.SetDirty(rendererData);
                addedFeatures++;
            }

            EnsureDepthTexturesEnabled();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"Beautiful Fog install complete. Added feature to {addedFeatures} renderer asset(s). Depth textures are now enabled on URP assets.");
        }

        [MenuItem("Tools/Rendering/Beautiful Fog/Select All URP Renderers")]
        public static void SelectAllRenderers()
        {
            var rendererGuids = AssetDatabase.FindAssets("t:UniversalRendererData");
            var results = new System.Collections.Generic.List<Object>(rendererGuids.Length);

            for (int i = 0; i < rendererGuids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(rendererGuids[i]);
                if (!path.StartsWith("Assets/"))
                {
                    continue;
                }

                var asset = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (asset != null)
                {
                    results.Add(asset);
                }
            }

            Selection.objects = results.ToArray();
        }

        private static bool HasFeature(UniversalRendererData rendererData)
        {
            foreach (var feature in rendererData.rendererFeatures)
            {
                if (feature is BeautifulFogRendererFeature)
                {
                    return true;
                }
            }

            return false;
        }

        private static void EnsureDepthTexturesEnabled()
        {
            var urpAssetGuids = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset");
            foreach (var guid in urpAssetGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/"))
                {
                    continue;
                }

                var serializedObject = new SerializedObject(AssetDatabase.LoadMainAssetAtPath(path));
                var requireDepthTextureProperty = serializedObject.FindProperty("m_RequireDepthTexture");
                if (requireDepthTextureProperty == null)
                {
                    continue;
                }

                if (!requireDepthTextureProperty.boolValue)
                {
                    requireDepthTextureProperty.boolValue = true;
                    serializedObject.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }
    }
}
#endif
