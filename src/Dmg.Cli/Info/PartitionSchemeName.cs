using Dmg.Core.Partitions;

namespace Dmg.Cli.Info;

/// <summary>
/// A partition scheme's two names: the one for a person and the one for a script.
/// </summary>
/// <remarks>
/// They are different on purpose. A reader wants "Apple partition map"; a script
/// wants a short stable token it can compare, and <c>PartitionScheme.ToString()</c>
/// is neither - it is a C# identifier, and renaming the enum member would silently
/// change the JSON contract.
/// </remarks>
public static class PartitionSchemeName
{
    /// <summary>The name for the screen.</summary>
    /// <param name="scheme">The scheme.</param>
    public static string Display(PartitionScheme scheme) => scheme switch
    {
        PartitionScheme.GuidPartitionTable => "GPT",
        PartitionScheme.MasterBootRecord => "MBR",
        PartitionScheme.ApplePartitionMap => "Apple partition map",
        PartitionScheme.WholeDisk => "none (whole disk)",
        _ => scheme.ToString(),
    };

    /// <summary>The token for JSON. Lower case, hyphenated, and not the enum name.</summary>
    /// <param name="scheme">The scheme.</param>
    public static string Token(PartitionScheme scheme) => scheme switch
    {
        PartitionScheme.GuidPartitionTable => "gpt",
        PartitionScheme.MasterBootRecord => "mbr",
        PartitionScheme.ApplePartitionMap => "apm",
        PartitionScheme.WholeDisk => "whole-disk",
        _ => "unknown",
    };
}
