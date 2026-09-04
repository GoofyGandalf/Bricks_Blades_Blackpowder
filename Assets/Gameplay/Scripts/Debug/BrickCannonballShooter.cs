using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Test-only cannonball shooter: right click fires a sphere from camera toward mouse.
/// Uses pooling and routes impact damage through BrickCannonballProjectile.
/// </summary>
public class BrickCannonballShooter : MonoBehaviour
{
    [Header("References")]
    public Camera shootCamera;
    public BrickDamageSystem damageSystem;

    [Header("Projectile")]
    [Tooltip("Upward offset from the player's root position for the spawn point.")]
    public float spawnHeightOffset = 1.4f;
    [Tooltip("Forward offset from the player's root position for the spawn point.")]
    public float spawnForwardOffset = 0.6f;
    public float projectileRadius = 0.15f;
    public float projectileMass = 1.0f;
    public float projectileSpeed = 55f;
    public float projectileLifetime = 6f;

    [Header("Damage")]
    public float impactDamageRadius = 0.6f;

    [Header("Aiming")]
    public LayerMask aimMask = ~0;
    public float aimMaxDistance = 500f;

    [Header("Player (ignored by projectile)")]
    [Tooltip("Root transform of the local player. All colliders on it are excluded from aim raycasts and projectile collisions.")]
    public Transform playerRoot;

    [Header("Pooling")]
    public int initialPoolSize = 16;
    public int maxPoolSize = 128;

    readonly Queue<BrickCannonballProjectile> _pool = new();
    readonly List<BrickCannonballProjectile> _active = new(64);
    Collider[] _playerColliders;

    void Awake()
    {
        if (shootCamera == null)
            shootCamera = Camera.main;

        if (damageSystem == null)
            damageSystem = FindFirstObjectByType<BrickDamageSystem>();

        CachePlayerColliders();

        for (int i = 0; i < initialPoolSize; i++)
            _pool.Enqueue(CreateProjectileInstance());
    }

    void CachePlayerColliders()
    {
        // Try to auto-find the local player if not assigned
        if (playerRoot == null)
        {
            var pc = FindFirstObjectByType<PlayerController>();
            if (pc != null) playerRoot = pc.transform;
        }
        _playerColliders = playerRoot != null
            ? playerRoot.GetComponentsInChildren<Collider>(true)
            : null;
    }

    void Update()
    {
        CleanupInactive();

        if (Input.GetKeyDown(KeyCode.F))
            Fire();
    }

    void CleanupInactive()
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (_active[i] == null || !_active[i].gameObject.activeSelf)
                _active.RemoveAt(i);
        }
    }

    void Fire()
    {
        if (shootCamera == null)
            shootCamera = Camera.main;

        if (shootCamera == null)
            return;

        // Re-cache player colliders each shot in case the player spawned after Awake
        if (_playerColliders == null || _playerColliders.Length == 0)
            CachePlayerColliders();

        // Determine aim point from screen ray, ignoring the player's own colliders
        var screenRay = shootCamera.ScreenPointToRay(Input.mousePosition);
        Vector3 aimPoint = GetAimPoint(screenRay);

        // Spawn from the player's chest/forward so the ball is never inside the camera
        // rig or between the camera and the character.
        Vector3 spawnPos;
        if (playerRoot != null)
        {
            spawnPos = playerRoot.position
                + Vector3.up * spawnHeightOffset
                + playerRoot.forward * spawnForwardOffset;
        }
        else
        {
            // Fallback: use the camera but push far enough to clear the scene
            spawnPos = shootCamera.transform.position + shootCamera.transform.forward * 2f;
        }

        Vector3 toAim = aimPoint - spawnPos;
        Vector3 dir = toAim.sqrMagnitude > 0.01f ? toAim.normalized : screenRay.direction;

        var projectile = GetProjectile();

        // Set up ignore-collision BEFORE positioning/activating so there is zero chance
        // of an accidental contact in the first physics step.
        if (_playerColliders != null)
            projectile.IgnoreColliders(_playerColliders);

        projectile.transform.position = spawnPos;
        projectile.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

        projectile.Configure(
            damageSystem,
            impactDamageRadius,
            projectileLifetime,
            projectileRadius,
            projectileMass,
            this
        );

        projectile.Launch(dir * projectileSpeed);

        _active.Add(projectile);
    }

    // Returns the world-space aim point, skipping the player's own colliders.
    Vector3 GetAimPoint(Ray screenRay)
    {
        // RaycastAll so we can filter out the player's own body
        var hits = Physics.RaycastAll(screenRay, aimMaxDistance, aimMask, QueryTriggerInteraction.Ignore);

        // Sort ascending by distance
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (var h in hits)
        {
            if (_playerColliders != null && System.Array.IndexOf(_playerColliders, h.collider) >= 0)
                continue; // skip player's own colliders
            return h.point;
        }

        return screenRay.origin + screenRay.direction * Mathf.Min(aimMaxDistance, 200f);
    }

    BrickCannonballProjectile GetProjectile()
    {
        while (_pool.Count > 0)
        {
            var p = _pool.Dequeue();
            if (p != null)
            {
                p.gameObject.SetActive(true);
                return p;
            }
        }

        // Pool exhausted — create a new instance and activate it immediately
        var overflow = CreateProjectileInstance();
        overflow.gameObject.SetActive(true);
        return overflow;
    }

    BrickCannonballProjectile CreateProjectileInstance()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "DebugCannonball";

        var projectile = go.GetComponent<BrickCannonballProjectile>();
        if (projectile == null)
            projectile = go.AddComponent<BrickCannonballProjectile>();

        var rb = go.GetComponent<Rigidbody>();
        if (rb == null)
            rb = go.AddComponent<Rigidbody>();
        
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.useGravity = true;

        go.SetActive(false);
        return projectile;
    }

    public void ReturnProjectile(BrickCannonballProjectile projectile)
    {
        if (projectile == null) return;

        projectile.gameObject.SetActive(false);

        if (_pool.Count < maxPoolSize)
            _pool.Enqueue(projectile);
        else
            Destroy(projectile.gameObject);
    }
}
