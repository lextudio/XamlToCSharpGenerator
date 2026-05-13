using System;
using System.Collections.Immutable;
using XamlToCSharpGenerator.Core.Models;
using XamlToCSharpGenerator.LanguageService.Models;

namespace XamlToCSharpGenerator.LanguageService.Definitions;

/// <summary>
/// Helper for detecting and mapping positions within x:Code blocks.
/// Used by language services to provide completions, hover, definitions, etc. for code blocks.
/// </summary>
internal static class XamlCodeBlockPositionHelper
{
    /// <summary>
    /// Determines if the given position falls within any x:Code block in the document.
    /// </summary>
    public static bool TryFindCodeBlockAtPosition(
        ImmutableArray<XamlCodeBlockDefinition> codeBlocks,
        SourcePosition position,
        out XamlCodeBlockDefinition block,
        out int lineOffsetWithinBlock)
    {
        block = default!;
        lineOffsetWithinBlock = 0;

        foreach (var cb in codeBlocks)
        {
            var blockLineCount = CountLines(cb.RawCode);

            // Check if position is within this block's line range
            if (position.Line >= cb.Line && position.Line < cb.Line + blockLineCount)
            {
                block = cb;
                lineOffsetWithinBlock = position.Line - cb.Line;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Counts the number of lines in the code block's raw code.
    /// </summary>
    public static int CountLines(string rawCode)
    {
        if (string.IsNullOrEmpty(rawCode))
            return 1;

        var count = 1;
        foreach (var ch in rawCode)
        {
            if (ch == '\n')
                count++;
        }

        return count;
    }

    /// <summary>
    /// Checks if a Roslyn diagnostic position falls within an x:Code block.
    /// </summary>
    public static bool IsPositionInCodeBlock(
        ImmutableArray<XamlCodeBlockDefinition> codeBlocks,
        int line,
        int character)
    {
        return TryFindCodeBlockAtPosition(codeBlocks, new SourcePosition(line, character), out _, out _);
    }

    /// <summary>
    /// Gets the character position within the code block at a given XAML line.
    /// For x:Code blocks, character position is typically the same as in XAML
    /// (it's already at the right offset within the block line).
    /// </summary>
    public static int GetCharacterWithinCodeBlock(
        XamlCodeBlockDefinition block,
        SourcePosition position,
        int lineOffsetWithinBlock)
    {
        // For x:Code blocks, we use the character position as-is
        // The #line directive in generated code maintains character offset fidelity
        return position.Character;
    }

    /// <summary>
    /// Maps a range from Roslyn (in generated code) back to XAML coordinates.
    /// Roslyn positions come with line numbers adjusted by #line directives,
    /// so we just need to filter out ranges outside code blocks.
    /// </summary>
    public static bool TryMapRoslynRangeToCodeBlock(
        ImmutableArray<XamlCodeBlockDefinition> codeBlocks,
        int roslynStartLine,
        int roslynEndLine,
        out XamlCodeBlockDefinition sourceBlock,
        out SourceRange xamlRange)
    {
        sourceBlock = default!;
        xamlRange = default!;

        // The Roslyn line numbers should already be mapped via #line directives
        // Just verify they fall within a code block
        if (!TryFindCodeBlockAtPosition(codeBlocks, new SourcePosition(roslynStartLine, 0), out sourceBlock, out _))
            return false;

        xamlRange = new SourceRange(
            Start: new SourcePosition(roslynStartLine, 0),
            End: new SourcePosition(roslynEndLine, 0));

        return true;
    }
}
