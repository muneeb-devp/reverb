import * as assert from "node:assert";
import { widgetMessage, widgetSender } from "../../src/renderer/messages";
import { Roots } from "../../src/renderer/roots";

class FakeRoot {
  unmounted = false;
  unmount() {
    this.unmounted = true;
  }
}

describe("renderer roots", () => {
  it("rendering the same widget twice keeps one root", () => {
    const roots = new Roots<object, FakeRoot>();
    const element = {};
    let created = 0;
    const create = () => {
      created++;
      return new FakeRoot();
    };
    const first = roots.rootFor("item1", element, create);
    const second = roots.rootFor("item1", element, create);
    assert.strictEqual(first, second);
    assert.strictEqual(created, 1);
  });

  it("gives a new root after the old one is disposed", () => {
    const roots = new Roots<object, FakeRoot>();
    const element = {};
    const first = roots.rootFor("item1", element, () => new FakeRoot());
    roots.dispose("item1");
    assert.ok(first.unmounted);
    const second = roots.rootFor("item1", element, () => new FakeRoot());
    assert.notStrictEqual(first, second);
    assert.ok(!second.unmounted);
  });

  it("keeps the root when VS Code renders a new item into the same element", () => {
    const roots = new Roots<object, FakeRoot>();
    const element = {};
    const first = roots.rootFor("item1", element, () => new FakeRoot());
    assert.strictEqual(roots.rootFor("item2", element, () => new FakeRoot()), first);
    // The element belongs to item2 now, so disposing item1 leaves it alone.
    roots.dispose("item1");
    assert.ok(!first.unmounted);
    roots.dispose("item2");
    assert.ok(first.unmounted);
  });

  it("makes a new root when another renderer replaced what the element showed", () => {
    const roots = new Roots<{ child: object | null }, FakeRoot>((e) => e.child);
    const element: { child: object | null } = { child: null };
    const first = roots.rootFor("item1", element, () => new FakeRoot());
    element.child = {};
    assert.strictEqual(roots.rootFor("item1", element, () => new FakeRoot()), first);
    element.child = {};
    const second = roots.rootFor("item1", element, () => new FakeRoot());
    assert.notStrictEqual(second, first);
    assert.ok(first.unmounted);
    assert.ok(!second.unmounted);
  });

  it("disposes only the item it's asked to", () => {
    const roots = new Roots<object, FakeRoot>();
    const a = roots.rootFor("a", {}, () => new FakeRoot());
    const b = roots.rootFor("b", {}, () => new FakeRoot());
    roots.dispose("a");
    assert.ok(a.unmounted);
    assert.ok(!b.unmounted);
  });

  it("disposes everything when no id is given", () => {
    const roots = new Roots<object, FakeRoot>();
    const a = roots.rootFor("a", {}, () => new FakeRoot());
    const b = roots.rootFor("b", {}, () => new FakeRoot());
    roots.dispose();
    assert.ok(a.unmounted && b.unmounted);
  });
});

describe("renderer messages", () => {
  it("builds the widget.setValue message", () => {
    assert.deepStrictEqual(widgetMessage("w1", 7, "slider"), { type: "widget.setValue", widgetId: "w1", value: 7, kind: "slider" });
  });

  it("the sender posts a widget.setValue message", () => {
    const posted: unknown[] = [];
    const send = widgetSender((m) => posted.push(m));
    assert.ok(send);
    send("w2", 1, "dropdown");
    assert.deepStrictEqual(posted, [{ type: "widget.setValue", widgetId: "w2", value: 1, kind: "dropdown" }]);
  });

  it("there is no sender without messaging, which turns the controls off", () => {
    assert.strictEqual(widgetSender(undefined), null);
  });
});
