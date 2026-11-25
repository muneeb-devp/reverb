using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using Reverb.Format;

namespace Reverb.Analysis;

/// <summary>
/// The analysis compilation: the whole notebook file compiled as the top-level program
/// <c>dotnet run</c> would build, plus everything derived from it (cell inputs and outputs,
/// the dependency graph, compiler and Reverb diagnostics).
/// </summary>
public sealed class NotebookAnalysis
{
    public NotebookDocument Document { get; }
    public SerializedNotebook Layout { get; }
    public SourceText Text { get; }
    public SyntaxTree Tree { get; }

    /// <summary>The notebook compilation, with the files the source generators added.</summary>
    public CSharpCompilation Compilation { get; }
    public SemanticModel Model { get; }
    public NotebookCompilationOptions Options { get; }
    public ReferenceSet References { get; }
    public CellMap Map { get; }
    public ImmutableArray<CellAnalysis> Cells { get; }
    public DependencyGraph Graph { get; }

    /// <summary>All diagnostics: per cell, then notebook-wide ones (CellKey null).</summary>
    public ImmutableArray<NotebookDiagnostic> Diagnostics { get; }

    public const string FilePath = "notebook.cs";

    /// <summary>The compilation before generators ran: what an edit replaces the notebook's tree in.</summary>
    readonly CSharpCompilation sourceCompilation;

    /// <summary>Kept across edits, so generators only redo work for input that changed.</summary>
    readonly GeneratorDriver? generatorDriver;

    readonly ImmutableArray<Diagnostic> generatorDiagnostics;

    NotebookAnalysis(NotebookDocument doc, SerializedNotebook layout, SourceText text, SyntaxTree tree,
        CSharpCompilation compilation, NotebookCompilationOptions options, ReferenceSet references, GeneratorDriver? driver)
    {
        Document = doc;
        Layout = layout;
        Text = text;
        Tree = tree;
        sourceCompilation = compilation;
        Compilation = SourceGenerators.Run(references.Generators, compilation, options.ParseOptions, ref driver, out generatorDiagnostics);
        generatorDriver = driver;
        Model = Compilation.GetSemanticModel(tree);
        Options = options;
        References = references;
        Map = new CellMap(layout, text);

        var analyzer = new Analyzer(this);
        (Cells, Graph, Diagnostics) = analyzer.Run();
    }

    public static NotebookAnalysis Create(NotebookDocument doc, ReferenceSet references)
    {
        var options = NotebookCompilationOptions.FromDirectives(doc.Preamble.Directives);
        var layout = NotebookWriter.Write(doc);
        var text = SourceText.From(layout.Text, Encoding.UTF8);
        var tree = CSharpSyntaxTree.ParseText(text, options.ParseOptions, FilePath);
        var usings = CSharpSyntaxTree.ParseText(options.GlobalUsingsSource, options.ParseOptions, "GlobalUsings.g.cs", Encoding.UTF8);
        var compilation = CSharpCompilation.Create("Notebook", [usings, tree], references.References,
            options.CompilationOptions(OutputKind.ConsoleApplication));
        return new NotebookAnalysis(doc, layout, text, tree, compilation, options, references, driver: null);
    }

    /// <summary>
    /// Analyses an edited version of the notebook, reusing this compilation's references and
    /// global usings when the compiler options haven't changed.
    /// </summary>
    public NotebookAnalysis Update(NotebookDocument doc)
    {
        var options = NotebookCompilationOptions.FromDirectives(doc.Preamble.Directives);
        if (!SameCompilerSettings(options, Options)) return Create(doc, References);
        var layout = NotebookWriter.Write(doc);
        var text = SourceText.From(layout.Text, Encoding.UTF8);
        var tree = Tree.WithChangedText(text);
        var compilation = sourceCompilation.ReplaceSyntaxTree(Tree, tree);
        return new NotebookAnalysis(doc, layout, text, tree, compilation, options, References, generatorDriver);
    }

    static bool SameCompilerSettings(NotebookCompilationOptions a, NotebookCompilationOptions b) =>
        a.LanguageVersion == b.LanguageVersion && a.Nullable == b.Nullable && a.ImplicitUsings == b.ImplicitUsings
        && a.AllowUnsafe == b.AllowUnsafe && a.ExtraDefines.SequenceEqual(b.ExtraDefines);

    public CellAnalysis? Cell(string key) => Cells.FirstOrDefault(c => c.Key == key);

    public bool HasErrors => Diagnostics.Any(d => d.IsError);

    /// <summary>Does the actual work. Separate so the fields above stay simple.</summary>
    sealed class Analyzer(NotebookAnalysis a)
    {
        static readonly SymbolDisplayFormat GeneratedFormat = SymbolDisplayFormat.FullyQualifiedFormat
            .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        static readonly SymbolDisplayFormat ShortFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
            .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        static readonly HashSet<string> MutatingMethods = new(StringComparer.Ordinal)
        {
            "Add", "AddRange", "Insert", "InsertRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "RemoveWhere",
            "Clear", "Sort", "Reverse", "Push", "Pop", "Enqueue", "Dequeue", "TryAdd", "TryRemove", "TryDequeue",
            "TryPop", "AddOrUpdate", "GetOrAdd", "Append", "AppendLine", "AppendFormat", "AppendJoin", "Replace",
            "UnionWith", "IntersectWith", "ExceptWith", "SymmetricExceptWith", "Set", "SetAll", "Fill", "EnsureCapacity",
            "TrimExcess",
        };

        readonly SemanticModel model = a.Model;
        readonly CellMap map = a.Map;
        readonly Dictionary<string, List<NotebookDiagnostic>> diagnostics = new(StringComparer.Ordinal);
        readonly List<NotebookDiagnostic> notebookDiagnostics = [];
        ImmutableHashSet<ISymbol> topLevel = ImmutableHashSet<ISymbol>.Empty;
        TextSpan? typesRegion;

        public (ImmutableArray<CellAnalysis>, DependencyGraph, ImmutableArray<NotebookDiagnostic>) Run()
        {
            var root = a.Tree.GetCompilationUnitRoot();
            var globals = root.Members.OfType<GlobalStatementSyntax>().ToList();
            topLevel = TopLevelSymbols(globals);
            var typesLayout = a.Layout.Cells.FirstOrDefault(c => c.Kind == CellKind.Types);
            typesRegion = typesLayout is null ? null : map.RegionOf(typesLayout);

            foreach (var problem in a.Options.Problems)
                notebookDiagnostics.Add(new NotebookDiagnostic("RVB100", DiagnosticSeverity.Error, problem, null, CellRange.Start));
            foreach (var problem in a.References.GeneratorProblems)
                notebookDiagnostics.Add(new NotebookDiagnostic(RuleIds.GeneratorNotLoaded, DiagnosticSeverity.Warning, problem, null, CellRange.Start));
            AddCompilerDiagnostics();
            CheckPlacement(root);

            var cells = new List<CellAnalysis>();
            foreach (var layout in a.Layout.Cells)
            {
                if (layout.Key is null) continue;
                var statements = globals.Where(g => map.CellAt(g.SpanStart)?.CellIndex == layout.CellIndex).ToList();
                cells.Add(layout.Kind == CellKind.Code && statements.Count > 0
                    ? AnalyzeCode(layout, statements)
                    : new CellAnalysis { Key = layout.Key, Index = layout.CellIndex, Kind = layout.Kind });
            }

            var graph = BuildGraph(cells);
            CheckSharedOutputs(cells);

            var finished = cells.Select(c => c with { Diagnostics = Sorted(Get(c.Key)) }).ToImmutableArray();
            var all = finished.SelectMany(c => c.Diagnostics).Concat(notebookDiagnostics).ToImmutableArray();
            return (finished, graph, all);
        }

        // ---- scope and symbols ----------------------------------------------------------------

        ImmutableHashSet<ISymbol> TopLevelSymbols(List<GlobalStatementSyntax> globals)
        {
            // The outermost block of the synthesized entry point lists exactly the locals scoped
            // to the top level. Its direct local function statements are the top-level functions.
            if (globals.Count == 0) return ImmutableHashSet<ISymbol>.Empty;
            IOperation? op = model.GetOperation(globals[0].Statement);
            while (op?.Parent is not null) op = op.Parent;
            var block = op switch
            {
                IMethodBodyOperation m => m.BlockBody,
                IBlockOperation b => b,
                _ => null,
            };
            if (block is null) return ImmutableHashSet<ISymbol>.Empty;
            var functions = block.Operations.OfType<ILocalFunctionOperation>().Select(f => (ISymbol)f.Symbol);
            return block.Locals.Cast<ISymbol>().Concat(functions).ToImmutableHashSet(SymbolEqualityComparer.Default);
        }

        CellLayout? DeclaringCell(ISymbol symbol)
        {
            var reference = symbol.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault(r => r.SyntaxTree == a.Tree);
            return reference is null ? null : map.CellAt(reference.Span.Start);
        }

        static bool IsEntryPointArgs(ISymbol s) =>
            s is IParameterSymbol { Name: "args", ContainingSymbol: IMethodSymbol { Name: WellKnownMemberNames.TopLevelStatementsEntryPointMethodName } };

        // ---- per-cell analysis ----------------------------------------------------------------

        CellAnalysis AnalyzeCode(CellLayout cell, List<GlobalStatementSyntax> statements)
        {
            var key = cell.Key!;
            var flow = model.AnalyzeDataFlow(statements[0].Statement, statements[^1].Statement);
            if (flow is null || !flow.Succeeded)
                return new CellAnalysis { Key = key, Index = cell.CellIndex, Kind = cell.Kind };

            bool Foreign(ISymbol s) => DeclaringCell(s)?.CellIndex != cell.CellIndex;
            var nodes = statements.SelectMany(s => s.DescendantNodesAndSelf()).ToList();

            // Inputs: variables read from other cells, and args.
            var inputs = new List<CellVariable>();
            var reads = flow.DataFlowsIn.Union(flow.ReadInside, SymbolEqualityComparer.Default);
            foreach (var s in reads.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                if (s is ILocalSymbol local && topLevel.Contains(local) && Foreign(local))
                    inputs.Add(Variable(local, local.Type, VariableKind.Local));
                else if (IsEntryPointArgs(s))
                    inputs.Add(Variable(s, ((IParameterSymbol)s).Type, VariableKind.Args) with { DeclaringCell = null });
            }

            // Local functions from other cells. Data flow doesn't report these; find them in the syntax.
            var functionUses = new Dictionary<IMethodSymbol, SyntaxNode>(SymbolEqualityComparer.Default);
            foreach (var node in nodes.Where(n => n is IdentifierNameSyntax or GenericNameSyntax))
            {
                var info = model.GetSymbolInfo(node);
                var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction } m
                    && topLevel.Contains(m.OriginalDefinition) && Foreign(m.OriginalDefinition)
                    && !functionUses.ContainsKey((IMethodSymbol)m.OriginalDefinition))
                {
                    functionUses[(IMethodSymbol)m.OriginalDefinition] = node;
                }
            }
            foreach (var (function, use) in functionUses.OrderBy(f => f.Key.Name, StringComparer.Ordinal))
            {
                inputs.Add(FunctionVariable(function));
                var declaring = DeclaringCell(function);
                if (declaring is not null && declaring.CellIndex > cell.CellIndex)
                {
                    Report(cell, use.Span, RuleIds.LocalFunctionFromLaterCell, DiagnosticSeverity.Error,
                        $"'{function.Name}' is a local function declared in cell '{declaring.Key}', which comes after this cell. Move cell '{declaring.Key}' above this one, or move '{function.Name}' into this cell or an earlier one.");
                }
                if (FunctionShareProblem(function) is { } problem)
                {
                    Report(cell, use.Span, RuleIds.ValueCannotCrossCells, DiagnosticSeverity.Error,
                        $"'{function.Name}' comes from cell '{declaring?.Key}' and {problem}, so it can't be passed to another cell. Move it into this cell, or make it a static method of a class in the types cell.");
                }
            }

            // Inputs whose type can't be passed between cells.
            foreach (var input in inputs.Where(i => i.Kind == VariableKind.Local))
            {
                var local = (ILocalSymbol)input.Symbol!;
                if (TypeShareProblem(local.Type) is not { } problem) continue;
                var first = nodes.OfType<IdentifierNameSyntax>().FirstOrDefault(n => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(n).Symbol, local));
                Report(cell, first?.Span ?? statements[0].Span, problem.Rule, DiagnosticSeverity.Error,
                    $"'{local.Name}' comes from cell '{input.DeclaringCell}' and {problem.Reason}, so it can't be passed to another cell. {problem.Fix}");
            }

            // Outputs.
            var declaredLocals = flow.VariablesDeclared.OfType<ILocalSymbol>().Where(topLevel.Contains).ToList();
            var declaredFunctions = topLevel.OfType<IMethodSymbol>().Where(f => DeclaringCell(f)?.CellIndex == cell.CellIndex)
                .OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            var outputs = declaredLocals.OrderBy(l => l.Name, StringComparer.Ordinal).Select(l => Variable(l, l.Type, VariableKind.Local))
                .Concat(declaredFunctions.Select(FunctionVariable)).ToImmutableArray();
            var stored = declaredLocals
                .Where(l => flow.DefinitelyAssignedOnExit.Contains(l, SymbolEqualityComparer.Default) && TypeShareProblem(l.Type) is null)
                .OrderBy(l => l.Name, StringComparer.Ordinal)
                .Select(l => Variable(l, l.Type, VariableKind.Local))
                .Concat(declaredFunctions.Where(f => FunctionShareProblem(f) is null).Select(FunctionVariable))
                .ToImmutableArray();

            CheckForeignWrites(cell, flow, nodes, Foreign);
            CheckReturns(cell, nodes);
            CheckMutations(cell, statements, Foreign, flow);

            return new CellAnalysis
            {
                Key = key,
                Index = cell.CellIndex,
                Kind = cell.Kind,
                Inputs = [.. inputs],
                Outputs = outputs,
                Stored = stored,
                UsesTypes = UsesTypes(nodes, inputs.Concat(outputs)),
                UsingDeclarations = UsingDeclarations(statements),
            };
        }

        CellVariable Variable(ISymbol symbol, ITypeSymbol type, VariableKind kind) =>
            new(symbol.Name, DisplayType(symbol, type), kind, DeclaringCell(symbol)?.Key)
            {
                GeneratedType = type.ToDisplayString(GeneratedFormat),
                Symbol = symbol,
            };

        CellVariable FunctionVariable(IMethodSymbol f) =>
            new(f.Name, DelegateType(f, ShortFormat), VariableKind.LocalFunction, DeclaringCell(f)?.Key)
            {
                GeneratedType = DelegateType(f, GeneratedFormat),
                Symbol = f,
            };

        /// <summary>
        /// C# declares every <c>var</c> local of reference type as nullable (<c>T?</c>). Showing
        /// <c>Random?</c> for <c>var rng = new Random()</c> would confuse people, so display types
        /// drop that top-level annotation. Generated code keeps the exact type.
        /// </summary>
        static string DisplayType(ISymbol symbol, ITypeSymbol type)
        {
            var declaredWithVar = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() switch
            {
                VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Type.IsVar: true } } => true,
                SingleVariableDesignationSyntax d => d.Ancestors().OfType<DeclarationExpressionSyntax>().FirstOrDefault()?.Type.IsVar == true
                    || d.Parent is VarPatternSyntax or ParenthesizedVariableDesignationSyntax,
                _ => false,
            };
            if (declaredWithVar && type.IsReferenceType) type = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            return type.ToDisplayString(ShortFormat);
        }

        static string DelegateType(IMethodSymbol f, SymbolDisplayFormat format)
        {
            var parameters = f.Parameters.Select(p => p.Type.ToDisplayString(format)).ToList();
            var prefix = format == GeneratedFormat ? "global::System." : "";
            if (f.ReturnsVoid)
                return parameters.Count == 0 ? $"{prefix}Action" : $"{prefix}Action<{string.Join(", ", parameters)}>";
            parameters.Add(f.ReturnType.ToDisplayString(format));
            return $"{prefix}Func<{string.Join(", ", parameters)}>";
        }

        // ---- type checks ----------------------------------------------------------------------

        sealed record ShareProblem(string Rule, string Reason, string Fix);

        static ShareProblem? TypeShareProblem(ITypeSymbol type)
        {
            if (Contains(type, t => t.IsAnonymousType))
                return new(RuleIds.AnonymousTypeShared, "its type is an anonymous type",
                    "Use a record from the types cell or a named tuple instead, e.g. (Region: g.Key, Count: g.Count()).");
            if (Contains(type, t => t.IsRefLikeType))
                return new(RuleIds.ValueCannotCrossCells, $"its type {type.ToDisplayString(ShortFormat)} is a ref struct",
                    "Use an array or a List<T> instead of a span, or keep this code in one cell.");
            if (Contains(type, t => t.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer))
                return new(RuleIds.ValueCannotCrossCells, "its type is a pointer type", "Keep this code in one cell.");
            return null;
        }

        static string? FunctionShareProblem(IMethodSymbol f)
        {
            if (f.IsGenericMethod) return "is generic";
            if (f.Parameters.Any(p => p.RefKind != RefKind.None)) return "has ref, out or in parameters";
            if (f.Parameters.Any(p => p.IsParams)) return "has a params parameter";
            if (f.Parameters.Any(p => p.IsOptional)) return "has optional parameters";
            if (f.ReturnsByRef || f.ReturnsByRefReadonly) return "returns by reference";
            if (f.Parameters.Length > 16) return "has more than 16 parameters";
            if (f.Parameters.Any(p => TypeShareProblem(p.Type) is not null) || (!f.ReturnsVoid && TypeShareProblem(f.ReturnType) is not null))
                return "uses a type that can't be passed between cells";
            return null;
        }

        static bool Contains(ITypeSymbol type, Func<ITypeSymbol, bool> predicate)
        {
            if (predicate(type)) return true;
            return type switch
            {
                IArrayTypeSymbol arr => Contains(arr.ElementType, predicate),
                IPointerTypeSymbol ptr => Contains(ptr.PointedAtType, predicate),
                INamedTypeSymbol named => named.TypeArguments.Any(t => Contains(t, predicate)),
                _ => false,
            };
        }

        bool UsesTypes(List<SyntaxNode> nodes, IEnumerable<CellVariable> variables)
        {
            if (typesRegion is not { } region) return false;
            bool FromTypes(ISymbol? s)
            {
                switch (s)
                {
                    case null: return false;
                    case ITypeSymbol t:
                        return Contains(t, x => x.OriginalDefinition.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == a.Tree && region.Contains(r.Span.Start)));
                    default:
                        return FromTypes(s.ContainingType);
                }
            }

            foreach (var v in variables)
            {
                var type = v.Symbol switch
                {
                    ILocalSymbol l => l.Type,
                    IParameterSymbol p => p.Type,
                    _ => null,
                };
                if (FromTypes(type)) return true;
            }
            foreach (var node in nodes)
            {
                if (node is IdentifierNameSyntax or GenericNameSyntax && FromTypes(model.GetSymbolInfo(node).Symbol)) return true;
                if (node is ImplicitObjectCreationExpressionSyntax or ObjectCreationExpressionSyntax && FromTypes(model.GetTypeInfo(node).Type)) return true;
            }
            return false;
        }

        // ---- rules ----------------------------------------------------------------------------

        void CheckForeignWrites(CellLayout cell, DataFlowAnalysis flow, List<SyntaxNode> nodes, Func<ISymbol, bool> foreign)
        {
            foreach (var local in flow.WrittenInside.OfType<ILocalSymbol>().Where(l => topLevel.Contains(l) && foreign(l)).OrderBy(l => l.Name, StringComparer.Ordinal))
            {
                var references = nodes.OfType<IdentifierNameSyntax>()
                    .Where(n => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(n).Symbol, local)).ToList();
                var site = references.FirstOrDefault(IsWrite) ?? references.FirstOrDefault();
                Report(cell, site?.Span ?? nodes[0].Span, RuleIds.AssignsOtherCellsVariable, DiagnosticSeverity.Error,
                    $"This cell changes '{local.Name}', which is declared in cell '{DeclaringCell(local)?.Key}'. A variable can only be set in the cell that declares it. Declare a new variable here instead, e.g. var {local.Name}2 = ...;");
            }
        }

        bool IsWrite(IdentifierNameSyntax node)
        {
            var op = model.GetOperation(node);
            if (op is null) return false;
            for (IOperation? child = op, parent = op.Parent; parent is not null; child = parent, parent = parent.Parent)
            {
                switch (parent)
                {
                    case IAssignmentOperation assign: return assign.Target == child;
                    case IIncrementOrDecrementOperation: return true;
                    case IArgumentOperation arg: return arg.Parameter?.RefKind is RefKind.Ref or RefKind.Out;
                    case ITupleOperation or IFieldReferenceOperation: continue; // deconstruction targets, struct fields
                    default: return false;
                }
            }
            return false;
        }

        void CheckReturns(CellLayout cell, List<SyntaxNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is ReturnStatementSyntax ret && !InsideFunction(ret))
                {
                    Report(cell, ret.ReturnKeyword.Span, RuleIds.TopLevelReturn, DiagnosticSeverity.Error,
                        "A notebook cell can't use a top-level 'return', because each cell runs on its own. Use if/else to skip the rest of the cell, or throw an exception to stop it.");
                }
                else if (node is GotoStatementSyntax { RawKind: (int)SyntaxKind.GotoStatement } go
                         && model.GetSymbolInfo(go.Expression!).Symbol is ILabelSymbol label
                         && DeclaringCell(label)?.CellIndex != cell.CellIndex)
                {
                    Report(cell, go.Span, RuleIds.TopLevelReturn, DiagnosticSeverity.Error,
                        $"'goto {label.Name}' jumps to a label in another cell. Cells run on their own, so control can't move between them.");
                }
            }

            static bool InsideFunction(SyntaxNode node) =>
                node.Ancestors().TakeWhile(n => n is not GlobalStatementSyntax)
                    .Any(n => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
        }

        void CheckMutations(CellLayout cell, List<GlobalStatementSyntax> statements, Func<ISymbol, bool> foreign, DataFlowAnalysis flow)
        {
            var written = flow.WrittenInside.ToImmutableHashSet(SymbolEqualityComparer.Default);
            bool IsForeignLocal(IOperation? op, out ILocalSymbol local)
            {
                local = null!;
                while (op is IConversionOperation { IsImplicit: true } conv) op = conv.Operand;
                if (op is not ILocalReferenceOperation r || !topLevel.Contains(r.Local) || !foreign(r.Local) || written.Contains(r.Local)) return false;
                if (r.Local.Type.IsValueType) return false; // a struct is copied into the cell; writes to it are RVB001
                local = r.Local;
                return true;
            }

            foreach (var op in statements.Select(s => model.GetOperation(s.Statement)).OfType<IOperation>().SelectMany(o => o.DescendantsAndSelf()))
            {
                switch (op)
                {
                    case IInvocationOperation call when call.Instance is not null
                        && MutatingMethods.Contains(call.TargetMethod.Name)
                        && !IsImmutable(call.TargetMethod.ContainingType)
                        && IsForeignLocal(call.Instance, out var receiver):
                        Report(cell, call.Syntax.Span, RuleIds.MutatesOtherCellsVariable, DiagnosticSeverity.Warning,
                            $"'{call.TargetMethod.Name}' changes '{receiver.Name}' in place, but '{receiver.Name}' is declared in cell '{DeclaringCell(receiver)?.Key}'. Reverb only tracks assignments, so cells that read '{receiver.Name}' won't re-run. Build a new value in a new variable instead (LINQ, 'with', or a copy).");
                        break;
                    case IInvocationOperation { Instance: null } call when call.TargetMethod.ContainingType.SpecialType == SpecialType.System_Array
                        && call.TargetMethod.Name is "Sort" or "Reverse" or "Clear" or "Fill"
                        && call.Arguments.Length > 0 && IsForeignLocal(call.Arguments[0].Value, out var array):
                        Report(cell, call.Syntax.Span, RuleIds.MutatesOtherCellsVariable, DiagnosticSeverity.Warning,
                            $"'Array.{call.TargetMethod.Name}' changes '{array.Name}' in place, but '{array.Name}' is declared in cell '{DeclaringCell(array)?.Key}'. Cells that read '{array.Name}' won't re-run. Sort or change a copy in a new variable instead.");
                        break;
                    case IAssignmentOperation assign when assign.Target switch
                    {
                        IPropertyReferenceOperation p => IsForeignLocal(p.Instance, out _),
                        IFieldReferenceOperation f => IsForeignLocal(f.Instance, out _),
                        IArrayElementReferenceOperation e => IsForeignLocal(e.ArrayReference, out _),
                        _ => false,
                    }:
                        var target = assign.Target switch
                        {
                            IPropertyReferenceOperation p => p.Instance,
                            IFieldReferenceOperation f => f.Instance,
                            IArrayElementReferenceOperation e => e.ArrayReference,
                            _ => null,
                        };
                        IsForeignLocal(target, out var owner);
                        Report(cell, assign.Target.Syntax.Span, RuleIds.MutatesOtherCellsVariable, DiagnosticSeverity.Warning,
                            $"This changes part of '{owner.Name}', which is declared in cell '{DeclaringCell(owner)?.Key}'. Reverb only tracks assignments, so cells that read '{owner.Name}' won't re-run. Build a new value in a new variable instead.");
                        break;
                }
            }

            static bool IsImmutable(INamedTypeSymbol type) =>
                type.SpecialType == SpecialType.System_String
                || type.ContainingNamespace?.ToDisplayString() == "System.Collections.Immutable";
        }

        const string MoveUsingMessage = "using directives belong in the setup cell ('// %% setup') at the top of the notebook. Move this line there.";

        void CheckPlacement(CompilationUnitSyntax root)
        {
            foreach (var u in root.Usings.Cast<SyntaxNode>().Concat(root.Externs))
            {
                var cell = map.CellAt(u.SpanStart);
                if (cell is null || cell.Kind == CellKind.Setup) continue;
                Report(cell, u.Span, RuleIds.MisplacedDeclaration, DiagnosticSeverity.Error, MoveUsingMessage);
            }
            foreach (var member in root.Members)
            {
                var cell = map.CellAt(member.SpanStart);
                if (cell is null) continue;
                if (member is GlobalStatementSyntax && cell.Kind is CellKind.Setup or CellKind.Types)
                {
                    Report(cell, member.Span, RuleIds.MisplacedDeclaration, DiagnosticSeverity.Error, cell.Kind == CellKind.Setup
                        ? "The setup cell can only contain using directives. Move this code into a code cell."
                        : "The types cell can only contain type declarations. Move this code into a code cell above it.");
                }
                else if (member is not GlobalStatementSyntax && cell.Kind != CellKind.Types)
                {
                    var name = member switch
                    {
                        BaseTypeDeclarationSyntax t => t.Identifier.Text,
                        DelegateDeclarationSyntax d => d.Identifier.Text,
                        BaseNamespaceDeclarationSyntax n => n.Name.ToString(),
                        _ => "this declaration",
                    };
                    var span = member is BaseTypeDeclarationSyntax td ? td.Identifier.Span : member.Span;
                    Report(cell, span, RuleIds.MisplacedDeclaration, DiagnosticSeverity.Error,
                        $"Type declarations belong in the types cell ('// %% types') at the end of the notebook. Move '{name}' there.");
                }
            }
        }

        ImmutableArray<UsingDeclaration> UsingDeclarations(List<GlobalStatementSyntax> statements)
        {
            var result = ImmutableArray.CreateBuilder<UsingDeclaration>();
            foreach (var g in statements)
            {
                if (g.Statement is not LocalDeclarationStatementSyntax { UsingKeyword.RawKind: (int)SyntaxKind.UsingKeyword } decl) continue;
                var type = model.GetTypeInfo(decl.Declaration.Type).Type;
                if (type is null || type.IsRefLikeType) continue; // ref structs can't be registered; they keep normal disposal
                result.Add(new UsingDeclaration(
                    decl.UsingKeyword.Span,
                    decl.AwaitKeyword.RawKind == (int)SyntaxKind.AwaitKeyword ? decl.AwaitKeyword.Span : null,
                    decl.Span.End,
                    [.. decl.Declaration.Variables.Select(v => v.Identifier.Text)]));
            }
            return result.ToImmutable();
        }

        // ---- cross-cell checks ----------------------------------------------------------------

        DependencyGraph BuildGraph(List<CellAnalysis> cells)
        {
            var nodes = cells.Where(c => c.Kind is CellKind.Code or CellKind.Types).Select(c => c.Key).ToList();
            var hasTypes = cells.Any(c => c.Kind == CellKind.Types);
            var edges = new List<DependencyEdge>();
            foreach (var c in cells.Where(c => c.Kind == CellKind.Code))
            {
                foreach (var group in c.Inputs.Where(i => i.DeclaringCell is not null && i.DeclaringCell != c.Key).GroupBy(i => i.DeclaringCell!))
                    edges.Add(new DependencyEdge(c.Key, group.Key, [.. group.Select(i => i.Name).Distinct().Order(StringComparer.Ordinal)]));
                if (c.UsesTypes && hasTypes) edges.Add(new DependencyEdge(c.Key, "types", []));
            }
            return new DependencyGraph(nodes, edges);
        }

        void CheckSharedOutputs(List<CellAnalysis> cells)
        {
            foreach (var cell in cells.Where(c => c.Kind == CellKind.Code))
            {
                foreach (var output in cell.Outputs)
                {
                    var readers = cells.Where(r => r.Key != cell.Key && r.Inputs.Any(i => SymbolEqualityComparer.Default.Equals(i.Symbol, output.Symbol)))
                        .Select(r => $"'{r.Key}'").ToList();
                    if (readers.Count == 0) continue;
                    var layout = a.Layout.Cells[cell.Index];
                    var declaration = output.Symbol!.DeclaringSyntaxReferences[0].GetSyntax();
                    var span = declaration switch
                    {
                        VariableDeclaratorSyntax v => v.Identifier.Span,
                        SingleVariableDesignationSyntax d => d.Identifier.Span,
                        LocalFunctionStatementSyntax f => f.Identifier.Span,
                        _ => declaration.Span,
                    };
                    var usedBy = $"It's used by cell{(readers.Count > 1 ? "s" : "")} {string.Join(", ", readers)}.";
                    // A warning here, not an error: this cell runs fine. The reading cell gets the blocking error.
                    if (output.Kind == VariableKind.Local && TypeShareProblem(((ILocalSymbol)output.Symbol).Type) is { } problem)
                        Report(layout, span, problem.Rule, DiagnosticSeverity.Warning, $"'{output.Name}' can't be passed to other cells because {problem.Reason}. {usedBy} {problem.Fix}");
                    else if (output.Kind == VariableKind.LocalFunction && FunctionShareProblem((IMethodSymbol)output.Symbol) is { } fproblem)
                        Report(layout, span, RuleIds.ValueCannotCrossCells, DiagnosticSeverity.Warning, $"Local function '{output.Name}' {fproblem}, so other cells can't call it. {usedBy} Move it into the cell that uses it, or make it a static method of a class in the types cell.");
                }
            }
        }

        // ---- diagnostics plumbing -------------------------------------------------------------

        void AddCompilerDiagnostics()
        {
            foreach (var d in a.Compilation.GetDiagnostics().Concat(a.generatorDiagnostics))
            {
                if (d.Severity < DiagnosticSeverity.Warning || d.IsSuppressed) continue;
                var message = d.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
                if (d.Location.SourceTree == a.Tree && map.CellAt(d.Location.SourceSpan.Start) is { } cell && cell.Key is not null)
                {
                    Report(cell, d.Location.SourceSpan, d.Id, d.Severity, message);
                    // A using after statements isn't a syntax node (the parser skips it), so CS1529 is how we see it.
                    if (d.Id == "CS1529" && cell.Kind != CellKind.Setup)
                        Report(cell, d.Location.SourceSpan, RuleIds.MisplacedDeclaration, DiagnosticSeverity.Error, MoveUsingMessage);
                }
                else
                    notebookDiagnostics.Add(new NotebookDiagnostic(d.Id, d.Severity, message, null, CellRange.Start));
            }
        }

        void Report(CellLayout cell, TextSpan span, string id, DiagnosticSeverity severity, string message)
        {
            var key = cell.Key ?? $"#{cell.CellIndex}";
            var range = map.ToCellRange(cell, span);
            var list = Get(key);
            if (list.Any(d => d.Id == id && d.Range == range && d.Message == message)) return;
            list.Add(new NotebookDiagnostic(id, severity, message, key, range));
        }

        List<NotebookDiagnostic> Get(string key)
        {
            if (!diagnostics.TryGetValue(key, out var list)) diagnostics[key] = list = [];
            return list;
        }

        static ImmutableArray<NotebookDiagnostic> Sorted(List<NotebookDiagnostic> list) =>
            [.. list.OrderBy(d => d.Range.StartLine).ThenBy(d => d.Range.StartColumn).ThenBy(d => d.Id, StringComparer.Ordinal)];
    }
}
