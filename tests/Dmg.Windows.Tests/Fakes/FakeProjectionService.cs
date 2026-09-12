using Dmg.Core;
using Dmg.Core.Projection;
using Dmg.Windows.Projection;

namespace Dmg.Windows.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IProjectionService"/> that behaves the way ProjFS
/// behaves, written by hand.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written for the same reasons as
/// <see cref="FakeVirtualDiskService"/>: a mocking framework would be a
/// third-party package, and a mock that replays whatever the test told it to
/// cannot enforce the awkward parts of the contract. Those are exactly what the
/// projection sequence has to get right - that a root already holding files is
/// refused, that stopping twice is not an error, that disposing a session stops
/// it - so this fake enforces them.
/// </para>
/// <para>
/// What the real service can refuse to do, this one can be told to refuse too, by
/// assigning a <see cref="DmgError"/> to one of the failure properties. ProjFS
/// being switched off on the machine is <see cref="AvailabilityFailure"/>, and it
/// is the case most worth exercising, because it is the one a user is most likely
/// to hit and the one that must not arrive as an HRESULT.
/// </para>
/// </remarks>
public sealed class FakeProjectionService : IProjectionService
{
    private readonly List<string> _calls = [];
    private readonly HashSet<string> _nonEmptyRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FakeProjectionSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every call made through this service, in order.</summary>
    public IReadOnlyList<string> Calls => _calls;

    /// <summary>The sessions this fake has started, running or stopped.</summary>
    public IReadOnlyCollection<FakeProjectionSession> Sessions => _sessions.Values;

    /// <summary>The sessions still running.</summary>
    public IEnumerable<FakeProjectionSession> Running =>
        _sessions.Values.Where(session => session.IsRunning);

    /// <summary>When set, <see cref="EnsureAvailable"/> fails with this error.</summary>
    public DmgError? AvailabilityFailure { get; set; }

    /// <summary>When set, every <see cref="Start"/> fails with this error.</summary>
    public DmgError? StartFailure { get; set; }

    /// <summary>When set, every <see cref="IProjectionSession.Stop"/> fails with this error.</summary>
    public DmgError? StopFailure { get; set; }

    /// <summary>
    /// Tells the fake that <paramref name="rootPath"/> already holds files, so
    /// projecting over it must be refused.
    /// </summary>
    public void MarkRootNotEmpty(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        _nonEmptyRoots.Add(rootPath);
    }

    /// <inheritdoc />
    public Result EnsureAvailable()
    {
        _calls.Add("EnsureAvailable");

        return AvailabilityFailure is null ? Result.Success() : Result.Failure(AvailabilityFailure);
    }

    /// <inheritdoc />
    public Result<IProjectionSession> Start(ProjectionOptions options, IProjectedContent content)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(content);

        _calls.Add($"Start({options.RootPath})");

        if (StartFailure is not null)
        {
            return Result<IProjectionSession>.Failure(StartFailure);
        }

        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            return Result<IProjectionSession>.Failure(DmgError.Usage(
                "A projection needs a directory to appear under.",
                "No root path was given."));
        }

        if (_nonEmptyRoots.Contains(options.RootPath))
        {
            return Result<IProjectionSession>.Failure(DmgError.Usage(
                $"'{options.RootPath}' already has files in it.",
                "A projection root must be empty, so that what appears there is only the image."));
        }

        if (_sessions.TryGetValue(options.RootPath, out FakeProjectionSession? existing)
            && existing.IsRunning)
        {
            return Result<IProjectionSession>.Failure(DmgError.Usage(
                $"'{options.RootPath}' is already being projected.",
                "Stop the running projection before starting another at the same root."));
        }

        FakeProjectionSession session = new(options, content, this);
        _sessions[options.RootPath] = session;

        return Result<IProjectionSession>.Success(session);
    }

    /// <summary>Records a stop, and reports the configured failure if there is one.</summary>
    internal Result RecordStop(string rootPath)
    {
        _calls.Add($"Stop({rootPath})");

        return StopFailure is null ? Result.Success() : Result.Failure(StopFailure);
    }

    /// <summary>One projection this fake is serving.</summary>
    public sealed class FakeProjectionSession : IProjectionSession
    {
        private readonly FakeProjectionService _service;

        internal FakeProjectionSession(
            ProjectionOptions options,
            IProjectedContent content,
            FakeProjectionService service)
        {
            Options = options;
            Content = content;
            _service = service;
            IsRunning = true;
        }

        /// <summary>What the projection was started with.</summary>
        public ProjectionOptions Options { get; }

        /// <summary>The contents being projected, for a test to interrogate.</summary>
        public IProjectedContent Content { get; }

        /// <inheritdoc />
        public string RootPath => Options.RootPath;

        /// <inheritdoc />
        public bool IsRunning { get; private set; }

        /// <summary>How many times <see cref="Stop"/> has been called.</summary>
        public int StopCount { get; private set; }

        /// <inheritdoc />
        public Result Stop()
        {
            StopCount++;

            if (!IsRunning)
            {
                // Stopping twice is not an error. The second call did nothing and
                // says so by succeeding.
                return Result.Success();
            }

            Result stopped = _service.RecordStop(RootPath);

            if (stopped.Ok)
            {
                IsRunning = false;
            }

            return stopped;
        }

        /// <inheritdoc />
        public void Dispose() => Stop();
    }
}
