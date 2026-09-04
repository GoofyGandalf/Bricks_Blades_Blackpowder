Shader "Hidden/BBB/BeautifulFog"
{
    Properties
    {
        _FogColor ("Fog Color", Color) = (0.71, 0.76, 0.82, 1.0)
        _StartDistance ("Start Distance", Float) = 6
        _LinearParams ("Linear Start End", Vector) = (20, 120, 0, 0)
        _ExponentialDensity ("Exponential Density", Float) = 0.02
        _FogMode ("Fog Mode", Float) = 2
        _UseRadialDistance ("Use Radial Distance", Float) = 1
        _UseHeightFog ("Use Height Fog", Float) = 1
        _HeightParams ("Height Params", Vector) = (2, -20, 0.8, 0)
        _FadeToSkybox ("Fade To Skybox", Float) = 1
        _AffectSkyPixels ("Affect Sky Pixels", Float) = 1
        _SkyCubemap ("Sky Cubemap", Cube) = "" {}
        _SkyTint ("Sky Tint", Color) = (1, 1, 1, 1)
        _SkyExposure ("Sky Exposure", Float) = 1
        _SkyRotation ("Sky Rotation", Float) = 0
        _DitherNoise ("Dither Noise", Float) = 0.0025
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
        }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend One Zero

        Pass
        {
            Name "Beautiful Fog"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURECUBE(_SkyCubemap);
            SAMPLER(sampler_SkyCubemap);

            CBUFFER_START(UnityPerMaterial)
                half4 _FogColor;
                float _StartDistance;
                float2 _LinearParams;
                float _ExponentialDensity;
                float _FogMode;
                float _UseRadialDistance;
                float _UseHeightFog;
                float4 _HeightParams;
                float _FadeToSkybox;
                float _AffectSkyPixels;
                half4 _SkyTint;
                float _SkyExposure;
                float _SkyRotation;
                half4 _SkyCubemap_HDR;
                float _DitherNoise;
            CBUFFER_END

            float3 RotateAroundYAxis(float3 v, float degrees)
            {
                float r = radians(degrees);
                float s;
                float c;
                sincos(r, s, c);
                return float3(c * v.x + s * v.z, v.y, -s * v.x + c * v.z);
            }

            float IsSkyPixel(float rawDepth)
            {
                #if UNITY_REVERSED_Z
                    return rawDepth <= 1e-5 ? 1.0 : 0.0;
                #else
                    return rawDepth >= 0.99999 ? 1.0 : 0.0;
                #endif
            }

            float ComputeDistanceFog(float distanceFromCamera)
            {
                if (_FogMode < 0.5)
                {
                    float start = _LinearParams.x;
                    float endDistance = max(_LinearParams.y, start + 0.001);
                    return saturate((distanceFromCamera - start) / (endDistance - start));
                }

                if (_FogMode < 1.5)
                {
                    return saturate(1.0 - exp(-_ExponentialDensity * distanceFromCamera));
                }

                float d = _ExponentialDensity * distanceFromCamera;
                return saturate(1.0 - exp(-(d * d)));
            }

            float ComputeHeightFog(float worldY)
            {
                float top = _HeightParams.x;
                float bottom = _HeightParams.y;
                float strength = _HeightParams.z;

                float minY = min(top, bottom);
                float maxY = max(top, bottom);
                float t = 1.0 - saturate((worldY - minY) / max(maxY - minY, 0.001));
                return saturate(t * strength);
            }

            float Hash12(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                half4 sceneColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                float rawDepth = SampleSceneDepth(uv);
                float isSkyPixel = IsSkyPixel(rawDepth);

                if (_AffectSkyPixels < 0.5 && isSkyPixel > 0.5)
                {
                    return sceneColor;
                }

                float3 worldPosition = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 worldView = worldPosition - _WorldSpaceCameraPos.xyz;

                float eyeDistance = LinearEyeDepth(rawDepth, _ZBufferParams);
                float distanceFromCamera = _UseRadialDistance > 0.5 ? length(worldView) : eyeDistance;
                distanceFromCamera = max(0.0, distanceFromCamera - _StartDistance);

                float fogAmount = ComputeDistanceFog(distanceFromCamera);

                if (_UseHeightFog > 0.5)
                {
                    fogAmount = max(fogAmount, ComputeHeightFog(worldPosition.y));
                }

                float noise = Hash12(uv * _ScreenParams.xy) - 0.5;
                fogAmount = saturate(fogAmount + noise * _DitherNoise);

                half3 targetFogColor = _FogColor.rgb;

                if (_FadeToSkybox > 0.5)
                {
                    float3 skyDir = normalize(worldView);
                    skyDir = RotateAroundYAxis(skyDir, _SkyRotation);
                    half4 encodedSky = SAMPLE_TEXTURECUBE(_SkyCubemap, sampler_SkyCubemap, skyDir);
                    half3 decodedSky = DecodeHDREnvironment(encodedSky, _SkyCubemap_HDR);
                    targetFogColor = decodedSky * _SkyTint.rgb * _SkyExposure;
                }

                sceneColor.rgb = lerp(sceneColor.rgb, targetFogColor, fogAmount);
                return sceneColor;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
