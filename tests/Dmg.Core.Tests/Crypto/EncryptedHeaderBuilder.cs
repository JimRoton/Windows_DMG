using System.Buffers.Binary;

namespace Dmg.Core.Tests.Crypto;

/// <summary>
/// Builds <c>encrcdsa</c> v2 headers byte by byte, so a test can make exactly one
/// field wrong and see what the parser does about it.
/// </summary>
/// <remarks>
/// The defaults reproduce the layout <c>hdiutil</c> writes: a 4 KiB header region,
/// one key-pointer entry of type 1 at offset <c>0x4C</c> pointing at a 616-byte key
/// description at <c>0x60</c>. Those numbers were read off the generated fixtures,
/// not off <c>docs/04</c>, which had them shifted.
/// </remarks>
internal sealed class EncryptedHeaderBuilder
{
    /// <summary>Where the key description sits in a header hdiutil wrote.</summary>
    public const int DescriptorOffset = 0x60;

    /// <summary>How long that description is.</summary>
    public const int DescriptorSize = 616;

    /// <summary>The header region hdiutil pads out before the ciphertext.</summary>
    public const int DefaultHeaderBytes = 4096;

    public uint Version { get; set; } = 2;

    public uint EncryptionIvSize { get; set; } = 16;

    public uint EncryptionMode { get; set; } = 5;

    public uint EncryptionAlgorithm { get; set; } = 0x8000_0001;

    public uint EncryptionKeyBits { get; set; } = 256;

    public uint BlockSize { get; set; } = 512;

    public ulong DataSize { get; set; } = 4096;

    public ulong DataOffset { get; set; } = DefaultHeaderBytes;

    public uint KeyCount { get; set; } = 1;

    public uint KeyType { get; set; } = 1;

    public ulong KeyOffset { get; set; } = DescriptorOffset;

    public ulong KeySize { get; set; } = DescriptorSize;

    public uint KdfAlgorithm { get; set; } = 103;

    public uint KdfIterationCount { get; set; } = 1000;

    public uint KdfSaltLength { get; set; } = 20;

    public uint IvLength { get; set; } = 8;

    public uint BlobKeyBits { get; set; } = 192;

    public uint BlobAlgorithm { get; set; } = 0x8000_0001;

    public uint BlobPadding { get; set; } = 7;

    public uint BlobMode { get; set; } = 6;

    public uint WrappedKeyLength { get; set; } = 64;

    /// <summary>The salt bytes written into the 32-byte container.</summary>
    public byte[] Salt { get; set; } = Fill(20, 0x11);

    /// <summary>The unwrap IV written into its 32-byte container.</summary>
    public byte[] Iv { get; set; } = Fill(8, 0x22);

    /// <summary>The wrapped key bytes.</summary>
    public byte[] WrappedKey { get; set; } = Fill(64, 0x33);

    /// <summary>How many bytes of header region to emit.</summary>
    public int HeaderBytes { get; set; } = DefaultHeaderBytes;

    /// <summary>Renders the header region on its own.</summary>
    public byte[] ToArray()
    {
        byte[] header = new byte[HeaderBytes];

        "encrcdsa"u8.CopyTo(header);
        PutUInt32(header, 0x08, Version);
        PutUInt32(header, 0x0C, EncryptionIvSize);
        PutUInt32(header, 0x10, EncryptionMode);
        PutUInt32(header, 0x14, EncryptionAlgorithm);
        PutUInt32(header, 0x18, EncryptionKeyBits);
        PutUInt32(header, 0x1C, 91);
        PutUInt32(header, 0x20, 160);

        for (int index = 0; index < 16; index++)
        {
            header[0x24 + index] = (byte)(0xA0 + index);
        }

        PutUInt32(header, 0x34, BlockSize);
        PutUInt64(header, 0x38, DataSize);
        PutUInt64(header, 0x40, DataOffset);
        PutUInt32(header, 0x48, KeyCount);

        PutUInt32(header, 0x4C, KeyType);
        PutUInt64(header, 0x50, KeyOffset);
        PutUInt64(header, 0x58, KeySize);

        int d = DescriptorOffset;
        PutUInt32(header, d + 0x00, KdfAlgorithm);
        PutUInt32(header, d + 0x04, 0);
        PutUInt32(header, d + 0x08, KdfIterationCount);
        PutUInt32(header, d + 0x0C, KdfSaltLength);
        Salt.CopyTo(header.AsSpan(d + 0x10));
        PutUInt32(header, d + 0x30, IvLength);
        Iv.CopyTo(header.AsSpan(d + 0x34));
        PutUInt32(header, d + 0x54, BlobKeyBits);
        PutUInt32(header, d + 0x58, BlobAlgorithm);
        PutUInt32(header, d + 0x5C, BlobPadding);
        PutUInt32(header, d + 0x60, BlobMode);
        PutUInt32(header, d + 0x64, WrappedKeyLength);
        WrappedKey.CopyTo(header.AsSpan(d + 0x68));

        return header;
    }

    /// <summary>Renders the header followed by <paramref name="payload"/> as a whole file.</summary>
    public byte[] ToFile(ReadOnlySpan<byte> payload)
    {
        byte[] header = ToArray();
        byte[] file = new byte[header.Length + payload.Length];
        header.CopyTo(file, 0);
        payload.CopyTo(file.AsSpan(header.Length));
        return file;
    }

    /// <summary>The file length a header on its own implies, for <c>Parse</c>.</summary>
    public long FileLength => (long)DataOffset + (long)DataSize;

    internal static void PutUInt32(Span<byte> target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(target[offset..], value);

    internal static void PutUInt64(Span<byte> target, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64BigEndian(target[offset..], value);

    private static byte[] Fill(int count, byte value)
    {
        byte[] bytes = new byte[count];
        Array.Fill(bytes, value);
        return bytes;
    }
}
