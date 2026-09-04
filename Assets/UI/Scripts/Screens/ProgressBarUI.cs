using UnityEngine;
using BricksBladesBlackpowder.Match;
using BricksBladesBlackpowder.Teams;
using UnityEngine.UI;

public class ProgressBarUI : MonoBehaviour
{
    [SerializeField] private RectTransform fill;
    [SerializeField] private float maxWidth = 120f;
    [SerializeField] private CapturePoint targetCapturePoint;
    [SerializeField] private Image fillImage;
    [SerializeField] private Color neutralColor = Color.white;
    [SerializeField] private Color samuraiFallbackColor = new Color(0.95f, 0.2f, 0.2f, 1f);
    [SerializeField] private Color knightsFallbackColor = new Color(0.2f, 0.45f, 0.95f, 1f);
    [SerializeField] private Color piratesFallbackColor = new Color(0.2f, 0.8f, 0.35f, 1f);

    private TeamColorApplicator teamColorSource;

    void OnEnable()
    {
        CapturePoint.ProgressChanged += HandleCaptureProgressChanged;
        if (fillImage == null && fill != null)
            fillImage = fill.GetComponent<Image>();

        TryBindCapturePoint();
        TryBindTeamColorSource();
    }

    void OnDisable()
    {
        CapturePoint.ProgressChanged -= HandleCaptureProgressChanged;
    }

    void Update()
    {
        if (targetCapturePoint == null)
            TryBindCapturePoint();

        if (teamColorSource == null)
            TryBindTeamColorSource();
    }

    void TryBindCapturePoint()
    {
        if (targetCapturePoint == null)
            targetCapturePoint = FindFirstObjectByType<CapturePoint>(FindObjectsInactive.Exclude);

        if (targetCapturePoint != null)
            SetCaptureState(targetCapturePoint.CaptureProgressCurrent, targetCapturePoint.OwnerTeamCurrent, targetCapturePoint.CapturingTeamCurrent);
    }

    void TryBindTeamColorSource()
    {
        if (teamColorSource == null)
            teamColorSource = FindFirstObjectByType<TeamColorApplicator>(FindObjectsInactive.Exclude);
    }

    void HandleCaptureProgressChanged(CapturePoint source, float progress, Team owner, Team capturing)
    {
        if (targetCapturePoint == null)
            targetCapturePoint = source;

        if (source != targetCapturePoint)
            return;

        SetCaptureState(progress, owner, capturing);
    }

    void SetCaptureState(float progress, Team owner, Team capturing)
    {
        SetProgress(progress);

        Team displayTeam = capturing != Team.None ? capturing : owner;
        SetColor(ResolveTeamColor(displayTeam));
    }

    void SetColor(Color color)
    {
        if (fillImage == null)
            return;

        fillImage.color = color;
    }

    Color ResolveTeamColor(Team team)
    {
        if (team == Team.None)
            return neutralColor;

        if (teamColorSource != null && teamColorSource.TryGetTeamColor(team, out Color configuredColor))
            return configuredColor;

        switch (team)
        {
            case Team.Samurai:
                return samuraiFallbackColor;
            case Team.Knights:
                return knightsFallbackColor;
            case Team.Pirates:
                return piratesFallbackColor;
            default:
                return neutralColor;
        }
    }

    public void SetProgress(float percent)
    {
        percent = Mathf.Clamp01(percent);

        if (fill == null)
            return;

        fill.sizeDelta = new Vector2(
            maxWidth * percent,
            fill.sizeDelta.y
        );
    }
}