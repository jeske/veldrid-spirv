using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Veldrid.SPIRV.Tests
{
    /// <summary>
    /// The shader interface contract (AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md), compile side.
    /// Fixture: ArcaneSiege's ChunkInstanced pair as of 2026-10-31 - the third production hit of the D3D11 varying-packing class.
    /// The vertex shader emits locations 0..6; the ORIGINAL vertex-lit fragment declared only 0,1,2 and 6, so fxc packed the two
    /// stages differently (red/green tiles). The compiler must REFUSE that pair forever (V5b). The FIXED fragment declares 3..5 without
    /// reading them; with D3 (declared interface never eliminated) that is a complete, matching interface and compiles.
    /// </summary>
    public class ShaderInterfaceContractTests
    {
        private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestShaders", "InterfaceContract", name));

        [Fact]
        public void ArcaneSiege_ChunkInstanced_VertexLit_Original_ThrowsNamingTheMissingVaryings()
        {
            string vert = Fixture("ChunkInstanced.vert.glsl");
            string frag = Fixture("ChunkInstanced_VertexLit_Original.frag.glsl");

            var ex = Assert.Throws<ShaderInterfaceContractException>(() =>
                ShaderBundleCompiler.CompileVertexFragment(vert, frag, "ChunkInstanced", null, "ChunkInstanced.vert.glsl", "ChunkInstanced.frag.glsl"));

            Assert.Equal("ChunkInstanced", ex.ShaderName);
            var names = ex.Violations.Select(v => v.Name).ToArray();
            Assert.Contains("fragTileUV", names);
            Assert.Contains("fragTileRowParity", names);
            Assert.Contains("fragGridTile", names);
            Assert.All(ex.Violations, v => Assert.Equal(ShaderInterfaceVerifier.Rule_V5b_VaryingSets, v.Rule));
            Assert.Equal(3, ex.Violations.Count);
            // The message is the violation table - a human reads WHICH name on WHICH side without a debugger.
            Assert.Contains("fragTileUV", ex.Message);
            Assert.Contains("vertex out location 3", ex.Message);
        }

        [Fact]
        public void ArcaneSiege_ChunkInstanced_VertexLit_DeclaresAllUnread_Passes()
        {
            // The fix the contract demands: declare 3..5 even though this stage never reads them. D3 keeps the declarations through
            // optimisation (before S2 they were stripped and this pair threw exactly like the original).
            string vert = Fixture("ChunkInstanced.vert.glsl");
            string frag = Fixture("ChunkInstanced_VertexLit_DeclaresAll.frag.glsl");
            Assert.NotNull(ShaderBundleCompiler.CompileVertexFragment(vert, frag, "ChunkInstanced", null, "ChunkInstanced.vert.glsl", "ChunkInstanced.frag.glsl"));
        }

        [Fact]
        public void ArcaneSiege_ChunkInstanced_GBuffer_DeclaredButUnreadVarying_Passes()
        {
            // FINDING 2026-10-31 (S1 gate): the GBuffer fragment DECLARES all seven varyings but never READS fragVertexLightColor
            // ("unused in this mode"). Before D3 the optimiser stripped it (6 inputs) and this pair rendered correctly only because the
            // dropped varying was the LAST one (location 6) - fxc packed 0..5 identically. With D3 the declaration survives and the
            // pair is a complete, matching interface.
            string vert = Fixture("ChunkInstanced.vert.glsl");
            string frag = Fixture("ChunkInstanced_GBuffer.frag.glsl");
            Assert.NotNull(ShaderBundleCompiler.CompileVertexFragment(vert, frag, "ChunkInstanced_GBuffer", null, "ChunkInstanced.vert.glsl", "ChunkInstanced_GBuffer.frag.glsl"));
        }

        [Fact]
        public void D3_DeclaredInterfaceSurvivesOptimisation_DebugAndOptimisedAgree()
        {
            // The regression guard for D3: a fragment input that is declared and never read must be present in the OPTIMISED SPIR-V
            // exactly as in the debug (unoptimised) SPIR-V. If the SPIRV-Tools pin moves and the pass list drifts, this fails.
            const string frag = @"#version 450
layout(location = 0) in vec2 vUv;
layout(location = 1) in vec3 vColor;
layout(location = 2) flat in int vInstance;   // declared, never read
layout(location = 0) out vec4 o;
void main() { o = vec4(vColor * vUv.x, 1.0); }";
            ShaderInterface_Varying[] Inputs(bool debug)
            {
                var vs = SpirvCompilation.CompileGlslToSpirv(PairVert, "v", ShaderStages.Vertex, new GlslCompileOptions(debug));
                var fs = SpirvCompilation.CompileGlslToSpirv(frag, "f", ShaderStages.Fragment, new GlslCompileOptions(debug));
#pragma warning disable CS0618
                return SpirvCompilation.CompileVertexFragment(vs.SpirvBytes, fs.SpirvBytes, CrossCompileTarget.HLSL, new CrossCompileOptions()).Reflection.Interface.FragmentInputs;
#pragma warning restore CS0618
            }
            var debugInputs = Inputs(debug: true).OrderBy(v => v.Location).ToArray();
            var optimisedInputs = Inputs(debug: false).OrderBy(v => v.Location).ToArray();
            Assert.Equal(3, debugInputs.Length);
            Assert.Equal(debugInputs, optimisedInputs);
            var inst = optimisedInputs.Single(v => v.Name == "vInstance");
            Assert.True(inst.Flat);
            Assert.Equal(ShaderInterface_ScalarType.Int, inst.Scalar);
        }

        [Fact]
        public void Reflection_ReportsVaryingsByAuthorName_WithLocationTypeAndFlat()
        {
            string vert = Fixture("ChunkInstanced.vert.glsl");
            string frag = Fixture("ChunkInstanced_GBuffer.frag.glsl");
            var vs = SpirvCompilation.CompileGlslToSpirv(vert, "v", ShaderStages.Vertex, new GlslCompileOptions());
            var fs = SpirvCompilation.CompileGlslToSpirv(frag, "f", ShaderStages.Fragment, new GlslCompileOptions());
#pragma warning disable CS0618
            var result = SpirvCompilation.CompileVertexFragment(vs.SpirvBytes, fs.SpirvBytes, CrossCompileTarget.HLSL, new CrossCompileOptions());
#pragma warning restore CS0618
            var itf = result.Reflection.Interface;

            Assert.Equal(7, itf.VertexOutputs.Length);
            Assert.Equal(7, itf.FragmentInputs.Length);   // includes the declared-but-unread fragVertexLightColor (D3)
            var gridTile = itf.VertexOutputs.Single(v => v.Name == "fragGridTile");
            Assert.Equal(5u, gridTile.Location);
            Assert.True(gridTile.Flat);
            Assert.Equal(ShaderInterface_ScalarType.Float, gridTile.Scalar);
            Assert.Equal(2u, gridTile.VecSize);
            var light = itf.FragmentInputs.Single(v => v.Name == "fragVertexLightColor");
            Assert.Equal(6u, light.Location);
            Assert.Equal(3u, light.VecSize);
            Assert.False(light.Flat);

            // Uniform blocks with std140 placement, by name. ChunkOriginBuffer is 12 floats = 48 bytes; PixelsPerTile is member 5 @ 20.
            var origin = itf.UniformBlocks.Single(b => b.BlockName == "ChunkOriginBuffer");
            Assert.Equal(1u, origin.Set);
            Assert.Equal(0u, origin.Binding);
            Assert.Equal(48u, origin.SizeBytes);
            var ppt = origin.Members.Single(m => m.Name == "PixelsPerTile");
            Assert.Equal(20u, ppt.OffsetBytes);
            Assert.Equal(4u, ppt.SizeBytes);
            Assert.Equal("float", ppt.GlslType);
            var plane = itf.UniformBlocks.Single(b => b.BlockName == "TilePlaneParams");
            Assert.Equal(ShaderStages.Fragment, plane.Stages);
            Assert.Equal(48u, plane.SizeBytes);
            Assert.Equal("vec4", plane.Members[0].GlslType);
        }

        private const string PairVert = @"#version 450
layout(location = 0) in vec2 Position;
layout(location = 0) out vec2 vUv;
layout(location = 1) out vec3 vColor;
void main() { vUv = Position; vColor = vec3(Position, 1.0); gl_Position = vec4(Position, 0, 1); }";

        [Fact]
        public void MatchedPair_Passes()
        {
            const string frag = @"#version 450
layout(location = 0) in vec2 vUv;
layout(location = 1) in vec3 vColor;
layout(location = 0) out vec4 o;
void main() { o = vec4(vColor * vUv.x, 1.0); }";
            Assert.NotNull(ShaderBundleCompiler.CompileVertexFragment(PairVert, frag, "Matched"));
        }

        [Fact]
        public void SameNamesSwappedLocations_Throws()
        {
            const string frag = @"#version 450
layout(location = 1) in vec2 vUv;
layout(location = 0) in vec3 vColor;
layout(location = 0) out vec4 o;
void main() { o = vec4(vColor * vUv.x, 1.0); }";
            var ex = Assert.Throws<ShaderInterfaceContractException>(() => ShaderBundleCompiler.CompileVertexFragment(PairVert, frag, "Swapped"));
            Assert.Equal(2, ex.Violations.Count);
            Assert.All(ex.Violations, v => Assert.Contains("different slot", v.Detail));
        }

        [Fact]
        public void FlatMismatch_Throws()
        {
            const string frag = @"#version 450
layout(location = 0) in vec2 vUv;
layout(location = 1) flat in vec3 vColor;
layout(location = 0) out vec4 o;
void main() { o = vec4(vColor * vUv.x, 1.0); }";
            var ex = Assert.Throws<ShaderInterfaceContractException>(() => ShaderBundleCompiler.CompileVertexFragment(PairVert, frag, "Flat"));
            Assert.Single(ex.Violations);
            Assert.Equal("vColor", ex.Violations[0].Name);
        }

        [Fact]
        public void FragmentReadsVaryingVertexNeverWrites_Throws()
        {
            const string frag = @"#version 450
layout(location = 0) in vec2 vUv;
layout(location = 1) in vec3 vColor;
layout(location = 2) in float vExtra;
layout(location = 0) out vec4 o;
void main() { o = vec4(vColor * vUv.x, vExtra); }";
            var ex = Assert.Throws<ShaderInterfaceContractException>(() => ShaderBundleCompiler.CompileVertexFragment(PairVert, frag, "Extra"));
            Assert.Single(ex.Violations);
            Assert.Equal("vExtra", ex.Violations[0].Name);
            Assert.Contains("vertex shader does not write", ex.Violations[0].Detail);
        }
    }
}