using System;
using UnityEngine;

[Serializable]
public class CleanroomSensorData
{
    public string sensorId;
    public string zoneId;

    public float particle05;      // particles / m3
    public float particle5;       // particles / m3

    public float temperature;     // Celsius
    public float humidity;        // %
    public float pressure;        // Pa
    public float airVelocity;     // m/s

    public string timestamp;
    public string receivedAt;
    public string sourceId = "SIM";
    public SensorDataQuality quality = SensorDataQuality.Unknown;
    public bool isStale;
    public float ageSeconds;
    public long sequenceNumber;

    public string GetDisplayTimestamp()
    {
        if (!string.IsNullOrEmpty(receivedAt))
            return receivedAt;

        return timestamp;
    }
}

public enum SensorDataQuality
{
    Unknown,
    Good,
    Degraded,
    Stale,
    Invalid
}

public enum CleanroomMetric
{
    Particle05,
    Temperature,
    Pressure
}

public enum CleanroomTrendDirection
{
    Stable,
    Rising,
    Falling
}

[Serializable]
public class CleanroomTrendSample
{
    public string timestamp;
    public float sampleTime;
    public float particle05;
    public float temperature;
    public float pressure;

    public float GetMetricValue(CleanroomMetric metric)
    {
        switch (metric)
        {
            case CleanroomMetric.Temperature:
                return temperature;
            case CleanroomMetric.Pressure:
                return pressure;
            default:
                return particle05;
        }
    }
}

[Serializable]
public class CleanroomHeatmapPoint
{
    public Vector3 worldPosition;
    public float value;
    public float normalizedValue;
}

public enum CleanroomStatus
{
    Normal,
    Warning,
    Alarm,
    Critical
}

[Serializable]
public class CleanroomThreshold
{
    [Header("Particle Limits")]
    public float warningParticle05 = 5000f;
    public float alarmParticle05 = 10000f;
    public float criticalParticle05 = 20000f;

    [Header("Temperature Limits")]
    public float minTemperature = 20f;
    public float maxTemperature = 24f;

    [Header("Humidity Limits")]
    public float minHumidity = 40f;
    public float maxHumidity = 60f;

    [Header("Pressure Limits")]
    public float minPressure = 10f;
    public float criticalPressure = 0f;

    [Header("Door")]
    public float maxDoorOpenSeconds = 30f;
}
