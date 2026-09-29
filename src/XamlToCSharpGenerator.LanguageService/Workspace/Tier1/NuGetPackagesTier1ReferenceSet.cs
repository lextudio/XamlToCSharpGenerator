using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XamlToCSharpGenerator.LanguageService.Workspace.Tier1;

/// <summary>
/// A framework shipped as NuGet packages: the platform-neutral <c>lib/net*</c> builds of those
/// packages in the NuGet cache, all from one release. Such a framework has no shared framework or
/// reference pack; its controls reach a project only through its packages, which any project of
/// that framework on the machine has restored into the cache. The platform-neutral build is the
/// right one for metadata: it carries the full public control surface and the XmlnsDefinition
/// mapping, and needs no platform SDK or workload to be read.
/// </summary>
public class NuGetPackagesTier1ReferenceSet : ITier1ReferenceSet
{
    readonly (string Package, string Assembly)[] assemblies;
    readonly Func<string, bool> isUsableLibFolder;
    readonly bool oneRelease;

    public string Name { get; }

    public IReadOnlyList<string> AnchorTypes { get; }

    /// <param name="anchorTypes">See <see cref="ITier1ReferenceSet.AnchorTypes"/>.</param>
    /// <param name="isUsableLibFolder">Which <c>lib/</c> TFM folders may be read; by default only
    /// platform-neutral <c>netX.Y</c> ones. Pass a filter for a framework that ships only
    /// platform builds (WinUI: <c>net6.0-windows10.0.17763.0</c>) - metadata reads anywhere.</param>
    /// <param name="oneRelease">True (the default) when the packages are versioned together, so all
    /// are read at the anchor package's version: mixing releases would pair a controls build with a
    /// core it was not compiled against. False for packages versioned independently (ProGPU's), each
    /// of which is then read at its own latest cached version.</param>
    /// <param name="assemblies">(package id, assembly) pairs. The FIRST names the anchor package,
    /// which must be present for the set to resolve at all.</param>
    public NuGetPackagesTier1ReferenceSet(string name, string[] anchorTypes, params (string Package, string Assembly)[] assemblies)
        : this(name, anchorTypes, IsPlatformNeutral, oneRelease: true, assemblies)
    {
    }

    public NuGetPackagesTier1ReferenceSet(string name, string[] anchorTypes, Func<string, bool> isUsableLibFolder, bool oneRelease, params (string Package, string Assembly)[] assemblies)
    {
        Name = name;
        this.assemblies = assemblies;
        this.isUsableLibFolder = isUsableLibFolder;
        this.oneRelease = oneRelease;
        AnchorTypes = anchorTypes;
    }

    /// <summary><c>netX.Y</c> without a platform suffix: not <c>netstandard*</c>, not <c>net10.0-android36.0</c>.</summary>
    public static bool IsPlatformNeutral(string tfmFolder) =>
        tfmFolder.StartsWith("net", StringComparison.OrdinalIgnoreCase)
        && !tfmFolder.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase)
        && !tfmFolder.Contains('-');

    public IEnumerable<string> ResolveFrameworkAssemblies(Tier1ReferenceEnvironment environment)
    {
        // All packages from one release: they are versioned together, and mixing releases would
        // pair a controls build with a core it was not compiled against.
        var (anchorPackage, anchorAssembly) = assemblies[0];
        var version = environment.FindLatestPackageVersion(anchorPackage,
            versionDir => FindLibDir(versionDir, anchorAssembly) is not null);
        if (version is null)
        {
            Console.Error.WriteLine(
                $"[XAML-LS] WARNING: no {anchorPackage} package with a usable lib/ build " +
                $"build in the NuGet cache ({environment.NuGetPackagesRoot}); {Name} Tier-1 unavailable.");
            return Array.Empty<string>();
        }

        Console.Error.WriteLine($"[XAML-LS] {Name} Tier-1 packages: {version}");
        var paths = new List<string>();
        foreach (var (package, assembly) in assemblies)
        {
            var packageVersion = oneRelease
                ? version
                : environment.FindLatestPackageVersion(package, versionDir => FindLibDir(versionDir, assembly) is not null);
            var libDir = packageVersion is null
                ? null
                : FindLibDir(Path.Combine(environment.NuGetPackagesRoot, package, packageVersion), assembly);
            if (libDir is not null)
            {
                paths.Add(Path.Combine(libDir, assembly));
            }
        }

        return paths;
    }

    /// <summary>The highest usable <c>lib/</c> TFM folder that contains <paramref name="assembly"/>.</summary>
    string? FindLibDir(string versionDir, string assembly)
    {
        var libRoot = Path.Combine(versionDir, "lib");
        if (!Directory.Exists(libRoot))
        {
            return null;
        }

        return Directory.GetDirectories(libRoot)
            .Where(dir => isUsableLibFolder(Path.GetFileName(dir)) && File.Exists(Path.Combine(dir, assembly)))
            .OrderByDescending(static d => d, Tier1ReferenceEnvironment.VersionDirComparer)
            .FirstOrDefault();
    }
}
