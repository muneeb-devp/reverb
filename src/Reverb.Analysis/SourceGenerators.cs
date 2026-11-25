using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Reverb.Analysis;

/// <summary>
/// The source generators <c>dotnet build</c> would run on a notebook: the framework's own
/// (System.Text.Json, Regex, LibraryImport and friends, from the Microsoft.NETCore.App.Ref pack)
/// and the ones the notebook's packages ship. Without them a <c>[JsonSerializable]</c> context or
/// a <c>[GeneratedRegex]</c> method compiles under <c>dotnet run</c> but not in Reverb.
/// </summary>
public static class SourceGenerators
{
    static readonly ConcurrentDictionary<string, LoadedGenerators> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The framework generators matching the runtime this process runs on: the reference pack of the
    /// same version, or else the newest one of the same major version, from the dotnet install or the
    /// NuGet cache.
    /// </summary>
    public static IReadOnlyList<string> FrameworkGeneratorPaths()
    {
        var runtime = Environment.Version;
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var pack = new[]
            {
                Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref"),
                Path.Combine(nugetRoot, "microsoft.netcore.app.ref"),
            }
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Select(dir => (Dir: dir, Version: Version.TryParse(Path.GetFileName(dir).Split('-')[0], out var v) ? v : null))
            .Where(p => p.Version?.Major == runtime.Major && Directory.Exists(AnalyzerDir(p.Dir)))
            .OrderByDescending(p => p.Version == new Version(runtime.Major, runtime.Minor, runtime.Build))
            .ThenByDescending(p => p.Version)
            .Select(p => AnalyzerDir(p.Dir))
            .FirstOrDefault();
        return pack is null ? [] : [.. Directory.EnumerateFiles(pack, "*.dll").Order(StringComparer.Ordinal)];

        static string AnalyzerDir(string packDir) => Path.Combine(packDir, "analyzers", "dotnet", "cs");
    }

    /// <summary>
    /// The analyzer assembly at <paramref name="path"/>, with Reference null when it has no C# source
    /// generators (most analyzer assemblies only have diagnostic analyzers) or failed to load.
    /// </summary>
    public static LoadedGenerators Load(string path) =>
        cache.GetOrAdd(Path.GetFullPath(path), p =>
        {
            if (!File.Exists(p)) return new LoadedGenerators(null, null);
            var reference = new AnalyzerFileReference(p, Loader.Instance);
            string? problem = null;
            reference.AnalyzerLoadFailed += (_, e) => problem ??= $"Couldn't load the source generators in {Path.GetFileName(p)}: {e.Message}";
            var found = !reference.GetGenerators(LanguageNames.CSharp).IsEmpty;
            return new LoadedGenerators(found ? reference : null, problem);
        });

    /// <summary>
    /// Runs the generators over <paramref name="compilation"/>, reusing what <paramref name="driver"/>
    /// already generated for unchanged input. Returns the compilation with the generated files added.
    /// </summary>
    public static CSharpCompilation Run(ImmutableArray<ISourceGenerator> generators, CSharpCompilation compilation,
        CSharpParseOptions parseOptions, ref GeneratorDriver? driver, out ImmutableArray<Diagnostic> diagnostics)
    {
        diagnostics = [];
        if (generators.IsEmpty) return compilation;
        driver ??= CSharpGeneratorDriver.Create(generators, parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out diagnostics);
        return (CSharpCompilation)updated;
    }

    /// <summary>
    /// Loads each generator folder into its own context, so two packages can ship different versions
    /// of a helper assembly. Roslyn and the framework always come from the host, as in the compiler.
    /// </summary>
    sealed class Loader : IAnalyzerAssemblyLoader
    {
        public static readonly Loader Instance = new();

        readonly ConcurrentDictionary<string, FolderContext> contexts = new(StringComparer.OrdinalIgnoreCase);

        public void AddDependencyLocation(string fullPath)
        {
        }

        public Assembly LoadFromPath(string fullPath)
        {
            var dir = Path.GetDirectoryName(fullPath)!;
            return contexts.GetOrAdd(dir, d => new FolderContext(d)).LoadFromAssemblyPath(fullPath);
        }

        sealed class FolderContext(string dir) : AssemblyLoadContext($"reverb:generators:{dir}")
        {
            protected override Assembly? Load(AssemblyName name)
            {
                if (name.Name is null || name.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)) return null;
                var path = Path.Combine(dir, name.Name + ".dll");
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }
        }
    }
}

public sealed record LoadedGenerators(AnalyzerFileReference? Reference, string? Problem);
