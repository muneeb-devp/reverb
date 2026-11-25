using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Reverb.Analysis;

namespace Reverb.Kernel;

/// <summary>A collectible load context for one compiled cell or the types cell.</summary>
sealed class CellLoadContext(string name, Func<AssemblyName, Assembly?> resolve) : AssemblyLoadContext(name, isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName) => resolve(assemblyName);
}

/// <summary>Compiler errors in generated code. These point to a Reverb bug, not a user mistake.</summary>
public sealed record CompileFailure(string CellKey, ImmutableArray<Diagnostic> Errors)
{
    public string Message => $"Reverb generated code for cell '{CellKey}' that doesn't compile. This is a bug in Reverb; please report it.\n"
        + string.Join("\n", Errors.Select(e => e.ToString()));
}

/// <summary>
/// Compiles generated code and loads it. Each cell lives in its own collectible context, replaced
/// (and unloaded) when the cell's code changes. Running an unchanged cell reuses its assembly.
/// </summary>
public sealed class CellCompiler : IDisposable
{
    sealed class Compiled
    {
        public required string Hash { get; init; }
        public required CellLoadContext Context { get; init; }
        public required Func<CellContext, Task> Run { get; init; }
    }

    /// <summary>
    /// One compile-and-load at a time per process. The warm-up session and the first real run can
    /// both compile, and running them together only makes them compete for the same CPU.
    /// </summary>
    static readonly Lock compileGate = new();

    readonly Dictionary<string, Compiled> cells = new(StringComparer.Ordinal);
    readonly ImmutableArray<MetadataReference> references;
    readonly ImmutableArray<ISourceGenerator> generators;
    CSharpCompilation? baseCompilation;
    string baseKey = "";

    CellLoadContext? typesContext;
    Assembly? typesAssembly;
    MetadataReference? typesReference;
    GeneratorDriver? typesGenerators;
    string typesGeneratorsKey = "";

    public CellCompiler(ReferenceSet references)
    {
        this.references = references.References.Add(MetadataReference.CreateFromFile(typeof(CellContext).Assembly.Location));
        generators = references.Generators;
    }

    /// <summary>Changes whenever the types cell is recompiled. Part of every cell's cache key.</summary>
    public string TypesVersion { get; private set; } = "none";

    public int CompileCount { get; private set; }

    /// <summary>Weak references to contexts that were asked to unload. Tests use these to check they really go away.</summary>
    public List<WeakReference> Unloaded { get; } = [];

    /// <summary>Compiles the types cell if its code changed. Returns false (with the failure) if it doesn't compile.</summary>
    public bool EnsureTypes(NotebookAnalysis analysis, out CompileFailure? failure)
    {
        failure = null;
        var generated = CellCodeGenerator.GenerateTypes(analysis);
        var source = generated.Code?.Source ?? "";
        var hash = Hash(source + OptionsKey(analysis));
        if (hash == TypesVersion && (typesAssembly is not null || source.Length == 0)) return true;

        UnloadTypes();
        TypesVersion = hash;
        if (generated.Code is null) return generated.Blockers.IsEmpty;

        lock (compileGate)
        {
            // Generators only run here: what they extend (partial classes, partial methods) can only be declared in the types cell.
            var (pe, pdb, errors) = Emit(analysis, CellCodeGenerator.TypesAssemblyName, source, references, runGenerators: true);
            if (pe is null)
            {
                failure = new CompileFailure("types", errors);
                return false;
            }
            CompileCount++;
            typesReference = MetadataReference.CreateFromImage(pe);
            typesContext = new CellLoadContext("reverb:types", _ => null);
            typesAssembly = typesContext.LoadFromStream(new MemoryStream(pe), new MemoryStream(pdb!));
            return true;
        }
    }

    /// <summary>The cell's entry point, compiling it first if its generated code changed.</summary>
    public Func<CellContext, Task>? GetCell(NotebookAnalysis analysis, GeneratedCode code, bool usesTypes, out CompileFailure? failure)
    {
        failure = null;
        // Only cells that use notebook types depend on the types assembly's version.
        var hash = Hash(code.Source + "\n" + (usesTypes ? TypesVersion : "") + "\n" + OptionsKey(analysis));
        if (cells.TryGetValue(code.CellKey, out var existing))
        {
            if (existing.Hash == hash) return existing.Run;
            Evict(code.CellKey);
        }

        var refs = typesReference is null ? references : references.Add(typesReference);
        CellLoadContext context;
        Assembly assembly;
        lock (compileGate)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (pe, pdb, errors) = Emit(analysis, CellCodeGenerator.CellsAssemblyName, code.Source, refs);
            KernelTrace.Mark($"{code.CellKey}: emitted in {clock.ElapsedMilliseconds} ms");
            if (pe is null)
            {
                failure = new CompileFailure(code.CellKey, errors);
                return null;
            }
            CompileCount++;

            // Read the field, don't capture the assembly: a captured reference would keep old types
            // contexts alive. A cell compiled against an older types assembly is recompiled before it runs.
            context = new CellLoadContext($"reverb:cell:{code.CellKey}",
                name => name.Name == CellCodeGenerator.TypesAssemblyName ? typesAssembly : null);
            clock.Restart();
            assembly = context.LoadFromStream(new MemoryStream(pe), new MemoryStream(pdb!));
            KernelTrace.Mark($"{code.CellKey}: loaded in {clock.ElapsedMilliseconds} ms");
        }
        var method = assembly.GetType(code.ClassName, throwOnError: true)!.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static)!;
        var run = method.CreateDelegate<Func<CellContext, Task>>();
        cells[code.CellKey] = new Compiled { Hash = hash, Context = context, Run = run };
        return run;
    }

    public void Evict(string cellKey)
    {
        if (!cells.Remove(cellKey, out var old)) return;
        Unloaded.Add(new WeakReference(old.Context));
        old.Context.Unload();
    }

    void UnloadTypes()
    {
        if (typesContext is null) return;
        Unloaded.Add(new WeakReference(typesContext));
        typesContext.Unload();
        typesContext = null;
        typesAssembly = null;
        typesReference = null;
    }

    public void Dispose()
    {
        foreach (var key in cells.Keys.ToList()) Evict(key);
        UnloadTypes();
    }

    (byte[]? Pe, byte[]? Pdb, ImmutableArray<Diagnostic> Errors) Emit(NotebookAnalysis analysis, string assemblyName, string source,
        ImmutableArray<MetadataReference> refs, bool runGenerators = false)
    {
        // Cells share a base compilation (global usings + references) so Roslyn reuses its
        // reference binding. A new base is made when the references or options change.
        var options = analysis.Options;
        var key = $"{OptionsKey(analysis)}|{refs.Length}|{RuntimeHelpersHash(refs)}|{assemblyName}";
        if (baseCompilation is null || baseKey != key)
        {
            baseCompilation = CSharpCompilation.Create(assemblyName,
                [CSharpSyntaxTree.ParseText(options.GlobalUsingsSource, options.ParseOptions, "GlobalUsings.g.cs", Encoding.UTF8)],
                refs,
                // Cells are small; a parallel build only adds thread-pool waits.
                options.CompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithConcurrentBuild(false));
            baseKey = key;
        }
        var compilation = baseCompilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(source, options.ParseOptions, assemblyName + ".g.cs", Encoding.UTF8));
        if (runGenerators)
        {
            if (typesGeneratorsKey != OptionsKey(analysis)) typesGenerators = null;
            typesGeneratorsKey = OptionsKey(analysis);
            compilation = SourceGenerators.Run(generators, compilation, options.ParseOptions, ref typesGenerators, out _);
        }
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        return result.Success ? (pe.ToArray(), pdb.ToArray(), errors) : (null, null, errors);
    }

    static int RuntimeHelpersHash(ImmutableArray<MetadataReference> refs) =>
        refs.Aggregate(17, (h, r) => h * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r));

    static string OptionsKey(NotebookAnalysis a) =>
        $"{a.Options.LanguageVersion}|{a.Options.Nullable}|{a.Options.ImplicitUsings}|{a.Options.AllowUnsafe}|{string.Join(",", a.Options.ExtraDefines)}";

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..20];
}
