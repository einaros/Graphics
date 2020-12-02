using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipeline
    {
        static void DrawOpaqueRendererList(in RenderGraphContext context, in FrameSettings frameSettings, in RendererList rendererList)
        {
            DrawOpaqueRendererList(context.renderContext, context.cmd, frameSettings, rendererList);
        }

        static void DrawTransparentRendererList(in RenderGraphContext context, in FrameSettings frameSettings, RendererList rendererList)
        {
            DrawTransparentRendererList(context.renderContext, context.cmd, frameSettings, rendererList);
        }

        internal static int SampleCountToPassIndex(MSAASamples samples)
        {
            switch (samples)
            {
                case MSAASamples.None:
                    return 0;
                case MSAASamples.MSAA2x:
                    return 1;
                case MSAASamples.MSAA4x:
                    return 2;
                case MSAASamples.MSAA8x:
                    return 3;
            }
            ;
            return 0;
        }

        bool NeedClearColorBuffer(HDCamera hdCamera)
        {
            if (hdCamera.clearColorMode == HDAdditionalCameraData.ClearColorMode.Color ||
                // If the luxmeter is enabled, the sky isn't rendered so we clear the background color
                m_CurrentDebugDisplaySettings.data.lightingDebugSettings.debugLightingMode == DebugLightingMode.LuxMeter ||
                // If the matcap view is enabled, the sky isn't updated so we clear the background color
                m_CurrentDebugDisplaySettings.DebugHideSky(hdCamera) ||
                // If we want the sky but the sky don't exist, still clear with background color
                (hdCamera.clearColorMode == HDAdditionalCameraData.ClearColorMode.Sky && !m_SkyManager.IsVisualSkyValid(hdCamera)) ||
                // Special handling for Preview we force to clear with background color (i.e black)
                // Note that the sky use in this case is the last one setup. If there is no scene or game, there is no sky use as reflection in the preview
                HDUtils.IsRegularPreviewCamera(hdCamera.camera) ||
                // If we run full screen debug we need to clear to avoid issues with skybox.
                m_CurrentDebugDisplaySettings.IsFullScreenDebugPassEnabled())
            {
                return true;
            }

            return false;
        }

        Color GetColorBufferClearColor(HDCamera hdCamera)
        {
            Color clearColor = hdCamera.backgroundColorHDR;

            // We set the background color to black when the luxmeter is enabled to avoid picking the sky color
            if (m_CurrentDebugDisplaySettings.data.lightingDebugSettings.debugLightingMode == DebugLightingMode.LuxMeter ||
                m_CurrentDebugDisplaySettings.DebugHideSky(hdCamera) ||
                m_CurrentDebugDisplaySettings.IsDebugMaterialDisplayEnabled())
                clearColor = Color.clear;

            return clearColor;
        }

        // XR Specific
        class XRRenderingPassData
        {
            public XRPass xr;
        }

        internal static void StartXRSinglePass(RenderGraph renderGraph, HDCamera hdCamera)
        {
            if (hdCamera.xr.enabled)
            {
                using (var builder = renderGraph.AddRenderPass<XRRenderingPassData>("Start XR single-pass", out var passData))
                {
                    passData.xr = hdCamera.xr;

                    builder.SetRenderFunc(
                        (XRRenderingPassData data, RenderGraphContext context) =>
                        {
                            data.xr.StartSinglePass(context.cmd);
                        });
                }
            }
        }

        internal static void StopXRSinglePass(RenderGraph renderGraph, HDCamera hdCamera)
        {
            if (hdCamera.xr.enabled)
            {
                using (var builder = renderGraph.AddRenderPass<XRRenderingPassData>("Stop XR single-pass", out var passData))
                {
                    passData.xr = hdCamera.xr;

                    builder.SetRenderFunc(
                        (XRRenderingPassData data, RenderGraphContext context) =>
                        {
                            data.xr.StopSinglePass(context.cmd);
                        });
                }
            }
        }

        class EndCameraXRPassData
        {
            public HDCamera hdCamera;
        }

        void EndCameraXR(RenderGraph renderGraph, HDCamera hdCamera)
        {
            if (hdCamera.xr.enabled)
            {
                using (var builder = renderGraph.AddRenderPass<EndCameraXRPassData>("End Camera", out var passData))
                {
                    passData.hdCamera = hdCamera;

                    builder.SetRenderFunc(
                        (EndCameraXRPassData data, RenderGraphContext ctx) =>
                        {
                            data.hdCamera.xr.EndCamera(ctx.cmd, data.hdCamera);
                        });
                }
            }
        }

        class RenderOcclusionMeshesPassData
        {
            public HDCamera hdCamera;
            public TextureHandle colorBuffer;
            public TextureHandle depthBuffer;
            public Color clearColor;
        }

        void RenderXROcclusionMeshes(RenderGraph renderGraph, HDCamera hdCamera, TextureHandle colorBuffer, TextureHandle depthBuffer)
        {
            if (hdCamera.xr.enabled && m_Asset.currentPlatformRenderPipelineSettings.xrSettings.occlusionMesh)
            {
                using (var builder = renderGraph.AddRenderPass<RenderOcclusionMeshesPassData>("XR Occlusion Meshes", out var passData))
                {
                    passData.hdCamera = hdCamera;
                    passData.colorBuffer = builder.WriteTexture(colorBuffer);
                    passData.depthBuffer = builder.UseDepthBuffer(depthBuffer, DepthAccess.Write);
                    passData.clearColor = GetColorBufferClearColor(hdCamera);

                    builder.SetRenderFunc(
                        (RenderOcclusionMeshesPassData data, RenderGraphContext ctx) =>
                        {
                            data.hdCamera.xr.RenderOcclusionMeshes(ctx.cmd, data.clearColor, data.colorBuffer, data.depthBuffer);
                        });
                }
            }
        }

        class BlitCameraTextureData
        {
            public TextureHandle source;
            public TextureHandle destination;
            public float mipLevel;
            public bool bilinear;
        }

        static internal void BlitCameraTexture(RenderGraph renderGraph, TextureHandle source, TextureHandle destination, float mipLevel = 0.0f, bool bilinear = false)
        {
            using (var builder = renderGraph.AddRenderPass<BlitCameraTextureData>("Blit Camera Texture", out var passData))
            {
                passData.source = builder.ReadTexture(source);
                passData.destination = builder.WriteTexture(destination);
                passData.mipLevel = mipLevel;
                passData.bilinear = bilinear;
                builder.SetRenderFunc(
                    (BlitCameraTextureData data, RenderGraphContext ctx) =>
                    {
                        HDUtils.BlitCameraTexture(ctx.cmd, data.source, data.destination, data.mipLevel, data.bilinear);
                    });
            }
        }
    }

    internal struct XRSinglePassScope : System.IDisposable
    {
        readonly RenderGraph m_RenderGraph;
        readonly HDCamera m_HDCamera;

        bool m_Disposed;

        public XRSinglePassScope(RenderGraph renderGraph, HDCamera hdCamera)
        {
            m_RenderGraph = renderGraph;
            m_HDCamera = hdCamera;
            m_Disposed = false;

            HDRenderPipeline.StartXRSinglePass(renderGraph, hdCamera);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        // Protected implementation of Dispose pattern.
        void Dispose(bool disposing)
        {
            if (m_Disposed)
                return;

            // As this is a struct, it could have been initialized using an empty constructor so we
            // need to make sure `cmd` isn't null to avoid a crash. Switching to a class would fix
            // this but will generate garbage on every frame (and this struct is used quite a lot).
            if (disposing)
            {
                HDRenderPipeline.StopXRSinglePass(m_RenderGraph, m_HDCamera);
            }

            m_Disposed = true;
        }
    }
}
