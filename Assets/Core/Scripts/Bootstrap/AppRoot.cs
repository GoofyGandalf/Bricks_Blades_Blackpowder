using UnityEngine;
using UnityEngine.SceneManagement;

public class AppRoot : PersistentSingleton<AppRoot>
{
    public SceneFlow SceneFlow { get; private set; }
    public GameSession GameSession { get; private set; }

    protected override void Awake()
    {
        base.Awake();

        SceneFlow   = GetComponent<SceneFlow>();
        GameSession = GetComponent<GameSession>();

        if (SceneFlow == null)
            Debug.LogError("SceneFlow missing on AppRoot");

        if (GameSession == null)
            Debug.LogError("GameSession missing on AppRoot");
    }

    async void Start()
    {
        if (SceneManager.GetActiveScene().name == "Boot")
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 144;
            await SceneFlow.LoadBootTargetAsync();
        }
    }
}
