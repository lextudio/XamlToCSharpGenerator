using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

    // Set to true once the background load succeeds.  Volatile so the write
    // from the prewarm task is immediately visible on the request thread.
    private volatile bool _fullProviderReady;

    // Track the last logged tier to suppress per-request repetition.
    private volatile bool _lastLoggedTierWasFull = false;

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
    public TieredCompilationProvider(
        ICompilationProvider fullProvider,
        CompilationSnapshot? fastSnapshot = null)
    {
        _fullProvider = fullProvider ?? throw new ArgumentNullException(nameof(fullProvider));
        _fastSnapshot = fastSnapshot;
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
            // Only log on the first transition from Tier 1 → Tier 2 to avoid per-request noise.
            if (!_lastLoggedTierWasFull)
            {
                _lastLoggedTierWasFull = true;
                Console.Error.WriteLine(
                    $"[AXSG-LS] Completion tier: FULL (first time) for {Path.GetFileName(filePath)}");
            }
            return _fullProvider.GetCompilationAsync(filePath, workspaceRoot, cancellationToken);
        }

        // Tier 1: fast framework snapshot — synchronous, no wait.
        // Only log once; suppress repetitive per-keystroke messages.
        if (_lastLoggedTierWasFull)
        {
            _lastLoggedTierWasFull = false;
            Console.Error.WriteLine(
                $"[AXSG-LS] Completion tier: FAST (reverted) for {Path.GetFileName(filePath)}");
        }
        return Task.FromResult(_fastSnapshot);
    }

    public void Invalidate(string filePath)
    {
        // Only reset the tier for project file changes (.csproj/.vbproj/.fsproj)
        // that affect the Roslyn compilation.  XAML file edits do not change the
        // compilation — they only need the analysis cache to be rebuilt, which the
        // caller handles separately via InvalidateUriCaches.  Resetting the tier
        // on every XAML keystroke would revert to Tier-1 and lose user-type
        // resolution until the next prewarm completes.
        if (IsProjectFile(filePath))
        {
            _fullProviderReady = false;
        }

        _fullProvider.Invalidate(filePath);
    }

    private static bool IsProjectFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        return filePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
               filePath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
               filePath.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);
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

                if (snapshot.Compilation is not null)
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
