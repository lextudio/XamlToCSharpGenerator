using System.Collections.Immutable;
using System.Linq;

using XamlToCSharpGenerator.Framework.Abstractions;
using XamlToCSharpGenerator.LanguageService.Framework;
using XamlToCSharpGenerator.LanguageService.Framework.Maui;
using XamlToCSharpGenerator.LanguageService.Framework.Uno;
using XamlToCSharpGenerator.LanguageService.Framework.WinUI;
using XamlToCSharpGenerator.LanguageService.Framework.Wpf;

using Xunit;

namespace XamlToCSharpGenerator.Tests.LanguageService;

/// <summary>
/// The Tier-1 seed-namespace capability exists so that a language server building a fast snapshot
/// reads the CLR namespaces from the framework profile instead of hardcoding one framework's list.
/// WPF is the framework that currently needs it; a second framework (MAUI) supplies its own.
/// </summary>
public sealed class Tier1SeedNamespaceTests
{
    /// <summary>
    /// WPF's list is what the server used to keep inline. It is pinned here deliberately: the
    /// refactor that moved it onto the profile must not change which namespaces are seeded, or
    /// WPF completions would silently regress.
    /// </summary>
    static readonly string[] ExpectedWpfNamespaces =
    [
        "System.Windows",
        "System.Windows.Controls",
        "System.Windows.Controls.Primitives",
        "System.Windows.Data",
        "System.Windows.Documents",
        "System.Windows.Input",
        "System.Windows.Media",
        "System.Windows.Navigation",
        "System.Windows.Shapes",
    ];

    [Fact]
    public void Wpf_Language_Framework_Provider_Declares_The_Same_List_On_Its_Profile()
    {
        ImmutableArray<string> actual =
            WpfLanguageFrameworkProvider.Instance.Framework.Profile.Tier1SeedClrNamespaces;

        Assert.Equal(ExpectedWpfNamespaces, actual.ToArray());
    }

    [Fact]
    public void Uno_And_WinUI_Declare_An_Empty_List_Because_They_Need_No_Synthetic_Map()
    {
        Assert.Empty(UnoLanguageFrameworkProvider.Instance.Framework.Profile.Tier1SeedClrNamespaces);
        Assert.Empty(WinUiLanguageFrameworkProvider.Instance.Framework.Profile.Tier1SeedClrNamespaces);
    }

    [Fact]
    public void A_Profile_Constructed_Without_Seed_Namespaces_Defaults_To_Empty_Not_Default()
    {
        // A default(ImmutableArray<string>) here would surface as IsDefault rather than Empty,
        // which reads as "uninitialised" and breaks the "nothing to seed" decision downstream.
        var profile = new PassiveXamlFrameworkProfile(
            "test",
            "urn:test",
            "Page",
            ImmutableArray.Create("Page"));

        Assert.False(profile.Tier1SeedClrNamespaces.IsDefault);
        Assert.Empty(profile.Tier1SeedClrNamespaces);
    }

    [Fact]
    public void Every_Framework_Profile_Answers_The_Capability_So_No_Implementation_Is_Missing()
    {
        IXamlFrameworkProfile[] profiles =
        [
            WpfLanguageFrameworkProvider.Instance.Framework.Profile,
            UnoLanguageFrameworkProvider.Instance.Framework.Profile,
            WinUiLanguageFrameworkProvider.Instance.Framework.Profile,
            MauiLanguageFrameworkProvider.Instance.Framework.Profile,
        ];

        foreach (IXamlFrameworkProfile profile in profiles)
        {
            Assert.False(profile.Tier1SeedClrNamespaces.IsDefault);
        }
    }

    [Fact]
    public void Maui_Declares_Its_Own_Empty_List_So_A_Maui_Server_Can_Extend_It()
    {
        // MAUI does not need one yet, but the point of the capability is that the value comes
        // from the profile. Asserting it is empty-and-explicit (not IsDefault) is what lets a
        // later change add MAUI namespaces without touching the contract again.
        ImmutableArray<string> maui = MauiLanguageFrameworkProvider.Instance.Framework.Profile.Tier1SeedClrNamespaces;

        Assert.False(maui.IsDefault);
        Assert.Empty(maui);
    }
}
