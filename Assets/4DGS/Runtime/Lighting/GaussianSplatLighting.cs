using System;
using System.Collections.Generic;
using UnityEngine;
using Tide;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Lights a GaussianSplatRenderer's splats with the scene's lights, each
    /// splat as a point of lit surface: directional, point and spot lights
    /// (color, intensity, range, cone) and the ambient light, on the splat's
    /// own color, before its style. Meant for splats sampled from a mesh
    /// (.meshsplat), which carry their surface normal; their baked lighting
    /// should be off. The brightest shadow-casting directional or spot light
    /// casts shadows from the splats themselves, so the source meshes can
    /// stay hidden. Lighting is view-independent: every view, cubemap face
    /// and eye sees the same. Weight fades between the unlit and lit colors
    /// and is a timeline channel.
    /// </summary>
    [AddComponentMenu("Gaussian Splatting/Lighting")]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [DisallowMultipleComponent]
    public class GaussianSplatLighting : MonoBehaviour, ITideChannelTarget
    {
        private static readonly string[] ChannelNames = { "Weight" };

        [Tooltip("Blend from the splats' own colors (0) to their lit colors (1)")]
        [SerializeField, Range(0f, 1f)] private float weight = 1f;

        [Header("Lights")]
        [Tooltip("Scene: every active light whose culling mask takes this object's layer. List: only the lights below")]
        [SerializeField] private GaussianSplatLightSource lightSource = GaussianSplatLightSource.Scene;
        [Tooltip("The lights, for the List source")]
        [SerializeField] private Light[] lights = Array.Empty<Light>();
        [Tooltip("Scales the ambient light: the light probes around this object, or the scene's ambient without any")]
        [SerializeField, Min(0f)] private float ambientIntensity = 1f;
        [Tooltip("Softens the terminator: light wraps past where the surface turns away from it")]
        [SerializeField, Range(0f, 1f)] private float wrap;

        [Header("Shadows")]
        [Tooltip("The brightest shadow-casting directional or spot light casts shadows from these splats onto themselves")]
        [SerializeField] private bool shadows = true;
        [Tooltip("How dark the shadows are: 0 leaves the light, 1 blocks it fully")]
        [SerializeField, Range(0f, 1f)] private float shadowStrength = 1f;
        [Tooltip("Shadow map texels per side, fitted to the splats each frame")]
        [SerializeField, Range(128, 4096)] private int shadowResolution = 1024;
        [Tooltip("Depth, in world units, a splat may lie behind the nearest one before it is in shadow; raise it if surfaces shadow themselves in speckles")]
        [SerializeField, Min(0f)] private float shadowBias = 0.02f;
        [Tooltip("Texels around a splat the shadow test averages, for softer edges")]
        [SerializeField, Range(0, 4)] private int shadowFilter = 1;

        // Lights found in the scene, refreshed once per frame
        private static readonly List<Light> s_sceneLights = new();
        private static int s_sceneLightsFrame = -1;
        private readonly List<Light> _candidates = new();

        public float Weight
        {
            get => weight;
            set => weight = Mathf.Clamp01(value);
        }

        public GaussianSplatLightSource LightSource => lightSource;
        public IReadOnlyList<Light> Lights => lights;
        public float AmbientIntensity => ambientIntensity;
        public float Wrap => wrap;
        public bool Shadows => shadows;
        public float ShadowStrength => shadowStrength;
        public int ShadowResolution => shadowResolution;
        public float ShadowBias => shadowBias;
        public int ShadowFilter => shadowFilter;

        int ITideChannelTarget.ChannelCount => ChannelNames.Length;

        string ITideChannelTarget.GetChannelName(int index) => ChannelNames[index];

        TideValueType ITideChannelTarget.GetChannelType(int index) => TideValueType.Float;

        TideValue ITideChannelTarget.GetChannelValue(int index) => weight;

        void ITideChannelTarget.SetChannelValue(int index, TideValue value) => Weight = value.AsFloat;

        // Loads the lights as they are now: directional lights first, then
        // the brightest, as many as the shaders take (GaussianLightSet.MaxLights)
        internal void FillLightSet(GaussianLightSet set)
        {
            set.Begin(isActiveAndEnabled ? weight : 0f);
            if (!set.Active)
                return;
            set.SetSurface(wrap, ambientIntensity, transform.position);
            set.SetShadows(shadows, shadowStrength, shadowResolution, shadowBias, shadowFilter);

            _candidates.Clear();
            if (lightSource == GaussianSplatLightSource.Scene)
            {
                int layerBit = 1 << gameObject.layer;
                foreach (Light light in SceneLights())
                {
                    if ((light.cullingMask & layerBit) != 0)
                        _candidates.Add(light);
                }
            }
            else
            {
                foreach (Light light in lights)
                {
                    if (light != null && light.isActiveAndEnabled)
                        _candidates.Add(light);
                }
            }
            _candidates.Sort(CompareLights);
            foreach (Light light in _candidates)
                set.AddLight(light);
        }

        private static int CompareLights(Light a, Light b)
        {
            bool aDirectional = a.type == LightType.Directional;
            bool bDirectional = b.type == LightType.Directional;
            if (aDirectional != bDirectional)
                return aDirectional ? -1 : 1;
            return b.intensity.CompareTo(a.intensity);
        }

        private static List<Light> SceneLights()
        {
            // Edit mode renders many times within a frame (scrubbing), where
            // lights may change between renders, so it always looks again
            if (s_sceneLightsFrame == Time.frameCount && Application.isPlaying)
                return s_sceneLights;
            s_sceneLightsFrame = Time.frameCount;
            s_sceneLights.Clear();
            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.isActiveAndEnabled)
                    s_sceneLights.Add(light);
            }
            return s_sceneLights;
        }
    }
}
