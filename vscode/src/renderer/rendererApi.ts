// The parts of VS Code's notebook renderer API this renderer uses, as in the
// vscode-notebook-renderer types for VS Code 1.95. Declared here so there's no new dependency.

export interface OutputItem {
  readonly id: string;
  readonly mime: string;
  text(): string;
  json(): any;
}

export interface RendererContext {
  /** Only there when messaging is available; the renderer asks for it with requiresMessaging "optional". */
  readonly postMessage?: (message: unknown) => void;
  readonly onDidReceiveMessage?: (listener: (message: any) => void) => { dispose(): void };
}

export interface RendererApi {
  renderOutputItem(item: OutputItem, element: HTMLElement, signal: AbortSignal): void | Promise<void>;
  /** Without an id, every output is being disposed. */
  disposeOutputItem?(id?: string): void;
}

export type ActivationFunction = (context: RendererContext) => RendererApi | Promise<RendererApi>;
