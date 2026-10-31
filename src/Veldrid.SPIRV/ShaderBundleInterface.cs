using System;
using System.Collections.Generic;
using System.Linq;

namespace Veldrid.SPIRV
{
    /// <summary>Scalar base type of a stage interface variable or uniform member (mirrors native <c>InterfaceScalarType</c>, uint8).</summary>
    public enum ShaderInterface_ScalarType : byte
    {
        Float = 0,
        Int = 1,
        UInt = 2,
        Bool = 3,
        Double = 4,
        Half = 5,
    }

    /// <summary>
    /// One stage varying - a vertex <c>out</c> or a fragment <c>in</c> - as the COMPILED (post-optimisation) SPIR-V declares it, under the
    /// GLSL author's name. Spec: <c>AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md</c>.
    /// </summary>
    public readonly record struct ShaderInterface_Varying(string Name, uint Location, ShaderInterface_ScalarType Scalar, uint VecSize, uint Columns, uint ArrayLength, bool Flat)
    {
        /// <summary>GLSL spelling of the type (vec3, flat ivec2, mat4, float[4]) for violation tables.</summary>
        public string TypeText
        {
            get
            {
                string prefix = Scalar switch { ShaderInterface_ScalarType.Int => "i", ShaderInterface_ScalarType.UInt => "u", ShaderInterface_ScalarType.Bool => "b", ShaderInterface_ScalarType.Double => "d", _ => "" };
                string scalar = Scalar switch { ShaderInterface_ScalarType.Float => "float", ShaderInterface_ScalarType.Int => "int", ShaderInterface_ScalarType.UInt => "uint", ShaderInterface_ScalarType.Bool => "bool", ShaderInterface_ScalarType.Double => "double", ShaderInterface_ScalarType.Half => "float16_t", _ => "?" };
                string core = Columns > 1 ? $"{prefix}mat{Columns}{(Columns != VecSize ? "x" + VecSize : "")}" : VecSize > 1 ? $"{prefix}vec{VecSize}" : scalar;
                if (ArrayLength > 0) core += $"[{ArrayLength}]";
                return Flat ? "flat " + core : core;
            }
        }

        /// <summary>Same slot and type (everything except the name) - what must agree across the two stages for one name.</summary>
        public bool SameShapeAs(in ShaderInterface_Varying other)
            => Location == other.Location && Scalar == other.Scalar && VecSize == other.VecSize && Columns == other.Columns && ArrayLength == other.ArrayLength && Flat == other.Flat;
    }

    /// <summary>One member of a uniform block with the std140 placement the compiler laid out.</summary>
    public readonly record struct ShaderInterface_UniformMember(string Name, string GlslType, uint OffsetBytes, uint SizeBytes);

    /// <summary>
    /// One uniform block as ONE stage sees it. <see cref="ShaderInterfaceVerifier"/> merges per (Set, Binding) and requires every stage that
    /// declares the block to declare it identically.
    /// </summary>
    public sealed record ShaderInterface_UniformBlock(string BlockName, uint Set, uint Binding, uint SizeBytes, ShaderStages Stages, ShaderInterface_UniformMember[] Members);

    /// <summary>
    /// The compiled interface of a shader set: what the GPU will actually bind, by NAME. Produced by reflection in
    /// <see cref="SpirvCompilation"/>, verified by <see cref="ShaderInterfaceVerifier"/>, carried in the .vdshader bundle (AN_Veldrid).
    /// Vertex inputs and resources are already on <see cref="SpirvReflection"/> (VertexElements, ResourceLayouts) and are not duplicated here.
    /// </summary>
    public sealed record ShaderBundleInterface(
        ShaderInterface_Varying[] VertexOutputs,
        ShaderInterface_Varying[] FragmentInputs,
        ShaderInterface_UniformBlock[] UniformBlocks)
    {
        public static readonly ShaderBundleInterface Empty = new(Array.Empty<ShaderInterface_Varying>(), Array.Empty<ShaderInterface_Varying>(), Array.Empty<ShaderInterface_UniformBlock>());
    }

    /// <summary>One failed contract rule: which rule, which NAME, what each side said.</summary>
    public readonly record struct ShaderContractViolation(string Rule, string Name, string SideA, string SideB, string Detail)
    {
        public override string ToString() => $"[{Rule}] {Name}: {SideA}  |  {SideB}  -  {Detail}";
    }

    /// <summary>
    /// Thrown at COMPILE time (both the precompile tool and the runtime path - they are the same call) when the compiled stages do not
    /// agree with each other or with their declarations. Never a warning (spec D2). The message is the full violation table.
    /// </summary>
    public sealed class ShaderInterfaceContractException : SpirvCompilationException
    {
        public IReadOnlyList<ShaderContractViolation> Violations { get; }
        public string ShaderName { get; }

        public ShaderInterfaceContractException(string shaderName, IReadOnlyList<ShaderContractViolation> violations)
            : base(BuildMessage(shaderName, violations))
        {
            ShaderName = shaderName;
            Violations = violations;
        }

        private static string BuildMessage(string shaderName, IReadOnlyList<ShaderContractViolation> violations)
        {
            var lines = new List<string> { $"Shader interface contract violated for '{shaderName}' ({violations.Count} violation(s)). A slot number is never evidence of agreement; every boundary joins by NAME." };
            lines.AddRange(violations.Select(v => "  " + v.ToString()));
            lines.Add("  See AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md");
            return string.Join(Environment.NewLine, lines);
        }
    }
}