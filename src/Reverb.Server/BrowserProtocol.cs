using System.Text.Json;
using System.Text.Json.Serialization;
using Reverb.Format;
using Reverb.Kernel;

namespace Reverb.Server;

/// <summary>Message types between the browser and the server, over the WebSocket at /ws.</summary>
public static class BrowserMessages
{
    // browser -> server
    public const string CellUpdate = "cell.update";
    public const string CellRun = "cell.run";
    public const string RunAll = "notebook.runAll";
    public const string SetLazy = "notebook.setLazy";
    public const string Save = "notebook.save";
    public const string CellCreate = "cell.create";
    public const string CellDelete = "cell.delete";
    public const string CellMove = "cell.move";
    public const string CellSetAttrs = "cell.setAttrs";
    public const string WidgetSetValue = "widget.setValue";
    public const string KernelInterrupt = "kernel.interrupt";
    public const string KernelRestart = "kernel.restart";
    public const string PackagesSet = "packages.set";
    public const string LangCompletions = "lang.completions";
    public const string LangHover = "lang.hover";
    public const string LangSignatureHelp = "lang.signatureHelp";
    public const string LangCodeFix = "lang.codeFix";

    // A client that owns the file (VS Code) sends the whole cell list, or the file's new text.
    public const string NotebookSync = "notebook.sync";
    public const string NotebookReload = "notebook.reload";

    // server -> browser
    public const string Snapshot = "notebook.snapshot";
    public const string CellStatus = "cell.status";
    public const string CellOutputs = "cell.outputs";
    public const string CellStream = "cell.stream";
    public const string CellDiagnostics = "cell.diagnostics";
    public const string GraphUpdate = "graph.update";
    public const string KernelStatus = "kernel.status";
    public const string FileSaved = "file.saved";
    public const string FileExternalChange = "file.externalChange";
    public const string LangCompletionsResult = "lang.completionsResult";
    public const string LangHoverResult = "lang.hoverResult";
    public const string LangSignatureHelpResult = "lang.signatureHelpResult";

    /// <summary>The answer to a lang.codeFix from a client that owns its file: the fixed cells (PartCells), or Message saying why not.</summary>
    public const string LangCodeFixResult = "lang.codeFixResult";

    /// <summary>The cell list after normalising a notebook.sync, sent back when it had to change.</summary>
    public const string NotebookCells = "notebook.cells";
    public const string Error = "error";
}

/// <summary>A message from the browser. Which fields are set depends on <see cref="Type"/>.</summary>
public sealed class ClientMessage
{
    public string Type { get; set; } = "";

    /// <summary>Increases with every message a page sends. Snapshots echo the last one handled.</summary>
    public long Seq { get; set; }

    public string? CellId { get; set; }
    public string? Source { get; set; }
    public string? AfterCellId { get; set; }

    /// <summary>For cell.create, "code" or "markdown"; for widget.setValue, the widget's kind.</summary>
    public string? Kind { get; set; }
    public CellAttrs? Attrs { get; set; }
    public bool? Lazy { get; set; }
    public string? WidgetId { get; set; }
    public JsonElement? Value { get; set; }

    /// <summary>For packages.set: every package the notebook should have (Reverb.Runtime aside).</summary>
    public List<PackageView>? Packages { get; set; }

    /// <summary>For lang.* requests: echoed in the answer so the page can match them up.</summary>
    public int? RequestId { get; set; }

    /// <summary>For lang.completions/hover/signatureHelp: the caret, as an offset into <see cref="Source"/> (the cell as the page has it now).</summary>
    public int? Offset { get; set; }

    /// <summary>For lang.codeFix: the diagnostic, by id and where it starts in the cell.</summary>
    public string? DiagnosticId { get; set; }
    public int? Line { get; set; }
    public int? Column { get; set; }

    /// <summary>For notebook.sync: every cell, in order, as the client has them.</summary>
    public List<PartCell>? Cells { get; set; }

    /// <summary>For notebook.reload and notebook.open: the notebook's text. For format.parse: the text to parse.</summary>
    public string? Text { get; set; }

    /// <summary>Over stdio: the full path of the notebook the message is for. Unset for format requests and hello.</summary>
    public string? Notebook { get; set; }

    /// <summary>For format.serialize: the document to write.</summary>
    public NotebookPartsData? Parts { get; set; }
}

/// <summary>A message to the browser. Which fields are set depends on <see cref="Type"/>.</summary>
public sealed class ServerMessage
{
    public string Type { get; set; } = "";
    public int? NotebookVersion { get; set; }
    public long? Ack { get; set; }

    // notebook.snapshot
    /// <summary>"edit", or "app" for a read-only app (<c>reverb run</c>).</summary>
    public string? Mode { get; set; }

    public string? Path { get; set; }
    public string? Name { get; set; }
    public bool? Lazy { get; set; }
    public List<CellView>? Cells { get; set; }
    public List<PackageView>? Packages { get; set; }
    public KernelView? Kernel { get; set; }
    public List<DiagnosticData>? NotebookDiagnostics { get; set; }

    /// <summary>The lines before the first cell, exactly as they'll be written: directives, header and all.</summary>
    public List<PreambleLine>? Preamble { get; set; }

    // notebook.cells
    public List<PartCell>? PartCells { get; set; }

    // cell messages
    public string? CellId { get; set; }
    public string? Status { get; set; }
    public int? Runs { get; set; }
    public long? ElapsedMs { get; set; }
    public string? Message { get; set; }
    public List<OutputData>? Outputs { get; set; }
    public string? Stream { get; set; }
    public string? Text { get; set; }
    public List<DiagnosticData>? Diagnostics { get; set; }

    // graph.update, and the graph in a snapshot
    public List<EdgeData>? Edges { get; set; }

    // kernel.status
    public string? State { get; set; }

    // lang.*Result: offsets are into the cell source the request carried. Hover text is Markdown.
    public int? RequestId { get; set; }
    public int? From { get; set; }
    public int? To { get; set; }
    public List<CompletionData>? Completions { get; set; }
    public List<SignatureData>? Signatures { get; set; }
    public int? ActiveSignature { get; set; }
    public int? ActiveParameter { get; set; }

    /// <summary>Over stdio: the notebook a message is about, as the client named it in notebook.open.</summary>
    public string? Notebook { get; set; }

    // stdio: format.parsed, hello.result
    public NotebookPartsData? Parts { get; set; }
    public int? ProtocolVersion { get; set; }
    public string? ReverbVersion { get; set; }
}

public sealed class CellAttrs
{
    [JsonPropertyName("hide_code")]
    public bool? HideCode { get; set; }

    public bool? Disabled { get; set; }

    /// <summary>Empty string removes the name.</summary>
    public string? Name { get; set; }
}

public sealed class CellView
{
    public string Id { get; set; } = "";

    /// <summary>code, markdown, setup or types.</summary>
    public string Kind { get; set; } = "";

    public string Source { get; set; } = "";
    public CellAttrs Attrs { get; set; } = new();
    public string Status { get; set; } = "idle";
    public long? ElapsedMs { get; set; }
    public string? Message { get; set; }
    public List<OutputData> Outputs { get; set; } = [];
    public List<DiagnosticData> Diagnostics { get; set; } = [];

    /// <summary>The source differs from what the kernel last ran.</summary>
    public bool Edited { get; set; }

    /// <summary>How many times the cell has started running since the editor opened.</summary>
    public int Runs { get; set; }
}

public sealed class PackageView
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
}

public sealed class KernelView
{
    /// <summary>building (getting packages), starting, ready, busy or dead.</summary>
    public string State { get; set; } = "starting";

    public string? Message { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClientMessage))]
[JsonSerializable(typeof(ServerMessage))]
[JsonSerializable(typeof(PartCell))]
[JsonSerializable(typeof(PreambleLine))]
public sealed partial class BrowserJson : JsonSerializerContext;
