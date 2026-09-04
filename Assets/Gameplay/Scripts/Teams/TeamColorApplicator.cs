using System;
using Fusion;
using UnityEngine;

namespace BricksBladesBlackpowder.Teams
{
    public class TeamColorApplicator : NetworkBehaviour
    {
        [Serializable]
        public struct TeamMaterialEntry
        {
            public Team team;
            public Material material;
        }

        [SerializeField] TeamMaterialEntry[] teamMaterials;
        [SerializeField] string torsoChildPath = "Root/Hips/Torso/TorsoMesh";
        [SerializeField] int materialSlot = 0;

        [Networked, OnChangedRender(nameof(ApplyColor))]
        private Team NetTeam { get; set; }

        /// <summary>Call on the server after spawning to set this object's team color.</summary>
        public void SetTeam(Team t)
        {
            NetTeam = t;
        }

        public override void Spawned()
        {
            ApplyColor();
        }

        public bool TryGetTeamColor(Team team, out Color color)
        {
            color = Color.white;

            if (teamMaterials == null || teamMaterials.Length == 0)
                return false;

            for (int i = 0; i < teamMaterials.Length; i++)
            {
                var entry = teamMaterials[i];
                if (entry.team != team || entry.material == null)
                    continue;

                if (entry.material.HasProperty("_BaseColor"))
                {
                    color = entry.material.GetColor("_BaseColor");
                    return true;
                }

                if (entry.material.HasProperty("_Color"))
                {
                    color = entry.material.GetColor("_Color");
                    return true;
                }
            }

            return false;
        }

        private void ApplyColor()
        {
            if (teamMaterials == null || teamMaterials.Length == 0)
                return;

            Material mat = null;
            foreach (var entry in teamMaterials)
            {
                if (entry.team == NetTeam)
                {
                    mat = entry.material;
                    break;
                }
            }

            if (mat == null)
                return;

            var torso = transform.Find(torsoChildPath);
            if (torso == null)
                return;

            var rend = torso.GetComponent<MeshRenderer>();
            if (rend == null)
                return;

            var mats = rend.sharedMaterials;
            if (materialSlot >= mats.Length)
                return;

            mats[materialSlot] = mat;
            rend.sharedMaterials = mats;
        }
    }
}
