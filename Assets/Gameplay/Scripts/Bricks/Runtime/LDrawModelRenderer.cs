using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class LDrawModelRenderer : MonoBehaviour
{
    public LDrawModelAsset model;
    public LDrawPartRegistry parts;
    public Material brickMaterial;
    public Texture2D paletteTexture;

    [Range(1, 1023)]
    public int batchSize = 1023;

    [Header("Optional Wave Animation")]
    public bool enableWaveAnimation;
    public float globalBobAmplitude = 0.18f;
    public float globalBobFrequency = 0.55f;
    public float waveAmplitudePrimary = 0.12f;
    public float waveFrequencyPrimary = 0.35f;
    public float waveSpeedPrimary = 0.75f;
    public float waveAmplitudeSecondary = 0.06f;
    public float waveFrequencySecondary = 0.8f;
    public float waveSpeedSecondary = 1.2f;

    // ─── Baked data (rebuilt only when model/parts change) ───
    struct BakedBrick
    {
        public Mesh mesh;
        public int colorCode;
        public Matrix4x4 baseWorld; // root * local, baked once
        public float wx, wz;        // world X/Z for wave phase (cached from baseWorld)
    }

    struct BatchGroup
    {
        public Mesh mesh;
        public int colorCode;
        public int start;
        public int count;
    }

    BakedBrick[] _bakedBricks;
    BatchGroup[] _batchGroups;
    Matrix4x4[][] _batchMatrices;   // pre-allocated per group, reused every frame
    Matrix4x4[][][] _staticChunkMatrices; // [group][chunk][matrix]
    MaterialPropertyBlock[] _groupMpbs;

    LDrawModelAsset _cachedModel;
    LDrawPartRegistry _cachedParts;
    Texture2D _cachedPalette;
    Matrix4x4 _cachedRoot;

    void OnEnable()
    {
        Invalidate();
    }

    void OnDisable() => Invalidate();

    void OnValidate() => Invalidate();

    void Invalidate()
    {
        _cachedModel = null;
        _cachedParts = null;
        _cachedPalette = null;
    }

    void EnsureBaked()
    {
        var root = transform.localToWorldMatrix;
        bool rootChanged = root != _cachedRoot;
        bool paletteChanged = _cachedPalette != paletteTexture;

        if (_cachedModel == model && _cachedParts == parts && !rootChanged && !paletteChanged)
            return;

        _cachedModel = model;
        _cachedParts = parts;
        _cachedPalette = paletteTexture;
        _cachedRoot = root;

        if (model == null || parts == null || model.bricks == null || model.bricks.Count == 0)
        {
            _bakedBricks = null;
            _batchGroups = null;
            _batchMatrices = null;
            _staticChunkMatrices = null;
            _groupMpbs = null;
            return;
        }

        // Gather valid bricks
        var temp = new List<BakedBrick>(model.bricks.Count);
        for (int i = 0; i < model.bricks.Count; i++)
        {
            var b = model.bricks[i];
            if (!parts.TryGet(b.part, out var mesh) || mesh == null) continue;
            Matrix4x4 world = root * b.local;
            Vector4 col3 = world.GetColumn(3);
            temp.Add(new BakedBrick
            {
                mesh = mesh,
                colorCode = b.colorCode,
                baseWorld = world,
                wx = col3.x,
                wz = col3.z,
            });
        }

        // Sort by (mesh instanceID, colorCode) to group identical keys together
        temp.Sort((a, b2) =>
        {
            int mi = a.mesh.GetInstanceID().CompareTo(b2.mesh.GetInstanceID());
            return mi != 0 ? mi : a.colorCode.CompareTo(b2.colorCode);
        });

        _bakedBricks = temp.ToArray();

        // Build batch groups
        var groups = new List<BatchGroup>(32);
        int idx = 0;
        while (idx < _bakedBricks.Length)
        {
            var first = _bakedBricks[idx];
            int start = idx;
            while (idx < _bakedBricks.Length &&
                   _bakedBricks[idx].mesh == first.mesh &&
                   _bakedBricks[idx].colorCode == first.colorCode)
                idx++;

            groups.Add(new BatchGroup
            {
                mesh = first.mesh,
                colorCode = first.colorCode,
                start = start,
                count = idx - start,
            });
        }

        _batchGroups = groups.ToArray();

        // Pre-allocate per-group matrix arrays (max 1023 each, reused every frame for wave animation).
        _batchMatrices = new Matrix4x4[_batchGroups.Length][];
        for (int g = 0; g < _batchGroups.Length; g++)
            _batchMatrices[g] = new Matrix4x4[Mathf.Min(_batchGroups[g].count, 1023)];

        // Build static chunks once so non-animated renders avoid per-frame matrix population.
        _staticChunkMatrices = new Matrix4x4[_batchGroups.Length][][];
        _groupMpbs = new MaterialPropertyBlock[_batchGroups.Length];
        float palW = paletteTexture != null ? paletteTexture.width : 0f;

        for (int gi = 0; gi < _batchGroups.Length; gi++)
        {
            ref BatchGroup grp = ref _batchGroups[gi];
            int chunkCount = (grp.count + 1022) / 1023;
            var chunks = new Matrix4x4[chunkCount][];

            int offset = grp.start;
            int remaining = grp.count;
            for (int ci = 0; ci < chunkCount; ci++)
            {
                int chunkSize = Mathf.Min(remaining, 1023);
                var chunk = new Matrix4x4[chunkSize];
                for (int j = 0; j < chunkSize; j++)
                    chunk[j] = _bakedBricks[offset + j].baseWorld;
                chunks[ci] = chunk;
                offset += chunkSize;
                remaining -= chunkSize;
            }
            _staticChunkMatrices[gi] = chunks;

            var mpb = new MaterialPropertyBlock();
            mpb.SetTexture("_PaletteTex", paletteTexture);
            mpb.SetFloat("_ColorCode", grp.colorCode);
            mpb.SetFloat("_PaletteWidth", palW);
            _groupMpbs[gi] = mpb;
        }
    }

    void LateUpdate()
    {
        if (model == null || parts == null || brickMaterial == null || paletteTexture == null) return;

        EnsureBaked();

        if (_bakedBricks == null || _batchGroups == null || _groupMpbs == null) return;

        bool doWave = enableWaveAnimation && Application.isPlaying;
        float t = doWave ? Time.time : 0f;
        float bob = doWave ? Mathf.Sin(t * globalBobFrequency) * globalBobAmplitude : 0f;

        int layer = gameObject.layer;

        if (!doWave)
        {
            for (int gi = 0; gi < _batchGroups.Length; gi++)
            {
                ref BatchGroup grp = ref _batchGroups[gi];
                var chunks = _staticChunkMatrices[gi];
                var mpb = _groupMpbs[gi];

                for (int ci = 0; ci < chunks.Length; ci++)
                {
                    var chunk = chunks[ci];
                    Graphics.DrawMeshInstanced(
                        grp.mesh, 0, brickMaterial,
                        chunk, chunk.Length, mpb,
                        UnityEngine.Rendering.ShadowCastingMode.On,
                        true, layer);
                }
            }
            return;
        }

        for (int gi = 0; gi < _batchGroups.Length; gi++)
        {
            ref BatchGroup grp = ref _batchGroups[gi];
            int brickCount = grp.count;

            // Reuse pre-allocated buffer for this group (size ≤ 1023)
            var buf = _batchMatrices[gi];
            var mpb = _groupMpbs[gi];

            int brickIdx = grp.start;
            int remaining = brickCount;

            while (remaining > 0)
            {
                int chunk = Mathf.Min(remaining, 1023);
                if (buf.Length < chunk)
                    buf = _batchMatrices[gi] = new Matrix4x4[chunk];

                for (int j = 0; j < chunk; j++)
                {
                    ref BakedBrick bk = ref _bakedBricks[brickIdx + j];
                    if (doWave)
                    {
                        float waveA = Mathf.Sin((bk.wx + bk.wz) * waveFrequencyPrimary + t * waveSpeedPrimary) * waveAmplitudePrimary;
                        float waveB = Mathf.Sin((bk.wx - bk.wz) * waveFrequencySecondary + t * waveSpeedSecondary) * waveAmplitudeSecondary;
                        Matrix4x4 m = bk.baseWorld;
                        m.m13 += bob + waveA + waveB; // Y column offset, no allocation
                        buf[j] = m;
                    }
                    else
                    {
                        buf[j] = bk.baseWorld;
                    }
                }

                Graphics.DrawMeshInstanced(
                    grp.mesh, 0, brickMaterial,
                    buf, chunk, mpb,
                    UnityEngine.Rendering.ShadowCastingMode.On,
                    true, layer);

                brickIdx += chunk;
                remaining -= chunk;
            }
        }
    }
}