using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
public class HeatWindMapVisualizer : MonoBehaviour
{
    [Tooltip("要讀取風場資料的熱模擬元件；留空時可自動搜尋。")]
    [SerializeField] private ClassroomHeatSimulation simulation;

    [Header("Sampling")]
    [Tooltip("當 simulation 為空時，自動在場景中搜尋 ClassroomHeatSimulation。")]
    [SerializeField] private bool autoFindSimulation = true;
    [Tooltip("非播放模式下是否也更新風場箭頭。")]
    [SerializeField] private bool updateInEditMode = false;
    [Tooltip("每次向 GPU 讀回速度場資料的最短時間間隔。")]
    [SerializeField, Min(0.05f)] private float readbackInterval = 0.2f;
    [Tooltip("X 軸每隔多少格取樣一次風場。")]
    [SerializeField, Min(1)] private int sampleStrideX = 2;
    [Tooltip("Y 軸每隔多少格取樣一次風場。")]
    [SerializeField, Min(1)] private int sampleStrideY = 1;
    [Tooltip("Z 軸每隔多少格取樣一次風場。")]
    [SerializeField, Min(1)] private int sampleStrideZ = 2;

    [Header("Display")]
    [Tooltip("是否繪製風場箭頭 Gizmo。")]
    [SerializeField] private bool drawGizmos = true;
    [Tooltip("只在選取物件時顯示箭頭。")]
    [SerializeField] private bool drawOnlyWhenSelected = false;
    [Tooltip("低於此風速的格點不顯示箭頭。")]
    [SerializeField, Min(0f)] private float minSpeedToDraw = 0.05f;
    [Tooltip("箭頭主體長度的倍率。")]
    [SerializeField, Min(0.01f)] private float arrowScale = 0.35f;
    [Tooltip("箭頭箭頭頭部的大小倍率。")]
    [SerializeField, Min(0.01f)] private float arrowHeadScale = 0.18f;
    [Tooltip("依風速顯示顏色的漸層。")]
    [SerializeField] private Gradient speedGradient;
    [Tooltip("對應漸層滿刻度的最大風速。")]
    [SerializeField, Min(0.01f)] private float gradientMaxSpeed = 3f;

    private Color[] _velocityData;
    private int _dataGridX;
    private int _dataGridY;
    private int _dataGridZ;
    private bool _hasValidReadback;
    private bool _readbackPending;
    private float _nextReadbackTime;
    private bool _loggedReadbackSupportWarning;

    private void Reset()
    {
        simulation = FindSimulationInScene();
        EnsureGradient();
    }

    private void OnEnable()
    {
        EnsureGradient();
        _nextReadbackTime = 0f;
        _hasValidReadback = false;
        _readbackPending = false;
    }

    private void OnValidate()
    {
        sampleStrideX = Mathf.Max(1, sampleStrideX);
        sampleStrideY = Mathf.Max(1, sampleStrideY);
        sampleStrideZ = Mathf.Max(1, sampleStrideZ);
        readbackInterval = Mathf.Max(0.05f, readbackInterval);
        minSpeedToDraw = Mathf.Max(0f, minSpeedToDraw);
        arrowScale = Mathf.Max(0.01f, arrowScale);
        arrowHeadScale = Mathf.Max(0.01f, arrowHeadScale);
        gradientMaxSpeed = Mathf.Max(0.01f, gradientMaxSpeed);
        EnsureGradient();
    }

    private void Update()
    {
        if (!Application.isPlaying && !updateInEditMode)
            return;

        if (autoFindSimulation && simulation == null)
            simulation = FindSimulationInScene();

        TryRequestVelocityReadback();
    }

    private void OnDrawGizmos()
    {
        if (!drawOnlyWhenSelected)
            DrawWindGizmos();
    }

    private void OnDrawGizmosSelected()
    {
        if (drawOnlyWhenSelected)
            DrawWindGizmos();
    }

    private void DrawWindGizmos()
    {
        if (!drawGizmos)
            return;

        if (autoFindSimulation && simulation == null)
            simulation = FindSimulationInScene();

        TryRequestVelocityReadback();

        if (!_hasValidReadback || simulation == null || !simulation.IsInitialized)
            return;

        int maxX = Mathf.Min(_dataGridX, simulation.GridX);
        int maxY = Mathf.Min(_dataGridY, simulation.GridY);
        int maxZ = Mathf.Min(_dataGridZ, simulation.GridZ);

        for (int z = 0; z < maxZ; z += sampleStrideZ)
        {
            for (int y = 0; y < maxY; y += sampleStrideY)
            {
                for (int x = 0; x < maxX; x += sampleStrideX)
                {
                    Vector3 velocity = GetVelocity(x, y, z);
                    float speed = velocity.magnitude;
                    if (speed < minSpeedToDraw)
                        continue;

                    Vector3 start = simulation.GridToWorldCenter(new Vector3Int(x, y, z));
                    Vector3 end = start + velocity * arrowScale;
                    Gizmos.color = EvaluateSpeedColor(speed);
                    Gizmos.DrawLine(start, end);
                    DrawArrowHead(end, velocity);
                }
            }
        }
    }

    private void DrawArrowHead(Vector3 tip, Vector3 velocity)
    {
        float magnitude = velocity.magnitude;
        if (magnitude <= 1e-5f)
            return;

        Vector3 dir = velocity / magnitude;
        Vector3 side = Vector3.Cross(dir, Vector3.up);
        if (side.sqrMagnitude < 1e-5f)
            side = Vector3.Cross(dir, Vector3.right);

        side.Normalize();
        float headLength = arrowHeadScale * Mathf.Max(0.35f, magnitude * arrowScale);
        Vector3 back = -dir * headLength;
        Vector3 wing = side * headLength * 0.5f;

        Gizmos.DrawLine(tip, tip + back + wing);
        Gizmos.DrawLine(tip, tip + back - wing);
    }

    private void TryRequestVelocityReadback()
    {
        if (!Application.isPlaying && !updateInEditMode)
            return;

        if (_readbackPending || simulation == null || !simulation.IsInitialized)
            return;

        if (Time.realtimeSinceStartup < _nextReadbackTime)
            return;

        RenderTexture velocityTexture = simulation.VelocityTexture;
        if (velocityTexture == null || !velocityTexture.IsCreated())
            return;

        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            if (!_loggedReadbackSupportWarning)
            {
                Debug.LogWarning("[HeatWindMapVisualizer] Async GPU readback is not supported on this device. Wind map visualization is disabled.");
                _loggedReadbackSupportWarning = true;
            }
            return;
        }

        _loggedReadbackSupportWarning = false;
        _readbackPending = true;
        _nextReadbackTime = Time.realtimeSinceStartup + readbackInterval;
        AsyncGPUReadback.Request(velocityTexture, 0, request => HandleReadback(request, velocityTexture));
    }

    private void HandleReadback(AsyncGPUReadbackRequest request, RenderTexture sourceTexture)
    {
        _readbackPending = false;

        if (this == null || !isActiveAndEnabled)
            return;

        if (simulation == null || sourceTexture != simulation.VelocityTexture)
            return;

        if (request.hasError)
        {
            _hasValidReadback = false;
            return;
        }

        var data = request.GetData<Color>();
        if (!data.IsCreated || data.Length == 0)
        {
            _hasValidReadback = false;
            return;
        }

        if (_velocityData == null || _velocityData.Length != data.Length)
            _velocityData = new Color[data.Length];

        data.CopyTo(_velocityData);
        _dataGridX = sourceTexture.width;
        _dataGridY = sourceTexture.height;
        _dataGridZ = sourceTexture.volumeDepth;
        _hasValidReadback = true;
    }

    private Vector3 GetVelocity(int x, int y, int z)
    {
        int index = x + _dataGridX * (y + _dataGridY * z);
        if (_velocityData == null || index < 0 || index >= _velocityData.Length)
            return Vector3.zero;

        Color c = _velocityData[index];
        return new Vector3(c.r, c.g, c.b);
    }

    private Color EvaluateSpeedColor(float speed)
    {
        if (speedGradient == null)
            EnsureGradient();

        return speedGradient.Evaluate(Mathf.Clamp01(speed / gradientMaxSpeed));
    }

    private void EnsureGradient()
    {
        if (speedGradient != null && speedGradient.colorKeys != null && speedGradient.colorKeys.Length > 0)
            return;

        speedGradient = new Gradient();
        speedGradient.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.1f, 0.75f, 1f), 0f),
                new GradientColorKey(new Color(0.25f, 1f, 0.35f), 0.5f),
                new GradientColorKey(new Color(1f, 0.55f, 0.1f), 1f)
            },
            new[]
            {
                new GradientAlphaKey(0.9f, 0f),
                new GradientAlphaKey(0.9f, 1f)
            });
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
