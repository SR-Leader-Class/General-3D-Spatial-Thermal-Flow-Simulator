using System;
using TMPro;
using UnityEngine;

public class SensorInfoPanel : MonoBehaviour
{
    [Header("UI")]
    public GameObject root;
    public TMP_Text titleText;
    public TMP_Text bodyText;
    public CleanroomTrendChart trendChart;
    public CleanroomMetric trendMetric = CleanroomMetric.Particle05;

    private void Start()
    {
        Hide();
    }

    public void Show(CleanroomSensor sensor, CleanroomZone zone)
    {
        if (root != null)
            root.SetActive(true);

        if (titleText != null)
            titleText.text = sensor.displayName;

        if (trendChart != null)
        {
            trendChart.SetMetric(trendMetric);
            trendChart.SetSensor(sensor);
        }

        if (bodyText == null)
            return;

        CleanroomSensorData data = sensor.currentData;

        string zoneText = zone != null ? zone.displayName : sensor.zoneId;
        string statusText = zone != null ? zone.CurrentStatus.ToString() : "Unknown";
        float ach = zone != null ? zone.ACH : 0f;
        float risk = zone != null ? zone.CalculateRiskScore() : 0f;
        string freshnessText = GetFreshnessText(data);
        string mainReasonText = GetMainReason(zone, data);

        bodyText.text =
            $"Sensor ID: {sensor.sensorId}\n" +
            $"Zone: {zoneText}\n" +
            $"Status: {statusText}\n" +
            $"Data Source: {data.sourceId}\n" +
            $"Quality: {data.quality}\n" +
            $"Freshness: {freshnessText}\n\n" +
            $"Particle 0.5 μm: {data.particle05:F0} particles/m³\n" +
            $"Particle 5.0 μm: {data.particle5:F0} particles/m³\n" +
            $"Temperature: {data.temperature:F1} °C\n" +
            $"Humidity: {data.humidity:F1} %\n" +
            $"Pressure: {data.pressure:F1} Pa\n" +
            $"Air Velocity: {data.airVelocity:F2} m/s\n" +
            $"ACH: {ach:F1} times/hour\n" +
            $"Risk Score: {risk:F2}\n" +
            $"Primary Risk: {mainReasonText}\n\n" +
            $"Last Update: {data.GetDisplayTimestamp()}";
    }

    public void Hide()
    {
        if (root != null)
            root.SetActive(false);

        if (trendChart != null)
            trendChart.ClearChart();
    }

    public void SetTrendMetric(CleanroomMetric metric)
    {
        trendMetric = metric;

        if (trendChart != null)
            trendChart.SetMetric(metric);
    }

    private string GetFreshnessText(CleanroomSensorData data)
    {
        if (TryGetDataAgeSeconds(data, out float ageSeconds))
        {
            if (ageSeconds <= 2f)
                return $"{ageSeconds:F1}s (Live)";

            if (ageSeconds <= 10f)
                return $"{ageSeconds:F1}s (Delayed)";

            return $"{ageSeconds:F1}s (Stale)";
        }

        if (data.isStale)
            return "Unknown (Stale Flag)";

        return "Unknown";
    }

    private bool TryGetDataAgeSeconds(CleanroomSensorData data, out float ageSeconds)
    {
        ageSeconds = data.ageSeconds;

        string rawTimestamp = data.GetDisplayTimestamp();

        if (string.IsNullOrEmpty(rawTimestamp))
            return ageSeconds > 0f;

        if (!DateTime.TryParse(rawTimestamp, out DateTime parsedTime))
            return ageSeconds > 0f;

        ageSeconds = Mathf.Max(0f, (float)(DateTime.Now - parsedTime).TotalSeconds);
        return true;
    }

    private string GetMainReason(CleanroomZone zone, CleanroomSensorData data)
    {
        if (data.isStale)
            return "Sensor data is stale";

        if (data.quality == SensorDataQuality.Invalid)
            return "Sensor data marked invalid";

        if (data.quality == SensorDataQuality.Degraded)
            return "Zone link degraded";

        if (zone == null)
            return "Zone context unavailable";

        if (zone.pressure < zone.threshold.criticalPressure)
            return $"Pressure reversed at {zone.pressure:F1} Pa";

        if (zone.pressure < zone.threshold.minPressure)
            return $"Pressure below minimum at {zone.pressure:F1} Pa";

        if (zone.particle05 >= zone.threshold.criticalParticle05)
            return $"Critical particle level at {zone.particle05:F0} / m³";

        if (zone.particle05 >= zone.threshold.alarmParticle05)
            return $"Particle level above alarm at {zone.particle05:F0} / m³";

        if (zone.doorOpenSeconds > zone.threshold.maxDoorOpenSeconds)
            return $"Door open for {zone.doorOpenSeconds:F1}s";

        if (zone.temperature < zone.threshold.minTemperature || zone.temperature > zone.threshold.maxTemperature)
            return $"Temperature out of range at {zone.temperature:F1} °C";

        if (zone.humidity < zone.threshold.minHumidity || zone.humidity > zone.threshold.maxHumidity)
            return $"Humidity out of range at {zone.humidity:F1}%";

        return "All major values within configured thresholds";
    }
}
