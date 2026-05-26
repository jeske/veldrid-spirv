namespace Veldrid.SPIRV
{
    /// <summary>
    /// Describes the mapping of a single shader resource from its GLSL descriptor set/binding
    /// to the flat register/argument index assigned during SPIR-V cross-compilation.
    /// Used for validation when loading precompiled shaders.
    /// </summary>
    public struct BindingMapEntry
    {
        /// <summary>The GLSL descriptor set number.</summary>
        public uint Set;

        /// <summary>The GLSL binding number within the descriptor set.</summary>
        public uint Binding;

        /// <summary>The resource kind (UniformBuffer, TextureReadOnly, Sampler, etc.).</summary>
        public ResourceKind Kind;

        /// <summary>Which shader stages use this resource.</summary>
        public ShaderStages Stages;

        /// <summary>
        /// The flat register/argument index assigned by the cross-compiler.
        /// On D3D11: the HLSL register index (bN, tN, sN, or uN).
        /// On Metal: the argument table index (buffer(N), texture(N), or sampler(N)).
        /// </summary>
        public uint FlatIndex;

        public BindingMapEntry(uint set, uint binding, ResourceKind kind, ShaderStages stages, uint flatIndex)
        {
            Set = set;
            Binding = binding;
            Kind = kind;
            Stages = stages;
            FlatIndex = flatIndex;
        }

        public override string ToString()
            => $"(set={Set}, binding={Binding}) {Kind} [{Stages}] → flat {FlatIndex}";
    }
}
