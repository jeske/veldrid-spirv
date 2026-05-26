using System;
using System.IO;
using Veldrid.SPIRV;

/// <summary>
/// Example CLI tool demonstrating ShaderBundleCompiler usage.
/// Compiles a vertex-fragment GLSL shader pair into a .vdshader bundle.
///
/// Usage: dotnet run -- --name SolidFill --vert fill.vert --frag fill.frag --output ./out/
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        string name = null;
        string vertPath = null;
        string fragPath = null;
        string outputDir = ".";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--name": name = args[++i]; break;
                case "--vert": vertPath = args[++i]; break;
                case "--frag": fragPath = args[++i]; break;
                case "--output": outputDir = args[++i]; break;
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
            }
        }

        if (name == null || vertPath == null || fragPath == null)
        {
            Console.Error.WriteLine("Error: --name, --vert, and --frag are required.");
            PrintUsage();
            return 1;
        }

        if (!File.Exists(vertPath))
        {
            Console.Error.WriteLine($"Error: Vertex shader not found: {vertPath}");
            return 1;
        }
        if (!File.Exists(fragPath))
        {
            Console.Error.WriteLine($"Error: Fragment shader not found: {fragPath}");
            return 1;
        }

        try
        {
            string vertGlsl = File.ReadAllText(vertPath);
            string fragGlsl = File.ReadAllText(fragPath);

            Console.WriteLine($"Compiling shader bundle: {name}");
            Console.WriteLine($"  Vertex:   {vertPath}");
            Console.WriteLine($"  Fragment: {fragPath}");

            var bundle = ShaderBundleCompiler.CompileVertexFragment(
                vertGlsl, fragGlsl, name,
                vertexSourceFile: Path.GetFileName(vertPath),
                fragmentSourceFile: Path.GetFileName(fragPath));

            Directory.CreateDirectory(outputDir);
            string outputPath = Path.Combine(outputDir, $"{name}.vdshader");
            bundle.SerializeToFile(outputPath);

            Console.WriteLine($"  Output:   {outputPath}");
            Console.WriteLine($"  Backends: {string.Join(", ", bundle.Backends.Keys)}");
            Console.WriteLine($"  Layouts:  {bundle.ResourceLayoutDescriptions?.Length ?? 0} sets");
            Console.WriteLine("Done.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("Usage: VariantCompiler --name <ShaderName> --vert <path.vert> --frag <path.frag> [--output <dir>]");
        Console.WriteLine();
        Console.WriteLine("Compiles GLSL vertex+fragment shaders into a .vdshader bundle for all backends.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --name    Shader variant name (e.g., SolidFill, Terrain)");
        Console.WriteLine("  --vert    Path to vertex shader GLSL source");
        Console.WriteLine("  --frag    Path to fragment shader GLSL source");
        Console.WriteLine("  --output  Output directory (default: current directory)");
    }
}
