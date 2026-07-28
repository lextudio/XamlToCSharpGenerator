using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlToCSharpGenerator.LanguageService.Workspace;

namespace XamlToCSharpGenerator.Tests.LanguageService;

/// <summary>
/// Covers <see cref="AvaloniaFastCompilationProvider"/>: locating/parsing the
/// Avalonia SDK's <c>Avalonia/references</c> build artifact, version
/// detection, snapshot construction, and the on-disk Tier-1 cache round trip.
///
/// <para>
/// <see cref="AvaloniaFastCompilationProvider"/>'s on-disk cache lives at a
/// fixed, machine-global path under <c>LocalApplicationData</c> (not scoped
/// to a temp/test directory), so this class backs up and restores whatever
/// is already there around every test to avoid leaking test data into (or
/// clobbering) the real developer cache. Tests in this class run
/// sequentially (the xunit default within a single test class), so they do
/// not race each other over that shared file.
/// </para>
/// </summary>
public sealed class AvaloniaFastCompilationProviderTests : IDisposable
{
    private readonly string _cacheFilePath;
    private readonly string? _originalCacheContent;
    private readonly bool _cacheFileExisted;

    public AvaloniaFastCompilationProviderTests()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cacheFilePath = Path.Combine(local, "LeXtudio", "axaml-ls", "tier1-cache.json");
        _cacheFileExisted = File.Exists(_cacheFilePath);
        _originalCacheContent = _cacheFileExisted ? File.ReadAllText(_cacheFilePath) : null;

        if (_cacheFileExisted)
        {
            File.Delete(_cacheFilePath);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_cacheFilePath))
        {
            File.Delete(_cacheFilePath);
        }

        if (_cacheFileExisted && _originalCacheContent is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
            File.WriteAllText(_cacheFilePath, _originalCacheContent);
        }
    }

    // -------------------------------------------------------------------
    // FindReferencesFile
    // -------------------------------------------------------------------

    [Fact]
    public void FindReferencesFile_ReturnsNull_WhenWorkspaceRootIsNullOrWhitespace()
    {
        Assert.Null(AvaloniaFastCompilationProvider.FindReferencesFile(null!));
        Assert.Null(AvaloniaFastCompilationProvider.FindReferencesFile("  "));
    }

    [Fact]
    public void FindReferencesFile_ReturnsNull_WhenNoProjectFileExists()
    {
        using var workspace = new TempDirectory();
        Assert.Null(AvaloniaFastCompilationProvider.FindReferencesFile(workspace.Path));
    }

    [Fact]
    public void FindReferencesFile_ReturnsNull_WhenProjectExistsButNoReferencesArtifact()
    {
        using var workspace = new TempDirectory();
        File.WriteAllText(Path.Combine(workspace.Path, "App.csproj"), "<Project />");

        Assert.Null(AvaloniaFastCompilationProvider.FindReferencesFile(workspace.Path));
    }

    /// <summary>
    /// Documents a real quirk of the current implementation: the probe
    /// patterns embed a literal <c>"*"</c> path segment (e.g.
    /// <c>obj/Debug/*/Avalonia/references</c>) and pass it straight to
    /// <see cref="Directory.Exists(string)"/>, which does NOT glob — it only
    /// matches a directory literally named <c>"*"</c>. As a result, a
    /// references file sitting under the real TFM directory (e.g.
    /// <c>obj/Debug/net8.0/Avalonia/references</c>, exactly what the Avalonia
    /// SDK actually emits) is never found by this probe today. This is
    /// existing behavior being locked in as-is, not a desired outcome — see
    /// the companion test below showing the literal <c>"*"</c> directory
    /// case that the current code does match.
    /// </summary>
    [Fact]
    public void FindReferencesFile_ReturnsNull_ForRealTfmDirectory_DueToNonGlobbingProbe()
    {
        using var workspace = new TempDirectory();
        File.WriteAllText(Path.Combine(workspace.Path, "App.csproj"), "<Project />");

        var avaloniaDir = Path.Combine(workspace.Path, "obj", "Debug", "net8.0", "Avalonia");
        Directory.CreateDirectory(avaloniaDir);
        File.WriteAllText(Path.Combine(avaloniaDir, "references"), "/some/fake/Avalonia.Base.dll");

        Assert.Null(AvaloniaFastCompilationProvider.FindReferencesFile(workspace.Path));
    }

    [Fact]
    public void FindReferencesFile_LocatesArtifact_WhenTfmSegmentIsLiterallyAnAsterisk()
    {
        using var workspace = new TempDirectory();
        File.WriteAllText(Path.Combine(workspace.Path, "App.csproj"), "<Project />");

        // Matches the literal probe pattern "obj/Debug/*/Avalonia/references".
        var avaloniaDir = Path.Combine(workspace.Path, "obj", "Debug", "*", "Avalonia");
        Directory.CreateDirectory(avaloniaDir);
        var referencesFile = Path.Combine(avaloniaDir, "references");
        File.WriteAllText(referencesFile, "/some/fake/Avalonia.Base.dll");

        var found = AvaloniaFastCompilationProvider.FindReferencesFile(workspace.Path);

        Assert.Equal(referencesFile, found);
    }

    // -------------------------------------------------------------------
    // GetAvaloniaVersionFromReferencesFile
    // -------------------------------------------------------------------

    [Fact]
    public void GetAvaloniaVersionFromReferencesFile_ReturnsNull_WhenFileDoesNotExist()
    {
        Assert.Null(AvaloniaFastCompilationProvider.GetAvaloniaVersionFromReferencesFile("/no/such/references"));
    }

    [Fact]
    public void GetAvaloniaVersionFromReferencesFile_ReturnsNull_WhenNoAvaloniaBaseDllListed()
    {
        using var workspace = new TempDirectory();
        var referencesFile = Path.Combine(workspace.Path, "references");
        var otherDll = CopyRealAssembly(workspace.Path, "SomeOther.dll");
        File.WriteAllLines(referencesFile, new[] { otherDll });

        Assert.Null(AvaloniaFastCompilationProvider.GetAvaloniaVersionFromReferencesFile(referencesFile));
    }

    [Fact]
    public void GetAvaloniaVersionFromReferencesFile_ReturnsVersion_WhenAvaloniaBaseDllListedAndExists()
    {
        using var workspace = new TempDirectory();
        var avaloniaBaseDll = CopyRealAssembly(workspace.Path, "Avalonia.Base.dll");
        var referencesFile = Path.Combine(workspace.Path, "references");
        File.WriteAllLines(referencesFile, new[] { avaloniaBaseDll });

        var version = AvaloniaFastCompilationProvider.GetAvaloniaVersionFromReferencesFile(referencesFile);

        Assert.False(string.IsNullOrWhiteSpace(version));

        var expected = System.Diagnostics.FileVersionInfo.GetVersionInfo(avaloniaBaseDll);
        var expectedVersion = !string.IsNullOrWhiteSpace(expected.ProductVersion)
            ? expected.ProductVersion
            : expected.FileVersion;
        Assert.Equal(expectedVersion, version);
    }

    [Fact]
    public void GetAvaloniaVersionFromReferencesFile_IgnoresLineWithMatchingNameButMissingFile()
    {
        using var workspace = new TempDirectory();
        var referencesFile = Path.Combine(workspace.Path, "references");
        // Path looks right but the file does not actually exist on disk.
        File.WriteAllLines(referencesFile, new[] { Path.Combine(workspace.Path, "Avalonia.Base.dll") });

        Assert.Null(AvaloniaFastCompilationProvider.GetAvaloniaVersionFromReferencesFile(referencesFile));
    }

    // -------------------------------------------------------------------
    // BuildFastSnapshot
    // -------------------------------------------------------------------

    [Fact]
    public void BuildFastSnapshot_ReturnsNull_WhenFileDoesNotExist()
    {
        Assert.Null(AvaloniaFastCompilationProvider.BuildFastSnapshot("/no/such/references"));
    }

    [Fact]
    public void BuildFastSnapshot_ReturnsNull_WhenNoValidReferencesFound()
    {
        using var workspace = new TempDirectory();
        var referencesFile = Path.Combine(workspace.Path, "references");
        File.WriteAllLines(referencesFile, new[] { "/does/not/exist.dll", "" });

        Assert.Null(AvaloniaFastCompilationProvider.BuildFastSnapshot(referencesFile));
    }

    [Fact]
    public void BuildFastSnapshot_BuildsCompilation_FromValidReferencesFile()
    {
        using var workspace = new TempDirectory();
        var dll1 = CopyRealAssembly(workspace.Path, "Ref1.dll");
        var dll2 = CopyRealAssemblyOf(typeof(Uri), workspace.Path, "Ref2.dll");
        var referencesFile = Path.Combine(workspace.Path, "references");
        File.WriteAllLines(referencesFile, new[] { dll1, dll2, string.Empty, "   " });

        var snapshot = AvaloniaFastCompilationProvider.BuildFastSnapshot(referencesFile);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.Compilation);
        Assert.Equal("AvaloniaCore", snapshot.Compilation!.AssemblyName);
        Assert.Equal(2, snapshot.Compilation.References.Count());
    }

    // -------------------------------------------------------------------
    // PersistToDisk / TryLoadFromCache round trip
    // -------------------------------------------------------------------

    [Fact]
    public void TryLoadFromCache_ReturnsNull_WhenNoCacheFileExists()
    {
        Assert.Null(AvaloniaFastCompilationProvider.TryLoadFromCache("11.0.0"));
    }

    [Fact]
    public void PersistToDisk_ThenTryLoadFromCache_RoundTripsAUsableSnapshot()
    {
        using var workspace = new TempDirectory();
        var refPath = CopyRealAssembly(workspace.Path, "CoreRef.dll");
        var compilation = CreateAvaloniaLikeCompilation(refPath);

        AvaloniaFastCompilationProvider.PersistToDisk(compilation, "11.3.7");

        var loaded = AvaloniaFastCompilationProvider.TryLoadFromCache("11.3.7");

        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.Compilation);
        Assert.Equal("AvaloniaCore", loaded.Compilation!.AssemblyName);
        Assert.Single(loaded.Compilation.References);
    }

    [Fact]
    public void TryLoadFromCache_ReturnsNull_WhenRequestedVersionIsNewerThanCache()
    {
        using var workspace = new TempDirectory();
        var refPath = CopyRealAssembly(workspace.Path, "CoreRef.dll");
        var compilation = CreateAvaloniaLikeCompilation(refPath);

        AvaloniaFastCompilationProvider.PersistToDisk(compilation, "11.0.0");

        // A cache for 11.0.0 must not be served to a project pinned to 12.0.0 —
        // it might be missing newer APIs.
        Assert.Null(AvaloniaFastCompilationProvider.TryLoadFromCache("12.0.0"));
    }

    [Fact]
    public void TryLoadFromCache_ServesCache_WhenCachedVersionIsNewerOrEqual()
    {
        using var workspace = new TempDirectory();
        var refPath = CopyRealAssembly(workspace.Path, "CoreRef.dll");
        var compilation = CreateAvaloniaLikeCompilation(refPath);

        AvaloniaFastCompilationProvider.PersistToDisk(compilation, "11.5.0");

        // Newer caches are usable for older/equal project versions.
        Assert.NotNull(AvaloniaFastCompilationProvider.TryLoadFromCache("11.0.0"));
        Assert.NotNull(AvaloniaFastCompilationProvider.TryLoadFromCache("11.5.0"));
    }

    [Fact]
    public void PersistToDisk_DoesNotDowngrade_ExistingNewerCache()
    {
        using var workspace = new TempDirectory();
        var refPath = CopyRealAssembly(workspace.Path, "CoreRef.dll");
        var compilation = CreateAvaloniaLikeCompilation(refPath);

        AvaloniaFastCompilationProvider.PersistToDisk(compilation, "11.5.0");
        // Attempt to persist an older version over the newer cache — should be skipped.
        AvaloniaFastCompilationProvider.PersistToDisk(compilation, "11.0.0");

        // If the (older) write had won, requesting 11.5.0 would now miss.
        Assert.NotNull(AvaloniaFastCompilationProvider.TryLoadFromCache("11.5.0"));
    }

    [Fact]
    public void PersistToDisk_DoesNothing_WhenVersionIsNullOrWhitespace()
    {
        using var workspace = new TempDirectory();
        var refPath = CopyRealAssembly(workspace.Path, "CoreRef.dll");
        var compilation = CreateAvaloniaLikeCompilation(refPath);

        AvaloniaFastCompilationProvider.PersistToDisk(compilation, "  ");

        Assert.False(File.Exists(GetCacheFilePathForAssertion()));
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static string GetCacheFilePathForAssertion()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "LeXtudio", "axaml-ls", "tier1-cache.json");
    }

    /// <summary>
    /// Builds a compilation containing a type mapped to the Avalonia default
    /// XML namespace ("https://github.com/avaloniaui") via a locally-defined
    /// XmlnsDefinitionAttribute, plus a real on-disk metadata reference — this
    /// mirrors the shape <see cref="AvaloniaFastCompilationProvider.PersistToDisk"/>
    /// needs (a compilation whose <see cref="AvaloniaTypeIndex"/> yields at
    /// least one Avalonia-namespaced type, and whose references resolve back
    /// to real files on disk for the round trip).
    /// </summary>
    private static Compilation CreateAvaloniaLikeCompilation(string referencePath)
    {
        const string source = """
                              using System;

                              namespace Avalonia.Metadata
                              {
                                  [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                                  public sealed class XmlnsDefinitionAttribute : Attribute
                                  {
                                      public XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace) { }
                                  }
                              }

                              [assembly: Avalonia.Metadata.XmlnsDefinitionAttribute("https://github.com/avaloniaui", "Avalonia.Controls")]

                              namespace Avalonia.Controls
                              {
                                  public class TextBlock { }
                              }
                              """;

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var reference = MetadataReference.CreateFromFile(referencePath);

        return CSharpCompilation.Create(
            "TestAvaloniaCore",
            new[] { syntaxTree },
            new MetadataReference[] { reference },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static string CopyRealAssembly(string destinationDir, string destinationFileName)
        => CopyRealAssemblyOf(typeof(object), destinationDir, destinationFileName);

    private static string CopyRealAssemblyOf(Type type, string destinationDir, string destinationFileName)
    {
        var sourcePath = type.Assembly.Location;
        var destinationPath = Path.Combine(destinationDir, destinationFileName);
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return destinationPath;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; ignore failures (e.g. file locks).
            }
        }
    }
}
