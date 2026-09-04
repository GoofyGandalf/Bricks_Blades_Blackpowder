using Fusion;
using UnityEngine;
using BricksBladesBlackpowder.Teams;
using BricksBladesBlackpowder.NPC;
using System;

namespace BricksBladesBlackpowder.Match
{
    public class CapturePoint : NetworkBehaviour
    {
        public static event Action<CapturePoint, float, Team, Team> ProgressChanged;

        [SerializeField, Min(0.1f)] float captureRadius = 5f;
        [SerializeField, Min(0.1f)] float captureTime = 10f;
        [SerializeField, Range(0f, 1f)] float additionalMemberBonus = 0.2f;
        [SerializeField] LayerMask detectLayers = ~0;
        [SerializeField] bool useStructureBoundsArea = true;
        [SerializeField, Min(0f)] float structureBoundsPadding = 1.5f;
        [SerializeField, Min(0.1f)] float boundsRefreshInterval = 1f;
        [SerializeField] bool debugLogs = false;
        [SerializeField, Min(0.1f)] float debugLogInterval = 0.5f;

        [Networked] public Team OwnerTeam { get; set; }
        [Networked] public Team CapturingTeam { get; set; }
        [Networked] public float CaptureProgress { get; set; }

        // index = (int)Team value (1=Samurai, 2=Knights, 3=Pirates)
        private readonly int[] _teamCounts = new int[4];
        private float _nextDebugLogTime;
        private float _nextBoundsRefreshTime;
        private string _lastDebugSignature;
        private Team _lastLoggedOwner = Team.None;
        private bool _loggedFallbackMode;
        private bool _hasCaptureBounds;
        private Bounds _captureBounds;
        private Team _ownerTeamLocal = Team.None;
        private Team _capturingTeamLocal = Team.None;
        private float _captureProgressLocal;
        private Team _lastBroadcastOwner = Team.None;
        private Team _lastBroadcastCapturing = Team.None;
        private float _lastBroadcastProgress = -1f;

        bool IsNetworkedActive => Object != null && Runner != null;

        Team CurrentOwnerTeam
        {
            get => IsNetworkedActive ? OwnerTeam : _ownerTeamLocal;
            set
            {
                if (IsNetworkedActive)
                    OwnerTeam = value;
                else
                    _ownerTeamLocal = value;
            }
        }

        public Team OwnerTeamCurrent => CurrentOwnerTeam;
        public Team CapturingTeamCurrent => CurrentCapturingTeam;
        public float CaptureProgressCurrent => CurrentCaptureProgress;

        Team CurrentCapturingTeam
        {
            get => IsNetworkedActive ? CapturingTeam : _capturingTeamLocal;
            set
            {
                if (IsNetworkedActive)
                    CapturingTeam = value;
                else
                    _capturingTeamLocal = value;
            }
        }

        float CurrentCaptureProgress
        {
            get => IsNetworkedActive ? CaptureProgress : _captureProgressLocal;
            set
            {
                if (IsNetworkedActive)
                    CaptureProgress = value;
                else
                    _captureProgressLocal = value;
            }
        }

        void Update()
        {
            // If Fusion owns this object, FixedUpdateNetwork drives capture progression.
            if (Object != null && Runner != null)
                return;

            var bootstrap = AppRoot.Instance ? AppRoot.Instance.GetComponent<BBB_FusionBootstrap>() : null;
            var runner = bootstrap != null ? bootstrap.Runner : null;
            if (runner == null || !runner.IsRunning || !runner.IsServer)
                return;

            if (debugLogs && !_loggedFallbackMode)
            {
                _loggedFallbackMode = true;
                Debug.Log("[CapturePoint:" + name + "] Running in fallback host mode (scene object not registered as spawned Fusion NetworkObject).");
            }

            StepCapture(Time.deltaTime);
        }

        public override void FixedUpdateNetwork()
        {
            if (!Runner.IsServer)
                return;

            StepCapture(Runner.DeltaTime);
        }

        void StepCapture(float deltaTime)
        {
            if (useStructureBoundsArea && Time.time >= _nextBoundsRefreshTime)
            {
                RefreshCaptureBounds();
                _nextBoundsRefreshTime = Time.time + boundsRefreshInterval;
            }

            Team ownerBefore = CurrentOwnerTeam;
            Team capturingBefore = CurrentCapturingTeam;
            float progressBefore = CurrentCaptureProgress;

            // Count players/NPCs by team within capture radius
            for (int i = 0; i < _teamCounts.Length; i++)
                _teamCounts[i] = 0;

            int found = CountActorsInArea();

            // Determine how many distinct teams are present
            int teamsPresent = 0;
            Team presentTeam = Team.None;
            int presentCount = 0;
            for (int t = 1; t <= 3; t++)
            {
                if (_teamCounts[t] > 0)
                {
                    teamsPresent++;
                    presentTeam = (Team)t;
                    presentCount = _teamCounts[t];
                }
            }

            if (teamsPresent == 0)
            {
                // Nobody present: keep captured points owned until challenged.
                if (CurrentOwnerTeam != Team.None)
                {
                    CurrentCapturingTeam = CurrentOwnerTeam;
                    CurrentCaptureProgress = 1f;
                }
                else if (CurrentCaptureProgress > 0f)
                {
                    // If nobody has captured yet, decay partial neutral progress.
                    CurrentCaptureProgress -= (2f / captureTime) * deltaTime;
                    if (CurrentCaptureProgress <= 0f)
                    {
                        CurrentCaptureProgress = 0f;
                        CurrentCapturingTeam = Team.None;
                    }
                }
            }
            else if (teamsPresent > 1)
            {
                // Contested: pause capture progress
            }
            else
            {
                // Single team present
                if (CurrentCapturingTeam == Team.None || CurrentCapturingTeam == presentTeam)
                {
                    CurrentCapturingTeam = presentTeam;
                    int extraMembers = Mathf.Max(0, presentCount - 1);
                    float rate = (1f + extraMembers * additionalMemberBonus) / captureTime;
                    CurrentCaptureProgress += rate * deltaTime;
                    if (CurrentCaptureProgress >= 1f)
                    {
                        CurrentCaptureProgress = 1f;
                        CurrentOwnerTeam = CurrentCapturingTeam;
                    }
                }
                else
                {
                    // A different team is eroding progress from the previous capturer
                    CurrentCaptureProgress -= (2f / captureTime) * deltaTime;
                    if (CurrentCaptureProgress <= 0f)
                    {
                        CurrentCaptureProgress = 0f;
                        CurrentCapturingTeam = Team.None;
                    }
                }
            }

            LogDebugState(found, teamsPresent, presentTeam, presentCount, ownerBefore, capturingBefore, progressBefore);
            BroadcastProgressIfChanged();
        }

        void BroadcastProgressIfChanged(bool force = false)
        {
            Team owner = CurrentOwnerTeam;
            Team capturing = CurrentCapturingTeam;
            float progress = CurrentCaptureProgress;

            bool changed = force ||
                           owner != _lastBroadcastOwner ||
                           capturing != _lastBroadcastCapturing ||
                           Mathf.Abs(progress - _lastBroadcastProgress) > 0.0001f;

            if (!changed)
                return;

            _lastBroadcastOwner = owner;
            _lastBroadcastCapturing = capturing;
            _lastBroadcastProgress = progress;
            ProgressChanged?.Invoke(this, progress, owner, capturing);
        }

        void OnEnable()
        {
            BroadcastProgressIfChanged(true);
        }

        void LogDebugState(int found, int teamsPresent, Team presentTeam, int presentCount, Team ownerBefore, Team capturingBefore, float progressBefore)
        {
            if (!debugLogs)
                return;

            float now = Time.time;
            Team owner = CurrentOwnerTeam;
            Team capturing = CurrentCapturingTeam;
            float progress = CurrentCaptureProgress;
            int progressPct = Mathf.RoundToInt(progress * 100f);
            string phase;
            if (teamsPresent == 0)
                phase = "Empty/Decaying";
            else if (teamsPresent > 1)
                phase = "Contested";
            else if (capturing == Team.None)
                phase = "Resetting";
            else if (capturing == presentTeam)
                phase = "Capturing";
            else
                phase = "Eroding";

            string signature =
                teamsPresent + "|" +
                presentTeam + "|" +
                presentCount + "|" +
                owner + "|" +
                capturing + "|" +
                progressPct + "|" +
                _teamCounts[(int)Team.Samurai] + "|" +
                _teamCounts[(int)Team.Knights] + "|" +
                _teamCounts[(int)Team.Pirates] + "|" +
                phase;

            bool ownerChanged = owner != _lastLoggedOwner;
            bool stateChanged = signature != _lastDebugSignature;
            bool intervalElapsed = now >= _nextDebugLogTime;

            if (!ownerChanged && !stateChanged && !intervalElapsed)
                return;

            _nextDebugLogTime = now + debugLogInterval;
            _lastDebugSignature = signature;
            _lastLoggedOwner = owner;

            string change = ownerChanged
                ? " OWNER_CHANGED"
                : (capturingBefore != capturing || Mathf.Abs(progressBefore - progress) > 0.0001f || ownerBefore != owner)
                    ? " STATE_STEP"
                    : "";

            Debug.Log(
                "[CapturePoint:" + name + "]" + change +
                " phase=" + phase +
                " owner=" + owner +
                " capturing=" + capturing +
                " progress=" + progressPct + "%" +
                " actors=" + found +
                " present=" + teamsPresent +
                " dominant=" + presentTeam + "(" + presentCount + ")" +
                " counts[S=" + _teamCounts[(int)Team.Samurai] +
                ",K=" + _teamCounts[(int)Team.Knights] +
                ",P=" + _teamCounts[(int)Team.Pirates] + "]");
        }

        int CountActorsInArea()
        {
            int found = 0;

            var players = FindObjectsByType<PlayerHealth>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < players.Length; i++)
            {
                var player = players[i];
                if (player == null || player.IsDead)
                    continue;

                if (!IsLayerAllowed(player.gameObject.layer))
                    continue;

                if (!IsInsideCaptureArea(player.transform.position))
                    continue;

                Team team = player.GetTeam();
                if (team == Team.None)
                    continue;

                _teamCounts[(int)team]++;
                found++;
            }

            var npcs = FindObjectsByType<NPCHealth>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < npcs.Length; i++)
            {
                var npc = npcs[i];
                if (npc == null || npc.IsDead)
                    continue;

                if (!IsLayerAllowed(npc.gameObject.layer))
                    continue;

                if (!IsInsideCaptureArea(npc.transform.position))
                    continue;

                Team team = npc.GetTeam();
                if (team == Team.None)
                    continue;

                _teamCounts[(int)team]++;
                found++;
            }

            return found;
        }

        bool IsLayerAllowed(int layer)
        {
            return (detectLayers.value & (1 << layer)) != 0;
        }

        bool IsInsideCaptureArea(Vector3 position)
        {
            if (useStructureBoundsArea && _hasCaptureBounds)
                return _captureBounds.Contains(position);

            float radiusSq = captureRadius * captureRadius;
            return (position - transform.position).sqrMagnitude <= radiusSq;
        }

        void RefreshCaptureBounds()
        {
            if (!useStructureBoundsArea)
            {
                _hasCaptureBounds = false;
                return;
            }

            var colliders = GetComponentsInChildren<Collider>(true);
            bool hasBounds = false;
            Bounds bounds = default;

            for (int i = 0; i < colliders.Length; i++)
            {
                var col = colliders[i];
                if (col == null || !col.enabled)
                    continue;

                if (!hasBounds)
                {
                    bounds = col.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(col.bounds);
                }
            }

            _hasCaptureBounds = hasBounds;
            if (!_hasCaptureBounds)
                return;

            bounds.Expand(structureBoundsPadding * 2f);
            _captureBounds = bounds;
        }
    }
}
