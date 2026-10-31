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
        /// Constructs a new <see cref="SpirvReflection"/> instance.
        /// </summary>
        /// <param name="vertexElements">An array containing a description of each vertex element that is used by
        /// the compiled shader set.</param>
        /// <param name="resourceLayouts">An array containing a description of each set of resources used by the
        /// compiled shader set.</param>
        /// <param name="bindingMap">An array containing the flat binding map entries from cross-compilation.</param>
        /// <param name="shaderInterface">The compiled stage interface (varyings, uniform blocks); null = empty.</param>
        public SpirvReflection(
            VertexElementDescription[] vertexElements,
            ResourceLayoutDescription[] resourceLayouts,
            BindingMapEntry[] bindingMap = null,
            ShaderBundleInterface shaderInterface = null)
        {
            VertexElements = vertexElements;
            ResourceLayouts = resourceLayouts;
            BindingMap = bindingMap ?? Array.Empty<BindingMapEntry>();
            Interface = shaderInterface ?? ShaderBundleInterface.Empty;
        }
    }
}