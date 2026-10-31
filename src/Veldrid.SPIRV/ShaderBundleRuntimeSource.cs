using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Veldrid.SPIRV
{
    /// <summary>
    /// Runtime shaders through the ONE pipeline (D1): <see cref="ShaderBundlePipeline.GetOrBuild"/> into a per-user cache, then
    /// <see cref="ResourceFactory.CreateFromBundle(VeldridShaderBundle, Func{string, byte[]}, string)"/>. Replaces every hand-rolled
    /// "compile GLSL at startup" class (Monsters RuntimeGlslShaderSource, TileMapKit ShaderLoader). Owns and disposes the shaders it creates.
    /// Thread-safe. A compile error or contract violation surfaces as the compiler's exception at first <see cref="Get"/> - never a warning.
    /// </summary>
    public sealed class ShaderBundleRuntimeSource : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly string _sourceDir;
        private readonly string _cacheDir;
        private readonly IReadOnlyDictionary<string, ShaderBundle_SourcePair> _known;
        private readonly Action<string> _log;
        private readonly CrossCompileOptions _options;
        private readonly Dictionary<string, PrecompiledShaderResult> _loaded = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        /// <param name="device">The device whose backend selects the bundle slot.</param>
        /// <param name="sourceDir">Directory holding the GLSL files named by <paramref name="known"/>.</param>
        /// <param name="cacheDir">Where .vdshader files and the hash manifest live (see <see cref="ShaderBundlePipeline.DefaultRuntimeCacheDir"/>).</param>
        /// <param name="known">Bundle name -> source pair. Only these names can be requested.</param>
        /// <param name="log">Optional line sink (Console.WriteLine).</param>
        /// <param name="options">Cross-compile options; null = defaults.</param>
        public ShaderBundleRuntimeSource(GraphicsDevice device, string sourceDir, string cacheDir, IReadOnlyDictionary<string, ShaderBundle_SourcePair> known, Action<string> log = null, CrossCompileOptions options = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _sourceDir = sourceDir ?? throw new ArgumentNullException(nameof(sourceDir));
            _cacheDir = cacheDir ?? throw new ArgumentNullException(nameof(cacheDir));
            _known = known ?? throw new ArgumentNullException(nameof(known));
            _log = log;
            _options = options;
            Directory.CreateDirectory(_cacheDir);
            _log?.Invoke($"[ShaderBundleRuntimeSource] sources {_sourceDir}; cache {_cacheDir}; backend {_device.BackendType}");
        }

        /// <summary>The bundle names this source can produce.</summary>
        public IEnumerable<string> KnownBundleNames => _known.Keys;

        /// <summary>Compile-or-load <paramref name="bundleName"/> and create its shaders. Cached per name for the life of this source.</summary>
        public PrecompiledShaderResult Get(string bundleName)
        {
            if (!_known.TryGetValue(bundleName, out var pair))
                throw new KeyNotFoundException($"Shader bundle '{bundleName}' is not known to this source. Known: [{string.Join(", ", _known.Keys.OrderBy(k => k, StringComparer.Ordinal))}].");

            lock (_lock)
            {
                if (_loaded.TryGetValue(bundleName, out var cached)) return cached;

                string bundlePath = ShaderBundlePipeline.GetOrBuild(_sourceDir, _cacheDir, pair, _log, _options);
                VeldridShaderBundle bundle = VeldridShaderBundle.DeserializeFromFile(bundlePath);
                if (!BundleHasLoadablePayloadForBackend(bundle, out string slotFormat))
                {
                    // A bundle hashed as current but whose slot for THIS backend would force a source compile at launch (an older
                    // 'hlsl_text' D3D11 slot) - rebuild it rather than pay that every start. The manifest entry is rewritten by GetOrBuild.
                    _log?.Invoke($"[ShaderBundleRuntimeSource] {bundleName}: {_device.BackendType} slot is '{slotFormat}', rebuilding");
                    File.Delete(bundlePath);
                    bundlePath = ShaderBundlePipeline.GetOrBuild(_sourceDir, _cacheDir, pair, _log, _options);
                    bundle = VeldridShaderBundle.DeserializeFromFile(bundlePath);
                }

                PrecompiledShaderResult result = _device.ResourceFactory.CreateFromBundle(bundle, ResolveExternalFile, _cacheDir);
                _loaded[bundleName] = result;
                _log?.Invoke($"[ShaderBundleRuntimeSource] loaded {bundleName} ({pair.VertexFile} + {pair.FragmentFile})");
                return result;
            }
        }

        private const string Direct3D11RequiredShaderFormat = "dxbc";

        private bool BundleHasLoadablePayloadForBackend(VeldridShaderBundle bundle, out string slotFormat)
        {
            string key = VeldridShaderBundle.GetBackendKey(_device.BackendType);
            if (!bundle.Backends.TryGetValue(key, out VdShaderBackendData data) || data == null) { slotFormat = "(missing)"; return false; }
            slotFormat = data.ShaderFormat ?? "(null)";
            if (_device.BackendType == GraphicsBackend.Direct3D11)
                return string.Equals(slotFormat, Direct3D11RequiredShaderFormat, StringComparison.OrdinalIgnoreCase);
            return true;
        }

        private static byte[] ResolveExternalFile(string relativePath)
            => throw new FileNotFoundException($"Shader bundle referenced external file '{relativePath}'; runtime bundles are inline-only.");

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var result in _loaded.Values)
                    foreach (var shader in result.Shaders)
                        shader?.Dispose();
                _log?.Invoke($"[ShaderBundleRuntimeSource] disposed ({_loaded.Count} bundles)");
                _loaded.Clear();
            }
        }
    }
}