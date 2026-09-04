using Fusion;
using UnityEngine;
using System.Collections;
using TMPro;

public class DynamiteModelToggle : NetworkBehaviour
{
    private GameObject dynamite;
    private GameObject targetObject;

    private Canvas billboardCanvas;
    private GameObject normalPromptTMP;
    private GameObject resetPromptTMP;
    private TMP_Text timerTMP;
    private TMP_Text defusingTMP;

    private Camera mainCam;

    private Vector3 originalPosition;
    private Quaternion originalRotation;

    private bool isResetting = false;
    private bool isHoldingForceReset = false;

    private float holdTimer = 0f;
    private float dotTimer = 0f;
    private int dotCount = 0;

    private bool forceResetTriggered = false;
    private float resetTimeRemaining = 15f;

    private Coroutine resetCoroutine;

    private bool lastCanvasState = false;
    private bool lastNormalPromptState = false;
    private bool lastResetPromptState = false;
    private bool lastTimerState = false;
    private bool lastDefusingState = false;

    private int lastTimerValue = -1;
    private int lastDotCount = -1;

    [SerializeField] private float maxDistance = 5f;
    [SerializeField] private GameObject playerDynamitePrefab;
    

    void Start()
    {
        dynamite = transform.Find("Root/Hips/Torso/LeftArm/LeftHand/Dynamite")?.gameObject;
        targetObject = GameObject.Find("Player_dynamite");

        mainCam = Camera.main;

        if (targetObject != null)
        {
            originalPosition = targetObject.transform.position;
            originalRotation = targetObject.transform.rotation;
            CacheBillboardReferences();
        }
    }

    void Update()
    {
        if (!Object.HasInputAuthority) return;

        if (!isResetting && Input.GetKeyDown(KeyCode.E))
        {
            if (targetObject != null &&
                Vector3.Distance(transform.position, targetObject.transform.position) < maxDistance)
            {
                if (dynamite != null)
                    dynamite.SetActive(true);

                Destroy(targetObject);
                targetObject = null;

                ClearCachedReferences();
            }
            else if (dynamite != null && dynamite.activeSelf)
            {
                dynamite.SetActive(false);

                if (playerDynamitePrefab != null)
                {
                    Vector3 spawnPos =
                        transform.position +
                        transform.forward * 1.5f +
                        Vector3.up * 0.5f;

                    targetObject = Instantiate(
                        playerDynamitePrefab,
                        spawnPos,
                        Quaternion.identity
                    );

                    targetObject.name = "Player_dynamite";
                    targetObject.SetActive(true);

                    CacheBillboardReferences();
                }
            }
        }

        if (!isResetting && Input.GetKeyDown(KeyCode.F))
        {
            if (dynamite != null && dynamite.activeSelf)
                return;

            resetCoroutine = StartCoroutine(ReturnDynamiteAfterDelay());
        }

        HandleForceResetHold();
        UpdateBillboardUI();
    }

    private void HandleForceResetHold()
    {
        if (isResetting &&
            targetObject != null &&
            Vector3.Distance(transform.position, targetObject.transform.position) < maxDistance)
        {
            if (Input.GetKey(KeyCode.F))
            {
                isHoldingForceReset = true;

                holdTimer += Time.deltaTime;
                dotTimer += Time.deltaTime;

                if (dotTimer >= 1f)
                {
                    dotTimer = 0f;
                    dotCount++;

                    if (dotCount > 5)
                        dotCount = 0;
                }

                if (holdTimer >= 5f && !forceResetTriggered)
                {
                    forceResetTriggered = true;
                    ForceResetPosition();
                }
            }
            else
            {
                ResetHoldState();
            }
        }
        else
        {
            ResetHoldState();
        }
    }

    private void ResetHoldState()
    {
        isHoldingForceReset = false;
        holdTimer = 0f;
        dotTimer = 0f;
        dotCount = 0;
    }

    private IEnumerator ReturnDynamiteAfterDelay()
    {
        isResetting = true;
        resetTimeRemaining = 15f;
        lastTimerValue = -1;

        while (resetTimeRemaining > 0f)
        {
            resetTimeRemaining -= Time.deltaTime;
            yield return null;
        }

        ForceResetPosition();
        resetCoroutine = null;
    }

    private void ForceResetPosition()
    {
        if (resetCoroutine != null)
        {
            StopCoroutine(resetCoroutine);
            resetCoroutine = null;
        }

        if (targetObject != null)
        {
            targetObject.transform.position = originalPosition;
            targetObject.transform.rotation = originalRotation;
            targetObject.SetActive(true);
        }

        if (dynamite != null)
            dynamite.SetActive(false);

        holdTimer = 0f;
        dotTimer = 0f;
        dotCount = 0;

        forceResetTriggered = false;
        isHoldingForceReset = false;
        isResetting = false;
        resetTimeRemaining = 15f;

        lastTimerValue = -1;
        lastDotCount = -1;
    }

    private void CacheBillboardReferences()
    {
        ClearCachedReferences();

        if (targetObject == null) return;

        billboardCanvas = targetObject.GetComponentInChildren<Canvas>(true);

        TMP_Text[] texts = targetObject.GetComponentsInChildren<TMP_Text>(true);

        foreach (TMP_Text text in texts)
        {
            if (text.gameObject.name == "Equip_text")
                normalPromptTMP = text.gameObject;
            else if (text.gameObject.name == "Defuse_text")
                resetPromptTMP = text.gameObject;
            else if (text.gameObject.name == "Timer_text")
                timerTMP = text;
            else if (text.gameObject.name == "Defusing_text")
                defusingTMP = text;
        }

        if (mainCam == null)
            mainCam = Camera.main;

        if (mainCam == null)
            mainCam = FindObjectOfType<Camera>();

        if (billboardCanvas != null)
        {
            billboardCanvas.renderMode = RenderMode.WorldSpace;

            if (mainCam != null)
                billboardCanvas.worldCamera = mainCam;
        }
    }

    private void ClearCachedReferences()
    {
        billboardCanvas = null;
        normalPromptTMP = null;
        resetPromptTMP = null;
        timerTMP = null;
        defusingTMP = null;

        lastCanvasState = false;
        lastNormalPromptState = false;
        lastResetPromptState = false;
        lastTimerState = false;
        lastDefusingState = false;

        lastTimerValue = -1;
        lastDotCount = -1;
    }

    private void UpdateBillboardUI()
    {
        if (targetObject == null) return;

        if (billboardCanvas == null)
            CacheBillboardReferences();

        if (billboardCanvas == null) return;

        float distance =
            Vector3.Distance(transform.position, targetObject.transform.position);

        bool closeEnough = distance < maxDistance;
        bool showCanvas = closeEnough || isResetting;

        SetCanvasActive(showCanvas);

        SetObjectActive(
            normalPromptTMP,
            closeEnough && !isResetting,
            ref lastNormalPromptState
        );

        SetObjectActive(
            resetPromptTMP,
            isResetting && !isHoldingForceReset,
            ref lastResetPromptState
        );

        bool showTimer = isResetting;

        if (timerTMP != null)
        {
            SetObjectActive(
                timerTMP.gameObject,
                showTimer,
                ref lastTimerState
            );

            if (showTimer)
            {
                int currentTimerValue =
                    Mathf.CeilToInt(resetTimeRemaining);

                if (currentTimerValue != lastTimerValue)
                {
                    timerTMP.text = currentTimerValue.ToString();
                    lastTimerValue = currentTimerValue;
                }
            }
        }

        bool showDefusing =
            isResetting &&
            isHoldingForceReset &&
            closeEnough;

        if (defusingTMP != null)
        {
            SetObjectActive(
                defusingTMP.gameObject,
                showDefusing,
                ref lastDefusingState
            );

            if (showDefusing && dotCount != lastDotCount)
            {
                defusingTMP.text =
                    "DEFUSING" + new string('.', dotCount);

                lastDotCount = dotCount;
            }
        }

        if (!showCanvas) return;

        RectTransform rt = billboardCanvas.GetComponent<RectTransform>();

        rt.localScale = Vector3.one * 0.01f;
        rt.position = targetObject.transform.position + Vector3.up * 2f;

        if (mainCam == null)
            mainCam = Camera.main;

        if (mainCam != null)
            billboardCanvas.worldCamera = mainCam;
    }

    private void SetCanvasActive(bool active)
    {
        if (billboardCanvas == null) return;

        if (lastCanvasState == active) return;

        billboardCanvas.gameObject.SetActive(active);
        lastCanvasState = active;
    }

    private void SetObjectActive(GameObject obj, bool active, ref bool lastState)
    {
        if (obj == null) return;

        if (lastState == active) return;

        obj.SetActive(active);
        lastState = active;
    }
}