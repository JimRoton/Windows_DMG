namespace Dmg.Core.Vhd;

/// <summary>
/// Asks the operating system how much room is left on the volume holding a path.
/// </summary>
/// <remarks>
/// A port, for one reason: the precheck's behaviour has to be testable. There is
/// no way to make a real volume have exactly 41 bytes free on demand, so the
/// suite substitutes a probe that says so. The production implementation is
/// <see cref="DriveFreeSpaceProbe"/>, and it is portable - the same
/// <see cref="DriveInfo"/> call answers on Windows and on the Mac this is
/// developed on.
/// </remarks>
public interface IFreeSpaceProbe
{
    /// <summary>
    /// The bytes available to this user on the volume that holds
    /// <paramref name="path"/>, which need not exist yet.
    /// </summary>
    /// <returns>
    /// The free byte count, or a failure when the volume cannot be identified or
    /// interrogated.
    /// </returns>
    Result<long> AvailableBytes(string path);
}
