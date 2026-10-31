// veldrid-spirv.cpp : Defines the entry point for the console application.
//

#include "libveldrid-spirv.hpp"
#include "InteropStructs.hpp"
#include <cstdint>
#include <fstream>
#include "spirv_hlsl.hpp"
#include "spirv_glsl.hpp"
#include "spirv_msl.hpp"
#include <map>
#include <sstream>
#include "shaderc.hpp"
#include "spirv-tools/optimizer.hpp"
#include <iostream>

using namespace spirv_cross;

namespace Veldrid
{
void ReflectVertexInfo(const Compiler& compiler, const ShaderResources& resources, ReflectionInfo& info);

// ---- Shader interface reflection (BUGFIX_ShaderInterfaceContract_NameVerified) ----------------------------------------------

static InterfaceScalarType ToInterfaceScalar(const SPIRType &type)
{
    switch (type.basetype)
    {
    case SPIRType::Float: return InterfaceScalarType::Float;
    case SPIRType::Int: return InterfaceScalarType::Int;
    case SPIRType::UInt: return InterfaceScalarType::UInt;
    case SPIRType::Boolean: return InterfaceScalarType::Bool;
    case SPIRType::Double: return InterfaceScalarType::Double;
    case SPIRType::Half: return InterfaceScalarType::Half;
    default:
        throw std::runtime_error("Unhandled SPIR-V scalar type in a stage interface variable or uniform member.");
    }
}

// GLSL spelling of a type for the violation table: float, vec3, mat4, ivec2, uint, float[4], ...
static std::string GlslTypeName(const Compiler &compiler, const SPIRType &type)
{
    std::string scalar;
    switch (type.basetype)
    {
    case SPIRType::Float: scalar = "float"; break;
    case SPIRType::Int: scalar = "int"; break;
    case SPIRType::UInt: scalar = "uint"; break;
    case SPIRType::Boolean: scalar = "bool"; break;
    case SPIRType::Double: scalar = "double"; break;
    case SPIRType::Half: scalar = "float16_t"; break;
    case SPIRType::Struct: scalar = compiler.get_name(type.self); break;
    default: scalar = "?"; break;
    }
    std::string name;
    if (type.basetype == SPIRType::Struct || (type.vecsize == 1 && type.columns == 1))
    {
        name = scalar;
    }
    else
    {
        std::string prefix;
        switch (type.basetype)
        {
        case SPIRType::Int: prefix = "i"; break;
        case SPIRType::UInt: prefix = "u"; break;
        case SPIRType::Boolean: prefix = "b"; break;
        case SPIRType::Double: prefix = "d"; break;
        default: prefix = ""; break;
        }
        if (type.columns > 1)
        {
            name = prefix + "mat" + std::to_string(type.columns);
            if (type.columns != type.vecsize) name += "x" + std::to_string(type.vecsize);
        }
        else
        {
            name = prefix + "vec" + std::to_string(type.vecsize);
        }
    }
    for (size_t i = 0; i < type.array.size(); i++)
    {
        name += "[" + (type.array_size_literal[i] ? std::to_string(type.array[i]) : std::string("?")) + "]";
    }
    return name;
}

static std::string InterfaceVariableName(const Compiler &compiler, const Resource &resource)
{
    std::string name = compiler.get_name(resource.id);
    if (name.empty()) name = compiler.get_fallback_name(resource.id);
    return name;
}

// The vertex `out` / fragment `in` set of one stage, by the author's names. Built-ins are not in stage_inputs/stage_outputs.
static InteropArray<VaryingDescription> ReflectVaryings(const Compiler &compiler, const SmallVector<Resource> &variables)
{
    InteropArray<VaryingDescription> ret(static_cast<uint32_t>(variables.size()));
    for (uint32_t i = 0; i < variables.size(); i++)
    {
        const Resource &var = variables[i];
        const SPIRType &type = compiler.get_type(var.type_id);
        std::string name = InterfaceVariableName(compiler, var);
        ret[i].Name.CopyFrom(static_cast<uint32_t>(name.size()), name.c_str());
        ret[i].Location = compiler.get_decoration(var.id, spv::DecorationLocation);
        ret[i].Scalar = ToInterfaceScalar(type);
        ret[i].VecSize = type.vecsize;
        ret[i].Columns = type.columns;
        ret[i].ArrayLength = type.array.empty() ? 0u : (type.array_size_literal[0] ? type.array[0] : 0u);
        ret[i].Flat = Bool32(compiler.has_decoration(var.id, spv::DecorationFlat));
    }
    return ret;
}

// Every uniform block of one stage with the std140 placement the compiler actually laid out. One entry per stage occurrence;
// the managed verifier merges per (Set, Binding) and checks that both stages describe the same block.
static void ReflectUniformBlocks(const Compiler &compiler, const SmallVector<Resource> &uniformBuffers, ShaderStages stage,
                                 std::vector<UniformBlockDescription *> &out)
{
    for (const Resource &ub : uniformBuffers)
    {
        const SPIRType &blockType = compiler.get_type(ub.base_type_id);
        UniformBlockDescription *block = new UniformBlockDescription();
        // ub.name is the block name for an anonymous-instance block ("uniform Foo { ... };"), which is the Veldrid convention.
        std::string blockName = ub.name.empty() ? compiler.get_name(ub.base_type_id) : ub.name;
        block->BlockName.CopyFrom(static_cast<uint32_t>(blockName.size()), blockName.c_str());
        block->Set = compiler.get_decoration(ub.id, spv::DecorationDescriptorSet);
        block->Binding = compiler.get_decoration(ub.id, spv::DecorationBinding);
        // std140 BLOCK size: the declared size rounded up to the block's 16-byte base alignment. get_declared_struct_size() stops at the
        // end of the last member (mat4 + float = 68), but a uniform buffer and the C# struct bound to it are 80 - that is what V2/V7c compare.
        uint32_t declaredSize = static_cast<uint32_t>(compiler.get_declared_struct_size(blockType));
        block->SizeBytes = (declaredSize + 15u) & ~15u;
        block->Stages = stage;
        block->Members.Resize(static_cast<uint32_t>(blockType.member_types.size()));
        for (uint32_t m = 0; m < blockType.member_types.size(); m++)
        {
            std::string memberName = compiler.get_member_name(ub.base_type_id, m);
            const SPIRType &memberType = compiler.get_type(blockType.member_types[m]);
            std::string glslType = GlslTypeName(compiler, memberType);
            block->Members[m].Name.CopyFrom(static_cast<uint32_t>(memberName.size()), memberName.c_str());
            block->Members[m].GlslType.CopyFrom(static_cast<uint32_t>(glslType.size()), glslType.c_str());
            block->Members[m].OffsetBytes = compiler.type_struct_member_offset(blockType, m);
            block->Members[m].SizeBytes = static_cast<uint32_t>(compiler.get_declared_struct_member_size(blockType, m));
        }
        out.push_back(block);
    }
}

// Move a vector of heap blocks into one InteropArray (element-wise move so the nested arrays keep single ownership).
static InteropArray<UniformBlockDescription> CollectUniformBlocks(std::vector<UniformBlockDescription *> &blocks)
{
    InteropArray<UniformBlockDescription> ret(static_cast<uint32_t>(blocks.size()));
    for (uint32_t i = 0; i < blocks.size(); i++)
    {
        ret[i].BlockName = std::move(blocks[i]->BlockName);
        ret[i].Set = blocks[i]->Set;
        ret[i].Binding = blocks[i]->Binding;
        ret[i].SizeBytes = blocks[i]->SizeBytes;
        ret[i].Stages = blocks[i]->Stages;
        ret[i].Members = std::move(blocks[i]->Members);
        delete blocks[i];
    }
    blocks.clear();
    return ret;
}

struct BindingInfo
{
    uint32_t Set;
    uint32_t Binding;
};

bool operator<(const BindingInfo &a, const BindingInfo &b)
{
    return a.Set < b.Set ||
           (a.Set == b.Set && a.Binding < b.Binding);
}

struct ResourceInfo
{
    std::string Name;
    ResourceKind Kind;
    std::uint32_t IDs[2]; // 0 == VS/CS, 1 == FS
};

ResourceKind ClassifyResource(const Compiler *compiler, const Resource &resource, bool image, bool storage)
{
    SPIRType type = compiler->get_type(resource.type_id);
    uint32_t nonWritable = compiler->get_decoration(resource.id, spv::Decoration::DecorationNonWritable);
    switch (type.basetype)
    {
    case SPIRType::BaseType::Struct:
        if (storage)
        {
            auto bufferBlockFlags = compiler->get_buffer_block_flags(resource.id);
            if (bufferBlockFlags.get(spv::Decoration::DecorationNonWritable))
            {
                return StorageBufferReadOnly;
            }
            else
            {
                return StorageBufferReadWrite;
            }
        }
        else
        {
            return UniformBuffer;
        }
    case SPIRType::BaseType::Image:
        return storage ? StorageImage : SampledImage;
    case SPIRType::BaseType::Sampler:
        return ResourceKind::Sampler;
    default:
        throw std::runtime_error("Unhandled SPIR-V data type.");
    }
}

void AddResources(
    spirv_cross::SmallVector<spirv_cross::Resource> &resources,
    spirv_cross::Compiler *compiler,
    std::map<BindingInfo, ResourceInfo> &allResources,
    const uint32_t idIndex,
    bool normalizeResourceNames,
    bool image = false,
    bool storage = false)
{
    for (auto &resource : resources)
    {
        ResourceKind kind = ClassifyResource(compiler, resource, image, storage);
        BindingInfo bi;
        bi.Set = compiler->get_decoration(resource.id, spv::Decoration::DecorationDescriptorSet);
        bi.Binding = compiler->get_decoration(resource.id, spv::Decoration::DecorationBinding);

        ResourceInfo ri = {};
        if (normalizeResourceNames)
        {
            std::string name = "vdspv_" + std::to_string(bi.Set) + "_" + std::to_string(bi.Binding);
            if (kind == ResourceKind::UniformBuffer)
            {
                compiler->set_name(resource.base_type_id, name);
            }
            else
            {
                compiler->set_name(resource.id, name);
            }
            ri.Name = name;
        }
        else
        {
            ri.Name = resource.name;
            // Preserve original uniform buffer names
            // Set both type and instance names to the original name
            // SPIRV-Cross will add a suffix to the instance (e.g., ProjView_1) to avoid conflicts
            // The block name (type) remains as ProjView for glGetUniformBlockIndex()
            if (kind == ResourceKind::UniformBuffer)
            {
                compiler->set_name(resource.base_type_id, resource.name);
                compiler->set_name(resource.id, resource.name);
            }
            else
            {
                compiler->set_name(resource.id, resource.name);
            }
        }

        ri.IDs[idIndex] = resource.id;
        ri.Kind = kind;

        auto pair = allResources.insert(std::pair<BindingInfo, ResourceInfo>(bi, ri));
        if (!pair.second) // Insertion failed; element already exists.
        {
            if (pair.first->second.IDs[idIndex] != 0)
            {
                std::stringstream msg;
                msg << "The same binding slot ";
                msg << "(" << std::to_string(bi.Set) << ", " << std::to_string(bi.Binding) << ") ";
                msg << "was used by multiple distinct resources. First resource: " << pair.first->second.Name << ". Second resource: " << ri.Name;
                throw std::runtime_error(msg.str());
            }

            pair.first->second.IDs[idIndex] = resource.id;
            if (pair.first->second.Kind != kind)
            {
                std::stringstream msg;
                msg << "The same binding slot ";
                msg << "(" << std::to_string(bi.Set) << ", " << std::to_string(bi.Binding) << ") ";
                msg << "was used by multiple resources with incompatible types: ";
                msg << "\"" << pair.first->second.Kind << "\" and ";
                msg << "\"" << kind << "\".";
                throw std::runtime_error(msg.str());
            }
        }
    }
}

uint32_t GetResourceIndex(
    CrossCompileTarget target,
    ResourceKind resourceKind,
    uint32_t &bufferIndex,
    uint32_t &textureIndex,
    uint32_t &uavIndex,
    uint32_t &samplerIndex)
{
    switch (resourceKind)
    {
    case UniformBuffer:
        return bufferIndex++;
    case StorageBufferReadWrite:
        if (target == MSL)
        {
            return bufferIndex++;
        }
        else
        {
            return uavIndex++;
        }
    case StorageImage:
        if (target == MSL)
        {
            return textureIndex++;
        }
        else
        {
            return uavIndex++;
        }
    case SampledImage:
        return textureIndex++;
    case StorageBufferReadOnly:
        if (target == MSL)
        {
            return bufferIndex++;
        }
        else
        {
            return textureIndex++;
        }
    case Sampler:
        return samplerIndex++;
    default:
        throw std::runtime_error("Invalid ResourceKind.");
    }
}

Compiler *GetCompiler(std::vector<uint32_t> spirvBytes, const CrossCompileInfo &info)
{
    switch (info.Target)
    {
    case HLSL:
    {
        auto ret = new CompilerHLSL(spirvBytes);
        CompilerHLSL::Options opts = {};
        opts.shader_model = 50;
        opts.point_size_compat = true;
        ret->set_hlsl_options(opts);
        CompilerGLSL::Options commonOpts;
        commonOpts.vertex.flip_vert_y = info.InvertY;
        commonOpts.vertex.fixup_clipspace = info.FixClipSpaceZ;
        ret->set_common_options(commonOpts);
        return ret;
    }
    case GLSL:
    case ESSL:
    {
        auto ret = new CompilerGLSL(spirvBytes);
        CompilerGLSL::Options opts = {};
        opts.es = info.Target == ESSL;
        opts.enable_420pack_extension = false;
        if (info.ComputeShader.Count > 0)
        {
            opts.version = info.Target == GLSL ? 430 : 310;
        }
        else
        {
            opts.version = info.Target == GLSL ? 330 : 300;
        }
        opts.vertex.fixup_clipspace = info.FixClipSpaceZ;
        opts.vertex.flip_vert_y = info.InvertY;
        ret->set_common_options(opts);
        return ret;
    }
    case MSL:
    {
        auto ret = new CompilerMSL(spirvBytes);
        CompilerMSL::Options opts = {};
        opts.enable_decoration_binding = true;
        ret->set_msl_options(opts);
        CompilerGLSL::Options commonOpts;
        commonOpts.vertex.flip_vert_y = info.InvertY;
        commonOpts.vertex.fixup_clipspace = info.FixClipSpaceZ;
        ret->set_common_options(commonOpts);
        return ret;
    }
    default:
        throw std::runtime_error("Invalid OutputKind.");
    }
}

void SetSpecializations(spirv_cross::Compiler *compiler, const CrossCompileInfo &info)
{
    auto specConstants = compiler->get_specialization_constants();
    for (uint32_t i = 0; i < info.Specializations.Count; i++)
    {
        uint32_t constID = info.Specializations[i].ID;
        uint32_t varID = 0;

        for (auto &constant : specConstants)
        {
            if (constant.constant_id == constID)
            {
                varID = constant.id;
            }
        }

        if (varID != 0)
        {
            auto &constVar = compiler->get_constant(varID);
            constVar.m.c[0].r[0].u64 = info.Specializations[i].Constant;
        }
    }
}

InteropArray<ResourceLayoutDescription> CreateResourceLayoutArray(
    std::map<BindingInfo, ResourceInfo> resources,
    bool compute)
{
    uint32_t currentSet = 0;
    std::vector<uint32_t> setSizes(1);
    for (auto& it : resources)
    {
        uint32_t set = it.first.Set;
        if (setSizes.size() <= set)
        {
            setSizes.resize(set + 1);
        }
        setSizes[set] = std::max(setSizes[set], it.first.Binding + 1);
    }

    uint32_t setCount = setSizes.size();
    InteropArray<ResourceLayoutDescription> ret(setCount);

    for (uint32_t i = 0; i < setCount; i++)
    {
        ret[i].ResourceElements.Resize(setSizes[i]);
        for (uint32_t j = 0; j < setSizes[i]; j++)
        {
            ret[i].ResourceElements[j].Name = InteropArray<char>();
            ret[i].ResourceElements[j].Kind = ResourceKind::UniformBuffer;
            ret[i].ResourceElements[j].Stages = ShaderStages::None;
            ret[i].ResourceElements[j].Options = 2; // "Unused"
        }
    }

    for (auto& it : resources)
    {
        ShaderStages stages = ShaderStages::None;
        if (it.second.IDs[0] != 0)
        {
            if (compute) { stages = stages | ShaderStages::Compute; }
            else { stages = stages | ShaderStages::Vertex; }
        }
        if (it.second.IDs[1] != 0)
        {
            stages = stages | ShaderStages::Fragment;
        }

        ret[it.first.Set].ResourceElements[it.first.Binding].Name.CopyFrom(it.second.Name.length(), it.second.Name.c_str());
        ret[it.first.Set].ResourceElements[it.first.Binding].Kind = it.second.Kind;
        ret[it.first.Set].ResourceElements[it.first.Binding].Stages = stages;
        ret[it.first.Set].ResourceElements[it.first.Binding].Options = 0;
    }

    return ret;
}

CompilationResult *CompileVertexFragment(const CrossCompileInfo &info)
{
    std::vector<uint32_t> vsBytes(
        info.VertexShader.Data,
        info.VertexShader.Data + info.VertexShader.Count);
    Compiler *vsCompiler = GetCompiler(vsBytes, info);

    std::vector<uint32_t> fsBytes(
        info.FragmentShader.Data,
        info.FragmentShader.Data + info.FragmentShader.Count);
    Compiler *fsCompiler = GetCompiler(fsBytes, info);

    SetSpecializations(vsCompiler, info);
    SetSpecializations(fsCompiler, info);

    ShaderResources vsResources = vsCompiler->get_shader_resources();
    ShaderResources fsResources = fsCompiler->get_shader_resources();

    // Interface reflection FIRST: AddResources below renames resources (NormalizeResourceNames) and the GLSL targets rename the
    // varyings to vdspv_fsinN further down. The contract is checked against the author's names.
    InteropArray<VaryingDescription> vertexOutputs = ReflectVaryings(*vsCompiler, vsResources.stage_outputs);
    InteropArray<VaryingDescription> fragmentInputs = ReflectVaryings(*fsCompiler, fsResources.stage_inputs);
    std::vector<UniformBlockDescription *> uniformBlocks;
    ReflectUniformBlocks(*vsCompiler, vsResources.uniform_buffers, ShaderStages::Vertex, uniformBlocks);
    ReflectUniformBlocks(*fsCompiler, fsResources.uniform_buffers, ShaderStages::Fragment, uniformBlocks);

    std::map<BindingInfo, ResourceInfo> allResources;

    AddResources(vsResources.uniform_buffers, vsCompiler, allResources, 0, info.NormalizeResourceNames);
    AddResources(vsResources.storage_buffers, vsCompiler, allResources, 0, info.NormalizeResourceNames, false, true);
    AddResources(vsResources.separate_images, vsCompiler, allResources, 0, info.NormalizeResourceNames, true, false);
    AddResources(vsResources.storage_images, vsCompiler, allResources, 0, info.NormalizeResourceNames, true, true);
    AddResources(vsResources.separate_samplers, vsCompiler, allResources, 0, info.NormalizeResourceNames);

    AddResources(fsResources.uniform_buffers, fsCompiler, allResources, 1, info.NormalizeResourceNames);
    AddResources(fsResources.storage_buffers, fsCompiler, allResources, 1, info.NormalizeResourceNames, false, true);
    AddResources(fsResources.separate_images, fsCompiler, allResources, 1, info.NormalizeResourceNames, true, false);
    AddResources(fsResources.storage_images, fsCompiler, allResources, 1, info.NormalizeResourceNames, true, true);
    AddResources(fsResources.separate_samplers, fsCompiler, allResources, 1, info.NormalizeResourceNames);

    std::vector<BindingMapEntry> bindingMapEntries;

    if (info.Target == HLSL || info.Target == MSL)
    {
        uint32_t bufferIndex = 0;
        uint32_t textureIndex = 0;
        uint32_t uavIndex = 0;
        uint32_t samplerIndex = 0;
        for (auto &it : allResources)
        {
            uint32_t index = GetResourceIndex(info.Target, it.second.Kind, bufferIndex, textureIndex, uavIndex, samplerIndex);

            uint32_t vsID = it.second.IDs[0];
            if (vsID != 0)
            {
                vsCompiler->set_decoration(vsID, spv::Decoration::DecorationBinding, index);
            }
            uint32_t fsID = it.second.IDs[1];
            if (fsID != 0)
            {
                fsCompiler->set_decoration(fsID, spv::Decoration::DecorationBinding, index);
            }

            // Capture the binding map entry
            ShaderStages stages = ShaderStages::None;
            if (vsID != 0) { stages = stages | ShaderStages::Vertex; }
            if (fsID != 0) { stages = stages | ShaderStages::Fragment; }

            BindingMapEntry entry = {};
            entry.Set = it.first.Set;
            entry.Binding = it.first.Binding;
            entry.Kind = it.second.Kind;
            entry.Stages = stages;
            entry.FlatIndex = index;
            bindingMapEntries.push_back(entry);
        }
    }

    if (info.Target == GLSL || info.Target == ESSL)
    {
        vsCompiler->build_dummy_sampler_for_combined_images();
        vsCompiler->build_combined_image_samplers();
        for (auto &remap : vsCompiler->get_combined_image_samplers())
        {
            vsCompiler->set_name(remap.combined_id, vsCompiler->get_name(remap.image_id));
        }

        fsCompiler->build_dummy_sampler_for_combined_images();
        fsCompiler->build_combined_image_samplers();
        for (auto &remap : fsCompiler->get_combined_image_samplers())
        {
            fsCompiler->set_name(remap.combined_id, fsCompiler->get_name(remap.image_id));
        }

        for (auto &output : vsResources.stage_outputs)
        {
            uint32_t location = vsCompiler->get_decoration(output.id, spv::Decoration::DecorationLocation);
            std::string newName = "vdspv_fsin" + std::to_string(location);
            vsCompiler->set_name(output.id, newName);
        }

        for (auto &input : fsResources.stage_inputs)
        {
            uint32_t location = fsCompiler->get_decoration(input.id, spv::Decoration::DecorationLocation);
            std::string newName = "vdspv_fsin" + std::to_string(location);
            fsCompiler->set_name(input.id, newName);
        }
    }

    if (info.Target == ESSL)
    {
        for (auto &uniformBuffer : vsResources.uniform_buffers)
        {
            vsCompiler->unset_decoration(uniformBuffer.id, spv::Decoration::DecorationBinding);
        }

        uint32_t bufferIndex = 0;
        uint32_t imageIndex = 0;
        for (auto &it : allResources)
        {
            if (it.second.Kind == StorageBufferReadOnly || it.second.Kind == StorageBufferReadWrite)
            {
                uint32_t id = bufferIndex++;
                if (it.second.IDs[0] != 0)
                {
                    vsCompiler->set_decoration(it.second.IDs[0], spv::Decoration::DecorationBinding, id);
                }
                if (it.second.IDs[1] != 0)
                {
                    fsCompiler->set_decoration(it.second.IDs[1], spv::Decoration::DecorationBinding, id);
                }
            }
            else if (it.second.Kind == StorageImage)
            {
                uint32_t id = imageIndex++;
                if (it.second.IDs[0] != 0)
                {
                    vsCompiler->set_decoration(it.second.IDs[0], spv::Decoration::DecorationBinding, id);
                }
                if (it.second.IDs[1] != 0)
                {
                    fsCompiler->set_decoration(it.second.IDs[1], spv::Decoration::DecorationBinding, id);
                }
            }
        }
    }

    std::string vsText = vsCompiler->compile();

    bool usesStorageResource = vsResources.storage_buffers.size() > 0 || vsResources.storage_images.size() > 0;
    if (info.Target == GLSL && usesStorageResource)
    {
        std::string key = "#version 330";
        std::size_t idx = vsText.find(key);

        if (idx != std::string::npos)
            vsText.replace(idx, key.length(), "#version 430");
    }
    else if (info.Target == ESSL && usesStorageResource)
    {
        std::string key = "#version 300";
        std::size_t idx = vsText.find(key);

        if (idx != std::string::npos)
            vsText.replace(idx, key.length(), "#version 310");
    }

    std::string fsText = fsCompiler->compile();

    usesStorageResource = fsResources.storage_buffers.size() > 0 || fsResources.storage_images.size() > 0;
    if (info.Target == GLSL && usesStorageResource)
    {
        std::string key = "#version 330";
        std::size_t idx = fsText.find(key);

        if (idx != std::string::npos)
            fsText.replace(idx, key.length(), "#version 430");
    }
    else if (info.Target == ESSL && usesStorageResource)
    {
        std::string key = "#version 300";
        std::size_t idx = fsText.find(key);

        if (idx != std::string::npos)
            fsText.replace(idx, key.length(), "#version 310");
    }

    CompilationResult *result = new CompilationResult();
    result->Succeeded = true;

    result->DataBuffers.Resize(2);
    result->DataBuffers[0].CopyFrom(static_cast<uint32_t>(vsText.length()), (uint8_t *)vsText.c_str());
    result->DataBuffers[1].CopyFrom(static_cast<uint32_t>(fsText.length()), (uint8_t *)fsText.c_str());

    ReflectVertexInfo(*vsCompiler, vsResources, result->Reflection);
    result->Reflection.ResourceLayouts = CreateResourceLayoutArray(allResources, false);
    result->Reflection.VertexOutputs = std::move(vertexOutputs);
    result->Reflection.FragmentInputs = std::move(fragmentInputs);
    result->Reflection.UniformBlocks = CollectUniformBlocks(uniformBlocks);

    if (!bindingMapEntries.empty())
    {
        result->Reflection.BindingMap.CopyFrom(
            static_cast<uint32_t>(bindingMapEntries.size()),
            bindingMapEntries.data());
    }

    delete vsCompiler;
    delete fsCompiler;

    return result;
}

CompilationResult *CompileCompute(const CrossCompileInfo &info)
{
    std::vector<uint32_t> csBytes(
        info.ComputeShader.Data,
        info.ComputeShader.Data + info.ComputeShader.Count);
    Compiler *csCompiler = GetCompiler(csBytes, info);

    SetSpecializations(csCompiler, info);

    ShaderResources csResources = csCompiler->get_shader_resources();

    // Interface reflection first (before AddResources may rename): a compute interface is blocks + resources only (no varyings).
    std::vector<UniformBlockDescription *> uniformBlocks;
    ReflectUniformBlocks(*csCompiler, csResources.uniform_buffers, ShaderStages::Compute, uniformBlocks);

    std::map<BindingInfo, ResourceInfo> allResources;

    AddResources(csResources.uniform_buffers, csCompiler, allResources, 0, info.NormalizeResourceNames);
    AddResources(csResources.storage_buffers, csCompiler, allResources, 0, info.NormalizeResourceNames, false, true);
    AddResources(csResources.separate_images, csCompiler, allResources, 0, info.NormalizeResourceNames, true, false);
    AddResources(csResources.storage_images, csCompiler, allResources, 0, info.NormalizeResourceNames, true, true);
    AddResources(csResources.separate_samplers, csCompiler, allResources, 0, info.NormalizeResourceNames);

    std::vector<BindingMapEntry> bindingMapEntries;

    if (info.Target == HLSL || info.Target == MSL)
    {
        uint32_t bufferIndex = 0;
        uint32_t textureIndex = 0;
        uint32_t uavIndex = 0;
        uint32_t samplerIndex = 0;
        for (auto &it : allResources)
        {
            uint32_t index = GetResourceIndex(info.Target, it.second.Kind, bufferIndex, textureIndex, uavIndex, samplerIndex);

            uint32_t csID = it.second.IDs[0];
            if (csID != 0)
            {
                csCompiler->set_decoration(csID, spv::Decoration::DecorationBinding, index);
            }

            // Capture the binding map entry
            BindingMapEntry entry = {};
            entry.Set = it.first.Set;
            entry.Binding = it.first.Binding;
            entry.Kind = it.second.Kind;
            entry.Stages = ShaderStages::Compute;
            entry.FlatIndex = index;
            bindingMapEntries.push_back(entry);
        }
    }

    if (info.Target == GLSL || info.Target == ESSL)
    {
        csCompiler->build_dummy_sampler_for_combined_images();
        csCompiler->build_combined_image_samplers();
        for (auto &remap : csCompiler->get_combined_image_samplers())
        {
            csCompiler->set_name(remap.combined_id, csCompiler->get_name(remap.image_id));
        }
    }

    if (info.Target == ESSL)
    {
        for (auto &uniformBuffer : csResources.uniform_buffers)
        {
            csCompiler->unset_decoration(uniformBuffer.id, spv::Decoration::DecorationBinding);
        }

        uint32_t bufferIndex = 0;
        uint32_t imageIndex = 0;
        for (auto &it : allResources)
        {
            if (it.second.Kind == StorageBufferReadOnly || it.second.Kind == StorageBufferReadWrite)
            {
                csCompiler->set_decoration(it.second.IDs[0], spv::Decoration::DecorationBinding, bufferIndex++);
            }
            else if (it.second.Kind == StorageImage)
            {
                csCompiler->set_decoration(it.second.IDs[0], spv::Decoration::DecorationBinding, imageIndex++);
            }
        }
    }

    std::string csText = csCompiler->compile();

    delete csCompiler;

    CompilationResult *result = new CompilationResult();
    result->Succeeded = true;
    result->DataBuffers.Resize(1);
    result->DataBuffers[0].CopyFrom(static_cast<uint32_t>(csText.length()), (uint8_t *)csText.c_str());

    result->Reflection.ResourceLayouts = CreateResourceLayoutArray(allResources, true);

    if (!bindingMapEntries.empty())
    {
        result->Reflection.BindingMap.CopyFrom(
            static_cast<uint32_t>(bindingMapEntries.size()),
            bindingMapEntries.data());
    }

    return result;
}

CompilationResult *Compile(const CrossCompileInfo &info)
{
    if (info.VertexShader.Count > 0 && info.FragmentShader.Count > 0)
    {
        return CompileVertexFragment(info);
    }
    else if (info.ComputeShader.Count > 0)
    {
        return CompileCompute(info);
    }

    return new CompilationResult("The given combination of shaders was not valid.");
}

std::vector<uint32_t> ReadFile(std::string path)
{
    std::ifstream is(path, std::ios::binary | std::ios::in | std::ios::ate);
    size_t size = is.tellg();
    is.seekg(0, std::ios::beg);
    char *shaderCode = new char[size];
    is.read(shaderCode, size);
    is.close();

    std::vector<uint32_t> ret(size / 4);
    memcpy(ret.data(), shaderCode, size);

    delete[] shaderCode;
    return ret;
}

void WriteToFile(const std::string &path, const std::string &text)
{
    auto outFile = std::ofstream(path);
    outFile << text;
    outFile.close();
}

CompilationResult *CompileGLSLToSPIRV(
    const std::string sourceText,
    shaderc_shader_kind kind,
    std::string fileName,
    const shaderc::CompileOptions &options)
{
    // (shaderc compiles at optimization_level_zero - see CompileGlslToSpirv; optimisation happens in OptimizeSpirvPreservingInterface.)
    shaderc::Compiler compiler;
    shaderc::SpvCompilationResult result = compiler.CompileGlslToSpv(sourceText, kind, fileName.c_str(), options);

    if (result.GetCompilationStatus() != shaderc_compilation_status_success)
    {
        return new CompilationResult(result.GetErrorMessage());
    }

    uint32_t length = static_cast<uint32_t>(result.end() - result.begin()) * sizeof(uint32_t);
    CompilationResult *ret = new CompilationResult();
    ret->Succeeded = 1;
    ret->DataBuffers.Resize(1);
    ret->DataBuffers[0].CopyFrom(length, (uint8_t *)result.begin());
    return ret;
}

// D3 (AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md): spirv-opt's performance pass list - the SAME list as
// spvtools::Optimizer::RegisterPerformancePasses() in the pinned SPIRV-Tools (ext/known_good.json) - with every ADCE instance
// created as CreateAggressiveDCEPass(/*preserve_interface*/ true), so a declared stage input/output is never eliminated.
// Bindings are preserved through OptimizerOptions (same as shaderc's SetPreserveBindings path). Keep this list in sync with
// optimizer.cpp when the SPIRV-Tools pin moves (a test compares debug vs optimised interfaces, so drift is caught).
static void RegisterPerformancePassesPreservingInterface(spvtools::Optimizer &opt)
{
    const bool preserveInterface = true;
    opt.RegisterPass(spvtools::CreateWrapOpKillPass())
        .RegisterPass(spvtools::CreateDeadBranchElimPass())
        .RegisterPass(spvtools::CreateMergeReturnPass())
        .RegisterPass(spvtools::CreateInlineExhaustivePass())
        .RegisterPass(spvtools::CreateEliminateDeadFunctionsPass())
        .RegisterPass(spvtools::CreateAggressiveDCEPass(preserveInterface))
        .RegisterPass(spvtools::CreatePrivateToLocalPass())
        .RegisterPass(spvtools::CreateLocalSingleBlockLoadStoreElimPass())
        .RegisterPass(spvtools::CreateLocalSingleStoreElimPass())
        .RegisterPass(spvtools::CreateAggressiveDCEPass(preserveInterface))
        .RegisterPass(spvtools::CreateScalarReplacementPass())
        .RegisterPass(spvtools::CreateLocalAccessChainConvertPass())
        .RegisterPass(spvtools::CreateLocalSingleBlockLoadStoreElimPass())
        .RegisterPass(spvtools::CreateLocalSingleStoreElimPass())
        .RegisterPass(spvtools::CreateAggressiveDCEPass(preserveInterface))
        .RegisterPass(spvtools::CreateLocalMultiStoreElimPass())
        .RegisterPass(spvtools::CreateAggressiveDCEPass(preserveInterface))
        .RegisterPass(spvtools::CreateCCPPass())
        .RegisterPass(spvtools::CreateAggressiveDCEPass(preserveInterface))
        .RegisterPass(spvtools::CreateLoopUnrollPass(true))
        .RegisterPass(spvtools::CreateDeadBranchElimPass())
        .RegisterPass(spvtools::CreateRedundancyEliminationPass())
        .RegisterPass(spvtools::CreateCombineAccessChainsPass())
        .RegisterPass(spvtools::CreateSimplificationPass())
        .RegisterPass(spvtools::CreateScalarReplacementPass())
        .RegisterPass(spvtools::CreateLocalAccessChainConvertPass())
        .RegisterPass(spvtools::CreateLocalSingleBlockLoadStoreElimPass())
        .RegisterPass(spvtools::CreateLocalSingleStoreElimPass())
        .RegisterPass(spvtools::CreateAggressiveDCEPass(preserveInterface))
        .RegisterPass(spvtools::CreateSSARewritePass())
        .RegisterPass(spvtools::CreateAggressiveDCEPass(preserveInterface))
        .RegisterPass(spvtools::CreateVectorDCEPass())
        .RegisterPass(spvtools::CreateDeadInsertElimPass())
        .RegisterPass(spvtools::CreateDeadBranchElimPass())
        .RegisterPass(spvtools::CreateSimplificationPass())
        .RegisterPass(spvtools::CreateIfConversionPass())
        .RegisterPass(spvtools::CreateCopyPropagateArraysPass())
        .RegisterPass(spvtools::CreateReduceLoadSizePass())
        .RegisterPass(spvtools::CreateAggressiveDCEPass(preserveInterface))
        .RegisterPass(spvtools::CreateBlockMergePass())
        .RegisterPass(spvtools::CreateRedundancyEliminationPass())
        .RegisterPass(spvtools::CreateDeadBranchElimPass())
        .RegisterPass(spvtools::CreateBlockMergePass())
        .RegisterPass(spvtools::CreateSimplificationPass());
}

// Optimise a SPIR-V module in place (the InteropArray<uint8_t> holding the words). False + message on failure.
static bool OptimizeSpirvPreservingInterface(InteropArray<uint8_t> &module, std::string &error)
{
    spvtools::Optimizer optimizer(SPV_ENV_VULKAN_1_0);  // shaderc's default target environment
    optimizer.SetMessageConsumer([&error](spv_message_level_t, const char *, const spv_position_t &, const char *message) {
        error += message;
        error += "\n";
    });
    RegisterPerformancePassesPreservingInterface(optimizer);

    spvtools::OptimizerOptions optOptions;
    optOptions.set_preserve_bindings(true);
    optOptions.set_run_validator(false);

    const uint32_t *words = reinterpret_cast<const uint32_t *>(module.Data);
    const size_t wordCount = module.Count / sizeof(uint32_t);
    std::vector<uint32_t> optimised;
    if (!optimizer.Run(words, wordCount, &optimised, optOptions))
    {
        if (error.empty()) error = "spvtools::Optimizer::Run returned false";
        return false;
    }
    module.CopyFrom(static_cast<uint32_t>(optimised.size() * sizeof(uint32_t)), reinterpret_cast<const uint8_t *>(optimised.data()));
    return true;
}

VD_EXPORT CompilationResult *CrossCompile(CrossCompileInfo *info)
{
    try
    {
        return Compile(*info);
    }
    catch (const std::exception &e)
    {
        return new CompilationResult(e.what());
    }
}

VD_EXPORT CompilationResult *CompileGlslToSpirv(GlslCompileInfo *info)
{
    try
    {
        shaderc::CompileOptions options;

        // CRITICAL: Always generate debug info to preserve OpName instructions.
        // OpName instructions store the original identifier names (uniform blocks, variables, etc.)
        // in the SPIRV bytecode. Without these, SPIRV-Cross cannot recover the original names
        // when cross-compiling SPIRV→GLSL, resulting in auto-generated names like
        // "_RESERVED_IDENTIFIER_FIXUP_XX_YY" instead of the original uniform block names.
        // This is REQUIRED for OpenGL/GLES targets where uniform block names are used at runtime
        // (e.g., glGetUniformBlockIndex("ViewSize")).
        // Performance optimization can still be applied independently of debug info.
        options.SetGenerateDebugInfo();

        // D3 (AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md): shaderc's performance level runs spirv-opt's
        // ADCE WITHOUT preserve_interface, which deletes any stage input/output the shader declares but never reads - and a
        // fragment input that vanishes from ONE stage changes how fxc packs D3D11 interpolators (three production hits). So shaderc
        // emits UNoptimised SPIR-V here and OptimizeSpirvPreservingInterface() below runs the identical performance pass list with
        // ADCE(preserve_interface = true). Declared == compiled, by construction.
        options.SetOptimizationLevel(shaderc_optimization_level_zero);
        const bool optimise = !info->Debug;

        // CRITICAL: Preserve binding and descriptor_set decorations during optimization.
        // Veldrid's resource model requires shader binding numbers to exactly match the
        // ResourceLayout slots declared in C# application code. On Vulkan, D3D12, and Metal
        // there is no runtime query to discover where bindings ended up after compilation —
        // the numbers ARE the contract. If the optimizer strips unused bindings and renumbers
        // the survivors, the pipeline silently binds the wrong resources.
        options.SetPreserveBindings(true);

        for (uint32_t i = 0; i < info->Macros.Count; i++)
        {
            const MacroDefinition &macro = info->Macros[i];
            if (macro.ValueLength == 0)
            {
                options.AddMacroDefinition(std::string(macro.Name, macro.NameLength));
            }
            else
            {
                options.AddMacroDefinition(
                    std::string(macro.Name, macro.NameLength),
                    std::string(macro.Value, macro.ValueLength));
            }
        }

        CompilationResult *compiled = CompileGLSLToSPIRV(
            std::string(info->SourceText.Data, info->SourceText.Count),
            info->Kind,
            std::string(info->FileName.Data, info->FileName.Count),
            options);
        if (!optimise || !compiled->Succeeded)
        {
            return compiled;
        }

        // D3: the performance passes, with the interface preserved (see the comment at SetOptimizationLevel above).
        std::string optError;
        if (!OptimizeSpirvPreservingInterface(compiled->DataBuffers[0], optError))
        {
            delete compiled;
            return new CompilationResult("SPIR-V optimisation failed: " + optError);
        }
        return compiled;
    }
    catch (std::exception e)
    {
        return new CompilationResult(e.what());
    }
}

VD_EXPORT void FreeResult(CompilationResult *result)
{
    delete result;
}

VD_EXPORT uint32_t GetAbiVersion()
{
    return VELDRID_SPIRV_ABI_VERSION;
}

// Build info string: "YYYY-MM-DD HH:MM:SS <git-hash>"
// __DATE__ gives "Mmm DD YYYY", __TIME__ gives "HH:MM:SS"
// GIT_SHORT_HASH is optionally defined via CMake (-DGIT_SHORT_HASH=...)
#ifndef GIT_SHORT_HASH
#define GIT_SHORT_HASH "unknown"
#endif

static const char BuildInfoString[] =
    __DATE__ " " __TIME__ " g" GIT_SHORT_HASH;

VD_EXPORT const char* GetBuildInfo()
{
    return BuildInfoString;
}

const VertexElementFormat FloatFormats[] =
    {
        VertexElementFormat::Float1,
        VertexElementFormat::Float1,
        VertexElementFormat::Float2,
        VertexElementFormat::Float3,
        VertexElementFormat::Float4};

const VertexElementFormat IntFormats[] =
    {
        VertexElementFormat::Int1,
        VertexElementFormat::Int1,
        VertexElementFormat::Int2,
        VertexElementFormat::Int3,
        VertexElementFormat::Int4,
};

const VertexElementFormat UIntFormats[] =
    {
        VertexElementFormat::UInt1,
        VertexElementFormat::UInt1,
        VertexElementFormat::UInt2,
        VertexElementFormat::UInt3,
        VertexElementFormat::UInt4,
};

void ReflectVertexInfo(const Compiler &compiler, const ShaderResources &resources, ReflectionInfo &info)
{
    uint32_t elementCount = 0;
    for (const auto &input : resources.stage_inputs)
    {
        uint32_t location = compiler.get_decoration(input.id, spv::DecorationLocation);
        elementCount = std::max(location + 1, elementCount);
    }

    info.VertexElements = InteropArray<VertexElementDescription>(elementCount);

    for (const auto &input : resources.stage_inputs)
    {
        uint32_t location = compiler.get_decoration(input.id, spv::DecorationLocation);
        info.VertexElements[location].Semantic = VertexElementSemantic::TextureCoordinate;
        std::string name = compiler.get_name(input.id);
        if (name.empty())
        {
            name = compiler.get_fallback_name(input.id);
        }
        info.VertexElements[location].Name.CopyFrom(name.size(), name.c_str());
        SPIRType baseType = compiler.get_type(input.base_type_id);
        SPIRType type = compiler.get_type(input.type_id);
        switch (baseType.basetype)
        {
        case SPIRType::Float:
            info.VertexElements[location].Format = FloatFormats[baseType.vecsize];
            break;
        case SPIRType::Int:
            info.VertexElements[location].Format = IntFormats[baseType.vecsize];
            break;
        case SPIRType::UInt:
            info.VertexElements[location].Format = UIntFormats[baseType.vecsize];
            break;
        default:
            throw std::runtime_error("Unhandled SPIR-V vertex input data type.");
        }
    }
}
} // namespace Veldrid
