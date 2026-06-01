using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class CleanroomHeatmapRenderer : MonoBehaviour
{
    [Header("Sources")]
    public DigitalTwinManager twinManager;

    [Header("Targets")]
    public RawImage targetImage;
    public Renderer targetRenderer;

    [Header("Texture")]
    public int textureWidth = 256;
    public int textureHeight = 256;
    public Color coldColor = new Color(0.07f, 0.23f, 0.55f, 1f);
    public Color midColor = new Color(0.12f, 0.72f, 0.63f, 1f);
    public Color hotColor = new Color(0.94f, 0.22f, 0.18f, 1f);

    private Texture2D heatmapTexture;

    private void OnEnable()
    {
        if (twinManager == null)
            twinManager = FindFirstObjectByType<DigitalTwinManager>();

        Subscribe();
        EnsureTexture();

        if (twinManager != null)
            twinManager.ForceRefreshHeatmap();
        else
            RenderFallback();
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    public void RefreshHeatmap()
    {
        if (twinManager == null)
        {
            RenderFallback();
            return;
        }

        RenderHeatmap(twinManager.LatestHeatmapPoints, twinManager.HeatmapResolutionX, twinManager.HeatmapResolutionY);
    }

    private void Subscribe()
    {
        if (twinManager == null)
            return;

        twinManager.HeatmapRebuilt += HandleHeatmapRebuilt;
    }

    private void Unsubscribe()
    {
        if (twinManager == null)
            return;

        twinManager.HeatmapRebuilt -= HandleHeatmapRebuilt;
    }

    private void HandleHeatmapRebuilt(DigitalTwinManager manager)
    {
        RefreshHeatmap();
    }

    private void EnsureTexture()
    {
        if (heatmapTexture != null && heatmapTexture.width == textureWidth && heatmapTexture.height == textureHeight)
        {
            ApplyTextureToTargets();
            return;
        }

        heatmapTexture = new Texture2D(Mathf.Max(2, textureWidth), Mathf.Max(2, textureHeight), TextureFormat.RGBA32, false);
        heatmapTexture.wrapMode = TextureWrapMode.Clamp;
        heatmapTexture.filterMode = FilterMode.Bilinear;
        ApplyTextureToTargets();
    }

    private void ApplyTextureToTargets()
    {
        if (targetImage != null)
            targetImage.texture = heatmapTexture;

        if (targetRenderer != null && targetRenderer.material != null)
            targetRenderer.material.mainTexture = heatmapTexture;
    }

    private void RenderFallback()
    {
        EnsureTexture();
        FillTexture(Color.black);
        heatmapTexture.Apply();
    }

    private void RenderHeatmap(IReadOnlyList<CleanroomHeatmapPoint> points, int resolutionX, int resolutionY)
    {
        EnsureTexture();

        if (points == null || points.Count == 0 || resolutionX <= 0 || resolutionY <= 0)
        {
            RenderFallback();
            return;
        }

        for (int py = 0; py < heatmapTexture.height; py++)
        {
            float gy = heatmapTexture.height <= 1 ? 0f : py / (float)(heatmapTexture.height - 1);
            float sampleY = gy * Mathf.Max(0, resolutionY - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(sampleY), 0, resolutionY - 1);
            int y1 = Mathf.Clamp(y0 + 1, 0, resolutionY - 1);
            float ty = sampleY - y0;

            for (int px = 0; px < heatmapTexture.width; px++)
            {
                float gx = heatmapTexture.width <= 1 ? 0f : px / (float)(heatmapTexture.width - 1);
                float sampleX = gx * Mathf.Max(0, resolutionX - 1);
                int x0 = Mathf.Clamp(Mathf.FloorToInt(sampleX), 0, resolutionX - 1);
                int x1 = Mathf.Clamp(x0 + 1, 0, resolutionX - 1);
                float tx = sampleX - x0;

                float v00 = GetPointValue(points, resolutionX, resolutionY, x0, y0);
                float v10 = GetPointValue(points, resolutionX, resolutionY, x1, y0);
                float v01 = GetPointValue(points, resolutionX, resolutionY, x0, y1);
                float v11 = GetPointValue(points, resolutionX, resolutionY, x1, y1);

                float top = Mathf.Lerp(v00, v10, tx);
                float bottom = Mathf.Lerp(v01, v11, tx);
                float normalizedValue = Mathf.Clamp01(Mathf.Lerp(top, bottom, ty));

                heatmapTexture.SetPixel(px, py, EvaluateHeatColor(normalizedValue));
            }
        }

        heatmapTexture.Apply();
    }

    private float GetPointValue(IReadOnlyList<CleanroomHeatmapPoint> points, int resolutionX, int resolutionY, int x, int y)
    {
        int index = y * resolutionX + x;

        if (index < 0 || index >= points.Count)
            return 0f;

        return points[index].normalizedValue;
    }

    private Color EvaluateHeatColor(float normalizedValue)
    {
        if (normalizedValue <= 0.5f)
            return Color.Lerp(coldColor, midColor, normalizedValue / 0.5f);

        return Color.Lerp(midColor, hotColor, (normalizedValue - 0.5f) / 0.5f);
    }

    private void FillTexture(Color color)
    {
        Color[] pixels = new Color[heatmapTexture.width * heatmapTexture.height];

        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = color;

        heatmapTexture.SetPixels(pixels);
    }
}
