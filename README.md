<h1>
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo-light.svg">
    <img alt="Reverb" src="assets/logo-light.svg" width="300">
  </picture>
</h1>

[![CI](https://github.com/muneeb-devp/reverb/actions/workflows/ci.yml/badge.svg)](https://github.com/muneeb-devp/reverb/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Reverb.Notebooks?label=Reverb.Notebooks)](https://www.nuget.org/packages/Reverb.Notebooks)
[![NuGet](https://img.shields.io/nuget/v/Reverb.Runtime?label=Reverb.Runtime)](https://www.nuget.org/packages/Reverb.Runtime)
[![VS Code](https://img.shields.io/visual-studio-marketplace/v/MuneebMughal.reverb-notebooks?label=VS%20Code)](https://marketplace.visualstudio.com/items?itemName=MuneebMughal.reverb-notebooks)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

Reverb is a reactive notebook for C#. Write your code in cells, and when you change one, only the cells that use its results run again, the way a spreadsheet recalculates.

A notebook is just a `.cs` file. It runs with `dotnet run`, shows up in Git as a normal diff, and has no JSON or saved output in it. You can edit it in the browser, in VS Code, or in any text editor.

![A tour of Reverb using samples/sales-explorer.cs: the notebook as a plain .cs file, a slider and a dropdown redrawing charts, a code change updating the cells after it, completions and hover, an error caught while typing, the file changed from outside, adding a NuGet package with a one-click fix for a misplaced using, dark mode, the notebook served as an app, an exported HTML page, and a Jupyter notebook converted](assets/demo.gif)

- [Install](#install)
- [Quickstart](#quickstart)
- [Writing notebooks](#writing-notebooks): cells, output, widgets, charts, packages
- [The browser editor](#the-browser-editor): shortcuts, lazy mode, file watching
- [VS Code](#vs-code): the same notebooks as native VS Code notebooks
- [Command line](#command-line) and [using Reverb in CI](#using-reverb-in-ci)
- [Rules and diagnostics](#rules-and-diagnostics)
- [Common questions](#common-questions)
- [How it compares](#how-it-compares)
- [How it works](#how-it-works)
- [Building from source](#building-from-source) and [contributing](#contributing)

## Install

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Then pick how you want to work:

**In VS Code.** Install [Reverb Notebooks](https://marketplace.visualstudio.com/items?itemName=MuneebMughal.reverb-notebooks) from the Extensions view. It offers to install the command line tool for you the first time you open a notebook.

**In the browser, or from the terminal.** Install the tool:

```sh
dotnet tool install -g Reverb.Notebooks
```

That gives you the `reverb` command. Update it later with `dotnet tool update -g Reverb.Notebooks`.

## Quickstart

```sh
reverb new hello.cs     # creates a starter notebook and opens it in your browser
```

Change a cell, press <kbd>Ctrl</kbd>/<kbd>Cmd</kbd>+<kbd>Enter</kbd>, and watch what depends on it run again. The same file works anywhere: `dotnet run hello.cs` prints its output in the terminal.

Or try one of the samples, which have sliders, dropdowns and charts:

```sh
git clone https://github.com/muneeb-devp/reverb
cd reverb/samples
reverb edit sales-explorer.cs
```

| Sample | What it shows |
|---|---|
| [sales-explorer.cs](samples/sales-explorer.cs) | A year of sales loaded from a CSV, with a region dropdown, a smoothing slider and charts |
| [loan-calculator.cs](samples/loan-calculator.cs) | Sliders for a loan: payment, balance chart, yearly table. Nice with `reverb run` |
| [github-explorer.cs](samples/github-explorer.cs) | Calls the public GitHub API for a repository and its releases |
| [linq-tour.cs](samples/linq-tour.cs) | LINQ, one idea per cell |

## Writing notebooks

Here's a complete notebook:

```csharp
#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.2.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% [markdown]
// # Sales explorer
// Drag the slider to change the minimum order size.

// %% id=data
var rng = new Random(42);
string[] regions = ["West", "East", "Central"];
var orders = Enumerable.Range(1, 500)
    .Select(i => new Order(i, regions[i % 3], Math.Round(rng.NextDouble() * 1000, 2)))
    .ToList();

// %% id=threshold
var threshold = Ui.Slider("Minimum order", min: 0, max: 1000, value: 400);

// %% id=summary
var big = orders.Where(o => o.Total >= threshold.Value).ToList();
big.GroupBy(o => o.Region)
   .Select(g => new { Region = g.Key, Count = g.Count(), Revenue = Math.Round(g.Sum(o => o.Total), 2) })
   .Dump("Orders above threshold");

// %% types
record Order(int Id, string Region, double Total);
```

In the editor, dragging the slider re-runs `summary` and nothing else. Outside the editor it's a normal program:

```
$ dotnet run sales.cs
== Orders above threshold ==
Region   Count  Revenue
-------  -----  --------
East     98     68284.24
Central  106    74980.33
West     91     65473.05
```

Widgets use their default values there, and output is printed as text.

There's no JSON and no saved output in the file, so changing one line of code is a one-line diff:

![The sales explorer in the Reverb editor, next to the git diff of changing the smoothing slider's default from 7 to 14: one line changed](assets/editor-and-diff.png)

### Cells

Cells are separated by `// %%` lines (the "percent format" that Jupytext, VS Code and Spyder also read).

| Marker | Cell |
|---|---|
| `// %% setup` | `using` directives. There's one, and it comes first |
| `// %% id=name` | A code cell. `name` is how other cells and the editor refer to it |
| `// %% [markdown]` | Markdown, written as `//` comments |
| `// %% types` | Records, classes and other type declarations. There's one, and it comes last |

Code cells can carry a couple of extra attributes:

```csharp
// %% id=report name="Monthly report" hide_code
```

`name` is the label shown in the editor, and `hide_code` folds the code away so only the output shows.

The top of the file holds `#:package` lines (see [Packages](#packages)) and the `// reverb-notebook v1` header. Reverb adds anything missing, including cell ids, the first time it opens a file.

### Output

```csharp
orders.Dump();                  // tables for collections, property grids for objects
orders.Dump("Big orders");      // with a title; Dump returns its argument, so it can end a chain
Md($"**{orders.Count}** orders"); // Markdown
Html("<em>hi</em>");            // raw HTML
Display(image);                 // same as Dump, as a statement
Console.WriteLine("plain text");
```

`Md`, `Html` and `Display` come from `using static Reverb.Notebook;`, which `reverb new` puts in the setup cell.

Register a formatter to control how your own types display:

```csharp
Formatters.Register<Money>(m => Output.FromHtml($"<b>{m.Amount:C}</b>", text: $"{m.Amount:C}"));
```

### Widgets

A widget is a variable. Any cell that reads `.Value` runs again when the widget changes.

| Widget | `.Value` |
|---|---|
| `Ui.Slider("Size", min: 0, max: 100, value: 50, step: 5)` | `double` |
| `Ui.Dropdown("Region", ["West", "East"], value: "East")` | the selected item (any type) |
| `Ui.Text("Name", value: "Ada")` | `string` |
| `Ui.Checkbox("Show totals", value: true)` | `bool` |
| `Ui.Number("Years", 30, min: 1, max: 40)` | the number type you passed (`int`, `decimal`, ...) |
| `Ui.Button("Refresh")` | how many times it's been clicked |

A cell that reads a button's `Value` runs again on every click, which is handy for re-fetching data.

### Charts

```csharp
Chart.Line(sales, x: s => s.Date, y: s => s.Revenue);
Chart.Bar(byProduct, x: p => p.Product, y: p => p.Units, title: "Units by product");
Chart.Scatter(points, x: p => p.Height, y: p => p.Weight, series: p => p.Species);
Chart.VegaLite(json);           // any Vega-Lite spec
```

Axis titles default to the lambda bodies (`Date`, `Revenue`). Pass `xTitle:` and `yTitle:` to change them, and `series:` to split the data into colored series. Charts follow the editor's light or dark theme, and under `dotnet run` they print a one-line summary.

### Packages

Add NuGet packages with `#:package` lines at the top of the file, or from the Packages panel in the editor:

```csharp
#:package Humanizer@2.14.1
```

The first time a set of packages is used, Reverb builds a kernel with them, which takes a few seconds. After that the build is cached in `~/.reverb/kernels`. Your `nuget.config` is respected.

### Data files, arguments, long-running cells

```csharp
var lines = File.ReadAllLines(Path.Combine(Notebook.Folder, "data.csv")); // relative to the notebook
var input = args.Length > 0 ? args[0] : "default.csv";                    // reverb exec nb.cs -- other.csv
await Task.Delay(5000, Notebook.CancellationToken);                       // stops when you press Interrupt
```

## The browser editor

`reverb edit notebook.cs` starts a local server and opens the notebook in your browser. Saving writes the `.cs` file, and nothing else.

| Keys | Where | What it does |
|---|---|---|
| <kbd>Ctrl</kbd>/<kbd>Cmd</kbd>+<kbd>Enter</kbd> | anywhere | Run the cell |
| <kbd>Shift</kbd>+<kbd>Enter</kbd> | anywhere | Run the cell and move to the next one |
| <kbd>Ctrl</kbd>/<kbd>Cmd</kbd>+<kbd>Shift</kbd>+<kbd>Enter</kbd> | anywhere | Run all cells |
| <kbd>Ctrl</kbd>/<kbd>Cmd</kbd>+<kbd>S</kbd> | anywhere | Save now (edits are also saved automatically) |
| <kbd>Esc</kbd> | in a cell | Stop editing and select the cell |
| <kbd>Enter</kbd> | cell selected | Edit the cell |
| <kbd>J</kbd> / <kbd>K</kbd> or arrows | cell selected | Select the next or previous cell |
| <kbd>A</kbd> / <kbd>B</kbd> | cell selected | Add a cell above or below |
| <kbd>D</kbd> <kbd>D</kbd> | cell selected | Delete the cell |

Some other things worth knowing:

- Completions, hover and signature help are Roslyn's, and they see every earlier cell. Hovering a variable tells you which cell defines it.
- Errors show up as you type, under the cell they belong to. Some have a fix button, such as moving a stray `using` into the setup cell.
- With **lazy** ticked in the toolbar, an edit marks dependent cells as stale instead of running them, which helps when cells are slow. The setting is saved in the file's header (`// reverb-notebook v1 lazy`).
- The editor watches the file. Change it in VS Code or Vim, or let a coding agent edit it, and the notebook reloads and re-runs what changed.
- The theme button switches between light, dark and your system setting.

## VS Code

If you live in VS Code, install [Reverb Notebooks](https://marketplace.visualstudio.com/items?itemName=MuneebMughal.reverb-notebooks) and your `.cs` notebooks open as native VS Code notebooks: VS Code's own cells, run buttons and outputs, with Reverb's reactive engine underneath. It needs the `reverb` tool (version 0.2.0 or later) and offers to install it for you. The extension talks to the tool through `reverb serve --stdio`, so it opens no network port. Its source is in the [`vscode`](vscode) folder.

To open a notebook, right-click the `.cs` file and choose Open With, then Reverb Notebook, or click "Open as Reverb notebook" at the top of the file. What you get:

- **Reactive runs.** Running a cell reruns the cells that depend on it. Lazy mode ("Reverb: Toggle Lazy Mode") marks them stale instead.
- **The same outputs as the browser editor.** Text, HTML, Markdown, images and errors, plus Reverb's tables, Vega-Lite charts and widgets, drawn in your VS Code theme. Moving a slider or picking from a dropdown sends the value to the kernel and reruns the cells that read it.
- **Completions, hover and live errors** in the cells. For a `using` or type in the wrong cell, or an anonymous type passed between cells, the lightbulb offers Reverb's fix as one undoable edit.
- **Cell options** in each cell's `…` menu: disable a cell, hide its code, or give it a name.
- **Dependencies.** Under each cell, "uses N cells" and "used by N cells", and "Reverb: Show Dependencies" jumps to them.
- **Packages.** The Reverb Packages view in the Explorer adds, updates and removes a notebook's NuGet packages.
- **Autosave and file watching.** Notebooks save about a second after you stop typing (the `reverb.autoSave` setting turns that off), and edits made to the file outside VS Code show up in the notebook. Saving only changes the bytes you changed.

In a folder you haven't trusted, notebooks open and can be edited, but no kernel starts and no packages are restored. App mode (`reverb run`) is still browser-only. See [vscode/README.md](vscode/README.md) for the details.

## Command line

| Command | What it does |
|---|---|
| `reverb new <file.cs>` | Create a notebook and open it (`--no-edit` to just create it) |
| `reverb edit <file.cs>` | Open the editor |
| `reverb run <file.cs>` | Serve the notebook as an app: outputs and widgets, no code |
| `reverb exec <file.cs> [--json] [-- args]` | Run it in the terminal and print the outputs. Exits 1 if a cell fails |
| `reverb check <file.cs>` | Print compiler and notebook diagnostics. Exits 1 if there are errors |
| `reverb export <file.cs> --html [-o out.html] [--no-code]` | Run it and write a single HTML page that works offline |
| `reverb convert <file.ipynb \| file.dib> [-o out.cs] [--force]` | Convert a Jupyter or Polyglot notebook |
| `reverb serve --stdio` | Used by the VS Code extension |

`new`, `edit` and `run` also take `--port <n>` and `--no-browser`. `run` takes `--host <address>` to serve beyond localhost.

`reverb convert` turns `#r "nuget: ..."` into `#:package`, moves usings into the setup cell and types into the types cell, and prints a warning for anything it couldn't bring over, with the cell it came from.

### Using Reverb in CI

```yaml
- uses: actions/setup-dotnet@v4
  with:
    dotnet-version: 10.0.x
- run: dotnet tool install -g Reverb.Notebooks
- run: reverb check notebooks/report.cs
- run: reverb exec notebooks/report.cs
- run: reverb export notebooks/report.cs --html -o report.html
```

You don't even need the tool to check that a notebook still runs: `dotnet run notebooks/report.cs` does that.

## Rules and diagnostics

Reverb works out what each cell reads and writes from Roslyn's semantic model. For that to be reliable there are a few rules, all reported while you type and by `reverb check`:

| Id | Rule | What to do |
|---|---|---|
| RVB001 | A variable can only be assigned in the cell that declares it | Declare a new variable instead: `var count2 = ...` |
| RVB002 | Anonymous types can't be used across cells | Use a record or a named tuple |
| RVB003 | No top-level `return`, and no `goto` into another cell | Use if/else to skip the rest of a cell, or throw to stop it |
| RVB004 | Usings go in the setup cell, types in the types cell | Click the fix button, or move the line |
| RVB005 | Changing another cell's object (e.g. `list.Add`) won't re-run its readers (warning) | Prefer records, `with` and LINQ |
| RVB006 | A local function can't be used above the cell that declares it | Move the function's cell up, or the function into an earlier cell |
| RVB007 | Some values can't be passed between cells (ref structs, pointers) | Keep that code in one cell, or use a static method in the types cell |

The C# compiler covers the rest. Top-level variables can't be declared twice or used before they're declared, so the order of cells in the file is always an order they can run in. One thing Reverb can't see is state you keep in static fields and singletons, which survives between runs. If something looks off, restart the kernel.

To check the whole thing actually holds, a fuzzer builds random notebooks, makes random edits, and compares the result with a fresh run after every step. CI runs 500 of those sequences on every push.

## Common questions

**A cell didn't re-run after I called `list.Add(...)` on a list from another cell.**
Reverb re-runs a cell when a variable it reads is assigned again. Changing an object in place isn't something it can see, so it warns (RVB005) instead. Prefer records, `with` and LINQ, which produce new values, or run the reading cell yourself.

**Where does a cell's output go when I save?**
Nowhere. The file only has your code, which is why diffs stay small. Outputs live in the editor. To share them, `reverb export notebook.cs --html` writes a single page that works offline.

**Why was the first run with a new package slow?**
Reverb builds a kernel that includes the package, which takes a few seconds. After that the build is cached in `~/.reverb/kernels`.

**Something behaves strangely after I changed a static field or a singleton.**
That state survives between runs and Reverb can't see it. Restart the kernel (the toolbar in the browser editor, "Reverb: Restart Kernel" in VS Code).

**Can I edit a notebook in another editor, or let a coding agent edit it?**
Yes. It's a plain text file, and both the browser editor and the VS Code extension reload it when it changes on disk.

## How it compares

|                                  | Reverb | Polyglot Notebooks | LINQPad | Verso |
|----------------------------------|:------:|:------------------:|:-------:|:-----:|
| Reactive (dependents re-run)     | Yes    | No                 | No      | No    |
| Notebook is a plain `.cs` file   | Yes    | No (`.ipynb`, `.dib`) | No (`.linq`) | No |
| Runs with `dotnet run`, no tool installed | Yes | No           | No      | No    |
| Open source                      | Yes    | Yes                | No      | Yes   |
| Actively developed               | Yes    | Deprecated in 2026 | Yes     | Yes   |

Polyglot Notebooks (.NET Interactive) runs cells REPL-style, so what's in memory depends on the order you ran things. It does support many languages, which Reverb doesn't. Jupyter with a C# kernel uses the same engine. `reverb convert` brings the C# cells from either one over.

LINQPad is a great C# scratchpad with excellent database support, but it isn't reactive or open source, and its files need LINQPad to run.

Verso is an open-source notebook in the Jupyter style, with cells run in the order you choose and its own file format.

## Security

The editor listens on 127.0.0.1 only, and the address it opens contains a random token. Connections without the token are refused, and so are requests from other websites, so a page you visit can't run code through your notebook.

`reverb serve --stdio`, which the VS Code extension uses, talks over stdin and stdout and opens no port. In a VS Code folder you haven't trusted, the extension starts no kernel.

`reverb run --host <address>` serves an app beyond localhost, for example to show it on your network. It prints a warning, because anyone who can reach that address can use the widgets, and the cells run as you.

Reverb sends no telemetry. To report a security problem, see [SECURITY.md](SECURITY.md).

## How it works

Every notebook is compiled twice. The whole file is compiled as one program, the way `dotnet run` sees it, and that gives the diagnostics and the dependency graph, so the editor never disagrees with the compiler. Then each cell is compiled on its own into a small assembly that reads its inputs from a value store and writes its outputs back. Cells you haven't changed aren't recompiled, so with a warm kernel a simple edit shows its output in about 27 ms (on an M1 Pro).

The cell assemblies load into collectible `AssemblyLoadContext`s, so an edited cell's old code is unloaded rather than piling up. Your code runs in a separate kernel process, so an infinite loop or a crash doesn't take the editor down.

## Building from source

You'll need the .NET 10 SDK and Node.js 22 or newer.

```sh
git clone https://github.com/muneeb-devp/reverb
cd reverb
dotnet build Reverb.slnx
dotnet test Reverb.slnx
```

The build runs `npm` to bundle the editor and embeds it in the CLI. When you're only working on the engine, `dotnet build -p:SkipFrontendBuild=true` skips that step and reuses whatever is in `frontend/dist`.

Run the CLI straight from the repo:

```sh
dotnet run --project src/Reverb.Cli -- edit samples/sales-explorer.cs
dotnet run --project src/Reverb.Cli -- exec tests/fixtures/sales.cs
dotnet run --project src/Reverb.Cli -- check tests/fixtures/diag-errors.cs
```

The browser tests use Playwright against the real CLI, so build first:

```sh
cd tests/e2e
npm ci
npx playwright install chromium
npx playwright test
```

| Folder | What's in it |
|---|---|
| `src/Reverb.Format` | Reading and writing the `.cs` notebook format, upgrades, conversion |
| `src/Reverb.Analysis` | Roslyn analysis: dependencies, diagnostics, code fixes |
| `src/Reverb.Kernel` | Compiles and runs cells in the kernel process |
| `src/Reverb.Runtime` | The small library notebooks reference: `Dump`, `Ui`, `Chart` |
| `src/Reverb.Server` | The editor's web server, and `reverb serve --stdio` |
| `src/Reverb.Cli` | The `reverb` command |
| `frontend` | The editor (React and CodeMirror) |
| `vscode` | The VS Code extension |

The extension has its own tests:

```sh
cd frontend && npm ci && cd ..   # the extension's renderer uses the editor's components
cd vscode
npm ci
npm test
npm run test:integration
```

Releases are published from tags: pushing `v1.2.3` runs [the release workflow](.github/workflows/release.yml), which tests, packs, smoke-tests the installed tool and publishes both packages to NuGet. Pushing `vscode-v1.2.3` publishes the VS Code extension; [vscode/PUBLISHING.md](vscode/PUBLISHING.md) covers the one-time setup and the steps.

## Contributing

Issues and pull requests are welcome. [CONTRIBUTING.md](CONTRIBUTING.md) explains how to set up, how changes get merged (everything goes through a pull request, `main` is protected) and what a pull request needs. If the consistency fuzzer finds a bug, the fix belongs in the engine, never in a looser check.

## License

[MIT](LICENSE)
