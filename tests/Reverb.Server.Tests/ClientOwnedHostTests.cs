using Reverb.Format;
using Reverb.Kernel;
using Reverb.Testing;

namespace Reverb.Server.Tests;

/// <summary>A host whose file belongs to a client (VS Code): it reads, syncs and reloads, but never writes.</summary>
[Collection("server")]
public class ClientOwnedHostTests
{
    static readonly string Chain = NotebookText.Build(["var a = 1;", "var b = a * 10;", "Console.WriteLine(b + 1);"]);
    static readonly TimeSpan AutosaveDelay = TimeSpan.FromMilliseconds(50);

    sealed class OwnedNotebook(NotebookHost host) : IAsyncDisposable
    {
        public NotebookHost Host { get; } = host;
        public string Path => Host.Path;

        public static string WriteFile(string text)
        {
            var path = System.IO.Path.Combine(TestServer.TempFolder(), "chain.cs");
            File.WriteAllText(path, text);
            return path;
        }

        public static async Task<OwnedNotebook> OpenAsync(string path, bool runOnOpen = true, string? initialText = null) =>
            new(await NotebookHost.OpenAsync(new EditorOptions
            {
                NotebookPath = path,
                KernelEntryAssembly = TestServer.CliAssembly,
                AutosaveDelay = AutosaveDelay,
                KernelCacheRoot = TestServer.KernelCache,
                ClientOwnsFile = true,
                RunOnOpen = runOnOpen,
                InitialText = initialText,
            }));

        public static Task<OwnedNotebook> OpenAsync(bool runOnOpen = true) => OpenAsync(WriteFile(Chain), runOnOpen);

        public Task<TestClient> ConnectAsync() => TestClient.AttachAsync(Host);

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    static ClientMessage Sync(IEnumerable<PartCell> cells) => new() { Type = BrowserMessages.NotebookSync, Cells = [.. cells] };

    static List<PartCell> PartsOf(NotebookHost host) => [.. NotebookParts.Split(host.Document).Cells];

    static IReadOnlyList<string?> Keys(NotebookHost host) => [.. host.Document.Cells.Select(c => c.Key)];

    static ServerMessage LastSnapshot(TestClient client) => client.Messages.Last(m => m.Type == BrowserMessages.Snapshot);

    static readonly string MisplacedUsing = NotebookText.Build(["var a = 2;", "using System.Text;\nvar sb = new StringBuilder().Append(a);\nConsole.WriteLine(sb);"]);

    static async Task<DiagnosticData> WaitForUsingDiagnostic(TestClient client)
    {
        await client.WaitFor(() => client["c2"].Diagnostics.Any(d => d.Id == "RVB004"), what: "the RVB004 diagnostic");
        return client["c2"].Diagnostics.First(d => d.Id == "RVB004");
    }

    static ClientMessage FixFor(DiagnosticData d, int requestId) => new()
    {
        Type = BrowserMessages.LangCodeFix, RequestId = requestId, CellId = "c2", DiagnosticId = d.Id, Line = d.StartLine, Column = d.StartColumn,
    };

    [Fact]
    public async Task A_client_owned_code_fix_returns_the_fixed_cells()
    {
        await using var nb = await OwnedNotebook.OpenAsync(OwnedNotebook.WriteFile(MisplacedUsing), runOnOpen: false);
        await using var client = await nb.ConnectAsync();
        var d = await WaitForUsingDiagnostic(client);
        var before = nb.Host.Document.Serialize();
        var snapshots = client.Snapshots;

        await client.SendAsync(FixFor(d, 7));
        await client.WaitFor(() => client.Has(BrowserMessages.LangCodeFixResult), seconds: 60, what: "the code fix result");

        var result = client.Messages.Single(m => m.Type == BrowserMessages.LangCodeFixResult);
        Assert.Equal(7, result.RequestId);
        Assert.Null(result.Message);
        var cells = result.PartCells!;
        Assert.Contains("using System.Text;", cells.First(c => c.Kind == "setup").Source);
        Assert.DoesNotContain("using System.Text;", cells.First(c => c.Id == "c2").Source);

        await nb.Host.IdleAsync();
        Assert.Equal(before, nb.Host.Document.Serialize());
        Assert.Equal(0, client.Runs("c2"));
        Assert.Equal(snapshots, client.Snapshots);
        Assert.False(client.Has(BrowserMessages.Error));
    }

    [Fact]
    public async Task A_fix_asked_before_a_sync_is_refused()
    {
        await using var nb = await OwnedNotebook.OpenAsync(OwnedNotebook.WriteFile(MisplacedUsing), runOnOpen: false);
        await using var client = await nb.ConnectAsync();
        var d = await WaitForUsingDiagnostic(client);

        await client.SendAsync(FixFor(d, 8));
        var cells = PartsOf(nb.Host);
        cells[^1] = cells[^1] with { Source = cells[^1].Source + "\n// edited", Raw = null };
        await client.SendAsync(Sync(cells));
        await client.WaitFor(() => client.Has(BrowserMessages.LangCodeFixResult), seconds: 60, what: "the code fix result");

        var result = client.Messages.Single(m => m.Type == BrowserMessages.LangCodeFixResult);
        Assert.Equal(8, result.RequestId);
        Assert.Null(result.PartCells);
        Assert.Contains("changed", result.Message);
    }

    [Fact]
    public async Task A_fix_for_an_unknown_diagnostic_says_why()
    {
        await using var nb = await OwnedNotebook.OpenAsync(OwnedNotebook.WriteFile(MisplacedUsing), runOnOpen: false);
        await using var client = await nb.ConnectAsync();
        await WaitForUsingDiagnostic(client);

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.LangCodeFix, RequestId = 9, CellId = "c2", DiagnosticId = "NOPE", Line = 0, Column = 0 });
        await client.WaitFor(() => client.Has(BrowserMessages.LangCodeFixResult), seconds: 60, what: "the code fix result");

        var result = client.Messages.Single(m => m.Type == BrowserMessages.LangCodeFixResult);
        Assert.Equal(9, result.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Null(result.PartCells);
        Assert.False(client.Has(BrowserMessages.Error));
    }

    [Fact]
    public async Task A_client_owned_host_never_writes_the_file()
    {
        await using var nb = await OwnedNotebook.OpenAsync();
        var original = File.ReadAllText(nb.Path);
        await using var client = await nb.ConnectAsync();

        await client.Update("c1", "var a = 2;");
        await client.SendAsync(new ClientMessage { Type = BrowserMessages.CellCreate, AfterCellId = "c3", Source = "var z = 0;" });
        await client.SendAsync(new ClientMessage { Type = BrowserMessages.PackagesSet, Packages = [new PackageView { Name = "Humanizer", Version = "2.14.1" }] });
        await client.SendAsync(new ClientMessage { Type = BrowserMessages.SetLazy, Lazy = true });
        await client.SendAsync(new ClientMessage { Type = BrowserMessages.Save });
        await nb.Host.IdleAsync();
        await Task.Delay(AutosaveDelay * 6); // long enough for an autosave to have happened
        await nb.Host.IdleAsync();

        Assert.Contains("Humanizer@2.14.1", nb.Host.Document.Serialize());
        Assert.Equal(original, File.ReadAllText(nb.Path));
        Assert.False(client.Has(BrowserMessages.FileSaved));
    }

    [Fact]
    public async Task A_client_owned_host_does_not_watch_the_file()
    {
        await using var nb = await OwnedNotebook.OpenAsync(runOnOpen: false);
        await using var client = await nb.ConnectAsync();

        await File.WriteAllTextAsync(nb.Path, Chain.Replace("var a = 1;", "var a = 9;"));
        await Task.Delay(TimeSpan.FromSeconds(2)); // past the watcher's debounce and its one-second poll
        await nb.Host.IdleAsync();

        Assert.Equal("var a = 1;", nb.Host.Document.FindCell("c1")!.Source);
        Assert.False(client.Has(BrowserMessages.FileExternalChange));
    }

    [Fact]
    public async Task Open_does_not_run_when_RunOnOpen_is_false()
    {
        await using var nb = await OwnedNotebook.OpenAsync(runOnOpen: false);
        await using var client = await nb.ConnectAsync();
        // The open request is outstanding until the kernel has handled it.
        await client.WaitFor(() => client.KernelState == "ready", what: "the kernel to open the notebook");
        await nb.Host.IdleAsync();

        foreach (var id in new[] { "c1", "c2", "c3" })
        {
            Assert.Equal("stale", client.Status(id));
            Assert.Equal(0, client.Runs(id));
        }

        await client.Run("c3");
        await client.WaitFor(() => client.Stdout("c3") == "11\n" && client.Status("c3") == "idle", what: "c3 and what it needs to run");
        Assert.All(new[] { "c1", "c2", "c3" }, id => Assert.Equal(1, client.Runs(id)));
    }

    [Fact]
    public async Task A_package_change_restarts_the_kernel_without_running_cells()
    {
        await using var nb = await OwnedNotebook.OpenAsync(runOnOpen: false);
        await using var client = await nb.ConnectAsync();
        await client.WaitFor(() => client.KernelState == "ready", what: "the kernel to open the notebook");
        await client.Run("c3");
        await client.WaitFor(() => client.Stdout("c3") == "11\n" && client.Status("c3") == "idle", what: "c3 to run");
        var runs = client.Runs("c3");

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.PackagesSet, Packages = [new PackageView { Name = "Humanizer", Version = "2.14.1" }] });
        await client.WaitFor(() => client.KernelState is "building" or "starting", what: "the kernel to restart");
        await client.WaitFor(() => client.KernelState == "ready", what: "the new kernel to be ready");
        await nb.Host.IdleAsync();

        foreach (var id in new[] { "c1", "c2", "c3" }) Assert.Equal("stale", client.Status(id));
        Assert.Equal(runs, client.Runs("c3"));
    }

    [Fact]
    public async Task A_reload_with_changed_packages_restarts_the_kernel_without_running_cells()
    {
        await using var nb = await OwnedNotebook.OpenAsync(runOnOpen: false);
        await using var client = await nb.ConnectAsync();
        await client.WaitFor(() => client.KernelState == "ready", what: "the kernel to open the notebook");
        await client.Run("c3");
        await client.WaitFor(() => client.Stdout("c3") == "11\n" && client.Status("c3") == "idle", what: "c3 to run");
        var runs = client.Runs("c3");

        var withPackage = Chain.Replace("#:package Reverb.Runtime@0.1.0\n", "#:package Reverb.Runtime@0.1.0\n#:package Humanizer@2.14.1\n");
        Assert.NotEqual(Chain, withPackage);
        await client.SendAsync(new ClientMessage { Type = BrowserMessages.NotebookReload, Text = withPackage });
        await client.WaitFor(() => client.KernelState is "building" or "starting", what: "the kernel to restart");
        await client.WaitFor(() => client.KernelState == "ready", what: "the new kernel to be ready");
        await nb.Host.IdleAsync();

        foreach (var id in new[] { "c1", "c2", "c3" }) Assert.Equal("stale", client.Status(id));
        Assert.Equal(runs, client.Runs("c3"));
    }

    [Fact]
    public async Task InitialText_is_used_instead_of_the_file()
    {
        var path = OwnedNotebook.WriteFile(Chain);
        await using var nb = await OwnedNotebook.OpenAsync(path, runOnOpen: false, initialText: Chain.Replace("var a = 1;", "var a = 5;"));
        await using var client = await nb.ConnectAsync();

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.RunAll });
        await client.WaitFor(() => client.Stdout("c3") == "51\n", what: "run all");
        Assert.Equal(Chain, File.ReadAllText(path));
    }

    [Fact]
    public async Task Sync_applies_source_changes_and_structure()
    {
        await using var nb = await OwnedNotebook.OpenAsync(runOnOpen: false);
        var original = File.ReadAllText(nb.Path);
        await using var client = await nb.ConnectAsync();

        var cells = PartsOf(nb.Host);
        var setup = cells.Single(c => c.Kind == "setup");
        var c2 = cells.Single(c => c.Id == "c2") with { Source = "var b = 2;" };
        var c3 = cells.Single(c => c.Id == "c3");
        var d = new PartCell("code", "d", "Console.WriteLine(\"d\");", [], null);
        var snapshots = client.Snapshots;
        await client.SendAsync(Sync([setup, c2, c3, d]));
        await nb.Host.IdleAsync();

        Assert.Equal(["setup", "c2", "c3", "d"], Keys(nb.Host));
        Assert.Equal("var b = 2;", nb.Host.Document.FindCell("c2")!.Source);
        await client.WaitFor(() => client.Snapshots > snapshots, what: "the snapshot after the structural change");
        Assert.True(client["c2"].Edited);
        Assert.False(client["d"].Edited);
        Assert.False(client.Has(BrowserMessages.NotebookCells));
        Assert.Equal(original, File.ReadAllText(nb.Path));

        // The same list again changes nothing.
        var version = nb.Host.Version;
        await client.SendAsync(Sync(PartsOf(nb.Host)));
        await nb.Host.IdleAsync();
        Assert.Equal(version, nb.Host.Version);
    }

    [Fact]
    public async Task Sync_of_a_source_change_only_keeps_the_kernels_code()
    {
        await using var nb = await OwnedNotebook.OpenAsync();
        await using var client = await nb.ConnectAsync();
        await client.WaitFor(() => client.Stdout("c3") == "11\n", what: "the first run");

        var cells = PartsOf(nb.Host);
        var at = cells.FindIndex(c => c.Id == "c1");
        cells[at] = cells[at] with { Source = "var a = 7;" };
        var version = nb.Host.Version;
        await client.SendAsync(Sync(cells));
        await nb.Host.IdleAsync();
        Assert.Equal(version + 1, nb.Host.Version);
        Assert.Equal("var a = 7;", nb.Host.Document.FindCell("c1")!.Source);

        // Like typing: the new code reaches the kernel when the cell runs.
        await client.Run("c1");
        await client.WaitFor(() => client.Stdout("c3") == "71\n", what: "the reactive run");
    }

    [Fact]
    public async Task Sync_with_a_duplicate_id_sends_the_corrected_list()
    {
        await using var nb = await OwnedNotebook.OpenAsync(runOnOpen: false);
        await using var client = await nb.ConnectAsync();
        await using var other = await nb.ConnectAsync();

        var cells = PartsOf(nb.Host);
        var at = cells.FindIndex(c => c.Id == "c1");
        // A pasted copy of c1, metadata and all.
        cells.Insert(at + 1, cells[at] with { Source = "var a2 = 2;" });
        await client.SendAsync(Sync(cells));
        await nb.Host.IdleAsync();

        var corrected = client.Messages.Single(m => m.Type == BrowserMessages.NotebookCells).PartCells!;
        Assert.Equal(5, corrected.Count);
        Assert.Equal(corrected.Count, corrected.Select(c => c.Kind == "code" ? c.Id : c.Kind).Distinct().Count());
        Assert.Equal("c1", corrected[1].Id);
        Assert.Equal("var a2 = 2;", corrected[2].Source);
        Assert.False(other.Has(BrowserMessages.NotebookCells));

        Assert.Equal(corrected.Select(c => c.Kind == "code" ? c.Id : c.Kind), Keys(nb.Host));
        Assert.Equal("var a2 = 2;", nb.Host.Document.FindCell(corrected[2].Id!)!.Source);
    }

    [Fact]
    public async Task Sync_gives_a_pasted_cell_the_id_format_serialize_gives_it()
    {
        // VS Code saves a pasted cell (through format.serialize) before the correction arrives.
        // With the same id both times, the file only changes once.
        await using var nb = await OwnedNotebook.OpenAsync(runOnOpen: false);
        await using var client = await nb.ConnectAsync();

        var cells = PartsOf(nb.Host);
        cells.Insert(2, new PartCell("code", null, "var pasted = 1;", [], null));
        await client.SendAsync(Sync(cells));
        await nb.Host.IdleAsync();

        var corrected = client.Messages.Single(m => m.Type == BrowserMessages.NotebookCells).PartCells!;
        var (serialized, _) = CellListNormalizer.Normalize(cells, new Random(0));
        Assert.Equal(serialized.Select(c => c.Id), corrected.Select(c => c.Id));
    }

    [Fact]
    public async Task Sync_ignores_raw_text_that_does_not_match_a_new_cell()
    {
        await using var nb = await OwnedNotebook.OpenAsync(runOnOpen: false);
        await using var client = await nb.ConnectAsync();

        // Raw text says one thing and the source another. The kernel must run the source.
        var source = "Console.WriteLine(5);";
        var lying = new RawCellData("// %% id=e\n", "", "Console.WriteLine(999);\n", "", true, "code", "e", [], source, 0);
        var broken = new RawCellData(null!, null!, null!, null!, false, "code", "f", [], "var f = 1;", 0);
        await client.SendAsync(Sync([.. PartsOf(nb.Host), new PartCell("code", "e", source, [], lying), new PartCell("code", "f", "var f = 1;", [], broken)]));
        await nb.Host.IdleAsync();

        Assert.Equal(["setup", "c1", "c2", "c3", "e", "f"], Keys(nb.Host));
        Assert.DoesNotContain("999", nb.Host.Document.Serialize());
        await client.Run("e");
        await client.WaitFor(() => client.Stdout("e") == "5\n" && client.Status("e") == "idle", what: "e to run its source");
        Assert.False(client.Has(BrowserMessages.Error));
    }

    [Fact]
    public async Task Reload_replaces_the_document_and_reruns_changed_cells()
    {
        await using var nb = await OwnedNotebook.OpenAsync();
        await using var client = await nb.ConnectAsync();
        await client.WaitFor(() => client.Stdout("c3") == "11\n", what: "the first run");

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.NotebookReload, Text = Chain.Replace("var a = 1;", "var a = 3;") });
        await client.Run("c3");
        await client.WaitFor(() => client.Stdout("c3") == "31\n", what: "the rerun");
        Assert.Equal("var a = 3;", nb.Host.Document.FindCell("c1")!.Source);
        Assert.True(client.Has(BrowserMessages.FileExternalChange));
        Assert.Equal(Chain, File.ReadAllText(nb.Path));
    }

    [Fact]
    public async Task Reloading_the_text_the_host_opened_changes_nothing()
    {
        // Written before notebooks listed Reverb.Runtime: loading upgrades it, so the host's text
        // differs from the file, but it's the same notebook.
        var old = Chain.Replace("#:package Reverb.Runtime@0.1.0\n", "");
        var path = OwnedNotebook.WriteFile(old);
        await using var nb = await OwnedNotebook.OpenAsync(path, runOnOpen: false, initialText: old);
        await using var client = await nb.ConnectAsync();
        await nb.Host.IdleAsync();
        Assert.NotEqual(old, nb.Host.Document.Serialize());
        var version = nb.Host.Version;
        var snapshots = client.Snapshots;

        await client.SendAsync(new ClientMessage { Type = BrowserMessages.NotebookReload, Text = old });
        await nb.Host.IdleAsync();

        Assert.False(client.Has(BrowserMessages.FileExternalChange));
        Assert.Equal(version, nb.Host.Version);
        Assert.Equal(snapshots, client.Snapshots);
    }

    [Fact]
    public async Task Snapshots_carry_the_preamble()
    {
        await using var nb = await OwnedNotebook.OpenAsync(runOnOpen: false);
        await using var client = await nb.ConnectAsync();
        Assert.Contains(LastSnapshot(client).Preamble!, l => l.Text == "#:package Reverb.Runtime@0.1.0");

        var snapshots = client.Snapshots;
        await client.SendAsync(new ClientMessage { Type = BrowserMessages.PackagesSet, Packages = [new PackageView { Name = "Humanizer", Version = "2.14.1" }] });
        await client.WaitFor(() => client.Snapshots > snapshots, what: "the snapshot after the package change");
        Assert.Contains(client.Messages.Where(m => m.Type == BrowserMessages.Snapshot).Skip(snapshots).First().Preamble!, l => l.Text == "#:package Humanizer@2.14.1");
    }

    [Fact]
    public async Task Two_hosts_on_one_file_follow_each_other()
    {
        var path = OwnedNotebook.WriteFile(Chain);
        await using var a = await OwnedNotebook.OpenAsync(path, runOnOpen: false);
        await using var b = await OwnedNotebook.OpenAsync(path, runOnOpen: false);
        await using var clientA = await a.ConnectAsync();
        await using var clientB = await b.ConnectAsync();

        var cells = PartsOf(a.Host);
        var at = cells.FindIndex(c => c.Id == "c1");
        cells[at] = cells[at] with { Source = "var a = 4;" };
        await clientA.SendAsync(Sync(cells));
        await a.Host.IdleAsync();

        // VS Code autosaves A's notebook, and the window showing B reloads it.
        var text = NotebookParts.Join(NotebookParts.Split(a.Host.Document)).Serialize();
        await File.WriteAllTextAsync(path, text);
        await clientB.SendAsync(new ClientMessage { Type = BrowserMessages.NotebookReload, Text = text });
        await b.Host.IdleAsync();

        Assert.Equal(a.Host.Document.Serialize(), b.Host.Document.Serialize());
        Assert.Equal(Keys(a.Host), Keys(b.Host));
        Assert.Equal("var a = 4;", b.Host.Document.FindCell("c1")!.Source);
        Assert.False(clientB.Has(BrowserMessages.Error), string.Join("; ", clientB.Messages.Where(m => m.Type == BrowserMessages.Error).Select(m => m.Message)));
    }
}
