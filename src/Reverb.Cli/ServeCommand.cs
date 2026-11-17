using System.Runtime.InteropServices;
using System.Text;
using Reverb.Kernel;
using Reverb.Server;

namespace Reverb.Cli;

/// <summary><c>reverb serve --stdio</c>: the process an editor extension starts and talks to over stdin and stdout.</summary>
static class ServeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["--stdio"])
        {
            Console.Error.WriteLine("Usage: reverb serve --stdio\nThe editor extension starts this itself; it talks JSON over stdin and stdout.");
            return 2;
        }

        // stdout carries only protocol messages. Anything else written to the console goes to stderr.
        var output = KernelProcess.ProtocolOutput();
        Console.SetOut(Console.Error);
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        using var stop = new CancellationTokenSource();
        void Stop(PosixSignalContext ctx)
        {
            ctx.Cancel = true;
            stop.Cancel();
        }
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);

        var server = new StdioServer(path => new EditorOptions
        {
            NotebookPath = path,
            KernelEntryAssembly = typeof(Program).Assembly.Location,
            RuntimeVersion = Program.RuntimeVersion,
            Log = Console.Error,
        }, Console.Error);
        return await server.RunAsync(input, output, stop.Token);
    }
}
