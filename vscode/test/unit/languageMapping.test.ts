import * as assert from "node:assert";
import { completionKindName, diagnosticRange, parameterLabel, severityName } from "../../src/languageMapping";

describe("completionKindName", () => {
  it("maps the kinds the kernel sends the way the browser editor does", () => {
    assert.strictEqual(completionKindName("variable"), "Variable");
    assert.strictEqual(completionKindName("method"), "Method");
    assert.strictEqual(completionKindName("property"), "Property");
    assert.strictEqual(completionKindName("field"), "Field");
    assert.strictEqual(completionKindName("event"), "Event");
    assert.strictEqual(completionKindName("class"), "Class");
    assert.strictEqual(completionKindName("struct"), "Struct");
    assert.strictEqual(completionKindName("interface"), "Interface");
    assert.strictEqual(completionKindName("enum"), "Enum");
    assert.strictEqual(completionKindName("enum-member"), "EnumMember");
    assert.strictEqual(completionKindName("namespace"), "Module");
    assert.strictEqual(completionKindName("keyword"), "Keyword");
    assert.strictEqual(completionKindName("type"), "TypeParameter");
    assert.strictEqual(completionKindName("snippet"), "Snippet");
  });

  it("falls back to Text for a kind it doesn't know", () => {
    assert.strictEqual(completionKindName("something-new"), "Text");
  });
});

describe("severityName", () => {
  it("maps error, warning and info", () => {
    assert.strictEqual(severityName("error"), "Error");
    assert.strictEqual(severityName("warning"), "Warning");
    assert.strictEqual(severityName("info"), "Information");
  });

  it("has nothing for hidden diagnostics, which aren't shown", () => {
    assert.strictEqual(severityName("hidden"), undefined);
  });
});

describe("diagnosticRange", () => {
  it("uses the lines and columns as sent", () => {
    assert.deepStrictEqual(diagnosticRange({ startLine: 0, startColumn: 12, endLine: 0, endColumn: 19 }), [0, 12, 0, 19]);
  });
});

describe("parameterLabel", () => {
  it("points at the parameter after the opening parenthesis, so a name inside the method name doesn't match", () => {
    assert.deepStrictEqual(parameterLabel("int Math.Max(int val1, int val2)", "int val2"), [23, 31]);
    assert.deepStrictEqual(parameterLabel("void x(int x)", "int x"), [7, 12]);
  });

  it("gives the text itself when it isn't in the label", () => {
    assert.strictEqual(parameterLabel("void f()", "int a"), "int a");
  });
});
