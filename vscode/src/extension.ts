import * as vscode from "vscode";
import * as fs from "node:fs";
import { ReverbProcess, ReverbTooOldError, findReverb, INSTALL_COMMAND, UPDATE_COMMAND } from "./reverbProcess";
import { ReverbSerializer } from "./serializer";
import { ReverbController } from "./controller";
import type { NotebookSession } from "./session";
import { defaultUi, registerCommands, type Ui } from "./commands";
import { CellCollapse } from "./cellCollapse";
import { ReverbStatusItems } from "./statusItems";
import { AutoSave } from "./autoSave";
import { registerLanguageServices, ReverbDiagnostics } from "./language";
import { handleWidgetMessage, registerWidgets } from "./widgets";
import { registerCodeFixes } from "./codeFixes";
import { PackagesView } from "./packagesView";

let output: vscode.OutputChannel;
let process_: Promise<ReverbProcess | undefined> | undefined;
let started: ReverbProcess | undefined;
/** The types of the messages sent to reverb. Only kept for the integration tests. */
let sent: string[] | undefined;

/** What activate returns when the integration tests run the extension, and only then. */
export interface ReverbApi {
  session(uri: vscode.Uri): NotebookSession | undefined;
  processId(): number | undefined;
  /** The type of every message sent to reverb so far, oldest first. */
  sentTypes(): readonly string[];
  /** Runs what happens when VS Code grants workspace trust, as if it were granted. */
  simulateTrustGranted(): Promise<void>;
  /** Calls the controller's execute handler, skipping the trust prompt VS Code shows before it. */
  executeCells(notebook: vscode.NotebookDocument, cells: vscode.NotebookCell[]): Promise<void>;
  /** The last error messages shown, newest last. */
  readonly errors: readonly string[];
  /** The prompts the commands use. Tests replace them. */
  readonly ui: Ui;
  /** Sends a renderer message to the notebook's kernel as the widget handler does. True when it was sent. */
  handleWidgetMessage(uri: vscode.Uri, message: unknown): boolean;
  /** The status bar text under a cell, or undefined when it has none. */
  cellStatus(cell: vscode.NotebookCell): string | undefined;
  /** The name shown under a cell, or undefined when it has none. */
  cellName(cell: vscode.NotebookCell): string | undefined;
  /** The dependency items under a cell: text and tooltip. */
  cellDependencies(cell: vscode.NotebookCell): { text: string; tooltip: string }[];
  /** Whether the last request for the cell's input was to collapse (true) or expand (false) it, or undefined when there was none. */
  inputCollapseRequested(cell: vscode.NotebookCell): boolean | undefined;
  /** The packages view: its rows, the rows as the item buttons get them, and whether its context key is set. */
  readonly packagesView: {
    items(): { label: string; description: string }[];
    nodes(): unknown[];
    notebookActive(): boolean;
  };
  /** The kernel item for the active notebook, or undefined when it's hidden. */
  kernelStatus(): { text: string; tooltip: string } | undefined;
}

export function activate(context: vscode.ExtensionContext): ReverbApi | undefined {
  output = vscode.window.createOutputChannel("Reverb");
  if (context.extensionMode === vscode.ExtensionMode.Test) sent = [];
  context.subscriptions.push(output, {
    dispose: () => void process_?.then((p) => p?.dispose()),
  });
  // Outputs aren't saved in the file. Every metadata key is part of the file, so none are transient.
  context.subscriptions.push(
    vscode.workspace.registerNotebookSerializer("reverb", new ReverbSerializer(requireReverb), { transientOutputs: true }),
  );
  const diagnostics = new ReverbDiagnostics();
  const controller = new ReverbController(getReverb, (line) => output.appendLine(line), diagnostics);
  const status = new ReverbStatusItems(controller);
  const ui: Ui = { ...defaultUi };
  const autoSave = new AutoSave((line) => output.appendLine(line));
  const collapse = new CellCollapse((line) => output.appendLine(line));
  const packagesView = new PackagesView(controller);
  context.subscriptions.push(
    controller,
    collapse,
    diagnostics,
    status,
    packagesView,
    autoSave,
    registerWidgets(controller),
    ...registerCommands(controller, getReverb, ui, collapse),
    ...registerLanguageServices((uri) => controller.session(uri)),
    ...registerCodeFixes(controller, diagnostics),
  );
  if (context.extensionMode !== vscode.ExtensionMode.Test) return undefined;
  return {
    session: (uri) => controller.session(uri),
    processId: () => controller.process?.pid ?? started?.pid,
    sentTypes: () => sent ?? [],
    simulateTrustGranted: async () => controller.simulateTrust(),
    executeCells: async (notebook, cells) => void (await controller.controller.executeHandler(cells, notebook, controller.controller)),
    errors: controller.errors,
    ui,
    handleWidgetMessage: (uri, message) => handleWidgetMessage(controller, uri, message),
    cellStatus: (cell) => status.cellText(cell),
    cellName: (cell) => status.nameText(cell),
    cellDependencies: (cell) => status.dependencyTexts(cell),
    inputCollapseRequested: (cell) => collapse.requestedFor(cell),
    kernelStatus: () => status.kernelText(),
    packagesView: {
      items: () => packagesView.getChildren().map((n) => ({ label: String(n.label), description: String(n.description) })),
      nodes: () => packagesView.getChildren(),
      notebookActive: () => packagesView.notebookActive,
    },
  };
}

async function requireReverb(): Promise<ReverbProcess> {
  const p = await getReverb();
  if (!p) throw new Error("Reverb couldn't start. Follow the notification or check the Reverb output panel, then reopen the notebook.");
  return p;
}

/** Starts reverb the first time something needs it. Returns undefined after telling the user why it can't. */
export function getReverb(): Promise<ReverbProcess | undefined> {
  return (process_ ??= startReverb().then((p) => {
    if (!p) process_ = undefined;
    else {
      started = p;
      p.onExit(() => {
        process_ = undefined;
        if (started === p) started = undefined;
      });
    }
    return p;
  }));
}

async function startReverb(): Promise<ReverbProcess | undefined> {
  const setting = vscode.workspace.getConfiguration("reverb").get<string>("path");
  const command = findReverb(setting, globalThis.process.env, fs.existsSync);
  if (!command) {
    await offer("Reverb isn't installed. It needs the .NET 10 SDK.", "Install", INSTALL_COMMAND);
    return undefined;
  }
  try {
    return await ReverbProcess.start(
      { command, args: ["serve", "--stdio"], cwd: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath, log: (l) => output.appendLine(l), onSend: (t) => sent?.push(t) },
    );
  } catch (e) {
    const message = (e as Error).message;
    if (e instanceof ReverbTooOldError) await offer(message, "Update", UPDATE_COMMAND);
    else void vscode.window.showErrorMessage(message);
    return undefined;
  }
}

async function offer(message: string, button: string, command: string) {
  const choice = await vscode.window.showErrorMessage(message, button);
  if (choice === button) {
    const terminal = vscode.window.createTerminal("Reverb");
    terminal.show();
    terminal.sendText(command);
  }
}

export function deactivate() {}
