/*
 * Created :    Summer 2026
 * Author :     蘇家賢
 * Project :    General-3D-Spatial-Thermal-Flow-Simulator
 * Filename :   ClassroomHeatSimulation.cs
 * 
 */

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using System.Collections.Generic;
using System;

[ExecuteAlways]
public class ClassroomHeatSimulation : MonoBehaviour
{
    // 這個元件負責整個教室熱模擬流程：
    // 1) 溫度擴散與平流
    // 2) 速度場（動量方程）更新
    // 3) 不可壓縮壓力投影（divergence-free）
    // 4) 對外提供熱源/冷源/障礙物/局部風速注入 API

    [Header("Compute Shader")]
    [Tooltip("執行熱傳、風場與壓力投影的 Compute Shader。")]
    [SerializeField] private ComputeShader heatSimulationShader;

    [Header("Grid Resolution")]
    [Tooltip("模擬網格在 X 軸的離散格數。")]
    [SerializeField] private int gridX = 32;
    [Tooltip("模擬網格在 Y 軸的離散格數。")]
    [SerializeField] private int gridY = 12;
    [Tooltip("模擬網格在 Z 軸的離散格數。")]
    [SerializeField] private int gridZ = 24;

    [Header("Simulation Volume (World Space)")]
    [Tooltip("整個模擬盒在世界空間中的尺寸。")]
    [SerializeField] private Vector3 simulationSize = new Vector3(8f, 3f, 6f);
    [Tooltip("模擬盒中心相對於此物件 Transform 的位移。")]
    [SerializeField] private Vector3 simulationCenterOffset = new Vector3(0f, 1.5f, 0f);

    [Header("Simulation Parameters")]
    [Tooltip("是否執行熱模擬更新。")]
    [SerializeField] private bool runSimulation = true;
    [Tooltip("每幀執行幾個模擬步驟。")]
    [SerializeField] private int simulationStepsPerFrame = 1;
    [Tooltip("單一步驟使用的模擬時間秒數。")]
    [SerializeField] private float simulationDeltaTime = 0.02f;
    [Tooltip("溫度擴散係數，越大表示熱傳導越快。")]
    [SerializeField] private float diffusion = 0.12f;
    [FormerlySerializedAs("cooling")]
    [Tooltip("溫度朝環境溫度回復的交換速率。")]
    [SerializeField] private float ambientExchangeRate = 0.08f;
    [Tooltip("環境基準溫度。")]
    [SerializeField] private float ambientTemperature = 24f;
    [Tooltip("空氣密度，用於功率轉溫升與浮力估算。")]
    [SerializeField] private float airDensity = 1.2f;
    [Tooltip("空氣比熱容，用於功率轉溫升與浮力估算。")]
    [SerializeField] private float airSpecificHeatCapacity = 1005f;

    [Header("Momentum Equation Parameters")]
    [Tooltip("速度場黏滯係數，越大表示風更容易被抹平。")]
    [SerializeField] private float velocityViscosity = 0.08f;
    [Tooltip("速度阻尼係數，用於逐步衰減風速。")]
    [SerializeField] private float velocityDamping = 0.15f;
    [Tooltip("溫差轉成浮力的強度倍率。")]
    [SerializeField] private float buoyancyStrength = 0.02f;
    [Tooltip("浮力計算使用的重力加速度。")]
    [SerializeField] private float gravity = 9.81f;

    [Header("Pressure Projection (Incompressible)")]
    [Tooltip("每次壓力投影的 Jacobi 迭代次數。")]
    [SerializeField, Range(1, 120)] private int pressureIterations = 30;
    [Tooltip("壓力 Jacobi 迭代的鬆弛係數。")]
    [SerializeField, Range(0.1f, 1.9f)] private float pressureRelaxation = 1.0f;
    [Tooltip("是否在殘差過大時自動追加壓力投影。")]
    [SerializeField] private bool enableAdaptivePressureProjection = true;
    [Tooltip("額外壓力投影最多再補幾輪。")]
    [SerializeField, Range(0, 4)] private int maxAdaptiveProjectionPasses = 1;
    [Tooltip("可接受的最大散度殘差門檻。")]
    [SerializeField, Min(0f)] private float divergenceTolerance = 0.05f;
    [Tooltip("壓力投影未收斂時是否輸出警告。")]
    [SerializeField] private bool logPressureConvergenceWarnings = true;

    [Header("Debug / Initialization")]
    [Tooltip("進入播放模式時是否重建並重設整個模擬。")]
    [SerializeField] private bool reinitializeOnPlay = true;
    [Tooltip("非播放模式下是否也執行模擬。")]
    [SerializeField] private bool simulateInEditMode = false;
    [Tooltip("每幀是否先清空熱源貼圖再重新套用。")]
    [SerializeField] private bool clearHeatSourceEachFrame = false;
    [Tooltip("是否繪製模擬範圍與除錯 Gizmo。")]
    [SerializeField] private bool drawGizmos = true;

    [Header("Optional Global Airflow")]
    [Tooltip("整個模擬空間的預設背景風速。")]
    [SerializeField] private Vector3 globalAirVelocity = new Vector3(0.2f, 0f, 0f);

    [Header("Solver Safety")]
    [Tooltip("允許建立的最大網格總格數，避免記憶體過量使用。")]
    [SerializeField, Min(64)] private int maxGridCellCount = 262144;
    [Tooltip("每幀最多允許拆成多少個內部穩定子步驟。")]
    [SerializeField, Min(1)] private int maxInternalSolverSubstepsPerFrame = 24;
    [Tooltip("單一內部子步驟允許的最大時間長度。")]
    [SerializeField, Min(0.0001f)] private float maxSolverStepDt = 0.02f;
    [Tooltip("溫度場允許的最小夾制值。")]
    [SerializeField] private float minTemperatureClamp = -50f;
    [Tooltip("溫度場允許的最大夾制值。")]
    [SerializeField] private float maxTemperatureClamp = 150f;
    [Tooltip("速度場允許的最大風速上限。")]
    [SerializeField, Min(0.1f)] private float maxVelocityMagnitude = 8f;

    public RenderTexture TemperatureTexture => _temperatureA;
    public RenderTexture HeatSourceTexture => _heatSource;
    public RenderTexture ObstacleTexture => _obstacle;
    public RenderTexture VelocityTexture => _velocity;
    public bool IsInitialized => _initialized;
    public int TemperatureSwapVersion => _temperatureSwapVersion;
    public int ObstacleClearVersion => _obstacleClearVersion;
    public int ObstacleFieldVersion => _obstacleFieldVersion;
    public bool UsesSolverBuoyancy => buoyancyStrength > 1e-6f && Mathf.Abs(gravity) > 1e-6f;

    public int GridX => gridX;
    public int GridY => gridY;
    public int GridZ => gridZ;
    public Vector3 SimulationSize => simulationSize;
    public Vector3 SimulationCenterWorld => transform.position + simulationCenterOffset;

    #region 公式計算 - Cell Size（單一網格尺寸）
    public Vector3 CellSize => new Vector3(
        simulationSize.x / Mathf.Max(1, gridX),
        simulationSize.y / Mathf.Max(1, gridY),
        simulationSize.z / Mathf.Max(1, gridZ)
    );
    #endregion

    private RenderTexture _temperatureA;
    private RenderTexture _temperatureB;
    private RenderTexture _heatSource;
    private RenderTexture _obstacle;
    private RenderTexture _velocity;
    private RenderTexture _velocityB;
    private RenderTexture _pressureA;
    private RenderTexture _pressureB;
    private RenderTexture _divergence;
    private ComputeBuffer _divergenceReductionMaxBuffer;

    private int _kernelHeatStep = -1;
    private int _kernelClearFloat3D = -1;
    private int _kernelFillTemperature = -1;
    private int _kernelFillVelocity = -1;
    private int _kernelVelocityStep = -1;
    private int _kernelComputeDivergence = -1;
    private int _kernelPressureJacobi = -1;
    private int _kernelProjectVelocity = -1;
    private int _kernelPaintSphereFloat = -1;
    private int _kernelPaintSphereFloat4 = -1;
    private int _kernelPaintCellsFloat = -1;
    private int _kernelPaintCellsFloat4 = -1;
    private int _kernelReduceAbsMaxFloat3D = -1;
    private int _kernelCopyTemperatureToBuffer = -1;

    private bool _initialized;
    private int _temperatureSwapVersion;
    private int _obstacleClearVersion;
    private int _obstacleFieldVersion;
    private float _simulationTimeAccumulator;
    private bool _hasLoggedGridLimitWarning;
    private bool _hasLoggedStepClampWarning;
    private bool _hasLoggedPressureConvergenceWarning;

    private RenderTextureFormat _scalarTextureFormat = RenderTextureFormat.RFloat;
    private RenderTextureFormat _vectorTextureFormat = RenderTextureFormat.ARGBFloat;

    private KernelDispatchInfo _dispatchHeatStep;
    private KernelDispatchInfo _dispatchClearFloat3D;
    private KernelDispatchInfo _dispatchFillTemperature;
    private KernelDispatchInfo _dispatchFillVelocity;
    private KernelDispatchInfo _dispatchVelocityStep;
    private KernelDispatchInfo _dispatchComputeDivergence;
    private KernelDispatchInfo _dispatchPressureJacobi;
    private KernelDispatchInfo _dispatchProjectVelocity;
    private KernelDispatchInfo _dispatchPaintSphereFloat;
    private KernelDispatchInfo _dispatchPaintSphereFloat4;
    private KernelDispatchInfo _dispatchReduceAbsMaxFloat3D;

    private const string KERNEL_HEAT_STEP = "HeatStep";
    private const string KERNEL_CLEAR_FLOAT_3D = "ClearFloat3D";
    private const string KERNEL_FILL_TEMPERATURE = "FillTemperature";
    private const string KERNEL_FILL_VELOCITY = "FillVelocity";
    private const string KERNEL_VELOCITY_STEP = "VelocityStep";
    private const string KERNEL_COMPUTE_DIVERGENCE = "ComputeDivergence";
    private const string KERNEL_PRESSURE_JACOBI = "PressureJacobi";
    private const string KERNEL_PROJECT_VELOCITY = "ProjectVelocity";
    private const string KERNEL_PAINT_SPHERE_FLOAT = "PaintSphereFloat";
    private const string KERNEL_PAINT_SPHERE_FLOAT4 = "PaintSphereFloat4";
    private const string KERNEL_PAINT_CELLS_FLOAT = "PaintCellsFloat";
    private const string KERNEL_PAINT_CELLS_FLOAT4 = "PaintCellsFloat4";
    private const string KERNEL_REDUCE_ABS_MAX_FLOAT_3D = "ReduceAbsMaxFloat3D";
    private const string KERNEL_COPY_TEMPERATURE_TO_BUFFER = "CopyTemperatureToBuffer";

    private static readonly int PID_GridX = Shader.PropertyToID("_GridX");
    private static readonly int PID_GridY = Shader.PropertyToID("_GridY");
    private static readonly int PID_GridZ = Shader.PropertyToID("_GridZ");
    private static readonly int PID_DeltaTime = Shader.PropertyToID("_DeltaTime");
    private static readonly int PID_Diffusion = Shader.PropertyToID("_Diffusion");
    private static readonly int PID_AmbientExchangeRate = Shader.PropertyToID("_AmbientExchangeRate");
    private static readonly int PID_AmbientTemperature = Shader.PropertyToID("_AmbientTemperature");
    private static readonly int PID_MinTemperatureClamp = Shader.PropertyToID("_MinTemperatureClamp");
    private static readonly int PID_MaxTemperatureClamp = Shader.PropertyToID("_MaxTemperatureClamp");
    private static readonly int PID_MaxVelocityMagnitude = Shader.PropertyToID("_MaxVelocityMagnitude");
    private static readonly int PID_VelocityViscosity = Shader.PropertyToID("_VelocityViscosity");
    private static readonly int PID_VelocityDamping = Shader.PropertyToID("_VelocityDamping");
    private static readonly int PID_BuoyancyStrength = Shader.PropertyToID("_BuoyancyStrength");
    private static readonly int PID_Gravity = Shader.PropertyToID("_Gravity");
    private static readonly int PID_PressureRelaxation = Shader.PropertyToID("_PressureRelaxation");
    private static readonly int PID_InvCellSize = Shader.PropertyToID("_InvCellSize");
    private static readonly int PID_ClearValue = Shader.PropertyToID("_ClearValue");
    private static readonly int PID_FillValue = Shader.PropertyToID("_FillValue");
    private static readonly int PID_FillVelocity = Shader.PropertyToID("_FillVelocity");
    private static readonly int PID_PaintMode = Shader.PropertyToID("_PaintMode");
    private static readonly int PID_PaintUseSoftFalloff = Shader.PropertyToID("_PaintUseSoftFalloff");
    private static readonly int PID_PaintCenterWorldRadius = Shader.PropertyToID("_PaintCenterWorldRadius");
    private static readonly int PID_PaintScalar = Shader.PropertyToID("_PaintScalar");
    private static readonly int PID_PaintVector = Shader.PropertyToID("_PaintVector");
    private static readonly int PID_SimulationBoundsMin = Shader.PropertyToID("_SimulationBoundsMin");
    private static readonly int PID_SimulationBoundsMax = Shader.PropertyToID("_SimulationBoundsMax");
    private static readonly int PID_PaintCells = Shader.PropertyToID("_PaintCells");
    private static readonly int PID_PaintCellCount = Shader.PropertyToID("_PaintCellCount");

    private static readonly int PID_TemperatureTex = Shader.PropertyToID("_TemperatureTex");
    private static readonly int PID_TemperatureNextTex = Shader.PropertyToID("_TemperatureNextTex");
    private static readonly int PID_HeatSourceTex = Shader.PropertyToID("_HeatSourceTex");
    private static readonly int PID_ObstacleTex = Shader.PropertyToID("_ObstacleTex");
    private static readonly int PID_VelocityTex = Shader.PropertyToID("_VelocityTex");
    private static readonly int PID_VelocityNextTex = Shader.PropertyToID("_VelocityNextTex");
    private static readonly int PID_DivergenceTex = Shader.PropertyToID("_DivergenceTex");
    private static readonly int PID_PressureTex = Shader.PropertyToID("_PressureTex");
    private static readonly int PID_PressureNextTex = Shader.PropertyToID("_PressureNextTex");
    private static readonly int PID_ResultFloatTex3D = Shader.PropertyToID("_ResultFloatTex3D");
    private static readonly int PID_ResultFloat4Tex3D = Shader.PropertyToID("_ResultFloat4Tex3D");
    private static readonly int PID_ReductionMaxBuffer = Shader.PropertyToID("_ReductionMaxBuffer");
    private static readonly int PID_TemperatureReadbackBuffer = Shader.PropertyToID("_TemperatureReadbackBuffer");

    private static readonly int MID_TemperatureTex3D = Shader.PropertyToID("_TemperatureTex3D");
    private static readonly int MID_VelocityTex3D = Shader.PropertyToID("_VelocityTex3D");
    private static readonly int MID_SimulationBoundsMin = Shader.PropertyToID("_SimulationBoundsMin");
    private static readonly int MID_SimulationBoundsMax = Shader.PropertyToID("_SimulationBoundsMax");
    private static readonly int MID_GridSize = Shader.PropertyToID("_GridSize");
    private static readonly int MID_AmbientTemperature = Shader.PropertyToID("_AmbientTemperature");

    // 每個 Kernel 的 Dispatch 群組資訊，避免每次執行都重新查詢。
    private struct KernelDispatchInfo
    {
        public int gx;
        public int gy;
        public int gz;
        public bool valid;
    }

    private struct GridCellPaintData
    {
        public int x;
        public int y;
        public int z;
        public int w;

        public GridCellPaintData(Vector3Int cell)
        {
            x = cell.x;
            y = cell.y;
            z = cell.z;
            w = 0;
        }
    }

    private readonly List<Vector3Int> _colliderObstacleCells = new List<Vector3Int>(1024);
    private readonly List<Vector3Int> _powerSourceCells = new List<Vector3Int>(256);
    private readonly List<Vector3Int> _boxPaintCells = new List<Vector3Int>(256);
    private readonly uint[] _divergenceReductionReadback = new uint[1];

    private void OnEnable()
    {
        if (ShouldRunInCurrentMode())
        {
            InitializeIfNeeded(forceRecreate: true);
        }
    }

    private void Start()
    {
        if (Application.isPlaying && reinitializeOnPlay)
        {
            InitializeIfNeeded(forceRecreate: true);
            ResetSimulation();
        }
    }

    private void OnDisable()
    {
        ReleaseTextures();
        ReleaseBuffers();
        _initialized = false;
    }

    private void OnValidate()
    {
        gridX = Mathf.Max(4, gridX);
        gridY = Mathf.Max(4, gridY);
        gridZ = Mathf.Max(4, gridZ);

        simulationSize.x = Mathf.Max(0.1f, simulationSize.x);
        simulationSize.y = Mathf.Max(0.1f, simulationSize.y);
        simulationSize.z = Mathf.Max(0.1f, simulationSize.z);

        simulationStepsPerFrame = Mathf.Max(1, simulationStepsPerFrame);
        simulationDeltaTime = Mathf.Max(0.0001f, simulationDeltaTime);
        diffusion = Mathf.Max(0f, diffusion);
        ambientExchangeRate = Mathf.Max(0f, ambientExchangeRate);
        airDensity = Mathf.Max(0.01f, airDensity);
        airSpecificHeatCapacity = Mathf.Max(1f, airSpecificHeatCapacity);
        velocityViscosity = Mathf.Max(0f, velocityViscosity);
        velocityDamping = Mathf.Max(0f, velocityDamping);
        pressureIterations = Mathf.Max(1, pressureIterations);
        pressureRelaxation = Mathf.Clamp(pressureRelaxation, 0.1f, 1.9f);
        maxAdaptiveProjectionPasses = Mathf.Clamp(maxAdaptiveProjectionPasses, 0, 4);
        divergenceTolerance = Mathf.Max(0f, divergenceTolerance);
        maxGridCellCount = Mathf.Max(64, maxGridCellCount);
        maxInternalSolverSubstepsPerFrame = Mathf.Max(1, maxInternalSolverSubstepsPerFrame);
        maxSolverStepDt = Mathf.Max(0.0001f, maxSolverStepDt);
        maxVelocityMagnitude = Mathf.Max(0.1f, maxVelocityMagnitude);
        if (maxTemperatureClamp < minTemperatureClamp)
            maxTemperatureClamp = minTemperatureClamp;

        if (ShouldRunInCurrentMode())
        {
            InitializeIfNeeded(forceRecreate: true);
        }
    }

    private void Update()
    {
        if (!ShouldRunInCurrentMode())
            return;

        if (!_initialized)
        {
            InitializeIfNeeded(forceRecreate: true);
            if (!_initialized)
                return;
        }

        if (!runSimulation || heatSimulationShader == null || simulationStepsPerFrame <= 0)
            return;

        float requestedTotalDt = simulationDeltaTime * simulationStepsPerFrame;

        if (Application.isPlaying)
        {
            // 依實際經過時間累積固定 dt 子步進，避免模擬速度直接綁定渲染幀率。
            _simulationTimeAccumulator = Mathf.Min(_simulationTimeAccumulator + Time.deltaTime, requestedTotalDt);
            requestedTotalDt = _simulationTimeAccumulator;
            if (requestedTotalDt < simulationDeltaTime)
                return;
        }

        if (!TryBuildSolverSchedule(requestedTotalDt, out int stepCount, out float stepDt, out float simulatedTotalDt))
            return;

        // 子步進（substeps）：
        // 先更新速度，再做壓力投影，最後用更新後速度搬運溫度。
        for (int i = 0; i < stepCount; i++)
        {
            PrepareMomentumStepConstants(stepDt);
            DispatchVelocityStepOnce();
            DispatchPressureProjection(stepDt);

            PrepareHeatStepConstants(stepDt);
            DispatchHeatStepOnce();
        }

        if (Application.isPlaying)
            _simulationTimeAccumulator = Mathf.Max(0f, _simulationTimeAccumulator - simulatedTotalDt);

        if (clearHeatSourceEachFrame)
        {
            ClearHeatSource();
        }
    }

    #region 初始化與資源建立

    public void InitializeIfNeeded(bool forceRecreate = false)
    {
        if (heatSimulationShader == null)
        {
            _initialized = false;
            Debug.LogWarning("[ClassroomHeatSimulation] Compute shader is not assigned.");
            return;
        }

        if (!CanRunSimulationOnCurrentDevice())
        {
            _initialized = false;
            return;
        }

        bool needsCreate =
            forceRecreate ||
            !_initialized ||
            !AreTexturesValidForGrid();

        if (!needsCreate)
            return;

        ReleaseTextures();
        CacheKernelsAndDispatch();
        CreateTextures();

        if (!AreTexturesReady())
        {
            ReleaseTextures();
            _initialized = false;
            return;
        }

        _initialized = true;
        ResetSimulationInternal();
    }

    private bool ShouldRunInCurrentMode()
    {
        return Application.isPlaying || simulateInEditMode;
    }

    private bool CanRunSimulationOnCurrentDevice()
    {
        int totalCellCount = GetTotalGridCellCount();
        if (totalCellCount > maxGridCellCount)
        {
            if (!_hasLoggedGridLimitWarning)
            {
                Debug.LogError($"[ClassroomHeatSimulation] Grid {gridX}x{gridY}x{gridZ} = {totalCellCount} cells exceeds safety limit {maxGridCellCount}. Reduce resolution or raise the safety limit intentionally.");
                _hasLoggedGridLimitWarning = true;
            }
            return false;
        }

        int max3DSize = SystemInfo.maxTexture3DSize;
        if (gridX > max3DSize || gridY > max3DSize || gridZ > max3DSize)
        {
            Debug.LogError($"[ClassroomHeatSimulation] Grid exceeds max 3D texture size {max3DSize}. Current grid: {gridX}x{gridY}x{gridZ}.");
            return false;
        }

        _hasLoggedGridLimitWarning = false;

        // 需要 Compute Shader 與 3D Texture 支援。
        if (!SystemInfo.supportsComputeShaders)
        {
            Debug.LogError("[ClassroomHeatSimulation] Compute shaders are not supported on this device.");
            return false;
        }

        if (!SystemInfo.supports3DTextures)
        {
            Debug.LogError("[ClassroomHeatSimulation] 3D textures are not supported on this device.");
            return false;
        }

        if (!TrySelectSupportedFormat(
                new[] { RenderTextureFormat.RFloat, RenderTextureFormat.RHalf, RenderTextureFormat.R8 },
                out _scalarTextureFormat))
        {
            Debug.LogError("[ClassroomHeatSimulation] No supported scalar 3D render texture format was found.");
            return false;
        }

        if (!TrySelectSupportedFormat(
                new[] { RenderTextureFormat.ARGBFloat, RenderTextureFormat.ARGBHalf, RenderTextureFormat.ARGB32 },
                out _vectorTextureFormat))
        {
            Debug.LogError("[ClassroomHeatSimulation] No supported vector 3D render texture format was found.");
            return false;
        }

        return true;
    }

    private static bool TrySelectSupportedFormat(RenderTextureFormat[] candidates, out RenderTextureFormat selected)
    {
        // 依序挑第一個可 random write 的格式。
        for (int i = 0; i < candidates.Length; i++)
        {
            if (SupportsRandomWriteFormat(candidates[i]))
            {
                selected = candidates[i];
                return true;
            }
        }

        selected = RenderTextureFormat.Default;
        return false;
    }

    private static bool SupportsRandomWriteFormat(RenderTextureFormat format)
    {
#if UNITY_2020_1_OR_NEWER
        return SystemInfo.SupportsRandomWriteOnRenderTextureFormat(format);
#else
        return SystemInfo.SupportsRenderTextureFormat(format);
#endif
    }

    private bool AreTexturesReady()
    {
        return _temperatureA != null &&
               _temperatureB != null &&
               _heatSource != null &&
               _obstacle != null &&
               _velocity != null &&
               _velocityB != null &&
               _pressureA != null &&
               _pressureB != null &&
               _divergence != null;
    }

    private bool AreTexturesValidForGrid()
    {
        return IsTextureValidForGrid(_temperatureA) &&
               IsTextureValidForGrid(_temperatureB) &&
               IsTextureValidForGrid(_heatSource) &&
               IsTextureValidForGrid(_obstacle) &&
               IsTextureValidForGrid(_velocity) &&
               IsTextureValidForGrid(_velocityB) &&
               IsTextureValidForGrid(_pressureA) &&
               IsTextureValidForGrid(_pressureB) &&
               IsTextureValidForGrid(_divergence);
    }

    private bool IsTextureValidForGrid(RenderTexture tex)
    {
        return tex != null &&
               tex.width == gridX &&
               tex.height == gridY &&
               tex.volumeDepth == gridZ &&
               tex.dimension == TextureDimension.Tex3D;
    }

    private void CacheKernelsAndDispatch()
    {
        _kernelHeatStep = FindKernelSafe(KERNEL_HEAT_STEP);
        _kernelClearFloat3D = FindKernelSafe(KERNEL_CLEAR_FLOAT_3D);
        _kernelFillTemperature = FindKernelSafe(KERNEL_FILL_TEMPERATURE);
        _kernelFillVelocity = FindKernelSafe(KERNEL_FILL_VELOCITY);
        _kernelVelocityStep = FindKernelSafe(KERNEL_VELOCITY_STEP);
        _kernelComputeDivergence = FindKernelSafe(KERNEL_COMPUTE_DIVERGENCE);
        _kernelPressureJacobi = FindKernelSafe(KERNEL_PRESSURE_JACOBI);
        _kernelProjectVelocity = FindKernelSafe(KERNEL_PROJECT_VELOCITY);
        _kernelPaintSphereFloat = FindKernelSafe(KERNEL_PAINT_SPHERE_FLOAT);
        _kernelPaintSphereFloat4 = FindKernelSafe(KERNEL_PAINT_SPHERE_FLOAT4);
        _kernelPaintCellsFloat = FindKernelSafe(KERNEL_PAINT_CELLS_FLOAT);
        _kernelPaintCellsFloat4 = FindKernelSafe(KERNEL_PAINT_CELLS_FLOAT4);
        _kernelReduceAbsMaxFloat3D = FindKernelSafe(KERNEL_REDUCE_ABS_MAX_FLOAT_3D);
        _kernelCopyTemperatureToBuffer = FindKernelSafe(KERNEL_COPY_TEMPERATURE_TO_BUFFER);

        _dispatchHeatStep = BuildDispatchInfo(_kernelHeatStep);
        _dispatchClearFloat3D = BuildDispatchInfo(_kernelClearFloat3D);
        _dispatchFillTemperature = BuildDispatchInfo(_kernelFillTemperature);
        _dispatchFillVelocity = BuildDispatchInfo(_kernelFillVelocity);
        _dispatchVelocityStep = BuildDispatchInfo(_kernelVelocityStep);
        _dispatchComputeDivergence = BuildDispatchInfo(_kernelComputeDivergence);
        _dispatchPressureJacobi = BuildDispatchInfo(_kernelPressureJacobi);
        _dispatchProjectVelocity = BuildDispatchInfo(_kernelProjectVelocity);
        _dispatchPaintSphereFloat = BuildDispatchInfo(_kernelPaintSphereFloat);
        _dispatchPaintSphereFloat4 = BuildDispatchInfo(_kernelPaintSphereFloat4);
        _dispatchReduceAbsMaxFloat3D = BuildDispatchInfo(_kernelReduceAbsMaxFloat3D);
    }

    private int FindKernelSafe(string kernelName)
    {
        if (heatSimulationShader == null)
            return -1;

        return heatSimulationShader.HasKernel(kernelName)
            ? heatSimulationShader.FindKernel(kernelName)
            : -1;
    }

    private KernelDispatchInfo BuildDispatchInfo(int kernel)
    {
        if (kernel < 0 || heatSimulationShader == null)
            return default;

        heatSimulationShader.GetKernelThreadGroupSizes(kernel, out uint tx, out uint ty, out uint tz);
        if (tx == 0 || ty == 0 || tz == 0)
            return default;

        #region Formula Calculations - Dispatch Group Count
        return new KernelDispatchInfo
        {
            gx = Mathf.CeilToInt(gridX / (float)tx),
            gy = Mathf.CeilToInt(gridY / (float)ty),
            gz = Mathf.CeilToInt(gridZ / (float)tz),
            valid = true
        };
        #endregion
    }

    private void CreateTextures()
    {
        _temperatureA = Create3DTexture(_scalarTextureFormat, "TemperatureA");
        _temperatureB = Create3DTexture(_scalarTextureFormat, "TemperatureB");
        _heatSource = Create3DTexture(_scalarTextureFormat, "HeatSource");
        _obstacle = Create3DTexture(_scalarTextureFormat, "Obstacle");
        _velocity = Create3DTexture(_vectorTextureFormat, "Velocity");
        _velocityB = Create3DTexture(_vectorTextureFormat, "VelocityB");
        _pressureA = Create3DTexture(_scalarTextureFormat, "PressureA");
        _pressureB = Create3DTexture(_scalarTextureFormat, "PressureB");
        _divergence = Create3DTexture(_scalarTextureFormat, "Divergence");
        CreateBuffers();
    }

    private RenderTexture Create3DTexture(RenderTextureFormat format, string texName)
    {
        var rt = new RenderTexture(gridX, gridY, 0, format)
        {
            name = texName,
            dimension = TextureDimension.Tex3D,
            volumeDepth = gridZ,
            enableRandomWrite = true,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            autoGenerateMips = false
        };

        if (!rt.Create())
        {
            Debug.LogError($"[ClassroomHeatSimulation] Failed to create 3D RenderTexture {texName} ({format}).");
#if UNITY_EDITOR
            if (!Application.isPlaying)
                DestroyImmediate(rt);
            else
                Destroy(rt);
#else
            Destroy(rt);
#endif
            return null;
        }

        return rt;
    }

    private void ReleaseTextures()
    {
        ReleaseTexture(ref _temperatureA);
        ReleaseTexture(ref _temperatureB);
        ReleaseTexture(ref _heatSource);
        ReleaseTexture(ref _obstacle);
        ReleaseTexture(ref _velocity);
        ReleaseTexture(ref _velocityB);
        ReleaseTexture(ref _pressureA);
        ReleaseTexture(ref _pressureB);
        ReleaseTexture(ref _divergence);
    }

    private void CreateBuffers()
    {
        ReleaseBuffers();
        _divergenceReductionMaxBuffer = new ComputeBuffer(1, sizeof(uint));
    }

    private void ReleaseBuffers()
    {
        if (_divergenceReductionMaxBuffer != null)
        {
            _divergenceReductionMaxBuffer.Release();
            _divergenceReductionMaxBuffer = null;
        }
    }

    private void ReleaseTexture(ref RenderTexture rt)
    {
        if (rt == null)
            return;

        if (rt.IsCreated())
            rt.Release();

#if UNITY_EDITOR
        if (!Application.isPlaying)
            DestroyImmediate(rt);
        else
            Destroy(rt);
#else
        Destroy(rt);
#endif

        rt = null;
    }

    #endregion

    #region 模擬流程控制

    [ContextMenu("Reset Simulation")]
    public void ResetSimulation()
    {
        if (heatSimulationShader == null)
            return;

        InitializeIfNeeded();
        if (!_initialized)
            return;

        ResetSimulationInternal();
    }

    private void ResetSimulationInternal()
    {
        // 初始化為環境溫度、清空場、速度重設成全域風場。
        FillTemperature(ambientTemperature, _temperatureA);
        FillTemperature(ambientTemperature, _temperatureB);
        ClearHeatSource();
        ClearObstacle();
        ClearFloatTexture(_pressureA, 0f);
        ClearFloatTexture(_pressureB, 0f);
        ClearFloatTexture(_divergence, 0f);
        FillVelocity(globalAirVelocity);
        _simulationTimeAccumulator = 0f;
    }

    private bool TryBuildSolverSchedule(float requestedTotalDt, out int stepCount, out float stepDt, out float simulatedTotalDt)
    {
        stepCount = 0;
        stepDt = 0f;
        simulatedTotalDt = 0f;

        float safeDt = Mathf.Min(ComputeRecommendedMaxStepDt(), maxSolverStepDt);
        safeDt = Mathf.Max(safeDt, 0.0001f);

        simulatedTotalDt = Mathf.Min(requestedTotalDt, safeDt * maxInternalSolverSubstepsPerFrame);
        if (simulatedTotalDt <= 0f)
            return false;

        stepCount = Mathf.Max(1, Mathf.CeilToInt(simulatedTotalDt / safeDt));
        stepDt = simulatedTotalDt / stepCount;

        bool clamped = simulatedTotalDt + 1e-6f < requestedTotalDt || stepDt + 1e-6f < simulationDeltaTime;
        if (clamped && !_hasLoggedStepClampWarning)
        {
            Debug.LogWarning($"[ClassroomHeatSimulation] Solver step was clamped for stability. Requested total dt={requestedTotalDt:F4}, simulated total dt={simulatedTotalDt:F4}, substeps={stepCount}, stepDt={stepDt:F4}.");
            _hasLoggedStepClampWarning = true;
        }
        else if (!clamped)
        {
            _hasLoggedStepClampWarning = false;
        }

        return true;
    }

    private float ComputeRecommendedMaxStepDt()
    {
        Vector3 cell = CellSize;
        float minCell = Mathf.Max(Mathf.Min(cell.x, Mathf.Min(cell.y, cell.z)), 1e-5f);
        float sumInvCell2 =
            1f / Mathf.Max(cell.x * cell.x, 1e-6f) +
            1f / Mathf.Max(cell.y * cell.y, 1e-6f) +
            1f / Mathf.Max(cell.z * cell.z, 1e-6f);

        float diffusionLimitedDt = float.PositiveInfinity;
        if (diffusion > 1e-6f)
            diffusionLimitedDt = 0.45f / Mathf.Max(diffusion * sumInvCell2, 1e-6f);

        float velocityViscosityLimitedDt = float.PositiveInfinity;
        if (velocityViscosity > 1e-6f)
            velocityViscosityLimitedDt = 0.45f / Mathf.Max(velocityViscosity * sumInvCell2, 1e-6f);

        float advectionLimitedDt = 0.6f * minCell / Mathf.Max(maxVelocityMagnitude, 1e-4f);
        float stableDt = Mathf.Min(diffusionLimitedDt, Mathf.Min(velocityViscosityLimitedDt, advectionLimitedDt));
        return Mathf.Max(stableDt, 0.0001f);
    }

    private int GetTotalGridCellCount()
    {
        long total = (long)gridX * gridY * gridZ;
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    public bool CopyTemperatureToBuffer(ComputeBuffer targetBuffer)
    {
        InitializeIfNeeded();

        if (!_initialized || heatSimulationShader == null || _temperatureA == null)
        {
            Debug.LogWarning("[ClassroomHeatSimulation] Cannot copy temperature. Simulation is not initialized.");
            return false;
        }

        if (_kernelCopyTemperatureToBuffer < 0)
        {
            Debug.LogWarning("[ClassroomHeatSimulation] Kernel CopyTemperatureToBuffer was not found. Check HeatSimulation.compute.");
            return false;
        }

        int expectedCount = GetTotalGridCellCount();

        if (targetBuffer == null || targetBuffer.count < expectedCount || targetBuffer.stride != sizeof(float))
        {
            Debug.LogWarning(
                $"[ClassroomHeatSimulation] Invalid temperature readback buffer. Expected count={expectedCount}, stride={sizeof(float)}."
            );
            return false;
        }

        SetCommonGridParams();

        heatSimulationShader.SetTexture(
            _kernelCopyTemperatureToBuffer,
            PID_TemperatureTex,
            _temperatureA
        );

        heatSimulationShader.SetBuffer(
            _kernelCopyTemperatureToBuffer,
            PID_TemperatureReadbackBuffer,
            targetBuffer
        );

        int groupCount = Mathf.CeilToInt(expectedCount / 64f);
        heatSimulationShader.Dispatch(_kernelCopyTemperatureToBuffer, groupCount, 1, 1);

        return true;
    }

    public void ClearHeatSource()
    {
        ClearFloatTexture(_heatSource, 0f);
    }

    public void ClearObstacle()
    {
        ClearFloatTexture(_obstacle, 0f);
        _obstacleClearVersion++;
        _obstacleFieldVersion++;
    }

    public void FillVelocity(Vector3 velocity)
    {
        if (_velocity == null || _velocityB == null || _kernelFillVelocity < 0 || !_dispatchFillVelocity.valid)
            return;

        SetCommonGridParams();
        heatSimulationShader.SetVector(PID_FillVelocity, new Vector4(velocity.x, velocity.y, velocity.z, 0f));
        heatSimulationShader.SetTexture(_kernelFillVelocity, PID_ResultFloat4Tex3D, _velocity);
        DispatchKernel(_kernelFillVelocity, _dispatchFillVelocity);
        heatSimulationShader.SetTexture(_kernelFillVelocity, PID_ResultFloat4Tex3D, _velocityB);
        DispatchKernel(_kernelFillVelocity, _dispatchFillVelocity);
    }

    private void FillTemperature(float value, RenderTexture target)
    {
        if (target == null || _kernelFillTemperature < 0 || !_dispatchFillTemperature.valid)
            return;

        SetCommonGridParams();
        heatSimulationShader.SetFloat(PID_FillValue, value);
        heatSimulationShader.SetTexture(_kernelFillTemperature, PID_ResultFloatTex3D, target);
        DispatchKernel(_kernelFillTemperature, _dispatchFillTemperature);
    }

    private void ClearFloatTexture(RenderTexture target, float value)
    {
        if (target == null || _kernelClearFloat3D < 0 || !_dispatchClearFloat3D.valid)
            return;

        SetCommonGridParams();
        heatSimulationShader.SetFloat(PID_ClearValue, value);
        heatSimulationShader.SetTexture(_kernelClearFloat3D, PID_ResultFloatTex3D, target);
        DispatchKernel(_kernelClearFloat3D, _dispatchClearFloat3D);
    }

    private void PrepareMomentumStepConstants(float dt)
    {
        if (_kernelVelocityStep < 0 || !_dispatchVelocityStep.valid || !AreTexturesReady())
            return;

        SetCommonGridParams();

        // _InvCellSize = (1/dx, 1/dy, 1/dz)，用於差分梯度與散度。
        Vector3 cellSize = CellSize;
        Vector3 invCellSize = new Vector3(
            1f / Mathf.Max(cellSize.x, 1e-5f),
            1f / Mathf.Max(cellSize.y, 1e-5f),
            1f / Mathf.Max(cellSize.z, 1e-5f)
        );

        heatSimulationShader.SetFloat(PID_DeltaTime, dt);
        heatSimulationShader.SetFloat(PID_AmbientTemperature, ambientTemperature);
        heatSimulationShader.SetFloat(PID_MaxVelocityMagnitude, maxVelocityMagnitude);
        heatSimulationShader.SetFloat(PID_VelocityViscosity, velocityViscosity);
        heatSimulationShader.SetFloat(PID_VelocityDamping, velocityDamping);
        heatSimulationShader.SetFloat(PID_BuoyancyStrength, buoyancyStrength);
        heatSimulationShader.SetFloat(PID_Gravity, gravity);
        heatSimulationShader.SetVector(PID_InvCellSize, new Vector4(invCellSize.x, invCellSize.y, invCellSize.z, 0f));

        heatSimulationShader.SetTexture(_kernelVelocityStep, PID_TemperatureTex, _temperatureA);
        heatSimulationShader.SetTexture(_kernelVelocityStep, PID_ObstacleTex, _obstacle);
    }

    private void DispatchVelocityStepOnce()
    {
        if (_kernelVelocityStep < 0 || !_dispatchVelocityStep.valid || _velocity == null || _velocityB == null)
            return;

        heatSimulationShader.SetTexture(_kernelVelocityStep, PID_VelocityTex, _velocity);
        heatSimulationShader.SetTexture(_kernelVelocityStep, PID_VelocityNextTex, _velocityB);
        DispatchKernel(_kernelVelocityStep, _dispatchVelocityStep);
        Swap(ref _velocity, ref _velocityB);
    }

    private void DispatchPressureProjection(float dt)
    {
        // 壓力投影三步驟：
        // 1) 先計算速度散度 divergence
        // 2) Jacobi 迭代求壓力 Poisson
        // 3) 用壓力梯度修正速度，得到近似不可壓縮流
        if (_kernelComputeDivergence < 0 || _kernelPressureJacobi < 0 || _kernelProjectVelocity < 0)
            return;

        if (!_dispatchComputeDivergence.valid || !_dispatchPressureJacobi.valid || !_dispatchProjectVelocity.valid)
            return;

        if (_velocity == null || _velocityB == null || _pressureA == null || _pressureB == null || _divergence == null)
            return;

        PreparePressureProjectionConstants(dt);
        DispatchComputeDivergence();

        int adaptivePass = 0;
        while (true)
        {
            DispatchPressureSolveJacobi();
            DispatchProjectVelocity();

            if (!enableAdaptivePressureProjection)
            {
                _hasLoggedPressureConvergenceWarning = false;
                break;
            }

            float maxAbsDivergence = MeasureCurrentVelocityMaxAbsDivergence();
            bool converged = maxAbsDivergence <= divergenceTolerance;
            bool exhaustedAdaptivePasses = adaptivePass >= maxAdaptiveProjectionPasses;

            if (converged || exhaustedAdaptivePasses)
            {
                if (!converged && logPressureConvergenceWarnings)
                {
                    if (!_hasLoggedPressureConvergenceWarning)
                    {
                        Debug.LogWarning($"[ClassroomHeatSimulation] Pressure projection stopped with residual divergence {maxAbsDivergence:F5} after {adaptivePass + 1} projection pass(es). Increase pressureIterations or relax the timestep.");
                        _hasLoggedPressureConvergenceWarning = true;
                    }
                }
                else
                {
                    _hasLoggedPressureConvergenceWarning = false;
                }

                break;
            }

            adaptivePass++;
        }
    }

    private void PreparePressureProjectionConstants(float dt)
    {
        SetCommonGridParams();

        Vector3 cellSize = CellSize;
        Vector3 invCellSize = new Vector3(
            1f / Mathf.Max(cellSize.x, 1e-5f),
            1f / Mathf.Max(cellSize.y, 1e-5f),
            1f / Mathf.Max(cellSize.z, 1e-5f)
        );

        heatSimulationShader.SetFloat(PID_DeltaTime, dt);
        heatSimulationShader.SetVector(PID_InvCellSize, new Vector4(invCellSize.x, invCellSize.y, invCellSize.z, 0f));
        heatSimulationShader.SetFloat(PID_PressureRelaxation, pressureRelaxation);
    }

    private void DispatchComputeDivergence()
    {
        heatSimulationShader.SetTexture(_kernelComputeDivergence, PID_VelocityTex, _velocity);
        heatSimulationShader.SetTexture(_kernelComputeDivergence, PID_ObstacleTex, _obstacle);
        heatSimulationShader.SetTexture(_kernelComputeDivergence, PID_ResultFloatTex3D, _divergence);
        DispatchKernel(_kernelComputeDivergence, _dispatchComputeDivergence);
    }

    private void DispatchPressureSolveJacobi()
    {
        // 每個子步先把壓力場歸零，提升投影穩定性（代價是多一些計算）。
        ClearFloatTexture(_pressureA, 0f);
        ClearFloatTexture(_pressureB, 0f);

        for (int i = 0; i < pressureIterations; i++)
        {
            heatSimulationShader.SetTexture(_kernelPressureJacobi, PID_PressureTex, _pressureA);
            heatSimulationShader.SetTexture(_kernelPressureJacobi, PID_DivergenceTex, _divergence);
            heatSimulationShader.SetTexture(_kernelPressureJacobi, PID_ObstacleTex, _obstacle);
            heatSimulationShader.SetTexture(_kernelPressureJacobi, PID_PressureNextTex, _pressureB);
            DispatchKernel(_kernelPressureJacobi, _dispatchPressureJacobi);
            Swap(ref _pressureA, ref _pressureB);
        }
    }

    private void DispatchProjectVelocity()
    {
        heatSimulationShader.SetTexture(_kernelProjectVelocity, PID_PressureTex, _pressureA);
        heatSimulationShader.SetTexture(_kernelProjectVelocity, PID_VelocityTex, _velocity);
        heatSimulationShader.SetTexture(_kernelProjectVelocity, PID_ObstacleTex, _obstacle);
        heatSimulationShader.SetTexture(_kernelProjectVelocity, PID_VelocityNextTex, _velocityB);
        DispatchKernel(_kernelProjectVelocity, _dispatchProjectVelocity);
        Swap(ref _velocity, ref _velocityB);
    }

    private float MeasureCurrentVelocityMaxAbsDivergence()
    {
        if (_divergenceReductionMaxBuffer == null || _kernelReduceAbsMaxFloat3D < 0 || !_dispatchReduceAbsMaxFloat3D.valid)
            return float.PositiveInfinity;

        DispatchComputeDivergence();

        _divergenceReductionReadback[0] = 0u;
        _divergenceReductionMaxBuffer.SetData(_divergenceReductionReadback);
        heatSimulationShader.SetTexture(_kernelReduceAbsMaxFloat3D, PID_DivergenceTex, _divergence);
        heatSimulationShader.SetBuffer(_kernelReduceAbsMaxFloat3D, PID_ReductionMaxBuffer, _divergenceReductionMaxBuffer);
        DispatchKernel(_kernelReduceAbsMaxFloat3D, _dispatchReduceAbsMaxFloat3D);
        _divergenceReductionMaxBuffer.GetData(_divergenceReductionReadback);

        return DecodePositiveFloatBits(_divergenceReductionReadback[0]);
    }

    private static float DecodePositiveFloatBits(uint encodedValue)
    {
        byte[] raw = BitConverter.GetBytes(encodedValue);
        return Mathf.Abs(BitConverter.ToSingle(raw, 0));
    }

    private void PrepareHeatStepConstants(float dt)
    {
        if (_kernelHeatStep < 0 || !_dispatchHeatStep.valid || !AreTexturesReady())
            return;

        SetCommonGridParams();

        #region Formula Calculations - Inverse Cell Size
        Vector3 cellSize = CellSize;
        Vector3 invCellSize = new Vector3(
            1f / Mathf.Max(cellSize.x, 1e-5f),
            1f / Mathf.Max(cellSize.y, 1e-5f),
            1f / Mathf.Max(cellSize.z, 1e-5f)
        );
        #endregion

        heatSimulationShader.SetFloat(PID_DeltaTime, dt);
        heatSimulationShader.SetFloat(PID_Diffusion, diffusion);
        heatSimulationShader.SetFloat(PID_AmbientExchangeRate, ambientExchangeRate);
        heatSimulationShader.SetFloat(PID_AmbientTemperature, ambientTemperature);
        heatSimulationShader.SetFloat(PID_MinTemperatureClamp, minTemperatureClamp);
        heatSimulationShader.SetFloat(PID_MaxTemperatureClamp, maxTemperatureClamp);
        heatSimulationShader.SetVector(PID_InvCellSize, new Vector4(invCellSize.x, invCellSize.y, invCellSize.z, 0f));

        heatSimulationShader.SetTexture(_kernelHeatStep, PID_HeatSourceTex, _heatSource);
        heatSimulationShader.SetTexture(_kernelHeatStep, PID_ObstacleTex, _obstacle);
        heatSimulationShader.SetTexture(_kernelHeatStep, PID_VelocityTex, _velocity);
    }

    private void DispatchHeatStepOnce()
    {
        if (_kernelHeatStep < 0 || !_dispatchHeatStep.valid || _temperatureA == null || _temperatureB == null)
            return;

        heatSimulationShader.SetTexture(_kernelHeatStep, PID_TemperatureTex, _temperatureA);
        heatSimulationShader.SetTexture(_kernelHeatStep, PID_TemperatureNextTex, _temperatureB);
        DispatchKernel(_kernelHeatStep, _dispatchHeatStep);

        Swap(ref _temperatureA, ref _temperatureB);
        _temperatureSwapVersion++;
    }

    private void SetCommonGridParams()
    {
        heatSimulationShader.SetInt(PID_GridX, gridX);
        heatSimulationShader.SetInt(PID_GridY, gridY);
        heatSimulationShader.SetInt(PID_GridZ, gridZ);
    }

    private void DispatchKernel(int kernel, KernelDispatchInfo dispatch)
    {
        if (kernel < 0 || !dispatch.valid)
            return;

        heatSimulationShader.Dispatch(kernel, dispatch.gx, dispatch.gy, dispatch.gz);
    }

    private void Swap(ref RenderTexture a, ref RenderTexture b)
    {
        RenderTexture temp = a;
        a = b;
        b = temp;
    }

    #endregion

    #region 世界座標與網格座標轉換（公式計算）

    public Bounds GetSimulationBounds()
    {
        return new Bounds(SimulationCenterWorld, simulationSize);
    }

    public bool WorldToGrid(Vector3 worldPos, out Vector3Int gridPos)
    {
        Bounds bounds = GetSimulationBounds();
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;

        if (worldPos.x < min.x || worldPos.x > max.x ||
            worldPos.y < min.y || worldPos.y > max.y ||
            worldPos.z < min.z || worldPos.z > max.z)
        {
            gridPos = default;
            return false;
        }

        // 先轉成 0..1，再映射到離散格點索引。
        Vector3 local01 = new Vector3(
            Mathf.InverseLerp(min.x, max.x, worldPos.x),
            Mathf.InverseLerp(min.y, max.y, worldPos.y),
            Mathf.InverseLerp(min.z, max.z, worldPos.z)
        );

        int x = Mathf.Clamp(Mathf.FloorToInt(local01.x * gridX), 0, gridX - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt(local01.y * gridY), 0, gridY - 1);
        int z = Mathf.Clamp(Mathf.FloorToInt(local01.z * gridZ), 0, gridZ - 1);

        gridPos = new Vector3Int(x, y, z);
        return true;
    }

    public Vector3 GridToWorldCenter(Vector3Int gridPos)
    {
        Bounds bounds = GetSimulationBounds();
        Vector3 cell = CellSize;
        Vector3 min = bounds.min;

        return min + new Vector3(
            (gridPos.x + 0.5f) * cell.x,
            (gridPos.y + 0.5f) * cell.y,
            (gridPos.z + 0.5f) * cell.z
        );
    }

    #endregion

    #region 熱源/冷源/障礙物/局部風速注入 API

    public void AddHeatSourceSphere(Vector3 worldCenter, float worldRadius, float strength)
    {
        PaintSphereTo3DTexture(_heatSource, worldCenter, worldRadius, strength, PaintMode.Add, useSoftFalloff: true);
    }

    public void SetHeatSourceSphere(Vector3 worldCenter, float worldRadius, float value)
    {
        PaintSphereTo3DTexture(_heatSource, worldCenter, worldRadius, value, PaintMode.Set, useSoftFalloff: true);
    }

    public void AddColdSourceSphere(Vector3 worldCenter, float worldRadius, float coolingStrength)
    {
        // 冷源以負熱量寫入熱源場。
        PaintSphereTo3DTexture(_heatSource, worldCenter, worldRadius, -Mathf.Abs(coolingStrength), PaintMode.Add, useSoftFalloff: true);
    }

    public void AddHeatSourcePowerSphere(Vector3 worldCenter, float worldRadius, float powerWatts)
    {
        PaintPowerSphereToHeatSource(worldCenter, worldRadius, powerWatts);
    }

    public void AddHeatSourcePowerBox(Vector3 worldCenter, Vector3 worldSize, Quaternion worldRotation, float powerWatts)
    {
        PaintPowerBoxToHeatSource(worldCenter, worldSize, worldRotation, powerWatts);
    }

    public void AddCoolingPowerSphere(Vector3 worldCenter, float worldRadius, float coolingPowerWatts)
    {
        PaintPowerSphereToHeatSource(worldCenter, worldRadius, -Mathf.Abs(coolingPowerWatts));
    }

    public void AddCoolingPowerBox(Vector3 worldCenter, Vector3 worldSize, Quaternion worldRotation, float coolingPowerWatts)
    {
        PaintPowerBoxToHeatSource(worldCenter, worldSize, worldRotation, -Mathf.Abs(coolingPowerWatts));
    }

    public void SetObstacleSphere(Vector3 worldCenter, float worldRadius, float value = 1f)
    {
        if (PaintSphereTo3DTexture(_obstacle, worldCenter, worldRadius, Mathf.Clamp01(value), PaintMode.Set, useSoftFalloff: false))
            _obstacleFieldVersion++;
    }

    public void ClearObstacleSphere(Vector3 worldCenter, float worldRadius)
    {
        if (PaintSphereTo3DTexture(_obstacle, worldCenter, worldRadius, 0f, PaintMode.Set, useSoftFalloff: false))
            _obstacleFieldVersion++;
    }

    public void SetObstacleCollider(Collider obstacleCollider, float value = 1f, float surfacePadding = 0.02f, bool requireConvexMeshCollider = false)
    {
        if (obstacleCollider == null)
            return;

        if (requireConvexMeshCollider && obstacleCollider is MeshCollider meshCollider && !meshCollider.convex)
        {
            Debug.LogWarning($"[ClassroomHeatSimulation] Skipped non-convex MeshCollider obstacle on {obstacleCollider.name} because requireConvexMeshCollider is enabled.");
            return;
        }

        InitializeIfNeeded();
        if (!_initialized || _obstacle == null || _kernelPaintCellsFloat < 0)
            return;

        BuildColliderObstacleCells(obstacleCollider, Mathf.Max(0f, surfacePadding), _colliderObstacleCells);
        if (PaintCellsToFloatTexture(_obstacle, _colliderObstacleCells, Mathf.Clamp01(value), PaintMode.Set))
            _obstacleFieldVersion++;
    }

    public void SetVelocitySphere(Vector3 worldCenter, float worldRadius, Vector3 velocity)
    {
        PaintVelocitySphereTo3DTexture(worldCenter, worldRadius, velocity, PaintMode.Set, useSoftFalloff: true);
    }

    public void SetVelocityBox(Vector3 worldCenter, Vector3 worldSize, Quaternion worldRotation, Vector3 velocity)
    {
        PaintVelocityBoxTo3DTexture(worldCenter, worldSize, worldRotation, velocity, PaintMode.Set);
    }

    public void AddVelocitySphere(Vector3 worldCenter, float worldRadius, Vector3 velocity)
    {
        PaintVelocitySphereTo3DTexture(worldCenter, worldRadius, velocity, PaintMode.Add, useSoftFalloff: true);
    }

    public void AddVelocityBox(Vector3 worldCenter, Vector3 worldSize, Quaternion worldRotation, Vector3 velocity)
    {
        PaintVelocityBoxTo3DTexture(worldCenter, worldSize, worldRotation, velocity, PaintMode.Add);
    }

    public void ResetVelocityToGlobal()
    {
        FillVelocity(globalAirVelocity);
    }

    public float GetAirVolumetricHeatCapacity()
    {
        return Mathf.Max(airDensity * airSpecificHeatCapacity, 1e-4f);
    }

    public float ConvertPowerToTemperatureRate(float powerWatts, float worldRadius)
    {
        float radius = Mathf.Max(Mathf.Abs(worldRadius), 1e-4f);
        float volume = 4f / 3f * Mathf.PI * radius * radius * radius;
        return powerWatts / Mathf.Max(GetAirVolumetricHeatCapacity() * volume, 1e-4f);
    }

    public float EstimateBuoyantPlumeSpeed(float powerWatts, float sourceRadius, float sampleHeightMultiplier = 1.5f, float plumeVelocityScale = 1f)
    {
        float radius = Mathf.Max(Mathf.Abs(sourceRadius), 1e-4f);
        float sampleHeight = Mathf.Max(radius * Mathf.Max(sampleHeightMultiplier, 0.25f), 1e-4f);
        float beta = 1f / Mathf.Max(ambientTemperature + 273.15f, 1e-3f);
        float buoyancyFlux = gravity * beta * Mathf.Abs(powerWatts) / Mathf.Max(GetAirVolumetricHeatCapacity(), 1e-4f);
        float plumeSpeed = Mathf.Pow(Mathf.Max(buoyancyFlux / sampleHeight, 0f), 1f / 3f);
        return Mathf.Max(0f, plumeSpeed * Mathf.Max(plumeVelocityScale, 0f));
    }

    #endregion

    #region 筆刷注入輔助

    private enum PaintMode
    {
        Set,
        Add
    }

    private bool PaintSphereTo3DTexture(
        RenderTexture target,
        Vector3 worldCenter,
        float worldRadius,
        float value,
        PaintMode mode,
        bool useSoftFalloff)
    {
        if (target == null || worldRadius <= 0f)
            return false;

        // 寫入前確保模擬資源就緒。
        InitializeIfNeeded();
        if (!_initialized || _kernelPaintSphereFloat < 0 || !_dispatchPaintSphereFloat.valid)
            return false;

        SetPaintCommonParams(worldCenter, worldRadius, mode, useSoftFalloff);
        heatSimulationShader.SetFloat(PID_PaintScalar, value);
        heatSimulationShader.SetTexture(_kernelPaintSphereFloat, PID_ResultFloatTex3D, target);
        DispatchKernel(_kernelPaintSphereFloat, _dispatchPaintSphereFloat);
        return true;
    }

    private void PaintVelocitySphereTo3DTexture(
        Vector3 worldCenter,
        float worldRadius,
        Vector3 velocity,
        PaintMode mode,
        bool useSoftFalloff)
    {
        if (worldRadius <= 0f)
            return;

        // 寫入前確保模擬資源就緒。
        InitializeIfNeeded();
        if (!_initialized || _velocity == null || _velocityB == null || _kernelPaintSphereFloat4 < 0 || !_dispatchPaintSphereFloat4.valid)
            return;

        SetPaintCommonParams(worldCenter, worldRadius, mode, useSoftFalloff);
        heatSimulationShader.SetVector(PID_PaintVector, new Vector4(velocity.x, velocity.y, velocity.z, 0f));
        heatSimulationShader.SetTexture(_kernelPaintSphereFloat4, PID_ResultFloat4Tex3D, _velocity);
        DispatchKernel(_kernelPaintSphereFloat4, _dispatchPaintSphereFloat4);
        heatSimulationShader.SetTexture(_kernelPaintSphereFloat4, PID_ResultFloat4Tex3D, _velocityB);
        DispatchKernel(_kernelPaintSphereFloat4, _dispatchPaintSphereFloat4);
    }

    private void SetPaintCommonParams(Vector3 worldCenter, float worldRadius, PaintMode mode, bool useSoftFalloff)
    {
        SetCommonGridParams();
        worldRadius = Mathf.Abs(worldRadius);

        Bounds bounds = GetSimulationBounds();
        heatSimulationShader.SetVector(PID_SimulationBoundsMin, new Vector4(bounds.min.x, bounds.min.y, bounds.min.z, 0f));
        heatSimulationShader.SetVector(PID_SimulationBoundsMax, new Vector4(bounds.max.x, bounds.max.y, bounds.max.z, 0f));
        heatSimulationShader.SetVector(
            PID_PaintCenterWorldRadius,
            new Vector4(worldCenter.x, worldCenter.y, worldCenter.z, worldRadius));
        heatSimulationShader.SetInt(PID_PaintMode, (int)mode);
        heatSimulationShader.SetInt(PID_PaintUseSoftFalloff, useSoftFalloff ? 1 : 0);
    }

    private void PaintPowerSphereToHeatSource(Vector3 worldCenter, float worldRadius, float powerWatts)
    {
        if (worldRadius <= 0f)
            return;

        InitializeIfNeeded();
        if (!_initialized || _heatSource == null)
            return;

        BuildSphereIntersectingCells(worldCenter, Mathf.Abs(worldRadius), _powerSourceCells);
        if (_powerSourceCells.Count == 0)
            return;

        Vector3 cellSize = CellSize;
        float cellVolume = Mathf.Max(cellSize.x * cellSize.y * cellSize.z, 1e-6f);
        float affectedVolume = cellVolume * _powerSourceCells.Count;
        float temperatureRate = powerWatts / Mathf.Max(GetAirVolumetricHeatCapacity() * affectedVolume, 1e-4f);
        PaintCellsToFloatTexture(_heatSource, _powerSourceCells, temperatureRate, PaintMode.Add);
    }

    private void PaintPowerBoxToHeatSource(Vector3 worldCenter, Vector3 worldSize, Quaternion worldRotation, float powerWatts)
    {
        Vector3 sanitizedWorldSize = SanitizeBoxSize(worldSize);
        if (sanitizedWorldSize.x <= 0f || sanitizedWorldSize.y <= 0f || sanitizedWorldSize.z <= 0f)
            return;

        InitializeIfNeeded();
        if (!_initialized || _heatSource == null)
            return;

        BuildOrientedBoxIntersectingCells(worldCenter, sanitizedWorldSize, worldRotation, _boxPaintCells);
        if (_boxPaintCells.Count == 0)
            return;

        Vector3 cellSize = CellSize;
        float cellVolume = Mathf.Max(cellSize.x * cellSize.y * cellSize.z, 1e-6f);
        float affectedVolume = cellVolume * _boxPaintCells.Count;
        float temperatureRate = powerWatts / Mathf.Max(GetAirVolumetricHeatCapacity() * affectedVolume, 1e-4f);
        PaintCellsToFloatTexture(_heatSource, _boxPaintCells, temperatureRate, PaintMode.Add);
    }

    private void PaintVelocityBoxTo3DTexture(
        Vector3 worldCenter,
        Vector3 worldSize,
        Quaternion worldRotation,
        Vector3 velocity,
        PaintMode mode)
    {
        Vector3 sanitizedWorldSize = SanitizeBoxSize(worldSize);
        if (sanitizedWorldSize.x <= 0f || sanitizedWorldSize.y <= 0f || sanitizedWorldSize.z <= 0f)
            return;

        InitializeIfNeeded();
        if (!_initialized || _velocity == null || _velocityB == null || _kernelPaintCellsFloat4 < 0)
            return;

        BuildOrientedBoxIntersectingCells(worldCenter, sanitizedWorldSize, worldRotation, _boxPaintCells);
        if (_boxPaintCells.Count == 0)
            return;

        PaintCellsToFloat4Texture(_velocity, _boxPaintCells, velocity, mode);
        PaintCellsToFloat4Texture(_velocityB, _boxPaintCells, velocity, mode);
    }

    private void BuildColliderObstacleCells(Collider obstacleCollider, float surfacePadding, List<Vector3Int> results)
    {
        results.Clear();
        if (obstacleCollider == null)
            return;

        Bounds simBounds = GetSimulationBounds();
        Bounds colliderBounds = obstacleCollider.bounds;
        colliderBounds.Expand(surfacePadding * 2f);

        if (!simBounds.Intersects(colliderBounds))
            return;

        Vector3 overlapMin = Vector3.Max(simBounds.min, colliderBounds.min);
        Vector3 overlapMax = Vector3.Min(simBounds.max, colliderBounds.max);
        if (overlapMin.x > overlapMax.x || overlapMin.y > overlapMax.y || overlapMin.z > overlapMax.z)
            return;

        Vector3Int minCell = WorldToGridClamped(overlapMin);
        Vector3Int maxCell = WorldToGridClamped(overlapMax);
        Vector3 cellSize = CellSize;

        for (int z = minCell.z; z <= maxCell.z; z++)
        {
            for (int y = minCell.y; y <= maxCell.y; y++)
            {
                for (int x = minCell.x; x <= maxCell.x; x++)
                {
                    Vector3Int cell = new Vector3Int(x, y, z);
                    Vector3 worldCenter = GridToWorldCenter(cell);
                    Vector3 cellMin = simBounds.min + Vector3.Scale(new Vector3(x, y, z), cellSize);
                    Vector3 cellMax = cellMin + cellSize;
                    if (DoesColliderOverlapCell(obstacleCollider, cellMin, cellMax, worldCenter, surfacePadding))
                        results.Add(cell);
                }
            }
        }
    }

    private void BuildOrientedBoxIntersectingCells(Vector3 worldCenter, Vector3 worldSize, Quaternion worldRotation, List<Vector3Int> results)
    {
        results.Clear();

        Vector3 sanitizedWorldSize = SanitizeBoxSize(worldSize);
        Bounds simBounds = GetSimulationBounds();
        Vector3 halfExtents = sanitizedWorldSize * 0.5f;
        Vector3[] corners = GetOrientedBoxCorners(worldCenter, halfExtents, worldRotation);
        Vector3 min = corners[0];
        Vector3 max = corners[0];
        for (int i = 1; i < corners.Length; i++)
        {
            min = Vector3.Min(min, corners[i]);
            max = Vector3.Max(max, corners[i]);
        }

        Bounds orientedBounds = new Bounds();
        orientedBounds.SetMinMax(min, max);
        if (!simBounds.Intersects(orientedBounds))
            return;

        Vector3 overlapMin = Vector3.Max(simBounds.min, orientedBounds.min);
        Vector3 overlapMax = Vector3.Min(simBounds.max, orientedBounds.max);
        if (overlapMin.x > overlapMax.x || overlapMin.y > overlapMax.y || overlapMin.z > overlapMax.z)
            return;

        Vector3Int minCell = WorldToGridClamped(overlapMin);
        Vector3Int maxCell = WorldToGridClamped(overlapMax);
        Quaternion inverseRotation = Quaternion.Inverse(worldRotation);

        for (int z = minCell.z; z <= maxCell.z; z++)
        {
            for (int y = minCell.y; y <= maxCell.y; y++)
            {
                for (int x = minCell.x; x <= maxCell.x; x++)
                {
                    Vector3 worldPosition = GridToWorldCenter(new Vector3Int(x, y, z));
                    if (IsPointInsideOrientedBox(worldPosition, worldCenter, inverseRotation, halfExtents))
                        results.Add(new Vector3Int(x, y, z));
                }
            }
        }
    }

    private void BuildSphereIntersectingCells(Vector3 worldCenter, float worldRadius, List<Vector3Int> results)
    {
        results.Clear();
        if (worldRadius <= 0f)
            return;

        Bounds simBounds = GetSimulationBounds();
        Bounds sphereBounds = new Bounds(worldCenter, Vector3.one * (worldRadius * 2f));
        if (!simBounds.Intersects(sphereBounds))
            return;

        Vector3 overlapMin = Vector3.Max(simBounds.min, sphereBounds.min);
        Vector3 overlapMax = Vector3.Min(simBounds.max, sphereBounds.max);
        if (overlapMin.x > overlapMax.x || overlapMin.y > overlapMax.y || overlapMin.z > overlapMax.z)
            return;

        Vector3Int minCell = WorldToGridClamped(overlapMin);
        Vector3Int maxCell = WorldToGridClamped(overlapMax);
        Vector3 cellSize = CellSize;
        float radiusSqr = worldRadius * worldRadius;

        for (int z = minCell.z; z <= maxCell.z; z++)
        {
            for (int y = minCell.y; y <= maxCell.y; y++)
            {
                for (int x = minCell.x; x <= maxCell.x; x++)
                {
                    Vector3 cellMin = simBounds.min + Vector3.Scale(new Vector3(x, y, z), cellSize);
                    Vector3 cellMax = cellMin + cellSize;
                    if (DoesSphereIntersectAabb(worldCenter, radiusSqr, cellMin, cellMax))
                        results.Add(new Vector3Int(x, y, z));
                }
            }
        }
    }

    private static bool DoesSphereIntersectAabb(Vector3 sphereCenter, float radiusSqr, Vector3 boxMin, Vector3 boxMax)
    {
        Vector3 closest = new Vector3(
            Mathf.Clamp(sphereCenter.x, boxMin.x, boxMax.x),
            Mathf.Clamp(sphereCenter.y, boxMin.y, boxMax.y),
            Mathf.Clamp(sphereCenter.z, boxMin.z, boxMax.z));
        return (sphereCenter - closest).sqrMagnitude <= radiusSqr;
    }

    private static Vector3 SanitizeBoxSize(Vector3 worldSize)
    {
        return new Vector3(
            Mathf.Max(Mathf.Abs(worldSize.x), 0.0001f),
            Mathf.Max(Mathf.Abs(worldSize.y), 0.0001f),
            Mathf.Max(Mathf.Abs(worldSize.z), 0.0001f));
    }

    private static Vector3[] GetOrientedBoxCorners(Vector3 center, Vector3 halfExtents, Quaternion rotation)
    {
        Vector3[] corners = new Vector3[8];
        int index = 0;
        for (int ix = -1; ix <= 1; ix += 2)
        {
            for (int iy = -1; iy <= 1; iy += 2)
            {
                for (int iz = -1; iz <= 1; iz += 2)
                {
                    Vector3 local = new Vector3(halfExtents.x * ix, halfExtents.y * iy, halfExtents.z * iz);
                    corners[index++] = center + rotation * local;
                }
            }
        }

        return corners;
    }

    private static bool IsPointInsideOrientedBox(Vector3 worldPosition, Vector3 center, Quaternion inverseRotation, Vector3 halfExtents)
    {
        Vector3 local = inverseRotation * (worldPosition - center);
        return Mathf.Abs(local.x) <= halfExtents.x &&
               Mathf.Abs(local.y) <= halfExtents.y &&
               Mathf.Abs(local.z) <= halfExtents.z;
    }

    private static bool DoesColliderOverlapCell(Collider obstacleCollider, Vector3 cellMin, Vector3 cellMax, Vector3 cellCenter, float surfacePadding)
    {
        switch (obstacleCollider)
        {
            case SphereCollider sphereCollider:
                return DoesSphereColliderOverlapCell(sphereCollider, cellMin, cellMax, surfacePadding);
            case BoxCollider boxCollider:
                return DoesBoxColliderOverlapCell(boxCollider, cellMin, cellMax, surfacePadding);
            default:
                return DoesColliderOverlapCellFallback(obstacleCollider, cellMin, cellMax, cellCenter, surfacePadding);
        }
    }

    private static bool DoesSphereColliderOverlapCell(SphereCollider sphereCollider, Vector3 cellMin, Vector3 cellMax, float surfacePadding)
    {
        Vector3 lossyScale = sphereCollider.transform.lossyScale;
        float radiusScale = Mathf.Max(Mathf.Abs(lossyScale.x), Mathf.Max(Mathf.Abs(lossyScale.y), Mathf.Abs(lossyScale.z)));
        Vector3 sphereCenter = sphereCollider.transform.TransformPoint(sphereCollider.center);
        float radius = Mathf.Max(0f, sphereCollider.radius * radiusScale + surfacePadding);
        return DoesSphereIntersectAabb(sphereCenter, radius * radius, cellMin, cellMax);
    }

    private static bool DoesBoxColliderOverlapCell(BoxCollider boxCollider, Vector3 cellMin, Vector3 cellMax, float surfacePadding)
    {
        Vector3 expandedCellMin = cellMin - Vector3.one * surfacePadding;
        Vector3 expandedCellMax = cellMax + Vector3.one * surfacePadding;
        Transform boxTransform = boxCollider.transform;

        Vector3 localMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 localMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        for (int ix = 0; ix < 2; ix++)
        {
            for (int iy = 0; iy < 2; iy++)
            {
                for (int iz = 0; iz < 2; iz++)
                {
                    Vector3 corner = new Vector3(
                        ix == 0 ? expandedCellMin.x : expandedCellMax.x,
                        iy == 0 ? expandedCellMin.y : expandedCellMax.y,
                        iz == 0 ? expandedCellMin.z : expandedCellMax.z);
                    Vector3 localCorner = boxTransform.InverseTransformPoint(corner);
                    localMin = Vector3.Min(localMin, localCorner);
                    localMax = Vector3.Max(localMax, localCorner);
                }
            }
        }

        Vector3 boxMin = boxCollider.center - boxCollider.size * 0.5f;
        Vector3 boxMax = boxCollider.center + boxCollider.size * 0.5f;
        return localMin.x <= boxMax.x && localMax.x >= boxMin.x &&
               localMin.y <= boxMax.y && localMax.y >= boxMin.y &&
               localMin.z <= boxMax.z && localMax.z >= boxMin.z;
    }

    private static bool DoesColliderOverlapCellFallback(Collider obstacleCollider, Vector3 cellMin, Vector3 cellMax, Vector3 cellCenter, float surfacePadding)
    {
        if (IsPointInsideExpandedCell(obstacleCollider.ClosestPoint(cellCenter), cellMin, cellMax, surfacePadding))
            return true;

        Vector3 expandedMin = cellMin - Vector3.one * surfacePadding;
        Vector3 expandedMax = cellMax + Vector3.one * surfacePadding;
        for (int ix = 0; ix < 2; ix++)
        {
            for (int iy = 0; iy < 2; iy++)
            {
                for (int iz = 0; iz < 2; iz++)
                {
                    Vector3 corner = new Vector3(
                        ix == 0 ? expandedMin.x : expandedMax.x,
                        iy == 0 ? expandedMin.y : expandedMax.y,
                        iz == 0 ? expandedMin.z : expandedMax.z);
                    Vector3 closestPoint = obstacleCollider.ClosestPoint(corner);
                    if ((closestPoint - corner).sqrMagnitude <= 1e-8f)
                        return true;
                }
            }
        }

        return false;
    }

    private static bool IsPointInsideExpandedCell(Vector3 point, Vector3 cellMin, Vector3 cellMax, float surfacePadding)
    {
        return point.x >= cellMin.x - surfacePadding &&
               point.x <= cellMax.x + surfacePadding &&
               point.y >= cellMin.y - surfacePadding &&
               point.y <= cellMax.y + surfacePadding &&
               point.z >= cellMin.z - surfacePadding &&
               point.z <= cellMax.z + surfacePadding;
    }

    private Vector3Int WorldToGridClamped(Vector3 worldPos)
    {
        Bounds bounds = GetSimulationBounds();
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;

        Vector3 local01 = new Vector3(
            Mathf.InverseLerp(min.x, max.x, Mathf.Clamp(worldPos.x, min.x, max.x)),
            Mathf.InverseLerp(min.y, max.y, Mathf.Clamp(worldPos.y, min.y, max.y)),
            Mathf.InverseLerp(min.z, max.z, Mathf.Clamp(worldPos.z, min.z, max.z))
        );

        return new Vector3Int(
            Mathf.Clamp(Mathf.FloorToInt(local01.x * gridX), 0, gridX - 1),
            Mathf.Clamp(Mathf.FloorToInt(local01.y * gridY), 0, gridY - 1),
            Mathf.Clamp(Mathf.FloorToInt(local01.z * gridZ), 0, gridZ - 1)
        );
    }

    private bool PaintCellsToFloatTexture(RenderTexture target, List<Vector3Int> cells, float value, PaintMode mode)
    {
        if (target == null || cells == null || cells.Count == 0 || _kernelPaintCellsFloat < 0)
            return false;

        SetCommonGridParams();
        heatSimulationShader.SetInt(PID_PaintMode, (int)mode);
        heatSimulationShader.SetFloat(PID_PaintScalar, value);
        heatSimulationShader.SetInt(PID_PaintCellCount, cells.Count);
        heatSimulationShader.SetTexture(_kernelPaintCellsFloat, PID_ResultFloatTex3D, target);

        GridCellPaintData[] cellData = new GridCellPaintData[cells.Count];
        for (int i = 0; i < cells.Count; i++)
            cellData[i] = new GridCellPaintData(cells[i]);

        using (var buffer = new ComputeBuffer(cells.Count, sizeof(int) * 4))
        {
            buffer.SetData(cellData);
            heatSimulationShader.SetBuffer(_kernelPaintCellsFloat, PID_PaintCells, buffer);
            int groupCount = Mathf.CeilToInt(cells.Count / 64f);
            heatSimulationShader.Dispatch(_kernelPaintCellsFloat, groupCount, 1, 1);
        }

        return true;
    }

    private bool PaintCellsToFloat4Texture(RenderTexture target, List<Vector3Int> cells, Vector3 value, PaintMode mode)
    {
        if (target == null || cells == null || cells.Count == 0 || _kernelPaintCellsFloat4 < 0)
            return false;

        SetCommonGridParams();
        heatSimulationShader.SetInt(PID_PaintMode, (int)mode);
        heatSimulationShader.SetVector(PID_PaintVector, new Vector4(value.x, value.y, value.z, 0f));
        heatSimulationShader.SetInt(PID_PaintCellCount, cells.Count);
        heatSimulationShader.SetTexture(_kernelPaintCellsFloat4, PID_ResultFloat4Tex3D, target);

        GridCellPaintData[] cellData = new GridCellPaintData[cells.Count];
        for (int i = 0; i < cells.Count; i++)
            cellData[i] = new GridCellPaintData(cells[i]);

        using (var buffer = new ComputeBuffer(cells.Count, sizeof(int) * 4))
        {
            buffer.SetData(cellData);
            heatSimulationShader.SetBuffer(_kernelPaintCellsFloat4, PID_PaintCells, buffer);
            int groupCount = Mathf.CeilToInt(cells.Count / 64f);
            heatSimulationShader.Dispatch(_kernelPaintCellsFloat4, groupCount, 1, 1);
        }

        return true;
    }

    #endregion

    #region Slice 顯示材質綁定輔助

    public void ApplyToSliceMaterial(Material mat)
    {
        if (mat == null || _temperatureA == null)
            return;

        Bounds bounds = GetSimulationBounds();
        mat.SetTexture(MID_TemperatureTex3D, _temperatureA);
        mat.SetTexture(MID_VelocityTex3D, _velocity);
        mat.SetVector(MID_SimulationBoundsMin, bounds.min);
        mat.SetVector(MID_SimulationBoundsMax, bounds.max);
        mat.SetVector(MID_GridSize, new Vector4(gridX, gridY, gridZ, 0f));
        mat.SetFloat(MID_AmbientTemperature, ambientTemperature);
    }

    public void ApplyToSlicePropertyBlock(MaterialPropertyBlock block)
    {
        if (block == null || _temperatureA == null)
            return;

        Bounds bounds = GetSimulationBounds();
        block.SetTexture(MID_TemperatureTex3D, _temperatureA);
        block.SetTexture(MID_VelocityTex3D, _velocity);
        block.SetVector(MID_SimulationBoundsMin, bounds.min);
        block.SetVector(MID_SimulationBoundsMax, bounds.max);
        block.SetVector(MID_GridSize, new Vector4(gridX, gridY, gridZ, 0f));
        block.SetFloat(MID_AmbientTemperature, ambientTemperature);
    }

    #endregion

    #region Gizmos 視覺化

    private void OnDrawGizmos()
    {
        if (!drawGizmos)
            return;

        Bounds bounds = GetSimulationBounds();

        Gizmos.color = new Color(1f, 0.6f, 0f, 0.8f);
        Gizmos.matrix = Matrix4x4.identity;
        Gizmos.DrawWireCube(bounds.center, bounds.size);

#if UNITY_EDITOR
        Vector3 cell = CellSize;
        if (gridX <= 32 && gridY <= 16 && gridZ <= 32)
        {
            Gizmos.color = new Color(1f, 1f, 1f, 0.08f);

            for (int x = 0; x <= gridX; x++)
            {
                float px = bounds.min.x + x * cell.x;
                Gizmos.DrawLine(
                    new Vector3(px, bounds.min.y, bounds.min.z),
                    new Vector3(px, bounds.min.y, bounds.max.z)
                );
            }

            for (int z = 0; z <= gridZ; z++)
            {
                float pz = bounds.min.z + z * cell.z;
                Gizmos.DrawLine(
                    new Vector3(bounds.min.x, bounds.min.y, pz),
                    new Vector3(bounds.max.x, bounds.min.y, pz)
                );
            }
        }
#endif
    }

    #endregion
}
