using Dmg.Cli.Commands;
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

    internal FakeCommand(string verb, Func<CliContext, DmgExitCode> body)
    {
        Verb = verb;
        _body = body;
    }

    /// <inheritdoc />
    public string Verb { get; }

    /// <inheritdoc />
    public string Summary => $"The {Verb} test double.";

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
