using System.Collections.Concurrent;
using Reverb.Format;
using Reverb.Testing;

namespace Reverb.Kernel.Tests;

/// <summary>A real kernel process, started the way the CLI and server start it.</summary>
[Collection("cli")]
public class KernelProcessTests(ITestOutputHelper output)
{
    sealed class Recorder
    {
        public ConcurrentQueue<KernelEvent> Events { get; } = new();
        public string? StatusOf(string cell) => Events.LastOrDefault(e => e.Type == EventTypes.Status && e.Cell == cell)?.Status;
        public string Stdout(string cell) => string.Concat(Events.Where(e => e.Type == EventTypes.Stream && e.Cell == cell && e.Stream == "stdout").Select(e => e.Text)).ReplaceLineEndings("\n");
    }

    static async Task<(KernelClient Client, Recorder Recorder)> StartAsync(string text)
    {
        var client = await KernelClient.StartAsync(new KernelLaunch
        {
            EntryAssembly = Cli.CliAssembly,
            Directives = NotebookDocument.Parse(text).Preamble.Directives,
        });
        var recorder = new Recorder();
        client.Event += recorder.Events.Enqueue;
        return (client, recorder);
    }

    static KernelRequest Open(string text) => new() { Type = RequestTypes.Open, Text = text, Run = true };

    static async Task WaitFor(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Runs_a_notebook()
    {
        var text = NotebookText.Build(["var a = 20;", "Console.WriteLine(a + 1);"]);
        var (client, rec) = await StartAsync(text);
        await using var _ = client;
        var done = await client.SendAsync(Open(text));
        Assert.True(done.Ok);
        Assert.Equal("21\n", rec.Stdout("c2"));
        Assert.Equal("idle", rec.StatusOf("c2"));
    }

    [Fact]
    public async Task Infinite_loop_ignores_interrupt_and_restart_recovers()
    {
        var text = NotebookText.Build(["var spin = 0;\nwhile (true) { spin++; }", "Console.WriteLine(\"after\");"]);
        var (client, rec) = await StartAsync(text);
        await using var _ = client;
        var open = client.SendAsync(Open(text));
        await WaitFor(() => rec.StatusOf("c1") == "running");

        // A loop that never looks at the cancellation token can't be stopped cooperatively.
        client.Interrupt();
        var stopped = await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(3))) == open;
        Assert.False(stopped);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await client.RestartAsync();
        output.WriteLine($"restart took {clock.ElapsedMilliseconds} ms");
        await Assert.ThrowsAsync<KernelExitedException>(() => open);

        // The new kernel is empty; open a fixed notebook and it works.
        var fixedText = NotebookText.Build(["var spin = 0;", "Console.WriteLine(\"after\");"]);
        var done = await client.SendAsync(Open(fixedText));
        Assert.True(done.Ok);
        await WaitFor(() => rec.Stdout("c2") == "after\n");
    }

    [Fact]
    public async Task Kernel_exits_when_stdin_closes_during_an_endless_cell()
    {
        var text = NotebookText.Build(["var spin = 0;\nwhile (true) { spin++; }", "Console.WriteLine(\"after\");"]);
        var (client, rec) = await StartAsync(text);
        await using var _ = client;
        var open = client.SendAsync(Open(text));
        await WaitFor(() => rec.StatusOf("c1") == "running");
        client.CloseInput();
        await client.WaitForProcessExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("", rec.Stdout("c2"));
    }

    [Fact]
    public async Task Kernel_exits_when_stdin_closes_while_idle()
    {
        var text = NotebookText.Build(["var a = 1;"]);
        var (client, _) = await StartAsync(text);
        await using var _c = client;
        client.CloseInput();
        await client.WaitForProcessExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Interrupt_stops_a_cooperative_cell()
    {
        var text = NotebookText.Build(["await Task.Delay(-1, Notebook.CancellationToken);", "Console.WriteLine(\"after\");"]);
        var (client, rec) = await StartAsync(text);
        await using var _ = client;
        var open = client.SendAsync(Open(text));
        await WaitFor(() => rec.StatusOf("c1") == "running");
        client.Interrupt();
        await open.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("stale", rec.StatusOf("c1"));
        Assert.Equal("stale", rec.StatusOf("c2"));
        Assert.Equal("", rec.Stdout("c2"));
    }

    [Fact]
    public async Task An_edit_preempts_the_running_cell()
    {
        var slow = NotebookText.Build(["await Task.Delay(-1, Notebook.CancellationToken);\nvar a = 1;", "Console.WriteLine(a);"]);
        var (client, rec) = await StartAsync(slow);
        await using var _ = client;
        var open = client.SendAsync(Open(slow));
        await WaitFor(() => rec.StatusOf("c1") == "running");
        var quick = NotebookText.Build(["var a = 1;", "Console.WriteLine(a);"]);
        var update = client.SendAsync(new KernelRequest { Type = RequestTypes.Update, Text = quick });
        await Task.WhenAll(open, update).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("1\n", rec.Stdout("c2"));
        Assert.Equal("idle", rec.StatusOf("c1"));
    }

    [Fact]
    public async Task A_cell_that_exits_the_process_is_reported()
    {
        var text = NotebookText.Build(["Environment.Exit(3);"]);
        var (client, _) = await StartAsync(text);
        await using var c = client;
        var exited = new TaskCompletionSource<int>();
        client.Exited += code => exited.TrySetResult(code);
        var e = await Assert.ThrowsAsync<KernelExitedException>(() => client.SendAsync(Open(text)));
        Assert.Equal(3, e.ExitCode);
        Assert.Equal(3, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Edit_to_output_latency_through_the_process()
    {
        string Text(int n) => NotebookText.Build([$"var n = {n};", "Console.WriteLine(n * 2);", .. Enumerable.Range(0, 10).Select(i => $"var filler{i} = Enumerable.Range(0, 50).Sum() + {i};")]);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (client, rec) = await StartAsync(Text(0));
        var startup = clock.ElapsedMilliseconds;
        await using var _ = client;
        clock.Restart();
        await client.SendAsync(Open(Text(0)));
        var firstOpen = clock.ElapsedMilliseconds;

        var times = new List<double>();
        for (var i = 1; i <= 40; i++)
        {
            clock.Restart();
            await client.SendAsync(new KernelRequest { Type = RequestTypes.Update, Text = Text(i) });
            if (i > 10) times.Add(clock.Elapsed.TotalMilliseconds);
        }
        Assert.EndsWith($"{40 * 2}\n", rec.Stdout("c2"));
        times.Sort();
        var p50 = times[times.Count / 2];
        var p90 = times[(int)(times.Count * 0.9)];
        output.WriteLine($"kernel start {startup} ms, first open (12 cells) {firstOpen} ms, edit-to-output p50 {p50:F1} ms, p90 {p90:F1} ms, max {times[^1]:F1} ms");
        if (Environment.GetEnvironmentVariable("CI") is null) Assert.True(p50 < 300, $"p50 was {p50:F1} ms");
    }

    [Fact]
    public async Task Widget_change_over_the_protocol_reruns_readers()
    {
        var text = NotebookText.Build(["var n = Ui.Slider(\"n\", min: 0, max: 10, value: 2);", "Console.WriteLine(n.Value * 10);"]);
        var (client, rec) = await StartAsync(text);
        await using var _ = client;
        await client.SendAsync(Open(text));
        Assert.Equal("20\n", rec.Stdout("c2"));
        var done = await client.SendAsync(new KernelRequest { Type = RequestTypes.SetWidget, WidgetId = "c1:0", Value = System.Text.Json.JsonDocument.Parse("7").RootElement });
        Assert.True(done.Ok);
        Assert.EndsWith("70\n", rec.Stdout("c2"));
        Assert.Contains(rec.Events, e => e.Type == EventTypes.Outputs && e.Cell == "c1" && e.Outputs![0].Data![Output.PlainText] == "[slider n = 7]");
    }

    [Fact]
    public async Task Analyze_answers_while_a_cell_runs()
    {
        var text = NotebookText.Build(["var a = 1;\nawait Task.Delay(-1, Notebook.CancellationToken);", "var b = a + 1;"]);
        var (client, rec) = await StartAsync(text);
        await using var _ = client;
        var open = client.SendAsync(Open(text));
        await WaitFor(() => rec.StatusOf("c1") == "running");

        var edited = NotebookText.Build(["var a = 1;\nawait Task.Delay(-1, Notebook.CancellationToken);", "var b = a + missing;", "var c = b;"]);
        var done = await client.SendAsync(new KernelRequest { Type = RequestTypes.Analyze, Text = edited, Version = 7 }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(done.Ok);
        var analysis = Assert.Single(rec.Events, e => e.Type == EventTypes.Analysis);
        Assert.Equal(7, analysis.Version);
        Assert.Contains(analysis.Diagnostics!, d => d.Id == "CS0103" && d.Cell == "c2");
        Assert.Contains(analysis.Graph!, e => e.From == "c2" && e.To == "c1" && e.Via.SequenceEqual(["a"]));
        Assert.Contains(analysis.Graph!, e => e.From == "c3" && e.To == "c2");

        // Analysing didn't touch the running cell.
        Assert.Equal("running", rec.StatusOf("c1"));
        Assert.False(open.IsCompleted);
        client.Interrupt();
        await open.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_cell_printing_while_diagnostics_are_sent_does_not_deadlock()
    {
        // On Unix the console stream locks Console.Out on every write. The protocol writer must not,
        // or a cell holding Console.Out and the analyzer holding the protocol lock wait on each other.
        var text = NotebookText.Build(["var watch = System.Diagnostics.Stopwatch.StartNew();\nwhile (watch.ElapsedMilliseconds < 4000) Console.WriteLine(watch.ElapsedMilliseconds);\nConsole.WriteLine(\"end\");"]);
        var (client, rec) = await StartAsync(text);
        await using var _ = client;
        var open = client.SendAsync(Open(text));
        await WaitFor(() => rec.StatusOf("c1") == "running");
        for (var v = 0; v < 40 && !open.IsCompleted; v++)
            await client.SendAsync(new KernelRequest { Type = RequestTypes.Analyze, Text = NotebookText.Build([$"var x = missing{v};"]), Version = v }).WaitAsync(TimeSpan.FromSeconds(20));
        await open.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.EndsWith("end\n", rec.Stdout("c1"));
    }

    [Fact]
    public async Task Kernels_starting_at_the_same_time_share_their_config_safely()
    {
        // The notebook path is part of the config, so a new one means a file nobody has written yet: all eight race to write it.
        var notebook = Path.Combine(Path.GetTempPath(), $"race-{Guid.NewGuid():N}.cs");
        var clients = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => KernelClient.StartAsync(new KernelLaunch { EntryAssembly = Cli.CliAssembly, NotebookPath = notebook })));
        foreach (var c in clients) await c.DisposeAsync();
    }

    [Fact]
    public async Task A_cell_reading_the_console_does_not_eat_requests()
    {
        var text = NotebookText.Build(["var line = Console.ReadLine();\nConsole.WriteLine(line is null);", "var n = 1;"]);
        var (client, rec) = await StartAsync(text);
        await using var _ = client;
        await client.SendAsync(Open(text)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("True\n", rec.Stdout("c1"));

        // The kernel still hears the next request.
        var edited = NotebookText.Build(["var line = Console.ReadLine();\nConsole.WriteLine(line is null);", "var n = 2;\nConsole.WriteLine(n);"]);
        var done = await client.SendAsync(new KernelRequest { Type = RequestTypes.Update, Text = edited }).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(done.Ok);
        Assert.Equal("2\n", rec.Stdout("c2"));
    }
}
