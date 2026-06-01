/*
 * Created :    Summer 2026
 * Author :     蘇家賢
 * Project :    General-3D-Spatial-Thermal-Flow-Simulator
 * Filename :   HeatThermometerSystem.cs
 * 
 */

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
public class HeatThermometerSystem : MonoBehaviour
{
    [Header("References")]
    [Tooltip("目前場景中的 ClassroomHeatSimulation。")]
    [SerializeField] private ClassroomHeatSimulation simulation;

    [Header("Sampling")]
    [Tooltip("多久從 GPU 溫度場讀取一次資料。數值越小越即時，但成本越高。")]
    [SerializeField, Min(0.02f)] private float sampleInterval = 0.2f;

    [Tooltip("是否使用三線性插值。開啟後溫度計移動時讀值較平滑。")]
    [SerializeField] private bool useTrilinearInterpolation = true;

    [Tooltip("如果溫度場版本沒有變化，是否避免重複 GPU Readback。")]
    [SerializeField] private bool skipReadbackWhenTemperatureUnchanged = false;

    [Tooltip("是否在編輯模式下也更新溫度讀值。")]
    [SerializeField] private bool sampleInEditMode = true;

    [Header("Probe Collection")]
    [Tooltip("是否自動搜尋此物件子階層下的 HeatThermometerProbe。")]
    [SerializeField] private bool autoFindProbesInChildren = true;

    [Tooltip("自動搜尋時是否包含未啟用的探針。")]
    [SerializeField] private bool includeInactiveProbes = false;

    [Tooltip("手動指定的溫度計探針。")]
    [SerializeField] private List<HeatThermometerProbe> probes = new List<HeatThermometerProbe>();

    [Header("Debug / Bounds")]
    [SerializeField] private bool clampOutOfBoundsProbe = true;
    [SerializeField] private bool drawSimulationBounds = true;
    [SerializeField] private Color simulationBoundsColor = Color.green;

    private readonly List<HeatThermometerProbe> _autoFoundProbes = new List<HeatThermometerProbe>();

    private bool _readbackInFlight;
    private float _nextSampleTime;
    private int _lastReadbackTemperatureVersion = -1;

    private float[] _temperatureCpuCache;
    private bool _hasCpuCache;

    private ComputeBuffer _temperatureReadbackBuffer;
    private int _temperatureReadbackBufferCount;

    private void OnDisable()
    {
        ReleaseTemperatureReadbackBuffer();
        _readbackInFlight = false;
    }

    private void OnDestroy()
    {
        ReleaseTemperatureReadbackBuffer();
        _readbackInFlight = false;
    }

    private void ReleaseTemperatureReadbackBuffer()
    {
        if (_temperatureReadbackBuffer != null)
        {
            _temperatureReadbackBuffer.Release();
            _temperatureReadbackBuffer = null;
        }

        _temperatureReadbackBufferCount = 0;
    }

    private bool EnsureTemperatureReadbackBuffer()
    {
        if (simulation == null)
            return false;

        int count = simulation.GridX * simulation.GridY * simulation.GridZ;

        if (count <= 0)
            return false;

        if (_temperatureReadbackBuffer != null && _temperatureReadbackBufferCount == count)
            return true;

        ReleaseTemperatureReadbackBuffer();

        _temperatureReadbackBuffer = new ComputeBuffer(
            count,
            sizeof(float),
            ComputeBufferType.Structured
        );

        _temperatureReadbackBufferCount = count;

        return true;
    }

    private void Reset()
    {
        ResolveSimulation();
        RefreshProbeList();
    }

    private void OnEnable()
    {
        ResolveSimulation();
        RefreshProbeList();
    }

    private void OnValidate()
    {
        sampleInterval = Mathf.Max(0.02f, sampleInterval);
        RefreshProbeList();
    }

    private void Update()
    {
        if (!Application.isPlaying && !sampleInEditMode)
            return;

        ResolveSimulation();

        if (simulation == null)
        {
            SetAllProbesInvalid();
            return;
        }

        float now = Application.isPlaying ? Time.unscaledTime : Time.realtimeSinceStartup;

        if (now < _nextSampleTime)
            return;

        _nextSampleTime = now + sampleInterval;

        RefreshProbeList();
        RequestTemperatureReadback();
    }

    public void RegisterProbe(HeatThermometerProbe probe)
    {
        if (probe == null)
            return;

        if (!probes.Contains(probe))
            probes.Add(probe);
    }

    public void UnregisterProbe(HeatThermometerProbe probe)
    {
        if (probe == null)
            return;

        probes.Remove(probe);
    }

    private void ResolveSimulation()
    {
        if (simulation != null)
            return;

        simulation = GetComponentInParent<ClassroomHeatSimulation>();

        if (simulation == null)
            simulation = FindObjectOfType<ClassroomHeatSimulation>();
    }

    private void RefreshProbeList()
    {
        probes.RemoveAll(p => p == null);

        if (!autoFindProbesInChildren)
            return;

        _autoFoundProbes.Clear();
        GetComponentsInChildren(includeInactiveProbes, _autoFoundProbes);

        for (int i = 0; i < _autoFoundProbes.Count; i++)
        {
            HeatThermometerProbe probe = _autoFoundProbes[i];

            if (probe != null && !probes.Contains(probe))
                probes.Add(probe);
        }
    }

    private void RequestTemperatureReadback()
    {
        if (_readbackInFlight)
            return;

        if (simulation == null)
            return;

        simulation.InitializeIfNeeded();

        if (!simulation.IsInitialized || simulation.TemperatureTexture == null)
        {
            SetAllProbesInvalid();
            return;
        }

        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            Debug.LogWarning("[HeatThermometerSystem] 此裝置不支援 AsyncGPUReadback，無法讀取 GPU 溫度場。");
            SetAllProbesInvalid();
            return;
        }

        int currentVersion = simulation.TemperatureSwapVersion;

        if (skipReadbackWhenTemperatureUnchanged &&
            _hasCpuCache &&
            currentVersion == _lastReadbackTemperatureVersion)
        {
            UpdateAllProbesFromCache();
            return;
        }

        if (!EnsureTemperatureReadbackBuffer())
        {
            Debug.LogWarning("[HeatThermometerSystem] 無法建立溫度讀回 ComputeBuffer。");
            SetAllProbesInvalid();
            return;
        }

        bool copied = simulation.CopyTemperatureToBuffer(_temperatureReadbackBuffer);

        if (!copied)
        {
            SetAllProbesInvalid();
            return;
        }

        _readbackInFlight = true;

        AsyncGPUReadback.Request(_temperatureReadbackBuffer, request =>
        {
            HandleTemperatureReadback(request, currentVersion);
        });
    }

    private void HandleTemperatureReadback(AsyncGPUReadbackRequest request, int temperatureVersion)
    {
        _readbackInFlight = false;

        if (this == null)
            return;

        if (request.hasError)
        {
            Debug.LogWarning("[HeatThermometerSystem] AsyncGPUReadback 讀取溫度場失敗。");
            SetAllProbesInvalid();
            return;
        }

        if (simulation == null)
        {
            SetAllProbesInvalid();
            return;
        }

        int expectedCount = simulation.GridX * simulation.GridY * simulation.GridZ;
        var data = request.GetData<float>();

        if (data.Length < expectedCount)
        {
            Debug.LogWarning(
                $"[HeatThermometerSystem] 溫度資料長度不正確。Expected={expectedCount}, Actual={data.Length}"
            );

            SetAllProbesInvalid();
            return;
        }

        if (_temperatureCpuCache == null || _temperatureCpuCache.Length != expectedCount)
            _temperatureCpuCache = new float[expectedCount];

        data.CopyTo(_temperatureCpuCache);

        _hasCpuCache = true;
        _lastReadbackTemperatureVersion = temperatureVersion;

        UpdateAllProbesFromCache();
    }

    private void UpdateAllProbesFromCache()
    {
        if (!_hasCpuCache || _temperatureCpuCache == null || simulation == null)
        {
            SetAllProbesInvalid();
            return;
        }

        for (int i = 0; i < probes.Count; i++)
        {
            HeatThermometerProbe probe = probes[i];

            if (probe == null)
                continue;

            Vector3Int gridPosition;
            bool valid;

            float temperature = SampleTemperatureAtWorldPosition(
                probe.SampleWorldPosition,
                out gridPosition,
                out valid
            );

            probe.ApplyReading(temperature, valid, gridPosition);
        }
    }

    private float SampleTemperatureAtWorldPosition(
        Vector3 worldPosition,
        out Vector3Int gridPosition,
        out bool valid
    )
    {
        gridPosition = Vector3Int.zero;
        valid = false;

        if (simulation == null || _temperatureCpuCache == null)
            return float.NaN;

        Bounds bounds = simulation.GetSimulationBounds();

        bool insideBounds = bounds.Contains(worldPosition);

        if (!insideBounds && !clampOutOfBoundsProbe)
        {
            return float.NaN;
        }

        Vector3 samplePosition = worldPosition;

        if (!insideBounds && clampOutOfBoundsProbe)
        {
            samplePosition = new Vector3(
                Mathf.Clamp(worldPosition.x, bounds.min.x, bounds.max.x),
                Mathf.Clamp(worldPosition.y, bounds.min.y, bounds.max.y),
                Mathf.Clamp(worldPosition.z, bounds.min.z, bounds.max.z)
            );
        }

        Vector3 local01 = new Vector3(
            Mathf.InverseLerp(bounds.min.x, bounds.max.x, samplePosition.x),
            Mathf.InverseLerp(bounds.min.y, bounds.max.y, samplePosition.y),
            Mathf.InverseLerp(bounds.min.z, bounds.max.z, samplePosition.z)
        );

        int nearestX = Mathf.Clamp(Mathf.FloorToInt(local01.x * simulation.GridX), 0, simulation.GridX - 1);
        int nearestY = Mathf.Clamp(Mathf.FloorToInt(local01.y * simulation.GridY), 0, simulation.GridY - 1);
        int nearestZ = Mathf.Clamp(Mathf.FloorToInt(local01.z * simulation.GridZ), 0, simulation.GridZ - 1);

        gridPosition = new Vector3Int(nearestX, nearestY, nearestZ);
        valid = true;

        if (!useTrilinearInterpolation)
        {
            return ReadCell(nearestX, nearestY, nearestZ);
        }

        float fx = Mathf.Clamp(local01.x * simulation.GridX - 0.5f, 0f, simulation.GridX - 1f);
        float fy = Mathf.Clamp(local01.y * simulation.GridY - 0.5f, 0f, simulation.GridY - 1f);
        float fz = Mathf.Clamp(local01.z * simulation.GridZ - 0.5f, 0f, simulation.GridZ - 1f);

        int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, simulation.GridX - 1);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, simulation.GridY - 1);
        int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, simulation.GridZ - 1);

        int x1 = Mathf.Min(x0 + 1, simulation.GridX - 1);
        int y1 = Mathf.Min(y0 + 1, simulation.GridY - 1);
        int z1 = Mathf.Min(z0 + 1, simulation.GridZ - 1);

        float tx = fx - x0;
        float ty = fy - y0;
        float tz = fz - z0;

        float c000 = ReadCell(x0, y0, z0);
        float c100 = ReadCell(x1, y0, z0);
        float c010 = ReadCell(x0, y1, z0);
        float c110 = ReadCell(x1, y1, z0);

        float c001 = ReadCell(x0, y0, z1);
        float c101 = ReadCell(x1, y0, z1);
        float c011 = ReadCell(x0, y1, z1);
        float c111 = ReadCell(x1, y1, z1);

        float c00 = Mathf.Lerp(c000, c100, tx);
        float c10 = Mathf.Lerp(c010, c110, tx);
        float c01 = Mathf.Lerp(c001, c101, tx);
        float c11 = Mathf.Lerp(c011, c111, tx);

        float c0 = Mathf.Lerp(c00, c10, ty);
        float c1 = Mathf.Lerp(c01, c11, ty);

        return Mathf.Lerp(c0, c1, tz);
    }

    private float ReadCell(int x, int y, int z)
    {
        x = Mathf.Clamp(x, 0, simulation.GridX - 1);
        y = Mathf.Clamp(y, 0, simulation.GridY - 1);
        z = Mathf.Clamp(z, 0, simulation.GridZ - 1);

        int index = x + y * simulation.GridX + z * simulation.GridX * simulation.GridY;

        if (_temperatureCpuCache == null || index < 0 || index >= _temperatureCpuCache.Length)
            return float.NaN;

        return _temperatureCpuCache[index];
    }

    private void SetAllProbesInvalid()
    {
        for (int i = 0; i < probes.Count; i++)
        {
            if (probes[i] != null)
                probes[i].ApplyReading(float.NaN, false, Vector3Int.zero);
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawSimulationBounds)
            return;

        ResolveSimulation();

        if (simulation == null)
            return;

        Bounds bounds = simulation.GetSimulationBounds();

        Gizmos.color = simulationBoundsColor;
        Gizmos.DrawWireCube(bounds.center, bounds.size);
    }
}
