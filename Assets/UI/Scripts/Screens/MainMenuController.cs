using Fusion;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using TMPro;

public class MainMenuController : MonoBehaviour
{
    [Header("Host Panel")]
    [SerializeField] GameObject hostPanel;
    [SerializeField] TMP_InputField hostRoomNameInput;
    [SerializeField] TMP_InputField hostPasswordInput;
    [SerializeField] Button hostConfirmButton;

    [Header("Join Panel (Session Browser)")]
    [SerializeField] GameObject joinPanel;
    [SerializeField] Transform sessionListContent;
    [SerializeField] GameObject sessionEntryPrefab;
    [SerializeField] Button joinRefreshButton;

    [Header("Password Prompt (optional — wire later)")]
    [SerializeField] GameObject passwordPanel;
    [SerializeField] TMP_InputField passwordInput;
    [SerializeField] Button passwordConfirmButton;
    [SerializeField] Button passwordCancelButton;

    [Header("Status (optional)")]
    [SerializeField] TMP_Text statusText;

    [Header("Options")]
    [SerializeField] Button optionsButton;

    [Header("Scene Build Indices")]
    [SerializeField] int lobbySceneBuildIndex = 2;

    bool busy;
    BBB_FusionBootstrap bootstrap;
    string pendingJoinSession;
    List<SessionInfo> cachedSessions = new();

    void Start()
    {
        bootstrap = AppRoot.Instance.GetComponent<BBB_FusionBootstrap>();

        // Host panel
        if (hostConfirmButton)
            hostConfirmButton.onClick.AddListener(ConfirmHost);

        // Join panel
        if (joinRefreshButton)
            joinRefreshButton.onClick.AddListener(RefreshSessionList);

        // Password prompt
        if (passwordConfirmButton)
            passwordConfirmButton.onClick.AddListener(ConfirmPasswordJoin);

        if (passwordCancelButton)
            passwordCancelButton.onClick.AddListener(HidePasswordPanel);

        // Options button
        if (optionsButton)
            optionsButton.onClick.AddListener(OpenOptions);

        bootstrap.OnSessionListUpdated += OnSessionsReceived;

        // Start browsing immediately
        RefreshSessionList();
    }

    void OnDestroy()
    {
        if (bootstrap != null)
            bootstrap.OnSessionListUpdated -= OnSessionsReceived;
    }

    // ─────────────────────────────────────
    // HOST FLOW
    // ─────────────────────────────────────

    async void ConfirmHost()
    {
        if (busy) return;

        string roomName =
            hostRoomNameInput
            ? hostRoomNameInput.text.Trim()
            : "BBB_Room";

        string password =
            hostPasswordInput
            ? hostPasswordInput.text
            : "";

        if (string.IsNullOrEmpty(roomName))
        {
            SetStatus("Room name cannot be empty.");
            return;
        }

        SetBusy(true);
        SetStatus("Creating room...");

        try
        {
            await bootstrap.StopSessionBrowser();

            var ok = await bootstrap.HostAsync(roomName, password);

            if (!ok)
            {
                SetStatus("Failed to create room.");
                await bootstrap.ShutdownAsync();
                return;
            }

            SetStatus("");
            bootstrap.LoadFusionScene(lobbySceneBuildIndex);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[MainMenu] Host error: {ex.Message}");
            SetStatus("Error creating room.");
            await SafeCleanup();
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ─────────────────────────────────────
    // JOIN FLOW
    // ─────────────────────────────────────

    async void RefreshSessionList()
    {
        SetStatus("Searching for rooms...");
        ClearSessionList();

        await bootstrap.StartSessionBrowser();
    }

    void OnSessionsReceived(List<SessionInfo> sessions)
    {
        cachedSessions.Clear();
        cachedSessions.AddRange(sessions);

        ClearSessionList();

        if (sessions.Count == 0)
        {
            SetStatus("No rooms found.");
            return;
        }

        SetStatus($"{sessions.Count} room(s) found.");

        foreach (var session in sessions)
        {
            if (!session.IsOpen || !session.IsValid)
                continue;

            CreateSessionEntry(session);
        }
    }

    void CreateSessionEntry(SessionInfo session)
    {
        if (sessionEntryPrefab == null || sessionListContent == null)
            return;

        var go =
            Instantiate(sessionEntryPrefab, sessionListContent);

        go.SetActive(true);

        var nameText =
            go.transform.Find("RoomName")
            ?.GetComponent<TMP_Text>();

        var playerText =
            go.transform.Find("PlayerCount")
            ?.GetComponent<TMP_Text>();

        var lockIcon =
            go.transform.Find("LockIcon")
            ?.gameObject;

        var joinBtn =
            go.GetComponentInChildren<Button>();

        bool hasPassword =
            BBB_FusionBootstrap.SessionHasPassword(session);

        if (nameText)
            nameText.text = session.Name;

        if (playerText)
            playerText.text =
                $"{session.PlayerCount}/{session.MaxPlayers}";

        if (lockIcon)
            lockIcon.SetActive(hasPassword);

        if (joinBtn)
        {
            string sessionName = session.Name;
            bool needsPassword = hasPassword;
            SessionInfo captured = session;

            joinBtn.onClick.AddListener(() =>
            {
                if (needsPassword && passwordPanel != null)
                    ShowPasswordPrompt(captured);
                else
                    JoinSession(sessionName);
            });
        }
    }

    void ClearSessionList()
    {
        if (sessionListContent == null)
            return;

        for (int i = sessionListContent.childCount - 1; i >= 0; i--)
        {
            Destroy(sessionListContent.GetChild(i).gameObject);
        }
    }

    // ─────────────────────────────────────
    // PASSWORD FLOW
    // ─────────────────────────────────────

    void ShowPasswordPrompt(SessionInfo session)
    {
        pendingJoinSession = session.Name;

        if (passwordPanel)
            passwordPanel.SetActive(true);

        if (passwordInput)
            passwordInput.text = "";
    }

    void HidePasswordPanel()
    {
        if (passwordPanel)
            passwordPanel.SetActive(false);

        pendingJoinSession = null;
    }

    void ConfirmPasswordJoin()
    {
        string enteredPassword =
            passwordInput
            ? passwordInput.text
            : "";

        bool found = false;

        foreach (var s in cachedSessions)
        {
            if (s.Name == pendingJoinSession)
            {
                found = true;

                if (!BBB_FusionBootstrap.CheckPassword(s, enteredPassword))
                {
                    SetStatus("Incorrect password.");
                    return;
                }

                break;
            }
        }

        if (!found)
        {
            SetStatus("Session no longer available.");
            return;
        }

        HidePasswordPanel();
        JoinSession(pendingJoinSession);
    }

    async void JoinSession(string sessionName)
    {
        if (busy) return;

        SetBusy(true);
        SetStatus("Joining room...");

        try
        {
            await bootstrap.StopSessionBrowser();

            var ok =
                await bootstrap.JoinAsync(sessionName);

            if (!ok)
            {
                SetStatus("Failed to join room.");
                await bootstrap.ShutdownAsync();
                return;
            }

            SetStatus("");

            // Fusion auto-syncs clients
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[MainMenu] Join error: {ex.Message}");
            SetStatus("Error joining room.");

            await SafeCleanup();
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ─────────────────────────────────────
    // OPTIONS BUTTON
    // ─────────────────────────────────────

    public void OpenOptions()
    {
        SceneManager.LoadScene("Options");
    }

    // ─────────────────────────────────────
    // HELPERS
    // ─────────────────────────────────────

    void SetBusy(bool state)
    {
        busy = state;

        if (hostConfirmButton)
            hostConfirmButton.interactable = !state;

        if (joinRefreshButton)
            joinRefreshButton.interactable = !state;
    }

    void SetStatus(string msg)
    {
        if (statusText)
            statusText.text = msg;
        else
            Debug.Log($"[MainMenu] {msg}");
    }

    async System.Threading.Tasks.Task SafeCleanup()
    {
        try
        {
            if (bootstrap != null)
                await bootstrap.ShutdownAsync();
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[MainMenu] SafeCleanup error: {ex.Message}");
        }
    }
}