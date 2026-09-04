using UnityEngine;

[DefaultExecutionOrder(90)]
public class LegoWaterAnimator : MonoBehaviour
{
    public bool animateWater = true;
    public float globalBobAmplitude = 0.55f;
    public float globalBobFrequency = 0.7f;
    public float waveAmplitudePrimary = 0.35f;
    public float waveFrequencyPrimary = 0.04f;
    public float waveSpeedPrimary = 1.1f;
    public float waveAmplitudeSecondary = 0.18f;
    public float waveFrequencySecondary = 0.06f;
    public float waveSpeedSecondary = 1.8f;

    LDrawModelRenderer _renderer;

    void OnEnable()
    {
        _renderer = GetComponent<LDrawModelRenderer>();
        ApplyToRenderer();
    }

    void OnValidate()
    {
        if (_renderer == null)
            _renderer = GetComponent<LDrawModelRenderer>();
        ApplyToRenderer();
    }

    void ApplyToRenderer()
    {
        if (_renderer == null)
            return;

        _renderer.enableWaveAnimation = animateWater;
        _renderer.globalBobAmplitude = globalBobAmplitude;
        _renderer.globalBobFrequency = globalBobFrequency;
        _renderer.waveAmplitudePrimary = waveAmplitudePrimary;
        _renderer.waveFrequencyPrimary = waveFrequencyPrimary;
        _renderer.waveSpeedPrimary = waveSpeedPrimary;
        _renderer.waveAmplitudeSecondary = waveAmplitudeSecondary;
        _renderer.waveFrequencySecondary = waveFrequencySecondary;
        _renderer.waveSpeedSecondary = waveSpeedSecondary;
    }
}
