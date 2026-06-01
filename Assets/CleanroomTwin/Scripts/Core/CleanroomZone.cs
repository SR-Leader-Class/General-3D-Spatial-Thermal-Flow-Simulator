using System;
using UnityEngine;

public class CleanroomZone : MonoBehaviour
{
    [Header("Zone Info")]
    public string zoneId = "ISO5_A";
    public string displayName = "ISO 5 Area A";
    public int isoClass = 5;

    [Header("Room Physical Data")]
    public float roomVolume = 120f;        // m3
    public float airFlowRate = 1.5f;       // m3/s
    public float targetPressure = 15f;     // Pa

    [Header("Current State")]
    public float particle05;
    public float particle5;
    public float temperature;
    public float humidity;
    public float pressure;
    public float airVelocity;
    public float doorOpenSeconds;

    [Header("Simulation")]
    public float particleGenerationRate = 20f;
    public float contaminationInputRate = 0f;
    public float filterEfficiency = 0.99f;

    [Header("Threshold")]
    public CleanroomThreshold threshold = new CleanroomThreshold();

    [Header("Visual")]
    public Renderer zoneRenderer;
    public Color normalColor = new Color(0.2f, 0.8f, 0.4f, 0.35f);
    public Color warningColor = new Color(1f, 0.8f, 0.1f, 0.45f);
    public Color alarmColor = new Color(1f, 0.35f, 0.1f, 0.55f);
    public Color criticalColor = new Color(1f, 0f, 0f, 0.7f);

    [Header("Heatmap")]
    public Transform heatmapAnchor;
    public Vector3 heatmapOffset;

    public event Action<CleanroomZone, CleanroomStatus> StatusChanged;
    public event Action<CleanroomZone> ZoneDataUpdated;

    public CleanroomStatus CurrentStatus { get; private set; } = CleanroomStatus.Normal;
    public float CurrentRiskScore { get; private set; }
    public string PrimaryRiskReason { get; private set; } = "Initializing";
    public string LastUpdateTimestamp { get; private set; } = string.Empty;
    public SensorDataQuality LastDataQuality { get; private set; } = SensorDataQuality.Unknown;
    public bool IsDataStale { get; private set; }
    public long LatestSequenceNumber { get; private set; }

    public float ACH
    {
        get
        {
            if (roomVolume <= 0f)
                return 0f;

            return airFlowRate * 3600f / roomVolume;
        }
    }

    private void Reset()
    {
        zoneRenderer = GetComponent<Renderer>();
        heatmapAnchor = transform;
    }

    private void Start()
    {
        RefreshState();
    }

    private void Update()
    {
        SimulateParticleDecay(Time.deltaTime);
        RefreshState();
    }

    public void ApplySensorData(CleanroomSensorData data)
    {
        if (data == null || data.zoneId != zoneId)
            return;

        ApplyDataValues(data);
        ApplyDataMetadata(data);
        RefreshState();
    }

    public void UpdateDataFreshness(CleanroomSensorData data)
    {
        if (data == null || data.zoneId != zoneId)
            return;

        ApplyDataMetadata(data);
        RefreshState();
    }

    public void AddDoorOpenTime(float deltaTime)
    {
        doorOpenSeconds += deltaTime;
    }

    public void ResetDoorOpenTime()
    {
        doorOpenSeconds = 0f;
    }

    public void AddContamination(float amount)
    {
        particle05 += amount;
        contaminationInputRate = amount;
    }

    private void ApplyDataValues(CleanroomSensorData data)
    {
        particle05 = data.particle05;
        particle5 = data.particle5;
        temperature = data.temperature;
        humidity = data.humidity;
        pressure = data.pressure;
        airVelocity = data.airVelocity;
    }

    private void ApplyDataMetadata(CleanroomSensorData data)
    {
        LastUpdateTimestamp = data.GetDisplayTimestamp();
        LastDataQuality = data.quality;
        IsDataStale = data.isStale;
        LatestSequenceNumber = data.sequenceNumber;
    }

    private void SimulateParticleDecay(float deltaTime)
    {
        if (roomVolume <= 0f)
            return;

        float lambda = ACH / 3600f;
        float removal = particle05 * lambda * filterEfficiency * deltaTime;
        float generation = particleGenerationRate * deltaTime;
        float contamination = contaminationInputRate * deltaTime;

        particle05 += generation + contamination - removal;
        particle05 = Mathf.Max(0f, particle05);

        contaminationInputRate = Mathf.Lerp(contaminationInputRate, 0f, deltaTime);
    }

    private void UpdateStatus()
    {
        CleanroomStatus previousStatus = CurrentStatus;
        CurrentRiskScore = CalculateRiskScore();
        PrimaryRiskReason = DeterminePrimaryRiskReason();

        if (CurrentRiskScore >= 0.85f)
            CurrentStatus = CleanroomStatus.Critical;
        else if (CurrentRiskScore >= 0.65f)
            CurrentStatus = CleanroomStatus.Alarm;
        else if (CurrentRiskScore >= 0.35f)
            CurrentStatus = CleanroomStatus.Warning;
        else
            CurrentStatus = CleanroomStatus.Normal;

        if (previousStatus != CurrentStatus)
            StatusChanged?.Invoke(this, CurrentStatus);
    }

    public float CalculateRiskScore()
    {
        float particleRisk = Mathf.InverseLerp(
            threshold.warningParticle05,
            threshold.criticalParticle05,
            particle05
        );

        float pressureRisk = 0f;

        if (pressure < threshold.minPressure)
        {
            pressureRisk = Mathf.Clamp01(
                (threshold.minPressure - pressure) /
                Mathf.Max(0.01f, threshold.minPressure - threshold.criticalPressure)
            );
        }

        float temperatureRisk = 0f;

        if (temperature < threshold.minTemperature)
        {
            temperatureRisk = Mathf.Clamp01(
                (threshold.minTemperature - temperature) / 5f
            );
        }
        else if (temperature > threshold.maxTemperature)
        {
            temperatureRisk = Mathf.Clamp01(
                (temperature - threshold.maxTemperature) / 5f
            );
        }

        float humidityRisk = 0f;

        if (humidity < threshold.minHumidity)
        {
            humidityRisk = Mathf.Clamp01(
                (threshold.minHumidity - humidity) / 20f
            );
        }
        else if (humidity > threshold.maxHumidity)
        {
            humidityRisk = Mathf.Clamp01(
                (humidity - threshold.maxHumidity) / 20f
            );
        }

        float doorRisk = Mathf.Clamp01(
            doorOpenSeconds / Mathf.Max(1f, threshold.maxDoorOpenSeconds)
        );

        float risk =
            particleRisk * 0.4f +
            pressureRisk * 0.3f +
            temperatureRisk * 0.1f +
            humidityRisk * 0.1f +
            doorRisk * 0.1f;

        return Mathf.Clamp01(risk);
    }

    private string DeterminePrimaryRiskReason()
    {
        if (IsDataStale)
            return "Sensor data is stale";

        if (LastDataQuality == SensorDataQuality.Invalid)
            return "Sensor data marked invalid";

        if (LastDataQuality == SensorDataQuality.Degraded)
            return "Sensor-zone link degraded";

        if (pressure < threshold.criticalPressure)
            return $"Pressure reversed: {pressure:F1} Pa";

        if (pressure < threshold.minPressure)
            return $"Low pressure: {pressure:F1} Pa";

        if (particle05 >= threshold.criticalParticle05)
            return $"Critical particle level: {particle05:F0} / m3";

        if (particle05 >= threshold.alarmParticle05)
            return $"High particle level: {particle05:F0} / m3";

        if (doorOpenSeconds > threshold.maxDoorOpenSeconds)
            return $"Door open too long: {doorOpenSeconds:F1}s";

        if (temperature < threshold.minTemperature || temperature > threshold.maxTemperature)
            return $"Temperature abnormal: {temperature:F1} C";

        if (humidity < threshold.minHumidity || humidity > threshold.maxHumidity)
            return $"Humidity abnormal: {humidity:F1}%";

        return "All major values within configured thresholds";
    }

    private void RefreshState()
    {
        UpdateStatus();
        UpdateVisual();
        ZoneDataUpdated?.Invoke(this);
    }

    public Vector3 GetHeatmapWorldPosition()
    {
        if (heatmapAnchor != null)
            return heatmapAnchor.TransformPoint(heatmapOffset);

        return transform.TransformPoint(heatmapOffset);
    }

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

    public float GetNormalizedMetricValue(CleanroomMetric metric)
    {
        switch (metric)
        {
            case CleanroomMetric.Temperature:
                return SafeInverseLerp(
                    threshold.minTemperature - 2f,
                    threshold.maxTemperature + 2f,
                    temperature
                );
            case CleanroomMetric.Pressure:
                return SafeInverseLerp(
                    threshold.criticalPressure,
                    Mathf.Max(targetPressure, threshold.minPressure),
                    pressure
                );
            default:
                return SafeInverseLerp(
                    0f,
                    Mathf.Max(1f, threshold.criticalParticle05),
                    particle05
                );
        }
    }

    private float SafeInverseLerp(float minValue, float maxValue, float value)
    {
        if (Mathf.Approximately(minValue, maxValue))
            return 0f;

        return Mathf.Clamp01(Mathf.InverseLerp(minValue, maxValue, value));
    }

    private void UpdateVisual()
    {
        if (zoneRenderer == null)
            return;

        Color targetColor = normalColor;

        switch (CurrentStatus)
        {
            case CleanroomStatus.Warning:
                targetColor = warningColor;
                break;
            case CleanroomStatus.Alarm:
                targetColor = alarmColor;
                break;
            case CleanroomStatus.Critical:
                targetColor = criticalColor;
                break;
        }

        // ensure material uses transparent blending if it supports the standard _Mode property
        var mat = zoneRenderer.material;
        if (mat != null && mat.HasProperty("_Mode"))
        {
            // 3 = Transparent for the Standard shader
            mat.SetFloat("_Mode", 3f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }

        mat.color = Color.Lerp(
            mat.color,
            targetColor,
            Time.deltaTime * 5f
        );
    }
}
