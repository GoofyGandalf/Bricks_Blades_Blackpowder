using UnityEngine;

[CreateAssetMenu(fileName = "LDrawSettings", menuName = "Bricks/LDraw Settings", order = 1)]
public class LDrawSettings : ScriptableObject
{
#if UNITY_EDITOR
    [Header("LDraw Library (Editor-only)")]
    [Tooltip("Dev machine only. Example: C:/Users/Public/Documents/LDraw")]
    public string ldrawRootPath;
#endif

    [Header("LDraw Config (Bundled)")]
    [Tooltip("Assign the LDConfig.ldr TextAsset from your project.")]
    public TextAsset ldConfigOverride;

    [Header("Brick Rendering Assets")]
    public Material brickMaterial;
    public Texture2D paletteTexture;

    [Header("Palette Generation")]
    [Min(256)]
    public int paletteWidth = 2048;

    public string paletteOutputAssetPath = "Assets/Content/Bricks/Textures/T_LDrawPalette.png";
}