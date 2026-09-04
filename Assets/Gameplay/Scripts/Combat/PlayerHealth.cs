using Fusion;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;

using BricksBladesBlackpowder.Teams;

public class PlayerHealth : NetworkBehaviour, IDamageable
{
    TeamIdentity teamIdentity;

    [Header("Health")]
    public int maxHearts = 3;

    [Header("Health UI")]
    public GameObject Heart_1;
    public GameObject Heart_2;
    public GameObject Heart_3;

    [Header("Damage")]
    public float invulnerabilitySeconds = 0.6f;
    public float hurtGlowSeconds = 0.25f;
    public float knockbackUpward = 1.2f;

    [Header("Knockback")]
    public float knockbackDurationSeconds = 0.14f;

    [Header("Death")]
    public float respawnDelaySeconds = 2.5f;

    [Header("Death Sound")]
    public AudioSource deathSource;
    public AudioClip deathClip;
    public float deathVolume = 1f;
    public Vector2 deathPitchRange = new Vector2(0.95f, 1.05f);

    [Header("Break Apart")]
    public Transform breakPartsRoot;
    public Transform visualRoot;
    public float breakForce = 7.5f;
    public float breakUpwardForce = 1.5f;
    public float breakRandomForce = 2.0f;
    public float breakTorque = 12f;
    public float breakColliderInflate = 0.02f;

    [Header("Hurt Glow")]
    public Transform rendererRoot;
    public Renderer[] renderers;
    public Color hurtColor = new Color(1f, 0.2f, 0.2f, 1f);
    [Range(0f, 1f)]
    public float hurtOverlayStrength = 0.55f;
    public string baseColorProperty = "_BaseColor";
    public string altColorProperty = "_Color";
    public string emissionProperty = "_EmissionColor";
    public float emissionIntensity = 1.5f;

    [Header("Water Kill")]
    [SerializeField] Transform torsoReference;
    [SerializeField] string torsoPath = "Root/Hips/Torso";
    [SerializeField] float torsoKillOffset = 0f;

    [Networked] public int Hearts { get; private set; }
    [Networked] TickTimer InvulnTimer { get; set; }
    [Networked] TickTimer HurtTimer { get; set; }

    [Networked] NetworkBool NetIsDead { get; set; }
    bool _spawnedReady;
    public bool IsDead => _spawnedReady && NetIsDead;
    bool IDamageable.IsDead => IsDead;

    public TickTimer KnockbackTimerPublic => _spawnedReady ? KnockbackTimer : default;
    public Vector3 KnockbackDirPublic => _spawnedReady ? KnockbackDir : Vector3.zero;
    public float KnockbackSpeedPublic => _spawnedReady ? KnockbackSpeed : 0f;

    [Networked] TickTimer RespawnTimer { get; set; }
    [Networked] int DeathSeq { get; set; }
    [Networked] Vector3 DeathImpulseDir { get; set; }

    [Networked] public TickTimer KnockbackTimer { get; private set; }
    [Networked] public Vector3 KnockbackDir { get; private set; }
    [Networked] public float KnockbackSpeed { get; private set; }

    MaterialPropertyBlock block;
    bool glowApplied;
    bool deathApplied;
    int lastDeathSeq;

    NetworkCharacterController ncc;
    Vector3 spawnPos;
    Quaternion spawnRot;
    readonly List<GameObject> debris = new List<GameObject>(16);

    Animator[] cachedAnimators;
    MeshRenderer[] cachedMeshRenderers;
    RotationConstraint[] cachedRotationConstraints;
    bool[] defaultConstraintEnabledStates;
    float lastTorsoY;
    bool hasLastTorsoY;

    public override void Spawned()
    {
        _spawnedReady = true;

        TryGetComponent(out ncc);
        teamIdentity = ResolveTeamIdentity();

        if (!deathSource)
            TryGetComponent(out deathSource);

        if (deathSource)
        {
            deathSource.spatialBlend = 1f;
            deathSource.rolloffMode = AudioRolloffMode.Linear;
        }

        CacheTorsoReference();

        if (torsoReference)
        {
            lastTorsoY = torsoReference.position.y + torsoKillOffset;
            hasLastTorsoY = true;
        }

        if (!breakPartsRoot)
        {
            var r = transform.Find("Root");
            if (r) breakPartsRoot = r;
        }

        if (!visualRoot)
        {
            var r = transform.Find("Root");
            if (r) visualRoot = r;
        }

        if (Object.HasStateAuthority)
        {
            Hearts = Mathf.Clamp(maxHearts, 1, 9);
            NetIsDead = false;
            RespawnTimer = default;
            DeathSeq = 0;
            DeathImpulseDir = Vector3.forward;

            KnockbackTimer = default;
            KnockbackDir = Vector3.forward;
            KnockbackSpeed = 0f;

            spawnPos = transform.position;
            spawnRot = transform.rotation;
        }

        if (Object.HasInputAuthority)
        {
            if (!Heart_1) Heart_1 = GameObject.Find("Heart_1");
            if (!Heart_2) Heart_2 = GameObject.Find("Heart_2");
            if (!Heart_3) Heart_3 = GameObject.Find("Heart_3");
        }

        CacheRenderersIfNeeded();
        CacheAnimators();
        CacheMeshRenderers();
        CacheRotationConstraints();
        block ??= new MaterialPropertyBlock();
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        _spawnedReady = false;
    }

    public override void FixedUpdateNetwork()
    {
        if (!Object.HasStateAuthority) return;

        if (!IsDead && ShouldDieFromWater())
        {
            Die(Vector3.up);
            return;
        }

        if (IsDead && RespawnTimer.IsRunning && RespawnTimer.Expired(Runner))
        {
            Respawn();
        }
    }

    public override void Render()
    {
        UpdateHeartsDisplay();

        if (IsDead)
        {
            if (!deathApplied || lastDeathSeq != DeathSeq)
            {
                lastDeathSeq = DeathSeq;
                deathApplied = true;
                ApplyDeathVisuals();
            }
        }
        else if (deathApplied)
        {
            deathApplied = false;
            ClearDeathVisuals();
        }

        bool shouldGlow = HurtTimer.IsRunning && HurtTimer.ExpiredOrNotRunning(Runner) == false;

        if (shouldGlow != glowApplied)
        {
            glowApplied = shouldGlow;
            ApplyGlow(glowApplied);
        }
    }

    public bool CanTakeDamage()
    {
        return !(InvulnTimer.IsRunning && InvulnTimer.ExpiredOrNotRunning(Runner) == false);
    }

    public Team GetTeam()
    {
        if (!teamIdentity)
            teamIdentity = ResolveTeamIdentity();

        return teamIdentity ? teamIdentity.team : Team.None;
    }

    public bool TryTakeDamage(GameObject attacker, int heartsDamage, Vector3 hitFromDir, float knockback)
    {
        if (!Object.HasStateAuthority) return false;
        if (Hearts <= 0) return false;
        if (IsDead) return false;
        if (heartsDamage <= 0) return false;
        if (!CanTakeDamage()) return false;

        if (attacker)
        {
            var attackerTeam = ResolveTeamFromObject(attacker);
            if (attackerTeam != Team.None && attackerTeam == GetTeam())
                return false;
        }

        var blocker = GetComponent<PlayerController>();

        if (blocker != null && blocker.IsBlockingPublic)
        {
            if (knockback > 0f)
            {
                hitFromDir.y = 0f;

                if (hitFromDir.sqrMagnitude > 0.0001f)
                {
                    hitFromDir.Normalize();
                    KnockbackDir = hitFromDir;
                    KnockbackSpeed = knockback * 0.3f;
                    KnockbackTimer = TickTimer.CreateFromSeconds(Runner, knockbackDurationSeconds * 0.5f);
                }
            }

            InvulnTimer = TickTimer.CreateFromSeconds(Runner, invulnerabilitySeconds * 0.5f);
            return false;
        }

        Hearts = Mathf.Max(0, Hearts - heartsDamage);

        if (knockback > 0f)
        {
            hitFromDir.y = 0f;

            if (hitFromDir.sqrMagnitude > 0.0001f)
            {
                hitFromDir.Normalize();

                KnockbackDir = hitFromDir;
                KnockbackSpeed = knockback;
                KnockbackTimer = TickTimer.CreateFromSeconds(Runner, knockbackDurationSeconds);
            }
        }

        InvulnTimer = TickTimer.CreateFromSeconds(Runner, invulnerabilitySeconds);
        HurtTimer = TickTimer.CreateFromSeconds(Runner, hurtGlowSeconds);

        UpdateHeartsDisplay();

        if (Hearts <= 0)
        {
            Die(hitFromDir);
        }

        return true;
    }

    TeamIdentity ResolveTeamIdentity()
    {
        return GetComponent<TeamIdentity>()
            ?? GetComponentInParent<TeamIdentity>()
            ?? GetComponentInChildren<TeamIdentity>(true);
    }

    static Team ResolveTeamFromObject(GameObject obj)
    {
        if (!obj)
            return Team.None;

        var id = obj.GetComponent<TeamIdentity>()
            ?? obj.GetComponentInParent<TeamIdentity>()
            ?? obj.GetComponentInChildren<TeamIdentity>(true);

        return id ? id.team : Team.None;
    }

    void CacheTorsoReference()
    {
        if (torsoReference)
            return;

        if (!string.IsNullOrEmpty(torsoPath))
            torsoReference = transform.Find(torsoPath);

        if (!torsoReference)
            torsoReference = transform;
    }

    bool ShouldDieFromWater()
    {
        CacheTorsoReference();

        if (torsoReference == null)
            return false;

        if (!WaterLevelProvider.TryGetWaterLevel(out float waterLevelY))
            return false;

        float torsoY = torsoReference.position.y + torsoKillOffset;

        if (!hasLastTorsoY)
        {
            lastTorsoY = torsoY;
            hasLastTorsoY = true;
            return false;
        }

        bool crossedDownward = lastTorsoY >= waterLevelY && torsoY < waterLevelY;
        lastTorsoY = torsoY;

        return crossedDownward;
    }

    void UpdateHeartsDisplay()
    {
        if (!Object.HasInputAuthority) return;

        if (Heart_1) Heart_1.SetActive(Hearts >= 1);
        if (Heart_2) Heart_2.SetActive(Hearts >= 2);
        if (Heart_3) Heart_3.SetActive(Hearts >= 3);
    }

    void Die(Vector3 impulseDir)
    {
        NetIsDead = true;
        RespawnTimer = TickTimer.CreateFromSeconds(Runner, respawnDelaySeconds);
        DeathSeq++;

        impulseDir.y = 0f;

        if (impulseDir.sqrMagnitude < 0.001f)
            impulseDir = transform.forward;

        impulseDir.Normalize();
        DeathImpulseDir = impulseDir;

        if (ncc)
        {
            ncc.Velocity = Vector3.zero;
        }

        if (TryGetComponent(out HitboxRoot hr))
            hr.enabled = false;
    }

    void Respawn()
    {
        Hearts = Mathf.Clamp(maxHearts, 1, 9);
        NetIsDead = false;
        InvulnTimer = TickTimer.CreateFromSeconds(Runner, invulnerabilitySeconds);
        HurtTimer = default;

        if (ncc)
            ncc.Teleport(spawnPos, spawnRot);
        else
            transform.SetPositionAndRotation(spawnPos, spawnRot);

        if (TryGetComponent(out HitboxRoot hr))
            hr.enabled = true;

        if (torsoReference)
        {
            lastTorsoY = torsoReference.position.y + torsoKillOffset;
            hasLastTorsoY = true;
        }
    }

    void CacheRenderersIfNeeded()
    {
        if (renderers != null && renderers.Length > 0) return;

        Transform root = rendererRoot ? rendererRoot : transform;
        renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
    }

    void CacheAnimators()
    {
        cachedAnimators = GetComponentsInChildren<Animator>(includeInactive: true);
    }

    void CacheRotationConstraints()
    {
        cachedRotationConstraints = GetComponentsInChildren<RotationConstraint>(includeInactive: true);

        if (cachedRotationConstraints == null || cachedRotationConstraints.Length == 0)
        {
            defaultConstraintEnabledStates = null;
            return;
        }

        defaultConstraintEnabledStates = new bool[cachedRotationConstraints.Length];

        for (int i = 0; i < cachedRotationConstraints.Length; i++)
        {
            var c = cachedRotationConstraints[i];
            defaultConstraintEnabledStates[i] = c != null && c.enabled;
        }
    }

    void CacheMeshRenderers()
    {
        Transform root = breakPartsRoot ? breakPartsRoot : (rendererRoot ? rendererRoot : transform);
        cachedMeshRenderers = root.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
    }

    void ApplyGlow(bool on)
    {
        CacheRenderersIfNeeded();

        if (renderers == null || renderers.Length == 0) return;

        if (!on)
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i])
                    renderers[i].SetPropertyBlock(null);
            }

            return;
        }

        block ??= new MaterialPropertyBlock();

        float strength = Mathf.Clamp01(hurtOverlayStrength);
        Color emission = hurtColor * (Mathf.Max(0f, emissionIntensity) * strength);

        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];

            if (!r) continue;

            var m = r.sharedMaterial;

            if (!m) continue;

            block.Clear();
            r.GetPropertyBlock(block);

            if (m.HasProperty(baseColorProperty))
            {
                Color baseCol = m.GetColor(baseColorProperty);
                Color blended = Color.Lerp(baseCol, hurtColor, strength);
                blended.a = baseCol.a;
                block.SetColor(baseColorProperty, blended);
            }
            else if (!string.IsNullOrWhiteSpace(altColorProperty) && m.HasProperty(altColorProperty))
            {
                Color baseCol = m.GetColor(altColorProperty);
                Color blended = Color.Lerp(baseCol, hurtColor, strength);
                blended.a = baseCol.a;
                block.SetColor(altColorProperty, blended);
            }

            if (!string.IsNullOrWhiteSpace(emissionProperty) && m.HasProperty(emissionProperty))
                block.SetColor(emissionProperty, emission);

            r.SetPropertyBlock(block);
        }
    }

    void ApplyDeathVisuals()
    {
        PlayDeathSound();

        SpawnDebris();

        if (visualRoot)
        {
            visualRoot.gameObject.SetActive(false);
        }
        else
        {
            CacheRenderersIfNeeded();

            if (renderers != null)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i]) renderers[i].enabled = false;
                }
            }
        }

        if (cachedAnimators != null)
        {
            for (int i = 0; i < cachedAnimators.Length; i++)
            {
                if (cachedAnimators[i]) cachedAnimators[i].enabled = false;
            }
        }

        if (TryGetComponent(out CharacterController cc))
            cc.enabled = false;
    }

    void PlayDeathSound()
    {
        if (!deathSource || !deathClip)
            return;

        deathSource.pitch = Random.Range(
            deathPitchRange.x,
            deathPitchRange.y
        );

        deathSource.PlayOneShot(
            deathClip,
            deathVolume
        );
    }

    void ClearDeathVisuals()
    {
        if (visualRoot)
        {
            visualRoot.gameObject.SetActive(true);
        }
        else
        {
            CacheRenderersIfNeeded();

            if (renderers != null)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i]) renderers[i].enabled = true;
                }
            }
        }

        if (cachedAnimators != null)
        {
            for (int i = 0; i < cachedAnimators.Length; i++)
            {
                if (cachedAnimators[i]) cachedAnimators[i].enabled = true;
            }
        }

        RestoreAnimationDrivenState();

        if (TryGetComponent(out CharacterController cc))
            cc.enabled = true;

        for (int i = 0; i < debris.Count; i++)
        {
            if (debris[i]) Destroy(debris[i]);
        }

        debris.Clear();
    }

    void RestoreAnimationDrivenState()
    {
        if (cachedAnimators != null)
        {
            for (int i = 0; i < cachedAnimators.Length; i++)
            {
                var anim = cachedAnimators[i];

                if (!anim) continue;

                anim.Rebind();
                anim.Update(0f);
            }
        }

        if (cachedRotationConstraints != null && defaultConstraintEnabledStates != null)
        {
            int len = Mathf.Min(cachedRotationConstraints.Length, defaultConstraintEnabledStates.Length);

            for (int i = 0; i < len; i++)
            {
                var c = cachedRotationConstraints[i];

                if (!c) continue;

                c.enabled = defaultConstraintEnabledStates[i];
            }
        }
    }

    static readonly List<Material> SharedMatBuffer = new List<Material>(8);

    void SpawnDebris()
    {
        if (cachedMeshRenderers == null) return;

        for (int i = 0; i < cachedMeshRenderers.Length; i++)
        {
            var mr = cachedMeshRenderers[i];

            if (!mr) continue;

            var mf = mr.GetComponent<MeshFilter>();

            if (!mf || !mf.sharedMesh) continue;

            var go = new GameObject("debris");

            go.transform.SetPositionAndRotation(mr.transform.position, mr.transform.rotation);
            go.transform.localScale = mr.transform.lossyScale;

            var newMf = go.AddComponent<MeshFilter>();
            newMf.sharedMesh = mf.sharedMesh;

            var newMr = go.AddComponent<MeshRenderer>();

            SharedMatBuffer.Clear();
            mr.GetSharedMaterials(SharedMatBuffer);
            newMr.SetSharedMaterials(SharedMatBuffer);

            if (mf.sharedMesh.isReadable)
            {
                var col = go.AddComponent<MeshCollider>();
                col.sharedMesh = mf.sharedMesh;
                col.convex = true;
            }
            else
            {
                var box = go.AddComponent<BoxCollider>();
                box.center = mf.sharedMesh.bounds.center;
                box.size = mf.sharedMesh.bounds.size;
            }

            if (breakColliderInflate > 0f)
                go.transform.localScale *= (1f + breakColliderInflate);

            var rb = go.AddComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            Vector3 rand = Random.insideUnitSphere * breakRandomForce;
            Vector3 dir = DeathImpulseDir;
            dir.y = 0f;

            if (dir.sqrMagnitude < 0.001f)
                dir = Vector3.forward;

            dir.Normalize();

            Vector3 force =
                dir * breakForce +
                Vector3.up * breakUpwardForce +
                rand;

            rb.AddForce(force, ForceMode.VelocityChange);
            rb.AddTorque(Random.insideUnitSphere * breakTorque, ForceMode.VelocityChange);

            debris.Add(go);
        }
    }
}