using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace XamlToCSharpGenerator.LanguageService.Workspace;

/// <summary>
/// A compilation provider that separates the completion experience into two
/// tiers and eliminates the cold-start wait users experience before IntelliSense
/// suggestions appear.
///
/// <list type="number">
///   <item>
///     <b>Tier 1 — fast snapshot (optional, framework-specific).</b>  A
///     lightweight compilation snapshot supplied by the caller.  It is returned
///     synchronously for every request while the full MSBuild compilation is
///     still loading.  For WPF this is a compilation built from the
///     <c>Microsoft.WindowsDesktop.App</c> shared framework assemblies; for
///     other frameworks the caller may omit it.
///   </item>
///   <item>
///     <b>Tier 2 — full MSBuild compilation (background).</b>  Loaded via the
///     inner <see cref="ICompilationProvider"/> after <see cref="PrewarmAsync"/>
///     is called at server startup.  Once ready, all requests switch to the
///     full compilation automatically, giving the editor NuGet packages and
///     user-project types.
///   </item>
/// </list>
///
/// <para>
/// When no <paramref name="fastSnapshot"/> is provided (e.g. for Avalonia,
/// whose assemblies are project-specific NuGet packages rather than SDK-wide
/// framework assemblies) the provider still supports the prewarm: the background
/// MSBuild load starts at server startup instead of on the first completion
/// keystroke, which cuts the wait significantly.
/// </para>
///
/// <para>
/// The upgrade from Tier 1 → Tier 2 is transparent to the caller.  Because
/// the engine's analysis cache is keyed on document version, the first document
/// edit after the upgrade causes a re-analysis using the full compilation.
/// </para>
/// </summary>
public sealed class TieredCompilationProvider : ICompilationProvider
{
    private readonly ICompilationProvider _fullProvider;

    // Optional Tier-1 snapshot supplied by the framework-specific server.
    private readonly CompilationSnapshot? _fastSnapshot;

    // What the full compilation lacks that Tier 1 offers; see the constructor.
    private readonly Func<Compilation, ImmutableArray<string>> _fastSnapshotGaps;

    // Set to true once the background load succeeds.  Volatile so the write
    // from the prewarm task is immediately visible on the request thread.
    private volatile bool _fullProviderReady;

    /// <summary>
    /// Optional callback invoked when the background prewarm completes.
    /// Called from <see cref="PrewarmAsync"/> after <c>_fullProviderReady</c> is set to true.
    /// </summary>
    public Action? OnPrewarmCompleted { get; set; }

    /// <param name="fullProvider">
    ///   The full MSBuild-backed compilation provider.  Its compilation loads
    ///   lazily — this class controls when the load is triggered.
    /// </param>
    /// <param name="fastSnapshot">
    ///   Optional Tier-1 snapshot served immediately while the full provider
    ///   is loading.  Pass <see langword="null"/> to skip Tier 1 (the provider
    ///   will still prewarm the full compilation in the background).
    /// </param>
    /// <param name="fastSnapshotGaps">
    ///   Optional: what the full compilation lacks that Tier 1 offers (empty = it may replace
    ///   Tier 1). Defaults to <see cref="MissingFastSnapshotAssemblies"/>. A host whose Tier 1 is a
    ///   stand-in - WinUI served from Uno's assemblies - supplies its own check, because the real
    ///   project never references the stand-in's assemblies and would otherwise never upgrade.
    /// </param>
    public TieredCompilationProvider(
        ICompilationProvider fullProvider,
        CompilationSnapshot? fastSnapshot = null,
        Func<Compilation, ImmutableArray<string>>? fastSnapshotGaps = null)
    {
        _fullProvider = fullProvider ?? throw new ArgumentNullException(nameof(fullProvider));
        _fastSnapshot = fastSnapshot;
        _fastSnapshotGaps = fastSnapshotGaps
                            ?? (full => MissingFastSnapshotAssemblies(full, fastSnapshot?.Compilation));
    }


    // -------------------------------------------------------------------------
    // ICompilationProvider
    // -------------------------------------------------------------------------

    public Task<CompilationSnapshot> GetCompilationAsync(
        string filePath,
        string? workspaceRoot,
        CancellationToken cancellationToken)
    {
        if (_fullProviderReady || _fastSnapshot is null)
        {
            // Tier 2: full MSBuild compilation (or no fast snapshot available).
            Console.Error.WriteLine(
                $"[AXSG-LS] Completion tier: FULL for {Path.GetFileName(filePath)}");
            return _fullProvider.GetCompilationAsync(filePath, workspaceRoot, cancellationToken);
        }

        // Tier 1: fast framework snapshot — synchronous, no wait.
        Console.Error.WriteLine(
            $"[AXSG-LS] Completion tier: FAST (MSBuild loading) for {Path.GetFileName(filePath)}");
        return Task.FromResult(_fastSnapshot);
    }

    public void Invalidate(string filePath)
    {
        // Reset so a project file change causes the full compilation to be
        // reloaded rather than serving stale data.
        _fullProviderReady = false;
        _fullProvider.Invalidate(filePath);
    }

    public void Dispose() => _fullProvider.Dispose();

    // -------------------------------------------------------------------------
    // Prewarm
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts the full MSBuild compilation load in the background.
    /// <para>
    /// Call this at server startup (after the LSP server loop begins) so the
    /// MSBuild evaluation runs in parallel with early editor activity.  The
    /// returned <see cref="Task"/> completes when the compilation is cached and
    /// the provider has switched to Tier 2.
    /// </para>
    /// </summary>
    /// <param name="projectFileOrPath">
    ///   Path of a <c>.csproj</c> file (or any file inside the project) that
    ///   the inner provider will use to locate the MSBuild project.
    /// </param>
    /// <param name="workspaceRoot">The LSP workspace root, forwarded to the inner provider.</param>
    public Task PrewarmAsync(string projectFileOrPath, string? workspaceRoot)
    {
        return Task.Run(async () =>
        {
            try
            {
                Console.Error.WriteLine(
                    $"[AXSG-LS] Background compilation prewarm started for {projectFileOrPath}");

                var snapshot = await _fullProvider
                    .GetCompilationAsync(projectFileOrPath, workspaceRoot, CancellationToken.None)
                    .ConfigureAwait(false);

                var missing = snapshot.Compilation is null
                    ? ImmutableArray<string>.Empty
                    : _fastSnapshot is null ? ImmutableArray<string>.Empty : _fastSnapshotGaps(snapshot.Compilation);
                if (snapshot.Compilation is not null && !missing.IsEmpty)
                {
                    // A project that has not been restored (or whose evaluation failed part-way)
                    // still yields a Compilation, just without its framework's assemblies.
                    // Upgrading to it replaced a working Tier 1 with one in which no control
                    // resolves, so every completion went empty the moment prewarm finished.
                    Console.Error.WriteLine(
                        "[AXSG-LS] Prewarm: full compilation lacks what Tier 1 offers (" +
                        string.Join(", ", missing) + "); staying on Tier 1.");
                }
                else if (snapshot.Compilation is not null)
                {
                    _fullProviderReady = true;
                    Console.Error.WriteLine(
                        "[AXSG-LS] Prewarm complete — upgraded to Tier 2 (full MSBuild compilation).");
                    OnPrewarmCompleted?.Invoke();
                }
                else
                {
                    Console.Error.WriteLine(
                        "[AXSG-LS] Prewarm: full compilation produced no Roslyn Compilation object.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AXSG-LS] Prewarm failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Names of assemblies the Tier-1 compilation references that <paramref name="full"/> does
    /// not. Empty means the full compilation can see at least everything Tier 1 could, so upgrading
    /// cannot lose a type; no fast compilation means there is nothing to lose.
    /// </summary>
    public static ImmutableArray<string> MissingFastSnapshotAssemblies(Compilation full, Compilation? fast)
    {
        if (fast is null)
        {
            return ImmutableArray<string>.Empty;
        }

        var fullNames = new HashSet<string>(ReferencedAssemblyNames(full), StringComparer.OrdinalIgnoreCase);
        return ReferencedAssemblyNames(fast)
            .Where(name => !fullNames.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static IEnumerable<string> ReferencedAssemblyNames(Compilation compilation) =>
        compilation.References
            .Select(compilation.GetAssemblyOrModuleSymbol)
            .OfType<IAssemblySymbol>()
            .Select(static assembly => assembly.Identity.Name);

    // -------------------------------------------------------------------------
    // Workspace discovery helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the first <c>.csproj</c> file found directly under
    /// <paramref name="workspaceRoot"/>, skipping <c>bin/</c> and <c>obj/</c>
    /// subtrees.  Returns <see langword="null"/> if none is found.
    /// </summary>
    public static string? FindFirstProjectFile(string workspaceRoot)
    {
        try
        {
            return Directory
                .GetFiles(workspaceRoot, "*.csproj", SearchOption.AllDirectories)
                .Where(static p =>
                    p.IndexOf(
                        Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    p.IndexOf(
                        Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) < 0)
                .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
