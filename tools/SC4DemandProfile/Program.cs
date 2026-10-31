using System.Security.Cryptography;
using System.Text.Json;
using SC4Data;
using SC4Evolution.Simulation;

internal static class Program
{
    private const uint ExemplarTypeId = 0x6534284A;
    private const uint DemandExemplarType = 3;
    private const uint TypeProperty = 0x00000010;
    private const uint NameProperty = 0x00000020;
    private const uint NodeKeyProperty = 0x00000021;
    private const uint MinimumProperty = 0x47BB3F10;
    private const uint MaximumProperty = 0x47BB3F11;
    private const uint InitialSupplyProperty = 0x47BB3F20;
    private const uint InitialDemandProperty = 0x47BB3F21;
    private const uint DrivesProperty = 0x47BB3F30;
    private const uint SatisfiesProperty = 0x47BB3F31;
    private const uint ContributesProperty = 0x47BB3F32;
    private const uint CapsProperty = 0x47BB3F41;
    private const uint SumsProperty = 0x47BB3F50;
    private const uint RegionUseProperty = 0x47BB3F60;
    private const uint ShippedDemandGroup = 0xC7BB4816;
    private const int DefaultActiveBoundBits = 0x7E7FFFFF;
    private const uint MaximumExactlyRepresentablePercent = 1u << 24;

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or KeyNotFoundException or FormatException or JsonException or UnsupportedExemplarFormatException)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var options = ParseArguments(args);
        if (options.Help)
        {
            Console.WriteLine("SC4DemandProfile [--root <SC4Evolution-root>] [--out <path-under-analysis/generated/sc4-demand-profile>] [--overwrite]");
            Console.WriteLine("Rejects selected demand exemplars with nonzero parent cohorts and link percentages above 16777216 (2^24).");
            return 0;
        }

        var layout = ProjectLayout.Load(options.Root);
        var archivePath = layout.ResolveArchive("SimCity_1.dat");
        var outputPath = layout.ResolveGeneratedOutputIn(
            options.Output ?? "analysis/generated/sc4-demand-profile/demand.sc4e",
            "sc4-demand-profile");
        var reportPath = layout.ResolveGeneratedOutputIn(Path.Combine(Path.GetDirectoryName(outputPath)!, "provenance.json"), "sc4-demand-profile");
        if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(reportPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The demand profile output must differ from the provenance.json report path.");
        }
        if ((File.Exists(outputPath) || File.Exists(reportPath)) && !options.Overwrite)
        {
            throw new IOException($"Generated output or provenance already exists under {Path.GetDirectoryName(outputPath)}. Pass --overwrite to replace them.");
        }

        var archiveInfo = new FileInfo(archivePath);
        using var archiveForHash = File.OpenRead(archivePath);
        var archiveSha256 = Convert.ToHexString(SHA256.HashData(archiveForHash)).ToLowerInvariant();
        var selected = new List<SelectedExemplar>();
        var entriesByGroup = new SortedDictionary<uint, int>();
        var demandByGroup = new SortedDictionary<uint, int>();
        var parentCohorts = new SortedSet<string>(StringComparer.Ordinal);
        var propertyPresence = new Dictionary<uint, int>();
        var scanned = 0;
        var noType = 0;
        uint archiveIndexVersion;

        using (var archive = DbpfArchive.Open(archivePath))
        {
            archiveIndexVersion = archive.IndexVersion;
            foreach (var entry in archive.Entries.Where(entry => entry.Tgi.Type == ExemplarTypeId))
            {
                scanned++;
                entriesByGroup[entry.Tgi.Group] = entriesByGroup.GetValueOrDefault(entry.Tgi.Group) + 1;
                var loaded = archive.ReadResource(entry.Tgi);
                var record = ExemplarDecoder.Decode(loaded.Data);
                var typeProperty = GetOptionalUniqueProperty(record, TypeProperty);
                if (typeProperty is null)
                {
                    noType++;
                    continue;
                }

                var typeValue = ReadUInt32Scalar(typeProperty, "ExemplarType (0x00000010)");
                if (typeValue != DemandExemplarType)
                {
                    continue;
                }
                if (record.parentCohort != default)
                {
                    throw new InvalidDataException($"Demand exemplar {entry.Tgi} has unsupported nonzero parent cohort {record.parentCohort}; inherited demand properties are not resolved.");
                }

                var keyProperty = GetOptionalUniqueProperty(record, NodeKeyProperty);
                var key = keyProperty is null
                    ? entry.Tgi.Instance
                    : ReadUInt32Scalar(keyProperty, "node key (0x00000021)");
                var nameProperty = GetOptionalUniqueProperty(record, NameProperty);
                var name = ReadOptionalAsciiName(nameProperty, entry.Tgi, out var nameStatus);
                var minimum = ReadOptionalInt32(record, MinimumProperty, "minimum", -BitConverter.Int32BitsToSingle(DefaultActiveBoundBits));
                var maximum = ReadOptionalInt32(record, MaximumProperty, "maximum", BitConverter.Int32BitsToSingle(DefaultActiveBoundBits));
                if (minimum > maximum)
                {
                    throw new InvalidDataException($"Demand exemplar {entry.Tgi} resolves to key 0x{key:X8} with minimum {minimum} above maximum {maximum}.");
                }

                var initialSupply = ReadOptionalInt32(record, InitialSupplyProperty, "initial supply", 0);
                var initialDemand = ReadOptionalInt32(record, InitialDemandProperty, "initial demand", 0);
                var regionUse = ReadOptionalByte(record, RegionUseProperty, "region use", 0);
                var links = ReadLinks(record, key);

                foreach (var property in record.properties)
                {
                    propertyPresence[property.id] = propertyPresence.GetValueOrDefault(property.id) + 1;
                }
                parentCohorts.Add(record.parentCohort.ToString());
                demandByGroup[entry.Tgi.Group] = demandByGroup.GetValueOrDefault(entry.Tgi.Group) + 1;
                selected.Add(new SelectedExemplar(
                    entry.Tgi,
                    loaded.IsCompressed,
                    key,
                    name,
                    nameStatus,
                    minimum,
                    maximum,
                    initialSupply,
                    initialDemand,
                    regionUse,
                    links));
            }
        }

        if (selected.Count == 0)
        {
            throw new InvalidDataException("No ExemplarType 3 resources were found in SimCity_1.dat.");
        }

        var definitions = selected.Select(item => new DemandNodeDefinition(
            item.Key,
            item.Name,
            item.Minimum,
            item.Maximum,
            item.InitialSupply,
            item.InitialDemand,
            item.RegionUse,
            item.Links)).ToArray();
        // The profile constructor rejects duplicate keys and dangling links rather than silently changing the source graph.
        var sourceIdentity = $"SimCity_1.dat sha256:{archiveSha256}";
        var profile = new DemandProfile(sourceIdentity, definitions);
        WriteAtomically(
            layout,
            outputPath,
            options.Overwrite,
            output => DemandProfileCodec.Write(output, profile),
            temporaryPath => VerifyRoundTrip(profile, temporaryPath));

        var provenance = new ProvenanceDocument(
            DateTimeOffset.UtcNow,
            Path.GetFileName(archivePath),
            archiveInfo.Length,
            archiveInfo.LastWriteTimeUtc,
            archiveSha256,
            archiveIndexVersion,
            scanned,
            noType,
            selected.Count,
            entriesByGroup.Select(pair => new GroupCount($"0x{pair.Key:X8}", pair.Value)).ToArray(),
            demandByGroup.Select(pair => new GroupCount($"0x{pair.Key:X8}", pair.Value)).ToArray(),
            selected.Select(item => new NodeProvenance(
                $"0x{item.SourceTgi.Type:X8}:{item.SourceTgi.Group:X8}:{item.SourceTgi.Instance:X8}",
                $"0x{item.Key:X8}",
                item.Name,
                item.NameStatus,
                item.RegionUse,
                item.Links.Length)).ToArray(),
            parentCohorts.ToArray(),
            propertyPresence.OrderBy(pair => pair.Key).ToDictionary(pair => $"0x{pair.Key:X8}", pair => pair.Value),
            outputPath);
        var provenanceBytes = JsonSerializer.SerializeToUtf8Bytes(provenance, new JsonSerializerOptions { WriteIndented = true });
        WriteAtomically(
            layout,
            reportPath,
            options.Overwrite,
            output => output.Write(provenanceBytes),
            VerifyJsonFile);

        Console.WriteLine($"Scanned {scanned} Exemplar resources; selected {selected.Count} ExemplarType 3 nodes.");
        Console.WriteLine($"Wrote profile: {outputPath}");
        Console.WriteLine($"Wrote provenance: {reportPath}");
        return 0;
    }

    private static SC4Data.ExemplarProperty? GetOptionalUniqueProperty(ExemplarRecord record, uint id)
    {
        var matching = record.properties.Where(property => property.id == id).Take(2).ToArray();
        if (matching.Length > 1)
        {
            throw new InvalidDataException($"Exemplar has duplicate property 0x{id:X8}.");
        }
        return matching.Length == 0 ? null : matching[0];
    }

    private static uint ReadUInt32Scalar(SC4Data.ExemplarProperty property, string label)
    {
        if (property.isRepeated || property.formatCode != 0x3 || property.value is not uint value)
        {
            throw new InvalidDataException($"Property {label} must be a scalar UInt32; found format {property.formatName}, repeated={property.isRepeated}.");
        }
        return value;
    }

    private static float ReadOptionalInt32(ExemplarRecord record, uint id, string label, float absentValue)
    {
        var property = GetOptionalUniqueProperty(record, id);
        if (property is null) return absentValue;
        if (property.isRepeated || property.formatCode != 0x7 || property.value is not int value)
        {
            throw new InvalidDataException($"Property {label} 0x{id:X8} must be a scalar Int32; found format {property.formatName}, repeated={property.isRepeated}.");
        }
        return (float)value;
    }

    private static byte ReadOptionalByte(ExemplarRecord record, uint id, string label, byte absentValue)
    {
        var property = GetOptionalUniqueProperty(record, id);
        if (property is null) return absentValue;
        if (property.isRepeated || property.formatCode != 0x1 || property.value is not byte value)
        {
            throw new InvalidDataException($"Property {label} 0x{id:X8} must be a scalar UInt8; found format {property.formatName}, repeated={property.isRepeated}.");
        }
        if (value > 2)
        {
            throw new InvalidDataException($"Property {label} 0x{id:X8} has unsupported value {value}; spec allows 0..2.");
        }
        return value;
    }

    private static string ReadOptionalAsciiName(SC4Data.ExemplarProperty? property, Tgi source, out string status)
    {
        if (property is null)
        {
            status = "absent_display_only";
            return string.Empty;
        }
        if (property.formatCode != 0xC || !property.isRepeated)
        {
            throw new InvalidDataException($"Demand exemplar {source} property 0x00000020 must use the repeated string representation; found {property.formatName}.");
        }
        if (property.value is string name)
        {
            status = "ascii";
            return name;
        }
        if (property.unsupportedReason is not null)
        {
            status = "unsupported_non_ascii_display_name";
            return string.Empty;
        }
        throw new InvalidDataException($"Demand exemplar {source} has an unsupported name property representation.");
    }

    private static DemandLink[] ReadLinks(ExemplarRecord record, uint sourceKey)
    {
        var links = new List<DemandLink>();
        AppendPairs(record, SatisfiesProperty, DemandLinkKind.Satisfies, links, sourceKey);
        AppendPairs(record, DrivesProperty, DemandLinkKind.Drives, links, sourceKey);
        AppendPairs(record, ContributesProperty, DemandLinkKind.Contributes, links, sourceKey);
        AppendTargets(record, CapsProperty, DemandLinkKind.Caps, links, sourceKey);
        AppendTargets(record, SumsProperty, DemandLinkKind.Sums, links, sourceKey);
        return links.ToArray();
    }

    private static void AppendPairs(ExemplarRecord record, uint propertyId, DemandLinkKind kind, List<DemandLink> links, uint sourceKey)
    {
        var values = ReadUInt32Array(record, propertyId, kind.ToString());
        if (values is null) return;
        if (values.Length % 2 != 0)
        {
            throw new InvalidDataException($"Demand node 0x{sourceKey:X8} property 0x{propertyId:X8} ({kind}) has {values.Length} values; expected target/percent pairs.");
        }
        for (var index = 0; index < values.Length; index += 2)
        {
            var percent = values[index + 1];
            if (percent > MaximumExactlyRepresentablePercent)
            {
                throw new InvalidDataException($"Demand node 0x{sourceKey:X8} property 0x{propertyId:X8} ({kind}) has percent {percent}; this extractor supports at most 16777216 (2^24) to preserve the documented float conversion.");
            }
            var factor = (float)percent * 0.01f;
            links.Add(new DemandLink(values[index], kind, factor));
        }
    }

    private static void AppendTargets(ExemplarRecord record, uint propertyId, DemandLinkKind kind, List<DemandLink> links, uint sourceKey)
    {
        var values = ReadUInt32Array(record, propertyId, kind.ToString());
        if (values is null) return;
        foreach (var target in values)
        {
            links.Add(new DemandLink(target, kind, 1.0f));
        }
    }

    private static uint[]? ReadUInt32Array(ExemplarRecord record, uint propertyId, string label)
    {
        var property = GetOptionalUniqueProperty(record, propertyId);
        if (property is null) return null;
        if (!property.isRepeated || property.formatCode != 0x3 || property.values is null)
        {
            throw new InvalidDataException($"Demand link property {label} 0x{propertyId:X8} must be a repeated UInt32 array; found {property.formatName}, repeated={property.isRepeated}.");
        }
        var result = new uint[property.values.Length];
        for (var index = 0; index < result.Length; index++)
        {
            if (property.values[index] is not uint value)
            {
                throw new InvalidDataException($"Demand link property {label} 0x{propertyId:X8} value {index} was not UInt32.");
            }
            result[index] = value;
        }
        return result;
    }

    private static void VerifyRoundTrip(DemandProfile original, string path)
    {
        using var input = File.OpenRead(path);
        var decoded = DemandProfileCodec.Read(input);
        if (!string.Equals(decoded.SourceIdentity, original.SourceIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Demand profile codec round trip changed source identity.");
        }
        var expectedNodes = original.Nodes;
        var actualNodes = decoded.Nodes;
        if (actualNodes.Length != expectedNodes.Length)
        {
            throw new InvalidDataException($"Demand profile codec round trip changed node count from {expectedNodes.Length} to {actualNodes.Length}.");
        }
        for (var index = 0; index < expectedNodes.Length; index++)
        {
            var expected = expectedNodes[index];
            var actual = actualNodes[index];
            if (expected.Key != actual.Key || !string.Equals(expected.Name, actual.Name, StringComparison.Ordinal) ||
                !SameFloat(expected.Minimum, actual.Minimum) || !SameFloat(expected.Maximum, actual.Maximum) ||
                !SameFloat(expected.InitialSupply, actual.InitialSupply) || !SameFloat(expected.InitialDemand, actual.InitialDemand) ||
                expected.RegionUse != actual.RegionUse)
            {
                throw new InvalidDataException($"Demand profile codec round trip changed node 0x{expected.Key:X8}.");
            }
            var expectedLinks = expected.Links;
            var actualLinks = actual.Links;
            if (actualLinks.Length != expectedLinks.Length)
            {
                throw new InvalidDataException($"Demand profile codec round trip changed link count for node 0x{expected.Key:X8}.");
            }
            for (var linkIndex = 0; linkIndex < expectedLinks.Length; linkIndex++)
            {
                if (expectedLinks[linkIndex].TargetKey != actualLinks[linkIndex].TargetKey ||
                    expectedLinks[linkIndex].Kind != actualLinks[linkIndex].Kind ||
                    !SameFloat(expectedLinks[linkIndex].Factor, actualLinks[linkIndex].Factor))
                {
                    throw new InvalidDataException($"Demand profile codec round trip changed link {linkIndex} for node 0x{expected.Key:X8}.");
                }
            }
        }
    }

    private static bool SameFloat(float left, float right) => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    private static void WriteAtomically(ProjectLayout layout, string destination, bool overwrite, Action<Stream> write, Action<string> verify)
    {
        var safeDestination = layout.ResolveGeneratedOutputIn(destination, "sc4-demand-profile");
        var directory = Path.GetDirectoryName(safeDestination)!;
        var temporaryCandidate = Path.Combine(directory, $".{Path.GetFileName(safeDestination)}.{Guid.NewGuid():N}.tmp");
        var temporaryPath = layout.ResolveGeneratedOutputIn(temporaryCandidate, "sc4-demand-profile");
        var ownsTemporary = false;
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                write(output);
                output.Flush(flushToDisk: true);
            }

            verify(temporaryPath);
            safeDestination = layout.ResolveGeneratedOutputIn(safeDestination, "sc4-demand-profile");
            File.Move(temporaryPath, safeDestination, overwrite);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary && File.Exists(temporaryPath))
            {
                try
                {
                    var safeTemporary = layout.ResolveGeneratedOutputIn(temporaryPath, "sc4-demand-profile");
                    File.Delete(safeTemporary);
                }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Console.Error.WriteLine($"warning: could not remove owned temporary output '{temporaryPath}': {cleanupError.Message}");
                }
            }
        }
    }

    private static void VerifyJsonFile(string path)
    {
        using var input = File.OpenRead(path);
        using var document = JsonDocument.Parse(input);
    }

    private static Options ParseArguments(string[] args)
    {
        string? root = null;
        string? output = null;
        var overwrite = false;
        var help = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--root":
                    root = ReadValue(args, ref index, "--root");
                    break;
                case "--out":
                    output = ReadValue(args, ref index, "--out");
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }
        return new Options(root, output, overwrite, help);
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{option} requires a value.");
        }
        return args[index];
    }

    private sealed record Options(string? Root, string? Output, bool Overwrite, bool Help);
    private sealed record SelectedExemplar(Tgi SourceTgi, bool IsCompressed, uint Key, string Name, string NameStatus, float Minimum, float Maximum, float InitialSupply, float InitialDemand, byte RegionUse, DemandLink[] Links);
    private sealed record GroupCount(string Group, int Count);
    private sealed record NodeProvenance(string SourceTgi, string NodeKey, string Name, string NameStatus, byte RegionUse, int LinkCount);
    private sealed record ProvenanceDocument(DateTimeOffset GeneratedUtc, string ArchiveFile, long ArchiveBytes, DateTime ArchiveLastWriteUtc, string ArchiveSha256, uint DbpfIndexVersion, int ExemplarResourcesScanned, int MissingTypeProperty, int DemandExemplarsSelected, GroupCount[] GroupsScanned, GroupCount[] DemandExemplarGroups, NodeProvenance[] Nodes, string[] ParentCohortsObserved, Dictionary<string, int> PropertyPresenceCounts, string ProfileOutput);
}
