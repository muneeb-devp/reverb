using Reverb.Format;

namespace Reverb.Format.Tests;

public class ImportAndUpgradeTests
{
    static UpgradeOptions Options() => new() { RuntimeVersion = "0.1.0", Random = new Random(1) };

    [Fact]
    public void File_without_markers_splits_into_setup_code_and_types()
    {
        var doc = NotebookDocument.Parse("""
            using System.Text;
            using static System.Math;

            // compute things
            var x = Sqrt(4);
            Console.WriteLine(x);

            record Point(int X, int Y);
            enum Color { Red }
            """);
        Assert.Equal([CellKind.Setup, CellKind.Code, CellKind.Types], doc.Cells.Select(c => c.Kind));
        Assert.Equal("using System.Text;\nusing static System.Math;", doc.Cells[0].Source);
        Assert.Equal("// compute things\nvar x = Sqrt(4);\nConsole.WriteLine(x);", doc.Cells[1].Source);
        Assert.Equal("record Point(int X, int Y);\nenum Color { Red }", doc.Cells[2].Source);
    }

    [Fact]
    public void File_with_only_statements_is_one_code_cell()
    {
        var doc = NotebookDocument.Parse("Console.WriteLine(1);\n");
        Assert.Equal(CellKind.Code, Assert.Single(doc.Cells).Kind);
    }

    [Fact]
    public void Leading_comments_and_directives_stay_in_the_preamble()
    {
        var doc = NotebookDocument.Parse("#!/usr/bin/env dotnet\n#:package Humanizer@2.14.1\n// A script\n\nusing Humanizer;\n\"x\".Humanize();\n");
        Assert.Equal(["#!/usr/bin/env dotnet", "#:package Humanizer@2.14.1", "// A script", ""], doc.Preamble.Lines.Select(l => l.Text));
        Assert.Equal([CellKind.Setup, CellKind.Code], doc.Cells.Select(c => c.Kind));
    }

    [Fact]
    public void Code_before_the_first_marker_is_imported_too()
    {
        var doc = NotebookDocument.Parse("using System;\nvar a = 1;\n\n// %% id=b\nvar b = a;\n");
        Assert.Equal([CellKind.Setup, CellKind.Code, CellKind.Code], doc.Cells.Select(c => c.Kind));
        Assert.Equal("b", doc.Cells[2].Id);
    }

    [Fact]
    public void Upgrade_of_a_plain_script()
    {
        var doc = NotebookDocument.Parse("#!/usr/bin/env dotnet\nusing System.Text;\nvar sb = new StringBuilder();\nrecord R;\n");
        var (upgraded, changes) = NotebookUpgrader.Upgrade(doc, Options());
        Assert.NotEmpty(changes);
        var text = upgraded.Serialize();
        Assert.Equal($"""
            #!/usr/bin/env dotnet
            #:package Reverb.Runtime@0.1.0
            // reverb-notebook v1

            // %% setup
            using System.Text;

            // %% id={upgraded.Cells[1].Id}
            var sb = new StringBuilder();

            // %% types
            record R;

            """.ReplaceLineEndings("\n"), text);
        // Upgrading again changes nothing.
        var (again, none) = NotebookUpgrader.Upgrade(NotebookDocument.Parse(text), Options());
        Assert.Empty(none);
        Assert.Equal(text, again.Serialize());
    }

    [Fact]
    public void Upgrade_of_a_jupytext_percent_file()
    {
        var doc = NotebookDocument.Parse("// %% [markdown]\n// # Title\n\n// %%\nvar x = 1;\n\n// %%\nvar y = x;\n");
        var (upgraded, _) = NotebookUpgrader.Upgrade(doc, Options());
        Assert.Equal([CellKind.Setup, CellKind.Markdown, CellKind.Code, CellKind.Code], upgraded.Cells.Select(c => c.Kind));
        Assert.Equal("using Reverb;", upgraded.Cells[0].Source);
        Assert.All(upgraded.Cells.Skip(1), c => Assert.Matches("^[a-z][a-z0-9]{5}$", c.Id!));
        Assert.Equal(3, upgraded.Cells.Skip(1).Select(c => c.Id).Distinct().Count());
        var text = upgraded.Serialize();
        Assert.StartsWith("#:package Reverb.Runtime@0.1.0\n// reverb-notebook v1\n\n// %% setup\nusing Reverb;\n\n// %% [markdown] id=", text);
    }

    [Fact]
    public void Upgrade_keeps_existing_ids_and_fills_missing_ones()
    {
        var doc = NotebookDocument.Parse("// reverb-notebook v1\n#:package Reverb.Runtime@0.2.0\n\n// %% setup\n\n// %% id=keep\nvar x = 1;\n\n// %% id=keep\nvar y = 2;\n");
        var (upgraded, _) = NotebookUpgrader.Upgrade(doc, Options());
        Assert.Equal("keep", upgraded.Cells[1].Id);
        Assert.NotNull(upgraded.Cells[2].Id);
        Assert.NotEqual("keep", upgraded.Cells[2].Id);
        // An existing runtime reference (any version) is left alone.
        Assert.Single(upgraded.Preamble.Directives, d => d.Name == "Reverb.Runtime");
    }

    [Fact]
    public void Upgrade_of_a_notebook_reverb_wrote_is_a_no_op()
    {
        var text = "#!/usr/bin/env dotnet\n#:package Reverb.Runtime@0.1.0\n// reverb-notebook v1\n\n// %% setup\nusing Reverb;\n\n// %% id=a\nvar x = 1;\n";
        var (upgraded, changes) = NotebookUpgrader.Upgrade(NotebookDocument.Parse(text), Options());
        Assert.Empty(changes);
        Assert.Equal(text, upgraded.Serialize());
    }

    [Theory]
    [InlineData("// reverb-notebook v1")]
    [InlineData("// reverb-notebook v1\n")]
    public void Upgrade_of_a_file_with_only_the_header(string text)
    {
        var (upgraded, changes) = NotebookUpgrader.Upgrade(NotebookDocument.Parse(text), Options());
        Assert.Contains("Added a setup cell.", changes);
        // The header line is terminated in the document itself, not only when the writer starts the
        // setup marker on a new line. Otherwise the preamble an editor keeps differs from the one it
        // reads back from the saved file.
        Assert.Equal(new PreambleLine("// reverb-notebook v1", "\n"), upgraded.Preamble.Lines[^1]);
        var written = upgraded.Serialize();
        Assert.Equal("#:package Reverb.Runtime@0.1.0\n// reverb-notebook v1\n// %% setup\nusing Reverb;\n", written);
        var reparsed = NotebookDocument.Parse(written);
        Assert.Equal([CellKind.Setup], reparsed.Cells.Select(c => c.Kind));
        Assert.Equal(upgraded.Preamble.Lines, reparsed.Preamble.Lines);
        Assert.Equal(written, reparsed.Serialize());
        var (again, none) = NotebookUpgrader.Upgrade(reparsed, Options());
        Assert.Empty(none);
        Assert.Equal(written, again.Serialize());
    }

    [Fact]
    public void Starter_template_is_already_upgraded()
    {
        var doc = NotebookTemplates.Starter("0.1.0", new Random(3));
        var (_, changes) = NotebookUpgrader.Upgrade(doc, Options());
        Assert.Empty(changes);
        Assert.Equal(doc.Serialize(), NotebookDocument.Parse(doc.Serialize()).Serialize());
    }

    [Fact]
    public void Directives_can_be_replaced()
    {
        var doc = NotebookDocument.Parse("#!/usr/bin/env dotnet\n#:package Reverb.Runtime@0.1.0\n#:package Old@1.0.0\n// reverb-notebook v1\n\n// %% setup\n");
        var updated = doc.WithDirectives([new Directive("package", "Reverb.Runtime", "0.1.0"), new Directive("package", "Humanizer", "2.14.1")]);
        Assert.Equal("#!/usr/bin/env dotnet\n#:package Reverb.Runtime@0.1.0\n#:package Humanizer@2.14.1\n// reverb-notebook v1\n\n// %% setup\n", updated.Serialize());
        var none = doc.WithDirectives([]);
        Assert.Equal("#!/usr/bin/env dotnet\n// reverb-notebook v1\n\n// %% setup\n", none.Serialize());
        Assert.Equal("#!/usr/bin/env dotnet\n#:property LangVersion=preview\n// reverb-notebook v1\n\n// %% setup\n",
            none.WithDirectives([new Directive("property", "LangVersion", "preview")]).Serialize());
    }

    [Fact]
    public void New_ids_are_unique_and_valid()
    {
        var rng = new Random(7);
        var taken = new HashSet<string>();
        for (var i = 0; i < 2000; i++)
        {
            var id = CellIds.New(rng, taken);
            Assert.True(CellIds.IsValid(id));
            Assert.True(taken.Add(id));
        }
    }
}
