using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Reverb.Format;

namespace Reverb.Kernel;

public sealed class KernelBuildException(string message) : Exception(message);

/// <summary>A kernel built for one set of packages, ready to start.</summary>
public sealed record KernelBuild(string Directory, string EntryAssembly, IReadOnlyList<string> References, IReadOnlyList<string> Analyzers, bool Reused);

public sealed record KernelBuildRequest
{
    public required IReadOnlyList<NotebookPackage> Packages { get; init; }

    /// <summary>The folder with Reverb.Kernel.dll and everything it needs (the CLI's own folder).</summary>
    public required string ToolDirectory { get; init; }

    /// <summary>Where the notebook lives. Its nearest nuget.config is used for restore, as <c>dotnet run</c> would.</summary>
    public string? NotebookDirectory { get; init; }

    public string CacheRoot { get; init; } = KernelBuilder.DefaultCacheRoot;

    public string DotnetPath { get; init; } = DotnetHost.Find();
}

/// <summary>
/// Builds a kernel that has a notebook's NuGet packages: a small generated project that references
/// Reverb's kernel assemblies and the packages, built with <c>dotnet build</c> so NuGet and the SDK
/// do the resolving. Builds are cached by package set, Reverb build and nuget.config, and shared by
/// every notebook that asks for the same packages.
/// </summary>
public static class KernelBuilder
{
    /// <summary>Bump when the generated project changes, so old cached kernels aren't reused.</summary>
    const int LayoutVersion = 2;

    const string EntryName = "reverb-kernel";

    public static string DefaultCacheRoot =>
        Environment.GetEnvironmentVariable("REVERB_KERNEL_CACHE") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".reverb", "kernels");

    /// <summary>The notebook's packages, without Reverb.Runtime (every kernel has its own copy).</summary>
    public static IReadOnlyList<NotebookPackage> PackagesOf(IEnumerable<Directive> directives) =>
    [
        .. directives
            .Where(d => d.Kind == "package" && !d.Name.Equals(NotebookUpgrader.RuntimePackage, StringComparison.OrdinalIgnoreCase))
            .Select(d => new NotebookPackage(d.Name.Trim(), d.Value?.Trim() ?? ""))
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>
    /// Returns a kernel with the requested packages, building it first if it isn't cached. Returns
    /// null when there are no packages: the plain kernel will do.
    /// </summary>
    public static async Task<KernelBuild?> EnsureAsync(KernelBuildRequest request, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request.Packages.Count == 0) return null;
        if (request.Packages.FirstOrDefault(p => p.Version.Length == 0) is { } unversioned)
            throw new KernelBuildException($"'#:package {unversioned.Name}' has no version. Add one, e.g. '#:package {unversioned.Name}@1.2.3' ('@*' takes the latest).");

        var assemblies = KernelAssemblies(request.ToolDirectory);
        var nugetConfig = FindNuGetConfig(request.NotebookDirectory);
        var dir = Path.Combine(request.CacheRoot, Key(request.Packages, assemblies, nugetConfig));
        if (TryLoad(dir) is { } cached) return cached;

        Directory.CreateDirectory(request.CacheRoot);
        await using var buildLock = await LockAsync(dir + ".lock", progress, cancellationToken);
        if (TryLoad(dir) is { } builtMeanwhile) return builtMeanwhile;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); // left over from a build that failed or was cut short

        var src = Path.Combine(dir, "src");
        var bin = Path.Combine(dir, "bin");
        Directory.CreateDirectory(src);
        WriteProject(src, request.Packages, assemblies);
        var project = Path.Combine(src, EntryName + ".csproj");

        progress?.Invoke($"Restoring {string.Join(", ", request.Packages)}");
        List<string> restore = ["restore", project];
        if (nugetConfig is not null) restore.AddRange(["--configfile", nugetConfig]);
        await RunDotnetAsync(request.DotnetPath, src, restore, "Couldn't restore the notebook's packages", cancellationToken);

        progress?.Invoke("Building the kernel");
        await RunDotnetAsync(request.DotnetPath, src, ["build", project, "--no-restore", "-c", "Release", "-o", bin], "Couldn't build a kernel with the notebook's packages", cancellationToken);

        File.Move(Path.Combine(src, KernelProcess.ReferencesFile), Path.Combine(bin, KernelProcess.ReferencesFile), overwrite: true);
        File.Move(Path.Combine(src, KernelProcess.AnalyzersFile), Path.Combine(bin, KernelProcess.AnalyzersFile), overwrite: true);
        File.WriteAllText(Path.Combine(dir, "ready"), string.Join("\n", request.Packages) + "\n");
        return (TryLoad(dir) ?? throw new KernelBuildException($"The kernel built in {dir} is incomplete. Delete that folder and try again.")) with { Reused = false };
    }

    static KernelBuild? TryLoad(string dir)
    {
        var entry = Path.Combine(dir, "bin", EntryName + ".dll");
        var references = Path.Combine(dir, "bin", KernelProcess.ReferencesFile);
        var analyzers = Path.Combine(dir, "bin", KernelProcess.AnalyzersFile);
        if (!File.Exists(Path.Combine(dir, "ready")) || !File.Exists(entry) || !File.Exists(references) || !File.Exists(analyzers)) return null;
        var paths = KernelProcess.ReadPaths(references);
        var analyzerPaths = KernelProcess.ReadPaths(analyzers);
        // Compile references and analyzers point into the NuGet cache. If that was cleared, build again.
        if (!paths.All(File.Exists) || !analyzerPaths.All(File.Exists)) return null;
        return new KernelBuild(dir, entry, paths, analyzerPaths, Reused: true);
    }

    /// <summary>Reverb.Kernel.dll and every assembly it needs from the tool folder.</summary>
    internal static List<string> KernelAssemblies(string toolDirectory)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(["Reverb.Kernel"]);
        while (queue.TryDequeue(out var name))
        {
            if (!seen.Add(name)) continue;
            var path = Path.Combine(toolDirectory, name + ".dll");
            if (!File.Exists(path)) continue; // part of the shared framework
            result.Add(path);
            using var pe = new PEReader(File.OpenRead(path));
            var md = pe.GetMetadataReader();
            foreach (var handle in md.AssemblyReferences) queue.Enqueue(md.GetString(md.GetAssemblyReference(handle).Name));
        }
        if (result.Count == 0) throw new KernelBuildException($"Reverb.Kernel.dll isn't in {toolDirectory}. Reinstall Reverb.");
        return result;
    }

    static string Key(IReadOnlyList<NotebookPackage> packages, List<string> assemblies, string? nugetConfig)
    {
        var text = new StringBuilder($"layout {LayoutVersion}\n");
        foreach (var p in packages) text.Append($"package {p.Name.ToLowerInvariant()}@{p.Version}\n");
        foreach (var a in assemblies)
        {
            using var pe = new PEReader(File.OpenRead(a));
            var md = pe.GetMetadataReader();
            text.Append($"assembly {Path.GetFileName(a)} {md.GetGuid(md.GetModuleDefinition().Mvid)}\n");
        }
        if (nugetConfig is not null)
            text.Append($"nuget.config {nugetConfig} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nugetConfig)))}\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16].ToLowerInvariant();
    }

    static string? FindNuGetConfig(string? start)
    {
        for (var dir = start is null ? null : new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
        {
            if (!dir.Exists) continue;
            var found = dir.EnumerateFiles().FirstOrDefault(f => f.Name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found.FullName;
        }
        return null;
    }

    static void WriteProject(string src, IReadOnlyList<NotebookPackage> packages, List<string> assemblies)
    {
        // Stop MSBuild picking up Directory.Build.* or central package management from folders above the cache.
        File.WriteAllText(Path.Combine(src, "Directory.Build.props"), "<Project />\n");
        File.WriteAllText(Path.Combine(src, "Directory.Build.targets"), "<Project />\n");
        File.WriteAllText(Path.Combine(src, "Directory.Packages.props"), "<Project />\n");
        File.WriteAllText(Path.Combine(src, "Program.cs"), "return await Reverb.Kernel.KernelProcess.RunAsync();\n");

        static string Xml(string s) => System.Security.SecurityElement.Escape(s);
        var project = new StringBuilder();
        project.Append($"""
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- Generated by Reverb: a kernel with this notebook's packages. -->
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>{EntryName}</AssemblyName>
                <UseAppHost>false</UseAppHost>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>disable</Nullable>
                <InvariantGlobalization>false</InvariantGlobalization>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
                <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
                <GenerateDocumentationFile>false</GenerateDocumentationFile>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
              </PropertyGroup>
              <ItemGroup>

            """);
        foreach (var a in assemblies) project.Append($"    <Reference Include=\"{Xml(a)}\" />\n");
        foreach (var p in packages) project.Append($"    <PackageReference Include=\"{Xml(p.Name)}\" Version=\"{Xml(p.Version)}\" />\n");
        project.Append($"""
              </ItemGroup>
              <!-- The package assemblies cells compile against: what the compiler would see in a dotnet build. -->
              <Target Name="ReverbListPackageReferences" AfterTargets="FindReferenceAssembliesForReferences">
                <ItemGroup>
                  <_ReverbPackageReference Include="@(ReferencePathWithRefAssemblies)"
                    Condition="'%(ReferencePathWithRefAssemblies.NuGetPackageId)' != '' and '%(ReferencePathWithRefAssemblies.FrameworkReferenceName)' == ''" />
                </ItemGroup>
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/{KernelProcess.ReferencesFile}" Lines="@(_ReverbPackageReference)" Overwrite="true" />
              </Target>
              <!-- The analyzers the compiler would load: the framework's and the packages' source generators are among them. -->
              <Target Name="ReverbListAnalyzers" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/{KernelProcess.AnalyzersFile}" Lines="@(Analyzer)" Overwrite="true" />
              </Target>
            </Project>

            """);
        File.WriteAllText(Path.Combine(src, EntryName + ".csproj"), project.ToString());
    }

    static async Task<FileStream> LockAsync(string path, Action<string>? progress, CancellationToken cancellationToken)
    {
        var told = false;
        while (true)
        {
            try
            {
                // Left in place afterwards: deleting it would let two waiting builders lock different files.
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (!told) progress?.Invoke("Waiting for another Reverb to finish building this kernel");
                told = true;
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    static async Task RunDotnetAsync(string dotnet, string workingDirectory, List<string> args, string failure, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        // Quiet, no build servers left running, nothing reported home.
        foreach (var a in new[] { "-nologo", "-v", "q", "-nodeReuse:false" }) info.ArgumentList.Add(a);
        if (args[0] == "build") info.ArgumentList.Add("--disable-build-servers");
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        // Variables set by an outer build or test run (MSBuildExtensionsPath and friends) would confuse this one.
        foreach (var name in info.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
            info.Environment.Remove(name);

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        if (process.ExitCode == 0) return;

        string text;
        lock (output) text = output.ToString();
        var errors = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(": error ", StringComparison.Ordinal))
            .Select(CleanError)
            .Distinct()
            .ToList();
        var details = errors.Count > 0 ? string.Join("\n", errors.Select(e => "  " + e)) : text.Trim();
        throw new KernelBuildException($"{failure}:\n{details}\nCheck the '#:package' lines (names and versions).");
    }

    /// <summary>"/path/x.csproj : error NU1101: Unable to find package Nope. [/path/x.csproj]" becomes "NU1101: Unable to find package Nope."</summary>
    static string CleanError(string line)
    {
        var at = line.IndexOf(": error ", StringComparison.Ordinal);
        var message = line[(at + ": error ".Length)..];
        if (message.EndsWith(']') && message.LastIndexOf(" [", StringComparison.Ordinal) is > 0 and var bracket) message = message[..bracket];
        return message.Trim();
    }
}
