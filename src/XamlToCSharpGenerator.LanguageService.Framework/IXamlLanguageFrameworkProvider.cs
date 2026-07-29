using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace XamlToCSharpGenerator.LanguageService.Framework;

/// <summary>
/// Supplies one XAML framework's language-service metadata, and optionally the
/// heuristics used to auto-detect it.
///
/// <para>
/// Only <see cref="Framework"/> is required. The <c>CanResolveFrom*</c> members
/// exist for hosts that serve several frameworks from one server and therefore
/// have to guess which one a document belongs to (the generic language server,
/// the editor extension). A host dedicated to a single framework — an IDE that
/// already knows it is editing WPF, or Uno, or MAUI — should not guess at all:
/// register only that framework's provider, and/or pass its id through
/// <c>XamlLanguageServiceOptions.FrameworkId</c>, which short-circuits detection
/// before any heuristic runs. Those providers can leave every detection member
/// at its default and pay nothing for machinery they never use.
/// </para>
/// </summary>
public interface IXamlLanguageFrameworkProvider
{
    XamlLanguageFrameworkInfo Framework { get; }

    /// <summary>
    /// Order in which this provider is offered a document during auto-detection;
    /// higher runs first. Only consulted when several providers are registered
    /// and no explicit framework id was supplied.
    /// </summary>
    int DetectionPriority => 0;

    bool CanResolveFromProject(XDocument projectDocument, string projectPath) => false;

    bool CanResolveFromCompilation(Compilation compilation) => false;

    bool CanResolveFromDocument(string filePath, string? documentText) => false;
}
