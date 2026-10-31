using System.Security.Cryptography;
using System.Text.Json;
using SC4Data;
using SC4Evolution.Simulation;

internal static class Program
{
    private static readonly Tgi TuningTgi = new(0x6534284A, 0xE7E2C2DB, 0x4A1F3CE6);
    private const uint NeutralCurvePropertyId = 0x4A1F38B5;
    private const uint ModifierCurvePropertyId = 0x4A1F38B6;
    private const uint PenaltyDaysPropertyId = 0x4A1F38B7;

    private static int Main(string[] args)
    {
        try
        {
            Dictionary<string, string?>? parsedOptions = ParseArguments(args);
            if (parsedOptions is null) return 0;
            Dictionary<string, string?> options = parsedOptions;
            ProjectLayout layout = ProjectLayout.Load(GetValue(options, "--root"));
            string archivePath = layout.ResolveArchive(GetValue(options, "--archive") ?? "SimCity_1.dat");
            string outputPath = layout.ResolveGeneratedOutputIn(
                GetValue(options, "--out") ?? "analysis/generated/sc4-tax-profile/tax-demand.s4td",
                "sc4-tax-profile");
            string provenancePath = layout.ResolveGeneratedOutputIn(
                Path.ChangeExtension(outputPath, ".provenance.json"),
                "sc4-tax-profile");

            if (File.Exists(outputPath) || File.Exists(provenancePath))
                throw new IOException("Output already exists; choose a new --out path or remove the prior generated result explicitly.");

            byte[] archiveHash;
            using (FileStream archiveStream = File.OpenRead(archivePath))
                archiveHash = SHA256.HashData(archiveStream);
            string archiveSha256 = Convert.ToHexString(archiveHash).ToLowerInvariant();

            using DbpfArchive archive = DbpfArchive.Open(archivePath);
            LoadedResource resource = archive.ReadResource(TuningTgi);
            ExemplarRecord exemplar = ExemplarDecoder.Decode(resource.Data);

            ExemplarProperty neutralProperty = RequireUniqueProperty(exemplar, NeutralCurvePropertyId);
            ExemplarProperty modifierProperty = RequireUniqueProperty(exemplar, ModifierCurvePropertyId);
            ExemplarProperty penaltyProperty = RequireUniqueProperty(exemplar, PenaltyDaysPropertyId);
            float[] neutralValues = RequireFloatArray(neutralProperty, "neutral tax-rate curve");
            float[] modifierValues = RequireFloatArray(modifierProperty, "tax modifier curve");
            uint penaltyDays = RequireUInt32Scalar(penaltyProperty, "tax penalty days");

            string sourceIdentity = "SC4 archive SHA-256 " + archiveSha256 + "; TGI " + resource.Entry.Tgi;
            TaxDemandTuning tuning = new TaxDemandTuning(neutralValues, modifierValues, penaltyDays, sourceIdentity);

            string outputDirectory = Path.GetDirectoryName(outputPath)!;
            string outputTemp = Path.Combine(outputDirectory, ".tax-demand-" + Guid.NewGuid().ToString("N") + ".tmp");
            string provenanceTemp = Path.Combine(outputDirectory, ".tax-demand-provenance-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream output = new(outputTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    TaxDemandTuningCodec.Write(output, tuning);

                TaxDemandTuning verified;
                using (FileStream input = File.OpenRead(outputTemp)) verified = TaxDemandTuningCodec.Read(input);
                if (!string.Equals(verified.SourceIdentity, tuning.SourceIdentity, StringComparison.Ordinal) ||
                    verified.TaxPenaltyDays != tuning.TaxPenaltyDays ||
                    verified.NeutralTaxRateByPopulation.PointCount != tuning.NeutralTaxRateByPopulation.PointCount ||
                    verified.TaxModifierByRateVariance.PointCount != tuning.TaxModifierByRateVariance.PointCount)
                    throw new InvalidDataException("The written tax tuning profile failed its read-back check.");

                var provenance = new
                {
                    archive = Path.GetFileName(archivePath),
                    archiveSha256,
                    archiveLength = new FileInfo(archivePath).Length,
                    dbpfIndexVersion = archive.IndexVersion,
                    tgi = resource.Entry.Tgi.ToString(),
                    signature = exemplar.signature,
                    storedBytes = resource.Entry.Size,
                    decodedBytes = resource.Data.Length,
                    compressed = resource.IsCompressed,
                    properties = new[]
                    {
                        PropertyProvenance(neutralProperty, neutralValues.Length / 2),
                        PropertyProvenance(modifierProperty, modifierValues.Length / 2),
                        PropertyProvenance(penaltyProperty, null)
                    },
                    penaltyDays,
                    outputFormat = "S4TD v1",
                    outputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outputTemp))).ToLowerInvariant()
                };
                File.WriteAllText(provenanceTemp, JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true }));

                File.Move(outputTemp, outputPath);
                File.Move(provenanceTemp, provenancePath);
            }
            finally
            {
                if (File.Exists(outputTemp)) File.Delete(outputTemp);
                if (File.Exists(provenanceTemp)) File.Delete(provenanceTemp);
            }

            Console.WriteLine("Loaded valid demand-tax tuning from " + resource.Entry.Tgi + ".");
            Console.WriteLine("Neutral curve points: " + tuning.NeutralTaxRateByPopulation.PointCount + "; modifier curve points: " + tuning.TaxModifierByRateVariance.PointCount + "; penalty days: " + tuning.TaxPenaltyDays + ".");
            Console.WriteLine("Wrote " + outputPath + " and provenance " + provenancePath + ".");
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
    }

    private static ExemplarProperty RequireUniqueProperty(ExemplarRecord exemplar, uint propertyId)
    {
        ExemplarProperty? match = null;
        foreach (ExemplarProperty property in exemplar.properties)
        {
            if (property.id != propertyId) continue;
            if (match is not null) throw new InvalidDataException("Tuning exemplar has duplicate property 0x" + propertyId.ToString("X8") + ".");
            match = property;
        }

        return match ?? throw new InvalidDataException("Tuning exemplar is missing required property 0x" + propertyId.ToString("X8") + ".");
    }

    private static float[] RequireFloatArray(ExemplarProperty property, string label)
    {
        if (property.formatName != "Float32" || !property.isRepeated || property.values is null || property.unsupportedReason is not null)
            throw new InvalidDataException(label + " must be a supported repeated Float32 property.");
        if (property.encodedCount != property.values.Length || property.values.Length < 2 || property.values.Length > TaxResponseCurve.MaximumValues)
            throw new InvalidDataException(label + " has an invalid value count.");

        float[] values = new float[property.values.Length];
        for (int index = 0; index < property.values.Length; index++)
        {
            if (property.values[index] is not float value || float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidDataException(label + " contains a non-finite or non-Float32 value.");
            values[index] = value;
        }

        return values;
    }

    private static uint RequireUInt32Scalar(ExemplarProperty property, string label)
    {
        if (property.formatName != "UInt32" || property.isRepeated || property.value is not uint value || property.unsupportedReason is not null)
            throw new InvalidDataException(label + " must be a supported scalar UInt32 property.");
        return value;
    }

    private static object PropertyProvenance(ExemplarProperty property, int? pointCount)
    {
        return new
        {
            propertyId = "0x" + property.id.ToString("X8"),
            typeCode = "0x" + property.typeCode.ToString("X8"),
            format = property.formatName,
            property.isRepeated,
            encodedCount = property.encodedCount,
            pointCount,
            scalarValue = property.value
        };
    }

    private static Dictionary<string, string?>? ParseArguments(string[] args)
    {
        Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            if (option is "--help" or "-h")
            {
                PrintUsage();
                return null;
            }
            if (option is not ("--root" or "--archive" or "--out"))
                throw new ArgumentException("Unknown option '" + option + "'. Use --help for usage.");
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Option " + option + " requires a value.");
            if (!options.TryAdd(option, args[++index]))
                throw new ArgumentException("Option " + option + " was supplied more than once.");
        }

        return options;
    }

    private static string? GetValue(Dictionary<string, string?> options, string name)
    {
        return options.TryGetValue(name, out string? value) ? value : null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: SC4TaxProfile [--root <SC4Evolution root>] [--archive <DAT name>] [--out <path under analysis/generated/sc4-tax-profile>]");
        Console.WriteLine("Reads only the installed demand-tuning exemplar and writes a bounded S4TD profile plus provenance JSON.");
    }
}
