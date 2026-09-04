using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Handles brick destruction: hit detection, damage application, debris spawning with object pooling.
/// Attach to a manager object or wire up from your projectile/weapon system.
/// </summary>
public class BrickDamageSystem : MonoBehaviour
{
    [Header("Damage Settings")]
    [Tooltip("Radius around hit point to destroy bricks")]
    public float damageRadius = 0.5f;

    [Header("Debris Physics")]
    [Tooltip("Material for debris bricks (should be the palette-based LDraw material)")]
    public Material debrisMaterial;
    [Tooltip("Palette texture for debris coloring")]
    public Texture2D paletteTexture;
    [Tooltip("Maximum active debris objects")]
    public int maxDebris = 200;
    [Tooltip("Minimum bricks for a chunk to persist (no decay). Smaller chunks fade out.")]
    public int persistBrickThreshold = 3;
    [Tooltip("Seconds small debris waits before starting to fade")]
    public float debrisDecayDelay = 4f;
    [Tooltip("Seconds the fade-out takes")]
    public float debrisFadeDuration = 1.5f;
    [Tooltip("Explosion force applied to debris")]
    public float explosionForce = 10f;
    [Tooltip("Random velocity added to debris")]
    public float randomVelocity = 4f;
    [Tooltip("Enable colliders on debris")]
    public bool debrisColliders = true;
    [Tooltip("Physics layer for debris (set to a layer that ignores self-collision). -1 = don't change layer.")]
    public int debrisLayer = -1;
    [Tooltip("Minimum bricks to spawn as a combined chunk instead of individual debris")]
    public int chunkSizeThreshold = 5;
    [Tooltip("DEBUG: Scale multiplier for debris visibility testing")]
    public float debugScaleMultiplier = 1f;
    [Tooltip("DEBUG: Freeze debris in place (no physics movement)")]
    public bool debugFreezeDebris = false;

    // ── Active debris tracking ──
    readonly List<GameObject> _activeDebris = new(256);

    // Reusable lists to reduce GC
    readonly List<int> _hitBricks = new(64);
    readonly List<List<int>> _detachedGroups = new(16);

    void Awake()
    {
        // If debris layer is set, disable debris-vs-debris collisions
        if (debrisLayer >= 0 && debrisLayer < 32)
            Physics.IgnoreLayerCollision(debrisLayer, debrisLayer, true);
    }

    void Update()
    {
        // Clean up destroyed debris references
        for (int i = _activeDebris.Count - 1; i >= 0; i--)
        {
            if (_activeDebris[i] == null)
                _activeDebris.RemoveAt(i);
        }
    }
    
    void OnDrawGizmos()
    {
        // Visualize active debris in scene view
        if (_activeDebris == null || _activeDebris.Count == 0) return;
        
        Gizmos.color = Color.red;
        foreach (var obj in _activeDebris)
        {
            if (obj != null)
            {
                Gizmos.DrawWireSphere(obj.transform.position, 0.5f);
            }
        }
    }

    // ─── Public API ───

    /// <summary>
    /// Apply damage to a structure at a world-space hit point.
    /// Call this from your projectile/weapon system.
    /// Returns the number of bricks destroyed.
    /// </summary>
    public int DealDamage(BrickStructure structure, Vector3 worldHitPoint, float? radiusOverride = null)
    {
        if (structure == null || structure.model == null || !structure.model.HasGpuData) return 0;

        float radius = radiusOverride ?? damageRadius;

        // Find bricks in blast radius
        structure.FindBricksInRadius(worldHitPoint, radius, _hitBricks);
        if (_hitBricks.Count == 0)
        {
            Debug.Log($"[BrickDamage] No bricks found in radius {radius} at {worldHitPoint}");
            return 0;
        }

        int destroyedCount = _hitBricks.Count;

        // Apply damage and get detached groups
        var detached = structure.ApplyDamage(_hitBricks);

        Debug.Log($"[BrickDamage] Destroyed {destroyedCount} bricks directly, {detached.Count} detached groups found");

        // Spawn individual debris for directly hit bricks
        SpawnIndividualDebris(structure, _hitBricks, worldHitPoint);
        
        // Spawn debris for detached groups (chunks vs individual based on size)
        for (int i = 0; i < detached.Count; i++)
        {
            var group = detached[i];
            if (group.Count >= chunkSizeThreshold)
                SpawnChunkDebris(structure, group, worldHitPoint);
            else
                SpawnIndividualDebris(structure, group, worldHitPoint);
            
            destroyedCount += group.Count;
        }

        return destroyedCount;
    }

    /// <summary>
    /// Force-based damage using LEGO-style connection strengths.
    /// Force propagates from the hit brick through stud connections.
    /// Strong (vertical/stud) connections resist more force than weak (horizontal) ones.
    /// Detached groups become physics chunks that fall realistically.
    /// </summary>
    public int DealDamageWithForce(BrickStructure structure, Vector3 worldHitPoint,
        Vector3 impactVelocity, float projectileMass, int hitBrickIndex = -1)
    {
        if (structure == null || structure.model == null || !structure.model.HasGpuData) return 0;

        float impactSpeed = impactVelocity.magnitude;
        float impactForce = projectileMass * impactSpeed;
        // Normalize to 0-1 range. ~50 = moderate, 100+ = devastating
        float normalizedForce = Mathf.Clamp01(impactForce / 60f);

        Debug.Log($"[BrickDamage] Impact speed: {impactSpeed:F1}, force: {impactForce:F1}, normalized: {normalizedForce:F2}");

        // If we know which brick was hit, use force propagation
        if (hitBrickIndex >= 0)
        {
            return DealForceDamage(structure, hitBrickIndex, normalizedForce, worldHitPoint, impactVelocity.normalized);
        }

        // Fallback: find nearest brick to hit point
        structure.FindBricksInRadius(worldHitPoint, 0.5f, _hitBricks);
        if (_hitBricks.Count == 0)
        {
            // Wider search
            structure.FindBricksInRadius(worldHitPoint, 1.5f, _hitBricks);
        }
        if (_hitBricks.Count == 0)
        {
            Debug.Log($"[BrickDamage] No bricks found near {worldHitPoint}");
            return 0;
        }

        // Pick the closest brick
        int closest = _hitBricks[0];
        float closestDist = float.MaxValue;
        for (int i = 0; i < _hitBricks.Count; i++)
        {
            Vector3 bpos = structure.worldTransforms[_hitBricks[i]].GetColumn(3);
            float d = (bpos - worldHitPoint).sqrMagnitude;
            if (d < closestDist) { closestDist = d; closest = _hitBricks[i]; }
        }

        return DealForceDamage(structure, closest, normalizedForce, worldHitPoint, impactVelocity.normalized);
    }

    /// <summary>
    /// Core force-propagation damage. Shatters the hit brick and nearby weak connections,
    /// then spawns detached groups as physics chunks.
    /// </summary>
    int DealForceDamage(BrickStructure structure, int hitBrickIndex, float normalizedForce,
        Vector3 worldHitPoint, Vector3 hitDirection)
    {
        var shattered = new List<int>(16);
        var detached = structure.ApplyForceDamage(hitBrickIndex, normalizedForce, shattered);

        int totalAffected = shattered.Count;

        Debug.Log($"[BrickDamage] Shattered {shattered.Count} bricks, {detached.Count} groups detached");

        // Spawn debris for shattered bricks (the ones directly destroyed)
        if (shattered.Count > 0)
            SpawnIndividualDebris(structure, shattered, worldHitPoint);

        // Spawn detached groups as physics chunks that fall realistically
        for (int i = 0; i < detached.Count; i++)
        {
            var group = detached[i];
            totalAffected += group.Count;

            // Mark them dead in the structure (they'll live as physics chunks now)
            for (int j = 0; j < group.Count; j++)
                structure.aliveFlags[group[j]] = 0;

            SpawnFallingChunk(structure, group, worldHitPoint, hitDirection, normalizedForce);
        }

        // Rebuild after marking detached groups dead
        if (detached.Count > 0)
        {
            structure.damageVersion++;
            BrickRenderSystem.NotifyStructureChanged();
            BrickConnectionSystem.NotifyStructureChanged();
            // Spatial hash and colliders will refresh on next LateUpdate via damageVersion
        }

        return totalAffected;
    }

    /// <summary>
    /// Spawns a detached brick group as a physics-enabled chunk.
    /// Chunk gently falls/tumbles rather than exploding like direct debris.
    /// </summary>
    void SpawnFallingChunk(BrickStructure structure, List<int> brickIndices,
        Vector3 hitPoint, Vector3 hitDirection, float normalizedForce)
    {
        if (debrisMaterial == null || structure.parts == null) return;
        if (brickIndices.Count == 0) return;

        // Calculate chunk center of mass
        Vector3 chunkCenter = Vector3.zero;
        for (int i = 0; i < brickIndices.Count; i++)
            chunkCenter += (Vector3)structure.worldTransforms[brickIndices[i]].GetColumn(3);
        chunkCenter /= brickIndices.Count;

        var chunkObj = new GameObject($"Chunk_{brickIndices.Count}bricks");
        chunkObj.transform.position = chunkCenter;
        chunkObj.transform.rotation = Quaternion.identity;

        for (int i = 0; i < brickIndices.Count; i++)
        {
            int brickIdx = brickIndices[i];
            var xf = structure.worldTransforms[brickIdx];
            var brick = structure.model.sortedBricks[brickIdx];

            Mesh brickMesh = structure.parts.GetMeshById(brick.partId);
            if (brickMesh == null) continue;

            var brickObj = new GameObject($"Brick_{brickIdx}");
            brickObj.transform.SetParent(chunkObj.transform, true);

            Vector3 pos = xf.GetColumn(3);
            Quaternion rot = xf.rotation;
            Vector3 scale = xf.lossyScale;

            brickObj.transform.SetPositionAndRotation(pos, rot);
            brickObj.transform.localScale = scale * debugScaleMultiplier;

            var mf = brickObj.AddComponent<MeshFilter>();
            mf.sharedMesh = brickMesh;

            var mr = brickObj.AddComponent<MeshRenderer>();
            mr.sharedMaterial = debrisMaterial;

            var mpb = new MaterialPropertyBlock();
            mpb.SetFloat("_ColorCode", brick.colorCode);
            if (paletteTexture != null)
            {
                mpb.SetTexture("_PaletteTex", paletteTexture);
                mpb.SetFloat("_PaletteWidth", paletteTexture.width);
            }
            mr.SetPropertyBlock(mpb);

            // Per-brick collider for accurate collision
            if (debrisColliders)
            {
                if (brickMesh.isReadable)
                {
                    var col = brickObj.AddComponent<MeshCollider>();
                    col.sharedMesh = brickMesh;
                    col.convex = true;
                }
                else
                {
                    var box = brickObj.AddComponent<BoxCollider>();
                    box.center = brickMesh.bounds.center;
                    box.size = brickMesh.bounds.size;
                }
            }
        }

        // Physics — chunk should tumble/fall, not explode
        var rb = chunkObj.AddComponent<Rigidbody>();
        rb.mass = 0.05f * brickIndices.Count;
        rb.linearDamping = 0.2f;
        rb.angularDamping = 0.3f;

        if (!debugFreezeDebris)
        {
            // Gentle push in hit direction — chunks should mostly just fall
            Vector3 pushDir = (chunkCenter - hitPoint).normalized;
            if (pushDir.sqrMagnitude < 0.01f)
                pushDir = hitDirection;

            float pushStrength = normalizedForce * explosionForce * 0.25f;
            rb.linearVelocity = pushDir * pushStrength + Vector3.down * 0.5f;
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * (normalizedForce * 3f);
        }
        else
        {
            rb.useGravity = false;
            rb.constraints = RigidbodyConstraints.FreezeAll;
        }

        // Assign debris layer to prevent debris-vs-debris jamming
        if (debrisLayer >= 0)
            chunkObj.layer = debrisLayer;

        // Make chunk destructible by projectiles
        var dc = chunkObj.AddComponent<DestructibleChunk>();
        dc.damageSystem = this;
        if (structure.model.adjacencyList != null)
            dc.InitConnectivity(brickIndices, structure.model.sortedBricks, structure.model.adjacencyList);

        // Track for cleanup; only decay if below persist threshold
        if (brickIndices.Count < persistBrickThreshold)
        {
            var fader = chunkObj.AddComponent<DebrisFader>();
            fader.delay = debrisDecayDelay;
            fader.fadeDuration = debrisFadeDuration;
        }
        _activeDebris.Add(chunkObj);

        Debug.Log($"[BrickDamage] Spawned falling chunk: {brickIndices.Count} bricks at {chunkCenter}");
    }

    /// <summary>
    /// Raycast version: shoot a ray and damage the first BrickStructure hit.
    /// Returns number of bricks destroyed.
    /// </summary>
    public int DealDamageRaycast(Ray ray, float maxDistance = 100f, float? radiusOverride = null)
    {
        if (!Physics.Raycast(ray, out var hit, maxDistance)) return 0;

        var structure = hit.collider.GetComponentInParent<BrickStructure>();
        if (structure == null) return 0;

        return DealDamage(structure, hit.point, radiusOverride);
    }

    // ─── Debris Spawning ───

    /// <summary>
    /// Spawn individual physics debris for a list of brick indices.
    /// Each brick becomes a separate GameObject with physics.
    /// </summary>
    void SpawnIndividualDebris(BrickStructure structure, List<int> brickIndices, Vector3 hitPoint)
    {
        if (debrisMaterial == null)
        {
            Debug.LogWarning("[BrickDamage] Cannot spawn debris: Debris Material not assigned to BrickDamageSystem!");
            return;
        }
        
        if (structure.parts == null)
        {
            Debug.LogWarning($"[BrickDamage] Cannot spawn debris: BrickStructure '{structure.name}' has no Part Registry assigned!");
            return;
        }
        
        Debug.Log($"[BrickDamage] Spawning {brickIndices.Count} debris objects...");
        
        int spawnedCount = 0;

        for (int i = 0; i < brickIndices.Count; i++)
        {
            if (_activeDebris.Count >= maxDebris)
            {
                // Destroy oldest debris to make room
                if (_activeDebris[0] != null)
                    Destroy(_activeDebris[0]);
                _activeDebris.RemoveAt(0);
            }

            int brickIdx = brickIndices[i];
            var xf = structure.worldTransforms[brickIdx];
            var brick = structure.model.sortedBricks[brickIdx];

            // Get the actual mesh for this brick part
            Mesh brickMesh = structure.parts.GetMeshById(brick.partId);
            if (brickMesh == null)
            {
                Debug.LogWarning($"[BrickDamage] No mesh found for partId {brick.partId} in part registry");
                continue;
            }

            // Create debris GameObject
            var debrisObj = new GameObject($"Debris_Brick_{brickIdx}");
            
            // Extract transform from world matrix (includes proper scale)
            Vector3 pos = xf.GetColumn(3);
            Quaternion rot = xf.rotation;
            Vector3 scale = xf.lossyScale;
            
            debrisObj.transform.SetPositionAndRotation(pos, rot);
            debrisObj.transform.localScale = scale * debugScaleMultiplier;

            // Add mesh rendering
            var mf = debrisObj.AddComponent<MeshFilter>();
            mf.sharedMesh = brickMesh;

            var mr = debrisObj.AddComponent<MeshRenderer>();
            mr.sharedMaterial = debrisMaterial;
            
            // TEMP DEBUG: Use solid color instead of palette to ensure visibility
            if (debrisMaterial == null)
            {
                // Fallback: create basic material
                mr.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mr.sharedMaterial.color = Color.red;
                Debug.LogWarning("[BrickDamage] Using fallback material for debris visibility");
            }

            // Set color via MaterialPropertyBlock for palette lookup
            var mpb = new MaterialPropertyBlock();
            mpb.SetFloat("_ColorCode", brick.colorCode);
            if (paletteTexture != null)
            {
                mpb.SetTexture("_PaletteTex", paletteTexture);
                mpb.SetFloat("_PaletteWidth", paletteTexture.width);
            }
            mr.SetPropertyBlock(mpb);

            // Add physics
            var rb = debrisObj.AddComponent<Rigidbody>();
            rb.mass = 0.1f;
            rb.linearDamping = 0.5f;
            rb.angularDamping = 0.5f;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            
            // Add collider if enabled — use the actual brick mesh for accurate collision
            if (debrisColliders)
            {
                if (brickMesh.isReadable)
                {
                    var col = debrisObj.AddComponent<MeshCollider>();
                    col.sharedMesh = brickMesh;
                    col.convex = true;
                }
                else
                {
                    var box = debrisObj.AddComponent<BoxCollider>();
                    box.center = brickMesh.bounds.center;
                    box.size = brickMesh.bounds.size;
                }
            }

            // Assign debris layer to prevent debris-vs-debris jamming
            if (debrisLayer >= 0)
                debrisObj.layer = debrisLayer;
            
            if (debugFreezeDebris)
            {
                rb.useGravity = false;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.constraints = RigidbodyConstraints.FreezeAll;
            }
            else
            {
                // Apply explosion force away from hit point
                Vector3 brickPos = pos;
                Vector3 awayDir = (brickPos - hitPoint).normalized;
                if (awayDir.sqrMagnitude < 0.01f)
                    awayDir = UnityEngine.Random.insideUnitSphere.normalized;

                Vector3 explosionVel = awayDir * explosionForce;
                explosionVel += Vector3.up * (explosionForce * 0.3f); // upward bias
                explosionVel += UnityEngine.Random.insideUnitSphere * randomVelocity;

                rb.linearVelocity = explosionVel;
                rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 10f;
            }

            // Individual debris always decays (1 brick < threshold)
            var fader = debrisObj.AddComponent<DebrisFader>();
            fader.delay = debrisDecayDelay;
            fader.fadeDuration = debrisFadeDuration;

            // Track for cleanup
            _activeDebris.Add(debrisObj);
            spawnedCount++;
        }
        
        Debug.Log($"[BrickDamage] Spawned {spawnedCount}/{brickIndices.Count} debris objects successfully. Total active debris: {_activeDebris.Count}");
    }

    /// <summary>
    /// Spawn a combined chunk debris for a group of connected bricks.
    /// Combines meshes into one object to keep bricks cohesive while falling.
    /// </summary>
    void SpawnChunkDebris(BrickStructure structure, List<int> brickIndices, Vector3 hitPoint)
    {
        if (debrisMaterial == null || structure.parts == null) return;
        if (brickIndices.Count == 0) return;

        // Calculate chunk center
        Vector3 chunkCenter = Vector3.zero;
        for (int i = 0; i < brickIndices.Count; i++)
        {
            chunkCenter += (Vector3)structure.worldTransforms[brickIndices[i]].GetColumn(3);
        }
        chunkCenter /= brickIndices.Count;

        // Create main chunk GameObject
        var chunkObj = new GameObject($"Debris_Chunk_{brickIndices.Count}bricks");
        chunkObj.transform.position = chunkCenter;
        chunkObj.transform.rotation = Quaternion.identity;

        // Build individual brick objects as children (maintains proper coloring)
        for (int i = 0; i < brickIndices.Count; i++)
        {
            int brickIdx = brickIndices[i];
            var xf = structure.worldTransforms[brickIdx];
            var brick = structure.model.sortedBricks[brickIdx];

            Mesh brickMesh = structure.parts.GetMeshById(brick.partId);
            if (brickMesh == null) continue;

            // Create brick child object (relative to chunk center)
            var brickObj = new GameObject($"Brick_{brickIdx}");
            brickObj.transform.SetParent(chunkObj.transform);
            
            Vector3 pos = xf.GetColumn(3);
            Quaternion rot = xf.rotation;
            Vector3 scale = xf.lossyScale;
            
            brickObj.transform.SetPositionAndRotation(pos, rot);
            brickObj.transform.localScale = scale * debugScaleMultiplier;

            // Add mesh and material
            var mf = brickObj.AddComponent<MeshFilter>();
            mf.sharedMesh = brickMesh;

            var mr = brickObj.AddComponent<MeshRenderer>();
            mr.sharedMaterial = debrisMaterial;

            // Set color
            var mpb = new MaterialPropertyBlock();
            mpb.SetFloat("_ColorCode", brick.colorCode);
            if (paletteTexture != null)
            {
                mpb.SetTexture("_PaletteTex", paletteTexture);
                mpb.SetFloat("_PaletteWidth", paletteTexture.width);
            }
            mr.SetPropertyBlock(mpb);

            // Per-brick collider for accurate collision
            if (debrisColliders)
            {
                if (brickMesh.isReadable)
                {
                    var brickCol = brickObj.AddComponent<MeshCollider>();
                    brickCol.sharedMesh = brickMesh;
                    brickCol.convex = true;
                }
                else
                {
                    var box = brickObj.AddComponent<BoxCollider>();
                    box.center = brickMesh.bounds.center;
                    box.size = brickMesh.bounds.size;
                }
            }
        }

        // Add physics to parent chunk
        var rb = chunkObj.AddComponent<Rigidbody>();
        rb.mass = 0.1f * brickIndices.Count;
        rb.linearDamping = 0.3f;
        rb.angularDamping = 0.3f;
        rb.collisionDetectionMode = CollisionDetectionMode.Discrete;

        if (!debugFreezeDebris)
        {
            // Apply explosion force
            Vector3 awayDir = (chunkCenter - hitPoint).normalized;
            if (awayDir.sqrMagnitude < 0.01f)
                awayDir = UnityEngine.Random.insideUnitSphere.normalized;

            Vector3 explosionVel = awayDir * (explosionForce * 0.7f); // Chunks move slower
            explosionVel += Vector3.up * (explosionForce * 0.2f);
            explosionVel += UnityEngine.Random.insideUnitSphere * (randomVelocity * 0.5f);

            rb.linearVelocity = explosionVel;
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 5f;
        }
        else
        {
            rb.useGravity = false;
            rb.constraints = RigidbodyConstraints.FreezeAll;
        }

        // Assign debris layer to prevent debris-vs-debris jamming
        if (debrisLayer >= 0)
            chunkObj.layer = debrisLayer;

        // Make chunk destructible by projectiles
        var dc2 = chunkObj.AddComponent<DestructibleChunk>();
        dc2.damageSystem = this;
        if (structure.model.adjacencyList != null)
            dc2.InitConnectivity(brickIndices, structure.model.sortedBricks, structure.model.adjacencyList);

        // Track; only decay if below persist threshold
        if (brickIndices.Count < persistBrickThreshold)
        {
            var fader = chunkObj.AddComponent<DebrisFader>();
            fader.delay = debrisDecayDelay;
            fader.fadeDuration = debrisFadeDuration;
        }
        _activeDebris.Add(chunkObj);
        
        Debug.Log($"[BrickDamage] Spawned chunk with {brickIndices.Count} bricks at {chunkCenter}");
    }

    /// <summary>
    /// Spawn debris for a list of brick indices without applying damage.
    /// Used by NetworkBrickStructure on clients when they detect newly-dead bricks
    /// from the network-synced alive flags.
    /// </summary>
    public void SpawnDebrisForBricks(BrickStructure structure, List<int> brickIndices)
    {
        if (brickIndices == null || brickIndices.Count == 0) return;

        // Compute a centroid to use as the "hit point" for debris direction
        Vector3 centroid = Vector3.zero;
        int validCount = 0;
        for (int i = 0; i < brickIndices.Count; i++)
        {
            int idx = brickIndices[i];
            if (idx >= 0 && idx < structure.worldTransforms.Length)
            {
                centroid += (Vector3)structure.worldTransforms[idx].GetColumn(3);
                validCount++;
            }
        }
        if (validCount > 0) centroid /= validCount;

        if (brickIndices.Count >= chunkSizeThreshold)
            SpawnChunkDebris(structure, brickIndices, centroid);
        else
            SpawnIndividualDebris(structure, brickIndices, centroid);
    }
}
