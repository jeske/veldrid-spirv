using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace Veldrid.SPIRV.Tests
{
    /// <summary>
    /// THE GOAL, end to end on a real device (user, 2026-11-01): the C# data bound at GLSL (set, binding) is what the shader reads
    /// there, no matter how the platform compiler numbered its registers.
    /// <para>Solid-colour triangle. Set 1 = { Color2 at binding 0, Color3 at binding 1 } declared identically by two shaders. Shader A
    /// paints the triangle with Color2; shader B paints with Color3 and never touches Color2. ONE resource set, created from shape A,
    /// is bound to both pipelines. Pipeline B's triangle must come out Color3 - never Color2.</para>
    /// <para>Why the trap exists: SPIRV-Cross numbers D3D11 / Metal registers per kind across ALL sets of a shader. Shader A has two
    /// set-0 blocks, B has one, so set 1 lands in different registers in the two compiled shaders (A: Color2=b2 Color3=b3;
    /// B: Color2=b1 Color3=b2). A's Color2 register IS B's Color3 register. If Veldrid translated (set, binding) through the shader the
    /// SET came from, pipeline B would paint Color2. (Declared-but-unused slots alone do not shift registers - see
    /// SharedMaterialSetFlatBindingTests; a difference elsewhere in the shader does.)</para>
    /// </summary>
    public class SharedSetCrossPipelineBindingGpuTests
    {
        private const string Vert = @"#version 450
void main() { vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2); gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); }
";
        private const string SharedColors = @"
layout(set = 1, binding = 0) uniform Color2 { vec4 color2; };
layout(set = 1, binding = 1) uniform Color3 { vec4 color3; };
layout(location = 0) out vec4 o;
";
        // A: two set-0 blocks; paints with Color2.
        private const string FragA = "#version 450\nlayout(set = 0, binding = 0) uniform PadA { vec4 padA; };\nlayout(set = 0, binding = 1) uniform PadB { vec4 padB; };\n" + SharedColors + @"
void main() { o = color2 + padA * 0.0 + padB * 0.0; }
";
        // B: one set-0 block; paints with Color3, never reads Color2.
        private const string FragB = "#version 450\nlayout(set = 0, binding = 0) uniform PadA { vec4 padA; };\n" + SharedColors + @"
void main() { o = color3 + padA * 0.0; }
";

        [StructLayout(LayoutKind.Sequential)] public struct PadABlock { public Vector4 padA; }
        [StructLayout(LayoutKind.Sequential)] public struct PadBBlock { public Vector4 padB; }
        [StructLayout(LayoutKind.Sequential)] public struct Color2Block { public Vector4 color2; }
        [StructLayout(LayoutKind.Sequential)] public struct Color3Block { public Vector4 color3; }

        [ShaderBundle("SolidColor2")]
        public sealed partial class ShapeA : ShaderPipelineShape
        {
            public UniformBlock<PadABlock> PadA; public UniformBlock<PadBBlock> PadB;
            public UniformBlock<Color2Block> Color2; public UniformBlock<Color3Block> Color3;
        }

        [ShaderBundle("SolidColor3")]
        public sealed partial class ShapeB : ShaderPipelineShape
        {
            public UniformBlock<PadABlock> PadA;
            public UniformBlock<Color2Block> Color2; public UniformBlock<Color3Block> Color3;
        }

        private static readonly Vector4 Red = new(1f, 0f, 0f, 1f);     // Color2
        private static readonly Vector4 Blue = new(0f, 0f, 1f, 1f);    // Color3

        [Fact]
        public void TheTwoCompiledShadersNumberSet1Differently_ThePremise()
        {
            var a = ShaderBundleCompiler.CompileVertexFragment(Vert, FragA, "SolidColor2");
            var b = ShaderBundleCompiler.CompileVertexFragment(Vert, FragB, "SolidColor3");
            uint Reg(VeldridShaderBundle bundle, uint binding) => bundle.FlatBindingMap.Single(e => e.Set == 1 && e.Binding == binding).FlatIndex;
            Assert.NotEqual(Reg(a, 1), Reg(b, 1));     // Color3 sits in different registers in A and B
            Assert.Equal(Reg(a, 0), Reg(b, 1));        // and A's Color2 register IS B's Color3 register - the trap
            var v = new System.Collections.Generic.List<Veldrid.ShaderContractViolation>();
            ShaderContractVerifier.VerifySharedSetBinding(1, b.GetResourceLayouts()[1], a.GetResourceLayouts()[1], v);
            Assert.Empty(v);                           // identical declarations: the set is legal on both pipelines
        }

        [Fact]
        public void OneSetFromShapeA_PipelineAPaintsColor2_PipelineBPaintsColor3_D3D11()
        {
            if (!OperatingSystem.IsWindows()) return;
            using var gd = GraphicsDevice.CreateD3D11(new GraphicsDeviceOptions());
            var f = gd.ResourceFactory;
            var bundleA = f.CreateFromBundle(ShaderBundleCompiler.CompileVertexFragment(Vert, FragA, "SolidColor2"), _ => throw new NotSupportedException(), null);
            var bundleB = f.CreateFromBundle(ShaderBundleCompiler.CompileVertexFragment(Vert, FragB, "SolidColor3"), _ => throw new NotSupportedException(), null);
            using var a = ShaderPipelineShape.Bind<ShapeA>(bundleA, f);
            using var b = ShaderPipelineShape.Bind<ShapeB>(bundleB, f);
            a.PadA.CreateBuffer(BufferUsage.UniformBuffer); a.PadB.CreateBuffer(BufferUsage.UniformBuffer);
            a.Color2.CreateBuffer(BufferUsage.UniformBuffer); a.Color3.CreateBuffer(BufferUsage.UniformBuffer);
            b.PadA.CreateBuffer(BufferUsage.UniformBuffer);

            // THE shared set - created from shape A, bound to both pipelines.
            using var colorsLayoutA = f.CreateResourceLayout(new ResourceLayoutDescription(a.Color2, a.Color3));
            using var colorsFromA = f.CreateResourceSet(new ResourceSetDescription(colorsLayoutA, a.Color2.With(), a.Color3.With()));
            // Pipeline A
            using var set0LayoutA = f.CreateResourceLayout(new ResourceLayoutDescription(a.PadA, a.PadB));
            using var set0A = f.CreateResourceSet(new ResourceSetDescription(set0LayoutA, a.PadA.With(), a.PadB.With()));
            // Pipeline B (its own layouts: a pipeline binds one shape's layouts)
            using var set0LayoutB = f.CreateResourceLayout(new ResourceLayoutDescription(b.PadA));
            using var colorsLayoutB = f.CreateResourceLayout(new ResourceLayoutDescription(b.Color2, b.Color3));
            using var set0B = f.CreateResourceSet(new ResourceSetDescription(set0LayoutB, b.PadA.With()));

            using var target = f.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, PixelFormat.R32G32B32A32Float, TextureUsage.RenderTarget));
            using var staging = f.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, PixelFormat.R32G32B32A32Float, TextureUsage.Staging));
            using var fb = f.CreateFramebuffer(new FramebufferDescription(null, target));
            using var pipelineA = Pipeline(f, fb, bundleA, set0LayoutA, colorsLayoutA);
            using var pipelineB = Pipeline(f, fb, bundleB, set0LayoutB, colorsLayoutB);

            using var cl = f.CreateCommandList();
            cl.Begin();
            cl.UpdateBuffer(a.Color2, new Color2Block { color2 = Red });
            cl.UpdateBuffer(a.Color3, new Color3Block { color3 = Blue });
            cl.UpdateBuffer(a.PadA, new PadABlock()); cl.UpdateBuffer(a.PadB, new PadBBlock()); cl.UpdateBuffer(b.PadA, new PadABlock());
            cl.End(); gd.SubmitCommands(cl); gd.WaitForIdle();

            Vector4 paintedByA = Draw(gd, cl, fb, target, staging, pipelineA, set0A, colorsFromA);
            Vector4 paintedByB = Draw(gd, cl, fb, target, staging, pipelineB, set0B, colorsFromA);

            Assert.True(Vector4.Distance(paintedByA, Red) < 1e-4f, $"pipeline A painted {paintedByA}; expected Color2 = red");
            Assert.True(Vector4.Distance(paintedByB, Blue) < 1e-4f, $"pipeline B painted {paintedByB}; expected Color3 = blue (red would mean B read Color2's data in Color3's slot)");
        }

        private static Pipeline Pipeline(ResourceFactory f, Framebuffer fb, PrecompiledShaderResult bundle, ResourceLayout set0, ResourceLayout set1)
        {
            var desc = new GraphicsPipelineDescription(BlendStateDescription.SingleOverrideBlend, DepthStencilStateDescription.DISABLED,
                RasterizerStateDescription.CULL_NONE, PrimitiveTopology.TriangleList,
                new ShaderSetDescription(Array.Empty<VertexInput>(), bundle.Shaders), [set0, set1], fb.OutputDescription);
            return f.CreateGraphicsPipeline(ref desc);
        }

        private static Vector4 Draw(GraphicsDevice gd, CommandList cl, Framebuffer fb, Texture target, Texture staging, Pipeline pipeline, ResourceSet set0, ResourceSet colors)
        {
            cl.Begin();
            cl.SetFramebuffer(fb);
            cl.ClearColorTarget(0, RgbaFloat.Black);
            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(set0);
            cl.SetGraphicsResourceSet(colors);
            cl.Draw(3);
            cl.CopyTexture(target, staging);
            cl.End();
            gd.SubmitCommands(cl);
            gd.WaitForIdle();
            var map = gd.Map<Vector4>(staging, MapMode.Read);
            Vector4 pixel = map[0];
            gd.Unmap(staging);
            return pixel;
        }
    }
}