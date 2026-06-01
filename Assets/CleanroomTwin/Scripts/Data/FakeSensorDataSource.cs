using System;
using System.Collections.Generic;
using UnityEngine;

public class FakeSensorDataSource : MonoBehaviour
{
    [Header("Update")]
    public float updateInterval = 1f;

    [Header("Metadata")]
    public string sourceId = "SIM_FAKE";

    [Header("Noise")]
    public float particleNoise = 500f;
    public float temperatureNoise = 0.2f;
    public float humidityNoise = 0.5f;
    public float pressureNoise = 0.8f;

    private float timer;
    private long sequenceCounter;

    private readonly Dictionary<string, CleanroomSensorData> dataMap = new();

    private void Start()
    {
        CleanroomSensor[] sensors = FindObjectsByType<CleanroomSensor>(FindObjectsSortMode.None);

        foreach (CleanroomSensor sensor in sensors)
        {
            if (dataMap.ContainsKey(sensor.sensorId))
                continue;

            CleanroomSensorData data = CreateInitialData(sensor);
            dataMap.Add(sensor.sensorId, data);
            sensor.UpdateData(data);
        }
    }

    private void Update()
    {
        timer += Time.deltaTime;

        if (timer >= updateInterval)
        {
            timer = 0f;
            UpdateFakeData();
        }
    }

    private CleanroomSensorData CreateInitialData(CleanroomSensor sensor)
    {
        CleanroomZone zone = FindZone(sensor.zoneId);

        float baseParticle = 2000f;
        float basePressure = 15f;

        if (zone != null)
        {
            if (zone.isoClass <= 5)
                baseParticle = 2500f;
            else if (zone.isoClass == 7)
                baseParticle = 8000f;
            else
                baseParticle = 15000f;

            basePressure = zone.targetPressure;
        }

        CleanroomSensorData data = new CleanroomSensorData
        {
            sensorId = sensor.sensorId,
            zoneId = sensor.zoneId,
            particle05 = baseParticle,
            particle5 = baseParticle * 0.01f,
            temperature = 22f,
            humidity = 45f,
            pressure = basePressure,
            airVelocity = 0.45f
        };

        StampData(data, zone != null);
        return data;
    }

    private void UpdateFakeData()
    {
        CleanroomSensor[] sensors = FindObjectsByType<CleanroomSensor>(FindObjectsSortMode.None);

        foreach (CleanroomSensor sensor in sensors)
        {
            if (!dataMap.TryGetValue(sensor.sensorId, out CleanroomSensorData data))
            {
                data = CreateInitialData(sensor);
                dataMap.Add(sensor.sensorId, data);
            }

            CleanroomZone zone = FindZone(sensor.zoneId);

            if (zone != null)
            {
                data.particle05 = zone.particle05 + UnityEngine.Random.Range(-particleNoise, particleNoise);
                data.particle05 = Mathf.Max(0f, data.particle05);

                data.particle5 = data.particle05 * 0.01f;
                data.temperature = Mathf.Clamp(
                    data.temperature + UnityEngine.Random.Range(-temperatureNoise, temperatureNoise),
                    18f,
                    28f
                );
                data.humidity = Mathf.Clamp(
                    data.humidity + UnityEngine.Random.Range(-humidityNoise, humidityNoise),
                    30f,
                    70f
                );
                data.pressure = zone.pressure + UnityEngine.Random.Range(-pressureNoise, pressureNoise);
                data.airVelocity = Mathf.Max(0f, data.airVelocity + UnityEngine.Random.Range(-0.03f, 0.03f));
            }
            else
            {
                data.particle05 = Mathf.Max(0f, data.particle05 + UnityEngine.Random.Range(-particleNoise, particleNoise));
                data.particle5 = data.particle05 * 0.01f;
                data.temperature = Mathf.Clamp(
                    data.temperature + UnityEngine.Random.Range(-temperatureNoise, temperatureNoise),
                    18f,
                    28f
                );
                data.humidity = Mathf.Clamp(
                    data.humidity + UnityEngine.Random.Range(-humidityNoise, humidityNoise),
                    30f,
                    70f
                );
                data.pressure += UnityEngine.Random.Range(-pressureNoise, pressureNoise);
                data.airVelocity = Mathf.Max(0f, data.airVelocity + UnityEngine.Random.Range(-0.03f, 0.03f));
            }

            data.pressure = Mathf.Clamp(data.pressure, -5f, 30f);
            StampData(data, zone != null);
            sensor.UpdateData(data);
        }
    }

    private void StampData(CleanroomSensorData data, bool hasLinkedZone)
    {
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        data.timestamp = now;
        data.receivedAt = now;
        data.sourceId = sourceId;
        data.quality = hasLinkedZone ? SensorDataQuality.Good : SensorDataQuality.Degraded;
        data.isStale = false;
        data.ageSeconds = 0f;
        data.sequenceNumber = ++sequenceCounter;
    }

    private CleanroomZone FindZone(string zoneId)
    {
        CleanroomZone[] zones = FindObjectsByType<CleanroomZone>(FindObjectsSortMode.None);

        foreach (CleanroomZone zone in zones)
        {
            if (zone.zoneId == zoneId)
                return zone;
        }

        return null;
    }
}
