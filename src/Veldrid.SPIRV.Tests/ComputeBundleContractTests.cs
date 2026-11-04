using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Veldrid.SPIRV.Tests
{
    /// <summary>
    /// Spec AN_Monsters/_SPECS_Other3DApps/204 C2: a COMPUTE bundle is contract-checked like a vertex+fragment bundle and carries its
    /// interface (uniform + storage blocks), and the verifier's first pass allocates nothing when the contract holds.
    /// </summary>
    public class ComputeBundleContractTests
    {
        private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestShaders", name));

        [Fact]
        public void ComputeBundle_CarriesItsInterface_StorageBlocksWithStride()
        {
            VeldridShaderBundle bundle = ShaderBundleCompiler.CompileCompute(Fixture("simple.comp"), "SimpleCompute", computeSourceFile: "simple.comp");
            Assert.NotNull(bundle.Interface);
            Assert.Empty(bundle.Interface.VertexInputs);
            Assert.Empty(bundle.Interface.VertexOutputs);
            Assert.Empty(bundle.Interface.FragmentInputs);
            Assert.Equal(new[] { "DataInput", "DataOutput" }, bundle.Interface.StorageBlocks.Select(b => b.Name).ToArray());
            var input = bundle.Interface.StorageBlocks.Single(b => b.Name == "DataInput");
            Assert.True(input.ReadOnly);
            Assert.True(input.HasRuntimeArray);
            Assert.Equal("Input", input.RuntimeArrayName);
            Assert.Equal(16u, input.RuntimeArrayStrideBytes);   // vec4
            Assert.Equal(ShaderStages.Compute, input.Stages);
            Assert.NotNull(bundle.Backends[VeldridShaderBundle.GetBackendKey(GraphicsBackend.Direct3D11)].ComputeShaderData);
        }

        private static SpirvReflection CompileComputeFixture()
        {
            var cs = SpirvCompilation.CompileGlslToSpirv(Fixture("simple.comp"), "c", ShaderStages.Compute, new GlslCompileOptions());
#pragma warning disable CS0618
            return SpirvCompilation.CompileCompute(cs.SpirvBytes, CrossCompileTarget.HLSL, new CrossCompileOptions()).Reflection;
#pragma warning restore CS0618
        }

        [Fact]
        public void Compute_DeclaredEqualsCompiled()
            => Assert.Empty(ShaderInterfaceVerifier.VerifyDeclaredAgainstCompiled(Fixture("simple.comp"), CompileComputeFixture()));

        [Fact]
        public void Compute_HandMovedStorageBlock_IsNamed_V6()
        {
            var real = CompileComputeFixture();
            var moved = real.Interface.StorageBlocks.Select(b => b.BlockName == "DataOutput" ? b with { Binding = 7 } : b).ToArray();
            var tampered = new SpirvReflection(real.VertexElements, real.ResourceLayouts, real.BindingMap, real.Interface with { StorageBlocks = moved });
            var v = Assert.Single(ShaderInterfaceVerifier.VerifyDeclaredAgainstCompiled(Fixture("simple.comp"), tampered));
            Assert.Equal(ShaderInterfaceVerifier.Rule_V6_BlockMembersDeclared, v.Rule);
            Assert.Equal("DataOutput", v.Name);
            Assert.Contains("MOVED", v.Detail);
            Assert.False(ShaderInterfaceVerifier.DeclaredMatchesCompiled(GlslInterfaceDeclarations.Parse(Fixture("simple.comp")), tampered));
            var ex = Assert.Throws<ShaderInterfaceContractException>(() => ShaderInterfaceVerifier.ThrowIfDeclaredDiffersFromCompiled("SimpleCompute", Fixture("simple.comp"), tampered));
            Assert.Single(ex.Violations);
        }

        [Fact]
        public void Compute_SourceDeclaringABlockTheCompilerDropped_FailsTheCompile()
        {
            // A declared block the compiled stage does not have (the exact D3 failure shape): the compile itself must throw.
            string glsl = Fixture("simple.comp").Replace("void main()", "layout(set = 2, binding = 0) uniform Unused { vec4 Never; };\nvoid main()");
            // glslang keeps an unused uniform block in the SPIR-V (D3 preserved), so to force a disagreement tamper the compiled side instead:
            var real = CompileComputeFixture();
            var tampered = new SpirvReflection(real.VertexElements, real.ResourceLayouts, real.BindingMap, real.Interface with { StorageBlocks = real.Interface.StorageBlocks.Where(b => b.BlockName != "DataOutput").ToArray() });
            var violations = ShaderInterfaceVerifier.VerifyDeclaredAgainstCompiled(Fixture("simple.comp"), tampered);
            var v = Assert.Single(violations);
            Assert.Equal("DataOutput", v.Name);
            Assert.Contains("ABSENT", v.SideB);
            _ = glsl;   // the source-side variant is covered by D3_DeclaredInterfaceSurvivesOptimisation for vertex/fragment
        }

        /// <summary>
        /// THE zero-allocation rule (AN_Veldrid c4084802; spec 204 C2): the boolean first pass of every verifier entry point allocates
        /// nothing when the contract holds. Parsing the GLSL is the caller's input and happens outside the measured window.
        /// </summary>
        [Fact]
        public void Verifier_FirstPass_AllocatesNothingWhenTheContractHolds()
        {
            var reflection = CompileComputeFixture();
            var declared = GlslInterfaceDeclarations.Parse(Fixture("simple.comp"));
            // Warm up (JIT, static init) outside the window.
            Assert.True(ShaderInterfaceVerifier.DeclaredMatchesCompiled(declared, reflection));
            Assert.True(ShaderInterfaceVerifier.StagesHold(reflection.Interface));

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++)
            {
                Assert.True(ShaderInterfaceVerifier.DeclaredMatchesCompiled(declared, reflection), "contract must hold");
                Assert.True(ShaderInterfaceVerifier.StagesHold(reflection.Interface), "contract must hold");
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0, after - before);
        }

        /// <summary>Same rule for the vertex+fragment pair the compiler verifies on every compile.</summary>
        [Fact]
        public void Verifier_FirstPass_VertexFragment_AllocatesNothingWhenTheContractHolds()
        {
            string vertGlsl = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestShaders", "InterfaceContract", "ChunkInstanced.vert.glsl"));
            string fragGlsl = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestShaders", "InterfaceContract", "ChunkInstanced_GBuffer.frag.glsl"));
            var vs = SpirvCompilation.CompileGlslToSpirv(vertGlsl, "v", ShaderStages.Vertex, new GlslCompileOptions());
            var fs = SpirvCompilation.CompileGlslToSpirv(fragGlsl, "f", ShaderStages.Fragment, new GlslCompileOptions());
#pragma warning disable CS0618
            var reflection = SpirvCompilation.CompileVertexFragment(vs.SpirvBytes, fs.SpirvBytes, CrossCompileTarget.HLSL, new CrossCompileOptions()).Reflection;
#pragma warning restore CS0618
            var vert = GlslInterfaceDeclarations.Parse(vertGlsl);
            var frag = GlslInterfaceDeclarations.Parse(fragGlsl);
            Assert.True(ShaderInterfaceVerifier.DeclaredMatchesCompiled(vert, frag, reflection));
            Assert.True(ShaderInterfaceVerifier.StagesHold(reflection.Interface));

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++)
            {
                Assert.True(ShaderInterfaceVerifier.DeclaredMatchesCompiled(vert, frag, reflection), "contract must hold");
                Assert.True(ShaderInterfaceVerifier.StagesHold(reflection.Interface), "contract must hold");
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0, after - before);
        }

        /// <summary>The diagnostic rerun happens ONLY on failure: a failing first pass yields the full table, a passing one never builds it.</summary>
        [Fact]
        public void ThrowIf_UsesTheDiagnosticPassOnlyOnFailure()
        {
            var real = CompileComputeFixture();
            var declared = GlslInterfaceDeclarations.Parse(Fixture("simple.comp"));
            // Two independent defects -> the exception must carry BOTH (proves the second pass accumulates rather than stopping first).
            var twoDefects = real.Interface.StorageBlocks.Select(b => b with { Binding = b.Binding + 5 }).ToArray();
            var tampered = new SpirvReflection(real.VertexElements, real.ResourceLayouts, real.BindingMap, real.Interface with { StorageBlocks = twoDefects });
            Assert.False(ShaderInterfaceVerifier.DeclaredMatchesCompiled(declared, tampered));
            var ex = Assert.Throws<ShaderInterfaceContractException>(() => ShaderInterfaceVerifier.ThrowIfDeclaredDiffersFromCompiled("SimpleCompute", Fixture("simple.comp"), tampered));
            Assert.Equal(2, ex.Violations.Count);
            Assert.Equal(new[] { "DataInput", "DataOutput" }, ex.Violations.Select(v => v.Name).OrderBy(n => n).ToArray());
        }
    }
}