using Fusion;
using UnityEngine;
using UnityEngine.AI;
using BricksBladesBlackpowder.Teams;

namespace BricksBladesBlackpowder.NPC
{
    [RequireComponent(typeof(NavMeshAgent))]
    public class NPCBasicAI : NetworkBehaviour
    {
        public float detectionRadius = 10f;
        public float attackRange = 2f;
        public float attackCooldown = 1.5f;
        public LayerMask targetMask;
        [Header("Idle Wander")]
        public bool wanderWhenIdle = true;
        public float wanderRadius = 20f;
        public float wanderRepathMinSeconds = 1.5f;
        public float wanderRepathMaxSeconds = 4f;
        public float wanderArrivalDistance = 0.8f;
        [Range(0.1f, 1f)] public float wanderSpeedMultiplier = 0.5f;
        [Range(0f, 1f)] public float wanderPauseChance = 0.35f;
        public float wanderPauseMinSeconds = 0.6f;
        public float wanderPauseMaxSeconds = 2.2f;
        [Range(0f, 1f)] public float curvyWanderChance = 0.45f;
        [Range(0f, 1f)] public float curveStrength = 0.35f;
        [Range(0.5f, 2f)] public float wanderAnimMultiplier = 0.85f;
        public Animator humanoidAnimator;
        public Animator legoAnimator;
        public string speedParam = "Speed";
        public string swingTrigger = "Swing";
        public float runAnimMultiplier = 2f;

        [Header("Agent Tuning")]
        public float angularSpeed = 360f;
        public float acceleration = 60f;

        private NavMeshAgent agent;
        private NPCController controller;
        private CombatAnimDriver combatAnimDriver;
        [Networked] private TickTimer AttackTimer { get; set; }
        [Networked, OnChangedRender(nameof(OnAnimSpeedChanged))] private float AnimSpeed { get; set; }
        [Networked] private int AttackSeq { get; set; }
        private Transform target;
        private int lastAttackSeq;
        private int speedHash;
        private int swingHash;
        private bool profileApplied;
        private Vector3 wanderAnchor;
        private bool hasWanderAnchor;
        private float nextWanderPickTime;
        private bool wanderPaused;
        private float wanderPauseUntilTime;
        private bool hasCurvedWanderLeg;
        private Vector3 curvedWanderLegFinal;
        private float chaseSpeed = 5f;
        private bool clearedForCurrentDeath;

        void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            // Disable agent so it doesn't try to connect to the NavMesh at the
            // prefab's origin position before Fusion has placed the object.
            agent.enabled = false;
            controller = GetComponent<NPCController>();
            combatAnimDriver = GetComponent<CombatAnimDriver>();

            // Auto-find animators from the hierarchy if not assigned in the inspector.
            // HumanoidDriver child = Mixamo/movement animations.
            // Root child = lego/combat animations.
            if (!humanoidAnimator)
            {
                var t = transform.Find("HumanoidDriver");
                if (t) humanoidAnimator = t.GetComponent<Animator>();
            }
            if (!legoAnimator)
            {
                var t = transform.Find("Root");
                if (t) legoAnimator = t.GetComponent<Animator>();
            }

            speedHash = Animator.StringToHash(speedParam);
            swingHash = Animator.StringToHash(swingTrigger);

            // Add relay to every child Animator so Unity can deliver animation
            // events (FootstepEvent, etc.) without "no receiver" errors.
            foreach (var anim in GetComponentsInChildren<Animator>(true))
            {
                if (!anim.GetComponent<NPCAnimationEventRelay>())
                    anim.gameObject.AddComponent<NPCAnimationEventRelay>();
            }
        }

        public override void Spawned()
        {
            // Re-enable after Fusion has positioned the object on the NavMesh.
            agent.enabled = true;
            agent.autoBraking = false;
            agent.angularSpeed = angularSpeed;
            agent.acceleration = acceleration;
            if (!Object.HasStateAuthority)
            {
                // Non-authority proxies should only render movement replicated by Fusion.
                if (agent.isOnNavMesh) agent.isStopped = true;
                agent.updatePosition = false;
                agent.updateRotation = false;
            }

            wanderAnchor = transform.position;
            hasWanderAnchor = true;
            nextWanderPickTime = 0f;
        }

        public override void FixedUpdateNetwork()
        {
            if (!Object || !Object.HasStateAuthority)
                return;

            // Apply profile stats the first time profile is assigned (profile is set after Spawn).
            if (!profileApplied && controller != null && controller.profile != null)
            {
                chaseSpeed = controller.profile.moveSpeed;
                agent.speed = chaseSpeed;
                attackRange = controller.profile.attackRange;
                attackCooldown = controller.profile.attackCooldown;
                profileApplied = true;
            }

            if (controller == null || controller.health == null || controller.health.IsDead)
            {
                if (!clearedForCurrentDeath)
                {
                    ResetAttackVisualState();
                    clearedForCurrentDeath = true;
                }

                if (agent.isOnNavMesh) agent.isStopped = true;
                SetAnimSpeed(0f);
                return;
            }

            if (clearedForCurrentDeath)
            {
                ResetAttackVisualState();
                clearedForCurrentDeath = false;
            }

            if (!agent.isOnNavMesh)
            {
                SetAnimSpeed(0f);
                return;
            }

            FindTarget();
            // Defensive check: clear target if it died between ticks.
            if (target != null)
            {
                var chaseIdmg = target.GetComponentInParent<IDamageable>();
                if (chaseIdmg == null || chaseIdmg.IsDead) target = null;
            }
            if (target)
            {
                wanderPaused = false;
                hasCurvedWanderLeg = false;
                agent.speed = chaseSpeed;

                float dist = Vector3.Distance(transform.position, target.position);
                if (dist > attackRange)
                {
                    agent.isStopped = false;
                    agent.SetDestination(target.position);
                }
                else
                {
                    agent.isStopped = true;
                    TryAttack();
                }
            }
            else
            {
                if (wanderWhenIdle)
                {
                    agent.speed = chaseSpeed * Mathf.Clamp(wanderSpeedMultiplier, 0.1f, 1f);
                    WanderIdle();
                }
                else
                    agent.isStopped = true;
            }

            float speed = agent.isStopped ? 0f : agent.velocity.magnitude;
            float normalized = Mathf.Clamp01(speed / Mathf.Max(0.01f, agent.speed));
            float animScale = target ? runAnimMultiplier : wanderAnimMultiplier;
            SetAnimSpeed(normalized * animScale);
        }

        public override void Render()
        {
            ApplyAnimSpeed();
            while (lastAttackSeq != AttackSeq)
            {
                lastAttackSeq++;
                TriggerAttackAnim();
            }
        }

        void FindTarget()
        {
            // If targetMask is not configured (0 = Nothing), default to all layers.
            LayerMask mask = targetMask == 0 ? ~0 : targetMask;
            Collider[] hits = Physics.OverlapSphere(transform.position, detectionRadius, mask);
            float closest = float.MaxValue;
            Transform best = null;
            var myTeam = controller.health.GetTeam();
            foreach (var hit in hits)
            {
                var idmg = hit.GetComponentInParent<IDamageable>();
                if (idmg == null || idmg.IsDead) continue;
                if (idmg.GetTeam() == myTeam) continue;
                float d = Vector3.Distance(transform.position, hit.transform.position);
                if (d < closest)
                {
                    closest = d;
                    best = hit.transform;
                }
            }
            target = best;
        }

        void TryAttack()
        {
            if (target == null) return;
            if (AttackTimer.IsRunning && !AttackTimer.ExpiredOrNotRunning(Runner)) return;

            var idmg = target.GetComponentInParent<IDamageable>();
            var myTeam = controller != null && controller.health != null ? controller.health.GetTeam() : Team.None;
            if (idmg != null && !idmg.IsDead)
            {
                if (myTeam != Team.None && idmg.GetTeam() == myTeam)
                    return;

                idmg.TryTakeDamage(gameObject, controller.profile ? controller.profile.attackDamage : 1, (target.position - transform.position).normalized, 0f);
                AttackTimer = TickTimer.CreateFromSeconds(Runner, attackCooldown);
                AttackSeq++;
            }
        }

        void WanderIdle()
        {
            if (!agent.isOnNavMesh)
            {
                agent.isStopped = true;
                return;
            }

            if (wanderPaused)
            {
                agent.isStopped = true;
                if (Time.time >= wanderPauseUntilTime)
                    wanderPaused = false;
                return;
            }

            if (!hasWanderAnchor)
            {
                wanderAnchor = transform.position;
                hasWanderAnchor = true;
            }

            bool hasPath = agent.hasPath && !agent.pathPending;
            bool reached = hasPath && agent.remainingDistance <= Mathf.Max(wanderArrivalDistance, agent.stoppingDistance + 0.05f);

            if (hasCurvedWanderLeg && reached)
            {
                hasCurvedWanderLeg = false;
                agent.isStopped = false;
                agent.SetDestination(curvedWanderLegFinal);
                return;
            }

            if (!hasPath || reached || Time.time >= nextWanderPickTime)
            {
                if (reached && Random.value < Mathf.Clamp01(wanderPauseChance))
                {
                    float minPause = Mathf.Max(0f, wanderPauseMinSeconds);
                    float maxPause = Mathf.Max(minPause, wanderPauseMaxSeconds);
                    wanderPauseUntilTime = Time.time + Random.Range(minPause, maxPause);
                    wanderPaused = true;
                    agent.isStopped = true;
                    return;
                }

                PickNextWanderDestination();
            }
        }

        void PickNextWanderDestination()
        {
            if (!agent.isOnNavMesh)
                return;

            int tries = 8;
            float radius = Mathf.Max(0.5f, wanderRadius);
            for (int i = 0; i < tries; i++)
            {
                Vector2 offset2D = Random.insideUnitCircle * radius;
                Vector3 probe = wanderAnchor + new Vector3(offset2D.x, 0f, offset2D.y);

                if (NavMesh.SamplePosition(probe, out NavMeshHit hit, radius, NavMesh.AllAreas))
                {
                    Vector3 destination = hit.position;

                    bool useCurvy = Random.value < Mathf.Clamp01(curvyWanderChance);
                    if (useCurvy && TryBuildCurvedLeg(destination, out Vector3 waypoint))
                    {
                        hasCurvedWanderLeg = true;
                        curvedWanderLegFinal = destination;
                        agent.isStopped = false;
                        agent.SetDestination(waypoint);
                    }
                    else
                    {
                        hasCurvedWanderLeg = false;
                        agent.isStopped = false;
                        agent.SetDestination(destination);
                    }

                    float minT = Mathf.Max(0.1f, wanderRepathMinSeconds);
                    float maxT = Mathf.Max(minT, wanderRepathMaxSeconds);
                    nextWanderPickTime = Time.time + Random.Range(minT, maxT);
                    return;
                }
            }

            // Could not find a valid point this tick; try again shortly.
            agent.isStopped = true;
            nextWanderPickTime = Time.time + 0.5f;
        }

        bool TryBuildCurvedLeg(Vector3 finalDestination, out Vector3 waypoint)
        {
            waypoint = default;

            Vector3 toFinal = finalDestination - transform.position;
            toFinal.y = 0f;
            float distance = toFinal.magnitude;
            if (distance < 0.5f)
                return false;

            Vector3 dir = toFinal / Mathf.Max(0.001f, distance);
            Vector3 side = new Vector3(-dir.z, 0f, dir.x);
            float lateral = distance * Mathf.Clamp01(curveStrength);
            float sideSign = Random.value < 0.5f ? -1f : 1f;

            Vector3 mid = Vector3.Lerp(transform.position, finalDestination, 0.5f) + side * lateral * sideSign;
            float sampleRadius = Mathf.Max(1f, wanderRadius * 0.5f);
            if (!NavMesh.SamplePosition(mid, out NavMeshHit midHit, sampleRadius, NavMesh.AllAreas))
                return false;

            waypoint = midHit.position;
            return true;
        }

        void SetAnimSpeed(float value)
        {
            if (Mathf.Abs(AnimSpeed - value) > 0.001f)
                AnimSpeed = value;
        }

        void OnAnimSpeedChanged()
        {
            ApplyAnimSpeed();
        }

        void ApplyAnimSpeed()
        {
            if (humanoidAnimator) humanoidAnimator.SetFloat(speedHash, AnimSpeed);
            if (legoAnimator) legoAnimator.SetFloat(speedHash, AnimSpeed);
        }

        void TriggerAttackAnim()
        {
            // Prefer CombatAnimDriver (same as PlayerController) for proper layer fade.
            if (combatAnimDriver)
            {
                combatAnimDriver.DoSwing();
                return;
            }
            // Fallback if CombatAnimDriver is not on the prefab.
            if (humanoidAnimator) humanoidAnimator.SetTrigger(swingHash);
            if (legoAnimator) legoAnimator.SetTrigger(swingHash);
        }

        void ResetAttackVisualState()
        {
            lastAttackSeq = AttackSeq;

            if (combatAnimDriver)
                combatAnimDriver.ResetToIdle();

            if (humanoidAnimator)
            {
                humanoidAnimator.ResetTrigger(swingHash);
                humanoidAnimator.Rebind();
                humanoidAnimator.Update(0f);
            }

            if (legoAnimator)
            {
                legoAnimator.ResetTrigger(swingHash);
                legoAnimator.Rebind();
                legoAnimator.Update(0f);
            }

            SetAnimSpeed(0f);
        }
    }
}
