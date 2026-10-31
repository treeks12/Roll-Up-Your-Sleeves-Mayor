using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SC4Evolution.Simulation.Taxation
{
    /// <summary>Save format for tax reaction history (sprint 04). Layout: ASCII magic
    /// "SC4ETAX1", payload length, payload (version, per-account entry counts and
    /// day/rate pairs), FNV-1a checksum. Reactions survive save/load byte-exactly;
    /// corruption, truncation and trailing bytes are rejected.</summary>
    public static class TaxHistoryCodec
    {
        private const string Magic = "SC4ETAX1";
        private const int Version = 1;

        public static byte[] Save(TaxAccountingModel model)
        {
            if (model == null) throw new ArgumentNullException("model");
            MemoryStream payload = new MemoryStream();
            WriteInt32(payload, Version);
            List<int>[] days = new List<int>[TaxAccounts.Accounts];
            List<int>[] rates = new List<int>[TaxAccounts.Accounts];
            for (int account = 0; account < TaxAccounts.Accounts; account++)
            {
                days[account] = new List<int>();
                rates[account] = new List<int>();
                model.History(account).CopyEntriesForSave(days[account], rates[account]);
                WriteInt32(payload, days[account].Count);
                for (int i = 0; i < days[account].Count; i++)
                {
                    WriteInt32(payload, days[account][i]);
                    WriteInt32(payload, rates[account][i]);
                }
            }
            byte[] payloadBytes = payload.ToArray();
            MemoryStream output = new MemoryStream();
            byte[] magic = Encoding.ASCII.GetBytes(Magic);
            output.Write(magic, 0, magic.Length);
            WriteInt32(output, payloadBytes.Length);
            output.Write(payloadBytes, 0, payloadBytes.Length);
            WriteUInt32(output, Fnv1a(payloadBytes));
            return output.ToArray();
        }

        public static void ReadInto(TaxAccountingModel model, byte[] data, int windowDays, int maxEntries)
        {
            if (model == null) throw new ArgumentNullException("model");
            if (data == null) throw new ArgumentNullException("data");
            byte[] magic = Encoding.ASCII.GetBytes(Magic);
            if (data.Length < magic.Length + 8) throw new InvalidDataException("Tax history is truncated.");
            for (int i = 0; i < magic.Length; i++)
                if (data[i] != magic[i]) throw new InvalidDataException("Tax history magic mismatch.");
            int offset = magic.Length;
            int payloadLength = ReadInt32(data, ref offset);
            if (payloadLength < 4 || payloadLength > data.Length - offset - 4)
                throw new InvalidDataException("Tax history payload length is invalid.");
            byte[] payload = new byte[payloadLength];
            Buffer.BlockCopy(data, offset, payload, 0, payloadLength);
            offset += payloadLength;
            uint checksum = ReadUInt32(data, ref offset);
            if (offset != data.Length) throw new InvalidDataException("Tax history has trailing bytes.");
            if (checksum != Fnv1a(payload)) throw new InvalidDataException("Tax history checksum mismatch.");
            int position = 0;
            int version = ReadInt32(payload, ref position);
            if (version != Version) throw new InvalidDataException("Tax history version " + version + " is unsupported.");
            for (int account = 0; account < TaxAccounts.Accounts; account++)
            {
                int count = ReadInt32(payload, ref position);
                if (count < 0 || count > (payloadLength - position) / 8)
                    throw new InvalidDataException("Tax history entry count is invalid.");
                int[] days = new int[count];
                int[] rates = new int[count];
                for (int i = 0; i < count; i++)
                {
                    days[i] = ReadInt32(payload, ref position);
                    rates[i] = ReadInt32(payload, ref position);
                }
                model.InstallHistory(account, TaxReactionHistory.FromEntries(windowDays, maxEntries, days, rates));
            }
            if (position != payloadLength) throw new InvalidDataException("Tax history payload has trailing bytes.");
        }

        private static uint Fnv1a(byte[] data)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= 16777619u;
            }
            return hash;
        }

        private static void WriteInt32(Stream stream, int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            stream.Write(bytes, 0, 4);
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            stream.Write(bytes, 0, 4);
        }

        private static int ReadInt32(byte[] data, ref int offset)
        {
            int value = BitConverter.ToInt32(data, offset);
            offset += 4;
            return value;
        }

        private static uint ReadUInt32(byte[] data, ref int offset)
        {
            uint value = BitConverter.ToUInt32(data, offset);
            offset += 4;
            return value;
        }
    }
}
