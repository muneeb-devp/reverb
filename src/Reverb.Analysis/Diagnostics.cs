using Microsoft.CodeAnalysis;

namespace Reverb.Analysis;

/// <summary>A range inside a cell's source. Lines and columns are 0-based.</summary>
public readonly record struct CellRange(int StartLine, int StartColumn, int EndLine, int EndColumn)
{
    public static readonly CellRange Start = new(0, 0, 0, 0);

    public override string ToString() => $"{StartLine + 1}:{StartColumn + 1}-{EndLine + 1}:{EndColumn + 1}";
}

/// <summary>
/// A compiler or Reverb diagnostic, attached to a cell. <see cref="CellKey"/> is null for
/// problems with the notebook as a whole, such as a bad <c>#:</c> directive.
/// </summary>
public sealed record NotebookDiagnostic(string Id, DiagnosticSeverity Severity, string Message, string? CellKey, CellRange Range)
{
    public bool IsError => Severity == DiagnosticSeverity.Error;

    public override string ToString() =>
        $"{CellKey ?? "notebook"} {Range} {Severity.ToString().ToLowerInvariant()} {Id}: {Message}";
}

public static class RuleIds
{
    public const string AssignsOtherCellsVariable = "RVB001";
    public const string AnonymousTypeShared = "RVB002";
    public const string TopLevelReturn = "RVB003";
    public const string MisplacedDeclaration = "RVB004";
    public const string MutatesOtherCellsVariable = "RVB005";
    public const string LocalFunctionFromLaterCell = "RVB006";
    public const string ValueCannotCrossCells = "RVB007";
    public const string GeneratorNotLoaded = "RVB106";
}
