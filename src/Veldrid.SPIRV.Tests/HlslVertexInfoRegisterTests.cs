using System;
using System.Linq;
using System.Text;
using Xunit;

namespace Veldrid.SPIRV.Tests
{
    /// <summary>
    /// V11 (AN_Monsters spec 204 T1, evidence 202 F13): on the HLSL target a vertex shader that reads gl_VertexIndex / gl_InstanceIndex gets
    /// SPIRV-Cross's host-filled <c>SPIRV_Cross_VertexInfo</c> cbuffer at the NEXT FREE b# after the shader's own uniform buffers, and the
    /// register is reported in the reflection so the host (Veldrid D3D11) binds exactly that. A shader reading neither builtin gets nothing.
    /// </summary>
    public class HlslVertexInfoRegisterTests
    {
        private const string FragPassThrough = "#version 450\nlayout(location = 0) in vec4 vColor;\nlayout(location = 0) out vec4 o;\nvoid main() { o = vColor; }\n";

        // Two uniform buffers (b0, b1 on HLSL) THEN the index builtin -> VertexInfo must land on b2.
        private const string VertReadsInstanceIndex =
            "#version 450\n" +
            "layout(set = 0, binding = 0) uniform A { vec4 ColumnBase; };\n" +
            "layout(set = 0, binding = 1) uniform B { vec4 Scale; };\n" +
            "layout(location = 0) out vec4 vColor;\n" +
            "void main() { float x = ColumnBase.x + float(gl_InstanceIndex) * Scale.x; vColor = vec4(x, 0, 0, 1); gl_Position = vec4(x, float(gl_VertexIndex), 0.5, 1.0); }\n";

        private const string VertReadsNoIndexBuiltin =
            "#version 450\n" +
            "layout(set = 0, binding = 0) uniform A { vec4 ColumnBase; };\n" +
            "layout(location = 0) out vec4 vColor;\n" +
            "void main() { vColor = ColumnBase; gl_Position = vec4(ColumnBase.xy, 0.5, 1.0); }\n";

#pragma warning disable CS0618
        private static VertexFragmentCompilationResult Compile(string vert, CrossCompileTarget target)
        {
            var vs = SpirvCompilation.CompileGlslToSpirv(vert, "v", ShaderStages.Vertex, new GlslCompileOptions());
            var fs = SpirvCompilation.CompileGlslToSpirv(FragPassThrough, "f", ShaderStages.Fragment, new GlslCompileOptions());
            return SpirvCompilation.CompileVertexFragment(vs.SpirvBytes, fs.SpirvBytes, target, new CrossCompileOptions());
        }
#pragma warning restore CS0618

        [Fact]
        public void Hlsl_VertexReadingInstanceIndex_GetsVertexInfoAtTheNextFreeRegister_AndReportsIt()
        {
            var result = Compile(VertReadsInstanceIndex, CrossCompileTarget.HLSL);
            Assert.Equal(2u, result.Reflection.HlslVertexInfoRegister);
            Assert.Contains("cbuffer SPIRV_Cross_VertexInfo : register(b2)", result.VertexShader);
            Assert.Contains("SPIRV_Cross_BaseInstance", result.VertexShader);
            Assert.Contains("SPIRV_Cross_BaseVertex", result.VertexShader);
            // The shader's own blocks keep b0/b1 - the aux register never displaces an application block.
            Assert.Contains("register(b0)", result.VertexShader);
            Assert.Contains("register(b1)", result.VertexShader);
            Assert.DoesNotContain(result.Reflection.BindingMap, e => e.Kind == ResourceKind.UniformBuffer && e.FlatIndex == 2);
        }

        [Fact]
        public void Hlsl_VertexReadingNoIndexBuiltin_GetsNoVertexInfo()
        {
            var result = Compile(VertReadsNoIndexBuiltin, CrossCompileTarget.HLSL);
            Assert.Null(result.Reflection.HlslVertexInfoRegister);
            Assert.DoesNotContain("SPIRV_Cross_VertexInfo", result.VertexShader);
        }

        [Theory]
        [InlineData(CrossCompileTarget.MSL)]
        [InlineData(CrossCompileTarget.GLSL)]
        public void NonHlslTargets_ReportNoVertexInfoRegister(CrossCompileTarget target)
        {
            var result = Compile(VertReadsInstanceIndex, target);
            Assert.Null(result.Reflection.HlslVertexInfoRegister);
        }

        [Fact]
        public void Bundle_D3D11Slot_StillCompilesToDxbc_WithVertexInfo()
        {
            // fxc must accept the aux cbuffer at b2 (the whole point of letting the compiler assign it).
            var bundle = ShaderBundleCompiler.CompileVertexFragment(VertReadsInstanceIndex, FragPassThrough, "VertexInfoProbe");
            var d3d = bundle.Backends[VeldridShaderBundle.GetBackendKey(GraphicsBackend.Direct3D11)];
            if (OperatingSystem.IsWindows()) Assert.Equal("dxbc", d3d.ShaderFormat);
            Assert.NotNull(d3d.VertexShaderData);
        }
    }
}