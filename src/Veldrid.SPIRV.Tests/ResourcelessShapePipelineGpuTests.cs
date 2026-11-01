using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace Veldrid.SPIRV.Tests
{
    /// <summary>
    /// A shape whose GLSL declares vertex <c>in</c>s and binds NOTHING (no uniform, texture or sampler - a clip-space debug quad) has no
    /// ResourceLayouts to name its pipeline; its ShaderSetDescription (built from the shape's VertexInput) does. Veldrid 5.261101.142151
    /// accepts that case in CreateGraphicsPipeline (before it, RequireOneShapeForLayouts refused the empty layout array and such a shape
    /// could never draw - found by ArcaneSiege's DebugQuadRenderer). This proves it end to end on D3D11: bind, create the pipeline with
    /// <c>ResourceLayouts = []</c>, draw a coloured triangle from a typed vertex buffer, read the colour back.
    /// </summary>
    public class ResourcelessShapePipelineGpuTests
    {
        private const string Vert = @"#version 450
layout(location = 0) in vec2 Position;
layout(location = 1) in vec4 Color;
layout(location = 0) out vec4 frag_Color;
void main() { frag_Color = Color; gl_Position = vec4(Position, 0.0, 1.0); }
";
        private const string Frag = @"#version 450
layout(location = 0) in vec4 frag_Color;
layout(location = 0) out vec4 out_Color;
void main() { out_Color = frag_Color; }
";

        [StructLayout(LayoutKind.Sequential)]
        public struct ColoredVertex { public Vector2 Position; public Vector4 Color; }

        [ShaderBundle("ResourcelessQuad")]
        public sealed partial class ResourcelessShape : ShaderPipelineShape
        {
            public VertexInput<ColoredVertex> Vertices;
        }

        private static readonly Vector4 Green = new(0f, 1f, 0f, 1f);

        [Fact]
        public void TheBundleDeclaresNoResources_ThePremise()
        {
            var bundle = ShaderBundleCompiler.CompileVertexFragment(Vert, Frag, "ResourcelessQuad");
            // The compiler records an EMPTY set 0 for a shader with no resources; what matters is that no set has an element.
            Assert.All(bundle.GetResourceLayouts(), layout => Assert.Empty(layout.Elements ?? Array.Empty<ResourceLayoutElementDescription>()));
        }

        [Fact]
        public void ShapeWithVertexInputAndNoSets_CreatesPipelineFromItsShaderSet_AndDraws_D3D11()
        {
            if (!OperatingSystem.IsWindows()) return;
            using var gd = GraphicsDevice.CreateD3D11(new GraphicsDeviceOptions());
            var f = gd.ResourceFactory;
            var bundle = f.CreateFromBundle(ShaderBundleCompiler.CompileVertexFragment(Vert, Frag, "ResourcelessQuad"), _ => throw new NotSupportedException(), null);
            using var shape = ShaderPipelineShape.Bind<ResourcelessShape>(bundle, f);

            using var target = f.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, PixelFormat.R32G32B32A32Float, TextureUsage.RenderTarget));
            using var staging = f.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, PixelFormat.R32G32B32A32Float, TextureUsage.Staging));
            using var fb = f.CreateFramebuffer(new FramebufferDescription(null, target));

            // The case under test: NO resource layouts; the shader set names the shape.
            var desc = new GraphicsPipelineDescription(BlendStateDescription.SingleOverrideBlend, DepthStencilStateDescription.DISABLED,
                RasterizerStateDescription.CULL_NONE, PrimitiveTopology.TriangleList,
                new ShaderSetDescription([shape.Vertices], bundle.Shaders), Array.Empty<ResourceLayout>(), fb.OutputDescription);
            using var pipeline = f.CreateGraphicsPipeline(ref desc);

            // A clip-space triangle covering the one pixel, painted green
            using var vertices = shape.Vertices.CreateBuffer(3, BufferUsage.VertexBuffer | BufferUsage.Dynamic);
            ColoredVertex[] triangle =
            [
                new() { Position = new(-1f, -1f), Color = Green },
                new() { Position = new( 3f, -1f), Color = Green },
                new() { Position = new(-1f,  3f), Color = Green },
            ];

            using var cl = f.CreateCommandList();
            cl.Begin();
            cl.UpdateBuffer(vertices, 0, (ReadOnlySpan<ColoredVertex>)triangle);
            cl.SetFramebuffer(fb);
            cl.ClearColorTarget(0, RgbaFloat.Black);
            cl.SetPipeline(pipeline);
            cl.SetVertexBuffer(shape.Vertices, vertices);
            cl.Draw(3);
            cl.CopyTexture(target, staging);
            cl.End();
            gd.SubmitCommands(cl);
            gd.WaitForIdle();
            var map = gd.Map<Vector4>(staging, MapMode.Read);
            Vector4 pixel = map[0];
            gd.Unmap(staging);

            Assert.True(Vector4.Distance(pixel, Green) < 1e-4f, $"the resource-less pipeline painted {pixel}; expected green");
        }
    }
}