using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "LDrawPartRegistry", menuName = "Bricks/LDraw Part Registry", order = 2)]
public class LDrawPartRegistry : ScriptableObject
{
    [Serializable]
    public struct Entry
    {
        public string part;
        public Mesh mesh;
    }

    public List<Entry> entries = new();

    // ── Lookup maps (built lazily) ──
    Dictionary<string, Mesh> _meshByName;
    Dictionary<string, int>  _idByName;
    Mesh[]                   _meshById;

    void OnEnable() => Rebuild();

    /// <summary>Invalidate lookup maps so they are rebuilt on next access.
    /// Call this after modifying entries at runtime or in the editor.</summary>
    public void Invalidate() { _meshByName = null; _idByName = null; _meshById = null; }

    /// <summary>Force-rebuild all lookup maps from the current entries list.</summary>
    public void Rebuild()
    {
        _meshByName = new Dictionary<string, Mesh>(entries.Count, StringComparer.OrdinalIgnoreCase);
        _idByName   = new Dictionary<string, int>(entries.Count, StringComparer.OrdinalIgnoreCase);
        _meshById   = new Mesh[entries.Count];

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (string.IsNullOrWhiteSpace(e.part) || e.mesh == null) continue;

            string key = Normalize(e.part);
            _meshByName[key] = e.mesh;
            _idByName[key]   = i;
            _meshById[i]     = e.mesh;
        }
    }

    static string Normalize(string p) => p.Replace('\\', '/').Trim();

    void EnsureBuilt() { if (_meshByName == null) Rebuild(); }

    // ── String-keyed lookups (importer / legacy) ──
    public bool TryGet(string part, out Mesh mesh)
    {
        EnsureBuilt();
        return _meshByName.TryGetValue(Normalize(part), out mesh);
    }

    // ── Integer-keyed lookups (runtime hot path) ──

    /// <summary>Returns a stable integer ID for a part name, or -1 if not found.</summary>
    public int GetPartId(string part)
    {
        EnsureBuilt();
        return _idByName.TryGetValue(Normalize(part), out int id) ? id : -1;
    }

    /// <summary>Returns the mesh for a given integer part ID (index into entries).</summary>
    public Mesh GetMeshById(int partId)
    {
        EnsureBuilt();
        return (partId >= 0 && partId < _meshById.Length) ? _meshById[partId] : null;
    }

    /// <summary>Total number of registered entries (used to size arrays).</summary>
    public int EntryCount => entries.Count;
}