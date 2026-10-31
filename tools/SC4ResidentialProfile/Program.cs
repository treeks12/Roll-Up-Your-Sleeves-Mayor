using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SC4Data;
using SC4Evolution.Simulation;

internal static class Program
{
    private const uint ExemplarType = 0x6534284A;
    private const uint ExemplarGroup = 0x67CD5FA1;
    private static readonly uint[] Instances = [0x00001010, 0x00001020, 0x00001030];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or KeyNotFoundException or JsonException or UnsupportedExemplarFormatException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var (root, output, overwrite, waterOnly, help) = Parse(args);
        if (help)
        {
            Console.WriteLine("SC4ResidentialProfile [--root <SC4Evolution-root>] [--out <path-under-analysis/generated/sc4-residential-profile>] [--overwrite] [--water-only]");
            return 0;
        }
        var layout = ProjectLayout.Load(root);
        var archivePath = layout.ResolveArchive("SimCity_1.dat");
        if (waterOnly) return RunWaterOnly(layout, archivePath, output, overwrite);
        var catalogPath = Path.Combine(layout.GeneratedRoot, "sc4-property-catalog", "catalog.json");
        if (!File.Exists(catalogPath)) throw new FileNotFoundException("Required property ID/name cross-reference is missing.", catalogPath);
        var destination = layout.ResolveGeneratedOutputIn(output ?? "analysis/generated/sc4-residential-profile/residential-developers.json", "sc4-residential-profile");
        var provenancePath = layout.ResolveGeneratedOutputIn(Path.Combine(Path.GetDirectoryName(destination)!, "provenance.json"), "sc4-residential-profile");
        if (string.Equals(destination, provenancePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Data and provenance destinations must be different files.");
        if ((File.Exists(destination) || File.Exists(provenancePath)) && !overwrite) throw new IOException("Generated output already exists; pass --overwrite to replace it.");

        var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var labels = catalog.RootElement.GetProperty("records").EnumerateArray().ToDictionary(
            e => e.GetProperty("property_id_decimal").GetUInt32(),
            e => e.TryGetProperty("exemplar_types_dict_name", out var name) ? name.GetString() : null);
        var file = new FileInfo(archivePath);
        string archiveHash;
        using (var stream = File.OpenRead(archivePath)) archiveHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        var records = new List<DeveloperRecord>();
        uint indexVersion;
        using (var archive = DbpfArchive.Open(archivePath))
        {
            indexVersion = archive.IndexVersion;
            foreach (var instance in Instances)
            {
                var tgi = new Tgi(ExemplarType, ExemplarGroup, instance);
                var loaded = archive.ReadResource(tgi);
                var exemplar = ExemplarDecoder.Decode(loaded.Data);
                if (exemplar.parentCohort != default) throw new InvalidDataException($"{tgi} has nonzero parent cohort {exemplar.parentCohort}; inherited properties are not resolved.");
                if (exemplar.properties.Count != 49) throw new InvalidDataException($"{tgi} has {exemplar.properties.Count} properties; expected 49 from the pinned index evidence.");
                var propertyRecords = exemplar.properties.Select(p => new PropertyRecord(
                    p.id, $"0x{p.id:X8}", labels.GetValueOrDefault(p.id), p.typeCode, $"0x{p.typeCode:X8}", p.formatCode, p.repetitionTag,
                    p.isRepeated, p.encodedCount, p.formatName, p.value, p.values, p.unsupportedReason, p.rawBytesBase64)).ToArray();
                records.Add(new DeveloperRecord(tgi.ToString(), exemplar.signature, exemplar.parentCohort.ToString(), exemplar.Name,
                    exemplar.Type is uint type ? type : null, propertyRecords, exemplar.unsupportedVariants.ToArray()));
            }
        }

        var ids = records.SelectMany(r => r.Properties).GroupBy(p => p.Id).OrderBy(g => g.Key).Select(g => new CatalogEntry(
            g.Key, $"0x{g.Key:X8}", g.First().Name, g.Select(p => p.FormatName).Distinct().ToArray(), g.Count())).ToArray();
        var payload = new OutputDocument(1, DateTimeOffset.UtcNow, "SimCity_1.dat", file.Length, archiveHash, indexVersion,
            "SC4Data.DbpfArchive.ReadResource + ExemplarDecoder.Decode; exact TGIs only", records.ToArray(), ids);
        var provenance = new Provenance(1, Path.GetRelativePath(layout.ProjectRoot, archivePath), file.Length, archiveHash, indexVersion,
            "analysis/generated/sc4-sdk-reference/OpenSC4-rust/docs/specs/growth-developer.md (pinned repository revision 82feac1f6b5addb278e58d3815a66aee0de75623)",
            "analysis/generated/sc4-property-catalog/catalog.json", records.Count, records.Select(r => new ItemProvenance(r.Tgi, r.Properties.Length, r.ParentCohort)).ToArray());

        WriteAtomically(layout, destination, overwrite, s => JsonSerializer.Serialize(s, payload, JsonOptions), VerifyJson);
        WriteAtomically(layout, provenancePath, overwrite, s => JsonSerializer.Serialize(s, provenance, JsonOptions), VerifyJson);
        Console.WriteLine($"Extracted {records.Count} developer exemplars ({records.Sum(r => r.Properties.Length)} properties) to {destination}");
        return 0;
    }

    private static int RunWaterOnly(ProjectLayout layout, string archivePath, string? suppliedOutput, bool overwrite)
    {
        const uint waterCapPropertyId = 0x87CD6370;
        var destination = layout.ResolveGeneratedOutputIn(
            suppliedOutput ?? "analysis/generated/sc4-residential-profile/water-prerequisites.s4ew", "sc4-residential-profile");
        var provenancePath = layout.ResolveGeneratedOutputIn(
            Path.Combine(Path.GetDirectoryName(destination)!, "water-prerequisites.provenance.json"), "sc4-residential-profile");
        if (string.Equals(destination, provenancePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Water profile output must differ from its provenance sidecar.");
        if ((File.Exists(destination) || File.Exists(provenancePath)) && !overwrite)
            throw new IOException("Water profile or provenance already exists; pass --overwrite to replace them.");

        var archiveInfo = new FileInfo(archivePath);
        byte[] archiveSha256;
        using (var stream = File.OpenRead(archivePath)) archiveSha256 = SHA256.HashData(stream);

        var caps = new int[Instances.Length];
        var tgis = new string[Instances.Length];
        using (var archive = DbpfArchive.Open(archivePath))
        {
            for (var i = 0; i < Instances.Length; i++)
            {
                var tgi = new Tgi(ExemplarType, ExemplarGroup, Instances[i]);
                tgis[i] = tgi.ToString();
                var exemplar = ExemplarDecoder.Decode(archive.ReadResource(tgi).Data);
                if (exemplar.parentCohort != default)
                    throw new InvalidDataException($"{tgi} has nonzero parent cohort {exemplar.parentCohort}; inherited water caps are unsupported.");
                var matches = exemplar.properties.Where(p => p.id == waterCapPropertyId).ToArray();
                if (matches.Length != 1)
                    throw new InvalidDataException($"{tgi} has {matches.Length} occurrences of water cap property 0x{waterCapPropertyId:X8}; expected exactly one.");
                var property = matches[0];
                if (property.unsupportedReason is not null || property.formatCode != 1 || property.formatName != "UInt8" || property.isRepeated || property.values is not null || property.value is not byte value)
                    throw new InvalidDataException($"{tgi} water cap property 0x{waterCapPropertyId:X8} is not one supported scalar UInt8.");
                caps[i] = value;
            }
        }

        var profile = new ResidentialWaterProfile(archiveSha256, caps[0], caps[1], caps[2]);
        byte[] encoded;
        using (var buffer = new MemoryStream())
        {
            ResidentialWaterProfileCodec.Write(buffer, profile);
            encoded = buffer.ToArray();
        }
        if (encoded.Length != ResidentialWaterProfileCodec.EncodedSize)
            throw new InvalidDataException($"Water profile codec produced {encoded.Length} bytes; expected {ResidentialWaterProfileCodec.EncodedSize}.");
        ResidentialWaterProfile decoded;
        using (var buffer = new MemoryStream(encoded, writable: false)) decoded = ResidentialWaterProfileCodec.Read(buffer);
        if (!decoded.SourceSha256.SequenceEqual(archiveSha256) || decoded.LowMaximumStage != caps[0] ||
            decoded.MediumMaximumStage != caps[1] || decoded.HighMaximumStage != caps[2])
            throw new InvalidDataException("Water profile codec round trip changed the source hash or caps.");

        var provenance = new WaterProvenance(1, Path.GetRelativePath(layout.ProjectRoot, archivePath), archiveInfo.Length,
            Convert.ToHexString(archiveSha256).ToLowerInvariant(), "SC4Data.DbpfArchive.ReadResource + ExemplarDecoder.Decode",
            tgis, $"0x{waterCapPropertyId:X8}", "UInt8 scalar", caps,
            Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant(), encoded.Length);
        WriteAtomically(layout, destination, overwrite, stream => stream.Write(encoded), path => VerifyWaterProfile(path, profile));
        WriteAtomically(layout, provenancePath, overwrite, stream => JsonSerializer.Serialize(stream, provenance, JsonOptions), VerifyJson);
        Console.WriteLine($"Extracted water caps {caps[0]}/{caps[1]}/{caps[2]} into {destination} ({encoded.Length} bytes; archive SHA-256 {provenance.ArchiveSha256})");
        return 0;
    }

    private static void VerifyWaterProfile(string path, ResidentialWaterProfile expected)
    {
        using var stream = File.OpenRead(path);
        var decoded = ResidentialWaterProfileCodec.Read(stream);
        if (!decoded.SourceSha256.SequenceEqual(expected.SourceSha256) || decoded.LowMaximumStage != expected.LowMaximumStage ||
            decoded.MediumMaximumStage != expected.MediumMaximumStage || decoded.HighMaximumStage != expected.HighMaximumStage)
            throw new InvalidDataException("Written residential water profile failed codec verification.");
    }

    private static void VerifyJson(string path) { using var stream = File.OpenRead(path); using var _ = JsonDocument.Parse(stream); }

    private static void WriteAtomically(ProjectLayout layout, string destination, bool overwrite, Action<Stream> write, Action<string> verify)
    {
        var safeDestination = layout.ResolveGeneratedOutputIn(destination, "sc4-residential-profile");
        var directory = Path.GetDirectoryName(safeDestination)!;
        var temporary = layout.ResolveGeneratedOutputIn(Path.Combine(directory, $".{Path.GetFileName(safeDestination)}.{Guid.NewGuid():N}.tmp"), "sc4-residential-profile");
        var owned = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { owned = true; write(stream); stream.Flush(true); }
            verify(temporary);
            safeDestination = layout.ResolveGeneratedOutputIn(safeDestination, "sc4-residential-profile");
            File.Move(temporary, safeDestination, overwrite);
            owned = false;
        }
        finally
        {
            if (owned && File.Exists(temporary))
            {
                try { File.Delete(layout.ResolveGeneratedOutputIn(temporary, "sc4-residential-profile")); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Console.Error.WriteLine($"warning: temporary cleanup failed: {e.Message}"); }
            }
        }
    }

    private static (string? Root, string? Output, bool Overwrite, bool WaterOnly, bool Help) Parse(string[] args)
    {
        string? root = null, output = null; var overwrite = false; var waterOnly = false; var help = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root": root = Value(args, ref i); break;
                case "--out": output = Value(args, ref i); break;
                case "--overwrite": overwrite = true; break;
                case "--water-only": waterOnly = true; break;
                case "--help": case "-h": help = true; break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }
        return (root, output, overwrite, waterOnly, help);
    }
    private static string Value(string[] args, ref int i) { if (++i >= args.Length || args[i].StartsWith("--")) throw new ArgumentException("Option requires a value."); return args[i]; }

    private sealed record OutputDocument(int SchemaVersion, DateTimeOffset GeneratedUtc, string Archive, long ArchiveBytes, string ArchiveSha256, uint ArchiveIndexVersion, string ExtractionMethod, DeveloperRecord[] Exemplars, CatalogEntry[] NumericIdNameCatalog);
    private sealed record DeveloperRecord(string Tgi, string Signature, string ParentCohort, string? Name, uint? Type, PropertyRecord[] Properties, string[] UnsupportedVariants);
    private sealed record PropertyRecord(uint Id, string IdHex, string? Name, uint TypeCode, string TypeCodeHex, byte FormatCode, ushort RepetitionTag, bool IsRepeated, uint? EncodedCount, string FormatName, object? Value, object[]? Values, string? UnsupportedReason, string? RawBytesBase64);
    private sealed record CatalogEntry(uint Id, string IdHex, string? Name, string[] ObservedFormats, int Occurrences);
    private sealed record Provenance(int SchemaVersion, string ArchivePath, long ArchiveBytes, string ArchiveSha256, uint ArchiveIndexVersion, string BehaviorReference, string PropertyCatalogReference, int ExemplarCount, ItemProvenance[] Exemplars);
    private sealed record ItemProvenance(string Tgi, int PropertyCount, string ParentCohort);
    private sealed record WaterProvenance(int SchemaVersion, string ArchivePath, long ArchiveBytes, string ArchiveSha256,
        string ExtractionMethod, string[] Exemplars, string PropertyId, string PropertyFormat, int[] RawCaps,
        string OutputSha256, int OutputBytes);
}
