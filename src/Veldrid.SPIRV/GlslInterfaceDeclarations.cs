using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Veldrid.SPIRV
{
    /// <summary>A stage input/output as the GLSL SOURCE declares it: <c>layout(location = N) [flat] in|out TYPE name[N];</c>.</summary>
    public readonly record struct GlslDeclared_Varying(string Name, uint Location, string GlslType, bool Flat, uint ArrayLength);

    /// <summary>A block member as declared: <c>TYPE name[N];</c> in declaration order. <paramref name="IsRuntimeArray"/> = <c>TYPE name[];</c> (V10, storage blocks only).</summary>
    public readonly record struct GlslDeclared_Member(string Name, string GlslType, uint ArrayLength, bool IsRuntimeArray = false);

    /// <summary>A uniform block as declared: <c>layout(set = S, binding = B[, std140]) uniform Name { members } [instance];</c>.</summary>
    public sealed record GlslDeclared_UniformBlock(string BlockName, uint Set, uint Binding, GlslDeclared_Member[] Members);

    /// <summary>V10: a storage block as declared: <c>layout(std430, set = S, binding = B) [readonly] buffer Name { members } [instance];</c>.</summary>
    public sealed record GlslDeclared_StorageBlock(string BlockName, uint Set, uint Binding, bool ReadOnly, GlslDeclared_Member[] Members);

    /// <summary>An opaque resource as declared: <c>layout(set = S, binding = B) uniform texture2D|sampler|... name;</c>.</summary>
    public readonly record struct GlslDeclared_Resource(string Name, uint Set, uint Binding, string GlslType);

    /// <summary>
    /// What ONE GLSL stage declares at its interface, read from the source text (comments stripped; the text is the include-resolved text the
    /// compiler received). This is the "declared" side of V4 / V5a / V6: the compiled reflection must match it BY NAME - if the toolchain
    /// moved, dropped, renumbered or re-laid-out anything, the two disagree and the compile fails (D0: a toolchain defect is a detected hard
    /// fail, never a visual). Deliberately a small parser: explicit layout qualifiers only, no preprocessor evaluation (#define'd numbers
    /// inside layout(...) are not understood - such a line is reported as unparseable, which is itself a violation).
    /// </summary>
    public sealed record GlslInterfaceDeclarations(
        GlslDeclared_Varying[] Inputs,
        GlslDeclared_Varying[] Outputs,
        GlslDeclared_UniformBlock[] UniformBlocks,
        GlslDeclared_Resource[] Resources,
        string[] UnparseableLayoutLines,
        GlslDeclared_StorageBlock[] StorageBlocks)
    {
        private static readonly Regex CommentRegex = new(@"//[^\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);
        // layout(...) qualifier list, then the declaration up to ';' or '{'. Block bodies are handled separately.
        private static readonly Regex LayoutDeclRegex = new(@"layout\s*\((?<quals>[^)]*)\)\s*(?<decl>[^;{]*?)\s*(?<term>[;{])", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex VaryingDeclRegex = new(@"^(?:(?<flat>flat)\s+)?(?:(?:smooth|noperspective|centroid|sample|precise|highp|mediump|lowp)\s+)*(?<dir>in|out)\s+(?:(?:highp|mediump|lowp)\s+)?(?<type>\w+)\s+(?<name>\w+)\s*(?:\[\s*(?<arr>\d+)\s*\])?$", RegexOptions.Compiled);
        private static readonly Regex ResourceDeclRegex = new(@"^uniform\s+(?:(?:highp|mediump|lowp)\s+)?(?<type>\w+)\s+(?<name>\w+)\s*(?:\[\s*(?<arr>\d+)\s*\])?$", RegexOptions.Compiled);
        private static readonly Regex BlockDeclRegex = new(@"^uniform\s+(?<name>\w+)$", RegexOptions.Compiled);
        // V10: `[readonly] [restrict|coherent|volatile|writeonly]* buffer Name`
        private static readonly Regex StorageBlockDeclRegex = new(@"^(?:(?<ro>readonly)\s+)?(?:(?:restrict|coherent|volatile|writeonly|readonly)\s+)*buffer\s+(?<name>\w+)$", RegexOptions.Compiled);
        // A member; `T Name[];` (empty brackets) is a runtime array - legal only as the LAST member of a buffer block.
        private static readonly Regex MemberRegex = new(@"(?:(?:highp|mediump|lowp)\s+)?(?<type>\w+)\s+(?<name>\w+)\s*(?:\[\s*(?<arr>\d*)\s*\])?\s*;", RegexOptions.Compiled);
        private static readonly Regex QualRegex = new(@"(?<key>\w+)\s*=\s*(?<val>\d+)", RegexOptions.Compiled);

        /// <summary>Parse one stage's GLSL. Never throws on odd input; unparseable layout lines are returned for the verifier to report.</summary>
        public static GlslInterfaceDeclarations Parse(string glsl)
        {
            if (glsl is null) throw new ArgumentNullException(nameof(glsl));
            string text = CommentRegex.Replace(glsl, m => m.Value.Contains('\n') ? "\n" : " ");
            var inputs = new List<GlslDeclared_Varying>();
            var outputs = new List<GlslDeclared_Varying>();
            var blocks = new List<GlslDeclared_UniformBlock>();
            var storageBlocks = new List<GlslDeclared_StorageBlock>();
            var resources = new List<GlslDeclared_Resource>();
            var unparseable = new List<string>();

            foreach (Match m in LayoutDeclRegex.Matches(text))
            {
                var quals = new Dictionary<string, uint>(StringComparer.Ordinal);
                foreach (Match q in QualRegex.Matches(m.Groups["quals"].Value)) quals[q.Groups["key"].Value] = uint.Parse(q.Groups["val"].Value);
                string decl = Regex.Replace(m.Groups["decl"].Value.Trim(), @"\s+", " ");
                string raw = ("layout(" + m.Groups["quals"].Value + ") " + decl).Trim();

                if (m.Groups["term"].Value == "{")
                {
                    // A block: uniform Name { ... } [instance];  or (V10)  [readonly] buffer Name { ... } [instance];   (in/out blocks are ignored)
                    var bm = BlockDeclRegex.Match(decl);
                    var sm = bm.Success ? null : StorageBlockDeclRegex.Match(decl);
                    if (!bm.Success && !(sm?.Success ?? false)) continue;   // e.g. 'in VertexData {' - not a block we check
                    if (!quals.TryGetValue("set", out uint set) || !quals.TryGetValue("binding", out uint binding)) { unparseable.Add(raw + " {"); continue; }
                    int bodyStart = m.Index + m.Length;
                    int bodyEnd = text.IndexOf('}', bodyStart);
                    if (bodyEnd < 0) { unparseable.Add(raw + " {"); continue; }
                    string body = text.Substring(bodyStart, bodyEnd - bodyStart);
                    var members = MemberRegex.Matches(body).Cast<Match>()
                        .Select(mm => new GlslDeclared_Member(mm.Groups["name"].Value, mm.Groups["type"].Value,
                            mm.Groups["arr"].Success && mm.Groups["arr"].Value.Length > 0 ? uint.Parse(mm.Groups["arr"].Value) : 0u,
                            IsRuntimeArray: mm.Groups["arr"].Success && mm.Groups["arr"].Value.Length == 0))
                        .ToArray();
                    if (bm.Success)
                        blocks.Add(new GlslDeclared_UniformBlock(bm.Groups["name"].Value, set, binding, members));
                    else
                        storageBlocks.Add(new GlslDeclared_StorageBlock(sm.Groups["name"].Value, set, binding, sm.Groups["ro"].Success, members));
                    continue;
                }

                var vm = VaryingDeclRegex.Match(decl);
                if (vm.Success)
                {
                    // A redeclared BUILT-IN is not an interface varying: 'layout(depth_less) out float gl_FragDepth;' (conservative depth),
                    // 'layout(origin_upper_left) in vec4 gl_FragCoord;'. Built-ins never appear in the compiled stage_inputs/stage_outputs
                    // either (ReflectVaryings), so there is nothing to match by name - skip, never report as unparseable.
                    if (vm.Groups["name"].Value.StartsWith("gl_", StringComparison.Ordinal)) continue;
                    if (!quals.TryGetValue("location", out uint location)) { unparseable.Add(raw); continue; }
                    var v = new GlslDeclared_Varying(vm.Groups["name"].Value, location, vm.Groups["type"].Value, vm.Groups["flat"].Success, vm.Groups["arr"].Success ? uint.Parse(vm.Groups["arr"].Value) : 0u);
                    (vm.Groups["dir"].Value == "in" ? inputs : outputs).Add(v);
                    continue;
                }

                var rm = ResourceDeclRegex.Match(decl);
                if (rm.Success)
                {
                    if (!quals.TryGetValue("set", out uint set) || !quals.TryGetValue("binding", out uint binding)) { unparseable.Add(raw); continue; }
                    resources.Add(new GlslDeclared_Resource(rm.Groups["name"].Value, set, binding, rm.Groups["type"].Value));
                    continue;
                }

                // Other layout() uses (fragment outputs 'layout(location = 0) out vec4 o' are varyings above; 'layout(local_size_x = ..) in;',
                // 'layout(push_constant)', 'layout(constant_id = N) const' etc.) are not interface declarations we check.
                if (decl.Length == 0 || decl.StartsWith("const ", StringComparison.Ordinal) || quals.ContainsKey("local_size_x") || quals.ContainsKey("constant_id")) continue;
                unparseable.Add(raw);
            }

            return new GlslInterfaceDeclarations(inputs.ToArray(), outputs.ToArray(), blocks.ToArray(), resources.ToArray(), unparseable.ToArray(), storageBlocks.ToArray());
        }

        /// <summary>
        /// std140 layout of a declared block computed INDEPENDENTLY of the compiler - the oracle V6 compares the compiled member offsets
        /// against. Supports scalars, vectors, matrices (column-major, as GLSL default) and arrays of those. Returns null for a member type it
        /// does not know (nested structs, double types): that member is then checked by name/order only.
        /// </summary>
        public static (uint Offset, uint Size)?[] Std140Layout(GlslDeclared_Member[] members, out uint blockSizeBytes)
        {
            var result = new (uint, uint)?[members.Length];
            uint cursor = 0;
            for (int i = 0; i < members.Length; i++)
            {
                if (!TryStd140(members[i].GlslType, out uint baseAlign, out uint size)) { result[i] = null; continue; }
                uint align = baseAlign, elemSize = size;
                if (members[i].ArrayLength > 0)
                {
                    // Array element base alignment/stride round up to vec4 (16) under std140.
                    align = Math.Max(16u, RoundUp(baseAlign, 16u));
                    elemSize = RoundUp(size, 16u) * members[i].ArrayLength;
                }
                cursor = RoundUp(cursor, align);
                result[i] = (cursor, elemSize);
                cursor += elemSize;
            }
            blockSizeBytes = RoundUp(cursor, 16u);
            return result;
        }

        private static uint RoundUp(uint value, uint multiple) => (value + multiple - 1) / multiple * multiple;

        private static bool TryStd140(string glslType, out uint baseAlign, out uint size)
        {
            baseAlign = size = 0;
            string t = glslType;
            string prefix = "";
            if (t.Length > 0 && (t[0] == 'i' || t[0] == 'u' || t[0] == 'b' || t[0] == 'd') && (t.StartsWith("ivec") || t.StartsWith("uvec") || t.StartsWith("bvec") || t.StartsWith("dvec") || t.StartsWith("dmat"))) { prefix = t[..1]; t = t[1..]; }
            uint scalar = prefix == "d" ? 8u : 4u;
            if (prefix == "d") return false;   // double layouts: not supported by this oracle
            switch (t)
            {
                case "float": case "int": case "uint": case "bool": baseAlign = size = 4; return true;
                case "vec2": baseAlign = size = 2 * scalar; return true;
                case "vec3": baseAlign = 4 * scalar; size = 3 * scalar; return true;
                case "vec4": baseAlign = size = 4 * scalar; return true;
                case "mat2": baseAlign = 16; size = 2 * 16; return true;    // 2 columns of vec2, each padded to vec4
                case "mat3": baseAlign = 16; size = 3 * 16; return true;    // 3 columns of vec3, each padded to vec4
                case "mat4": baseAlign = 16; size = 4 * 16; return true;
                case "mat2x3": case "mat2x4": baseAlign = 16; size = 2 * 16; return true;
                case "mat3x2": case "mat3x4": baseAlign = 16; size = 3 * 16; return true;
                case "mat4x2": case "mat4x3": baseAlign = 16; size = 4 * 16; return true;
                default: return false;
            }
        }

        /// <summary>Map a declared GLSL input type to the vertex format reflection reports for it (float/int/uint scalars and vectors). Null = not a vertex-attribute type.</summary>
        public static VertexElementFormat? VertexFormatOf(string glslType) => glslType switch
        {
            "float" => VertexElementFormat.Float1, "vec2" => VertexElementFormat.Float2, "vec3" => VertexElementFormat.Float3, "vec4" => VertexElementFormat.Float4,
            "int" => VertexElementFormat.Int1, "ivec2" => VertexElementFormat.Int2, "ivec3" => VertexElementFormat.Int3, "ivec4" => VertexElementFormat.Int4,
            "uint" => VertexElementFormat.UInt1, "uvec2" => VertexElementFormat.UInt2, "uvec3" => VertexElementFormat.UInt3, "uvec4" => VertexElementFormat.UInt4,
            _ => null,
        };
    }
}