using TMPro;
using UnityEngine;
using BricksBladesBlackpowder.Teams;

public class DestructionStatsUI : MonoBehaviour
{
    [SerializeField] private TMP_Text piratePercent;
    [SerializeField] private TMP_Text knightPercent;
    [SerializeField] private TMP_Text samuraiPercent;

    [Header("Data Source")]
    [SerializeField] private BaseDestructionWinController source;
    [SerializeField] private bool showRemainingHealth = true;
    [SerializeField] private bool showPercentSuffix = true;

    void Awake()
    {
        if (source == null)
            source = FindFirstObjectByType<BaseDestructionWinController>(FindObjectsInactive.Exclude);
    }

    void Update()
    {
        if (source == null)
        {
            source = FindFirstObjectByType<BaseDestructionWinController>(FindObjectsInactive.Exclude);
            if (source == null) return;
        }

        float pirateValue = GetDisplayValue(Team.Pirates);
        float knightValue = GetDisplayValue(Team.Knights);
        float samuraiValue = GetDisplayValue(Team.Samurai);

        if (piratePercent != null) piratePercent.text = FormatPercent(pirateValue);
        if (knightPercent != null) knightPercent.text = FormatPercent(knightValue);
        if (samuraiPercent != null) samuraiPercent.text = FormatPercent(samuraiValue);
    }

    float GetDisplayValue(Team team)
    {
        if (source == null) return 0f;
        return showRemainingHealth
            ? source.GetTeamRemainingHealthPercent(team)
            : source.GetTeamDestructionPercent(team);
    }

    string FormatPercent(float value)
    {
        int rounded = Mathf.RoundToInt(Mathf.Clamp(value, 0f, 100f));
        return showPercentSuffix ? rounded + "%" : rounded.ToString();
    }
}