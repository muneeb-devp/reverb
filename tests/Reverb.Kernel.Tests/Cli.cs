using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Reverb.Testing;

namespace Reverb.Kernel.Tests;

/// <summary>Runs the built CLI and plain `dotnet run`, for tests that compare the two.</summary>
static class Cli
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr);

    static string Configuration =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    public static string CliAssembly { get; } = Path.Combine(Snapshot.RepoRoot, "src", "Reverb.Cli", "bin", Configuration, "net10.0", "Reverb.Cli.dll");

    public static Task<Result> Reverb(string workingDirectory, params string[] args) =>
        RunAsync(DotnetHost.Find(), [CliAssembly, .. args], workingDirectory);

    public static Task<Result> Dotnet(string workingDirectory, params string[] args) =>
        RunAsync(DotnetHost.Find(), args, workingDirectory);

    public static async Task<Result> RunAsync(string exe, string[] args, string workingDirectory, int timeoutSeconds = 300)
    {
        using var p = Process.Start(StartInfo(exe, args, workingDirectory))!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException($"{exe} {string.Join(' ', args)} didn't finish in {timeoutSeconds}s.");
        }
        return new Result(p.ExitCode, await stdout, await stderr);
    }

    /// <summary>Starts the built CLI with stdin, stdout and stderr redirected, for commands that keep running.</summary>
    public static Process StartReverb(string workingDirectory, params string[] args)
    {
        var info = StartInfo(DotnetHost.Find(), [CliAssembly, .. args], workingDirectory);
        info.RedirectStandardInput = true;
        info.StandardInputEncoding = new UTF8Encoding(false);
        return Process.Start(info)!;
    }

    static ProcessStartInfo StartInfo(string exe, string[] args, string workingDirectory)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        // `dotnet test` passes its own SDK's MSBuild paths down to the tests. A child `dotnet` must
        // find its SDK itself, or one pinned by global.json loads the wrong SDK's targets.
        foreach (var name in new[] { "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildLoadMicrosoftTargetsReadOnly" })
            info.Environment.Remove(name);
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        // Package kernels the CLI builds go under artifacts/, not the user's ~/.reverb.
        info.Environment["REVERB_KERNEL_CACHE"] = Path.Combine(Snapshot.RepoRoot, "artifacts", "kernels");
        return info;
    }

    /// <summary>
    /// A folder where notebooks can be run with `dotnet run` against the Reverb.Runtime built from
    /// this repo. The package gets a version derived from its content, so NuGet caches never serve
    /// an older build, and its packages folder lives under artifacts/ instead of the user's cache.
    /// </summary>
    public static async Task<(string Directory, string RuntimeVersion)> NotebookFolderAsync()
    {
        var version = await RuntimeVersion.Value;
        var dir = Path.Combine(Path.GetTempPath(), "reverb-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        var artifacts = Path.Combine(Snapshot.RepoRoot, "artifacts");
        await File.WriteAllTextAsync(Path.Combine(dir, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config><add key="globalPackagesFolder" value="{Path.Combine(artifacts, "packages")}" /></config>
              <packageSources>
                <clear />
                <add key="local" value="{Path.Combine(artifacts, "feed")}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, "Directory.Build.props"), "<Project />");
        return (dir, version);
    }

    static readonly Lazy<Task<string>> RuntimeVersion = new(PackRuntimeAsync);

    static async Task<string> PackRuntimeAsync()
    {
        var dll = typeof(Ui).Assembly.Location;
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(dll)))[..10].ToLowerInvariant();
        var version = $"0.1.0-test.{hash}";
        var feed = Path.Combine(Snapshot.RepoRoot, "artifacts", "feed");
        if (File.Exists(Path.Combine(feed, $"Reverb.Runtime.{version}.nupkg"))) return version;
        var project = Path.Combine(Snapshot.RepoRoot, "src", "Reverb.Runtime", "Reverb.Runtime.csproj");
        var result = await Dotnet(Snapshot.RepoRoot, "pack", project, "-c", "Release", "-o", feed, $"-p:Version={version}", "--nologo");
        if (result.ExitCode != 0) throw new InvalidOperationException($"Packing Reverb.Runtime failed:\n{result.Stdout}\n{result.Stderr}");
        return version;
    }

    /// <summary>Copies a fixture into a notebook folder, pointing its runtime reference at the test package.</summary>
    public static async Task<string> CopyFixtureAsync(string name, string directory, string runtimeVersion)
    {
        var source = Path.Combine(Snapshot.FixturesDirectory, name + ".cs");
        var bytes = await File.ReadAllBytesAsync(source);
        var text = Encoding.UTF8.GetString(bytes).Replace("#:package Reverb.Runtime@0.1.0", $"#:package Reverb.Runtime@{runtimeVersion}");
        var target = Path.Combine(directory, name + ".cs");
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        await File.WriteAllBytesAsync(target, [.. (hasBom ? new byte[] { 0xEF, 0xBB, 0xBF } : []), .. new UTF8Encoding(false).GetBytes(text.TrimStart('﻿'))]);
        return target;
    }
}
