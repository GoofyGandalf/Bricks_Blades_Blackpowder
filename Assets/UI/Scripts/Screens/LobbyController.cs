using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using Fusion;

public class LobbyController : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] Button startButton;

    [Header("Scenes (Build Settings index)")]
    [SerializeField] int matchSceneBuildIndex = 0; // set this to Match's index in Build Settings

    NetworkRunner runner;

    void Awake()
    {
        if (startButton)
            startButton.onClick.AddListener(StartMatch);
    }

    void OnEnable()
    {
        FindRunner();
        RefreshButton();
    }

    void FindRunner()
    {
        if (runner != null && runner.IsRunning) return;

        var bootstrap = AppRoot.Instance
            ? AppRoot.Instance.GetComponent<BBB_FusionBootstrap>()
            : null;

        runner = bootstrap != null ? bootstrap.Runner : null;
    }

    void Update()
    {
        // lightweight: keep visibility correct if host/client state changes
        FindRunner();
        RefreshButton();
    }

    void RefreshButton()
    {
        if (!startButton) return;

        bool show =
            runner != null &&
            runner.IsRunning &&
            runner.IsServer;

        if (startButton.gameObject.activeSelf != show)
            startButton.gameObject.SetActive(show);
    }

    void StartMatch()
    {
        if (runner == null || !runner.IsRunning) return;
        if (!runner.IsServer) return;

        var matchScene = SceneRef.FromIndex(matchSceneBuildIndex);

        // Fusion scene sync: host triggers, everyone follows
        runner.LoadScene(matchScene, LoadSceneMode.Single);
    }
}
