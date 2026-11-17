// What the status bar items say. No vscode import, so it can be unit tested in plain Node.
import type { CellStatus, KernelState } from "./protocol";

/** The text under a code cell, or undefined when there is nothing to say. */
export function cellStatusText(status: CellStatus | undefined, message: string | undefined): string | undefined {
  switch (status) {
    case "stale":
      return "Stale: run to update";
    case "disabled":
      return "Disabled";
    case "upstream-error":
      // The server's message names the cell that failed.
      return message || "An upstream cell failed, so this one didn't run. Fix that cell and run again.";
    default:
      return undefined;
  }
}

/** The kernel item for the active notebook. `state` is undefined while it has no session. */
export function kernelStatusText(
  state: KernelState | undefined,
  message: string | undefined,
  trusted: boolean,
): { text: string; tooltip: string } {
  if (!trusted) {
    return { text: "Reverb: restricted", tooltip: "Trust this folder to run Reverb notebooks. Until then no kernel is started." };
  }
  if (!state) {
    return { text: "Reverb: not started", tooltip: "Reverb hasn't opened this notebook yet. Run a cell to start it." };
  }
  return { text: `Reverb: ${state}`, tooltip: message || `The kernel is ${state}.` };
}
