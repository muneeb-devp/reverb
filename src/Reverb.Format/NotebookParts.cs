using System.Collections.Immutable;

namespace Reverb.Format;

/// <summary>The public mirror of the internal raw cell text. Kind is one of setup, code, markdown, types.</summary>
public sealed record RawCellData(
    string MarkerLine,
    string LeadingPad,
    string Content,
    string TrailingPad,
    bool WasLast,
    string Kind,
    string? Id,
    IReadOnlyList<CellAttributeData> Attributes,
    string Source,
    int MarkerLineNumber);

public sealed record CellAttributeData(string Key, string? Value);

public sealed record PartCell(
    string Kind,
    string? Id,
    string Source,
    IReadOnlyList<CellAttributeData> Attributes,
    RawCellData? Raw);

public sealed record NotebookPartsData(
    IReadOnlyList<PreambleLine> Preamble,
    string Newline,
    bool Bom,
    IReadOnlyList<PartCell> Cells);

/// <summary>
/// Splits a document into plain data a client can store, and joins it back. Each cell keeps its
/// raw text, so cells the client didn't change are written back exactly.
/// </summary>
public static class NotebookParts
{
    public static NotebookPartsData Split(NotebookDocument doc) => new(
        [.. doc.Preamble.Lines],
        doc.Newline,
        doc.HasBom,
        [.. doc.Cells.Select(c => new PartCell(
            KindName(c.Kind), c.Id, c.Source, ToData(c.Attributes), c.Raw is null ? null : ToData(c.Raw)))]);

    public static NotebookDocument Join(NotebookPartsData parts) => new()
    {
        Newline = parts.Newline,
        HasBom = parts.Bom,
        Preamble = new Preamble([.. parts.Preamble]),
        Cells = [.. parts.Cells.Select(c => new NotebookCell
        {
            Kind = ParseKind(c.Kind),
            Id = c.Id,
            Source = c.Source,
            Attributes = FromData(c.Attributes),
            Raw = c.Raw is null ? null : FromData(c.Raw),
        })],
    };

    static string KindName(CellKind kind) => kind switch
    {
        CellKind.Setup => "setup",
        CellKind.Code => "code",
        CellKind.Markdown => "markdown",
        CellKind.Types => "types",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    static CellKind ParseKind(string kind) => kind switch
    {
        "setup" => CellKind.Setup,
        "code" => CellKind.Code,
        "markdown" => CellKind.Markdown,
        "types" => CellKind.Types,
        _ => throw new ArgumentException($"'{kind}' isn't a cell kind. Use setup, code, markdown or types."),
    };

    static CellAttributeData[] ToData(ImmutableArray<CellAttribute> attributes) =>
        [.. attributes.Select(a => new CellAttributeData(a.Key, a.Value))];

    static ImmutableArray<CellAttribute> FromData(IReadOnlyList<CellAttributeData> attributes) =>
        [.. attributes.Select(a => new CellAttribute(a.Key, a.Value))];

    static RawCellData ToData(RawCell r) => new(
        r.MarkerLine, r.LeadingPad, r.Content, r.TrailingPad, r.WasLast,
        KindName(r.Kind), r.Id, ToData(r.Attributes), r.Source, r.MarkerLineNumber);

    static RawCell FromData(RawCellData r) => new(
        r.MarkerLine, r.LeadingPad, r.Content, r.TrailingPad, r.WasLast,
        ParseKind(r.Kind), r.Id, FromData(r.Attributes), r.Source, r.MarkerLineNumber);
}
