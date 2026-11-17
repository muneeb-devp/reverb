namespace Reverb.Format;

public sealed record UpgradeOptions
{
    /// <summary>Version written in <c>#:package Reverb.Runtime@...</c> when the notebook has no runtime reference.</summary>
    public required string RuntimeVersion { get; init; }

    public Random Random { get; init; } = Random.Shared;
}

/// <summary>
/// Brings a parsed file up to what Reverb writes: header comment, runtime package, setup cell,
/// and an id on every cell. Leaves a file Reverb wrote untouched.
/// </summary>
public static class NotebookUpgrader
{
    public const string RuntimePackage = "Reverb.Runtime";

    public static (NotebookDocument Document, IReadOnlyList<string> Changes) Upgrade(NotebookDocument doc, UpgradeOptions options)
    {
        var changes = new List<string>();
        var nl = doc.Newline;
        var lines = doc.Preamble.Lines.ToList();

        if (!doc.Preamble.Directives.Any(d => d.Kind == "package" && string.Equals(d.Name, RuntimePackage, StringComparison.OrdinalIgnoreCase)))
        {
            var at = doc.Preamble.Shebang is null ? 0 : 1;
            lines.Insert(at, new PreambleLine(new Directive("package", RuntimePackage, options.RuntimeVersion).ToString(), nl));
            changes.Add($"Added '#:package {RuntimePackage}@{options.RuntimeVersion}'.");
        }

        if (doc.Preamble.Header is null)
        {
            var at = lines.FindLastIndex(l => Preamble.IsDirective(l) || l.Text.StartsWith("#!", StringComparison.Ordinal)) + 1;
            lines.Insert(at, new PreambleLine(NotebookHeader.CurrentLine, nl));
            if (at + 1 >= lines.Count || !string.IsNullOrWhiteSpace(lines[at + 1].Text))
                lines.Insert(at + 1, new PreambleLine("", nl));
            changes.Add("Added the '// reverb-notebook v1' header.");
        }

        var cells = doc.Cells.ToList();
        if (!cells.Any(c => c.Kind == CellKind.Setup))
        {
            cells.Insert(0, NotebookCell.Setup("using Reverb;"));
            changes.Add("Added a setup cell.");
        }

        // The last preamble line must end with a newline before the first cell marker, including a setup cell added just now.
        if (lines.Count > 0 && lines[^1].Terminator.Length == 0 && cells.Count > 0)
            lines[^1] = lines[^1] with { Terminator = nl };

        var taken = new HashSet<string>(cells.Select(c => c.Id).OfType<string>(), StringComparer.Ordinal);
        for (var i = 0; i < cells.Count; i++)
        {
            if (cells[i].Kind is not (CellKind.Code or CellKind.Markdown) || cells[i].Id is not null) continue;
            var id = CellIds.New(options.Random, taken);
            taken.Add(id);
            cells[i] = cells[i] with { Id = id };
            changes.Add($"Gave cell {i + 1} the id '{id}'.");
        }

        var upgraded = doc with { Preamble = new Preamble([.. lines]), Cells = [.. cells] };
        return (upgraded, changes);
    }
}

public static class NotebookTemplates
{
    /// <summary>The notebook <c>reverb new</c> creates.</summary>
    public static NotebookDocument Starter(string runtimeVersion, Random? random = null)
    {
        var rng = random ?? Random.Shared;
        var taken = new HashSet<string>();
        string Id() { var id = CellIds.New(rng, taken); taken.Add(id); return id; }

        var text = $"""
            #!/usr/bin/env dotnet
            #:package {NotebookUpgrader.RuntimePackage}@{runtimeVersion}
            {NotebookHeader.CurrentLine}

            // %% setup
            using Reverb;
            using static Reverb.Notebook;

            // %% [markdown] id={Id()}
            // # New notebook
            // Edit a cell and everything that depends on it re-runs.

            // %% id={Id()}
            var name = Ui.Text("Your name", value: "world");

            // %% id={Id()}
            Md($"Hello, **{"{"}name.Value{"}"}**!");

            """;
        return NotebookDocument.Parse(text.ReplaceLineEndings("\n"));
    }
}
