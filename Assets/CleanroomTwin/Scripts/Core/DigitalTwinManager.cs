using System;
using System.Collections.Generic;
using UnityEngine;

public class DigitalTwinManager : MonoBehaviour
{
    [Header("Global Simulation")]
    public bool pauseSimulation;
    public float globalTimeScale = 1f;

    [Header("Keyboard Debug")]
    public KeyCode togglePauseKey = KeyCode.Space;
    public KeyCode injectContaminationKey = KeyCode.C;

    [Header("Heatmap")]
    public CleanroomMetric heatmapMetric = CleanroomMetric.Particle05;
    public Transform heatmapCenter;
    public Vector2 heatmapSize = new Vector2(20f, 20f);
    public int heatmapResolutionX = 12;
    public int heatmapResolutionY = 12;
    public float heatmapRefreshInterval = 0.2f;
    public float maxInfluenceDistance = 20f;
    public float interpolationPower = 2f;
    public bool autoRebuildHeatmap = true;

    [SerializeField]
    private List<CleanroomHeatmapPoint> latestHeatmapPoints = new List<CleanroomHeatmapPoint>();

    private readonly List<CleanroomZone> cachedZones = new List<CleanroomZone>();
    private bool heatmapDirty = true;
    private float nextHeatmapRefreshTime;

    public event Action<DigitalTwinManager> HeatmapRebuilt;

    public IReadOnlyList<CleanroomHeatmapPoint> LatestHeatmapPoints => latestHeatmapPoints;
    public int HeatmapResolutionX => Mathf.Max(1, heatmapResolutionX);
    public int HeatmapResolutionY => Mathf.Max(1, heatmapResolutionY);

    private void OnEnable()
    {
        RefreshZoneCache();
        heatmapDirty = true;
        RebuildHeatmap();
    }

    private void OnDisable()
    {
        UnsubscribeFromZones();
    }

    private void Update()
    {
        Time.timeScale = pauseSimulation ? 0f : globalTimeScale;

        if (Input.GetKeyDown(togglePauseKey))
            pauseSimulation = !pauseSimulation;

        if (Input.GetKeyDown(injectContaminationKey))
            InjectContaminationToAllZones();

        if (autoRebuildHeatmap && heatmapDirty && Time.unscaledTime >= nextHeatmapRefreshTime)
        {
            RebuildHeatmap();
            nextHeatmapRefreshTime = Time.unscaledTime + heatmapRefreshInterval;
        }
    }

    private void InjectContaminationToAllZones()
    {
        foreach (CleanroomZone zone in cachedZones)
        {
            if (zone == null)
                continue;

            zone.AddContamination(10000f);
        }
    }

    public void RefreshZoneCache()
    {
        UnsubscribeFromZones();

        CleanroomZone[] zones = FindObjectsByType<CleanroomZone>(FindObjectsSortMode.None);

        foreach (CleanroomZone zone in zones)
        {
            if (zone == null)
                continue;

            zone.ZoneDataUpdated += HandleZoneDataUpdated;
            cachedZones.Add(zone);
        }
    }

    public bool TrySampleHeatmap(CleanroomMetric metric, Vector3 worldPosition, out float value, out float normalizedValue)
    {
        value = 0f;
        normalizedValue = 0f;

        CleanroomZone nearestZone = null;
        float nearestDistance = float.MaxValue;
        float weightedValue = 0f;
        float weightedNormalizedValue = 0f;
        float totalWeight = 0f;

        foreach (CleanroomZone zone in cachedZones)
        {
            if (zone == null)
                continue;

            Vector3 zonePosition = zone.GetHeatmapWorldPosition();
            float distance = Vector3.Distance(zonePosition, worldPosition);

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestZone = zone;
            }

            if (maxInfluenceDistance > 0f && distance > maxInfluenceDistance)
                continue;

            if (distance <= 0.001f)
            {
                value = zone.GetMetricValue(metric);
                normalizedValue = zone.GetNormalizedMetricValue(metric);
                return true;
            }

            float weight = 1f / Mathf.Pow(distance, Mathf.Max(0.01f, interpolationPower));
            weightedValue += zone.GetMetricValue(metric) * weight;
            weightedNormalizedValue += zone.GetNormalizedMetricValue(metric) * weight;
            totalWeight += weight;
        }

        if (totalWeight > 0f)
        {
            value = weightedValue / totalWeight;
            normalizedValue = weightedNormalizedValue / totalWeight;
            return true;
        }

        if (nearestZone == null)
            return false;

        value = nearestZone.GetMetricValue(metric);
        normalizedValue = nearestZone.GetNormalizedMetricValue(metric);
        return true;
    }

    public void RebuildHeatmap()
    {
        latestHeatmapPoints.Clear();

        int resolutionX = HeatmapResolutionX;
        int resolutionY = HeatmapResolutionY;

        for (int y = 0; y < resolutionY; y++)
        {
            for (int x = 0; x < resolutionX; x++)
            {
                Vector3 samplePosition = GetHeatmapSamplePosition(x, y, resolutionX, resolutionY);

                if (!TrySampleHeatmap(heatmapMetric, samplePosition, out float value, out float normalizedValue))
                    continue;

                latestHeatmapPoints.Add(new CleanroomHeatmapPoint
                {
                    worldPosition = samplePosition,
                    value = value,
                    normalizedValue = normalizedValue
                });
            }
        }

        heatmapDirty = false;
        HeatmapRebuilt?.Invoke(this);
    }

    public void ForceRefreshHeatmap()
    {
        heatmapDirty = true;
        RebuildHeatmap();
        nextHeatmapRefreshTime = Time.unscaledTime + heatmapRefreshInterval;
    }

    private Vector3 GetHeatmapSamplePosition(int x, int y, int resolutionX, int resolutionY)
    {
        Vector3 center = heatmapCenter != null ? heatmapCenter.position : transform.position;
        float tx = resolutionX <= 1 ? 0.5f : x / (float)(resolutionX - 1);
        float ty = resolutionY <= 1 ? 0.5f : y / (float)(resolutionY - 1);
        float offsetX = (tx - 0.5f) * heatmapSize.x;
        float offsetZ = (ty - 0.5f) * heatmapSize.y;

        return center + new Vector3(offsetX, 0f, offsetZ);
    }

    private void UnsubscribeFromZones()
    {
        foreach (CleanroomZone zone in cachedZones)
        {
            if (zone == null)
                continue;

            zone.ZoneDataUpdated -= HandleZoneDataUpdated;
        }

        cachedZones.Clear();
    }

    private void HandleZoneDataUpdated(CleanroomZone zone)
    {
        heatmapDirty = true;
    }
}
