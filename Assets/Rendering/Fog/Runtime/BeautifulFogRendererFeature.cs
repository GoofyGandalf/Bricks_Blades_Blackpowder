using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace BBB.Rendering.Fog
{
    public sealed class BeautifulFogRendererFeature : ScriptableRendererFeature
    {
        public enum FogEquation
        {
            Linear,
            Exponential,
            ExponentialSquared
        }

        [System.Serializable]
        public sealed class Settings
        {
            [Header("Core")]
            public bool effectEnabled = true;
            public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingTransparents;
            public Shader shader;

            [Header("Distance Fog")]
            public bool useRenderSettings = true;
            public FogEquation fogEquation = FogEquation.ExponentialSquared;
            [ColorUsage(false, true)]
            public Color fogColor = new Color(0.71f, 0.76f, 0.82f, 1f);
            [Min(0f)] public float startDistance = 6f;
            [Min(0.0001f)] public float linearStart = 20f;
            [Min(0.0002f)] public float linearEnd = 120f;
            [Range(0f, 1f)] public float exponentialDensity = 0.02f;
            public bool useRadialDistance = true;

            [Header("Height Fog")]
            public bool useHeightFog = true;
            public float heightStart = 2f;
            public float heightEnd = -20f;
            [Range(0f, 2f)] public float heightStrength = 0.8f;

            [Header("Skybox Fade")]
            public bool fadeToSkybox = true;
            public bool affectSkyPixels = true;
            public Cubemap skyCubemap;
            [ColorUsage(false, true)]
            public Color skyTint = Color.white;
            [Range(0f, 8f)] public float skyExposure = 1f;
            [Range(-180f, 180f)] public float skyRotation = 0f;

            [Header("Quality")]
            [Range(0f, 0.02f)] public float ditherNoise = 0.0025f;
        }

        [SerializeField] private Settings settings = new Settings();

        private Material _material;
        private FogPass _pass;

        private sealed class FogPass : ScriptableRenderPass
        {
            private static readonly int FogColorId = Shader.PropertyToID("_FogColor");
            private static readonly int StartDistanceId = Shader.PropertyToID("_StartDistance");
            private static readonly int LinearParamsId = Shader.PropertyToID("_LinearParams");
            private static readonly int ExponentialDensityId = Shader.PropertyToID("_ExponentialDensity");
            private static readonly int FogModeId = Shader.PropertyToID("_FogMode");
            private static readonly int UseRadialDistanceId = Shader.PropertyToID("_UseRadialDistance");
            private static readonly int UseHeightFogId = Shader.PropertyToID("_UseHeightFog");
            private static readonly int HeightParamsId = Shader.PropertyToID("_HeightParams");
            private static readonly int FadeToSkyboxId = Shader.PropertyToID("_FadeToSkybox");
            private static readonly int AffectSkyPixelsId = Shader.PropertyToID("_AffectSkyPixels");
            private static readonly int SkyCubemapId = Shader.PropertyToID("_SkyCubemap");
            private static readonly int SkyTintId = Shader.PropertyToID("_SkyTint");
            private static readonly int SkyExposureId = Shader.PropertyToID("_SkyExposure");
            private static readonly int SkyRotationId = Shader.PropertyToID("_SkyRotation");
            private static readonly int DitherNoiseId = Shader.PropertyToID("_DitherNoise");

            private readonly Settings _settings;
            private readonly Material _material;

            public FogPass(Settings settings, Material material)
            {
                _settings = settings;
                _material = material;
                profilingSampler = new ProfilingSampler("Beautiful Fog");
                ConfigureInput(ScriptableRenderPassInput.Depth);
                requiresIntermediateTexture = true;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (_material == null)
                {
                    return;
                }

                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var camera = cameraData.camera;

                if (camera == null || camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.Reflection)
                {
                    return;
                }

                if (resourceData.isActiveTargetBackBuffer)
                {
                    return;
                }

                ApplySettings(_settings, camera, _material);

                var source = resourceData.activeColorTexture;
                if (!source.IsValid())
                {
                    return;
                }

                var destinationDesc = renderGraph.GetTextureDesc(source);
                destinationDesc.name = "CameraColor-BeautifulFog";
                destinationDesc.clearBuffer = false;
                var destination = renderGraph.CreateTexture(destinationDesc);

                var blitParameters = new RenderGraphUtils.BlitMaterialParameters(source, destination, _material, 0);
                renderGraph.AddBlitPass(blitParameters, "Beautiful Fog");

                resourceData.cameraColor = destination;
            }

            public void Dispose()
            {
                // RenderGraph handles transient textures; no RTHandle resources to release.
            }

            private static void ApplySettings(Settings settings, Camera camera, Material material)
            {
                material.SetColor(FogColorId, settings.fogColor);
                material.SetFloat(StartDistanceId, Mathf.Max(0f, settings.startDistance));
                material.SetFloat(UseRadialDistanceId, settings.useRadialDistance ? 1f : 0f);
                material.SetFloat(UseHeightFogId, settings.useHeightFog ? 1f : 0f);
                material.SetVector(HeightParamsId, new Vector4(settings.heightStart, settings.heightEnd, Mathf.Max(0f, settings.heightStrength), 0f));
                material.SetFloat(AffectSkyPixelsId, settings.affectSkyPixels ? 1f : 0f);
                material.SetFloat(DitherNoiseId, settings.ditherNoise);

                var mode = settings.fogEquation;
                var linearStart = settings.linearStart;
                var linearEnd = settings.linearEnd;
                var density = settings.exponentialDensity;

                if (settings.useRenderSettings)
                {
                    mode = ConvertFogMode(RenderSettings.fogMode);
                    linearStart = RenderSettings.fogStartDistance;
                    linearEnd = RenderSettings.fogEndDistance;
                    density = RenderSettings.fogDensity;
                    material.SetColor(FogColorId, RenderSettings.fogColor);
                }

                linearEnd = Mathf.Max(linearEnd, linearStart + 0.01f);
                material.SetVector(LinearParamsId, new Vector2(linearStart, linearEnd));
                material.SetFloat(ExponentialDensityId, Mathf.Max(0f, density));
                material.SetFloat(FogModeId, (float)mode);

                var shouldUseSky = settings.fadeToSkybox;
                var activeSkybox = RenderSettings.skybox;

                if (settings.useRenderSettings && activeSkybox != null)
                {
                    Cubemap skyTexture = null;
                    if (activeSkybox.HasProperty("_Tex"))
                    {
                        skyTexture = activeSkybox.GetTexture("_Tex") as Cubemap;
                    }

                    if (skyTexture != null)
                    {
                        material.SetTexture(SkyCubemapId, skyTexture);
                        material.SetColor(SkyTintId, activeSkybox.HasProperty("_Tint") ? activeSkybox.GetColor("_Tint") : Color.white);
                        material.SetFloat(SkyExposureId, activeSkybox.HasProperty("_Exposure") ? activeSkybox.GetFloat("_Exposure") : 1f);
                        material.SetFloat(SkyRotationId, activeSkybox.HasProperty("_Rotation") ? activeSkybox.GetFloat("_Rotation") : 0f);
                    }
                    else
                    {
                        shouldUseSky = false;
                    }
                }
                else if (settings.skyCubemap != null)
                {
                    material.SetTexture(SkyCubemapId, settings.skyCubemap);
                    material.SetColor(SkyTintId, settings.skyTint);
                    material.SetFloat(SkyExposureId, settings.skyExposure);
                    material.SetFloat(SkyRotationId, settings.skyRotation);
                }
                else
                {
                    shouldUseSky = false;
                }

                material.SetFloat(FadeToSkyboxId, shouldUseSky ? 1f : 0f);
            }

            private static FogEquation ConvertFogMode(FogMode mode)
            {
                return mode switch
                {
                    FogMode.Exponential => FogEquation.Exponential,
                    FogMode.ExponentialSquared => FogEquation.ExponentialSquared,
                    _ => FogEquation.Linear,
                };
            }
        }

        public override void Create()
        {
            if (settings.shader == null)
            {
                settings.shader = Shader.Find("Hidden/BBB/BeautifulFog");
            }

            if (settings.shader == null)
            {
                return;
            }

            if (_material == null || _material.shader != settings.shader)
            {
                CoreUtils.Destroy(_material);
                _material = CoreUtils.CreateEngineMaterial(settings.shader);
            }

            _pass = new FogPass(settings, _material)
            {
                renderPassEvent = settings.injectionPoint
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!settings.effectEnabled || _pass == null || _material == null)
            {
                return;
            }

            _pass.renderPassEvent = settings.injectionPoint;
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            _pass?.Dispose();
            _pass = null;
            CoreUtils.Destroy(_material);
            _material = null;
        }
    }
}
