using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using SC4Data;

internal static class Program
{
    private const uint PngType = 0x856DDBAC;
    private const uint UiXmlGroup = 0x96A006B0;
    private const uint Ui800Group = 0x08000600;
    private static readonly uint[] UiImageGroups = [0x46A006B0, 0x1ABE787D, 0x22DEC92D];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly (string Name, string Tgi)[] AdvisorButtonImages =
    [
        ("environment", "856DDBAC:46A006B0:14015572"),
        ("transportation", "856DDBAC:46A006B0:14015575"),
        ("utilities", "856DDBAC:46A006B0:14015576"),
        ("finance", "856DDBAC:46A006B0:14015570"),
        ("health-education", "856DDBAC:46A006B0:14015574"),
        ("public-safety", "856DDBAC:46A006B0:14015573"),
        ("city-planner", "856DDBAC:46A006B0:14015571"),
    ];

    private static int Main(string[] args)
    {
        try
        {
            var extractAdvisorButtons = args.Length > 0 && string.Equals(args[0], "extract-advisor-buttons", StringComparison.OrdinalIgnoreCase);
            var mapUiImageLinks = args.Length > 0 && string.Equals(args[0], "map-ui-image-links", StringComparison.OrdinalIgnoreCase);
            var specialCommand = extractAdvisorButtons || mapUiImageLinks;
            if (args.Length > (specialCommand ? 2 : 1) || (args.Length > 0 && !specialCommand && args[0].StartsWith("-", StringComparison.Ordinal)))
            {
                throw new ArgumentException("Usage: SC4ImageCatalog [project-root] | SC4ImageCatalog extract-advisor-buttons [project-root] | SC4ImageCatalog map-ui-image-links [project-root]");
            }

            var projectRoot = specialCommand ? (args.Length == 2 ? args[1] : null) : (args.Length == 1 ? args[0] : null);
            var layout = ProjectLayout.Load(projectRoot);
            var archivePath = layout.ResolveArchive("SimCity_1.dat");
            if (specialCommand)
            {
                using var specialArchive = DbpfArchive.Open(archivePath);
                if (extractAdvisorButtons)
                {
                    ExtractAdvisorButtons(layout, specialArchive);
                }
                else
                {
                    MapUiImageLinks(layout, specialArchive);
                }
                return 0;
            }

            var outputDirectory = Path.Combine(layout.ProjectRoot, "analysis", "generated", "sc4-ui-resources");
            var pngInventoryPath = layout.ResolveGeneratedOutput(Path.Combine(outputDirectory, "png-inventory.csv"));
            var groupInventoryPath = layout.ResolveGeneratedOutput(Path.Combine(outputDirectory, "type-group-inventory.csv"));
            var uiResourcePath = layout.ResolveGeneratedOutput(Path.Combine(outputDirectory, "ui-related-resources.csv"));
            var candidateTextPath = layout.ResolveGeneratedOutput(Path.Combine(outputDirectory, "ui-text-candidates.txt"));
            var outputPaths = new[] { pngInventoryPath, groupInventoryPath, uiResourcePath, candidateTextPath };
            var existing = outputPaths.Where(File.Exists).ToArray();
            if (existing.Length > 0)
            {
                throw new IOException("Refusing to overwrite existing inventory output: " + string.Join(", ", existing));
            }

            using var archive = DbpfArchive.Open(archivePath);
            WritePngInventory(archive, pngInventoryPath);
            WriteGroupInventory(archive, groupInventoryPath);
            WriteUiRelatedInventory(archive, uiResourcePath, candidateTextPath);
            Console.WriteLine($"Read {archive.Entries.Count} index entries from {archive.Path}");
            Console.WriteLine($"Wrote PNG, type/group, and UI-related inventories under {outputDirectory}");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or KeyNotFoundException or FormatException)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static void ExtractAdvisorButtons(ProjectLayout layout, DbpfArchive archive)
        => ExtractCandidates(layout, archive, AdvisorButtonImages);

    private static void ExtractCandidates(ProjectLayout layout, DbpfArchive archive, IEnumerable<(string Name, string Tgi)> images)
    {
        foreach (var image in images)
        {
            var tgi = Tgi.Parse(image.Tgi);
            var loaded = archive.ReadResource(tgi);
            var width = GetPngDimension(loaded.Data, width: true);
            var height = GetPngDimension(loaded.Data, width: false);
            if (!StartsWith(loaded.Data, PngSignature) || width == 0 || height == 0)
            {
                throw new InvalidDataException($"Advisor image resource {tgi} is not a valid PNG by signature/IHDR dimensions.");
            }

            var outputPath = layout.ResolveGeneratedOutput(Path.Combine("analysis", "generated", "sc4-ui-resources", $"advisor-{image.Name}-{width}x{height}.png"));
            using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(loaded.Data);
            Console.WriteLine($"Extracted {tgi} ({width}x{height}, {loaded.Data.Length} decoded PNG bytes) to {outputPath}");
        }
    }

    private static void MapUiImageLinks(ProjectLayout layout, DbpfArchive archive)
    {
        var outputPath = layout.ResolveGeneratedOutput(Path.Combine("analysis", "generated", "sc4-ui-resources", "ui-image-links.csv"));
        using var writer = NewWriter(outputPath);
        writer.WriteLine("sourceUiTgi,elementId,iid,area,caption,tiptext,imageField,imageTgi,imageStoredBytes,imageDecodedBytes,imageQfs,pngMagic,width,height");
        var imagePattern = new Regex(@"\bimage=\{(?<group>[0-9a-fA-F]{8}),(?<instance>[0-9a-fA-F]{8})\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var idPattern = new Regex(@"\bid=(?<value>0x[0-9a-fA-F]+|[0-9a-fA-F]{1,8})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var iidPattern = new Regex(@"\biid=(?<value>[^\s>]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var areaPattern = new Regex(@"\barea=(?<value>\([^)]*\))", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var captionPattern = new Regex(@"\bcaption=""(?<value>[^""]*)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var tipPattern = new Regex(@"\btiptext=""(?<value>[^""]*)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var uiEntries = archive.Entries.Where(entry => entry.Tgi.Type == 0 && entry.Tgi.Group is UiXmlGroup or Ui800Group)
            .OrderBy(entry => entry.Tgi.Group).ThenBy(entry => entry.Tgi.Instance);
        var mappedCount = 0;
        foreach (var sourceEntry in uiEntries)
        {
            LoadedResource source;
            try
            {
                source = archive.ReadResource(sourceEntry.Tgi);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                continue;
            }

            if (!TryDecodeText(source.Data, out var body))
            {
                continue;
            }

            foreach (var line in body.Split('\n'))
            {
                foreach (Match imageMatch in imagePattern.Matches(line))
                {
                    var group = Convert.ToUInt32(imageMatch.Groups["group"].Value, 16);
                    var instance = Convert.ToUInt32(imageMatch.Groups["instance"].Value, 16);
                    var imageTgi = new Tgi(PngType, group, instance);
                    var id = MatchValue(idPattern, line);
                    var iid = MatchValue(iidPattern, line);
                    var area = MatchValue(areaPattern, line);
                    var caption = MatchValue(captionPattern, line);
                    var tiptext = MatchValue(tipPattern, line);
                    try
                    {
                        var image = archive.ReadResource(imageTgi);
                        var isPng = StartsWith(image.Data, PngSignature);
                        var width = GetPngDimension(image.Data, width: true);
                        var height = GetPngDimension(image.Data, width: false);
                        writer.WriteLine(Csv(sourceEntry.Tgi.ToString(), id, iid, area, caption, tiptext, "image", imageTgi.ToString(), image.Entry.Size.ToString(), image.Data.Length.ToString(), image.IsCompressed.ToString(), isPng.ToString(), width.ToString(), height.ToString()));
                    }
                    catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or IOException)
                    {
                        writer.WriteLine(Csv(sourceEntry.Tgi.ToString(), id, iid, area, caption, tiptext, "image", imageTgi.ToString(), string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty));
                        Console.Error.WriteLine($"UI image reference {imageTgi} from {sourceEntry.Tgi} could not be read: {exception.Message}");
                    }

                    mappedCount++;
                }
            }
        }

        Console.WriteLine($"Wrote {mappedCount} UI image-field links from UI resources to {outputPath}");
    }

    private static void WritePngInventory(DbpfArchive archive, string path)
    {
        using var writer = NewWriter(path);
        writer.WriteLine("TGI,storedBytes,decodedBytes,qfs,pngMagic,ihdr,width,height,parseStatus");
        foreach (var entry in archive.Entries.Where(entry => entry.Tgi.Type == PngType).OrderBy(entry => entry.Tgi.Group).ThenBy(entry => entry.Tgi.Instance))
        {
            try
            {
                var loaded = archive.ReadResource(entry.Tgi);
                var hasMagic = StartsWith(loaded.Data, PngSignature);
                var hasIhdr = hasMagic && loaded.Data.Length >= 24 &&
                    loaded.Data[12] == (byte)'I' && loaded.Data[13] == (byte)'H' && loaded.Data[14] == (byte)'D' && loaded.Data[15] == (byte)'R';
                var width = hasIhdr ? BinaryPrimitives.ReadUInt32BigEndian(loaded.Data.AsSpan(16, 4)) : 0;
                var height = hasIhdr ? BinaryPrimitives.ReadUInt32BigEndian(loaded.Data.AsSpan(20, 4)) : 0;
                var status = !hasMagic ? "bad PNG signature" : !hasIhdr ? "missing/truncated IHDR" : width == 0 || height == 0 ? "zero IHDR dimension" : "IHDR dimensions";
                writer.WriteLine(Csv(entry.Tgi.ToString(), entry.Size.ToString(), loaded.Data.Length.ToString(), loaded.IsCompressed.ToString(), hasMagic.ToString(), hasIhdr.ToString(), width.ToString(), height.ToString(), status));
            }
            catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or IOException)
            {
                writer.WriteLine(Csv(entry.Tgi.ToString(), entry.Size.ToString(), string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "read error: " + exception.Message));
            }
        }
    }

    private static void WriteGroupInventory(DbpfArchive archive, string path)
    {
        using var writer = NewWriter(path);
        writer.WriteLine("type,group,count,totalStoredBytes,compressedCount");
        foreach (var group in archive.Entries.GroupBy(entry => (entry.Tgi.Type, entry.Tgi.Group)).OrderBy(group => group.Key.Type).ThenBy(group => group.Key.Group))
        {
            var rows = group.ToArray();
            var compressedCount = rows.Count(archive.IsCompressed);
            var totalStoredBytes = rows.Aggregate(0UL, (total, entry) => total + entry.Size);
            writer.WriteLine(Csv(group.Key.Type.ToString("X8"), group.Key.Group.ToString("X8"), rows.Length.ToString(), totalStoredBytes.ToString(), compressedCount.ToString()));
        }
    }

    private static void WriteUiRelatedInventory(DbpfArchive archive, string inventoryPath, string candidateTextPath)
    {
        using var inventory = NewWriter(inventoryPath);
        inventory.WriteLine("TGI,storedBytes,qfs,selectedBy");
        using var text = NewWriter(candidateTextPath);
        var candidates = archive.Entries.Where(entry =>
            entry.Tgi.Type is 0x00000000 or 0x96A006B0 or 0x2026960B ||
            entry.Tgi.Group == UiXmlGroup || entry.Tgi.Group == Ui800Group || UiImageGroups.Contains(entry.Tgi.Group));

        foreach (var entry in candidates.OrderBy(entry => entry.Tgi.Type).ThenBy(entry => entry.Tgi.Group).ThenBy(entry => entry.Tgi.Instance))
        {
            var reasons = new List<string>();
            if (entry.Tgi.Type is 0x00000000 or 0x96A006B0 or 0x2026960B) reasons.Add("text/type");
            if (entry.Tgi.Group == UiXmlGroup) reasons.Add("UI_XML group");
            if (entry.Tgi.Group == Ui800Group) reasons.Add("UI_800x600 group");
            if (UiImageGroups.Contains(entry.Tgi.Group)) reasons.Add("Core.gd named UI image group");
            inventory.WriteLine(Csv(entry.Tgi.ToString(), entry.Size.ToString(), archive.IsCompressed(entry).ToString(), string.Join(";", reasons)));

            if (entry.Tgi.Type != 0x00000000 && entry.Tgi.Group != UiXmlGroup && entry.Tgi.Group != Ui800Group)
            {
                continue;
            }

            try
            {
                var loaded = archive.ReadResource(entry.Tgi);
                if (!TryDecodeText(loaded.Data, out var body))
                {
                    continue;
                }

                if (!body.Contains("LEGACY", StringComparison.OrdinalIgnoreCase) &&
                    !body.Contains("IGZWin", StringComparison.OrdinalIgnoreCase) &&
                    !body.Contains("image=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                text.WriteLine($"--- {entry.Tgi} stored={entry.Size} decoded={loaded.Data.Length} qfs={loaded.IsCompressed}");
                text.WriteLine(body);
                text.WriteLine();
            }
            catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or IOException)
            {
                text.WriteLine($"--- {entry.Tgi} read error: {exception.Message}");
            }
        }
    }

    private static StreamWriter NewWriter(string path) => new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false));

    private static bool StartsWith(byte[] data, byte[] prefix) => data.Length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static uint GetPngDimension(byte[] data, bool width)
    {
        if (!StartsWith(data, PngSignature) || data.Length < 24 ||
            data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R')
        {
            return 0;
        }

        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(width ? 16 : 20, 4));
    }

    private static string MatchValue(Regex pattern, string line)
    {
        var match = pattern.Match(line);
        return match.Success ? match.Groups["value"].Value : string.Empty;
    }

    private static bool TryDecodeText(byte[] bytes, out string result)
    {
        result = string.Empty;
        if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024)
        {
            return false;
        }

        try
        {
            result = new UTF8Encoding(false, true).GetString(bytes);
            return result.Any(character => !char.IsControl(character) || character is '\r' or '\n' or '\t');
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Csv(params string[] fields) => string.Join(',', fields.Select(field => '"' + field.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'));
}
