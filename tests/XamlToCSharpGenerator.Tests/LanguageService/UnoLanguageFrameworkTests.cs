using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlToCSharpGenerator.Core.Models;
using XamlToCSharpGenerator.LanguageService;
using XamlToCSharpGenerator.LanguageService.Analysis;
using XamlToCSharpGenerator.LanguageService.Framework;
using XamlToCSharpGenerator.LanguageService.Framework.All;
using XamlToCSharpGenerator.LanguageService.Framework.Uno;
using XamlToCSharpGenerator.LanguageService.Models;
using XamlToCSharpGenerator.LanguageService.Text;

namespace XamlToCSharpGenerator.Tests.LanguageService;

/// <summary>
/// Uno Platform language-service support, and the composition model that an
/// IDE-embedded host is expected to use: pick the framework at composition time
/// instead of letting the server guess per document.
/// </summary>
public sealed class UnoLanguageFrameworkTests
{
    private const string PresentationXmlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void BuiltInRegistry_ContainsUno()
    {
        var registry = XamlBuiltInLanguageFrameworkRegistry.Create();

        Assert.True(registry.TryGetById(FrameworkProfileIds.Uno, out var uno));
        Assert.Equal(FrameworkProfileIds.Uno, uno.Id);
        Assert.Equal(PresentationXmlNamespace, uno.DefaultXmlNamespace);
    }

    /// <summary>
    /// The model a dedicated host (UnoDevelop) uses: register only Uno, and name
    /// it explicitly. Nothing about the document, project, or compilation is
    /// inspected to reach that decision — a server that already knows which
    /// framework it serves should not pay for guessing.
    /// </summary>
    [Fact]
    public async Task SingleFrameworkRegistry_ResolvesUno_WithoutConsultingAnyHeuristic()
    {
        var registry = new XamlLanguageFrameworkRegistryBuilder()
            .Add(UnoLanguageFrameworkProvider.Instance)
            .Build(FrameworkProfileIds.Uno);
        var analysisService = new XamlCompilerAnalysisService(
            new InMemoryCompilationProvider(CreateUnoCompilation()),
            registry);

        // Deliberately hostile inputs for detection: a .xaml path (which the
        // resolver's own last-resort branch maps to WPF) and markup carrying no
        // Uno marker whatsoever. The explicit id must win regardless.
        const string documentPath = "/tmp/UnoHost/MainPage.xaml";
        const string xaml = "<Page />";
        var document = new LanguageServiceDocument(
            UriPathHelper.ToDocumentUri(documentPath),
            documentPath,
            xaml,
            Version: 1);

        var analysis = await analysisService.AnalyzeAsync(
            document,
            new XamlLanguageServiceOptions(
                WorkspaceRoot: null,
                FrameworkId: FrameworkProfileIds.Uno,
                IncludeCompilationDiagnostics: false,
                IncludeSemanticDiagnostics: false),
            CancellationToken.None);

        Assert.Equal(FrameworkProfileIds.Uno, analysis.Framework.Id);
    }

    /// <summary>
    /// Uno's profile is a <see cref="PassiveXamlFrameworkProfile"/> — "passive"
    /// meaning it contributes no code generation, not that it degrades the
    /// language service. Completions still come from the project's real
    /// compilation, so an Uno project offers Uno's own controls.
    /// </summary>
    [Fact]
    public async Task Completion_Uno_OffersControlTypesFromTheCompilation()
    {
        var registry = new XamlLanguageFrameworkRegistryBuilder()
            .Add(UnoLanguageFrameworkProvider.Instance)
            .Build(FrameworkProfileIds.Uno);
        using var engine = new XamlLanguageServiceEngine(
            new InMemoryCompilationProvider(CreateUnoCompilation()),
            registry);

        const string uri = "file:///tmp/UnoCompletion.xaml";
        const string xaml =
            "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">\n" +
            "  <Nav\n" +
            "</Page>";
        var options = CreateOptions(frameworkId: FrameworkProfileIds.Uno);

        await engine.OpenDocumentAsync(uri, xaml, version: 1, options, CancellationToken.None);
        var caret = ToPosition(xaml, xaml.IndexOf("<Nav", StringComparison.Ordinal) + "<Nav".Length);
        var completions = await engine.GetCompletionsAsync(uri, caret, options, CancellationToken.None);

        // NavigationView exists only in this test's Uno-shaped compilation; it is
        // not part of any hardcoded list in the provider.
        Assert.Contains(completions, item => string.Equals(item.Label, "NavigationView", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Completion_Uno_OffersXBind()
    {
        var registry = new XamlLanguageFrameworkRegistryBuilder()
            .Add(UnoLanguageFrameworkProvider.Instance)
            .Build(FrameworkProfileIds.Uno);
        using var engine = new XamlLanguageServiceEngine(
            new InMemoryCompilationProvider(CreateUnoCompilation()),
            registry);

        const string uri = "file:///tmp/UnoBind.xaml";
        const string xaml =
            "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">\n" +
            "  <TextBlock Text=\"{\" />\n" +
            "</Page>";
        var options = CreateOptions(frameworkId: FrameworkProfileIds.Uno);

        await engine.OpenDocumentAsync(uri, xaml, version: 1, options, CancellationToken.None);
        var caret = ToPosition(xaml, xaml.IndexOf("{", StringComparison.Ordinal) + 1);
        var completions = await engine.GetCompletionsAsync(uri, caret, options, CancellationToken.None);

        Assert.Contains(completions, item => item.Label.Contains("x:Bind", StringComparison.Ordinal));
    }

    [Fact]
    public void Detection_UnoSdkProject_IsRecognized()
    {
        var project = XDocument.Parse(
            """
            <Project Sdk="Uno.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-desktop</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(UnoLanguageFrameworkProvider.Instance.CanResolveFromProject(project, "/tmp/App.csproj"));
    }

    [Fact]
    public void Detection_UnoWinUiPackageProject_IsRecognized()
    {
        var project = XDocument.Parse(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Uno.WinUI" Version="5.0.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.True(UnoLanguageFrameworkProvider.Instance.CanResolveFromProject(project, "/tmp/App.csproj"));
    }

    /// <summary>
    /// An Uno project also references Microsoft.UI.Xaml types, so WinUI would
    /// happily claim it. Uno's marker is the more specific signal and it runs
    /// first.
    /// </summary>
    [Fact]
    public void Detection_UnoOutranksWinUi()
    {
        var registry = XamlBuiltInLanguageFrameworkRegistry.Create();
        Assert.True(registry.TryGetById(FrameworkProfileIds.Uno, out var uno));
        Assert.True(registry.TryGetById(FrameworkProfileIds.WinUI, out var winui));

        var unoProvider = registry.Providers.Single(p => p.Framework.Id == uno.Id);
        var winuiProvider = registry.Providers.Single(p => p.Framework.Id == winui.Id);

        Assert.True(unoProvider.DetectionPriority > winuiProvider.DetectionPriority);

        // Ordering is what the resolver actually walks, so assert on that too.
        var ids = registry.Providers.Select(p => p.Framework.Id).ToList();
        Assert.True(ids.IndexOf(uno.Id) < ids.IndexOf(winui.Id));
    }

    /// <summary>
    /// Detection is opt-in: a provider that only describes its framework — no
    /// heuristics at all — is a complete implementation. This is what lets a
    /// single-framework host skip writing guessing logic it would never call.
    /// </summary>
    [Fact]
    public async Task DetectionIsOptional_MetadataOnlyProvider_IsUsableOnItsOwn()
    {
        var registry = new XamlLanguageFrameworkRegistryBuilder()
            .Add(new MetadataOnlyFrameworkProvider())
            .Build(MetadataOnlyFrameworkProvider.FrameworkId);
        var analysisService = new XamlCompilerAnalysisService(
            new InMemoryCompilationProvider(CreateUnoCompilation()),
            registry);

        const string documentPath = "/tmp/MetadataOnly/View.xaml";
        var document = new LanguageServiceDocument(
            UriPathHelper.ToDocumentUri(documentPath),
            documentPath,
            "<Page />",
            Version: 1);

        var analysis = await analysisService.AnalyzeAsync(
            document,
            CreateOptions(frameworkId: MetadataOnlyFrameworkProvider.FrameworkId),
            CancellationToken.None);

        Assert.Equal(MetadataOnlyFrameworkProvider.FrameworkId, analysis.Framework.Id);
    }

    [Fact]
    public void DetectionIsOptional_DefaultsAreInert()
    {
        IXamlLanguageFrameworkProvider provider = new MetadataOnlyFrameworkProvider();

        Assert.Equal(0, provider.DetectionPriority);
        Assert.False(provider.CanResolveFromProject(XDocument.Parse("<Project />"), "/tmp/App.csproj"));
        Assert.False(provider.CanResolveFromCompilation(CreateUnoCompilation()));
        Assert.False(provider.CanResolveFromDocument("/tmp/View.xaml", "<Page />"));
    }

    // -----------------------------------------------------------------------

    private static XamlLanguageServiceOptions CreateOptions(string? workspaceRoot = null, string? frameworkId = null)
    {
        return new XamlLanguageServiceOptions(
            WorkspaceRoot: workspaceRoot,
            FrameworkId: frameworkId,
            IncludeCompilationDiagnostics: false,
            IncludeSemanticDiagnostics: false);
    }

    private static SourcePosition ToPosition(string text, int offset)
    {
        var linePosition = Microsoft.CodeAnalysis.Text.SourceText.From(text).Lines.GetLinePosition(offset);
        return new SourcePosition(linePosition.Line, linePosition.Character);
    }

    /// <summary>
    /// Shaped like an Uno assembly: WinUI's <c>Microsoft.UI.Xaml</c> surface, but
    /// with a control (<c>NavigationView</c>) that only exists here, so a passing
    /// completion assertion can only have come from scanning this compilation.
    /// </summary>
    private static CSharpCompilation CreateUnoCompilation()
    {
        const string source = """
                              using System;

                              [assembly: Microsoft.UI.Xaml.Markup.XmlnsDefinitionAttribute("http://schemas.microsoft.com/winfx/2006/xaml/presentation", "Microsoft.UI.Xaml.Controls")]

                              namespace Microsoft.UI.Xaml.Markup
                              {
                                  [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                                  public sealed class XmlnsDefinitionAttribute : Attribute
                                  {
                                      public XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace) { }
                                  }

                                  [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                                  public sealed class XmlnsPrefixAttribute : Attribute
                                  {
                                      public XmlnsPrefixAttribute(string xmlNamespace, string prefix) { }
                                  }
                              }

                              namespace Microsoft.UI.Xaml
                              {
                                  public class Application { }
                              }

                              namespace Microsoft.UI.Xaml.Controls
                              {
                                  public class Page
                                  {
                                      public object? Content { get; set; }
                                  }

                                  public class TextBlock
                                  {
                                      public string? Text { get; set; }
                                  }

                                  public class NavigationView
                                  {
                                      public object? Header { get; set; }
                                  }
                              }
                              """;

        return CSharpCompilation.Create(
            "UnoLanguageServiceTests",
            [CSharpSyntaxTree.ParseText(source)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location)
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private sealed class MetadataOnlyFrameworkProvider : IXamlLanguageFrameworkProvider
    {
        public const string FrameworkId = "metadata-only";

        private const string XmlNamespace = "https://example.com/metadata-only";

        public MetadataOnlyFrameworkProvider()
        {
            Framework = new XamlLanguageFrameworkInfo(
                Id: FrameworkId,
                Profile: new PassiveXamlFrameworkProfile(
                    FrameworkId,
                    XmlNamespace,
                    preferredProjectXamlItemName: "Page",
                    projectXamlItemNames: ["Page"]),
                DefaultXmlNamespace: XmlNamespace,
                XmlnsDefinitionAttributeMetadataNames: ["Microsoft.UI.Xaml.Markup.XmlnsDefinitionAttribute"],
                XmlnsPrefixAttributeMetadataNames: ["Microsoft.UI.Xaml.Markup.XmlnsPrefixAttribute"],
                MarkupExtensionNamespaces: ["Microsoft.UI.Xaml.Markup"],
                PreferredProjectXamlItemName: "Page",
                ProjectXamlItemNames: ["Page"],
                DirectiveCompletions: [],
                MarkupExtensionCompletions: []);
        }

        public XamlLanguageFrameworkInfo Framework { get; }
    }
}
