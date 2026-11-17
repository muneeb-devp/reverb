using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Reverb.Format;

namespace Reverb.Kernel;

/// <summary>How to start a kernel process.</summary>
public sealed record KernelLaunch
{
    /// <summary>The assembly whose <c>__kernel</c> command runs <see cref="KernelServer"/> (the CLI).</summary>
    public required string EntryAssembly { get; init; }

    /// <summary>The notebook file, if there is one. Sets the same AppContext data <c>dotnet run</c> does.</summary>
    public string? NotebookPath { get; init; }

    public IReadOnlyList<Directive> Directives { get; init; } = [];

    public string DotnetPath { get; init; } = DotnetHost.Find();

    public string? WorkingDirectory { get; init; }

    /// <summary>Arguments after the entry assembly. The CLI needs <c>__kernel</c>; a package kernel needs none.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = ["__kernel"];

    /// <summary>
    /// Run on Microsoft.NETCore.App alone. Right for the CLI, which also lists ASP.NET Core for the editor
    /// server; a package kernel keeps whatever frameworks its packages need.
    /// </summary>
    public bool BaseFrameworkOnly { get; init; } = true;

    /// <summary>This launch, but starting the given package kernel (or the plain one when null).</summary>
    public KernelLaunch WithBuild(KernelBuild? build) => build is null
        ? this
        : this with { EntryAssembly = build.EntryAssembly, Arguments = [], BaseFrameworkOnly = false };
}

public sealed class KernelExitedException(int exitCode, string stderr)
    : Exception($"The kernel process stopped (exit code {exitCode}).{(stderr.Length > 0 ? "\n" + stderr : "")}")
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Starts a kernel process and talks to it. Requests complete when the kernel reports them done;
/// everything else arrives through <see cref="Event"/>, in order.
/// </summary>
public sealed class KernelClient : IAsyncDisposable
{
    readonly KernelLaunch launch;
    readonly ConcurrentDictionary<int, TaskCompletionSource<KernelEvent>> waiting = new();
    readonly StringBuilder stderr = new();
    readonly Lock writeGate = new();
    Process process = null!;
    TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int nextId;

    KernelClient(KernelLaunch launch) => this.launch = launch;

    public event Action<KernelEvent>? Event;

    /// <summary>Lines the kernel process wrote to stdout that aren't protocol messages.</summary>
    public event Action<string>? StrayOutput;

    /// <summary>Raised when the process exits on its own (crash, Environment.Exit), not when we kill it.</summary>
    public event Action<int>? Exited;

    public bool HasExited => process.HasExited;

    public int ProcessId => process.Id;

    /// <summary>Closes the kernel's stdin without killing it, as when the parent process dies.</summary>
    internal void CloseInput() => process.StandardInput.Close();

    internal Task WaitForProcessExitAsync() => process.WaitForExitAsync();

    public string StandardError
    {
        get { lock (stderr) return stderr.ToString(); }
    }

    public static async Task<KernelClient> StartAsync(KernelLaunch launch, CancellationToken cancellationToken = default)
    {
        var client = new KernelClient(launch);
        await client.StartProcessAsync(cancellationToken);
        return client;
    }

    async Task StartProcessAsync(CancellationToken cancellationToken)
    {
        var config = FileBasedAppRuntime.WriteRuntimeConfig(launch.EntryAssembly, launch.Directives, launch.NotebookPath, launch.BaseFrameworkOnly);
        var info = new ProcessStartInfo(launch.DotnetPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = launch.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var arg in new[] { "exec", "--runtimeconfig", config, launch.EntryAssembly }.Concat(launch.Arguments)) info.ArgumentList.Add(arg);

        ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var p = new Process { StartInfo = info, EnableRaisingEvents = true };
        p.Start();
        process = p;
        _ = Task.Run(() => ReadEventsAsync(p));
        _ = Task.Run(() => ReadErrorsAsync(p));

        var exited = p.WaitForExitAsync(cancellationToken);
        var first = await Task.WhenAny(ready.Task, exited, Task.Delay(TimeSpan.FromSeconds(60), cancellationToken));
        if (first != ready.Task)
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            throw new KernelExitedException(p.HasExited ? p.ExitCode : -1, $"The kernel didn't start.\n{StandardError}");
        }
    }

    async Task ReadEventsAsync(Process p)
    {
        while (await p.StandardOutput.ReadLineAsync() is { } line)
        {
            KernelEvent? e;
            try
            {
                e = Protocol.DecodeEvent(line);
            }
            catch (System.Text.Json.JsonException)
            {
                e = null;
            }
            if (e is null)
            {
                StrayOutput?.Invoke(line);
                continue;
            }
            if (e.Type == EventTypes.Ready) ready.TrySetResult();
            Event?.Invoke(e);
            if (e.Type == EventTypes.Done && e.RequestId is { } id && waiting.TryRemove(id, out var tcs)) tcs.TrySetResult(e);
        }

        // stdout closed: the process is gone. Fail whatever was waiting.
        await p.WaitForExitAsync();
        if (!ReferenceEquals(p, process)) return; // an old process we killed on purpose
        var failure = new KernelExitedException(p.ExitCode, Tail(StandardError));
        foreach (var (id, tcs) in waiting) if (waiting.TryRemove(id, out _)) tcs.TrySetException(failure);
        if (!killing) Exited?.Invoke(p.ExitCode);
    }

    async Task ReadErrorsAsync(Process p)
    {
        while (await p.StandardError.ReadLineAsync() is { } line)
            lock (stderr) stderr.Append(line).Append('\n');
    }

    static string Tail(string text) => text.Length <= 4000 ? text : text[^4000..];

    /// <summary>Sends a request and waits until the kernel says it's done.</summary>
    public Task<KernelEvent> SendAsync(KernelRequest request)
    {
        request.Id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<KernelEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[request.Id] = tcs;
        try
        {
            Write(request);
        }
        catch (IOException e)
        {
            waiting.TryRemove(request.Id, out _);
            tcs.TrySetException(new KernelExitedException(process.HasExited ? process.ExitCode : -1, e.Message));
        }
        return tcs.Task;
    }

    /// <summary>Asks the running cell to stop. Doesn't wait.</summary>
    public void Interrupt()
    {
        try
        {
            Write(new KernelRequest { Type = RequestTypes.Interrupt, Id = Interlocked.Increment(ref nextId) });
        }
        catch (IOException)
        {
            // Already gone.
        }
    }

    void Write(KernelRequest request)
    {
        var line = Protocol.Encode(request);
        lock (writeGate)
        {
            process.StandardInput.WriteLine(line);
            process.StandardInput.Flush();
        }
    }

    volatile bool killing;

    /// <summary>Kills the kernel and starts a new one. Values and outputs are gone; reopen the notebook.</summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        Kill();
        await StartProcessAsync(cancellationToken);
    }

    public void Kill()
    {
        killing = true;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
            // Never started or already gone.
        }
        var failure = new KernelExitedException(-1, "The kernel was restarted.");
        foreach (var (id, tcs) in waiting) if (waiting.TryRemove(id, out _)) tcs.TrySetException(failure);
        killing = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            try
            {
                await SendAsync(new KernelRequest { Type = RequestTypes.Shutdown }).WaitAsync(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception e) when (e is TimeoutException or KernelExitedException or IOException)
            {
            }
        }
        killing = true;
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        process.Dispose();
    }
}

/// <summary>Finds the <c>dotnet</c> executable that is running this process.</summary>
public static class DotnetHost
{
    public static string Find()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath)) return hostPath;
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        // The runtime lives in <root>/shared/Microsoft.NETCore.App/<version>/.
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var root = runtimeDir is null ? null : Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        if (root is not null && File.Exists(Path.Combine(root, exe))) return Path.Combine(root, exe);
        return exe;
    }
}

/// <summary>
/// The runtime settings <c>dotnet run file.cs</c> gives a file-based app, so the kernel behaves the
/// same. File-based apps default to PublishAot=true, which writes these feature switches into the
/// app's runtimeconfig even for a plain run (taken from SDK 10.0.302).
/// </summary>
public static class FileBasedAppRuntime
{
    public static readonly IReadOnlyDictionary<string, bool> AotSwitches = new Dictionary<string, bool>
    {
        ["Microsoft.Extensions.DependencyInjection.VerifyOpenGenericServiceTrimmability"] = true,
        ["System.ComponentModel.DefaultValueAttribute.IsSupported"] = false,
        ["System.ComponentModel.Design.IDesignerHost.IsSupported"] = false,
        ["System.ComponentModel.TypeConverter.EnableUnsafeBinaryFormatterInDesigntimeLicenseContextSerialization"] = false,
        ["System.ComponentModel.TypeDescriptor.IsComObjectDescriptorSupported"] = false,
        ["System.Data.DataSet.XmlSerializationIsSupported"] = false,
        ["System.Diagnostics.Tracing.EventSource.IsSupported"] = false,
        ["System.Linq.Enumerable.IsSizeOptimized"] = true,
        ["System.Linq.Expressions.CanEmitObjectArrayDelegate"] = false,
        ["System.Net.SocketsHttpHandler.Http3Support"] = false,
        ["System.Reflection.Metadata.MetadataUpdater.IsSupported"] = false,
        ["System.Resources.ResourceManager.AllowCustomResourceTypes"] = false,
        ["System.Resources.UseSystemResourceKeys"] = false,
        ["System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported"] = false,
        ["System.Runtime.InteropServices.BuiltInComInterop.IsSupported"] = false,
        ["System.Runtime.InteropServices.EnableConsumingManagedCodeFromNativeHosting"] = false,
        ["System.Runtime.InteropServices.EnableCppCLIHostActivation"] = false,
        ["System.Runtime.InteropServices.Marshalling.EnableGeneratedComInterfaceComImportInterop"] = false,
        ["System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization"] = false,
        ["System.StartupHookProvider.IsSupported"] = false,
        ["System.Text.Encoding.EnableUnsafeUTF7Encoding"] = false,
        ["System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault"] = false,
        ["System.Threading.Thread.EnableAutoreleasePool"] = false,
    };

    /// <summary>True unless the notebook turns PublishAot off with <c>#:property PublishAot=false</c>.</summary>
    public static bool UsesAotSwitches(IEnumerable<Directive> directives) =>
        !directives.Any(d => d.Kind == "property" && d.Name.Equals("PublishAot", StringComparison.OrdinalIgnoreCase)
                             && d.Value?.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// Writes a runtimeconfig for the kernel: the entry assembly's own config plus the notebook's
    /// switches. Returns its path. Files are named by content, so identical configs are shared.
    /// </summary>
    public static string WriteRuntimeConfig(string entryAssembly, IEnumerable<Directive> directives, string? notebookPath, bool baseFrameworkOnly = true)
    {
        var basePath = Path.ChangeExtension(entryAssembly, ".runtimeconfig.json");
        var root = JsonNode.Parse(File.ReadAllText(basePath))!.AsObject();
        var options = root["runtimeOptions"]!.AsObject();
        // The entry assembly may also need ASP.NET Core (the editor server does). A file-based app
        // only gets the base framework, so the kernel does too.
        if (baseFrameworkOnly && options["frameworks"] is JsonArray frameworks)
        {
            var core = frameworks.FirstOrDefault(f => (string?)f?["name"] == "Microsoft.NETCore.App")
                ?? throw new InvalidOperationException($"{basePath} doesn't list Microsoft.NETCore.App.");
            options.Remove("frameworks");
            options["framework"] = core.DeepClone();
        }
        if (options["configProperties"] is not JsonObject props)
        {
            props = new JsonObject();
            options["configProperties"] = props;
        }
        if (UsesAotSwitches(directives))
            foreach (var (name, value) in AotSwitches) props[name] = value;
        if (notebookPath is not null)
        {
            var full = Path.GetFullPath(notebookPath);
            props["EntryPointFilePath"] = full;
            props["EntryPointFileDirectoryPath"] = Path.GetDirectoryName(full);
        }

        var json = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json + entryAssembly)))[..16];
        var dir = Path.Combine(Path.GetTempPath(), "reverb", "kernels");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"kernel-{hash}.runtimeconfig.json");
        if (!File.Exists(path)) WriteOnce(path, json);
        return path;
    }

    /// <summary>
    /// Writes a file that other processes may be writing or reading at the same moment (two
    /// kernels starting together). Written under a unique name and moved into place, so nobody
    /// sees half a file; if another process got there first, its copy is identical.
    /// </summary>
    static void WriteOnce(string path, string text)
    {
        var temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Someone else wrote it first.
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }
}
