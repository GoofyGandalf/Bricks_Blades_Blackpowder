using UnityEngine;

/// <summary>
/// Physics cannonball fired by CannonController.
/// Fire-and-forget: self-destructs on impact or after lifetime.
/// Routes brick destruction through PlayerController RPCs so all clients see it.
/// </summary>
[RequireComponent(typeof(SphereCollider))]
[RequireComponent(typeof(Rigidbody))]
public class CannonProjectile : MonoBehaviour
{
    BrickDamageSystem _damageSystem;
    Rigidbody _rb;
    bool _hasImpacted;
    float _damageForceMultiplier = 1f;
    float _gravityScale = 1f;
    float _impactBlastRadius = 0f;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
    }

    public void Launch(Vector3 velocity, BrickDamageSystem damageSystem, float lifetime, float damageForceMultiplier, float gravityScale, float impactBlastRadius)
    {
        _damageSystem = damageSystem;
        _damageForceMultiplier = Mathf.Max(0.1f, damageForceMultiplier);
        _gravityScale = Mathf.Clamp01(gravityScale);
        _impactBlastRadius = Mathf.Max(0f, impactBlastRadius);
        _rb.linearVelocity = velocity;
        _rb.angularVelocity = Vector3.zero;
        Destroy(gameObject, lifetime);
    }

    void FixedUpdate()
    {
        if (_rb == null) return;
        if (_gravityScale <= 0f) return;

        _rb.linearVelocity += Physics.gravity * (_gravityScale * Time.fixedDeltaTime);
    }

    public void IgnoreColliders(Collider[] colliders)
    {
        var sphere = GetComponent<SphereCollider>();
        if (sphere == null) return;
        foreach (var c in colliders)
            if (c != null) Physics.IgnoreCollision(sphere, c, true);
    }

    void OnCollisionEnter(Collision collision)
    {
        if (_hasImpacted) return;
        _hasImpacted = true;

        Vector3 hitPoint = collision.contactCount > 0
            ? collision.GetContact(0).point
            : transform.position;

        // Destructible debris chunk
        var chunk = collision.collider.GetComponentInParent<DestructibleChunk>();
        if (chunk != null)
        {
            chunk.Shatter(hitPoint, collision.relativeVelocity, _rb.mass);
            Destroy(gameObject);
            return;
        }

        // Brick structure — route through PlayerController RPCs so all clients see destruction
        BrickColliderManager.TryGetBrickFromCollider(collision.collider, out var structure, out int brickIndex);
        if (structure != null)
        {
            var local = PlayerController.Local;
            Vector3 scaledVelocity = collision.relativeVelocity * _damageForceMultiplier;
            float scaledMass = _rb.mass * _damageForceMultiplier;
            if (local != null)
            {
                local.RequestBrickDamageReplication(structure, hitPoint, scaledVelocity, scaledMass, brickIndex);
                if (_impactBlastRadius > 0f)
                    local.RequestBrickRadiusDamageReplication(structure, hitPoint, _impactBlastRadius);
            }
            else if (_damageSystem != null)
            {
                _damageSystem.DealDamageWithForce(structure, hitPoint, scaledVelocity, scaledMass, brickIndex);
                if (_impactBlastRadius > 0f)
                    _damageSystem.DealDamage(structure, hitPoint, _impactBlastRadius);
            }
        }

        Destroy(gameObject);
    }

}
