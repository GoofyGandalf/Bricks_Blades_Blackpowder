using UnityEngine;

/// <summary>
/// Zero-allocation update-based state machine replacing coroutine-per-swing.
/// </summary>
public class CombatAnimDriver : MonoBehaviour
{
    public Animator animator;
    public string combatLayerName = "Combat Layer";
    public string swingTriggerName = "Swing";
    public float fadeIn = 0.08f;
    public float fadeOut = 0.10f;

    int L;
    int swingTriggerHash;

    enum Phase { Idle, FadeIn, WaitSwingStart, WaitSwingEnd, FadeOut }
    Phase phase;
    float fadeElapsed;
    float fadeDuration;
    float fadeFrom;
    float fadeTo;

    void Awake()
    {
        L = animator.GetLayerIndex(combatLayerName);
        swingTriggerHash = Animator.StringToHash(swingTriggerName);
        animator.SetLayerWeight(L, 0f);
    }

    public void DoSwing()
    {
        // Restart from fade-in regardless of current phase
        StartFade(0f, 1f, fadeIn, Phase.FadeIn);
    }

    public void ResetToIdle()
    {
        phase = Phase.Idle;
        fadeElapsed = 0f;
        fadeDuration = 0f;
        fadeFrom = 0f;
        fadeTo = 0f;

        animator.ResetTrigger(swingTriggerHash);
        animator.SetLayerWeight(L, 0f);
        animator.Rebind();
        animator.Update(0f);
    }

    void Update()
    {
        if (phase == Phase.Idle) return;

        switch (phase)
        {
            case Phase.FadeIn:
                if (TickFade())
                {
                    animator.ResetTrigger(swingTriggerHash);
                    animator.SetTrigger(swingTriggerHash);
                    phase = Phase.WaitSwingStart;
                }
                break;

            case Phase.WaitSwingStart:
                if (animator.GetCurrentAnimatorStateInfo(L).IsName("Swing"))
                    phase = Phase.WaitSwingEnd;
                break;

            case Phase.WaitSwingEnd:
                if (!animator.GetCurrentAnimatorStateInfo(L).IsName("Swing"))
                    StartFade(1f, 0f, fadeOut, Phase.FadeOut);
                break;

            case Phase.FadeOut:
                if (TickFade())
                    phase = Phase.Idle;
                break;
        }
    }

    void StartFade(float from, float to, float duration, Phase next)
    {
        fadeFrom = from;
        fadeTo = to;
        fadeDuration = duration;
        fadeElapsed = 0f;
        phase = next;

        if (duration <= 0f)
            animator.SetLayerWeight(L, to);
    }

    /// <summary>Returns true when the fade is complete.</summary>
    bool TickFade()
    {
        if (fadeDuration <= 0f) return true;

        fadeElapsed += Time.deltaTime;
        float t = Mathf.Clamp01(fadeElapsed / fadeDuration);
        animator.SetLayerWeight(L, Mathf.Lerp(fadeFrom, fadeTo, t));
        return t >= 1f;
    }
}
