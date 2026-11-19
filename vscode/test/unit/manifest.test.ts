import * as assert from "node:assert";
import * as fs from "node:fs";
import * as path from "node:path";

// package.json is at the extension root, two levels above out/test/unit.
const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "..", "..", "package.json"), "utf8"));

describe("package.json", () => {
  it("activates on C# files, so the CodeLens appears on a notebook opened as text", () => {
    assert.ok(manifest.activationEvents.includes("onLanguage:csharp"));
  });

  it("contributes every command with its title", () => {
    const titles = Object.fromEntries(manifest.contributes.commands.map((c: { command: string; title: string }) => [c.command, c.title]));
    assert.deepStrictEqual(titles, {
      "reverb.openAsNotebook": "Open as Reverb Notebook",
      "reverb.newNotebook": "Reverb: New Notebook",
      "reverb.restartKernel": "Reverb: Restart Kernel",
      "reverb.toggleLazy": "Reverb: Toggle Lazy Mode",
      "reverb.addPackage": "Reverb: Add Package",
      "reverb.removePackage": "Reverb: Remove Package",
    });
  });

  it("contributes the outputs renderer for widgets, charts and tables", () => {
    assert.deepStrictEqual(manifest.contributes.notebookRenderer, [
      {
        id: "reverb.outputs",
        displayName: "Reverb outputs",
        entrypoint: "./out/renderer/index.js",
        mimeTypes: ["application/vnd.reverb.widget+json", "application/vnd.vegalite.v5+json", "application/vnd.reverb.table+json"],
        requiresMessaging: "optional",
      },
    ]);
  });
});
