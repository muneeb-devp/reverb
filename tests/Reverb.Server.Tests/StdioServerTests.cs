using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Reverb.Format;
using Reverb.Testing;

namespace Reverb.Server.Tests;

/// <summary>The stdio server in-process, talking JSON lines over pipes the way an editor extension does.</summary>
[Collection("server")]
public class StdioServerTests
{
    sealed class Session : IAsyncDisposable
    {
        readonly Pipe input = new();
        readonly Pipe output = new();
        readonly StreamWriter writer;
        readonly Task<int> running;
        readonly Task reading;

        public Session()
        {
            var server = new StdioServer(path => new EditorOptions
            {
                NotebookPath = path,
                KernelEntryAssembly = TestServer.CliAssembly,
                KernelCacheRoot = TestServer.KernelCache,
            }, TextWriter.Null);
            running = server.RunAsync(new StreamReader(input.Reader.AsStream(), new UTF8Encoding(false)), output.Writer.AsStream());
            writer = new StreamWriter(input.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            reading = Task.Run(ReadAsync);
        }

        public ConcurrentQueue<ServerMessage> Messages { get; } = new();
        public ConcurrentQueue<string> Lines { get; } = new();

        async Task ReadAsync()
        {
            using var reader = new StreamReader(output.Reader.AsStream(), Encoding.UTF8);
            while (await reader.ReadLineAsync() is { } line)
            {
                Lines.Enqueue(line);
                Messages.Enqueue(JsonSerializer.Deserialize(line, BrowserJson.Default.ServerMessage)!);
            }
        }

        public Task SendAsync(ClientMessage m) => SendLineAsync(JsonSerializer.Serialize(m, BrowserJson.Default.ClientMessage));

        public Task SendLineAsync(string line) => writer.WriteLineAsync(line);

        public async Task<ServerMessage> WaitFor(Func<ServerMessage, bool> match, string what, int seconds = 30)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                if (Messages.FirstOrDefault(match) is { } m) return m;
                if (running.IsCompleted) throw new InvalidOperationException($"The server stopped while waiting for {what}: {running.Status} {running.Exception}");
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Timed out waiting for {what}. Messages so far:\n{string.Join("\n", Lines)}");
                await Task.Delay(20);
            }
        }

        public Task<ServerMessage> Answer(int requestId, string what) =>
            WaitFor(m => m.RequestId == requestId && m.Type != BrowserMessages.Snapshot, what);

        /// <summary>The text a notebook's cell has written to stdout so far.</summary>
        public string Stdout(string notebook, string cellId) => string.Concat(Messages
            .Where(m => m.Notebook == notebook && m.CellId == cellId && m.Type == BrowserMessages.CellStream && m.Stream == "stdout")
            .Select(m => m.Text)).ReplaceLineEndings("\n");

        public async ValueTask DisposeAsync()
        {
            // Closing stdin ends the server, and it shuts every notebook down.
            await writer.DisposeAsync();
            var exit = await running.WaitAsync(TimeSpan.FromSeconds(60));
            await output.Writer.CompleteAsync();
            await reading.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, exit);
        }
    }

    static string Notebook(string name, string text)
    {
        var path = Path.Combine(TestServer.TempFolder(), name);
        File.WriteAllText(path, text);
        return path;
    }

    static ClientMessage Open(string path, string text) => new() { Type = "notebook.open", Notebook = path, Text = text };

    [Fact]
    public async Task Hello_reports_the_protocol_version()
    {
        await using var s = new Session();
        await s.SendLineAsync("""{"type":"hello","requestId":1}""");

        var answer = await s.Answer(1, "hello.result");

        Assert.Equal("hello.result", answer.Type);
        Assert.Equal(StdioProtocol.Version, answer.ProtocolVersion);
        Assert.Equal(1, answer.ProtocolVersion);
        Assert.False(string.IsNullOrEmpty(answer.ReverbVersion));
    }

    [Fact]
    public async Task Format_template_returns_the_starter_notebook()
    {
        await using var s = new Session();
        await s.SendLineAsync("""{"type":"format.template","requestId":3}""");

        var answer = await s.Answer(3, "format.templated");

        Assert.Equal("format.templated", answer.Type);
        Assert.StartsWith("#!/usr/bin/env dotnet\n#:package Reverb.Runtime@", answer.Text);
        Assert.Contains("// reverb-notebook v1", answer.Text);
        Assert.Contains("// %% setup", answer.Text);
        // It's a real notebook: parsing it gives the same text back.
        Assert.Equal(answer.Text, NotebookDocument.Parse(answer.Text!).Serialize());
    }

    public static TheoryData<string> Fixtures => new(
        Directory.EnumerateFiles(Snapshot.FixturesDirectory, "*.cs")
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .Order(StringComparer.Ordinal));

    [Fact]
    public async Task Format_parse_then_serialize_round_trips_every_fixture()
    {
        await using var s = new Session();
        var id = 0;
        foreach (var name in Fixtures)
        {
            var text = NotebookFile.ReadText(Path.Combine(Snapshot.FixturesDirectory, name + ".cs"));
            var (upgraded, _) = NotebookUpgrader.Upgrade(NotebookDocument.Parse(text), new UpgradeOptions { RuntimeVersion = "0.1.0", Random = new Random(0) });

            await s.SendAsync(new ClientMessage { Type = "format.parse", RequestId = ++id, Text = text });
            var parsed = await s.Answer(id, $"format.parsed for {name}");
            Assert.True(parsed.Type == "format.parsed", $"{name}: {parsed.Type} {parsed.Message}");

            await s.SendAsync(new ClientMessage { Type = "format.serialize", RequestId = ++id, Parts = parsed.Parts });
            var serialized = await s.Answer(id, $"format.serialized for {name}");
            Assert.True(serialized.Type == "format.serialized", $"{name}: {serialized.Type} {serialized.Message}");
            Assert.Equal(upgraded.Serialize(), serialized.Text);
        }
    }

    [Fact]
    public async Task Format_parse_upgrades_an_unmarked_script()
    {
        await using var s = new Session();
        await s.SendAsync(new ClientMessage { Type = "format.parse", RequestId = 7, Text = "Console.WriteLine(1);\n" });

        var parts = (await s.Answer(7, "format.parsed")).Parts!;

        Assert.Contains(parts.Preamble, l => l.Text == NotebookHeader.CurrentLine);
        var code = Assert.Single(parts.Cells, c => c.Kind == "code");
        Assert.Equal("Console.WriteLine(1);", code.Source);
        Assert.True(code.Id is { } cellId && CellIds.IsValid(cellId), $"'{code.Id}' isn't a valid id");
    }

    [Theory]
    [InlineData("// reverb-notebook v1")]
    [InlineData("// reverb-notebook v1\n")]
    public async Task A_header_only_file_has_the_same_preamble_after_serialize_and_parse(string text)
    {
        await using var s = new Session();
        await s.SendAsync(new ClientMessage { Type = "format.parse", RequestId = 1, Text = text });
        var first = (await s.Answer(1, "first parse")).Parts!;
        await s.SendAsync(new ClientMessage { Type = "format.serialize", RequestId = 2, Parts = first });
        var written = (await s.Answer(2, "serialize")).Text!;
        await s.SendAsync(new ClientMessage { Type = "format.parse", RequestId = 3, Text = written });
        var second = (await s.Answer(3, "second parse")).Parts!;

        Assert.Equal(first.Preamble, second.Preamble);
    }

    [Fact]
    public async Task Format_parse_gives_the_same_ids_every_time()
    {
        await using var s = new Session();
        await s.SendAsync(new ClientMessage { Type = "format.parse", RequestId = 1, Text = "var a = 1;\n" });
        await s.SendAsync(new ClientMessage { Type = "format.parse", RequestId = 2, Text = "var a = 1;\n" });

        var first = (await s.Answer(1, "first parse")).Parts!.Cells.Single(c => c.Kind == "code").Id;
        var second = (await s.Answer(2, "second parse")).Parts!.Cells.Single(c => c.Kind == "code").Id;

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Format_serialize_fills_in_missing_attributes_and_skips_null_entries()
    {
        await using var s = new Session();
        await s.SendLineAsync("""
            {"type":"format.serialize","requestId":3,"parts":{"preamble":[{"text":"// reverb-notebook v1","terminator":"\n"}],"newline":"\n","bom":false,
             "cells":[{"kind":"code","id":"x1","source":"var a = 1;","attributes":null},null,
                      {"kind":"code","id":"x2","source":"var b = 2;","attributes":[null,{"key":"hide_code","value":null}]}]}}
            """.ReplaceLineEndings(""));

        var answer = await s.Answer(3, "format.serialized");

        Assert.True(answer.Type == "format.serialized", $"{answer.Type}: {answer.Message}");
        Assert.Contains("// %% id=x1\nvar a = 1;", answer.Text);
        Assert.Contains("// %% id=x2 hide_code\nvar b = 2;", answer.Text);
    }

    [Fact]
    public async Task Format_serialize_gives_a_cell_without_an_id_the_same_id_every_time()
    {
        // With no host to send ids back (Restricted Mode), the editor keeps saving the id-less cell.
        // A new id on each save would change a file the user didn't touch.
        await using var s = new Session();
        var request = """
            {"type":"format.serialize","requestId":ID,"parts":{"preamble":[{"text":"// reverb-notebook v1","terminator":"\n"}],"newline":"\n","bom":false,
             "cells":[{"kind":"code","id":"x1","source":"var a = 1;","attributes":[]},{"kind":"code","source":"var b = 2;","attributes":[]}]}}
            """.ReplaceLineEndings("");
        await s.SendLineAsync(request.Replace("ID", "5"));
        await s.SendLineAsync(request.Replace("ID", "6"));

        var first = await s.Answer(5, "the first format.serialized");
        var second = await s.Answer(6, "the second format.serialized");

        Assert.True(first.Type == "format.serialized", $"{first.Type}: {first.Message}");
        Assert.Matches(@"// %% id=[a-z0-9]+\nvar b = 2;", first.Text);
        Assert.Equal(first.Text, second.Text);
    }

    [Fact]
    public async Task Format_serialize_reads_CRLF_in_a_cell_source_as_one_line_break()
    {
        await using var s = new Session();
        await s.SendLineAsync("""
            {"type":"format.serialize","requestId":7,"parts":{"preamble":[{"text":"// reverb-notebook v1","terminator":"\r\n"}],"newline":"\r\n","bom":false,
             "cells":[{"kind":"code","id":"x1","source":"var a = 1;\r\nvar b = 2;","attributes":[]}]}}
            """.ReplaceLineEndings(""));

        var answer = await s.Answer(7, "format.serialized");

        Assert.True(answer.Type == "format.serialized", $"{answer.Type}: {answer.Message}");
        Assert.Contains("// %% id=x1\r\nvar a = 1;\r\nvar b = 2;\r\n", answer.Text);
        Assert.DoesNotContain("\r\r", answer.Text);
    }

    [Fact]
    public async Task Format_serialize_rejects_an_unknown_kind()
    {
        await using var s = new Session();
        await s.SendLineAsync("""{"type":"format.serialize","requestId":4,"parts":{"preamble":[],"newline":"\n","bom":false,"cells":[{"kind":"python","id":"x1","source":"print(1)","attributes":[]}]}}""");

        var answer = await s.Answer(4, "an error");

        Assert.Equal(BrowserMessages.Error, answer.Type);
        Assert.Contains("python", answer.Message);
        Assert.Contains("setup, code, markdown or types", answer.Message);
    }

    [Fact]
    public async Task Messages_are_routed_by_notebook()
    {
        await using var s = new Session();
        var aText = NotebookText.Build(["Console.WriteLine(\"from a\");"]);
        var bText = NotebookText.Build(["Console.WriteLine(\"from b\");"]);
        var a = Notebook("a.cs", aText);
        var b = Notebook("b.cs", bText);

        await s.SendAsync(Open(a, aText));
        await s.SendAsync(Open(b, bText));
        await s.SendAsync(new ClientMessage { Type = BrowserMessages.RunAll, Notebook = a });
        await s.SendAsync(new ClientMessage { Type = BrowserMessages.RunAll, Notebook = b });

        await s.WaitFor(_ => s.Stdout(a, "c1") == "from a\n" && s.Stdout(b, "c1") == "from b\n", "both notebooks' output", 60);
        var snapshots = s.Messages.Where(m => m.Type == BrowserMessages.Snapshot).Select(m => m.Notebook).ToList();
        Assert.Contains(a, snapshots);
        Assert.Contains(b, snapshots);
        Assert.All(s.Messages.Where(m => m.Type == BrowserMessages.CellStream), m => Assert.Contains(m.Notebook == a ? "from a" : "from b", m.Text));
    }

    [Fact]
    public async Task Code_fix_results_are_routed_with_their_request_id()
    {
        await using var s = new Session();
        var text = NotebookText.Build(["var a = 2;", "using System.Text;\nvar sb = new StringBuilder().Append(a);\nConsole.WriteLine(sb);"]);
        var a = Notebook("a.cs", text);

        await s.SendAsync(Open(a, text));
        var diagnostics = await s.WaitFor(m => m.Type == BrowserMessages.CellDiagnostics && m.Notebook == a && m.CellId == "c2" && m.Diagnostics!.Any(d => d.Id == "RVB004"), "the RVB004 diagnostic", 60);
        var d = diagnostics.Diagnostics!.First(d => d.Id == "RVB004");
        await s.SendAsync(new ClientMessage { Type = BrowserMessages.LangCodeFix, Notebook = a, RequestId = 31, CellId = "c2", DiagnosticId = d.Id, Line = d.StartLine, Column = d.StartColumn });

        var result = await s.WaitFor(m => m.Type == BrowserMessages.LangCodeFixResult, "the code fix result", 60);
        Assert.Equal(a, result.Notebook);
        Assert.Equal(31, result.RequestId);
        Assert.DoesNotContain("using System.Text;", result.PartCells!.First(c => c.Id == "c2").Source);
        Assert.Equal(text, File.ReadAllText(a));
    }

    [Fact]
    public async Task A_notebook_opens_with_the_text_it_was_sent_and_runs_nothing()
    {
        await using var s = new Session();
        var onDisk = NotebookText.Build(["Console.WriteLine(\"disk\");"]);
        var unsaved = NotebookText.Build(["Console.WriteLine(\"unsaved\");"]);
        var a = Notebook("a.cs", onDisk);

        await s.SendAsync(Open(a, unsaved));
        var snapshot = await s.WaitFor(m => m.Type == BrowserMessages.Snapshot && m.Notebook == a, "a snapshot");

        Assert.Contains(snapshot.Cells!, c => c.Source.Contains("unsaved"));
        Assert.All(snapshot.Cells!, c => Assert.Equal(0, c.Runs));
        Assert.Equal(onDisk, File.ReadAllText(a));
    }

    [Fact]
    public async Task Opening_an_open_notebook_again_replaces_it()
    {
        await using var s = new Session();
        var first = NotebookText.Build(["Console.WriteLine(\"first\");"]);
        var second = NotebookText.Build(["Console.WriteLine(\"second\");"]);
        var a = Notebook("a.cs", first);

        await s.SendAsync(Open(a, first));
        await s.WaitFor(m => m.Type == BrowserMessages.Snapshot && m.Notebook == a, "the first snapshot");
        await s.SendAsync(Open(a, second));
        await s.SendAsync(new ClientMessage { Type = BrowserMessages.RunAll, Notebook = a });

        await s.WaitFor(_ => s.Stdout(a, "c1") == "second\n", "the second notebook's output", 60);
    }

    [Fact]
    public async Task A_message_for_a_closed_notebook_gets_an_error()
    {
        await using var s = new Session();
        var text = NotebookText.Build(["var x = 1;"]);
        var a = Notebook("a.cs", text);
        await s.SendAsync(Open(a, text));
        await s.WaitFor(m => m.Type == BrowserMessages.Snapshot && m.Notebook == a, "a snapshot");

        await s.SendAsync(new ClientMessage { Type = "notebook.close", Notebook = a });
        await s.SendAsync(new ClientMessage { Type = BrowserMessages.CellRun, Notebook = a, CellId = "c1" });

        var error = await s.WaitFor(m => m.Type == BrowserMessages.Error, "an error");
        Assert.Contains("a.cs", error.Message);
        // The VS Code extension recognises this text (notebookLost in vscode/src/notebookChanges.ts).
        Assert.Contains(" isn't open in Reverb", error.Message);
        Assert.Equal(a, error.Notebook);
    }

    [Fact]
    public async Task A_sync_with_an_unknown_kind_gets_an_error_naming_the_notebook()
    {
        await using var s = new Session();
        var text = NotebookText.Build(["var x = 1;"]);
        var a = Notebook("a.cs", text);
        await s.SendAsync(Open(a, text));
        await s.WaitFor(m => m.Type == BrowserMessages.Snapshot && m.Notebook == a, "a snapshot");

        await s.SendLineAsync($$"""{"type":"notebook.sync","notebook":{{JsonSerializer.Serialize(a)}},"cells":[{"kind":"sql","id":"c1","source":"select 1","attributes":[]}]}""");

        var error = await s.WaitFor(m => m.Type == BrowserMessages.Error, "an error");
        Assert.Equal(a, error.Notebook);
        Assert.Contains("a.cs", error.Message);
        Assert.Contains("setup, code, markdown or types", error.Message);
    }

    [Fact]
    public async Task A_bad_line_gets_an_error_and_the_server_keeps_going()
    {
        await using var s = new Session();
        await s.SendLineAsync("not json");
        await s.SendLineAsync("null");
        await s.SendLineAsync("""{"type":"hello","requestId":2}""");

        await s.Answer(2, "hello.result");
        var types = s.Messages.Select(m => m.Type).ToList();
        Assert.Equal([BrowserMessages.Error, BrowserMessages.Error, "hello.result"], types);
    }

    [Fact]
    public async Task An_unknown_request_gets_an_error_with_its_request_id()
    {
        await using var s = new Session();
        await s.SendLineAsync("""{"type":"format.frobnicate","requestId":9}""");

        var answer = await s.Answer(9, "an error");

        Assert.Equal(BrowserMessages.Error, answer.Type);
        Assert.Contains("format.frobnicate", answer.Message);
    }
}
