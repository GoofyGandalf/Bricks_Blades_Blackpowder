using UnityEngine;

public class GameSession : MonoBehaviour
{
    public static GameSession I { get; private set; }

    [Header("Selection")]
    public string selectedMapSceneName = "Arena_01";

    [Header("Session")]
    public string sessionName = "BBB_Room";
    public string sessionPassword = "";

    void Awake()
    {
        I = this;
        DontDestroyOnLoad(gameObject);
    }
}
