using System.Text.Json;
using Reverb.Format;
using Reverb.Testing;

namespace Reverb.Format.Tests;

public class NotebookPartsTests
{
    [Theory, MemberData(nameof(FixtureTests.Names), MemberType = typeof(FixtureTests))]
    public void Split_then_join_is_byte_exact(string name)
    {
        var text = File.ReadAllText(Path.Combine(Snapshot.FixturesDirectory, name + ".cs"));
        var doc = NotebookDocument.Parse(text);
        Assert.Equal(doc.Serialize(), NotebookParts.Join(NotebookParts.Split(doc)).Serialize());
    }

    [Fact]
    public void Changing_one_cell_keeps_the_others_byte_for_byte()
    {
        var text = File.ReadAllText(Path.Combine(Snapshot.FixturesDirectory, "chain.cs"));
        var parts = NotebookParts.Split(NotebookDocument.Parse(text));
        var b = parts.Cells.Single(c => c.Id == "b");
        var edited = parts with { Cells = [.. parts.Cells.Select(c => c == b ? c with { Source = "var b = a * 3;" } : c)] };
        var result = NotebookParts.Join(edited).Serialize();
        Assert.Equal(text.Replace("var b = a * 10;", "var b = a * 3;"), result);
    }

    [Theory, InlineData("crlf"), InlineData("bom")]
    public void Crlf_and_bom_survive_an_edit(string name)
    {
        var text = File.ReadAllText(Path.Combine(Snapshot.FixturesDirectory, name + ".cs"));
        var parts = NotebookParts.Split(NotebookDocument.Parse(text));
        var first = parts.Cells.First(c => c.Kind == "code");
        var result = NotebookParts.Join(parts with { Cells = [.. parts.Cells.Select(c => c == first ? c with { Source = c.Source + "\n// edited" } : c)] }).Serialize();
        Assert.Equal(text.StartsWith('﻿'), result.StartsWith('﻿'));
        if (text.Contains("\r\n")) Assert.DoesNotContain("\n", result.Replace("\r\n", ""));
    }

    [Theory, InlineData("\n"), InlineData("\r\n")]
    public void A_source_sent_with_crlf_is_written_with_the_file_s_newline(string newline)
    {
        var text = $"// reverb-notebook v1{newline}{newline}// %% id=a{newline}var a = 1;{newline}";
        var parts = NotebookParts.Split(NotebookDocument.Parse(text));
        var edited = parts with { Cells = [.. parts.Cells.Select(c => c.Id == "a" ? c with { Source = "var a = 1;\r\nvar b = 2;" } : c)] };
        Assert.Equal($"// reverb-notebook v1{newline}{newline}// %% id=a{newline}var a = 1;{newline}var b = 2;{newline}", NotebookParts.Join(edited).Serialize());
    }

    [Fact]
    public void A_cell_without_raw_text_is_written_fresh()
    {
        var parts = NotebookParts.Split(NotebookDocument.Parse("// reverb-notebook v1\n\n// %% id=a\nvar a = 1;\n"));
        var added = parts with { Cells = [.. parts.Cells, new PartCell("code", "b", "var b = 2;", [], null)] };
        Assert.EndsWith("// %% id=b\nvar b = 2;\n", NotebookParts.Join(added).Serialize());
    }

    [Fact]
    public void Parts_survive_a_json_round_trip()
    {
        var parts = NotebookParts.Split(NotebookDocument.Parse(File.ReadAllText(Path.Combine(Snapshot.FixturesDirectory, "attributes.cs"))));
        var json = JsonSerializer.Serialize(parts);
        Assert.Equal(NotebookParts.Join(parts).Serialize(), NotebookParts.Join(JsonSerializer.Deserialize<NotebookPartsData>(json)!).Serialize());
    }
}
