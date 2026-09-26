using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace XamlToCSharpGenerator.LanguageService.Workspace;

/// <summary>
/// Loads analyzer/generator assemblies from in-memory copies so the language server never holds a
/// file lock on them. MSBuildWorkspace's default loader opens the DLL in place, and an analyzer that
/// is a project's own build output (a ProjectReference with OutputItemType="Analyzer") then cannot
/// be overwritten: the user's next build fails with MSB3027 "file is locked by .NET Host".
/// </summary>
internal sealed class InMemoryAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
{
    private readonly ConcurrentDictionary<string, Assembly> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly LoadContext _context;

    public InMemoryAnalyzerAssemblyLoader()
    {
        _context = new LoadContext(this);
    }

    public void AddDependencyLocation(string fullPath)
    {
        if (Path.GetDirectoryName(fullPath) is { Length: > 0 } directory)
        {
            _directories.TryAdd(directory, 0);
        }
    }

    public Assembly LoadFromPath(string fullPath)
    {
        AddDependencyLocation(fullPath);
        return _byPath.GetOrAdd(fullPath, path => _context.LoadFromStream(new MemoryStream(File.ReadAllBytes(path))));
    }

    /// <summary>Replaces every file-based analyzer reference with one served by this loader.</summary>
    public Project Apply(Project project)
    {
        var changed = false;
        var references = project.AnalyzerReferences.Select(reference =>
        {
            if (reference is AnalyzerFileReference file && !ReferenceEquals(file.AssemblyLoader, this))
            {
                changed = true;
                AddDependencyLocation(file.FullPath);
                return new AnalyzerFileReference(file.FullPath, this);
            }
            return reference;
        }).ToList();
        return changed ? project.WithAnalyzerReferences(references) : project;
    }

    private sealed class LoadContext(InMemoryAnalyzerAssemblyLoader owner)
        : AssemblyLoadContext("XamlLanguageServiceAnalyzers", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Roslyn and anything the host already has must stay shared with the host's types.
            if (Default.Assemblies.Any(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), assemblyName)))
            {
                return null;
            }
            foreach (var directory in owner._directories.Keys)
            {
                var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return owner.LoadFromPath(candidate);
                }
            }
            return null;
        }
    }
}
