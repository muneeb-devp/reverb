# Reverb

Reactive notebooks for C#. A notebook is an ordinary `.cs` file: change a cell and the cells that use its results run again. The same file runs with `dotnet run`, diffs cleanly in Git, and needs nothing else installed to work.

![Reverb demo: a slider and a dropdown redrawing charts, and a code change updating the cells after it](https://raw.githubusercontent.com/muneeb-devp/reverb/main/assets/demo.gif)

## Install

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
dotnet tool install -g Reverb.Notebooks
reverb new hello.cs
```

`reverb new` creates a starter notebook and opens it in your browser.

## What a notebook looks like

```csharp
#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.1
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=data
var orders = Enumerable.Range(1, 500)
    .Select(i => new Order(i, i % 2 == 0 ? "West" : "East", i * 1.5))
    .ToList();

// %% id=threshold
var threshold = Ui.Slider("Minimum order", min: 0, max: 750, value: 400);

// %% id=summary
orders.Where(o => o.Total >= threshold.Value)
      .GroupBy(o => o.Region)
      .Select(g => new { Region = g.Key, Count = g.Count() })
      .Dump("Orders above threshold");

// %% types
record Order(int Id, string Region, double Total);
```

In the editor, moving the slider re-runs `summary` and nothing else. With `dotnet run`, the slider uses its default value and the table prints as text.

## What you get

- Cells re-run when something they depend on changes, and only then
- No hidden state: delete a cell and its variables are gone too
- Tables, charts, Markdown, HTML and images from `.Dump()`
- Sliders, dropdowns, text boxes and buttons that are plain variables, with no callbacks
- Completions, hover and live errors from Roslyn
- NuGet packages with `#:package` lines
- The editor watches the file, so edits from VS Code, Vim or a coding agent show up live

## Commands

| Command | What it does |
|---|---|
| `reverb new <file.cs>` | Create a notebook and open it |
| `reverb edit <file.cs>` | Open the editor |
| `reverb run <file.cs>` | Serve the notebook as an app: outputs and widgets, no code |
| `reverb exec <file.cs>` | Run it in the terminal and print the outputs |
| `reverb check <file.cs>` | Print compiler and notebook diagnostics |
| `reverb export <file.cs> --html` | Write a single HTML page that works offline |
| `reverb convert <file.ipynb \| file.dib>` | Convert a Jupyter or Polyglot notebook |
| `reverb serve --stdio` | Used by the VS Code extension |

## More

The full guide, samples and source are on GitHub: [github.com/muneeb-devp/reverb](https://github.com/muneeb-devp/reverb). Bug reports and ideas are welcome in the [issues](https://github.com/muneeb-devp/reverb/issues).

MIT licensed. Reverb sends no telemetry.
