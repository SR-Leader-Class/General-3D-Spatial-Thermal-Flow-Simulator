Shader "Custom/HeatSliceDisplay"
{
    // 將 3D 溫度場投影到切片平面上顯示：
    // - 先把世界座標轉成模擬盒內 uvw
    // - 取樣溫度後映射到色帶
    // - 可選擇疊加格線輔助觀察網格解析度

    Properties
    {
        _TemperatureTex3D ("Temperature Texture 3D", 3D) = "" {}
        _VelocityTex3D ("Velocity Texture 3D", 3D) = "" {}
        _MinTemp ("Min Display Temperature", Float) = 18
        _MaxTemp ("Max Display Temperature", Float) = 32
        _Alpha ("Alpha", Range(0,1)) = 0.85
        _OutOfBoundsAlpha ("Out Of Bounds Alpha", Range(0,1)) = 0.0
        _ShowGrid ("Show Grid", Range(0,1)) = 0
        _GridLineWidth ("Grid Line Width", Range(0.0, 0.2)) = 0.03
        _WindOverlayEnabled ("Wind Overlay Enabled", Range(0,1)) = 0
        _WindCellDensity ("Wind Cell Density", Float) = 12
        _WindArrowThickness ("Wind Arrow Thickness", Range(0.005,0.12)) = 0.025
        _WindArrowHeadSize ("Wind Arrow Head Size", Range(0.05,0.45)) = 0.16
        _WindArrowLengthScale ("Wind Arrow Length Scale", Range(0.2,1.2)) = 0.7
        _WindMinSpeed ("Wind Min Speed", Float) = 0.05
        _WindMaxSpeed ("Wind Max Speed", Float) = 2.5
        _WindOpacity ("Wind Opacity", Range(0,1)) = 0.9
        _WindOrientation ("Wind Orientation", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "RenderType"="Transparent"
            "IgnoreProjector"="True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler3D _TemperatureTex3D;
            sampler3D _VelocityTex3D;

            float _MinTemp;
            float _MaxTemp;
            float _Alpha;
            float _OutOfBoundsAlpha;
            float _ShowGrid;
            float _GridLineWidth;
            float _WindOverlayEnabled;
            float _WindCellDensity;
            float _WindArrowThickness;
            float _WindArrowHeadSize;
            float _WindArrowLengthScale;
            float _WindMinSpeed;
            float _WindMaxSpeed;
            float _WindOpacity;
            float _WindOrientation;

            float4 _SimulationBoundsMin;
            float4 _SimulationBoundsMax;
            float4 _GridSize;
            float _AmbientTemperature;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            half3 TemperatureRamp(half t)
            {
                // 五段式色帶：冷（藍）-> 暖（黃）-> 熱（紅）。
                half3 c1 = half3(0.0h, 0.1h, 0.8h);
                half3 c2 = half3(0.0h, 0.8h, 1.0h);
                half3 c3 = half3(1.0h, 0.95h, 0.2h);
                half3 c4 = half3(1.0h, 0.4h, 0.0h);
                half3 c5 = half3(0.85h, 0.0h, 0.0h);

                half u = saturate(t) * 4.0h;
                half s1 = saturate(u);
                half s2 = saturate(u - 1.0h);
                half s3 = saturate(u - 2.0h);
                half s4 = saturate(u - 3.0h);

                half3 col = lerp(c1, c2, s1);
                col = lerp(col, c3, s2);
                col = lerp(col, c4, s3);
                col = lerp(col, c5, s4);
                return col;
            }

            float IsInsideBounds(float3 worldPos, float3 bmin, float3 bmax)
            {
                // 使用 step 做無分支盒內判斷。
                float3 insideMin = step(bmin, worldPos);
                float3 insideMax = step(worldPos, bmax);
                return insideMin.x * insideMin.y * insideMin.z *
                       insideMax.x * insideMax.y * insideMax.z;
            }

            half GridMask(float3 texUVW, float3 gridSize, float lineWidth)
            {
                // 以 cell 中心距離 + fwidth 抗鋸齒，產生格線遮罩。
                float3 gridCoord = texUVW * gridSize;
                float3 fracPart = abs(frac(gridCoord) - 0.5);

                float3 fw = max(fwidth(gridCoord), 1e-5);
                float3 lineMask = smoothstep(0.5 - lineWidth - fw, 0.5 - lineWidth, fracPart);
                float grid = 1.0 - min(min(lineMask.x, lineMask.y), lineMask.z);
                return (half)saturate(grid);
            }

            float2 SliceUV(float3 uvw, float orientation)
            {
                if (orientation < 0.5)
                    return uvw.xz;
                if (orientation < 1.5)
                    return uvw.xy;
                return uvw.zy;
            }

            float2 SliceVelocity(float3 velocity, float orientation)
            {
                if (orientation < 0.5)
                    return velocity.xz;
                if (orientation < 1.5)
                    return velocity.xy;
                return float2(velocity.z, velocity.y);
            }

            float SegmentDistance(float2 p, float2 a, float2 b)
            {
                float2 pa = p - a;
                float2 ba = b - a;
                float h = saturate(dot(pa, ba) / max(dot(ba, ba), 1e-6));
                return length(pa - ba * h);
            }

            float ArrowMask(float2 local, float2 dir, float thickness, float headSize, float lengthScale)
            {
                float arrowLength = lerp(0.18, 0.46, saturate(lengthScale));
                float shaftStart = -arrowLength * 0.42;
                float shaftEnd = arrowLength * 0.18;
                float head = arrowLength * 0.42;
                float wingOffset = max(headSize, 0.05) * 0.45;

                float2 p = float2(dot(local, dir), dot(local, float2(-dir.y, dir.x)));
                float fw = max(fwidth(p.x) + fwidth(p.y), 1e-4);

                float shaft = 1.0 - smoothstep(thickness, thickness + fw, SegmentDistance(p, float2(shaftStart, 0.0), float2(shaftEnd, 0.0)));
                float headLeft = 1.0 - smoothstep(thickness, thickness + fw, SegmentDistance(p, float2(head, 0.0), float2(head - headSize, wingOffset)));
                float headRight = 1.0 - smoothstep(thickness, thickness + fw, SegmentDistance(p, float2(head, 0.0), float2(head - headSize, -wingOffset)));
                return saturate(max(shaft, max(headLeft, headRight)));
            }

            half3 WindColor(float speed01)
            {
                half3 slow = half3(0.72h, 0.95h, 1.0h);
                half3 mid = half3(0.35h, 1.0h, 0.72h);
                half3 fast = half3(1.0h, 0.95h, 0.68h);
                half3 col = lerp(slow, mid, saturate(speed01 * 1.5h));
                col = lerp(col, fast, saturate(speed01 * speed01));
                return col;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 bmin = _SimulationBoundsMin.xyz;
                float3 bmax = _SimulationBoundsMax.xyz;

                if (IsInsideBounds(i.worldPos, bmin, bmax) < 0.5)
                {
                    return fixed4(0, 0, 0, _OutOfBoundsAlpha);
                }

                float3 size = max(bmax - bmin, float3(1e-5, 1e-5, 1e-5));
                // 將世界座標標準化到 0..1 作為 3D 貼圖取樣座標。
                float3 uvw = saturate((i.worldPos - bmin) / size);

                half temp = tex3D(_TemperatureTex3D, uvw).r;
                // 溫度範圍正規化後送入色帶函式。
                half invRange = rcp(max((half)(_MaxTemp - _MinTemp), 1e-5h));
                half t = saturate((temp - (half)_MinTemp) * invRange);
                half3 col = TemperatureRamp(t);

                if (_ShowGrid > 0.5)
                {
                    half grid = GridMask(uvw, max(_GridSize.xyz, 1.0), _GridLineWidth);
                    col = lerp(col, half3(1, 1, 1), grid * 0.35h);
                }

                if (_WindOverlayEnabled > 0.5)
                {
                    float3 velocity3 = tex3D(_VelocityTex3D, uvw).xyz;
                    float2 planeVelocity = SliceVelocity(velocity3, _WindOrientation);
                    float speed = length(planeVelocity);

                    if (speed >= _WindMinSpeed)
                    {
                        float2 planeUv = SliceUV(uvw, _WindOrientation);
                        float2 cellCoord = planeUv * max(_WindCellDensity, 1.0);
                        float2 local = frac(cellCoord) - 0.5;
                        float2 dir = planeVelocity / max(speed, 1e-5);
                        float speed01 = saturate((speed - _WindMinSpeed) / max(_WindMaxSpeed - _WindMinSpeed, 1e-5));
                        float arrow = ArrowMask(local, dir, _WindArrowThickness, _WindArrowHeadSize, speed01 * _WindArrowLengthScale);
                        half3 windCol = WindColor(speed01);
                        col = lerp(col, windCol, arrow * _WindOpacity);
                    }
                }

                return fixed4(col, _Alpha);
            }
            ENDCG
        }
    }
}
