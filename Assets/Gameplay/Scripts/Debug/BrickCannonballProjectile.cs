using UnityEngine;

/// <summary>
/// Physical projectile that applies brick damage on impact.
/// Network-aware: prefers NetworkBrickStructure.RequestDamage when available.
/// </summary>
[RequireComponent(typeof(SphereCollider))]
[RequireComponent(typeof(Rigidbody))]
public class BrickCannonballProjectile : MonoBehaviour
{
    BrickDamageSystem _damageSystem;
    BrickCannonballShooter _owner;

    float _damageRadius;
    float _despawnAt;
    bool _hasImpacted;

    SphereCollider _sphere;
    Rigidbody _rb;

    void Awake()
    {
        _sphere = GetComponent<SphereCollider>();
        _rb = GetComponent<Rigidbody>();
    }

    void OnEnable()
    {
        _hasImpacted = false;
        // Prevent Update from despawning the projectile before Configure() is called
        // (can happen if this script's Update runs in the same frame as SetActive(true))
        _despawnAt = float.MaxValue;
    }

    void Update()
    {
        if (Time.time >= _despawnAt)
            ReturnToPool();
    }

    public void Configure(
        BrickDamageSystem damageSystem,
        float damageRadius,
        float lifetime,
        float radius,
        float mass,
        BrickCannonballShooter owner)
    {
        _damageSystem = damageSystem;
        _damageRadius = Mathf.Max(0.01f, damageRadius);
        _despawnAt = Time.time + Mathf.Max(0.1f, lifetime);
        _owner = owner;

        if (_sphere == null) _sphere = GetComponent<SphereCollider>();
        if (_rb == null) _rb = GetComponent<Rigidbody>();

        _sphere.radius = Mathf.Max(0.01f, radius);
        _rb.mass = Mathf.Max(0.01f, mass);
    }

    public void Launch(Vector3 velocity)
    {
        if (_rb == null) _rb = GetComponent<Rigidbody>();

        _rb.linearVelocity = velocity;
        _rb.angularVelocity = Vector3.zero;
    }

    /// <summary>Ignores physics collisions between this projectile and a set of colliders (e.g. the local player).</summary>
    public void IgnoreColliders(Collider[] colliders)
    {
        if (_sphere == null) _sphere = GetComponent<SphereCollider>();
        if (_sphere == null) return;
        foreach (var c in colliders)
            if (c != null) Physics.IgnoreCollision(_sphere, c, true);
    }

    void OnCollisionEnter(Collision collision)
    {
        if (_hasImpacted) return;
        _hasImpacted = true;

        Vector3 hitPoint = collision.contactCount > 0
            ? collision.GetContact(0).point
            : transform.position;

        // Check if we hit a destructible debris chunk
        var chunk = collision.collider.GetComponentInParent<DestructibleChunk>();
        if (chunk != null)
        {
            Debug.Log($"[Cannonball] Hit debris chunk {chunk.name}, shattering!");
            chunk.Shatter(hitPoint, collision.relativeVelocity, _rb.mass);
            ReturnToPool();
            return;
        }

        // Resolve which brick (if any) was hit via per-brick collider tags
        BrickColliderManager.TryGetBrickFromCollider(collision.collider, out var structure, out int brickIndex);

        if (structure != null)
        {
            var local = PlayerController.Local;
            if (local != null)
            {
                // Route through PlayerController RPCs so all clients see the destruction
                local.RequestBrickDamageReplication(structure, hitPoint, collision.relativeVelocity, _rb.mass, brickIndex);
            }
            else if (_damageSystem != null)
            {
                // Offline / no local player — apply locally only
                _damageSystem.DealDamageWithForce(structure, hitPoint, collision.relativeVelocity, _rb.mass, brickIndex);
            }
        }

        ReturnToPool();
    }

    void ReturnToPool()
    {
        if (_rb != null)
        {
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }

        if (_owner != null)
            _owner.ReturnProjectile(this);
        else
            gameObject.SetActive(false);
    }
}
