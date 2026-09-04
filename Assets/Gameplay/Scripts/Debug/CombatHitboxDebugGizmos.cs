using UnityEngine;

public class CombatHitboxDebugGizmos : MonoBehaviour
{
    public PlayerController controller;
    public bool drawAlways;
    public bool drawColliders = true;
    public Color meleeNearColor = new Color(0f, 1f, 0.4f, 0.9f);
    public Color meleeFarColor = new Color(0f, 0.6f, 1f, 0.9f);
    public Color colliderColor = new Color(1f, 0.9f, 0.1f, 0.7f);

    void Reset()
    {
        controller = GetComponent<PlayerController>();
    }

    void OnDrawGizmos()
    {
        if (!drawAlways) return;
        Draw();
    }

    void OnDrawGizmosSelected()
    {
        if (drawAlways) return;
        Draw();
    }

    void Draw()
    {
        if (!controller) controller = GetComponent<PlayerController>();
        if (!controller) return;

        var t = controller.transform;
        Vector3 forward = t.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();

        Vector3 basePos = t.position + Vector3.up * controller.meleeHeight;
        Vector3 near = basePos + forward * (controller.meleeRange * 0.55f);
        Vector3 far = basePos + forward * controller.meleeRange;

        Gizmos.color = meleeNearColor;
        Gizmos.DrawWireSphere(near, controller.meleeRadius);

        Gizmos.color = meleeFarColor;
        Gizmos.DrawWireSphere(far, controller.meleeRadius);

        if (!drawColliders) return;

        Gizmos.color = colliderColor;
        var cols = controller.GetComponentsInChildren<Collider>(includeInactive: true);
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (!c) continue;
            if (!c.enabled) continue;

            var b = c.bounds;
            Gizmos.DrawWireCube(b.center, b.size);
        }
    }
}
