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
      "reverb.applyFix": "Reverb: Apply Fix",
      "reverb.toggleDisabled": "Reverb: Toggle Disabled",
      "reverb.toggleHideCode": "Reverb: Toggle Hide Code",
      "reverb.nameCell": "Reverb: Name Cell…",
      "reverb.showDependencies": "Reverb: Show Dependencies",
      "reverb.changePackageVersion": "Reverb: Change Package Version",
    });
  });

  it("offers the cell commands on code cells of Reverb notebooks, and in the palette for them", () => {
    const commands = ["reverb.toggleDisabled", "reverb.toggleHideCode", "reverb.nameCell", "reverb.showDependencies"];
    const menus = manifest.contributes.menus;
    for (const command of commands) {
      assert.strictEqual(menus["notebook/cell/title"].find((m: { command: string }) => m.command === command)?.when, "notebookType == reverb && notebookCellType == code");
      assert.strictEqual(menus.commandPalette.find((m: { command: string }) => m.command === command)?.when, "notebookType == reverb");
    }
  });

  it("keeps the fix command out of the palette, since it only works from a quick fix", () => {
    const entry = manifest.contributes.menus.commandPalette.find((m: { command: string }) => m.command === "reverb.applyFix");
    assert.deepStrictEqual(entry, { command: "reverb.applyFix", when: "false" });
  });

  it("contributes the packages view in the Explorer, with its menus and both welcome texts", () => {
    const c = manifest.contributes;
    assert.deepStrictEqual(c.views.explorer, [{ id: "reverb.packages", name: "Reverb Packages", when: "reverb.notebookActive" }]);
    assert.ok(c.menus["view/title"].some((m: { command: string; when: string }) => m.command === "reverb.addPackage" && m.when.includes("view == reverb.packages")));
    for (const command of ["reverb.changePackageVersion", "reverb.removePackage"]) {
      const item = c.menus["view/item/context"].find((m: { command: string }) => m.command === command);
      assert.ok(item?.when.includes("viewItem == reverb.package"), command);
      assert.ok(item.group.startsWith("inline"), command);
    }
    const welcome = c.viewsWelcome.filter((w: { view: string }) => w.view === "reverb.packages").map((w: { contents: string }) => w.contents);
    assert.ok(welcome.includes("No packages yet.\n[Add Package](command:reverb.addPackage)"));
    assert.ok(welcome.includes("Trust this folder to add or change packages."));
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
