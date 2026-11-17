using System.Text.Json;
using System.Threading.Channels;
using Reverb.Format;
using Reverb.Kernel;

namespace Reverb.Server;

public sealed record EditorOptions
{
    public required string NotebookPath { get; init; }

    /// <summary>The assembly that runs the kernel with its <c>__kernel</c> command (the CLI).</summary>
    public required string KernelEntryAssembly { get; init; }

    /// <summary>The Reverb.Runtime version to add to notebooks that don't reference it yet.</summary>
    public string RuntimeVersion { get; init; } = "0.1.0";

    public string Host { get; init; } = "127.0.0.1";

    /// <summary>0 picks a free port.</summary>
    public int Port { get; init; }

    /// <summary>The access token. A random one is made when this is null.</summary>
    public string? Token { get; init; }

    public TimeSpan AutosaveDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Where kernels built for notebook packages are kept. Null means the usual place (~/.reverb/kernels).</summary>
    public string? KernelCacheRoot { get; init; }

    /// <summary>
    /// Serve the notebook as an app (<c>reverb run</c>): outputs and widgets only. Code isn't sent
    /// to the browser, nothing can be edited, the file is never written, and lazy mode is off.
    /// </summary>
    public bool AppMode { get; init; }

    /// <summary>
    /// A client (VS Code) owns the file: it saves it and notices changes on disk, and tells the
    /// host with notebook.sync and notebook.reload. The host never writes or watches the file.
    /// </summary>
    public bool ClientOwnsFile { get; init; }

    /// <summary>Run the notebook once the kernel is up. Off for VS Code, which opens files without being asked.</summary>
    public bool RunOnOpen { get; init; } = true;

    /// <summary>The notebook's text, used instead of reading the file.</summary>
    public string? InitialText { get; init; }

    public TextWriter Log { get; init; } = TextWriter.Null;
}

/// <summary>A browser tab connected to the notebook.</summary>
public sealed class ClientSession(Action<ServerMessage> send)
{
    internal Action<ServerMessage> Send { get; } = send;

    /// <summary>The last <see cref="ClientMessage.Seq"/> handled for this tab.</summary>
    internal long LastSeq { get; set; }
}

/// <summary>
/// One open notebook: the document as the editor has it, the file on disk, the kernel process and
/// what it last reported, and the connected tabs. Everything runs on one logical thread: browser
/// messages, kernel events, timers and file changes are all queued and handled in order.
/// </summary>
/// <remarks>
/// The editor's text and the kernel's code differ on purpose. Typing changes the document (and is
/// saved), but a cell's new code only reaches the kernel when the cell is run, the notebook is run
/// all, or the file changes on disk. Structural changes (add, delete, move, disable) apply at once.
/// </remarks>
public sealed class NotebookHost : IAsyncDisposable
{
    sealed class CellRuntime
    {
        public string Status = "stale";
        public long? ElapsedMs;
        public string? Message;
        public List<OutputData> Outputs = [];

        /// <summary>How many times the cell has started running in this session.</summary>
        public int Runs;
    }

    readonly EditorOptions options;
    readonly Channel<Func<Task>> inbox = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task loop;
    readonly List<ClientSession> clients = [];
    readonly Dictionary<string, string> kernelSources = new(StringComparer.Ordinal);
    readonly Dictionary<string, CellRuntime> runtime = new(StringComparer.Ordinal);
    readonly Timer saveTimer;
    readonly FileWatcher? watcher;

    NotebookDocument doc;
    string? diskText;
    int saveFailures;
    int version;
    int analyzedVersion = -1;
    List<DiagnosticData> diagnostics = [];
    List<EdgeData> graph = [];

    KernelClient? kernel;
    int kernelGeneration;
    int outstanding;
    string kernelState = "starting";
    string? kernelMessage;
    bool kernelLazy;
    volatile bool disposed;

    /// <summary>The packages the current kernel was started with (or is being built with).</summary>
    IReadOnlyList<NotebookPackage> kernelPackages = [];

    /// <summary>Work sent while the kernel was building or starting. Sent once it's up.</summary>
    readonly List<(KernelRequest Request, Action? Done)> waitingWork = [];

    /// <summary>Cancelled when the host shuts down, so a package build in progress stops too.</summary>
    readonly CancellationTokenSource lifetime = new();
    readonly List<DateTime> crashes = [];

    // A widget change that's still running, and the newest value that arrived meanwhile.
    readonly HashSet<string> widgetsInFlight = new(StringComparer.Ordinal);
    readonly Dictionary<string, JsonElement?> widgetsWaiting = new(StringComparer.Ordinal);

    NotebookHost(EditorOptions options, NotebookDocument doc, string? diskText)
    {
        this.options = options;
        this.doc = doc;
        this.diskText = diskText;
        Path = System.IO.Path.GetFullPath(options.NotebookPath);
        saveTimer = new Timer(_ => Post(() => SaveNow()));
        if (!options.ClientOwnsFile) watcher = new FileWatcher(Path, () => Post(CheckFileAsync));
        loop = Task.Run(RunLoopAsync);
    }

    public string Path { get; }

    /// <summary>The document as the editor has it. Read it from the host's thread or after <see cref="IdleAsync"/>.</summary>
    public NotebookDocument Document => doc;

    public int Version => version;

    /// <summary>The kernel's process id, for tests that kill it.</summary>
    public int? KernelProcessId => kernel?.ProcessId;

    /// <summary>What the kernel process has written to stderr, for diagnosing failures.</summary>
    public string KernelStandardError => kernel?.StandardError ?? "";

    public static async Task<NotebookHost> OpenAsync(EditorOptions options)
    {
        var path = System.IO.Path.GetFullPath(options.NotebookPath);
        var text = options.InitialText ?? NotebookFile.ReadText(path);
        var host = new NotebookHost(options, Load(text, options.RuntimeVersion), text);
        Task started = Task.CompletedTask;
        await host.InvokeAsync(() =>
        {
            started = host.StartKernel(run: options.RunOnOpen, readyMessage: null);
            return Task.CompletedTask;
        });
        // A plain kernel is up in a moment, so wait for it. Building one for packages can take a
        // while; the page opens meanwhile and shows the progress.
        if (host.kernelPackages.Count == 0) await started;
        return host;
    }

    /// <summary>Parses a file and brings it up to date in memory. The file itself is only rewritten when it's next saved.</summary>
    internal static NotebookDocument Load(string text, string runtimeVersion)
    {
        // A fixed seed: an unmarked file gets the same cell ids every time it's read.
        var (upgraded, _) = NotebookUpgrader.Upgrade(NotebookDocument.Parse(text), new UpgradeOptions { RuntimeVersion = runtimeVersion, Random = new Random(0) });
        return upgraded;
    }

    // ---- the host's thread ----------------------------------------------------------------------

    void Post(Func<Task> work) => inbox.Writer.TryWrite(work);

    void Post(Action work) => Post(() =>
    {
        work();
        return Task.CompletedTask;
    });

    Task InvokeAsync(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try
            {
                await work();
                done.TrySetResult();
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        });
        return done.Task;
    }

    /// <summary>Completes when everything queued so far has been handled.</summary>
    public Task IdleAsync() => InvokeAsync(() => Task.CompletedTask);

    async Task RunLoopAsync()
    {
        await foreach (var work in inbox.Reader.ReadAllAsync())
        {
            try
            {
                await work();
            }
            catch (Exception e)
            {
                options.Log.WriteLine($"reverb: internal error: {e}");
                Broadcast(new ServerMessage { Type = BrowserMessages.Error, Message = $"Something went wrong in the server: {e.Message}" });
            }
        }
    }

    // ---- browser tabs ---------------------------------------------------------------------------

    public ClientSession Connect(Action<ServerMessage> send)
    {
        var client = new ClientSession(send);
        Post(() =>
        {
            clients.Add(client);
            client.Send(Snapshot(client));
        });
        return client;
    }

    public void Disconnect(ClientSession client) => Post(() => { clients.Remove(client); });

    public void Receive(ClientSession client, ClientMessage message) => Post(() => HandleAsync(client, message));

    void Broadcast(ServerMessage message)
    {
        foreach (var c in clients) c.Send(message);
    }

    void BroadcastSnapshot()
    {
        foreach (var c in clients) c.Send(Snapshot(c));
    }

    async Task HandleAsync(ClientSession client, ClientMessage m)
    {
        client.LastSeq = Math.Max(client.LastSeq, m.Seq);
        if (options.AppMode && m.Type != BrowserMessages.WidgetSetValue)
        {
            client.Send(Error("This notebook is running as an app, so it can't be changed here. Open it with 'reverb edit' to edit it."));
            return;
        }
        switch (m.Type)
        {
            case BrowserMessages.CellUpdate:
                if (m.CellId is null || m.Source is null || doc.FindCell(m.CellId) is not { } cell) return;
                var source = NotebookDocument.NormalizeSource(m.Source);
                if (cell.Source == source)
                {
                    // Nothing changed (typed and undone, say), but the tab still waits to hear it's saved.
                    saveTimer.Change(options.AutosaveDelay, Timeout.InfiniteTimeSpan);
                    return;
                }
                doc = doc.WithCellSource(m.CellId, source);
                Changed(saveNow: false);
                break;

            case BrowserMessages.CellRun:
                if (m.CellId is null || doc.FindCell(m.CellId) is not { } run) return;
                if (run.Kind == CellKind.Markdown) return;
                var changed = Submit(run);
                if (run.Kind != CellKind.Code)
                {
                    if (changed) SendUpdate();
                }
                else if (changed || kernelLazy)
                {
                    SendUpdate(runCell: run.Key);
                }
                else
                {
                    SendWork(new KernelRequest { Type = RequestTypes.Run, Cell = run.Key });
                }
                break;

            case BrowserMessages.RunAll:
                foreach (var c in doc.Cells) Submit(c);
                SendUpdate(runAll: true);
                break;

            case BrowserMessages.SetLazy:
                doc = doc.WithHeaderFlag("lazy", m.Lazy ?? false);
                SyncLazy();
                Changed(saveNow: true);
                BroadcastSnapshot();
                break;

            case BrowserMessages.Save:
                SaveNow();
                break;

            case BrowserMessages.CellCreate:
                CreateCell(m);
                break;

            case BrowserMessages.CellDelete:
                var di = m.CellId is null ? -1 : doc.IndexOf(m.CellId);
                if (di < 0) return;
                if (doc.Cells[di].Kind == CellKind.Setup)
                {
                    client.Send(Error("The setup cell can't be deleted. It holds the notebook's using directives."));
                    return;
                }
                doc = doc.RemoveCell(di);
                kernelSources.Remove(m.CellId!);
                runtime.Remove(m.CellId!);
                Structural();
                break;

            case BrowserMessages.CellMove:
                MoveCell(client, m);
                break;

            case BrowserMessages.CellSetAttrs:
                var ai = m.CellId is null || m.Attrs is null ? -1 : doc.IndexOf(m.CellId);
                if (ai < 0) return;
                var target = doc.Cells[ai];
                if (m.Attrs!.HideCode is { } hide) target = target.WithFlag("hide_code", hide);
                if (m.Attrs.Disabled is { } disabled) target = target.WithFlag("disabled", disabled);
                if (m.Attrs.Name is { } name) target = name.Length == 0 ? target.WithoutAttribute("name") : target.WithAttribute("name", name);
                doc = doc.ReplaceCell(ai, target);
                Structural();
                break;

            case BrowserMessages.WidgetSetValue:
                if (m.WidgetId is null) return;
                SetWidget(m.WidgetId, m.Value, everyChangeCounts: m.Kind == "button");
                break;

            case BrowserMessages.KernelInterrupt:
                kernel?.Interrupt();
                break;

            case BrowserMessages.KernelRestart:
                await RestartKernelAsync("Restarted.");
                break;

            case BrowserMessages.LangCompletions or BrowserMessages.LangHover or BrowserMessages.LangSignatureHelp:
                Language(client, m);
                break;

            case BrowserMessages.LangCodeFix:
                CodeFix(client, m);
                break;

            case BrowserMessages.PackagesSet:
                if (m.Packages is null) return;
                NotebookDocument withPackages;
                try
                {
                    withPackages = doc.WithPackages(m.Packages.Select(p => new NotebookPackage(p.Name.Trim(), (p.Version ?? "").Trim())));
                }
                catch (ArgumentException e)
                {
                    client.Send(Error(e.Message));
                    return;
                }
                if (ReferenceEquals(withPackages, doc)) return;
                doc = withPackages;
                Changed(saveNow: true);
                BroadcastSnapshot();
                await RestartIfPackagesChangedAsync();
                break;

            case BrowserMessages.NotebookSync or BrowserMessages.NotebookReload when !options.ClientOwnsFile:
                client.Send(Error($"'{m.Type}' is only for an editor that saves the notebook file itself, such as VS Code. This notebook is saved by Reverb, so send cell changes instead."));
                break;

            case BrowserMessages.NotebookSync:
                if (m.Cells is null) return;
                SyncCells(client, m.Cells);
                break;

            case BrowserMessages.NotebookReload:
                // Compared after loading, so a file that only differs by what loading upgrades isn't a change.
                if (m.Text is null || Load(m.Text, options.RuntimeVersion).Serialize() == doc.Serialize()) return;
                await ApplyFileTextAsync(m.Text);
                break;

            default:
                client.Send(Error($"The page sent a message the server doesn't understand ('{m.Type}'). Reload the page; if it keeps happening, the page and the server are from different versions of Reverb."));
                break;
        }
    }

    static ServerMessage Error(string message) => new() { Type = BrowserMessages.Error, Message = message };

    /// <summary>
    /// Completions, hover or signature help, answered to the asking tab only. The request carries
    /// the cell as the page has it right now (edits may not have reached the server yet).
    /// </summary>
    void Language(ClientSession client, ClientMessage m)
    {
        var resultType = m.Type switch
        {
            BrowserMessages.LangCompletions => BrowserMessages.LangCompletionsResult,
            BrowserMessages.LangHover => BrowserMessages.LangHoverResult,
            _ => BrowserMessages.LangSignatureHelpResult,
        };
        var empty = new ServerMessage { Type = resultType, RequestId = m.RequestId };
        var index = m.CellId is null ? -1 : doc.IndexOf(m.CellId);
        if (index < 0 || m.Offset is not { } offset || kernel is null || kernelState == "dead")
        {
            client.Send(empty);
            return;
        }

        // The kernel sees the cell as it will be saved: without blank lines at the start. Shift
        // offsets to match, and back again in the answer.
        var raw = (m.Source ?? doc.Cells[index].Source).ReplaceLineEndings("\n");
        var lead = LeadingBlankLines(raw);
        var text = doc.ReplaceCell(index, doc.Cells[index] with { Source = NotebookDocument.NormalizeSource(raw) }).Serialize();
        var request = new KernelRequest
        {
            Type = m.Type switch
            {
                BrowserMessages.LangCompletions => RequestTypes.Complete,
                BrowserMessages.LangHover => RequestTypes.Hover,
                _ => RequestTypes.SignatureHelp,
            },
            Text = text,
            Cell = m.CellId,
            Offset = Math.Max(0, offset - lead),
        };
        _ = kernel.SendAsync(request).ContinueWith(t => Post(() =>
        {
            if (!t.IsCompletedSuccessfully || t.Result.Ok != true)
            {
                client.Send(empty);
                return;
            }
            var r = t.Result;
            client.Send(new ServerMessage
            {
                Type = resultType,
                RequestId = m.RequestId,
                From = r.From + lead,
                To = r.To + lead,
                Text = r.Text,
                Completions = r.Completions,
                Signatures = r.Signatures,
                ActiveSignature = r.ActiveSignature,
                ActiveParameter = r.ActiveParameter,
            });
        }), TaskScheduler.Default);
    }

    static int LeadingBlankLines(string source)
    {
        var at = 0;
        while (at < source.Length)
        {
            var end = source.IndexOf('\n', at);
            if (end < 0 || source[at..end].Trim().Length > 0) break;
            at = end + 1;
        }
        return at;
    }

    /// <summary>
    /// Applies Reverb's fix for a diagnostic. The kernel works out the new notebook; it's used only if
    /// nothing changed meanwhile. The cells the fix touched are run, as if the user had edited and run them.
    /// </summary>
    void CodeFix(ClientSession client, ClientMessage m)
    {
        if (m.CellId is null || m.DiagnosticId is null || kernel is null || kernelState == "dead")
        {
            client.Send(Error("The kernel isn't running, so Reverb can't work out the fix. Restart it and try again."));
            return;
        }
        var asked = version;
        var request = new KernelRequest { Type = RequestTypes.CodeFix, Text = doc.Serialize(), Cell = m.CellId, DiagnosticId = m.DiagnosticId, Line = m.Line, Column = m.Column };
        _ = kernel.SendAsync(request).ContinueWith(t => Post(() =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                client.Send(Error("The kernel stopped before it could work out the fix. Try again when it's back."));
                return;
            }
            if (t.Result.Ok != true || t.Result.Text is not { } fixedText)
            {
                client.Send(Error(t.Result.Message ?? "Reverb couldn't fix that."));
                return;
            }
            if (version != asked)
            {
                client.Send(Error("The notebook changed while Reverb was working out the fix. Try the fix again."));
                return;
            }
            var before = doc;
            doc = NotebookDocument.Parse(fixedText);
            foreach (var cell in doc.Cells)
                if (cell.Key is { } key && before.FindCell(key)?.Source != cell.Source) Submit(cell);
            Changed(saveNow: true);
            SendUpdate();
            BroadcastSnapshot();
        }), TaskScheduler.Default);
    }

    void CreateCell(ClientMessage m)
    {
        var kind = m.Kind == "markdown" ? CellKind.Markdown : CellKind.Code;
        var taken = doc.Cells.Select(c => c.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var id = m.CellId is { } wanted && CellIds.IsValid(wanted) && !CellIds.IsReserved(wanted) && !taken.Contains(wanted)
            ? wanted
            : CellIds.New(Random.Shared, taken);
        var index = InsertionIndex(doc, m.AfterCellId);
        var cell = kind == CellKind.Markdown ? NotebookCell.Markdown(id, m.Source ?? "") : NotebookCell.Code(id, m.Source ?? "");
        doc = doc.InsertCell(index, cell);
        Submit(doc.Cells[index]);
        Structural();
    }

    void MoveCell(ClientSession client, ClientMessage m)
    {
        var from = m.CellId is null ? -1 : doc.IndexOf(m.CellId);
        if (from < 0) return;
        var cell = doc.Cells[from];
        if (cell.Kind is CellKind.Setup or CellKind.Types)
        {
            client.Send(Error("The setup cell stays first and the types cell stays last."));
            return;
        }
        if (m.AfterCellId == m.CellId) return;
        var without = doc.RemoveCell(from);
        doc = without.InsertCell(InsertionIndex(without, m.AfterCellId), cell);
        Structural();
    }

    /// <summary>
    /// Makes the document match the whole cell list a client sent. Cells are matched by key; each
    /// difference is applied the way the matching browser message would apply it.
    /// </summary>
    void SyncCells(ClientSession client, List<PartCell> incoming)
    {
        // JSON can leave out fields the records say are always there.
        var sent = incoming.Select(c => c.Source is null || c.Attributes is null ? c with { Source = c.Source ?? "", Attributes = c.Attributes ?? [] } : c).ToList();
        // The seed format.serialize uses: the editor may save the list before this correction
        // reaches it, and the cell should get the same id both times.
        var (cells, corrected) = CellListNormalizer.Normalize(sent, new Random(0));
        if (corrected) client.Send(new ServerMessage { Type = BrowserMessages.NotebookCells, PartCells = [.. cells] });

        var before = NotebookParts.Split(doc);
        var had = new Dictionary<string, PartCell>(StringComparer.Ordinal);
        foreach (var c in before.Cells)
            if (KeyOf(c) is { } key) had.TryAdd(key, c);

        // The host's raw text for a cell it already has is what was read from the file; trust that over the client's copy.
        var merged = cells.Select(c => c with
        {
            Source = NotebookDocument.NormalizeSource(c.Source),
            Raw = had.TryGetValue(KeyOf(c)!, out var old) ? old.Raw : Complete(c.Raw) ? c.Raw : null,
        }).ToList();
        // Normalising made every key distinct.
        var now = merged.ToDictionary(c => KeyOf(c)!, StringComparer.Ordinal);
        var kept = now.Keys.Where(had.ContainsKey).ToList();
        // A cell that changed kind is a different cell to the kernel.
        var renewed = kept.Where(k => had[k].Kind != now[k].Kind).ToHashSet(StringComparer.Ordinal);
        var removed = had.Keys.Where(k => !now.ContainsKey(k) || renewed.Contains(k)).ToList();
        var added = now.Keys.Where(k => !had.ContainsKey(k) || renewed.Contains(k)).ToList();
        var structural = !merged.Select(KeyOf).SequenceEqual(before.Cells.Select(KeyOf))
            || renewed.Count > 0
            || kept.Any(k => !had[k].Attributes.SequenceEqual(now[k].Attributes));
        var edited = kept.Any(k => had[k].Source != now[k].Source);
        if (!structural && !edited) return;

        NotebookDocument next;
        try
        {
            next = doc with { Cells = NotebookParts.Join(before with { Cells = merged }).Cells };
            // The kernel runs the written text, and unchanged raw text is written as it is. So the
            // client's raw text for a new cell is only kept if it writes back to the same cells.
            if (merged.Any(c => c.Raw is not null && !had.ContainsKey(KeyOf(c)!)) && !WritesBack(next))
                next = doc with { Cells = NotebookParts.Join(before with { Cells = [.. merged.Select(c => had.ContainsKey(KeyOf(c)!) ? c : c with { Raw = null })] }).Cells };
        }
        catch (ArgumentException e)
        {
            client.Send(Error($"The notebook's cells couldn't be updated: {e.Message} Reopen the notebook to get the cells back in step."));
            return;
        }
        doc = next;
        foreach (var key in removed)
        {
            kernelSources.Remove(key);
            runtime.Remove(key);
        }
        foreach (var key in added) Submit(doc.FindCell(key)!);
        if (structural) Structural();
        else Changed(saveNow: false);
    }

    static bool WritesBack(NotebookDocument d)
    {
        var again = NotebookDocument.Parse(d.Serialize());
        return again.Cells.Length == d.Cells.Length && again.Cells.Zip(d.Cells).All(p =>
            p.First.Kind == p.Second.Kind && p.First.Key == p.Second.Key && p.First.Source == p.Second.Source
            && p.First.Attributes.SequenceEqual(p.Second.Attributes));
    }

    /// <summary>Raw text from JSON can have holes the writer can't fill.</summary>
    internal static bool Complete(RawCellData? r) =>
        r is { MarkerLine: not null, LeadingPad: not null, Content: not null, TrailingPad: not null, Source: not null, Attributes: not null }
        && r.Kind is "setup" or "code" or "markdown" or "types"
        && r.Attributes.All(a => a is { Key: not null });

    static string? KeyOf(PartCell cell) => cell.Kind switch
    {
        "setup" => "setup",
        "types" => "types",
        _ => cell.Id,
    };

    /// <summary>Where a new or moved cell goes: after the given cell (or first), but never before setup or after types.</summary>
    static int InsertionIndex(NotebookDocument doc, string? afterCellId)
    {
        var first = doc.Cells.Length > 0 && doc.Cells[0].Kind == CellKind.Setup ? 1 : 0;
        var last = doc.Cells.Length > 0 && doc.Cells[^1].Kind == CellKind.Types ? doc.Cells.Length - 1 : doc.Cells.Length;
        var index = afterCellId is null ? first : doc.IndexOf(afterCellId) + 1;
        if (index <= 0) index = first;
        return Math.Clamp(index, first, last);
    }

    /// <summary>After a structural change: tell the kernel, save, and resend the notebook.</summary>
    void Structural()
    {
        Changed(saveNow: true);
        SendUpdate();
        BroadcastSnapshot();
    }

    /// <summary>The document changed: bump the version, analyse it and schedule a save.</summary>
    void Changed(bool saveNow)
    {
        version++;
        Analyze();
        if (saveNow) SaveNow();
        else saveTimer.Change(options.AutosaveDelay, Timeout.InfiniteTimeSpan);
    }

    // ---- what the kernel has ----------------------------------------------------------------------

    /// <summary>Hands the cell's current code to the kernel. Returns true if it differs from what the kernel had.</summary>
    bool Submit(NotebookCell cell)
    {
        if (cell.Kind == CellKind.Markdown || cell.Key is not { } key) return false;
        if (kernelSources.TryGetValue(key, out var had) && had == cell.Source) return false;
        kernelSources[key] = cell.Source;
        return true;
    }

    void SubmitAll()
    {
        kernelSources.Clear();
        foreach (var c in doc.Cells) Submit(c);
    }

    bool IsEdited(NotebookCell cell) =>
        cell.Kind != CellKind.Markdown && cell.Key is { } key && kernelSources.TryGetValue(key, out var had) && had != cell.Source;

    /// <summary>The notebook as the kernel should see it: current structure, each cell's last submitted code.</summary>
    NotebookDocument KernelDocument() => doc with
    {
        Cells = [.. doc.Cells.Select(c => c.Key is { } key && c.Kind != CellKind.Markdown && kernelSources.TryGetValue(key, out var src) ? c with { Source = src } : c)],
    };

    void SendUpdate(string? runCell = null, bool runAll = false) =>
        SendWork(new KernelRequest { Type = RequestTypes.Update, Text = KernelDocument().Serialize(), Cell = runCell, Run = runAll ? true : null });

    void Analyze()
    {
        if (kernel is null || kernelState == "dead" || options.AppMode) return;
        var request = new KernelRequest { Type = RequestTypes.Analyze, Text = doc.Serialize(), Version = version };
        _ = Swallow(kernel.SendAsync(request));
    }

    bool LazyInFile => !options.AppMode && (doc.Preamble.Header?.Lazy ?? false);

    void SyncLazy()
    {
        var lazy = LazyInFile;
        if (lazy == kernelLazy) return;
        kernelLazy = lazy;
        SendWork(new KernelRequest { Type = RequestTypes.SetLazy, Lazy = lazy });
    }

    /// <summary>
    /// Sends a widget change. While one change to a widget is running, later ones wait and only
    /// the newest is sent, so dragging a slider doesn't queue up a run for every pixel. Button
    /// clicks all count, so they're sent as they come.
    /// </summary>
    void SetWidget(string id, JsonElement? value, bool everyChangeCounts)
    {
        if (!everyChangeCounts && widgetsInFlight.Contains(id))
        {
            widgetsWaiting[id] = value;
            return;
        }
        widgetsInFlight.Add(id);
        SendWork(new KernelRequest { Type = RequestTypes.SetWidget, WidgetId = id, Value = value }, () =>
        {
            widgetsInFlight.Remove(id);
            if (widgetsWaiting.Remove(id, out var next)) SetWidget(id, next, everyChangeCounts: false);
        });
    }

    /// <summary>Sends a request that runs code. The kernel shows as busy until it's done.</summary>
    void SendWork(KernelRequest request, Action? done = null)
    {
        if (kernel is null)
        {
            if (kernelState is "building" or "starting") waitingWork.Add((request, done));
            return;
        }
        if (kernelState == "dead") return;
        var generation = kernelGeneration;
        outstanding++;
        SetKernelState("busy", null);
        var task = kernel.SendAsync(request);
        _ = task.ContinueWith(_ => Post(() =>
        {
            if (generation != kernelGeneration) return;
            outstanding = Math.Max(0, outstanding - 1);
            if (outstanding == 0 && kernelState == "busy") SetKernelState("ready", null);
            done?.Invoke();
        }), TaskScheduler.Default);
    }

    static async Task Swallow(Task task)
    {
        try
        {
            await task;
        }
        catch (KernelExitedException)
        {
        }
    }

    void SetKernelState(string state, string? message)
    {
        if (state == kernelState && message == kernelMessage) return;
        kernelState = state;
        kernelMessage = message;
        Broadcast(new ServerMessage { Type = BrowserMessages.KernelStatus, State = state, Message = message });
    }

    // ---- the kernel process -----------------------------------------------------------------------

    /// <summary>
    /// Starts a kernel, building one with the notebook's packages first if it has any, and opens the
    /// notebook in it. The build and start happen off the host's thread so typing and saving carry
    /// on; work sent meanwhile waits. <paramref name="run"/> runs the notebook too (unless it's lazy).
    /// The task completes once the kernel is up or has failed to start.
    /// </summary>
    Task StartKernel(bool run, string? readyMessage)
    {
        var generation = ++kernelGeneration;
        outstanding = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var packages = KernelBuilder.PackagesOf(doc.Preamble.Directives);
        kernelPackages = packages;
        if (packages.Count > 0) SetKernelState("building", $"Getting {string.Join(", ", packages)}…");
        else SetKernelState("starting", readyMessage);

        var launch = new KernelLaunch { EntryAssembly = options.KernelEntryAssembly, NotebookPath = Path, Directives = doc.Preamble.Directives };
        var request = new KernelBuildRequest
        {
            Packages = packages,
            ToolDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(options.KernelEntryAssembly))!,
            NotebookDirectory = System.IO.Path.GetDirectoryName(Path),
            CacheRoot = options.KernelCacheRoot ?? KernelBuilder.DefaultCacheRoot,
        };
        _ = Task.Run(async () =>
        {
            KernelClient client;
            try
            {
                var build = await KernelBuilder.EnsureAsync(request, step => Post(() =>
                {
                    if (generation == kernelGeneration) SetKernelState("building", step + "…");
                }), lifetime.Token);
                if (build is not null) Post(() => { if (generation == kernelGeneration) SetKernelState("starting", readyMessage); });
                client = await KernelClient.StartAsync(launch.WithBuild(build), lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                started.TrySetResult();
                return;
            }
            catch (Exception e) when (e is KernelBuildException or KernelExitedException or IOException or System.ComponentModel.Win32Exception)
            {
                var message = e is KernelBuildException ? e.Message : $"The kernel didn't start: {e.Message} Check that the .NET 10 SDK is installed ('dotnet --version'), then press Restart.";
                if (!inbox.Writer.TryWrite(() => Failed(message))) started.TrySetResult();
                return;
            }
            // If the host has shut down there's nobody to hand the kernel to.
            if (!inbox.Writer.TryWrite(() => AttachAsync(client))) await client.DisposeAsync();
        });
        return started.Task;

        Task Failed(string message)
        {
            if (generation == kernelGeneration)
            {
                waitingWork.Clear();
                SetKernelState("dead", message);
            }
            started.TrySetResult();
            return Task.CompletedTask;
        }

        async Task AttachAsync(KernelClient client)
        {
            started.TrySetResult();
            if (generation != kernelGeneration || disposed)
            {
                // Restarted, or the packages changed again, while this one was starting.
                await client.DisposeAsync();
                return;
            }
            client.Event += e => Post(() => OnKernelEvent(generation, e));
            client.StrayOutput += line => options.Log.WriteLine(line);
            client.Exited += code => Post(() => OnKernelExited(generation, code));
            kernel = client;

            SetKernelState("ready", readyMessage);
            kernelLazy = LazyInFile;
            SubmitAll();
            SendWork(new KernelRequest { Type = RequestTypes.Open, Text = KernelDocument().Serialize(), Lazy = kernelLazy, Run = run });
            Analyze();

            // Runs asked for while the kernel was on its way. The document they carried may be
            // out of date by now, so send the current one.
            var waiting = waitingWork.ToList();
            waitingWork.Clear();
            foreach (var (r, done) in waiting)
            {
                if (r.Type == RequestTypes.Update) r.Text = KernelDocument().Serialize();
                SendWork(r, done);
            }
        }
    }

    async Task RestartKernelAsync(string message, bool? run = null)
    {
        var old = kernel;
        kernel = null;
        kernelGeneration++;
        widgetsInFlight.Clear();
        widgetsWaiting.Clear();
        waitingWork.Clear();
        if (old is not null)
        {
            old.Kill();
            await old.DisposeAsync();
        }
        MarkAllStale();
        // In the editor nothing runs until asked: if a cell stopped the kernel, running it again
        // would just do it again. An app's viewers can't run cells, so an app runs everything.
        _ = StartKernel(run ?? options.AppMode, message);
        BroadcastSnapshot();
    }

    /// <summary>A kernel only has the packages it was built with, so a new set needs a new kernel.</summary>
    async Task RestartIfPackagesChangedAsync()
    {
        if (KernelBuilder.PackagesOf(doc.Preamble.Directives).SequenceEqual(kernelPackages)) return;
        // Everything is stale in a new kernel. The browser runs it again (unless lazy), and an editor
        // that doesn't run on open leaves the cells stale, as it does when it opens a notebook.
        await RestartKernelAsync("The packages changed, so the kernel was rebuilt.", run: options.RunOnOpen);
    }

    void OnKernelExited(int generation, int exitCode)
    {
        if (generation != kernelGeneration || disposed) return;
        var stderr = kernel?.StandardError.Trim() ?? "";
        options.Log.WriteLine($"reverb: the kernel stopped (exit code {exitCode}).{(stderr.Length > 0 ? "\n" + stderr : "")}");
        crashes.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(1));
        crashes.Add(DateTime.UtcNow);
        if (crashes.Count >= 3)
        {
            // Something keeps killing it (a cell calling Environment.Exit, say). Stop trying.
            MarkAllStale();
            SetKernelState("dead", $"The kernel stopped (exit code {exitCode}) three times in a minute, so Reverb stopped restarting it.{(options.AppMode ? "" : " Fix the cell that stops it, then press Restart.")}");
            return;
        }
        SetKernelState("dead", $"The kernel stopped (exit code {exitCode}), usually because a cell called Environment.Exit or overflowed the stack. Starting a new one.");
        Post(() => RestartKernelAsync(options.AppMode
            ? $"The kernel stopped (exit code {exitCode}) and was restarted."
            : $"The kernel stopped (exit code {exitCode}) and was restarted. Run cells to get their values back."));
    }

    void MarkAllStale()
    {
        foreach (var cell in doc.Cells.Where(c => c.Kind == CellKind.Code && c.Key is not null))
        {
            var r = Runtime(cell.Key!);
            if (r.Status is "stale" or "disabled") continue;
            r.Status = "stale";
            r.ElapsedMs = null;
            r.Message = null;
            Broadcast(new ServerMessage { Type = BrowserMessages.CellStatus, CellId = cell.Key, Status = "stale" });
        }
    }

    CellRuntime Runtime(string key)
    {
        if (!runtime.TryGetValue(key, out var r)) runtime[key] = r = new CellRuntime();
        return r;
    }

    void OnKernelEvent(int generation, KernelEvent e)
    {
        if (generation != kernelGeneration) return;
        switch (e.Type)
        {
            case EventTypes.Status when e.Cell is not null:
            {
                var r = Runtime(e.Cell);
                if (e.Status == "running" && r.Outputs.Count > 0)
                {
                    // A new kernel doesn't know about outputs from the last one, so clear them here.
                    r.Outputs = [];
                    Broadcast(new ServerMessage { Type = BrowserMessages.CellOutputs, CellId = e.Cell, Outputs = [] });
                }
                if (e.Status == "running") r.Runs++;
                r.Status = e.Status ?? "idle";
                r.ElapsedMs = e.ElapsedMs;
                r.Message = e.Message;
                Broadcast(new ServerMessage { Type = BrowserMessages.CellStatus, CellId = e.Cell, Status = r.Status, ElapsedMs = e.ElapsedMs, Message = e.Message, Runs = r.Runs });
                break;
            }
            case EventTypes.Cleared when e.Cell is not null:
                Runtime(e.Cell).Outputs = [];
                Broadcast(new ServerMessage { Type = BrowserMessages.CellOutputs, CellId = e.Cell, Outputs = [] });
                break;
            case EventTypes.Stream when e.Cell is not null:
            {
                var outputs = Runtime(e.Cell).Outputs;
                if (outputs.Count > 0 && outputs[^1].Stream == e.Stream) outputs[^1].Text += e.Text;
                else outputs.Add(new OutputData { Stream = e.Stream, Text = e.Text });
                Broadcast(new ServerMessage { Type = BrowserMessages.CellStream, CellId = e.Cell, Stream = e.Stream, Text = e.Text });
                break;
            }
            case EventTypes.Display when e.Cell is not null && e.Output is not null:
            {
                var outputs = Runtime(e.Cell).Outputs;
                outputs.Add(e.Output);
                Broadcast(new ServerMessage { Type = BrowserMessages.CellOutputs, CellId = e.Cell, Outputs = outputs });
                break;
            }
            case EventTypes.Outputs when e.Cell is not null:
                Runtime(e.Cell).Outputs = e.Outputs ?? [];
                Broadcast(new ServerMessage { Type = BrowserMessages.CellOutputs, CellId = e.Cell, Outputs = Runtime(e.Cell).Outputs });
                break;
            case EventTypes.Analysis:
                if ((e.Version ?? -1) < analyzedVersion) return;
                analyzedVersion = e.Version ?? analyzedVersion;
                diagnostics = e.Diagnostics ?? [];
                graph = e.Graph ?? [];
                BroadcastAnalysis();
                break;
            case EventTypes.Error:
                options.Log.WriteLine($"reverb: kernel error: {e.Message}");
                break;
        }
    }

    void BroadcastAnalysis()
    {
        foreach (var cell in doc.Cells)
        {
            if (cell.Key is not { } key) continue;
            Broadcast(new ServerMessage { Type = BrowserMessages.CellDiagnostics, CellId = key, NotebookVersion = analyzedVersion, Diagnostics = DiagnosticsOf(key) });
        }
        Broadcast(new ServerMessage { Type = BrowserMessages.CellDiagnostics, NotebookVersion = analyzedVersion, Diagnostics = DiagnosticsOf(null) });
        Broadcast(new ServerMessage { Type = BrowserMessages.GraphUpdate, NotebookVersion = analyzedVersion, Edges = graph });
    }

    List<DiagnosticData> DiagnosticsOf(string? key) => [.. diagnostics.Where(d => d.Cell == key)];

    // ---- snapshots --------------------------------------------------------------------------------

    ServerMessage Snapshot(ClientSession client) => new()
    {
        Type = BrowserMessages.Snapshot,
        NotebookVersion = version,
        Ack = client.LastSeq,
        Path = Path,
        Name = System.IO.Path.GetFileName(Path),
        Mode = options.AppMode ? "app" : "edit",
        Lazy = LazyInFile,
        Kernel = new KernelView { State = kernelState, Message = kernelMessage },
        // Reverb.Runtime is Reverb's to manage, so it isn't listed.
        Packages = [.. doc.Preamble.Directives
            .Where(d => d.Kind == "package" && !d.Name.Equals(NotebookUpgrader.RuntimePackage, StringComparison.OrdinalIgnoreCase))
            .Select(d => new PackageView { Name = d.Name, Version = d.Value })],
        NotebookDiagnostics = options.AppMode ? [] : DiagnosticsOf(null),
        Preamble = options.AppMode ? [] : [.. doc.Preamble.Lines],
        Edges = options.AppMode ? [] : graph,
        Cells = [.. doc.Cells.Where(c => c.Key is not null && (!options.AppMode || c.Kind is CellKind.Code or CellKind.Markdown)).Select(CellViewOf)],
    };

    CellView CellViewOf(NotebookCell cell)
    {
        var key = cell.Key!;
        var r = cell.Kind == CellKind.Code ? Runtime(key) : null;
        return new CellView
        {
            Id = key,
            Kind = cell.Kind.ToString().ToLowerInvariant(),
            // An app's viewers see outputs, not code.
            Source = options.AppMode && cell.Kind != CellKind.Markdown ? "" : cell.Source,
            Attrs = new CellAttrs { HideCode = cell.HideCode, Disabled = cell.Disabled, Name = cell.Name },
            Status = r?.Status ?? "idle",
            ElapsedMs = r?.ElapsedMs,
            Message = r?.Message,
            Outputs = r?.Outputs ?? [],
            Diagnostics = options.AppMode ? [] : DiagnosticsOf(key),
            Runs = r?.Runs ?? 0,
            Edited = IsEdited(cell),
        };
    }

    // ---- the file on disk ---------------------------------------------------------------------------

    void SaveNow()
    {
        saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (options.AppMode || options.ClientOwnsFile) return;
        var text = doc.Serialize();
        try
        {
            if (text != diskText)
            {
                NotebookFile.WriteTextAtomic(Path, text);
                diskText = text;
                watcher?.Saw();
            }
            // Each tab learns which of its edits are now on disk.
            foreach (var c in clients) c.Send(new ServerMessage { Type = BrowserMessages.FileSaved, NotebookVersion = version, Ack = c.LastSeq });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Often another program has the file open for a moment (common on Windows). Try again shortly.
            saveFailures++;
            if (saveFailures == 3)
                Broadcast(Error($"Couldn't save {System.IO.Path.GetFileName(Path)}: {e.Message} Your changes are kept in the editor and Reverb keeps trying."));
            saveTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            return;
        }
        saveFailures = 0;
    }

    async Task CheckFileAsync()
    {
        string text;
        try
        {
            text = await ReadWithRetriesAsync();
        }
        catch (FileNotFoundException)
        {
            return; // deleted or mid-rename; the next save writes it again
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Broadcast(Error($"{System.IO.Path.GetFileName(Path)} changed on disk but couldn't be read: {e.Message} The editor keeps what it has; fix the file, or keep editing here and the next save replaces it."));
            return;
        }
        if (text == diskText) return;
        await ApplyFileTextAsync(text);
    }

    /// <summary>The file has new text, read from disk or sent by the client that owns the file.</summary>
    async Task ApplyFileTextAsync(string text)
    {
        diskText = text;
        doc = Load(text, options.RuntimeVersion);
        saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        foreach (var gone in runtime.Keys.Where(k => doc.FindCell(k) is null).ToList()) runtime.Remove(gone);
        version++;
        if (!KernelBuilder.PackagesOf(doc.Preamble.Directives).SequenceEqual(kernelPackages))
        {
            await RestartIfPackagesChangedAsync();
        }
        else
        {
            SyncLazy();
            SubmitAll();
            SendUpdate();
            Analyze();
            BroadcastSnapshot();
        }
        Broadcast(new ServerMessage { Type = BrowserMessages.FileExternalChange, NotebookVersion = version, Message = $"{System.IO.Path.GetFileName(Path)} changed on disk. The notebook was reloaded." });
    }

    async Task<string> ReadWithRetriesAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return NotebookFile.ReadText(Path);
            }
            catch (IOException) when (attempt < 5 && File.Exists(Path))
            {
                await Task.Delay(50); // another program is still writing it
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await InvokeAsync(async () =>
        {
            disposed = true;
            watcher?.Dispose();
            SaveNow();
            kernelGeneration++;
            if (kernel is not null) await kernel.DisposeAsync();
            kernel = null;
        });
        inbox.Writer.TryComplete();
        await loop;
        await saveTimer.DisposeAsync();
    }
}
