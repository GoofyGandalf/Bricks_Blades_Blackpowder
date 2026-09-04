using UnityEngine;

public class AnimationEventRelay : MonoBehaviour
{
    PlayerController controller;

    void Awake()
    {
        controller = GetComponentInParent<PlayerController>();
    }

    public void FootstepEvent()
    {
        if (controller)
        {
            controller.FootstepEvent();
        }
    }
}
