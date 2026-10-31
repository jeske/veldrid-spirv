using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Veldrid.SPIRV.Tests
{
    /// <summary>D1: one compile -> verify -> cache path, shared by build tools and runtime; cached output is a pure function of the inputs.</summary>
    public class ShaderBundlePipelineTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "VeldridSpirvTests", Guid.NewGuid().ToString("N"));
        private string SourceDir => Path.Combine(_root, "Source");
        private string CacheDir => Path.Combine(_root, "Compiled");

        private const string Vert = "#version 450\nlayout(location = 0) in vec2 Position;\nlayout(location = 0) out vec2 vUv;\nvoid main() { vUv = Position; gl_Position = vec4(Position, 0, 1); }\n";
        private const string Frag = "#version 450\nlayout(location = 0) in vec2 vUv;\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(vUv, 0, 1); }\n";
        private static readonly ShaderBundle_SourcePair Pair = new("Quad", "quad.vert.glsl", "quad.frag.glsl");

        public ShaderBundlePipelineTests()
        {
            Directory.CreateDirectory(SourceDir);
            File.WriteAllText(Path.Combine(SourceDir, Pair.VertexFile), Vert);
            File.WriteAllText(Path.Combine(SourceDir, Pair.FragmentFile), Frag);
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        [Fact]
        public void GetOrBuild_CompilesOnce_ThenUpToDate_ThenRebuildsWhenSourceChanges()
        {
            var log = new System.Collections.Generic.List<string>();
            string path1 = ShaderBundlePipeline.GetOrBuild(SourceDir, CacheDir, Pair, log.Add);
            Assert.True(File.Exists(path1));
            Assert.Contains(log, l => l.Contains("compiling"));
            Assert.True(File.Exists(Path.Combine(CacheDir, ShaderBundlePipeline.HashManifestFileName)));

            log.Clear();
            string path2 = ShaderBundlePipeline.GetOrBuild(SourceDir, CacheDir, Pair, log.Add);
            Assert.Equal(path1, path2);
            Assert.Single(log);
            Assert.Contains("up-to-date", log[0]);

            File.WriteAllText(Path.Combine(SourceDir, Pair.FragmentFile), Frag.Replace("vec4(vUv, 0, 1)", "vec4(vUv, 1, 1)"));
            log.Clear();
            ShaderBundlePipeline.GetOrBuild(SourceDir, CacheDir, Pair, log.Add);
            Assert.Contains(log, l => l.Contains("compiling"));
        }

        [Fact]
        public void SameSources_ProduceByteIdenticalBundles_BuildTimeEqualsRuntime()
        {
            string a = ShaderBundlePipeline.GetOrBuild(SourceDir, Path.Combine(_root, "BuildTool"), Pair);
            string b = ShaderBundlePipeline.GetOrBuild(SourceDir, Path.Combine(_root, "RuntimeCache"), Pair);
            Assert.True(File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)), "a build-time bundle and a runtime bundle from the same sources must be byte-identical (D1)");
        }

        [Fact]
        public void InputHash_FoldsInCompilerVersion_AndNormalisesLineEndings()
        {
            var crlf = ShaderBundlePipeline.ComputeInputHash(Pair, Vert.Replace("\n", "\r\n"), Frag.Replace("\n", "\r\n"));
            var lf = ShaderBundlePipeline.ComputeInputHash(Pair, Vert, Frag);
            Assert.Equal(lf, crlf);
            Assert.Contains("spirv=", ShaderBundlePipeline.CompilerVersionTag);
            Assert.Contains("native-abi=2", ShaderBundlePipeline.CompilerVersionTag);
        }

        [Fact]
        public void GetOrBuildAll_DeletesOrphans_AndWritesManifestInPairOrder()
        {
            File.WriteAllText(Path.Combine(SourceDir, "b.vert.glsl"), Vert);
            File.WriteAllText(Path.Combine(SourceDir, "b.frag.glsl"), Frag);
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(Path.Combine(CacheDir, "Orphan.vdshader"), "{}");
            var pairs = new[] { new ShaderBundle_SourcePair("Zed", "b.vert.glsl", "b.frag.glsl"), Pair };

            int compiled = ShaderBundlePipeline.GetOrBuildAll(SourceDir, CacheDir, pairs, deleteOrphans: true);
            Assert.Equal(2, compiled);
            Assert.False(File.Exists(Path.Combine(CacheDir, "Orphan.vdshader")));
            string[] manifest = File.ReadAllLines(Path.Combine(CacheDir, ShaderBundlePipeline.HashManifestFileName));
            Assert.Equal(2, manifest.Length);
            Assert.StartsWith("Zed ", manifest[0]);
            Assert.StartsWith("Quad ", manifest[1]);
            Assert.Equal(0, ShaderBundlePipeline.GetOrBuildAll(SourceDir, CacheDir, pairs, deleteOrphans: true));
        }

        [Fact]
        public void InterfaceSnapshot_WrittenBesideBundle_AndToolchainDriftWithoutSourceChange_Throws_V8()
        {
            ShaderBundlePipeline.GetOrBuild(SourceDir, CacheDir, Pair);
            string snapshot = ShaderBundlePipeline.InterfaceSnapshotPath(CacheDir, Pair.BundleName);
            Assert.True(File.Exists(snapshot));
            Assert.Contains("\"vUv\"", File.ReadAllText(snapshot));

            // Simulate "the compiler changed what it produced for the same source": edit the recorded interface, keep the source hash,
            // then force a recompile (forget the hash manifest, as a compiler upgrade would by changing the compiler tag).
            File.WriteAllText(snapshot, File.ReadAllText(snapshot).Replace("\"location\": 0", "\"location\": 7"));
            File.Delete(Path.Combine(CacheDir, ShaderBundlePipeline.HashManifestFileName));
            var ex = Assert.Throws<ShaderInterfaceContractException>(() => ShaderBundlePipeline.GetOrBuild(SourceDir, CacheDir, Pair));
            Assert.Equal(ShaderBundlePipeline.Rule_V8_InterfaceSnapshot, ex.Violations[0].Rule);

            // A SOURCE change legitimately rewrites the snapshot.
            File.WriteAllText(Path.Combine(SourceDir, Pair.FragmentFile), Frag.Replace("vec4(vUv, 0, 1)", "vec4(vUv, 1, 1)"));
            ShaderBundlePipeline.GetOrBuild(SourceDir, CacheDir, Pair);
            Assert.Contains("\"location\": 0", File.ReadAllText(snapshot));
        }

        [Fact]
        public void GetOrBuild_ContractViolation_Throws_AndLeavesNoBundle()
        {
            File.WriteAllText(Path.Combine(SourceDir, Pair.FragmentFile), "#version 450\nlayout(location = 1) in vec2 vUv;\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(vUv, 0, 1); }\n");
            Assert.Throws<ShaderInterfaceContractException>(() => ShaderBundlePipeline.GetOrBuild(SourceDir, CacheDir, Pair));
            Assert.False(File.Exists(ShaderBundlePipeline.BundlePath(CacheDir, Pair.BundleName)));
        }

        [Fact]
        public void GetOrBuild_ResolvesOneLevelIncludes_AndRebuildsWhenTheIncludedFileChanges()
        {
            File.WriteAllText(Path.Combine(SourceDir, "common.glsl"), "vec4 tint(vec2 uv) { return vec4(uv, 0, 1); }\n");
            File.WriteAllText(Path.Combine(SourceDir, Pair.FragmentFile), "#version 450\nlayout(location = 0) in vec2 vUv;\nlayout(location = 0) out vec4 o;\n//#include \"common.glsl\"\nvoid main() { o = tint(vUv); }\n");
            var log = new System.Collections.Generic.List<string>();
            ShaderBundlePipeline.GetOrBuild(SourceDir, CacheDir, Pair, log.Add);
            Assert.Contains(log, l => l.Contains("compiling"));

            File.WriteAllText(Path.Combine(SourceDir, "common.glsl"), "vec4 tint(vec2 uv) { return vec4(uv, 1, 1); }\n");
            log.Clear();
            ShaderBundlePipeline.GetOrBuild(SourceDir, CacheDir, Pair, log.Add);
            Assert.Contains(log, l => l.Contains("compiling"));   // the include changed -> the bundle is stale
        }
    }
}