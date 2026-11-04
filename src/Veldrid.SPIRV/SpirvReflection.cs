using System;

namespace Veldrid.SPIRV
{
    /// <summary>
    /// Contains information about the vertex attributes and resource types, and their binding slots, for a compiled
    /// set of shaders. This information can be used to construct <see cref="ResourceLayout"/> and
    /// <see cref="Pipeline"/> objects.
    /// </summary>
    public class SpirvReflection
    {
        /// <summary>
        /// An array containing a description of each vertex element that is used by the compiled shader set.
        /// This array will be empty for compute shaders.
        /// </summary>
        public VertexElementDescription[] VertexElements { get; }

        /// <summary>
        /// An array containing a description of each set of resources used by the compiled shader set.
        /// This is the authoritative data for creating ResourceLayout objects that match the compiled shader.
        /// </summary>
        public ResourceLayoutDescription[] ResourceLayouts { get; }

        /// <summary>
        /// An array containing the flat binding map entries produced during cross-compilation.
        /// Each entry maps a (set, binding) pair to the flat register/argument index assigned by the cross-compiler.
        /// Used for validation when loading precompiled shaders on backends that flatten bindings (D3D11, Metal).
        /// This array will be empty for GLSL/ESSL targets (which don't perform flattening).
        /// </summary>
        public BindingMapEntry[] BindingMap { get; }

        /// <summary>
        /// The compiled stage interface by NAME: vertex outputs, fragment inputs, uniform blocks with std140 members (ABI 2).
        /// What <see cref="ShaderInterfaceVerifier"/> checks and the .vdshader bundle carries. Never null (<see cref="ShaderBundleInterface.Empty"/>).
        /// </summary>
        public ShaderBundleInterface Interface { get; }

        /// <summary>
        /// V11 (AN_Monsters spec 204 T1): the HLSL constant-buffer register (<c>b#</c>) of SPIRV-Cross's <c>cbuffer SPIRV_Cross_VertexInfo
        /// { int SPIRV_Cross_BaseVertex; int SPIRV_Cross_BaseInstance; }</c>, emitted into the VERTEX stage of the Direct3D11 target when the
        /// shader reads <c>gl_VertexIndex</c> or <c>gl_InstanceIndex</c>. D3D11's <c>SV_VertexID</c> / <c>SV_InstanceID</c> EXCLUDE the draw's
        /// base vertex / base instance (<c>vertexOffset</c>, <c>instanceStart</c>, indirect <c>FirstInstance</c>) while SPIR-V's
        /// <c>VertexIndex</c> / <c>InstanceIndex</c> INCLUDE them; the HOST (Veldrid's D3D11 backend) must write the two bases into this
        /// cbuffer before every draw whose bases are non-zero. The register is the next free <c>b#</c> after the shader's own uniform
        /// buffers, chosen by the cross-compiler and recorded here (V9: the compiler assigns, the host binds exactly that).
        /// Null = not emitted (the shader reads neither builtin, or this is not the HLSL target / a compute shader).
        /// </summary>
        public uint? HlslVertexInfoRegister { get; }

        /// <summary>Human description of <see cref="HlslVertexInfoRegister"/> for logs, snapshots and violation tables.</summary>
        public const string HlslVertexInfoDescription =
            "SPIRV_Cross_VertexInfo cbuffer: base vertex + base instance for the HLSL vertex stage (D3D11 SV_VertexID/SV_InstanceID exclude them); the host fills it before every draw whose bases are non-zero; register assigned by the cross-compiler as the next free b#";

        /// <summary>
        /// Constructs a new <see cref="SpirvReflection"/> instance.
        /// </summary>
        /// <param name="vertexElements">An array containing a description of each vertex element that is used by
        /// the compiled shader set.</param>
        /// <param name="resourceLayouts">An array containing a description of each set of resources used by the
        /// compiled shader set.</param>
        /// <param name="bindingMap">An array containing the flat binding map entries from cross-compilation.</param>
        /// <param name="shaderInterface">The compiled stage interface (varyings, uniform blocks); null = empty.</param>
        /// <param name="hlslVertexInfoRegister">See <see cref="HlslVertexInfoRegister"/>.</param>
        public SpirvReflection(
            VertexElementDescription[] vertexElements,
            ResourceLayoutDescription[] resourceLayouts,
            BindingMapEntry[] bindingMap = null,
            ShaderBundleInterface shaderInterface = null,
            uint? hlslVertexInfoRegister = null)
        {
            VertexElements = vertexElements;
            ResourceLayouts = resourceLayouts;
            BindingMap = bindingMap ?? Array.Empty<BindingMapEntry>();
            Interface = shaderInterface ?? ShaderBundleInterface.Empty;
            HlslVertexInfoRegister = hlslVertexInfoRegister;
        }
    }
}