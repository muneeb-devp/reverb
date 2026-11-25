using System.Diagnostics;
using Reverb.Analysis;
using Reverb.Format;
using Reverb.Testing;

namespace Reverb.Kernel.Tests;

public class LanguageTests(ITestOutputHelper output)
{
    static readonly ReferenceSet References = ReferenceSet.FromCurrentProcess(typeof(Ui).Assembly);

    /// <summary>A notebook and the caret: '|' in a cell marks the offset and is removed.</summary>
    static (NotebookDocument Doc, string Cell, int Offset) At(string[] cells, string? types = null, string? setup = null)
    {
        var index = Array.FindIndex(cells, c => c.Contains('|'));
        var text = NotebookText.Build([.. cells.Select(c => c.Replace("|", ""))], types: types?.Replace("|", ""), setup: setup?.Replace("|", "") ?? "using Reverb;");
        var doc = NotebookDocument.Parse(text);
        if (index >= 0) return (doc, $"c{index + 1}", cells[index].IndexOf('|'));
        if (types?.Contains('|') == true) return (doc, "types", types.IndexOf('|'));
        return (doc, "setup", setup!.IndexOf('|'));
    }

    [Fact]
    public async Task Completions_list_a_variable_from_an_earlier_cell()
    {
        using var service = new LanguageService(References);
        var (doc, cell, offset) = At(["var orders = new List<int> { 1, 2 };", "var total = or|"]);
        var clock = Stopwatch.StartNew();
        var result = await service.CompleteAsync(doc, cell, offset);
        output.WriteLine($"first completion: {clock.ElapsedMilliseconds} ms, {result.Items.Count} items");

        var orders = Assert.Single(result.Items, i => i.Label == "orders");
        Assert.Equal("variable", orders.Kind);
        Assert.Equal(offset - 2, result.From);
        Assert.Equal(offset, result.To);

        clock.Restart();
        await service.CompleteAsync(doc.WithCellSource("c2", "var total = ord"), cell, offset + 1);
        output.WriteLine($"second completion: {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Members_added_by_source_generators_complete()
    {
        using var service = new LanguageService(References);
        var (doc, cell, offset) = At(["var info = AppJson.|"],
            types: "record Order(int Id);\n[JsonSerializable(typeof(Order))]\npartial class AppJson : JsonSerializerContext;",
            setup: "using Reverb;\nusing System.Text.Json.Serialization;");
        var result = await service.CompleteAsync(doc, cell, offset);
        Assert.Contains(result.Items, i => i.Label == "Default" && i.Kind == "property");
    }

    [Fact]
    public async Task Member_completions_after_a_dot()
    {
        using var service = new LanguageService(References);
        var (doc, cell, offset) = At(["var orders = new List<int> { 1, 2 };", "var n = orders.|"]);
        var result = await service.CompleteAsync(doc, cell, offset);
        Assert.Contains(result.Items, i => i.Label == "Count" && i.Kind == "property");
        Assert.Contains(result.Items, i => i.Label == "Add" && i.Kind == "method");
        Assert.Equal(offset, result.From);
    }

    [Fact]
    public async Task Types_from_the_types_cell_and_namespaces_in_setup_complete()
    {
        using var service = new LanguageService(References);
        var (doc, cell, offset) = At(["var o = new Ord|"], types: "record Order(int Id);");
        Assert.Contains((await service.CompleteAsync(doc, cell, offset)).Items, i => i.Label == "Order" && i.Kind == "class");

        (doc, cell, offset) = At(["var a = 1;"], setup: "using Reverb;\nusing System.Te|");
        Assert.Contains((await service.CompleteAsync(doc, cell, offset)).Items, i => i.Label == "Text" && i.Kind == "namespace");
    }

    [Fact]
    public async Task Completions_in_a_markdown_cell_are_empty()
    {
        using var service = new LanguageService(References);
        var doc = NotebookDocument.Parse(NotebookText.Build(["var a = 1;"])).InsertCell(1, NotebookCell.Markdown("notes", "Some te"));
        Assert.Empty((await service.CompleteAsync(doc, "notes", 7)).Items);
    }

    [Fact]
    public async Task Hover_shows_the_type_and_which_cell_defines_a_variable()
    {
        using var service = new LanguageService(References);
        var (doc, cell, offset) = At(["var orders = new List<int> { 1, 2 };", "var n = ord|ers.Count;"]);
        var hover = await service.HoverAsync(doc, cell, offset);
        Assert.NotNull(hover);
        Assert.Contains("List<int>", hover.Text);
        Assert.Contains("orders", hover.Text);
        Assert.Contains("Defined in cell 'c1'.", hover.Text);
        Assert.DoesNotContain("%%", hover.Text);
        Assert.Equal(8, hover.From);
        Assert.Equal(14, hover.To);
    }

    [Fact]
    public async Task Hover_on_a_variable_in_its_own_cell_does_not_point_elsewhere()
    {
        using var service = new LanguageService(References);
        var (doc, cell, offset) = At(["var orders = 1;\nvar n = ord|ers + 1;"]);
        var hover = await service.HoverAsync(doc, cell, offset);
        Assert.NotNull(hover);
        Assert.DoesNotContain("Defined in cell", hover.Text);
    }

    [Fact]
    public async Task Signature_help_shows_overloads_and_the_current_parameter()
    {
        using var service = new LanguageService(References);
        var (doc, cell, offset) = At(["var m = Math.Max(1, |"]);
        var help = await service.SignatureHelpAsync(doc, cell, offset);
        Assert.NotNull(help);
        Assert.Equal(1, help.ActiveParameter);
        Assert.Contains(help.Signatures, s => s.Label == "int Max(int val1, int val2)");
        Assert.True(help.Signatures.Count > 5);
        Assert.Equal("int Max(int val1, int val2)", help.Signatures[help.ActiveSignature].Label);

        (doc, cell, offset) = At(["var m = Math.Max(1.5, |"]);
        help = await service.SignatureHelpAsync(doc, cell, offset);
        Assert.Equal("double Max(double val1, double val2)", help!.Signatures[help.ActiveSignature].Label);
    }

    [Fact]
    public async Task Signature_help_for_a_constructor_from_the_types_cell()
    {
        using var service = new LanguageService(References);
        var (doc, cell, offset) = At(["var o = new Order(|"], types: "record Order(int Id, string Region);");
        var help = await service.SignatureHelpAsync(doc, cell, offset);
        Assert.NotNull(help);
        Assert.Equal(0, help.ActiveParameter);
        var signature = help.Signatures[help.ActiveSignature];
        Assert.Equal(["int Id", "string Region"], signature.Parameters);
    }

    [Fact]
    public async Task No_signature_help_outside_an_argument_list()
    {
        using var service = new LanguageService(References);
        var (doc, cell, offset) = At(["var m = Math.Max(1, 2);|"]);
        Assert.Null(await service.SignatureHelpAsync(doc, cell, offset));
    }
}
