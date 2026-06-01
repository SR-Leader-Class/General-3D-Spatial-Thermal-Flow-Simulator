using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
[RequireComponent(typeof(Renderer))]
public class HeatSlicePlaneController : MonoBehaviour
{
    // 這個元件會把一個平面對齊到模擬體積中的指定切片位置，
    // 並把 ClassroomHeatSimulation 的 3D 溫度貼圖綁定到材質做可視化。

    public enum SliceOrientation
    {
        HorizontalXZ,
        VerticalXY,
        VerticalYZ
    }

    [Header("References")]
    [Tooltip("提供溫度與風場 3D 貼圖的熱模擬元件。")]
    [SerializeField] private ClassroomHeatSimulation simulation;
    [Tooltip("要套用切片材質與屬性的 Renderer。")]
    [SerializeField] private Renderer targetRenderer;

    [Header("Slice Settings")]
    [Tooltip("切片平面的方向。")]
    [SerializeField] private SliceOrientation orientation = SliceOrientation.HorizontalXZ;
    [Tooltip("切片在模擬體積中的相對位置，0 到 1 代表從一側到另一側。")]
    [SerializeField, Range(0.01f, 0.99f)] private float normalizedSlice = 0.5f;
    [Tooltip("是否每幀自動更新切片位置與材質參數。")]
    [SerializeField] private bool updateEveryFrame = true;
    [Tooltip("在編輯模式下是否直接改 sharedMaterial，而不是使用 PropertyBlock。")]
    [SerializeField] private bool useSharedMaterialInEditMode = false;

    [Header("Display Settings")]
    [Tooltip("顏色映射的最低溫度。")]
    [SerializeField] private float minTemp = 20f;
    [Tooltip("顏色映射的最高溫度。")]
    [SerializeField] private float maxTemp = 30f;
    [Tooltip("切片整體透明度。")]
    [SerializeField, Range(0f, 1f)] private float alpha = 0.85f;
    [Tooltip("超出模擬範圍區域的透明度。")]
    [SerializeField, Range(0f, 1f)] private float outOfBoundsAlpha = 0f;
    [Tooltip("是否在切片上顯示格線。")]
    [SerializeField] private bool showGrid = true;
    [Tooltip("格線粗細。")]
    [SerializeField, Range(0.001f, 0.2f)] private float gridLineWidth = 0.03f;

    [Header("Wind Overlay")]
    [Tooltip("是否在切片上疊加 2D 風場箭頭。")]
    [SerializeField] private bool showWindOverlay = true;
    [Tooltip("每單位區域繪製多少風場箭頭。")]
    [SerializeField, Min(1f)] private float windCellDensity = 12f;
    [Tooltip("風場箭頭線條粗細。")]
    [SerializeField, Range(0.005f, 0.12f)] private float windArrowThickness = 0.025f;
    [Tooltip("風場箭頭頭部大小。")]
    [SerializeField, Range(0.05f, 0.45f)] private float windArrowHeadSize = 0.16f;
    [Tooltip("風場箭頭長度倍率。")]
    [SerializeField, Range(0.2f, 1.2f)] private float windArrowLengthScale = 0.7f;
    [Tooltip("低於此速度的風場箭頭不顯示。")]
    [SerializeField, Min(0f)] private float windMinSpeed = 0.05f;
    [Tooltip("對應風場箭頭滿刻度長度的最大速度。")]
    [SerializeField, Min(0.01f)] private float windMaxSpeed = 2.5f;
    [Tooltip("風場箭頭整體透明度。")]
    [SerializeField, Range(0f, 1f)] private float windOpacity = 0.9f;

    [Header("Performance")]
    [Tooltip("自動搜尋模擬元件時的最短搜尋間隔秒數。")]
    [SerializeField, Min(0.1f)] private float simulationSearchInterval = 0.5f;

    [Header("Export")]
    [Tooltip("匯出切片熱圖時使用的圖片寬度。")]
    [SerializeField, Min(64)] private int exportWidth = 1024;
    [Tooltip("匯出切片熱圖時使用的圖片高度；若保持切片比例則會自動覆寫。")]
    [SerializeField, Min(64)] private int exportHeight = 1024;
    [Tooltip("是否依照目前切片的實際長寬比自動調整輸出高度。")]
    [SerializeField] private bool preserveSliceAspectOnExport = true;
    [Tooltip("匯出圖片檔名的前綴。")]
    [SerializeField] private string exportFilePrefix = "HeatSlice";
    [Tooltip("匯出時清除背景所使用的顏色。若 Alpha 為 0 可得到透明背景。")]
    [SerializeField] private Color exportBackgroundColor = new Color(0f, 0f, 0f, 0f);

    public float MinDisplayTemperature => minTemp;
    public float MaxDisplayTemperature => maxTemp;

    private MaterialPropertyBlock _propertyBlock;
    private float _nextSimulationSearchTime;
    private bool _stateInitialized;

    private Bounds _lastBounds;
    private SliceOrientation _lastOrientation;
    private float _lastNormalizedSlice;
    private float _lastMinTemp;
    private float _lastMaxTemp;
    private float _lastAlpha;
    private float _lastOutOfBoundsAlpha;
    private bool _lastShowGrid;
    private float _lastGridLineWidth;
    private bool _lastShowWindOverlay;
    private float _lastWindCellDensity;
    private float _lastWindArrowThickness;
    private float _lastWindArrowHeadSize;
    private float _lastWindArrowLengthScale;
    private float _lastWindMinSpeed;
    private float _lastWindMaxSpeed;
    private float _lastWindOpacity;
    private Texture _lastTemperatureTexture;
    private int _lastTemperatureVersion = -1;
    private bool _lastUsedSharedMode;

    private static readonly int MID_MinTemp = Shader.PropertyToID("_MinTemp");
    private static readonly int MID_MaxTemp = Shader.PropertyToID("_MaxTemp");
    private static readonly int MID_Alpha = Shader.PropertyToID("_Alpha");
    private static readonly int MID_OutOfBoundsAlpha = Shader.PropertyToID("_OutOfBoundsAlpha");
    private static readonly int MID_ShowGrid = Shader.PropertyToID("_ShowGrid");
    private static readonly int MID_GridLineWidth = Shader.PropertyToID("_GridLineWidth");
    private static readonly int MID_WindOverlayEnabled = Shader.PropertyToID("_WindOverlayEnabled");
    private static readonly int MID_WindCellDensity = Shader.PropertyToID("_WindCellDensity");
    private static readonly int MID_WindArrowThickness = Shader.PropertyToID("_WindArrowThickness");
    private static readonly int MID_WindArrowHeadSize = Shader.PropertyToID("_WindArrowHeadSize");
    private static readonly int MID_WindArrowLengthScale = Shader.PropertyToID("_WindArrowLengthScale");
    private static readonly int MID_WindMinSpeed = Shader.PropertyToID("_WindMinSpeed");
    private static readonly int MID_WindMaxSpeed = Shader.PropertyToID("_WindMaxSpeed");
    private static readonly int MID_WindOpacity = Shader.PropertyToID("_WindOpacity");
    private static readonly int MID_WindOrientation = Shader.PropertyToID("_WindOrientation");

    private void Reset()
    {
        targetRenderer = GetComponent<Renderer>();
        if (simulation == null)
            simulation = FindSimulationInScene();
    }

    private void OnEnable()
    {
        // 啟用時立刻抓參考並刷新一次顯示，避免進場第一幀空白。
        EnsureReferences(allowSceneSearch: true);
        ForceRefresh();
        UpdateSliceImmediate();
    }

    private void OnValidate()
    {
        normalizedSlice = Mathf.Clamp01(normalizedSlice);
        minTemp = Mathf.Min(minTemp, maxTemp - 0.001f);
        simulationSearchInterval = Mathf.Max(0.1f, simulationSearchInterval);
        windCellDensity = Mathf.Max(1f, windCellDensity);
        windArrowThickness = Mathf.Clamp(windArrowThickness, 0.005f, 0.12f);
        windArrowHeadSize = Mathf.Clamp(windArrowHeadSize, 0.05f, 0.45f);
        windArrowLengthScale = Mathf.Clamp(windArrowLengthScale, 0.2f, 1.2f);
        windMinSpeed = Mathf.Max(0f, windMinSpeed);
        windMaxSpeed = Mathf.Max(0.01f, windMaxSpeed);
        windOpacity = Mathf.Clamp01(windOpacity);
        if (windMaxSpeed < windMinSpeed)
            windMaxSpeed = windMinSpeed;
        exportWidth = Mathf.Max(64, exportWidth);
        exportHeight = Mathf.Max(64, exportHeight);
        if (string.IsNullOrWhiteSpace(exportFilePrefix))
            exportFilePrefix = "HeatSlice";

        EnsureReferences(allowSceneSearch: true);
        ForceRefresh();
        UpdateSliceImmediate();
    }

    private void Update()
    {
        if (!updateEveryFrame)
            return;

        // 依需求每幀同步切片位置與材質參數。
        UpdateSliceImmediate();
    }

    [ContextMenu("Update Slice Now")]
    public void UpdateSliceImmediate()
    {
        EnsureReferences(allowSceneSearch: true);
        if (simulation == null || targetRenderer == null)
            return;

        if (!IsSimulationDisplayReady())
        {
            ClearMaterialBindings();
            return;
        }

        Bounds bounds = simulation.GetSimulationBounds();

        // 只有在狀態變更時才更新 Transform/材質，減少不必要 SetPropertyBlock 成本。
        bool boundsChanged = !_stateInitialized || !Approximately(bounds, _lastBounds);
        bool sliceChanged = !_stateInitialized ||
                            orientation != _lastOrientation ||
                            !Mathf.Approximately(normalizedSlice, _lastNormalizedSlice);

        if (boundsChanged || sliceChanged)
        {
            UpdateTransformToMatchSlice(bounds);
        }

        bool displayChanged = !_stateInitialized ||
                              !Mathf.Approximately(minTemp, _lastMinTemp) ||
                              !Mathf.Approximately(maxTemp, _lastMaxTemp) ||
                              !Mathf.Approximately(alpha, _lastAlpha) ||
                              !Mathf.Approximately(outOfBoundsAlpha, _lastOutOfBoundsAlpha) ||
                              showGrid != _lastShowGrid ||
                              !Mathf.Approximately(gridLineWidth, _lastGridLineWidth) ||
                              showWindOverlay != _lastShowWindOverlay ||
                              !Mathf.Approximately(windCellDensity, _lastWindCellDensity) ||
                              !Mathf.Approximately(windArrowThickness, _lastWindArrowThickness) ||
                              !Mathf.Approximately(windArrowHeadSize, _lastWindArrowHeadSize) ||
                              !Mathf.Approximately(windArrowLengthScale, _lastWindArrowLengthScale) ||
                              !Mathf.Approximately(windMinSpeed, _lastWindMinSpeed) ||
                              !Mathf.Approximately(windMaxSpeed, _lastWindMaxSpeed) ||
                              !Mathf.Approximately(windOpacity, _lastWindOpacity);

        bool modeChanged = !_stateInitialized || _lastUsedSharedMode != useSharedMaterialInEditMode;
        bool tempTextureChanged = !_stateInitialized ||
                                  simulation.TemperatureTexture != _lastTemperatureTexture ||
                                  simulation.TemperatureSwapVersion != _lastTemperatureVersion;

        if (boundsChanged || sliceChanged || displayChanged || modeChanged || tempTextureChanged)
        {
            UpdateMaterialBindings();
        }

        _lastBounds = bounds;
        _lastOrientation = orientation;
        _lastNormalizedSlice = normalizedSlice;
        _lastMinTemp = minTemp;
        _lastMaxTemp = maxTemp;
        _lastAlpha = alpha;
        _lastOutOfBoundsAlpha = outOfBoundsAlpha;
        _lastShowGrid = showGrid;
        _lastGridLineWidth = gridLineWidth;
        _lastShowWindOverlay = showWindOverlay;
        _lastWindCellDensity = windCellDensity;
        _lastWindArrowThickness = windArrowThickness;
        _lastWindArrowHeadSize = windArrowHeadSize;
        _lastWindArrowLengthScale = windArrowLengthScale;
        _lastWindMinSpeed = windMinSpeed;
        _lastWindMaxSpeed = windMaxSpeed;
        _lastWindOpacity = windOpacity;
        _lastTemperatureTexture = simulation.TemperatureTexture;
        _lastTemperatureVersion = simulation.TemperatureSwapVersion;
        _lastUsedSharedMode = useSharedMaterialInEditMode;
        _stateInitialized = true;
    }

    private void ForceRefresh()
    {
        _stateInitialized = false;
    }

    private void EnsureReferences(bool allowSceneSearch)
    {
        if (targetRenderer == null)
            targetRenderer = GetComponent<Renderer>();

        if (simulation != null || !allowSceneSearch)
            return;

        // 限流搜尋頻率，避免編輯器模式下頻繁 FindObjectOfType。
        if (Time.realtimeSinceStartup < _nextSimulationSearchTime)
            return;

        _nextSimulationSearchTime = Time.realtimeSinceStartup + simulationSearchInterval;
        simulation = FindSimulationInScene();
    }

    private static ClassroomHeatSimulation FindSimulationInScene()
    {
#if UNITY_2023_1_OR_NEWER
        return FindFirstObjectByType<ClassroomHeatSimulation>();
#else
        return FindObjectOfType<ClassroomHeatSimulation>();
#endif
    }

    public static Color EvaluateTemperatureRamp01(float normalizedTemperature)
    {
        float t = Mathf.Clamp01(normalizedTemperature);

        Color c1 = new Color(0.0f, 0.1f, 0.8f);
        Color c2 = new Color(0.0f, 0.8f, 1.0f);
        Color c3 = new Color(1.0f, 0.95f, 0.2f);
        Color c4 = new Color(1.0f, 0.4f, 0.0f);
        Color c5 = new Color(0.85f, 0.0f, 0.0f);

        float u = t * 4f;
        float s1 = Mathf.Clamp01(u);
        float s2 = Mathf.Clamp01(u - 1f);
        float s3 = Mathf.Clamp01(u - 2f);
        float s4 = Mathf.Clamp01(u - 3f);

        Color color = Color.Lerp(c1, c2, s1);
        color = Color.Lerp(color, c3, s2);
        color = Color.Lerp(color, c4, s3);
        color = Color.Lerp(color, c5, s4);
        color.a = 1f;
        return color;
    }

    #region 公式計算 - 邊界與切片座標

    private static bool Approximately(Bounds a, Bounds b)
    {
        // Bounds 沒有直接 Approximately，比較 center/size 的平方距離。
        const float epsilon = 1e-4f;
        return (a.center - b.center).sqrMagnitude <= epsilon * epsilon &&
               (a.size - b.size).sqrMagnitude <= epsilon * epsilon;
    }

    private void UpdateTransformToMatchSlice(Bounds bounds)
    {
        Vector3 center = bounds.center;
        Vector3 size = bounds.size;

        Vector3 pos = center;
        Quaternion rot = Quaternion.identity;
        Vector3 scale = Vector3.one;

        // 依切片方向決定：
        // - 平面位置（沿對應軸的線性插值）
        // - 平面旋轉（對齊 XY/XZ/YZ）
        // - 平面縮放（對齊模擬體積截面大小）
        switch (orientation)
        {
            case SliceOrientation.HorizontalXZ:
                pos = new Vector3(center.x, Mathf.Lerp(bounds.min.y, bounds.max.y, normalizedSlice), center.z);
                rot = Quaternion.Euler(90f, 0f, 0f);
                scale = new Vector3(size.x, size.z, 1f);
                break;

            case SliceOrientation.VerticalXY:
                pos = new Vector3(center.x, center.y, Mathf.Lerp(bounds.min.z, bounds.max.z, normalizedSlice));
                rot = Quaternion.identity;
                scale = new Vector3(size.x, size.y, 1f);
                break;

            case SliceOrientation.VerticalYZ:
                pos = new Vector3(Mathf.Lerp(bounds.min.x, bounds.max.x, normalizedSlice), center.y, center.z);
                rot = Quaternion.Euler(0f, 90f, 0f);
                scale = new Vector3(size.z, size.y, 1f);
                break;
        }

        transform.SetPositionAndRotation(pos, rot);
        transform.localScale = scale;
    }

    private void UpdateMaterialBindings()
    {
        if (targetRenderer == null || simulation == null)
            return;

        // 編輯模式可選擇直接改 sharedMaterial（方便即時預覽），
        // 遊戲模式或一般情況使用 MaterialPropertyBlock 避免污染材質資產。
        if (!Application.isPlaying && useSharedMaterialInEditMode)
        {
            targetRenderer.SetPropertyBlock(null);
            Material shared = targetRenderer.sharedMaterial;
            if (shared == null)
                return;

            simulation.ApplyToSliceMaterial(shared);
            ApplySliceDisplayProperties(shared);
            return;
        }

        if (_propertyBlock == null)
            _propertyBlock = new MaterialPropertyBlock();

        _propertyBlock.Clear();
        simulation.ApplyToSlicePropertyBlock(_propertyBlock);
        _propertyBlock.SetFloat(MID_MinTemp, minTemp);
        _propertyBlock.SetFloat(MID_MaxTemp, maxTemp);
        _propertyBlock.SetFloat(MID_Alpha, alpha);
        _propertyBlock.SetFloat(MID_OutOfBoundsAlpha, outOfBoundsAlpha);
        _propertyBlock.SetFloat(MID_ShowGrid, showGrid ? 1f : 0f);
        _propertyBlock.SetFloat(MID_GridLineWidth, gridLineWidth);
        ApplyWindOverlayProperties(_propertyBlock);
        targetRenderer.SetPropertyBlock(_propertyBlock);
    }

    private void ApplySliceDisplayProperties(Material target)
    {
        if (target == null)
            return;

        target.SetFloat(MID_MinTemp, minTemp);
        target.SetFloat(MID_MaxTemp, maxTemp);
        target.SetFloat(MID_Alpha, alpha);
        target.SetFloat(MID_OutOfBoundsAlpha, outOfBoundsAlpha);
        target.SetFloat(MID_ShowGrid, showGrid ? 1f : 0f);
        target.SetFloat(MID_GridLineWidth, gridLineWidth);
        ApplyWindOverlayProperties(target);
    }

    private void ApplyWindOverlayProperties(Material target)
    {
        if (target == null)
            return;

        target.SetFloat(MID_WindOverlayEnabled, showWindOverlay ? 1f : 0f);
        target.SetFloat(MID_WindCellDensity, windCellDensity);
        target.SetFloat(MID_WindArrowThickness, windArrowThickness);
        target.SetFloat(MID_WindArrowHeadSize, windArrowHeadSize);
        target.SetFloat(MID_WindArrowLengthScale, windArrowLengthScale);
        target.SetFloat(MID_WindMinSpeed, windMinSpeed);
        target.SetFloat(MID_WindMaxSpeed, windMaxSpeed);
        target.SetFloat(MID_WindOpacity, windOpacity);
        target.SetFloat(MID_WindOrientation, (float)orientation);
    }

    private void ApplyWindOverlayProperties(MaterialPropertyBlock target)
    {
        if (target == null)
            return;

        target.SetFloat(MID_WindOverlayEnabled, showWindOverlay ? 1f : 0f);
        target.SetFloat(MID_WindCellDensity, windCellDensity);
        target.SetFloat(MID_WindArrowThickness, windArrowThickness);
        target.SetFloat(MID_WindArrowHeadSize, windArrowHeadSize);
        target.SetFloat(MID_WindArrowLengthScale, windArrowLengthScale);
        target.SetFloat(MID_WindMinSpeed, windMinSpeed);
        target.SetFloat(MID_WindMaxSpeed, windMaxSpeed);
        target.SetFloat(MID_WindOpacity, windOpacity);
        target.SetFloat(MID_WindOrientation, (float)orientation);
    }

    private bool IsSimulationDisplayReady()
    {
        if (simulation == null || !simulation.IsInitialized)
            return false;

        Texture tex = simulation.TemperatureTexture;
        if (tex == null)
            return false;

        RenderTexture rt = tex as RenderTexture;
        return rt != null &&
               rt.IsCreated() &&
               rt.dimension == UnityEngine.Rendering.TextureDimension.Tex3D &&
               rt.width > 0 &&
               rt.height > 0 &&
               rt.volumeDepth > 0;
    }

    private void ClearMaterialBindings()
    {
        if (targetRenderer == null)
            return;

        targetRenderer.SetPropertyBlock(null);
        _stateInitialized = false;
        _lastTemperatureTexture = null;
        _lastTemperatureVersion = -1;
    }

    public void SetNormalizedSlice(float value)
    {
        normalizedSlice = Mathf.Clamp01(value);
        UpdateSliceImmediate();
    }

    public void SetOrientation(SliceOrientation newOrientation)
    {
        orientation = newOrientation;
        UpdateSliceImmediate();
    }

    [ContextMenu("Export Slice Heatmap PNG")]
    public void ExportSliceHeatmapPng()
    {
        EnsureReferences(allowSceneSearch: true);
        UpdateSliceImmediate();

        if (!TryExportSliceHeatmapPng(out string outputPath))
            return;

        Debug.Log($"[HeatSlicePlaneController] Slice heatmap exported to: {outputPath}");
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }

    public float GetWorldSlicePosition()
    {
        if (simulation == null)
            return 0f;

        Bounds bounds = simulation.GetSimulationBounds();

        // 回傳目前切片在世界座標中的實際軸向位置。
        switch (orientation)
        {
            case SliceOrientation.HorizontalXZ:
                return Mathf.Lerp(bounds.min.y, bounds.max.y, normalizedSlice);
            case SliceOrientation.VerticalXY:
                return Mathf.Lerp(bounds.min.z, bounds.max.z, normalizedSlice);
            case SliceOrientation.VerticalYZ:
                return Mathf.Lerp(bounds.min.x, bounds.max.x, normalizedSlice);
            default:
                return 0f;
        }
    }

    private bool TryExportSliceHeatmapPng(out string outputPath)
    {
        outputPath = string.Empty;

        if (simulation == null)
        {
            Debug.LogWarning("[HeatSlicePlaneController] Cannot export slice heatmap because simulation is null.");
            return false;
        }

        if (targetRenderer == null)
        {
            Debug.LogWarning("[HeatSlicePlaneController] Cannot export slice heatmap because targetRenderer is null.");
            return false;
        }

        if (!IsSimulationDisplayReady())
        {
            Debug.LogWarning("[HeatSlicePlaneController] Cannot export slice heatmap because the simulation display is not ready.");
            return false;
        }

        Bounds bounds = simulation.GetSimulationBounds();
        GetSliceCaptureDimensions(bounds, out float sliceWidth, out float sliceHeight);

        int width = Mathf.Max(64, exportWidth);
        int height = preserveSliceAspectOnExport
            ? Mathf.Max(64, Mathf.RoundToInt(width * (sliceHeight / Mathf.Max(sliceWidth, 1e-5f))))
            : Mathf.Max(64, exportHeight);

        RenderTexture renderTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        renderTexture.name = "HeatSliceCaptureRT";
        renderTexture.Create();

        Texture2D image = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
        CommandBuffer commandBuffer = new CommandBuffer
        {
            name = "Heat Slice Capture"
        };
        Material[] captureMaterials = null;

        try
        {
            Vector3 forward = GetSliceCaptureForward();
            Vector3 up = GetSliceCaptureUp();
            Vector3 center = transform.position;
            float distance = 2f;
            Vector3 cameraPosition = center - forward * distance;

            Quaternion rotation = Quaternion.LookRotation(forward, up);
            Matrix4x4 viewMatrix = Matrix4x4.TRS(cameraPosition, rotation, Vector3.one).inverse;

            float aspect = width / Mathf.Max((float)height, 1f);
            float halfWidth = sliceWidth * 0.5f;
            float halfHeight = sliceHeight * 0.5f;
            float orthographicHalfHeight = Mathf.Max(halfHeight, halfWidth / Mathf.Max(aspect, 1e-5f));
            float orthographicHalfWidth = orthographicHalfHeight * aspect;

            Matrix4x4 projectionMatrix = Matrix4x4.Ortho(
                -orthographicHalfWidth,
                orthographicHalfWidth,
                -orthographicHalfHeight,
                orthographicHalfHeight,
                0.01f,
                10f);

            commandBuffer.SetRenderTarget(renderTexture);
            commandBuffer.ClearRenderTarget(true, true, exportBackgroundColor);
            commandBuffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);

            Material[] materials = targetRenderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
            {
                Debug.LogWarning("[HeatSlicePlaneController] Cannot export slice heatmap because targetRenderer has no material.");
                return false;
            }

            captureMaterials = new Material[materials.Length];

            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null)
                    continue;

                Material captureMaterial = new Material(material);
                simulation.ApplyToSliceMaterial(captureMaterial);
                ApplySliceDisplayProperties(captureMaterial);
                captureMaterials[i] = captureMaterial;
                commandBuffer.DrawRenderer(targetRenderer, captureMaterial, i, 0);
            }

            Graphics.ExecuteCommandBuffer(commandBuffer);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = renderTexture;
            image.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
            image.Apply(false, false);
            RenderTexture.active = previous;

            byte[] pngBytes = image.EncodeToPNG();
            if (pngBytes == null || pngBytes.Length == 0)
            {
                Debug.LogWarning("[HeatSlicePlaneController] PNG encoding failed while exporting slice heatmap.");
                return false;
            }

            string outputDirectory = Path.Combine(Application.dataPath, "Output");
            Directory.CreateDirectory(outputDirectory);

            string fileName = $"{SanitizeFileName(exportFilePrefix)}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            outputPath = Path.Combine(outputDirectory, fileName);
            File.WriteAllBytes(outputPath, pngBytes);
            return true;
        }
        finally
        {
            commandBuffer.Release();

            if (captureMaterials != null)
            {
                for (int i = 0; i < captureMaterials.Length; i++)
                {
                    Material material = captureMaterials[i];
                    if (material == null)
                        continue;

#if UNITY_EDITOR
                    if (!Application.isPlaying)
                        DestroyImmediate(material);
                    else
#endif
                        Destroy(material);
                }
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                DestroyImmediate(image);
                if (renderTexture.IsCreated())
                    renderTexture.Release();
                DestroyImmediate(renderTexture);
            }
            else
#endif
            {
                Destroy(image);
                if (renderTexture.IsCreated())
                    renderTexture.Release();
                Destroy(renderTexture);
            }
        }
    }

    private void GetSliceCaptureDimensions(Bounds bounds, out float width, out float height)
    {
        Vector3 size = bounds.size;
        switch (orientation)
        {
            case SliceOrientation.HorizontalXZ:
                width = size.x;
                height = size.z;
                break;
            case SliceOrientation.VerticalXY:
                width = size.x;
                height = size.y;
                break;
            case SliceOrientation.VerticalYZ:
                width = size.z;
                height = size.y;
                break;
            default:
                width = size.x;
                height = size.y;
                break;
        }
    }

    private Vector3 GetSliceCaptureForward()
    {
        switch (orientation)
        {
            case SliceOrientation.HorizontalXZ:
                return Vector3.down;
            case SliceOrientation.VerticalXY:
                return Vector3.forward;
            case SliceOrientation.VerticalYZ:
                return Vector3.left;
            default:
                return transform.forward;
        }
    }

    private Vector3 GetSliceCaptureUp()
    {
        switch (orientation)
        {
            case SliceOrientation.HorizontalXZ:
                return Vector3.forward;
            case SliceOrientation.VerticalXY:
            case SliceOrientation.VerticalYZ:
                return Vector3.up;
            default:
                return transform.up;
        }
    }

    private static string SanitizeFileName(string value)
    {
        string result = string.IsNullOrWhiteSpace(value) ? "HeatSlice" : value.Trim();
        char[] invalidChars = Path.GetInvalidFileNameChars();
        for (int i = 0; i < invalidChars.Length; i++)
        {
            result = result.Replace(invalidChars[i], '_');
        }

        return string.IsNullOrWhiteSpace(result) ? "HeatSlice" : result;
    }

    #endregion
}
