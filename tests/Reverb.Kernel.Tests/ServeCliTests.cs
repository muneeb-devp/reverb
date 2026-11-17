using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Reverb.Testing;

namespace Reverb.Kernel.Tests;

/// <summary><c>reverb serve --stdio</c> as an editor extension starts it: JSON lines over the real stdin and stdout.</summary>
[Collection("cli")]
public class ServeCliTests
{
    [Fact]
    public async Task Serve_stdio_runs_a_notebook()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reverb-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "Directory.Build.props"), "<Project />");
        var text = NotebookText.Build(["var a = 1;", "var b = a * 10;", "Console.WriteLine(b + 1);"]);
        var path = Path.Combine(dir, "chain.cs");
        await File.WriteAllTextAsync(path, text);

        using var serve = Cli.StartReverb(dir, "serve", "--stdio");
        var stderr = serve.StandardError.ReadToEndAsync();
        var lines = Channel.CreateUnbounded<string>();
        var reading = Task.Run(async () =>
        {
            while (await serve.StandardOutput.ReadLineAsync() is { } line) lines.Writer.TryWrite(line);
            lines.Writer.TryComplete();
        });
        var seen = new List<string>();
        var c3 = new StringBuilder();

        async Task<JsonElement> NextAsync(Func<JsonElement, bool> match, string what)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                while (true)
                {
                    var line = await lines.Reader.ReadAsync(timeout.Token);
                    seen.Add(line);
                    JsonElement m;
                    try
                    {
                        m = JsonDocument.Parse(line).RootElement;
                    }
                    catch (JsonException)
                    {
                        throw new Xunit.Sdk.XunitException($"stdout had a line that isn't JSON: {line}");
                    }
                    Assert.Equal(JsonValueKind.Object, m.ValueKind);
                    Assert.True(m.TryGetProperty("type", out _), $"A message without a type: {line}");
                    if (Str(m, "type") == "cell.stream" && Str(m, "cellId") == "c3" && Str(m, "stream") == "stdout") c3.Append(Str(m, "text"));
                    if (match(m)) return m;
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ChannelClosedException)
            {
                if (!serve.HasExited) serve.Kill(entireProcessTree: true);
                throw new TimeoutException($"No {what} from reverb serve. stdout:\n{string.Join("\n", seen)}\nstderr:\n{await stderr}");
            }
        }

        try
        {
            await SendAsync(serve, new { type = "hello", requestId = 1 });
            var hello = await NextAsync(m => Str(m, "type") == "hello.result", "hello.result");
            Assert.Equal(1, hello.GetProperty("protocolVersion").GetInt32());
            Assert.Equal(1, hello.GetProperty("requestId").GetInt32());

            await SendAsync(serve, new { type = "notebook.open", notebook = path, text });
            await SendAsync(serve, new { type = "notebook.runAll", notebook = path });
            var snapshot = await NextAsync(m => Str(m, "type") == "notebook.snapshot", "snapshot");
            Assert.Equal(path, Str(snapshot, "notebook"));
            await NextAsync(_ => c3.ToString().ReplaceLineEndings("\n") == "11\n", "\"11\" from c3");
        }
        finally
        {
            serve.StandardInput.Close();
        }

        // Closing stdin shuts the server down: notebooks closed, kernels stopped, exit code 0.
        using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await serve.WaitForExitAsync(exit.Token);
        await reading;
        foreach (var line in await ReadRestAsync(lines.Reader))
        {
            seen.Add(line);
            Assert.True(IsJsonObject(line), $"stdout had a line that isn't a JSON message: {line}");
        }
        Assert.True(serve.ExitCode == 0, $"exit code {serve.ExitCode}; stderr:\n{await stderr}");
        Assert.True(File.ReadAllText(path) == text, "serve --stdio wrote the notebook file; the editor owns it.");
    }

    static string? Str(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool IsJsonObject(string line)
    {
        try
        {
            return JsonDocument.Parse(line).RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static async Task<List<string>> ReadRestAsync(ChannelReader<string> reader)
    {
        var rest = new List<string>();
        await foreach (var line in reader.ReadAllAsync()) rest.Add(line);
        return rest;
    }

    static async Task SendAsync(System.Diagnostics.Process process, object message)
    {
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(message) + "\n");
        await process.StandardInput.FlushAsync();
    }
}
