using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Reverb.Kernel;
using Reverb.Testing;

namespace Reverb.Server.Tests;

/// <summary>A server for one notebook in a temp folder, with a real kernel process.</summary>
sealed class TestServer : IAsyncDisposable
{
    static string Configuration =>
        typeof(TestServer).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    public static string CliAssembly { get; } = Path.Combine(Snapshot.RepoRoot, "src", "Reverb.Cli", "bin", Configuration, "net10.0", "Reverb.Cli.dll");

    TestServer(EditorServer server, string path)
    {
        Server = server;
        NotebookPath = path;
    }

    public EditorServer Server { get; }
    public string NotebookPath { get; }
    public NotebookHost Host => Server.Notebook;

    public static string TempFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reverb-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        // Keep any Directory.Build.props above the temp folder out of the notebook's build.
        File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />");
        return dir;
    }

    /// <summary>Package kernels built by tests, shared between them (under artifacts/, which git ignores).</summary>
    public static string KernelCache { get; } = Path.Combine(Snapshot.RepoRoot, "artifacts", "kernels");

    public static async Task<TestServer> StartAsync(string text, string name = "notebook.cs", bool appMode = false, string? kernelCache = null)
    {
        var path = Path.Combine(TempFolder(), name);
        await File.WriteAllTextAsync(path, text);
        var server = await EditorServer.StartAsync(new EditorOptions
        {
            NotebookPath = path,
            KernelEntryAssembly = CliAssembly,
            AutosaveDelay = TimeSpan.FromMilliseconds(200),
            AppMode = appMode,
            KernelCacheRoot = kernelCache ?? KernelCache,
        });
        return new TestServer(server, path);
    }

    public async Task<TestClient> ConnectAsync()
    {
        var client = await TestClient.ConnectAsync(Server);
        client.Context = DescribeKernel;
        return client;
    }

    /// <summary>
    /// Managed stacks of a stuck kernel, when the dotnet-stack tool is installed (CI installs it).
    /// </summary>
    static string Stacks(int pid)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("dotnet-stack") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "report", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) }) info.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(info)!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(30_000)) p.Kill();
            return "\nkernel stacks:\n" + output.Result + p.StandardError.ReadToEnd();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ""; // dotnet-stack isn't installed
        }
    }

    /// <summary>Whether the kernel process is working or stuck, for timeout messages.</summary>
    string DescribeKernel()
    {
        if (Host.KernelProcessId is not { } pid) return "no kernel process";
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            var cpu = p.HasExited ? "exited" : $"{p.TotalProcessorTime.TotalSeconds:F1} s CPU, {p.Threads.Count} threads, started {(DateTime.Now - p.StartTime).TotalSeconds:F0} s ago";
            return $"kernel {pid}: {cpu}; stderr: {Host.KernelStandardError.Trim()}{(p.HasExited ? "" : Stacks(pid))}";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"kernel {pid}: gone ({e.Message})";
        }
    }

    public string FileText() => File.ReadAllText(NotebookPath);

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

/// <summary>
/// A WebSocket client that keeps a copy of the notebook the way the browser does, from the
/// snapshot and the messages after it.
/// </summary>
sealed class TestClient : IAsyncDisposable
{
    public sealed class Cell
    {
        public string Id = "";
        public string Kind = "";
        public string Source = "";
        public string Status = "";
        public string? Message;
        public bool Edited;
        public int Runs;
        public List<OutputData> Outputs = [];
        public List<DiagnosticData> Diagnostics = [];
    }

    readonly ClientWebSocket? socket;
    readonly Task receiving = Task.CompletedTask;
    readonly Lock gate = new();
    long seq;

    // Set when the client talks to a host in-process instead of over the WebSocket.
    NotebookHost? host;
    ClientSession? session;

    TestClient(ClientWebSocket? socket)
    {
        this.socket = socket;
        if (socket is not null) receiving = Task.Run(ReceiveAsync);
    }

    public ConcurrentQueue<ServerMessage> Messages { get; } = new();
    public List<Cell> Cells { get; private set; } = [];
    public List<EdgeData> Edges { get; private set; } = [];
    public string KernelState { get; private set; } = "";
    public bool Lazy { get; private set; }
    public string Mode { get; private set; } = "";
    public int Snapshots { get; private set; }
    public List<PackageView> Packages { get; private set; } = [];

    public static async Task<TestClient> ConnectAsync(EditorServer server, string? token = null, string? origin = null)
    {
        var socket = new ClientWebSocket();
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws?token={token ?? server.Token}"), CancellationToken.None);
        var client = new TestClient(socket);
        await client.WaitFor(() => client.Snapshots > 0);
        return client;
    }

    /// <summary>
    /// Connects straight to a host, the way the stdio protocol will. Messages still go through
    /// JSON both ways, so they're checked like the WebSocket's.
    /// </summary>
    public static async Task<TestClient> AttachAsync(NotebookHost host)
    {
        var client = new TestClient(null) { host = host };
        client.session = host.Connect(m =>
        {
            var copy = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(m, BrowserJson.Default.ServerMessage), BrowserJson.Default.ServerMessage)!;
            lock (client.gate) client.Apply(copy);
            client.Messages.Enqueue(copy);
        });
        await client.WaitFor(() => client.Snapshots > 0);
        return client;
    }

    async Task ReceiveAsync()
    {
        var buffer = new byte[1 << 16];
        var message = new MemoryStream();
        try
        {
            while (socket!.State == WebSocketState.Open)
            {
                var r = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var m = JsonSerializer.Deserialize(message.ToArray(), BrowserJson.Default.ServerMessage)!;
                message.SetLength(0);
                lock (gate) Apply(m);
                Messages.Enqueue(m);
            }
        }
        catch (WebSocketException)
        {
        }
    }

    void Apply(ServerMessage m)
    {
        switch (m.Type)
        {
            case BrowserMessages.Snapshot:
                Snapshots++;
                Lazy = m.Lazy ?? false;
                Mode = m.Mode ?? "";
                KernelState = m.Kernel!.State;
                Edges = m.Edges ?? [];
                Packages = m.Packages ?? [];
                Cells = [.. m.Cells!.Select(c => new Cell { Id = c.Id, Kind = c.Kind, Source = c.Source, Status = c.Status, Message = c.Message, Edited = c.Edited, Runs = c.Runs, Outputs = c.Outputs, Diagnostics = c.Diagnostics })];
                break;
            case BrowserMessages.CellStatus when Find(m.CellId) is { } c:
                c.Status = m.Status!;
                c.Runs = m.Runs ?? c.Runs;
                c.Message = m.Message;
                break;
            case BrowserMessages.CellOutputs when Find(m.CellId) is { } c:
                c.Outputs = m.Outputs ?? [];
                break;
            case BrowserMessages.CellStream when Find(m.CellId) is { } c:
                if (c.Outputs.Count > 0 && c.Outputs[^1].Stream == m.Stream) c.Outputs[^1] = new OutputData { Stream = m.Stream, Text = c.Outputs[^1].Text + m.Text };
                else c.Outputs.Add(new OutputData { Stream = m.Stream, Text = m.Text });
                break;
            case BrowserMessages.CellDiagnostics when Find(m.CellId) is { } c:
                c.Diagnostics = m.Diagnostics ?? [];
                break;
            case BrowserMessages.GraphUpdate:
                Edges = m.Edges ?? [];
                break;
            case BrowserMessages.KernelStatus:
                KernelState = m.State!;
                break;
        }
    }

    Cell? Find(string? id) => id is null ? null : Cells.FirstOrDefault(c => c.Id == id);

    public Cell this[string id]
    {
        get
        {
            lock (gate) return Find(id) ?? throw new KeyNotFoundException($"No cell '{id}'. Cells: {string.Join(", ", Cells.Select(c => c.Id))}");
        }
    }

    public string Status(string id)
    {
        lock (gate) return Find(id)?.Status ?? "";
    }

    public string Stdout(string id)
    {
        lock (gate)
            return Find(id) is { } c ? string.Concat(c.Outputs.Where(o => o.Stream == "stdout").Select(o => o.Text)).ReplaceLineEndings("\n") : "";
    }

    public string Text(string id)
    {
        lock (gate)
            return Find(id) is { } c ? string.Concat(c.Outputs.Select(o => o.Text ?? o.Data?[Output.PlainText] ?? "")).ReplaceLineEndings("\n") : "";
    }

    public int Runs(string id)
    {
        lock (gate) return Find(id)?.Runs ?? 0;
    }

    /// <summary>The widget a cell shows, as the kernel describes it.</summary>
    public JsonElement? Widget(string id)
    {
        lock (gate)
        {
            var json = Find(id)?.Outputs.Select(o => o.Data?.GetValueOrDefault(Output.Widget)).FirstOrDefault(j => j is not null);
            return json is null ? null : JsonDocument.Parse(json).RootElement.Clone();
        }
    }

    public Task SetWidget(string widgetId, object value, string kind) =>
        SendAsync(new ClientMessage { Type = BrowserMessages.WidgetSetValue, WidgetId = widgetId, Kind = kind, Value = JsonSerializer.SerializeToElement(value, value.GetType(), WidgetJson.Default) });

    public IReadOnlyList<string> CellIds()
    {
        lock (gate) return [.. Cells.Select(c => c.Id)];
    }

    public bool Has(string type) => Messages.Any(m => m.Type == type);

    /// <summary>Extra detail for timeout messages, such as the kernel process's state.</summary>
    public Func<string>? Context { get; set; }

    public async Task SendAsync(ClientMessage m)
    {
        m.Seq = Interlocked.Increment(ref seq);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(m, BrowserJson.Default.ClientMessage);
        if (host is not null)
        {
            host.Receive(session!, JsonSerializer.Deserialize(bytes, BrowserJson.Default.ClientMessage)!);
            return;
        }
        await socket!.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public Task Update(string cell, string source) => SendAsync(new ClientMessage { Type = BrowserMessages.CellUpdate, CellId = cell, Source = source });
    public Task Run(string cell) => SendAsync(new ClientMessage { Type = BrowserMessages.CellRun, CellId = cell });

    public async Task WaitFor(Func<bool> condition, int seconds = 30, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            bool ok;
            lock (gate) ok = condition();
            if (ok) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what ?? "a condition"}. Cells: {Describe()}");
            await Task.Delay(20);
        }
    }

    public string Describe()
    {
        lock (gate)
        {
            var cells = string.Join(" | ", Cells.Select(c => $"{c.Id}={c.Status} runs={c.Runs} '{string.Concat(c.Outputs.Select(o => o.Text ?? o.Data?[Output.PlainText])).ReplaceLineEndings(" ")}'"));
            var kernel = string.Join(" / ", Messages.Where(m => m.Type is BrowserMessages.KernelStatus or BrowserMessages.Error).Select(m => $"{m.State ?? "error"}: {m.Message}"));
            return $"{cells}. Kernel now {KernelState}; kernel and error messages: {kernel}. {Context?.Invoke()}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (socket is null)
        {
            host!.Disconnect(session!);
            return;
        }
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception e) when (e is WebSocketException or TimeoutException)
        {
        }
        await receiving.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        socket.Dispose();
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(double))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
partial class WidgetJson : System.Text.Json.Serialization.JsonSerializerContext;
