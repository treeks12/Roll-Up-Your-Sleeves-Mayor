using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SC4Data;

internal static class ModelInspector
{
    private const uint S3DType = 0x5AD0E817;
    private const uint FshType = 0x7AB50E44;
    private const uint ExpectedFshGroup = 0x1ABE787D;

    internal sealed record SourceMapping(string Advisor, string Gender, string State, uint Instance);
    internal sealed record ModelResource(string Archive, Tgi Tgi, uint StoredBytes, bool Compressed, byte[] Data);
    private sealed record Chunk(string Tag, int Offset, int Length, int End);
    private sealed record Material(uint Id, string Name);
    private sealed record S3dInfo(int VertexGroups, int Vertices, int IndexGroups, int Indices, List<Material> Materials, List<Chunk> Chunks, string? Error);
    private sealed record FshImage(string Tag, byte EntryType, uint Width, uint Height, uint CenterX, uint CenterY, uint X, uint Y, int Offset, int Length);
    private sealed record FshInfo(string Signature, uint DeclaredSize, uint Count, string DirectoryId, List<FshImage> Images, string? Error);
    private sealed record LocatedFsh(string Archive, DbpfEntry Entry, byte[] Data);

    public static void Analyze(ProjectLayout layout, string outputDirectory, IReadOnlyList<ModelResource> models, IReadOnlyList<SourceMapping> mappings)
    {
        var modelPath = Path.Combine(outputDirectory, "model-analysis.csv");
        var blocksPath = Path.Combine(outputDirectory, "s3d-blocks.csv");
        var linksPath = Path.Combine(outputDirectory, "model-texture-links.csv");
        var fshPath = Path.Combine(outputDirectory, "fsh-images.csv");
        var mappingsByInstance = mappings.GroupBy(mapping => mapping.Instance).ToDictionary(group => group.Key, group => group.ToArray());
        var materialsByModel = new Dictionary<Tgi, List<Material>>();
        var materialsByInstance = new Dictionary<uint, List<(Tgi Model, Material Material)>>();
        var infos = models.ToDictionary(model => model.Tgi, model => ParseS3d(model.Data));

        using (var writer = NewWriter(modelPath))
        using (var blockWriter = NewWriter(blocksPath))
        {
            writer.WriteLine("modelTgi,archive,advisorTypes,genders,states,decodedBytes,signature,vertexGroups,vertices,indexGroups,indices,materialCount,materialIds,animBlocks,animBytes,propBlocks,regpBlocks,parseError");
            blockWriter.WriteLine("modelTgi,blockTag,offset,declaredBytes,containsPayload");
            foreach (var model in models.OrderBy(model => model.Tgi.Instance))
            {
                var info = infos[model.Tgi];
                materialsByModel[model.Tgi] = info.Materials;
                var refs = mappingsByInstance.TryGetValue(model.Tgi.Instance, out var uses) ? uses : [];
                var advisorTypes = string.Join("|", refs.Select(mapping => mapping.Advisor).Distinct(StringComparer.Ordinal));
                var genders = string.Join("|", refs.Select(mapping => mapping.Gender).Distinct(StringComparer.Ordinal));
                var states = string.Join("|", refs.Select(mapping => mapping.State).Distinct(StringComparer.Ordinal));
                foreach (var material in info.Materials)
                {
                    if (!materialsByInstance.TryGetValue(material.Id, out var materialRefs))
                    {
                        materialRefs = [];
                        materialsByInstance.Add(material.Id, materialRefs);
                    }
                    materialRefs.Add((model.Tgi, material));
                }

                var signature = model.Data.Length >= 4 ? Encoding.ASCII.GetString(model.Data, 0, 4) : "short";
                var animBlocks = info.Chunks.Count(chunk => chunk.Tag == "ANIM");
                var animBytes = info.Chunks.Where(chunk => chunk.Tag == "ANIM").Sum(chunk => chunk.Length);
                var propBlocks = info.Chunks.Count(chunk => chunk.Tag == "PROP");
                var regpBlocks = info.Chunks.Count(chunk => chunk.Tag == "REGP");
                WriteCsv(writer, model.Tgi.ToString(), model.Archive, advisorTypes, genders, states, model.Data.Length.ToString(CultureInfo.InvariantCulture), signature,
                    info.VertexGroups.ToString(CultureInfo.InvariantCulture), info.Vertices.ToString(CultureInfo.InvariantCulture), info.IndexGroups.ToString(CultureInfo.InvariantCulture),
                    info.Indices.ToString(CultureInfo.InvariantCulture), info.Materials.Count.ToString(CultureInfo.InvariantCulture), string.Join("|", info.Materials.Select(material => $"{material.Id:X8}:{material.Name}")),
                    animBlocks.ToString(CultureInfo.InvariantCulture), animBytes.ToString(CultureInfo.InvariantCulture), propBlocks.ToString(CultureInfo.InvariantCulture), regpBlocks.ToString(CultureInfo.InvariantCulture), info.Error ?? string.Empty);
                foreach (var chunk in info.Chunks)
                {
                    WriteCsv(blockWriter, model.Tgi.ToString(), chunk.Tag, chunk.Offset.ToString(CultureInfo.InvariantCulture), chunk.Length.ToString(CultureInfo.InvariantCulture), (chunk.Length > 8).ToString());
                }
            }
        }

        var materialInstances = materialsByInstance.Keys.ToHashSet();
        var archives = Directory.GetFiles(layout.GameRoot, "*.dat", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).Equals("SimCityLocale.DAT", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var fshResources = new Dictionary<Tgi, LocatedFsh>();
        foreach (var archivePath in archives)
        {
            using var archive = DbpfArchive.Open(layout.ResolveArchive(archivePath));
            foreach (var entry in archive.Entries.Where(entry => entry.Tgi.Type == FshType && materialInstances.Contains(entry.Tgi.Instance)))
            {
                var loaded = archive.ReadResource(entry.Tgi);
                fshResources.TryAdd(entry.Tgi, new LocatedFsh(Path.GetFileName(archivePath), entry, loaded.Data));
            }
        }

        var imageRows = new List<(Tgi Tgi, LocatedFsh Resource, FshInfo Info, string Path)>();
        foreach (var resource in fshResources.Values.OrderBy(resource => resource.Entry.Tgi.Group).ThenBy(resource => resource.Entry.Tgi.Instance))
        {
            var info = ParseFsh(resource.Data);
            var fileName = $"{resource.Entry.Tgi.Group:X8}_{resource.Entry.Tgi.Instance:X8}.fsh.bin";
            var extractedPath = Path.Combine(outputDirectory, "textures", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(extractedPath)!);
            if (!File.Exists(extractedPath)) File.WriteAllBytes(extractedPath, resource.Data);
            imageRows.Add((resource.Entry.Tgi, resource, info, Path.GetRelativePath(outputDirectory, extractedPath)));
        }

        using (var writer = NewWriter(linksPath))
        {
            writer.WriteLine("modelTgi,modelArchive,materialId,materialName,textureTgi,textureArchive,expectedFshGroup,matchedGroup,entryCount,entryDetails,extractedFile,parseError");
            foreach (var model in models.OrderBy(model => model.Tgi.Instance))
            {
                foreach (var material in materialsByModel[model.Tgi])
                {
                    var matches = imageRows.Where(row => row.Tgi.Instance == material.Id).ToArray();
                    if (matches.Length == 0)
                    {
                        WriteCsv(writer, model.Tgi.ToString(), model.Archive, material.Id.ToString("X8"), material.Name, "", "", ExpectedFshGroup.ToString("X8"), "", "0", "", "", "no FSH type 0x7AB50E44 with this material instance in scanned root archives");
                        continue;
                    }
                    foreach (var match in matches)
                    {
                        var info = match.Info;
                        var entries = string.Join("|", info.Images.Select(image => $"{image.Tag}:type{image.EntryType}:{image.Width}x{image.Height}"));
                        WriteCsv(writer, model.Tgi.ToString(), model.Archive, material.Id.ToString("X8"), material.Name, match.Tgi.ToString(), match.Resource.Archive,
                            ExpectedFshGroup.ToString("X8"), match.Tgi.Group.ToString("X8"), info.Count.ToString(CultureInfo.InvariantCulture), entries, match.Path, info.Error ?? string.Empty);
                    }
                }
            }
        }

        using (var writer = NewWriter(fshPath))
        {
            writer.WriteLine("textureTgi,archive,storedBytes,decodedBytes,qfs,signature,declaredFileBytes,entryCount,directoryId,entryTag,entryType,width,height,centerX,centerY,x,y,entryOffset,entryBytes,parseError");
            foreach (var row in imageRows)
            {
                if (row.Info.Images.Count == 0)
                {
                    WriteCsv(writer, row.Tgi.ToString(), row.Resource.Archive, row.Resource.Entry.Size.ToString(CultureInfo.InvariantCulture), row.Resource.Data.Length.ToString(CultureInfo.InvariantCulture), "", row.Info.Signature,
                        row.Info.DeclaredSize.ToString(CultureInfo.InvariantCulture), row.Info.Count.ToString(CultureInfo.InvariantCulture), row.Info.DirectoryId, "", "", "", "", "", "", "", "", "", row.Info.Error ?? string.Empty);
                    continue;
                }
                foreach (var image in row.Info.Images)
                {
                    WriteCsv(writer, row.Tgi.ToString(), row.Resource.Archive, row.Resource.Entry.Size.ToString(CultureInfo.InvariantCulture), row.Resource.Data.Length.ToString(CultureInfo.InvariantCulture), "",
                        row.Info.Signature, row.Info.DeclaredSize.ToString(CultureInfo.InvariantCulture), row.Info.Count.ToString(CultureInfo.InvariantCulture), row.Info.DirectoryId,
                        image.Tag, image.EntryType.ToString(CultureInfo.InvariantCulture), image.Width.ToString(CultureInfo.InvariantCulture), image.Height.ToString(CultureInfo.InvariantCulture),
                        image.CenterX.ToString(CultureInfo.InvariantCulture), image.CenterY.ToString(CultureInfo.InvariantCulture), image.X.ToString(CultureInfo.InvariantCulture), image.Y.ToString(CultureInfo.InvariantCulture),
                        image.Offset.ToString(CultureInfo.InvariantCulture), image.Length.ToString(CultureInfo.InvariantCulture), row.Info.Error ?? string.Empty);
                }
            }
        }

        Console.WriteLine($"Parsed {models.Count} S3D resources; material IDs: {materialsByInstance.Count}; resolved FSH TGIs: {fshResources.Count}; FSH image entries: {imageRows.Sum(row => row.Info.Images.Count)}");
        Console.WriteLine($"Wrote {modelPath}, {blocksPath}, {linksPath}, {fshPath}; extracted FSH resource samples under {Path.Combine(outputDirectory, "textures")}");
    }

    private static S3dInfo ParseS3d(byte[] data)
    {
        var chunks = new List<Chunk>();
        var materials = new List<Material>();
        try
        {
            if (ReadAscii(data, 0, 4) != "3DMD") throw new InvalidDataException("Signature is not 3DMD.");
            var offset = 8;
            var head = ReadChunk(data, offset, "HEAD");
            chunks.Add(head);
            offset = head.End;

            var vert = ReadChunk(data, offset, "VERT");
            chunks.Add(vert);
            var vertexGroups = CheckedCount(ReadUInt32(data, vert.Offset + 8), "VERT group count");
            var totalVertices = 0;
            var cursor = vert.Offset + 12;
            for (var group = 0; group < vertexGroups; group++)
            {
                Need(data, cursor, 8, "VERT group header");
                cursor += 2;
                var count = ReadUInt16(data, cursor); cursor += 2;
                cursor += 4;
                totalVertices = checked(totalVertices + count);
                cursor = checked(cursor + count * 20);
                Need(data, cursor, 0, "VERT vertex data");
            }
            if (cursor != vert.End) throw new InvalidDataException($"VERT group records end at {cursor}, declared chunk ends at {vert.End}.");
            offset = vert.End;

            var indx = ReadChunk(data, offset, "INDX");
            chunks.Add(indx);
            var indexGroups = CheckedCount(ReadUInt32(data, indx.Offset + 8), "INDX group count");
            cursor = indx.Offset + 12;
            var totalIndices = 0;
            for (var group = 0; group < indexGroups; group++)
            {
                Need(data, cursor, 6, "INDX group header");
                cursor += 4;
                var count = ReadUInt16(data, cursor); cursor += 2;
                totalIndices = checked(totalIndices + count);
                cursor = checked(cursor + count * 2);
                Need(data, cursor, 0, "INDX entries");
            }
            if (cursor != indx.End) throw new InvalidDataException($"INDX group records end at {cursor}, declared chunk ends at {indx.End}.");
            offset = indx.End;

            var prim = ReadChunk(data, offset, "PRIM");
            chunks.Add(prim);
            var primitiveGroups = CheckedCount(ReadUInt32(data, prim.Offset + 8), "PRIM group count");
            cursor = checked(prim.Offset + 12 + primitiveGroups * 14);
            Need(data, cursor, 0, "PRIM group records");
            if (cursor != prim.End) throw new InvalidDataException($"PRIM group records end at {cursor}, declared chunk ends at {prim.End}.");
            offset = prim.End;

            var mats = ReadChunk(data, offset, "MATS");
            chunks.Add(mats);
            var materialCount = CheckedCount(ReadUInt32(data, mats.Offset + 8), "MATS group count");
            cursor = mats.Offset + 12;
            for (var group = 0; group < materialCount; group++)
            {
                Need(data, cursor, 29, "MATS material header");
                cursor += 8; // flags, reserved bytes, alpha/depth/blend fields
                cursor += 4; // alpha threshold
                cursor += 4; // material mask
                var materialId = ReadUInt32(data, cursor); cursor += 4;
                cursor += 4; // wrap/filter settings
                cursor += 4; // reserved
                var nameLength = data[cursor++];
                Need(data, cursor, nameLength, "MATS group name");
                var name = Encoding.ASCII.GetString(data, cursor, nameLength).TrimEnd('\0');
                cursor += nameLength;
                // MATS string length includes its NUL terminator in these installed resources.
                materials.Add(new Material(materialId, name));
            }
            if (cursor != mats.End) throw new InvalidDataException($"MATS records end at {cursor}, declared chunk ends at {mats.End}.");
            offset = mats.End;

            while (offset < data.Length)
            {
                if (data.Length - offset < 8) break;
                var tag = ReadAscii(data, offset, 4);
                if (tag != "ANIM" && tag != "PROP" && tag != "REGP") break;
                var length = ReadUInt32(data, offset + 4);
                if (length < 8 || length > data.Length - offset) break;
                var chunk = new Chunk(tag, offset, checked((int)length), checked(offset + (int)length));
                chunks.Add(chunk);
                offset = chunk.End;
            }

            return new S3dInfo(vertexGroups, totalVertices, indexGroups, totalIndices, materials, chunks, null);
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
        {
            return new S3dInfo(0, 0, 0, 0, materials, chunks, exception.Message);
        }
    }

    private static FshInfo ParseFsh(byte[] data)
    {
        var images = new List<FshImage>();
        if (data.Length < 16) return new FshInfo(data.Length < 4 ? "short" : ReadAscii(data, 0, 4), 0, 0, string.Empty, images, "FSH header is shorter than 16 bytes.");
        var signature = ReadAscii(data, 0, 4);
        var declaredSize = ReadUInt32(data, 4);
        var count = ReadUInt32(data, 8);
        var directoryId = ReadAscii(data, 12, 4);
        if (signature != "SHPI") return new FshInfo(signature, declaredSize, count, directoryId, images, "Signature is not SHPI.");
        if (count > 4096) return new FshInfo(signature, declaredSize, count, directoryId, images, "Entry count exceeds bounded reader limit.");
        var directoryEnd = checked(16 + (int)count * 8);
        if (directoryEnd > data.Length || declaredSize > data.Length) return new FshInfo(signature, declaredSize, count, directoryId, images, "Declared FSH directory/file bounds exceed decoded payload.");
        var entries = new List<(string Tag, int Offset)>();
        var cursor = 16;
        for (var index = 0; index < count; index++)
        {
            var tag = ReadAscii(data, cursor, 4); cursor += 4;
            var entryOffset = ReadUInt32(data, cursor); cursor += 4;
            if (entryOffset > int.MaxValue || entryOffset > declaredSize || entryOffset + 16 > declaredSize)
                return new FshInfo(signature, declaredSize, count, directoryId, images, $"Entry {tag} offset is outside declared FSH size.");
            entries.Add((tag, (int)entryOffset));
        }
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var end = index + 1 < entries.Count ? entries[index + 1].Offset : checked((int)declaredSize);
            if (end < entry.Offset + 16 || end > data.Length) return new FshInfo(signature, declaredSize, count, directoryId, images, $"Entry {entry.Tag} bounds are invalid.");
            var start = entry.Offset;
            var entryType = data[start];
            var width = ReadUInt16(data, start + 4);
            var height = ReadUInt16(data, start + 6);
            var centerX = ReadUInt16(data, start + 8);
            var centerY = ReadUInt16(data, start + 10);
            var xPacked = ReadUInt16(data, start + 12);
            var yPacked = ReadUInt16(data, start + 14);
            images.Add(new FshImage(entry.Tag, entryType, width, height, centerX, centerY, (uint)(xPacked >> 2), (uint)(yPacked >> 2), start, end - start));
        }
        return new FshInfo(signature, declaredSize, count, directoryId, images, null);
    }

    private static Chunk ReadChunk(byte[] data, int offset, string expected)
    {
        Need(data, offset, 8, $"{expected} block header");
        var tag = ReadAscii(data, offset, 4);
        if (!string.Equals(tag, expected, StringComparison.Ordinal)) throw new InvalidDataException($"Expected {expected} at {offset}, found {tag}.");
        var length = ReadUInt32(data, offset + 4);
        if (length < 8 || length > data.Length - offset) throw new InvalidDataException($"{tag} chunk length {length} is invalid at {offset}.");
        return new Chunk(tag, offset, checked((int)length), checked(offset + (int)length));
    }

    private static int CheckedCount(uint count, string description)
    {
        if (count > 100_000) throw new InvalidDataException($"{description} {count} exceeds bound.");
        return checked((int)count);
    }

    private static void Need(byte[] data, int offset, int count, string description)
    {
        if (offset < 0 || count < 0 || offset > data.Length || count > data.Length - offset) throw new InvalidDataException($"{description} exceeds resource bounds.");
    }

    private static string ReadAscii(byte[] data, int offset, int count)
    {
        Need(data, offset, count, "ASCII field");
        return Encoding.ASCII.GetString(data, offset, count);
    }

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        Need(data, offset, 2, "UInt16");
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        Need(data, offset, 4, "UInt32");
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static StreamWriter NewWriter(string path) => new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None), new UTF8Encoding(false));
    private static void WriteCsv(StreamWriter writer, params string[] fields) => writer.WriteLine(string.Join(',', fields.Select(field => '"' + field.Replace("\"", "\"\"", StringComparison.Ordinal) + '"')));
}
