using System.Reflection;
using System.Runtime.InteropServices;

namespace Dmg.Cli;

/// <summary>
/// What this particular <c>dmg.exe</c> is: its version, what it was built for, and
/// what it is running on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the architecture is part of the answer.</b> dmg ships as a NativeAOT
/// binary, so there is an x64 build and an arm64 build and they are different
/// files. A user on an arm64 machine can run the x64 one under emulation without
/// noticing, right up to the point where a driver-adjacent call behaves oddly - and
/// the first question anyone will ask them is which build they have.
/// <see cref="ProcessArchitecture"/> is what this binary was compiled for;
/// <see cref="OsArchitecture"/> is what the machine is. When the two differ, that is
/// the headline, not a footnote.
/// </para>
/// <para>
/// <b>What is deliberately not reported: whether this is the AOT build.</b> It looks
/// like one line of code - <c>RuntimeFeature.IsDynamicCodeSupported</c> - and it is
/// wrong. Setting <c>PublishAot</c> writes that switch, and
/// <c>IsDynamicCodeCompiled</c> with it, into <c>dmg.runtimeconfig.json</c> for
/// every build, so both read false on an ordinary JIT run of <c>dmg.dll</c>.
/// Measured, not assumed. The only reliable discriminator left is
/// <c>Assembly.Location</c> being empty, which this project cannot use because the
/// AOT analyzer makes reading it a build error. So the line is not printed rather
/// than printed wrong: a version screen exists to be quoted in a bug report, and a
/// field that is confidently false is worse than a field that is absent.
/// </para>
/// <para>
/// <b>Everything here is read once, at first use.</b> The values cannot change while
/// the process lives, and <c>version</c> is not the only thing that wants them - a
/// bug report from any verb should be able to name the build.
/// </para>
/// <para>
/// The reflection is limited to attributes on this assembly, which survive both
/// trimming and AOT compilation. Nothing here enumerates types or looks anything up
/// by name.
/// </para>
/// </remarks>
public sealed record BuildInfo
{
    private static BuildInfo? _current;

    private BuildInfo(
        string version,
        string? commit,
        string configuration,
        Architecture processArchitecture,
        Architecture osArchitecture,
        string framework,
        string operatingSystem)
    {
        Version = version;
        Commit = commit;
        Configuration = configuration;
        ProcessArchitecture = processArchitecture;
        OsArchitecture = osArchitecture;
        Framework = framework;
        OperatingSystem = operatingSystem;
    }

    /// <summary>This build, read from the running assembly and the runtime.</summary>
    public static BuildInfo Current => _current ??= Read();

    /// <summary>The tool's name as it is typed and as it is installed.</summary>
    public static string ToolName => "dmg";

    /// <summary>The version on its own: <c>0.1.0</c>.</summary>
    public string Version { get; }

    /// <summary>
    /// The commit the build came from, when the SDK stamped one on, else null. It
    /// is what a bug report actually needs and what a headline does not want, so it
    /// is a field of its own rather than forty characters of noise after the
    /// version.
    /// </summary>
    public string? Commit { get; }

    /// <summary>
    /// <c>Debug</c> or <c>Release</c>. Worth printing: a Debug build is slower in a
    /// way that gets reported as a bug.
    /// </summary>
    public string Configuration { get; }

    /// <summary>The architecture this binary was built for.</summary>
    public Architecture ProcessArchitecture { get; }

    /// <summary>The architecture the machine actually is.</summary>
    public Architecture OsArchitecture { get; }

    /// <summary>The runtime, as it describes itself.</summary>
    public string Framework { get; }

    /// <summary>The operating system, as it describes itself.</summary>
    public string OperatingSystem { get; }

    /// <summary>
    /// True when an x64 build is running on an arm64 machine, or anything else of
    /// that shape. Worth saying out loud rather than leaving the reader to compare
    /// two lines.
    /// </summary>
    public bool IsEmulated => ProcessArchitecture != OsArchitecture;

    /// <summary>
    /// The one line <c>dmg version</c> leads with: <c>dmg 0.1.0 (Release, x64)</c>.
    /// </summary>
    public string Headline =>
        $"{ToolName} {Version} ({Configuration}, {Name(ProcessArchitecture)})";

    /// <summary>The version screen, in the order a bug report wants to read it.</summary>
    public IReadOnlyList<(string Label, string Value)> Details =>
    [
        ("version", Version),
        .. Commit is null ? Array.Empty<(string, string)>() : [("commit", Commit)],
        ("configuration", Configuration),
        ("built for", Name(ProcessArchitecture)),
        ("running on", IsEmulated
            ? $"{Name(OsArchitecture)} - this is a {Name(ProcessArchitecture)} build under emulation"
            : Name(OsArchitecture)),
        ("runtime", Framework),
        ("os", OperatingSystem),
    ];

    /// <summary>An architecture's name in the spelling people actually type.</summary>
    public static string Name(Architecture architecture) => architecture switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x64",
        Architecture.Arm => "arm",
        Architecture.Arm64 => "arm64",
        _ => architecture.ToString().ToLowerInvariant(),
    };

    private static BuildInfo Read()
    {
        Assembly assembly = typeof(BuildInfo).Assembly;

        string stamped =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        // The SDK writes "0.1.0+<commit sha>" when it knows the commit. Split it
        // rather than print it: the version is what a user reads, the sha is what
        // a maintainer needs, and neither is served by running them together.
        int plus = stamped.IndexOf('+', StringComparison.Ordinal);
        string version = plus < 0 ? stamped : stamped[..plus];
        string? commit = plus < 0 || plus == stamped.Length - 1 ? null : stamped[(plus + 1)..];

        return new BuildInfo(
            version.Length == 0 ? "unknown" : version,
            commit,
            assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.OSArchitecture,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription.Trim());
    }
}
