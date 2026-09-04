using System.Collections.Generic;
using System.Threading.Tasks;
using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;
using BricksBladesBlackpowder.Teams;

/// <summary>
/// Host-authoritative team-base elimination controller.
///
/// Each tracked base is normalized by its own total brick count:
///   destructionPercent = (1 - alive/total) * 100
/// so different-sized bases are evaluated fairly.
///
/// A team is eliminated when its aggregated destruction percent reaches
/// eliminationThresholdPercent. Match winner is the last team not eliminated.
/// </summary>
public class BaseDestructionWinController : NetworkBehaviour
{
    [System.Serializable]
    public class TrackedBase
    {
        public Team team = Team.None;
        public BrickStructure structure;
    }

    [Header("Teams / Bases")]
    [Tooltip("Populate manually. If empty, bases are auto-discovered from BrickStructure objects using TeamIdentity or object names.")]
    public List<TrackedBase> trackedBases = new List<TrackedBase>();

    [Header("Rules")]
    [Range(1f, 99f)]
    [Tooltip("Team is eliminated when destruction reaches this percent.")]
    public float eliminationThresholdPercent = 80f;
    [Tooltip("How often to recompute destruction state.")]
    public float evaluateIntervalSeconds = 0.5f;

    [Header("Debug")]
    public bool logStateChanges = true;

    [Header("Win Statement")]
    [Tooltip("Shows a simple on-screen winner banner when a team wins.")]
    public bool showWinStatement = true;
    [Range(24, 120)] public int winStatementFontSize = 64;
    public Color winStatementTextColor = Color.white;
    public Color winStatementShadowColor = Color.black;

    [Networked, OnChangedRender(nameof(OnNetworkStateChanged))]
    int WinnerTeamRaw { get; set; }

    [Networked, OnChangedRender(nameof(OnNetworkStateChanged))]
    NetworkBool MatchEnded { get; set; }

    bool _fusionReady;
    float _nextEvalAt;

    // Fallback local fields when Spawned() never fires (scene object not registered by Fusion)
    Team _localWinnerTeam = Team.None;
    bool _localMatchEnded;

    readonly Dictionary<Team, int> _totalByTeam = new Dictionary<Team, int>(8);
    readonly Dictionary<Team, int> _aliveByTeam = new Dictionary<Team, int>(8);
    readonly List<Team> _teamsInMatch = new List<Team>(8);
    GUIStyle _winStyle;
    GUIStyle _winShadowStyle;
    bool _returnToMenuStarted;

    public Team WinnerTeam => _fusionReady ? (Team)WinnerTeamRaw : _localWinnerTeam;
    public bool IsMatchEnded => _fusionReady ? MatchEnded : _localMatchEnded;

    void Awake()
    {
        if (trackedBases == null)
            trackedBases = new List<TrackedBase>();

        if (trackedBases.Count == 0)
            AutoDiscoverBases();
    }

    public override void Spawned()
    {
        _fusionReady = true;

        if (trackedBases == null)
            trackedBases = new List<TrackedBase>();

        if (trackedBases.Count == 0)
            AutoDiscoverBases();

        if (Object.HasStateAuthority)
        {
            WinnerTeamRaw = (int)Team.None;
            MatchEnded = false;
        }

        _nextEvalAt = Time.time + evaluateIntervalSeconds;
        EvaluateAndApply(authoritative: Object.HasStateAuthority);
    }

    void Update()
    {
        if (Time.time < _nextEvalAt)
            return;

        _nextEvalAt = Time.time + Mathf.Max(0.05f, evaluateIntervalSeconds);

        if (_fusionReady)
        {
            if (Object.HasStateAuthority)
                EvaluateAndApply(authoritative: true);
            return;
        }

        // Local-only fallback mode
        EvaluateAndApply(authoritative: false);
    }

    public float GetTeamDestructionPercent(Team team)
    {
        BuildTeamStats();
        if (!_totalByTeam.TryGetValue(team, out int total) || total <= 0)
            return 0f;

        int alive = _aliveByTeam.TryGetValue(team, out int a) ? a : 0;
        return (1f - (alive / (float)total)) * 100f;
    }

    public float GetTeamRemainingHealthPercent(Team team)
    {
        return Mathf.Clamp(100f - GetTeamDestructionPercent(team), 0f, 100f);
    }

    void EvaluateAndApply(bool authoritative)
    {
        BuildTeamStats();

        int contenders = 0;
        Team lastStanding = Team.None;

        for (int i = 0; i < _teamsInMatch.Count; i++)
        {
            Team t = _teamsInMatch[i];
            float destroyedPct = GetTeamDestructionPercent_NoRebuild(t);
            bool eliminated = destroyedPct >= eliminationThresholdPercent;

            if (!eliminated)
            {
                contenders++;
                lastStanding = t;
            }
        }

        bool hasWinner = _teamsInMatch.Count >= 2 && contenders <= 1;
        Team winner = hasWinner ? lastStanding : Team.None;

        if (authoritative)
        {
            bool changed = (MatchEnded != hasWinner) || (WinnerTeamRaw != (int)winner);
            MatchEnded = hasWinner;
            WinnerTeamRaw = (int)winner;

            if (changed && hasWinner)
                RPC_EndMatchAndReturnToMenu((int)winner);

            if (changed && logStateChanges)
            {
                if (hasWinner)
                    Debug.Log($"[BaseDestructionWinController] Winner: {winner} (threshold={eliminationThresholdPercent:F1}%)");
                else
                    Debug.Log("[BaseDestructionWinController] No winner yet.");
            }
        }
        else
        {
            bool changed = (_localMatchEnded != hasWinner) || (_localWinnerTeam != winner);
            _localMatchEnded = hasWinner;
            _localWinnerTeam = winner;

            if (changed && hasWinner)
                BeginReturnToMenuFlow();

            if (changed && logStateChanges)
            {
                if (hasWinner)
                    Debug.Log($"[BaseDestructionWinController/Fallback] Winner: {winner}");
                else
                    Debug.Log("[BaseDestructionWinController/Fallback] No winner yet.");
            }
        }
    }

    float GetTeamDestructionPercent_NoRebuild(Team team)
    {
        if (!_totalByTeam.TryGetValue(team, out int total) || total <= 0)
            return 0f;

        int alive = _aliveByTeam.TryGetValue(team, out int a) ? a : 0;
        return (1f - (alive / (float)total)) * 100f;
    }

    void BuildTeamStats()
    {
        _totalByTeam.Clear();
        _aliveByTeam.Clear();
        _teamsInMatch.Clear();

        for (int i = trackedBases.Count - 1; i >= 0; i--)
        {
            var entry = trackedBases[i];
            if (entry == null || entry.structure == null)
                continue;

            entry.structure.EnsureInitialized();
            if (entry.structure.aliveFlags == null || entry.structure.model == null || !entry.structure.model.HasGpuData)
                continue;

            Team team = entry.team;
            if (team == Team.None)
                team = InferTeam(entry.structure.gameObject);

            if (team == Team.None)
                continue;

            int total = entry.structure.model.sortedBricks.Length;
            int alive = entry.structure.AliveCount;

            if (!_totalByTeam.ContainsKey(team))
            {
                _totalByTeam.Add(team, 0);
                _aliveByTeam.Add(team, 0);
                _teamsInMatch.Add(team);
            }

            _totalByTeam[team] += Mathf.Max(0, total);
            _aliveByTeam[team] += Mathf.Clamp(alive, 0, total);
        }
    }

    void OnNetworkStateChanged()
    {
        if (!logStateChanges)
            return;

        if (MatchEnded)
            Debug.Log($"[BaseDestructionWinController] Match ended. Winner: {(Team)WinnerTeamRaw}");
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    void RPC_EndMatchAndReturnToMenu(int winnerTeamRaw)
    {
        if (logStateChanges)
            Debug.Log($"[BaseDestructionWinController] Ending match. Winner={(Team)winnerTeamRaw}. Returning to MainMenu.");

        BeginReturnToMenuFlow();
    }

    void BeginReturnToMenuFlow()
    {
        if (_returnToMenuStarted)
            return;

        _returnToMenuStarted = true;
        StartCoroutine(ReturnToMainMenuRoutine());
    }

    System.Collections.IEnumerator ReturnToMainMenuRoutine()
    {
        // Allow one frame for any final replicated state/UI updates.
        yield return null;

        Task shutdownTask = null;

        var bootstrap = AppRoot.Instance != null
            ? AppRoot.Instance.GetComponent<BBB_FusionBootstrap>()
            : FindFirstObjectByType<BBB_FusionBootstrap>();

        if (bootstrap != null)
        {
            shutdownTask = bootstrap.ShutdownAsync();
        }
        else
        {
            var runner = FindFirstObjectByType<NetworkRunner>();
            if (runner != null && runner.IsRunning)
                shutdownTask = runner.Shutdown();
        }

        if (shutdownTask != null)
        {
            while (!shutdownTask.IsCompleted)
                yield return null;

            if (shutdownTask.IsFaulted && logStateChanges)
                Debug.LogWarning($"[BaseDestructionWinController] Runner shutdown reported an error: {shutdownTask.Exception}");
        }

        SceneManager.LoadScene("MainMenu", LoadSceneMode.Single);
    }

    void AutoDiscoverBases()
    {
        trackedBases.Clear();

        var structures = FindObjectsByType<BrickStructure>(FindObjectsSortMode.None);
        for (int i = 0; i < structures.Length; i++)
        {
            BrickStructure s = structures[i];
            if (s == null)
                continue;

            Team team = InferTeam(s.gameObject);
            if (team == Team.None)
                continue;

            trackedBases.Add(new TrackedBase
            {
                team = team,
                structure = s
            });
        }

        if (logStateChanges)
            Debug.Log($"[BaseDestructionWinController] Auto-discovered {trackedBases.Count} team base(s).");
    }

    Team InferTeam(GameObject go)
    {
        if (go == null)
            return Team.None;

        var identity = go.GetComponentInParent<TeamIdentity>();
        if (identity != null && identity.team != Team.None)
            return identity.team;

        string n = go.name.ToLowerInvariant();
        if (n.Contains("pirate")) return Team.Pirates;
        if (n.Contains("samurai")) return Team.Samurai;
        if (n.Contains("castle") || n.Contains("knight")) return Team.Knights;

        return Team.None;
    }

    void OnGUI()
    {
        if (!showWinStatement) return;
        if (!IsMatchEnded) return;

        Team winner = WinnerTeam;
        if (winner == Team.None) return;

        EnsureWinStyles();

        string msg = winner + " Wins!";
        var rect = new Rect(0f, Screen.height * 0.12f, Screen.width, 80f);
        var shadowRect = new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height);

        GUI.Label(shadowRect, msg, _winShadowStyle);
        GUI.Label(rect, msg, _winStyle);
    }

    void EnsureWinStyles()
    {
        if (_winStyle == null)
        {
            _winStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = winStatementFontSize,
                fontStyle = FontStyle.Bold
            };
        }

        if (_winShadowStyle == null)
        {
            _winShadowStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = winStatementFontSize,
                fontStyle = FontStyle.Bold
            };
        }

        _winStyle.fontSize = winStatementFontSize;
        _winShadowStyle.fontSize = winStatementFontSize;
        _winStyle.normal.textColor = winStatementTextColor;
        _winShadowStyle.normal.textColor = winStatementShadowColor;
    }
}
