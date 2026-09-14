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

            // Build the bundle. No timestamp: output is a pure function of the inputs (bit-reproducible).
            var bundle = new VeldridShaderBundle
            {
                ShaderName = shaderName,
                VertexSource = vertexSourceFile,
                FragmentSource = fragmentSourceFile,
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
                GraphicsBackend backend = TargetToBackend(target);
                string vertexEntry = target == CrossCompileTarget.MSL ? "main0" : "main";
                string fragmentEntry = target == CrossCompileTarget.MSL ? "main0" : "main";

                // Direct3D11: DXBC bytecode when d3dcompiler is available (Windows), else HLSL text. Other targets: cross-compiled source text.
                byte[] vsOut, fsOut; string format;
                if (target == CrossCompileTarget.HLSL)
                {
                    (vsOut, format) = Direct3D11StagePayload(result.VertexShader ?? "", vertexEntry, ShaderStages.Vertex, shaderName);
                    (fsOut, _) = Direct3D11StagePayload(result.FragmentShader ?? "", fragmentEntry, ShaderStages.Fragment, shaderName);
                }
                else
                {
                    vsOut = Encoding.UTF8.GetBytes(result.VertexShader ?? "");
                    fsOut = Encoding.UTF8.GetBytes(result.FragmentShader ?? "");
                    format = GetShaderFormat(target);
                }

                bundle.Backends[VeldridShaderBundle.GetBackendKey(backend)] = new VdShaderBackendData
                {
                    ShaderFormat = format,
                    VertexEntryPoint = vertexEntry,
                    FragmentEntryPoint = fragmentEntry,
                    VertexShaderData = Convert.ToBase64String(vsOut),
                    FragmentShaderData = Convert.ToBase64String(fsOut),
                    OutputHash = ComputeSha256(Concat(vsOut, fsOut)),
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

            var bundle = new VeldridShaderBundle
            {
                ShaderName = shaderName,
                ComputeSource = computeSourceFile,
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
                GraphicsBackend backend = TargetToBackend(target);
                string entry = target == CrossCompileTarget.MSL ? "main0" : "main";

                // Direct3D11: DXBC bytecode when d3dcompiler is available (Windows), else HLSL text. Other targets: cross-compiled source text.
                byte[] csOut; string format;
                if (target == CrossCompileTarget.HLSL)
                    (csOut, format) = Direct3D11StagePayload(result.ComputeShader ?? "", entry, ShaderStages.Compute, shaderName);
                else
                    (csOut, format) = (Encoding.UTF8.GetBytes(result.ComputeShader ?? ""), GetShaderFormat(target));

                bundle.Backends[VeldridShaderBundle.GetBackendKey(backend)] = new VdShaderBackendData
                {
                    ShaderFormat = format,
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

        /// <summary>
        /// HLSL text → DXBC (Direct3D shader bytecode) via d3dcompiler_47, at BUNDLE-BUILD time. Without this the D3D11 slot ships
        /// <c>hlsl_text</c> and Veldrid's <c>D3D11Shader</c> runs <c>D3DCompile</c> on every launch — measured at 2.76 s of a 3.0 s
        /// PixelStudio start for SilkyNvg's 29-blend-mode fragment shaders. DXBC is hardware-independent (bound to the shader model, not
        /// the GPU; the driver translates it to ISA at <c>Create*Shader</c>), so it is as portable within D3D11 as SPIR-V is within Vulkan.
        /// Profile/flags mirror <c>D3D11Shader.compileCode</c>'s feature-level-11 branch exactly (vs/ps/cs_5_0, OptimizationLevel3), so the
        /// bytecode is what the runtime would have produced. Windows-only (d3dcompiler is a Windows DLL): elsewhere the caller keeps hlsl_text.
        /// A compile error throws — it is the same error the runtime compile would have raised, surfaced at build time instead.
        /// </summary>
        private static byte[] CompileHlslToDxbc(string hlslSource, string entryPoint, ShaderStages stage, string shaderName)
        {
            string profile = stage switch
            {
                ShaderStages.Vertex => "vs_5_0",
                ShaderStages.Fragment => "ps_5_0",
                ShaderStages.Compute => "cs_5_0",
                ShaderStages.Geometry => "gs_5_0",
                ShaderStages.TessellationControl => "hs_5_0",
                ShaderStages.TessellationEvaluation => "ds_5_0",
                _ => throw new SpirvCompilationException($"No DXBC profile for stage {stage}"),
            };
            byte[] hlslBytes = Encoding.UTF8.GetBytes(hlslSource);
            Vortice.D3DCompiler.Compiler.Compile(hlslBytes, null!, null!, entryPoint, null!, profile,
                Vortice.D3DCompiler.ShaderFlags.OptimizationLevel3, out var result, out var error);
            if (result == null)
            {
                string message = error != null ? Encoding.ASCII.GetString(error.AsBytes()) : "(no compiler output)";
                throw new SpirvCompilationException($"HLSL → DXBC failed for {shaderName} {stage} ({profile}): {message}");
            }
            return result.AsBytes();
        }

        /// <summary>
        /// The Direct3D11 slot's payload for one stage: DXBC bytecode when d3dcompiler is available (Windows), else the HLSL text the
        /// runtime will compile itself. Returns the format tag alongside so the two can never disagree.
        /// </summary>
        private static (byte[] Data, string Format) Direct3D11StagePayload(string hlslSource, string entryPoint, ShaderStages stage, string shaderName)
        {
            if (OperatingSystem.IsWindows())
                return (CompileHlslToDxbc(hlslSource, entryPoint, stage, shaderName), "dxbc");
            return (Encoding.UTF8.GetBytes(hlslSource), "hlsl_text");
        }

        private static byte[] Concat(byte[] first, byte[] second)
        {
            byte[] combined = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, combined, 0, first.Length);
            Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
            return combined;
        }

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
