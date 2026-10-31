using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SC4Evolution.Simulation.Neighborhoods
{
    /// <summary>Compact save format for the neighborhood cycle state (sprint 02). Layout:
    /// ASCII magic "SC4ENBR1", payload length, payload (version, lot count, fixed-order
    /// records), FNV-1a 32-bit checksum of the payload. Round-trips every per-lot field
    /// including generation identity; rejects truncation, corruption, wrong version and
    /// trailing bytes. Integers use the machine's BitConverter layout, matching the other
    /// project codecs' local-save scope.</summary>
    public static class NeighborhoodStateCodec
    {
        private const string Magic = "SC4ENBR1";
        private const int Version = 1;
        private const int MaximumLots = 65536;
        private const int RecordBytes = 36;

        public static byte[] Save(IEnumerable<NeighborhoodLotState> states)
        {
            if (states == null) throw new ArgumentNullException("states");
            List<NeighborhoodLotState> list = new List<NeighborhoodLotState>(states);
            if (list.Count > MaximumLots) throw new ArgumentException("Too many lots to persist.", "states");
            MemoryStream payload = new MemoryStream(4 + 4 + list.Count * RecordBytes);
            WriteInt32(payload, Version);
            WriteInt32(payload, list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                NeighborhoodLotState state = list[i];
                WriteUInt16(payload, state.LotId);
                WriteUInt32(payload, state.BuildIndex);
                WriteInt32(payload, state.Level);
                WriteInt32(payload, state.ResidentClass);
                payload.WriteByte((byte)state.Phase);
                WriteInt32(payload, state.VacancyUpdates);
                WriteInt32(payload, state.MaintenanceStrainUpdates);
                WriteInt32(payload, state.ReplacementSustainedUpdates);
                WriteFloat(payload, state.LastAttractiveness);
                payload.WriteByte((byte)state.LastReason);
                WriteUInt32(payload, state.LastTransitionFrame);
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

        public static List<NeighborhoodLotState> Read(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            byte[] magic = Encoding.ASCII.GetBytes(Magic);
            if (data.Length < magic.Length + 4 + 4 + 4)
                throw new InvalidDataException("Neighborhood state is truncated.");
            for (int i = 0; i < magic.Length; i++)
                if (data[i] != magic[i]) throw new InvalidDataException("Neighborhood state magic mismatch.");
            int offset = magic.Length;
            int payloadLength = ReadInt32(data, ref offset);
            if (payloadLength < 8 || payloadLength > data.Length - offset - 4)
                throw new InvalidDataException("Neighborhood state payload length is invalid.");
            byte[] payload = new byte[payloadLength];
            Buffer.BlockCopy(data, offset, payload, 0, payloadLength);
            offset += payloadLength;
            uint checksum = ReadUInt32(data, ref offset);
            if (offset != data.Length) throw new InvalidDataException("Neighborhood state has trailing bytes.");
            if (checksum != Fnv1a(payload)) throw new InvalidDataException("Neighborhood state checksum mismatch.");
            int position = 0;
            int version = ReadInt32(payload, ref position);
            if (version != Version) throw new InvalidDataException("Neighborhood state version " + version + " is unsupported.");
            int count = ReadInt32(payload, ref position);
            if (count < 0 || count > MaximumLots) throw new InvalidDataException("Neighborhood state lot count is invalid.");
            if (payloadLength != 8 + count * RecordBytes)
                throw new InvalidDataException("Neighborhood state size does not match its lot count.");
            List<NeighborhoodLotState> states = new List<NeighborhoodLotState>(count);
            for (int i = 0; i < count; i++)
            {
                NeighborhoodLotState state = new NeighborhoodLotState();
                state.LotId = ReadUInt16(payload, ref position);
                state.BuildIndex = ReadUInt32(payload, ref position);
                state.Level = ReadInt32(payload, ref position);
                state.ResidentClass = ReadInt32(payload, ref position);
                byte phase = payload[position++];
                if (phase > (byte)NeighborhoodPhase.NativeUnavailable)
                    throw new InvalidDataException("Neighborhood state contains an unknown phase.");
                state.Phase = (NeighborhoodPhase)phase;
                state.VacancyUpdates = ReadInt32(payload, ref position);
                state.MaintenanceStrainUpdates = ReadInt32(payload, ref position);
                state.ReplacementSustainedUpdates = ReadInt32(payload, ref position);
                state.LastAttractiveness = ReadFloat(payload, ref position);
                byte reason = payload[position++];
                if (reason > (byte)NeighborhoodTransitionReason.ObservedReoccupation)
                    throw new InvalidDataException("Neighborhood state contains an unknown reason.");
                state.LastReason = (NeighborhoodTransitionReason)reason;
                state.LastTransitionFrame = ReadUInt32(payload, ref position);
                if (state.Level < 0 || state.ResidentClass < -1 || state.ResidentClass > 2 ||
                    state.VacancyUpdates < 0 || state.MaintenanceStrainUpdates < 0 || state.ReplacementSustainedUpdates < 0 ||
                    float.IsNaN(state.LastAttractiveness) || state.LastAttractiveness < 0f || state.LastAttractiveness > 100f)
                    throw new InvalidDataException("Neighborhood state contains an out-of-range field.");
                states.Add(state);
            }
            return states;
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

        private static void WriteUInt16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
        }

        private static void WriteFloat(Stream stream, float value)
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

        private static ushort ReadUInt16(byte[] data, ref int offset)
        {
            ushort value = (ushort)(data[offset] | (data[offset + 1] << 8));
            offset += 2;
            return value;
        }

        private static float ReadFloat(byte[] data, ref int offset)
        {
            float value = BitConverter.ToSingle(data, offset);
            offset += 4;
            return value;
        }
    }
}
