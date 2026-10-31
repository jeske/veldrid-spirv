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
        public const string Rule_V4_VertexInputsDeclared = "V4";
        public const string Rule_V5a_VaryingsDeclared = "V5a";
        public const string Rule_V5b_VaryingSets = "V5b";
        public const string Rule_V6_BlockMembersDeclared = "V6";
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
        /// D0 / D3 regression guard - "did the toolchain move an element?": what the GLSL SOURCE declares versus what the compiler PRODUCED, by
        /// NAME. V4 vertex inputs (name, location, format), V5a varyings per stage (name, location, type, flat), V6 uniform blocks (name,
        /// set, binding, member names in order, member offsets against an independent std140 oracle). Any disagreement is a violation;
        /// an unparseable layout line is a violation too (the parser must understand what it is asked to guard).
        /// </summary>
        public static List<ShaderContractViolation> VerifyDeclaredAgainstCompiled(string vertexGlsl, string fragmentGlsl, SpirvReflection compiled)
        {
            if (compiled is null) throw new ArgumentNullException(nameof(compiled));
            var violations = new List<ShaderContractViolation>();
            var vert = GlslInterfaceDeclarations.Parse(vertexGlsl ?? "");
            var frag = GlslInterfaceDeclarations.Parse(fragmentGlsl ?? "");
            foreach (string line in vert.UnparseableLayoutLines) violations.Add(new ShaderContractViolation(Rule_V5a_VaryingsDeclared, "(vertex)", line, "", "layout declaration the contract parser cannot read - give it explicit integer set/binding/location qualifiers"));
            foreach (string line in frag.UnparseableLayoutLines) violations.Add(new ShaderContractViolation(Rule_V5a_VaryingsDeclared, "(fragment)", line, "", "layout declaration the contract parser cannot read - give it explicit integer set/binding/location qualifiers"));

            VerifyDeclaredVertexInputs(vert.Inputs, compiled.VertexElements, violations);
            VerifyDeclaredVaryings("vertex out", vert.Outputs, compiled.Interface.VertexOutputs, violations);
            VerifyDeclaredVaryings("fragment in", frag.Inputs, compiled.Interface.FragmentInputs, violations);
            VerifyDeclaredBlocks("vertex", vert.UniformBlocks, compiled.Interface.UniformBlocks.Where(b => b.Stages == ShaderStages.Vertex).ToArray(), violations);
            VerifyDeclaredBlocks("fragment", frag.UniformBlocks, compiled.Interface.UniformBlocks.Where(b => b.Stages == ShaderStages.Fragment).ToArray(), violations);
            return violations;
        }

        private static void VerifyDeclaredVertexInputs(GlslDeclared_Varying[] declared, VertexElementDescription[] compiled, List<ShaderContractViolation> violations)
        {
            // compiled[] is indexed by location; a hole has an empty name.
            var compiledByName = new Dictionary<string, (uint Location, VertexElementFormat Format)>(StringComparer.Ordinal);
            for (int i = 0; i < compiled.Length; i++)
                if (!string.IsNullOrEmpty(compiled[i].Name)) compiledByName[compiled[i].Name] = ((uint)i, compiled[i].Format);
            foreach (var d in declared)
            {
                if (!compiledByName.TryGetValue(d.Name, out var c))
                {
                    violations.Add(new ShaderContractViolation(Rule_V4_VertexInputsDeclared, d.Name, $"declared in location {d.Location} {d.GlslType}", "compiled: ABSENT", "a declared vertex input was eliminated or renamed by the toolchain"));
                    continue;
                }
                if (c.Location != d.Location)
                    violations.Add(new ShaderContractViolation(Rule_V4_VertexInputsDeclared, d.Name, $"declared location {d.Location}", $"compiled location {c.Location}", "the toolchain MOVED a vertex input"));
                var expected = GlslInterfaceDeclarations.VertexFormatOf(d.GlslType);
                if (expected.HasValue && expected.Value != c.Format)
                    violations.Add(new ShaderContractViolation(Rule_V4_VertexInputsDeclared, d.Name, $"declared {d.GlslType} ({expected.Value})", $"compiled {c.Format}", "the toolchain changed a vertex input's type"));
            }
            foreach (var (name, c) in compiledByName)
                if (!declared.Any(d => string.Equals(d.Name, name, StringComparison.Ordinal)))
                    violations.Add(new ShaderContractViolation(Rule_V4_VertexInputsDeclared, name, "declared: ABSENT", $"compiled location {c.Location} {c.Format}", "the compiled stage has a vertex input the source never declared"));
        }

        private static void VerifyDeclaredVaryings(string side, GlslDeclared_Varying[] declared, ShaderInterface_Varying[] compiled, List<ShaderContractViolation> violations)
        {
            var compiledByName = compiled.ToDictionary(v => v.Name, StringComparer.Ordinal);
            foreach (var d in declared)
            {
                if (!compiledByName.TryGetValue(d.Name, out var c))
                {
                    violations.Add(new ShaderContractViolation(Rule_V5a_VaryingsDeclared, d.Name, $"declared {side} location {d.Location} {(d.Flat ? "flat " : "")}{d.GlslType}", "compiled: ABSENT", "a declared varying was eliminated or renamed by the toolchain (D3 forbids this)"));
                    continue;
                }
                if (c.Location != d.Location)
                    violations.Add(new ShaderContractViolation(Rule_V5a_VaryingsDeclared, d.Name, $"declared {side} location {d.Location}", $"compiled location {c.Location}", "the toolchain MOVED a varying"));
                if (c.Flat != d.Flat)
                    violations.Add(new ShaderContractViolation(Rule_V5a_VaryingsDeclared, d.Name, $"declared {(d.Flat ? "flat" : "smooth")}", $"compiled {(c.Flat ? "flat" : "smooth")}", "the toolchain changed a varying's interpolation"));
                string compiledType = c.ArrayLength > 0 ? c.TypeText.Replace("flat ", "") : c.TypeText.Replace("flat ", "");
                string declaredType = d.ArrayLength > 0 ? $"{d.GlslType}[{d.ArrayLength}]" : d.GlslType;
                if (!string.Equals(compiledType, declaredType, StringComparison.Ordinal))
                    violations.Add(new ShaderContractViolation(Rule_V5a_VaryingsDeclared, d.Name, $"declared {declaredType}", $"compiled {compiledType}", "the toolchain changed a varying's type"));
            }
            foreach (var c in compiled)
                if (!declared.Any(d => string.Equals(d.Name, c.Name, StringComparison.Ordinal)))
                    violations.Add(new ShaderContractViolation(Rule_V5a_VaryingsDeclared, c.Name, "declared: ABSENT", $"compiled {side} location {c.Location} {c.TypeText}", "the compiled stage has a varying the source never declared"));
        }

        private static void VerifyDeclaredBlocks(string stage, GlslDeclared_UniformBlock[] declared, ShaderInterface_UniformBlock[] compiled, List<ShaderContractViolation> violations)
        {
            var compiledByName = compiled.ToDictionary(b => b.BlockName, StringComparer.Ordinal);
            foreach (var d in declared)
            {
                if (!compiledByName.TryGetValue(d.BlockName, out var c))
                {
                    violations.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, d.BlockName, $"declared {stage} uniform block at set {d.Set} binding {d.Binding}", "compiled: ABSENT", "a declared uniform block was eliminated or renamed by the toolchain"));
                    continue;
                }
                if (c.Set != d.Set || c.Binding != d.Binding)
                    violations.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, d.BlockName, $"declared set {d.Set} binding {d.Binding}", $"compiled set {c.Set} binding {c.Binding}", "the toolchain MOVED a uniform block"));
                if (c.Members.Length != d.Members.Length)
                    violations.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, d.BlockName, $"declared {d.Members.Length} member(s)", $"compiled {c.Members.Length} member(s)", "member count differs"));
                var oracle = GlslInterfaceDeclarations.Std140Layout(d.Members, out uint oracleSize);
                for (int i = 0; i < Math.Min(c.Members.Length, d.Members.Length); i++)
                {
                    var dm = d.Members[i]; var cm = c.Members[i];
                    if (!string.Equals(dm.Name, cm.Name, StringComparison.Ordinal))
                    {
                        violations.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, $"{d.BlockName}.{dm.Name}", $"declared member #{i} '{dm.Name}'", $"compiled member #{i} '{cm.Name}'", "member order/name differs - the toolchain reordered or renamed a member"));
                        continue;
                    }
                    if (oracle[i] is { } expected && (expected.Offset != cm.OffsetBytes || expected.Size != cm.SizeBytes))
                        violations.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, $"{d.BlockName}.{dm.Name}", $"std140 oracle @ {expected.Offset} ({expected.Size} B)", $"compiled @ {cm.OffsetBytes} ({cm.SizeBytes} B)", "the toolchain laid a member out where std140 does not put it"));
                }
                if (oracle.All(o => o.HasValue) && oracleSize != c.SizeBytes)
                    violations.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, d.BlockName, $"std140 oracle size {oracleSize} B", $"compiled size {c.SizeBytes} B", "block size differs from the std140 oracle"));
            }
            foreach (var c in compiled)
                if (!declared.Any(d => string.Equals(d.BlockName, c.BlockName, StringComparison.Ordinal)))
                    violations.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, c.BlockName, "declared: ABSENT", $"compiled {stage} block at set {c.Set} binding {c.Binding}", "the compiled stage has a uniform block the source never declared"));
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