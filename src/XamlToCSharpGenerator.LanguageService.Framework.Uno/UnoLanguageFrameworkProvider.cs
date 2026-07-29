using System.Collections.Immutable;
using System.Xml.Linq;
using XamlToCSharpGenerator.Core.Models;

namespace XamlToCSharpGenerator.LanguageService.Framework.Uno;

/// <summary>
/// Uno Platform language-service support.
///
/// <para>
/// Uno's XAML dialect is WinUI's: the same presentation xmlns, <c>using:</c>
/// prefixes, and <c>x:Bind</c>. The difference is where the types come from —
/// <c>Microsoft.UI.Xaml.*</c> is provided by the Uno.UI assemblies rather than
/// the Windows App SDK. Completions and go-to-definition are driven off the
/// project's real compilation, so an Uno project naturally offers Uno's own
/// types; nothing here has to enumerate them.
/// </para>
///
/// <para>
/// Detection is deliberately limited to project-file markers. A host dedicated
/// to Uno (UnoDevelop) selects this framework explicitly and never reaches the
/// heuristics; the generic server only needs enough to tell an Uno project from
/// a Windows App SDK one, since guessing wrong between the two still yields a
/// working WinUI dialect. Compilation- and document-level detection are left at
/// the interface default (no) rather than duplicating WinUI's checks — both
/// would match Uno's <c>Microsoft.UI.Xaml</c> surface and could only mislead.
/// </para>
/// </summary>
public sealed class UnoLanguageFrameworkProvider : IXamlLanguageFrameworkProvider
{
    private const string PresentationXmlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    public static UnoLanguageFrameworkProvider Instance { get; } = new();

    private UnoLanguageFrameworkProvider()
    {
        Framework = new XamlLanguageFrameworkInfo(
            Id: FrameworkProfileIds.Uno,
            Profile: new PassiveXamlFrameworkProfile(
                FrameworkProfileIds.Uno,
                PresentationXmlNamespace,
                preferredProjectXamlItemName: "Page",
                projectXamlItemNames:
                [
                    "Page",
                    "ApplicationDefinition",
                    "UnoPage",
                    "UnoApplicationDefinition",
                    "Content",
                    "None",
                    "AdditionalFiles"
                ]),
            DefaultXmlNamespace: PresentationXmlNamespace,
            XmlnsDefinitionAttributeMetadataNames:
            [
                "Microsoft.UI.Xaml.Markup.XmlnsDefinitionAttribute",
                // Uno.UI still ships the UWP-era attribute for compatibility.
                "Windows.UI.Xaml.Markup.XmlnsDefinitionAttribute"
            ],
            XmlnsPrefixAttributeMetadataNames:
            [
                "Microsoft.UI.Xaml.Markup.XmlnsPrefixAttribute",
                "Windows.UI.Xaml.Markup.XmlnsPrefixAttribute"
            ],
            MarkupExtensionNamespaces:
            [
                "Microsoft.UI.Xaml",
                "Microsoft.UI.Xaml.Data",
                "Microsoft.UI.Xaml.Markup",
                "System.Windows.Markup"
            ],
            PreferredProjectXamlItemName: "Page",
            ProjectXamlItemNames:
            [
                "Page",
                "ApplicationDefinition",
                "UnoPage",
                "UnoApplicationDefinition",
                "Content",
                "None",
                "AdditionalFiles"
            ],
            DirectiveCompletions:
            [
                XamlLanguageFrameworkCompletion.Create("x:DataType", "x:DataType=\"$0\"", "Compiled binding data type")
            ],
            MarkupExtensionCompletions:
            [
                XamlLanguageFrameworkCompletion.Create("x:Bind", "{x:Bind $0}", "x:Bind compiled binding")
            ]);
    }

    public XamlLanguageFrameworkInfo Framework { get; }

    // Ahead of WinUI (300): an Uno project also carries Microsoft.UI.Xaml types,
    // so whichever of the two is asked first wins, and Uno's markers are the
    // more specific signal.
    public int DetectionPriority => 350;

    public bool CanResolveFromProject(XDocument projectDocument, string projectPath)
    {
        _ = projectPath;
        return XamlLanguageFrameworkDetectionHelpers.MatchesProjectSdk(projectDocument, "Uno.Sdk") ||
               XamlLanguageFrameworkDetectionHelpers.HasPackageReference(projectDocument, "Uno.WinUI") ||
               XamlLanguageFrameworkDetectionHelpers.HasPackageReference(projectDocument, "Uno.UI");
    }
}
