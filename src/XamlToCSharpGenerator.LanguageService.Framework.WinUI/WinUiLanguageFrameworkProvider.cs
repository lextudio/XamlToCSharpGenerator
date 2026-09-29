using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using XamlToCSharpGenerator.Core.Models;

namespace XamlToCSharpGenerator.LanguageService.Framework.WinUI;

public sealed class WinUiLanguageFrameworkProvider : IXamlLanguageFrameworkProvider
{
    private const string PresentationXmlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    public static WinUiLanguageFrameworkProvider Instance { get; } = new();

    private WinUiLanguageFrameworkProvider()
    {
        Framework = new XamlLanguageFrameworkInfo(
            Id: FrameworkProfileIds.WinUI,
            Profile: new PassiveXamlFrameworkProfile(
                FrameworkProfileIds.WinUI,
                PresentationXmlNamespace,
                preferredProjectXamlItemName: "Page",
                projectXamlItemNames:
                [
                    "Page",
                    "ApplicationDefinition",
                    "PRIResource",
                    "Content",
                    "None",
                    "AdditionalFiles"
                ],
                // The WinUI XAML compiler maps the presentation xmlns to these namespaces
                // implicitly: Microsoft.WinUI.dll carries no XmlnsDefinition for it, so without
                // this list a project's real WinUI compilation indexed no control at all.
                tier1SeedClrNamespaces:
                [
                    "Microsoft.UI.Xaml",
                    "Microsoft.UI.Xaml.Automation",
                    "Microsoft.UI.Xaml.Automation.Peers",
                    "Microsoft.UI.Xaml.Controls",
                    "Microsoft.UI.Xaml.Controls.Primitives",
                    "Microsoft.UI.Xaml.Data",
                    "Microsoft.UI.Xaml.Documents",
                    "Microsoft.UI.Xaml.Input",
                    "Microsoft.UI.Xaml.Media",
                    "Microsoft.UI.Xaml.Media.Animation",
                    "Microsoft.UI.Xaml.Media.Imaging",
                    "Microsoft.UI.Xaml.Media.Media3D",
                    "Microsoft.UI.Xaml.Navigation",
                    "Microsoft.UI.Xaml.Shapes"
                ]),
            DefaultXmlNamespace: PresentationXmlNamespace,
            XmlnsDefinitionAttributeMetadataNames:
            [
                "Microsoft.UI.Xaml.Markup.XmlnsDefinitionAttribute",
                // Uno.UI's name for it. A host without the Windows App SDK serves WinUI's
                // Microsoft.UI.Xaml API from Uno's assemblies, whose mapping uses this attribute.
                "Microsoft.UI.Xaml.XmlnsDefinitionAttribute"
            ],
            XmlnsPrefixAttributeMetadataNames:
            [
                "Microsoft.UI.Xaml.Markup.XmlnsPrefixAttribute"
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
                "PRIResource",
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

    public int DetectionPriority => 300;

    public bool CanResolveFromProject(XDocument projectDocument, string projectPath)
    {
        _ = projectPath;
        return XamlLanguageFrameworkDetectionHelpers.HasTrueProperty(projectDocument, "UseWinUI") ||
               XamlLanguageFrameworkDetectionHelpers.HasTrueProperty(projectDocument, "WindowsAppSDKWinUI") ||
               XamlLanguageFrameworkDetectionHelpers.HasTrueProperty(projectDocument, "UseWinUITools") ||
               XamlLanguageFrameworkDetectionHelpers.HasPackageReference(projectDocument, "Microsoft.WindowsAppSDK");
    }

    public bool CanResolveFromCompilation(Compilation compilation)
    {
        return XamlLanguageFrameworkDetectionHelpers.HasType(compilation, "Microsoft.UI.Xaml.Application") ||
               XamlLanguageFrameworkDetectionHelpers.HasAssembly(compilation, "Microsoft.UI.Xaml");
    }

    public bool CanResolveFromDocument(string filePath, string? documentText)
    {
        _ = filePath;
        return XamlLanguageFrameworkDetectionHelpers.DocumentContains(documentText, PresentationXmlNamespace) &&
               XamlLanguageFrameworkDetectionHelpers.DocumentContains(documentText, "using:");
    }
}
