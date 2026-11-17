using System.Reflection;
using System.Text.Json;
using Reverb.Format;

namespace Reverb.Server;

/// <summary>
/// The stdio protocol's version and the message types only it has. Everything else is the browser
/// protocol, with a <c>notebook</c> field saying which notebook a message is for.
/// </summary>
public static class StdioProtocol
{
    /// <summary>Goes up when a change would break an extension written for the previous version.</summary>
    public const int Version = 1;

    public const string Hello = "hello";
    public const string HelloResult = "hello.result";
    public const string FormatParse = "format.parse";
    public const string FormatParsed = "format.parsed";
    public const string FormatSerialize = "format.serialize";
    public const string FormatSerialized = "format.serialized";
    public const string FormatTemplate = "format.template";
    public const string FormatTemplated = "format.templated";
    public const string NotebookOpen = "notebook.open";
    public const string NotebookClose = "notebook.close";
}

/// <summary>
/// <c>reverb serve --stdio</c>: one process per editor window, JSON lines both ways. Format requests
/// are answered here and keep no state. Each open notebook gets its own <see cref="NotebookHost"/>,
/// and messages are routed to it by the notebook's path.
/// </summary>
public sealed class StdioServer
{
    sealed class OpenNotebook(string name)
    {
        /// <summary>The path as the client sent it in notebook.open, put on every message from the host.</summary>
        public string Name { get; } = name;

        public Task Opening = Task.CompletedTask;
        public NotebookHost? Host;
        public ClientSession? Session;

        /// <summary>Messages that arrived while the host was starting. Null once they've been handed over.</summary>
        public List<ClientMessage>? Waiting = [];

        /// <summary>Closed or replaced: nothing more from its host reaches the client.</summary>
        public bool Closed;
    }

    static readonly StringComparer PathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    static readonly string ReverbVersion =
        typeof(StdioServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    readonly Func<string, EditorOptions> optionsFor;
    readonly TextWriter log;

    // Lock order: gate, then writeLock.
    readonly Lock gate = new();
    readonly Lock writeLock = new();
    readonly Dictionary<string, OpenNotebook> notebooks = new(PathComparer);
    readonly List<Task> closing = [];

    Stream output = Stream.Null;
    bool outputBroken;

    /// <param name="optionsFor">The options for a notebook's host, given its full path. The server sets the client-owned options itself.</param>
    /// <param name="log">For the server's own problems. Messages and notebook output never go here.</param>
    public StdioServer(Func<string, EditorOptions> optionsFor, TextWriter log)
    {
        this.optionsFor = optionsFor;
        this.log = log;
    }

    /// <summary>Handles messages until the input ends or the token is cancelled, then closes every notebook. Returns the exit code.</summary>
    public async Task<int> RunAsync(TextReader input, Stream output, CancellationToken cancellationToken = default)
    {
        this.output = output;
        try
        {
            // Some streams ignore cancellation while a read is waiting, so stop waiting instead.
            while (await input.ReadLineAsync(cancellationToken).AsTask().WaitAsync(cancellationToken) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line)) Handle(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        await ShutDownAsync();
        return 0;
    }

    void Handle(string line)
    {
        ClientMessage? m;
        string? problem = null;
        try
        {
            m = JsonSerializer.Deserialize(line, BrowserJson.Default.ClientMessage);
        }
        catch (JsonException e)
        {
            m = null;
            problem = e.Message + " ";
        }
        if (m is null)
        {
            Send(new ServerMessage { Type = BrowserMessages.Error, Message = $"A line from the editor isn't a message the server can read: {problem}Each line must be one JSON object with a \"type\"." });
            return;
        }

        try
        {
            Dispatch(m);
        }
        catch (Exception e)
        {
            log.WriteLine($"reverb: internal error handling '{m.Type}': {e}");
            Send(Error(m, $"Something went wrong in the server handling '{m.Type}': {e.Message}"));
        }
    }

    void Dispatch(ClientMessage m)
    {
        switch (m.Type)
        {
            case StdioProtocol.Hello:
                Send(new ServerMessage { Type = StdioProtocol.HelloResult, RequestId = m.RequestId, ProtocolVersion = StdioProtocol.Version, ReverbVersion = ReverbVersion });
                return;
            case StdioProtocol.FormatParse:
                Parse(m);
                return;
            case StdioProtocol.FormatSerialize:
                Serialize(m);
                return;
            case StdioProtocol.FormatTemplate:
                Send(new ServerMessage
                {
                    Type = StdioProtocol.FormatTemplated,
                    RequestId = m.RequestId,
                    Text = NotebookTemplates.Starter(optionsFor("").RuntimeVersion).Serialize(),
                });
                return;
            case StdioProtocol.NotebookOpen:
                Open(m);
                return;
            case StdioProtocol.NotebookClose:
                Close(m);
                return;
        }
        if (m.Notebook is null)
        {
            Send(Error(m, $"'{m.Type}' isn't a request this server knows. Without a \"notebook\" path it understands hello, format.parse, format.serialize and format.template; anything else needs the path of an open notebook."));
            return;
        }
        Route(m);
    }

    static ServerMessage Error(ClientMessage m, string message) =>
        new() { Type = BrowserMessages.Error, RequestId = m.RequestId, Notebook = m.Notebook, Message = message };

    // ---- format requests ------------------------------------------------------------------------

    void Parse(ClientMessage m)
    {
        if (m.Text is null)
        {
            Send(Error(m, "format.parse needs the notebook's text in \"text\"."));
            return;
        }
        // The same upgrade a host does when it opens the text, so the ids match.
        var doc = NotebookHost.Load(m.Text, optionsFor(m.Notebook ?? "").RuntimeVersion);
        Send(new ServerMessage { Type = StdioProtocol.FormatParsed, RequestId = m.RequestId, Parts = NotebookParts.Split(doc) });
    }

    void Serialize(ClientMessage m)
    {
        if (m.Parts is not { } parts)
        {
            Send(Error(m, "format.serialize needs the document in \"parts\", as format.parsed returned it."));
            return;
        }
        var newline = parts.Newline ?? "\n";
        if (newline is not ("\n" or "\r\n"))
        {
            Send(Error(m, "The document's \"newline\" must be \"\\n\" or \"\\r\\n\"."));
            return;
        }
        if (CleanCells(parts.Cells, out var cells) is { } problem)
        {
            Send(Error(m, $"The notebook couldn't be written: {problem}"));
            return;
        }
        // Stateless: a list that needed fixing is written fixed. The live host tells the editor what changed.
        // A fixed seed, as in format.parse: without a host to send new ids back, the editor saves the same
        // id-less cell again, and it must get the same id or the file changes on every save.
        var (valid, _) = CellListNormalizer.Normalize(cells, new Random(0));
        PreambleLine[] preamble = [.. (parts.Preamble ?? []).Where(l => l is not null).Select(l => new PreambleLine(l.Text ?? "", l.Terminator ?? newline))];
        string text;
        try
        {
            text = NotebookParts.Join(new NotebookPartsData(preamble, newline, parts.Bom, valid)).Serialize();
        }
        catch (ArgumentException e)
        {
            Send(Error(m, $"The notebook couldn't be written: {e.Message}"));
            return;
        }
        Send(new ServerMessage { Type = StdioProtocol.FormatSerialized, RequestId = m.RequestId, Text = text });
    }

    /// <summary>
    /// Makes a cell list from JSON safe to use: missing sources and attributes become empty, CRLF in
    /// sources becomes LF, null entries are dropped, and raw text with holes is ignored. Returns what's wrong if a cell can't be used.
    /// </summary>
    static string? CleanCells(IReadOnlyList<PartCell?>? cells, out List<PartCell> clean)
    {
        clean = [];
        if (cells is null) return null;
        for (var i = 0; i < cells.Count; i++)
        {
            if (cells[i] is not { } c) continue;
            if (c.Kind is not ("setup" or "code" or "markdown" or "types"))
            {
                var which = c.Id is null ? $"Cell {i + 1}" : $"Cell '{c.Id}'";
                return c.Kind is null
                    ? $"{which} has no kind. A cell's kind is setup, code, markdown or types."
                    : $"{which} has kind '{c.Kind}'. A cell's kind is setup, code, markdown or types.";
            }
            clean.Add(c with
            {
                // Sources use \n. A \r\n from the editor would be written as \r\r\n in a CRLF file.
                Source = (c.Source ?? "").Replace("\r\n", "\n"),
                Attributes = c.Attributes is null ? [] : [.. c.Attributes.Where(a => a is { Key: not null })],
                Raw = NotebookHost.Complete(c.Raw) ? c.Raw : null,
            });
        }
        return null;
    }

    // ---- notebooks ------------------------------------------------------------------------------

    static string? KeyOf(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    void Open(ClientMessage m)
    {
        if (m.Notebook is null || m.Text is null)
        {
            Send(Error(m, "notebook.open needs \"notebook\" (the file's full path) and \"text\" (the notebook's text as the editor has it)."));
            return;
        }
        if (KeyOf(m.Notebook) is not { } key)
        {
            Send(Error(m, $"'{m.Notebook}' isn't a valid file path. Send the notebook's full path."));
            return;
        }
        var options = optionsFor(key) with { NotebookPath = key, ClientOwnsFile = true, RunOnOpen = false, InitialText = m.Text };
        var notebook = new OpenNotebook(m.Notebook);
        OpenNotebook? old;
        lock (gate)
        {
            notebooks.Remove(key, out old);
            notebooks[key] = notebook;
            notebook.Opening = OpenHostAsync(key, notebook, options);
        }
        // Opening it again (the editor reverted it, say) starts over with the new text.
        if (old is not null) Retire(old);
    }

    async Task OpenHostAsync(string key, OpenNotebook notebook, EditorOptions options)
    {
        await Task.Yield(); // the caller holds the gate
        NotebookHost host;
        try
        {
            host = await NotebookHost.OpenAsync(options);
        }
        catch (Exception e)
        {
            log.WriteLine($"reverb: couldn't open {notebook.Name}: {e}");
            bool closed;
            lock (gate)
            {
                if (notebooks.TryGetValue(key, out var current) && current == notebook) notebooks.Remove(key);
                closed = notebook.Closed;
            }
            // The VS Code extension recognises "couldn't be opened: " and drops its session, so keep the wording.
            if (!closed) Send(new ServerMessage { Type = BrowserMessages.Error, Notebook = notebook.Name, Message = $"{notebook.Name} couldn't be opened: {e.Message}" });
            return;
        }

        var keep = false;
        lock (gate)
        {
            if (!notebook.Closed)
            {
                keep = true;
                notebook.Host = host;
                notebook.Session = host.Connect(message => Forward(notebook, message));
                foreach (var waiting in notebook.Waiting!) host.Receive(notebook.Session, waiting);
                notebook.Waiting = null;
            }
        }
        if (!keep) await DisposeHostAsync(notebook.Name, host);
    }

    void Close(ClientMessage m)
    {
        if (m.Notebook is null || KeyOf(m.Notebook) is not { } key) return;
        OpenNotebook? notebook;
        lock (gate) notebooks.Remove(key, out notebook);
        // Closing one that isn't open is fine: the editor may close a notebook that failed to open.
        if (notebook is not null) Retire(notebook);
    }

    void Route(ClientMessage m)
    {
        if (m.Type == BrowserMessages.NotebookSync && m.Cells is not null)
        {
            if (CleanCells(m.Cells, out var cells) is { } problem)
            {
                Send(Error(m, $"The cells sent for {m.Notebook} couldn't be used: {problem} Reopen the notebook to get the cells back in step."));
                return;
            }
            m.Cells = cells;
        }

        var found = false;
        if (KeyOf(m.Notebook!) is { } key)
        {
            lock (gate)
            {
                if (notebooks.TryGetValue(key, out var notebook))
                {
                    found = true;
                    if (notebook.Waiting is { } waiting) waiting.Add(m);
                    else notebook.Host!.Receive(notebook.Session!, m);
                }
            }
        }
        // The VS Code extension recognises "isn't open in Reverb" and drops its session, so keep the wording.
        if (!found)
            Send(Error(m, $"The notebook {m.Notebook} isn't open in Reverb, so its '{m.Type}' message was dropped. Reopen the notebook to keep working on it."));
    }

    /// <summary>Stops a notebook that's no longer in the table: nothing more from it is sent, and its host shuts down.</summary>
    void Retire(OpenNotebook notebook)
    {
        lock (gate)
        {
            lock (writeLock) notebook.Closed = true;
        }
        var done = RetireAsync(notebook);
        lock (closing) closing.Add(done);
    }

    async Task RetireAsync(OpenNotebook notebook)
    {
        // Still starting: once it's up, OpenHostAsync sees it's closed and disposes it.
        await notebook.Opening;
        if (notebook.Host is not { } host) return;
        host.Disconnect(notebook.Session!);
        await DisposeHostAsync(notebook.Name, host);
    }

    async Task DisposeHostAsync(string name, NotebookHost host)
    {
        try
        {
            await host.DisposeAsync();
        }
        catch (Exception e)
        {
            log.WriteLine($"reverb: problem shutting down {name}: {e}");
        }
    }

    async Task ShutDownAsync()
    {
        List<OpenNotebook> open;
        lock (gate)
        {
            open = [.. notebooks.Values];
            notebooks.Clear();
        }
        foreach (var notebook in open) Retire(notebook);
        Task[] pending;
        lock (closing) pending = [.. closing];
        await Task.WhenAll(pending);
    }

    // ---- output ---------------------------------------------------------------------------------

    void Forward(OpenNotebook notebook, ServerMessage m)
    {
        lock (writeLock)
        {
            if (notebook.Closed) return;
            m.Notebook = notebook.Name;
            Write(m);
        }
    }

    void Send(ServerMessage m)
    {
        lock (writeLock) Write(m);
    }

    /// <summary>One message, one line. Called with the write lock held.</summary>
    void Write(ServerMessage m)
    {
        if (outputBroken) return;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(m, BrowserJson.Default.ServerMessage);
            output.Write(bytes);
            output.WriteByte((byte)'\n');
            output.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The editor has gone. Its end of stdin closes too, and that shuts the server down.
            outputBroken = true;
            log.WriteLine($"reverb: can't write to the editor any more: {e.Message}");
        }
    }
}
