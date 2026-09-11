using System.Text;
using Dmg.Cli.Commands;
using Dmg.Cli.Output;
using Dmg.Cli.Parsing;
using Dmg.Core.Diagnostics;

namespace Dmg.Cli.Help;

/// <summary>
/// Renders the two help screens - the top-level one and one verb's - from the same
/// tables the parser uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is a hand-written list of options.</b> Every line comes out of
/// an <see cref="OptionSpec"/> or a <see cref="CommandLineSpec"/>, which is also
/// what <see cref="ArgumentParser"/> parses against. A CLI whose help is a string
/// literal ships a lie the first time someone renames an option, and the lie is
/// worse than no help at all because the user believes it.
/// </para>
/// <para>
/// <b>It returns strings; it does not print.</b> Where help goes - stdout when the
/// user asked for it, stderr when it is the tail of a usage error - is a decision
/// for the caller, and keeping it out of here is what lets a test assert on the
/// exact text without a sink in the way.
/// </para>
/// <para>
/// The two columns are aligned to the widest entry rather than to a fixed number,
/// so adding a long option name reflows the block instead of pushing one line out
/// of alignment with the rest.
/// </para>
/// </remarks>
public static class HelpText
{
    /// <summary>The gap between the syntax column and the description column.</summary>
    private const int ColumnGap = 3;

    /// <summary>The left margin every listed line carries.</summary>
    private const string Indent = "  ";

    /// <summary>
    /// One line under the tool's name, used at the top of the top-level help.
    /// </summary>
    public const string Tagline = "dmg - read and mount Apple disk images on Windows.";

    /// <summary>
    /// The whole-tool help: what the tool is, the verbs it has, the switches that
    /// belong to the tool rather than a verb, and where to go next.
    /// </summary>
    /// <param name="registry">The verbs this build knows.</param>
    public static string ForTool(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        StringBuilder text = new();

        text.AppendLine(Tagline);
        text.AppendLine();
        text.AppendLine("Usage: dmg [OPTIONS] <command> [ARGUMENTS]");

        if (registry.Commands.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Commands:");
            AppendColumns(
                text,
                [.. registry.Commands.Select(command => (command.Verb, command.Summary))]);
        }

        text.AppendLine();
        text.AppendLine("Global options:");
        AppendColumns(text, [.. GlobalOptions.Specs.Select(Describe), HelpOption]);

        text.AppendLine();
        text.AppendLine("Run 'dmg help <command>' for what one command takes.");

        return text.ToString();
    }

    /// <summary>
    /// One verb's help: what it is for, how it is spelled, and every option it
    /// accepts.
    /// </summary>
    /// <param name="command">The verb to describe.</param>
    public static string ForCommand(ICliCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        CommandLineSpec spec = command.Spec;
        StringBuilder text = new();

        text.AppendLine($"dmg {command.Verb} - {command.Summary}");
        text.AppendLine();
        text.AppendLine($"Usage: {spec.UsageLine}");

        if (spec.Options.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Options:");
            AppendColumns(text, [.. spec.Options.Select(Describe)]);
        }

        text.AppendLine();
        text.AppendLine("Global options:");
        AppendColumns(text, [.. GlobalOptions.Specs.Select(Describe), HelpOption]);

        foreach (string note in spec.Notes)
        {
            text.AppendLine();

            foreach (string line in TextWrap.Wrap(note, TextWrap.LineWidth))
            {
                text.AppendLine(line);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes a help screen to stdout, one line at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Help that was asked for is a result, and results go to stdout - so
    /// <c>dmg help &gt; commands.txt</c> captures it and <c>dmg info --help | more</c>
    /// pages it. Help that is the consequence of a mistake is not printed at all:
    /// what goes to stderr there is the one line saying what was wrong, plus
    /// <see cref="Pointer"/>. A wall of options on top of an error message buries
    /// the only line the user needed to read.
    /// </para>
    /// <para>
    /// Line at a time rather than one blob, because the sink is the thing that
    /// knows what a line ending is.
    /// </para>
    /// </remarks>
    /// <param name="output">Where to write.</param>
    /// <param name="text">A screen from <see cref="ForTool"/> or <see cref="ForCommand"/>.</param>
    public static void WriteTo(IOutput output, string text)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(text);

        foreach (string line in text.TrimEnd('\r', '\n').Split('\n'))
        {
            output.WriteLine(line.TrimEnd('\r'));
        }
    }

    /// <summary>
    /// The one line printed after a usage error, pointing at the help rather than
    /// reprinting it - a wall of options on top of the message that says what went
    /// wrong buries the message.
    /// </summary>
    /// <param name="verb">The verb the user was trying to run, or null.</param>
    public static string Pointer(string? verb) =>
        string.IsNullOrEmpty(verb)
            ? "Run 'dmg help' to see the commands."
            : $"Run 'dmg help {verb}' for what it takes.";

    /// <summary>
    /// <c>--help</c> as an <see cref="OptionSpec"/>. It is not in any verb's spec -
    /// the dispatcher answers it before a verb's parse runs - but it has to appear
    /// in the listing, because an option a user cannot discover may as well not
    /// exist.
    /// </summary>
    public static OptionSpec Help { get; } = new("help", "Show this help and stop.", 'h');

    private static (string Syntax, string Description) HelpOption => Describe(Help);

    private static (string Syntax, string Description) Describe(OptionSpec option) =>
        // Long-only options are pushed right by the width of "-x, " so that every
        // long form starts in the same column - otherwise --json sits under -q and
        // the block reads as two lists rather than one.
        (option.ShortForm is null ? "    " + option.Syntax : option.Syntax, option.Description);

    private static void AppendColumns(
        StringBuilder text,
        IReadOnlyList<(string Syntax, string Description)> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        int width = rows.Max(row => row.Syntax.Length) + ColumnGap;

        foreach ((string syntax, string description) in rows)
        {
            text.Append(Indent).Append(syntax.PadRight(width)).AppendLine(description);
        }
    }
}
