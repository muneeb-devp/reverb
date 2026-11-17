using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Reverb.Analysis;
using Reverb.Format;

namespace Reverb.Kernel;

/// <summary>
/// The kernel process's main loop: reads requests from stdin, runs them one at a time, and writes
/// events to stdout. Interrupts are handled as soon as they arrive, even while a cell runs.
/// </summary>
public static class KernelServer
{
    public static async Task<int> RunAsync(TextReader input, Stream output)
    {
        // Keep the real stdout for protocol messages; Console.Out is routed to cells from here on.
        KernelTrace.Mark("server starting");
        var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var events = new ProtocolEvents(writer);
        var session = new KernelSession(events);
        KernelTrace.Mark("session created");
        var queue = Channel.CreateUnbounded<KernelRequest>(new UnboundedChannelOptions { SingleReader = true });

        var abandoned = false;
        var processor = Task.Run(async () =>
        {
            await foreach (var request in queue.Reader.ReadAllAsync())
            {
                if (Volatile.Read(ref abandoned)) break;
                var ok = true;
                string? message = null;
                try
                {
                    ok = await HandleAsync(session, request);
                }
                catch (Exception e)
                {
                    ok = false;
                    message = $"{e.GetType().Name}: {e.Message}";
                    events.Send(new KernelEvent { Type = EventTypes.Error, Message = e.ToString() });
                }
                events.Send(new KernelEvent { Type = EventTypes.Done, RequestId = request.Id, Ok = ok, Message = message });
                if (request.Type == RequestTypes.Shutdown) break;
            }
        });

        // Analysis has its own worker so diagnostics keep coming while a cell runs.
        var analyses = Channel.CreateUnbounded<KernelRequest>(new UnboundedChannelOptions { SingleReader = true });
        var analyzer = Task.Run(() => AnalyzeLoopAsync(analyses.Reader, session.References, events));

        // Completions and the like too, and separately from analysis so a slow one doesn't hold up diagnostics.
        var language = Channel.CreateUnbounded<KernelRequest>(new UnboundedChannelOptions { SingleReader = true });
        var languageWorker = Task.Run(() => LanguageLoopAsync(language.Reader, session.References, events));

        Warmup.Start();
        KernelTrace.Mark("sending ready");
        events.Send(new KernelEvent { Type = EventTypes.Ready });
        KernelTrace.Mark("ready sent");

        var shutdownRequested = false;
        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            KernelRequest? request;
            try
            {
                request = Protocol.DecodeRequest(line);
            }
            catch (JsonException e)
            {
                events.Send(new KernelEvent { Type = EventTypes.Error, Message = $"Couldn't read request: {e.Message}" });
                continue;
            }
            if (request is null) continue;

            if (request.Type == RequestTypes.Analyze)
            {
                await analyses.Writer.WriteAsync(request);
                continue;
            }
            if (request.Type is RequestTypes.Complete or RequestTypes.Hover or RequestTypes.SignatureHelp or RequestTypes.CodeFix)
            {
                await language.Writer.WriteAsync(request);
                continue;
            }
            if (request.Type == RequestTypes.Interrupt)
            {
                session.Interrupt();
                events.Send(new KernelEvent { Type = EventTypes.Done, RequestId = request.Id, Ok = true });
                continue;
            }
            // Work requests pre-empt the running cell now, not when the queue gets to them.
            if (request.Type is RequestTypes.Open or RequestTypes.Update or RequestTypes.Run or RequestTypes.RunAll or RequestTypes.SetWidget)
            {
                Warmup.Stop();
                session.CancelForNewWork();
            }
            await queue.Writer.WriteAsync(request);
            if (request.Type == RequestTypes.Shutdown)
            {
                shutdownRequested = true;
                break;
            }
        }

        if (!shutdownRequested)
        {
            // stdin closed without a shutdown: whoever started us is gone. Stop the running cell, skip
            // what's queued, and don't wait on a cell that ignores cancellation.
            Volatile.Write(ref abandoned, true);
            session.Interrupt();
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                Environment.Exit(0);
            });
        }

        queue.Writer.TryComplete();
        analyses.Writer.TryComplete();
        language.Writer.TryComplete();
        await processor;
        await analyzer;
        await languageWorker;
        await session.DisposeAsync();
        return 0;
    }

    static async Task<bool> HandleAsync(KernelSession session, KernelRequest r)
    {
        switch (r.Type)
        {
            case RequestTypes.Open:
                session.Lazy = r.Lazy ?? false;
                session.Args = r.Args ?? [];
                await session.OpenAsync(NotebookDocument.Parse(r.Text ?? ""), r.Run ?? true);
                return true;
            case RequestTypes.Update:
                await session.UpdateAsync(NotebookDocument.Parse(r.Text ?? ""), r.Cell, r.Run ?? false);
                return true;
            case RequestTypes.Run:
                await session.RunCellAsync(r.Cell ?? "");
                return true;
            case RequestTypes.RunAll:
                await session.RunAllAsync();
                return true;
            case RequestTypes.SetLazy:
                session.Lazy = r.Lazy ?? false;
                return true;
            case RequestTypes.SetWidget:
                return await session.SetWidgetAsync(r.WidgetId ?? "", WidgetValue(r.Value));
            case RequestTypes.Shutdown:
                return true;
            default:
                throw new InvalidOperationException($"Unknown request type '{r.Type}'.");
        }
    }

    /// <summary>
    /// Answers analyze requests. When several are waiting, only the newest is analysed; the others
    /// are answered as done without an analysis, since their text is already out of date.
    /// </summary>
    static async Task AnalyzeLoopAsync(ChannelReader<KernelRequest> reader, ReferenceSet references, ProtocolEvents events)
    {
        NotebookAnalysis? last = null;
        while (await reader.WaitToReadAsync())
        {
            var batch = new List<KernelRequest>();
            while (reader.TryRead(out var r)) batch.Add(r);
            foreach (var skipped in batch.SkipLast(1))
                events.Send(new KernelEvent { Type = EventTypes.Done, RequestId = skipped.Id, Ok = true });

            var request = batch[^1];
            try
            {
                var doc = NotebookDocument.Parse(request.Text ?? "");
                last = last is null ? NotebookAnalysis.Create(doc, references) : last.Update(doc);
                events.Send(new KernelEvent
                {
                    Type = EventTypes.Analysis,
                    RequestId = request.Id,
                    Version = request.Version,
                    Diagnostics = [.. last.Diagnostics.Select(d => ToData(d, last))],
                    Graph = [.. last.Graph.Edges.Select(e => new EdgeData { From = e.From, To = e.To, Via = [.. e.Via] })],
                });
                events.Send(new KernelEvent { Type = EventTypes.Done, RequestId = request.Id, Ok = true });
            }
            catch (Exception e)
            {
                last = null;
                events.Send(new KernelEvent { Type = EventTypes.Error, Message = e.ToString() });
                events.Send(new KernelEvent { Type = EventTypes.Done, RequestId = request.Id, Ok = false, Message = $"{e.GetType().Name}: {e.Message}" });
            }
        }
    }

    /// <summary>
    /// Answers completion, hover, signature help and code fix requests, in order. A request that's
    /// been overtaken by a newer one of the same kind (the user kept typing) gets an empty answer.
    /// </summary>
    static async Task LanguageLoopAsync(ChannelReader<KernelRequest> reader, ReferenceSet references, ProtocolEvents events)
    {
        using var service = new LanguageService(references);
        NotebookAnalysis? analysis = null;
        while (await reader.WaitToReadAsync())
        {
            var batch = new List<KernelRequest>();
            while (reader.TryRead(out var r)) batch.Add(r);
            foreach (var request in batch)
            {
                var done = new KernelEvent { Type = EventTypes.Done, RequestId = request.Id, Ok = true };
                if (request.Type != RequestTypes.CodeFix && batch.Any(b => b.Type == request.Type && b.Id > request.Id))
                {
                    events.Send(done);
                    continue;
                }
                try
                {
                    var doc = NotebookDocument.Parse(request.Text ?? "");
                    var cell = request.Cell ?? "";
                    var offset = request.Offset ?? 0;
                    switch (request.Type)
                    {
                        case RequestTypes.Complete:
                            var completions = await service.CompleteAsync(doc, cell, offset);
                            (done.From, done.To, done.Completions) = (completions.From, completions.To, [.. completions.Items]);
                            break;
                        case RequestTypes.Hover:
                            if (await service.HoverAsync(doc, cell, offset) is { } hover)
                                (done.From, done.To, done.Text) = (hover.From, hover.To, hover.Text);
                            break;
                        case RequestTypes.SignatureHelp:
                            if (await service.SignatureHelpAsync(doc, cell, offset) is { } help)
                                (done.Signatures, done.ActiveSignature, done.ActiveParameter) = ([.. help.Signatures], help.ActiveSignature, help.ActiveParameter);
                            break;
                        case RequestTypes.CodeFix:
                            analysis = analysis is null ? NotebookAnalysis.Create(doc, references) : analysis.Update(doc);
                            var diagnostic = analysis.Diagnostics.FirstOrDefault(d =>
                                d.CellKey == cell && d.Id == request.DiagnosticId && d.Range.StartLine == request.Line && d.Range.StartColumn == request.Column);
                            if (diagnostic is null)
                            {
                                (done.Ok, done.Message) = (false, "That problem isn't there any more, so there's nothing to fix.");
                                break;
                            }
                            done.Text = NotebookCodeFixes.Apply(analysis, diagnostic).Serialize();
                            break;
                    }
                }
                catch (CodeFixException e)
                {
                    (done.Ok, done.Message) = (false, e.Message);
                }
                catch (Exception e)
                {
                    analysis = null;
                    events.Send(new KernelEvent { Type = EventTypes.Error, Message = e.ToString() });
                    (done.Ok, done.Message) = (false, $"{e.GetType().Name}: {e.Message}");
                }
                events.Send(done);
            }
        }
    }

    static object? WidgetValue(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.Number when value.Value.TryGetInt32(out var i) => i,
        JsonValueKind.Number => value.Value.GetDouble(),
        JsonValueKind.String => value.Value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    sealed class ProtocolEvents(StreamWriter writer) : IKernelEvents
    {
        readonly Lock gate = new();

        public void Send(KernelEvent e)
        {
            var line = Protocol.Encode(e);
            lock (gate) writer.WriteLine(line);
        }

        public void CellStatus(string cell, CellStatus status, long? elapsedMs, string? message) =>
            Send(new KernelEvent { Type = EventTypes.Status, Cell = cell, Status = StatusName(status), ElapsedMs = elapsedMs, Message = message });

        public void CellCleared(string cell) => Send(new KernelEvent { Type = EventTypes.Cleared, Cell = cell });

        public void CellStream(string cell, string stream, string text) =>
            Send(new KernelEvent { Type = EventTypes.Stream, Cell = cell, Stream = stream, Text = text });

        public void CellDisplay(string cell, Output output) =>
            Send(new KernelEvent { Type = EventTypes.Display, Cell = cell, Output = OutputData.From(output) });

        public void CellOutputs(string cell, IReadOnlyList<CellOutputItem> outputs) =>
            Send(new KernelEvent { Type = EventTypes.Outputs, Cell = cell, Outputs = [.. outputs.Select(OutputData.From)] });

        public void Diagnostics(NotebookAnalysis analysis) =>
            Send(new KernelEvent { Type = EventTypes.Diagnostics, Diagnostics = [.. analysis.Diagnostics.Select(d => ToData(d, analysis))] });
    }

    public static string StatusName(CellStatus status) => status switch
    {
        Kernel.CellStatus.Idle => "idle",
        Kernel.CellStatus.Queued => "queued",
        Kernel.CellStatus.Running => "running",
        Kernel.CellStatus.Stale => "stale",
        Kernel.CellStatus.Error => "error",
        Kernel.CellStatus.UpstreamError => "upstream-error",
        Kernel.CellStatus.Disabled => "disabled",
        _ => status.ToString().ToLowerInvariant(),
    };

    /// <summary>The diagnostic as sent to clients. With the analysis, it also says what Reverb's fix would do.</summary>
    public static DiagnosticData ToData(NotebookDiagnostic d, NotebookAnalysis? analysis = null) => new()
    {
        Fix = analysis is null ? null : NotebookCodeFixes.Title(analysis, d),
        Id = d.Id,
        Severity = d.Severity.ToString().ToLowerInvariant(),
        Message = d.Message,
        Cell = d.CellKey,
        StartLine = d.Range.StartLine,
        StartColumn = d.Range.StartColumn,
        EndLine = d.Range.EndLine,
        EndColumn = d.Range.EndColumn,
    };
}

/// <summary>
/// Pushes a small notebook through analysis, codegen, compile and run in the background so the
/// JIT and Roslyn are warm before the first real edit.
/// </summary>
static class Warmup
{
    static readonly CancellationTokenSource stop = new();

    public static void Start() => _ = Task.Run(async () =>
    {
        try
        {
            await using var session = new KernelSession();
            var text = "#:package Reverb.Runtime@0.1.0\n// reverb-notebook v1\n\n// %% setup\nusing Reverb;\n\n// %% id=a\nvar a = 1;\n\n// %% id=b\nvar b = a + 1;\n";
            var doc = NotebookDocument.Parse(text);
            if (stop.IsCancellationRequested) return;
            await session.OpenAsync(doc);
            for (var i = 2; i < 12 && !stop.IsCancellationRequested; i++)
                await session.UpdateAsync(doc.WithCellSource("a", $"var a = {i};"));
            // Roslyn's completion machinery takes a moment to put together the first time.
            if (!stop.IsCancellationRequested)
            {
                using var language = new LanguageService(session.References);
                await language.CompleteAsync(doc.WithCellSource("b", "var b = a."), "b", 10);
            }
            KernelTrace.Mark("warm-up done");
        }
        catch (Exception)
        {
            // Warm-up is best effort.
        }
    });

    /// <summary>Real work has arrived: finish the step in progress and stop.</summary>
    public static void Stop() => stop.Cancel();
}
