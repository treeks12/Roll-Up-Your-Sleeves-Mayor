using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SC4Data;

internal static class Program
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const int MaximumPreviewCharacters = 512;
    private const int MaximumIndexedResources = 10_000;
    private const long MaximumExemplarIndexBytes = 512L * 1024 * 1024;
    private const int MaximumDisplayedProperties = 128;
    private const int MaximumDisplayedValues = 16;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

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
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        if (command is not ("list" or "inspect" or "extract" or "ltext-index" or "exemplar-index"))
        {
            throw new ArgumentException($"Unknown command '{args[0]}'.");
        }

        var options = ParseOptions(args[1..]);
        EnsureAllowedOptions(command, options);
        var layout = ProjectLayout.Load(GetValue(options, "--root", required: false));
        var archivePath = layout.ResolveArchive(GetValue(options, "--archive", required: true)!);
        using var archive = DbpfArchive.Open(archivePath);

        return command switch
        {
            "list" => List(archive, options),
            "inspect" => Inspect(archive, options),
            "extract" => Extract(archive, layout, options),
            "ltext-index" => WriteLTextIndex(archive, layout, options),
            "exemplar-index" => WriteExemplarIndex(archive, layout, options),
            _ => throw new InvalidOperationException("Unexpected command.")
        };
    }

    private static int List(DbpfArchive archive, Dictionary<string, string?> options)
    {
        uint? typeFilter = options.TryGetValue("--type", out var typeValue) ? ParseUInt32(typeValue!, "--type") : null;
        Console.WriteLine($"Archive: {archive.Path}");
        Console.WriteLine($"Index version: {archive.IndexVersion}; entries: {archive.Entries.Count}");
        Console.WriteLine("TGI                                Offset       Stored bytes  Stored form");
        foreach (var entry in archive.Entries)
        {
            if (typeFilter.HasValue && entry.Tgi.Type != typeFilter.Value)
            {
                continue;
            }

            var decodedSize = archive.DecodedSize(entry);
            var storedForm = decodedSize.HasValue ? $"QFS -> {decodedSize.Value} bytes" : "raw";
            Console.WriteLine($"{entry.Tgi,-34} {entry.Offset,12} {entry.Size,13}  {storedForm}");
        }

        return 0;
    }

    private static int Inspect(DbpfArchive archive, Dictionary<string, string?> options)
    {
        var tgi = Tgi.Parse(GetValue(options, "--tgi", required: true)!);
        var loaded = archive.ReadResource(tgi);
        var isLText = loaded.Entry.Tgi.Type == LTextDecoder.TypeId;
        var isExemplar = loaded.Entry.Tgi.Type == ExemplarDecoder.TypeId;
        var isPng = StartsWith(loaded.Data, PngSignature);
        var contentKind = isLText ? "LTEXT" : isExemplar ? "Exemplar/Cohort" : isPng ? "PNG by magic" : "unknown/raw";
        Console.WriteLine($"TGI: {loaded.Entry.Tgi}");
        Console.WriteLine($"Offset: {loaded.Entry.Offset}");
        Console.WriteLine($"Stored bytes: {loaded.Entry.Size}");
        Console.WriteLine($"Compression: {(loaded.IsCompressed ? "SC4 QFS via DIR" : "none")}");
        Console.WriteLine($"Decoded bytes: {loaded.Data.Length}");
        Console.WriteLine($"Content: {contentKind}");

        if (isLText)
        {
            var decoded = LTextDecoder.DecodeWithInfo(loaded.Data);
            var text = decoded.Text;
            var previewLength = ParsePreviewCharacters(GetValue(options, "--preview-chars", required: false) ?? "240");
            var preview = TakeSafePreview(text, previewLength);
            Console.WriteLine($"LTEXT code units: {decoded.CodeUnitCount}; control field: 0x{decoded.Control:X4}");
            Console.WriteLine($"LTEXT UTF-8 preview ({text.Length} UTF-16 code units, first {preview.Length} shown): {JsonSerializer.Serialize(preview, JsonOptions)}");
        }
        else if (isExemplar)
        {
            var record = ExemplarDecoder.Decode(loaded.Data);
            PrintExemplar(record, ParsePreviewCharacters(GetValue(options, "--preview-chars", required: false) ?? "160"));
        }
        else if (isPng)
        {
            Console.WriteLine("PNG signature: 89 50 4E 47 0D 0A 1A 0A");
        }

        return 0;
    }

    private static int Extract(DbpfArchive archive, ProjectLayout layout, Dictionary<string, string?> options)
    {
        var tgi = Tgi.Parse(GetValue(options, "--tgi", required: true)!);
        var loaded = archive.ReadResource(tgi);
        byte[] outputBytes;
        if (options.ContainsKey("--text-utf8"))
        {
            if (loaded.Entry.Tgi.Type != LTextDecoder.TypeId)
            {
                throw new InvalidDataException("--text-utf8 can only be used with an LTEXT resource (type 0x2026960B).");
            }

            outputBytes = LTextDecoder.EncodeUtf8(loaded.Data);
        }
        else
        {
            outputBytes = loaded.Data;
        }

        var outputPath = layout.ResolveGeneratedOutput(GetValue(options, "--out", required: true)!);
        using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(outputBytes);
        }

        Console.WriteLine($"Extracted {loaded.Entry.Tgi} ({loaded.Data.Length} decoded bytes{(options.ContainsKey("--text-utf8") ? $", {outputBytes.Length} UTF-8 bytes" : string.Empty)}) to {outputPath}");
        return 0;
    }

    private static int WriteLTextIndex(DbpfArchive archive, ProjectLayout layout, Dictionary<string, string?> options)
    {
        var outputPath = layout.ResolveGeneratedOutput(GetValue(options, "--out", required: true)!);
        var previewChars = ParsePreviewCharacters(GetValue(options, "--preview-chars", required: false) ?? "160");
        var maxEntries = ParseNonNegativeInt(GetValue(options, "--max-entries", required: false) ?? "10000", "--max-entries");
        if (maxEntries is < 1 or > 10000)
        {
            throw new ArgumentException("--max-entries must be from 1 through 10000.");
        }

        var ltextEntries = archive.Entries.Where(entry => entry.Tgi.Type == LTextDecoder.TypeId).ToArray();
        var tempPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var count = 0;
        var invalidCount = 0;
        try
        {
            using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                foreach (var entry in ltextEntries.Take(maxEntries))
                {
                    var loaded = archive.ReadResource(entry.Tgi);
                    LTextInfo decoded;
                    try
                    {
                        decoded = LTextDecoder.DecodeWithInfo(loaded.Data);
                    }
                    catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException)
                    {
                        writer.WriteLine(JsonSerializer.Serialize(new LTextIndexRow(
                            entry.Tgi.ToString(),
                            entry.Size,
                            loaded.Data.Length,
                            loaded.IsCompressed,
                            LTextDecoder.ReadControlFieldIfPresent(loaded.Data),
                            string.Empty,
                            false,
                            exception.Message), JsonOptions));
                        invalidCount++;
                        continue;
                    }

                    var text = decoded.Text;
                    var preview = TakeSafePreview(text, previewChars);
                    var row = new LTextIndexRow(
                        entry.Tgi.ToString(),
                        entry.Size,
                        loaded.Data.Length,
                        loaded.IsCompressed,
                        decoded.Control,
                        preview,
                        preview.Length < text.Length,
                        null);
                    writer.WriteLine(JsonSerializer.Serialize(row, JsonOptions));
                    count++;
                }
            }

            File.Move(tempPath, outputPath);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            throw;
        }

        Console.WriteLine($"Wrote {count} previews and recorded {invalidCount} invalid LTEXT payloads ({count + invalidCount} of {ltextEntries.Length} inspected) to {outputPath}");
        return 0;
    }

    private static int WriteExemplarIndex(DbpfArchive archive, ProjectLayout layout, Dictionary<string, string?> options)
    {
        var outputPath = layout.ResolveGeneratedOutputIn(GetValue(options, "--out", required: true)!, "sc4-exemplars");
        var maxEntries = ParseNonNegativeInt(GetValue(options, "--max-entries", required: false) ?? "10000", "--max-entries");
        if (maxEntries is < 1 or > MaximumIndexedResources)
        {
            throw new ArgumentException($"--max-entries must be from 1 through {MaximumIndexedResources}.");
        }

        var exemplarEntries = archive.Entries.Where(entry => entry.Tgi.Type == ExemplarDecoder.TypeId).ToArray();
        var tempPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var decodedCount = 0;
        var partialCount = 0;
        var unsupportedCount = 0;
        var invalidCount = 0;
        long outputBytes = 0;
        try
        {
            using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                foreach (var entry in exemplarEntries.Take(maxEntries))
                {
                    ExemplarIndexRow row;
                    try
                    {
                        var loaded = archive.ReadResource(entry.Tgi);
                        var record = ExemplarDecoder.Decode(loaded.Data);
                        var hasUnsupportedVariants = record.unsupportedVariants.Count > 0;
                        row = new ExemplarIndexRow(
                            entry.Tgi.ToString(),
                            hasUnsupportedVariants ? "partial" : "ok",
                            record.signature,
                            record.Name,
                            record.Type,
                            record.parentCohort.ToString(),
                            record.properties.Count,
                            record.properties,
                            hasUnsupportedVariants ? string.Join("; ", record.unsupportedVariants) : null);
                        if (hasUnsupportedVariants)
                        {
                            partialCount++;
                        }
                        else
                        {
                            decodedCount++;
                        }
                    }
                    catch (UnsupportedExemplarFormatException exception)
                    {
                        row = ExemplarIndexRow.Failure(entry.Tgi, "unsupported", exception.Message);
                        unsupportedCount++;
                    }
                    catch (InvalidDataException exception)
                    {
                        row = ExemplarIndexRow.Failure(entry.Tgi, "invalid", exception.Message);
                        invalidCount++;
                    }
                    catch (KeyNotFoundException exception)
                    {
                        row = ExemplarIndexRow.Failure(entry.Tgi, "invalid", exception.Message);
                        invalidCount++;
                    }

                    var json = JsonSerializer.Serialize(row, JsonOptions);
                    var rowBytes = (long)Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(Environment.NewLine);
                    if (outputBytes + rowBytes > MaximumExemplarIndexBytes)
                    {
                        throw new InvalidDataException($"Exemplar index would exceed the {MaximumExemplarIndexBytes} byte output limit.");
                    }

                    writer.WriteLine(json);
                    outputBytes += rowBytes;
                }
            }

            File.Move(tempPath, outputPath);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            throw;
        }

        var inspectedCount = decodedCount + partialCount + unsupportedCount + invalidCount;
        Console.WriteLine($"Wrote {decodedCount} decoded, {partialCount} partial, {unsupportedCount} unsupported, and {invalidCount} invalid records ({inspectedCount} of {exemplarEntries.Length} inspected; {outputBytes} UTF-8 bytes) to {outputPath}");
        return 0;
    }

    private static void PrintExemplar(ExemplarRecord record, int previewCharacters)
    {
        Console.WriteLine($"Signature: {record.signature}");
        Console.WriteLine($"Parent cohort: {record.parentCohort}");
        Console.WriteLine($"Name: {(record.Name is null ? "<not present or ambiguous>" : JsonSerializer.Serialize(TakeSafePreview(record.Name, previewCharacters), JsonOptions))}");
        Console.WriteLine($"Type property 0x00000010: {(record.Type is null ? "<not present or ambiguous>" : JsonSerializer.Serialize(record.Type, JsonOptions))}");
        Console.WriteLine($"Properties: {record.properties.Count}");

        foreach (var property in record.properties.Take(MaximumDisplayedProperties))
        {
            var label = property.id switch
            {
                0x00000010 => " (Exemplar Type)",
                0x00000020 => " (Exemplar Name)",
                _ => string.Empty
            };
            string displayValue;
            if (property.value is string text)
            {
                var preview = TakeSafePreview(text, previewCharacters);
                displayValue = JsonSerializer.Serialize(preview, JsonOptions) + (preview.Length < text.Length ? "…" : string.Empty);
            }
            else if (property.unsupportedReason is not null)
            {
                displayValue = $"<unsupported: {property.unsupportedReason}>";
            }
            else if (property.values is not null)
            {
                var shown = property.values.Take(MaximumDisplayedValues).ToArray();
                displayValue = JsonSerializer.Serialize(shown, JsonOptions);
                if (shown.Length < property.values.Length)
                {
                    displayValue += $" … ({property.values.Length - shown.Length} more)";
                }
            }
            else
            {
                displayValue = JsonSerializer.Serialize(property.value, JsonOptions);
            }

            Console.WriteLine($"  0x{property.id:X8}{label} type=0x{property.typeCode:X8} {property.formatName}{(property.isRepeated ? $" repeated(count={property.encodedCount})" : "")} = {displayValue}");
        }

        if (record.properties.Count > MaximumDisplayedProperties)
        {
            Console.WriteLine($"  … {record.properties.Count - MaximumDisplayedProperties} properties omitted from this bounded display.");
        }
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{option}'.");
            }

            if (option == "--text-utf8")
            {
                if (!options.TryAdd(option, null))
                {
                    throw new ArgumentException($"Option {option} was supplied more than once.");
                }
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option {option} requires a value.");
            }

            if (!options.TryAdd(option, args[++index]))
            {
                throw new ArgumentException($"Option {option} was supplied more than once.");
            }
        }

        return options;
    }

    private static void EnsureAllowedOptions(string command, Dictionary<string, string?> options)
    {
        var allowed = command switch
        {
            "list" => new[] { "--root", "--archive", "--type" },
            "inspect" => new[] { "--root", "--archive", "--tgi", "--preview-chars" },
            "extract" => new[] { "--root", "--archive", "--tgi", "--out", "--text-utf8" },
            "ltext-index" => new[] { "--root", "--archive", "--out", "--preview-chars", "--max-entries" },
            "exemplar-index" => new[] { "--root", "--archive", "--out", "--max-entries" },
            _ => []
        };

        foreach (var option in options.Keys)
        {
            if (!allowed.Contains(option, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Option {option} is not valid for {command}.");
            }
        }
    }

    private static string? GetValue(Dictionary<string, string?> options, string name, bool required)
    {
        if (!options.TryGetValue(name, out var value) || value is null)
        {
            if (required)
            {
                throw new ArgumentException($"Missing required option {name}.");
            }
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Option {name} cannot be empty.");
        }

        return value;
    }

    private static uint ParseUInt32(string value, string optionName)
    {
        var digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (!uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ArgumentException($"{optionName} must be a hexadecimal 32-bit number.");
        }

        return parsed;
    }

    private static int ParseNonNegativeInt(string value, string optionName)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            throw new ArgumentException($"{optionName} must be a non-negative integer.");
        }

        return parsed;
    }

    private static int ParsePreviewCharacters(string value)
    {
        var parsed = ParseNonNegativeInt(value, "--preview-chars");
        if (parsed > MaximumPreviewCharacters)
        {
            throw new ArgumentException($"--preview-chars must be from 0 through {MaximumPreviewCharacters}.");
        }

        return parsed;
    }

    private static string TakeSafePreview(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        var end = limit;
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }

    private static bool StartsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix) => value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);

    private static void PrintHelp()
    {
        Console.WriteLine("SC4Data: bounded, read-only SimCity 4 DBPF inspector.");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  list    --archive <game-relative-or-absolute-path> [--root <project-root>] [--type <hex-type-id>]");
        Console.WriteLine("  inspect --archive <path> --tgi <TYPE:GROUP:INSTANCE> [--root <project-root>] [--preview-chars <n>]");
        Console.WriteLine("  extract --archive <path> --tgi <TYPE:GROUP:INSTANCE> --out <path-under-analysis/generated> [--root <project-root>] [--text-utf8]");
        Console.WriteLine("  ltext-index --archive <path> --out <path-under-analysis/generated> [--root <project-root>] [--preview-chars <n>] [--max-entries <n>]");
        Console.WriteLine("  exemplar-index --archive <path> --out <path-under-analysis/generated/sc4-exemplars> [--root <project-root>] [--max-entries <n>]");
        Console.WriteLine();
        Console.WriteLine("Archive paths must resolve inside config/local.paths.json's SimCity4 directory. Extraction never overwrites an existing file.");
        Console.WriteLine($"ltext-index writes UTF-8 JSON Lines previews for up to 10000 LTEXT entries, with at most {MaximumPreviewCharacters} characters per preview; it does not write full game payloads.");
        Console.WriteLine($"exemplar-index writes typed JSON Lines for up to {MaximumIndexedResources} Exemplar/Cohort resources, capped at {MaximumExemplarIndexBytes} bytes.");
        Console.WriteLine("Resources are limited to 64 MiB; compressed entries are selected through the SC4 DIR resource and decoded size is checked.");
    }

    private sealed record LTextIndexRow(string tgi, uint storedBytes, int decodedBytes, bool compressed, ushort? control, string preview, bool previewTruncated, string? decodeError);

    private sealed record ExemplarIndexRow(string tgi, string status, string? signature, string? name, object? type, string? parentCohort, int? propertyCount, IReadOnlyList<ExemplarProperty> properties, string? error)
    {
        public static ExemplarIndexRow Failure(Tgi tgi, string status, string error) =>
            new(tgi.ToString(), status, null, null, null, null, null, [], error);
    }
}
