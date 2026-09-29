using System;
using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using XamlToCSharpGenerator.Core.Models;

namespace XamlToCSharpGenerator.LanguageService.Framework.Wpf;

public sealed class WpfLanguageFrameworkProvider : IXamlLanguageFrameworkProvider
{
    private const string PresentationXmlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    public static WpfLanguageFrameworkProvider Instance { get; } = new();

    private WpfLanguageFrameworkProvider()
    {
        Framework = new XamlLanguageFrameworkInfo(
            Id: FrameworkProfileIds.Wpf,
            Profile: new PassiveXamlFrameworkProfile(
                FrameworkProfileIds.Wpf,
                PresentationXmlNamespace,
                preferredProjectXamlItemName: "Page",
                projectXamlItemNames:
                [
                    "Page",
                    "ApplicationDefinition",
                    "EmbeddedResource",
                    "Resource",
                    "Content",
                    "None",
                    "AdditionalFiles"
                ],
                // WPF reference assemblies do not reliably expose enough
                // XmlnsDefinitionAttribute metadata during Tier-1 startup, so the WPF language
                // server seeds a synthetic xmlns map for these to guarantee core control
                // completions before the project's own packages load. Declared here so the list
                // belongs to the framework rather than to one server's source.
                tier1SeedClrNamespaces:
                [
                    "System.Windows",
                    "System.Windows.Controls",
                    "System.Windows.Controls.Primitives",
                    "System.Windows.Data",
                    "System.Windows.Documents",
                    "System.Windows.Input",
                    "System.Windows.Media",
                    "System.Windows.Navigation",
                    "System.Windows.Shapes"
                ]),
            DefaultXmlNamespace: PresentationXmlNamespace,
            XmlnsDefinitionAttributeMetadataNames:
            [
                "System.Windows.Markup.XmlnsDefinitionAttribute"
            ],
            XmlnsPrefixAttributeMetadataNames:
            [
                "System.Windows.Markup.XmlnsPrefixAttribute"
            ],
            MarkupExtensionNamespaces:
            [
                "System.Windows",
                "System.Windows.Data",
                "System.Windows.Markup"
            ],
            PreferredProjectXamlItemName: "Page",
            ProjectXamlItemNames:
            [
                "Page",
                "ApplicationDefinition",
                "EmbeddedResource",
                "Resource",
                "Content",
                "None",
                "AdditionalFiles"
            ],
            DirectiveCompletions: ImmutableArray<XamlLanguageFrameworkCompletion>.Empty,
            MarkupExtensionCompletions: ImmutableArray<XamlLanguageFrameworkCompletion>.Empty);
    }

    public XamlLanguageFrameworkInfo Framework { get; }

    public int DetectionPriority => 100;

    public bool CanResolveFromProject(XDocument projectDocument, string projectPath)
    {
        _ = projectPath;
        return XamlLanguageFrameworkDetectionHelpers.HasTrueProperty(projectDocument, "UseWPF");
    }

    public bool CanResolveFromCompilation(Compilation compilation)
    {
        return XamlLanguageFrameworkDetectionHelpers.HasType(compilation, "System.Windows.Application") ||
               XamlLanguageFrameworkDetectionHelpers.HasAssembly(compilation, "PresentationFramework") ||
               XamlLanguageFrameworkDetectionHelpers.HasAssembly(compilation, "System.Xaml");
    }

    public bool CanResolveFromDocument(string filePath, string? documentText)
    {
        _ = filePath;
        return XamlLanguageFrameworkDetectionHelpers.DocumentContains(documentText, PresentationXmlNamespace) &&
               !XamlLanguageFrameworkDetectionHelpers.DocumentContains(documentText, "using:");
    }
}
