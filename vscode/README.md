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
- Outputs: text, HTML, Markdown, images and errors, including what the cell writes to the console. Reverb's own table and object views show as plain text for now.
- Compiler diagnostics and completions in the cell editors.
- "Reverb: Add Package" and "Reverb: Remove Package" for NuGet packages, and "Reverb: Restart Kernel".
- Autosave. Notebooks save about a second after you stop typing, and right away after you add, delete or move cells or change packages. Turn it off with the `reverb.autoSave` setting, and the notebook then behaves like any other unsaved file. With autosave off, changes made to the file on disk only show up while the notebook has no unsaved edits.
- Restricted Mode. In a folder you haven't trusted, notebooks open and you can read and edit them, but no kernel starts, no packages are restored and there are no completions. Trust the folder to turn those on.

## What's coming

Widgets, charts, the dependency view and fix buttons for diagnostics are planned for a later version. Most of these already exist in the browser editor that comes with the Reverb tool.

## Privacy

Reverb sends no telemetry.

## Links

The project, the notebook format and the browser editor are at https://github.com/muneeb-devp/reverb.
