using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlToCSharpGenerator.LanguageService;
using XamlToCSharpGenerator.LanguageService.Framework;
using XamlToCSharpGenerator.LanguageService.Symbols;
using XamlToCSharpGenerator.LanguageService.Workspace;
using XamlToCSharpGenerator.LanguageService.Workspace.Tier1;
using XamlToCSharpGenerator.LanguageServer.Protocol;
using XamlToCSharpGenerator.LanguageServer.Server;

namespace XamlToCSharpGenerator.LanguageServer.Hosting;

/// <summary>
/// The body of a single-framework XAML language server. Each framework ships its own server
/// executable - wpf-xaml-ls, winui-xaml-ls, uno-xaml-ls, maui-xaml-ls - whose entry point is one
/// call to <see cref="RunAsync"/> with that framework's profile and Tier-1 reference set, and
/// nothing else. A server therefore serves exactly one framework and references only that
/// framework's profile and assemblies: frameworks are never served by one another's server, and no
/// command-line switch can make one serve another's XAML.
/// </summary>
public static class XamlLanguageServerHost
{
    /// <param name="args">The process arguments; <c>--workspace &lt;dir&gt;</c> names the project
    /// directory whose project the full (Tier-2) compilation is prewarmed from.</param>
    /// <param name="framework">The one framework this server serves.</param>
    /// <param name="referenceSet">Where its Tier-1 assemblies come from, or null to serve from the
    /// project's own compilation only.</param>
    /// <param name="decorateFullProvider">Optional wrapper around the MSBuild provider (a server's
    /// own diagnostics logging, for instance).</param>
    public static async Task<int> RunAsync(
        string[] args,
        XamlLanguageFrameworkInfo framework,
        ITier1ReferenceSet? referenceSet,
        Func<ICompilationProvider, ICompilationProvider>? decorateFullProvider = null)
    {
        ArgumentNullException.ThrowIfNull(framework);

        // Trace output must not corrupt the LSP stdio stream.
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var log = $"[{referenceSet?.Name ?? framework.Id}-LS]";
        var workspaceRoot = ParseArg(args, "--workspace");
        Console.Error.WriteLine($"{log} Starting. workspaceRoot={workspaceRoot ?? "(null)"}");
        Console.Error.WriteLine($"{log} Args: [{string.Join(", ", args)}]");
        Console.Error.WriteLine($"{log} Framework: {framework.Id} (presentation xmlns {framework.DefaultXmlNamespace})");

        // A host dedicated to one framework passes its id, which short-circuits the engine's
        // framework auto-detection (see IXamlLanguageFrameworkProvider).
        var options = new XamlLanguageServiceOptions(workspaceRoot, framework.Id);

        // Tier 1 (framework core, instant): a Roslyn compilation built from the framework's own
        // assemblies, so standard element / attribute completions appear before MSBuild finishes.
        // Tier 2 (full, background): the project's own MSBuild compilation, which also brings its
        // packages and user-defined controls. TieredCompilationProvider owns the handoff.
        var fastSnapshot = referenceSet is null ? null : FastCompilationProvider.BuildFastSnapshot(framework, referenceSet);
        if (referenceSet is null)
        {
            Console.Error.WriteLine($"{log} No Tier-1 reference set; serving from the project's own compilation only.");
        }

        if (fastSnapshot?.Compilation is { } fastCompilation)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                // Under the served framework: the analysis looks the index up by framework id.
                _ = AvaloniaTypeIndex.Create(fastCompilation, framework);
                Console.Error.WriteLine($"{log} Tier-1 type index ready in {stopwatch.ElapsedMilliseconds} ms.");
                Console.Error.WriteLine(FastCompilationProvider.PersistTypeIndexToDisk(fastCompilation, framework, referenceSet!));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{log} Tier-1 type index prewarm failed: {ex.Message}");
            }
        }

        ICompilationProvider fullProvider = new MsBuildCompilationProvider();
        if (decorateFullProvider is not null)
        {
            fullProvider = decorateFullProvider(fullProvider);
        }

        var tieredProvider = new TieredCompilationProvider(
            fullProvider,
            fastSnapshot,
            // Tier 2 takes over once the project's compilation has the framework's own types. An
            // unrestored project still yields a Compilation - one in which no control resolves.
            fastSnapshotGaps: referenceSet is null
                ? null
                : full => referenceSet.AnchorTypes
                    .Where(type => full.GetTypesByMetadataName(type).IsEmpty)
                    .ToImmutableArray());

        using var engine = new XamlLanguageServiceEngine(tieredProvider);
        using var server = new AxsgLanguageServer(
            new LspMessageReader(Console.OpenStandardInput()),
            new LspMessageWriter(Console.OpenStandardOutput()),
            engine,
            options);

        // After the upgrade, analyses cached during Tier 1 are stale (e.g. "type not found" for a
        // project type Tier 1 cannot see): bump their generation and tell the client to re-pull.
        tieredProvider.OnPrewarmCompleted = () =>
        {
            engine.InvalidateAllOpenDocumentCaches();
            _ = server.NotifyCacheReadyAsync(framework.Id);
        };

        if (workspaceRoot is not null)
        {
            var projectFile = TieredCompilationProvider.FindFirstProjectFile(workspaceRoot);
            if (projectFile is not null)
            {
                Console.Error.WriteLine($"{log} Starting background prewarm for {projectFile}");
                _ = tieredProvider.PrewarmAsync(projectFile, workspaceRoot);
            }
            else
            {
                Console.Error.WriteLine($"{log} No project file (.csproj/.vbproj/.fsproj) found in workspace — prewarm skipped.");
            }
        }

        return await server.RunAsync(CancellationToken.None).ConfigureAwait(false);
    }

    static string? ParseArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
