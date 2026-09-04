using UnityEngine;
using BricksBladesBlackpowder.NPC;

namespace BricksBladesBlackpowder.NPC
{
    public class NPCSpawnGroup : MonoBehaviour
    {
        public NPCSpawner spawner;
        public int groupSize = 3;

        void Start()
        {
            if (spawner)
            {
                for (int i = 0; i < groupSize; i++)
                    spawner.SpawnNPC(i);
            }
        }
    }
}
