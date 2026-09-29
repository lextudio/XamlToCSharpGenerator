using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using XamlToCSharpGenerator.LanguageService.Symbols;
using XamlToCSharpGenerator.LanguageService.Models;
using XamlToCSharpGenerator.LanguageService.Workspace;
using XamlToCSharpGenerator.LanguageService.Framework;
using XamlToCSharpGenerator.Core.Models;

namespace XamlToCSharpGenerator.LanguageService.Workspace.Tier1;

/// <summary>
/// Factory for the Tier-1 (fast) compilation snapshot used by
/// <see cref="TieredCompilationProvider"/>, for whichever XAML framework this server serves.
///
/// <para>
/// Builds a lightweight Roslyn compilation from the framework's own assemblies plus the BCL, so
/// the editor can offer that framework's element and attribute completions immediately, without
/// waiting for MSBuild to evaluate the user's project.
/// </para>
///
/// <para>
/// Everything here is framework-neutral except <em>where the assemblies come from</em>: that is the
/// <see cref="ITier1ReferenceSet"/> passed in (WPF's reference pack, MAUI's NuGet packages, ...).
/// The metadata facts - presentation xmlns, XmlnsDefinition attribute name, seed namespaces -
/// come from the framework profile, and the type index is created and primed under that same
/// framework, which is the key the analysis looks it up by.
/// </para>
/// </summary>
public static class FastCompilationProvider
{
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Builds the Tier-1 snapshot for <paramref name="framework"/>. Returns <see langword="null"/>
    /// when <paramref name="referenceSet"/> finds none of the framework's assemblies, so the server
    /// runs on Tier-2 alone rather than on a compilation that cannot see a single control.
    /// </summary>
    public static CompilationSnapshot? BuildFastSnapshot(XamlLanguageFrameworkInfo framework, ITier1ReferenceSet referenceSet)
    {
        try
        {
            var referencesResult = BuildReferences(referenceSet);
            if (referencesResult.FrameworkAssemblyCount == 0)
            {
                Console.Error.WriteLine(
                    $"[XAML-LS] {referenceSet.Name} Tier-1 compilation skipped — none of its assemblies were found.");
                return null;
            }

            var references = referencesResult.References;
            var assemblyName = CompilationAssemblyName(referenceSet);
            var compilation = CSharpCompilation.Create(
                assemblyName: assemblyName,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                references: references,
                syntaxTrees: new[]
                {
                    CSharpSyntaxTree.ParseText(
                        SourceText.From(BuildSyntheticXmlnsMapSource(framework)),
                        path: assemblyName + ".XmlnsMap.g.cs")
                });

            Console.Error.WriteLine(
                $"[XAML-LS] {referenceSet.Name} (Tier-1) compilation built with {references.Count} references.");

            var cacheInfo = TryPrimeTypeIndexFromDisk(compilation, framework, referenceSet, referencesResult.ReferencePaths);
            if (cacheInfo is not null)
            {
                Console.Error.WriteLine(cacheInfo);
            }

            return new CompilationSnapshot(
                ProjectPath: null,
                Project: null,
                Compilation: compilation,
                Diagnostics: ImmutableArray<LanguageServiceDiagnostic>.Empty);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[XAML-LS] Failed to build the {framework.Id} Tier-1 compilation: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The Tier-1 compilation's assembly name. Microsoft WPF keeps "WpfCore": diagnostics key on it
    /// to tell a stale Tier-1 analysis from a Tier-2 one (see vscode-wpf's CLAUDE.md).
    /// </summary>
    public static string CompilationAssemblyName(ITier1ReferenceSet referenceSet) =>
        referenceSet.Name == FrameworkProfileIds.Wpf ? "WpfCore" : referenceSet.Name.Replace(".", "") + "Core";

    public static string PersistTypeIndexToDisk(Compilation compilation, XamlLanguageFrameworkInfo framework, ITier1ReferenceSet referenceSet)
    {
        try
        {
            var presentationXmlNamespace = framework.DefaultXmlNamespace;
            var index = AvaloniaTypeIndex.Create(compilation, framework);
            var exported = index.ExportXmlNamespaceTypes(new[] { presentationXmlNamespace });
            if (!exported.TryGetValue(presentationXmlNamespace, out var presentationTypes) || presentationTypes.IsDefaultOrEmpty)
            {
                return $"[XAML-LS] Tier-1 metadata cache skipped: no {framework.Id} presentation types to persist.";
            }

            var referencePaths = compilation.References
                .OfType<PortableExecutableReference>()
                .Select(static r => r.FilePath)
                .Where(static p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .Select(static p => p!)
                .ToImmutableArray();

            var key = ComputeCacheKey(referencePaths);
            if (string.IsNullOrWhiteSpace(key))
            {
                return "[XAML-LS] Tier-1 metadata cache skipped: could not compute cache key.";
            }

            var payload = new Tier1CachePayload
            {
                Version = 1,
                Key = key,
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
                XmlNamespaces = new[]
                {
                    new CachedNamespace
                    {
                        XmlNamespace = presentationXmlNamespace,
                        Types = presentationTypes.Select(ToCachedType).ToArray()
                    }
                }
            };

            var filePath = GetCacheFilePath(referenceSet);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, JsonSerializer.Serialize(payload, CacheJsonOptions), Encoding.UTF8);
            return $"[XAML-LS] Tier-1 metadata cache refreshed ({presentationTypes.Length} types) at {filePath}";
        }
        catch (Exception ex)
        {
            return $"[XAML-LS] Tier-1 metadata cache persist failed: {ex.Message}";
        }
    }

    private static string BuildSyntheticXmlnsMapSource(XamlLanguageFrameworkInfo framework)
    {
        // Some runtime/reference packs do not reliably expose enough
        // XmlnsDefinitionAttribute metadata during Tier-1 startup (WPF's does not).
        // Seed a minimal mapping so core control completions (Button/Grid/etc.)
        // are always available while MSBuild Tier-2 is loading. The namespace
        // list and the attribute name come from the framework profile; a framework
        // whose assemblies carry their own mapping (MAUI) declares no seeds.
        var namespaces = framework.Profile.Tier1SeedClrNamespaces;
        var xmlnsDefinitionAttributeName = framework.XmlnsDefinitionAttributeMetadataNames.IsDefaultOrEmpty
            ? "System.Windows.Markup.XmlnsDefinitionAttribute"
            : framework.XmlnsDefinitionAttributeMetadataNames[0];
        if (namespaces.IsDefaultOrEmpty)
        {
            return "internal static class __Tier1XmlnsMapAnchor { }";
        }

        // Strip the "Attribute" suffix so the emitted source reads
        // [assembly: System.Windows.Markup.XmlnsDefinition(...)] rather than
        // naming the attribute type itself.
        string attributeName = xmlnsDefinitionAttributeName.EndsWith("Attribute", StringComparison.Ordinal)
            ? xmlnsDefinitionAttributeName[..^"Attribute".Length]
            : xmlnsDefinitionAttributeName;

        var lines = new List<string>(namespaces.Length + 1);
        foreach (var clrNs in namespaces)
        {
            lines.Add(
                $"[assembly: {attributeName}(\"{framework.DefaultXmlNamespace}\", \"{clrNs}\")]"
            );
        }

        lines.Add("internal static class __Tier1XmlnsMapAnchor { }");
        return string.Join(Environment.NewLine, lines);
    }

    // -------------------------------------------------------------------------
    // Reference resolution
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves <see cref="MetadataReference"/> objects for the Tier-1 compilation without loading
    /// any assembly into the CLR: the BCL reference pack (so Roslyn can resolve base types across
    /// the control hierarchy) plus whatever <paramref name="referenceSet"/> locates for the
    /// framework itself. Roslyn reads metadata directly from the files.
    /// </summary>
    private static ReferenceBuildResult BuildReferences(ITier1ReferenceSet referenceSet)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>();
        var refPaths = new List<string>();

        bool TryAdd(string path)
        {
            if (File.Exists(path) && visited.Add(path))
            {
                refs.Add(MetadataReference.CreateFromFile(path));
                refPaths.Add(path);
                return true;
            }
            return false;
        }

        var environment = Tier1ReferenceEnvironment.Discover();

        // BCL: the reference pack when available (NuGet cache, then SDK packs/), else the running
        // runtime's implementation assemblies.
        var bclDir = environment.FindNuGetRefDir("microsoft.netcore.app.ref")
                     ?? environment.FindPackRefDir("Microsoft.NETCore.App.Ref")
                     ?? System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        foreach (var coreAssembly in new[]
        {
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.ObjectModel.dll",
            "System.ComponentModel.dll",
            "System.ComponentModel.TypeConverter.dll",
            "System.ComponentModel.Primitives.dll",
            "System.Linq.dll",
            "netstandard.dll",
        })
        {
            TryAdd(Path.Combine(bclDir, coreAssembly));
        }

        var frameworkAssemblyCount = 0;
        foreach (var path in referenceSet.ResolveFrameworkAssemblies(environment))
        {
            if (TryAdd(path))
            {
                frameworkAssemblyCount++;
            }
        }

        return new ReferenceBuildResult(refs, refPaths, frameworkAssemblyCount);
    }

    private static string? TryPrimeTypeIndexFromDisk(Compilation compilation, XamlLanguageFrameworkInfo framework, ITier1ReferenceSet referenceSet, IReadOnlyList<string> referencePaths)
    {
        try
        {
            var key = ComputeCacheKey(referencePaths);
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var filePath = GetCacheFilePath(referenceSet);
            if (!File.Exists(filePath))
            {
                return $"[XAML-LS] Tier-1 metadata cache miss: no cache file at {filePath}.";
            }

            var payload = JsonSerializer.Deserialize<Tier1CachePayload>(File.ReadAllText(filePath), CacheJsonOptions);
            if (payload is null || payload.Version != 1 || !string.Equals(payload.Key, key, StringComparison.Ordinal))
            {
                return "[XAML-LS] Tier-1 metadata cache miss: cache key mismatch (SDK/reference pack changed).";
            }

            var mapBuilder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<AvaloniaTypeInfo>>(StringComparer.Ordinal);
            foreach (var ns in payload.XmlNamespaces ?? Array.Empty<CachedNamespace>())
            {
                if (string.IsNullOrWhiteSpace(ns.XmlNamespace))
                {
                    continue;
                }

                var types = (ns.Types ?? Array.Empty<CachedType>())
                    .Select(t => new AvaloniaTypeInfo(
                        XmlTypeName: t.XmlTypeName ?? string.Empty,
                        FullTypeName: t.FullTypeName ?? string.Empty,
                        XmlNamespace: ns.XmlNamespace!,
                        ClrNamespace: t.ClrNamespace ?? string.Empty,
                        AssemblyName: t.AssemblyName ?? string.Empty,
                        Properties: (t.Properties ?? Array.Empty<CachedProperty>())
                            .Select(p => new AvaloniaPropertyInfo(
                                Name: p.Name ?? string.Empty,
                                TypeName: p.TypeName ?? string.Empty,
                                IsSettable: p.IsSettable,
                                IsAttached: p.IsAttached,
                                SourceLocation: null))
                            .ToImmutableArray(),
                        Summary: t.Summary ?? string.Empty,
                        SourceLocation: null,
                        PseudoClasses: ImmutableArray<AvaloniaPseudoClassInfo>.Empty))
                    .ToImmutableArray();

                if (!types.IsDefaultOrEmpty)
                {
                    mapBuilder[ns.XmlNamespace!] = types;
                }
            }

            var map = mapBuilder.ToImmutable();
            if (map.IsEmpty)
            {
                return "[XAML-LS] Tier-1 metadata cache miss: cache file contained no usable type data.";
            }

            // Primed under the framework being served: the analysis asks for
            // AvaloniaTypeIndex.Create(compilation, framework), keyed by framework id.
            AvaloniaTypeIndex.TryPrimeCache(compilation, framework, map);
            var count = map.TryGetValue(framework.DefaultXmlNamespace, out var presentationTypes) ? presentationTypes.Length : 0;
            return $"[XAML-LS] Tier-1 metadata cache hit: loaded {count} {framework.Id} types from disk.";
        }
        catch (Exception ex)
        {
            return $"[XAML-LS] Tier-1 metadata cache read failed: {ex.Message}";
        }
    }

    private static string GetCacheFilePath(ITier1ReferenceSet referenceSet)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, "LeXtudio", "vscode-wpf");
        // One file per reference set (see ITier1ReferenceSet.Name); Microsoft WPF keeps its old name
        // so an existing cache stays valid.
        var name = referenceSet.Name == FrameworkProfileIds.Wpf ? "wpf" : referenceSet.Name.ToLowerInvariant();
        return Path.Combine(root, name + "-ls-tier1-cache.json");
    }

    private static string ComputeCacheKey(IReadOnlyList<string> referencePaths)
    {
        if (referencePaths is null || referencePaths.Count == 0)
        {
            return string.Empty;
        }

        using var sha = SHA256.Create();
        var ordered = referencePaths
            .Where(static p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder(4096);
        foreach (var path in ordered)
        {
            var info = new FileInfo(path);
            sb.Append(path).Append('|')
              .Append(info.Length).Append('|')
              .Append(info.LastWriteTimeUtc.Ticks).AppendLine();
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    private static CachedType ToCachedType(AvaloniaTypeInfo typeInfo)
    {
        return new CachedType
        {
            XmlTypeName = typeInfo.XmlTypeName,
            FullTypeName = typeInfo.FullTypeName,
            ClrNamespace = typeInfo.ClrNamespace,
            AssemblyName = typeInfo.AssemblyName,
            Summary = typeInfo.Summary,
            Properties = typeInfo.Properties
                .Select(p => new CachedProperty
                {
                    Name = p.Name,
                    TypeName = p.TypeName,
                    IsSettable = p.IsSettable,
                    IsAttached = p.IsAttached,
                })
                .ToArray(),
        };
    }

    private sealed record ReferenceBuildResult(
        IReadOnlyList<MetadataReference> References,
        IReadOnlyList<string> ReferencePaths,
        int FrameworkAssemblyCount);

    private sealed class Tier1CachePayload
    {
        public int Version { get; set; }
        public string? Key { get; set; }
        public string? CreatedUtc { get; set; }
        public CachedNamespace[]? XmlNamespaces { get; set; }
    }

    private sealed class CachedNamespace
    {
        public string? XmlNamespace { get; set; }
        public CachedType[]? Types { get; set; }
    }

    private sealed class CachedType
    {
        public string? XmlTypeName { get; set; }
        public string? FullTypeName { get; set; }
        public string? ClrNamespace { get; set; }
        public string? AssemblyName { get; set; }
        public string? Summary { get; set; }
        public CachedProperty[]? Properties { get; set; }
    }

    private sealed class CachedProperty
    {
        public string? Name { get; set; }
        public string? TypeName { get; set; }
        public bool IsSettable { get; set; }
        public bool IsAttached { get; set; }
    }
}
