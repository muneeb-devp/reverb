// The only module that talks to the reverb child process. It never imports vscode at run time,
// so the framing, request matching and routing can be tested with a fake spawn.
import { spawn as nodeSpawn, type ChildProcess } from "node:child_process";
import * as path from "node:path";
import type { Disposable, Event } from "vscode";
import { PROTOCOL_VERSION, type HelloResult, type ServerMessage, type StdioMessage } from "./protocol";

export interface ReverbProcessOptions {
  command: string;
  args: string[];
  cwd?: string;
  log: (line: string) => void;
  /** Called with the type of every message written to reverb. */
  onSend?: (type: string) => void;
}

export const UPDATE_COMMAND = "dotnet tool update -g Reverb.Notebooks";
export const INSTALL_COMMAND = "dotnet tool install -g Reverb.Notebooks";

/** The installed Reverb is older than this extension needs. Updating the tool fixes it. */
export class ReverbTooOldError extends Error {
  override name = "ReverbTooOldError";
}

/** Reverb stopped before it answered. The extension starts it again the next time it's needed. */
export class ReverbExitedError extends Error {
  override name = "ReverbExitedError";
}

/** What to tell the user when saving a notebook failed with `e`. */
export function saveFailureMessage(e: Error): string {
  if (e instanceof ReverbExitedError) {
    return "Reverb stopped while saving this notebook, so the file wasn't changed. It starts again by itself: save again in a moment.";
  }
  return `Reverb couldn't save this notebook, so the file wasn't changed: ${e.message}`;
}

const DEFAULT_TIMEOUT_MS = 30_000;
const SHUTDOWN_GRACE_MS = 3_000;
/** How long to wait for the rest of reverb's error output after it exits during the handshake. */
const STDERR_DRAIN_MS = 500;
/** How much error output is kept from before the handshake, to tell why reverb didn't start. */
const STARTUP_STDERR_LIMIT = 4096;

interface Pending {
  type: string;
  resolve: (m: any) => void;
  reject: (e: Error) => void;
  timer: NodeJS.Timeout;
}

class Emitter<T> {
  private listeners = new Set<(e: T) => void>();
  readonly event: Event<T> = (listener) => {
    this.listeners.add(listener);
    return { dispose: () => void this.listeners.delete(listener) };
  };
  fire(e: T) {
    for (const l of [...this.listeners]) l(e);
  }
}

export class ReverbProcess implements Disposable {
  private nextId = 1;
  private pending = new Map<number, Pending>();
  private handlers = new Map<string, Set<(m: ServerMessage) => void>>();
  private exited = new Emitter<number | null>();
  private exitCode: number | null | undefined;
  private disposed = false;
  /** Error output from before the handshake finished. Undefined after it. */
  private startupStderr: string | undefined = "";

  readonly onExit: Event<number | null> = this.exited.event;

  private constructor(
    private child: ChildProcess,
    private log: (line: string) => void,
    private onSend?: (type: string) => void,
  ) {
    this.readLines(child.stdout!, (line) => this.receive(line));
    this.readLines(child.stderr!, (line) => {
      if (this.startupStderr !== undefined && this.startupStderr.length < STARTUP_STDERR_LIMIT) this.startupStderr += line + "\n";
      this.log(line);
    });
    child.stdin?.on("error", (e) => this.handleExit(null, e));
    child.stdout!.on("error", (e) => this.log(`reverb's output failed: ${e.message}`));
    child.stderr!.on("error", (e) => this.log(`reverb's error output failed: ${e.message}`));
    child.on("exit", (code) => this.handleExit(code));
    child.on("error", (e) => this.handleExit(null, e));
  }

  /** Starts the process and checks that it speaks this extension's protocol version. */
  static async start(opts: ReverbProcessOptions, spawnFn: typeof nodeSpawn = nodeSpawn): Promise<ReverbProcess> {
    let child: ChildProcess;
    try {
      child = spawnFn(opts.command, opts.args, { cwd: opts.cwd, stdio: ["pipe", "pipe", "pipe"] });
    } catch (e) {
      throw new Error(`Reverb couldn't start (${opts.command}): ${(e as Error).message}. Check the reverb.path setting.`);
    }
    const p = new ReverbProcess(child, opts.log, opts.onSend);
    let hello: HelloResult;
    try {
      hello = await p.request<HelloResult>({ type: "hello" });
    } catch (e) {
      const stderr = await p.stderrAfterExit();
      p.dispose();
      // Reverb.Notebooks from before `reverb serve` existed rejects the command with exit code 2.
      if (p.exitCode === 2 || stderr.includes("Unknown command 'serve'")) {
        throw new ReverbTooOldError(
          `The installed Reverb (${opts.command}) is too old for this extension: it has no \`reverb serve\` command. Run \`${UPDATE_COMMAND}\` to get a version that has it, then reopen the notebook.`,
        );
      }
      throw new Error(
        `Reverb couldn't start (${opts.command}): ${(e as Error).message} Check that the .NET 10 SDK is installed and the reverb.path setting is right.`,
      );
    }
    p.startupStderr = undefined;
    if (hello.protocolVersion < PROTOCOL_VERSION) {
      p.dispose();
      throw new ReverbTooOldError(
        `This extension needs Reverb protocol version ${PROTOCOL_VERSION}, but the installed Reverb (${hello.reverbVersion}) speaks version ${hello.protocolVersion}. Run \`${UPDATE_COMMAND}\` to update it.`,
      );
    }
    if (hello.protocolVersion !== PROTOCOL_VERSION) {
      p.dispose();
      throw new Error(
        `The installed Reverb (${hello.reverbVersion}) speaks protocol version ${hello.protocolVersion}, newer than this extension's version ${PROTOCOL_VERSION}. Update the Reverb extension in the Extensions view.`,
      );
    }
    return p;
  }

  /**
   * For replies that carry the request's id: hello and format.*. Host-routed errors don't echo
   * the id, so never wait on one of those with this.
   */
  request<T>(message: object, timeoutMs = DEFAULT_TIMEOUT_MS): Promise<T> {
    const type = String((message as { type?: unknown }).type);
    if (this.exitCode !== undefined) return Promise.reject(this.exitedError());
    const requestId = this.nextId++;
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(requestId);
        reject(new Error(`Reverb didn't answer the '${type}' request within ${Math.round(timeoutMs / 1000)} s. Reload the window to restart it.`));
      }, timeoutMs);
      this.pending.set(requestId, { type, resolve, reject, timer });
      this.write({ ...message, requestId });
    });
  }

  get pid(): number | undefined {
    return this.child.pid;
  }

  send(message: object): void {
    this.write(message);
  }

  /** Receives every message the server routes to the notebook opened as `path`. */
  onNotebookMessage(path: string, handler: (m: ServerMessage) => void): Disposable {
    let set = this.handlers.get(path);
    if (!set) this.handlers.set(path, (set = new Set()));
    set.add(handler);
    return {
      dispose: () => {
        set.delete(handler);
        if (set.size === 0) this.handlers.delete(path);
      },
    };
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.rejectPending(new Error("Reverb process was stopped. Reload the window to start it again."));
    try {
      this.child.stdin?.end();
    } catch {
      // already closed
    }
    if (this.exitCode === undefined) {
      const timer = setTimeout(() => this.child.kill(), SHUTDOWN_GRACE_MS);
      timer.unref();
      this.child.once("exit", () => clearTimeout(timer));
    }
  }

  private write(message: object) {
    if (this.exitCode !== undefined || this.disposed) return;
    this.onSend?.(String((message as { type?: unknown }).type));
    try {
      this.child.stdin!.write(JSON.stringify(message) + "\n");
    } catch (e) {
      this.log(`Couldn't write to reverb: ${(e as Error).message}`);
    }
  }

  private readLines(stream: NodeJS.ReadableStream, onLine: (line: string) => void) {
    let buffer = "";
    stream.setEncoding("utf8");
    stream.on("data", (chunk: string) => {
      buffer += chunk;
      let i: number;
      while ((i = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, i).replace(/\r$/, "");
        buffer = buffer.slice(i + 1);
        if (line) onLine(line);
      }
    });
    // The last line may have no line break.
    stream.on("end", () => {
      const line = buffer.replace(/\r$/, "");
      buffer = "";
      if (line) onLine(line);
    });
  }

  private receive(line: string) {
    let m: StdioMessage;
    try {
      m = JSON.parse(line);
      if (m === null || typeof m !== "object") throw new Error("not an object");
    } catch {
      this.log(`Ignoring a line from reverb that isn't JSON: ${line.slice(0, 200)}`);
      return;
    }
    if (m.notebook !== undefined) {
      const set = this.handlers.get(m.notebook);
      if (set) for (const h of [...set]) h(m as ServerMessage);
      return;
    }
    if (m.requestId !== undefined) {
      const p = this.pending.get(m.requestId);
      if (p) {
        this.pending.delete(m.requestId);
        clearTimeout(p.timer);
        if (m.type === "error") p.reject(new Error(m.message));
        else p.resolve(m);
        return;
      }
    }
    if (m.type === "error") this.log(`reverb: ${m.message}`);
  }

  /**
   * The error output from before the handshake. After an exit, it waits a little for the rest,
   * since the process can be reported gone before its last output has been read.
   */
  private async stderrAfterExit(): Promise<string> {
    const stderr = this.child.stderr;
    if (this.exitCode !== undefined && stderr && !stderr.readableEnded && !stderr.destroyed) {
      await new Promise<void>((resolve) => {
        const timer = setTimeout(resolve, STDERR_DRAIN_MS);
        stderr.once("close", () => {
          clearTimeout(timer);
          resolve();
        });
        stderr.once("end", () => {
          clearTimeout(timer);
          resolve();
        });
      });
    }
    return this.startupStderr ?? "";
  }

  private exitedError() {
    return new ReverbExitedError("Reverb stopped. The Reverb output panel says why.");
  }

  private handleExit(code: number | null, error?: Error) {
    if (this.exitCode !== undefined) return;
    this.exitCode = code;
    if (error) this.log(`reverb failed: ${error.message}`);
    this.rejectPending(error ? new ReverbExitedError(error.message) : this.exitedError());
    this.exited.fire(code);
  }

  private rejectPending(e: Error) {
    for (const p of this.pending.values()) {
      clearTimeout(p.timer);
      p.reject(e);
    }
    this.pending.clear();
  }
}

/** The setting, then PATH, then where `dotnet tool install -g` puts tools. */
export function findReverb(
  setting: string | undefined,
  env: NodeJS.ProcessEnv,
  exists: (p: string) => boolean,
  platform: NodeJS.Platform = process.platform,
): string | undefined {
  const win = platform === "win32";
  const p = win ? path.win32 : path.posix;
  const exe = win ? "reverb.exe" : "reverb";
  if (setting && exists(setting)) return setting;
  const pathVar = env.PATH ?? env.Path ?? "";
  for (const dir of pathVar.split(win ? ";" : ":")) {
    if (!dir) continue;
    const candidate = p.join(dir, exe);
    if (exists(candidate)) return candidate;
  }
  const home = win ? env.USERPROFILE : env.HOME;
  if (home) {
    const candidate = p.join(home, ".dotnet", "tools", exe);
    if (exists(candidate)) return candidate;
  }
  return undefined;
}
