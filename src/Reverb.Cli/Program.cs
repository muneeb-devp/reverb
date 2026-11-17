using System.Reflection;
using Reverb.Kernel;

namespace Reverb.Cli;

static class Program
{
    const string Usage = """
        Usage: reverb <command> [options]

        Commands:
          exec <file.cs> [--json] [-- args]   Run a notebook headlessly and print its outputs.
                                             Exits non-zero if any cell fails.
          check <file.cs>                    Report compiler and notebook diagnostics.
                                             Exits non-zero if there are errors.
          new <file.cs>                      Create a notebook and open it in the editor.
          edit <file.cs>                     Open a notebook in the editor (a local web page).
              --port <n>                     Port to listen on (default: any free port).
              --no-browser                   Print the address instead of opening a browser.
          run <file.cs>                      Serve a notebook as a read-only app: outputs and
                                             widgets only, code hidden.
              --port <n>, --no-browser       As for edit.
              --host <address>               Listen on this address (default 127.0.0.1).
          export <file.cs> --html            Run a notebook and write a self-contained HTML page
                                             of its outputs (-o out.html, --no-code).
          convert <file.ipynb | file.dib>    Convert a Jupyter or Polyglot notebook to a Reverb
                                             notebook (-o out.cs, --force to overwrite).
          serve --stdio                      Talk to an editor extension over stdin and stdout.

        Options:
          --version                          Print the version.
          --help                             Print this help.

        A notebook also runs without Reverb: dotnet run file.cs
        """;

    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        switch (args[0])
        {
            case "__kernel":
                return await KernelProcess.RunAsync();
            case "exec":
                return await ExecCommand.RunAsync(args[1..]);
            case "check":
                return await CheckCommand.RunAsync(args[1..]);
            case "--version" or "-v" or "version":
                Console.WriteLine(Version);
                return 0;
            case "--help" or "-h" or "help":
                Console.WriteLine(Usage);
                return 0;
            case "edit":
                return await EditCommand.RunEditAsync(args[1..]);
            case "new":
                return await EditCommand.RunNewAsync(args[1..]);
            case "run":
                return await EditCommand.RunAppAsync(args[1..]);
            case "export":
                return await ExportCommand.RunAsync(args[1..]);
            case "convert":
                return ConvertCommand.Run(args[1..]);
            case "serve":
                return await ServeCommand.RunAsync(args[1..]);
            default:
                Console.Error.WriteLine($"Unknown command '{args[0]}'.\n");
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }

    public static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>The Reverb.Runtime version new or upgraded notebooks reference.</summary>
    public static string RuntimeVersion
    {
        get
        {
            // The package version, prerelease label included ("0.2.0-beta.1"), without the commit hash.
            var informational = typeof(Ui).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
            if (!string.IsNullOrEmpty(informational)) return informational;
            var v = typeof(Ui).Assembly.GetName().Version ?? new Version(0, 1, 0);
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
