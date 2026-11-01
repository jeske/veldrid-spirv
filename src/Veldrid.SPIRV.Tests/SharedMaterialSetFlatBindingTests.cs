using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Veldrid.SPIRV.Tests
{
    /// <summary>
    /// THE recurring binding bug, reproduced at its source and checked at its guard (2026-11-01, user):
    /// one ResourceSet (a mesh MATERIAL set: 4 textures + sampler + block at set 1) is bound to pipelines compiled from DIFFERENT
    /// bundles. On D3D11 / Metal every bundle carries its own FLAT binding map (absolute register per (set, binding)). A fragment
    /// stage that reads only EmissiveTexture gets it flattened to t0 if that is the only texture it DECLARES - while the shared set,
    /// built from the full-material bundle, binds Emissive at t3. Nothing on the GPU complains; the visuals are simply wrong.
    /// <para>(a) EXISTENCE in compiled output: does the cross-compiler renumber? For a stage that declares the full set and reads part
    /// of it, it must NOT (contract rule D3: declared interface preserved) - identical declarations => identical flat maps. For a stage
    /// that declares only what it reads, it DOES renumber (t0) - that is the condition the policy "every sharer declares the whole
    /// set" exists to avoid, and this test pins what it looks like.</para>
    /// <para>(b) DETECTION: Veldrid V7d (<see cref="ShaderContractVerifier.VerifySharedSetBinding"/>) refuses the mismatched pair
    /// naming the binding, and accepts the identical pair.</para>
    /// </summary>
    public class SharedMaterialSetFlatBindingTests
    {
        private const string Vert = @"#version 450
layout(set = 0, binding = 0) uniform Transforms { mat4 MVP; };
layout(location = 0) in vec3 Position;
layout(location = 1) in vec2 TexCoord;
layout(location = 0) out vec2 frag_TexCoord;
void main() { gl_Position = MVP * vec4(Position, 1.0); frag_TexCoord = TexCoord; }
";

        // The full material set as GBuffer.frag declares it.
        private const string MaterialSetDeclarations = @"
layout(set = 1, binding = 0) uniform texture2D AlbedoTexture;
layout(set = 1, binding = 1) uniform texture2D NormalTexture;
layout(set = 1, binding = 2) uniform texture2D SpecularTexture;
layout(set = 1, binding = 3) uniform texture2D EmissiveTexture;
layout(set = 1, binding = 4) uniform sampler MaterialSampler;
layout(set = 1, binding = 5) uniform MaterialParams { float EmissiveStrength; int HasEmissiveMap; float _pad1; float _pad2; };
";

        private const string FragFull = "#version 450\n" + MaterialSetDeclarations + @"
layout(location = 0) in vec2 frag_TexCoord;
layout(location = 0) out vec4 o;
void main() {
    vec4 a = texture(sampler2D(AlbedoTexture, MaterialSampler), frag_TexCoord);
    vec3 e = HasEmissiveMap == 1 ? texture(sampler2D(EmissiveTexture, MaterialSampler), frag_TexCoord).rgb * EmissiveStrength : vec3(0.0);
    o = vec4(a.rgb + e, a.a);
}
";

        // Policy (a): declares the WHOLE set, reads only Emissive (+ sampler). Bindings 0-2 and 5 are declared and dead.
        private const string FragDeclaresAll_ReadsEmissiveOnly = "#version 450\n" + MaterialSetDeclarations + @"
layout(location = 0) in vec2 frag_TexCoord;
layout(location = 0) out vec4 o;
void main() { o = texture(sampler2D(EmissiveTexture, MaterialSampler), frag_TexCoord); }
";

        // The old ShadowDepth style: declares ONLY what it reads. Emissive is the first texture this stage declares.
        private const string FragUndeclared_ReadsEmissiveOnly = @"#version 450
layout(set = 1, binding = 3) uniform texture2D EmissiveTexture;
layout(set = 1, binding = 4) uniform sampler MaterialSampler;
layout(location = 0) in vec2 frag_TexCoord;
layout(location = 0) out vec4 o;
void main() { o = texture(sampler2D(EmissiveTexture, MaterialSampler), frag_TexCoord); }
";

        private static VeldridShaderBundle Compile(string frag, string name) => ShaderBundleCompiler.CompileVertexFragment(Vert, frag, name);

        private static uint FlatOf(VeldridShaderBundle b, uint set, uint binding)
            => b.FlatBindingMap.Single(e => e.Set == set && e.Binding == binding).FlatIndex;

        private static IReadOnlyList<VdShaderBindingEntry> Set1(VeldridShaderBundle b) => b.FlatBindingMap.Where(e => e.Set == 1).OrderBy(e => e.Binding).ToList();

        // ───────────────────────── (a) existence in compiled output ─────────────────────────

        [Fact]
        public void DeclaringTheWholeSet_ButReadingOnlyEmissive_KeepsEveryBindingAndEveryFlatIndex_D3()
        {
            var full = Compile(FragFull, "MatFull");
            var partialReader = Compile(FragDeclaresAll_ReadsEmissiveOnly, "MatDeclaresAllReadsEmissive");

            // Every declared binding survives compilation in both bundles (declared interface preserved).
            Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, Set1(full).Select(e => e.Binding).ToArray());
            Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, Set1(partialReader).Select(e => e.Binding).ToArray());

            // And the flat registers are identical binding-for-binding: Emissive is t3 in BOTH, even though the partial reader never
            // touches t0..t2. If this assertion ever fails, the cross-compiler renumbered a declared-but-unread resource - fix the
            // compiler (or its binding-map emission), never the policy.
            Assert.Equal(3u, FlatOf(full, 1, 3));
            Assert.Equal(3u, FlatOf(partialReader, 1, 3));
            Assert.Equal(Set1(full).Select(e => (e.Binding, e.Kind, e.FlatIndex)), Set1(partialReader).Select(e => (e.Binding, e.Kind, e.FlatIndex)));

            // The compiled set-1 layouts are identical too (what Vulkan compares).
            var fullLayout = full.GetResourceLayouts()[1].Elements.Select(e => (e.Name, e.Kind, e.Stages)).ToArray();
            var partialLayout = partialReader.GetResourceLayouts()[1].Elements.Select(e => (e.Name, e.Kind, e.Stages)).ToArray();
            Assert.Equal(fullLayout, partialLayout);
        }

        [Fact]
        public void DeclaringOnlyWhatItReads_RenumbersEmissiveToTheFirstTextureRegister_TheBugCondition()
        {
            var full = Compile(FragFull, "MatFull");
            var undeclared = Compile(FragUndeclared_ReadsEmissiveOnly, "MatUndeclaredReadsEmissive");

            // Same GLSL binding (set 1, binding 3) - DIFFERENT absolute register: the full bundle puts Emissive at t3, the
            // declares-only-what-it-reads bundle at t0. A set built from the full bundle binds Emissive where this shader never
            // reads. This is the mapping drift the whole shape/bundle contract exists to catch.
            Assert.Equal(3u, FlatOf(full, 1, 3));
            Assert.Equal(0u, FlatOf(undeclared, 1, 3));
            Assert.Equal(new uint[] { 3, 4 }, Set1(undeclared).Select(e => e.Binding).ToArray());
        }

        // ───────────────────────── (b) Veldrid V7d: identical DECLARATIONS decide; registers are the pipeline's business ─────────────────────────

        [Fact]
        public void V7d_AcceptsTheSharedSet_WhenBothBundlesDeclareTheWholeSet()
        {
            var full = Compile(FragFull, "MatFull");
            var partialReader = Compile(FragDeclaresAll_ReadsEmissiveOnly, "MatDeclaresAllReadsEmissive");
            var violations = new List<Veldrid.ShaderContractViolation>();
            ShaderContractVerifier.VerifySharedSetBinding(1, partialReader.GetResourceLayouts()[1], full.GetResourceLayouts()[1], violations);
            Assert.Empty(violations);
        }

        [Fact]
        public void V7d_RefusesTheSharedSet_WhenThePipelineBundleDeclaredOnlyWhatItReads()
        {
            var full = Compile(FragFull, "MatFull");
            var undeclared = Compile(FragUndeclared_ReadsEmissiveOnly, "MatUndeclaredReadsEmissive");
            var violations = new List<Veldrid.ShaderContractViolation>();
            ShaderContractVerifier.VerifySharedSetBinding(1, undeclared.GetResourceLayouts()[1], full.GetResourceLayouts()[1], violations);
            Assert.NotEmpty(violations);
            Assert.All(violations, v => Assert.Equal(ShaderContractVerifier.Rule_V7d_SharedSetBinding, v.Rule));
            // The under-declared shader's set 1 is not the material set (bindings 0-2 / 5 absent) - a DECLARATION mismatch, by set/binding.
            // Where its Emissive register ended up (t0) is irrelevant to the rule: the pipeline-side bind translates registers per shader
            // (SharedSetCrossPipelineBindingGpuTests).
            Assert.Contains(violations, v => v.Name.StartsWith("set 1"));
        }
    }
}