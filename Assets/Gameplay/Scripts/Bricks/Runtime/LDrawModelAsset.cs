using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "LDrawModel", menuName = "Bricks/LDraw Model Asset", order = 3)]
public class LDrawModelAsset : ScriptableObject
{
    // ─── Legacy format (kept for backward compat / serialization) ───
    [Serializable]
    public struct Brick
    {
        public string part;
        public int colorCode;
        public Matrix4x4 local;
    }

    [HideInInspector]
    public List<Brick> bricks = new();

    // ─── New GPU-ready format ───

    /// <summary>Blittable per-brick data. No managed types — can be copied to NativeArray / GPU.</summary>
    [Serializable]
    public struct BrickInstance
    {
        public int       partId;              // index into LDrawPartRegistry.entries
        public int       colorCode;           // LDraw palette index
        public Matrix4x4 localTransform;      // pre-computed local-space TRS
        public int       connectivityOffset;  // index into adjacencyList
        public int       neighborCount;       // number of neighbors in adjacencyList
    }

    /// <summary>One draw-call batch: all bricks sharing same mesh + color.</summary>
    [Serializable]
    public struct BatchGroup
    {
        public int partId;     // registry index → mesh
        public int colorCode;  // palette index
        public int startIndex; // first BrickInstance index in sortedBricks[]
        public int count;      // number of bricks in this group
    }

    [Header("GPU-Ready Data (auto-generated)")]
    [HideInInspector]
    public BrickInstance[] sortedBricks;
    [HideInInspector]
    public BatchGroup[]    batchGroups;

    [Header("Connectivity Graph")]
    [HideInInspector]
    public int[]   adjacencyList;           // flat-packed neighbor indices
    [HideInInspector]
    public float[] connectionStrengths;     // parallel to adjacencyList — strength of each connection (0..1)

    /// <summary>True when sortedBricks/batchGroups have been baked.</summary>
    public bool HasGpuData => sortedBricks != null && sortedBricks.Length > 0
                           && batchGroups  != null && batchGroups.Length > 0;

    /// <summary>Total brick count (prefers GPU data, falls back to legacy).</summary>
    public int BrickCount => HasGpuData ? sortedBricks.Length : bricks.Count;
}