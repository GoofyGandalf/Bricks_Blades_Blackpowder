using UnityEngine;

public class WaterLevelProvider : MonoBehaviour
{
    [SerializeField] Transform waterLevelMarker;
    [SerializeField] float levelOffset = 0f;
    [SerializeField] bool autoFindMarkerByName = true;
    [SerializeField] string markerObjectName = "waterlevle";

    public static WaterLevelProvider Instance { get; private set; }

    static Transform cachedAutoMarker;
    static bool autoMarkerSearched;

    void Awake()
    {
        if (Instance != null && Instance != this)
            Debug.LogWarning("[WaterLevelProvider] Multiple instances found. Using the newest one.", this);

        Instance = this;

        if (waterLevelMarker == null && autoFindMarkerByName)
            waterLevelMarker = TryFindMarker(markerObjectName);
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    public static bool TryGetWaterLevel(out float levelY)
    {
        if (Instance != null)
            return Instance.TryGetWaterLevelInternal(out levelY);

        return TryGetAutoMarkerLevel("waterlevle", 0f, out levelY);
    }

    bool TryGetWaterLevelInternal(out float levelY)
    {
        if (waterLevelMarker != null)
        {
            levelY = waterLevelMarker.position.y + levelOffset;
            return true;
        }

        if (!autoFindMarkerByName)
        {
            levelY = 0f;
            return false;
        }

        if (cachedAutoMarker == null)
            cachedAutoMarker = TryFindMarker(markerObjectName);

        if (cachedAutoMarker == null)
        {
            levelY = 0f;
            return false;
        }

        levelY = cachedAutoMarker.position.y + levelOffset;
        return true;
    }

    static bool TryGetAutoMarkerLevel(string markerName, float offset, out float levelY)
    {
        if (!autoMarkerSearched || cachedAutoMarker == null)
        {
            autoMarkerSearched = true;
            cachedAutoMarker = TryFindMarker(markerName);
        }

        if (cachedAutoMarker == null)
        {
            levelY = 0f;
            return false;
        }

        levelY = cachedAutoMarker.position.y + offset;
        return true;
    }

    static Transform TryFindMarker(string markerName)
    {
        if (string.IsNullOrWhiteSpace(markerName))
            markerName = "waterlevle";

        var go = GameObject.Find(markerName);
        if (!go)
            go = GameObject.Find("WaterLevel");

        return go ? go.transform : null;
    }
}
