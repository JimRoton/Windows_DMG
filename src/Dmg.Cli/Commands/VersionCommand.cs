using System.Text.Json;
using Dmg.Cli.Output;
using Dmg.Cli.Parsing;
using Dmg.Core;

namespace Dmg.Cli.Commands;

/// <summary>
/// <c>dmg version</c> - which build this is, and what it was built for.
/// </summary>
/// <remarks>
/// <para>
/// The architecture is the part that earns this verb its place. dmg is a NativeAOT
/// binary with a separate x64 and arm64 build, and an x64 build running under
/// emulation on an arm64 machine is a real situation that produces confusing bug
/// reports. The version screen says which build is running and, when they differ,
/// what the machine underneath actually is.
/// </para>
/// <para>
/// It takes no options of its own; <c>--json</c> is global, and the whole screen is
/// available as one object for a script that wants to gate on the version.
/// </para>
/// </remarks>
public sealed class VersionCommand : ICliCommand
{
    /// <inheritdoc />
    public string Verb => "version";

    /// <inheritdoc />
    public string Summary => "Show the version and the architecture this build is for.";

    /// <inheritdoc />
    public CommandLineSpec Spec { get; } = new(
        "version",
        [],
        positionals: null,
        notes:
        [
            "With --json the same information is written to stdout as one object.",
        ]);

    /// <inheritdoc />
    public DmgExitCode Execute(CliContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Result<ParsedArguments> parsed = ArgumentParser.Parse(Spec, context.Arguments);

        if (!parsed.TryGetValue(out ParsedArguments? arguments))
        {
            context.Output.Error(parsed.Error);

            return parsed.Error.Code;
        }

        if (arguments.Positionals.Count > 0)
        {
            context.Output.Error(DmgError.Usage(
                $"'version' takes no arguments, but got '{arguments.Positionals[0]}'.",
                $"Usage: {Spec.UsageLine}"));

            return DmgExitCode.UsageError;
        }

        BuildInfo build = BuildInfo.Current;

        if (context.Output.IsJson)
        {
            context.Output.WriteJson(JsonSerializer.Serialize(
                VersionPayload.From(build),
                CliJson.Readable.VersionPayload));

            return DmgExitCode.Success;
        }

        context.Output.WriteLine(build.Headline);

        int width = build.Details.Max(detail => detail.Label.Length) + 3;

        foreach ((string label, string value) in build.Details)
        {
            context.Output.WriteLine($"  {label.PadRight(width)}{value}");
        }

        return DmgExitCode.Success;
    }
}
