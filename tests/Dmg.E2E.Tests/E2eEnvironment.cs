namespace Dmg.E2E.Tests;

/// <summary>
/// The one reason every test in this suite may decline to do anything: it is not
/// running on Windows.
/// </summary>
/// <remarks>
/// This whole suite proves a Windows-only fact - that <c>virtdisk.dll</c> and the
/// real <c>dmg.exe</c> behave the way the code assumes. <c>Dmg.E2E.Tests.csproj</c>
/// cross-builds on macOS like every other <c>net10.0-windows</c> project in this
/// repo, but there is nothing for these tests to do there: no <c>virtdisk.dll</c>
/// exists to call, and a mount attempt would not fail informatively, it would throw
/// <see cref="System.DllNotFoundException"/> out of the real Windows P/Invoke layer.
/// So every test checks this first, before touching <see cref="DmgProcess"/> or a
/// fixture, the same way <c>EncryptedFixtures.Skip</c> in <c>Dmg.Core.Tests</c>
/// checks fixture availability first - this repo's established idiom for "cannot
/// prove anything here, and says so" rather than crashing or reporting a false pass.
/// </remarks>
internal static class E2eEnvironment
{
    /// <summary>True when this suite cannot run here; <paramref name="reason"/> says why.</summary>
    public static bool Skip(out string? reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            reason = "Dmg.E2E.Tests needs a real Windows virtdisk.dll to mount anything; this is "
                + $"{Environment.OSVersion.Platform}. Run the 'e2e' CI job, or this suite on a Windows box.";

            return true;
        }

        reason = null;
        return false;
    }
}
