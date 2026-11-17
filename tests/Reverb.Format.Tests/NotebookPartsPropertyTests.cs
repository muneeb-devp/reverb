using System.Text;
using Reverb.Format;
using Reverb.Testing;

namespace Reverb.Format.Tests;

/// <summary>
/// An editor keeps a notebook as parts and writes it back with Join. Whatever it does to some
/// cells, the cells it didn't touch, the lines above the cells, the line endings and the BOM must
/// come back byte for byte.
/// </summary>
public class NotebookPartsPropertyTests
{
    const int Seeds = 8;

    public static TheoryData<string, int, bool> Cases()
    {
        var data = new TheoryData<string, int, bool>();
        foreach (var name in FixtureTests.Names)
            for (var seed = 0; seed < Seeds; seed++)
                foreach (var loose in new[] { false, true }) data.Add(name, seed, loose);
        return data;
    }

    sealed class Tracked(PartCell cell)
    {
        public PartCell Cell = cell;
        public readonly RawCellData? Raw = cell.Raw;
        public bool SourceChanged;
        public bool MarkerChanged;
    }

    [Theory, MemberData(nameof(Cases))]
    public void Untouched_cells_and_the_preamble_keep_their_bytes(string name, int seed, bool loose)
    {
        var original = File.ReadAllText(Path.Combine(Snapshot.FixturesDirectory, name + ".cs"));
        // The fixtures are written the way Reverb writes them, so writing a cell afresh gives the
        // same bytes. The loose copy has markers and comments written differently, which only
        // survive if the original text is kept.
        if (loose) original = Loosen(original);
        var parts = NotebookParts.Split(NotebookDocument.Parse(original));
        var rng = new Random(seed);
        var cells = parts.Cells.Select(c => new Tracked(c)).ToList();
        var log = new List<string>();
        var added = 0;

        var steps = rng.Next(1, 9);
        for (var step = 0; step < steps; step++)
        {
            switch (rng.Next(5))
            {
                case 0 when cells.Count > 0:
                {
                    var t = cells[rng.Next(cells.Count)];
                    t.Cell = t.Cell with { Source = t.Cell.Source + $"\n// edit {step}" };
                    t.SourceChanged = true;
                    log.Add($"edit {t.Cell.Id ?? t.Cell.Kind}");
                    break;
                }
                case 1:
                {
                    var at = rng.Next(cells.Count + 1);
                    var markdown = rng.Next(2) == 0;
                    var cell = new PartCell(markdown ? "markdown" : "code", $"new{++added}", markdown ? $"Note {added}" : $"var n{added} = {added};", [], null);
                    cells.Insert(at, new Tracked(cell));
                    log.Add($"insert new{added} at {at}");
                    break;
                }
                case 2 when cells.Count > 0:
                {
                    var at = rng.Next(cells.Count);
                    log.Add($"delete {cells[at].Cell.Id ?? cells[at].Cell.Kind}");
                    cells.RemoveAt(at);
                    break;
                }
                case 3 when cells.Count > 1:
                {
                    var from = rng.Next(cells.Count);
                    var t = cells[from];
                    cells.RemoveAt(from);
                    var to = rng.Next(cells.Count + 1);
                    cells.Insert(to, t);
                    log.Add($"move {t.Cell.Id ?? t.Cell.Kind} {from}->{to}");
                    break;
                }
                case 4:
                {
                    // Only code and markdown cells take attributes in the editors.
                    var candidates = cells.Where(c => c.Cell.Kind is "code" or "markdown").ToList();
                    if (candidates.Count == 0) break;
                    var t = candidates[rng.Next(candidates.Count)];
                    var attrs = t.Cell.Attributes.Any(a => a.Key == "hide_code")
                        ? t.Cell.Attributes.Where(a => a.Key != "hide_code").ToList()
                        : [.. t.Cell.Attributes, new CellAttributeData("hide_code", null)];
                    t.Cell = t.Cell with { Attributes = attrs };
                    t.MarkerChanged = true;
                    log.Add($"attributes {t.Cell.Id}");
                    break;
                }
            }
        }

        var doc = NotebookParts.Join(parts with { Cells = [.. cells.Select(t => t.Cell)] });
        var written = NotebookWriter.Write(doc);
        var text = written.Text;
        var because = $"{name}{(loose ? " (loose)" : "")}, seed {seed}: {string.Join(", ", log)}";

        // BOM, the preamble and the line endings.
        var bom = original.StartsWith('﻿');
        Assert.Equal(bom, doc.Serialize().StartsWith('﻿'));
        Assert.Equal((bom ? "﻿" : "") + text, doc.Serialize());
        var body = bom ? original[1..] : original;
        var preamble = string.Concat(parts.Preamble.Select(l => l.Text + l.Terminator));
        Assert.True(body.StartsWith(preamble, StringComparison.Ordinal), because);
        Assert.True(text.StartsWith(preamble, StringComparison.Ordinal), $"The preamble changed. {because}");
        if (!HasBareLf(body)) Assert.False(HasBareLf(text), $"A \\n line ending appeared in a \\r\\n file. {because}");
        if (!body.Contains("\r\n")) Assert.DoesNotContain("\r\n", text);

        var lineStarts = LineStarts(text);
        for (var i = 0; i < cells.Count; i++)
        {
            var t = cells[i];
            if (t.Raw is not { } raw) continue;
            var layout = written.Cells[i];
            var where = $"Cell {i} ({raw.Id ?? raw.Kind}). {because}";
            var markerAt = lineStarts[layout.MarkerLine];
            if (!t.MarkerChanged)
                Assert.True(string.CompareOrdinal(text, markerAt, raw.MarkerLine, 0, raw.MarkerLine.Length) == 0, $"The marker line changed. {where}");
            if (t.SourceChanged) continue;
            Assert.True(string.CompareOrdinal(text, layout.ContentStart, raw.Content, 0, raw.Content.Length) == 0, $"The content changed. {where}");
            // The padding after a cell is adjusted when it becomes last or stops being last, by
            // design, so it's only compared for cells whose position at the end didn't change.
            var last = i == cells.Count - 1;
            if (last == raw.WasLast)
            {
                var after = layout.ContentStart + raw.Content.Length;
                Assert.True(string.CompareOrdinal(text, after, raw.TrailingPad, 0, raw.TrailingPad.Length) == 0, $"The padding after the cell changed. {where}");
            }
        }
    }

    /// <summary>
    /// The same notebook with two spaces after each marker's <c>// %%</c>, a space at the end of
    /// each marker line, and Markdown comments written <c>//text</c>. It parses to the same cells.
    /// </summary>
    static string Loosen(string text)
    {
        var sb = new StringBuilder();
        var markdown = false;
        var at = 0;
        while (at < text.Length)
        {
            var end = text.IndexOf('\n', at);
            var line = end < 0 ? text[at..] : text[at..(end + 1)];
            at = end < 0 ? text.Length : end + 1;
            var terminator = line.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : line.EndsWith('\n') ? "\n" : "";
            var body = line[..^terminator.Length];
            if (CellMarker.IsMarker(body))
            {
                markdown = CellMarker.Parse(body).Kind == CellKind.Markdown;
                body = CellMarker.Prefix + " " + body[CellMarker.Prefix.Length..] + " ";
            }
            else if (markdown && body.StartsWith("// ", StringComparison.Ordinal) && body.Length > 3 && body[3] != ' ')
            {
                body = "//" + body[3..];
            }
            sb.Append(body).Append(terminator);
        }
        var loose = sb.ToString();
        Assert.NotEqual(text, loose);
        var before = NotebookDocument.Parse(text);
        var after = NotebookDocument.Parse(loose);
        Assert.Equal(before.Cells.Select(c => (c.Kind, c.Id, c.Source)), after.Cells.Select(c => (c.Kind, c.Id, c.Source)));
        Assert.Equal(loose, after.Serialize());
        return loose;
    }

    static bool HasBareLf(string s)
    {
        for (var i = 0; i < s.Length; i++)
            if (s[i] == '\n' && (i == 0 || s[i - 1] != '\r')) return true;
        return false;
    }

    static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }
}
