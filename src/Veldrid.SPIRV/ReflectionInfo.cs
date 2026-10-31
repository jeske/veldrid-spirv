using System.Runtime.InteropServices;

namespace Veldrid.SPIRV
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ReflectionInfo
    {
        public InteropArray VertexElements; // InteropArray<NativeVertexElementDescription>
        public InteropArray ResourceLayouts; // InteropArray<NativeResourceLayoutDescription>
        public InteropArray BindingMap; // InteropArray<NativeBindingMapEntry>
        // ABI 2 (BUGFIX_ShaderInterfaceContract_NameVerified): the stage interface, mirrored from InteropStructs.hpp ReflectionInfo.
        public InteropArray VertexOutputs; // InteropArray<NativeVaryingDescription>
        public InteropArray FragmentInputs; // InteropArray<NativeVaryingDescription>
        public InteropArray UniformBlocks; // InteropArray<NativeUniformBlockDescription>
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct NativeVertexElementDescription
    {
        public InteropArray Name; // InteropArray<byte>
        public VertexElementSemantic Semantic;
        public VertexElementFormat Format;
        public uint Offset;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct NativeResourceLayoutDescription
    {
        public InteropArray ResourceElements; // InteropArray<NativeResourceElementDescription>
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct NativeResourceElementDescription
    {
        public InteropArray Name; // InteropArray<byte>
        public ResourceKind Kind;
        public ShaderStages Stages;
        public ResourceLayoutElementOptions Options;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct NativeBindingMapEntry
    {
        public uint Set;
        public uint Binding;
        public ResourceKind Kind;
        public ShaderStages Stages;
        public uint FlatIndex;
    }

    /// <summary>Mirror of native <c>VaryingDescription</c> (pack 1, ABI 2).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct NativeVaryingDescription
    {
        public InteropArray Name; // InteropArray<byte>
        public uint Location;
        public ShaderInterface_ScalarType Scalar; // uint8
        public uint VecSize;
        public uint Columns;
        public uint ArrayLength;
        public Bool32 Flat;
    }

    /// <summary>Mirror of native <c>UniformMemberDescription</c> (pack 1, ABI 2).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct NativeUniformMemberDescription
    {
        public InteropArray Name; // InteropArray<byte>
        public InteropArray GlslType; // InteropArray<byte>
        public uint OffsetBytes;
        public uint SizeBytes;
    }

    /// <summary>Mirror of native <c>UniformBlockDescription</c> (pack 1, ABI 2).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct NativeUniformBlockDescription
    {
        public InteropArray BlockName; // InteropArray<byte>
        public uint Set;
        public uint Binding;
        public uint SizeBytes;
        public ShaderStages Stages; // uint8
        public InteropArray Members; // InteropArray<NativeUniformMemberDescription>
    }
}
