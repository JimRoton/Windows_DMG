using Dmg.Core.Vhd;

namespace Dmg.Core.Tests.Vhd;

/// <summary>
/// Guards the one property that matters about a mount id: it is generated here,
/// and anything that did not come from here is refused rather than cleaned up.
/// </summary>
public sealed class MountIdTests
{
    [Fact]
    public void GeneratesThirtyTwoLowercaseHexCharacters()
    {
        MountId id = MountId.New();

        Assert.True(id.IsValid);
        Assert.Equal(32, id.Value.Length);
        Assert.All(id.Value, character =>
            Assert.True(
                character is (>= '0' and <= '9') or (>= 'a' and <= 'f'),
                $"'{character}' is not a lowercase hex digit."));
    }

    [Fact]
    public void GeneratesADifferentIdEveryTime()
    {
        HashSet<string> seen = [];

        for (int index = 0; index < 512; index++)
        {
            Assert.True(seen.Add(MountId.New().Value), "Mount ids must not collide.");
        }
    }

    [Fact]
    public void ADefaultInstanceIsNotValidAndHasNoText()
    {
        MountId id = default;

        Assert.False(id.IsValid);
        Assert.Equal(string.Empty, id.Value);
        Assert.Equal(string.Empty, id.ToString());
    }

    [Fact]
    public void RoundTripsItsOwnValue()
    {
        MountId generated = MountId.New();

        Assert.True(MountId.TryParse(generated.Value, out MountId parsed));
        Assert.Equal(generated, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../../../etc/passwd")]
    [InlineData(@"..\..\Windows\System32")]
    [InlineData("CON")]
    [InlineData("C:\\Windows")]
    [InlineData("0123456789abcdef0123456789abcde")]    // one short
    [InlineData("0123456789abcdef0123456789abcdef0")]  // one long
    [InlineData("0123456789ABCDEF0123456789abcdef")]   // uppercase
    [InlineData("0123456789abcdef0123456789abcdeg")]   // not hex
    [InlineData("0123456789abcdef0123456789abcd.f")]   // a dot
    [InlineData("0123456789abcdef 123456789abcdef")]   // a space
    [InlineData("0123456789abcdef/123456789abcdef")]   // a separator
    public void RefusesAnythingItDidNotGenerate(string? text)
    {
        Assert.False(MountId.TryParse(text, out MountId id));
        Assert.False(id.IsValid);

        Result<MountId> parsed = MountId.Parse(text);

        Assert.False(parsed.Ok);
        Assert.Equal(DmgExitCode.UsageError, parsed.Error.Code);
    }

    [Fact]
    public void KeepsAHostileStringOutOfTheErrorMessageItSelf()
    {
        string hostile = new('\u0007', 400);

        Result<MountId> parsed = MountId.Parse(hostile);

        Assert.False(parsed.Ok);

        string? detail = parsed.Error.Detail;

        Assert.NotNull(detail);
        Assert.DoesNotContain("\u0007", detail, StringComparison.Ordinal);
        Assert.True(detail!.Length < 200, "A rejected id must not be echoed in full.");
    }
}
