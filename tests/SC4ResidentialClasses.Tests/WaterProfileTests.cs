using System;
using System.IO;
using System.Text;
using SC4Evolution.Simulation;

internal static class WaterProfileTests
{
    private static int _assertions;

    internal static int Run()
    {
        _assertions = 0;
        TestFrozenGoldenLayoutAndRoundTrip();
        TestImmutableSourceHashAndCapBoundaries();
        TestRejectsTruncationTrailingAndBadChecksum();
        TestRejectsRechecksummedHeaderAndExemplarMutants();
        TestStreamsRemainOpenAndShortReadsWork();
        return _assertions;
    }

    private static void TestFrozenGoldenLayoutAndRoundTrip()
    {
        byte[] sourceHash = new byte[32];
        int index;
        for (index = 0; index < sourceHash.Length; index++) sourceHash[index] = (byte)index;
        ResidentialWaterProfile profile = new ResidentialWaterProfile(sourceHash, 3, 6, 0);
        byte[] expected = HexBytes(
            "533445574154523101000000000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f" +
            "7063cd874a283465a15fcd671010000003000000" +
            "4a283465a15fcd672010000006000000" +
            "4a283465a15fcd673010000000000000b3ae317b");
        Equal(100, ResidentialWaterProfileCodec.EncodedSize, "codec publishes exact fixed size");
        Equal(100, expected.Length, "golden payload contains exactly 100 bytes");
        Equal(0xCBF43926u, Crc32(Encoding.ASCII.GetBytes("123456789"), 0, 9), "CRC implementation matches IEEE CRC-32 reference vector");

        byte[] encoded = Encode(profile);
        BytesEqual(expected, encoded, "writer matches frozen 100-byte golden payload including checksum");
        BytesEqual(expected, Encode(profile), "same immutable profile encodes deterministically");
        ResidentialWaterProfile decoded = Decode(encoded);
        BytesEqual(sourceHash, decoded.SourceSha256, "roundtrip preserves source hash");
        Equal(3, decoded.LowMaximumStage, "roundtrip preserves low raw cap");
        Equal(6, decoded.MediumMaximumStage, "roundtrip preserves medium raw cap");
        Equal(0, decoded.HighMaximumStage, "roundtrip preserves high raw cap");
        BytesEqual(encoded, Encode(decoded), "decode-encode roundtrip remains byte-identical");
    }

    private static void TestImmutableSourceHashAndCapBoundaries()
    {
        byte[] originalHash = MakeHash(0xA5);
        byte[] expected = (byte[])originalHash.Clone();
        ResidentialWaterProfile profile = new ResidentialWaterProfile(originalHash, 0, 255, 1);
        originalHash[0] ^= 0xFF;
        BytesEqual(expected, profile.SourceSha256, "constructor clones source SHA-256 bytes");
        byte[] exposedHash = profile.SourceSha256;
        exposedHash[1] ^= 0xFF;
        BytesEqual(expected, profile.SourceSha256, "source SHA-256 property returns a defensive clone");
        Equal(0, profile.LowMaximumStage, "zero raw cap is accepted");
        Equal(255, profile.MediumMaximumStage, "maximum UInt8 raw cap is accepted");
        Equal(1, profile.HighMaximumStage, "interior raw cap is preserved without level mapping");
        ResidentialWaterProfile reloaded = Decode(Encode(profile));
        Equal(255, reloaded.MediumMaximumStage, "codec accepts and preserves cap 255");

        Throws<ArgumentNullException>(delegate { new ResidentialWaterProfile(null, 0, 0, 0); }, "null source hash is rejected");
        Throws<ArgumentException>(delegate { new ResidentialWaterProfile(new byte[31], 0, 0, 0); }, "short source hash is rejected");
        Throws<ArgumentException>(delegate { new ResidentialWaterProfile(new byte[33], 0, 0, 0); }, "long source hash is rejected");
        Throws<ArgumentException>(delegate { new ResidentialWaterProfile(new byte[32], 0, 0, 0); }, "all-zero source hash is rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ResidentialWaterProfile(expected, -1, 0, 0); }, "negative low cap is rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ResidentialWaterProfile(expected, 0, -1, 0); }, "negative medium cap is rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ResidentialWaterProfile(expected, 0, 0, -1); }, "negative high cap is rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ResidentialWaterProfile(expected, 256, 0, 0); }, "low cap above UInt8 is rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ResidentialWaterProfile(expected, 0, 256, 0); }, "medium cap above UInt8 is rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ResidentialWaterProfile(expected, 0, 0, 256); }, "high cap above UInt8 is rejected");
    }

    private static void TestRejectsTruncationTrailingAndBadChecksum()
    {
        byte[] encoded = Encode(new ResidentialWaterProfile(MakeHash(9), 0, 255, 8));
        int length;
        for (length = 0; length < ResidentialWaterProfileCodec.EncodedSize; length++)
        {
            byte[] truncated = new byte[length];
            Array.Copy(encoded, truncated, length);
            Throws<InvalidDataException>(delegate { Decode(truncated); }, "truncated fixed record at " + length.ToString() + " bytes is rejected");
        }

        byte[] trailing = new byte[encoded.Length + 1];
        Array.Copy(encoded, trailing, encoded.Length);
        Throws<InvalidDataException>(delegate { Decode(trailing); }, "one trailing byte is rejected");

        byte[] corrupted = (byte[])encoded.Clone();
        corrupted[80] ^= 0x01;
        Throws<InvalidDataException>(delegate { Decode(corrupted); }, "CRC mismatch rejects payload corruption");

        byte[] shortButOpen = new byte[99];
        using (MemoryStream stream = new MemoryStream(shortButOpen, false))
        {
            Throws<InvalidDataException>(delegate { ResidentialWaterProfileCodec.Read(stream); }, "truncated stream is rejected");
            True(stream.CanRead, "failed read leaves caller stream open");
        }
    }

    private static void TestRejectsRechecksummedHeaderAndExemplarMutants()
    {
        byte[] valid = Encode(new ResidentialWaterProfile(MakeHash(1), 3, 6, 0));

        byte[] magic = (byte[])valid.Clone();
        magic[0] ^= 1;
        RejectAfterRechecksum(magic, "bad magic is rejected with valid CRC");

        byte[] version = (byte[])valid.Clone();
        PutUInt32(version, 8, 2);
        RejectAfterRechecksum(version, "unknown version is rejected with valid CRC");

        byte[] zeroHash = (byte[])valid.Clone();
        Array.Clear(zeroHash, 12, 32);
        RejectAfterRechecksum(zeroHash, "all-zero serialized source hash is rejected");

        byte[] property = (byte[])valid.Clone();
        PutUInt32(property, 44, 0x87CD6371u);
        RejectAfterRechecksum(property, "wrong property ID is rejected");

        byte[] lowType = (byte[])valid.Clone();
        PutUInt32(lowType, 48, 0x6534284Bu);
        RejectAfterRechecksum(lowType, "wrong low exemplar type is rejected");

        byte[] mediumGroup = (byte[])valid.Clone();
        PutUInt32(mediumGroup, 68, 0x67CD5FA0u);
        RejectAfterRechecksum(mediumGroup, "wrong medium exemplar group is rejected");

        byte[] lowInstance = (byte[])valid.Clone();
        PutUInt32(lowInstance, 56, 0x1020u);
        RejectAfterRechecksum(lowInstance, "wrong low exemplar instance is rejected");

        byte[] swappedOrder = (byte[])valid.Clone();
        PutUInt32(swappedOrder, 56, 0x1020u);
        PutUInt32(swappedOrder, 72, 0x1010u);
        RejectAfterRechecksum(swappedOrder, "out-of-order exemplar instances are rejected");

        byte[] lowCap256 = (byte[])valid.Clone();
        PutUInt32(lowCap256, 60, 256);
        RejectAfterRechecksum(lowCap256, "UInt32 cap value above UInt8 is rejected");

        byte[] mediumCap256 = (byte[])valid.Clone();
        PutUInt32(mediumCap256, 76, 256);
        RejectAfterRechecksum(mediumCap256, "medium UInt32 cap value above UInt8 is rejected");

        byte[] highCap256 = (byte[])valid.Clone();
        PutUInt32(highCap256, 92, 256);
        RejectAfterRechecksum(highCap256, "high UInt32 cap value above UInt8 is rejected");
    }

    private static void TestStreamsRemainOpenAndShortReadsWork()
    {
        ResidentialWaterProfile expected = new ResidentialWaterProfile(MakeHash(0x30), 0, 17, 255);
        byte[] encoded;
        using (MemoryStream output = new MemoryStream())
        {
            ResidentialWaterProfileCodec.Write(output, expected);
            True(output.CanWrite, "successful write leaves caller stream open");
            encoded = output.ToArray();
        }

        using (ChunkedReadStream input = new ChunkedReadStream(encoded, 3))
        {
            ResidentialWaterProfile actual = ResidentialWaterProfileCodec.Read(input);
            True(input.CanRead, "successful read leaves caller stream open");
            True(!input.CanSeek, "codec does not require a seekable stream");
            BytesEqual(expected.SourceSha256, actual.SourceSha256, "short nonseekable read preserves source hash");
            Equal(0, actual.LowMaximumStage, "short read preserves zero low cap");
            Equal(17, actual.MediumMaximumStage, "short read preserves medium cap");
            Equal(255, actual.HighMaximumStage, "short read preserves max high cap");
        }

        using (MemoryStream output = new MemoryStream())
        {
            Throws<ArgumentNullException>(delegate { ResidentialWaterProfileCodec.Write(output, null); }, "null profile is rejected before writing");
            Equal(0L, output.Length, "invalid write leaves output unchanged");
        }
        Throws<ArgumentException>(delegate { ResidentialWaterProfileCodec.Write(new MemoryStream(new byte[0], false), expected); }, "nonwritable output stream is rejected");
        MemoryStream closed = new MemoryStream();
        closed.Dispose();
        Throws<ArgumentException>(delegate { ResidentialWaterProfileCodec.Read(closed); }, "nonreadable input stream is rejected");
        Throws<ArgumentException>(delegate { ResidentialWaterProfileCodec.Read(null); }, "null input stream is rejected");
    }

    private static void RejectAfterRechecksum(byte[] bytes, string message)
    {
        Rechecksum(bytes);
        Throws<InvalidDataException>(delegate { Decode(bytes); }, message);
    }

    private static byte[] MakeHash(byte start)
    {
        byte[] hash = new byte[32];
        int index;
        for (index = 0; index < hash.Length; index++) hash[index] = (byte)(start + index);
        if (IsAllZero(hash)) hash[31] = 1;
        return hash;
    }

    private static bool IsAllZero(byte[] bytes)
    {
        int index;
        for (index = 0; index < bytes.Length; index++) if (bytes[index] != 0) return false;
        return true;
    }

    private static byte[] Encode(ResidentialWaterProfile profile)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            ResidentialWaterProfileCodec.Write(stream, profile);
            return stream.ToArray();
        }
    }

    private static ResidentialWaterProfile Decode(byte[] bytes)
    {
        using (MemoryStream stream = new MemoryStream(bytes, false))
            return ResidentialWaterProfileCodec.Read(stream);
    }

    private static byte[] HexBytes(string hex)
    {
        if (hex.Length % 2 != 0) throw new ArgumentException("Hex string length must be even.", "hex");
        byte[] result = new byte[hex.Length / 2];
        int index;
        for (index = 0; index < result.Length; index++)
            result[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
        return result;
    }

    private static void PutUInt32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    private static uint Crc32(byte[] bytes, int offset, int count)
    {
        uint crc = uint.MaxValue;
        int index;
        for (index = offset; index < offset + count; index++)
        {
            crc ^= bytes[index];
            int bit;
            for (bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1u) == 0u ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }

    private static void Rechecksum(byte[] bytes)
    {
        PutUInt32(bytes, 96, Crc32(bytes, 0, 96));
    }

    private static void BytesEqual(byte[] expected, byte[] actual, string message)
    {
        Equal(expected.Length, actual.Length, message + " length");
        int index;
        for (index = 0; index < expected.Length; index++)
        {
            if (expected[index] != actual[index])
                throw new Exception(message + ": byte difference at offset " + index.ToString() + ".");
            _assertions++;
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        _assertions++;
        if (!object.Equals(expected, actual))
            throw new Exception(message + ": expected " + expected + ", got " + actual + ".");
    }

    private static void True(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception(message + ": condition was false.");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        _assertions++;
        try { action(); }
        catch (T) { return; }
        catch (Exception error) { throw new Exception(message + ": expected " + typeof(T).Name + ", got " + error.GetType().Name + ".", error); }
        throw new Exception(message + ": expected " + typeof(T).Name + " but no exception was thrown.");
    }

    private sealed class ChunkedReadStream : MemoryStream
    {
        private readonly int _chunkSize;
        internal ChunkedReadStream(byte[] bytes, int chunkSize) : base(bytes, false) { _chunkSize = chunkSize; }
        public override bool CanSeek { get { return false; } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }
        public override long Seek(long offset, SeekOrigin loc) { throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { return base.Read(buffer, offset, Math.Min(count, _chunkSize)); }
    }
}
