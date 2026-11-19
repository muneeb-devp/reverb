import * as assert from "node:assert";
import * as fs from "node:fs";
import * as vscode from "vscode";
import { defaultUi } from "../../src/commands";
import type { ReverbApi } from "../../src/extension";
import { reverbExtension, closeAll, notebookText, openReverbNotebook, setAutoSave, waitFor, workspaceFile } from "./helpers";

let fileCount = 0;

function api(): ReverbApi {
  const ext = reverbExtension<ReverbApi>();
  assert.ok(ext?.isActive, "the extension should be active once a notebook is open");
  assert.ok(ext.exports, "the extension only returns its test API in test mode");
  return ext.exports;
}

async function open(text: string): Promise<vscode.NotebookDocument> {
  const file = workspaceFile(`packages-view-${++fileCount}.cs`);
  fs.writeFileSync(file, text);
  const nb = await openReverbNotebook(file);
  await waitFor(() => api().session(nb.uri)?.kernelState === "ready", "the kernel to be ready");
  return nb;
}

const shown = () => api().packagesView.items().map((i) => `${i.label} ${i.description}`);

describe("packages view", () => {
  before(() => setAutoSave(false));
  after(() => setAutoSave(undefined));
  afterEach(async () => {
    Object.assign(api().ui, defaultUi);
    await closeAll();
  });

  it("is shown while a Reverb notebook is active, and not otherwise", async () => {
    await open(notebookText(["var a = 1;"]));
    await waitFor(() => api().packagesView.notebookActive(), "the notebook-active context key to be set");
    await closeAll();
    await waitFor(() => !api().packagesView.notebookActive(), "the context key to clear once no notebook is open");
  });

  it("lists the notebook's packages", async () => {
    await open(notebookText(["var a = 1;"]).replace("#:package Reverb.Runtime@0.1.0\n", "#:package Reverb.Runtime@0.1.0\n#:package Humanizer@2.14.1\n"));
    await waitFor(() => shown().join() === "Humanizer 2.14.1", "the package in the view");
  });

  it("adds, changes the version of, and removes a package, changing only its line", async () => {
    const nb = await open(notebookText(["var a = 1;"]));
    assert.deepStrictEqual(shown(), []);
    api().ui.showInputBox = async (o) => (/name/i.test(o?.prompt ?? "") ? "Humanizer" : "2.14.1");
    await vscode.commands.executeCommand("reverb.addPackage");
    await waitFor(() => shown().join() === "Humanizer 2.14.1", "the added package in the view");
    assert.ok(await nb.save());
    await waitFor(() => api().session(nb.uri)?.kernelState === "ready", "the rebuilt kernel");
    const afterAdd = fs.readFileSync(nb.uri.fsPath, "utf8");
    assert.match(afterAdd, /^#:package Humanizer@2\.14\.1$/m);

    // Change Version from the item's button: it asks for the version, with the current one filled in.
    let offered: string | undefined;
    api().ui.showInputBox = async (o) => {
      offered = o?.value;
      return "2.13.14";
    };
    await vscode.commands.executeCommand("reverb.changePackageVersion", api().packagesView.nodes()[0]);
    assert.strictEqual(offered, "2.14.1");
    await waitFor(() => shown().join() === "Humanizer 2.13.14", "the new version in the view");
    assert.ok(await nb.save());
    assert.strictEqual(fs.readFileSync(nb.uri.fsPath, "utf8"), afterAdd.replace("Humanizer@2.14.1", "Humanizer@2.13.14"));
    await waitFor(() => api().session(nb.uri)?.kernelState === "ready", "the rebuilt kernel");

    // Remove from the item's button.
    await vscode.commands.executeCommand("reverb.removePackage", api().packagesView.nodes()[0]);
    await waitFor(() => shown().length === 0, "the package to leave the view");
    assert.ok(await nb.save());
    assert.strictEqual(fs.readFileSync(nb.uri.fsPath, "utf8"), afterAdd.replace("#:package Humanizer@2.14.1\n", ""));
  });
});
