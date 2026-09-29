using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XamlToCSharpGenerator.LanguageService.Workspace.Tier1;

/// <summary>The places Tier-1 assemblies are looked up: the NuGet global-packages folder and the
/// .NET install roots (for SDK <c>packs/</c> and the shared runtime).</summary>
public sealed class Tier1ReferenceEnvironment
{
    public Tier1ReferenceEnvironment(string nuGetPackagesRoot, IReadOnlyList<string> dotnetRoots, string? sharedRoot)
    {
        NuGetPackagesRoot = nuGetPackagesRoot;
        DotnetRoots = dotnetRoots;
        SharedRoot = sharedRoot;
    }

    public string NuGetPackagesRoot { get; }

    public IReadOnlyList<string> DotnetRoots { get; }

    /// <summary>The running runtime's <c>shared/</c> folder.</summary>
    public string? SharedRoot { get; }

    public static Tier1ReferenceEnvironment Discover()
    {
        // Runtime directory example:
        //   /usr/local/share/dotnet/shared/Microsoft.NETCore.App/10.0.5/
        //   C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.5\
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sharedRoot = Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)); // …/shared
        var dotnetRoot = Path.GetDirectoryName(sharedRoot);                      // …/dotnet

        var nuGetPackagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                                ?? Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                    ".nuget", "packages");

        var roots = new List<string?> { dotnetRoot, Environment.GetEnvironmentVariable("DOTNET_ROOT") };
        // Common install locations
        roots.Add("/usr/local/share/dotnet");
        roots.Add("/opt/homebrew/share/dotnet");
        roots.Add("/usr/share/dotnet");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            roots.Add(Path.Combine(programFiles, "dotnet"));
        }

        return new Tier1ReferenceEnvironment(
            nuGetPackagesRoot,
            roots.Where(static r => !string.IsNullOrWhiteSpace(r)).Select(static r => r!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            sharedRoot);
    }

    /// <summary>The highest <c>ref/net*</c> folder of the latest version of a NuGet-cached
    /// reference pack. Layout: {root}/{packageId}/{version}/ref/{tfm}/</summary>
    public string? FindNuGetRefDir(string packageId)
    {
        var versionDir = FindLatestVersionDir(Path.Combine(NuGetPackagesRoot, packageId));
        return versionDir is null ? null : FindLatestTfmDir(Path.Combine(versionDir, "ref"));
    }

    /// <summary>Same as <see cref="FindNuGetRefDir"/>, for an SDK <c>packs/</c> folder.</summary>
    public string? FindPackRefDir(string packName)
    {
        foreach (var root in DotnetRoots)
        {
            var versionDir = FindLatestVersionDir(Path.Combine(root, "packs", packName));
            var tfmDir = versionDir is null ? null : FindLatestTfmDir(Path.Combine(versionDir, "ref"));
            if (tfmDir is not null)
            {
                return tfmDir;
            }
        }

        return null;
    }

    /// <summary>The latest version folder of a shared runtime (implementation assemblies).</summary>
    public string? FindSharedRuntimeDir(string frameworkName) =>
        SharedRoot is null ? null : FindLatestVersionDir(Path.Combine(SharedRoot, frameworkName));

    /// <summary>The highest cached version of <paramref name="packageId"/> that satisfies
    /// <paramref name="isUsable"/>, as a folder name.</summary>
    public string? FindLatestPackageVersion(string packageId, Func<string, bool> isUsable)
    {
        var packageRoot = Path.Combine(NuGetPackagesRoot, packageId);
        if (!Directory.Exists(packageRoot))
        {
            return null;
        }

        var best = Directory.GetDirectories(packageRoot)
            .Where(isUsable)
            .OrderByDescending(static d => d, VersionDirComparer)
            .FirstOrDefault();
        return best is null ? null : Path.GetFileName(best);
    }

    static string? FindLatestTfmDir(string refRoot) =>
        Directory.Exists(refRoot)
            ? Directory.GetDirectories(refRoot).OrderByDescending(static d => d, VersionDirComparer).FirstOrDefault()
            : null;

    static string? FindLatestVersionDir(string root) =>
        Directory.Exists(root)
            ? Directory.GetDirectories(root).OrderByDescending(static d => d, VersionDirComparer).FirstOrDefault()
            : null;

    /// <summary>
    /// Orders version / TFM folder names (the last path segment) highest first when used with
    /// <c>OrderByDescending</c>: numerically by the version ("net10.0" &gt; "net8.0", "10.0.2" &gt;
    /// "9.0.9"), then a release above its prereleases, then prerelease labels segment by segment,
    /// numeric segments numerically ("0.1.0-preview.65" &gt; "0.1.0-preview.57" &gt; "0.1.0-preview.9").
    /// Dropping the prerelease label made every "0.1.0-preview.*" folder tie, so which one was
    /// picked was arbitrary - often not the latest.
    /// </summary>
    public static IComparer<string> VersionDirComparer { get; } = Comparer<string>.Create(CompareVersionDirs);

    static int CompareVersionDirs(string left, string right)
    {
        var (leftVersion, leftLabel) = Split(left);
        var (rightVersion, rightLabel) = Split(right);
        var byVersion = leftVersion.CompareTo(rightVersion);
        if (byVersion != 0) return byVersion;
        if (leftLabel.Length == 0 || rightLabel.Length == 0)
            return (leftLabel.Length == 0 ? 1 : 0) - (rightLabel.Length == 0 ? 1 : 0);

        var leftParts = leftLabel.Split('.');
        var rightParts = rightLabel.Split('.');
        for (var i = 0; i < Math.Min(leftParts.Length, rightParts.Length); i++)
        {
            var bothNumeric = long.TryParse(leftParts[i], out var l) & long.TryParse(rightParts[i], out var r);
            var byPart = bothNumeric ? l.CompareTo(r) : string.CompareOrdinal(leftParts[i], rightParts[i]);
            if (byPart != 0) return byPart;
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    /// <summary>The version of a folder name, stripping a leading non-numeric prefix ("net") and
    /// separating a prerelease label ("-preview.3"); 0.0 when unparseable, so it still sorts.</summary>
    static (Version Version, string Label) Split(string dirPath)
    {
        var name = Path.GetFileName(dirPath) ?? string.Empty;
        var i = 0;
        while (i < name.Length && !char.IsDigit(name[i])) i++;
        var versionStr = name.Substring(i);
        var label = string.Empty;
        var dash = versionStr.IndexOf('-');
        if (dash >= 0)
        {
            label = versionStr.Substring(dash + 1);
            versionStr = versionStr.Substring(0, dash);
        }

        return (Version.TryParse(versionStr, out var v) ? v : new Version(0, 0), label);
    }
}
