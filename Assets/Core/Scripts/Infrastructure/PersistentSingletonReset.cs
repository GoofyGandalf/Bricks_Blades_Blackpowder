using UnityEngine;

/// <summary>
/// [RuntimeInitializeOnLoadMethod] cannot live in a generic class.
/// This non-generic helper resets all PersistentSingleton instances
/// on domain reload so they don't hold stale references in the Editor.
/// </summary>
static class PersistentSingletonReset
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetAll()
    {
        PersistentSingleton<AppRoot>.ResetInstance();
    }
}
