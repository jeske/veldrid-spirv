#!/usr/bin/env -S dotnet run
// publish-local.cs — Build and pack Veldrid.SPIRV package to local NuGet feed (macOS/Linux)
//
// Versioning is timestamp-based (v2) — every build gets a unique version
// automatically via VeldridSpirv.Build.props. No version files to manage.
// The timestamp is captured once here and passed to MSBuild so all projects
// get the exact same version (no inter-project skew).
//
// Usage:
//   dotnet unix-publish-local.cs                    # Debug build + pack + deploy
//   dotnet unix-publish-local.cs --release          # Release configuration
//
// Requires: LOCAL_NUGET_REPO environment variable set to local feed path

using System.Diagnostics;
using System.Runtime.InteropServices;

// ─── Parse arguments ────────────────────────────────────────────────────────

bool release = args.Any(a => a.Equals("--release", StringComparison.OrdinalIgnoreCase)
                          || a.Equals("-release", StringComparison.OrdinalIgnoreCase));

string configuration = release ? "Release" : "Debug";

// ─── Resolve repo root (walk up from cwd to find VeldridSpirv.Build.props) ──

string repoRoot = FindRepoRoot(Directory.GetCurrentDirectory())
    ?? throw new InvalidOperationException(
        "Cannot find project root (looked for VeldridSpirv.Build.props walking up from cwd)");

string projectPath = Path.Combine(repoRoot, "src", "Veldrid.SPIRV", "Veldrid.SPIRV.csproj");

// ─── Validate LOCAL_NUGET_REPO ──────────────────────────────────────────────

string? localNuGetFeedPath = Environment.GetEnvironmentVariable("LOCAL_NUGET_REPO");
if (string.IsNullOrEmpty(localNuGetFeedPath))
{
    WriteColored("ERROR: LOCAL_NUGET_REPO environment variable not set.", ConsoleColor.Red);
    WriteColored("Set it to your local NuGet feed path, e.g.:", ConsoleColor.Yellow);
    WriteColored("  export LOCAL_NUGET_REPO=\"/path/to/LocalNuGet\"", ConsoleColor.Yellow);
    Environment.Exit(1);
}

WriteColored($"\n=== Veldrid.SPIRV publish-local ({configuration}) ===", ConsoleColor.Cyan);
WriteColored($"Local NuGet feed: {localNuGetFeedPath}", ConsoleColor.DarkGray);

// ─── Check SPIRV-Cross submodule freshness ──────────────────────────────────────

CheckSubmoduleFreshness(repoRoot);

// ─── Locate native libraries ────────────────────────────────────────────────
// The csproj picks up native assets from build/<Configuration>/<rid>/.
// We build the native library in Release, but may pack the managed assembly in
// Debug. To bridge the gap, copy Release native libs into the Debug tree
// so the pack step can find them. If neither Release nor Debug exist,
// fail early — the native library hasn't been built.

bool isOsx = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
string nativeLibName = isOsx ? "libveldrid-spirv.dylib" : "libveldrid-spirv.so";

// RIDs to check for this platform
string[] rids = isOsx
    ? ["osx"]
    : ["linux-x64", "linux-arm64"];

bool foundAny = false;

foreach (string rid in rids)
{
    string releaseLib = Path.Combine(repoRoot, "build", "Release", rid, nativeLibName);
    string debugDir   = Path.Combine(repoRoot, "build", "Debug", rid);
    string debugLib   = Path.Combine(debugDir, nativeLibName);

    if (File.Exists(releaseLib))
    {
        Directory.CreateDirectory(debugDir);
        File.Copy(releaseLib, debugLib, overwrite: true);
        WriteColored($"  Native lib ({rid}): copied Release -> Debug", ConsoleColor.DarkGray);
        foundAny = true;
    }
    else if (File.Exists(debugLib))
    {
        WriteColored($"  Native lib ({rid}): using existing Debug build", ConsoleColor.DarkGray);
        foundAny = true;
    }
    else
    {
        WriteColored($"  Native lib ({rid}): not found (skipped)", ConsoleColor.Yellow);
    }
}

if (!foundAny)
{
    WriteColored("Native library not found — building it now...", ConsoleColor.Yellow);
    RunOrExit("bash", $"\"{Path.Combine(repoRoot, "build-native.sh")}\" Release");
    // Re-check after build
    foreach (string rid2 in rids)
    {
        string releaseLib2 = Path.Combine(repoRoot, "build", "Release", rid2, nativeLibName);
        string debugDir2   = Path.Combine(repoRoot, "build", "Debug", rid2);
        string debugLib2   = Path.Combine(debugDir2, nativeLibName);
        if (File.Exists(releaseLib2))
        {
            Directory.CreateDirectory(debugDir2);
            File.Copy(releaseLib2, debugLib2, overwrite: true);
            WriteColored($"  Native lib ({rid2}): built and copied Release -> Debug", ConsoleColor.DarkGray);
            foundAny = true;
        }
    }
    if (!foundAny)
    {
        WriteColored("ERROR: Native build completed but no library found. Check build-native.sh output.", ConsoleColor.Red);
        Environment.Exit(1);
    }
}

// Verify at least the primary platform native lib exists
string primaryRid = isOsx ? "osx" : $"linux-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
string requiredLib = Path.Combine(repoRoot, "build", "Debug", primaryRid, nativeLibName);
if (!File.Exists(requiredLib))
{
    WriteColored($"ERROR: Required native binary not found at '{requiredLib}'.", ConsoleColor.Red);
    WriteColored($"You must build at least {primaryRid} before publishing.", ConsoleColor.Yellow);
    Environment.Exit(1);
}

// ─── Capture timestamp ONCE so all projects get the same version ────────────

DateTime now = DateTime.Now;
string buildYYMM   = now.ToString("yyMM");
string buildDDHH   = now.ToString("ddHH");
string buildmmss   = now.ToString("mmss");
string buildYYMMDD = now.ToString("yyMMdd");
string buildHHmmss = now.ToString("HHmmss");

string versionStamp = $"5.{buildYYMM}.{buildDDHH}.{buildmmss}";
string packageStamp = $"5.{buildYYMMDD}.{buildHHmmss}";

string versionProps = $"/p:_BuildYYMM={buildYYMM} /p:_BuildDDHH={buildDDHH} /p:_Buildmmss={buildmmss} /p:_BuildYYMMDD={buildYYMMDD} /p:_BuildHHmmss={buildHHmmss}";

WriteColored($"Version stamp: {versionStamp} (pkg: {packageStamp})", ConsoleColor.DarkGray);

// ─── Capture deploy start time ──────────────────────────────────────────────

DateTime deployStartTime = DateTime.UtcNow;

// ─── Clean ──────────────────────────────────────────────────────────────────

WriteColored("\n[1/3] Cleaning...", ConsoleColor.Green);
RunOrExit("dotnet", $"clean \"{projectPath}\" -c {configuration} -v quiet");

// ─── Build ──────────────────────────────────────────────────────────────────

WriteColored("\n[2/3] Building...", ConsoleColor.Green);
RunOrExit("dotnet", $"build \"{projectPath}\" -c {configuration} {versionProps}");

// ─── Pack ───────────────────────────────────────────────────────────────────

WriteColored("\n[3/3] Packing...", ConsoleColor.Green);
RunOrExit("dotnet", $"pack \"{projectPath}\" -c {configuration} --no-build {versionProps}");

// ─── Deploy to local NuGet feed ─────────────────────────────────────────────

string packageOutputDir = Path.Combine(repoRoot, "bin", "Packages", configuration);
Directory.CreateDirectory(localNuGetFeedPath);

var nupkgFiles = Directory.Exists(packageOutputDir)
    ? new DirectoryInfo(packageOutputDir)
        .GetFiles("*.nupkg")
        .Where(f => f.LastWriteTimeUtc >= deployStartTime)
        .ToList()
    : [];

if (nupkgFiles.Count == 0)
{
    WriteColored($"ERROR: No .nupkg files found in '{packageOutputDir}'. Pack may have failed silently.", ConsoleColor.Red);
    Environment.Exit(1);
}

foreach (var nupkg in nupkgFiles)
{
    string dest = Path.Combine(localNuGetFeedPath, nupkg.Name);
    File.Copy(nupkg.FullName, dest, overwrite: true);
}

// ─── Show deployed packages ─────────────────────────────────────────────────

var deployedPackages = new DirectoryInfo(localNuGetFeedPath)
    .GetFiles("*.nupkg")
    .Where(f => f.LastWriteTimeUtc >= deployStartTime)
    .OrderBy(f => f.Name)
    .ToList();

if (deployedPackages.Count > 0)
{
    WriteColored("\nDeployed packages:", ConsoleColor.Cyan);
    foreach (var pkg in deployedPackages)
    {
        double sizeKB = Math.Round(pkg.Length / 1024.0, 1);
        WriteColored($"  {pkg.Name}  ({sizeKB} KB)", ConsoleColor.Green);
    }
}
else
{
    WriteColored($"\nWARNING: No packages were deployed to {localNuGetFeedPath}", ConsoleColor.Yellow);
}

// ═════════════════════════════════════════════════════════════════════════════
// Helper methods
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>Walk up from <paramref name="startDir"/> to find a directory containing VeldridSpirv.Build.props.</summary>
static string? FindRepoRoot(string startDir)
{
    string? dir = startDir;
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir, "VeldridSpirv.Build.props")))
            return dir;
        dir = Path.GetDirectoryName(dir);
    }
    return null;
}

/// <summary>Write a colored line to the console, restoring the original color afterward.</summary>
static void WriteColored(string message, ConsoleColor color)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ForegroundColor = prev;
}

/// <summary>Run a process, inherit stdout/stderr, and exit if it fails.</summary>
static void RunOrExit(string fileName, string arguments)
{
    var psi = new ProcessStartInfo(fileName, arguments)
    {
        UseShellExecute = false,
    };
    using var proc = Process.Start(psi)
        ?? throw new InvalidOperationException($"Failed to start: {fileName} {arguments}");
    proc.WaitForExit();
    if (proc.ExitCode != 0)
    {
        WriteColored($"ERROR: '{fileName} {arguments}' exited with code {proc.ExitCode}", ConsoleColor.Red);
        Environment.Exit(proc.ExitCode);
    }
}
