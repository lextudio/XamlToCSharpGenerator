namespace XamlToCSharpGenerator.Core.Models;

public sealed record XamlCodeBlockDefinition(
    string RawCode,
    int Line,
    int Column,
    ConditionalXamlExpression? Condition = null);
