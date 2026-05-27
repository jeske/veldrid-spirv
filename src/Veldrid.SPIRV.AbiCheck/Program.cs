using System;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// Build-time utility that loads libveldrid-spirv and verifies its ABI version
/// matches the expected version from the NATIVE_ABI_VERSION file.
/// Exit codes:
///   0 = match
///   1 = mismatch
///   2 = usage error / DLL not found
/// </summary>
class Program
{
    [DllImport("libveldrid-spirv", CallingConvention = CallingConvention.Cdecl)]
    static extern uint GetAbiVersion();

    [DllImport("libveldrid-spirv", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr GetBuildInfo();

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: Veldrid.SPIRV.AbiCheck <path-to-native-dll-directory> <expected-abi-version>");
            return 2;
        }

        string dllDir = Path.GetFullPath(args[0]);
        uint expectedVersion = uint.Parse(args[1]);

        // Add the DLL directory to the native library search path
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, asm, path) =>
        {
            if (name == "libveldrid-spirv")
            {
                string dllPath;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    dllPath = Path.Combine(dllDir, "libveldrid-spirv.dll");
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    dllPath = Path.Combine(dllDir, "libveldrid-spirv.dylib");
                else
                    dllPath = Path.Combine(dllDir, "libveldrid-spirv.so");

                if (!File.Exists(dllPath))
                {
                    Console.Error.WriteLine($"ERROR: Native library not found at: {dllPath}");
                    return IntPtr.Zero;
                }

                return NativeLibrary.Load(dllPath);
            }
            return IntPtr.Zero;
        });

        uint actualVersion;
        string buildInfo = "unknown";
        try
        {
            actualVersion = GetAbiVersion();
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine($"ERROR: Could not load libveldrid-spirv from '{dllDir}': {ex.Message}");
            return 2;
        }
        catch (EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"ERROR: libveldrid-spirv in '{dllDir}' does not export GetAbiVersion(). It is too old and must be rebuilt.");
            return 1;
        }

        try
        {
            IntPtr ptr = GetBuildInfo();
            if (ptr != IntPtr.Zero)
                buildInfo = Marshal.PtrToStringAnsi(ptr) ?? "unknown";
        }
        catch { /* old DLL without GetBuildInfo */ }

        Console.WriteLine($"Native ABI version: {actualVersion} (expected: {expectedVersion})");
        Console.WriteLine($"Native build info:  {buildInfo}");

        if (actualVersion != expectedVersion)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"FATAL: ABI VERSION MISMATCH!");
            Console.Error.WriteLine($"  Expected: {expectedVersion}");
            Console.Error.WriteLine($"  Actual:   {actualVersion}");
            Console.Error.WriteLine($"  DLL path: {dllDir}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("The native library must be rebuilt. Run: build-native.cmd release win-x64");
            return 1;
        }

        Console.WriteLine("ABI version check PASSED.");
        return 0;
    }
}