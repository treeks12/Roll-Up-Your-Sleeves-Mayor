using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SC4Data;

internal static class Program
{
    private const uint S3DType = 0x5AD0E817;
    private const uint FshType = 0x7AB50E44;
    private const uint OpenSc4FshGroup = 0x1ABE787D;
    private static readonly Regex NewRecord = new(@"new_record\(advisor_ids\.(?<advisor>[A-Z_]+),\s*advisor_genders\.(?<gender>[MF])\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StateModel = new("hr\\[advisor_anim_types\\.(?<state>[A-Z_]+)\\]\\s*=\\s*hex2dec\\(\"(?<iid>[0-9a-fA-F]{8})\"\\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record Mapping(string Advisor, string Gender, string State, uint Instance, int Line);
    private sealed record Located(string Archive, Tgi Tgi, uint StoredBytes, bool Compressed, byte[] Data);

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && string.Equals(args[0], "analyze-existing", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length > 2) throw new ArgumentException("Usage: SC4AdvisorModels analyze-existing [project-root]");
                return AnalyzeExisting(ProjectLayout.Load(args.Length == 2 ? args[1] : null));
            }
            var root = args.Length == 1 ? args[0] : null;
            if (args.Length > 1) throw new ArgumentException("Usage: SC4AdvisorModels [project-root] | SC4AdvisorModels analyze-existing [project-root]");
            var layout = ProjectLayout.Load(root);
            var luaPath = Path.Combine(layout.ProjectRoot, "analysis", "generated", "sc4-lua", "resources", "CA63E2A3_4A5E8EF6_FF322502.bin");
            var outputRoot = layout.ResolveGeneratedOutput(Path.Combine("analysis", "generated", "sc4-advisor-models", "advisor-model-map.csv"));
            var mappings = ParseMappings(luaPath);
            WriteMappings(outputRoot, mappings);
            var targets = mappings.Select(mapping => mapping.Instance).ToHashSet();
            var found = new Dictionary<Tgi, Located>();
            var outputDir = Path.GetDirectoryName(outputRoot)!;
            var archives = Directory.GetFiles(layout.GameRoot, "*.dat", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).Equals("SimCityLocale.DAT", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var archivePath in archives)
            {
                using var archive = DbpfArchive.Open(layout.ResolveArchive(archivePath));
                foreach (var entry in archive.Entries.Where(entry => entry.Tgi.Type == S3DType && targets.Contains(entry.Tgi.Instance)))
                {
                    var loaded = archive.ReadResource(entry.Tgi);
                    found.TryAdd(entry.Tgi, new Located(Path.GetFileName(archivePath), entry.Tgi, entry.Size, loaded.IsCompressed, loaded.Data));
                }
            }

            ModelInspector.Analyze(layout, outputDir, found.Values.Select(resource => new ModelInspector.ModelResource(resource.Archive, resource.Tgi, resource.StoredBytes, resource.Compressed, resource.Data)).ToArray(), mappings.Select(mapping => new ModelInspector.SourceMapping(mapping.Advisor, mapping.Gender, mapping.State, mapping.Instance)).ToArray());
            var indexPath = Path.Combine(outputDir, "s3d-resources.csv");
            using (var writer = NewWriter(indexPath))
            {
                writer.WriteLine("advisor,gender,state,instance,tgi,archive,storedBytes,decodedBytes,qfs,signature,sha256,extractedFile");
                foreach (var mapping in mappings)
                {
                    var matches = found.Values.Where(resource => resource.Tgi.Instance == mapping.Instance).OrderBy(resource => resource.Tgi.Group).ThenBy(resource => resource.Archive, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (matches.Length == 0)
                    {
                        WriteCsv(writer, mapping.Advisor, mapping.Gender, mapping.State, mapping.Instance.ToString("X8"), "", "", "", "", "", "", "");
                        continue;
                    }

                    foreach (var resource in matches)
                    {
                        var signature = resource.Data.Length >= 4 ? Encoding.ASCII.GetString(resource.Data, 0, 4) : "short";
                        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(resource.Data)).ToLowerInvariant();
                        var fileName = $"{resource.Tgi.Group:X8}_{resource.Tgi.Instance:X8}.s3d.bin";
                        var extractedPath = Path.Combine(outputDir, "models", fileName);
                        Directory.CreateDirectory(Path.GetDirectoryName(extractedPath)!);
                        if (!File.Exists(extractedPath)) File.WriteAllBytes(extractedPath, resource.Data);
                        WriteCsv(writer, mapping.Advisor, mapping.Gender, mapping.State, mapping.Instance.ToString("X8"), resource.Tgi.ToString(), resource.Archive, resource.StoredBytes.ToString(CultureInfo.InvariantCulture), resource.Data.Length.ToString(CultureInfo.InvariantCulture), resource.Compressed.ToString(), signature, hash, Path.GetRelativePath(outputDir, extractedPath));
                    }
                }
            }

            Console.WriteLine($"Parsed {mappings.Count} animation assignments from {luaPath}");
            Console.WriteLine($"Scanned {archives.Length} top-level game DBPF archives; found {found.Count} unique S3D TGIs for {targets.Count} mapped instances");
            Console.WriteLine($"Wrote {indexPath} and decoded model samples under {Path.Combine(outputDir, "models")}");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or KeyNotFoundException or FormatException or DecoderFallbackException)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static int AnalyzeExisting(ProjectLayout layout)
    {
        var outputDirectory = Path.Combine(layout.ProjectRoot, "analysis", "generated", "sc4-advisor-models");
        var luaPath = Path.Combine(layout.ProjectRoot, "analysis", "generated", "sc4-lua", "resources", "CA63E2A3_4A5E8EF6_FF322502.bin");
        var indexPath = Path.Combine(outputDirectory, "s3d-resources.csv");
        if (!File.Exists(indexPath)) throw new FileNotFoundException("Run the inventory command first; S3D index is missing.", indexPath);
        var mappings = ParseMappings(luaPath);
        var resources = new Dictionary<Tgi, ModelInspector.ModelResource>();
        var archiveCache = new Dictionary<string, DbpfArchive>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadLines(indexPath).Skip(1))
            {
                var fields = line.Split(',').Select(field => field.Trim().Trim('"')).ToArray();
                if (fields.Length < 12 || fields[4].Length == 0 || fields[5].Length == 0) continue;
                var tgi = Tgi.Parse(fields[4]);
                var archiveName = fields[5];
                if (!archiveCache.TryGetValue(archiveName, out var archive))
                {
                    archive = DbpfArchive.Open(layout.ResolveArchive(archiveName));
                    archiveCache.Add(archiveName, archive);
                }
                var loaded = archive.ReadResource(tgi);
                resources.TryAdd(tgi, new ModelInspector.ModelResource(archiveName, tgi, loaded.Entry.Size, loaded.IsCompressed, loaded.Data));
            }
        }
        finally
        {
            foreach (var archive in archiveCache.Values) archive.Dispose();
        }

        var modelDirectory = Path.Combine(outputDirectory, "models");
        var missing = resources.Keys.Where(tgi => !File.Exists(Path.Combine(modelDirectory, $"{tgi.Group:X8}_{tgi.Instance:X8}.s3d.bin"))).ToArray();
        if (missing.Length > 0) throw new FileNotFoundException($"Expected extracted S3D sample is missing: {missing[0]}");
        ModelInspector.Analyze(layout, outputDirectory, resources.Values.ToArray(), mappings.Select(mapping => new ModelInspector.SourceMapping(mapping.Advisor, mapping.Gender, mapping.State, mapping.Instance)).ToArray());
        return 0;
    }

    private static List<Mapping> ParseMappings(string path)
    {
        var lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        var mappings = new List<Mapping>();
        var advisor = string.Empty;
        var gender = string.Empty;
        for (var index = 0; index < lines.Length; index++)
        {
            var record = NewRecord.Match(lines[index]);
            if (record.Success)
            {
                advisor = record.Groups["advisor"].Value;
                gender = record.Groups["gender"].Value;
                continue;
            }

            var state = StateModel.Match(lines[index]);
            if (state.Success && advisor.Length != 0)
            {
                mappings.Add(new Mapping(advisor, gender, state.Groups["state"].Value, uint.Parse(state.Groups["iid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), index + 1));
            }
        }

        if (mappings.Count == 0 || !mappings.Any(mapping => mapping.Advisor == "UTILITIES" && mapping.State == "NEUTRAL"))
        {
            throw new InvalidDataException("The advisor animation Lua source did not produce the expected mapping records.");
        }

        return mappings;
    }

    private static void WriteMappings(string outputPath, IReadOnlyList<Mapping> mappings)
    {
        using var writer = NewWriter(outputPath);
        writer.WriteLine("advisor,gender,state,modelInstance,luaLine");
        foreach (var mapping in mappings)
        {
            WriteCsv(writer, mapping.Advisor, mapping.Gender, mapping.State, mapping.Instance.ToString("X8"), mapping.Line.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static StreamWriter NewWriter(string path) => new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None), new UTF8Encoding(false));

    private static void WriteCsv(StreamWriter writer, params string[] fields) => writer.WriteLine(string.Join(',', fields.Select(field => '"' + field.Replace("\"", "\"\"", StringComparison.Ordinal) + '"')));
}
