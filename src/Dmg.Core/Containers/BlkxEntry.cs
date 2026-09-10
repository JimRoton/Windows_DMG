namespace Dmg.Core.Containers;

/// <summary>
/// One entry of the <c>blkx</c> array: a named region of the decoded disk, with
/// the mish block that describes how to rebuild it.
/// </summary>
/// <param name="Index">Position in the <c>blkx</c> array, for error messages.</param>
/// <param name="Name">The <c>Name</c> string, or empty when absent.</param>
/// <param name="CfName">The <c>CFName</c> string - the human-readable one - or empty.</param>
/// <param name="Attributes">The <c>Attributes</c> string, typically <c>0x0050</c>.</param>
/// <param name="Id">The <c>ID</c> string. <c>-1</c> for the protective MBR.</param>
/// <param name="Data">The decoded <c>Data</c> payload: a mish block, still unparsed.</param>
public sealed record BlkxEntry(
    int Index,
    string Name,
    string CfName,
    string Attributes,
    string Id,
    ReadOnlyMemory<byte> Data)
{
    /// <summary>
    /// The best label to show a user: <c>CFName</c> if there is one, else
    /// <c>Name</c>, else the entry's position.
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(CfName) ? CfName
        : !string.IsNullOrWhiteSpace(Name) ? Name
        : $"blkx[{Index}]";

    /// <inheritdoc />
    public override string ToString() => $"{DisplayName} ({Data.Length} bytes)";
}
