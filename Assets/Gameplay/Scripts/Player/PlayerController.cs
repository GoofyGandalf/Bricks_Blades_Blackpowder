using Fusion;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(NetworkCharacterController))]
[RequireComponent(typeof(PlayerHealth))]
public class PlayerController : NetworkBehaviour
{
    [Header("Instances")]
    public Animator humanoidAnimator;
    public Animator legoAnimator;

    [Header("Speed")]
    public float walkSpeed = 12f;
    public float runSpeed  = 20f;

    [Header("Acceleration")]
    public float walkAcceleration = 60f;
    public float runAcceleration  = 80f;
    public float braking          = 120f;

    [Header("Rotation")]
    public float rotationSpeed = 720f;
    public float combatRotationSpeed = 1080f;

    [Header("Melee")]
    public float meleeRange = 2.2f;
    public float meleeRadius = 0.9f;
    public float meleeHeight = 1.0f;
    public float meleeHitDelaySeconds = 0.12f;
    public float meleeHitDelayStep2Seconds = 0.10f;
    public float meleeHitDelayStep3Seconds = 0.08f;
    public float meleeHitWindowSeconds = 0.18f;
    public float meleeComboResetSeconds = 0.8f;
    public int meleeDamageHearts = 1;
    public float meleeKnockback = 14f;
    public float knockbackAcceleration = 220f;
    public LayerMask meleeHitMask = ~0;
    public QueryTriggerInteraction meleeQueryTriggers = QueryTriggerInteraction.Ignore;
    public HitOptions meleeHitOptions = HitOptions.IncludePhysX;

    [Header("Stair Stepping")]
    public float maxStepHeight = 0.45f;
    public float stepCheckDistance = 0.55f;
    public LayerMask stairLayerMask = ~0;

    [Header("Footsteps")]
    public AudioSource footstepSource;
    public AudioClip[] footstepClips;
    public float footstepVolume = 1f;
    public Vector2 footstepPitchRange = new Vector2(0.9f, 1.1f);
    public float footstepSpeedThreshold = 0.15f;
    public float stepIntervalSlow = 0.55f;
    public float stepIntervalFast = 0.32f;
    public bool useAnimationEvents = true;

    [Header("Attack Sound")]
    public AudioSource attackSource;
    public AudioClip attackSwingClip;
    public float attackVolume = 1f;
    public Vector2 attackPitchRange = new Vector2(0.95f, 1.05f);

    [Networked, OnChangedRender(nameof(OnAnimSpeedChanged))] float AnimSpeed { get; set; }
    [Networked, OnChangedRender(nameof(OnAnimStrafeChanged))] float AnimStrafe { get; set; }
    [Networked] int AttackSeq { get; set; }
    [Networked] int FootstepSeq { get; set; }
    [Networked] int FootstepIndex { get; set; }
    [Networked] float FootstepPitch { get; set; }

    [Networked, OnChangedRender(nameof(OnBlockChanged))] NetworkBool IsBlocking { get; set; }

    [Networked] int ServerComboStep { get; set; }
    [Networked] int LastAttackTick { get; set; }
    [Networked] int PendingHitCount { get; set; }
    [Networked] int PendingHitTick0 { get; set; }
    [Networked] int PendingHitTick1 { get; set; }
    [Networked] int PendingHitTick2 { get; set; }
    [Networked] int PendingHitEndTick0 { get; set; }
    [Networked] int PendingHitEndTick1 { get; set; }
    [Networked] int PendingHitEndTick2 { get; set; }
    [Networked] int PendingHitStep0 { get; set; }
    [Networked] int PendingHitStep1 { get; set; }
    [Networked] int PendingHitStep2 { get; set; }
    [Networked] int PendingHitApplied0 { get; set; }
    [Networked] int PendingHitApplied1 { get; set; }
    [Networked] int PendingHitApplied2 { get; set; }

    public static PlayerController Local { get; private set; }

    NetworkCharacterController ncc;
    CharacterController _cc;
    PlayerHealth health;
    IDamageable damageable;
    BrickDamageSystem _brickDamageSystem;

    static readonly List<LagCompensatedHit> LagHits = new List<LagCompensatedHit>(16);
    static readonly Collider[] PhysXHitBuffer = new Collider[16];
    static readonly int SpeedHash = Animator.StringToHash("Speed");
    static readonly int SwingHash = Animator.StringToHash("Swing");
    static readonly int ComboHash = Animator.StringToHash("Combo");
    static readonly int QueueNextHash = Animator.StringToHash("QueueNext");
    static readonly int Strafe_Hash = Animator.StringToHash("Strafe");
    static readonly int BlockHash = Animator.StringToHash("Block");

    // Expected state names from your Animator graph
    static readonly int EmptyStateHash = Animator.StringToHash("Empty");
    static readonly int Swing1StateHash = Animator.StringToHash("Swing #1");
    static readonly int Swing2StateHash = Animator.StringToHash("Swing #2");
    static readonly int Swing3StateHash = Animator.StringToHash("Swing #3");

    [Header("Combat Combo")]
    public string combatLayerName = "Combat Layer";
    public float comboInputGrace = 0.4f;
    int lastAttackSeq;
    int lastFootstepSeq;
    float footstepTimer;

    int humanoidCombatLayer = -1;
    int legoCombatLayer = -1;
    int currentComboStep;
    bool queuedNext;
    float lastComboInputTime;
    int lastHumanoidStateHash;
    int lastLegoStateHash;
    bool clearedForCurrentDeath;
    Transform leftArm;
    Transform rightArm;
    Quaternion leftArmDefaultLocalRotation;
    Quaternion rightArmDefaultLocalRotation;
    bool hasArmDefaults;

    void Awake()
    {
        ncc = GetComponent<NetworkCharacterController>();
        TryGetComponent(out _cc);
        TryGetComponent(out health);
        TryGetComponent(out damageable);
        if (!footstepSource) TryGetComponent(out footstepSource);
        if (footstepSource)
        {
            footstepSource.spatialBlend = 1f;
            footstepSource.rolloffMode = AudioRolloffMode.Linear;
        }

        if (!attackSource) TryGetComponent(out attackSource);
        if (attackSource)
        {
            attackSource.spatialBlend = 1f;
            attackSource.rolloffMode = AudioRolloffMode.Linear;
        }

        if (humanoidAnimator) humanoidCombatLayer = humanoidAnimator.GetLayerIndex(combatLayerName);
        if (legoAnimator) legoCombatLayer = legoAnimator.GetLayerIndex(combatLayerName);
    }

    public override void Spawned()
    {
        _brickDamageSystem = FindAnyObjectByType<BrickDamageSystem>();
        CacheArmDefaults();

        if (!Object.HasInputAuthority) return;

        Local = this;

        var cam = FindFirstObjectByType<PlayerFollowCamera>();
        if (cam)
        {
            cam.SetTarget(transform, snapPosition: true);
        }
    }

    public override void FixedUpdateNetwork()
    {
        if (health != null && health.IsDead)
        {
            if (!clearedForCurrentDeath)
            {
                if (Object.HasStateAuthority)
                    ClearServerCombatQueues();

                ResetCombatVisualState();
                clearedForCurrentDeath = true;
            }

            return;
        }

        if (clearedForCurrentDeath)
        {
            ResetCombatVisualState();
            clearedForCurrentDeath = false;
        }

        // If recently hit, apply a forced shove that isn't instantly cancelled by braking.
        if (health != null && health.KnockbackTimerPublic.IsRunning && !health.KnockbackTimerPublic.Expired(Runner))
        {
            Vector3 kbDir = health.KnockbackDirPublic;
            kbDir.y = 0f;
            if (kbDir.sqrMagnitude > 0.0001f)
            {
                kbDir.Normalize();
                ncc.maxSpeed = Mathf.Max(ncc.maxSpeed, health.KnockbackSpeedPublic);
                ncc.acceleration = Mathf.Max(ncc.acceleration, knockbackAcceleration);
                ncc.braking = 0f;
                ncc.rotationSpeed = 0f;
                ncc.Move(kbDir);
            }

            SetAnimStrafe(0f);
            if (Object.HasStateAuthority) ProcessPendingMeleeHits();
            return;
        }

        if (GetInput(out NetworkInputData data))
        {
            Vector3 dir = data.direction;
            if (dir.sqrMagnitude > 1f) dir.Normalize();

            bool running = data.running;

            ncc.maxSpeed = running ? runSpeed : walkSpeed;
            ncc.acceleration = running ? runAcceleration : walkAcceleration;
            ncc.braking = braking;

            ncc.rotationSpeed = 0f;
            TryStepUp(dir);
            ncc.Move(dir);

            if (!running && data.combat && data.lookDirection.sqrMagnitude > 0.001f)
            {
                Vector3 lookDir = data.lookDirection;
                lookDir.y = 0f;
                if (lookDir.sqrMagnitude > 0.001f)
                {
                    Quaternion targetRot = Quaternion.LookRotation(lookDir);
                    transform.rotation = Quaternion.RotateTowards(
                        transform.rotation,
                        targetRot,
                        combatRotationSpeed * Runner.DeltaTime
                    );
                }

                lookDir.Normalize();
                Vector3 lookRight = Vector3.Cross(Vector3.up, lookDir);
                float strafeAxis  = Vector3.Dot(dir, lookRight);
                float forwardAxis = Vector3.Dot(dir, lookDir);

                if (forwardAxis < -0.25f) strafeAxis = -strafeAxis;
                SetAnimStrafe(dir.sqrMagnitude < 0.01f ? 0f : Mathf.Clamp(strafeAxis, -1f, 1f));
            }
            else
            {
                SetAnimStrafe(0f);

                if (dir.sqrMagnitude > 0.001f)
                {
                    Quaternion targetRot = Quaternion.LookRotation(dir);
                    transform.rotation = Quaternion.RotateTowards(
                        transform.rotation,
                        targetRot,
                        rotationSpeed * Runner.DeltaTime
                    );
                }
            }

            float inputMag = dir.magnitude;
            if (inputMag < 0.1f) inputMag = 0f;

            SetAnimSpeed(inputMag * (running ? 2f : 1f));

            if (Object.HasStateAuthority)
                IsBlocking = data.block && !data.attack && !data.running;

            if (data.attack && !IsBlocking)
            {
                AttackSeq++;
                QueueMeleeHit();
            }

            if (!useAnimationEvents && Object.HasStateAuthority)
            {
                UpdateFootstepsServer();
            }
        }
        else
        {
            SetAnimStrafe(0f);
        }

        if (Object.HasStateAuthority) ProcessPendingMeleeHits();
    }

    void TryStepUp(Vector3 moveDir)
    {
        if (_cc == null || !ncc.Grounded) return;
        Vector3 flatDir = new Vector3(moveDir.x, 0f, moveDir.z);
        if (flatDir.sqrMagnitude < 0.001f) return;
        flatDir.Normalize();
        Vector3 pos = transform.position;
        // Probe 1: foot-level riser must exist
        if (!Physics.Raycast(pos + Vector3.up * 0.05f, flatDir, stepCheckDistance, stairLayerMask, QueryTriggerInteraction.Ignore))
            return;
        // Probe 2: above step height must be clear (not a wall)
        if (Physics.Raycast(pos + Vector3.up * (maxStepHeight + 0.05f), flatDir, stepCheckDistance, stairLayerMask, QueryTriggerInteraction.Ignore))
            return;
        // Probe 3: find exact step top surface
        Vector3 downOrigin = pos + Vector3.up * (maxStepHeight + 0.05f) + flatDir * stepCheckDistance;
        if (!Physics.Raycast(downOrigin, Vector3.down, out RaycastHit hit, maxStepHeight + 0.15f, stairLayerMask, QueryTriggerInteraction.Ignore))
            return;
        float stepUp = hit.point.y - pos.y;
        if (stepUp <= 0.01f || stepUp > maxStepHeight) return;
        _cc.Move(Vector3.up * (stepUp + 0.01f));
    }

    void SetAnimSpeed(float value)
    {
        if (Mathf.Abs(AnimSpeed - value) > 0.001f)
            AnimSpeed = value;
    }

    void SetAnimStrafe(float value)
    {
        if (Mathf.Abs(AnimStrafe - value) > 0.001f)
            AnimStrafe = value;
    }

    void QueueMeleeHit()
    {
        int comboResetTicks = Mathf.Max(1, Mathf.RoundToInt(meleeComboResetSeconds * Runner.TickRate));
        if (LastAttackTick > 0 && (Runner.Tick - LastAttackTick) > comboResetTicks)
        {
            ServerComboStep = 0;
        }

        ServerComboStep = Mathf.Clamp(ServerComboStep + 1, 1, 3);
        LastAttackTick = Runner.Tick;

        if (PendingHitCount >= 3)
            return;

        float delaySeconds = ServerComboStep == 1 ? meleeHitDelaySeconds : (ServerComboStep == 2 ? meleeHitDelayStep2Seconds : meleeHitDelayStep3Seconds);
        int delayTicks = Mathf.Max(0, Mathf.RoundToInt(delaySeconds * Runner.TickRate));
        int windowTicks = Mathf.Max(1, Mathf.RoundToInt(meleeHitWindowSeconds * Runner.TickRate));
        int hitTick = Runner.Tick + delayTicks;
        int endTick = hitTick + windowTicks;

        if (PendingHitCount == 0)
        {
            PendingHitTick0 = hitTick;
            PendingHitEndTick0 = endTick;
            PendingHitStep0 = ServerComboStep;
            PendingHitApplied0 = 0;
        }
        else if (PendingHitCount == 1)
        {
            PendingHitTick1 = hitTick;
            PendingHitEndTick1 = endTick;
            PendingHitStep1 = ServerComboStep;
            PendingHitApplied1 = 0;
        }
        else
        {
            PendingHitTick2 = hitTick;
            PendingHitEndTick2 = endTick;
            PendingHitStep2 = ServerComboStep;
            PendingHitApplied2 = 0;
        }

        PendingHitCount++;
    }

    void ProcessPendingMeleeHits()
    {
        if (PendingHitCount <= 0)
            return;

        if (Runner.Tick < PendingHitTick0)
            return;

        // Expired or already applied -> pop
        if (PendingHitApplied0 != 0 || Runner.Tick > PendingHitEndTick0)
        {
            PopPendingHit();
            return;
        }

        if (DoMeleeHit(PendingHitStep0))
        {
            PendingHitApplied0 = 1;
            PopPendingHit();
        }
    }

    void PopPendingHit()
    {
        PendingHitTick0 = PendingHitTick1;
        PendingHitEndTick0 = PendingHitEndTick1;
        PendingHitStep0 = PendingHitStep1;
        PendingHitApplied0 = PendingHitApplied1;

        PendingHitTick1 = PendingHitTick2;
        PendingHitEndTick1 = PendingHitEndTick2;
        PendingHitStep1 = PendingHitStep2;
        PendingHitApplied1 = PendingHitApplied2;

        PendingHitTick2 = 0;
        PendingHitEndTick2 = 0;
        PendingHitStep2 = 0;
        PendingHitApplied2 = 0;

        PendingHitCount--;
    }

    bool DoMeleeHit(int comboStep)
    {
        if (meleeDamageHearts <= 0)
            return false;

        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;
        forward.Normalize();

        // Cover both close and mid-range so you don't have to stand at an exact distance.
        Vector3 basePos = transform.position + Vector3.up * meleeHeight;
        Vector3 centerNear = basePos + forward * (meleeRange * 0.55f);
        Vector3 centerFar = basePos + forward * meleeRange;

        // Prefer Fusion lag compensation when available.
        if (Runner.LagCompensation != null)
        {
            if (TryLagCompHit(centerNear)) return true;
            if (TryLagCompHit(centerFar)) return true;

            // Some targets (like simple NPC colliders without HitboxRoot)
            // are not represented in lag-comp history; fallback to server PhysX.
            if (TryPhysXHit(centerNear)) return true;
            if (TryPhysXHit(centerFar)) return true;
            return false;
        }

        // Fallback: server-side physics overlap.
        if (TryPhysXHit(centerNear)) return true;
        if (TryPhysXHit(centerFar)) return true;

        return false;
    }

    bool TryLagCompHit(Vector3 center)
    {
        LagHits.Clear();
        Runner.LagCompensation.OverlapSphere(center, meleeRadius, Object.InputAuthority, LagHits, meleeHitMask, meleeHitOptions);
        return ApplyHits(LagHits);
    }

    bool TryPhysXHit(Vector3 center)
    {
        int count = Physics.OverlapSphereNonAlloc(center, meleeRadius, PhysXHitBuffer, meleeHitMask, meleeQueryTriggers);
        for (int i = 0; i < count; i++)
        {
            if (!PhysXHitBuffer[i]) continue;
            var target = PhysXHitBuffer[i].GetComponentInParent<IDamageable>();
            if (target == null) continue;
            if (ReferenceEquals(target, damageable)) continue;
            var targetHealth = PhysXHitBuffer[i].GetComponentInParent<PlayerHealth>();
            if (targetHealth != null && (!targetHealth.Object || !targetHealth.Object.HasStateAuthority)) continue;

            Vector3 dir = ((Component)target).transform.position - transform.position;
            if (dir.sqrMagnitude < 0.001f) dir = transform.forward;
            if (target.TryTakeDamage(gameObject, meleeDamageHearts, dir, meleeKnockback))
                return true;
        }
        return false;
    }

    bool ApplyHits(List<LagCompensatedHit> hits)
    {
        if (hits == null || hits.Count <= 0)
            return false;

        IDamageable damaged = null;

        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];

            var target = hit.GameObject ? hit.GameObject.GetComponentInParent<IDamageable>() : null;
            if (target == null) continue;
            if (ReferenceEquals(target, damageable)) continue;
            if (damaged == target) continue;
            var targetHealth = hit.GameObject ? hit.GameObject.GetComponentInParent<PlayerHealth>() : null;
            if (targetHealth != null && (!targetHealth.Object || !targetHealth.Object.HasStateAuthority)) continue;

            Vector3 dir = ((Component)target).transform.position - transform.position;
            if (dir.sqrMagnitude < 0.001f) dir = transform.forward;
            if (target.TryTakeDamage(gameObject, meleeDamageHearts, dir, meleeKnockback))
            {
                damaged = target;
                return true;
            }
        }

        return damaged != null;
    }

    public override void Render()
    {
        if (health != null && health.IsDead)
        {
            ResetCombatVisualState();
            return;
        }

        ApplyAnimSpeed();
        ApplyAnimStrafe();
        ApplyAttackCombo();
        ApplyBlock();
        ApplyFootstep();
    }

    public bool IsBlockingPublic => IsBlocking;

    void OnAnimSpeedChanged()
    {
        ApplyAnimSpeed();
    }

    void OnAnimStrafeChanged()
    {
        ApplyAnimStrafe();
    }

    void OnBlockChanged()
    {
        ApplyBlock();
    }

    void ApplyBlock()
    {
        if (humanoidAnimator) humanoidAnimator.SetBool(BlockHash, IsBlocking);
        if (legoAnimator) legoAnimator.SetBool(BlockHash, IsBlocking);

        if (IsBlocking)
            SetCombatLayerWeight(1f);
    }

    void ClearServerCombatQueues()
    {
        IsBlocking = false;
        ServerComboStep = 0;
        LastAttackTick = 0;

        PendingHitCount = 0;
        PendingHitTick0 = 0;
        PendingHitTick1 = 0;
        PendingHitTick2 = 0;
        PendingHitEndTick0 = 0;
        PendingHitEndTick1 = 0;
        PendingHitEndTick2 = 0;
        PendingHitStep0 = 0;
        PendingHitStep1 = 0;
        PendingHitStep2 = 0;
        PendingHitApplied0 = 0;
        PendingHitApplied1 = 0;
        PendingHitApplied2 = 0;

        AnimSpeed = 0f;
        AnimStrafe = 0f;
    }

    void ResetCombatVisualState()
    {
        lastAttackSeq = AttackSeq;
        lastComboInputTime = -999f;
        currentComboStep = 0;
        queuedNext = false;
        lastHumanoidStateHash = 0;
        lastLegoStateHash = 0;

        if (humanoidAnimator)
        {
            humanoidAnimator.ResetTrigger(SwingHash);
            humanoidAnimator.SetInteger(ComboHash, 0);
            humanoidAnimator.SetBool(QueueNextHash, false);
            humanoidAnimator.SetBool(BlockHash, false);
            humanoidAnimator.SetFloat(SpeedHash, 0f);
            humanoidAnimator.SetFloat(Strafe_Hash, 0f);
            if (humanoidCombatLayer >= 0)
                humanoidAnimator.Play(EmptyStateHash, humanoidCombatLayer, 0f);
            humanoidAnimator.Update(0f);
        }

        if (legoAnimator)
        {
            legoAnimator.ResetTrigger(SwingHash);
            legoAnimator.SetInteger(ComboHash, 0);
            legoAnimator.SetBool(QueueNextHash, false);
            legoAnimator.SetBool(BlockHash, false);
            legoAnimator.SetFloat(SpeedHash, 0f);
            if (legoCombatLayer >= 0)
                legoAnimator.Play(EmptyStateHash, legoCombatLayer, 0f);
            legoAnimator.Update(0f);
        }

        SetCombatLayerWeight(0f);
        RestoreArmDefaults();
        footstepTimer = 0f;
    }

    void CacheArmDefaults()
    {
        if (hasArmDefaults)
            return;

        leftArm = transform.Find("Root/Hips/Torso/LeftArm");
        rightArm = transform.Find("Root/Hips/Torso/RightArm");
        if (leftArm == null)
            leftArm = FindChildByName(transform, "LeftArm");
        if (rightArm == null)
            rightArm = FindChildByName(transform, "RightArm");

        if (leftArm != null)
            leftArmDefaultLocalRotation = leftArm.localRotation;
        if (rightArm != null)
            rightArmDefaultLocalRotation = rightArm.localRotation;

        hasArmDefaults = leftArm != null || rightArm != null;
    }

    void RestoreArmDefaults()
    {
        if (!hasArmDefaults)
            CacheArmDefaults();

        if (leftArm != null)
            leftArm.localRotation = leftArmDefaultLocalRotation;
        if (rightArm != null)
            rightArm.localRotation = rightArmDefaultLocalRotation;
    }

    static Transform FindChildByName(Transform root, string name)
    {
        if (root == null)
            return null;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (child.name == name)
                return child;

            Transform nested = FindChildByName(child, name);
            if (nested != null)
                return nested;
        }

        return null;
    }

    void ApplyAnimSpeed()
    {
        if (humanoidAnimator)
        {
            humanoidAnimator.SetFloat(SpeedHash, AnimSpeed);
        }
    }

    void ApplyAnimStrafe()
    {
        if (!humanoidAnimator) return;
        humanoidAnimator.SetFloat(Strafe_Hash, AnimStrafe);
    }

    void ApplyAttackCombo()
    {
        while (AttackSeq != lastAttackSeq)
        {
            lastAttackSeq++;
            OnAttackPressed();
        }

        UpdateComboFromAnimator(humanoidAnimator, humanoidCombatLayer, ref lastHumanoidStateHash);
        UpdateComboFromAnimator(legoAnimator, legoCombatLayer, ref lastLegoStateHash);

        if (currentComboStep == 0) return;
        if (Time.time - lastComboInputTime <= comboInputGrace) return;

        bool humanoidIdle = IsAnimatorInEmpty(humanoidAnimator, humanoidCombatLayer);
        bool legoIdle = IsAnimatorInEmpty(legoAnimator, legoCombatLayer);
        if (humanoidIdle && legoIdle)
        {
            ResetComboParams();
        }
    }

    void OnAttackPressed()
    {
        lastComboInputTime = Time.time;

        bool anySwinging = IsAnimatorSwinging(humanoidAnimator, humanoidCombatLayer) || IsAnimatorSwinging(legoAnimator, legoCombatLayer);

        if (!anySwinging || currentComboStep <= 0)
        {
            // Start combo
            currentComboStep = 1;
            queuedNext = false;
            SetComboParams(currentComboStep, queuedNext);
            FireSwingTrigger();
            SetCombatLayerWeight(1f);
            return;
        }

        // Queue next swing
        if (currentComboStep < 3)
        {
            currentComboStep++;
            queuedNext = true;
            SetComboParams(currentComboStep, queuedNext);
        }
    }

    void FireSwingTrigger()
    {
        if (humanoidAnimator) humanoidAnimator.SetTrigger(SwingHash);
        if (legoAnimator) legoAnimator.SetTrigger(SwingHash);

    }

    void PlayAttackSound()
    {
        if (!attackSource || !attackSwingClip) return;

        attackSource.pitch = Random.Range(attackPitchRange.x, attackPitchRange.y);
        attackSource.PlayOneShot(attackSwingClip, attackVolume);
    }

    void SetComboParams(int comboStep, bool queueNext)
    {
        if (humanoidAnimator)
        {
            humanoidAnimator.SetInteger(ComboHash, comboStep);
            humanoidAnimator.SetBool(QueueNextHash, queueNext);
        }
        if (legoAnimator)
        {
            legoAnimator.SetInteger(ComboHash, comboStep);
            legoAnimator.SetBool(QueueNextHash, queueNext);
        }
    }

    void ResetComboParams()
    {
        currentComboStep = 0;
        queuedNext = false;
        SetComboParams(0, false);
        SetCombatLayerWeight(0f);
    }

    void SetCombatLayerWeight(float weight)
    {
        if (humanoidAnimator && humanoidCombatLayer >= 0)
            humanoidAnimator.SetLayerWeight(humanoidCombatLayer, weight);
        if (legoAnimator && legoCombatLayer >= 0)
            legoAnimator.SetLayerWeight(legoCombatLayer, weight);
    }

    static bool IsAnimatorInEmpty(Animator anim, int layer)
    {
        if (!anim) return true;
        int idx = layer >= 0 ? layer : 0;
        var st = anim.GetCurrentAnimatorStateInfo(idx);
        return st.shortNameHash == EmptyStateHash;
    }

    static bool IsAnimatorSwinging(Animator anim, int layer)
    {
        if (!anim) return false;
        int idx = layer >= 0 ? layer : 0;
        var st = anim.GetCurrentAnimatorStateInfo(idx);
        int h = st.shortNameHash;
        return h == Swing1StateHash || h == Swing2StateHash || h == Swing3StateHash;
    }

    void UpdateComboFromAnimator(Animator anim, int layer, ref int lastStateHash)
    {
        if (!anim) return;
        int idx = layer >= 0 ? layer : 0;
        var st = anim.GetCurrentAnimatorStateInfo(idx);
        int currentState = st.shortNameHash;

        if (currentState == lastStateHash) return;
        lastStateHash = currentState;

        // When a queued transition happens (Swing #1 -> Swing #2, etc) clear QueueNext so another click is required.
        if (currentState == Swing1StateHash || currentState == Swing2StateHash || currentState == Swing3StateHash)
        {
            PlayAttackSound();
            
            if (queuedNext)
            {
                queuedNext = false;
                SetComboParams(currentComboStep, false);
            }
            SetCombatLayerWeight(1f);
        }
        else if (currentState == EmptyStateHash)
        {
            // Combo finished
            ResetComboParams();
        }
    }


    void UpdateFootstepsServer()
    {
        if (footstepClips == null || footstepClips.Length == 0) return;
        if (ncc == null) return;

        var planarVel = ncc.Velocity;
        planarVel.y = 0f;
        float speed = planarVel.magnitude;

        if (!ncc.Grounded || speed < footstepSpeedThreshold)
        {
            footstepTimer = 0f;
            return;
        }

        float t = Mathf.InverseLerp(0f, runSpeed, speed);
        float interval = Mathf.Lerp(stepIntervalSlow, stepIntervalFast, t);

        footstepTimer -= Runner.DeltaTime;
        if (footstepTimer > 0f) return;

        footstepTimer = interval;
        FootstepIndex = Random.Range(0, footstepClips.Length);
        FootstepPitch = Random.Range(footstepPitchRange.x, footstepPitchRange.y);
        FootstepSeq++;
    }

    void ApplyFootstep()
    {
        if (FootstepSeq == lastFootstepSeq) return;
        lastFootstepSeq = FootstepSeq;

        if (!footstepSource || footstepClips == null || footstepClips.Length == 0) return;
        int idx = Mathf.Clamp(FootstepIndex, 0, footstepClips.Length - 1);
        var clip = footstepClips[idx];
        if (!clip) return;

        footstepSource.pitch = FootstepPitch == 0f ? 1f : FootstepPitch;
        footstepSource.PlayOneShot(clip, footstepVolume);
    }

    public void FootstepEvent()
    {
        if (!Object.HasStateAuthority) return;
        if (footstepClips == null || footstepClips.Length == 0) return;

        FootstepIndex = Random.Range(0, footstepClips.Length);
        FootstepPitch = Random.Range(footstepPitchRange.x, footstepPitchRange.y);
        FootstepSeq++;
    }

    // ─── Brick Damage Networking ───

    /// <summary>
    /// Called by BrickCannonballProjectile when a cannonball hits a BrickStructure.
    /// Routes damage through the network so all clients see the destruction.
    /// </summary>
    public void RequestBrickDamageReplication(BrickStructure structure, Vector3 hitPoint,
        Vector3 velocity, float mass, int hitBrickIndex)
    {
        int idx = BrickStructure.GetIndex(structure);
        if (idx < 0) return;

        if (Object.HasStateAuthority)
        {
            // Host: apply locally, then broadcast to clients
            ApplyBrickDamageLocally(idx, hitPoint, velocity, mass, hitBrickIndex);
            RPC_BroadcastBrickDamage(idx, hitPoint, velocity, mass, hitBrickIndex);
        }
        else
        {
            // Client: send to server, wait for broadcast back
            RPC_RequestBrickDamage(idx, hitPoint, velocity, mass, hitBrickIndex);
        }
    }

    public void RequestBrickRadiusDamageReplication(BrickStructure structure, Vector3 hitPoint, float radius)
    {
        int idx = BrickStructure.GetIndex(structure);
        if (idx < 0) return;

        float safeRadius = Mathf.Max(0.05f, radius);

        if (Object.HasStateAuthority)
        {
            ApplyBrickRadiusDamageLocally(idx, hitPoint, safeRadius);
            RPC_BroadcastBrickRadiusDamage(idx, hitPoint, safeRadius);
        }
        else
        {
            RPC_RequestBrickRadiusDamage(idx, hitPoint, safeRadius);
        }
    }

    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    void RPC_RequestBrickDamage(int structureIndex, Vector3 hitPoint,
        Vector3 velocity, float mass, int hitBrickIndex)
    {
        // Server: apply damage, then broadcast to all clients
        ApplyBrickDamageLocally(structureIndex, hitPoint, velocity, mass, hitBrickIndex);
        RPC_BroadcastBrickDamage(structureIndex, hitPoint, velocity, mass, hitBrickIndex);
    }

    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    void RPC_RequestBrickRadiusDamage(int structureIndex, Vector3 hitPoint, float radius)
    {
        ApplyBrickRadiusDamageLocally(structureIndex, hitPoint, radius);
        RPC_BroadcastBrickRadiusDamage(structureIndex, hitPoint, radius);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    void RPC_BroadcastBrickDamage(int structureIndex, Vector3 hitPoint,
        Vector3 velocity, float mass, int hitBrickIndex)
    {
        if (Object.HasStateAuthority) return; // Server already applied
        ApplyBrickDamageLocally(structureIndex, hitPoint, velocity, mass, hitBrickIndex);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    void RPC_BroadcastBrickRadiusDamage(int structureIndex, Vector3 hitPoint, float radius)
    {
        if (Object.HasStateAuthority) return; // Server already applied
        ApplyBrickRadiusDamageLocally(structureIndex, hitPoint, radius);
    }

    void ApplyBrickDamageLocally(int structureIndex, Vector3 hitPoint,
        Vector3 velocity, float mass, int hitBrickIndex)
    {
        var reg = BrickStructure.Registry;
        if (structureIndex < 0 || structureIndex >= reg.Count) return;

        var structure = reg[structureIndex];
        if (structure == null) return;

        if (_brickDamageSystem == null)
            _brickDamageSystem = FindAnyObjectByType<BrickDamageSystem>();
        if (_brickDamageSystem != null)
            _brickDamageSystem.DealDamageWithForce(structure, hitPoint, velocity, mass, hitBrickIndex);
    }

    void ApplyBrickRadiusDamageLocally(int structureIndex, Vector3 hitPoint, float radius)
    {
        var reg = BrickStructure.Registry;
        if (structureIndex < 0 || structureIndex >= reg.Count) return;

        var structure = reg[structureIndex];
        if (structure == null) return;

        if (_brickDamageSystem == null)
            _brickDamageSystem = FindAnyObjectByType<BrickDamageSystem>();
        if (_brickDamageSystem != null)
            _brickDamageSystem.DealDamage(structure, hitPoint, Mathf.Max(0.05f, radius));
    }
}



