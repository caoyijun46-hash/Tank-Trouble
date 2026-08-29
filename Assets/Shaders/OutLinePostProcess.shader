Shader "Custom/PostProcess/OutlinePostProcess"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (0, 0, 0, 1)
        _DepthThreshold ("Depth Threshold", Float) = 1.5
        _NormalThreshold ("Normal Threshold", Float) = 0.1
        _OutlineThickness ("Outline Thickness", Float) = 0.6
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "OutlinePost"
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _OutlineColor;
            float _DepthThreshold;
            float _NormalThreshold;
            float _OutlineThickness;

            float GetLinearDepth(float2 uv)
            {
                float rawDepth = SampleSceneDepth(uv);
                return LinearEyeDepth(rawDepth, _ZBufferParams);
            }

            float2 SobelDepth(float2 uv, float2 texelSize)
            {
                float d00 = GetLinearDepth(uv + float2(-1, -1) * texelSize);
                float d10 = GetLinearDepth(uv + float2( 0, -1) * texelSize);
                float d20 = GetLinearDepth(uv + float2( 1, -1) * texelSize);
                float d01 = GetLinearDepth(uv + float2(-1,  0) * texelSize);
                float d21 = GetLinearDepth(uv + float2( 1,  0) * texelSize);
                float d02 = GetLinearDepth(uv + float2(-1,  1) * texelSize);
                float d12 = GetLinearDepth(uv + float2( 0,  1) * texelSize);
                float d22 = GetLinearDepth(uv + float2( 1,  1) * texelSize);

                float gx = -d00 + d20 - 2.0*d01 + 2.0*d21 - d02 + d22;
                float gy = -d00 - 2.0*d10 - d20 + d02 + 2.0*d12 + d22;

                return float2(gx, gy);
            }

            float2 SobelNormal(float2 uv, float2 texelSize)
            {
                float3 nc = SampleSceneNormals(uv);
                
                float s00 = dot(nc, SampleSceneNormals(uv + float2(-1, -1) * texelSize));
                float s10 = dot(nc, SampleSceneNormals(uv + float2( 0, -1) * texelSize));
                float s20 = dot(nc, SampleSceneNormals(uv + float2( 1, -1) * texelSize));
                float s01 = dot(nc, SampleSceneNormals(uv + float2(-1,  0) * texelSize));
                float s21 = dot(nc, SampleSceneNormals(uv + float2( 1,  0) * texelSize));
                float s02 = dot(nc, SampleSceneNormals(uv + float2(-1,  1) * texelSize));
                float s12 = dot(nc, SampleSceneNormals(uv + float2( 0,  1) * texelSize));
                float s22 = dot(nc, SampleSceneNormals(uv + float2( 1,  1) * texelSize));

                float gx = -s00 + s20 - 2.0*s01 + 2.0*s21 - s02 + s22;
                float gy = -s00 - 2.0*s10 - s20 + s02 + 2.0*s12 + s22;

                return float2(gx, gy);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float rawDepth = SampleSceneDepth(uv);

                if (rawDepth <= 0.0001)
                    return SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv);

                float2 texelSize = _BlitTexture_TexelSize.xy * _OutlineThickness;

                // 一次性获取中心深度（线性）
                float centerDepth = GetLinearDepth(uv);

                // === 深度边缘检测 ===
                float2 depthGrad = SobelDepth(uv, texelSize);

                // 基础阈值（乘以 0.5，你可以根据需要调整这个系数）
                float depthThreshold = centerDepth * _DepthThreshold * 0.5;

                // 1. 限制最小阈值（防止极端视角下阈值太小导致全黑）
                depthThreshold = max(depthThreshold, 0.5);

                // 2. 计算深度边缘强度
                float depthEdge = length(depthGrad) / max(depthThreshold, 0.001);

                // 3. 距离衰减：远处（centerDepth > 50）的深度边缘强制削弱
                float distanceFade = 1.0 - saturate((centerDepth - 20.0) / 80.0);
                depthEdge *= distanceFade;

                // 4. 硬上限：防止个别像素爆炸
                depthEdge = min(depthEdge, 1.5);

                // === 法线边缘检测 ===
                float2 normalGrad = SobelNormal(uv, texelSize);
                float normalEdge = length(normalGrad) / max(_NormalThreshold, 0.01);

                // 合并边缘（法线权重0.5）
                float edge = max(depthEdge, normalEdge * 0.5);

                // 抗锯齿平滑
                float edgeWidth = fwidth(edge);
                edgeWidth = clamp(edgeWidth, 0.05, 0.3);
                float threshold = 0.3;
                edge = smoothstep(threshold - edgeWidth, threshold + edgeWidth, edge);

                // 混合输出
                float4 color = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv);
                return lerp(color, _OutlineColor, edge * _OutlineColor.a);
            }

            ENDHLSL
        }
    }
}