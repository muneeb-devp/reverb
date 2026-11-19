// The commands in the palette and the CodeLens on notebook files opened as text.
import * as vscode from "vscode";
import { hasFlag, nameOf, withFlag, withName, type CellFlag } from "./cellAttrs";
import type { CellCollapse } from "./cellCollapse";
import type { ReverbController } from "./controller";
import type { ReverbCellMetadata } from "./metadata";
import type { FormatTemplated } from "./protocol";
import type { ReverbProcess } from "./reverbProcess";
import type { NotebookSession } from "./session";

/** The prompts the commands use. The integration tests replace them, since they can't click a dialog. */
export interface Ui {
  showSaveDialog(options: vscode.SaveDialogOptions): Thenable<vscode.Uri | undefined>;
  showInputBox(options: vscode.InputBoxOptions): Thenable<string | undefined>;
  showQuickPick(items: string[], options: vscode.QuickPickOptions): Thenable<string | undefined>;
  showWarningMessage(message: string, options: vscode.MessageOptions, ...items: string[]): Thenable<string | undefined>;
}

export const defaultUi: Ui = {
  showSaveDialog: (o) => vscode.window.showSaveDialog(o),
  showInputBox: (o) => vscode.window.showInputBox(o),
  showQuickPick: (items, o) => vscode.window.showQuickPick(items, o),
  showWarningMessage: (message, o, ...items) => vscode.window.showWarningMessage(message, o, ...items),
};

const NOTEBOOK_MARKER = /^\/\/ reverb-notebook v1\b/m;

export function registerCommands(
  controller: ReverbController,
  getProcess: () => Promise<ReverbProcess | undefined>,
  ui: Ui,
  collapse?: CellCollapse,
): vscode.Disposable[] {
  const reg = vscode.commands.registerCommand;

  /** The session of the notebook the command is for (the one in the editor), after the trust check. */
  async function session(arg: unknown): Promise<NotebookSession | undefined> {
    const doc = notebookFor(arg);
    if (!doc) {
      void controller.showError("Open a Reverb notebook first, then run this command.");
      return undefined;
    }
    return controller.requireSession(doc);
  }

  /** A session that has told us its packages, which a package command needs. */
  async function sessionWithPackages(arg: unknown): Promise<(NotebookSession & { packages: NonNullable<NotebookSession["packages"]> }) | undefined> {
    const s = await session(arg);
    if (!s) return undefined;
    if (!s.packages) {
      void controller.showError(`Reverb hasn't listed the packages of ${baseName(s.path)} yet. Wait a moment and run the command again.`);
      return undefined;
    }
    return s as NotebookSession & { packages: NonNullable<NotebookSession["packages"]> };
  }

  /**
   * The code cells a cell command is for: the cell from the menu, else the cells selected in the
   * editor. Says why and returns none when there isn't one it can change.
   */
  function codeCells(arg: unknown, what: string): vscode.NotebookCell[] {
    const cells = cellsFor(arg);
    if (cells.length === 0) {
      void controller.showError("Select a code cell in a Reverb notebook first, then run this command.");
      return [];
    }
    const code = cells.filter((c) => c.kind === vscode.NotebookCellKind.Code && attrsMeta(c).kind === "code");
    if (code.length === 0) {
      const kind = cells[0].kind === vscode.NotebookCellKind.Markup ? "A Markdown cell" : attrsMeta(cells[0]).kind === "types" ? "The types cell" : "The setup cell";
      void controller.showError(`${kind} can't be ${what}. Select a code cell instead.`);
    }
    return code;
  }

  async function setAttrs(cells: vscode.NotebookCell[], attrs: (c: vscode.NotebookCell) => ReverbCellMetadata["attrs"]): Promise<boolean> {
    const edit = new vscode.WorkspaceEdit();
    const byNotebook = new Map<vscode.NotebookDocument, vscode.NotebookEdit[]>();
    for (const cell of cells) {
      const meta = { ...cell.metadata, reverb: { ...attrsMeta(cell), attrs: attrs(cell) } };
      byNotebook.set(cell.notebook, [...(byNotebook.get(cell.notebook) ?? []), vscode.NotebookEdit.updateCellMetadata(cell.index, meta)]);
    }
    for (const [nb, edits] of byNotebook) edit.set(nb.uri, edits);
    if (await vscode.workspace.applyEdit(edit)) return true;
    void controller.showError("Reverb couldn't change the cell. Check the notebook isn't read-only and try again.");
    return false;
  }

  async function toggleFlag(arg: unknown, key: CellFlag, what: string) {
    const cells = codeCells(arg, what);
    if (cells.length === 0) return;
    const on = !hasFlag(attrsMeta(cells[0]).attrs, key);
    if (!(await setAttrs(cells, (c) => withFlag(attrsMeta(c).attrs, key, on)))) return;
    if (key === "hide_code") for (const c of cells) await collapse?.set(c, on);
  }

  return [
    reg("reverb.toggleDisabled", (arg?: unknown) => toggleFlag(arg, "disabled", "disabled")),
    reg("reverb.toggleHideCode", (arg?: unknown) => toggleFlag(arg, "hide_code", "hidden")),

    reg("reverb.nameCell", async (arg?: unknown) => {
      const cell = codeCells(arg, "named")[0];
      if (!cell) return;
      const current = nameOf(attrsMeta(cell).attrs);
      const answer = await ui.showInputBox({
        title: "Name Cell",
        prompt: "A name for this cell. Leave it empty to remove the name.",
        placeHolder: "load",
        value: current,
        validateInput: (v) => (/[\r\n]/.test(v) ? "A name can't have line breaks. Use a single line." : undefined),
      });
      if (answer === undefined) return; // cancelled
      const name = answer.trim() || undefined;
      if (name === current) return;
      await setAttrs([cell], (c) => withName(attrsMeta(c).attrs, name));
    }),

    reg("reverb.openAsNotebook", async (arg?: unknown) => {
      const uri = arg instanceof vscode.Uri ? arg : vscode.window.activeTextEditor?.document.uri;
      if (!uri) {
        void controller.showError("Open a .cs file first, then run Open as Reverb Notebook.");
        return;
      }
      await vscode.commands.executeCommand("vscode.openWith", uri, "reverb");
    }),

    reg("reverb.newNotebook", async () => {
      const folder = vscode.workspace.workspaceFolders?.[0]?.uri;
      const chosen = await ui.showSaveDialog({
        title: "New Reverb Notebook",
        saveLabel: "Create",
        filters: { "C# notebook": ["cs"] },
        defaultUri: folder ? vscode.Uri.joinPath(folder, "notebook.cs") : undefined,
      });
      if (!chosen) return;
      const hasExtension = chosen.path.toLowerCase().endsWith(".cs");
      const uri = hasExtension ? chosen : chosen.with({ path: `${chosen.path}.cs` });
      // The save dialog asked about replacing the path it returned, but not about the one with .cs added.
      if (!hasExtension && (await exists(uri))) {
        const replace = "Replace";
        const answer = await ui.showWarningMessage(`Replace ${baseName(uri.path)}?`, { modal: true, detail: `${baseName(uri.path)} already exists. A new notebook replaces what's in it.` }, replace);
        if (answer !== replace) return;
      }
      try {
        const proc = await getProcess();
        if (!proc) return; // it has said why it couldn't start
        const { text } = await proc.request<FormatTemplated>({ type: "format.template" });
        await vscode.workspace.fs.writeFile(uri, new TextEncoder().encode(text));
        await vscode.commands.executeCommand("vscode.openWith", uri, "reverb");
      } catch (e) {
        void controller.showError(`Reverb couldn't create ${baseName(uri.path)}: ${(e as Error).message} Check the path is writable and try again.`);
      }
    }),

    reg("reverb.restartKernel", async (arg?: unknown) => {
      (await session(arg))?.restart();
    }),

    reg("reverb.toggleLazy", async (arg?: unknown) => {
      const s = await session(arg);
      if (!s) return;
      if (s.lazy === undefined) {
        void controller.showError(`Reverb hasn't opened ${baseName(s.path)} yet, so lazy mode wasn't changed. Wait a moment and run the command again.`);
        return;
      }
      s.setLazy(!s.lazy);
    }),

    reg("reverb.addPackage", async (arg?: unknown) => {
      const s = await sessionWithPackages(arg);
      if (!s) return;
      const name = (
        await ui.showInputBox({
          title: "Add Package",
          prompt: "Package name, as on nuget.org",
          placeHolder: "Humanizer",
          validateInput: (v) => (v.trim() ? undefined : "Enter a package name."),
        })
      )?.trim();
      if (!name) return;
      if (name.toLowerCase() === "reverb.runtime") {
        void controller.showError("Reverb.Runtime is managed by Reverb, so it can't be added or removed here.");
        return;
      }
      const existing = s.packages.find((p) => p.name.toLowerCase() === name.toLowerCase());
      const version = (
        await ui.showInputBox({
          title: `Add Package: ${name}`,
          prompt: `Version of ${name} ('*' takes the latest)`,
          placeHolder: "2.14.1",
          value: existing?.version,
          validateInput: (v) => (v.trim() ? undefined : `Enter a version of ${name}, for example 2.14.1, or * for the latest.`),
        })
      )?.trim();
      if (!version) return;
      const rest = s.packages.map((p) => (p === existing ? { name, version } : p));
      s.setPackages(existing ? rest : [...rest, { name, version }]);
    }),

    reg("reverb.removePackage", async (arg?: unknown) => {
      const s = await sessionWithPackages(arg);
      if (!s) return;
      if (s.packages.length === 0) {
        void controller.showError(`${baseName(s.path)} has no packages to remove.`);
        return;
      }
      const name = await ui.showQuickPick(
        s.packages.map((p) => p.name),
        { title: "Remove Package", placeHolder: "Package to remove" },
      );
      if (name) s.setPackages(s.packages.filter((p) => p.name !== name));
    }),

    vscode.languages.registerCodeLensProvider(
      { scheme: "file", pattern: "**/*.cs" },
      {
        provideCodeLenses(doc) {
          if (!NOTEBOOK_MARKER.test(doc.getText())) return [];
          const range = new vscode.Range(0, 0, 0, 0);
          return [new vscode.CodeLens(range, { title: "Open as Reverb notebook", command: "reverb.openAsNotebook", arguments: [doc.uri] })];
        },
      },
    ),
  ];
}

function attrsMeta(cell: vscode.NotebookCell): ReverbCellMetadata {
  const m = cell.metadata.reverb as Partial<ReverbCellMetadata> | undefined;
  return { ...m, kind: m?.kind ?? "code", attrs: Array.isArray(m?.attrs) ? m.attrs : [] };
}

/** The cell a command was given by a cell menu, else the cells selected in the notebook in the editor. */
function cellsFor(arg: unknown): vscode.NotebookCell[] {
  if (typeof arg === "object" && arg !== null && "notebook" in arg && "metadata" in arg && "index" in arg) {
    const cell = arg as vscode.NotebookCell;
    return cell.notebook.notebookType === "reverb" ? [cell] : [];
  }
  const editor = vscode.window.activeNotebookEditor;
  if (editor?.notebook.notebookType !== "reverb") return [];
  return editor.selections.flatMap((r) => editor.notebook.getCells(r));
}

function notebookFor(arg: unknown): vscode.NotebookDocument | undefined {
  if (arg instanceof vscode.Uri) {
    const key = arg.toString();
    return vscode.workspace.notebookDocuments.find((d) => d.uri.toString() === key && d.notebookType === "reverb");
  }
  const doc = vscode.window.activeNotebookEditor?.notebook;
  return doc?.notebookType === "reverb" ? doc : undefined;
}

async function exists(uri: vscode.Uri): Promise<boolean> {
  try {
    await vscode.workspace.fs.stat(uri);
    return true;
  } catch {
    return false;
  }
}

function baseName(path: string): string {
  return path.slice(Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\")) + 1);
}
