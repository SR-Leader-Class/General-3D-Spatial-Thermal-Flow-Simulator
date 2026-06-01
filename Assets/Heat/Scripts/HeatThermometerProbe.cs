/*
 * Created :    Summer 2026
 * Author :     蘇家賢
 * Project :    General-3D-Spatial-Thermal-Flow-Simulator
 * Filename :   HeatThermometerProbe.cs
 * 
 */

using UnityEngine;
using UnityEngine.UI;

[ExecuteAlways]
public class HeatThermometerProbe : MonoBehaviour
{
    [Header("Sampling")]
    [Tooltip("溫度計名稱。空白時使用 GameObject 名稱。")]
    [SerializeField] private string thermometerName = "";

    [Tooltip("取樣點相對於此物件的位置。")]
    [SerializeField] private Vector3 localSampleOffset = Vector3.zero;

    [Header("Display")]
    [Tooltip("UI Text 顯示。可不填。")]
    [SerializeField] private Text uiText;

    [Tooltip("世界空間 TextMesh 顯示。可不填。")]
    [SerializeField] private TextMesh worldText;

    [Tooltip("有效讀值格式。{0}=名稱，{1}=溫度，{2}=GridX，{3}=GridY，{4}=GridZ")]
    [SerializeField] private string displayFormat = "{0}: {1:0.0} °C";

    [Tooltip("無效讀值格式。通常代表探針超出模擬範圍。")]
    [SerializeField] private string invalidFormat = "{0}: -- °C";

    [Header("Indicator")]
    [Tooltip("用來依溫度變色的 Renderer，例如溫度計外殼、球體、指示燈。可不填。")]
    [SerializeField] private Renderer indicatorRenderer;

    [Tooltip("溫度色彩映射的最低溫。")]
    [SerializeField] private float colorMinTemperature = 18f;

    [Tooltip("溫度色彩映射的最高溫。")]
    [SerializeField] private float colorMaxTemperature = 35f;

    [SerializeField] private Gradient temperatureColorGradient;

    [Header("Gizmos")]
    [SerializeField] private bool drawGizmo = true;
    [SerializeField, Min(0.01f)] private float gizmoRadius = 0.08f;

    public float CurrentTemperature { get; private set; } = float.NaN;
    public bool HasValidReading { get; private set; }
    public Vector3Int GridPosition { get; private set; }

    public Vector3 SampleWorldPosition
    {
        get { return transform.TransformPoint(localSampleOffset); }
    }

    public string DisplayName
    {
        get
        {
            return string.IsNullOrWhiteSpace(thermometerName)
                ? gameObject.name
                : thermometerName;
        }
    }

    private HeatThermometerSystem _registeredSystem;
    private MaterialPropertyBlock _propertyBlock;

    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    private void Reset()
    {
        uiText = GetComponentInChildren<Text>();
        worldText = GetComponentInChildren<TextMesh>();
        indicatorRenderer = GetComponentInChildren<Renderer>();

        EnsureDefaultGradient();
    }

    private void OnEnable()
    {
        EnsureDefaultGradient();
        TryRegisterToSystem();
        UpdateDisplay();
    }

    private void OnDisable()
    {
        if (_registeredSystem != null)
            _registeredSystem.UnregisterProbe(this);

        _registeredSystem = null;
    }

    private void OnValidate()
    {
        EnsureDefaultGradient();
        UpdateDisplay();
    }

    public void ApplyReading(float temperatureCelsius, bool valid, Vector3Int gridPosition)
    {
        CurrentTemperature = temperatureCelsius;
        HasValidReading = valid;
        GridPosition = gridPosition;

        UpdateDisplay();
        UpdateIndicatorColor();
    }

    private void TryRegisterToSystem()
    {
        if (_registeredSystem != null)
            return;

        _registeredSystem = GetComponentInParent<HeatThermometerSystem>();

        if (_registeredSystem == null)
            _registeredSystem = FindObjectOfType<HeatThermometerSystem>();

        if (_registeredSystem != null)
            _registeredSystem.RegisterProbe(this);
    }

    private void UpdateDisplay()
    {
        string text;

        if (HasValidReading && !float.IsNaN(CurrentTemperature))
        {
            text = string.Format(
                displayFormat,
                DisplayName,
                CurrentTemperature,
                GridPosition.x,
                GridPosition.y,
                GridPosition.z
            );
        }
        else
        {
            text = string.Format(invalidFormat, DisplayName);
        }

        if (uiText != null)
            uiText.text = text;

        if (worldText != null)
            worldText.text = text;
    }

    private void UpdateIndicatorColor()
    {
        Color color = GetCurrentColor();

        if (worldText != null)
            worldText.color = color;

        if (indicatorRenderer == null)
            return;

        if (_propertyBlock == null)
            _propertyBlock = new MaterialPropertyBlock();

        indicatorRenderer.GetPropertyBlock(_propertyBlock);

        _propertyBlock.SetColor(ColorId, color);
        _propertyBlock.SetColor(BaseColorId, color);

        indicatorRenderer.SetPropertyBlock(_propertyBlock);
    }

    private Color GetCurrentColor()
    {
        if (!HasValidReading || float.IsNaN(CurrentTemperature))
            return Color.gray;

        float t = Mathf.InverseLerp(
            colorMinTemperature,
            colorMaxTemperature,
            CurrentTemperature
        );

        return temperatureColorGradient.Evaluate(t);
    }

    private void EnsureDefaultGradient()
    {
        if (temperatureColorGradient != null)
            return;

        temperatureColorGradient = new Gradient();

        temperatureColorGradient.SetKeys(
            new GradientColorKey[]
            {
                new GradientColorKey(new Color(0.1f, 0.3f, 1f), 0f),
                new GradientColorKey(new Color(0.1f, 1f, 1f), 0.33f),
                new GradientColorKey(new Color(1f, 0.9f, 0.1f), 0.66f),
                new GradientColorKey(new Color(1f, 0.15f, 0.05f), 1f)
            },
            new GradientAlphaKey[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f)
            }
        );
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmo)
            return;

        Gizmos.color = GetCurrentColor();
        Gizmos.DrawSphere(SampleWorldPosition, gizmoRadius);

        Gizmos.color = new Color(1f, 1f, 1f, 0.8f);
        Gizmos.DrawWireSphere(SampleWorldPosition, gizmoRadius);
    }
}
