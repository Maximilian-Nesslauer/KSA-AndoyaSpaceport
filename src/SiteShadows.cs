#if ANDOYA_SHADOW_PROFILE
using System.Diagnostics;
#endif
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KSA.Rendering.Lighting;
using Core;

namespace AndoyaSpaceport;

/// <summary>
/// Draws the site into the cascades of the sun shadow map, so that the deck, the tower and the other buildings cast
/// shadows. The game draws only the planet, the parts, the splines and the SuperMesh renderables into the cascades
/// (Program.RenderGame); StaticObjectRenderer has a colour and a pre-pass pipeline but no depth pipeline, so no static
/// object casts a sun shadow, the stock launch pads included. It does receive shadows, because its fragment shader
/// samples the cascades.
/// The pass reuses the per-frame data of the game: StaticObjectModel.WriteInstancesToGpu writes one indirect draw
/// command per model and the instance matrices of that model for each viewport and frame, and this class remembers
/// which of those commands belong to the site. A postfix of PartModelRenderer.DepthData.AddCommandsCSM, which runs
/// once per cascade inside the cascade render pass, then draws those commands with a depth-only pipeline made like
/// the CSM pipeline of the parts. The site is drawn into a cascade only when its bounding sphere can shade a receiver
/// of that cascade, so the pass is usually skipped when the site is outside the shadow range of the camera (1500 m by
/// default) or behind every receiver as seen from the sun. The near plane is not tested, so a far site that lies
/// toward the sun from the camera can still be drawn; the picture stays correct.
/// </summary>
internal static class SiteShadows
{
    private const string ShaderId = "AndoyaSpaceport_StaticObjectShadowCSM";

    // A sphere that holds every opaque model of the site, in metres in the site frame: the centre is this far
    // above the site origin. The models reach about 141 m from the origin horizontally and 27 m up (the light masts).
    private const double SphereCentreUp = 10.0;
    private const float SphereRadius = 150f;

    private static readonly int CommandStride = (int)ByteSize.Of<VkDrawIndexedIndirectCommand>().Bytes;

    private static readonly HashSet<string> SiteModelKeys = new(StringComparer.Ordinal);
    private static bool _modelsResolved;

    // The draw commands of the site per viewport and frame slot, as runs of consecutive command indices.
    private sealed class FrameCommands
    {
        public readonly List<(int First, int Count)> Runs = new();
    }

    private static readonly Dictionary<IViewport, FrameCommands[]> Commands = new();
    private static readonly Dictionary<IViewport, double3> SiteCentreEgo = new();

    private static VkPipeline _pipeline;
    private static VkPipelineLayout _pipelineLayout;
    private static CascadedShadowSystem? _pipelineCsm;
    private static VkPipeline _pipelineStaticPrePass;
    private static bool _pipelineFailed;
    private static int _faults;
    private static Action<string> _log = _ => { };

    // The test build (ShadowProfile) switches the pass off and on with this flag to measure its cost.
    internal static bool Enabled = true;

    internal static void Install(Harmony harmony, Action<string> log)
    {
        _log = log;
        harmony.Patch(AccessTools.Method(typeof(StaticObjectRenderer), nameof(StaticObjectRenderer.UpdateRenderData)),
            prefix: new HarmonyMethod(typeof(SiteShadows), nameof(UpdateRenderDataPrefix)));
        harmony.Patch(AccessTools.Method(typeof(StaticObjectModel), nameof(StaticObjectModel.WriteInstancesToGpu)),
            prefix: new HarmonyMethod(typeof(SiteShadows), nameof(WriteInstancesPrefix)),
            postfix: new HarmonyMethod(typeof(SiteShadows), nameof(WriteInstancesPostfix)));
        harmony.Patch(AccessTools.Method(typeof(PartModelRenderer.DepthData), nameof(PartModelRenderer.DepthData.AddCommandsCSM)),
            postfix: new HarmonyMethod(typeof(SiteShadows), nameof(AddCommandsCsmPostfix)));
        log("[AndoyaSpaceport shadows] installed.");
#if ANDOYA_SHADOW_PROFILE
        ShadowProfile.Start(log);
#endif
    }

    internal static double SiteDistance(IViewport viewport)
    {
        return SiteCentreEgo.TryGetValue(viewport, out double3 centre) ? centre.Length() : -1.0;
    }

    /// <summary>Called from the StaticObject.UpdateRenderData postfix with the site-to-eye matrix of the viewport.</summary>
    internal static void NoteSite(IViewport viewport, in double4x4 siteToEgo)
    {
        double3 up = new double3(siteToEgo.M11, siteToEgo.M12, siteToEgo.M13).Normalized();
        SiteCentreEgo[viewport] = new double3(siteToEgo.M41, siteToEgo.M42, siteToEgo.M43) + up * SphereCentreUp;
#if ANDOYA_SHADOW_PROFILE
        ShadowProfile.ClearSky();
#endif
    }

    private static void ResolveModels()
    {
        _modelsResolved = true;
        StaticObject? site = StaticObject.Find(LightsMod.SiteStaticObject);
        if (site == null)
        {
            _log("[AndoyaSpaceport shadows] static object " + LightsMod.SiteStaticObject + " not found, no shadows.");
            return;
        }
        StaticObjectTemplate template = site.Template;
        AddModels(template.Models);
        // The content loader marks a static object whose SubObject Ref does not resolve as failed, and
        // StaticObject.ResolveAll leaves a failed one out, so every Value of a site that Find returns is set.
        foreach (TransformDataReference<StaticSubObjectTemplate> sub in template.SubObjectInstances)
            AddModels(sub.Value!.Models);
        _log("[AndoyaSpaceport shadows] " + SiteModelKeys.Count + " shadow casting models.");
    }

    // Only models of the opaque bucket cast (see WriteInstancesPrefix), so the terrain skirt and the alpha blended
    // decals, which lie flat on the ground, cast nothing. ShadowCaster false in the XML of a PartModel excludes a
    // model too, as it does for a part. StaticObjectModel.Get shares one model per PartModel Key over all static
    // objects, so the Key names the model; make_site_mod.py checks that no Key of the site is also a stock Key.
    private static void AddModels(List<PartModelModule.Template> models)
    {
        foreach (PartModelModule.Template model in models)
        {
            if (model.ShadowCaster)
                SiteModelKeys.Add(model.Key);
        }
    }

    private static void UpdateRenderDataPrefix(IViewport viewport, int frameIndex)
    {
        if (Commands.TryGetValue(viewport, out FrameCommands[]? frames) && frameIndex < frames.Length)
            frames[frameIndex].Runs.Clear();
    }

    private static void WriteInstancesPrefix(StaticObjectModel __instance, IViewport viewport, int frameIndex, out int __state)
    {
        __state = -1;
        if (!Enabled || __instance.Bucket != StaticObjectModel.DrawBucket.Opaque)
            return;
        if (!_modelsResolved)
            ResolveModels();
        if (!SiteModelKeys.Contains(__instance.Template.Key))
            return;
        StaticObjectModel.Shared.ViewportData? data = StaticObjectModel.Shared.ViewportData.TryGet(viewport, StaticObjectModel.DrawBucket.Opaque);
        __state = data == null ? 0 : (int)data.DrawCommandVectors[frameIndex].ElementCount;
    }

    // WriteInstancesToGpu adds one command when the model has instances in this viewport, so a grown count marks the
    // command of a site model at the old count.
    private static void WriteInstancesPostfix(IViewport viewport, int frameIndex, int __state)
    {
        if (__state < 0)
            return;
        StaticObjectModel.Shared.ViewportData? data = StaticObjectModel.Shared.ViewportData.TryGet(viewport, StaticObjectModel.DrawBucket.Opaque);
        if (data == null || (int)data.DrawCommandVectors[frameIndex].ElementCount != __state + 1)
            return;
        if (!Commands.TryGetValue(viewport, out FrameCommands[]? frames))
        {
            frames = new FrameCommands[data.DrawCommandVectors.Length];
            for (int i = 0; i < frames.Length; i++)
                frames[i] = new FrameCommands();
            Commands.Add(viewport, frames);
        }
        List<(int First, int Count)> runs = frames[frameIndex].Runs;
        if (runs.Count > 0 && runs[^1].First + runs[^1].Count == __state)
            runs[^1] = (runs[^1].First, runs[^1].Count + 1);
        else
            runs.Add((__state, 1));
    }

    private static void AddCommandsCsmPostfix(CommandBuffer inCmdBuffer, IViewport viewport, int inFrameIndex, int inCascade)
    {
#if ANDOYA_SHADOW_PROFILE
        ShadowProfile.OnCascade(viewport, inCascade);
        long start = Stopwatch.GetTimestamp();
#endif
        if (!Enabled || _pipelineFailed)
            return;
        try
        {
            if (!Commands.TryGetValue(viewport, out FrameCommands[]? frames) || inFrameIndex >= frames.Length)
                return;
            List<(int First, int Count)> runs = frames[inFrameIndex].Runs;
            if (runs.Count == 0 || !SiteCentreEgo.TryGetValue(viewport, out double3 centre))
                return;
            CascadedShadowSystem csm = Program.GetCSMSystem();
            if (!OverlapsCascade(csm.CascadeData[inCascade].ViewProj, centre))
                return;
            StaticObjectModel.Shared.ViewportData? data = StaticObjectModel.Shared.ViewportData.TryGet(viewport, StaticObjectModel.DrawBucket.Opaque);
            if (data == null || !EnsurePipeline(csm))
                return;

            inCmdBuffer.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
            inCmdBuffer.SetDepthBias(csm.CurrentSettings.VkDepthBias, 0f, csm.CurrentSettings.VkSlopeBias);
            VkDescriptorSet csmSet = csm.DescriptorSet;
            ByteSize32 csmOffset = csm.DescriptorDynamicOffset!.Value;
            inCmdBuffer.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 0,
                new ReadOnlySpan<VkDescriptorSet>(in csmSet), new Span<ByteSize32>(ref csmOffset));
            inCmdBuffer.BindDescriptorSet(VkPipelineBindPoint.Graphics, _pipelineLayout, 1,
                data.PerInstanceDescriptorSets[inFrameIndex], default(Span<ByteSize32>));
            inCmdBuffer.PushConstants(_pipelineLayout, VkShaderStageFlags.VertexBit, ByteSize.Zero, inCascade);
            DeviceMeshInterleaved.Shared.BindBuffers(inCmdBuffer);
            DeviceVector commands = data.DrawCommandVectors[inFrameIndex];
            foreach ((int first, int count) in runs)
            {
                inCmdBuffer.DrawIndexedIndirect(commands.VkBuffer, commands.Offset + (ulong)(first * CommandStride), count,
                    ByteSize.Of<VkDrawIndexedIndirectCommand>());
            }
#if ANDOYA_SHADOW_PROFILE
            ShadowProfile.OnDrawn(Stopwatch.GetTimestamp() - start);
#endif
        }
        catch (Exception e)
        {
            if (_faults++ < 5)
                _log("[AndoyaSpaceport shadows] draw fault: " + e);
        }
    }

    // The cascade matrices are orthographic and map the eye frame to clip space with row vectors (clip = p * M), so
    // column j of the upper 3x3 block scales the clip coordinate j. A caster shades the receivers at the same light
    // space x and y, so the sphere can cast into the cascade only when it overlaps the x and y range [-1, 1].
    // CascadedShadowSystem.ComputeCascadeViewProj puts the far plane (clip z 1) just behind the farthest receiver of
    // the cascade as seen from the sun, so a sphere wholly beyond it is behind every receiver and shades none. The
    // near plane is not tested, because the pipeline clamps depth and a caster between the sun and the near plane
    // still casts.
    private static bool OverlapsCascade(in float4x4 viewProj, double3 centreEgo)
    {
        float4 c = float4.Transform(new float4((float)centreEgo.X, (float)centreEgo.Y, (float)centreEgo.Z, 1f), viewProj);
        float rx = SphereRadius * MathF.Sqrt(viewProj.M11 * viewProj.M11 + viewProj.M21 * viewProj.M21 + viewProj.M31 * viewProj.M31);
        float ry = SphereRadius * MathF.Sqrt(viewProj.M12 * viewProj.M12 + viewProj.M22 * viewProj.M22 + viewProj.M32 * viewProj.M32);
        float rz = SphereRadius * MathF.Sqrt(viewProj.M13 * viewProj.M13 + viewProj.M23 * viewProj.M23 + viewProj.M33 * viewProj.M33);
        return MathF.Abs(c.X) - rx <= 1f && MathF.Abs(c.Y) - ry <= 1f && c.Z - rz <= 1f;
    }

    // The pipeline depends on the descriptor set layout and the render target of the cascades, which
    // Program.RebuildShadowRenderers replaces, and on the shaders and settings that Program.RebuildRenderer reloads,
    // which StaticObjectRenderer.Rebuild marks with a new pre-pass pipeline. Both rebuilds run while the device is
    // idle, so the old pipeline is no longer in use when the first frame after them builds a new one.
    // A fault while the pipeline is built switches the shadows off until the game ends, because a retry would throw again
    // in every cascade of every frame. ModLibrary.Get throws for a missing shader Id instead of returning null.
    private static bool EnsurePipeline(CascadedShadowSystem csm)
    {
        if (_pipeline.VkHandle != 0 && ReferenceEquals(csm, _pipelineCsm) && _pipelineStaticPrePass.VkHandle == StaticObjectRenderer.PrePassPipeline.VkHandle)
            return true;
        try
        {
            return BuildPipeline(csm);
        }
        catch (Exception e)
        {
            _pipelineFailed = true;
            _log("[AndoyaSpaceport shadows] the shadow pipeline failed to build, no shadows: " + e);
            return false;
        }
    }

    private static unsafe bool BuildPipeline(CascadedShadowSystem csm)
    {
        DeviceEx device = Program.GetRenderer().Device;
        DestroyPipeline(device);
        ShaderReference shader = ModLibrary.Get<ShaderReference>(ShaderId);
        if (shader?.Shader == null)
        {
            _pipelineFailed = true;
            _log("[AndoyaSpaceport shadows] shader " + ShaderId + " is not loaded, no shadows.");
            return false;
        }

        VkPushConstantRange pushRange = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit,
            Offset = ByteSize.Zero,
            Size = ByteSize.Of<int>()
        };
        VkDescriptorSetLayout* setLayouts = stackalloc VkDescriptorSetLayout[2];
        setLayouts[0] = csm.DescriptorSetLayout;
        setLayouts[1] = StaticObjectRenderer.PerInstanceDataDescriptorSetLayout;
        _pipelineLayout = device.CreatePipelineLayout(new ReadOnlySpan<VkDescriptorSetLayout>(setLayouts, 2),
            new ReadOnlySpan<VkPushConstantRange>(in pushRange), null);

        VkPipelineShaderStageCreateInfo stage = new VkPipelineShaderStageCreateInfo
        {
            Name = Presets.Entrypoint.Main,
            Module = shader.Shader.Value,
            Stage = shader.Stage
        };
        // DepthData.VertexInput holds native memory that only PartModelRenderer.DepthData.Dispose frees, which
        // Program.Dispose calls when the game ends, so it is valid whenever a cascade pass builds this pipeline.
        VkPipelineRasterizationStateCreateInfo rasterization = PartModelRenderer.DepthData.CreateRasterizationStateCreateInfo();
        VkDynamicState* dynamicStates = stackalloc VkDynamicState[1] { VkDynamicState.DepthBias };
        VkPipelineDynamicStateCreateInfo dynamicState = new VkPipelineDynamicStateCreateInfo
        {
            DynamicStateCount = 1,
            DynamicStates = dynamicStates
        };
        VkGraphicsPipelineCreateInfo info = new VkGraphicsPipelineCreateInfo
        {
            Layout = _pipelineLayout,
            StageCount = 1,
            Stages = &stage,
            DynamicState = &dynamicState,
            VertexInputState = PartModelRenderer.DepthData.VertexInput,
            InputAssemblyState = Presets.InputAssembly.TriangleList,
            RasterizationState = &rasterization,
            DepthStencilState = Presets.DepthStencil.DepthTestWrite,
            ColorBlendState = Presets.BlendState.BlendNone
        };
        csm.SetupGraphicsPipeline(ref info);
        _pipeline = device.CreateGraphicsPipeline(PipelineCaches.Shared, info, null);
        if (_pipeline.VkHandle == 0)
        {
            _pipelineFailed = true;
            _log("[AndoyaSpaceport shadows] the shadow pipeline failed to build, no shadows.");
            return false;
        }
        _pipelineCsm = csm;
        _pipelineStaticPrePass = StaticObjectRenderer.PrePassPipeline;
        _log("[AndoyaSpaceport shadows] shadow pipeline built.");
        return true;
    }

    private static void DestroyPipeline(DeviceEx device)
    {
        if (_pipeline.VkHandle != 0)
            device.DestroyPipeline(_pipeline, null);
        if (_pipelineLayout.VkHandle != 0)
            device.DestroyPipelineLayout(_pipelineLayout, null);
        _pipeline = default;
        _pipelineLayout = default;
    }
}
