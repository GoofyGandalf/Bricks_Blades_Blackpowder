Shader "Bricks/LDrawURP_Opaque"
{
    Properties
    {
        _PaletteTex ("Palette", 2D) = "white" {}
        _PaletteWidth ("Palette Width", Float) = 2048
        _ColorCode ("Color Code", Float) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0.55
        _SpecularStrength ("Specular Strength", Range(0,2)) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }

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
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_PaletteTex);
            SAMPLER(sampler_PaletteTex);

            CBUFFER_START(UnityPerMaterial)
                float _ColorCode;
                float _PaletteWidth;
                float _Smoothness;
                float _SpecularStrength;
            CBUFFER_END

            float4 SamplePalette(float code)
            {
                float u = (code + 0.5) / max(_PaletteWidth, 1.0);
                return SAMPLE_TEXTURE2D(_PaletteTex, sampler_PaletteTex, float2(u, 0.5));
            }

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);

                o.positionWS  = TransformObjectToWorld(input.positionOS.xyz);
                o.normalWS    = TransformObjectToWorldNormal(input.normalOS);
                o.positionHCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float3 N = normalize(input.normalWS);
                float3 V = normalize(GetWorldSpaceViewDir(input.positionWS));

                float4 pal = SamplePalette(_ColorCode);
                float3 baseColor = pal.rgb;

                // Simple “plastic” PBR-ish:
                // dielectric F0 ~ 0.04, boosted a bit by _SpecularStrength
                float3 F0 = 0.04 * _SpecularStrength.xxx;

                // Main light
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float3 L = normalize(mainLight.direction);
                float NdotL = saturate(dot(N, L));

                // Diffuse
                float3 diffuse = baseColor * NdotL * mainLight.color * mainLight.distanceAttenuation * mainLight.shadowAttenuation;

                // Specular (Blinn-Phong-ish, looks LEGO enough)
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

        // Shadow caster pass (so it CASTS shadows)
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }

            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        // DepthOnly (helps some URP paths)
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }
    }
}