using System.Buffers.Binary;
using System.Text;
using SC4Data;

var tests = new (string Name, Action Run)[]
{
    ("QFS literal run decodes", QfsLiteralRunDecodes),
    ("QFS overlapping back-reference decodes", QfsOverlappingBackReferenceDecodes),
    ("QFS malformed and truncated inputs reject", QfsMalformedInputsReject),
    ("QFS prefix and decoded sizes are checked", QfsSizeFieldsAreChecked),
    ("DBPF v0 lists and selects a bounded raw entry", DbpfVersion0ReadsRawEntry),
    ("DBPF v2 reads resource ID and selection", DbpfVersion2ReadsResourceId),
    ("DBPF DIR lookup and QFS extraction work", DbpfDirectoryLoadsCompressedResource),
    ("DBPF rejects index and payload ranges past EOF", DbpfRejectsOutOfBounds),
    ("DBPF rejects ambiguous and absent TGI selection", DbpfSelectionIsUnambiguous),
    ("DBPF rejects malformed compression directory", DbpfRejectsMalformedDirectory),
    ("LTEXT decodes UTF-16LE and emits UTF-8", LTextDecodesAndEncodes),
    ("LTEXT checks character bounds and preserves control field", LTextChecksBoundsAndPreservesControl),
    ("EQZB exemplar decodes header, parent TGI, name, type, and repetitions", ExemplarDecodesProperties),
    ("CQZB cohort header is supported", CohortHeaderDecodes),
    ("Exemplar scalar numeric formats decode with signedness", ExemplarNumericFormatsDecode),
    ("Exemplar rejects malformed counts, truncation, and trailing bytes", ExemplarMalformedBoundsReject),
    ("Exemplar labels unknown types and string encodings unsupported", ExemplarUnsupportedVariantsReject),
    ("Output paths remain inside analysis/generated", GeneratedOutputMustBeContained)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
        Console.Error.WriteLine(exception.StackTrace);
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} checks passed.");
return failed == 0 ? 0 : 1;

static void QfsLiteralRunDecodes()
{
    var decoded = Encoding.ASCII.GetBytes("ABCD");
    var stored = QfsFixture.Encode(decoded);
    var actual = QfsDecoder.DecodeSc4(stored, (uint)stored.Length, (uint)decoded.Length, 1024);
    Assert.SequenceEqual(decoded, actual);
}

static void QfsOverlappingBackReferenceDecodes()
{
    var decoded = Encoding.ASCII.GetBytes("ABCABCABC");
    var stored = QfsFixture.EncodeWithStream(decoded, [0x0F, 0x02, (byte)'A', (byte)'B', (byte)'C', 0xFC]);
    var actual = QfsDecoder.DecodeSc4(stored, (uint)stored.Length, (uint)decoded.Length, 1024);
    Assert.SequenceEqual(decoded, actual);
}

static void QfsMalformedInputsReject()
{
    var truncatedShortReference = QfsFixture.EncodeWithStream([0x41, 0x42, 0x43], [0x00]);
    Assert.Throws<InvalidDataException>(() => QfsDecoder.DecodeSc4(truncatedShortReference, (uint)truncatedShortReference.Length, 3, 1024));

    var truncatedTail = QfsFixture.EncodeWithStream([0x41, 0x42, 0x43], [0xFF, 0x41, 0x42]);
    Assert.Throws<InvalidDataException>(() => QfsDecoder.DecodeSc4(truncatedTail, (uint)truncatedTail.Length, 3, 1024));

    var invalidDistance = QfsFixture.EncodeWithStream([0x41, 0x42, 0x43], [0x00, 0x03, 0xFC]);
    Assert.Throws<InvalidDataException>(() => QfsDecoder.DecodeSc4(invalidDistance, (uint)invalidDistance.Length, 3, 1024));

    var noEndMarker = QfsFixture.EncodeWithStream([0x41, 0x42, 0x43, 0x44], [0xE0, 0x41, 0x42, 0x43, 0x44]);
    Assert.Throws<InvalidDataException>(() => QfsDecoder.DecodeSc4(noEndMarker, (uint)noEndMarker.Length, 4, 1024));

    Assert.Throws<InvalidDataException>(() => QfsDecoder.DecodeSc4([0x01, 0x02], 2, 0, 1024));
}

static void QfsSizeFieldsAreChecked()
{
    var stored = QfsFixture.Encode(Encoding.ASCII.GetBytes("ABCD"));
    Assert.Throws<InvalidDataException>(() => QfsDecoder.DecodeSc4(stored, (uint)stored.Length, 5, 1024));
    Assert.Throws<InvalidDataException>(() => QfsDecoder.DecodeSc4(stored, (uint)(stored.Length + 1), 4, 1024));

    var badPrefix = (byte[])stored.Clone();
    BinaryPrimitives.WriteUInt32LittleEndian(badPrefix, (uint)(badPrefix.Length - 1));
    Assert.Throws<InvalidDataException>(() => QfsDecoder.DecodeSc4(badPrefix, (uint)badPrefix.Length, 4, 1024));

    Assert.Throws<InvalidDataException>(() => QfsDecoder.DecodeSc4(stored, (uint)stored.Length, 4, 3));
}

static void DbpfVersion0ReadsRawEntry()
{
    var tgi = new Tgi(0x12345678, 0x90ABCDEF, 0x01020304);
    var payload = new byte[] { 1, 2, 3, 4 };
    using var fixture = ArchiveFixture.Create([new FixtureEntry(tgi, payload)], indexVersion: 0);
    using var archive = DbpfArchive.Open(fixture.Path);
    Assert.Equal(0U, archive.IndexVersion);
    Assert.Equal(1, archive.Entries.Count);
    var loaded = archive.ReadResource(tgi);
    Assert.False(loaded.IsCompressed);
    Assert.SequenceEqual(payload, loaded.Data);
}

static void DbpfVersion2ReadsResourceId()
{
    var tgi = new Tgi(0xAABBCCDD, 0x01020304, 0xFEDCBA98);
    var fixtureEntry = new FixtureEntry(tgi, [9, 8, 7], ResourceId: 0x76543210);
    using var fixture = ArchiveFixture.Create([fixtureEntry], indexVersion: 2);
    using var archive = DbpfArchive.Open(fixture.Path);
    Assert.Equal(0x76543210U, archive.Entries[0].ResourceId);
    Assert.SequenceEqual(new byte[] { 9, 8, 7 }, archive.ReadResource(tgi).Data);
}

static void DbpfDirectoryLoadsCompressedResource()
{
    var tgi = new Tgi(0x5AD0E817, 0xBADB57F1, 0x009C0410);
    var decoded = Encoding.ASCII.GetBytes("ABCDABCDABCD");
    var compressed = QfsFixture.Encode(decoded);
    var dirRow = ArchiveFixture.MakeDirectoryRow(tgi, (uint)decoded.Length);
    using var fixture = ArchiveFixture.Create([
        new FixtureEntry(tgi, compressed),
        new FixtureEntry(DbpfArchive.DirectoryTgi, dirRow)
    ]);
    using var archive = DbpfArchive.Open(fixture.Path);
    var entry = archive.FindUnique(tgi);
    Assert.True(archive.IsCompressed(entry));
    Assert.Equal((uint)decoded.Length, archive.DecodedSize(entry));
    Assert.SequenceEqual(decoded, archive.ReadResource(tgi).Data);
}

static void DbpfRejectsOutOfBounds()
{
    var tgi = new Tgi(1, 2, 3);
    using (var indexFixture = ArchiveFixture.Create([new FixtureEntry(tgi, [0x55])]))
    {
        var bytes = File.ReadAllBytes(indexFixture.Path);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), (uint)(bytes.Length - 1));
        File.WriteAllBytes(indexFixture.Path, bytes);
        Assert.Throws<InvalidDataException>(() => DbpfArchive.Open(indexFixture.Path));
    }

    using (var resourceFixture = ArchiveFixture.Create([new FixtureEntry(tgi, [0x55, 0x66])]))
    {
        var bytes = File.ReadAllBytes(resourceFixture.Path);
        var indexOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(indexOffset + 12, 4), (uint)(bytes.Length - 1));
        File.WriteAllBytes(resourceFixture.Path, bytes);
        Assert.Throws<InvalidDataException>(() => DbpfArchive.Open(resourceFixture.Path));
    }
}

static void DbpfSelectionIsUnambiguous()
{
    var tgi = new Tgi(1, 2, 3);
    using var empty = ArchiveFixture.Create([]);
    using (var archive = DbpfArchive.Open(empty.Path))
    {
        Assert.Throws<KeyNotFoundException>(() => archive.FindUnique(tgi));
    }

    using var duplicate = ArchiveFixture.Create([
        new FixtureEntry(tgi, [0x11]),
        new FixtureEntry(tgi, [0x22])
    ]);
    using (var archive = DbpfArchive.Open(duplicate.Path))
    {
        Assert.Throws<InvalidDataException>(() => archive.FindUnique(tgi));
    }
}

static void DbpfRejectsMalformedDirectory()
{
    using var fixture = ArchiveFixture.Create([new FixtureEntry(DbpfArchive.DirectoryTgi, new byte[15])]);
    Assert.Throws<InvalidDataException>(() => DbpfArchive.Open(fixture.Path));
}

static void LTextDecodesAndEncodes()
{
    var payload = new byte[] { 5, 0, 0, 0x10, 0x53, 0, 0xE3, 0, 0xE7, 0, 0xC3, 0, 0xA3, 0 };
    var text = LTextDecoder.Decode(payload);
    Assert.Equal("SãçÃ£", text);
    Assert.SequenceEqual(Encoding.UTF8.GetBytes(text), LTextDecoder.EncodeUtf8(payload));
}

static void LTextChecksBoundsAndPreservesControl()
{
    Assert.Throws<InvalidDataException>(() => LTextDecoder.Decode([1, 0, 0, 0x10]));
    Assert.Throws<InvalidDataException>(() => LTextDecoder.Decode([2, 0, 0, 0, (byte)'A', (byte)'B']));
    Assert.Equal((ushort?)0x0010, LTextDecoder.ReadControlFieldIfPresent([0, 0, 0, 0x10]));
    Assert.Equal((ushort?)null, LTextDecoder.ReadControlFieldIfPresent([0, 0, 0]));
    var zeroControl = LTextDecoder.DecodeWithInfo([0, 0, 0, 0]);
    Assert.Equal((ushort)0, zeroControl.Control);
    Assert.Equal(string.Empty, zeroControl.Text);
}

static void ExemplarDecodesProperties()
{
    var parent = new Tgi(0x11111111, 0x22222222, 0x33333333);
    var resourceKey = new Tgi(0x5AD0E817, 0xBADB57F1, 0x009C0410);
    var payload = ExemplarFixture.Build("EQZB", parent, 3, writer =>
    {
        WritePropertyHeader(writer, 0x10, 0x3);
        writer.Write(11U);

        WritePropertyHeader(writer, 0x20, 0x800C);
        writer.Write(13U);
        writer.Write(Encoding.ASCII.GetBytes("Highway piece"));

        WritePropertyHeader(writer, 0x27812820, 0x8003);
        writer.Write(3U);
        writer.Write(resourceKey.Type);
        writer.Write(resourceKey.Group);
        writer.Write(resourceKey.Instance);
    });

    var record = ExemplarDecoder.Decode(payload);
    Assert.Equal("EQZB", record.signature);
    Assert.Equal(ExemplarDecoder.ParentMarker, record.parentMarker);
    Assert.Equal(parent, record.parentCohort);
    Assert.Equal("Highway piece", record.Name);
    Assert.Equal(11U, record.Type);
    Assert.Equal(3, record.properties.Count);
    Assert.Equal("UInt32", record.properties[0].formatName);
    Assert.Equal(11U, record.properties[0].value);
    Assert.Equal("String", record.properties[1].formatName);
    Assert.Equal((ushort)0x8000, record.properties[1].repetitionTag);
    Assert.Equal("Highway piece", record.properties[1].value);
    Assert.Equal(3U, record.properties[2].encodedCount);
    Assert.SequenceEqual(new object[] { resourceKey.Type, resourceKey.Group, resourceKey.Instance }, record.properties[2].values!);
}

static void CohortHeaderDecodes()
{
    var parent = new Tgi(1, 2, 3);
    var record = ExemplarDecoder.Decode(ExemplarFixture.Build("CQZB", parent, 0, _ => { }));
    Assert.Equal("CQZB", record.signature);
    Assert.Equal(parent, record.parentCohort);
    Assert.Equal(0, record.properties.Count);
}

static void ExemplarNumericFormatsDecode()
{
    var payload = ExemplarFixture.Build("EQZB", new Tgi(0, 0, 0), 12, writer =>
    {
        WritePropertyHeader(writer, 1, 1); writer.Write((byte)0xFE);
        WritePropertyHeader(writer, 2, 2); writer.Write((ushort)0x1234);
        WritePropertyHeader(writer, 3, 3); writer.Write(0x89ABCDEFU);
        WritePropertyHeader(writer, 4, 4); writer.Write(0x0123456789ABCDEFUL);
        WritePropertyHeader(writer, 5, 5); writer.Write((byte)0xFF);
        WritePropertyHeader(writer, 6, 6); writer.Write((short)-1234);
        WritePropertyHeader(writer, 7, 7); writer.Write(-123456);
        WritePropertyHeader(writer, 8, 8); writer.Write(-1234567890123L);
        WritePropertyHeader(writer, 9, 9); writer.Write(1.5F);
        WritePropertyHeader(writer, 10, 10); writer.Write(-2.5D);
        WritePropertyHeader(writer, 11, 11); writer.Write((byte)2);
        WritePropertyHeader(writer, 12, 0x8002); writer.Write(3U); writer.Write((ushort)1); writer.Write((ushort)2); writer.Write((ushort)65535);
    });

    var properties = ExemplarDecoder.Decode(payload).properties;
    Assert.Equal((byte)0xFE, properties[0].value);
    Assert.Equal((ushort)0x1234, properties[1].value);
    Assert.Equal(0x89ABCDEFU, properties[2].value);
    Assert.Equal(0x0123456789ABCDEFUL, properties[3].value);
    Assert.Equal((sbyte)-1, properties[4].value);
    Assert.Equal((short)-1234, properties[5].value);
    Assert.Equal(-123456, properties[6].value);
    Assert.Equal(-1234567890123L, properties[7].value);
    Assert.Equal(1.5F, properties[8].value);
    Assert.Equal(-2.5D, properties[9].value);
    Assert.Equal(true, properties[10].value);
    Assert.SequenceEqual(new object[] { (ushort)1, (ushort)2, ushort.MaxValue }, properties[11].values!);
}

static void ExemplarMalformedBoundsReject()
{
    Assert.Throws<InvalidDataException>(() => ExemplarDecoder.Decode(Encoding.ASCII.GetBytes("EQZ")));
    Assert.Throws<InvalidDataException>(() => ExemplarDecoder.Decode(ExemplarFixture.Build("EQZB", new Tgi(0, 0, 0), 1, _ => { }, declaredPropertyCount: 1)));
    Assert.Throws<InvalidDataException>(() => ExemplarDecoder.Decode(ExemplarFixture.Build("EQZB", new Tgi(0, 0, 0), 0, _ => { }, declaredPropertyCount: (uint)ExemplarDecoder.MaximumPropertyCount + 1)));

    var truncatedArray = ExemplarFixture.Build("EQZB", new Tgi(0, 0, 0), 1, writer =>
    {
        WritePropertyHeader(writer, 0x1234, 0x8003);
        writer.Write(2U);
        writer.Write(1U);
    });
    Assert.Throws<InvalidDataException>(() => ExemplarDecoder.Decode(truncatedArray));

    var excessiveArray = ExemplarFixture.Build("EQZB", new Tgi(0, 0, 0), 1, writer =>
    {
        WritePropertyHeader(writer, 0x1234, 0x8001);
        writer.Write((uint)ExemplarDecoder.MaximumValuesPerProperty + 1);
    });
    Assert.Throws<InvalidDataException>(() => ExemplarDecoder.Decode(excessiveArray));

    var trailing = ExemplarFixture.Build("CQZB", new Tgi(0, 0, 0), 0, _ => { }).Concat(new byte[] { 0x00 }).ToArray();
    Assert.Throws<InvalidDataException>(() => ExemplarDecoder.Decode(trailing));
}

static void ExemplarUnsupportedVariantsReject()
{
    var wrongMarker = ExemplarFixture.Build("EQZB", new Tgi(0, 0, 0), 0, _ => { }, parentMarker: 0x12345678);
    Assert.Throws<UnsupportedExemplarFormatException>(() => ExemplarDecoder.Decode(wrongMarker));

    var unknownFormat = ExemplarFixture.Build("EQZB", new Tgi(0, 0, 0), 1, writer => WritePropertyHeader(writer, 0x1234, 0xD));
    Assert.Throws<UnsupportedExemplarFormatException>(() => ExemplarDecoder.Decode(unknownFormat));

    var scalarString = ExemplarFixture.Build("EQZB", new Tgi(0, 0, 0), 1, writer => WritePropertyHeader(writer, 0x20, 0xC));
    Assert.Throws<UnsupportedExemplarFormatException>(() => ExemplarDecoder.Decode(scalarString));

    var nonAsciiString = ExemplarFixture.Build("EQZB", new Tgi(0, 0, 0), 2, writer =>
    {
        WritePropertyHeader(writer, 0x20, 0x800C);
        writer.Write(1U);
        writer.Write((byte)0xE9);
        WritePropertyHeader(writer, 0x1234, 0x3);
        writer.Write(42U);
    });
    var partialString = ExemplarDecoder.Decode(nonAsciiString);
    Assert.Equal(1, partialString.unsupportedVariants.Count);
    Assert.Equal("Property 0x00000020 contains non-ASCII string byte 0xE9 at relative offset 0; string encoding is unsupported.", partialString.unsupportedVariants[0]);
    Assert.Equal(42U, partialString.properties[1].value);
    Assert.SequenceEqual(new byte[] { 0xE9 }, Convert.FromBase64String(partialString.properties[0].rawBytesBase64!));

    var nonzeroReserved = ExemplarFixture.Build("EQZB", new Tgi(0, 0, 0), 1, writer =>
    {
        writer.Write(1U);
        writer.Write((byte)1);
        writer.Write(3U);
        writer.Write(1U);
    });
    Assert.Throws<UnsupportedExemplarFormatException>(() => ExemplarDecoder.Decode(nonzeroReserved));
}

static void WritePropertyHeader(BinaryWriter writer, uint propertyId, uint typeCode)
{
    writer.Write(propertyId);
    writer.Write((byte)0);
    writer.Write(typeCode);
}

static void GeneratedOutputMustBeContained()
{
    using var temp = new TemporaryDirectory();
    var root = temp.Path;
    Directory.CreateDirectory(System.IO.Path.Combine(root, "game"));
    Directory.CreateDirectory(System.IO.Path.Combine(root, "config"));
    Directory.CreateDirectory(System.IO.Path.Combine(root, "scripts"));
    File.WriteAllText(System.IO.Path.Combine(root, "config", "local.paths.json"), "{\"SimCity4\":\"game\"}");
    File.WriteAllText(System.IO.Path.Combine(root, "scripts", "Enter-Project.ps1"), "");
    var layout = ProjectLayout.Load(root);
    var resolved = layout.ResolveGeneratedOutput("analysis/generated/text/sample.bin");
    Assert.True(resolved.StartsWith(layout.GeneratedRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    Assert.Throws<InvalidDataException>(() => layout.ResolveGeneratedOutput("analysis/outside.bin"));
}

static class Assert
{
    public static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected condition to be true.");
    }

    public static void False(bool value)
    {
        if (value) throw new InvalidOperationException("Expected condition to be false.");
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', found '{actual}'.");
        }
    }

    public static void SequenceEqual(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Expected bytes {Convert.ToHexString(expected)}, found {Convert.ToHexString(actual)}.");
        }
    }

    public static void SequenceEqual<T>(T[] expected, T[] actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Expected [{string.Join(", ", expected)}], found [{string.Join(", ", actual)}].");
        }
    }

    public static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}

sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sc4data-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

sealed class ArchiveFixture : IDisposable
{
    private ArchiveFixture(string path) => Path = path;

    public string Path { get; }

    public static ArchiveFixture Create(IReadOnlyList<FixtureEntry> entries, uint indexVersion = 0)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sc4data-dbpf-" + Guid.NewGuid().ToString("N") + ".dat");
        var entrySize = indexVersion == 2 ? 24 : 20;
        var indexOffset = checked((uint)(96 + entries.Sum(entry => entry.Data.Length)));
        var indexSize = checked((uint)(entrySize * entries.Count));
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("DBPF"));
        writer.Write(1U);
        writer.Write(0U);
        writer.Write(new byte[12]);
        writer.Write(0U); // Created
        writer.Write(0U); // Modified
        writer.Write(7U); // Index type version
        writer.Write((uint)entries.Count);
        writer.Write(indexOffset);
        writer.Write(indexSize);
        writer.Write(0U); // Hole count
        writer.Write(0U); // Hole offset
        writer.Write(0U); // Hole size
        writer.Write(indexVersion);
        writer.Write(new byte[32]);

        var offsets = new List<uint>(entries.Count);
        var nextOffset = 96U;
        foreach (var entry in entries)
        {
            offsets.Add(nextOffset);
            writer.Write(entry.Data);
            nextOffset = checked(nextOffset + (uint)entry.Data.Length);
        }

        foreach (var (entry, offset) in entries.Zip(offsets))
        {
            writer.Write(entry.Tgi.Type);
            writer.Write(entry.Tgi.Group);
            writer.Write(entry.Tgi.Instance);
            if (indexVersion == 2)
            {
                writer.Write(entry.ResourceId ?? 0U);
            }
            writer.Write(offset);
            writer.Write((uint)entry.Data.Length);
        }

        writer.Flush();
        return new ArchiveFixture(path);
    }

    public static byte[] MakeDirectoryRow(Tgi tgi, uint decodedSize)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), tgi.Type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), tgi.Group);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), tgi.Instance);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), decodedSize);
        return bytes;
    }

    public void Dispose() => File.Delete(Path);
}

sealed record FixtureEntry(Tgi Tgi, byte[] Data, uint? ResourceId = null);

static class ExemplarFixture
{
    public static byte[] Build(
        string signature,
        Tgi parent,
        uint propertyCount,
        Action<BinaryWriter> writeProperties,
        uint? declaredPropertyCount = null,
        uint parentMarker = ExemplarDecoder.ParentMarker)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(signature));
        writer.Write(parentMarker);
        writer.Write(parent.Type);
        writer.Write(parent.Group);
        writer.Write(parent.Instance);
        writer.Write(declaredPropertyCount ?? propertyCount);
        writeProperties(writer);
        writer.Flush();
        return stream.ToArray();
    }
}

static class QfsFixture
{
    public static byte[] Encode(byte[] decoded)
    {
        using var stream = new MemoryStream();
        var position = 0;
        while (decoded.Length - position > 3)
        {
            var literalCount = Math.Min(112, ((decoded.Length - position) / 4) * 4);
            stream.WriteByte((byte)(0xE0 + (literalCount / 4) - 1));
            stream.Write(decoded, position, literalCount);
            position += literalCount;
        }

        var tailCount = decoded.Length - position;
        stream.WriteByte((byte)(0xFC + tailCount));
        stream.Write(decoded, position, tailCount);
        return EncodeWithStream(decoded, stream.ToArray());
    }

    public static byte[] EncodeWithStream(byte[] decoded, byte[] commands)
    {
        var data = new byte[9 + commands.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), (uint)data.Length);
        data[4] = 0x10;
        data[5] = 0xFB;
        data[6] = (byte)(decoded.Length >> 16);
        data[7] = (byte)(decoded.Length >> 8);
        data[8] = (byte)decoded.Length;
        commands.CopyTo(data, 9);
        return data;
    }
}
