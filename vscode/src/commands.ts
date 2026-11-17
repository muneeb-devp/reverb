// The commands in the palette and the CodeLens on notebook files opened as text.
import * as vscode from "vscode";
import type { ReverbController } from "./controller";
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

  return [
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
