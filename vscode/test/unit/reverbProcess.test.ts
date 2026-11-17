import * as assert from "node:assert";
import { EventEmitter } from "node:events";
import { PassThrough } from "node:stream";
import { ReverbExitedError, ReverbProcess, ReverbTooOldError, findReverb, saveFailureMessage } from "../../src/reverbProcess";
import type { ServerMessage } from "../../src/protocol";

class FakeChild extends EventEmitter {
  stdin = new PassThrough();
  stdout = new PassThrough();
  stderr = new PassThrough();
  killed = false;
  sent: any[] = [];
  private buffer = "";
  /** Called for every line the extension writes. */
  onLine: (m: any) => void = () => {};

  constructor() {
    super();
    this.stdin.on("data", (d: Buffer) => {
      this.buffer += d.toString("utf8");
      let i: number;
      while ((i = this.buffer.indexOf("\n")) >= 0) {
        const line = this.buffer.slice(0, i);
        this.buffer = this.buffer.slice(i + 1);
        const m = JSON.parse(line);
        this.sent.push(m);
        this.onLine(m);
      }
    });
  }
  reply(m: object) {
    this.stdout.write(JSON.stringify(m) + "\n");
  }
  kill() {
    this.killed = true;
    this.emit("exit", null, "SIGTERM");
    return true;
  }
}

function fake(protocolVersion = 1) {
  const child = new FakeChild();
  child.onLine = (m) => {
    if (m.type === "hello") child.reply({ type: "hello.result", requestId: m.requestId, protocolVersion, reverbVersion: "0.1.1" });
  };
  const spawnFn: any = () => child;
  return { child, spawnFn };
}

const opts = (log: string[] = []) => ({ command: "reverb", args: ["serve", "--stdio"], log: (l: string) => log.push(l) });
const tick = () => new Promise((r) => setTimeout(r, 5));

describe("ReverbProcess", () => {
  it("handshakes and resolves requests by id", async () => {
    const { child, spawnFn } = fake();
    const p = await ReverbProcess.start(opts(), spawnFn);
    assert.deepStrictEqual(child.sent[0], { type: "hello", requestId: 1 });
    child.onLine = (m) => {
      if (m.type === "format.parse") child.reply({ type: "format.parsed", requestId: m.requestId, parts: { cells: [] } });
    };
    const result = await p.request<any>({ type: "format.parse", text: "x" });
    assert.strictEqual(result.type, "format.parsed");
    assert.strictEqual(child.sent[1].requestId, 2);
    p.dispose();
  });

  it("rejects a request when the server answers with an error", async () => {
    const { child, spawnFn } = fake();
    const p = await ReverbProcess.start(opts(), spawnFn);
    child.onLine = (m) => child.reply({ type: "error", requestId: m.requestId, message: "Bad cell kind." });
    await assert.rejects(p.request({ type: "format.serialize" }), /Bad cell kind\./);
    p.dispose();
  });

  it("times out a request that is never answered", async () => {
    const { spawnFn } = fake();
    const p = await ReverbProcess.start(opts(), spawnFn);
    await assert.rejects(p.request({ type: "format.parse" }, 20), /didn't answer .*format\.parse.* within/);
    p.dispose();
  });

  it("rejects start with ReverbTooOldError when Reverb speaks an older protocol", async () => {
    const { child, spawnFn } = fake(0);
    await assert.rejects(ReverbProcess.start(opts(), spawnFn), (e: Error) => {
      assert.ok(e instanceof ReverbTooOldError, `expected a ReverbTooOldError, got ${e.constructor.name}: ${e.message}`);
      assert.match(e.message, /dotnet tool update -g Reverb\.Notebooks/);
      assert.match(e.message, /version 1/);
      return true;
    });
    assert.ok(child.stdin.writableEnded);
  });

  it("says to update the extension when Reverb speaks a newer protocol", async () => {
    const { spawnFn } = fake(2);
    await assert.rejects(ReverbProcess.start(opts(), spawnFn), (e: Error) => {
      assert.ok(!(e instanceof ReverbTooOldError), "updating Reverb wouldn't help");
      assert.match(e.message, /Update the Reverb extension/);
      return true;
    });
  });

  // Every published Reverb.Notebooks before `reverb serve` existed answers like this.
  for (const [what, code, stderr] of [
    ["exit code 2 and Unknown command", 2, "Unknown command 'serve'.\n\nUsage: reverb <command>\n"],
    ["exit code 2 alone", 2, ""],
    ["Unknown command on stderr alone", 1, "Unknown command 'serve'."],
  ] as const) {
    it(`rejects start with ReverbTooOldError when an old reverb exits before the handshake (${what})`, async () => {
      const child = new FakeChild();
      child.onLine = () => {
        child.stderr.write(stderr);
        child.stderr.end();
        child.emit("exit", code, null);
      };
      const log: string[] = [];
      await assert.rejects(ReverbProcess.start(opts(log), (() => child) as any), (e: Error) => {
        assert.ok(e instanceof ReverbTooOldError, `expected a ReverbTooOldError, got ${e.constructor.name}: ${e.message}`);
        assert.match(e.message, /too old/);
        assert.match(e.message, /reverb serve/);
        assert.match(e.message, /dotnet tool update -g Reverb\.Notebooks/);
        assert.doesNotMatch(e.message, /\.NET 10 SDK/);
        return true;
      });
      if (stderr) assert.ok(log.some((l) => l.includes("Unknown command 'serve'")));
    });
  }

  it("keeps the SDK hint when reverb exits early for another reason", async () => {
    const child = new FakeChild();
    child.onLine = () => {
      child.stderr.end("The .NET runtime wasn't found.\n");
      child.emit("exit", 1, null);
    };
    await assert.rejects(ReverbProcess.start(opts(), (() => child) as any), (e: Error) => {
      assert.ok(!(e instanceof ReverbTooOldError));
      assert.match(e.message, /\.NET 10 SDK/);
      return true;
    });
  });

  it("rejects start when the process can't be spawned", async () => {
    const child = new FakeChild();
    child.onLine = () => child.emit("error", Object.assign(new Error("spawn reverb ENOENT"), { code: "ENOENT" }));
    await assert.rejects(ReverbProcess.start(opts(), (() => child) as any), /couldn't start/);
  });

  it("routes notebook messages by path", async () => {
    const { child, spawnFn } = fake();
    const p = await ReverbProcess.start(opts(), spawnFn);
    const a: ServerMessage[] = [];
    const b: ServerMessage[] = [];
    p.onNotebookMessage("/n/a.cs", (m) => a.push(m));
    const subB = p.onNotebookMessage("/n/b.cs", (m) => b.push(m));
    child.reply({ type: "kernel.status", state: "ready", notebook: "/n/a.cs" });
    child.reply({ type: "kernel.status", state: "busy", notebook: "/n/b.cs" });
    child.reply({ type: "kernel.status", state: "dead", notebook: "/n/c.cs" });
    await tick();
    assert.deepStrictEqual(a.map((m: any) => m.state), ["ready"]);
    assert.deepStrictEqual(b.map((m: any) => m.state), ["busy"]);
    subB.dispose();
    child.reply({ type: "kernel.status", state: "ready", notebook: "/n/b.cs" });
    await tick();
    assert.strictEqual(b.length, 1);
    p.dispose();
  });

  it("does not treat a host reply that reuses a request id as a pending request", async () => {
    const { child, spawnFn } = fake();
    const p = await ReverbProcess.start(opts(), spawnFn);
    const got: any[] = [];
    p.onNotebookMessage("/n/a.cs", (m) => got.push(m));
    let settled = false;
    const pending = p.request({ type: "format.parse" }).then(() => (settled = true));
    child.reply({ type: "lang.hoverResult", requestId: 2, text: "x", notebook: "/n/a.cs" });
    await tick();
    assert.strictEqual(got.length, 1);
    assert.strictEqual(settled, false);
    child.reply({ type: "format.parsed", requestId: 2 });
    await pending;
    p.dispose();
  });

  it("splits lines that arrive in pieces", async () => {
    const { child, spawnFn } = fake();
    const p = await ReverbProcess.start(opts(), spawnFn);
    const got: ServerMessage[] = [];
    p.onNotebookMessage("/n/a.cs", (m) => got.push(m));
    const line = JSON.stringify({ type: "kernel.status", state: "ready", notebook: "/n/a.cs" }) + "\n";
    child.stdout.write(line.slice(0, 20));
    await tick();
    assert.strictEqual(got.length, 0);
    child.stdout.write(line.slice(20));
    await tick();
    assert.strictEqual(got.length, 1);
    p.dispose();
  });

  it("handles a multi-byte character split across chunks", async () => {
    const { child, spawnFn } = fake();
    const p = await ReverbProcess.start(opts(), spawnFn);
    const got: any[] = [];
    p.onNotebookMessage("/n/a.cs", (m) => got.push(m));
    const bytes = Buffer.from(JSON.stringify({ type: "cell.stream", cellId: "c", stream: "stdout", text: "é\n", notebook: "/n/a.cs" }) + "\n");
    const cut = bytes.indexOf(0xc3) + 1;
    child.stdout.write(bytes.subarray(0, cut));
    await tick();
    child.stdout.write(bytes.subarray(cut));
    await tick();
    assert.strictEqual(got[0].text, "é\n");
    p.dispose();
  });

  it("logs lines it can't parse and keeps going", async () => {
    const log: string[] = [];
    const { child, spawnFn } = fake();
    const p = await ReverbProcess.start(opts(log), spawnFn);
    const got: ServerMessage[] = [];
    p.onNotebookMessage("/n/a.cs", (m) => got.push(m));
    child.stdout.write("not json\n");
    child.reply({ type: "kernel.status", state: "ready", notebook: "/n/a.cs" });
    await tick();
    assert.strictEqual(got.length, 1);
    assert.ok(log.some((l) => /not json/.test(l)));
    p.dispose();
  });

  it("sends messages as JSON lines", async () => {
    const { child, spawnFn } = fake();
    const p = await ReverbProcess.start(opts(), spawnFn);
    p.send({ type: "notebook.runAll", notebook: "/n/a.cs" });
    await tick();
    assert.deepStrictEqual(child.sent[1], { type: "notebook.runAll", notebook: "/n/a.cs" });
    p.dispose();
  });

  it("sends stderr to the log", async () => {
    const log: string[] = [];
    const { child, spawnFn } = fake();
    const p = await ReverbProcess.start(opts(log), spawnFn);
    child.stderr.write("warning: something\n");
    await tick();
    assert.ok(log.some((l) => l.includes("warning: something")));
    p.dispose();
  });

  it("rejects pending requests when the process exits", async () => {
    const { child, spawnFn } = fake();
    const p = await ReverbProcess.start(opts(), spawnFn);
    let code: number | null | undefined;
    p.onExit((c) => (code = c));
    const pending = p.request({ type: "format.serialize" });
    child.emit("exit", 3, null);
    await assert.rejects(pending, (e: Error) => e instanceof ReverbExitedError && /Reverb stopped/.test(e.message));
    assert.strictEqual(code, 3);
    await assert.rejects(p.request({ type: "format.parse" }), ReverbExitedError);
  });

  it("a save that failed because reverb stopped says it restarts, not to reload the window", async () => {
    const { child, spawnFn } = fake();
    const p = await ReverbProcess.start(opts(), spawnFn);
    const pending = p.request({ type: "format.serialize" });
    child.emit("exit", null, "SIGKILL");
    const error = await pending.then(() => assert.fail("the save should fail"), (e: Error) => e);
    const message = saveFailureMessage(error);
    assert.match(message, /file wasn't changed/);
    assert.match(message, /Reverb stopped/);
    assert.match(message, /starts again/);
    assert.match(message, /save again in a moment/i);
    assert.doesNotMatch(message, /Reload the window/);
    // Other failures keep the server's reason.
    assert.match(saveFailureMessage(new Error("Bad cell kind.")), /file wasn't changed: Bad cell kind\./);
  });

  it("treats a stdin error as the process being gone", async () => {
    const { child, spawnFn } = fake();
    const log: string[] = [];
    const p = await ReverbProcess.start(opts(log), spawnFn);
    const pending = p.request({ type: "format.parse" });
    child.stdin.emit("error", Object.assign(new Error("write EPIPE"), { code: "EPIPE" }));
    await assert.rejects(pending, /EPIPE/);
    assert.ok(log.some((l) => l.includes("EPIPE")));
    child.stdout.emit("error", new Error("read failed"));
    child.stderr.emit("error", new Error("read failed"));
    await assert.rejects(p.request({ type: "format.parse" }), /stopped|EPIPE/);
  });

  it("parses lines that end in CRLF", async () => {
    const { child, spawnFn } = fake();
    const p = await ReverbProcess.start(opts(), spawnFn);
    const got: ServerMessage[] = [];
    p.onNotebookMessage("/n/a.cs", (m) => got.push(m));
    child.stdout.write(JSON.stringify({ type: "kernel.status", state: "ready", notebook: "/n/a.cs" }) + "\r\n");
    await tick();
    assert.strictEqual(got.length, 1);
    p.dispose();
  });

  it("rejects pending requests on dispose", async () => {
    const { spawnFn } = fake();
    const p = await ReverbProcess.start(opts(), spawnFn);
    const pending = p.request({ type: "format.parse" });
    p.dispose();
    await assert.rejects(pending, /Reverb process was stopped/);
  });

  it("closes stdin on dispose so the server shuts down cleanly", async () => {
    const { child, spawnFn } = fake();
    const p = await ReverbProcess.start(opts(), spawnFn);
    p.dispose();
    assert.ok(child.stdin.writableEnded);
  });
});

describe("findReverb", () => {
  const none = () => false;

  it("prefers the setting", () => {
    assert.strictEqual(findReverb("/opt/reverb", { PATH: "/usr/bin" }, () => true, "linux"), "/opt/reverb");
  });

  it("ignores a setting that doesn't exist and falls back to PATH", () => {
    const exists = (p: string) => p === "/usr/bin/reverb";
    assert.strictEqual(findReverb("/nope", { PATH: "/bin:/usr/bin" }, exists, "linux"), "/usr/bin/reverb");
  });

  it("falls back to ~/.dotnet/tools on Linux and macOS", () => {
    const exists = (p: string) => p === "/home/me/.dotnet/tools/reverb";
    assert.strictEqual(findReverb(undefined, { PATH: "/usr/bin", HOME: "/home/me" }, exists, "linux"), "/home/me/.dotnet/tools/reverb");
    assert.strictEqual(findReverb("", { HOME: "/home/me" }, exists, "darwin"), "/home/me/.dotnet/tools/reverb");
  });

  it("uses reverb.exe and USERPROFILE on Windows", () => {
    const exists = (p: string) => p === "C:\\Users\\me\\.dotnet\\tools\\reverb.exe";
    assert.strictEqual(
      findReverb(undefined, { Path: "C:\\Windows", USERPROFILE: "C:\\Users\\me" }, exists, "win32"),
      "C:\\Users\\me\\.dotnet\\tools\\reverb.exe",
    );
  });

  it("returns undefined when nothing is found", () => {
    assert.strictEqual(findReverb(undefined, { PATH: "/usr/bin", HOME: "/h" }, none, "linux"), undefined);
  });
});
