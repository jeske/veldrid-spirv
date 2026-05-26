using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Veldrid.SPIRV
{
    /// <summary>
    /// Compiles GLSL shader source into a <see cref="VeldridShaderBundle"/> (.vdshader) containing
    /// compiled shader data for all backends. This is the primary API for producing precompiled shaders.
    /// </summary>
    public static class ShaderBundleCompiler
    {
        private static readonly CrossCompileTarget[] AllTargets =
        {
            CrossCompileTarget.HLSL,
            CrossCompileTarget.MSL,
            CrossCompileTarget.GLSL,
            CrossCompileTarget.ESSL,
        };

        /// <summary>
        /// Compiles a vertex-fragment shader pair from GLSL source into a <see cref="VeldridShaderBundle"/>
        /// containing compiled output for all backends (Vulkan, D3D11, Metal, OpenGL, OpenGL ES).
        /// </summary>
        /// <param name="vertexGlsl">Vulkan-style GLSL vertex shader source code.</param>
        /// <param name="fragmentGlsl">Vulkan-style GLSL fragment shader source code.</param>
        /// <param name="shaderName">Name for this shader variant (e.g., "SolidFill", "Terrain").</param>
        /// <param name="options">Cross-compilation options. If null, defaults are used.</param>
        /// <param name="vertexSourceFile">Original vertex source filename (informational).</param>
        /// <param name="fragmentSourceFile">Original fragment source filename (informational).</param>
        /// <returns>A <see cref="VeldridShaderBundle"/> ready to serialize as a .vdshader file.</returns>
        public static VeldridShaderBundle CompileVertexFragment(
            string vertexGlsl,
            string fragmentGlsl,
            string shaderName,
            CrossCompileOptions options = null,
            string vertexSourceFile = null,
            string fragmentSourceFile = null)
        {
            options ??= new CrossCompileOptions();
            byte[] vsGlslBytes = Encoding.ASCII.GetBytes(vertexGlsl);
            byte[] fsGlslBytes = Encoding.ASCII.GetBytes(fragmentGlsl);

            // Compile GLSL → SPIR-V
            var vsSpirvResult = SpirvCompilation.CompileGlslToSpirv(vertexGlsl, vertexSourceFile ?? "<vertex>", ShaderStages.Vertex, new GlslCompileOptions());
            var fsSpirvResult = SpirvCompilation.CompileGlslToSpirv(fragmentGlsl, fragmentSourceFile ?? "<fragment>", ShaderStages.Fragment, new GlslCompileOptions());

            byte[] vsSpirv = vsSpirvResult.SpirvBytes;
            byte[] fsSpirv = fsSpirvResult.SpirvBytes;

            // Build the bundle
            var now = DateTimeOffset.Now;
            var bundle = new VeldridShaderBundle
            {
                ShaderName = shaderName,
                VertexSource = vertexSourceFile,
                FragmentSource = fragmentSourceFile,
                CompiledAt = now.ToString("o"),
                CompiledAtEpoch = now.ToUnixTimeSeconds(),
            };

            // Input hash from SPIR-V
            byte[] spirvCombined = new byte[vsSpirv.Length + fsSpirv.Length];
            Buffer.BlockCopy(vsSpirv, 0, spirvCombined, 0, vsSpirv.Length);
            Buffer.BlockCopy(fsSpirv, 0, spirvCombined, vsSpirv.Length, fsSpirv.Length);
            bundle.InputHash = ComputeSha256(spirvCombined);

            // Vulkan backend: raw SPIR-V (inline base64)
            bundle.Backends[VeldridShaderBundle.GetBackendKey(GraphicsBackend.Vulkan)] = new VdShaderBackendData
            {
                ShaderFormat = "spirv",
                VertexEntryPoint = "main",
                FragmentEntryPoint = "main",
                VertexShaderData = Convert.ToBase64String(vsSpirv),
                FragmentShaderData = Convert.ToBase64String(fsSpirv),
                OutputHash = ComputeSha256(spirvCombined),
            };

            // Cross-compile for each target
            SpirvReflection capturedReflection = null;

            foreach (var target in AllTargets)
            {
                var result = SpirvCompilation.CompileVertexFragment(vsSpirv, fsSpirv, target, options);

                byte[] vsOut = Encoding.UTF8.GetBytes(result.VertexShader ?? "");
                byte[] fsOut = Encoding.UTF8.GetBytes(result.FragmentShader ?? "");
                byte[] combined = new byte[vsOut.Length + fsOut.Length];
                Buffer.BlockCopy(vsOut, 0, combined, 0, vsOut.Length);
                Buffer.BlockCopy(fsOut, 0, combined, vsOut.Length, fsOut.Length);

                GraphicsBackend backend = TargetToBackend(target);
                string vertexEntry = target == CrossCompileTarget.MSL ? "main0" : "main";
                string fragmentEntry = target == CrossCompileTarget.MSL ? "main0" : "main";

                bundle.Backends[VeldridShaderBundle.GetBackendKey(backend)] = new VdShaderBackendData
                {
                    ShaderFormat = GetShaderFormat(target),
                    VertexEntryPoint = vertexEntry,
                    FragmentEntryPoint = fragmentEntry,
                    VertexShaderData = Convert.ToBase64String(vsOut),
                    FragmentShaderData = Convert.ToBase64String(fsOut),
                    OutputHash = ComputeSha256(combined),
                };

                // Capture reflection from first successful compile (same for all targets)
                capturedReflection ??= result.Reflection;
            }

            // Populate ResourceLayoutDescriptions from reflection
            if (capturedReflection != null)
            {
                bundle.ResourceLayoutDescriptions = new VdShaderResourceLayout[capturedReflection.ResourceLayouts.Length];
                for (int i = 0; i < capturedReflection.ResourceLayouts.Length; i++)
                {
                    var layout = capturedReflection.ResourceLayouts[i];
                    var elements = new VdShaderResourceElement[layout.Elements.Length];
                    for (int j = 0; j < layout.Elements.Length; j++)
                    {
                        elements[j] = new VdShaderResourceElement
                        {
                            Name = layout.Elements[j].Name,
                            Kind = layout.Elements[j].Kind,
                            Stages = layout.Elements[j].Stages,
                        };
                    }
                    bundle.ResourceLayoutDescriptions[i] = new VdShaderResourceLayout { Elements = elements };
                }

                // Flat binding map (validation data)
                if (capturedReflection.BindingMap.Length > 0)
                {
                    bundle.FlatBindingMap = new VdShaderBindingEntry[capturedReflection.BindingMap.Length];
                    for (int i = 0; i < capturedReflection.BindingMap.Length; i++)
                    {
                        var entry = capturedReflection.BindingMap[i];
                        bundle.FlatBindingMap[i] = new VdShaderBindingEntry
                        {
                            Set = entry.Set,
                            Binding = entry.Binding,
                            Kind = entry.Kind,
                            Stages = entry.Stages,
                            FlatIndex = entry.FlatIndex,
                        };
                    }
                }
            }

            return bundle;
        }

        /// <summary>
        /// Compiles a compute shader from GLSL source into a <see cref="VeldridShaderBundle"/>
        /// containing compiled output for all backends.
        /// </summary>
        /// <param name="computeGlsl">Vulkan-style GLSL compute shader source code.</param>
        /// <param name="shaderName">Name for this shader variant.</param>
        /// <param name="options">Cross-compilation options. If null, defaults are used.</param>
        /// <param name="computeSourceFile">Original compute source filename (informational).</param>
        /// <returns>A <see cref="VeldridShaderBundle"/> ready to serialize as a .vdshader file.</returns>
        public static VeldridShaderBundle CompileCompute(
            string computeGlsl,
            string shaderName,
            CrossCompileOptions options = null,
            string computeSourceFile = null)
        {
            options ??= new CrossCompileOptions();

            // Compile GLSL → SPIR-V
            var csSpirvResult = SpirvCompilation.CompileGlslToSpirv(
                computeGlsl, computeSourceFile ?? "<compute>", ShaderStages.Compute, new GlslCompileOptions());
            byte[] csSpirv = csSpirvResult.SpirvBytes;

            var now = DateTimeOffset.Now;
            var bundle = new VeldridShaderBundle
            {
                ShaderName = shaderName,
                ComputeSource = computeSourceFile,
                CompiledAt = now.ToString("o"),
                CompiledAtEpoch = now.ToUnixTimeSeconds(),
                InputHash = ComputeSha256(csSpirv),
            };

            // Vulkan: raw SPIR-V
            bundle.Backends[VeldridShaderBundle.GetBackendKey(GraphicsBackend.Vulkan)] = new VdShaderBackendData
            {
                ShaderFormat = "spirv",
                ComputeEntryPoint = "main",
                ComputeShaderData = Convert.ToBase64String(csSpirv),
                OutputHash = ComputeSha256(csSpirv),
            };

            // Cross-compile for each target
            SpirvReflection capturedReflection = null;

            foreach (var target in AllTargets)
            {
                var result = SpirvCompilation.CompileCompute(csSpirv, target, options);
                byte[] csOut = Encoding.UTF8.GetBytes(result.ComputeShader ?? "");

                GraphicsBackend backend = TargetToBackend(target);
                string entry = target == CrossCompileTarget.MSL ? "main0" : "main";

                bundle.Backends[VeldridShaderBundle.GetBackendKey(backend)] = new VdShaderBackendData
                {
                    ShaderFormat = GetShaderFormat(target),
                    ComputeEntryPoint = entry,
                    ComputeShaderData = Convert.ToBase64String(csOut),
                    OutputHash = ComputeSha256(csOut),
                };

                capturedReflection ??= result.Reflection;
            }

            // Populate ResourceLayoutDescriptions
            if (capturedReflection != null)
            {
                bundle.ResourceLayoutDescriptions = new VdShaderResourceLayout[capturedReflection.ResourceLayouts.Length];
                for (int i = 0; i < capturedReflection.ResourceLayouts.Length; i++)
                {
                    var layout = capturedReflection.ResourceLayouts[i];
                    var elements = new VdShaderResourceElement[layout.Elements.Length];
                    for (int j = 0; j < layout.Elements.Length; j++)
                    {
                        elements[j] = new VdShaderResourceElement
                        {
                            Name = layout.Elements[j].Name,
                            Kind = layout.Elements[j].Kind,
                            Stages = layout.Elements[j].Stages,
                        };
                    }
                    bundle.ResourceLayoutDescriptions[i] = new VdShaderResourceLayout { Elements = elements };
                }

                if (capturedReflection.BindingMap.Length > 0)
                {
                    bundle.FlatBindingMap = new VdShaderBindingEntry[capturedReflection.BindingMap.Length];
                    for (int i = 0; i < capturedReflection.BindingMap.Length; i++)
                    {
                        var entry = capturedReflection.BindingMap[i];
                        bundle.FlatBindingMap[i] = new VdShaderBindingEntry
                        {
                            Set = entry.Set,
                            Binding = entry.Binding,
                            Kind = entry.Kind,
                            Stages = entry.Stages,
                            FlatIndex = entry.FlatIndex,
                        };
                    }
                }
            }

            return bundle;
        }

        // ─── Helpers ────────────────────────────────────────────────────────────

        private static GraphicsBackend TargetToBackend(CrossCompileTarget target) => target switch
        {
            CrossCompileTarget.HLSL => GraphicsBackend.Direct3D11,
            CrossCompileTarget.MSL => GraphicsBackend.Metal,
            CrossCompileTarget.GLSL => GraphicsBackend.OpenGL,
            CrossCompileTarget.ESSL => GraphicsBackend.OpenGLES,
            _ => throw new SpirvCompilationException($"Unknown target: {target}")
        };

        private static string GetShaderFormat(CrossCompileTarget target) => target switch
        {
            CrossCompileTarget.HLSL => "hlsl_text",
            CrossCompileTarget.MSL => "msl_text",
            CrossCompileTarget.GLSL => "glsl_text",
            CrossCompileTarget.ESSL => "glsl_text",
            _ => "unknown"
        };

        private static string ComputeSha256(byte[] data)
        {
            byte[] hash = SHA256.HashData(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
