using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Reverb.Kernel;

/// <summary>
/// The kernel process's entry point. The CLI calls it for <c>reverb __kernel</c>, and a kernel built
/// for a notebook's packages calls it from its own Main.
/// </summary>
public static class KernelProcess
{
    /// <summary>
    /// Written next to a package kernel's entry assembly when it's built: the package assemblies
    /// cells compile against, one path per line.
    /// </summary>
    public const string ReferencesFile = "reverb-references.txt";

    /// <summary>Written next to it too: the analyzer assemblies the build used, whose source generators cells run.</summary>
    public const string AnalyzersFile = "reverb-analyzers.txt";

    public static Task<int> RunAsync()
    {
        KernelTrace.Enable();
        KernelTrace.Mark("main");
        return KernelServer.RunAsync(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)), ProtocolOutput());
    }

    /// <summary>
    /// The process's stdout, for protocol messages. On Unix the console stream locks Console.Out on
    /// every write, and Console.Out is the cells' writer: a cell printing while another thread sends
    /// an event would deadlock. A plain file stream on descriptor 1 takes no such lock.
    /// </summary>
    public static Stream ProtocolOutput() => OperatingSystem.IsWindows()
        ? Console.OpenStandardOutput()
        : new FileStream(new SafeFileHandle(1, ownsHandle: false), FileAccess.Write, bufferSize: 0);

    /// <summary>The package assemblies this kernel was built with. Empty for the plain kernel.</summary>
    public static IReadOnlyList<string> PackageReferences() => ReadPaths(Path.Combine(AppContext.BaseDirectory, ReferencesFile));

    /// <summary>The analyzer assemblies this kernel was built with. Empty for the plain kernel.</summary>
    public static IReadOnlyList<string> PackageAnalyzers() => ReadPaths(Path.Combine(AppContext.BaseDirectory, AnalyzersFile));

    internal static IReadOnlyList<string> ReadPaths(string file) =>
        File.Exists(file) ? [.. File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0)] : [];
}
