using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Veldrid.SPIRV.Tests
{
    /// <summary>
    /// D0 / D3: "did the toolchain move an element?" - V4 / V5a / V6 compare what the GLSL SOURCE declares with what the compiler
    /// PRODUCED, by name. The detector is proven with hand-moved compiled interfaces (we cannot make the real compiler misbehave on
    /// demand); the real-compiler regression guard is D3_DeclaredInterfaceSurvivesOptimisation in ShaderInterfaceContractTests.
    /// </summary>
    public class DeclaredVsCompiledTests
    {
        private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestShaders", "InterfaceContract", name));

        [Fact]
        public void Parser_ReadsTheArcaneSiegeVertexShader()
        {
            var d = GlslInterfaceDeclarations.Parse(Fixture("ChunkInstanced.vert.glsl"));
            Assert.Empty(d.UnparseableLayoutLines);
            Assert.Equal(new[] { "CornerOffset", "Color" }, d.Inputs.Select(i => i.Name).ToArray());
            Assert.Equal(7, d.Outputs.Length);
            var grid = d.Outputs.Single(o => o.Name == "fragGridTile");
            Assert.Equal(5u, grid.Location); Assert.True(grid.Flat); Assert.Equal("vec2", grid.GlslType);
            Assert.Equal(new[] { "ProjectionBuffer", "ChunkOriginBuffer", "TileLightingParams" }, d.UniformBlocks.Select(b => b.BlockName).ToArray());
            var origin = d.UniformBlocks.Single(b => b.BlockName == "ChunkOriginBuffer");
            Assert.Equal(1u, origin.Set); Assert.Equal(0u, origin.Binding);
            Assert.Equal(12, origin.Members.Length);
            Assert.Equal("PixelsPerTile", origin.Members[5].Name);
            Assert.Equal(5, d.Resources.Length);
            Assert.Contains(d.Resources, r => r.Name == "LightSampler" && r.Set == 4 && r.Binding == 2 && r.GlslType == "sampler");
        }

        [Fact]
        public void Std140Oracle_MatchesTheCompilerOnTheFixtureBlocks()
        {
            var d = GlslInterfaceDeclarations.Parse(Fixture("ChunkInstanced.vert.glsl"));
            var origin = d.UniformBlocks.Single(b => b.BlockName == "ChunkOriginBuffer");
            var layout = GlslInterfaceDeclarations.Std140Layout(origin.Members, out uint size);
            Assert.Equal(48u, size);
            Assert.Equal((20u, 4u), layout[5]!.Value);   // PixelsPerTile
            // mat4 + float: 64 + 4 -> block padded to 80; vec3 then float packs into the vec3's padding
            var members = new[] { new GlslDeclared_Member("M", "mat4", 0), new GlslDeclared_Member("S", "float", 0) };
            GlslInterfaceDeclarations.Std140Layout(members, out uint sz); Assert.Equal(80u, sz);
            var packed = GlslInterfaceDeclarations.Std140Layout(new[] { new GlslDeclared_Member("N", "vec3", 0), new GlslDeclared_Member("W", "float", 0), new GlslDeclared_Member("A", "float", 4) }, out uint psz);
            Assert.Equal((12u, 4u), packed[1]!.Value);
            Assert.Equal((16u, 64u), packed[2]!.Value);   // float[4]: stride 16
            Assert.Equal(80u, psz);
        }

        private static SpirvReflection CompileFixture()
        {
            var vs = SpirvCompilation.CompileGlslToSpirv(Fixture("ChunkInstanced.vert.glsl"), "v", ShaderStages.Vertex, new GlslCompileOptions());
            var fs = SpirvCompilation.CompileGlslToSpirv(Fixture("ChunkInstanced_GBuffer.frag.glsl"), "f", ShaderStages.Fragment, new GlslCompileOptions());
#pragma warning disable CS0618
            return SpirvCompilation.CompileVertexFragment(vs.SpirvBytes, fs.SpirvBytes, CrossCompileTarget.HLSL, new CrossCompileOptions()).Reflection;
#pragma warning restore CS0618
        }

        [Fact]
        public void RealCompile_DeclaredEqualsCompiled()
            => Assert.Empty(ShaderInterfaceVerifier.VerifyDeclaredAgainstCompiled(Fixture("ChunkInstanced.vert.glsl"), Fixture("ChunkInstanced_GBuffer.frag.glsl"), CompileFixture()));

        [Fact]
        public void HandMovedVarying_IsNamed_V5a()
        {
            var real = CompileFixture();
            var moved = real.Interface.VertexOutputs.Select(v => v.Name == "fragTileUV" ? v with { Location = 9 } : v).ToArray();
            var tampered = new SpirvReflection(real.VertexElements, real.ResourceLayouts, real.BindingMap, real.Interface with { VertexOutputs = moved });
            var v = Assert.Single(ShaderInterfaceVerifier.VerifyDeclaredAgainstCompiled(Fixture("ChunkInstanced.vert.glsl"), Fixture("ChunkInstanced_GBuffer.frag.glsl"), tampered));
            Assert.Equal(ShaderInterfaceVerifier.Rule_V5a_VaryingsDeclared, v.Rule);
            Assert.Equal("fragTileUV", v.Name);
            Assert.Contains("MOVED", v.Detail);
        }

        [Fact]
        public void HandDroppedVarying_IsNamed_V5a()
        {
            var real = CompileFixture();
            var dropped = real.Interface.FragmentInputs.Where(v => v.Name != "fragTileRowParity").ToArray();
            var tampered = new SpirvReflection(real.VertexElements, real.ResourceLayouts, real.BindingMap, real.Interface with { FragmentInputs = dropped });
            var v = Assert.Single(ShaderInterfaceVerifier.VerifyDeclaredAgainstCompiled(Fixture("ChunkInstanced.vert.glsl"), Fixture("ChunkInstanced_GBuffer.frag.glsl"), tampered));
            Assert.Equal("fragTileRowParity", v.Name);
            Assert.Contains("eliminated", v.Detail);
        }

        [Fact]
        public void HandMovedUniformMember_IsNamed_V6()
        {
            var real = CompileFixture();
            var blocks = real.Interface.UniformBlocks.Select(b => b.BlockName != "ChunkOriginBuffer" ? b
                : b with { Members = b.Members.Select(m => m.Name == "PixelsPerTile" ? m with { OffsetBytes = 24 } : m).ToArray() }).ToArray();
            var tampered = new SpirvReflection(real.VertexElements, real.ResourceLayouts, real.BindingMap, real.Interface with { UniformBlocks = blocks });
            var v = Assert.Single(ShaderInterfaceVerifier.VerifyDeclaredAgainstCompiled(Fixture("ChunkInstanced.vert.glsl"), Fixture("ChunkInstanced_GBuffer.frag.glsl"), tampered));
            Assert.Equal(ShaderInterfaceVerifier.Rule_V6_BlockMembersDeclared, v.Rule);
            Assert.Equal("ChunkOriginBuffer.PixelsPerTile", v.Name);
            Assert.Contains("std140 oracle @ 20", v.SideA);
        }

        [Fact]
        public void HandMovedVertexInput_IsNamed_V4()
        {
            var real = CompileFixture();
            var elements = (VertexElementDescription[])real.VertexElements.Clone();
            (elements[0], elements[1]) = (elements[1], elements[0]);   // CornerOffset and Color swapped locations
            var tampered = new SpirvReflection(elements, real.ResourceLayouts, real.BindingMap, real.Interface);
            var violations = ShaderInterfaceVerifier.VerifyDeclaredAgainstCompiled(Fixture("ChunkInstanced.vert.glsl"), Fixture("ChunkInstanced_GBuffer.frag.glsl"), tampered);
            Assert.Equal(2, violations.Count(v => v.Rule == ShaderInterfaceVerifier.Rule_V4_VertexInputsDeclared && v.Detail.Contains("MOVED")));
        }

        [Fact]
        public void UnparseableLayoutLine_IsAViolation()
        {
            const string vert = "#version 450\n#define LOC 0\nlayout(location = LOC) in vec2 Position;\nlayout(location = 0) out vec2 vUv;\nvoid main() { vUv = Position; gl_Position = vec4(Position, 0, 1); }\n";
            const string frag = "#version 450\nlayout(location = 0) in vec2 vUv;\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(vUv, 0, 1); }\n";
            var ex = Assert.Throws<ShaderInterfaceContractException>(() => ShaderBundleCompiler.CompileVertexFragment(vert, frag, "Macro"));
            Assert.Contains(ex.Violations, v => v.Detail.Contains("cannot read"));
        }
    }
}