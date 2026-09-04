using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

public class OptionsMenu : MonoBehaviour
{
    [SerializeField] private TMP_InputField sensitivityInput;

    private const string SensitivityKey = "MouseSensitivityX";
    private const string MusicMutedKey = "MusicMuted";

    void Start()
    {
        float savedSensitivity =
            PlayerPrefs.GetFloat(SensitivityKey, 220f);

        sensitivityInput.text = "";

        SetSensitivityPlaceholder(savedSensitivity);
    }

    public void ToggleMusicMute()
    {
        bool currentlyMuted =
            PlayerPrefs.GetInt(MusicMutedKey, 0) == 1;

        bool newMutedValue = !currentlyMuted;

        PlayerPrefs.SetInt(MusicMutedKey, newMutedValue ? 1 : 0);
        PlayerPrefs.Save();
    }

    public void ApplySensitivity()
    {
        float value;

        if (string.IsNullOrWhiteSpace(sensitivityInput.text))
        {
            value = PlayerPrefs.GetFloat(SensitivityKey, 220f);
        }
        else if (!float.TryParse(sensitivityInput.text, out value))
        {
            return;
        }

        value = Mathf.Clamp(value, 50f, 250f);

        PlayerPrefs.SetFloat(SensitivityKey, value);
        PlayerPrefs.Save();

        sensitivityInput.text = "";

        SetSensitivityPlaceholder(value);
    }

    private void SetSensitivityPlaceholder(float value)
    {
        if (sensitivityInput == null || sensitivityInput.placeholder == null)
            return;

        TMP_Text placeholder =
            sensitivityInput.placeholder.GetComponent<TMP_Text>();

        if (placeholder != null)
        {
            placeholder.text = value.ToString();
        }
    }

    public void ReturnToMainMenu()
    {
        SceneManager.LoadScene("MainMenu");
    }
}