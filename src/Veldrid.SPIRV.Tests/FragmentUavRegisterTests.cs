using System.Linq;
using Xunit;

namespace Veldrid.SPIRV.Tests
{
    /// <summary>
    /// V9 (2026-11-04): the cross-compiler is the ONE place that assigns HLSL registers, and for pixel-shader UAVs it must assign ones
    /// fxc accepts. On D3D11 a PS UAV register (u#) shares its namespace with the render-target outputs (o#): fxc rejects u# below the
    /// PS output count (X4509) and the DXBC validator counts a depth output as an output slot too. So fragment-stage UAVs start at
    /// <c>max(1, fragmentOutputCount)</c>. The register chosen is recorded in <see cref="VeldridShaderBundle.FlatBindingMap"/> and
    /// Veldrid's D3D11 backend binds EXACTLY that register - nothing is added at bind time (the old <c>ColorTargets.Count + slot</c>
    /// offset is gone). These tests pin the assignment; the compile itself (DXBC in the D3D11 slot) proves fxc accepted it.
    /// </summary>
    public class FragmentUavRegisterTests
    {
        private const string Vert = @"#version 450
layout(set = 0, binding = 0) uniform Params { vec4 Rect; };
void main() { gl_Position = vec4(Rect.xy, 0.5, 1.0); }
";

        // Depth-only pixel shader with a UAV counter and a conservative depth output (the ViewCards P0.a spike shape).
        private const string FragDepthOnlyWithUav = @"#version 450
layout(set = 0, binding = 0) uniform Params { vec4 Rect; };
layout(std430, set = 0, binding = 1) buffer Counters { uint Counts[]; };
layout(depth_less) out float gl_FragDepth;
void main() { atomicAdd(Counts[0], 1u); gl_FragDepth = gl_FragCoord.z - 0.1; }
";

        // Two colour outputs + a UAV: the UAV must land at u2 or above.
        private const string FragTwoOutputsWithUav = @"#version 450
layout(set = 0, binding = 0) uniform Params { vec4 Rect; };
layout(std430, set = 0, binding = 1) buffer Counters { uint Counts[]; };
layout(location = 0) out vec4 o0;
layout(location = 1) out vec4 o1;
void main() { atomicAdd(Counts[0], 1u); o0 = Rect; o1 = Rect.yxwz; }
";

        // (A storage IMAGE is a UAV too and follows the same rule, but the bundle's ESSL slot is emitted at version 300 for vertex+fragment
        //  and ESSL 3.00 has no image load/store, so that case cannot be pinned through CompileVertexFragment today - a GLSL/ESSL-target limitation.)

        private static uint FlatOf(VeldridShaderBundle b, uint set, uint binding)
            => b.FlatBindingMap.Single(e => e.Set == set && e.Binding == binding).FlatIndex;

        [Fact]
        public void DepthOnlyPixelShader_UavStartsAtU1_NotU0()
        {
            // No colour outputs but a depth output: the validator still refuses u0, so the floor is 1.
            var bundle = ShaderBundleCompiler.CompileVertexFragment(Vert, FragDepthOnlyWithUav, "FragUavDepthOnly");
            Assert.Equal(1u, FlatOf(bundle, 0, 1));
            // The uniform block is unaffected (b0).
            Assert.Equal(0u, FlatOf(bundle, 0, 0));
        }

        [Fact]
        public void TwoColourOutputs_UavStartsAtU2()
        {
            var bundle = ShaderBundleCompiler.CompileVertexFragment(Vert, FragTwoOutputsWithUav, "FragUavTwoOutputs");
            Assert.Equal(2u, FlatOf(bundle, 0, 1));
        }

        [Fact]
        public void ConservativeDepth_FragCoordInput_IsLinearNoperspectiveCentroid_SoTheValidatorAccepts()
        {
            // D3D11 rule #2 behind the UAV one: with oDepthLE/GE output, SV_Position input must be centroid or sample. GLSL cannot
            // decorate gl_FragCoord, so the HLSL backend (local SPIRV-Cross patch, branch hlsl-fragcoord-centroid-conservative-depth
            // on github.com/jeske/SPIRV-Cross) infers centroid. Check the HLSL text directly.
            var hlsl = SpirvCompilation.CompileVertexFragment(
                SpirvCompilation.CompileGlslToSpirv(Vert, "v", ShaderStages.Vertex, new GlslCompileOptions()).SpirvBytes,
                SpirvCompilation.CompileGlslToSpirv(FragDepthOnlyWithUav, "f", ShaderStages.Fragment, new GlslCompileOptions()).SpirvBytes,
                CrossCompileTarget.HLSL).FragmentShader;
            Assert.Contains("linear noperspective centroid float4 gl_FragCoord : SV_Position", hlsl);
            Assert.Contains("SV_DepthLessEqual", hlsl);
        }

        [Fact]
        public void D3D11Slot_IsDxbc_SoFxcAcceptedTheRegisters()
        {
            // The point of the rule: fxc compiles (and the DXBC validator passes) with the chosen registers. A regression here is
            // an exception from CompileVertexFragment, but pin the slot format too so a silent HLSL-text fallback cannot hide it.
            foreach (var (frag, name) in new[] { (FragDepthOnlyWithUav, "A"), (FragTwoOutputsWithUav, "B") })
            {
                var bundle = ShaderBundleCompiler.CompileVertexFragment(Vert, frag, "FragUavDxbc" + name);
                string key = VeldridShaderBundle.GetBackendKey(GraphicsBackend.Direct3D11);
                Assert.True(bundle.Backends.TryGetValue(key, out var d3d), "no D3D11 slot");
                if (System.OperatingSystem.IsWindows()) Assert.Equal("dxbc", d3d.ShaderFormat, ignoreCase: true);
            }
        }
    }
}