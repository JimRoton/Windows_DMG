using Dmg.Cli.Commands;
using Dmg.Cli.Parsing;
using Dmg.Core;

namespace Dmg.Cli.Tests.Commands;

/// <summary>
/// A verb that records what it was handed and returns whatever the test asked for -
/// or throws, for the tests that prove a bug in a verb becomes exit 1 rather than a
/// stack trace.
/// </summary>
internal sealed class FakeCommand : ICliCommand
{
    private readonly Func<CliContext, DmgExitCode> _body;

    internal FakeCommand(string verb, DmgExitCode result = DmgExitCode.Success)
        : this(verb, _ => result)
    {
    }

    internal FakeCommand(string verb, CommandLineSpec spec)
        : this(verb, _ => DmgExitCode.Success, spec)
    {
    }

    internal FakeCommand(string verb, Func<CliContext, DmgExitCode> body, CommandLineSpec? spec = null)
    {
        Verb = verb;
        _body = body;
        Spec = spec ?? new CommandLineSpec(verb, []);
    }

    /// <inheritdoc />
    public string Verb { get; }

    /// <inheritdoc />
    public string Summary => $"The {Verb} test double.";

    /// <inheritdoc />
    public CommandLineSpec Spec { get; }

    /// <summary>How many times the verb ran.</summary>
    internal int Calls { get; private set; }

    /// <summary>The arguments the dispatcher handed over on the last call.</summary>
    internal IReadOnlyList<string> LastArguments { get; private set; } = [];

    /// <inheritdoc />
    public DmgExitCode Execute(CliContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Calls++;
        LastArguments = context.Arguments;

        return _body(context);
    }
}
