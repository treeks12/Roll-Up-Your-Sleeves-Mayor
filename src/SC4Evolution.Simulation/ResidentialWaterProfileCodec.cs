using System;
using System.IO;
using System.Text;

namespace SC4Evolution.Simulation
{
    /// <summary>Exact fixed-width, source-attributed codec for the SC4 residential water caps.</summary>
    public static class ResidentialWaterProfileCodec
    {
        public const int EncodedSize = 100;

        private const uint Version = 1;
        private const uint PropertyId = 0x87CD6370u;
        private const uint ExemplarType = 0x6534284Au;
        private const uint ExemplarGroup = 0x67CD5FA1u;
        private const uint LowInstance = 0x00001010u;
        private const uint MediumInstance = 0x00001020u;
        private const uint HighInstance = 0x00001030u;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("S4EWATR1");

        public static void Write(Stream stream, ResidentialWaterProfile profile)
        {
            if (stream == null || !stream.CanWrite) throw new ArgumentException("Writable stream required.", "stream");
            if (profile == null) throw new ArgumentNullException("profile");

            byte[] sourceHash = profile.SourceSha256;
            ResidentialWaterProfile.ValidateSourceHash(sourceHash);
            ResidentialWaterProfile.ValidateCap(profile.LowMaximumStage, "LowMaximumStage");
            ResidentialWaterProfile.ValidateCap(profile.MediumMaximumStage, "MediumMaximumStage");
            ResidentialWaterProfile.ValidateCap(profile.HighMaximumStage, "HighMaximumStage");

            byte[] bytes = new byte[EncodedSize];
            Array.Copy(Magic, 0, bytes, 0, Magic.Length);
            WriteUInt32(bytes, 8, Version);
            Array.Copy(sourceHash, 0, bytes, 12, 32);
            WriteUInt32(bytes, 44, PropertyId);
            WriteEntry(bytes, 48, LowInstance, profile.LowMaximumStage);
            WriteEntry(bytes, 64, MediumInstance, profile.MediumMaximumStage);
            WriteEntry(bytes, 80, HighInstance, profile.HighMaximumStage);
            WriteUInt32(bytes, 96, Crc32(bytes, 0, 96));

            // Validation and encoding finish before the caller's stream is touched.
            stream.Write(bytes, 0, bytes.Length);
        }

        public static ResidentialWaterProfile Read(Stream stream)
        {
            if (stream == null || !stream.CanRead) throw new ArgumentException("Readable stream required.", "stream");
            byte[] bytes = new byte[EncodedSize];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) throw new InvalidDataException("Truncated residential water profile.");
                offset += read;
            }
            if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing bytes after residential water profile.");

            if (ReadUInt32(bytes, 96) != Crc32(bytes, 0, 96))
                throw new InvalidDataException("Residential water profile checksum mismatch.");
            if (!BytesEqual(Magic, bytes, 0))
                throw new InvalidDataException("Invalid residential water profile signature.");
            if (ReadUInt32(bytes, 8) != Version)
                throw new InvalidDataException("Unsupported residential water profile version.");

            byte[] sourceHash = new byte[32];
            Array.Copy(bytes, 12, sourceHash, 0, sourceHash.Length);
            if (IsAllZero(sourceHash)) throw new InvalidDataException("Residential water profile source hash is empty.");
            if (ReadUInt32(bytes, 44) != PropertyId)
                throw new InvalidDataException("Unexpected residential water property ID.");

            ValidateEntry(bytes, 48, LowInstance);
            ValidateEntry(bytes, 64, MediumInstance);
            ValidateEntry(bytes, 80, HighInstance);
            int low = (int)ReadUInt32(bytes, 60);
            int medium = (int)ReadUInt32(bytes, 76);
            int high = (int)ReadUInt32(bytes, 92);
            return new ResidentialWaterProfile(sourceHash, low, medium, high);
        }

        private static void WriteEntry(byte[] bytes, int offset, uint instance, int cap)
        {
            WriteUInt32(bytes, offset, ExemplarType);
            WriteUInt32(bytes, offset + 4, ExemplarGroup);
            WriteUInt32(bytes, offset + 8, instance);
            WriteUInt32(bytes, offset + 12, (uint)cap);
        }

        private static void ValidateEntry(byte[] bytes, int offset, uint expectedInstance)
        {
            if (ReadUInt32(bytes, offset) != ExemplarType)
                throw new InvalidDataException("Unexpected residential water exemplar type.");
            if (ReadUInt32(bytes, offset + 4) != ExemplarGroup)
                throw new InvalidDataException("Unexpected residential water exemplar group.");
            if (ReadUInt32(bytes, offset + 8) != expectedInstance)
                throw new InvalidDataException("Residential water exemplars are missing or out of order.");
            if (ReadUInt32(bytes, offset + 12) > byte.MaxValue)
                throw new InvalidDataException("Residential water cap exceeds the raw UInt8 range.");
        }

        private static bool BytesEqual(byte[] expected, byte[] actual, int offset)
        {
            int index;
            for (index = 0; index < expected.Length; index++)
                if (expected[index] != actual[offset + index]) return false;
            return true;
        }

        private static bool IsAllZero(byte[] bytes)
        {
            int index;
            for (index = 0; index < bytes.Length; index++)
                if (bytes[index] != 0) return false;
            return true;
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)bytes[offset] | ((uint)bytes[offset + 1] << 8) |
                ((uint)bytes[offset + 2] << 16) | ((uint)bytes[offset + 3] << 24);
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
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
    }
}
