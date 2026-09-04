using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class MatchMusicController : MonoBehaviour
{
    [SerializeField] AudioClip musicClip;
    [SerializeField, Range(0f, 1f)] float volume = 0.5f;

    private const string MusicMutedKey = "MusicMuted";

    AudioSource _source;

    void Awake()
    {
        _source = GetComponent<AudioSource>();
        _source.clip = musicClip;
        _source.loop = true;
        _source.volume = volume;
        _source.playOnAwake = false;
        _source.spatialBlend = 0f;
    }

    void Start()
    {
        if (musicClip == null)
        {
            Debug.LogWarning("[MatchMusicController] No music clip assigned.");
            return;
        }

        bool muted = PlayerPrefs.GetInt(MusicMutedKey, 0) == 1;
        _source.mute = muted;

        _source.Play();
    }

    public void ToggleMute()
    {
        bool currentlyMuted = PlayerPrefs.GetInt(MusicMutedKey, 0) == 1;
        bool newMutedValue = !currentlyMuted;

        PlayerPrefs.SetInt(MusicMutedKey, newMutedValue ? 1 : 0);
        PlayerPrefs.Save();

        _source.mute = newMutedValue;
    }

    public void FadeOut(float durationSeconds)
    {
        StartCoroutine(FadeCoroutine(durationSeconds));
    }

    System.Collections.IEnumerator FadeCoroutine(float duration)
    {
        float start = _source.volume;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            _source.volume = Mathf.Lerp(start, 0f, elapsed / duration);
            yield return null;
        }

        _source.Stop();
    }
}