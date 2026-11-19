// One React root per output element. Rendering into an element again updates its root in place,
// so a widget keeps its state (a slider mid-drag) when the kernel sends a new value.
// Generic over the element and root so the unit tests can run it without a DOM.

export class Roots<E extends object, R extends { unmount(): void }> {
  // The id is the output item last rendered into the element; VS Code disposes by item id.
  private readonly byElement = new Map<E, { id: string; root: R; child?: unknown }>();

  /** firstChild says what an element holds now, so a root whose output another renderer replaced isn't reused. */
  constructor(private readonly firstChild: (element: E) => unknown = () => undefined) {}

  rootFor(id: string, element: E, create: (element: E) => R): R {
    let entry = this.byElement.get(element);
    if (entry) {
      const now = this.firstChild(element);
      // The root's first render is asynchronous, so what it drew is only known on a later call.
      if (entry.child === undefined && now != null) entry.child = now;
      else if (entry.child !== undefined && now !== entry.child) {
        entry.root.unmount();
        entry = undefined;
      }
    }
    if (!entry) {
      entry = { id, root: create(element) };
      this.byElement.set(element, entry);
    }
    entry.id = id;
    return entry.root;
  }

  /** Unmounts the root showing this output item, or every root when there's no id. */
  dispose(id?: string): void {
    for (const [element, entry] of this.byElement) {
      if (id !== undefined && entry.id !== id) continue;
      entry.root.unmount();
      this.byElement.delete(element);
    }
  }
}
