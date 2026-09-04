using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Optional binder — only needed if your buttons are on a different GameObject
/// than MainMenuController. If you wire buttons directly on MainMenuController's
/// serialized fields, you can remove this script entirely.
/// </summary>
public class MainMenuBinder : MonoBehaviour
{
    [SerializeField] Button hostButton;
    [SerializeField] Button joinButton;
    [SerializeField] MainMenuController controller;

    void Awake()
    {
        // MainMenuController now wires its own buttons in Start().
        // This binder is kept for backward compat / alternate UI setups.
        // If controller has its own button refs assigned, this does nothing extra.
    }
}
