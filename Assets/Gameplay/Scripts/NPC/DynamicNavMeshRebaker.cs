using UnityEngine;
using UnityEngine.AI;
using BricksBladesBlackpowder.NPC;
using Unity.AI.Navigation;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Triggers an initial NavMesh bake once all BrickColliderManagers have finished
/// building their per-brick colliders, then rebakes whenever bricks are destroyed.
/// 
/// Key fix: the old version mutated damageVersion and baked before colliders existed,
/// meaning the NavMesh had no walls — NPCs walked through everything.
/// </summary>
[RequireComponent(typeof(NavMeshSurface))]
public class DynamicNavMeshRebaker : MonoBehaviour
{
    public float rebakeDelay = 0.5f;

    private float rebakeTimer = 0f;
    private bool needsRebake = false;
    private bool initialBakeDone = false;
    private bool isBaking = false;
    private NavMeshSurface surface;

    // Track the last-seen damageVersion per structure without mutating it.
    private readonly Dictionary<BrickStructure, int> lastKnownVersions =
        new Dictionary<BrickStructure, int>();

    void Awake()
    {
        surface = GetComponent<NavMeshSurface>();
    }

    IEnumerator Start()
    {
        // Wait until every BrickColliderManager has finished building its per-brick
        // colliders. Without this the walls don't exist as physics geometry and the
        // NavMesh bakes straight through them.
        yield return WaitForAllColliders();

        surface.BuildNavMesh();
        initialBakeDone = true;

        // Snapshot all current versions so Update only reacts to future changes.
        foreach (var s in BrickStructure.Registry)
            lastKnownVersions[s] = s.damageVersion;
    }

    IEnumerator WaitForAllColliders()
    {
        while (true)
        {
            var managers = FindObjectsByType<BrickColliderManager>(FindObjectsSortMode.None);
            bool allDone = true;
            foreach (var m in managers)
            {
                if (!m.BuildComplete) { allDone = false; break; }
            }
            if (allDone) yield break;
            yield return null;
        }
    }

    void Update()
    {
        if (!initialBakeDone) return;

        bool changed = false;
        foreach (var s in BrickStructure.Registry)
        {
            if (!lastKnownVersions.TryGetValue(s, out int last) ||
                s.damageVersion != last)
            {
                lastKnownVersions[s] = s.damageVersion;
                changed = true;
            }
        }

        if (changed)
        {
            needsRebake = true;
            rebakeTimer = rebakeDelay;
        }

        if (needsRebake)
        {
            rebakeTimer -= Time.deltaTime;
            if (rebakeTimer <= 0f && !isBaking)
            {
                needsRebake = false;
                StartCoroutine(RebakeAsync());
            }
        }
    }

    IEnumerator RebakeAsync()
    {
        isBaking = true;
        // UpdateNavMesh runs the bake mostly off the main thread, avoiding a hard freeze.
        // Fall back to synchronous BuildNavMesh if navMeshData isn't initialised yet.
        if (surface.navMeshData != null)
            yield return surface.UpdateNavMesh(surface.navMeshData);
        else
            surface.BuildNavMesh();
        isBaking = false;
    }
}
