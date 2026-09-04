using UnityEngine;
using BricksBladesBlackpowder.Teams;

namespace BricksBladesBlackpowder.NPC
{
    [CreateAssetMenu(menuName = "NPC/NPC Profile", fileName = "NPCProfile")]
    public class NPCProfile : ScriptableObject
    {
        public string displayName = "NPC";
        public int maxHearts = 2;
        public float moveSpeed = 5f;
        public float attackRange = 2f;
        public int attackDamage = 1;
        public float attackCooldown = 1.5f;
        public Team defaultTeam = Team.None;
        // Extend with more archetype/role data as needed
    }
}
