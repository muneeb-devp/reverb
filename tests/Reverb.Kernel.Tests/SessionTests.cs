using Reverb.Format;
using Reverb.Testing;

namespace Reverb.Kernel.Tests;

public class SessionTests(ITestOutputHelper output)
{
    static NotebookDocument Doc(string[] cells, string? types = null, string? setup = NotebookText.DefaultSetup, IReadOnlyList<string>? attributes = null) =>
        NotebookDocument.Parse(NotebookText.Build(cells, types, setup, attributes: attributes));

    async Task<KernelSession> Open(string[] cells, string? types = null, string? setup = NotebookText.DefaultSetup, bool lazy = false, IReadOnlyList<string>? attributes = null)
    {
        var session = new KernelSession { Lazy = lazy };
        await session.OpenAsync(Doc(cells, types, setup, attributes));
        Dump(session);
        return session;
    }

    void Dump(KernelSession s)
    {
        foreach (var c in s.Analysis!.Cells.Where(c => c.Kind == CellKind.Code))
            output.WriteLine($"{c.Key}: {s.StatusOf(c.Key)} {s.MessageOf(c.Key)} | {string.Join(" | ", s.OutputsOf(c.Key).Select(Text))}");
    }

    static string Text(CellOutputItem item) => item switch
    {
        StreamItem st => $"{st.Stream}:{st.Text.ReplaceLineEndings("\n")}",
        DisplayItem d => $"display:{(d.Output.Title is null ? "" : d.Output.Title + ":")}{d.Output.Text}",
        _ => "?",
    };

    static string[] Texts(KernelSession s, string cell) => [.. s.OutputsOf(cell).Select(Text)];

    [Fact]
    public async Task Run_all_produces_values_and_outputs()
    {
        await using var s = await Open(["var a = 2;", "var b = a * 3;\nConsole.WriteLine(b);", "b.Dump(\"b\");"]);
        Assert.Equal(6, s.Store.Get<int>("b"));
        Assert.Equal(["stdout:6\n"], Texts(s, "c2"));
        Assert.Equal(["display:b:6"], Texts(s, "c3"));
        Assert.All(["c1", "c2", "c3"], c => Assert.Equal(CellStatus.Idle, s.StatusOf(c)));
    }

    [Fact]
    public async Task Console_and_display_output_keep_their_order()
    {
        await using var s = await Open(["Console.WriteLine(\"a\");\n1.Dump();\nConsole.Write(\"b\");\nConsole.Error.WriteLine(\"e\");\nMd(\"**m**\");"]);
        Assert.Equal(["stdout:a\n", "display:1", "stdout:b", "stderr:e\n", "display:**m**"], Texts(s, "c1"));
    }

    [Fact]
    public async Task Editing_a_cell_reruns_it_and_its_dependents_only()
    {
        string[] cells = ["var a = 1;", "var b = a + 1;", "var c = b + 1;", "var other = 10;"];
        await using var s = await Open(cells);
        s.Stats.Reset();
        cells[0] = "var a = 5;";
        await s.UpdateAsync(Doc(cells));
        Assert.Equal(["c1", "c2", "c3"], s.Stats.RunLog);
        Assert.Equal(1, s.Compiler.CompileCount - 4); // only c1 recompiled
        Assert.Equal(7, s.Store.Get<int>("c"));
    }

    [Fact]
    public async Task Changing_an_input_type_recompiles_the_reader()
    {
        string[] cells = ["var a = 1;", "var text = a.ToString();"];
        await using var s = await Open(cells);
        var before = s.Compiler.CompileCount;
        cells[0] = "var a = \"one\";";
        await s.UpdateAsync(Doc(cells));
        Assert.Equal(before + 2, s.Compiler.CompileCount);
        Assert.Equal("one", s.Store.Get<string>("text"));
    }

    [Fact]
    public async Task Unchanged_edit_runs_nothing()
    {
        string[] cells = ["var a = 1;", "var b = a + 1;"];
        await using var s = await Open(cells);
        s.Stats.Reset();
        await s.UpdateAsync(Doc(cells));
        Assert.Empty(s.Stats.RunLog);
    }

    [Fact]
    public async Task Deleting_a_cell_removes_its_values_and_breaks_dependents()
    {
        await using var s = await Open(["var a = 1;", "var b = a + 1;", "var c = 3;"]);
        s.Stats.Reset();
        await s.UpdateAsync(NotebookDocument.Parse(NotebookText.Build(["var b = a + 1;", "var c = 3;"], ids: ["c2", "c3"])));
        Dump(s);
        Assert.False(s.Store.Contains("a"));
        Assert.False(s.Store.Contains("b"));
        Assert.Equal(CellStatus.Error, s.StatusOf("c2"));
        Assert.Empty(s.Stats.RunLog); // c2 is blocked by its compile error; c3 didn't need to run
        Assert.Equal(3, s.Store.Get<int>("c"));
    }

    [Fact]
    public async Task Runtime_errors_block_dependents_but_not_other_cells()
    {
        string[] cells = ["var zero = 0;\nvar q = 1 / zero;", "var r = q + 1;", "var fine = 1;"];
        await using var s = await Open(cells);
        Assert.Equal(CellStatus.Error, s.StatusOf("c1"));
        var error = Assert.Single(Texts(s, "c1"));
        Assert.Equal("display:DivideByZeroException: Attempted to divide by zero.\n  at cell 'c1', line 2", error);
        Assert.Equal(CellStatus.UpstreamError, s.StatusOf("c2"));
        Assert.Equal(CellStatus.Idle, s.StatusOf("c3"));
        Assert.False(s.Store.Contains("zero"));

        cells[0] = "var zero = 1;\nvar q = 1 / zero;";
        await s.UpdateAsync(Doc(cells));
        Assert.Equal(2, s.Store.Get<int>("r"));
        Assert.Equal(CellStatus.Idle, s.StatusOf("c2"));
    }

    [Fact]
    public async Task Compile_errors_are_cell_errors()
    {
        await using var s = await Open(["var a = ;", "var b = 2;"]);
        Assert.Equal(CellStatus.Error, s.StatusOf("c1"));
        Assert.Contains("CS1525", s.MessageOf("c1"));
        Assert.Equal(CellStatus.Idle, s.StatusOf("c2"));
    }

    [Fact]
    public async Task Disabled_cells_block_their_dependents()
    {
        await using var s = await Open(["var a = 1;", "var b = a + 1;", "var c = 3;"], attributes: ["disabled", "", ""]);
        Assert.Equal(CellStatus.Disabled, s.StatusOf("c1"));
        Assert.Equal(CellStatus.UpstreamError, s.StatusOf("c2"));
        Assert.Equal("Cell 'c1' is disabled, so this cell can't run.", s.MessageOf("c2"));
        Assert.Equal(CellStatus.Idle, s.StatusOf("c3"));

        await s.UpdateAsync(Doc(["var a = 1;", "var b = a + 1;", "var c = 3;"]));
        Assert.Equal(2, s.Store.Get<int>("b"));
    }

    [Fact]
    public async Task Lazy_mode_marks_dependents_stale()
    {
        string[] cells = ["var a = 1;", "var b = a + 1;", "var c = b + 1;"];
        await using var s = await Open(cells, lazy: true);
        Assert.Equal(0, s.Stats.Runs);
        Assert.All(["c1", "c2", "c3"], c => Assert.Equal(CellStatus.Stale, s.StatusOf(c)));

        await s.RunAllAsync();
        Assert.Equal(3, s.Store.Get<int>("c"));
        s.Stats.Reset();

        cells[0] = "var a = 10;";
        await s.UpdateAsync(Doc(cells));
        Assert.Empty(s.Stats.RunLog);
        Assert.All(["c1", "c2", "c3"], c => Assert.Equal(CellStatus.Stale, s.StatusOf(c)));

        await s.RunCellAsync("c1");
        Assert.Equal(["c1"], s.Stats.RunLog);
        Assert.Equal(CellStatus.Idle, s.StatusOf("c1"));
        Assert.Equal(CellStatus.Stale, s.StatusOf("c2"));
        Assert.Equal(10, s.Store.Get<int>("a"));
    }

    [Fact]
    public async Task Running_a_cell_runs_its_stale_ancestors_first()
    {
        string[] cells = ["var a = 1;", "var b = a + 1;", "var c = b + 1;", "var other = 5;", "var d = a + other;"];
        await using var s = new KernelSession();
        await s.OpenAsync(Doc(cells), run: false);
        await s.RunCellAsync("c3");
        Dump(s);
        // c5 runs because its input a changes, and so its other stale input c4 runs too.
        Assert.Equal(["c1", "c2", "c3", "c4", "c5"], s.Stats.RunLog);
        Assert.Equal(3, s.Store.Get<int>("c"));
        Assert.Equal(6, s.Store.Get<int>("d"));
    }

    [Fact]
    public async Task An_edit_after_opening_without_running_runs_what_it_needs()
    {
        string[] cells = ["var a = 1;", "var b = 2;", "var c = a + b;"];
        await using var s = new KernelSession();
        await s.OpenAsync(Doc(cells), run: false);
        cells[0] = "var a = 10;";
        await s.UpdateAsync(Doc(cells));
        Assert.Equal(["c1", "c2", "c3"], s.Stats.RunLog);
        Assert.Equal(12, s.Store.Get<int>("c"));
    }

    [Fact]
    public async Task Lazy_run_of_a_cell_runs_stale_ancestors_and_the_cells_between()
    {
        string[] cells = ["var a = 1;", "var b = a + 1;", "var c = b + 1;", "var d = a * 100;"];
        await using var s = await Open(cells, lazy: true);
        await s.RunCellAsync("c3");
        Assert.Equal(["c1", "c2", "c3"], s.Stats.RunLog);
        Assert.Equal(3, s.Store.Get<int>("c"));
        Assert.Equal(CellStatus.Stale, s.StatusOf("c4"));

        s.Stats.Reset();
        cells[0] = "var a = 10;";
        await s.UpdateAsync(Doc(cells));
        await s.RunCellAsync("c2");
        Assert.Equal(["c1", "c2"], s.Stats.RunLog);
        Assert.Equal(11, s.Store.Get<int>("b"));
        Assert.Equal(CellStatus.Stale, s.StatusOf("c3"));
        Assert.Equal(CellStatus.Stale, s.StatusOf("c4"));
    }

    [Fact]
    public async Task Update_and_run_in_one_request_runs_each_cell_once()
    {
        string[] cells = ["var a = 1;", "var b = a + 1;", "var r = Random.Shared.Next();"];
        await using var s = await Open(cells);
        s.Stats.Reset();
        cells[0] = "var a = 2;";
        await s.UpdateAsync(Doc(cells), runCell: "c1");
        Assert.Equal(["c1", "c2"], s.Stats.RunLog);

        s.Stats.Reset();
        cells[0] = "var a = 3;";
        await s.UpdateAsync(Doc(cells), runAll: true);
        Assert.Equal(["c1", "c2", "c3"], s.Stats.RunLog);

        // An unchanged cell still runs when asked to, with its dependents.
        s.Stats.Reset();
        await s.UpdateAsync(Doc(cells), runCell: "c2");
        Assert.Equal(["c2"], s.Stats.RunLog);
    }

    [Fact]
    public async Task Lazy_update_and_run_runs_the_cell_and_leaves_dependents_stale()
    {
        string[] cells = ["var a = 1;", "var b = a + 1;"];
        await using var s = await Open(cells, lazy: true);
        await s.RunAllAsync();
        s.Stats.Reset();
        cells[0] = "var a = 5;";
        await s.UpdateAsync(Doc(cells), runCell: "c1");
        Assert.Equal(["c1"], s.Stats.RunLog);
        Assert.Equal(CellStatus.Stale, s.StatusOf("c2"));
    }

    [Fact]
    public async Task Moving_a_declaration_between_cells_keeps_both_values()
    {
        await using var s = await Open(["var y = 1;", "var x = 2;"]);
        await s.UpdateAsync(Doc(["var x = 2;", "var y = 1;"]));
        Assert.Equal(2, s.Store.Get<int>("x"));
        Assert.Equal(1, s.Store.Get<int>("y"));
    }

    [Fact]
    public async Task Types_change_reruns_cells_that_use_types()
    {
        string[] cells = ["var o = new R(1);", "var n = 5;", "var a = o.A;"];
        await using var s = await Open(cells, types: "record R(int A);");
        s.Stats.Reset();
        await s.UpdateAsync(Doc(cells, types: "record R(int A, int B = 2);"));
        Assert.Equal(["c1", "c3"], s.Stats.RunLog);
        Assert.Equal(1, s.Store.Get<int>("a"));
    }

    [Fact]
    public async Task Setup_change_reruns_everything()
    {
        string[] cells = ["var a = 1;", "var b = 2;"];
        await using var s = await Open(cells);
        s.Stats.Reset();
        await s.UpdateAsync(Doc(cells, setup: NotebookText.DefaultSetup + "\nusing System.Text;"));
        Assert.Equal(["c1", "c2"], s.Stats.RunLog);
    }

    [Fact]
    public async Task Args_reach_cells()
    {
        var s = new KernelSession { Args = ["x", "y"] };
        await using var _ = s;
        await s.OpenAsync(Doc(["var n = args.Length;"]));
        Assert.Equal(2, s.Store.Get<int>("n"));
    }

    [Fact]
    public async Task Using_var_lives_until_its_cell_reruns()
    {
        string[] cells = ["using var ms = new MemoryStream(new byte[] { 7, 8 });", "var first = ms.ReadByte();"];
        await using var s = await Open(cells);
        Assert.Equal(7, s.Store.Get<int>("first"));
        var old = s.Store.Get<MemoryStream>("ms");
        Assert.True(old.CanRead);

        cells[0] = "using var ms = new MemoryStream(new byte[] { 9 });";
        await s.UpdateAsync(Doc(cells));
        Assert.False(old.CanRead); // disposed when its cell re-ran
        Assert.Equal(9, s.Store.Get<int>("first"));
    }

    [Fact]
    public async Task Replaced_cell_contexts_unload()
    {
        string[] cells = ["var a = 1;\nFunc<int, int> f = x => x + a;", "var b = f(1);"];
        await using var s = await Open(cells);
        cells[0] = "var a = 2;\nFunc<int, int> f = x => x + a;";
        await s.UpdateAsync(Doc(cells));
        Assert.Equal(3, s.Store.Get<int>("b"));
        Assert.NotEmpty(s.Compiler.Unloaded);
        Assert.True(Collected(s.Compiler.Unloaded), "the old context for c1 is still alive");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static bool Collected(IEnumerable<WeakReference> refs)
    {
        for (var i = 0; i < 30 && refs.Any(r => r.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return refs.All(r => !r.IsAlive);
    }

    [Fact]
    public async Task Widget_change_reruns_readers_only()
    {
        await using var s = await Open([
            "var size = Ui.Slider(\"Size\", min: 0, max: 10, value: 3);",
            "var doubled = size.Value * 2;",
            "var label = $\"{doubled}!\";",
            "var unrelated = 1;",
        ]);
        Assert.Equal(6.0, s.Store.Get<double>("doubled"));
        s.Stats.Reset();

        Assert.True(await s.SetWidgetAsync("c1:0", 5.0));
        Assert.Equal(["c2", "c3"], s.Stats.RunLog);
        Assert.Equal("10!", s.Store.Get<string>("label"));

        Assert.False(await s.SetWidgetAsync("c1:0", 50.0)); // out of range
        Assert.False(await s.SetWidgetAsync("nope:0", 1));
    }

    [Fact]
    public async Task Every_widget_kind_takes_the_values_the_editor_sends()
    {
        await using var s = await Open([
            """
            var region = Ui.Dropdown("Region", [2, 0, 1], value: 0);
            var query = Ui.Text("Search");
            var all = Ui.Checkbox("All");
            var rows = Ui.Number("Rows", 10, min: 1, max: 100);
            var refresh = Ui.Button("Refresh");
            """,
            "var summary = $\"{region.Value}|{query.Value}|{all.Value}|{rows.Value}|{refresh.Value}\";",
        ]);
        Assert.Equal("0||False|10|0", s.Store.Get<string>("summary"));

        // A dropdown gets an index: index 0 is the option 2, even though 0 is also an option.
        Assert.True(await s.SetWidgetAsync("c1:0", 0));
        Assert.True(await s.SetWidgetAsync("c1:1", "abc"));
        Assert.True(await s.SetWidgetAsync("c1:2", true));
        Assert.True(await s.SetWidgetAsync("c1:3", 42));
        Assert.True(await s.SetWidgetAsync("c1:4", true));
        Assert.True(await s.SetWidgetAsync("c1:4", true));
        Assert.Equal("2|abc|True|42|2", s.Store.Get<string>("summary"));

        Assert.False(await s.SetWidgetAsync("c1:0", 3));    // no fourth option
        Assert.False(await s.SetWidgetAsync("c1:3", 500));  // above max
        Assert.False(await s.SetWidgetAsync("c1:2", "yes")); // not a bool
        Assert.Equal("2|abc|True|42|2", s.Store.Get<string>("summary"));
    }

    [Fact]
    public async Task Text_area_takes_multiline_text_and_splits_it_into_lines()
    {
        await using var s = await Open([
            "var ids = Ui.TextArea(\"Ids\", value: \"1\", rows: 6);",
            "var joined = string.Join(\",\", ids.Lines);",
        ]);
        Assert.Equal("1", s.Store.Get<string>("joined"));

        Assert.True(await s.SetWidgetAsync("c1:0", "192320\r\n  1024397 \n\n42\n"));
        Assert.Equal("192320,1024397,42", s.Store.Get<string>("joined"));
        Assert.False(await s.SetWidgetAsync("c1:0", 42)); // not text
    }

    [Fact]
    public async Task Widget_value_survives_its_cell_rerunning_while_it_still_fits()
    {
        string[] cells = ["var size = Ui.Slider(\"Size\", min: 0, max: 10, value: 3);", "var doubled = size.Value * 2;"];
        await using var s = await Open(cells);
        await s.SetWidgetAsync("c1:0", 8.0);

        cells[0] = "var size = Ui.Slider(\"Size (edited)\", min: 0, max: 10, value: 3);";
        await s.UpdateAsync(Doc(cells));
        Assert.Equal(16.0, s.Store.Get<double>("doubled"));

        cells[0] = "var size = Ui.Slider(\"Size\", min: 0, max: 5, value: 3);";
        await s.UpdateAsync(Doc(cells));
        Assert.Equal(6.0, s.Store.Get<double>("doubled")); // 8 no longer fits, so back to the default
    }

    [Fact]
    public async Task Interrupt_stops_a_cooperative_cell_and_marks_the_rest_stale()
    {
        var s = new KernelSession();
        await using var _ = s;
        var run = s.OpenAsync(Doc(["await Task.Delay(-1, Notebook.CancellationToken);", "var after = 1;"]));
        await WaitFor(() => s.StatusOf("c1") == CellStatus.Running);
        s.Interrupt();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CellStatus.Stale, s.StatusOf("c1"));
        Assert.Equal(CellStatus.Stale, s.StatusOf("c2"));
        Assert.False(s.Store.Contains("after"));
    }

    [Fact]
    public async Task New_edit_cancels_the_running_cell_and_merges_the_work()
    {
        var s = new KernelSession();
        await using var _ = s;
        string[] cells = ["await Task.Delay(-1, Notebook.CancellationToken);\nvar a = 1;", "var b = 2;"];
        var first = s.OpenAsync(Doc(cells));
        await WaitFor(() => s.StatusOf("c1") == CellStatus.Running);
        cells[0] = "var a = 1;";
        await s.UpdateAsync(Doc(cells)).WaitAsync(TimeSpan.FromSeconds(10));
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CellStatus.Idle, s.StatusOf("c1"));
        Assert.Equal(CellStatus.Idle, s.StatusOf("c2"));
        Assert.Equal(2, s.Store.Get<int>("b"));
    }

    static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}
