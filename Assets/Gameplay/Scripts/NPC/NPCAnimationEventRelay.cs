using UnityEngine;

namespace BricksBladesBlackpowder.NPC
{
    /// <summary>
    /// Lives on the HumanoidDriver child GameObject (the Mixamo Animator).
    /// Receives animation events fired by Mixamo clips so Unity doesn't spam
    /// "FootstepEvent has no receiver" warnings.
    /// Mirrors AnimationEventRelay used on the player prefab.
    /// </summary>
    public class NPCAnimationEventRelay : MonoBehaviour
    {
        // Silently consume footstep events. Add audio forwarding here later if needed.
        public void FootstepEvent() { }
    }
}
