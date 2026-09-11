using Dmg.Core.Crypto;
using Dmg.Core.Tests.Codecs;

namespace Dmg.Core.Tests.Crypto;

/// <summary>
/// The encrcdsa header is the only plaintext in an encrypted image, so it is the
/// only place a hostile file gets to lie before any key exists. Every declared
/// length is checked against the fixed-size field holding it.
/// </summary>
public sealed class EncryptedDmgHeaderTests
{
    [SkippableFact]
    public void ParsesAHeaderWrittenByHdiutil()
    {
        EncryptedFixtures.SkipUnless("exfat-enc256.dmg");

        using FileStream file = File.OpenRead(EncryptedFixtures.Path("exfat-enc256.dmg"));
        Result<EncryptedDmgHeader> result = EncryptedDmgHeader.Read(file);

        Assert.True(result.TryGetValue(out EncryptedDmgHeader? header), result.Ok ? "" : result.Error.ToString());

        Assert.Equal(2u, header.Version);
        Assert.Equal(256u, header.EncryptionKeyBits);
        Assert.Equal(16u, header.EncryptionIvSize);

        // docs/04 put BlockSize at 0x38 and claimed 4096. It is at 0x34 and hdiutil
        // writes 512. Getting this wrong shifts DataSize and DataOffset too.
        Assert.Equal(512u, header.BlockSize);

        // The payload runs from DataOffset to the end of the file, exactly.
        Assert.Equal(file.Length, header.DataOffset + header.DataSize);

        Assert.Equal(1u, header.KeyCount);
        Assert.Equal(EncryptedDmgKeyBlob.Pbkdf2Algorithm, header.KeyBlob.KdfAlgorithm);
        Assert.Equal(20, header.KeyBlob.KdfSalt.Length);
        Assert.Equal(8, header.KeyBlob.BlobEncryptionIv.Length);
        Assert.Equal(192u, header.KeyBlob.BlobEncryptionKeyBits);

        // The wrap cipher is Apple's vendor-defined AES id, not the 3DES id the
        // older reverse-engineering notes record. 52 bytes of key material padded
        // to 64 is only possible with a 16-byte block.
        Assert.Equal(EncryptedDmgKeyBlob.AesAlgorithm, header.KeyBlob.BlobEncryptionAlgorithm);
        Assert.Equal(64, header.KeyBlob.WrappedKey.Length);
        Assert.Equal(16, header.Uuid.Length);
    }

    [SkippableFact]
    public void TheAes128FixtureDiffersOnlyInItsKeySize()
    {
        EncryptedFixtures.SkipUnless("exfat-enc128.dmg");

        using FileStream file = File.OpenRead(EncryptedFixtures.Path("exfat-enc128.dmg"));
        EncryptedDmgHeader header = Parsed(file);

        Assert.Equal(128u, header.EncryptionKeyBits);
        Assert.Equal(16, header.EncryptionKeyBytes);
        Assert.Equal(512u, header.BlockSize);
        Assert.Equal(48, header.KeyBlob.WrappedKey.Length);
    }

    [Fact]
    public void ParsesASyntheticHeader()
    {
        EncryptedHeaderBuilder builder = new();

        EncryptedDmgHeader header = Parsed(builder);

        Assert.Equal(256u, header.EncryptionKeyBits);
        Assert.Equal(512u, header.BlockSize);
        Assert.Equal(4096L, header.DataOffset);
        Assert.Equal(8L, header.BlockCount);
        Assert.Equal(20, header.KeyBlob.KdfSalt.Length);
        Assert.Equal(0x11, header.KeyBlob.KdfSalt.Span[0]);
        Assert.Equal(8, header.KeyBlob.BlobEncryptionIv.Length);
        Assert.Equal(64, header.KeyBlob.WrappedKey.Length);
    }

    [Fact]
    public void AFileWithoutTheSignatureIsUnsupportedNotCorrupt()
    {
        byte[] header = new EncryptedHeaderBuilder().ToArray();
        header[0] = (byte)'X';

        Result<EncryptedDmgHeader> result = EncryptedDmgHeader.Parse(header, header.Length + 4096);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(0u)]
    public void AVersionOtherThanTwoIsRefusedByName(uint version)
    {
        Result<EncryptedDmgHeader> result = Attempt(new EncryptedHeaderBuilder { Version = version });

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
        Assert.Contains(version.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(64u)]
    [InlineData(192u)]
    [InlineData(512u)]
    public void AKeySizeOtherThan128Or256IsRejected(uint bits)
    {
        // "Best effort" here would mean slicing key material at a guessed offset and
        // decrypting to noise, which reports as a wrong passphrase.
        Result<EncryptedDmgHeader> result = Attempt(new EncryptedHeaderBuilder { EncryptionKeyBits = bits });

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(500u)]
    [InlineData(513u)]
    [InlineData(1u << 21)]
    public void ABlockSizeThatIsNotAWholeNumberOfAesBlocksIsRejected(uint blockSize)
    {
        Result<EncryptedDmgHeader> result = Attempt(new EncryptedHeaderBuilder { BlockSize = blockSize });

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void APayloadRunningPastTheEndOfTheFileIsRejected()
    {
        EncryptedHeaderBuilder builder = new() { DataSize = 1 << 20 };

        Result<EncryptedDmgHeader> result = EncryptedDmgHeader.Parse(builder.ToArray(), 8192);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void APayloadOverlappingTheHeaderIsRejected()
    {
        Result<EncryptedDmgHeader> result = Attempt(new EncryptedHeaderBuilder { DataOffset = 8 });

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void ADataSizeAndOffsetThatOverflowAreRejected()
    {
        EncryptedHeaderBuilder builder = new()
        {
            DataOffset = ulong.MaxValue - 16,
            DataSize = 4096,
        };

        Result<EncryptedDmgHeader> result = EncryptedDmgHeader.Parse(builder.ToArray(), 65536);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(33u)]
    [InlineData(uint.MaxValue)]
    public void ASaltLongerThanItsContainerIsRejected(uint saltLength)
    {
        Result<EncryptedDmgHeader> result = Attempt(new EncryptedHeaderBuilder { KdfSaltLength = saltLength });

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
        Assert.Contains("salt", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(33u)]
    [InlineData(uint.MaxValue)]
    public void AnUnwrapIvLongerThanItsContainerIsRejected(uint ivLength)
    {
        Result<EncryptedDmgHeader> result = Attempt(new EncryptedHeaderBuilder { IvLength = ivLength });

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(600u)]
    [InlineData(uint.MaxValue)]
    public void AWrappedKeyLongerThanItsContainerIsRejected(uint wrappedLength)
    {
        Result<EncryptedDmgHeader> result = Attempt(new EncryptedHeaderBuilder { WrappedKeyLength = wrappedLength });

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AKeyDescriptionPointingOutsideTheFileIsRejected()
    {
        Result<EncryptedDmgHeader> result = Attempt(new EncryptedHeaderBuilder { KeyOffset = ulong.MaxValue - 8 });

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AKeyDescriptionOutsideTheHeaderWindowIsUnsupported()
    {
        EncryptedHeaderBuilder builder = new()
        {
            DataSize = 1 << 20,
            KeyOffset = 900_000,
        };

        Result<EncryptedDmgHeader> result = EncryptedDmgHeader.Parse(builder.ToArray(), 2 << 20);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    [Fact]
    public void AnImageWithNoPassphraseKeyIsUnsupported()
    {
        // Certificate-unlocked images are a real thing; they get a name, not a
        // wrong-passphrase message.
        Result<EncryptedDmgHeader> result = Attempt(new EncryptedHeaderBuilder { KeyType = 2 });

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    [Fact]
    public void AnImageWithNoKeysAtAllIsCorrupt()
    {
        Result<EncryptedDmgHeader> result = Attempt(new EncryptedHeaderBuilder { KeyCount = 0 });

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AnAbsurdKeyCountIsRejectedBeforeTheTableIsWalked()
    {
        Result<EncryptedDmgHeader> result = Attempt(new EncryptedHeaderBuilder { KeyCount = uint.MaxValue });

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void AKdfOtherThanPbkdf2IsUnsupported()
    {
        Result<EncryptedDmgHeader> result = Attempt(new EncryptedHeaderBuilder { KdfAlgorithm = 99 });

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(0x4B)]
    public void AHeaderTruncatedBeforeItsFixedFieldsEndIsRejected(int keep)
    {
        byte[] header = new EncryptedHeaderBuilder().ToArray();

        Result<EncryptedDmgHeader> result = EncryptedDmgHeader.Parse(header.AsSpan(0, keep), 65536);

        Assert.False(result.Ok);
        Assert.NotEqual(DmgExitCode.Success, result.Error.Code);
    }

    [Fact]
    public void ReadRefusesAStreamItCannotSeek()
    {
        using UnseekableStream stream = new(new EncryptedHeaderBuilder().ToArray());

        Result<EncryptedDmgHeader> result = EncryptedDmgHeader.Read(stream);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.InternalError, result.Error.Code);
    }

    [Fact]
    public void AFileTooSmallToHoldAHeaderIsUnsupported()
    {
        using MemoryStream stream = new(new byte[16]);

        Result<EncryptedDmgHeader> result = EncryptedDmgHeader.Read(stream);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.UnsupportedFormat, result.Error.Code);
    }

    private static Result<EncryptedDmgHeader> Attempt(EncryptedHeaderBuilder builder) =>
        EncryptedDmgHeader.Parse(builder.ToArray(), builder.FileLength);

    private static EncryptedDmgHeader Parsed(EncryptedHeaderBuilder builder)
    {
        Result<EncryptedDmgHeader> result = Attempt(builder);
        Assert.True(result.TryGetValue(out EncryptedDmgHeader? header), result.Ok ? "" : result.Error.ToString());
        return header;
    }

    private static EncryptedDmgHeader Parsed(Stream stream)
    {
        Result<EncryptedDmgHeader> result = EncryptedDmgHeader.Read(stream);
        Assert.True(result.TryGetValue(out EncryptedDmgHeader? header), result.Ok ? "" : result.Error.ToString());
        return header;
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}

/// <summary>
/// The two encrypted fixtures, and the one reason a test may quietly pass without
/// them: <c>fixtures/generated/</c> is gitignored and only a Mac can fill it.
/// </summary>
internal static class EncryptedFixtures
{
    /// <summary>The passphrase <c>tools/make-fixtures.sh</c> gives every encrypted fixture.</summary>
    /// <remarks>
    /// <b>The trailing newline is part of the passphrase.</b> The script feeds
    /// <c>hdiutil -stdinpass</c> with <c>printf '%s\n'</c> and hdiutil keys the
    /// image off everything it read, terminator included. Deriving from the string
    /// without it produces a key that unwraps to noise - which is precisely the
    /// case the key-derivation step retries with a trailing LF.
    /// </remarks>
    public const string Passphrase = "dmg-test-passphrase\n";

    /// <summary>The passphrase as a caller would type it, with no terminator.</summary>
    public const string TypedPassphrase = "dmg-test-passphrase";

    /// <summary>True when <paramref name="names"/> are not all usable; <paramref name="reason"/> says why.</summary>
    public static bool Skip(out string? reason, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);

        if (!FixtureCorpus.IsAvailable)
        {
            reason = $"No fixture corpus: {FixtureCorpus.UnavailableReason} {FixtureCorpus.Regenerate}";
            return true;
        }

        foreach (string name in names)
        {
            if (FixtureCorpus.Find(name) is null)
            {
                reason = $"'{name}' is not in the corpus. {FixtureCorpus.Regenerate}";
                return true;
            }
        }

        reason = null;
        return false;
    }

    /// <summary>
    /// Skips the calling test - which must be a <c>[SkippableFact]</c> or
    /// <c>[SkippableTheory]</c> - unless every named fixture is usable.
    /// </summary>
    /// <remarks>
    /// Prefer this to <see cref="Skip"/>. That overload reports absence and lets the
    /// test <em>pass</em>, which means a run with no corpus is indistinguishable in
    /// the results from a run that actually decrypted something. This one reports it
    /// as a skip, so the difference is visible.
    /// </remarks>
    public static void SkipUnless(params string[] names)
    {
        bool skip = Skip(out string? reason, names);

        // Fully qualified: the unqualified name binds to the method above, not to
        // xunit's static Skip class.
        Xunit.Skip.If(skip, reason);
    }

    /// <summary>The path of a fixture the caller has already checked with <see cref="Skip"/>.</summary>
    public static string Path(string name) =>
        FixtureCorpus.Find(name)?.Path
        ?? throw new InvalidOperationException($"'{name}' is not in the fixture corpus.");

    /// <summary>The manifest record for a fixture, for ground-truth comparisons.</summary>
    public static FixtureRecord Record(string name) =>
        FixtureCorpus.Find(name)
        ?? throw new InvalidOperationException($"'{name}' is not in the fixture corpus.");

    /// <summary>The passphrase as bytes, freshly allocated so the caller may zero it.</summary>
    public static byte[] PassphraseBytes() => System.Text.Encoding.UTF8.GetBytes(Passphrase);
}
