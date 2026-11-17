namespace Reverb.Format;

/// <summary>
/// Turns any cell list into a valid one. Editors let users paste, duplicate and move cells
/// freely, so ids can be missing or repeated and setup or types cells can end up anywhere.
/// </summary>
public static class CellListNormalizer
{
    /// <summary>Returns the valid list and whether anything had to change.</summary>
    public static (IReadOnlyList<PartCell> Cells, bool Changed) Normalize(IReadOnlyList<PartCell> cells, Random random)
    {
        var taken = new HashSet<string>();
        foreach (var c in cells)
            if (c.Id is { } id) taken.Add(id);

        var used = new HashSet<string>();
        var fixedCells = new List<PartCell>(cells.Count);
        var haveSetup = false;
        var haveTypes = false;

        foreach (var cell in cells)
        {
            var kind = cell.Kind;
            if (kind is not ("setup" or "code" or "markdown" or "types")) kind = "code";
            if (kind == "setup") { if (haveSetup) kind = "code"; else haveSetup = true; }
            if (kind == "types") { if (haveTypes) kind = "code"; else haveTypes = true; }

            var id = cell.Id;
            if (kind is "code" or "markdown")
            {
                if (id is null || !CellIds.IsValid(id) || CellIds.IsReserved(id) || !used.Add(id))
                {
                    id = CellIds.New(random, taken);
                    taken.Add(id);
                    used.Add(id);
                }
            }

            fixedCells.Add(kind == cell.Kind && id == cell.Id ? cell : cell with { Kind = kind, Id = id });
        }

        PartCell[] ordered =
        [
            .. fixedCells.Where(c => c.Kind == "setup"),
            .. fixedCells.Where(c => c.Kind is not ("setup" or "types")),
            .. fixedCells.Where(c => c.Kind == "types"),
        ];

        var changed = !ordered.SequenceEqual(cells);
        return (changed ? ordered : cells, changed);
    }
}
