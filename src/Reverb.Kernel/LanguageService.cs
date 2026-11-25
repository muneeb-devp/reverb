using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using Reverb.Analysis;
using Reverb.Format;

namespace Reverb.Kernel;

public sealed record CompletionResult(int From, int To, IReadOnlyList<CompletionData> Items);

public sealed record HoverResult(int From, int To, string Text);

public sealed record SignatureResult(IReadOnlyList<SignatureData> Signatures, int ActiveSignature, int ActiveParameter);

/// <summary>
/// Completions, hover and signature help for a cell. Roslyn sees the whole notebook as one file,
/// the way <c>dotnet run</c> compiles it, so a cell sees the variables and types of the others.
/// Offsets are into the cell's source.
/// </summary>
public sealed class LanguageService : IDisposable
{
    /// <summary>More than this many completions are cut off; the editor filters as the user types.</summary>
    public const int MaxCompletions = 500;

    static readonly SymbolDisplayFormat SignatureFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut);

    readonly ReferenceSet references;
    readonly AdhocWorkspace workspace = new();
    Solution? solution;
    DocumentId? notebookId;
    DocumentId? typesId;
    string optionsKey = "";

    public LanguageService(ReferenceSet references) => this.references = references;

    /// <summary>The notebook as one Roslyn document, plus where each cell is in it.</summary>
    sealed record Prepared(Document Document, CellMap Map, CellLayout Cell, string Source, int Position);

    Prepared? Prepare(NotebookDocument doc, string cellKey, int offset)
    {
        var index = doc.IndexOf(cellKey);
        if (index < 0 || doc.Cells[index] is not { Kind: not CellKind.Markdown } cell) return null;

        // The cell being edited is often half typed: 'new Order(' with no closing paren would swallow
        // the cells after it, the types cell included, and Order would vanish. So Roslyn gets the
        // notebook up to this cell, and the types cell as a file of its own (with the setup usings).
        // Nothing declared after the caret's cell can be used there anyway.
        var typesSource = "";
        if (cell.Kind != CellKind.Types)
        {
            if (doc.TypesCell is { } types) typesSource = (doc.SetupCell?.Source ?? "") + "\n" + types.Source;
            doc = doc with { Cells = [.. doc.Cells.Take(index + 1).Where(c => c.Kind != CellKind.Types)] };
        }
        var layout = NotebookWriter.Write(doc);
        if (layout.Find(cellKey) is not { } cellLayout) return null;
        var text = SourceText.From(layout.Text);
        var map = new CellMap(layout, text);

        var options = NotebookCompilationOptions.FromDirectives(doc.Preamble.Directives);
        var key = $"{options.LanguageVersion}|{options.Nullable}|{options.ImplicitUsings}|{options.AllowUnsafe}|{string.Join(",", options.ExtraDefines)}";
        if (solution is null || key != optionsKey)
        {
            var projectId = ProjectId.CreateNewId();
            var project = ProjectInfo.Create(projectId, VersionStamp.Create(), "Notebook", "Notebook", LanguageNames.CSharp,
                compilationOptions: options.CompilationOptions(OutputKind.ConsoleApplication),
                parseOptions: options.ParseOptions,
                metadataReferences: references.References,
                analyzerReferences: references.Analyzers);
            notebookId = DocumentId.CreateNewId(projectId);
            typesId = DocumentId.CreateNewId(projectId);
            // Nothing is ever applied to the workspace, so its current solution is empty to start from.
            solution = workspace.CurrentSolution
                .AddProject(project)
                .AddDocument(DocumentId.CreateNewId(projectId), "GlobalUsings.g.cs", SourceText.From(options.GlobalUsingsSource))
                .AddDocument(notebookId, NotebookAnalysis.FilePath, text)
                .AddDocument(typesId, "types.cs", SourceText.From(typesSource));
            optionsKey = key;
        }
        else
        {
            solution = solution.WithDocumentText(notebookId!, text).WithDocumentText(typesId!, SourceText.From(typesSource));
        }

        var position = map.ToFileOffset(cellLayout, cell.Source, Math.Clamp(offset, 0, cell.Source.Length));
        return new Prepared(solution.GetDocument(notebookId!)!, map, cellLayout, cell.Source, position);
    }

    public async Task<CompletionResult> CompleteAsync(NotebookDocument doc, string cellKey, int offset, CancellationToken cancellationToken = default)
    {
        if (Prepare(doc, cellKey, offset) is not { } p) return new CompletionResult(offset, offset, []);
        var service = CompletionService.GetService(p.Document);
        if (service is null) return new CompletionResult(offset, offset, []);
        var list = await service.GetCompletionsAsync(p.Document, p.Position, cancellationToken: cancellationToken);

        var from = p.Map.ToSourceOffset(p.Cell, p.Source, list.Span.Start);
        var to = p.Map.ToSourceOffset(p.Cell, p.Source, list.Span.End);
        var typed = p.Source[Math.Min(from, p.Source.Length)..Math.Min(Math.Max(to, from), p.Source.Length)];

        // Roslyn offers every type in scope. Send the ones that could match what's typed, best
        // matches first, so the cap never cuts off a name that starts with what the user typed.
        static int Rank(CompletionItem i, string typed) =>
            typed.Length == 0 || i.FilterText.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? 0
            : i.FilterText.StartsWith(typed[..1], StringComparison.OrdinalIgnoreCase) ? 1
            : i.FilterText.Contains(typed, StringComparison.OrdinalIgnoreCase) ? 2
            : 3;
        var items = list.ItemsList
            .Select(i => (Item: i, Rank: Rank(i, typed)))
            .Where(x => x.Rank < 3)
            .OrderBy(x => x.Rank).ThenBy(x => x.Item.SortText, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Item)
            .Take(MaxCompletions)
            .Select(i => new CompletionData
            {
                Label = i.DisplayTextPrefix + i.DisplayText + i.DisplayTextSuffix,
                Insert = i.Properties.TryGetValue("InsertionText", out var insert) ? insert : null,
                Kind = KindOf(i.Tags),
                Detail = i.InlineDescription.Length > 0 ? i.InlineDescription : null,
                SortText = i.SortText,
            })
            .ToList();
        return new CompletionResult(from, to, items);
    }

    static string KindOf(ImmutableArray<string> tags)
    {
        foreach (var t in tags)
        {
            switch (t)
            {
                case WellKnownTags.Local or WellKnownTags.Parameter or WellKnownTags.RangeVariable: return "variable";
                case WellKnownTags.Method or WellKnownTags.ExtensionMethod: return "method";
                case WellKnownTags.Property: return "property";
                case WellKnownTags.Field or WellKnownTags.Constant: return "field";
                case WellKnownTags.Event: return "event";
                case WellKnownTags.Class or WellKnownTags.Delegate or WellKnownTags.Module or "Record": return "class";
                case WellKnownTags.Structure or "RecordStruct": return "struct";
                case WellKnownTags.Interface: return "interface";
                case WellKnownTags.Enum: return "enum";
                case WellKnownTags.EnumMember: return "enum-member";
                case WellKnownTags.Namespace: return "namespace";
                case WellKnownTags.Keyword: return "keyword";
                case WellKnownTags.TypeParameter: return "type";
                case WellKnownTags.Snippet: return "snippet";
            }
        }
        return "text";
    }

    public async Task<HoverResult?> HoverAsync(NotebookDocument doc, string cellKey, int offset, CancellationToken cancellationToken = default)
    {
        if (Prepare(doc, cellKey, offset) is not { } p) return null;
        var service = QuickInfoService.GetService(p.Document);
        var info = service is null ? null : await service.GetQuickInfoAsync(p.Document, p.Position, cancellationToken);
        if (info is null) return null;

        var parts = new List<string>();
        foreach (var section in info.Sections)
        {
            // Roslyn shows the comment above a local's declaration as its documentation. For the
            // first statement in a cell that's the cell's own '// %%' marker, which says nothing useful.
            var text = string.Join("\n", section.Text.Split('\n').Where(l => !l.TrimStart().StartsWith("%%", StringComparison.Ordinal))).Trim();
            if (text.Length == 0) continue;
            parts.Add(section.Kind == QuickInfoSectionKinds.Description ? "```csharp\n" + text + "\n```" : text);
        }
        if (await DefiningCellAsync(p, cancellationToken) is { } defined) parts.Add(defined);
        if (parts.Count == 0) return null;
        return new HoverResult(p.Map.ToSourceOffset(p.Cell, p.Source, info.Span.Start), p.Map.ToSourceOffset(p.Cell, p.Source, info.Span.End), string.Join("\n\n", parts));
    }

    /// <summary>"Defined in cell 'data'" for a variable or local function from another cell.</summary>
    static async Task<string?> DefiningCellAsync(Prepared p, CancellationToken cancellationToken)
    {
        var root = await p.Document.GetSyntaxRootAsync(cancellationToken);
        var model = await p.Document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return null;
        var token = root.FindToken(p.Position);
        if (token.Parent is not IdentifierNameSyntax name) return null;
        var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
        if (symbol is not (ILocalSymbol or IMethodSymbol { MethodKind: MethodKind.LocalFunction })) return null;
        var declaration = symbol.DeclaringSyntaxReferences.FirstOrDefault(r => r.SyntaxTree == root.SyntaxTree);
        if (declaration is null || p.Map.CellAt(declaration.Span.Start) is not { Key: { } key } cell || key == p.Cell.Key) return null;
        return $"Defined in cell '{key}'.";
    }

    public async Task<SignatureResult?> SignatureHelpAsync(NotebookDocument doc, string cellKey, int offset, CancellationToken cancellationToken = default)
    {
        if (Prepare(doc, cellKey, offset) is not { } p) return null;
        var root = await p.Document.GetSyntaxRootAsync(cancellationToken);
        var model = await p.Document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return null;

        // The innermost argument list the caret is inside.
        var list = root.FindToken(p.Position).Parent?.AncestorsAndSelf().OfType<ArgumentListSyntax>()
            .FirstOrDefault(a => p.Position > a.OpenParenToken.SpanStart && (a.CloseParenToken.IsMissing || p.Position <= a.CloseParenToken.SpanStart));
        if (list is null) return null;

        IReadOnlyList<IMethodSymbol> methods;
        ISymbol? chosen;
        switch (list.Parent)
        {
            case InvocationExpressionSyntax invocation:
                methods = [.. model.GetMemberGroup(invocation.Expression, cancellationToken).OfType<IMethodSymbol>()];
                var info = model.GetSymbolInfo(invocation, cancellationToken);
                chosen = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                break;
            case BaseObjectCreationExpressionSyntax creation when model.GetTypeInfo(creation, cancellationToken).Type is INamedTypeSymbol type:
                methods = [.. type.InstanceConstructors.Where(c => model.IsAccessible(p.Position, c))];
                chosen = model.GetSymbolInfo(creation, cancellationToken).Symbol;
                break;
            default:
                return null;
        }
        if (methods.Count == 0) return null;

        var active = list.Arguments.GetSeparators().Count(s => s.SpanStart < p.Position);
        var signatures = methods.Select(m => Signature(m)).ToList();
        // A finished call binds to one overload. A half-typed one only has candidates (all of
        // them), so pick the one that best fits the arguments typed so far.
        var activeSignature = chosen is null || model.GetSymbolInfo(list.Parent!, cancellationToken).Symbol is null
            ? BestFit(model, methods, list, active)
            : methods.ToList().FindIndex(m => SymbolEqualityComparer.Default.Equals(m, chosen) || SymbolEqualityComparer.Default.Equals(m, chosen.OriginalDefinition));
        return new SignatureResult(signatures, Math.Max(0, activeSignature), active);
    }

    /// <summary>
    /// The overload whose parameters take the arguments written so far, preferring exact type
    /// matches: for Math.Max(1, ... that's Max(int, int), not Max(byte, byte).
    /// </summary>
    static int BestFit(SemanticModel model, IReadOnlyList<IMethodSymbol> methods, ArgumentListSyntax list, int active)
    {
        var argumentTypes = list.Arguments.Take(active).Select(a => model.GetTypeInfo(a.Expression).Type).ToList();
        var best = -1;
        var bestScore = -1;
        for (var i = 0; i < methods.Count; i++)
        {
            var parameters = methods[i].Parameters;
            if (parameters.Length <= active && !parameters.Any(x => x.IsParams)) continue;
            var score = 0;
            var fits = true;
            for (var a = 0; a < argumentTypes.Count && a < parameters.Length; a++)
            {
                if (argumentTypes[a] is not { } type) continue;
                var conversion = model.Compilation.ClassifyConversion(type, parameters[a].Type);
                if (conversion.IsIdentity) score += 2;
                else if (conversion.IsImplicit) score += 1;
                else fits = false;
            }
            if (fits && score > bestScore) (best, bestScore) = (i, score);
        }
        return best;
    }

    static SignatureData Signature(IMethodSymbol method)
    {
        var parameters = method.Parameters.Select(x => x.ToDisplayString(SignatureFormat)).ToList();
        var name = method.MethodKind == MethodKind.Constructor ? method.ContainingType.ToDisplayString(SignatureFormat) : method.Name;
        var typeArguments = method.IsGenericMethod ? $"<{string.Join(", ", method.TypeArguments.Select(t => t.ToDisplayString(SignatureFormat)))}>" : "";
        var returns = method.MethodKind == MethodKind.Constructor ? "" : method.ReturnType.ToDisplayString(SignatureFormat) + " ";
        return new SignatureData { Label = $"{returns}{name}{typeArguments}({string.Join(", ", parameters)})", Parameters = parameters };
    }

    public void Dispose() => workspace.Dispose();
}
