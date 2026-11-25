using Microsoft.CodeAnalysis;
using Reverb.Analysis;
using Reverb.Format;
using Reverb.Kernel;

namespace Reverb.Cli;

/// <summary>
/// <c>reverb check</c>: parses and analyses a notebook without running it, prints diagnostics in
/// the usual <c>file(line,col): error ID: message</c> form, and fails if there are errors.
/// </summary>
static class CheckCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || args[0].StartsWith('-'))
        {
            Console.Error.WriteLine("Usage: reverb check <file.cs>");
            return 2;
        }
        var path = args[0];
        var loaded = LoadedNotebook.Load(path, Console.Error);
        if (loaded is null) return 2;

        var errors = 0;
        var warnings = 0;
        var output = Console.Out;

        foreach (var d in loaded.Original.Diagnostics)
        {
            output.WriteLine($"{path}({d.Line},1): warning {d.Code}: {d.Message}");
            warnings++;
        }
        if (!loaded.LinesMatchFile)
            output.WriteLine($"{path}: info: this file isn't in Reverb's notebook format yet, so positions below are given per cell. Opening it in Reverb converts it.");

        foreach (var f in AmbientBuildFiles.Find(path))
        {
            output.WriteLine($"{f.Path}: warning RVB200: {f.Why}");
            warnings++;
        }

        var references = ReferenceSet.FromCurrentProcess(typeof(Ui).Assembly);
        try
        {
            // Package types have to be known for the diagnostics to be right.
            if (await PackageKernels.EnsureAsync(path, loaded.Document.Preamble.Directives) is { } build)
                references = references.With(build.References).WithGenerators(build.Analyzers);
        }
        catch (KernelBuildException e)
        {
            output.WriteLine($"{path}: error RVB201: {e.Message}");
            return 1;
        }

        var analysis = NotebookAnalysis.Create(loaded.Document, references);
        foreach (var d in analysis.Diagnostics)
        {
            if (d.Severity == DiagnosticSeverity.Error) errors++;
            else warnings++;
            output.WriteLine($"{Where(loaded, analysis, d)}: {d.Severity.ToString().ToLowerInvariant()} {d.Id}: {d.Message}");
        }

        output.WriteLine(errors == 0 && warnings == 0
            ? $"{path}: no problems found."
            : $"{path}: {errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}.");
        return errors > 0 ? 1 : 0;
    }

    static string Where(LoadedNotebook loaded, NotebookAnalysis analysis, NotebookDiagnostic d)
    {
        if (d.CellKey is null) return loaded.Path;
        var layout = analysis.Layout.Find(d.CellKey);
        if (layout is null || !loaded.LinesMatchFile)
            return $"{loaded.Path}[cell {d.CellKey}]({d.Range.StartLine + 1},{d.Range.StartColumn + 1})";
        return $"{loaded.Path}({layout.ContentStartLine + d.Range.StartLine + 1},{d.Range.StartColumn + 1}) [cell {d.CellKey}]";
    }
}
