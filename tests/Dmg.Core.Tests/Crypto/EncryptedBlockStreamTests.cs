using System.Security.Cryptography;
using System.Text;
using Dmg.Core.Crypto;
using Dmg.Core.Imaging;

namespace Dmg.Core.Tests.Crypto;

/// <summary>
/// The decrypting decorator. It has to behave like any other read-only seekable
/// stream, because everything above it is written against <see cref="Stream"/> and
/// knows nothing about encryption.
/// </summary>
public sealed class EncryptedBlockStreamTests
{
    private const string Passphrase = "open-sesame";

    [Fact]
    public void ReadsBackExactlyWhatWasEncrypted()
    {
        byte[] plaintext = Payload(4096);
        using EncryptedBlockStream stream = Open(plaintext);

        byte[] read = new byte[plaintext.Length];
        stream.ReadExactly(read);

        Assert.Equal(plaintext, read);
    }

    [Fact]
    public void LengthIsThePlaintextLengthNotTheCiphertextLength()
    {
        // The whole design rests on this. The reader above looks for a koly trailer
        // in the last 512 bytes of Length; report the ciphertext length and it looks
        // in the wrong place and calls a good image "not a DMG".
        byte[] plaintext = Payload(5000);
        SyntheticImage image = SyntheticImage.Create(Passphrase, plaintext: plaintext);

        using EncryptedBlockStream stream = OpenImage(image);

        Assert.Equal(5000, stream.Length);
        Assert.True(image.File.Length - 4096 > 5000, "the ciphertext should be padded past the plaintext");
    }

    [Fact]
    public void AShortFinalBlockIsReadWithoutItsPadding()
    {
        // 5000 bytes is nine whole 512-byte blocks and 392 bytes of a tenth. The
        // tenth block on disk is a full block; only 392 bytes of it are real.
        byte[] plaintext = Payload(5000);
        using EncryptedBlockStream stream = Open(plaintext);

        byte[] read = new byte[6000];
        int got = ReadFully(stream, read);

        Assert.Equal(5000, got);
        Assert.Equal(plaintext, read[..5000]);
    }

    [Fact]
    public void ReadingPastTheEndReturnsZero()
    {
        using EncryptedBlockStream stream = Open(Payload(1024));

        stream.Position = 1024;

        Assert.Equal(0, stream.Read(new byte[16]));
    }

    [Fact]
    public void SeekingToAnyOffsetReadsTheRightBytes()
    {
        byte[] plaintext = Payload(4096);
        using EncryptedBlockStream stream = Open(plaintext);

        foreach (int offset in new[] { 0, 1, 15, 16, 511, 512, 513, 1000, 2048, 4095 })
        {
            stream.Position = offset;

            byte[] one = new byte[1];
            Assert.Equal(1, stream.Read(one));
            Assert.Equal(plaintext[offset], one[0]);
        }
    }

    [Fact]
    public void SeekingBackwardsGivesTheSameBytesAsSeekingForwards()
    {
        // Random access is the point of the per-block IV; if it were chained, this
        // would quietly return different bytes the second time.
        byte[] plaintext = Payload(8192);
        using EncryptedBlockStream stream = Open(plaintext);

        byte[] forwards = ReadAt(stream, 6000, 64);
        _ = ReadAt(stream, 100, 64);
        byte[] again = ReadAt(stream, 6000, 64);

        Assert.Equal(forwards, again);
        Assert.Equal(plaintext[6000..6064], again);
    }

    [Theory]
    [InlineData(0, 512)]
    [InlineData(0, 513)]
    [InlineData(1, 511)]
    [InlineData(500, 100)]
    [InlineData(511, 2)]
    [InlineData(1023, 1025)]
    [InlineData(3000, 1096)]
    public void ReadsThatDoNotAlignToABlockAreSlicedOutOfTheBlockTheyLandIn(int offset, int count)
    {
        byte[] plaintext = Payload(4096);
        using EncryptedBlockStream stream = Open(plaintext);

        Assert.Equal(plaintext[offset..(offset + count)], ReadAt(stream, offset, count));
    }

    [Fact]
    public void ReadingOneByteAtATimeGivesTheWholePlaintext()
    {
        byte[] plaintext = Payload(2048);
        using EncryptedBlockStream stream = Open(plaintext);

        byte[] read = new byte[plaintext.Length];

        for (int index = 0; index < read.Length; index++)
        {
            int value = stream.ReadByte();
            Assert.InRange(value, 0, 255);
            read[index] = (byte)value;
        }

        Assert.Equal(plaintext, read);
        Assert.Equal(-1, stream.ReadByte());
    }

    [Fact]
    public void SeekFromEndCountsBackFromThePlaintextLength()
    {
        byte[] plaintext = Payload(5000);
        using EncryptedBlockStream stream = Open(plaintext);

        Assert.Equal(4488, stream.Seek(-512, SeekOrigin.End));

        byte[] tail = new byte[512];
        stream.ReadExactly(tail);

        Assert.Equal(plaintext[4488..], tail);
    }

    [Fact]
    public void SeekingBeforeTheStartIsRejected()
    {
        using EncryptedBlockStream stream = Open(Payload(1024));

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(-1, SeekOrigin.Begin));
    }

    [Fact]
    public void ItIsReadOnly()
    {
        using EncryptedBlockStream stream = Open(Payload(512));

        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[4], 0, 4));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
    }

    [Fact]
    public void DisposingClosesTheSourceUnlessAskedNotTo()
    {
        SyntheticImage image = SyntheticImage.Create(Passphrase, plaintext: Payload(512));

        MemoryStream owned = image.OpenRead();
        OpenImage(image, owned, leaveOpen: false).Dispose();
        Assert.False(owned.CanRead);

        MemoryStream borrowed = image.OpenRead();
        OpenImage(image, borrowed, leaveOpen: true).Dispose();
        Assert.True(borrowed.CanRead);
        borrowed.Dispose();
    }

    [Fact]
    public void ReadingAfterDisposeThrows()
    {
        EncryptedBlockStream stream = Open(Payload(512));
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[4], 0, 4));
    }

    [Fact]
    public void AWrongPassphraseNeverGetsAsFarAsAStream()
    {
        SyntheticImage image = SyntheticImage.Create(Passphrase, plaintext: Payload(512));

        using MemoryStream file = image.OpenRead();
        Result<EncryptedBlockStream> result = EncryptedBlockStream.Open(file, "wrong"u8);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.DecryptionFailed, result.Error.Code);
    }

    [Fact]
    public void AFileShorterThanItsDeclaredPayloadIsCorruptImage()
    {
        SyntheticImage image = SyntheticImage.Create(Passphrase, plaintext: Payload(4096));
        byte[] truncated = image.File[..(image.File.Length - 1024)];

        using MemoryStream file = new(truncated, writable: false);
        Result<EncryptedBlockStream> result = EncryptedBlockStream.Open(
            file,
            Encoding.UTF8.GetBytes(Passphrase));

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.CorruptImage, result.Error.Code);
    }

    [Fact]
    public void ADecryptedFixtureLooksLikeARealDisk()
    {
        // Not the round-trip test - that is S4.9 - but enough to say the block IV,
        // the key split and the block size all agree with what hdiutil wrote. The
        // decrypted payload of the exFAT fixtures starts with a partition sector,
        // which ends in the 0x55AA boot signature.
        if (EncryptedFixtures.Skip(out string? why, "exfat-enc256.dmg"))
        {
            Assert.True(true, why);
            return;
        }

        using FileStream file = File.OpenRead(EncryptedFixtures.Path("exfat-enc256.dmg"));
        Result<EncryptedBlockStream> result = EncryptedBlockStream.Open(
            file,
            Encoding.UTF8.GetBytes(EncryptedFixtures.Passphrase),
            leaveOpen: true);

        Assert.True(result.TryGetValue(out EncryptedBlockStream? stream), result.Ok ? "" : result.Error.ToString());

        using (stream)
        {
            byte[] sector = new byte[512];
            stream.ReadExactly(sector);

            Assert.Equal(0x55, sector[510]);
            Assert.Equal(0xAA, sector[511]);
            Assert.Equal(file.Length - stream.Header.DataOffset, stream.Length);
        }
    }

    [Fact]
    public void TheDecoratorIsInvisibleToTheContainerReaderAboveIt()
    {
        // The contract the design depends on: hand DmgBlockStream a decrypting
        // stream and it behaves exactly as it does over a file, because all it
        // needs is Length, Seek and Read.
        if (EncryptedFixtures.Skip(out string? why, "exfat-enc256.dmg"))
        {
            Assert.True(true, why);
            return;
        }

        using FileStream file = File.OpenRead(EncryptedFixtures.Path("exfat-enc256.dmg"));
        Assert.True(EncryptedBlockStream.Open(
            file,
            Encoding.UTF8.GetBytes(EncryptedFixtures.Passphrase),
            leaveOpen: true).TryGetValue(out EncryptedBlockStream? plain));

        using (plain)
        {
            // The exFAT fixtures decrypt to a bare sector stream rather than a UDIF
            // container, so the UDIF reader declines it - and declines it as "not a
            // UDIF image" (exit 3), which is a statement about the plaintext. That
            // is only possible because the trailer search ran against the plaintext
            // length.
            Result<DmgBlockStream> opened = DmgBlockStream.Open(plain, leaveOpen: true);

            Assert.False(opened.Ok);
            Assert.Equal(DmgExitCode.UnsupportedFormat, opened.Error.Code);
        }
    }

    private static byte[] Payload(int length)
    {
        byte[] payload = new byte[length];
        RandomNumberGenerator.Fill(payload);
        return payload;
    }

    private static EncryptedBlockStream Open(byte[] plaintext) =>
        OpenImage(SyntheticImage.Create(Passphrase, plaintext: plaintext));

    private static EncryptedBlockStream OpenImage(
        SyntheticImage image,
        Stream? file = null,
        bool leaveOpen = false)
    {
        Stream source = file ?? image.OpenRead();
        Result<EncryptedBlockStream> result = EncryptedBlockStream.Open(
            source,
            Encoding.UTF8.GetBytes(Passphrase),
            leaveOpen);

        Assert.True(result.TryGetValue(out EncryptedBlockStream? stream), result.Ok ? "" : result.Error.ToString());

        return stream;
    }

    private static byte[] ReadAt(EncryptedBlockStream stream, int offset, int count)
    {
        stream.Position = offset;

        byte[] buffer = new byte[count];
        int got = ReadFully(stream, buffer);

        return buffer[..got];
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        int total = 0;
        int read;

        while (total < buffer.Length && (read = stream.Read(buffer.AsSpan(total))) > 0)
        {
            total += read;
        }

        return total;
    }
}
