using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Attached to spawned debris chunks. Stores local connectivity sub-graph so
/// cannonball hits use the same Teardown-style crater + flood-fill logic as
/// the original BrickStructure: only the impact area shatters, disconnected
/// pieces break off, and the largest surviving group stays as a chunk.
/// </summary>
public class DestructibleChunk : MonoBehaviour
{
    [HideInInspector] public BrickDamageSystem damageSystem;

    // Local adjacency graph (remapped to 0-based child indices)
    int[] _nbrOffsets;   // per brick: start index into _nbrs
    int[] _nbrCounts;    // per brick: number of neighbors
    int[] _nbrs;         // flat-packed local neighbor indices
    byte[] _alive;

    /// <summary>
    /// Build a local connectivity sub-graph from the original structure data.
    /// Called by BrickDamageSystem right after spawning the chunk.
    /// </summary>
    public void InitConnectivity(List<int> originalIndices,
        LDrawModelAsset.BrickInstance[] sortedBricks, int[] adjacencyList)
    {
        int count = originalIndices.Count;
        _alive = new byte[count];
        _nbrOffsets = new int[count];
        _nbrCounts = new int[count];

        // global brick index → local chunk index
        var g2l = new Dictionary<int, int>(count);
        for (int i = 0; i < count; i++)
        {
            g2l[originalIndices[i]] = i;
            _alive[i] = 1;
        }

        // Extract edges where both endpoints live inside this chunk
        var flat = new List<int>();
        for (int li = 0; li < count; li++)
        {
            int gi = originalIndices[li];
            var brick = sortedBricks[gi];

            _nbrOffsets[li] = flat.Count;
            int start = flat.Count;

            for (int n = 0; n < brick.neighborCount; n++)
            {
                int gn = adjacencyList[brick.connectivityOffset + n];
                if (g2l.TryGetValue(gn, out int ln))
                    flat.Add(ln);
            }
            _nbrCounts[li] = flat.Count - start;
        }
        _nbrs = flat.ToArray();
    }

    /// <summary>
    /// Teardown-style damage: crater around impact → flood-fill →
    /// largest connected group stays, others break off.
    /// </summary>
    public void Shatter(Vector3 hitPoint, Vector3 hitVelocity, float projectileMass)
    {
        var children = CollectChildren();

        // Fallback if no connectivity was baked
        if (_alive == null || _alive.Length == 0 || children.Count != _alive.Length)
        {
            FullShatter(children, hitPoint, hitVelocity, projectileMass);
            return;
        }

        float impactForce = hitVelocity.magnitude * projectileMass;
        float normalizedForce = Mathf.Clamp01(impactForce / 60f);
        Vector3 hitDir = hitVelocity.normalized;

        // ── 1. Crater ──
        int hitBrick = ClosestAliveBrick(children, hitPoint);
        if (hitBrick < 0) return;

        var shattered = new List<int>();
        _alive[hitBrick] = 0;
        shattered.Add(hitBrick);

        float craterRadius = 0.5f + normalizedForce * 1.5f;
        float craterSq = craterRadius * craterRadius;
        Vector3 hitBrickPos = children[hitBrick].position;

        for (int i = 0; i < children.Count; i++)
        {
            if (_alive[i] == 0) continue;
            if ((children[i].position - hitBrickPos).sqrMagnitude <= craterSq)
            {
                _alive[i] = 0;
                shattered.Add(i);
            }
        }

        // ── 2. Connected components among survivors ──
        var components = FindComponents(children.Count);

        // ── 3. Largest stays, rest detaches ──
        int largestIdx = -1, largestSize = 0;
        for (int i = 0; i < components.Count; i++)
        {
            if (components[i].Count > largestSize)
            { largestSize = components[i].Count; largestIdx = i; }
        }

        var parentRb = GetComponent<Rigidbody>();
        Vector3 inheritVel = parentRb != null ? parentRb.linearVelocity : Vector3.zero;

        int layer = damageSystem != null ? damageSystem.debrisLayer : -1;
        float decayDelay = damageSystem != null ? damageSystem.debrisDecayDelay : 4f;
        float fadeDur = damageSystem != null ? damageSystem.debrisFadeDuration : 1.5f;
        int persistThr = damageSystem != null ? damageSystem.persistBrickThreshold : 3;

        // Shattered bricks → individual debris
        foreach (int idx in shattered)
            SpawnBrickDebris(children[idx], hitPoint, hitDir, impactForce,
                             inheritVel, layer, decayDelay, fadeDur);

        // Detached groups → sub-chunks or individual debris
        for (int c = 0; c < components.Count; c++)
        {
            if (c == largestIdx) continue;
            var group = components[c];
            if (group.Count == 1)
                SpawnBrickDebris(children[group[0]], hitPoint, hitDir,
                                 impactForce * 0.3f, inheritVel, layer, decayDelay, fadeDur);
            else
                SpawnSubChunk(children, group, hitPoint, hitDir, normalizedForce,
                              inheritVel, layer, decayDelay, fadeDur, persistThr);
        }

        // ── 4. Rebuild or destroy this chunk ──
        if (largestIdx >= 0 && largestSize > 0)
            RebuildSelf(children, components[largestIdx], persistThr, decayDelay, fadeDur);
        else
            Destroy(gameObject);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    List<Transform> CollectChildren()
    {
        var list = new List<Transform>();
        foreach (Transform child in transform) list.Add(child);
        return list;
    }

    int ClosestAliveBrick(List<Transform> children, Vector3 point)
    {
        int best = -1; float bestSq = float.MaxValue;
        for (int i = 0; i < children.Count; i++)
        {
            if (_alive[i] == 0) continue;
            float sq = (children[i].position - point).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        return best;
    }

    List<List<int>> FindComponents(int count)
    {
        var visited = new bool[count];
        var result = new List<List<int>>();
        var queue = new Queue<int>();

        for (int i = 0; i < count; i++)
        {
            if (_alive[i] == 0 || visited[i]) continue;
            var comp = new List<int>();
            queue.Enqueue(i);
            visited[i] = true;
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                comp.Add(cur);
                for (int n = 0; n < _nbrCounts[cur]; n++)
                {
                    int nb = _nbrs[_nbrOffsets[cur] + n];
                    if (!visited[nb] && _alive[nb] == 1)
                    { visited[nb] = true; queue.Enqueue(nb); }
                }
            }
            result.Add(comp);
        }
        return result;
    }

    void SpawnBrickDebris(Transform brick, Vector3 hitPoint, Vector3 hitDir,
        float force, Vector3 inheritVel, int layer, float decayDelay, float fadeDur)
    {
        var mf = brick.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) { Destroy(brick.gameObject); return; }

        brick.SetParent(null, true);

        var rb = brick.gameObject.AddComponent<Rigidbody>();
        rb.mass = 0.05f;
        rb.linearDamping = 0.2f;
        rb.angularDamping = 0.3f;

        if (mf.sharedMesh.isReadable)
        {
            var col = brick.gameObject.AddComponent<MeshCollider>();
            col.sharedMesh = mf.sharedMesh;
            col.convex = true;
        }
        else
        {
            var box = brick.gameObject.AddComponent<BoxCollider>();
            box.center = mf.sharedMesh.bounds.center;
            box.size = mf.sharedMesh.bounds.size;
        }

        if (layer >= 0) brick.gameObject.layer = layer;

        Vector3 away = (brick.position - hitPoint).normalized;
        if (away.sqrMagnitude < 0.01f) away = hitDir;
        float push = Mathf.Clamp(force * 0.03f, 0.5f, 5f);
        rb.linearVelocity = inheritVel + away * push + Vector3.up * (push * 0.3f);
        rb.angularVelocity = Random.insideUnitSphere * 5f;

        var fader = brick.gameObject.AddComponent<DebrisFader>();
        fader.delay = decayDelay;
        fader.fadeDuration = fadeDur;
    }

    void SpawnSubChunk(List<Transform> allChildren, List<int> indices,
        Vector3 hitPoint, Vector3 hitDir, float normalizedForce,
        Vector3 inheritVel, int layer, float decayDelay, float fadeDur, int persistThr)
    {
        Vector3 center = Vector3.zero;
        for (int i = 0; i < indices.Count; i++)
            center += allChildren[indices[i]].position;
        center /= indices.Count;

        var chunkObj = new GameObject($"SubChunk_{indices.Count}bricks");
        chunkObj.transform.position = center;
        chunkObj.transform.rotation = Quaternion.identity;

        // Build sub-adjacency: remap old local indices → new local indices
        var old2new = new Dictionary<int, int>(indices.Count);
        for (int i = 0; i < indices.Count; i++)
            old2new[indices[i]] = i;

        var subOffsets = new int[indices.Count];
        var subCounts = new int[indices.Count];
        var subFlat = new List<int>();

        for (int i = 0; i < indices.Count; i++)
        {
            int oldLi = indices[i];
            subOffsets[i] = subFlat.Count;
            int start = subFlat.Count;
            for (int n = 0; n < _nbrCounts[oldLi]; n++)
            {
                int oldNb = _nbrs[_nbrOffsets[oldLi] + n];
                if (old2new.TryGetValue(oldNb, out int newNb))
                    subFlat.Add(newNb);
            }
            subCounts[i] = subFlat.Count - start;

            var childXf = allChildren[indices[i]];
            childXf.SetParent(chunkObj.transform, true);

            // Per-brick collider if not already present
            var mf = childXf.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && childXf.GetComponent<Collider>() == null)
            {
                if (mf.sharedMesh.isReadable)
                {
                    var bCol = childXf.gameObject.AddComponent<MeshCollider>();
                    bCol.sharedMesh = mf.sharedMesh;
                    bCol.convex = true;
                }
                else
                {
                    var box = childXf.gameObject.AddComponent<BoxCollider>();
                    box.center = mf.sharedMesh.bounds.center;
                    box.size = mf.sharedMesh.bounds.size;
                }
            }
        }

        var rb = chunkObj.AddComponent<Rigidbody>();
        rb.mass = 0.05f * indices.Count;
        rb.linearDamping = 0.2f;
        rb.angularDamping = 0.3f;

        Vector3 pushDir = (center - hitPoint).normalized;
        if (pushDir.sqrMagnitude < 0.01f) pushDir = hitDir;
        float pushStr = normalizedForce * 2f;
        rb.linearVelocity = inheritVel + pushDir * pushStr + Vector3.down * 0.5f;
        rb.angularVelocity = Random.insideUnitSphere * (normalizedForce * 3f);

        if (layer >= 0) chunkObj.layer = layer;

        var dc = chunkObj.AddComponent<DestructibleChunk>();
        dc.damageSystem = damageSystem;
        dc._nbrOffsets = subOffsets;
        dc._nbrCounts = subCounts;
        dc._nbrs = subFlat.ToArray();
        dc._alive = new byte[indices.Count];
        for (int i = 0; i < indices.Count; i++) dc._alive[i] = 1;

        if (indices.Count < persistThr)
        {
            var fader = chunkObj.AddComponent<DebrisFader>();
            fader.delay = decayDelay;
            fader.fadeDuration = fadeDur;
        }
    }

    void RebuildSelf(List<Transform> allChildren, List<int> survivors,
        int persistThr, float decayDelay, float fadeDur)
    {
        // Destroy dead children that haven't been reparented
        var keepSet = new HashSet<int>(survivors);
        for (int i = 0; i < allChildren.Count; i++)
        {
            if (!keepSet.Contains(i) && allChildren[i] != null && allChildren[i].parent == transform)
                Destroy(allChildren[i].gameObject);
        }

        // Remap adjacency for survivors only
        var old2new = new Dictionary<int, int>(survivors.Count);
        for (int i = 0; i < survivors.Count; i++)
            old2new[survivors[i]] = i;

        var newOffsets = new int[survivors.Count];
        var newCounts = new int[survivors.Count];
        var newFlat = new List<int>();
        var newAlive = new byte[survivors.Count];

        for (int i = 0; i < survivors.Count; i++)
        {
            int oldLi = survivors[i];
            newOffsets[i] = newFlat.Count;
            int start = newFlat.Count;
            for (int n = 0; n < _nbrCounts[oldLi]; n++)
            {
                int oldNb = _nbrs[_nbrOffsets[oldLi] + n];
                if (old2new.TryGetValue(oldNb, out int newNb))
                    newFlat.Add(newNb);
            }
            newCounts[i] = newFlat.Count - start;
            newAlive[i] = 1;
        }

        _nbrOffsets = newOffsets;
        _nbrCounts = newCounts;
        _nbrs = newFlat.ToArray();
        _alive = newAlive;

        // Ensure surviving children have per-brick colliders
        foreach (Transform child in transform)
        {
            if (child == null) continue;
            var mf = child.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && child.GetComponent<Collider>() == null)
            {
                if (mf.sharedMesh.isReadable)
                {
                    var bCol = child.gameObject.AddComponent<MeshCollider>();
                    bCol.sharedMesh = mf.sharedMesh;
                    bCol.convex = true;
                }
                else
                {
                    var box = child.gameObject.AddComponent<BoxCollider>();
                    box.center = mf.sharedMesh.bounds.center;
                    box.size = mf.sharedMesh.bounds.size;
                }
            }
        }

        gameObject.name = $"Chunk_{survivors.Count}bricks";

        if (survivors.Count < persistThr && GetComponent<DebrisFader>() == null)
        {
            var fader = gameObject.AddComponent<DebrisFader>();
            fader.delay = decayDelay;
            fader.fadeDuration = fadeDur;
        }
    }

    /// <summary>Fallback when no connectivity data exists.</summary>
    void FullShatter(List<Transform> children, Vector3 hitPoint,
        Vector3 hitVelocity, float projectileMass)
    {
        float force = hitVelocity.magnitude * projectileMass;
        Vector3 hitDir = hitVelocity.normalized;
        int layer = damageSystem != null ? damageSystem.debrisLayer : -1;
        float decayDelay = damageSystem != null ? damageSystem.debrisDecayDelay : 4f;
        float fadeDur = damageSystem != null ? damageSystem.debrisFadeDuration : 1.5f;
        var parentRb = GetComponent<Rigidbody>();
        Vector3 inheritVel = parentRb != null ? parentRb.linearVelocity : Vector3.zero;

        foreach (var child in children)
            SpawnBrickDebris(child, hitPoint, hitDir, force, inheritVel,
                             layer, decayDelay, fadeDur);
        Destroy(gameObject);
    }
}
