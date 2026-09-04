Shader "Bricks/LDrawURP_Indirect"
{
    Properties
    {
        _PaletteTex ("Palette", 2D) = "white" {}
        _PaletteWidth ("Palette Width", Float) = 2048
        _BrickBufferOffset ("Brick Buffer Offset", Float) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0.55
        _SpecularStrength ("Specular Strength", Range(0,2)) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }

        // ─── ForwardLit Pass ───
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // ── Per-instance data from GPU buffers ──
            struct BrickData
            {
                float4x4 objectToWorld;
                float    colorCode;
                float3   _pad;
            };

            StructuredBuffer<BrickData> _BrickBuffer;

            // ── Material properties ──
            TEXTURE2D(_PaletteTex);
            SAMPLER(sampler_PaletteTex);

            CBUFFER_START(UnityPerMaterial)
                float _PaletteWidth;
                float _BrickBufferOffset;
                float _Smoothness;
                float _SpecularStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv1        : TEXCOORD1;  // per-vertex color code (x), -1 = inherit instance color
                uint   instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float  colorCode   : TEXCOORD2;  // resolved: per-vertex explicit OR instance color
            };

            float4 SamplePalette(float code)
            {
                float u = (code + 0.5) / max(_PaletteWidth, 1.0);
                return SAMPLE_TEXTURE2D(_PaletteTex, sampler_PaletteTex, float2(u, 0.5));
            }

            Varyings vert(Attributes input)
            {
                Varyings o;

                uint brickIndex = input.instanceID + (uint)_BrickBufferOffset;
                BrickData brick = _BrickBuffer[brickIndex];

                float4x4 objToWorld = brick.objectToWorld;

                float3 posWS = mul(objToWorld, float4(input.positionOS.xyz, 1.0)).xyz;
                o.positionWS  = posWS;
                o.positionHCS = TransformWorldToHClip(posWS);

                float3x3 objToWorld3x3 = (float3x3)objToWorld;
                o.normalWS = normalize(mul(objToWorld3x3, input.normalOS));

                // Per-vertex color: -1 means inherit from instance, anything >= 0 is an explicit palette index.
                o.colorCode = (input.uv1.x < 0.0) ? brick.colorCode : input.uv1.x;

                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 N = normalize(input.normalWS);
                float3 V = normalize(GetWorldSpaceViewDir(input.positionWS));

                float4 pal = SamplePalette(input.colorCode);
                float3 baseColor = pal.rgb;

                float3 F0 = 0.04 * _SpecularStrength.xxx;

                // Main light
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float3 L = normalize(mainLight.direction);
                float NdotL = saturate(dot(N, L));

                // Diffuse
                float3 diffuse = baseColor * NdotL * mainLight.color * mainLight.distanceAttenuation * mainLight.shadowAttenuation;

                // Specular (Blinn-Phong plastic)
                float3 H = normalize(L + V);
                float NdotH = saturate(dot(N, H));
                float specPower = lerp(16.0, 256.0, saturate(_Smoothness));
                float spec = pow(NdotH, specPower) * NdotL;

                float3 specular = spec * F0 * mainLight.color * mainLight.distanceAttenuation * mainLight.shadowAttenuation;

                // Ambient via SH
                float3 ambient = baseColor * SampleSH(N);

                float3 color = diffuse + specular + ambient;

                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // ─── ShadowCaster Pass (GPU-indirect) ───
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }

            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct BrickData
            {
                float4x4 objectToWorld;
                float    colorCode;
                float3   _pad;
            };

            StructuredBuffer<BrickData> _BrickBuffer;

            float3 _LightDirection;

            CBUFFER_START(UnityPerMaterial)
                float _PaletteWidth;
                float _BrickBufferOffset;
                float _Smoothness;
                float _SpecularStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                uint   instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            Varyings ShadowVert(Attributes input)
            {
                Varyings o;

                uint brickIndex = input.instanceID + (uint)_BrickBufferOffset;
                BrickData brick = _BrickBuffer[brickIndex];
                float4x4 objToWorld = brick.objectToWorld;

                float3 posWS = mul(objToWorld, float4(input.positionOS.xyz, 1.0)).xyz;
                float3 normalWS = normalize(mul((float3x3)objToWorld, input.normalOS));

                // Apply shadow bias
                posWS = ApplyShadowBias(posWS, normalWS, _LightDirection);
                o.positionHCS = TransformWorldToHClip(posWS);

                #if UNITY_REVERSED_Z
                    o.positionHCS.z = min(o.positionHCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    o.positionHCS.z = max(o.positionHCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                return o;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // ─── DepthOnly Pass (GPU-indirect) ───
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct BrickData
            {
                float4x4 objectToWorld;
                float    colorCode;
                float3   _pad;
            };

            StructuredBuffer<BrickData> _BrickBuffer;

            CBUFFER_START(UnityPerMaterial)
                float _PaletteWidth;
                float _BrickBufferOffset;
                float _Smoothness;
                float _SpecularStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                uint   instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings o;
                uint brickIndex = input.instanceID + (uint)_BrickBufferOffset;
                BrickData brick = _BrickBuffer[brickIndex];
                float4x4 objToWorld = brick.objectToWorld;
                float3 posWS = mul(objToWorld, float4(input.positionOS.xyz, 1.0)).xyz;
                o.positionHCS = TransformWorldToHClip(posWS);
                return o;
            }

            half4 DepthFrag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
