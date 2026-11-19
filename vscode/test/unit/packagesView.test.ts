import * as assert from "node:assert";
import { packageItems, packageNameError, packageVersionError } from "../../src/packages";

describe("packageItems", () => {
  it("lists packages in the notebook's order, with the version as the description", () => {
    const items = packageItems([
      { name: "Humanizer", version: "2.14.1" },
      { name: "CsvHelper", version: "[30.0,31.0)" },
    ]);
    assert.deepStrictEqual(items, [
      { label: "Humanizer", description: "2.14.1", contextValue: "reverb.package" },
      { label: "CsvHelper", description: "[30.0,31.0)", contextValue: "reverb.package" },
    ]);
  });

  it("says latest when there is no version", () => {
    assert.deepStrictEqual(packageItems([{ name: "A" }, { name: "B", version: "" }]).map((i) => i.description), ["latest", "latest"]);
  });

  it("has nothing for no packages", () => {
    assert.deepStrictEqual(packageItems([]), []);
  });
});

describe("package validation", () => {
  it("accepts the version forms NuGet takes", () => {
    for (const v of ["2.14.1", "*", "[1.0,2.0)", "1.0.0-beta.1", "1.*", "1.0+build5"]) assert.strictEqual(packageVersionError(v, "X"), undefined, v);
  });

  it("rejects a version with spaces, or an empty one, and says what to do", () => {
    assert.match(packageVersionError("1.0 2.0", "Humanizer") ?? "", /Humanizer/);
    assert.match(packageVersionError("", "Humanizer") ?? "", /\*/);
    assert.ok(packageVersionError("1.0;rm", "X"));
  });

  it("accepts package names with dots, dashes and underscores", () => {
    for (const n of ["Humanizer", "Newtonsoft.Json", "my-pkg_2"]) assert.strictEqual(packageNameError(n), undefined, n);
  });

  it("rejects empty names and names with spaces or symbols", () => {
    assert.ok(packageNameError(""));
    assert.ok(packageNameError("   "));
    assert.ok(packageNameError("two words"));
    assert.ok(packageNameError("a@b"));
  });
});
