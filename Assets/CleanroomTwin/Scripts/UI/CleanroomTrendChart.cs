using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class CleanroomTrendChart : MonoBehaviour
{
    [Header("Targets")]
    public RawImage targetImage;

    [Header("Metric")]
    public CleanroomMetric metric = CleanroomMetric.Particle05;

    [Header("Texture")]
    public int textureWidth = 512;
    public int textureHeight = 256;
    public int padding = 20;
    public Color backgroundColor = new Color(0.05f, 0.07f, 0.11f, 1f);
    public Color gridColor = new Color(0.18f, 0.24f, 0.32f, 1f);
    public Color lineColor = new Color(0.12f, 0.85f, 0.7f, 1f);
    public Color axisColor = new Color(0.75f, 0.82f, 0.9f, 1f);

    private Texture2D chartTexture;
    private CleanroomSensor currentSensor;

    private void OnEnable()
    {
        EnsureTexture();
        RefreshChart();
    }

    private void OnDisable()
    {
        UnsubscribeFromSensor();
    }

    public void SetSensor(CleanroomSensor sensor)
    {
        if (currentSensor == sensor)
        {
            RefreshChart();
            return;
        }

        UnsubscribeFromSensor();
        currentSensor = sensor;
        SubscribeToSensor();
        RefreshChart();
    }

    public void SetMetric(CleanroomMetric newMetric)
    {
        metric = newMetric;
        RefreshChart();
    }

    public void ClearChart()
    {
        EnsureTexture();
        FillTexture(backgroundColor);
        DrawGrid();
        chartTexture.Apply();
    }

    public void RefreshChart()
    {
        EnsureTexture();
        FillTexture(backgroundColor);
        DrawGrid();

        if (currentSensor == null || currentSensor.TrendHistory == null || currentSensor.TrendHistory.Count == 0)
        {
            chartTexture.Apply();
            return;
        }

        IReadOnlyList<CleanroomTrendSample> samples = currentSensor.TrendHistory;

        if (!currentSensor.TryGetTrendBounds(metric, out float minValue, out float maxValue))
        {
            chartTexture.Apply();
            return;
        }

        if (Mathf.Approximately(minValue, maxValue))
        {
            minValue -= 1f;
            maxValue += 1f;
        }

        int plotWidth = Mathf.Max(1, textureWidth - padding * 2);
        int plotHeight = Mathf.Max(1, textureHeight - padding * 2);
        int lastX = padding;
        int lastY = padding;

        for (int i = 0; i < samples.Count; i++)
        {
            float tx = samples.Count <= 1 ? 0f : i / (float)(samples.Count - 1);
            float value = samples[i].GetMetricValue(metric);
            float normalized = Mathf.InverseLerp(minValue, maxValue, value);
            int x = padding + Mathf.RoundToInt(tx * plotWidth);
            int y = padding + Mathf.RoundToInt(normalized * plotHeight);

            if (i > 0)
                DrawLine(lastX, lastY, x, y, lineColor);

            lastX = x;
            lastY = y;
        }

        chartTexture.Apply();
    }

    private void SubscribeToSensor()
    {
        if (currentSensor == null)
            return;

        currentSensor.DataUpdated += HandleSensorDataUpdated;
    }

    private void UnsubscribeFromSensor()
    {
        if (currentSensor == null)
            return;

        currentSensor.DataUpdated -= HandleSensorDataUpdated;
    }

    private void HandleSensorDataUpdated(CleanroomSensor sensor, CleanroomSensorData data)
    {
        RefreshChart();
    }

    private void EnsureTexture()
    {
        if (chartTexture != null && chartTexture.width == textureWidth && chartTexture.height == textureHeight)
        {
            if (targetImage != null && targetImage.texture != chartTexture)
                targetImage.texture = chartTexture;

            return;
        }

        chartTexture = new Texture2D(Mathf.Max(2, textureWidth), Mathf.Max(2, textureHeight), TextureFormat.RGBA32, false);
        chartTexture.wrapMode = TextureWrapMode.Clamp;
        chartTexture.filterMode = FilterMode.Bilinear;

        if (targetImage != null)
            targetImage.texture = chartTexture;
    }

    private void FillTexture(Color color)
    {
        Color[] pixels = new Color[chartTexture.width * chartTexture.height];

        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = color;

        chartTexture.SetPixels(pixels);
    }

    private void DrawGrid()
    {
        int width = chartTexture.width;
        int height = chartTexture.height;
        int left = Mathf.Clamp(padding, 0, width - 1);
        int right = Mathf.Clamp(width - padding - 1, 0, width - 1);
        int bottom = Mathf.Clamp(padding, 0, height - 1);
        int top = Mathf.Clamp(height - padding - 1, 0, height - 1);

        DrawLine(left, bottom, right, bottom, axisColor);
        DrawLine(left, bottom, left, top, axisColor);

        for (int i = 1; i <= 3; i++)
        {
            int y = bottom + Mathf.RoundToInt((top - bottom) * (i / 4f));
            DrawLine(left, y, right, y, gridColor);
        }

        for (int i = 1; i <= 3; i++)
        {
            int x = left + Mathf.RoundToInt((right - left) * (i / 4f));
            DrawLine(x, bottom, x, top, gridColor);
        }
    }

    private void DrawLine(int x0, int y0, int x1, int y1, Color color)
    {
        int dx = Mathf.Abs(x1 - x0);
        int dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            SetPixelSafe(x0, y0, color);

            if (x0 == x1 && y0 == y1)
                break;

            int err2 = err * 2;

            if (err2 > -dy)
            {
                err -= dy;
                x0 += sx;
            }

            if (err2 < dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private void SetPixelSafe(int x, int y, Color color)
    {
        if (chartTexture == null)
            return;

        if (x < 0 || x >= chartTexture.width || y < 0 || y >= chartTexture.height)
            return;

        chartTexture.SetPixel(x, y, color);
    }
}
