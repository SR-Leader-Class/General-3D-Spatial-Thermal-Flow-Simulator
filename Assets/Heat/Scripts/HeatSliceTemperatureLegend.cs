using UnityEngine;
using UnityEngine.UI;

[ExecuteAlways]
[RequireComponent(typeof(RectTransform))]
public class HeatSliceTemperatureLegend : MonoBehaviour
{
    [Header("References")]
    [Tooltip("要同步顏色範圍的 HeatSlicePlaneController。")]
    [SerializeField] private HeatSlicePlaneController sliceController;
    [Tooltip("當 sliceController 為空時，是否自動搜尋場景中的切片控制器。")]
    [SerializeField] private bool autoFindSliceController = true;
    [Tooltip("顯示色帶的 RawImage。留空時會自動建立。")]
    [SerializeField] private RawImage gradientImage;
    [Tooltip("顯示圖例標題的 UI Text。留空時會自動建立。")]
    [SerializeField] private Text titleText;
    [Tooltip("顯示最高溫度的 UI Text。留空時會自動建立。")]
    [SerializeField] private Text maxValueText;
    [Tooltip("顯示中間溫度的 UI Text。留空時會自動建立。")]
    [SerializeField] private Text midValueText;
    [Tooltip("顯示最低溫度的 UI Text。留空時會自動建立。")]
    [SerializeField] private Text minValueText;

    [Header("Content")]
    [Tooltip("圖例標題文字。")]
    [SerializeField] private string title = "氣溫分布";
    [Tooltip("溫度數值顯示格式。{0} 會代入溫度值。")]
    [SerializeField] private string valueFormat = "{0:0.0} °C";
    [Tooltip("色帶貼圖的垂直解析度。")]
    [SerializeField, Range(16, 1024)] private int gradientResolution = 256;

    [Header("Layout")]
    [Tooltip("是否自動把這個圖例面板錨定在畫面右側。")]
    [SerializeField] private bool autoAnchorRightSide = true;
    [Tooltip("圖例面板在右側的錨定位置偏移。")]
    [SerializeField] private Vector2 anchoredPosition = new Vector2(-36f, 0f);
    [Tooltip("圖例面板的整體尺寸。")]
    [SerializeField] private Vector2 panelSize = new Vector2(150f, 320f);
    [Tooltip("色帶本體的尺寸。")]
    [SerializeField] private Vector2 gradientSize = new Vector2(34f, 220f);
    [Tooltip("數值標籤與色帶之間的水平距離。")]
    [SerializeField] private float labelOffsetX = 44f;
    [Tooltip("標題與色帶之間的垂直距離。")]
    [SerializeField] private float titleOffsetY = 28f;

    [Header("Style")]
    [Tooltip("圖例標題與數值的文字顏色。")]
    [SerializeField] private Color textColor = Color.white;
    [Tooltip("圖例面板背景顏色。")]
    [SerializeField] private Color backgroundColor = new Color(0f, 0f, 0f, 0.35f);
    [Tooltip("是否顯示半透明背景底板。")]
    [SerializeField] private bool showBackground = true;
    [Tooltip("標題與數值文字大小。")]
    [SerializeField, Min(8)] private int fontSize = 18;

    [Header("Update")]
    [Tooltip("是否每幀同步圖例內容。")]
    [SerializeField] private bool updateEveryFrame = true;
    [Tooltip("非播放模式下是否也更新圖例。")]
    [SerializeField] private bool updateInEditMode = true;

    private RectTransform _rectTransform;
    private Image _backgroundImage;
    private Texture2D _gradientTexture;
    private float _lastMinTemperature = float.NaN;
    private float _lastMaxTemperature = float.NaN;
    private string _lastTitle = "";
    private bool _uiBuilt;

    private void Reset()
    {
        _rectTransform = GetComponent<RectTransform>();
        ResolveSliceController();
        EnsureUiReferences();
        ApplyLayout();
        RefreshLegend(forceRegenerateTexture: true);
    }

    private void OnEnable()
    {
        _rectTransform = GetComponent<RectTransform>();
        ResolveSliceController();
        EnsureUiReferences();
        ApplyLayout();
        RefreshLegend(forceRegenerateTexture: true);
    }

    private void OnDisable()
    {
        DestroyGradientTexture();
    }

    private void OnDestroy()
    {
        DestroyGradientTexture();
    }

    private void OnValidate()
    {
        gradientResolution = Mathf.Clamp(gradientResolution, 16, 1024);
        panelSize.x = Mathf.Max(panelSize.x, 60f);
        panelSize.y = Mathf.Max(panelSize.y, 100f);
        gradientSize.x = Mathf.Max(gradientSize.x, 8f);
        gradientSize.y = Mathf.Max(gradientSize.y, 32f);
        fontSize = Mathf.Max(fontSize, 8);

        _rectTransform = GetComponent<RectTransform>();
        ResolveSliceController();
        EnsureUiReferences();
        ApplyLayout();
        RefreshLegend(forceRegenerateTexture: true);
    }

    private void Update()
    {
        if (!updateEveryFrame)
            return;

        if (!Application.isPlaying && !updateInEditMode)
            return;

        ResolveSliceController();
        EnsureUiReferences();
        RefreshLegend(forceRegenerateTexture: false);
    }

    [ContextMenu("Refresh Legend")]
    public void RefreshLegendNow()
    {
        ResolveSliceController();
        EnsureUiReferences();
        ApplyLayout();
        RefreshLegend(forceRegenerateTexture: true);
    }

    private void ResolveSliceController()
    {
        if (sliceController != null || !autoFindSliceController)
            return;

        sliceController = GetComponentInParent<HeatSlicePlaneController>();

        if (sliceController == null)
            sliceController = FindObjectOfType<HeatSlicePlaneController>();
    }

    private void EnsureUiReferences()
    {
        if (_uiBuilt)
            return;

        if (_rectTransform == null)
            _rectTransform = GetComponent<RectTransform>();

        EnsureBackground();
        Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        if (gradientImage == null)
            gradientImage = CreateRawImage("Gradient", font);

        if (titleText == null)
            titleText = CreateText("Title", font, TextAnchor.MiddleCenter);

        if (maxValueText == null)
            maxValueText = CreateText("MaxValue", font, TextAnchor.MiddleLeft);

        if (midValueText == null)
            midValueText = CreateText("MidValue", font, TextAnchor.MiddleLeft);

        if (minValueText == null)
            minValueText = CreateText("MinValue", font, TextAnchor.MiddleLeft);

        _uiBuilt = true;
    }

    private void EnsureBackground()
    {
        _backgroundImage = GetComponent<Image>();
        if (_backgroundImage == null)
            _backgroundImage = gameObject.AddComponent<Image>();
    }

    private RawImage CreateRawImage(string objectName, Font font)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform), typeof(RawImage));
        go.transform.SetParent(transform, false);
        RawImage image = go.GetComponent<RawImage>();
        image.raycastTarget = false;
        return image;
    }

    private Text CreateText(string objectName, Font font, TextAnchor alignment)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(transform, false);
        Text text = go.GetComponent<Text>();
        text.font = font;
        text.alignment = alignment;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        return text;
    }

    private void ApplyLayout()
    {
        if (_rectTransform == null)
            return;

        if (autoAnchorRightSide)
        {
            _rectTransform.anchorMin = new Vector2(1f, 0.5f);
            _rectTransform.anchorMax = new Vector2(1f, 0.5f);
            _rectTransform.pivot = new Vector2(1f, 0.5f);
            _rectTransform.anchoredPosition = anchoredPosition;
        }

        _rectTransform.sizeDelta = panelSize;

        if (_backgroundImage != null)
        {
            _backgroundImage.enabled = showBackground;
            _backgroundImage.color = backgroundColor;
            _backgroundImage.raycastTarget = false;
        }

        if (gradientImage != null)
        {
            RectTransform gradientRect = gradientImage.rectTransform;
            gradientRect.anchorMin = new Vector2(0f, 0.5f);
            gradientRect.anchorMax = new Vector2(0f, 0.5f);
            gradientRect.pivot = new Vector2(0f, 0.5f);
            gradientRect.anchoredPosition = new Vector2(18f, 0f);
            gradientRect.sizeDelta = gradientSize;
        }

        LayoutText(titleText, new Vector2(panelSize.x * 0.5f, gradientSize.y * 0.5f + titleOffsetY), new Vector2(panelSize.x - 20f, 28f), TextAnchor.MiddleCenter);
        LayoutText(maxValueText, new Vector2(labelOffsetX + gradientSize.x, gradientSize.y * 0.5f), new Vector2(90f, 22f), TextAnchor.MiddleLeft);
        LayoutText(midValueText, new Vector2(labelOffsetX + gradientSize.x, 0f), new Vector2(90f, 22f), TextAnchor.MiddleLeft);
        LayoutText(minValueText, new Vector2(labelOffsetX + gradientSize.x, -gradientSize.y * 0.5f), new Vector2(90f, 22f), TextAnchor.MiddleLeft);
    }

    private void LayoutText(Text target, Vector2 anchoredPos, Vector2 size, TextAnchor alignment)
    {
        if (target == null)
            return;

        RectTransform rect = target.rectTransform;
        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(0f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.anchoredPosition = anchoredPos;
        rect.sizeDelta = size;

        target.alignment = alignment;
        target.fontSize = fontSize;
        target.color = textColor;
        target.supportRichText = false;
    }

    private void RefreshLegend(bool forceRegenerateTexture)
    {
        if (gradientImage == null || titleText == null || maxValueText == null || midValueText == null || minValueText == null)
            return;

        float minValue = sliceController != null ? sliceController.MinDisplayTemperature : 20f;
        float maxValue = sliceController != null ? sliceController.MaxDisplayTemperature : 30f;

        if (maxValue < minValue)
        {
            float temp = minValue;
            minValue = maxValue;
            maxValue = temp;
        }

        bool needTextureRefresh =
            forceRegenerateTexture ||
            _gradientTexture == null ||
            _gradientTexture.height != gradientResolution;

        if (needTextureRefresh)
        {
            RebuildGradientTexture();
        }

        if (gradientImage.texture != _gradientTexture)
            gradientImage.texture = _gradientTexture;

        if (!Mathf.Approximately(_lastMinTemperature, minValue) ||
            !Mathf.Approximately(_lastMaxTemperature, maxValue) ||
            _lastTitle != title)
        {
            titleText.text = title;
            maxValueText.text = string.Format(valueFormat, maxValue);
            midValueText.text = string.Format(valueFormat, Mathf.Lerp(minValue, maxValue, 0.5f));
            minValueText.text = string.Format(valueFormat, minValue);

            _lastMinTemperature = minValue;
            _lastMaxTemperature = maxValue;
            _lastTitle = title;
        }
    }

    private void RebuildGradientTexture()
    {
        DestroyGradientTexture();

        _gradientTexture = new Texture2D(1, gradientResolution, TextureFormat.RGBA32, false, true);
        _gradientTexture.name = "HeatSliceTemperatureLegend";
        _gradientTexture.wrapMode = TextureWrapMode.Clamp;
        _gradientTexture.filterMode = FilterMode.Bilinear;

        for (int y = 0; y < gradientResolution; y++)
        {
            float t = gradientResolution <= 1 ? 0f : y / (float)(gradientResolution - 1);
            Color color = HeatSlicePlaneController.EvaluateTemperatureRamp01(t);
            _gradientTexture.SetPixel(0, y, color);
        }

        _gradientTexture.Apply(false, false);
    }

    private void DestroyGradientTexture()
    {
        if (_gradientTexture == null)
            return;

#if UNITY_EDITOR
        if (!Application.isPlaying)
            DestroyImmediate(_gradientTexture);
        else
            Destroy(_gradientTexture);
#else
        Destroy(_gradientTexture);
#endif

        _gradientTexture = null;
    }
}
