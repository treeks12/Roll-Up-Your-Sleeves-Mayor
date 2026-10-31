using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SC4Data;

// Independent bounded reader and software projection. Format references are documented in analysis/sc4-advisor-rendering.md.
internal static class Program
{
    record V(float X, float Y, float Z, float U, float T);
    record Primitive(uint Kind, int First, int Count);
    record Part(V[] Vert, int[] Ind, Primitive[] Prim, uint Texture);
    record AnimatedModel(int FrameRate, List<List<Part>> Frames);
    record Image(int Width, int Height, byte[] Pixels);
    static readonly string[] Names = ["Utilities", "PublicSafety", "Environment", "Transport", "Finance", "EducationHealth", "UrbanPlanning"];
    static readonly int[] Bases = [0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60];
    static readonly string[] Moods = ["neutral", "happy", "attention", "alarmed"];
    static readonly int[] States = [0, 2, 5, 8];
    static Dictionary<uint, byte[]> textures = new();
    static BinaryReader Reader(byte[] d) => new(new MemoryStream(d, false));
    static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    static int Count(uint n, int cap = 4096) { Check(n <= cap, "Count cap exceeded"); return (int)n; }
    static float Number(BinaryReader r) { float f = r.ReadSingle(); Check(float.IsFinite(f) && Math.Abs(f) < 100000, "Nonfinite/out-of-range geometry"); return f; }
    static AnimatedModel Model(byte[] data)
    {
        Check(data.Length < 16000000 && Encoding.ASCII.GetString(data, 0, 4) == "3DMD", "S3D header");
        using var stream = Reader(data); stream.BaseStream.Position = 8; BinaryReader Next(string tag) { Check(Encoding.ASCII.GetString(stream.ReadBytes(4)) == tag, "Expected " + tag); stream.ReadUInt32(); return stream; }
        var h = Next("HEAD"); int major = h.ReadUInt16(), minor = h.ReadUInt16(); Check(major == 1 && minor >= 4 && minor <= 5, "Only verified S3D 1.4/1.5 supported");
        var v = Next("VERT"); var verts = new V[Count(v.ReadUInt32())][];
        for (int i = 0; i < verts.Length; i++) { v.ReadUInt16(); int n = v.ReadUInt16(); uint format = v.ReadUInt32(); Check(format == 0x80004001, "Unsupported vertex format " + format.ToString("X")); var a = new V[n]; for (int j = 0; j < n; j++) { float x = Number(v), y = Number(v), z = Number(v), u = Number(v), t = Number(v); Check(Math.Abs(u) < 100 && Math.Abs(t) < 100, "UV bounds"); a[j] = new(x, y, z, u, t); } verts[i] = a; }
        var ix = Next("INDX"); var inds = new int[Count(ix.ReadUInt32())][];
        for (int i = 0; i < inds.Length; i++) { ix.ReadUInt16(); Check(ix.ReadUInt16() == 2, "Index width"); int n = ix.ReadUInt16(); inds[i] = new int[n]; for (int j = 0; j < n; j++) inds[i][j] = ix.ReadUInt16(); }
        var pr = Next("PRIM"); var prims = new Primitive[Count(pr.ReadUInt32())][];
        for (int i = 0; i < prims.Length; i++) { int n = pr.ReadUInt16(); Check(n <= 64, "Primitive cap"); prims[i] = new Primitive[n]; for (int j = 0; j < n; j++) prims[i][j] = new(pr.ReadUInt32(), Count(pr.ReadUInt32(), 65535), Count(pr.ReadUInt32(), 65535)); }
        var mat = Next("MATS"); var mats = new uint[Count(mat.ReadUInt32())];
        for (int i = 0; i < mats.Length; i++) { mat.ReadBytes(14); mat.ReadByte(); int n = mat.ReadByte(); Check(n == 1, "Only single texture materials supported"); mats[i] = mat.ReadUInt32(); mat.ReadBytes(minor == 5 ? 8 : 6); int len = mat.ReadByte(); Check(mat.ReadBytes(len).Length == len, "Material name"); }
        Part MakePart(int a, int b, int c, int d) { Check(a >= 0 && a < verts.Length && b >= 0 && b < inds.Length && c >= 0 && c < prims.Length && d >= 0 && d < mats.Length, "ANIM buffer reference"); foreach (var p in prims[c]) { Check(p.Kind == 0 && p.Count % 3 == 0, "Only triangle lists supported"); Check(p.First + p.Count <= inds[b].Length, "Primitive bounds"); for (int k = p.First; k < p.First + p.Count; k++) Check(inds[b][k] < verts[a].Length, "Vertex index bounds"); } return new(verts[a], inds[b], prims[c], mats[d]); }
        {
            var an = Next("ANIM"); int frames = an.ReadUInt16(), rate = an.ReadUInt16(); Check(frames > 0 && frames <= 1024, "Frame cap"); an.ReadBytes(10); int meshes = an.ReadUInt16(); Check(meshes <= 256, "Mesh cap");
            var frameParts = Enumerable.Range(0, frames).Select(_ => new List<Part>()).ToList();
            for (int m = 0; m < meshes; m++)
            {
                int len = an.ReadByte(); an.ReadByte(); Check(an.ReadBytes(len).Length == len, "Mesh name");
                for (int f = 0; f < frames; f++)
                {
                    int a = an.ReadInt16(), b = an.ReadInt16(), c = an.ReadInt16(), d = an.ReadInt16();
                    frameParts[f].Add(MakePart(a, b, c, d));
                }
                Check(an.BaseStream.Position <= an.BaseStream.Length, "ANIM truncation");
            }
            Check(frameParts.Any(parts => parts.Count != 0), "ANIM has no drawable meshes");
            Console.WriteLine($"  animation frames {frames} at {rate} fps, meshes {meshes}");
            return new AnimatedModel(rate, frameParts);
        }
        throw new InvalidDataException("ANIM chunk missing");
    }
    static byte[] Color(ushort n) => [(byte)(((n >> 11) & 31) * 255 / 31), (byte)(((n >> 5) & 63) * 255 / 63), (byte)((n & 31) * 255 / 31), 255];
    static Image Decode(byte[] data)
    {
        using var r = Reader(data); Check(Encoding.ASCII.GetString(r.ReadBytes(4)) == "SHPI", "FSH magic"); Check(r.ReadUInt32() == data.Length, "FSH length"); Check(r.ReadUInt32() == 1, "FSH image count"); r.ReadBytes(8); int offset = r.ReadInt32(); Check(offset >= 24 && offset <= data.Length - 16, "FSH offset"); r.BaseStream.Position = offset; int type = r.ReadByte(); r.ReadBytes(3); int w = r.ReadUInt16(), h = r.ReadUInt16(); Check((type == 96 || type == 97) && w > 0 && h > 0 && w <= 1024 && h <= 1024, "FSH dimensions/type"); r.ReadBytes(8); var pixels = new byte[w * h * 4];
        for (int by = 0; by < h; by += 4) for (int bx = 0; bx < w; bx += 4) { ulong alpha = type == 97 ? r.ReadUInt64() : 0; ushort a = r.ReadUInt16(), b = r.ReadUInt16(); var c = new byte[4][]; c[0] = Color(a); c[1] = Color(b); for (int i = 2; i < 4; i++) { c[i] = new byte[4]; c[i][3] = 255; for (int k = 0; k < 3; k++) c[i][k] = (byte)(a > b || type == 97 ? ((i == 2 ? 2 : 1) * c[0][k] + (i == 2 ? 1 : 2) * c[1][k]) / 3 : (i == 2 ? (c[0][k] + c[1][k]) / 2 : 0)); } if (a <= b && type == 96) c[3][3] = 0; uint bits = r.ReadUInt32(); for (int j = 0; j < 16; j++) { int x = bx + j % 4, y = by + j / 4; if (x >= w || y >= h) continue; var q = c[(bits >> (2 * j)) & 3]; int o = (y * w + x) * 4; Array.Copy(q, 0, pixels, o, 4); if (type == 97) pixels[o + 3] = (byte)(((alpha >> (4 * j)) & 15) * 17); } }
        return new(w, h, pixels);
    }
    static Image Render(List<Part> parts)
    {
        const int size = 384; var output = new byte[size * size * 4]; var depth = Enumerable.Repeat(float.PositiveInfinity, size * size).ToArray(); var all = parts.SelectMany(p => p.Vert).ToArray(); float minX = all.Min(v => v.X), maxX = all.Max(v => v.X), minY = all.Min(v => v.Y), maxY = all.Max(v => v.Y); float scale = 350 / Math.Max(maxX - minX, maxY - minY); Check(float.IsFinite(scale) && scale > 0, "Empty geometry");
        V Screen(V q) => new((q.X - (minX + maxX) / 2) * scale + size / 2, size / 2 - (q.Y - (minY + maxY) / 2) * scale, q.Z, q.U, q.T);
        static float Edge(V a, V b, float x, float y) => (x - a.X) * (b.Y - a.Y) - (y - a.Y) * (b.X - a.X);
        foreach (var part in parts) { var image = Decode(textures[part.Texture]); foreach (var prim in part.Prim) for (int k = prim.First; k < prim.First + prim.Count; k += 3) { var a = Screen(part.Vert[part.Ind[k]]); var b = Screen(part.Vert[part.Ind[k + 1]]); var c = Screen(part.Vert[part.Ind[k + 2]]); float area = Edge(a, b, c.X, c.Y); if (Math.Abs(area) < 0.0001) continue; int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)))), x1 = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X)))); int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)))), y1 = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y)))); for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) { float wa = Edge(b, c, x + .5f, y + .5f) / area, wb = Edge(c, a, x + .5f, y + .5f) / area, wc = 1 - wa - wb; if (wa < 0 || wb < 0 || wc < 0) continue; float z = wa * a.Z + wb * b.Z + wc * c.Z; int n = y * size + x; if (z >= depth[n]) continue; float u = wa * a.U + wb * b.U + wc * c.U, t = wa * a.T + wb * b.T + wc * c.T; int tx = Math.Clamp((int)(u * image.Width), 0, image.Width - 1), ty = Math.Clamp((int)(t * image.Height), 0, image.Height - 1), o = (ty * image.Width + tx) * 4; if (image.Pixels[o + 3] < 128) continue; depth[n] = z; Array.Copy(image.Pixels, o, output, n * 4, 4); } } }
        return new(size, size, output);
    }
    static void WriteAtomically(ProjectLayout layout, string destination, Action<Stream> write)
    {
        var safeDestination = layout.ResolveGeneratedOutputIn(destination, "sc4-advisor-assets");
        var directory = Path.GetDirectoryName(safeDestination)!;
        var temporaryCandidate = Path.Combine(directory, $".{Path.GetFileName(safeDestination)}.{Guid.NewGuid():N}.tmp");
        var temporaryPath = layout.ResolveGeneratedOutputIn(temporaryCandidate, "sc4-advisor-assets");
        var ownsTemporary = false;
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            // Replace the directory entry instead of opening a destination that could be a hard link.
            safeDestination = layout.ResolveGeneratedOutputIn(safeDestination, "sc4-advisor-assets");
            File.Move(temporaryPath, safeDestination, overwrite: true);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary && File.Exists(temporaryPath))
            {
                try
                {
                    var safeTemporary = layout.ResolveGeneratedOutputIn(temporaryPath, "sc4-advisor-assets");
                    File.Delete(safeTemporary);
                }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Console.Error.WriteLine($"warning: could not remove owned temporary output '{temporaryPath}': {cleanupError.Message}");
                }
            }
        }
    }

    static void WriteLines(ProjectLayout layout, string destination, IEnumerable<string> lines)
    {
        WriteAtomically(layout, destination, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
            foreach (string line in lines) writer.WriteLine(line);
        });
    }

    static void Png(Stream s, Image image)
    {
        s.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        void Block(string tag, byte[] data) { void Be(uint n) => s.Write([(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n]); Be((uint)data.Length); byte[] bytes = Encoding.ASCII.GetBytes(tag).Concat(data).ToArray(); s.Write(bytes); uint crc = 0xffffffff; foreach (byte b in bytes) { crc ^= b; for (int j = 0; j < 8; j++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); } Be(~crc); }
        byte[] ihdr = new byte[13]; ihdr[2] = (byte)(image.Width >> 8); ihdr[3] = (byte)image.Width; ihdr[6] = (byte)(image.Height >> 8); ihdr[7] = (byte)image.Height; ihdr[8] = 8; ihdr[9] = 6; Block("IHDR", ihdr); using var memory = new MemoryStream(); using (var z = new ZLibStream(memory, CompressionLevel.Optimal, true)) for (int y = 0; y < image.Height; y++) { z.WriteByte(0); z.Write(image.Pixels, y * image.Width * 4, image.Width * 4); }
        Block("IDAT", memory.ToArray()); Block("IEND", []);
    }
    static void SelfTest()
    {
        byte[] Fixture(int type, ulong alpha, uint indices)
        {
            using var memory = new MemoryStream();
            using var writer = new BinaryWriter(memory, Encoding.ASCII, true);
            writer.Write(Encoding.ASCII.GetBytes("SHPI")); writer.Write(type == 96 ? 48 : 56);
            writer.Write(1); writer.Write(Encoding.ASCII.GetBytes("G2640000")); writer.Write(24);
            writer.Write((byte)type); writer.Write(new byte[3]); writer.Write((ushort)4); writer.Write((ushort)4); writer.Write(new byte[8]);
            if (type == 97) writer.Write(alpha);
            writer.Write((ushort)0xf800); writer.Write((ushort)0x07e0); writer.Write(indices);
            return memory.ToArray();
        }
        var dxt1 = Decode(Fixture(96, 0, 0xe4));
        Check(dxt1.Pixels[0] == 255 && dxt1.Pixels[1] == 0 && dxt1.Pixels[3] == 255, "DXT1 red endpoint");
        Check(dxt1.Pixels[4] == 0 && dxt1.Pixels[5] == 255, "DXT1 green endpoint");
        Check(dxt1.Pixels[8] == 170 && dxt1.Pixels[9] == 85, "DXT1 interpolation");
        var dxt3 = Decode(Fixture(97, 0xfedcba9876543210, 0));
        Check(dxt3.Pixels[3] == 0 && dxt3.Pixels[7] == 17 && dxt3.Pixels[63] == 255, "DXT3 alpha nibble order");
        bool rejected = false;
        try { Decode(Fixture(96, 0, 0)[..^1]); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Truncated FSH rejected");
        rejected = false;
        var malformed = Fixture(96, 0, 0); malformed[28] = 0; malformed[29] = 0;
        try { Decode(malformed); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Zero FSH width rejected");
        Console.WriteLine("6 codec checks passed.");
    }
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") { SelfTest(); return 0; }
        try
        {
            Check(args.Length <= 1, "Usage: SC4AdvisorAssets [project-root] | --self-test");
            var layout = ProjectLayout.Load(args.Length == 1 ? args[0] : null);
            string output = Path.GetDirectoryName(layout.ResolveGeneratedOutput("analysis/generated/sc4-advisor-assets/manifest.csv"))!;
            var models = new Dictionary<uint, (byte[] Data, AnimatedModel Model)>();
            using (var archive = DbpfArchive.Open(layout.ResolveArchive("SimCity_1.dat")))
            {
                for (int i = 0; i < Names.Length; i++)
                for (int j = 0; j < Moods.Length; j++)
                {
                    uint id = 0x2A42B500u + (uint)(Bases[i] + States[j]);
                    byte[] data = archive.ReadResource(new Tgi(0x5AD0E817, 0xBADB57F1, id)).Data;
                    Console.WriteLine($"Read {Names[i]} {Moods[j]} {id:X8}");
                    models.Add(id, (data, Model(data)));
                }
            }
            var required = models.Values.SelectMany(model => model.Model.Frames).SelectMany(frame => frame).Select(part => part.Texture).ToHashSet();
            var provenance = new List<string> { "archive,tgi,decodedBytes,sha256" };
            foreach (string name in new[] { "SimCity_2.dat", "SimCity_3.dat" })
            {
                using var archive = DbpfArchive.Open(layout.ResolveArchive(name));
                foreach (var entry in archive.Entries.Where(entry => entry.Tgi.Type == 0x7AB50E44 && entry.Tgi.Group == 0x1ABE787D && required.Contains(entry.Tgi.Instance)))
                {
                    byte[] data = archive.ReadResource(entry.Tgi).Data;
                    Check(data.Length <= 2000000, "FSH input cap");
                    Check(textures.TryAdd(entry.Tgi.Instance, data), "Ambiguous duplicate texture");
                    provenance.Add($"{name},{entry.Tgi},{data.Length},{Convert.ToHexString(SHA256.HashData(data))}");
                }
            }
            Check(required.SetEquals(textures.Keys), "Unresolved material texture");
            var manifest = new List<string> { "advisor,state,model,sha256,status" };
            for (int i = 0; i < Names.Length; i++)
            for (int j = 0; j < Moods.Length; j++)
            {
                uint id = 0x2A42B500u + (uint)(Bases[i] + States[j]);
                var model = models[id];
                var image = Render(model.Model.Frames[0]);
                WriteAtomically(layout, Path.Combine(output, Names[i] + "-" + Moods[j] + ".png"), stream => Png(stream, image));
                string hash = Convert.ToHexString(SHA256.HashData(model.Data));
                manifest.Add($"{Names[i]},{Moods[j]},{id:X8},{hash},rendered-first-frame;frames={model.Model.Frames.Count};rate={model.Model.FrameRate}");
                provenance.Add($"SimCity_1.dat,5AD0E817:BADB57F1:{id:X8},{model.Data.Length},{hash}");
                for (int frame = 0; frame < model.Model.Frames.Count; frame++)
                {
                    Image animation = Render(model.Model.Frames[frame]);
                    WriteAtomically(layout, Path.Combine(output, Names[i] + "-" + Moods[j] + "-f" + frame.ToString("D3") + ".png"), stream => Png(stream, animation));
                }
            }
            WriteLines(layout, Path.Combine(output, "manifest.csv"), manifest);
            WriteLines(layout, Path.Combine(output, "source-hashes.csv"), provenance);
            Console.WriteLine($"Rendered 28/28 local portraits with {textures.Count} FSH textures to {output}");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
