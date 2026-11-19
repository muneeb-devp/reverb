// The "Reverb Packages" view in the Explorer: the packages of the notebook in the active editor.
import * as vscode from "vscode";
import type { ReverbController } from "./controller";
import { packageItems } from "./packages";

/** A package row. The commands on it get this as their argument. */
export class PackageNode extends vscode.TreeItem {
  constructor(
    readonly packageName: string,
    description: string,
    readonly notebook: vscode.Uri,
  ) {
    super(packageName, vscode.TreeItemCollapsibleState.None);
    this.description = description;
    this.contextValue = "reverb.package";
  }
}

export class PackagesView implements vscode.TreeDataProvider<PackageNode>, vscode.Disposable {
  private changed = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this.changed.event;
  private disposables: vscode.Disposable[] = [];
  private active = false;

  constructor(private controller: ReverbController) {
    this.disposables.push(
      this.changed,
      vscode.window.registerTreeDataProvider("reverb.packages", this),
      controller.onDidChangeSession(() => this.changed.fire()),
      vscode.window.onDidChangeActiveNotebookEditor(() => this.activeChanged()),
      vscode.workspace.onDidGrantWorkspaceTrust(() => this.changed.fire()),
    );
    this.activeChanged();
  }

  /** The context key that shows the view is set whenever the active notebook editor changes. */
  private activeChanged(): void {
    this.active = vscode.window.activeNotebookEditor?.notebook.notebookType === "reverb";
    void vscode.commands.executeCommand("setContext", "reverb.notebookActive", this.active);
    this.changed.fire();
  }

  get notebookActive(): boolean {
    return this.active;
  }

  getTreeItem(node: PackageNode): vscode.TreeItem {
    return node;
  }

  getChildren(node?: PackageNode): PackageNode[] {
    if (node) return [];
    const doc = vscode.window.activeNotebookEditor?.notebook;
    if (doc?.notebookType !== "reverb") return [];
    const packages = this.controller.session(doc.uri)?.packages ?? [];
    return packageItems(packages).map((i) => new PackageNode(i.label, i.description, doc.uri));
  }

  dispose(): void {
    for (const d of this.disposables) d.dispose();
  }
}
