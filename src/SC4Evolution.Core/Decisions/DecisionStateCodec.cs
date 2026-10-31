using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SC4Evolution.Core.Decisions
{
    /// <summary>Persistence for active decision events (sprint 06): magic "SC4EDCN1",
    /// payload length, payload (version, count, per-event cause key + id + frames +
    /// urgency/cooldown + recommendation fields), FNV-1a checksum. Reactions (cooldown
    /// memory) survive save/load so a reloaded cabinet cannot re-spam the same cause.</summary>
    public static class DecisionStateCodec
    {
        private const string Magic = "SC4EDCN1";
        private const int Version = 1;
        private const int MaximumEvents = 512;

        public static byte[] Save(DecisionEngine engine)
        {
            if (engine == null) throw new ArgumentNullException("engine");
            MemoryStream payload = new MemoryStream();
            WriteInt32(payload, Version);
            List<KeyValuePair<string, DecisionEvent>> entries = new List<KeyValuePair<string, DecisionEvent>>(engine.Entries());
            WriteInt32(payload, entries.Count);
            foreach (KeyValuePair<string, DecisionEvent> pair in entries)
            {
                WriteString(payload, pair.Key);
                DecisionEvent decision = pair.Value;
                WriteInt64(payload, decision.Id);
                WriteUInt32(payload, decision.FirstFrame);
                WriteUInt32(payload, decision.ExpiresAtFrame);
                WriteUInt32(payload, decision.CooldownFrames);
                WriteUInt32(payload, decision.LastAlertFrame);
                payload.WriteByte((byte)decision.Urgency);
                payload.WriteByte((byte)decision.Recommendation.Advisor);
                payload.WriteByte((byte)decision.Recommendation.Consequence);
                WriteString(payload, decision.Recommendation.Department ?? string.Empty);
                WriteString(payload, decision.Recommendation.SuggestedAction ?? string.Empty);
                WriteString(payload, decision.Recommendation.IndicatorName ?? string.Empty);
                WriteDouble(payload, decision.Recommendation.IndicatorValue);
                WriteDouble(payload, decision.Recommendation.ExpectedIndicatorAfterAction);
                WriteString(payload, decision.Recommendation.TargetKey ?? string.Empty);
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

        public static void ReadInto(DecisionEngine engine, byte[] data)
        {
            if (engine == null) throw new ArgumentNullException("engine");
            if (data == null) throw new ArgumentNullException("data");
            byte[] magic = Encoding.ASCII.GetBytes(Magic);
            if (data.Length < magic.Length + 8) throw new InvalidDataException("Decision state is truncated.");
            for (int i = 0; i < magic.Length; i++)
                if (data[i] != magic[i]) throw new InvalidDataException("Decision state magic mismatch.");
            int offset = magic.Length;
            int payloadLength = ReadInt32(data, ref offset);
            if (payloadLength < 8 || payloadLength > data.Length - offset - 4)
                throw new InvalidDataException("Decision state payload length is invalid.");
            byte[] payload = new byte[payloadLength];
            Buffer.BlockCopy(data, offset, payload, 0, payloadLength);
            offset += payloadLength;
            uint checksum = ReadUInt32(data, ref offset);
            if (offset != data.Length) throw new InvalidDataException("Decision state has trailing bytes.");
            if (checksum != Fnv1a(payload)) throw new InvalidDataException("Decision state checksum mismatch.");
            int position = 0;
            int version = ReadInt32(payload, ref position);
            if (version != Version) throw new InvalidDataException("Decision state version " + version + " is unsupported.");
            int count = ReadInt32(payload, ref position);
            if (count < 0 || count > MaximumEvents) throw new InvalidDataException("Decision state event count is invalid.");
            for (int i = 0; i < count; i++)
            {
                string causeKey = ReadString(payload, ref position);
                DecisionEvent decision = new DecisionEvent();
                decision.Id = ReadInt64(payload, ref position);
                decision.FirstFrame = ReadUInt32(payload, ref position);
                decision.ExpiresAtFrame = ReadUInt32(payload, ref position);
                decision.CooldownFrames = ReadUInt32(payload, ref position);
                decision.LastAlertFrame = ReadUInt32(payload, ref position);
                byte urgency = payload[position++];
                byte advisor = payload[position++];
                byte consequence = payload[position++];
                if (urgency > (byte)DecisionUrgency.Critical || advisor > 6 || consequence > (byte)DecisionConsequence.ServiceUnderfunding)
                    throw new InvalidDataException("Decision state contains an unknown enum value.");
                decision.Urgency = (DecisionUrgency)urgency;
                AdvisorRecommendation recommendation = new AdvisorRecommendation();
                recommendation.Advisor = (AdvisorId)advisor;
                recommendation.Consequence = (DecisionConsequence)consequence;
                recommendation.Department = ReadString(payload, ref position);
                recommendation.SuggestedAction = ReadString(payload, ref position);
                recommendation.IndicatorName = ReadString(payload, ref position);
                recommendation.IndicatorValue = ReadDouble(payload, ref position);
                recommendation.ExpectedIndicatorAfterAction = ReadDouble(payload, ref position);
                recommendation.TargetKey = ReadString(payload, ref position);
                if (string.IsNullOrEmpty(causeKey) || string.IsNullOrEmpty(recommendation.SuggestedAction))
                    throw new InvalidDataException("Decision state contains an empty required field.");
                decision.Recommendation = recommendation;
                engine.InstallEntry(causeKey, decision);
            }
            if (position != payloadLength) throw new InvalidDataException("Decision state payload has trailing bytes.");
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

        private static void WriteInt64(Stream stream, long value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            stream.Write(bytes, 0, 8);
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            stream.Write(bytes, 0, 4);
        }

        private static void WriteDouble(Stream stream, double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            stream.Write(bytes, 0, 8);
        }

        private static void WriteString(Stream stream, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            WriteInt32(stream, bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static int ReadInt32(byte[] data, ref int offset)
        {
            int value = BitConverter.ToInt32(data, offset);
            offset += 4;
            return value;
        }

        private static long ReadInt64(byte[] data, ref int offset)
        {
            long value = BitConverter.ToInt64(data, offset);
            offset += 8;
            return value;
        }

        private static uint ReadUInt32(byte[] data, ref int offset)
        {
            uint value = BitConverter.ToUInt32(data, offset);
            offset += 4;
            return value;
        }

        private static double ReadDouble(byte[] data, ref int offset)
        {
            double value = BitConverter.ToDouble(data, offset);
            offset += 8;
            return value;
        }

        private static string ReadString(byte[] data, ref int offset)
        {
            int length = ReadInt32(data, ref offset);
            if (length < 0 || length > data.Length - offset) throw new InvalidDataException("Decision state string is invalid.");
            string value = Encoding.UTF8.GetString(data, offset, length);
            offset += length;
            return value;
        }
    }
}
