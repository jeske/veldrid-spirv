using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Veldrid.SPIRV
{
    /// <summary>One GLSL vertex+fragment pair that becomes one .vdshader bundle. File names are relative to the source directory.</summary>
    public readonly record struct ShaderBundle_SourcePair(string BundleName, string VertexFile, string FragmentFile);

    /// <summary>
    /// The identity of a bundle's INPUTS: bundle name, source file names, normalised source text, the compiler version and the bundle
    /// format. Two equal hashes mean the cached bundle is exactly what a fresh compile would produce (the compiler is bit-reproducible).
    /// </summary>
    public readonly record struct ShaderBundle_InputHash(string Hex)
    {
        public override string ToString() => Hex;
    }

    /// <summary>
    /// How a GLSL source FILE becomes the text the compiler sees. The one per-consumer policy the pipeline accepts (where sources live and
    /// how they include each other); everything after it - compile, verify, hash, serialise, cache - is the pipeline's. Default:
    /// <see cref="ShaderBundlePipeline.DefaultSourceReader"/>. RenderKit passes its transitive <c>//#include</c> expander with search paths.
    /// </summary>
    public delegate string ShaderBundle_SourceReader(string sourceFilePath);

    /// <summary>
    /// THE one path from GLSL to a cached .vdshader (AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md, D1):
    /// compile (D3: declared interface preserved) -> verify (V5b/V6b, exception on violation) -> serialise -> cache by input hash.
    /// A build tool (committed <c>Shaders/Compiled/</c>, embedded as resources) and a runtime source (a per-user cache directory)
    /// call the SAME <see cref="GetOrBuild"/>; the only differences are WHEN it runs and WHERE the files live. A bundle produced at
    /// build time and one produced at runtime from the same sources are byte-identical.
    /// </summary>
    public static class ShaderBundlePipeline
    {
        /// <summary>Hash manifest written beside the bundles: one <c>&lt;BundleName&gt; &lt;hash&gt;</c> line per bundle (the historical name, kept).</summary>
        public const string HashManifestFileName = "shaderinputhashes.manifest";

        /// <summary>Folded into every input hash: the SAME GLSL compiled by a different Veldrid.SPIRV or stored in a different bundle format must never match a cached bundle.</summary>
        public static readonly string CompilerVersionTag =
            "spirv=" + (typeof(ShaderBundleCompiler).Assembly.GetName().Version?.ToString() ?? "unknown") +
            ";bundle=" + (typeof(VeldridShaderBundle).Assembly.GetName().Version?.ToString() ?? "unknown") +
            ";native-abi=" + NativeAbiVersion.Expected;

        /// <summary>Compile + verify. Throws <see cref="ShaderInterfaceContractException"/> on a contract violation, <see cref="SpirvCompilationException"/> on a compile error.</summary>
        public static VeldridShaderBundle BuildBundle(string vertexGlsl, string fragmentGlsl, string bundleName, string vertexFile, string fragmentFile, CrossCompileOptions options = null)
            => ShaderBundleCompiler.CompileVertexFragment(vertexGlsl, fragmentGlsl, bundleName, options, vertexFile, fragmentFile);

        /// <summary>The input hash of a pair from its already-read sources (line endings normalised so git checkouts agree).</summary>
        public static ShaderBundle_InputHash ComputeInputHash(ShaderBundle_SourcePair pair, string vertexGlsl, string fragmentGlsl)
        {
            var sb = new StringBuilder();
            sb.Append(pair.BundleName).Append('|').Append(pair.VertexFile).Append('|').Append(pair.FragmentFile).Append('\n');
            sb.Append(NormaliseLineEndings(vertexGlsl)).Append('\n');
            sb.Append(NormaliseLineEndings(fragmentGlsl)).Append('\n');
            sb.Append("|compiler=").Append(CompilerVersionTag);
            return new ShaderBundle_InputHash(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant());
        }

        /// <summary>Path of the bundle file for <paramref name="bundleName"/> in <paramref name="cacheDir"/>.</summary>
        public static string BundlePath(string cacheDir, string bundleName) => Path.Combine(cacheDir, bundleName + ".vdshader");

        /// <summary>
        /// Return the cached bundle for <paramref name="pair"/>, compiling and caching it first if the sources, compiler or format changed
        /// (or the file is missing). Returns the bundle file path. <paramref name="log"/> receives one human line per decision.
        /// </summary>
        public static string GetOrBuild(string sourceDir, string cacheDir, ShaderBundle_SourcePair pair, Action<string> log = null, CrossCompileOptions options = null, ShaderBundle_SourceReader readSource = null)
        {
            Directory.CreateDirectory(cacheDir);
            var manifest = ReadHashManifest(cacheDir);
            string path = GetOrBuildCore(sourceDir, cacheDir, pair, manifest, log, options, readSource, out _);
            WriteHashManifest(cacheDir, manifest);
            return path;
        }

        /// <summary>
        /// Every <see cref="CompileShaders"/>-style tool in one call: build what is stale, keep what is current, optionally delete .vdshader
        /// files that no pair produces, rewrite the hash manifest in pair order. Returns the number of bundles compiled.
        /// </summary>
        public static int GetOrBuildAll(string sourceDir, string cacheDir, IReadOnlyList<ShaderBundle_SourcePair> pairs, bool deleteOrphans, Action<string> log = null, CrossCompileOptions options = null, ShaderBundle_SourceReader readSource = null)
        {
            Directory.CreateDirectory(cacheDir);
            var manifest = ReadHashManifest(cacheDir);
            int compiled = 0;
            var fresh = new Dictionary<string, ShaderBundle_InputHash>(StringComparer.Ordinal);
            foreach (var pair in pairs)
            {
                GetOrBuildCore(sourceDir, cacheDir, pair, manifest, log, options, readSource, out bool didCompile);
                if (didCompile) compiled++;
                fresh[pair.BundleName] = manifest[pair.BundleName];
            }
            if (deleteOrphans)
            {
                var known = new HashSet<string>(pairs.Select(p => p.BundleName), StringComparer.Ordinal);
                foreach (string file in Directory.EnumerateFiles(cacheDir, "*.vdshader"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (known.Contains(name)) continue;
                    File.Delete(file);
                    log?.Invoke($"  deleted orphan: {Path.GetFileName(file)}");
                }
            }
            WriteHashManifest(cacheDir, fresh, pairs.Select(p => p.BundleName).ToList());
            return compiled;
        }

        private static string GetOrBuildCore(string sourceDir, string cacheDir, ShaderBundle_SourcePair pair, Dictionary<string, ShaderBundle_InputHash> manifest, Action<string> log, CrossCompileOptions options, ShaderBundle_SourceReader readSource, out bool didCompile)
        {
            string vertexPath = Path.Combine(sourceDir, pair.VertexFile);
            string fragmentPath = Path.Combine(sourceDir, pair.FragmentFile);
            if (!File.Exists(vertexPath)) throw new FileNotFoundException($"Vertex shader source for bundle '{pair.BundleName}' not found", vertexPath);
            if (!File.Exists(fragmentPath)) throw new FileNotFoundException($"Fragment shader source for bundle '{pair.BundleName}' not found", fragmentPath);
            readSource ??= DefaultSourceReader;
            string vertexGlsl = readSource(vertexPath);
            string fragmentGlsl = readSource(fragmentPath);
            ShaderBundle_InputHash hash = ComputeInputHash(pair, vertexGlsl, fragmentGlsl);
            string bundlePath = BundlePath(cacheDir, pair.BundleName);

            if (File.Exists(bundlePath) && manifest.TryGetValue(pair.BundleName, out var stored) && stored == hash)
            {
                log?.Invoke($"  up-to-date: {pair.BundleName}.vdshader");
                didCompile = false;
                return bundlePath;
            }

            log?.Invoke($"  compiling:  {pair.BundleName} ({pair.VertexFile} + {pair.FragmentFile})");
            VeldridShaderBundle bundle = BuildBundle(vertexGlsl, fragmentGlsl, pair.BundleName, pair.VertexFile, pair.FragmentFile, options);
            CheckAndWriteInterfaceSnapshot(cacheDir, pair, vertexGlsl, fragmentGlsl, bundle, log);
            bundle.SerializeToFile(bundlePath);
            manifest[pair.BundleName] = hash;
            log?.Invoke($"    -> {pair.BundleName}.vdshader ({new FileInfo(bundlePath).Length} bytes; layouts={bundle.ResourceLayoutDescriptions?.Length ?? 0}; backends={string.Join(", ", bundle.Backends.Keys)})");
            didCompile = true;
            return bundlePath;
        }

        /// <summary>Rule id for a snapshot mismatch: the compiled interface changed while the sources did not - only the toolchain can do that.</summary>
        public const string Rule_V8_InterfaceSnapshot = "V8";

        /// <summary>Path of the interface snapshot written beside a bundle (commit it with the bundle).</summary>
        public static string InterfaceSnapshotPath(string cacheDir, string bundleName) => Path.Combine(cacheDir, bundleName + ".interface.json");

        /// <summary>Hash of the SOURCES only (no compiler version) - the key that tells "the source changed" from "the compiler changed".</summary>
        public static string ComputeSourceOnlyHash(ShaderBundle_SourcePair pair, string vertexGlsl, string fragmentGlsl)
        {
            var sb = new StringBuilder();
            sb.Append(pair.BundleName).Append('|').Append(pair.VertexFile).Append('|').Append(pair.FragmentFile).Append('\n');
            sb.Append(NormaliseLineEndings(vertexGlsl)).Append('\n').Append(NormaliseLineEndings(fragmentGlsl));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        }

        private sealed class InterfaceSnapshot
        {
            public string SourceHash { get; set; }
            public string CompilerVersionTag { get; set; }
            public VdShaderInterface Interface { get; set; }
            public VdShaderResourceLayout[] ResourceLayoutDescriptions { get; set; }
        }

        private static readonly System.Text.Json.JsonSerializerOptions SnapshotJson = new() { WriteIndented = true };

        // D0 / S2c: a committed record of the compiled interface. If the sources are byte-identical to the snapshot's but the compiled
        // interface differs, a toolchain change (compiler upgrade, pass-list drift, cross-compiler behaviour) moved something that
        // declared-vs-compiled (V4/V5a/V6) does not model - e.g. resource layout slots, vertex formats. That is a hard fail, with a diff.
        private static void CheckAndWriteInterfaceSnapshot(string cacheDir, ShaderBundle_SourcePair pair, string vertexGlsl, string fragmentGlsl, VeldridShaderBundle bundle, Action<string> log)
        {
            string path = InterfaceSnapshotPath(cacheDir, pair.BundleName);
            string sourceHash = ComputeSourceOnlyHash(pair, vertexGlsl, fragmentGlsl);
            string interfaceJson = System.Text.Json.JsonSerializer.Serialize(bundle.Interface, SnapshotJson);
            string layoutsJson = System.Text.Json.JsonSerializer.Serialize(bundle.ResourceLayoutDescriptions, SnapshotJson);
            if (File.Exists(path))
            {
                InterfaceSnapshot previous = null;
                try { previous = System.Text.Json.JsonSerializer.Deserialize<InterfaceSnapshot>(File.ReadAllText(path), SnapshotJson); } catch { /* unreadable snapshot: rewrite */ }
                if (previous?.SourceHash == sourceHash)
                {
                    string prevInterface = System.Text.Json.JsonSerializer.Serialize(previous.Interface, SnapshotJson);
                    string prevLayouts = System.Text.Json.JsonSerializer.Serialize(previous.ResourceLayoutDescriptions, SnapshotJson);
                    if (prevInterface != interfaceJson || prevLayouts != layoutsJson)
                    {
                        throw new ShaderInterfaceContractException(pair.BundleName, new[]
                        {
                            new ShaderContractViolation(Rule_V8_InterfaceSnapshot, pair.BundleName,
                                $"snapshot (compiler {previous.CompilerVersionTag})", $"now (compiler {CompilerVersionTag})",
                                $"the GLSL did not change but the compiled interface did - a toolchain change moved something. Diff {Path.GetFileName(path)} against the new output; if the change is intended, delete the snapshot and recompile."),
                        });
                    }
                }
            }
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new InterfaceSnapshot { SourceHash = sourceHash, CompilerVersionTag = CompilerVersionTag, Interface = bundle.Interface, ResourceLayoutDescriptions = bundle.ResourceLayoutDescriptions }, SnapshotJson));
            log?.Invoke($"    -> {Path.GetFileName(path)}");
        }

        /// <summary>Per-user runtime cache location for an application: <c>%LOCALAPPDATA%/ArtificialNecessity/ShaderCache/&lt;appName&gt;</c>.</summary>
        public static string DefaultRuntimeCacheDir(string appName)
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtificialNecessity", "ShaderCache", appName);

        /// <summary>The default <see cref="ShaderBundle_SourceReader"/>: UTF-8 text with one-level <c>//#include</c> resolved against the file's directory.</summary>
        public static string DefaultSourceReader(string sourceFilePath)
            => ResolveIncludes(File.ReadAllText(sourceFilePath, Encoding.UTF8), Path.GetDirectoryName(sourceFilePath) ?? ".");

        /// <summary>
        /// The one source-level preprocessor the pipeline supports (SilkyNvg's convention): a line <c>//#include "file"</c> is replaced by the
        /// contents of <c>sourceDir/file</c>. One level only (no nesting), so a shared block of GLSL can live in one file. Hashing is over
        /// the RESOLVED text, so a change in an included file rebuilds every bundle that includes it.
        /// </summary>
        public static string ResolveIncludes(string glslSource, string sourceDir)
        {
            if (glslSource.IndexOf("//#include", StringComparison.Ordinal) < 0) return glslSource;
            return System.Text.RegularExpressions.Regex.Replace(
                glslSource,
                "^//#include\\s+\"(?<file>[^\"]+)\"\\s*$",
                match =>
                {
                    string includePath = Path.Combine(sourceDir, match.Groups["file"].Value);
                    if (!File.Exists(includePath)) throw new FileNotFoundException($"//#include not found: {includePath}", includePath);
                    return File.ReadAllText(includePath, Encoding.UTF8);
                },
                System.Text.RegularExpressions.RegexOptions.Multiline);
        }

        private static string NormaliseLineEndings(string text) => text.Replace("\r\n", "\n");

        private static Dictionary<string, ShaderBundle_InputHash> ReadHashManifest(string cacheDir)
        {
            var result = new Dictionary<string, ShaderBundle_InputHash>(StringComparer.Ordinal);
            string path = Path.Combine(cacheDir, HashManifestFileName);
            if (!File.Exists(path)) return result;
            foreach (string line in File.ReadAllLines(path))
            {
                string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2) result[parts[0]] = new ShaderBundle_InputHash(parts[1].ToLowerInvariant());
            }
            return result;
        }

        private static void WriteHashManifest(string cacheDir, Dictionary<string, ShaderBundle_InputHash> manifest, IReadOnlyList<string> order = null)
        {
            var names = order ?? manifest.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            var sb = new StringBuilder();
            foreach (string name in names)
                if (manifest.TryGetValue(name, out var hash)) sb.Append(name).Append(' ').Append(hash.Hex).Append('\n');
            File.WriteAllText(Path.Combine(cacheDir, HashManifestFileName), sb.ToString());
        }
    }
}