using System;
using System.Collections.Generic;

namespace Veldrid.SPIRV
{
    /// <summary>
    /// The compile-time half of the shader interface contract (AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md).
    /// Pure: takes a compiled interface (and, for declared-vs-compiled, the parsed GLSL declarations), reports violations.
    /// <see cref="ShaderBundleCompiler"/> calls it on EVERY compile - precompile tool and runtime are the same call - and throws
    /// <see cref="ShaderInterfaceContractException"/> on any. Rules: V4 (vertex inputs declared == compiled), V5a (varyings declared ==
    /// compiled per stage), V5b (vertex outputs == fragment inputs by NAME), V6 (block members declared == compiled, std140 oracle),
    /// V6b / V6c (a uniform / storage block declared by two stages is declared identically).
    /// <para>
    /// <b>ZERO-ALLOCATION HOT PATH (repo rule, AN_Veldrid c4084802 / AN_Monsters spec 204 C2).</b> Every check is written ONCE as a
    /// <c>bool Check*(..., List&lt;ShaderContractViolation&gt; collector)</c>:
    /// <list type="bullet">
    /// <item><c>collector == null</c> - FIRST PASS: returns false at the first violation, allocates NOTHING (no LINQ, no dictionaries, no
    /// string formatting; name lookups are linear scans over the small interface arrays).</item>
    /// <item><c>collector != null</c> - DIAGNOSTIC PASS: keeps going, formats and collects every violation, returns false if any.</item>
    /// </list>
    /// The <c>ThrowIf*</c> wrappers run the first pass and ONLY on failure rerun with a collector to build the exception. The
    /// <c>Verify*</c> list-returning forms exist for tests and tools that want the table; they are the diagnostic pass by definition.
    /// Do not "simplify" a check back to a single allocating pass: a passing compile must not pay for the failure report.
    /// </para>
    /// </summary>
    public static class ShaderInterfaceVerifier
    {
        public const string Rule_V4_VertexInputsDeclared = "V4";
        public const string Rule_V5a_VaryingsDeclared = "V5a";
        public const string Rule_V5b_VaryingSets = "V5b";
        public const string Rule_V6_BlockMembersDeclared = "V6";
        public const string Rule_V6b_BlockStagesAgree = "V6b";
        /// <summary>V10: a storage block declared by two stages must be declared identically.</summary>
        public const string Rule_V6c_StorageStagesAgree = "V6c";

        // ───────────────────────────── stage agreement (V5b, V6b, V6c) ─────────────────────────────

        /// <summary>First pass, zero allocation: do the compiled stages agree with each other? (V5b, V6b, V6c)</summary>
        public static bool StagesHold(ShaderBundleInterface compiled)
        {
            if (compiled is null) throw new ArgumentNullException(nameof(compiled));
            return CheckStages(compiled, null);
        }

        /// <summary>Diagnostic pass: all violations for a compiled vertex+fragment interface. Empty list = contract holds.</summary>
        public static List<ShaderContractViolation> VerifyStages(ShaderBundleInterface compiled)
        {
            if (compiled is null) throw new ArgumentNullException(nameof(compiled));
            var violations = new List<ShaderContractViolation>();
            CheckStages(compiled, violations);
            return violations;
        }

        /// <summary>Zero-allocation first pass; on failure a diagnostic rerun builds the <see cref="ShaderInterfaceContractException"/>.</summary>
        public static void ThrowIfViolated(string shaderName, ShaderBundleInterface compiled)
        {
            if (StagesHold(compiled)) return;
            throw new ShaderInterfaceContractException(shaderName, VerifyStages(compiled));
        }

        private static bool CheckStages(ShaderBundleInterface compiled, List<ShaderContractViolation> collector)
        {
            bool ok = true;
            if (!CheckVaryingSets(compiled.VertexOutputs, compiled.FragmentInputs, collector)) { if (collector is null) return false; ok = false; }
            if (!CheckUniformBlocksAgreeAcrossStages(compiled.UniformBlocks, collector)) { if (collector is null) return false; ok = false; }
            if (!CheckStorageBlocksAgreeAcrossStages(compiled.StorageBlocks, collector)) { if (collector is null) return false; ok = false; }
            return ok;
        }

        // ───────────────────────────── declared vs compiled, vertex + fragment (V4, V5a, V6) ─────────────────────────────

        /// <summary>
        /// First pass, zero allocation: does what the GLSL SOURCE declares match what the compiler PRODUCED, by NAME? V4 vertex inputs
        /// (name, location, format), V5a varyings per stage (name, location, type, flat), V6 uniform / storage blocks (name, set, binding,
        /// member names in order, member offsets against the independent std140 oracle). Parsing the GLSL (<see cref="GlslInterfaceDeclarations.Parse"/>)
        /// is the caller's one-time input; this method allocates nothing itself.
        /// </summary>
        public static bool DeclaredMatchesCompiled(GlslInterfaceDeclarations vertex, GlslInterfaceDeclarations fragment, SpirvReflection compiled)
        {
            if (vertex is null) throw new ArgumentNullException(nameof(vertex));
            if (fragment is null) throw new ArgumentNullException(nameof(fragment));
            if (compiled is null) throw new ArgumentNullException(nameof(compiled));
            return CheckDeclaredVertexFragment(vertex, fragment, compiled, null);
        }

        /// <summary>Diagnostic pass (D0 / D3 regression guard - "did the toolchain move an element?"): every violation, parsed from the sources.</summary>
        public static List<ShaderContractViolation> VerifyDeclaredAgainstCompiled(string vertexGlsl, string fragmentGlsl, SpirvReflection compiled)
        {
            if (compiled is null) throw new ArgumentNullException(nameof(compiled));
            var vert = GlslInterfaceDeclarations.Parse(vertexGlsl ?? "");
            var frag = GlslInterfaceDeclarations.Parse(fragmentGlsl ?? "");
            var violations = new List<ShaderContractViolation>();
            CheckDeclaredVertexFragment(vert, frag, compiled, violations);
            return violations;
        }

        /// <summary>Parse once, zero-allocation first pass, diagnostic rerun only on failure.</summary>
        public static void ThrowIfDeclaredDiffersFromCompiled(string shaderName, string vertexGlsl, string fragmentGlsl, SpirvReflection compiled)
        {
            if (compiled is null) throw new ArgumentNullException(nameof(compiled));
            var vert = GlslInterfaceDeclarations.Parse(vertexGlsl ?? "");
            var frag = GlslInterfaceDeclarations.Parse(fragmentGlsl ?? "");
            if (CheckDeclaredVertexFragment(vert, frag, compiled, null)) return;
            var violations = new List<ShaderContractViolation>();
            CheckDeclaredVertexFragment(vert, frag, compiled, violations);
            throw new ShaderInterfaceContractException(shaderName, violations);
        }

        private static bool CheckDeclaredVertexFragment(GlslInterfaceDeclarations vert, GlslInterfaceDeclarations frag, SpirvReflection compiled, List<ShaderContractViolation> collector)
        {
            bool ok = true;
            if (!CheckNoUnparseableLines("(vertex)", vert, collector)) { if (collector is null) return false; ok = false; }
            if (!CheckNoUnparseableLines("(fragment)", frag, collector)) { if (collector is null) return false; ok = false; }
            if (!CheckDeclaredVertexInputs(vert.Inputs, compiled.VertexElements, collector)) { if (collector is null) return false; ok = false; }
            if (!CheckDeclaredVaryings("vertex out", vert.Outputs, compiled.Interface.VertexOutputs, collector)) { if (collector is null) return false; ok = false; }
            if (!CheckDeclaredVaryings("fragment in", frag.Inputs, compiled.Interface.FragmentInputs, collector)) { if (collector is null) return false; ok = false; }
            if (!CheckDeclaredBlocks("vertex", ShaderStages.Vertex, vert.UniformBlocks, compiled.Interface.UniformBlocks, collector)) { if (collector is null) return false; ok = false; }
            if (!CheckDeclaredBlocks("fragment", ShaderStages.Fragment, frag.UniformBlocks, compiled.Interface.UniformBlocks, collector)) { if (collector is null) return false; ok = false; }
            if (!CheckDeclaredStorageBlocks("vertex", ShaderStages.Vertex, vert.StorageBlocks, compiled.Interface.StorageBlocks, collector)) { if (collector is null) return false; ok = false; }
            if (!CheckDeclaredStorageBlocks("fragment", ShaderStages.Fragment, frag.StorageBlocks, compiled.Interface.StorageBlocks, collector)) { if (collector is null) return false; ok = false; }
            return ok;
        }

        // ───────────────────────────── declared vs compiled, compute (V6; spec 204 C2) ─────────────────────────────

        /// <summary>First pass, zero allocation, for a COMPUTE stage: declared uniform / storage blocks vs compiled (there are no varyings or vertex inputs).</summary>
        public static bool DeclaredMatchesCompiled(GlslInterfaceDeclarations compute, SpirvReflection compiled)
        {
            if (compute is null) throw new ArgumentNullException(nameof(compute));
            if (compiled is null) throw new ArgumentNullException(nameof(compiled));
            return CheckDeclaredCompute(compute, compiled, null);
        }

        /// <summary>Diagnostic pass for a compute stage.</summary>
        public static List<ShaderContractViolation> VerifyDeclaredAgainstCompiled(string computeGlsl, SpirvReflection compiled)
        {
            if (compiled is null) throw new ArgumentNullException(nameof(compiled));
            var comp = GlslInterfaceDeclarations.Parse(computeGlsl ?? "");
            var violations = new List<ShaderContractViolation>();
            CheckDeclaredCompute(comp, compiled, violations);
            return violations;
        }

        /// <summary>Compute: parse once, zero-allocation first pass, diagnostic rerun only on failure.</summary>
        public static void ThrowIfDeclaredDiffersFromCompiled(string shaderName, string computeGlsl, SpirvReflection compiled)
        {
            if (compiled is null) throw new ArgumentNullException(nameof(compiled));
            var comp = GlslInterfaceDeclarations.Parse(computeGlsl ?? "");
            if (CheckDeclaredCompute(comp, compiled, null)) return;
            var violations = new List<ShaderContractViolation>();
            CheckDeclaredCompute(comp, compiled, violations);
            throw new ShaderInterfaceContractException(shaderName, violations);
        }

        private static bool CheckDeclaredCompute(GlslInterfaceDeclarations comp, SpirvReflection compiled, List<ShaderContractViolation> collector)
        {
            bool ok = true;
            if (!CheckNoUnparseableLines("(compute)", comp, collector)) { if (collector is null) return false; ok = false; }
            if (!CheckDeclaredBlocks("compute", ShaderStages.Compute, comp.UniformBlocks, compiled.Interface.UniformBlocks, collector)) { if (collector is null) return false; ok = false; }
            if (!CheckDeclaredStorageBlocks("compute", ShaderStages.Compute, comp.StorageBlocks, compiled.Interface.StorageBlocks, collector)) { if (collector is null) return false; ok = false; }
            return ok;
        }

        // ───────────────────────────── checks (each: bool, nullable collector, no allocation when collector is null) ─────────────────────────────

        private static bool CheckNoUnparseableLines(string stageLabel, GlslInterfaceDeclarations declared, List<ShaderContractViolation> collector)
        {
            string[] lines = declared.UnparseableLayoutLines;
            if (lines.Length == 0) return true;
            if (collector is null) return false;
            foreach (string line in lines)
                collector.Add(new ShaderContractViolation(Rule_V5a_VaryingsDeclared, stageLabel, line, "", "layout declaration the contract parser cannot read - give it explicit integer set/binding/location qualifiers"));
            return false;
        }

        private static bool CheckDeclaredVertexInputs(GlslDeclared_Varying[] declared, VertexElementDescription[] compiled, List<ShaderContractViolation> collector)
        {
            // compiled[] is indexed by location; a hole has an empty name.
            bool ok = true;
            foreach (ref readonly GlslDeclared_Varying d in declared.AsSpan())
            {
                int ci = IndexOfVertexElement(compiled, d.Name);
                if (ci < 0)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V4_VertexInputsDeclared, d.Name, $"declared in location {d.Location} {d.GlslType}", "compiled: ABSENT", "a declared vertex input was eliminated or renamed by the toolchain"));
                    continue;
                }
                if ((uint)ci != d.Location)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V4_VertexInputsDeclared, d.Name, $"declared location {d.Location}", $"compiled location {ci}", "the toolchain MOVED a vertex input"));
                }
                VertexElementFormat? expected = GlslInterfaceDeclarations.VertexFormatOf(d.GlslType);
                if (expected.HasValue && expected.Value != compiled[ci].Format)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V4_VertexInputsDeclared, d.Name, $"declared {d.GlslType} ({expected.Value})", $"compiled {compiled[ci].Format}", "the toolchain changed a vertex input's type"));
                }
            }
            for (int i = 0; i < compiled.Length; i++)
            {
                if (string.IsNullOrEmpty(compiled[i].Name)) continue;
                if (IndexOfDeclaredVarying(declared, compiled[i].Name) >= 0) continue;
                if (collector is null) return false;
                ok = false; collector.Add(new ShaderContractViolation(Rule_V4_VertexInputsDeclared, compiled[i].Name, "declared: ABSENT", $"compiled location {i} {compiled[i].Format}", "the compiled stage has a vertex input the source never declared"));
            }
            return ok;
        }

        private static bool CheckDeclaredVaryings(string side, GlslDeclared_Varying[] declared, ShaderInterface_Varying[] compiled, List<ShaderContractViolation> collector)
        {
            bool ok = true;
            foreach (ref readonly GlslDeclared_Varying d in declared.AsSpan())
            {
                int ci = IndexOfVarying(compiled, d.Name);
                if (ci < 0)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V5a_VaryingsDeclared, d.Name, $"declared {side} location {d.Location} {(d.Flat ? "flat " : "")}{d.GlslType}", "compiled: ABSENT", "a declared varying was eliminated or renamed by the toolchain (D3 forbids this)"));
                    continue;
                }
                ref readonly ShaderInterface_Varying c = ref compiled[ci];
                if (c.Location != d.Location)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V5a_VaryingsDeclared, d.Name, $"declared {side} location {d.Location}", $"compiled location {c.Location}", "the toolchain MOVED a varying"));
                }
                if (c.Flat != d.Flat)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V5a_VaryingsDeclared, d.Name, $"declared {(d.Flat ? "flat" : "smooth")}", $"compiled {(c.Flat ? "flat" : "smooth")}", "the toolchain changed a varying's interpolation"));
                }
                if (!DeclaredTypeMatchesCompiled(d.GlslType, d.ArrayLength, c))
                {
                    if (collector is null) return false;
                    string declaredType = d.ArrayLength > 0 ? $"{d.GlslType}[{d.ArrayLength}]" : d.GlslType;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V5a_VaryingsDeclared, d.Name, $"declared {declaredType}", $"compiled {c.TypeText.Replace("flat ", "")}", "the toolchain changed a varying's type"));
                }
            }
            foreach (ref readonly ShaderInterface_Varying c in compiled.AsSpan())
            {
                if (IndexOfDeclaredVarying(declared, c.Name) >= 0) continue;
                if (collector is null) return false;
                ok = false; collector.Add(new ShaderContractViolation(Rule_V5a_VaryingsDeclared, c.Name, "declared: ABSENT", $"compiled {side} location {c.Location} {c.TypeText}", "the compiled stage has a varying the source never declared"));
            }
            return ok;
        }

        /// <summary>V6 for uniform blocks of ONE stage (<paramref name="stage"/> selects the compiled entries; stages are reflected per stage occurrence).</summary>
        private static bool CheckDeclaredBlocks(string stageLabel, ShaderStages stage, GlslDeclared_UniformBlock[] declared, ShaderInterface_UniformBlock[] compiledAllStages, List<ShaderContractViolation> collector)
        {
            bool ok = true;
            foreach (GlslDeclared_UniformBlock d in declared)
            {
                ShaderInterface_UniformBlock c = FindUniformBlock(compiledAllStages, stage, d.BlockName);
                if (c is null)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, d.BlockName, $"declared {stageLabel} uniform block at set {d.Set} binding {d.Binding}", "compiled: ABSENT", "a declared uniform block was eliminated or renamed by the toolchain"));
                    continue;
                }
                if (c.Set != d.Set || c.Binding != d.Binding)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, d.BlockName, $"declared set {d.Set} binding {d.Binding}", $"compiled set {c.Set} binding {c.Binding}", "the toolchain MOVED a uniform block"));
                }
                if (c.Members.Length != d.Members.Length)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, d.BlockName, $"declared {d.Members.Length} member(s)", $"compiled {c.Members.Length} member(s)", "member count differs"));
                }
                // The std140 oracle runs as an allocation-free cursor over the declared members in order.
                var oracle = new GlslInterfaceDeclarations.Std140LayoutCursor();
                int n = Math.Min(c.Members.Length, d.Members.Length);
                for (int i = 0; i < d.Members.Length; i++)
                {
                    ref readonly GlslDeclared_Member dm = ref d.Members[i];
                    bool known = oracle.TryPlace(dm, out uint expectedOffset, out uint expectedSize);
                    if (i >= n) continue;
                    ref readonly ShaderInterface_UniformMember cm = ref c.Members[i];
                    if (!string.Equals(dm.Name, cm.Name, StringComparison.Ordinal))
                    {
                        if (collector is null) return false;
                        ok = false; collector.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, $"{d.BlockName}.{dm.Name}", $"declared member #{i} '{dm.Name}'", $"compiled member #{i} '{cm.Name}'", "member order/name differs - the toolchain reordered or renamed a member"));
                        continue;
                    }
                    if (known && (expectedOffset != cm.OffsetBytes || expectedSize != cm.SizeBytes))
                    {
                        if (collector is null) return false;
                        ok = false; collector.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, $"{d.BlockName}.{dm.Name}", $"std140 oracle @ {expectedOffset} ({expectedSize} B)", $"compiled @ {cm.OffsetBytes} ({cm.SizeBytes} B)", "the toolchain laid a member out where std140 does not put it"));
                    }
                }
                if (oracle.AllMembersKnown && oracle.BlockSizeBytes != c.SizeBytes)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, d.BlockName, $"std140 oracle size {oracle.BlockSizeBytes} B", $"compiled size {c.SizeBytes} B", "block size differs from the std140 oracle"));
                }
            }
            foreach (ShaderInterface_UniformBlock c in compiledAllStages)
            {
                if (c.Stages != stage) continue;
                if (IndexOfDeclaredBlock(declared, c.BlockName) >= 0) continue;
                if (collector is null) return false;
                ok = false; collector.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, c.BlockName, "declared: ABSENT", $"compiled {stageLabel} block at set {c.Set} binding {c.Binding}", "the compiled stage has a uniform block the source never declared"));
            }
            return ok;
        }

        /// <summary>
        /// V6 for storage blocks (V10): every declared <c>buffer</c> block exists in the compiled stage at the declared (set, binding) with the
        /// declared member NAMES in order (fixed members, then the runtime array), and the compiled stage has no storage block the source did
        /// not declare. Placement is verified against the C# struct at pipeline creation (Veldrid V7e); an independent std430 oracle is a follow-up.
        /// </summary>
        private static bool CheckDeclaredStorageBlocks(string stageLabel, ShaderStages stage, GlslDeclared_StorageBlock[] declared, ShaderInterface_StorageBlock[] compiledAllStages, List<ShaderContractViolation> collector)
        {
            bool ok = true;
            foreach (GlslDeclared_StorageBlock d in declared)
            {
                ShaderInterface_StorageBlock c = FindStorageBlock(compiledAllStages, stage, d.BlockName);
                if (c is null)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, d.BlockName, $"declared {stageLabel} storage block at set {d.Set} binding {d.Binding}", "compiled: ABSENT", "a declared storage block was eliminated or renamed by the toolchain"));
                    continue;
                }
                if (c.Set != d.Set || c.Binding != d.Binding)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, d.BlockName, $"declared set {d.Set} binding {d.Binding}", $"compiled set {c.Set} binding {c.Binding}", "the toolchain MOVED a storage block"));
                }
                if (c.ReadOnly != d.ReadOnly)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, d.BlockName, $"declared {(d.ReadOnly ? "readonly" : "read-write")}", $"compiled {(c.ReadOnly ? "readonly" : "read-write")}", "the toolchain changed a storage block's access"));
                }
                if (!StorageMemberNamesMatch(d, c))
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, d.BlockName, $"declared members [{JoinDeclaredMemberNames(d)}]", $"compiled members [{JoinCompiledStorageMemberNames(c)}]", "member order/names differ - the toolchain reordered, renamed or dropped a member"));
                }
                bool declaredRuntime = d.Members.Length > 0 && d.Members[d.Members.Length - 1].IsRuntimeArray;
                if (declaredRuntime != c.HasRuntimeArray)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, d.BlockName, $"declared {(declaredRuntime ? "a trailing runtime array" : "no runtime array")}", $"compiled {(c.HasRuntimeArray ? "runtime array " + c.RuntimeArrayName + "[]" : "no runtime array")}", "runtime-array shape differs"));
                }
            }
            foreach (ShaderInterface_StorageBlock c in compiledAllStages)
            {
                if (c.Stages != stage) continue;
                if (IndexOfDeclaredStorageBlock(declared, c.BlockName) >= 0) continue;
                if (collector is null) return false;
                ok = false; collector.Add(new ShaderContractViolation(Rule_V6_BlockMembersDeclared, c.BlockName, "declared: ABSENT", $"compiled {stageLabel} storage block at set {c.Set} binding {c.Binding}", "the compiled stage has a storage block the source never declared"));
            }
            return ok;
        }

        /// <summary>
        /// D4 / V5b: the vertex <c>out</c> set and the fragment <c>in</c> set must be IDENTICAL - same names, and per name the same location, type
        /// and interpolation. Identical sets cross-compile to identical HLSL I/O structs, which fxc packs identically; any difference is the
        /// D3D11 register-mismatch bug class. A name on one side only, or two names sharing a location, is reported by name.
        /// </summary>
        private static bool CheckVaryingSets(ShaderInterface_Varying[] vertexOutputs, ShaderInterface_Varying[] fragmentInputs, List<ShaderContractViolation> collector)
        {
            bool ok = true;
            foreach (ref readonly ShaderInterface_Varying vo in vertexOutputs.AsSpan())
            {
                int fi = IndexOfVarying(fragmentInputs, vo.Name);
                if (fi < 0)
                {
                    if (collector is null) return false;
                    int atSameLocation = IndexOfVaryingAtLocation(fragmentInputs, vo.Location);
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V5b_VaryingSets, vo.Name,
                        $"vertex out location {vo.Location} {vo.TypeText}", "fragment in: ABSENT",
                        atSameLocation < 0
                            ? "the fragment shader must declare every vertex output (unused ones too); it was not declared, or it was declared and eliminated (see D3)"
                            : $"location {vo.Location} is '{fragmentInputs[atSameLocation].Name}' in the fragment shader - two names for one slot"));
                    continue;
                }
                if (!vo.SameShapeAs(fragmentInputs[fi]))
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V5b_VaryingSets, vo.Name,
                        $"vertex out location {vo.Location} {vo.TypeText}", $"fragment in location {fragmentInputs[fi].Location} {fragmentInputs[fi].TypeText}",
                        "same name, different slot/type/interpolation"));
                }
            }
            foreach (ref readonly ShaderInterface_Varying fi in fragmentInputs.AsSpan())
            {
                if (IndexOfVarying(vertexOutputs, fi.Name) >= 0) continue;
                if (collector is null) return false;
                ok = false; collector.Add(new ShaderContractViolation(Rule_V5b_VaryingSets, fi.Name,
                    "vertex out: ABSENT", $"fragment in location {fi.Location} {fi.TypeText}",
                    "the fragment shader reads a varying the vertex shader does not write"));
            }
            return ok;
        }

        /// <summary>V6b: when two stages declare a block at (set, binding), name, size and every member (name, offset, size) must agree. Pairwise over the small array - no grouping allocation.</summary>
        private static bool CheckUniformBlocksAgreeAcrossStages(ShaderInterface_UniformBlock[] blocks, List<ShaderContractViolation> collector)
        {
            bool ok = true;
            for (int i = 0; i < blocks.Length; i++)
            {
                ShaderInterface_UniformBlock first = blocks[i];
                for (int j = i + 1; j < blocks.Length; j++)
                {
                    ShaderInterface_UniformBlock other = blocks[j];
                    if (other.Set != first.Set || other.Binding != first.Binding) continue;
                    if (!string.Equals(first.BlockName, other.BlockName, StringComparison.Ordinal))
                    {
                        if (collector is null) return false;
                        ok = false; collector.Add(new ShaderContractViolation(Rule_V6b_BlockStagesAgree, first.BlockName, $"{first.Stages}: block '{first.BlockName}'", $"{other.Stages}: block '{other.BlockName}'", $"different block names at set {first.Set} binding {first.Binding}"));
                    }
                    if (first.SizeBytes != other.SizeBytes)
                    {
                        if (collector is null) return false;
                        ok = false; collector.Add(new ShaderContractViolation(Rule_V6b_BlockStagesAgree, first.BlockName, $"{first.Stages}: {first.SizeBytes} bytes", $"{other.Stages}: {other.SizeBytes} bytes", $"different std140 sizes at set {first.Set} binding {first.Binding}"));
                    }
                    foreach (ref readonly ShaderInterface_UniformMember m in first.Members.AsSpan())
                    {
                        int oi = IndexOfMember(other.Members, m.Name);
                        if (oi < 0)
                        {
                            if (collector is null) return false;
                            ok = false; collector.Add(new ShaderContractViolation(Rule_V6b_BlockStagesAgree, $"{first.BlockName}.{m.Name}", $"{first.Stages}: {m.GlslType} @ {m.OffsetBytes}", $"{other.Stages}: ABSENT", $"member missing in one stage at set {first.Set} binding {first.Binding}"));
                        }
                        else if (other.Members[oi].OffsetBytes != m.OffsetBytes || other.Members[oi].SizeBytes != m.SizeBytes)
                        {
                            if (collector is null) return false;
                            ref readonly ShaderInterface_UniformMember om = ref other.Members[oi];
                            ok = false; collector.Add(new ShaderContractViolation(Rule_V6b_BlockStagesAgree, $"{first.BlockName}.{m.Name}", $"{first.Stages}: {m.GlslType} @ {m.OffsetBytes} ({m.SizeBytes} B)", $"{other.Stages}: {om.GlslType} @ {om.OffsetBytes} ({om.SizeBytes} B)", $"member placement differs at set {first.Set} binding {first.Binding}"));
                        }
                    }
                    foreach (ref readonly ShaderInterface_UniformMember om in other.Members.AsSpan())
                    {
                        if (IndexOfMember(first.Members, om.Name) >= 0) continue;
                        if (collector is null) return false;
                        ok = false; collector.Add(new ShaderContractViolation(Rule_V6b_BlockStagesAgree, $"{first.BlockName}.{om.Name}", $"{first.Stages}: ABSENT", $"{other.Stages}: {om.GlslType} @ {om.OffsetBytes}", $"member missing in one stage at set {first.Set} binding {first.Binding}"));
                    }
                }
            }
            return ok;
        }

        /// <summary>V6c (V10): when two stages declare a storage block at (set, binding), name, read-only, fixed size, runtime-array stride and member names/placement must agree.</summary>
        private static bool CheckStorageBlocksAgreeAcrossStages(ShaderInterface_StorageBlock[] blocks, List<ShaderContractViolation> collector)
        {
            bool ok = true;
            for (int i = 0; i < blocks.Length; i++)
            {
                ShaderInterface_StorageBlock first = blocks[i];
                for (int j = i + 1; j < blocks.Length; j++)
                {
                    ShaderInterface_StorageBlock other = blocks[j];
                    if (other.Set != first.Set || other.Binding != first.Binding) continue;
                    if (!string.Equals(first.BlockName, other.BlockName, StringComparison.Ordinal))
                    {
                        if (collector is null) return false;
                        ok = false; collector.Add(new ShaderContractViolation(Rule_V6c_StorageStagesAgree, first.BlockName, $"{first.Stages}: block '{first.BlockName}'", $"{other.Stages}: block '{other.BlockName}'", $"different storage block names at set {first.Set} binding {first.Binding}"));
                    }
                    if (first.ReadOnly != other.ReadOnly)
                    {
                        if (collector is null) return false;
                        ok = false; collector.Add(new ShaderContractViolation(Rule_V6c_StorageStagesAgree, first.BlockName, $"{first.Stages}: {(first.ReadOnly ? "readonly" : "read-write")}", $"{other.Stages}: {(other.ReadOnly ? "readonly" : "read-write")}", $"storage access differs at set {first.Set} binding {first.Binding} - one resource kind per binding"));
                    }
                    if (first.FixedSizeBytes != other.FixedSizeBytes || first.HasRuntimeArray != other.HasRuntimeArray || first.RuntimeArrayStrideBytes != other.RuntimeArrayStrideBytes)
                    {
                        if (collector is null) return false;
                        ok = false; collector.Add(new ShaderContractViolation(Rule_V6c_StorageStagesAgree, first.BlockName, $"{first.Stages}: fixed {first.FixedSizeBytes} B, array {(first.HasRuntimeArray ? first.RuntimeArrayElementType + " stride " + first.RuntimeArrayStrideBytes : "none")}", $"{other.Stages}: fixed {other.FixedSizeBytes} B, array {(other.HasRuntimeArray ? other.RuntimeArrayElementType + " stride " + other.RuntimeArrayStrideBytes : "none")}", $"storage layout differs at set {first.Set} binding {first.Binding}"));
                    }
                    // Fixed members and runtime-array element members are one name space here (as before); search both arrays of the other side.
                    if (!CheckStorageMembersAgree(first, first.Members, other, collector)) { if (collector is null) return false; ok = false; }
                    if (!CheckStorageMembersAgree(first, first.RuntimeArrayElementMembers, other, collector)) { if (collector is null) return false; ok = false; }
                }
            }
            return ok;
        }

        private static bool CheckStorageMembersAgree(ShaderInterface_StorageBlock first, ShaderInterface_UniformMember[] firstMembers, ShaderInterface_StorageBlock other, List<ShaderContractViolation> collector)
        {
            bool ok = true;
            foreach (ref readonly ShaderInterface_UniformMember m in firstMembers.AsSpan())
            {
                int oi = IndexOfMember(other.Members, m.Name);
                ShaderInterface_UniformMember[] otherArray = other.Members;
                if (oi < 0) { oi = IndexOfMember(other.RuntimeArrayElementMembers, m.Name); otherArray = other.RuntimeArrayElementMembers; }
                if (oi < 0)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V6c_StorageStagesAgree, $"{first.BlockName}.{m.Name}", $"{first.Stages}: {m.GlslType} @ {m.OffsetBytes}", $"{other.Stages}: ABSENT", $"member missing in one stage at set {first.Set} binding {first.Binding}"));
                    continue;
                }
                ref readonly ShaderInterface_UniformMember om = ref otherArray[oi];
                if (om.OffsetBytes != m.OffsetBytes || om.SizeBytes != m.SizeBytes)
                {
                    if (collector is null) return false;
                    ok = false; collector.Add(new ShaderContractViolation(Rule_V6c_StorageStagesAgree, $"{first.BlockName}.{m.Name}", $"{first.Stages}: {m.GlslType} @ {m.OffsetBytes} ({m.SizeBytes} B)", $"{other.Stages}: {om.GlslType} @ {om.OffsetBytes} ({om.SizeBytes} B)", $"member placement differs at set {first.Set} binding {first.Binding}"));
                }
            }
            return ok;
        }

        // ───────────────────────────── allocation-free lookups and comparisons ─────────────────────────────

        private static int IndexOfVertexElement(VertexElementDescription[] compiled, string name)
        {
            for (int i = 0; i < compiled.Length; i++)
                if (string.Equals(compiled[i].Name, name, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static int IndexOfVarying(ShaderInterface_Varying[] varyings, string name)
        {
            for (int i = 0; i < varyings.Length; i++)
                if (string.Equals(varyings[i].Name, name, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static int IndexOfVaryingAtLocation(ShaderInterface_Varying[] varyings, uint location)
        {
            for (int i = 0; i < varyings.Length; i++)
                if (varyings[i].Location == location) return i;
            return -1;
        }

        private static int IndexOfDeclaredVarying(GlslDeclared_Varying[] declared, string name)
        {
            for (int i = 0; i < declared.Length; i++)
                if (string.Equals(declared[i].Name, name, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static int IndexOfDeclaredBlock(GlslDeclared_UniformBlock[] declared, string blockName)
        {
            for (int i = 0; i < declared.Length; i++)
                if (string.Equals(declared[i].BlockName, blockName, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static int IndexOfDeclaredStorageBlock(GlslDeclared_StorageBlock[] declared, string blockName)
        {
            for (int i = 0; i < declared.Length; i++)
                if (string.Equals(declared[i].BlockName, blockName, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static int IndexOfMember(ShaderInterface_UniformMember[] members, string name)
        {
            for (int i = 0; i < members.Length; i++)
                if (string.Equals(members[i].Name, name, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static ShaderInterface_UniformBlock FindUniformBlock(ShaderInterface_UniformBlock[] blocks, ShaderStages stage, string blockName)
        {
            foreach (ShaderInterface_UniformBlock b in blocks)
                if (b.Stages == stage && string.Equals(b.BlockName, blockName, StringComparison.Ordinal)) return b;
            return null;
        }

        private static ShaderInterface_StorageBlock FindStorageBlock(ShaderInterface_StorageBlock[] blocks, ShaderStages stage, string blockName)
        {
            foreach (ShaderInterface_StorageBlock b in blocks)
                if (b.Stages == stage && string.Equals(b.BlockName, blockName, StringComparison.Ordinal)) return b;
            return null;
        }

        /// <summary>Declared member names == compiled fixed member names followed by the runtime array name, in order. No allocation.</summary>
        private static bool StorageMemberNamesMatch(GlslDeclared_StorageBlock d, ShaderInterface_StorageBlock c)
        {
            int compiledCount = c.Members.Length + (c.HasRuntimeArray ? 1 : 0);
            if (d.Members.Length != compiledCount) return false;
            for (int i = 0; i < c.Members.Length; i++)
                if (!string.Equals(d.Members[i].Name, c.Members[i].Name, StringComparison.Ordinal)) return false;
            if (c.HasRuntimeArray && !string.Equals(d.Members[d.Members.Length - 1].Name, c.RuntimeArrayName, StringComparison.Ordinal)) return false;
            return true;
        }

        // Diagnostic-pass-only formatting helpers (never reached while collector is null).
        private static string JoinDeclaredMemberNames(GlslDeclared_StorageBlock d)
        {
            var names = new string[d.Members.Length];
            for (int i = 0; i < names.Length; i++) names[i] = d.Members[i].Name;
            return string.Join(", ", names);
        }

        private static string JoinCompiledStorageMemberNames(ShaderInterface_StorageBlock c)
        {
            var names = new List<string>(c.Members.Length + 1);
            foreach (ref readonly ShaderInterface_UniformMember m in c.Members.AsSpan()) names.Add(m.Name);
            if (c.HasRuntimeArray) names.Add(c.RuntimeArrayName);
            return string.Join(", ", names);
        }

        /// <summary>
        /// Structural comparison of a declared GLSL type (<c>vec3</c>, <c>flat ivec2</c> minus the qualifier, <c>mat3x4</c>, <c>float16_t</c>) against a
        /// compiled varying's (Scalar, VecSize, Columns, ArrayLength) - what <see cref="ShaderInterface_Varying.TypeText"/> would spell, compared
        /// WITHOUT building the string. Unknown declared spellings compare unequal (reported by the diagnostic pass with both spellings).
        /// </summary>
        private static bool DeclaredTypeMatchesCompiled(string glslType, uint declaredArrayLength, in ShaderInterface_Varying c)
        {
            if (!TryParseGlslVaryingType(glslType, out ShaderInterface_ScalarType scalar, out uint vecSize, out uint columns)) return false;
            return scalar == c.Scalar && vecSize == c.VecSize && columns == c.Columns && declaredArrayLength == c.ArrayLength;
        }

        private static bool TryParseGlslVaryingType(string t, out ShaderInterface_ScalarType scalar, out uint vecSize, out uint columns)
        {
            scalar = ShaderInterface_ScalarType.Float; vecSize = 1; columns = 1;
            switch (t)
            {
                case "float": return true;
                case "int": scalar = ShaderInterface_ScalarType.Int; return true;
                case "uint": scalar = ShaderInterface_ScalarType.UInt; return true;
                case "bool": scalar = ShaderInterface_ScalarType.Bool; return true;
                case "double": scalar = ShaderInterface_ScalarType.Double; return true;
                case "float16_t": scalar = ShaderInterface_ScalarType.Half; return true;
            }
            int p = 0;
            if (t.StartsWith("ivec", StringComparison.Ordinal) || t.StartsWith("imat", StringComparison.Ordinal)) { scalar = ShaderInterface_ScalarType.Int; p = 1; }
            else if (t.StartsWith("uvec", StringComparison.Ordinal) || t.StartsWith("umat", StringComparison.Ordinal)) { scalar = ShaderInterface_ScalarType.UInt; p = 1; }
            else if (t.StartsWith("bvec", StringComparison.Ordinal)) { scalar = ShaderInterface_ScalarType.Bool; p = 1; }
            else if (t.StartsWith("dvec", StringComparison.Ordinal) || t.StartsWith("dmat", StringComparison.Ordinal)) { scalar = ShaderInterface_ScalarType.Double; p = 1; }
            // vecN
            if (t.Length == p + 4 && string.CompareOrdinal(t, p, "vec", 0, 3) == 0 && IsDigit2to4(t[p + 3]))
            {
                vecSize = (uint)(t[p + 3] - '0'); return true;
            }
            // matN  or  matNxM  (TypeText spells Columns then VecSize: mat{Columns}x{VecSize})
            if (t.Length >= p + 4 && string.CompareOrdinal(t, p, "mat", 0, 3) == 0 && IsDigit2to4(t[p + 3]))
            {
                columns = (uint)(t[p + 3] - '0');
                if (t.Length == p + 4) { vecSize = columns; return true; }
                if (t.Length == p + 6 && t[p + 4] == 'x' && IsDigit2to4(t[p + 5])) { vecSize = (uint)(t[p + 5] - '0'); return true; }
            }
            return false;
        }

        private static bool IsDigit2to4(char ch) => ch >= '2' && ch <= '4';
    }
}