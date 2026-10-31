using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SC4Evolution.Simulation;

namespace SC4Evolution.Mod.Runtime
{
    // One authoritative storage entry. The inner capacity codec binds the exact
    // embedded class bytes; the envelope digest also protects framing/nullness.
    internal static class EconomicSaveEnvelope
    {
        internal const int MaximumPartBytes = 16 * 1024 * 1024;
        internal const int MaximumBytes = 2 * MaximumPartBytes + 50;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SC4EPAR1");

        internal static byte[] Encode(byte[] classes, byte[] capacity)
        {
            ValidateParts(classes, capacity);
            using (MemoryStream stream = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write(Magic); writer.Write((ushort)1);
                    writer.Write(classes.Length); writer.Write(capacity == null ? -1 : capacity.Length);
                    writer.Write(classes); if (capacity != null) writer.Write(capacity);
                    byte[] body = stream.ToArray();
                    using (SHA256 hash = SHA256.Create()) writer.Write(hash.ComputeHash(body));
                    return stream.ToArray();
                }
            }
        }

        internal static void Decode(byte[] payload, out byte[] classes, out byte[] capacity)
        {
            classes = null; capacity = null;
            if (payload == null || payload.Length < 50 || payload.Length > MaximumBytes)
                throw new InvalidDataException("Economic pair envelope size is invalid.");
            using (MemoryStream stream = new MemoryStream(payload, false))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (!Same(reader.ReadBytes(8), Magic) || reader.ReadUInt16() != 1)
                    throw new InvalidDataException("Unsupported economic pair envelope identifier/version.");
                int classLength = reader.ReadInt32(), capacityLength = reader.ReadInt32();
                if (classLength < 1 || classLength > MaximumPartBytes || capacityLength < -1 || capacityLength == 0 || capacityLength > MaximumPartBytes ||
                    (long)classLength + (capacityLength < 0 ? 0 : capacityLength) + 50L != payload.Length)
                    throw new InvalidDataException("Economic pair envelope lengths are invalid.");
                byte[] exactClasses = reader.ReadBytes(classLength);
                byte[] exactCapacity = capacityLength < 0 ? null : reader.ReadBytes(capacityLength);
                byte[] expected = reader.ReadBytes(32);
                using (SHA256 hash = SHA256.Create())
                    if (!Same(expected, hash.ComputeHash(payload, 0, payload.Length - 32)))
                        throw new InvalidDataException("Economic pair envelope checksum mismatch.");
                ValidateParts(exactClasses, exactCapacity);
                classes = exactClasses; capacity = exactCapacity;
            }
        }

        private static void ValidateParts(byte[] classes, byte[] capacity)
        {
            if (classes == null || classes.Length < 1 || classes.Length > MaximumPartBytes ||
                (capacity != null && (capacity.Length < 1 || capacity.Length > MaximumPartBytes)))
                throw new InvalidDataException("Economic pair payload part is missing or oversized.");
            using (MemoryStream stream = new MemoryStream(classes, false)) ResidentialClassLedgerCodec.Read(stream);
            if (capacity != null)
                using (MemoryStream stream = new MemoryStream(capacity, false)) ResidentialCapacityLedgerCodec.ReadBound(stream, classes);
        }
        private static bool Same(byte[] first, byte[] second)
        {
            if (first.Length != second.Length) return false;
            int difference = 0; for (int i = 0; i < first.Length; i++) difference |= first[i] ^ second[i];
            return difference == 0;
        }
    }
}
