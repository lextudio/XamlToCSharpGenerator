using System.Collections.Generic;

namespace XamlToCSharpGenerator.LanguageService.Workspace.Tier1;

/// <summary>
/// Where one XAML framework's Tier-1 assemblies come from. This is the only framework-specific
/// part of the Tier-1 pipeline (see <see cref="FastCompilationProvider"/>): the metadata facts
/// are in the framework profile, but a profile cannot say which files on this machine hold the
/// framework's controls - WPF ships a reference pack, MAUI ships NuGet packages.
/// </summary>
public interface ITier1ReferenceSet
{
    /// <summary>Full paths of the framework's own assemblies (not the BCL, which is common).
    /// Paths that do not exist are ignored by the caller.</summary>
    /// <summary>A name for this set, unique among servers: it names the Tier-1 compilation and its
    /// disk cache. Two servers can share a framework profile - LibreWPF and Microsoft WPF are both
    /// WPF XAML, ProGPU and Microsoft WinUI both WinUI XAML - while reading different assemblies,
    /// so a cache keyed by framework id would be overwritten by each in turn.</summary>
    string Name { get; }

    IEnumerable<string> ResolveFrameworkAssemblies(Tier1ReferenceEnvironment environment);

    /// <summary>Metadata names of types any real project of this framework can see. Tier 2 may
    /// replace Tier 1 only once its compilation has all of them: an unrestored project's has none,
    /// and asking by type rather than by assembly is what lets a stand-in Tier 1 (WinUI served from
    /// Uno's assemblies) give way to the project's real WinUI compilation.</summary>
    IReadOnlyList<string> AnchorTypes { get; }
}
