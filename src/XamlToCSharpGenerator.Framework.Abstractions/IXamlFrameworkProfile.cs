using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlToCSharpGenerator.Core.Abstractions;
using XamlToCSharpGenerator.Core.Models;

namespace XamlToCSharpGenerator.Framework.Abstractions;

public interface IXamlFrameworkProfile
{
    string Id { get; }

    IXamlFrameworkBuildContract BuildContract { get; }

    IXamlFrameworkTransformProvider TransformProvider { get; }

    IXamlFrameworkSemanticBinder CreateSemanticBinder();

    IXamlFrameworkEmitter CreateEmitter();

    ImmutableArray<IXamlDocumentEnricher> CreateDocumentEnrichers();

    XamlFrameworkParserSettings BuildParserSettings(Compilation compilation, GeneratorOptions options);

    /// <summary>
    /// CLR namespaces a fast (Tier-1) language-service snapshot should guarantee type completion
    /// for, before the full MSBuild compilation is available. A Tier-1 snapshot is built from a
    /// small set of framework reference assemblies, and the frameworks that host a language server
    /// used to seed a synthetic xmlns map for these namespaces so that core element/attribute
    /// completions appear before the project's own packages have loaded.
    /// <para>
    /// The namespaces are mapped to the framework's presentation
    /// namespace by the consuming server; the framework itself only names them, so the same list
    /// serves any server that builds a Tier-1 snapshot for this profile. An empty array means "no
    /// synthetic map is needed", which is correct for frameworks whose reference assemblies expose
    /// complete xmlns metadata on their own.
    /// </summary>
    ImmutableArray<string> Tier1SeedClrNamespaces { get; }
}
