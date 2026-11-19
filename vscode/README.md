# Reverb for VS Code

Reverb is a reactive notebook for C#. A notebook is a plain `.cs` file that also runs with `dotnet run`, and when you run a cell, the cells that depend on it run again too. This extension opens those files as native VS Code notebooks, so you get the usual notebook editor, kernel picker and outputs without leaving VS Code.

## Install

You need the .NET 10 SDK and the Reverb command line tool:

    dotnet tool install -g Reverb.Notebooks

If the tool isn't installed when you open a notebook, the extension offers to install it for you. If it lives somewhere unusual, set `reverb.path` to the executable.

## Open a notebook

Reverb notebooks are `.cs` files that start with `// reverb-notebook v1`. There are three ways to open one as a notebook:

- Right-click the file and choose Open With, then Reverb Notebook.
- Click the "Open as Reverb notebook" CodeLens at the top of the file when you have it open as text.
- Run "Open as Reverb Notebook" from the Command Palette.

"Reverb: New Notebook" creates an empty one.

## What works in this version

- Running cells. In a trusted folder the kernel starts when you open the notebook, but no cell runs until you run it.
- Reactive reruns. Editing a cell doesn't run anything. Running a cell also reruns the cells that depend on it. "Reverb: Toggle Lazy Mode" marks those cells stale instead, until you run them.
- Outputs: text, HTML, Markdown, images and errors, including what the cell writes to the console. Reverb's tables, Vega-Lite charts and widgets (slider, dropdown, text, checkbox, number and button) are drawn like they are in the browser editor, in your VS Code theme. Changing a widget sends its value to the notebook's kernel and reruns the cells that read it. Charts have no actions menu, since it can send a chart spec to an outside site.
- Compiler diagnostics and completions in the cell editors. For two diagnostics, RVB004 (code in the wrong cell) and RVB002 (an anonymous type passed between cells), the lightbulb offers a fix. It's one undoable edit, and the cells it changes run again.
- Cell options in the cell's `...` menu and the Command Palette: "Reverb: Toggle Disabled", "Reverb: Toggle Hide Code" (which collapses the cell's input) and "Reverb: Name Cell...".
- Dependencies. Under each cell, "uses N cells" and "used by N cells" show how it's connected, with the cell names in the tooltip. "Reverb: Show Dependencies" jumps to one of them.
- NuGet packages. While a Reverb notebook is active, the Reverb Packages view in the Explorer lists them and lets you add, change the version of and remove them. "Reverb: Add Package" and "Reverb: Remove Package" still work from the Command Palette. "Reverb: Restart Kernel" restarts the kernel.
- Autosave. Notebooks save about a second after you stop typing, and right away after you add, delete or move cells or change packages. Turn it off with the `reverb.autoSave` setting, and the notebook then behaves like any other unsaved file. With autosave off, changes made to the file on disk only show up while the notebook has no unsaved edits.
- Restricted Mode. In a folder you haven't trusted, notebooks open and you can read and edit them, but no kernel starts, no packages are restored, no widget values are sent and there are no completions or fixes. Trust the folder to turn those on.

## What's not here

App mode (`reverb run`) is browser-only, and the extension doesn't work in vscode.dev, since the kernel needs a local process.

## Privacy

Reverb sends no telemetry.

## Links

The project, the notebook format and the browser editor are at https://github.com/muneeb-devp/reverb.
