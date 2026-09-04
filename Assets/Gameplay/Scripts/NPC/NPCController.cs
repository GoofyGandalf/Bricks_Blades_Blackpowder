using UnityEngine;
using BricksBladesBlackpowder.Teams;

namespace BricksBladesBlackpowder.NPC
{
    [RequireComponent(typeof(NPCHealth))]
    [RequireComponent(typeof(TeamIdentity))]
    public class NPCController : MonoBehaviour
    {
        public NPCProfile profile;
        public NPCHealth health { get; private set; }
        TeamIdentity teamIdentity;

        void Awake()
        {
            health = GetComponent<NPCHealth>();
            teamIdentity = GetComponent<TeamIdentity>();
        }


    }
}
