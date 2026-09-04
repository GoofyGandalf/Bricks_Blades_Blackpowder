Shader "Bricks/LDrawURP_Fade"
{
    Properties
    {
        _PaletteTex ("Palette", 2D) = "white" {}
        _PaletteWidth ("Palette Width", Float) = 2048
        _ColorCode ("Color Code", Float) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0.55
        _SpecularStrength ("Specular Strength", Range(0,2)) = 1
        _Alpha ("Alpha", Range(0,1)) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            Cull Back
            ZWrite Off
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
                float _Alpha;
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

                float3 F0 = 0.04 * _SpecularStrength.xxx;

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float3 L = normalize(mainLight.direction);
                float NdotL = saturate(dot(N, L));

                float3 diffuse = baseColor * NdotL * mainLight.color * mainLight.distanceAttenuation * mainLight.shadowAttenuation;

                float3 H = normalize(L + V);
                float NdotH = saturate(dot(N, H));
                float specPower = lerp(16.0, 256.0, saturate(_Smoothness));
                float spec = pow(NdotH, specPower) * NdotL;
                float3 specular = spec * F0 * mainLight.color * mainLight.distanceAttenuation * mainLight.shadowAttenuation;

                float3 ambient = baseColor * SampleSH(N);

                float3 color = diffuse + specular + ambient;

                return half4(color, _Alpha);
            }
            ENDHLSL
        }
    }
}
