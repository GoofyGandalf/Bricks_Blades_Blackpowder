using System;
using Fusion;
using UnityEngine;
using UnityEngine.Rendering.Universal;

[RequireComponent(typeof(Camera))]
public class PlayerFollowCamera : MonoBehaviour
{
    [Header("Target")]
    public Transform target;
    public Vector3 targetOffset = new Vector3(0f, 1.5f, 0f);

    [Header("Orbit")]
    public float distance = 8f;
    public float minDistance = 3f;
    public float maxDistance = 12f;
    public float zoomSpeed = 6f;

    public float minPitch = -30f;
    public float maxPitch = 60f;

    [Header("Sensitivity")]
    public float mouseSensitivityX = 220f;
    const string SensitivityKey = "MouseSensitivityX";
    public float mouseSensitivityY = 180f;

    [Header("Smoothing")]
    public float rotationSmoothTime = 0.06f;
    public float positionSmoothTime = 0.05f;
    public float zoomSmoothTime = 0.08f;

    [Header("Collision")]
    public bool enableCollision = true;
    public LayerMask collisionMask = ~0;
    public float cameraRadius = 0.25f;
    public float collisionBuffer = 0.15f;
    public bool ignoreTargetInCollision = true;
    public float collisionMinHitDistance = 0.02f;

    [Header("Target Stabilization")]
    public bool stabilizeHorizontalSway = true;
    public float targetHorizontalSmoothTime = 0.07f;

    [Header("FOV")]
    public float baseFOV = 60f;
    public float maxFOV = 70f;
    public float fovSpeedInfluence = 6f;
    public float fovSmoothTime = 0.1f;

    [Header("Action Effects")]
    public bool enableActionEffects = true;
    public float effectMaxSpeed = 16f;
    public float bobAmplitude = 0.08f;
    public float bobSideAmplitude = 0.035f;
    public float bobFrequency = 10f;
    public float bobSmoothTime = 0.06f;
    public float strafeRollAmount = 5f;
    public float turnRollAmount = 2.5f;
    public float maxTurnRateForRoll = 220f;
    public float rollSmoothTime = 0.08f;
    public float accelerationLagAmount = 0.22f;
    public float maxAccelerationForLag = 28f;
    public float lagSmoothTime = 0.08f;
    public float accelerationFovKick = 3f;
    public float maxAccelerationForFovKick = 24f;
    public float fovKickSmoothTime = 0.08f;

    [Header("Controls")]
    public bool lockCursorOnStart = false;
    public KeyCode toggleCursorKey = KeyCode.LeftAlt;

    [Header("Anti-Aliasing")]
    public bool forceDisablePostProcessing = true;
    public bool forceEnableMsaa = true;

    [Header("Combat Head Follow")]
    public bool enableHeadFollowInCombat = true;
    [Range(0f, 1f)] public float headFollowWeight = 0.6f;
    public float headFollowMaxOffset = 1f;
    public float headFollowSmoothTime = 0.08f;


    float yaw, pitch;
    float currentYaw, currentPitch;
    float yawVel, pitchVel;

    float desiredDistance;
    float distanceVel;

    float fovVel;
    Vector3 posVel;
    Vector3 headOffsetVel;
    Vector3 smoothedHeadOffset;
    Vector3 stabilizedTargetPos;
    Vector3 stabilizedTargetVel;
    bool hasStabilizedTarget;
    float actionBobPhase;
    Vector3 actionBobCurrent;
    Vector3 actionBobVel;
    float actionRollCurrent;
    float actionRollVel;
    Vector3 actionLagCurrent;
    Vector3 actionLagVel;
    float actionFovKickCurrent;
    float actionFovKickVel;
    Vector3 lastVelocity;
    bool hasLastVelocity;
    float lastYawForEffects;
    bool hasLastYawForEffects;

    static readonly RaycastHit[] CollisionHitBuffer = new RaycastHit[32];

    Camera cam;
    Animator targetAnimator;
    Transform headTransform;
    bool headLookupComplete;

    // Cached speed source — avoids 3x TryGetComponent per frame
    enum SpeedSourceType { None, NCC, CC, RB }
    SpeedSourceType speedSourceType;
    NetworkCharacterController cachedNCC;
    CharacterController cachedCC;
    Rigidbody cachedRB;

    void Start()
    {
        mouseSensitivityX =
        PlayerPrefs.GetFloat(SensitivityKey, mouseSensitivityX);
        
        cam = GetComponent<Camera>();
        cam.fieldOfView = baseFOV;

        if (forceEnableMsaa)
            cam.allowMSAA = true;

        var urpCamData = cam.GetUniversalAdditionalCameraData();
        if (urpCamData != null)
        {
            if (forceDisablePostProcessing)
                urpCamData.renderPostProcessing = false;

            // With post disabled, URP camera AA modes are not used; MSAA handles edge smoothing.
            urpCamData.antialiasing = AntialiasingMode.None;
        }

        desiredDistance = distance;

        if (lockCursorOnStart) LockCursor(true);

        var e = transform.rotation.eulerAngles;
        yaw = currentYaw = e.y;
        pitch = currentPitch = NormalizeAngle(e.x);
        lastYawForEffects = currentYaw;
        hasLastYawForEffects = true;
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleCursorKey))
            LockCursor(Cursor.lockState != CursorLockMode.Locked);

        if (!target) return;

        if (Cursor.lockState == CursorLockMode.Locked)
        {
            yaw += Input.GetAxisRaw("Mouse X") * mouseSensitivityX * Time.unscaledDeltaTime;
            pitch -= Input.GetAxisRaw("Mouse Y") * mouseSensitivityY * Time.unscaledDeltaTime;
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        }

        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Abs(scroll) > 0.01f)
        {
            desiredDistance -= scroll * zoomSpeed;
            desiredDistance = Mathf.Clamp(desiredDistance, minDistance, maxDistance);
        }
    }

    void LateUpdate()
    {
        if (!target) return;

        currentYaw = Mathf.SmoothDampAngle(currentYaw, yaw, ref yawVel, rotationSmoothTime);
        currentPitch = Mathf.SmoothDampAngle(currentPitch, pitch, ref pitchVel, rotationSmoothTime);

        distance = Mathf.SmoothDamp(distance, desiredDistance, ref distanceVel, zoomSmoothTime);

        Quaternion rot = Quaternion.Euler(currentPitch, currentYaw, 0f);
        bool combatCamera = Cursor.lockState == CursorLockMode.Locked;
        Vector3 focusPoint = GetFocusPoint(combatCamera);

        Vector3 worldVelocity = GetTargetVelocity();
        float speed = worldVelocity.magnitude;

        if (enableActionEffects)
        {
            ApplyActionEffects(worldVelocity, speed, ref rot, ref focusPoint);
        }

        Vector3 desiredCamPos = focusPoint - rot * Vector3.forward * distance;

        if (enableCollision)
        {
            Vector3 dir = desiredCamPos - focusPoint;
            float len = dir.magnitude;
            if (len > 0.001f)
            {
                dir /= len;
                if (TryGetCameraCollisionDistance(focusPoint, dir, len, out float hitDistance))
                {
                    desiredCamPos = focusPoint + dir * Mathf.Max(hitDistance - collisionBuffer, minDistance);
                }
            }
        }

        transform.position = Vector3.SmoothDamp(transform.position, desiredCamPos, ref posVel, positionSmoothTime);
        transform.rotation = rot;

        float t = fovSpeedInfluence <= 0.001f ? 0f : Mathf.Clamp01(speed / fovSpeedInfluence);
        float targetFOV = Mathf.Lerp(baseFOV, maxFOV, t) + actionFovKickCurrent;
        cam.fieldOfView = Mathf.SmoothDamp(cam.fieldOfView, targetFOV, ref fovVel, fovSmoothTime);
    }

    public void SetTarget(Transform newTarget, float? snapYaw = null, float? snapPitch = null, bool snapPosition = false)
    {
        target = newTarget;
        if (!target) return;

        targetAnimator = null;
        headTransform = null;
        headLookupComplete = false;
        smoothedHeadOffset = Vector3.zero;
        headOffsetVel = Vector3.zero;
        hasStabilizedTarget = false;
        stabilizedTargetPos = Vector3.zero;
        stabilizedTargetVel = Vector3.zero;
        actionBobPhase = 0f;
        actionBobCurrent = Vector3.zero;
        actionBobVel = Vector3.zero;
        actionRollCurrent = 0f;
        actionRollVel = 0f;
        actionLagCurrent = Vector3.zero;
        actionLagVel = Vector3.zero;
        actionFovKickCurrent = 0f;
        actionFovKickVel = 0f;
        lastVelocity = Vector3.zero;
        hasLastVelocity = false;
        hasLastYawForEffects = false;

        // Cache speed source once instead of 3x TryGetComponent per frame
        cachedNCC = null; cachedCC = null; cachedRB = null;
        speedSourceType = SpeedSourceType.None;
        if (target.TryGetComponent(out cachedNCC))
            speedSourceType = SpeedSourceType.NCC;
        else if (target.TryGetComponent(out cachedCC))
            speedSourceType = SpeedSourceType.CC;
        else if (target.TryGetComponent(out cachedRB))
            speedSourceType = SpeedSourceType.RB;

        desiredDistance = Mathf.Clamp(distance, minDistance, maxDistance);

        if (snapYaw.HasValue) yaw = currentYaw = snapYaw.Value;
        if (snapPitch.HasValue) pitch = currentPitch = Mathf.Clamp(snapPitch.Value, minPitch, maxPitch);

        if (snapPosition)
        {
            var rot = Quaternion.Euler(currentPitch, currentYaw, 0f);
            var focusPoint = GetFocusPoint(Cursor.lockState == CursorLockMode.Locked);
            var desiredCamPos = focusPoint - rot * Vector3.forward * distance;
            transform.position = desiredCamPos;
            posVel = Vector3.zero;
        }
    }

    Vector3 GetTargetVelocity()
    {
        return speedSourceType switch
        {
            SpeedSourceType.NCC => cachedNCC ? cachedNCC.Velocity : Vector3.zero,
            SpeedSourceType.CC => cachedCC ? cachedCC.velocity : Vector3.zero,
            SpeedSourceType.RB => cachedRB ? cachedRB.linearVelocity : Vector3.zero,
            _ => Vector3.zero
        };
    }

    void ApplyActionEffects(Vector3 worldVelocity, float speed, ref Quaternion rot, ref Vector3 focusPoint)
    {
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        float speed01 = effectMaxSpeed <= 0.001f ? 0f : Mathf.Clamp01(speed / effectMaxSpeed);

        Vector3 localVelocity = target ? target.InverseTransformDirection(worldVelocity) : Vector3.zero;
        float strafe01 = effectMaxSpeed <= 0.001f ? 0f : Mathf.Clamp(localVelocity.x / effectMaxSpeed, -1f, 1f);

        actionBobPhase += dt * (bobFrequency * Mathf.Lerp(0.35f, 1f, speed01));
        Vector3 bobTarget = new Vector3(
            Mathf.Cos(actionBobPhase * 0.5f) * bobSideAmplitude * speed01,
            Mathf.Sin(actionBobPhase) * bobAmplitude * speed01,
            0f);
        actionBobCurrent = Vector3.SmoothDamp(actionBobCurrent, bobTarget, ref actionBobVel, bobSmoothTime);

        float yawDelta = hasLastYawForEffects ? Mathf.DeltaAngle(lastYawForEffects, currentYaw) : 0f;
        lastYawForEffects = currentYaw;
        hasLastYawForEffects = true;
        float yawRate = yawDelta / dt;
        float turn01 = maxTurnRateForRoll <= 0.001f ? 0f : Mathf.Clamp(yawRate / maxTurnRateForRoll, -1f, 1f);

        float rollTarget = (-strafe01 * strafeRollAmount) + (-turn01 * turnRollAmount);
        actionRollCurrent = Mathf.SmoothDamp(actionRollCurrent, rollTarget, ref actionRollVel, rollSmoothTime);

        float forwardAcceleration = 0f;
        if (hasLastVelocity)
        {
            float lastSpeed = lastVelocity.magnitude;
            forwardAcceleration = (speed - lastSpeed) / dt;
        }
        lastVelocity = worldVelocity;
        hasLastVelocity = true;

        float accel01ForLag = maxAccelerationForLag <= 0.001f
            ? 0f
            : Mathf.Clamp(forwardAcceleration / maxAccelerationForLag, -1f, 1f);
        Vector3 lagTarget = new Vector3(0f, 0f, -accel01ForLag * accelerationLagAmount);
        actionLagCurrent = Vector3.SmoothDamp(actionLagCurrent, lagTarget, ref actionLagVel, lagSmoothTime);

        float accelForKick = Mathf.Max(0f, forwardAcceleration);
        float kick01 = maxAccelerationForFovKick <= 0.001f
            ? 0f
            : Mathf.Clamp01(accelForKick / maxAccelerationForFovKick);
        float fovKickTarget = kick01 * accelerationFovKick;
        actionFovKickCurrent = Mathf.SmoothDamp(actionFovKickCurrent, fovKickTarget, ref actionFovKickVel, fovKickSmoothTime);

        Vector3 localOffset = actionBobCurrent + actionLagCurrent;
        focusPoint += rot * localOffset;
        rot = rot * Quaternion.Euler(0f, 0f, actionRollCurrent);
    }

    void LockCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    Vector3 GetFocusPoint(bool combatCamera)
    {
        Vector3 focusPoint = GetBaseFocusPoint();

        if (!combatCamera || !enableHeadFollowInCombat)
        {
            smoothedHeadOffset = Vector3.SmoothDamp(smoothedHeadOffset, Vector3.zero, ref headOffsetVel, headFollowSmoothTime);
            return focusPoint + smoothedHeadOffset;
        }

        if (!headLookupComplete)
        {
            if (!target.TryGetComponent(out targetAnimator))
            {
                targetAnimator = target.GetComponentInChildren<Animator>();
            }

            if (targetAnimator && targetAnimator.isHuman)
            {
                headTransform = targetAnimator.GetBoneTransform(HumanBodyBones.Head);
            }

            if (!headTransform)
            {
                Transform[] children = target.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < children.Length; i++)
                {
                    string name = children[i].name;
                    if (name == "Head" || name == "head")
                    {
                        headTransform = children[i];
                        break;
                    }
                }
            }

            headLookupComplete = true;
        }

        if (headTransform)
        {
            Vector3 rawOffset = headTransform.position - focusPoint;
            if (rawOffset.sqrMagnitude > 0.0001f)
            {
                if (headFollowMaxOffset > 0f)
                {
                    rawOffset = Vector3.ClampMagnitude(rawOffset, headFollowMaxOffset);
                }

                Vector3 weightedOffset = rawOffset * Mathf.Clamp01(headFollowWeight);
                smoothedHeadOffset = Vector3.SmoothDamp(smoothedHeadOffset, weightedOffset, ref headOffsetVel, headFollowSmoothTime);
                return focusPoint + smoothedHeadOffset;
            }
        }

        smoothedHeadOffset = Vector3.SmoothDamp(smoothedHeadOffset, Vector3.zero, ref headOffsetVel, headFollowSmoothTime);
        return focusPoint + smoothedHeadOffset;
    }

    Vector3 GetBaseFocusPoint()
    {
        Vector3 raw = target.position + targetOffset;

        if (!stabilizeHorizontalSway)
            return raw;

        if (!hasStabilizedTarget)
        {
            stabilizedTargetPos = raw;
            hasStabilizedTarget = true;
            return raw;
        }

        Vector3 targetXZ = new Vector3(raw.x, 0f, raw.z);
        Vector3 stabilizedXZ = new Vector3(stabilizedTargetPos.x, 0f, stabilizedTargetPos.z);
        Vector3 newXZ = Vector3.SmoothDamp(
            stabilizedXZ,
            targetXZ,
            ref stabilizedTargetVel,
            targetHorizontalSmoothTime);

        stabilizedTargetPos = new Vector3(newXZ.x, raw.y, newXZ.z);
        return stabilizedTargetPos;
    }

    bool TryGetCameraCollisionDistance(Vector3 origin, Vector3 dir, float len, out float hitDistance)
    {
        hitDistance = 0f;

        int hitCount = Physics.SphereCastNonAlloc(
            origin,
            cameraRadius,
            dir,
            CollisionHitBuffer,
            len,
            collisionMask,
            QueryTriggerInteraction.Ignore);

        if (hitCount <= 0)
            return false;

        bool found = false;
        float closest = float.MaxValue;

        for (int i = 0; i < hitCount; i++)
        {
            var hit = CollisionHitBuffer[i];
            if (hit.distance < collisionMinHitDistance)
                continue;

            if (ignoreTargetInCollision && target != null && hit.transform != null)
            {
                if (hit.transform == target || hit.transform.IsChildOf(target))
                    continue;
            }

            if (hit.distance < closest)
            {
                closest = hit.distance;
                found = true;
            }
        }

        if (!found)
            return false;

        hitDistance = closest;
        return true;
    }

    
    static float NormalizeAngle(float a)
    {
        while (a > 180f) a -= 360f;
        while (a < -180f) a += 360f;
        return a;
    }

}
