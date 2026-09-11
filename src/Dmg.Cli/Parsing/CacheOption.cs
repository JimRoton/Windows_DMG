using Dmg.Core;
using Dmg.Core.Imaging;

namespace Dmg.Cli.Parsing;

/// <summary>
/// The <c>--cache MB</c> option shared by every verb that opens an image:
/// <c>mount</c>, <c>extract</c> and <c>verify</c> all read a
/// <see cref="DmgBlockStream"/>, so all three take the same knob for its chunk
/// cache's byte budget rather than three slightly different ones.
/// </summary>
public static class CacheOption
{
    /// <summary>The long name, without dashes.</summary>
    public const string Name = "cache";

    /// <summary>The option, ready to add to a verb's <see cref="OptionSpec"/> list.</summary>
    public static OptionSpec Spec { get; } = new(
        Name,
        "The chunk-cache byte budget in MB, instead of the "
        + $"{DmgBlockStream.DefaultCacheCapacityBytes / (1024 * 1024)} MB default.",
        ValueName: "MB");

    /// <summary>
    /// Reads <c>--cache</c>, in megabytes, and converts it to the byte budget
    /// <see cref="DmgBlockStream"/> takes.
    /// </summary>
    /// <returns>
    /// The byte budget, or null when the option was not given - the caller then
    /// leaves <see cref="DmgBlockStream"/> to its own default. A value that is not
    /// a whole number, or is zero or negative, is a usage failure.
    /// </returns>
    public static Result<long?> BytesFor(ParsedArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        Result<int?> parsed = arguments.TryGetInt32(Name);

        if (!parsed.TryGetValue(out int? megabytes))
        {
            return parsed.CastFailure<long?>();
        }

        if (megabytes is null)
        {
            return Result<long?>.Success(null);
        }

        if (megabytes <= 0)
        {
            return Result<long?>.Failure(DmgError.Usage(
                $"--{Name} must be a positive number of megabytes, but got {megabytes}.",
                "A zero or negative chunk-cache budget is not something DmgBlockStream accepts."));
        }

        return Result<long?>.Success(checked((long)megabytes.Value * 1024 * 1024));
    }
}
