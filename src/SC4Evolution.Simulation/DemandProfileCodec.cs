using System;
using System.IO;
using System.Text;

namespace SC4Evolution.Simulation
{
    public static class DemandProfileCodec
    {
        private const uint Magic = 0x44453453; // S4ED, little endian.
        private const int Version = 1;
        private const int MaximumBodyBytes = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static void Write(Stream output, DemandProfile profile)
        {
            if (output == null || !output.CanWrite) throw new ArgumentException("Writable stream required.", "output");
            if (profile == null) throw new ArgumentNullException("profile");
            byte[] body;
            using (MemoryStream buffer = new MemoryStream())
            {
                BinaryWriter writer = new BinaryWriter(buffer, Utf8);
                WriteText(writer, profile.SourceIdentity, 4096);
                DemandNodeDefinition[] nodes = profile.Nodes;
                writer.Write(nodes.Length);
                foreach (DemandNodeDefinition node in nodes)
                {
                    writer.Write(node.Key); WriteText(writer, node.Name, 2048);
                    writer.Write(node.Minimum); writer.Write(node.Maximum);
                    writer.Write(node.InitialSupply); writer.Write(node.InitialDemand); writer.Write(node.RegionUse);
                    DemandLink[] links = node.Links;
                    writer.Write(links.Length);
                    foreach (DemandLink link in links)
                    {
                        writer.Write(link.TargetKey); writer.Write((byte)link.Kind); writer.Write(link.Factor);
                    }
                    if (buffer.Length > MaximumBodyBytes) throw new InvalidDataException("Demand profile exceeds size limit.");
                }
                writer.Flush(); body = buffer.ToArray();
            }
            // Do not dispose the writer: its stream belongs to the caller on .NET 3.5.
            BinaryWriter header = new BinaryWriter(output, Utf8);
            header.Write(Magic); header.Write(Version); header.Write(body.Length); header.Write(Crc32(body));
            header.Write(body); header.Flush();
        }

        public static DemandProfile Read(Stream input)
        {
            if (input == null || !input.CanRead) throw new ArgumentException("Readable stream required.", "input");
            BinaryReader header = new BinaryReader(input, Utf8);
            try
            {
                if (header.ReadUInt32() != Magic) throw new InvalidDataException("Invalid demand profile signature.");
                if (header.ReadInt32() != Version) throw new InvalidDataException("Unsupported demand profile version.");
                int length = header.ReadInt32();
                if (length < 0 || length > MaximumBodyBytes) throw new InvalidDataException("Invalid demand profile length.");
                uint expectedCrc = header.ReadUInt32();
                byte[] body = header.ReadBytes(length);
                if (body.Length != length) throw new InvalidDataException("Truncated demand profile.");
                if (input.ReadByte() != -1) throw new InvalidDataException("Trailing data after demand profile.");
                if (Crc32(body) != expectedCrc) throw new InvalidDataException("Demand profile checksum mismatch.");
                using (MemoryStream buffer = new MemoryStream(body, false))
                {
                    BinaryReader reader = new BinaryReader(buffer, Utf8);
                    string source = ReadText(reader, 4096);
                    int count = reader.ReadInt32();
                    if (count < 1 || count > DemandProfile.MaximumNodes) throw new InvalidDataException("Invalid demand node count.");
                    DemandNodeDefinition[] nodes = new DemandNodeDefinition[count];
                    int linkCount = 0;
                    for (int index = 0; index < nodes.Length; index++)
                    {
                        uint key = reader.ReadUInt32(); string name = ReadText(reader, 2048);
                        float minimum = reader.ReadSingle(), maximum = reader.ReadSingle();
                        float supply = reader.ReadSingle(), demand = reader.ReadSingle();
                        byte regionUse = reader.ReadByte();
                        int links = reader.ReadInt32();
                        if (links < 0 || links > DemandProfile.MaximumLinks - linkCount) throw new InvalidDataException("Invalid demand link count.");
                        linkCount += links;
                        DemandLink[] nodeLinks = new DemandLink[links];
                        for (int link = 0; link < links; link++)
                        {
                            uint target = reader.ReadUInt32(); DemandLinkKind kind = (DemandLinkKind)reader.ReadByte();
                            nodeLinks[link] = new DemandLink(target, kind, reader.ReadSingle());
                        }
                        nodes[index] = new DemandNodeDefinition(key, name, minimum, maximum, supply, demand, regionUse, nodeLinks);
                    }
                    if (buffer.Position != buffer.Length) throw new InvalidDataException("Trailing data inside demand profile.");
                    return new DemandProfile(source, nodes);
                }
            }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated demand profile.", error); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid demand profile text.", error); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid demand profile values.", error); }
        }

        private static void WriteText(BinaryWriter writer, string text, int maximumCharacters)
        {
            if (text == null || text.Length > maximumCharacters) throw new InvalidDataException("Invalid profile text length.");
            byte[] bytes = Utf8.GetBytes(text);
            writer.Write(bytes.Length); writer.Write(bytes);
        }

        private static string ReadText(BinaryReader reader, int maximumCharacters)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > maximumCharacters * 4) throw new InvalidDataException("Invalid profile text byte length.");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidDataException("Truncated profile text.");
            string text = Utf8.GetString(bytes);
            if (text.Length > maximumCharacters) throw new InvalidDataException("Profile text exceeds character limit.");
            return text;
        }

        private static uint Crc32(byte[] bytes)
        {
            uint crc = uint.MaxValue;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1u) == 0 ? 0u : 0xEDB88320u);
            }
            return ~crc;
        }
    }
}
