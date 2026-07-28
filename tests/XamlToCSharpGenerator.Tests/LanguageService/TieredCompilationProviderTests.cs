using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlToCSharpGenerator.LanguageService.Models;
using XamlToCSharpGenerator.LanguageService.Workspace;

namespace XamlToCSharpGenerator.Tests.LanguageService;

public sealed class TieredCompilationProviderTests
{
    private static Compilation CreateCompilation(string assemblyName)
    {
        return CSharpCompilation.Create(
            assemblyName,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static CompilationSnapshot CreateSnapshot(Compilation? compilation)
    {
        return new CompilationSnapshot(
            ProjectPath: null,
            Project: null,
            Compilation: compilation,
            Diagnostics: ImmutableArray<LanguageServiceDiagnostic>.Empty);
    }

    [Fact]
    public async Task GetCompilationAsync_ServesFastSnapshot_BeforePrewarmResolves()
    {
        var fastSnapshot = CreateSnapshot(CreateCompilation("Fast"));
        var full = new GatedCompilationProvider(CreateSnapshot(CreateCompilation("Full")));

        using var provider = new TieredCompilationProvider(full, fastSnapshot);

        // Prewarm is started but the gate keeps the full provider from resolving.
        var prewarmTask = provider.PrewarmAsync("/tmp/proj.csproj", "/tmp");

        var snapshot = await provider.GetCompilationAsync("/tmp/view.axaml", "/tmp", CancellationToken.None);

        Assert.Same(fastSnapshot, snapshot);
        Assert.Equal("Fast", snapshot.Compilation!.AssemblyName);

        full.Release();
        await prewarmTask;
    }

    [Fact]
    public async Task GetCompilationAsync_SwitchesToFullProvider_OncePrewarmCompletes()
    {
        var fastSnapshot = CreateSnapshot(CreateCompilation("Fast"));
        var fullSnapshot = CreateSnapshot(CreateCompilation("Full"));
        var full = new GatedCompilationProvider(fullSnapshot);

        using var provider = new TieredCompilationProvider(full, fastSnapshot);

        var prewarmTask = provider.PrewarmAsync("/tmp/proj.csproj", "/tmp");

        // Still Tier 1 while gated.
        var beforeUpgrade = await provider.GetCompilationAsync("/tmp/view.axaml", "/tmp", CancellationToken.None);
        Assert.Same(fastSnapshot, beforeUpgrade);

        full.Release();
        await prewarmTask;

        var afterUpgrade = await provider.GetCompilationAsync("/tmp/view.axaml", "/tmp", CancellationToken.None);
        Assert.Same(fullSnapshot, afterUpgrade);
    }

    [Fact]
    public async Task PrewarmAsync_InvokesOnPrewarmCompleted_WhenCompilationIsProduced()
    {
        var fullSnapshot = CreateSnapshot(CreateCompilation("Full"));
        var full = new InstantCompilationProvider(fullSnapshot);
        using var provider = new TieredCompilationProvider(full);

        var callbackInvoked = false;
        provider.OnPrewarmCompleted = () => callbackInvoked = true;

        await provider.PrewarmAsync("/tmp/proj.csproj", "/tmp");

        Assert.True(callbackInvoked);
    }

    [Fact]
    public async Task PrewarmAsync_DoesNotInvokeCallback_WhenNoCompilationIsProduced()
    {
        var full = new InstantCompilationProvider(CreateSnapshot(compilation: null));
        using var provider = new TieredCompilationProvider(full);

        var callbackInvoked = false;
        provider.OnPrewarmCompleted = () => callbackInvoked = true;

        await provider.PrewarmAsync("/tmp/proj.csproj", "/tmp");

        Assert.False(callbackInvoked);
    }

    [Fact]
    public async Task GetCompilationAsync_SkipsTier1_WhenFastSnapshotIsNull()
    {
        var fullSnapshot = CreateSnapshot(CreateCompilation("Full"));
        var full = new InstantCompilationProvider(fullSnapshot);

        using var provider = new TieredCompilationProvider(full, fastSnapshot: null);

        // Even before prewarm, requests go straight to the full provider.
        var snapshot = await provider.GetCompilationAsync("/tmp/view.axaml", "/tmp", CancellationToken.None);

        Assert.Same(fullSnapshot, snapshot);
        Assert.Equal(1, full.CallCount);
    }

    [Fact]
    public async Task PrewarmAsync_StillWorks_WhenFastSnapshotIsNull()
    {
        var fullSnapshot = CreateSnapshot(CreateCompilation("Full"));
        var full = new InstantCompilationProvider(fullSnapshot);
        using var provider = new TieredCompilationProvider(full, fastSnapshot: null);

        var callbackInvoked = false;
        provider.OnPrewarmCompleted = () => callbackInvoked = true;

        await provider.PrewarmAsync("/tmp/proj.csproj", "/tmp");

        Assert.True(callbackInvoked);
        Assert.Equal(1, full.CallCount);
    }

    [Fact]
    public async Task Invalidate_ResetsFullProviderReady_SoNextRequestReloadsTier2()
    {
        var fastSnapshot = CreateSnapshot(CreateCompilation("Fast"));
        var fullSnapshotV1 = CreateSnapshot(CreateCompilation("FullV1"));
        var fullSnapshotV2 = CreateSnapshot(CreateCompilation("FullV2"));
        var full = new SequencedCompilationProvider(fullSnapshotV1, fullSnapshotV2);

        using var provider = new TieredCompilationProvider(full, fastSnapshot);

        await provider.PrewarmAsync("/tmp/proj.csproj", "/tmp");

        var afterFirstPrewarm = await provider.GetCompilationAsync("/tmp/view.axaml", "/tmp", CancellationToken.None);
        Assert.Same(fullSnapshotV1, afterFirstPrewarm);

        // A project file change invalidates the provider.
        provider.Invalidate("/tmp/proj.csproj");

        // Immediately after Invalidate, and before any new prewarm resolves,
        // requests must fall back to Tier 1 rather than serving the stale
        // (now-invalidated) Tier 2 snapshot.
        var afterInvalidate = await provider.GetCompilationAsync("/tmp/view.axaml", "/tmp", CancellationToken.None);
        Assert.Same(fastSnapshot, afterInvalidate);

        Assert.True(full.InvalidateCalled);

        // Re-running prewarm reloads Tier 2 (picking up the new snapshot).
        await provider.PrewarmAsync("/tmp/proj.csproj", "/tmp");
        var afterSecondPrewarm = await provider.GetCompilationAsync("/tmp/view.axaml", "/tmp", CancellationToken.None);
        Assert.Same(fullSnapshotV2, afterSecondPrewarm);
    }

    [Fact]
    public void FindFirstProjectFile_ReturnsNull_WhenNoProjectFileExists()
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            Assert.Null(TieredCompilationProvider.FindFirstProjectFile(tempDir));
        }
        finally
        {
            System.IO.Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void FindFirstProjectFile_FindsCsprojFile_SkippingBinAndObj()
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        var objDir = System.IO.Path.Combine(tempDir, "obj");
        System.IO.Directory.CreateDirectory(objDir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(objDir, "Ignored.csproj"), "<Project />");
            var realProject = System.IO.Path.Combine(tempDir, "Real.csproj");
            System.IO.File.WriteAllText(realProject, "<Project />");

            var found = TieredCompilationProvider.FindFirstProjectFile(tempDir);

            Assert.Equal(realProject, found);
        }
        finally
        {
            System.IO.Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// A compilation provider whose completion can be gated open/closed, to
    /// deterministically test the moment before/after a prewarm resolves
    /// without relying on timing/sleeps.
    /// </summary>
    private sealed class GatedCompilationProvider : ICompilationProvider
    {
        private readonly CompilationSnapshot _snapshot;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedCompilationProvider(CompilationSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public void Release() => _gate.TrySetResult();

        public async Task<CompilationSnapshot> GetCompilationAsync(
            string filePath, string? workspaceRoot, CancellationToken cancellationToken)
        {
            await _gate.Task.ConfigureAwait(false);
            return _snapshot;
        }

        public void Invalidate(string filePath)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class InstantCompilationProvider : ICompilationProvider
    {
        private readonly CompilationSnapshot _snapshot;

        public int CallCount { get; private set; }

        public InstantCompilationProvider(CompilationSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<CompilationSnapshot> GetCompilationAsync(
            string filePath, string? workspaceRoot, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_snapshot);
        }

        public void Invalidate(string filePath)
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Returns the snapshot for the current "generation": generation 0 until
    /// <see cref="Invalidate"/> is called, generation 1 afterward — simulating
    /// a reload producing a different result after a project file changed.
    /// Every request is still routed through the real
    /// <c>GetCompilationAsync</c> call each time (matching production
    /// behavior, where Tier 2 is re-fetched whenever the caller asks for it),
    /// but the returned snapshot only changes once <see cref="Invalidate"/>
    /// has actually been invoked.
    /// </summary>
    private sealed class SequencedCompilationProvider : ICompilationProvider
    {
        private readonly CompilationSnapshot[] _snapshots;
        private int _generation;

        public bool InvalidateCalled { get; private set; }

        public SequencedCompilationProvider(params CompilationSnapshot[] snapshots)
        {
            _snapshots = snapshots;
        }

        public Task<CompilationSnapshot> GetCompilationAsync(
            string filePath, string? workspaceRoot, CancellationToken cancellationToken)
        {
            var index = Math.Min(_generation, _snapshots.Length - 1);
            return Task.FromResult(_snapshots[index]);
        }

        public void Invalidate(string filePath)
        {
            InvalidateCalled = true;
            _generation++;
        }

        public void Dispose()
        {
        }
    }
}
