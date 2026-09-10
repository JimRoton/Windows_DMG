using Dmg.Core;
using Dmg.Windows.Elevation;

namespace Dmg.Windows.Tests.Fakes;

/// <summary>
/// A process token that never existed, so the elevation check can be exercised on a
/// machine that has no tokens.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written, like <see cref="FakeVirtualDiskService"/> and for the same reasons:
/// shipping code here has no third-party packages, and a mock that replays whatever
/// the test told it to cannot enforce the parts of the contract that matter. This
/// one does - an unknown privilege comes back <see cref="PrivilegeState.Absent"/>
/// rather than throwing, because that is what a real token does, and names are
/// matched case-sensitively, because <c>LookupPrivilegeValue</c> is.
/// </para>
/// <para>
/// The two factories are the two states anybody cares about: a shell started with
/// "Run as administrator", and one that was not.
/// </para>
/// </remarks>
public sealed class FakePrivilegeService : IPrivilegeService
{
    private readonly Dictionary<string, PrivilegeState> _states = new(StringComparer.Ordinal);
    private readonly List<string> _calls = [];

    /// <summary>Every privilege asked about, in order - for tests that care that the check is cheap.</summary>
    public IReadOnlyList<string> Calls => _calls;

    /// <summary>When set, every <see cref="Query"/> fails with this error.</summary>
    public DmgError? QueryFailure { get; set; }

    /// <summary>
    /// An elevated shell: it holds the Manage Volume privilege, switched off, which
    /// is how Windows actually hands it over.
    /// </summary>
    public static FakePrivilegeService Elevated() =>
        new FakePrivilegeService().Holding(WindowsPrivilege.ManageVolume, PrivilegeState.Disabled);

    /// <summary>An ordinary shell: it holds nothing.</summary>
    public static FakePrivilegeService Unelevated() => new();

    /// <summary>Adds a privilege to this token.</summary>
    /// <param name="privilegeName">The <c>winnt.h</c> name.</param>
    /// <param name="state">The state to report for it.</param>
    public FakePrivilegeService Holding(string privilegeName, PrivilegeState state = PrivilegeState.Enabled)
    {
        _states[privilegeName] = state;
        return this;
    }

    /// <inheritdoc />
    public Result<PrivilegeState> Query(string privilegeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegeName);

        _calls.Add(privilegeName);

        if (QueryFailure is not null)
        {
            return Result<PrivilegeState>.Failure(QueryFailure);
        }

        return Result<PrivilegeState>.Success(
            _states.TryGetValue(privilegeName, out PrivilegeState state) ? state : PrivilegeState.Absent);
    }
}
