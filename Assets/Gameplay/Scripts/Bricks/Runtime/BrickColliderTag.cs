using UnityEngine;

/// <summary>
/// Tag on each per-brick collider child. Maps a physics hit back to a specific brick.
/// </summary>
public class BrickColliderTag : MonoBehaviour
{
    [HideInInspector] public BrickStructure structure;
    [HideInInspector] public int brickIndex;
}
