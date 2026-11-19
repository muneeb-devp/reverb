# Contributing to Reverb

Thanks for wanting to help. Bug reports, fixes, docs and new samples are all welcome. This page says how to get set up and what a pull request needs before it can be merged.

## Before you start

- For a bug, open an issue with the notebook (or the smallest part of it that shows the problem), what you expected, and what happened. `reverb check` and `reverb exec` output helps.
- For anything bigger than a small fix, open an issue first and describe what you want to change. Some changes need a design decision before any code is written:
  - the notebook file format,
  - the protocol between the editor, the server and the kernel (including `reverb serve --stdio`),
  - new dependencies, especially in `Reverb.Runtime`,
  - anything that changes the security defaults below.
- Security problems don't go in public issues. See [SECURITY.md](SECURITY.md).

## Setting up

You need the .NET 10 SDK and Node.js 22 or newer.

```sh
git clone https://github.com/muneeb-devp/reverb
cd reverb
dotnet build Reverb.slnx
dotnet test Reverb.slnx
```

`dotnet build -p:SkipFrontendBuild=true` skips bundling the browser editor when you're only working on the engine.

The browser tests:

```sh
cd tests/e2e
npm ci
npx playwright install chromium
npx playwright test
```

The VS Code extension:

```sh
dotnet build Reverb.slnx      # the extension's integration tests use this CLI
cd frontend && npm ci && cd .. # the extension's renderer builds from the frontend's source and packages
cd vscode
npm ci
npm test                      # unit tests
npm run test:integration      # downloads VS Code and runs the notebook tests
```

To try the extension by hand, open the `vscode` folder in VS Code and press F5, or run `npm run package` and install the `.vsix` it writes.

## How changes are made

`main` is protected. Nobody pushes to it directly, including the maintainer. Every change goes through a pull request:

1. Fork the repo (or create a branch if you have access) and branch from `main`.
2. Make the change, with tests.
3. Open a pull request against `main`. CI has to pass on Linux, macOS and Windows before it can be merged.

Keep pull requests small and about one thing. A fix and an unrelated cleanup are two pull requests. Don't reformat code you aren't otherwise changing.

### Commits

- Write the subject line as a plain sentence in the imperative: "Fix the kernel hanging when stdin closes", not "fixed stuff" or "feat: kernel".
- Keep the subject under about 72 characters. Use the body to say why, if it isn't obvious.
- One logical change per commit, and every commit should build.
- Commits are made by people. Don't add AI attribution lines ("Co-Authored-By" an assistant, "Generated with ...") to commits or pull requests. If a tool helped you write the code, you're still the author and you're responsible for it.

## What a pull request needs

- **Tests with the code.** New behaviour comes with tests that fail without it. Changes to the file format or the analysis code are written test-first, and fixtures in `tests/fixtures` are snapshot-tested.
- **The consistency invariant holds.** Reverb promises that what the editor shows matches what a fresh run of the whole file would produce. The fuzzer in `tests/Reverb.Consistency.Tests` checks this. If it finds a mismatch, the fix goes in the engine, or the case becomes a diagnostic. Never loosen the check to make it pass.
- **Byte-exact files.** Opening and saving a notebook must not change bytes the user didn't change. That includes line endings and the BOM.
- **`Reverb.Runtime` stays small and has no dependencies.** It ships inside every notebook.
- **Error messages help.** Say what went wrong, how to fix it, and point at the cell.
- **No telemetry.** Reverb doesn't phone home, and contributions can't add it.
- **The security defaults stay.** The editor listens on 127.0.0.1 only, requires a random token, and rejects foreign origins and non-loopback Host headers. `reverb serve --stdio` opens no port. The VS Code extension starts no kernel in an untrusted workspace.
- **Docs stay current.** If you change how something works, update the README, the command help, or the extension's README in the same pull request.

## Writing style

Docs, comments, error messages and the README should read like a person wrote them: plain and direct, without filler, hype words or heavy formatting. Comments are only for things the code doesn't already say.

## Project layout

| Folder | What's in it |
|---|---|
| `src/Reverb.Format` | Reading and writing the `.cs` notebook format, upgrades, conversion |
| `src/Reverb.Analysis` | Roslyn analysis: dependencies, diagnostics, code fixes |
| `src/Reverb.Kernel` | Compiles and runs cells in the kernel process |
| `src/Reverb.Runtime` | The small library notebooks reference: `Dump`, `Ui`, `Chart` |
| `src/Reverb.Server` | The browser editor's server, and `reverb serve --stdio` |
| `src/Reverb.Cli` | The `reverb` command |
| `frontend` | The browser editor (React and CodeMirror) |
| `vscode` | The VS Code extension |
| `tests` | xUnit tests, fixtures, the consistency fuzzer, and the Playwright tests in `tests/e2e` |

## Releases

The maintainer publishes releases. Pushing a `v1.2.3` tag publishes the `Reverb.Notebooks` and `Reverb.Runtime` NuGet packages. Pushing `vscode-v1.2.3` publishes the extension. You don't need to bump versions in a pull request.

## License

By contributing you agree that your work is released under the [MIT license](LICENSE).
