namespace XamlToCSharpGenerator.LanguageService;

public sealed record XamlLanguageServiceOptions(
    string? WorkspaceRoot,
    string? FrameworkId = null,
    bool IncludeCompilationDiagnostics = true,
    bool IncludeSemanticDiagnostics = true,
    string? AvaloniaVersion = null)
{
    public static XamlLanguageServiceOptions Default { get; } = new(
        WorkspaceRoot: null,
        FrameworkId: null,
        IncludeCompilationDiagnostics: true,
        IncludeSemanticDiagnostics: true);
}
