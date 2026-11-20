# Reverb.Runtime

The library that [Reverb](https://github.com/muneeb-devp/reverb) notebooks reference: `Dump`, Markdown and HTML output, widgets and charts. It has no dependencies.

You don't usually add it yourself. `reverb new` writes this line at the top of every notebook:

```csharp
#:package Reverb.Runtime@0.2.0
```

To create and edit notebooks, install the tool, [Reverb.Notebooks](https://www.nuget.org/packages/Reverb.Notebooks):

```sh
dotnet tool install -g Reverb.Notebooks
```

## Two modes

- **In the Reverb editor**, output is rich (tables, charts, HTML) and widgets are live.
- **With `dotnet run`**, there's no editor. Output prints to the console as text, and widgets use their default values. The same notebook works both ways.

## Output

```csharp
using Reverb;
using static Reverb.Notebook;

orders.Dump();                    // a table for collections, a property list for objects
orders.Dump("Big orders");        // with a title; Dump returns its argument
Md($"**{orders.Count}** orders"); // Markdown
Html("<em>hi</em>");              // raw HTML
```

Control how your own types display:

```csharp
Formatters.Register<Money>(m => Output.FromHtml($"<b>{m.Amount:C}</b>", text: $"{m.Amount:C}"));
```

## Widgets

A widget is a variable. In the editor, any cell that reads `.Value` runs again when the widget changes.

| Widget | `.Value` |
|---|---|
| `Ui.Slider("Size", min: 0, max: 100, value: 50, step: 5)` | `double` |
| `Ui.Dropdown("Region", ["West", "East"], value: "East")` | the selected item |
| `Ui.Text("Name", value: "Ada")` | `string` |
| `Ui.Checkbox("Show totals", value: true)` | `bool` |
| `Ui.Number("Years", 30, min: 1, max: 40)` | the number type you passed |
| `Ui.Button("Refresh")` | how many times it's been clicked |

## Charts

```csharp
Chart.Line(sales, x: s => s.Date, y: s => s.Revenue);
Chart.Bar(byProduct, x: p => p.Product, y: p => p.Units, title: "Units by product");
Chart.Scatter(points, x: p => p.Height, y: p => p.Weight, series: p => p.Species);
Chart.VegaLite(json);             // any Vega-Lite spec
```

Under `dotnet run`, charts print a one-line summary.

## Other helpers

```csharp
var path = Path.Combine(Notebook.Folder, "data.csv");     // a file next to the notebook
await Task.Delay(5000, Notebook.CancellationToken);       // stops when you press Interrupt
```

The full guide is at [github.com/muneeb-devp/reverb](https://github.com/muneeb-devp/reverb). MIT licensed.
