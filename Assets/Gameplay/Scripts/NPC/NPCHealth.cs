using Fusion;
using Fusion.Sockets;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Animations;
using BricksBladesBlackpowder.Teams;

namespace BricksBladesBlackpowder.NPC
{
    public class NPCHealth : NetworkBehaviour, IDamageable
    {
        public int maxHearts = 2;
        public float invulnerabilitySeconds = 0.5f;
        public float respawnDelaySeconds = 3.0f;

        [Header("Break Apart")]
        public Transform breakPartsRoot;
        public Transform visualRoot;
        public float breakForce = 7.5f;
        public float breakUpwardForce = 1.5f;
        public float breakRandomForce = 2.0f;
        public float breakTorque = 12f;
        public float breakColliderInflate = 0.02f;

        [Header("Water Kill")]
        [SerializeField] Transform torsoReference;
        [SerializeField] string torsoPath = "Root/Hips/Torso";
        [SerializeField] float torsoKillOffset = 0f;

        [Networked] private int Hearts { get; set; }
        [Networked] private NetworkBool NetIsDead { get; set; }
        [Networked] private TickTimer InvulnTimer { get; set; }
        [Networked] private TickTimer RespawnTimer { get; set; }
        [Networked] private int DeathSeq { get; set; }
        [Networked] private Vector3 DeathImpulseDir { get; set; }
        private bool _spawnedReady;
        private TeamIdentity teamIdentity;
        private NavMeshAgent navMeshAgent;
        private Vector3 spawnPos;
        private Quaternion spawnRot;
        private bool lastIsDead;
        private int lastDeathSeq;
        private bool deathApplied;
        private Animator[] cachedAnimators;
        private MeshRenderer[] cachedMeshRenderers;
        private RotationConstraint[] cachedRotationConstraints;
        private bool[] defaultConstraintEnabledStates;
        private readonly List<GameObject> debris = new List<GameObject>(16);
        private float lastTorsoY;
        private bool hasLastTorsoY;

        public bool IsDead => _spawnedReady && NetIsDead;

        void Awake()
        {
            teamIdentity = ResolveTeamIdentity();
            navMeshAgent = GetComponent<NavMeshAgent>();
        }

        public override void Spawned()
        {
            _spawnedReady = true;

            CacheTorsoReference();
            if (torsoReference)
            {
                lastTorsoY = torsoReference.position.y + torsoKillOffset;
                hasLastTorsoY = true;
            }

            if (!visualRoot)
            {
                var r = transform.Find("Root");
                if (r) visualRoot = r;
            }
            if (!breakPartsRoot)
                breakPartsRoot = visualRoot;

            if (Object.HasStateAuthority)
            {
                spawnPos = transform.position;
                spawnRot = transform.rotation;
                Hearts = Mathf.Clamp(maxHearts, 1, 99);
                NetIsDead = false;
                InvulnTimer = default;
                RespawnTimer = default;
                DeathSeq = 0;
                DeathImpulseDir = Vector3.forward;
            }

            CacheAnimators();
            CacheMeshRenderers();
            CacheRotationConstraints();

            // Sync visual state on late-joining clients.
            lastIsDead = NetIsDead;
            lastDeathSeq = DeathSeq;
            if (visualRoot) visualRoot.gameObject.SetActive(!NetIsDead);
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            _spawnedReady = false;
        }

        public override void FixedUpdateNetwork()
        {
            if (!Object.HasStateAuthority)
                return;

            if (!NetIsDead && ShouldDieFromWater())
            {
                Die(Vector3.up);
                return;
            }

            if (NetIsDead && RespawnTimer.IsRunning && RespawnTimer.Expired(Runner))
            {
                Respawn();
            }
        }

        public override void Render()
        {
            if (NetIsDead)
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
        }

        public Team GetTeam() => teamIdentity ? teamIdentity.team : Team.None;

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

        public bool TryTakeDamage(GameObject attacker, int heartsDamage, Vector3 hitFromDir, float knockback)
        {
            if (!teamIdentity)
                teamIdentity = ResolveTeamIdentity();

            if (!Object) return false;

            if (!Object.HasStateAuthority)
            {
                // Client: forward the request to the server via RPC.
                int attackerTeam = (int)Team.None;
                attackerTeam = (int)ResolveTeamFromObject(attacker);
                RPC_RequestDamage(heartsDamage, hitFromDir, attackerTeam);
                return true;
            }

            return ApplyDamage(attacker, heartsDamage, hitFromDir);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_RequestDamage(int heartsDamage, Vector3 hitFromDir, int attackerTeamInt)
        {
            if (NetIsDead || Hearts <= 0 || heartsDamage <= 0) return;
            if (InvulnTimer.IsRunning && !InvulnTimer.ExpiredOrNotRunning(Runner)) return;
            if ((Team)attackerTeamInt == GetTeam()) return; // prevent friendly fire

            Hearts = Mathf.Max(0, Hearts - heartsDamage);
            InvulnTimer = TickTimer.CreateFromSeconds(Runner, invulnerabilitySeconds);
            if (Hearts <= 0) Die(hitFromDir);
        }

        private bool ApplyDamage(GameObject attacker, int heartsDamage, Vector3 hitFromDir)
        {
            if (NetIsDead || Hearts <= 0 || heartsDamage <= 0)
                return false;

            if (InvulnTimer.IsRunning && !InvulnTimer.ExpiredOrNotRunning(Runner))
                return false;

            // Team check
            if (attacker)
            {
                if (ResolveTeamFromObject(attacker) == GetTeam())
                    return false;
            }

            Hearts = Mathf.Max(0, Hearts - heartsDamage);
            InvulnTimer = TickTimer.CreateFromSeconds(Runner, invulnerabilitySeconds);

            if (Hearts <= 0)
                Die(hitFromDir);

            return true;
        }

        void Die(Vector3 impulseDir)
        {
            impulseDir.y = 0f;
            if (impulseDir.sqrMagnitude < 0.001f)
                impulseDir = transform.forward;
            impulseDir.Normalize();
            DeathImpulseDir = impulseDir;
            NetIsDead = true;
            DeathSeq++;
            RespawnTimer = TickTimer.CreateFromSeconds(Runner, respawnDelaySeconds);
            if (navMeshAgent) navMeshAgent.enabled = false;
        }

        void Respawn()
        {
            Hearts = Mathf.Clamp(maxHearts, 1, 99);
            NetIsDead = false;
            InvulnTimer = TickTimer.CreateFromSeconds(Runner, invulnerabilitySeconds);
            RespawnTimer = default;
            transform.SetPositionAndRotation(spawnPos, spawnRot);
            if (navMeshAgent) navMeshAgent.enabled = true;

            if (torsoReference)
            {
                lastTorsoY = torsoReference.position.y + torsoKillOffset;
                hasLastTorsoY = true;
            }
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
            Transform root = breakPartsRoot ? breakPartsRoot : transform;
            cachedMeshRenderers = root.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
        }

        void ApplyDeathVisuals()
        {
            SpawnDebris();
            if (visualRoot) visualRoot.gameObject.SetActive(false);
            if (cachedAnimators != null)
            {
                for (int i = 0; i < cachedAnimators.Length; i++)
                    if (cachedAnimators[i]) cachedAnimators[i].enabled = false;
            }
        }

        void ClearDeathVisuals()
        {
            if (visualRoot) visualRoot.gameObject.SetActive(true);
            if (cachedAnimators != null)
            {
                for (int i = 0; i < cachedAnimators.Length; i++)
                    if (cachedAnimators[i]) cachedAnimators[i].enabled = true;
            }

            RestoreAnimationDrivenState();

            for (int i = 0; i < debris.Count; i++)
                if (debris[i]) Destroy(debris[i]);
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

                var go = new GameObject("npc_debris");
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
                if (dir.sqrMagnitude < 0.001f) dir = Vector3.forward;
                dir.Normalize();

                Vector3 force = dir * breakForce + Vector3.up * breakUpwardForce + rand;
                rb.AddForce(force, ForceMode.VelocityChange);
                rb.AddTorque(Random.insideUnitSphere * breakTorque, ForceMode.VelocityChange);

                debris.Add(go);
            }
        }
    }
}
