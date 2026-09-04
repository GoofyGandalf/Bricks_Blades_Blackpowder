using UnityEngine;
using BricksBladesBlackpowder.Teams;

public interface IDamageable
{
    Team GetTeam();
    bool TryTakeDamage(GameObject attacker, int heartsDamage, Vector3 hitFromDir, float knockback);
    bool IsDead { get; }
}
