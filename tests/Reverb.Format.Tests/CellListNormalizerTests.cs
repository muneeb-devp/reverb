using Reverb.Format;

namespace Reverb.Format.Tests;

public class CellListNormalizerTests
{
    static PartCell Code(string? id, string src = "") => new("code", id, src, [], null);

    [Fact]
    public void A_valid_list_is_unchanged()
    {
        var cells = new[] { new PartCell("setup", null, "using X;", [], null), Code("a"), Code("b"), new PartCell("types", null, "record R;", [], null) };
        var (result, changed) = CellListNormalizer.Normalize(cells, new Random(0));
        Assert.False(changed);
        Assert.Equal(cells, result);
    }

    [Fact]
    public void A_duplicate_id_gets_a_new_one_and_the_first_keeps_it()
    {
        var (result, changed) = CellListNormalizer.Normalize([Code("a", "1"), Code("a", "2")], new Random(0));
        Assert.True(changed);
        Assert.Equal("a", result[0].Id);
        Assert.NotEqual("a", result[1].Id);
        Assert.True(CellIds.IsValid(result[1].Id!));
    }

    [Fact]
    public void Cells_without_ids_get_one() =>
        Assert.All(CellListNormalizer.Normalize([Code(null), Code(null)], new Random(0)).Cells, c => Assert.True(CellIds.IsValid(c.Id!)));

    [Fact]
    public void Setup_moves_first_and_types_last()
    {
        var (result, _) = CellListNormalizer.Normalize([Code("a"), new PartCell("types", null, "", [], null), new PartCell("setup", null, "", [], null), Code("b")], new Random(0));
        Assert.Equal(["setup", "code", "code", "types"], result.Select(c => c.Kind));
        Assert.Equal(["a", "b"], result.Where(c => c.Kind == "code").Select(c => c.Id));
    }

    [Fact]
    public void A_second_setup_cell_becomes_code()
    {
        var (result, _) = CellListNormalizer.Normalize([new PartCell("setup", null, "using A;", [], null), new PartCell("setup", null, "using B;", [], null)], new Random(0));
        Assert.Equal(["setup", "code"], result.Select(c => c.Kind));
        Assert.Equal("using B;", result[1].Source);
        Assert.True(CellIds.IsValid(result[1].Id!));
    }

    [Fact]
    public void No_setup_cell_is_fine() =>
        Assert.False(CellListNormalizer.Normalize([Code("a")], new Random(0)).Changed);

    [Fact]
    public void An_unknown_kind_becomes_code()
    {
        var (result, changed) = CellListNormalizer.Normalize([new PartCell("python", null, "x", [], null)], new Random(0));
        Assert.True(changed);
        Assert.Equal("code", result[0].Kind);
        Assert.True(CellIds.IsValid(result[0].Id!));
    }

    [Fact]
    public void Invalid_and_reserved_ids_are_replaced()
    {
        var (result, _) = CellListNormalizer.Normalize([Code("Bad Id"), Code("setup")], new Random(0));
        Assert.All(result, c => Assert.True(CellIds.IsValid(c.Id!) && !CellIds.IsReserved(c.Id!)));
        Assert.NotEqual(result[0].Id, result[1].Id);
    }

    [Fact]
    public void Raw_is_kept()
    {
        var raw = new RawCellData("m", "", "c", "", false, "code", "a", [], "s", 1);
        var (result, _) = CellListNormalizer.Normalize([new PartCell("code", "a", "s", [], raw), Code("a")], new Random(0));
        Assert.Same(raw, result[0].Raw);
    }
}
