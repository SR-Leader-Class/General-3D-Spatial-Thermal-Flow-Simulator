using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public class CleanroomSensor : MonoBehaviour
{
    [Header("Sensor Info")]
    public string sensorId = "SENSOR_001";
    public string zoneId = "ISO5_A";
    public string displayName = "Particle Sensor 001";

    [Header("Current Data")]
    public CleanroomSensorData currentData = new CleanroomSensorData();
    public float staleThresholdSeconds = 10f;

    [Header("Trend History")]
    public int maxTrendSamples = 180;
    public float trendWindowSeconds = 300f;

    [Header("Visual")]
    public Renderer indicatorRenderer;
    public Color normalColor = Color.green;
    public Color warningColor = Color.yellow;
    public Color alarmColor = new Color(1f, 0.35f, 0f);
    public Color criticalColor = Color.red;

    public event Action<CleanroomSensor, CleanroomSensorData> DataUpdated;
    public event Action<CleanroomSensor, bool> StaleStateChanged;

    private readonly List<CleanroomTrendSample> trendHistory = new List<CleanroomTrendSample>();
    private CleanroomZone linkedZone;
    private bool lastStaleState;

    public IReadOnlyList<CleanroomTrendSample> TrendHistory => trendHistory;

    private void Reset()
    {
        indicatorRenderer = GetComponentInChildren<Renderer>();
    }

    private void Start()
    {
        linkedZone = FindZone();
        RefreshDataFreshness(false);

        if (currentData != null && !string.IsNullOrEmpty(currentData.sensorId))
            AppendTrendSample(currentData);
    }

    private void Update()
    {
        if (linkedZone == null)
            linkedZone = FindZone();

        RefreshDataFreshness(true);
        UpdateVisual();
    }

    public void UpdateData(CleanroomSensorData data)
    {
        if (data == null || data.sensorId != sensorId)
            return;

        currentData = data;

        if (linkedZone == null)
            linkedZone = FindZone();

        RefreshDataFreshness(false);

        if (linkedZone != null)
            linkedZone.ApplySensorData(data);

        AppendTrendSample(currentData);
        DataUpdated?.Invoke(this, currentData);
        UpdateVisual();
    }

    private CleanroomZone FindZone()
    {
        CleanroomZone[] zones = FindObjectsByType<CleanroomZone>(FindObjectsSortMode.None);

        foreach (CleanroomZone zone in zones)
        {
            if (zone.zoneId == zoneId)
                return zone;
        }

        return null;
    }

    private void UpdateVisual()
    {
        if (indicatorRenderer == null)
            return;

        Color color = normalColor;

        if (currentData != null && currentData.isStale)
        {
            color = criticalColor;
        }
        else if (linkedZone != null)
        {
            switch (linkedZone.CurrentStatus)
            {
                case CleanroomStatus.Warning:
                    color = warningColor;
                    break;
                case CleanroomStatus.Alarm:
                    color = alarmColor;
                    break;
                case CleanroomStatus.Critical:
                    color = criticalColor;
                    break;
            }
        }

        indicatorRenderer.material.color = color;
    }

    private void OnMouseDown()
    {
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            return;

        SensorInfoPanel panel = FindFirstObjectByType<SensorInfoPanel>();

        if (panel != null)
            panel.Show(this, linkedZone);
    }

    private void RefreshDataFreshness(bool notifyZone)
    {
        if (currentData == null)
            return;

        float ageSeconds = GetDataAgeSeconds(currentData);
        bool isStale = ageSeconds > staleThresholdSeconds;

        currentData.ageSeconds = ageSeconds;
        currentData.isStale = isStale;

        if (currentData.quality != SensorDataQuality.Invalid)
        {
            if (isStale)
                currentData.quality = SensorDataQuality.Stale;
            else
                currentData.quality = linkedZone != null ? SensorDataQuality.Good : SensorDataQuality.Degraded;
        }

        if (notifyZone && linkedZone != null)
            linkedZone.UpdateDataFreshness(currentData);

        if (lastStaleState != isStale)
        {
            lastStaleState = isStale;
            StaleStateChanged?.Invoke(this, isStale);
        }
    }

    private float GetDataAgeSeconds(CleanroomSensorData data)
    {
        string rawTimestamp = data.GetDisplayTimestamp();

        if (!string.IsNullOrEmpty(rawTimestamp) && DateTime.TryParse(rawTimestamp, out DateTime parsedTime))
            return Mathf.Max(0f, (float)(DateTime.Now - parsedTime).TotalSeconds);

        return Mathf.Max(0f, data.ageSeconds);
    }

    public bool TryGetTrendBounds(CleanroomMetric metric, out float minValue, out float maxValue)
    {
        minValue = 0f;
        maxValue = 0f;

        if (trendHistory.Count == 0)
            return false;

        minValue = float.MaxValue;
        maxValue = float.MinValue;

        foreach (CleanroomTrendSample sample in trendHistory)
        {
            float value = sample.GetMetricValue(metric);
            minValue = Mathf.Min(minValue, value);
            maxValue = Mathf.Max(maxValue, value);
        }

        return true;
    }

    public CleanroomTrendDirection GetTrendDirection(CleanroomMetric metric, int sampleWindow = 12)
    {
        if (trendHistory.Count < 2)
            return CleanroomTrendDirection.Stable;

        int lastIndex = trendHistory.Count - 1;
        int firstIndex = Mathf.Max(0, lastIndex - Mathf.Max(1, sampleWindow - 1));

        float firstValue = trendHistory[firstIndex].GetMetricValue(metric);
        float lastValue = trendHistory[lastIndex].GetMetricValue(metric);
        float delta = lastValue - firstValue;
        float threshold = GetTrendDirectionThreshold(metric);

        if (delta > threshold)
            return CleanroomTrendDirection.Rising;

        if (delta < -threshold)
            return CleanroomTrendDirection.Falling;

        return CleanroomTrendDirection.Stable;
    }

    private void AppendTrendSample(CleanroomSensorData data)
    {
        if (data == null)
            return;

        trendHistory.Add(new CleanroomTrendSample
        {
            timestamp = data.GetDisplayTimestamp(),
            sampleTime = Time.time,
            particle05 = data.particle05,
            temperature = data.temperature,
            pressure = data.pressure
        });

        TrimTrendHistory();
    }

    private void TrimTrendHistory()
    {
        while (trendHistory.Count > maxTrendSamples)
            trendHistory.RemoveAt(0);

        if (trendWindowSeconds <= 0f)
            return;

        float cutoffTime = Time.time - trendWindowSeconds;

        while (trendHistory.Count > 1 && trendHistory[0].sampleTime < cutoffTime)
            trendHistory.RemoveAt(0);
    }

    private float GetTrendDirectionThreshold(CleanroomMetric metric)
    {
        switch (metric)
        {
            case CleanroomMetric.Temperature:
                return 0.15f;
            case CleanroomMetric.Pressure:
                return 0.4f;
            default:
                return 150f;
        }
    }
}
