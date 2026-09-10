using System.Diagnostics.CodeAnalysis;

namespace Dmg.Core;

/// <summary>
/// The outcome of an operation that produces a <typeparamref name="T"/>: either a
/// value or a <see cref="DmgError"/>, never both and never neither.
/// </summary>
/// <typeparam name="T">The value produced on success.</typeparam>
/// <remarks>
/// <para>
/// This type exists so that parsing a hostile <c>.dmg</c> does not become an
/// exercise in exception handling. Every expected failure - bad magic, a codec we
/// do not implement, a wrong passphrase - is a value flowing back up the call
/// stack with an exit code already attached. Exceptions are reserved for bugs.
/// </para>
/// <para>
/// <b>Defined behaviour on misuse.</b> Reading <see cref="Value"/> on a failed
/// result throws <see cref="InvalidOperationException"/>; it does not quietly hand
/// back <c>default</c>. A zeroed <c>int</c> or a null span looks exactly like real
/// data, and a silent default would turn a caught error back into a corruption bug
/// somewhere further along. Callers that genuinely want the lenient read have
/// <see cref="GetValueOrDefault"/> and <see cref="TryGetValue"/>.
/// </para>
/// <para>
/// Reading <see cref="Error"/> on a successful result throws for the same reason.
/// <c>default(Result&lt;T&gt;)</c> is a failure carrying an
/// <see cref="DmgExitCode.InternalError"/>, so an unassigned result can never be
/// mistaken for a success.
/// </para>
/// </remarks>
public readonly struct Result<T> : IEquatable<Result<T>>
{
    private static readonly DmgError Uninitialised = new(
        DmgExitCode.InternalError,
        "An operation produced no result.",
        $"default(Result<{typeof(T).Name}>) was used before it was assigned.");

    private readonly T? _value;
    private readonly DmgError? _error;

    private Result(bool ok, T? value, DmgError? error)
    {
        Ok = ok;
        _value = value;
        _error = error;
    }

    /// <summary>True when the operation succeeded and <see cref="Value"/> is readable.</summary>
    public bool Ok { get; }

    /// <summary>
    /// The produced value.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result is a failure.</exception>
    public T? Value => Ok
        ? _value
        : throw new InvalidOperationException(
            $"Result<{typeof(T).Name}>.Value was read on a failed result: {Error}");

    /// <summary>
    /// The failure.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result is a success.</exception>
    public DmgError Error => Ok
        ? throw new InvalidOperationException(
            $"Result<{typeof(T).Name}>.Error was read on a successful result. Check Ok first.")
        : _error ?? Uninitialised;

    /// <summary>Wraps a value as a successful result.</summary>
    public static Result<T> Success(T value) => new(ok: true, value, error: null);

    /// <summary>Wraps an error as a failed result.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public static Result<T> Failure(DmgError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(ok: false, value: default, error);
    }

    /// <summary>Builds and wraps a failure in one step.</summary>
    public static Result<T> Failure(DmgExitCode code, string message, string? detail = null) =>
        Failure(new DmgError(code, message, detail));

    /// <summary>The value on success, or <c>default</c> on failure. Never throws.</summary>
    public T? GetValueOrDefault() => Ok ? _value : default;

    /// <summary>The lenient read: true on success, with the value written to <paramref name="value"/>.</summary>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = Ok ? _value! : default;
        return Ok;
    }

    /// <summary>
    /// Drops the value, keeping only success or failure - for callers that ran an
    /// operation for its effect rather than its result.
    /// </summary>
    public Result Discard() => Ok ? Result.Success() : Result.Failure(Error);

    /// <summary>
    /// Re-types a failure so it can be returned from an operation producing a
    /// different value. Propagating errors up the stack without restating them.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result is a success and has no error to carry.</exception>
    public Result<TOther> CastFailure<TOther>() => Result<TOther>.Failure(Error);

    /// <inheritdoc />
    public bool Equals(Result<T> other) =>
        Ok == other.Ok
        && EqualityComparer<T?>.Default.Equals(_value, other._value)
        && Equals(_error, other._error);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Result<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Ok, _value, _error);

    /// <inheritdoc />
    public override string ToString() => Ok ? $"Ok({_value})" : $"Error({_error ?? Uninitialised})";

    public static bool operator ==(Result<T> left, Result<T> right) => left.Equals(right);

    public static bool operator !=(Result<T> left, Result<T> right) => !left.Equals(right);
}

/// <summary>
/// The outcome of an operation that produces nothing: mount, unmount, verify.
/// The void-returning twin of <see cref="Result{T}"/>, with the same rules.
/// </summary>
/// <remarks>
/// <c>default(Result)</c> is a failure carrying an
/// <see cref="DmgExitCode.InternalError"/>, so a forgotten assignment cannot read
/// as success.
/// </remarks>
public readonly struct Result : IEquatable<Result>
{
    private static readonly DmgError Uninitialised = new(
        DmgExitCode.InternalError,
        "An operation produced no result.",
        "default(Result) was used before it was assigned.");

    private readonly DmgError? _error;

    private Result(bool ok, DmgError? error)
    {
        Ok = ok;
        _error = error;
    }

    /// <summary>True when the operation succeeded.</summary>
    public bool Ok { get; }

    /// <summary>
    /// The failure.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result is a success.</exception>
    public DmgError Error => Ok
        ? throw new InvalidOperationException(
            "Result.Error was read on a successful result. Check Ok first.")
        : _error ?? Uninitialised;

    /// <summary>The exit code this outcome maps to.</summary>
    public DmgExitCode ExitCode => Ok ? DmgExitCode.Success : Error.Code;

    /// <summary>A successful outcome.</summary>
    public static Result Success() => new(ok: true, error: null);

    /// <summary>Wraps an error as a failed outcome.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public static Result Failure(DmgError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(ok: false, error);
    }

    /// <summary>Builds and wraps a failure in one step.</summary>
    public static Result Failure(DmgExitCode code, string message, string? detail = null) =>
        Failure(new DmgError(code, message, detail));

    /// <summary>
    /// Re-types a failure so it can be returned from a value-producing operation.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result is a success and has no error to carry.</exception>
    public Result<T> CastFailure<T>() => Result<T>.Failure(Error);

    /// <inheritdoc />
    public bool Equals(Result other) => Ok == other.Ok && Equals(_error, other._error);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Result other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Ok, _error);

    /// <inheritdoc />
    public override string ToString() => Ok ? "Ok" : $"Error({_error ?? Uninitialised})";

    public static bool operator ==(Result left, Result right) => left.Equals(right);

    public static bool operator !=(Result left, Result right) => !left.Equals(right);
}
