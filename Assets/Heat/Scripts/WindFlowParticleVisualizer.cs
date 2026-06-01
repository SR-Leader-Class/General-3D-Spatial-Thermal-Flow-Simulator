/*
 * Created :    Summer 2026
 * Author :     蘇家賢
 * Project :    General-3D-Spatial-Thermal-Flow-Simulator
 * Filename :   WindFlowParticleVisualizer.cs
 * 
 */

using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
[RequireComponent(typeof(ParticleSystem))]
public class WindFlowParticleVisualizer : MonoBehaviour
{
    public enum DisplayBoundsMode
    {
        SimulationBounds,
        CustomBounds
    }

    public enum RespawnMode
    {
        RandomInVolume,
        XMinFace,
        XMaxFace,
        YMinFace,
        YMaxFace,
        ZMinFace,
        ZMaxFace
    }

    public enum DebugWindMode
    {
        None,
        ConstantDirection,
        Swirl,
        Updraft
    }

    [Header("References")]
    [Tooltip("要讀取風場資料的熱模擬元件。")]
    [SerializeField] private ClassroomHeatSimulation simulation;
    [Tooltip("實際承載粒子的 ParticleSystem 元件。")]
    [SerializeField] private ParticleSystem targetParticleSystem;

    [Header("Sampling")]
    [Tooltip("當 simulation 為空時，自動搜尋場景中的熱模擬元件。")]
    [SerializeField] private bool autoFindSimulation = true;
    [Tooltip("非播放模式下是否也更新粒子流向。")]
    [SerializeField] private bool updateInEditMode = false;
    [Tooltip("向 GPU 讀回速度場資料的時間間隔。")]
    [SerializeField, Min(0.05f)] private float readbackInterval = 0.15f;
    [Tooltip("是否把顯示範圍映射成速度取樣範圍。")]
    [SerializeField] private bool useDisplayBoundsForVelocitySampling = false;

    [Header("Particles")]
    [Tooltip("同時存在的粒子總數。")]
    [SerializeField, Min(1)] private int particleCount = 256;
    [Tooltip("單顆粒子的生命週期秒數。")]
    [SerializeField, Min(0.1f)] private float particleLifetime = 4f;
    [SerializeField, Min(0f), Tooltip("速度場很弱時，建議把此倍率調到 2 到 10。")]
    private float speedMultiplier = 4f;
    [Tooltip("粒子的顯示尺寸。")]
    [SerializeField, Min(0.005f)] private float particleSize = 0.035f;
    [Tooltip("粒子的顏色。")]
    [SerializeField] private Color particleColor = new Color(0.75f, 0.95f, 1f, 0.9f);
    [Tooltip("判定速度是否可視為非零的門檻值。")]
    [SerializeField, Min(0f)] private float nonZeroVelocityThreshold = 0.0001f;

    [Header("Display Range")]
    [Tooltip("粒子顯示範圍使用模擬盒或自訂盒。")]
    [SerializeField] private DisplayBoundsMode displayBoundsMode = DisplayBoundsMode.SimulationBounds;
    [Tooltip("自訂顯示範圍是否跟隨此物件 Transform。")]
    [SerializeField] private bool customBoundsFollowTransform = true;
    [Tooltip("自訂顯示範圍中心相對於物件的位置偏移。")]
    [SerializeField] private Vector3 customBoundsCenterOffset = Vector3.zero;
    [Tooltip("自訂顯示範圍的尺寸。")]
    [SerializeField] private Vector3 customBoundsSize = new Vector3(4f, 2f, 4f);

    [Header("Respawn")]
    [Tooltip("粒子重生時的生成方式。")]
    [SerializeField] private RespawnMode respawnMode = RespawnMode.RandomInVolume;
    [Tooltip("首次生成粒子時是否隨機化剩餘生命週期。")]
    [SerializeField] private bool randomizeInitialLifetime = true;

    [Header("Debug Wind")]
    [Tooltip("除錯用測試風場模式；不是 None 時不讀取模擬速度場。")]
    [SerializeField] private DebugWindMode debugWindMode = DebugWindMode.None;
    [Tooltip("固定方向測試風場使用的方向向量。")]
    [SerializeField] private Vector3 debugWindDirection = Vector3.right;
    [Tooltip("除錯風場模式使用的基準風速。")]
    [SerializeField, Min(0f)] private float debugWindSpeed = 1.5f;
    [Tooltip("Swirl 模式繞流所使用的旋轉軸。")]
    [SerializeField] private Vector3 debugSwirlAxis = Vector3.up;

    [Header("Velocity Debug")]
    [Tooltip("最近一次速度場讀回的狀態訊息。")]
    [SerializeField] private string lastReadbackStatus = "No readback requested yet.";
    [Tooltip("最近一次速度場讀回後計算出的平均速度向量。")]
    [SerializeField] private Vector3 lastAverageVelocity = Vector3.zero;
    [Tooltip("最近一次速度場讀回後的最大速度。")]
    [SerializeField] private float lastMaxVelocity = 0f;
    [Tooltip("最近一次速度場讀回後，速度大於門檻的格點數。")]
    [SerializeField] private int lastNonZeroVelocityCount = 0;
    [Tooltip("目前速度場讀回是否有效。")]
    [SerializeField] private bool isVelocityReadbackValid = false;
    [Tooltip("目前速度貼圖的 RenderTexture 格式。")]
    [SerializeField] private string velocityTextureFormat = "Unknown";
    [Tooltip("目前速度貼圖的尺寸。")]
    [SerializeField] private Vector3Int velocityTextureSize = Vector3Int.zero;

    [Header("Debug")]
    [Tooltip("是否顯示粒子顯示範圍的 Gizmo 線框。")]
    [SerializeField] private bool drawBoundsGizmo = true;

    private ParticleSystem.Particle[] _particles;
    private Vector3[] _velocityData;
    private int _dataGridX;
    private int _dataGridY;
    private int _dataGridZ;
    private bool _hasValidReadback;
    private bool _readbackPending;
    private float _nextReadbackTime;
    private bool _loggedReadbackSupportWarning;
    private bool _loggedLowPrecisionWarning;
    private bool _loggedMissingSimulationWarning;
    private bool _loggedSimulationNotInitializedWarning;
    private bool _loggedVelocityTextureNullWarning;
    private bool _loggedVelocityTextureNotCreatedWarning;
    private bool _loggedReadbackErrorWarning;
    private bool _loggedUnsupportedFormatWarning;
    private bool _loggedZeroVelocityWarning;
    private bool _loggedWeakVelocityHint;
    private bool _loggedBoundsMismatchWarning;

    private void Reset()
    {
        targetParticleSystem = GetComponent<ParticleSystem>();
        simulation = FindSimulationInScene();
    }

    private void OnEnable()
    {
        EnsureReferences();
        ConfigureParticleSystem();
        ResetDebugState();
        RebuildParticles(forceReset: true);
    }

    private void OnDisable()
    {
        _readbackPending = false;
    }

    private void OnValidate()
    {
        particleCount = Mathf.Max(1, particleCount);
        particleLifetime = Mathf.Max(0.1f, particleLifetime);
        speedMultiplier = Mathf.Max(0f, speedMultiplier);
        particleSize = Mathf.Max(0.005f, particleSize);
        readbackInterval = Mathf.Max(0.05f, readbackInterval);
        nonZeroVelocityThreshold = Mathf.Max(0.000001f, nonZeroVelocityThreshold);
        debugWindSpeed = Mathf.Max(0f, debugWindSpeed);
        customBoundsSize.x = Mathf.Max(0.01f, customBoundsSize.x);
        customBoundsSize.y = Mathf.Max(0.01f, customBoundsSize.y);
        customBoundsSize.z = Mathf.Max(0.01f, customBoundsSize.z);

        EnsureReferences();
        ConfigureParticleSystem();
        RebuildParticles(forceReset: true);
    }

    private void Update()
    {
        if (!Application.isPlaying && !updateInEditMode)
            return;

        EnsureReferences();
        ConfigureParticleSystem();
        UpdateBoundsDiagnostics();
        TryRequestVelocityReadback();
        SimulateParticles(GetFrameDeltaTime());
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawBoundsGizmo)
            return;

        Bounds bounds = GetActiveBounds();
        Gizmos.color = new Color(0.5f, 0.85f, 1f, 0.9f);
        Gizmos.DrawWireCube(bounds.center, bounds.size);
    }

    [ContextMenu("Reset Particles")]
    public void ResetParticles()
    {
        EnsureReferences();
        ConfigureParticleSystem();
        RebuildParticles(forceReset: true);
    }

    private void EnsureReferences()
    {
        if (targetParticleSystem == null)
            targetParticleSystem = GetComponent<ParticleSystem>();

        if (simulation == null && autoFindSimulation)
            simulation = FindSimulationInScene();
    }

    private void ConfigureParticleSystem()
    {
        if (targetParticleSystem == null)
            return;

        var main = targetParticleSystem.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = particleCount;
        main.startLifetime = particleLifetime;
        main.startSpeed = 0f;
        main.startSize = particleSize;
        main.startColor = particleColor;
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;

        var emission = targetParticleSystem.emission;
        emission.enabled = false;

        var shape = targetParticleSystem.shape;
        shape.enabled = false;

        if (!targetParticleSystem.isPlaying)
            targetParticleSystem.Play();
    }

    private void ResetDebugState()
    {
        _nextReadbackTime = 0f;
        _readbackPending = false;
        _hasValidReadback = false;
        isVelocityReadbackValid = false;
        lastReadbackStatus = "No readback requested yet.";
        lastAverageVelocity = Vector3.zero;
        lastMaxVelocity = 0f;
        lastNonZeroVelocityCount = 0;
        velocityTextureFormat = "Unknown";
        velocityTextureSize = Vector3Int.zero;

        _loggedReadbackSupportWarning = false;
        _loggedLowPrecisionWarning = false;
        _loggedMissingSimulationWarning = false;
        _loggedSimulationNotInitializedWarning = false;
        _loggedVelocityTextureNullWarning = false;
        _loggedVelocityTextureNotCreatedWarning = false;
        _loggedReadbackErrorWarning = false;
        _loggedUnsupportedFormatWarning = false;
        _loggedZeroVelocityWarning = false;
        _loggedWeakVelocityHint = false;
        _loggedBoundsMismatchWarning = false;
    }

    private void RebuildParticles(bool forceReset)
    {
        if (targetParticleSystem == null)
            return;

        if (_particles == null || _particles.Length != particleCount)
        {
            _particles = new ParticleSystem.Particle[particleCount];
            forceReset = true;
        }

        if (!forceReset)
            return;

        Bounds bounds = GetActiveBounds();
        for (int i = 0; i < _particles.Length; i++)
        {
            RespawnParticle(ref _particles[i], bounds, randomizeInitialLifetime);
        }

        targetParticleSystem.SetParticles(_particles, _particles.Length);
    }

    private void TryRequestVelocityReadback()
    {
        if (debugWindMode != DebugWindMode.None)
        {
            _hasValidReadback = false;
            isVelocityReadbackValid = false;
            lastReadbackStatus = $"Debug wind mode active: {debugWindMode}";
            return;
        }

        if (simulation == null)
        {
            _hasValidReadback = false;
            isVelocityReadbackValid = false;
            lastReadbackStatus = "Simulation reference is null.";
            LogOnce(ref _loggedMissingSimulationWarning, "[WindFlowParticleVisualizer] simulation == null. Assign ClassroomHeatSimulation or enable autoFindSimulation.");
            return;
        }
        _loggedMissingSimulationWarning = false;

        if (!simulation.IsInitialized)
        {
            _hasValidReadback = false;
            isVelocityReadbackValid = false;
            lastReadbackStatus = "Simulation exists but is not initialized.";
            LogOnce(ref _loggedSimulationNotInitializedWarning, "[WindFlowParticleVisualizer] simulation.IsInitialized == false. VelocityTexture is not ready yet.");
            return;
        }
        _loggedSimulationNotInitializedWarning = false;

        if (_readbackPending)
            return;

        if (Time.realtimeSinceStartup < _nextReadbackTime)
            return;

        RenderTexture velocityTexture = simulation.VelocityTexture;
        if (velocityTexture == null)
        {
            _hasValidReadback = false;
            isVelocityReadbackValid = false;
            velocityTextureFormat = "Null";
            velocityTextureSize = Vector3Int.zero;
            lastReadbackStatus = "VelocityTexture is null.";
            LogOnce(ref _loggedVelocityTextureNullWarning, "[WindFlowParticleVisualizer] simulation.VelocityTexture == null.");
            return;
        }
        _loggedVelocityTextureNullWarning = false;

        velocityTextureFormat = velocityTexture.format.ToString();
        velocityTextureSize = new Vector3Int(velocityTexture.width, velocityTexture.height, velocityTexture.volumeDepth);

        if (!velocityTexture.IsCreated())
        {
            _hasValidReadback = false;
            isVelocityReadbackValid = false;
            lastReadbackStatus = "VelocityTexture exists but IsCreated() is false.";
            LogOnce(ref _loggedVelocityTextureNotCreatedWarning, "[WindFlowParticleVisualizer] VelocityTexture exists but has not been created yet.");
            return;
        }
        _loggedVelocityTextureNotCreatedWarning = false;

        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            _hasValidReadback = false;
            isVelocityReadbackValid = false;
            lastReadbackStatus = "AsyncGPUReadback is not supported on this device.";
            LogOnce(ref _loggedReadbackSupportWarning, "[WindFlowParticleVisualizer] AsyncGPUReadback is not supported on this device. Particle flow visualization is disabled.");
            return;
        }
        _loggedReadbackSupportWarning = false;

        _readbackPending = true;
        _nextReadbackTime = Time.realtimeSinceStartup + readbackInterval;
        lastReadbackStatus = "Velocity readback requested.";
        AsyncGPUReadback.Request(velocityTexture, 0, request => HandleVelocityReadback(request, velocityTexture));
    }

    private void HandleVelocityReadback(AsyncGPUReadbackRequest request, RenderTexture sourceTexture)
    {
        _readbackPending = false;

        if (this == null || !isActiveAndEnabled)
            return;

        if (simulation == null || sourceTexture == null)
            return;

        if (request.hasError)
        {
            _hasValidReadback = false;
            isVelocityReadbackValid = false;
            lastReadbackStatus = "AsyncGPUReadback request.hasError == true.";
            LogOnce(ref _loggedReadbackErrorWarning, "[WindFlowParticleVisualizer] AsyncGPUReadback failed while reading VelocityTexture.");
            return;
        }
        _loggedReadbackErrorWarning = false;

        int voxelCount = sourceTexture.width * sourceTexture.height * sourceTexture.volumeDepth;
        EnsureVelocityBuffer(voxelCount);

        bool decoded = DecodeVelocityTextureData(request, sourceTexture, voxelCount);
        if (!decoded)
            return;

        _dataGridX = sourceTexture.width;
        _dataGridY = sourceTexture.height;
        _dataGridZ = sourceTexture.volumeDepth;
        _hasValidReadback = true;
        isVelocityReadbackValid = true;

        ComputeVelocityDiagnostics(voxelCount);
        lastReadbackStatus = $"Velocity readback succeeded. Non-zero cells: {lastNonZeroVelocityCount}/{voxelCount}, max speed: {lastMaxVelocity:F5}.";

        if (lastMaxVelocity < 0.0001f)
        {
            LogOnce(ref _loggedZeroVelocityWarning, "[WindFlowParticleVisualizer] Velocity readback succeeded, but velocity field is almost zero. Particles will not move.");
        }
        else
        {
            _loggedZeroVelocityWarning = false;
        }

        if (lastMaxVelocity > 0f && lastMaxVelocity < 0.01f)
        {
            LogOnce(ref _loggedWeakVelocityHint, "[WindFlowParticleVisualizer] Velocity field is valid but very weak. Increase speedMultiplier to roughly 2 to 10 if the motion is hard to see.");
        }
        else
        {
            _loggedWeakVelocityHint = false;
        }
    }

    private bool DecodeVelocityTextureData(AsyncGPUReadbackRequest request, RenderTexture sourceTexture, int voxelCount)
    {
        switch (sourceTexture.format)
        {
            case RenderTextureFormat.ARGBFloat:
            {
                var data = request.GetData<Color>();
                if (!data.IsCreated || data.Length != voxelCount)
                {
                    MarkReadbackInvalid("VelocityTexture ARGBFloat data length mismatch.");
                    return false;
                }

                for (int i = 0; i < voxelCount; i++)
                {
                    Color sample = data[i];
                    _velocityData[i] = new Vector3(sample.r, sample.g, sample.b);
                }
                return true;
            }
            case RenderTextureFormat.ARGBHalf:
            {
                var data = request.GetData<ushort>();
                if (!data.IsCreated || data.Length != voxelCount * 4)
                {
                    MarkReadbackInvalid("VelocityTexture ARGBHalf data length mismatch.");
                    return false;
                }

                for (int i = 0, j = 0; i < voxelCount; i++, j += 4)
                {
                    _velocityData[i] = new Vector3(
                        HalfToFloat(data[j]),
                        HalfToFloat(data[j + 1]),
                        HalfToFloat(data[j + 2]));
                }
                return true;
            }
            case RenderTextureFormat.ARGB32:
            {
                var data = request.GetData<Color32>();
                if (!data.IsCreated || data.Length != voxelCount)
                {
                    MarkReadbackInvalid("VelocityTexture ARGB32 data length mismatch.");
                    return false;
                }

                LogOnce(ref _loggedLowPrecisionWarning, "[WindFlowParticleVisualizer] VelocityTexture is using ARGB32 format. Negative or high-range velocity components may be clamped, so particle flow may not be physically accurate.");

                for (int i = 0; i < voxelCount; i++)
                {
                    Color32 sample = data[i];
                    _velocityData[i] = new Vector3(
                        sample.r / 255f,
                        sample.g / 255f,
                        sample.b / 255f);
                }
                return true;
            }
            default:
            {
                lastReadbackStatus = $"Unsupported VelocityTexture format: {sourceTexture.format}.";
                LogOnce(ref _loggedUnsupportedFormatWarning, $"[WindFlowParticleVisualizer] Unsupported VelocityTexture format {sourceTexture.format}. Particle flow visualization is disabled.");
                _hasValidReadback = false;
                isVelocityReadbackValid = false;
                return false;
            }
        }
    }

    private void ComputeVelocityDiagnostics(int voxelCount)
    {
        Vector3 velocitySum = Vector3.zero;
        float maxSpeed = 0f;
        int nonZeroCount = 0;

        for (int i = 0; i < voxelCount; i++)
        {
            Vector3 v = _velocityData[i];
            float speed = v.magnitude;
            velocitySum += v;
            if (speed > maxSpeed)
                maxSpeed = speed;

            if (speed > nonZeroVelocityThreshold)
                nonZeroCount++;
        }

        lastAverageVelocity = voxelCount > 0 ? velocitySum / voxelCount : Vector3.zero;
        lastMaxVelocity = maxSpeed;
        lastNonZeroVelocityCount = nonZeroCount;
    }

    private void SimulateParticles(float deltaTime)
    {
        if (targetParticleSystem == null || _particles == null || _particles.Length == 0)
            return;

        int liveCount = targetParticleSystem.GetParticles(_particles);
        if (liveCount == 0)
        {
            RebuildParticles(forceReset: true);
            liveCount = targetParticleSystem.GetParticles(_particles);
        }

        Bounds respawnBounds = GetActiveBounds();
        bool hasVelocityField = debugWindMode != DebugWindMode.None ||
                                (_hasValidReadback && simulation != null && simulation.IsInitialized);

        for (int i = 0; i < _particles.Length; i++)
        {
            ParticleSystem.Particle particle = _particles[i];

            if (i >= liveCount || particle.remainingLifetime <= 0f)
            {
                RespawnParticle(ref particle, respawnBounds, randomizeLifetime: false);
                _particles[i] = particle;
                continue;
            }

            Vector3 sampledVelocity = hasVelocityField
                ? EvaluateVelocityAtParticle(particle.position, respawnBounds)
                : Vector3.zero;

            Vector3 worldVelocity = sampledVelocity * speedMultiplier;
            particle.position += worldVelocity * deltaTime;
            particle.velocity = worldVelocity;

            if (!respawnBounds.Contains(particle.position))
            {
                RespawnParticle(ref particle, respawnBounds, randomizeLifetime: false);
            }

            _particles[i] = particle;
        }

        targetParticleSystem.SetParticles(_particles, _particles.Length);
    }

    private Vector3 EvaluateVelocityAtParticle(Vector3 particleWorldPosition, Bounds activeBounds)
    {
        switch (debugWindMode)
        {
            case DebugWindMode.ConstantDirection:
                return EvaluateConstantDirectionWind();
            case DebugWindMode.Swirl:
                return EvaluateSwirlWind(particleWorldPosition, activeBounds);
            case DebugWindMode.Updraft:
                return EvaluateUpdraftWind(particleWorldPosition, activeBounds);
            default:
                return SampleVelocityWorld(particleWorldPosition);
        }
    }

    private Vector3 EvaluateConstantDirectionWind()
    {
        Vector3 direction = debugWindDirection.sqrMagnitude > 1e-6f ? debugWindDirection.normalized : Vector3.right;
        return direction * debugWindSpeed;
    }

    private Vector3 EvaluateSwirlWind(Vector3 particleWorldPosition, Bounds bounds)
    {
        Vector3 axis = debugSwirlAxis.sqrMagnitude > 1e-6f ? debugSwirlAxis.normalized : Vector3.up;
        Vector3 radial = particleWorldPosition - bounds.center;
        Vector3 tangent = Vector3.Cross(axis, radial);
        if (tangent.sqrMagnitude <= 1e-6f)
            tangent = Vector3.Cross(axis, Vector3.right);

        return tangent.normalized * debugWindSpeed;
    }

    private Vector3 EvaluateUpdraftWind(Vector3 particleWorldPosition, Bounds bounds)
    {
        Vector3 toCenter = bounds.center - particleWorldPosition;
        Vector3 horizontalPull = new Vector3(toCenter.x, 0f, toCenter.z);
        Vector3 horizontal = horizontalPull.sqrMagnitude > 1e-6f ? horizontalPull.normalized * (debugWindSpeed * 0.35f) : Vector3.zero;
        return Vector3.up * debugWindSpeed + horizontal;
    }

    private void RespawnParticle(ref ParticleSystem.Particle particle, Bounds bounds, bool randomizeLifetime)
    {
        Vector3 position = GetRespawnPosition(bounds);
        float lifetime = randomizeLifetime ? Random.Range(0.05f, particleLifetime) : particleLifetime;

        particle.position = position;
        particle.velocity = Vector3.zero;
        particle.startLifetime = particleLifetime;
        particle.remainingLifetime = lifetime;
        particle.startSize = particleSize;
        particle.startColor = particleColor;
        particle.randomSeed = (uint)Random.Range(1, int.MaxValue);
    }

    private Vector3 GetRespawnPosition(Bounds bounds)
    {
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;
        Vector3 position = bounds.center;

        switch (respawnMode)
        {
            case RespawnMode.XMinFace:
                position.x = min.x;
                position.y = Random.Range(min.y, max.y);
                position.z = Random.Range(min.z, max.z);
                break;
            case RespawnMode.XMaxFace:
                position.x = max.x;
                position.y = Random.Range(min.y, max.y);
                position.z = Random.Range(min.z, max.z);
                break;
            case RespawnMode.YMinFace:
                position.x = Random.Range(min.x, max.x);
                position.y = min.y;
                position.z = Random.Range(min.z, max.z);
                break;
            case RespawnMode.YMaxFace:
                position.x = Random.Range(min.x, max.x);
                position.y = max.y;
                position.z = Random.Range(min.z, max.z);
                break;
            case RespawnMode.ZMinFace:
                position.x = Random.Range(min.x, max.x);
                position.y = Random.Range(min.y, max.y);
                position.z = min.z;
                break;
            case RespawnMode.ZMaxFace:
                position.x = Random.Range(min.x, max.x);
                position.y = Random.Range(min.y, max.y);
                position.z = max.z;
                break;
            default:
                position.x = Random.Range(min.x, max.x);
                position.y = Random.Range(min.y, max.y);
                position.z = Random.Range(min.z, max.z);
                break;
        }

        return position;
    }

    private Bounds GetActiveBounds()
    {
        if (displayBoundsMode == DisplayBoundsMode.SimulationBounds && simulation != null)
            return simulation.GetSimulationBounds();

        Vector3 center;
        Vector3 size = customBoundsSize;
        if (customBoundsFollowTransform)
        {
            center = transform.TransformPoint(customBoundsCenterOffset);
            size = Vector3.Scale(customBoundsSize, AbsVector3(transform.lossyScale));
        }
        else
        {
            center = customBoundsCenterOffset;
        }

        return new Bounds(center, size);
    }

    private void UpdateBoundsDiagnostics()
    {
        if (simulation == null || displayBoundsMode != DisplayBoundsMode.CustomBounds)
        {
            _loggedBoundsMismatchWarning = false;
            return;
        }

        Bounds simulationBounds = simulation.GetSimulationBounds();
        Bounds displayBounds = GetActiveBounds();
        if (!simulationBounds.Intersects(displayBounds))
        {
            string message = useDisplayBoundsForVelocitySampling
                ? "[WindFlowParticleVisualizer] Display bounds do not overlap simulation bounds. Sampling is being remapped with useDisplayBoundsForVelocitySampling enabled."
                : "[WindFlowParticleVisualizer] Display bounds do not overlap simulation bounds. Particles outside simulation bounds will sample zero velocity. Either move the bounds or enable useDisplayBoundsForVelocitySampling.";
            LogOnce(ref _loggedBoundsMismatchWarning, message);
        }
        else
        {
            _loggedBoundsMismatchWarning = false;
        }
    }

    private Vector3 SampleVelocityWorld(Vector3 worldPosition)
    {
        if (!_hasValidReadback || simulation == null || _velocityData == null || _velocityData.Length == 0)
            return Vector3.zero;

        Bounds samplingBounds = useDisplayBoundsForVelocitySampling
            ? GetActiveBounds()
            : simulation.GetSimulationBounds();

        Vector3 min = samplingBounds.min;
        Vector3 max = samplingBounds.max;

        if (worldPosition.x < min.x || worldPosition.x > max.x ||
            worldPosition.y < min.y || worldPosition.y > max.y ||
            worldPosition.z < min.z || worldPosition.z > max.z)
        {
            return Vector3.zero;
        }

        float px = Mathf.Clamp(
            Mathf.InverseLerp(min.x, max.x, worldPosition.x) * _dataGridX - 0.5f,
            0f,
            Mathf.Max(0f, _dataGridX - 1f));
        float py = Mathf.Clamp(
            Mathf.InverseLerp(min.y, max.y, worldPosition.y) * _dataGridY - 0.5f,
            0f,
            Mathf.Max(0f, _dataGridY - 1f));
        float pz = Mathf.Clamp(
            Mathf.InverseLerp(min.z, max.z, worldPosition.z) * _dataGridZ - 0.5f,
            0f,
            Mathf.Max(0f, _dataGridZ - 1f));

        int x0 = Mathf.FloorToInt(px);
        int y0 = Mathf.FloorToInt(py);
        int z0 = Mathf.FloorToInt(pz);
        int x1 = Mathf.Min(x0 + 1, _dataGridX - 1);
        int y1 = Mathf.Min(y0 + 1, _dataGridY - 1);
        int z1 = Mathf.Min(z0 + 1, _dataGridZ - 1);

        float tx = px - x0;
        float ty = py - y0;
        float tz = pz - z0;

        Vector3 c000 = GetVelocityCell(x0, y0, z0);
        Vector3 c100 = GetVelocityCell(x1, y0, z0);
        Vector3 c010 = GetVelocityCell(x0, y1, z0);
        Vector3 c110 = GetVelocityCell(x1, y1, z0);
        Vector3 c001 = GetVelocityCell(x0, y0, z1);
        Vector3 c101 = GetVelocityCell(x1, y0, z1);
        Vector3 c011 = GetVelocityCell(x0, y1, z1);
        Vector3 c111 = GetVelocityCell(x1, y1, z1);

        Vector3 c00 = Vector3.LerpUnclamped(c000, c100, tx);
        Vector3 c10 = Vector3.LerpUnclamped(c010, c110, tx);
        Vector3 c01 = Vector3.LerpUnclamped(c001, c101, tx);
        Vector3 c11 = Vector3.LerpUnclamped(c011, c111, tx);
        Vector3 c0 = Vector3.LerpUnclamped(c00, c10, ty);
        Vector3 c1 = Vector3.LerpUnclamped(c01, c11, ty);
        return Vector3.LerpUnclamped(c0, c1, tz);
    }

    private Vector3 GetVelocityCell(int x, int y, int z)
    {
        int index = x + _dataGridX * (y + _dataGridY * z);
        if (_velocityData == null || index < 0 || index >= _velocityData.Length)
            return Vector3.zero;

        return _velocityData[index];
    }

    private void EnsureVelocityBuffer(int voxelCount)
    {
        if (_velocityData == null || _velocityData.Length != voxelCount)
            _velocityData = new Vector3[voxelCount];
    }

    private void MarkReadbackInvalid(string reason)
    {
        _hasValidReadback = false;
        isVelocityReadbackValid = false;
        lastReadbackStatus = reason;
    }

    private static void LogOnce(ref bool hasLogged, string message)
    {
        if (hasLogged)
            return;

        Debug.LogWarning(message);
        hasLogged = true;
    }

    private static float GetFrameDeltaTime()
    {
        if (Application.isPlaying)
            return Mathf.Max(Time.deltaTime, 0.0001f);

        return 1f / 60f;
    }

    private static Vector3 AbsVector3(Vector3 value)
    {
        return new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
    }

    private static float HalfToFloat(ushort halfValue)
    {
        int sign = (halfValue >> 15) & 0x1;
        int exponent = (halfValue >> 10) & 0x1F;
        int mantissa = halfValue & 0x3FF;
        float signMultiplier = sign == 0 ? 1f : -1f;

        if (exponent == 0)
        {
            if (mantissa == 0)
                return sign == 0 ? 0f : -0f;

            return signMultiplier * Mathf.Pow(2f, -14f) * (mantissa / 1024f);
        }

        if (exponent == 31)
        {
            if (mantissa == 0)
                return sign == 0 ? float.PositiveInfinity : float.NegativeInfinity;

            return float.NaN;
        }

        return signMultiplier * Mathf.Pow(2f, exponent - 15f) * (1f + mantissa / 1024f);
    }

    private static ClassroomHeatSimulation FindSimulationInScene()
    {
#if UNITY_2023_1_OR_NEWER
        return FindFirstObjectByType<ClassroomHeatSimulation>();
#else
        return FindObjectOfType<ClassroomHeatSimulation>();
#endif
    }
}
