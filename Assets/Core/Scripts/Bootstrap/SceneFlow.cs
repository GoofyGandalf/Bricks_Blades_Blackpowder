using System.Threading.Tasks;
using UnityEngine.SceneManagement;
using UnityEngine;

public class SceneFlow : MonoBehaviour
{
    public Task LoadBootTargetAsync() => LoadSingleAsync("MainMenu");
    public Task GoToLobbyAsync() => LoadSingleAsync("Lobby");
    //public Task GoToMatchAsync() => LoadSingleAsync("Match");

    static async Task LoadSingleAsync(string sceneName)
    {
        var op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        while (!op.isDone) await Task.Yield();
    }
}
