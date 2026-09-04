using UnityEngine;

public abstract class PersistentSingleton<T> : MonoBehaviour where T : MonoBehaviour
{
    public static T Instance { get; private set; }

    // Reset is handled by PersistentSingletonReset to avoid
    // [RuntimeInitializeOnLoadMethod] inside a generic class.
    internal static void ResetInstance() => Instance = null;

    protected virtual void Awake()
    {
        transform.SetParent(null, false);

        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this as T;
        DontDestroyOnLoad(gameObject);
    }
}
