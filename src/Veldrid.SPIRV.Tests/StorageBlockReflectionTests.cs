using System.Linq;
using Xunit;

namespace Veldrid.SPIRV.Tests
{
    /// <summary>
    /// V10 (2026-11-04): storage (`buffer`) blocks are reflected like uniform blocks - std430 placement, read-only flag, the fixed-member size and
    /// the trailing runtime array (element type, ARRAY STRIDE, element members) - so Veldrid can verify a StorageBufferSlot&lt;TElement&gt; by NAME
    /// (rule V7e) instead of refusing every SSBO. Native ABI 3. The declared-interface parser reads `buffer` blocks and `T Name[];` too.
    /// </summary>
    public class StorageBlockReflectionTests
    {
        private const string Vert = @"#version 450
layout(set = 0, binding = 0) uniform Params { vec4 Rect; };
void main() { gl_Position = vec4(Rect.xy, 0.5, 1.0); }
";

        // The ViewCards P0.a spike counter: a block that is ONLY a runtime array of uint.
        private const string FragCounters = @"#version 450
layout(set = 0, binding = 0) uniform Params { vec4 Rect; };
layout(std430, set = 0, binding = 1) buffer SpikeCounters { uint Counts[]; };
layout(location = 0) out vec4 o0;
void main() { atomicAdd(Counts[1], 1u); o0 = Rect; }
";

        // A struct element (the ViewCards instance SSBO shape), read-only.
        private const string FragInstances = @"#version 450
struct InstanceData { vec3 Position; float Yaw; float Scale; float Pad0; float Pad1; float Pad2; };
layout(set = 0, binding = 0) uniform Params { vec4 Rect; };
layout(std430, set = 0, binding = 1) readonly buffer Instances { InstanceData Items[]; };
layout(location = 0) out vec4 o0;
void main() { o0 = vec4(Items[2].Position, Items[2].Scale); }
";

        // No runtime array: the whole block is the struct (indirect-args style).
        private const string FragFixed = @"#version 450
layout(set = 0, binding = 0) uniform Params { vec4 Rect; };
layout(std430, set = 0, binding = 1) buffer DrawArgs { uint IndexCount; uint InstanceCount; uint FirstIndex; int VertexOffset; uint FirstInstance; };
layout(location = 0) out vec4 o0;
void main() { atomicAdd(InstanceCount, 1u); o0 = Rect; }
";

        private static VeldridShaderBundle Compile(string frag, string name) => ShaderBundleCompiler.CompileVertexFragment(Vert, frag, name);

        [Fact]
        public void RuntimeArrayOfUint_IsReflected_WithStride4()
        {
            var b = Compile(FragCounters, "StorageCounters");
            var s = Assert.Single(b.Interface.StorageBlocks);
            Assert.Equal("SpikeCounters", s.Name);
            Assert.Equal(0u, s.Set); Assert.Equal(1u, s.Binding);
            Assert.Equal(ShaderStages.Fragment, s.Stages);
            Assert.False(s.ReadOnly);
            Assert.True(s.HasRuntimeArray);
            Assert.Equal("Counts", s.RuntimeArrayName);
            Assert.Equal("uint", s.RuntimeArrayElementType);
            Assert.Equal(4u, s.RuntimeArrayStrideBytes);
            Assert.Equal(0u, s.FixedSizeBytes);
            Assert.Empty(s.Members);
            Assert.Empty(s.RuntimeArrayElementMembers);
            // The resource kind the layout carries agrees with the block.
            Assert.Equal(ResourceKind.StructuredBufferReadWrite, b.GetResourceLayouts()[0].Elements[1].Kind);
        }

        [Fact]
        public void RuntimeArrayOfStruct_IsReflected_WithElementMembers_AndReadOnly()
        {
            var b = Compile(FragInstances, "StorageInstances");
            var s = Assert.Single(b.Interface.StorageBlocks);
            Assert.Equal("Instances", s.Name);
            Assert.True(s.ReadOnly);
            Assert.True(s.HasRuntimeArray);
            Assert.Equal("Items", s.RuntimeArrayName);
            Assert.Equal("InstanceData", s.RuntimeArrayElementType);
            // std430: vec3 (16-aligned) + 5 floats = 32 bytes, no trailing vec4 rounding beyond the 16 alignment.
            Assert.Equal(32u, s.RuntimeArrayStrideBytes);
            var members = s.RuntimeArrayElementMembers.Select(m => (m.Name, m.OffsetBytes, m.SizeBytes)).ToArray();
            Assert.Equal(new[] { ("Position", 0u, 12u), ("Yaw", 12u, 4u), ("Scale", 16u, 4u), ("Pad0", 20u, 4u), ("Pad1", 24u, 4u), ("Pad2", 28u, 4u) }, members);
            Assert.Equal(ResourceKind.StructuredBufferReadOnly, b.GetResourceLayouts()[0].Elements[1].Kind);
        }

        [Fact]
        public void FixedBlock_IsReflected_AsWholeBlockMembers()
        {
            var b = Compile(FragFixed, "StorageFixed");
            var s = Assert.Single(b.Interface.StorageBlocks);
            Assert.Equal("DrawArgs", s.Name);
            Assert.False(s.HasRuntimeArray);
            Assert.Equal(20u, s.FixedSizeBytes);
            Assert.Equal(new[] { "IndexCount", "InstanceCount", "FirstIndex", "VertexOffset", "FirstInstance" }, s.Members.Select(m => m.Name).ToArray());
            Assert.Equal(new uint[] { 0, 4, 8, 12, 16 }, s.Members.Select(m => m.OffsetBytes).ToArray());
        }

        [Fact]
        public void DeclaredParser_ReadsBufferBlocks_AndRuntimeArrays()
        {
            var d = GlslInterfaceDeclarations.Parse(FragInstances);
            Assert.Empty(d.UnparseableLayoutLines);
            var s = Assert.Single(d.StorageBlocks);
            Assert.Equal("Instances", s.BlockName);
            Assert.True(s.ReadOnly);
            Assert.Equal(0u, s.Set); Assert.Equal(1u, s.Binding);
            var m = Assert.Single(s.Members);
            Assert.Equal("Items", m.Name); Assert.Equal("InstanceData", m.GlslType); Assert.True(m.IsRuntimeArray);
            // A uniform block is still a uniform block (not re-classified).
            Assert.Single(d.UniformBlocks);
        }

        [Fact]
        public void SerialisedBundle_RoundTripsStorageBlocks()
        {
            var b = Compile(FragInstances, "StorageInstances");
            var back = VeldridShaderBundle.Deserialize(b.Serialize());
            var s = Assert.Single(back.Interface.StorageBlocks);
            Assert.Equal(32u, s.RuntimeArrayStrideBytes);
            Assert.Equal(6, s.RuntimeArrayElementMembers.Length);
        }
    }
}