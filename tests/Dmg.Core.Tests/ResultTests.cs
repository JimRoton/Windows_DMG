namespace Dmg.Core.Tests;

public sealed class ResultTests
{
    private static readonly DmgError SampleError = new(
        DmgExitCode.UnsupportedFormat,
        "LZFSE chunks are not supported.",
        "chunk type 0x80000011");

    // ---- success shape -------------------------------------------------

    [Fact]
    public void SuccessCarriesItsValue()
    {
        Result<int> result = Result<int>.Success(42);

        Assert.True(result.Ok);
        Assert.Equal(42, result.Value);
        Assert.Equal(42, result.GetValueOrDefault());
    }

    [Fact]
    public void SuccessCanCarryAReferenceType()
    {
        Result<string> result = Result<string>.Success("koly");

        Assert.True(result.Ok);
        Assert.Equal("koly", result.Value);
    }

    [Fact]
    public void SuccessCanCarryNullForANullableReferenceType()
    {
        Result<string?> result = Result<string?>.Success(null);

        Assert.True(result.Ok);
        Assert.Null(result.Value);
    }

    [Fact]
    public void ReadingErrorOnASuccessThrows()
    {
        Result<int> result = Result<int>.Success(1);

        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void TryGetValueYieldsTheValueOnSuccess()
    {
        Assert.True(Result<int>.Success(7).TryGetValue(out int value));
        Assert.Equal(7, value);
    }

    // ---- failure shape -------------------------------------------------

    [Fact]
    public void FailureCarriesItsError()
    {
        Result<int> result = Result<int>.Failure(SampleError);

        Assert.False(result.Ok);
        Assert.Same(SampleError, result.Error);
    }

    [Fact]
    public void FailureCanBeBuiltFromTheCodeAndMessageDirectly()
    {
        Result<int> result = Result<int>.Failure(DmgExitCode.CorruptImage, "Bad magic.", "expected 'koly'");

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Equal("Bad magic.", result.Error.Message);
        Assert.Equal("expected 'koly'", result.Error.Detail);
    }

    /// <summary>
    /// Documented behaviour: reading <c>Value</c> on a failure throws rather than
    /// returning <c>default</c>, because a silent zero is indistinguishable from a
    /// real decoded value and would turn a handled error into a corruption bug.
    /// </summary>
    [Fact]
    public void ReadingValueOnAFailureThrows()
    {
        Result<int> result = Result<int>.Failure(SampleError);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => result.Value);

        Assert.Contains("failed result", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetValueOrDefaultIsTheLenientReadOnAFailure()
    {
        Assert.Equal(0, Result<int>.Failure(SampleError).GetValueOrDefault());
        Assert.Null(Result<string>.Failure(SampleError).GetValueOrDefault());
    }

    [Fact]
    public void TryGetValueReportsFailureWithoutThrowing()
    {
        Assert.False(Result<int>.Failure(SampleError).TryGetValue(out int value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void FailureRejectsANullError()
    {
        Assert.Throws<ArgumentNullException>(() => Result<int>.Failure(null!));
    }

    // ---- the default struct --------------------------------------------

    [Fact]
    public void DefaultGenericResultIsAnInternalErrorNotASuccess()
    {
        Result<int> result = default;

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.InternalError, result.Error.Code);
    }

    [Fact]
    public void DefaultNonGenericResultIsAnInternalErrorNotASuccess()
    {
        Result result = default;

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.InternalError, result.Error.Code);
        Assert.Equal(DmgExitCode.InternalError, result.ExitCode);
    }

    // ---- propagation ---------------------------------------------------

    [Fact]
    public void DiscardKeepsTheOutcomeAndDropsTheValue()
    {
        Assert.True(Result<int>.Success(1).Discard().Ok);

        Result discarded = Result<int>.Failure(SampleError).Discard();
        Assert.False(discarded.Ok);
        Assert.Same(SampleError, discarded.Error);
    }

    [Fact]
    public void CastFailureCarriesTheErrorIntoADifferentResultType()
    {
        Result<string> retyped = Result<int>.Failure(SampleError).CastFailure<string>();

        Assert.False(retyped.Ok);
        Assert.Same(SampleError, retyped.Error);
    }

    [Fact]
    public void CastFailureOnASuccessThrowsBecauseThereIsNoErrorToCarry()
    {
        Assert.Throws<InvalidOperationException>(() => Result<int>.Success(1).CastFailure<string>());
    }

    // ---- the non-generic twin ------------------------------------------

    [Fact]
    public void NonGenericSuccessHasNoErrorAndExitsZero()
    {
        Result result = Result.Success();

        Assert.True(result.Ok);
        Assert.Equal(DmgExitCode.Success, result.ExitCode);
        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void NonGenericFailureCarriesItsErrorAndExitCode()
    {
        Result result = Result.Failure(SampleError);

        Assert.False(result.Ok);
        Assert.Same(SampleError, result.Error);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.ExitCode);
    }

    [Fact]
    public void NonGenericFailureCanBeBuiltFromTheCodeAndMessageDirectly()
    {
        Result result = Result.Failure(DmgExitCode.ElevationRequired, "Run this from an elevated shell.");

        Assert.Equal(DmgExitCode.ElevationRequired, result.ExitCode);
    }

    [Fact]
    public void NonGenericFailureRejectsANullError()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
    }

    [Fact]
    public void NonGenericCastFailureProducesATypedFailure()
    {
        Result<int> retyped = Result.Failure(SampleError).CastFailure<int>();

        Assert.False(retyped.Ok);
        Assert.Same(SampleError, retyped.Error);
    }

    // ---- equality ------------------------------------------------------

    [Fact]
    public void ResultsCompareByOutcomeAndPayload()
    {
        Assert.Equal(Result<int>.Success(3), Result<int>.Success(3));
        Assert.NotEqual(Result<int>.Success(3), Result<int>.Success(4));
        Assert.True(Result<int>.Success(3) == Result<int>.Success(3));
        Assert.True(Result<int>.Success(3) != Result<int>.Failure(SampleError));

        Assert.Equal(Result.Success(), Result.Success());
        Assert.True(Result.Failure(SampleError) == Result.Failure(SampleError));
    }

    [Fact]
    public void ToStringSaysWhichSideTheResultLandedOn()
    {
        Assert.Equal("Ok(5)", Result<int>.Success(5).ToString());
        Assert.Equal("Ok", Result.Success().ToString());
        Assert.StartsWith("Error(", Result<int>.Failure(SampleError).ToString(), StringComparison.Ordinal);
    }
}
