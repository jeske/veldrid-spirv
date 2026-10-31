using System;
using System.Collections.Generic;
using System.Linq;

namespace Veldrid.SPIRV
{
    /// <summary>
    /// The compile-time half of the shader interface contract (AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md).
    /// Pure: takes a compiled <see cref="ShaderBundleInterface"/>, returns violations. <see cref="ShaderBundleCompiler"/> calls it on
    /// EVERY compile - precompile tool and runtime are the same call - and throws <see cref="ShaderInterfaceContractException"/> on any.
    /// Rules here: V5b (vertex outputs == fragment inputs by NAME, then slot/type/flat per name) and V6b (a uniform block declared by
    /// two stages must be declared identically). Declared-vs-compiled (V4/V5a/V6) joins in S2 once elimination is prevented (D3).
    /// </summary>
    public static class ShaderInterfaceVerifier
    {
        public const string Rule_V5b_VaryingSets = "V5b";
        public const string Rule_V6b_BlockStagesAgree = "V6b";

        /// <summary>All violations for a compiled vertex+fragment interface. Empty list = contract holds.</summary>
        public static List<ShaderContractViolation> VerifyStages(ShaderBundleInterface compiled)
        {
            if (compiled is null) throw new ArgumentNullException(nameof(compiled));
            var violations = new List<ShaderContractViolation>();
            VerifyVaryingSets(compiled.VertexOutputs, compiled.FragmentInputs, violations);
            VerifyUniformBlocksAgreeAcrossStages(compiled.UniformBlocks, violations);
            return violations;
        }

        /// <summary>Throws <see cref="ShaderInterfaceContractException"/> when <see cref="VerifyStages"/> finds anything.</summary>
        public static void ThrowIfViolated(string shaderName, ShaderBundleInterface compiled)
        {
            var violations = VerifyStages(compiled);
            if (violations.Count > 0) throw new ShaderInterfaceContractException(shaderName, violations);
        }

        /// <summary>
        /// D4: the vertex `out` set and the fragment `in` set must be IDENTICAL - same names, and per name the same location, type and
        /// interpolation. Identical sets cross-compile to identical HLSL I/O structs, which fxc packs identically; any difference is the
        /// D3D11 register-mismatch bug class. A name on one side only, or two names sharing a location, is reported by name.
        /// </summary>
        private static void VerifyVaryingSets(ShaderInterface_Varying[] vertexOutputs, ShaderInterface_Varying[] fragmentInputs, List<ShaderContractViolation> violations)
        {
            var byNameOut = vertexOutputs.ToDictionary(v => v.Name, StringComparer.Ordinal);
            var byNameIn = fragmentInputs.ToDictionary(v => v.Name, StringComparer.Ordinal);

            foreach (var vo in vertexOutputs.OrderBy(v => v.Location))
            {
                if (!byNameIn.TryGetValue(vo.Name, out var fi))
                {
                    string atSameLocation = fragmentInputs.Where(f => f.Location == vo.Location).Select(f => f.Name).FirstOrDefault();
                    violations.Add(new ShaderContractViolation(Rule_V5b_VaryingSets, vo.Name,
                        $"vertex out location {vo.Location} {vo.TypeText}", "fragment in: ABSENT",
                        atSameLocation is null
                            ? "the fragment shader must declare every vertex output (unused ones too); it was not declared, or it was declared and eliminated (see D3)"
                            : $"location {vo.Location} is '{atSameLocation}' in the fragment shader - two names for one slot"));
                    continue;
                }
                if (!vo.SameShapeAs(fi))
                {
                    violations.Add(new ShaderContractViolation(Rule_V5b_VaryingSets, vo.Name,
                        $"vertex out location {vo.Location} {vo.TypeText}", $"fragment in location {fi.Location} {fi.TypeText}",
                        "same name, different slot/type/interpolation"));
                }
            }

            foreach (var fi in fragmentInputs.OrderBy(v => v.Location))
            {
                if (!byNameOut.ContainsKey(fi.Name))
                {
                    violations.Add(new ShaderContractViolation(Rule_V5b_VaryingSets, fi.Name,
                        "vertex out: ABSENT", $"fragment in location {fi.Location} {fi.TypeText}",
                        "the fragment shader reads a varying the vertex shader does not write"));
                }
            }
        }

        /// <summary>V6b: when both stages declare the block at (set, binding), name, size and every member (name, offset, size) must agree.</summary>
        private static void VerifyUniformBlocksAgreeAcrossStages(ShaderInterface_UniformBlock[] blocks, List<ShaderContractViolation> violations)
        {
            foreach (var group in blocks.GroupBy(b => (b.Set, b.Binding)))
            {
                var first = group.First();
                foreach (var other in group.Skip(1))
                {
                    string slot = $"set {first.Set} binding {first.Binding}";
                    if (!string.Equals(first.BlockName, other.BlockName, StringComparison.Ordinal))
                        violations.Add(new ShaderContractViolation(Rule_V6b_BlockStagesAgree, first.BlockName, $"{first.Stages}: block '{first.BlockName}'", $"{other.Stages}: block '{other.BlockName}'", $"different block names at {slot}"));
                    if (first.SizeBytes != other.SizeBytes)
                        violations.Add(new ShaderContractViolation(Rule_V6b_BlockStagesAgree, first.BlockName, $"{first.Stages}: {first.SizeBytes} bytes", $"{other.Stages}: {other.SizeBytes} bytes", $"different std140 sizes at {slot}"));
                    var otherMembers = other.Members.ToDictionary(m => m.Name, StringComparer.Ordinal);
                    foreach (var m in first.Members)
                    {
                        if (!otherMembers.TryGetValue(m.Name, out var om))
                            violations.Add(new ShaderContractViolation(Rule_V6b_BlockStagesAgree, $"{first.BlockName}.{m.Name}", $"{first.Stages}: {m.GlslType} @ {m.OffsetBytes}", $"{other.Stages}: ABSENT", $"member missing in one stage at {slot}"));
                        else if (om.OffsetBytes != m.OffsetBytes || om.SizeBytes != m.SizeBytes)
                            violations.Add(new ShaderContractViolation(Rule_V6b_BlockStagesAgree, $"{first.BlockName}.{m.Name}", $"{first.Stages}: {m.GlslType} @ {m.OffsetBytes} ({m.SizeBytes} B)", $"{other.Stages}: {om.GlslType} @ {om.OffsetBytes} ({om.SizeBytes} B)", $"member placement differs at {slot}"));
                    }
                    foreach (var om in other.Members)
                        if (!first.Members.Any(m => string.Equals(m.Name, om.Name, StringComparison.Ordinal)))
                            violations.Add(new ShaderContractViolation(Rule_V6b_BlockStagesAgree, $"{first.BlockName}.{om.Name}", $"{first.Stages}: ABSENT", $"{other.Stages}: {om.GlslType} @ {om.OffsetBytes}", $"member missing in one stage at {slot}"));
                }
            }
        }
    }
}