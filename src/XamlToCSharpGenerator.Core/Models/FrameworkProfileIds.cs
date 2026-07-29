namespace XamlToCSharpGenerator.Core.Models;

public static class FrameworkProfileIds
{
    public const string Avalonia = "Avalonia";
    public const string NoUi = "NoUi";
    public const string Wpf = "WPF";
    public const string WinUI = "WinUI";
    public const string Maui = "MAUI";

    /// <summary>
    /// Uno Platform. Its XAML dialect is WinUI's — same presentation xmlns,
    /// <c>using:</c> prefixes, <c>x:Bind</c> — served by the Uno.UI assemblies
    /// rather than the Windows App SDK, so completions come from whatever
    /// <c>Microsoft.UI.Xaml.*</c> types the project actually references. It gets
    /// its own id (instead of reusing <see cref="WinUI"/>) so a host can target
    /// Uno explicitly and so Uno-specific behaviour has somewhere to live.
    /// </summary>
    public const string Uno = "Uno";
}
