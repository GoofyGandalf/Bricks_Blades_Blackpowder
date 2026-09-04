using Fusion;
using UnityEngine;

/// <summary>
/// Attach to each cannon GameObject (with or without a NetworkObject).
///
/// Fallback mode: if Fusion never calls Spawned() (scene object not registered),
/// the cannon operates locally — occupation is per-client, no cross-client sync.
/// Networked mode: occupation is replicated; material swap visible to all clients.
///
/// - Press E within <see cref="occupyRadius"/> to mount.
/// - Hold left mouse button to fire cannonballs at the cursor.
///   Aim is clamped to ±<see cref="barrelClampDeg"/>° from barrel forward.
/// - Press E again or walk beyond <see cref="exitRadius"/> to dismount.
/// - Materials swap while occupied.
/// </summary>
public class CannonController : NetworkBehaviour
{
    // ─── Inspector ───────────────────────────────────────────────────────────

    [Header("Interaction")]
    [Tooltip("Distance at which the player can press E to mount the cannon.")]
    public float occupyRadius = 6f;
    [Tooltip("Distance beyond which the player is automatically dismounted.")]
    public float exitRadius   = 9f;

    [Header("Barrel")]
    [Tooltip("Muzzle transform — cannonballs spawn here and aim from here.")]
    public Transform barrelTip;
    [Tooltip("Optional separate transform whose forward defines the barrel axis for clamping. Defaults to barrelTip.")]
    public Transform barrelAxisRef;

    [Header("Firing")]
    [Tooltip("Rounds per second while the left mouse button is held.")]
    public float fireRate          = 0.8f;
    public float projectileSpeed   = 70f;
    public float projectileRadius  = 0.75f;
    public float projectileMass    = 2.5f;
    public float projectileLifetime = 10f;
    [Range(0f, 1f)]
    [Tooltip("Gravity multiplier for cannonballs. Lower = flatter trajectory.")]
    public float projectileGravityScale = 0.35f;
    [Tooltip("Multiplier applied to impact force before sending brick damage.")]
    public float damageForceMultiplier = 2.0f;
    [Tooltip("Additional replicated blast radius applied on impact.")]
    public float impactBlastRadius = 2.25f;
    [Tooltip("Maximum degrees the aim direction can deviate from barrel forward (horizontally).")]
    public float barrelClampDeg    = 90f;
    public LayerMask aimMask       = ~0;
    public float aimMaxDistance    = 500f;

    [Header("Materials")]
    [Tooltip("Renderers whose material will be swapped to indicate occupancy.")]
    public Renderer[] targetRenderers;
    [Tooltip("Material slot index within each renderer to swap.")]
    public int materialIndex    = 0;
    public Material freeMaterial;
    public Material occupiedMaterial;

    // ─── Networked state (only active when Fusion lifecycle fires) ────────────

    [Networked, OnChangedRender(nameof(OnOccupancyChanged))]
    PlayerRef OccupiedBy { get; set; }

    // ─── Fallback (local-only) state ─────────────────────────────────────────

    bool _fusionActive;       // true once Spawned() fires
    bool _localOccupiedByMe;  // fallback: this client is mounted

    // ─── Shared ──────────────────────────────────────────────────────────────

    BrickDamageSystem _damageSystem;
    float             _nextFireTime;
    Material          _runtimeProjectileMat;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    void Awake()
    {
        _damageSystem = FindAnyObjectByType<BrickDamageSystem>();
        _runtimeProjectileMat = BuildProjectileMaterial();
    }

    public override void Spawned()
    {
        _fusionActive = true;
        _damageSystem = FindAnyObjectByType<BrickDamageSystem>();
        OnOccupancyChanged();
    }

    void Update()
    {
        var local = PlayerController.Local;
        if (local == null) return;

        if (_fusionActive)
            UpdateNetworked(local);
        else
            UpdateFallback(local);
    }

    // ─── Networked update ─────────────────────────────────────────────────────

    void UpdateNetworked(PlayerController local)
    {
        bool amMounted = Runner != null && OccupiedBy == Runner.LocalPlayer;

        if (amMounted)
        {
            float dist = Vector3.Distance(local.transform.position, transform.position);
            if (Input.GetKeyDown(KeyCode.E) || dist > exitRadius)
            {
                RPC_RequestOccupy(Runner.LocalPlayer, release: true);
                return;
            }
            TryFire(local);
        }
        else if (OccupiedBy == PlayerRef.None)
        {
            if (!Input.GetKeyDown(KeyCode.E)) return;
            float dist = Vector3.Distance(local.transform.position, transform.position);
            if (dist <= occupyRadius)
                RPC_RequestOccupy(Runner.LocalPlayer, release: false);
        }
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    void RPC_RequestOccupy(PlayerRef requester, NetworkBool release)
    {
        if (!Object.HasStateAuthority) return;

        if (release)
        {
            if (OccupiedBy == requester)
            {
                OccupiedBy = PlayerRef.None;
                OnOccupancyChanged();
            }
        }
        else
        {
            if (OccupiedBy == PlayerRef.None)
            {
                OccupiedBy = requester;
                OnOccupancyChanged();
            }
        }
    }

    // ─── Fallback (local) update ──────────────────────────────────────────────

    void UpdateFallback(PlayerController local)
    {
        if (_localOccupiedByMe)
        {
            float dist = Vector3.Distance(local.transform.position, transform.position);
            if (Input.GetKeyDown(KeyCode.E) || dist > exitRadius)
            {
                _localOccupiedByMe = false;
                ApplyMaterial(occupied: false);
                return;
            }
            TryFire(local);
        }
        else
        {
            if (!Input.GetKeyDown(KeyCode.E)) return;
            float dist = Vector3.Distance(local.transform.position, transform.position);
            if (dist <= occupyRadius)
            {
                _localOccupiedByMe = true;
                ApplyMaterial(occupied: true);
            }
        }
    }

    // ─── Firing ──────────────────────────────────────────────────────────────

    void TryFire(PlayerController firer)
    {
        if (!Input.GetMouseButton(0)) return;
        if (Time.time < _nextFireTime) return;

        _nextFireTime = Time.time + 1f / Mathf.Max(0.01f, fireRate);
        Fire(firer);
    }

    void Fire(PlayerController firer)
    {
        if (barrelTip == null) return;

        var cam = Camera.main;
        if (cam == null) return;

        Ray screenRay    = cam.ScreenPointToRay(Input.mousePosition);
        Vector3 aimPoint = GetAimPoint(screenRay, firer);

        Vector3 rawDir = aimPoint - barrelTip.position;
        if (rawDir.sqrMagnitude < 0.001f) rawDir = barrelTip.forward;
        rawDir.Normalize();

        Transform axisRef = barrelAxisRef != null ? barrelAxisRef : barrelTip;
        Vector3 fireDir   = ClampToBarrelCone(rawDir, axisRef.forward, barrelClampDeg);

        // Spawn cannonball
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "Cannonball";
        go.transform.SetPositionAndRotation(barrelTip.position, Quaternion.LookRotation(fireDir, Vector3.up));
        float effectiveRadius = Mathf.Max(0.75f, projectileRadius);
        go.transform.localScale = Vector3.one * (effectiveRadius * 2f);

        var renderer = go.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = _runtimeProjectileMat != null ? _runtimeProjectileMat : renderer.sharedMaterial;

        var sphere = go.GetComponent<SphereCollider>();
        sphere.radius = 0.5f; // local scale handles actual size

        var rb = go.GetComponent<Rigidbody>();
        if (rb == null) rb = go.AddComponent<Rigidbody>();
        rb.interpolation          = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.useGravity             = false;
        rb.mass                   = projectileMass;

        var proj = go.AddComponent<CannonProjectile>();

        // Ignore cannon's own colliders and firer's colliders
        var cannonColliders  = GetComponentsInChildren<Collider>(true);
        var firerColliders   = firer.GetComponentsInChildren<Collider>(true);
        proj.IgnoreColliders(cannonColliders);
        proj.IgnoreColliders(firerColliders);

        proj.Launch(fireDir * projectileSpeed, _damageSystem, projectileLifetime, damageForceMultiplier, projectileGravityScale, impactBlastRadius);
    }

    Material BuildProjectileMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (shader == null)
            return null;

        var mat = new Material(shader);
        mat.color = Color.black;
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", Color.black);
        if (mat.HasProperty("_Color"))
            mat.SetColor("_Color", Color.black);
        return mat;
    }

    // ─── Aim helpers ─────────────────────────────────────────────────────────

    Vector3 GetAimPoint(Ray screenRay, PlayerController firer)
    {
        var firerColliders = firer.GetComponentsInChildren<Collider>(true);
        var hits = Physics.RaycastAll(screenRay, aimMaxDistance, aimMask, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var h in hits)
        {
            if (System.Array.IndexOf(firerColliders, h.collider) >= 0)
                continue;
            return h.point;
        }
        return screenRay.origin + screenRay.direction * Mathf.Min(aimMaxDistance, 200f);
    }

    static Vector3 ClampToBarrelCone(Vector3 dir, Vector3 barrelFwd, float maxDeg)
    {
        Vector3 dirFlat = new Vector3(dir.x, 0f, dir.z);
        Vector3 fwdFlat = new Vector3(barrelFwd.x, 0f, barrelFwd.z);

        if (dirFlat.sqrMagnitude < 0.001f) dirFlat = fwdFlat;
        if (fwdFlat.sqrMagnitude < 0.001f) fwdFlat = Vector3.forward;

        dirFlat.Normalize();
        fwdFlat.Normalize();

        float yaw        = Vector3.SignedAngle(fwdFlat, dirFlat, Vector3.up);
        float clampedYaw = Mathf.Clamp(yaw, -maxDeg, maxDeg);
        Vector3 clampedFlat = Quaternion.AngleAxis(clampedYaw, Vector3.up) * fwdFlat;

        return new Vector3(clampedFlat.x, dir.y, clampedFlat.z).normalized;
    }

    // ─── Material swap ────────────────────────────────────────────────────────

    void OnOccupancyChanged()
    {
        bool occupied = _fusionActive
            ? OccupiedBy != PlayerRef.None
            : _localOccupiedByMe;
        ApplyMaterial(occupied);
    }

    void ApplyMaterial(bool occupied)
    {
        Material mat = occupied ? occupiedMaterial : freeMaterial;
        if (mat == null || targetRenderers == null) return;

        foreach (var r in targetRenderers)
        {
            if (r == null) continue;
            var mats = r.materials;
            if (materialIndex >= 0 && materialIndex < mats.Length)
                mats[materialIndex] = mat;
            r.materials = mats;
        }
    }
}
