using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    private const int MaxImageBytes = 512 * 1024 * 1024;
    private const int MaxImports = 512;
    private const int MaxSymbolsPerImport = 2048;
    private const int MaxExports = 4096;
    private const int MaxStrings = 500;
    private const int MaxStringLength = 512;

    private static readonly string[] SearchTerms =
    [
        "simulat", "advisor", "budget", "demand", "traffic", "interface", "city", "population",
        "commercial", "residential", "industrial", "tax", "crime", "pollution", "school", "health",
        "water", "power", "electric", "transport", "commute", "road", "service", "municipal", "job",
        "workplace", "education", "garbage", "sewage", "income", "business", "happiness"
    ];

    private static readonly Regex KeywordRegex = new(
        string.Join("|", SearchTerms.Select(Regex.Escape)), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static int Main(string[] args)
    {
        try
        {
            RunSyntheticBoundaryChecks();
            var root = ParseRoot(args);
            var executable = ResolveExecutable(root);
            var report = Inspect(executable);
            WriteOutputs(root, report);
            Console.WriteLine($"Wrote {Path.Combine(root, "analysis", "sc4-native-inventory.md")}");
            Console.WriteLine($"Machine: {report.Pe.Machine} ({report.Pe.Bitness}); sections: {report.Pe.Sections.Count}; imported modules: {report.Imports.Modules.Count}; matched strings: {report.Strings.Matches.Count}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SC4 native inventory failed: {exception.Message}");
            return 1;
        }
    }

    private static string ParseRoot(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--root" && i + 1 < args.Length)
                return Path.GetFullPath(args[i + 1]);
        }
        throw new ArgumentException("Pass --root <project-root>.");
    }

    private static string ResolveExecutable(string root)
    {
        var configPath = Path.Combine(root, "config", "local.paths.json");
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        if (!document.RootElement.TryGetProperty("SimCity4", out var pathElement) || pathElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("config/local.paths.json must contain a string SimCity4 path.");

        var gameRoot = pathElement.GetString();
        if (string.IsNullOrWhiteSpace(gameRoot) || !Path.IsPathRooted(gameRoot))
            throw new InvalidDataException("The configured SimCity4 path must be absolute.");

        var fullGameRoot = Path.GetFullPath(gameRoot);
        if (!Directory.Exists(fullGameRoot))
            throw new DirectoryNotFoundException($"Configured SimCity 4 directory does not exist: {fullGameRoot}");

        var executable = Path.GetFullPath(Path.Combine(fullGameRoot, "Apps", "SimCity 4.exe"));
        if (!File.Exists(executable))
            throw new FileNotFoundException("Installed SimCity 4 executable was not found.", executable);
        return executable;
    }

    private static NativeReport Inspect(string executable)
    {
        var fileInfo = new FileInfo(executable);
        if (fileInfo.Length <= 0 || fileInfo.Length > MaxImageBytes)
            throw new InvalidDataException($"Executable size {fileInfo.Length} is outside the supported 1..{MaxImageBytes} byte bound.");

        var image = File.ReadAllBytes(executable);
        using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var headers = peReader.PEHeaders;
        var peHeader = headers.PEHeader ?? throw new BadImageFormatException("PE optional header is missing.");
        var coffHeader = headers.CoffHeader;
        var issues = new List<string>();
        var sections = headers.SectionHeaders.Select(section => new PeSection(
            section.Name,
            unchecked((uint)section.VirtualAddress),
            unchecked((uint)section.VirtualSize),
            unchecked((uint)section.PointerToRawData),
            unchecked((uint)section.SizeOfRawData),
            unchecked((uint)section.SectionCharacteristics))).ToList();

        var sectionReports = new List<SectionReport>();
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var virtualEnd = (ulong)section.VirtualAddress + Math.Max(section.VirtualSize, section.RawSize);
            var rawEnd = (ulong)section.RawPointer + section.RawSize;
            var rawRangeValid = rawEnd <= (ulong)image.LongLength;
            if (!rawRangeValid)
                issues.Add($"Section {section.Name} raw range ends at 0x{rawEnd:X}, past file size 0x{image.LongLength:X}.");

            sectionReports.Add(new SectionReport(
                i + 1, section.Name, Hex(section.VirtualAddress), section.VirtualSize,
                Hex(section.RawPointer), section.RawSize, Hex(virtualEnd), Hex(rawEnd),
                rawRangeValid, Hex(section.Characteristics), DescribeSectionFlags(section.Characteristics)));
        }

        var symbolPointer = coffHeader.PointerToSymbolTable;
        var symbolCount = coffHeader.NumberOfSymbols;
        var symbolBytes = symbolCount < 0 ? ulong.MaxValue : (ulong)symbolCount * 18UL;
        var symbolRangeValid = symbolPointer == 0 && symbolCount == 0 ||
            symbolPointer > 0 && symbolCount > 0 && (ulong)symbolPointer + symbolBytes <= (ulong)image.LongLength;
        if (!symbolRangeValid)
            issues.Add("COFF symbol-table pointer/count is nonzero but its 18-byte records extend beyond the file.");

        var pe = new PeReport(
            DescribeMachine(unchecked((ushort)coffHeader.Machine)), unchecked((ushort)coffHeader.Machine),
            DescribeBitness((ushort)peHeader.Magic), (ushort)peHeader.Magic,
            coffHeader.TimeDateStamp, coffHeader.NumberOfSections, coffHeader.Characteristics.ToString(),
            peHeader.AddressOfEntryPoint, peHeader.ImageBase, peHeader.SectionAlignment, peHeader.FileAlignment,
            peHeader.SizeOfImage, peHeader.SizeOfHeaders, peHeader.Subsystem.ToString(),
            symbolPointer, symbolCount, symbolRangeValid, sectionReports);

        var imports = ReadImports(image, sections, peHeader.ImportTableDirectory, peHeader.Magic, issues);
        var exports = ReadExports(image, sections, peHeader.ExportTableDirectory, issues);
        var debug = ReadDebugDirectory(peReader, image, issues);
        var fileVersion = FileVersionInfo.GetVersionInfo(executable);
        var strings = ExtractKeywordStrings(image);

        return new NativeReport(
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), executable, fileInfo.Length,
            Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            new FileVersionReport(fileVersion.FileVersion, fileVersion.ProductVersion, fileVersion.ProductName, fileVersion.CompanyName),
            pe, imports, exports, debug, strings, issues,
            "Read-only PE metadata and bounded string inventory. SimCity 4.exe was not executed or decompiled; strings are search clues, not reconstructed rules.");
    }

    private static ImportReport ReadImports(byte[] image, IReadOnlyList<PeSection> sections, DirectoryEntry directory, PEMagic magic, List<string> issues)
    {
        if (directory.RelativeVirtualAddress <= 0 || directory.Size <= 0)
            return new ImportReport(false, 0, 0, false, []);

        var directoryRva = unchecked((uint)directory.RelativeVirtualAddress);
        var directorySize = unchecked((uint)directory.Size);
        var descriptorCount = (int)Math.Min(directorySize / 20U, MaxImports);
        var truncated = directorySize / 20U > MaxImports;
        var modules = new List<ImportModuleReport>();
        var totalSymbols = 0;
        var terminated = false;

        for (var descriptorIndex = 0; descriptorIndex < descriptorCount; descriptorIndex++)
        {
            var descriptorDelta = (ulong)descriptorIndex * 20UL;
            if (descriptorDelta + 20UL > directorySize || !TryAddRva(directoryRva, descriptorDelta, out var descriptorRva) ||
                !TryMapRva(descriptorRva, 20, sections, image.LongLength, out var descriptorOffset, out _))
            {
                issues.Add($"Import descriptor {descriptorIndex} is outside a file-backed section or the declared directory.");
                break;
            }

            if (!TryReadUInt32(image, descriptorOffset, out var originalThunk) ||
                !TryReadUInt32(image, descriptorOffset + 4, out _) ||
                !TryReadUInt32(image, descriptorOffset + 8, out _) ||
                !TryReadUInt32(image, descriptorOffset + 12, out var moduleNameRva) ||
                !TryReadUInt32(image, descriptorOffset + 16, out var firstThunk))
            {
                issues.Add($"Import descriptor {descriptorIndex} is truncated.");
                break;
            }

            if (originalThunk == 0 && moduleNameRva == 0 && firstThunk == 0)
            {
                terminated = true;
                break;
            }

            if (moduleNameRva == 0 || !TryReadAsciiAtRva(image, sections, moduleNameRva, 256, out var moduleName))
            {
                issues.Add($"Import descriptor {descriptorIndex} has an invalid or unterminated module name.");
                continue;
            }

            var thunkRva = originalThunk != 0 ? originalThunk : firstThunk;
            var pointerSize = magic == PEMagic.PE32Plus ? 8 : 4;
            var symbols = new List<ImportSymbolReport>();
            var symbolTerminated = false;
            for (var symbolIndex = 0; symbolIndex < MaxSymbolsPerImport; symbolIndex++)
            {
                var thunkDelta = (ulong)symbolIndex * (uint)pointerSize;
                if (!TryAddRva(thunkRva, thunkDelta, out var entryRva) ||
                    !TryMapRva(entryRva, pointerSize, sections, image.LongLength, out var entryOffset, out _))
                {
                    issues.Add($"Import thunk for {moduleName} ends outside a file-backed section.");
                    break;
                }

                ulong thunkValue;
                if (pointerSize == 8)
                {
                    if (!TryReadUInt64(image, entryOffset, out thunkValue))
                    {
                        issues.Add($"Import thunk for {moduleName} is truncated.");
                        break;
                    }
                }
                else
                {
                    if (!TryReadUInt32(image, entryOffset, out var value32))
                    {
                        issues.Add($"Import thunk for {moduleName} is truncated.");
                        break;
                    }
                    thunkValue = value32;
                }

                if (thunkValue == 0)
                {
                    symbolTerminated = true;
                    break;
                }

                var ordinalFlag = pointerSize == 8 ? 0x8000000000000000UL : 0x80000000UL;
                if ((thunkValue & ordinalFlag) != 0)
                {
                    symbols.Add(new ImportSymbolReport(null, null, (int)(thunkValue & 0xFFFF), true));
                }
                else if (thunkValue <= uint.MaxValue)
                {
                    var importByNameRva = (uint)thunkValue;
                    if (!TryMapRva(importByNameRva, 2, sections, image.LongLength, out var importByNameOffset, out _) ||
                        !TryReadUInt16(image, importByNameOffset, out var hint) ||
                        !TryReadAsciiAtFileOffset(image, importByNameOffset + 2, 1024, out var symbolName))
                    {
                        issues.Add($"Import-by-name entry for {moduleName} is invalid or unterminated.");
                        continue;
                    }
                    symbols.Add(new ImportSymbolReport(symbolName, hint, null, false));
                }
                else
                {
                    issues.Add($"Import thunk for {moduleName} contains an unsupported non-RVA value.");
                }
            }

            if (!symbolTerminated)
            {
                truncated = true;
                issues.Add($"Import symbol list for {moduleName} reached the {MaxSymbolsPerImport} entry bound.");
            }
            totalSymbols += symbols.Count;
            modules.Add(new ImportModuleReport(moduleName, symbols, symbolTerminated));
        }

        if (!terminated && directorySize / 20U <= MaxImports)
            issues.Add("Import descriptor table has no all-zero terminator within its declared size.");

        return new ImportReport(true, directory.RelativeVirtualAddress, directory.Size, truncated, modules, totalSymbols);
    }

    private static ExportReport ReadExports(byte[] image, IReadOnlyList<PeSection> sections, DirectoryEntry directory, List<string> issues)
    {
        if (directory.RelativeVirtualAddress <= 0 || directory.Size <= 0)
            return new ExportReport(false, 0, 0, null, 0, 0, false, []);

        var directoryRva = unchecked((uint)directory.RelativeVirtualAddress);
        if (directory.Size < 40 || !TryMapRva(directoryRva, 40, sections, image.LongLength, out var directoryOffset, out _))
        {
            issues.Add("Export directory is smaller than 40 bytes or outside a file-backed section.");
            return new ExportReport(true, directory.RelativeVirtualAddress, directory.Size, null, 0, 0, false, []);
        }

        TryReadUInt32(image, directoryOffset + 12, out var nameRva);
        TryReadUInt32(image, directoryOffset + 16, out var ordinalBase);
        TryReadUInt32(image, directoryOffset + 20, out var functionCount);
        TryReadUInt32(image, directoryOffset + 24, out var nameCount);
        TryReadUInt32(image, directoryOffset + 28, out var functionsRva);
        TryReadUInt32(image, directoryOffset + 32, out var namesRva);
        TryReadUInt32(image, directoryOffset + 36, out var ordinalsRva);

        string? moduleName = null;
        if (nameRva != 0 && !TryReadAsciiAtRva(image, sections, nameRva, 256, out moduleName))
            issues.Add("Export module name RVA is invalid or unterminated.");

        var entries = new List<ExportEntryReport>();
        var truncated = nameCount > MaxExports;
        var boundedNameCount = Math.Min(nameCount, MaxExports);
        for (uint i = 0; i < boundedNameCount; i++)
        {
            var nameDelta = (ulong)i * 4UL;
            var ordinalDelta = (ulong)i * 2UL;
            if (!TryAddRva(namesRva, nameDelta, out var nameEntryRva) ||
                !TryMapRva(nameEntryRva, 4, sections, image.LongLength, out var nameEntryOffset, out _) ||
                !TryReadUInt32(image, nameEntryOffset, out var exportedNameRva) ||
                !TryAddRva(ordinalsRva, ordinalDelta, out var ordinalEntryRva) ||
                !TryMapRva(ordinalEntryRva, 2, sections, image.LongLength, out var ordinalEntryOffset, out _) ||
                !TryReadUInt16(image, ordinalEntryOffset, out var ordinalIndex))
            {
                issues.Add($"Export name/ordinal entry {i} is out of bounds.");
                break;
            }

            if (ordinalIndex >= functionCount)
            {
                issues.Add($"Export name entry {i} references function index {ordinalIndex}, beyond count {functionCount}.");
                continue;
            }

            if (!TryReadAsciiAtRva(image, sections, exportedNameRva, 1024, out var exportedName))
            {
                issues.Add($"Export name entry {i} is invalid or unterminated.");
                continue;
            }

            var functionDelta = (ulong)ordinalIndex * 4UL;
            if (!TryAddRva(functionsRva, functionDelta, out var functionEntryRva) ||
                !TryMapRva(functionEntryRva, 4, sections, image.LongLength, out var functionEntryOffset, out _) ||
                !TryReadUInt32(image, functionEntryOffset, out var functionRva))
            {
                issues.Add($"Export function entry {ordinalIndex} is out of bounds.");
                continue;
            }

            entries.Add(new ExportEntryReport(exportedName, (ulong)ordinalBase + ordinalIndex, Hex(functionRva)));
        }

        return new ExportReport(true, directory.RelativeVirtualAddress, directory.Size, moduleName, functionCount, nameCount, truncated, entries);
    }

    private static List<DebugEntryReport> ReadDebugDirectory(PEReader peReader, byte[] image, List<string> issues)
    {
        var entries = new List<DebugEntryReport>();
        try
        {
            foreach (var entry in peReader.ReadDebugDirectory())
            {
                var dataPointer = entry.DataPointer;
                var dataSize = entry.DataSize;
                var dataRangeValid = dataPointer >= 0 && dataSize >= 0 && IsRangeWithin(image.LongLength, dataPointer, dataSize);
                string? pdbName = null;
                string? guid = null;
                int? age = null;
                if (!dataRangeValid)
                {
                    issues.Add($"Debug directory data range at 0x{dataPointer:X} with size {dataSize} exceeds the file.");
                }
                else if (entry.Type == DebugDirectoryEntryType.CodeView)
                {
                    try
                    {
                        var codeView = peReader.ReadCodeViewDebugDirectoryData(entry);
                        pdbName = Path.GetFileName(codeView.Path);
                        guid = codeView.Guid.ToString("D");
                        age = codeView.Age;
                    }
                    catch (Exception exception) when (exception is BadImageFormatException or ArgumentException or InvalidOperationException)
                    {
                        issues.Add($"Could not read CodeView debug data: {exception.Message}");
                    }
                }

                entries.Add(new DebugEntryReport(
                    entry.Type.ToString(), unchecked((int)entry.Stamp), entry.MajorVersion, entry.MinorVersion,
                    entry.DataRelativeVirtualAddress, dataPointer, dataSize, dataRangeValid, pdbName, guid, age));
            }
        }
        catch (Exception exception) when (exception is BadImageFormatException or ArgumentException or InvalidOperationException)
        {
            issues.Add($"Could not read PE debug directory: {exception.Message}");
        }
        return entries;
    }

    private static StringReport ExtractKeywordStrings(byte[] image)
    {
        var matches = new List<StringMatchReport>();
        var keywordCounts = SearchTerms.ToDictionary(term => term, _ => 0, StringComparer.OrdinalIgnoreCase);
        var candidateCount = 0;
        var truncated = false;

        for (var i = 0; i < image.Length;)
        {
            if (!IsPrintableAscii(image[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < image.Length && IsPrintableAscii(image[i]))
                i++;
            var length = i - start;
            if (length >= 4 && length <= MaxStringLength)
            {
                var text = Encoding.ASCII.GetString(image, start, length);
                AddStringMatch(matches, keywordCounts, ref candidateCount, ref truncated, text, start, "ASCII");
            }
            if (i == start)
                i++;
        }

        for (var i = 0; i + 1 < image.Length;)
        {
            if (!IsPrintableAscii(image[i]) || image[i + 1] != 0)
            {
                i++;
                continue;
            }

            var start = i;
            var builder = new StringBuilder();
            var characterCount = 0;
            while (i + 1 < image.Length && IsPrintableAscii(image[i]) && image[i + 1] == 0)
            {
                if (characterCount < MaxStringLength)
                    builder.Append((char)image[i]);
                characterCount++;
                i += 2;
            }
            if (characterCount >= 4 && characterCount <= MaxStringLength)
                AddStringMatch(matches, keywordCounts, ref candidateCount, ref truncated, builder.ToString(), start, "UTF-16LE");
            if (i == start)
                i++;
        }

        return new StringReport(image.LongLength, candidateCount, matches, truncated, SearchTerms, keywordCounts);
    }

    private static void AddStringMatch(List<StringMatchReport> matches, Dictionary<string, int> keywordCounts, ref int candidateCount, ref bool truncated, string text, long offset, string encoding)
    {
        if (!KeywordRegex.IsMatch(text))
            return;
        candidateCount++;
        foreach (var term in SearchTerms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                keywordCounts[term]++;
        }
        if (matches.Count >= MaxStrings)
        {
            truncated = true;
            return;
        }
        matches.Add(new StringMatchReport(Hex((ulong)offset), encoding, text));
    }

    private static bool TryReadAsciiAtRva(byte[] image, IReadOnlyList<PeSection> sections, uint rva, int maxLength, out string value)
    {
        value = string.Empty;
        if (!TryMapRva(rva, 1, sections, image.LongLength, out var offset, out var available))
            return false;
        return TryReadAsciiAtFileOffset(image, offset, Math.Min(maxLength, available), out value);
    }

    private static bool TryReadAsciiAtFileOffset(byte[] image, int offset, int maxLength, out string value)
    {
        value = string.Empty;
        if (offset < 0 || offset >= image.Length || maxLength <= 0)
            return false;

        var end = offset + (int)Math.Min((long)maxLength, image.LongLength - offset);
        var builder = new StringBuilder();
        for (var i = offset; i < end; i++)
        {
            var current = image[i];
            if (current == 0)
            {
                value = builder.ToString();
                return true;
            }
            if (!IsPrintableAscii(current))
                return false;
            builder.Append((char)current);
        }
        return false;
    }

    private static bool TryReadUInt16(byte[] image, int offset, out ushort value)
    {
        value = 0;
        if (!IsRangeWithin(image.LongLength, offset, 2))
            return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset, 2));
        return true;
    }

    private static bool TryReadUInt32(byte[] image, int offset, out uint value)
    {
        value = 0;
        if (!IsRangeWithin(image.LongLength, offset, 4))
            return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));
        return true;
    }

    private static bool TryReadUInt64(byte[] image, int offset, out ulong value)
    {
        value = 0;
        if (!IsRangeWithin(image.LongLength, offset, 8))
            return false;
        value = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(offset, 8));
        return true;
    }

    private static bool TryMapRva(uint rva, int requiredSize, IReadOnlyList<PeSection> sections, long fileLength, out int fileOffset, out int available)
    {
        fileOffset = 0;
        available = 0;
        if (requiredSize < 0)
            return false;

        foreach (var section in sections)
        {
            var start = (ulong)section.VirtualAddress;
            var span = Math.Max((ulong)section.VirtualSize, section.RawSize);
            var target = (ulong)rva;
            if (target < start || target - start >= span)
                continue;

            var delta = target - start;
            if (delta > section.RawSize || (ulong)requiredSize > section.RawSize - delta)
                return false;
            var rawOffset = (ulong)section.RawPointer + delta;
            if (rawOffset > int.MaxValue || rawOffset + (ulong)requiredSize > (ulong)fileLength)
                return false;

            fileOffset = (int)rawOffset;
            available = (int)Math.Min((ulong)(int.MaxValue - fileOffset), section.RawSize - delta);
            available = (int)Math.Min((ulong)available, (ulong)(fileLength - fileOffset));
            return true;
        }
        return false;
    }

    private static bool TryAddRva(uint rva, ulong delta, out uint result)
    {
        var sum = (ulong)rva + delta;
        if (sum > uint.MaxValue)
        {
            result = 0;
            return false;
        }
        result = (uint)sum;
        return true;
    }

    private static bool IsRangeWithin(long totalLength, long offset, long size) =>
        offset >= 0 && size >= 0 && offset <= totalLength && size <= totalLength - offset;

    private static bool IsPrintableAscii(byte value) => value is >= 0x20 and <= 0x7E;

    private static string DescribeMachine(ushort machine) => machine switch
    {
        0x014C => "x86 (I386)",
        0x8664 => "x64 (AMD64)",
        0x01C0 => "ARM",
        0x01C4 => "ARMv7",
        0xAA64 => "ARM64",
        0x0200 => "IA64",
        _ => "Unknown"
    };

    private static string DescribeBitness(ushort magic) => magic switch
    {
        0x010B => "32-bit PE32",
        0x020B => "64-bit PE32+",
        _ => "Unknown optional-header format"
    };

    private static string DescribeSectionFlags(uint flags)
    {
        var names = new List<string>();
        if ((flags & 0x00000020) != 0) names.Add("code");
        if ((flags & 0x00000040) != 0) names.Add("initialized-data");
        if ((flags & 0x00000080) != 0) names.Add("uninitialized-data");
        if ((flags & 0x20000000) != 0) names.Add("execute");
        if ((flags & 0x40000000) != 0) names.Add("read");
        if ((flags & 0x80000000) != 0) names.Add("write");
        return string.Join(", ", names);
    }

    private static string Hex(uint value) => $"0x{value:X8}";
    private static string Hex(ulong value) => $"0x{value:X}";

    private static void RunSyntheticBoundaryChecks()
    {
        var section = new PeSection("synthetic", 0x1000, 0x100, 0x200, 0x80, 0);
        if (!TryMapRva(0x1004, 4, [section], 0x400, out var mapped, out _) || mapped != 0x204)
            throw new InvalidOperationException("Synthetic RVA mapping check failed.");
        if (TryMapRva(0x107F, 2, [section], 0x400, out _, out _))
            throw new InvalidOperationException("Synthetic raw-section end check failed.");
        if (TryMapRva(0x1080, 1, [section], 0x400, out _, out _))
            throw new InvalidOperationException("Synthetic virtual-only range check failed.");
        if (TryMapRva(0x1000, 2, [section with { RawPointer = 0x3FF }], 0x400, out _, out _))
            throw new InvalidOperationException("Synthetic file end check failed.");

        var stringBytes = Encoding.ASCII.GetBytes("budget\0");
        if (!TryReadAsciiAtFileOffset(stringBytes, 0, stringBytes.Length, out var text) || text != "budget")
            throw new InvalidOperationException("Synthetic null-terminated string check failed.");
        if (TryReadAsciiAtFileOffset(Encoding.ASCII.GetBytes("budget"), 0, 6, out _))
            throw new InvalidOperationException("Synthetic unterminated string check failed.");
        if (TryReadUInt32([0x01, 0x02, 0x03], 0, out _))
            throw new InvalidOperationException("Synthetic truncated integer check failed.");
    }

    private static void WriteOutputs(string root, NativeReport report)
    {
        var generatedDirectory = Path.Combine(root, "analysis", "generated", "sc4-native");
        Directory.CreateDirectory(generatedDirectory);
        var jsonPath = Path.Combine(generatedDirectory, "sc4-native-inventory.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));

        var markdownPath = Path.Combine(root, "analysis", "sc4-native-inventory.md");
        Directory.CreateDirectory(Path.GetDirectoryName(markdownPath)!);
        File.WriteAllText(markdownPath, RenderMarkdown(report), new UTF8Encoding(false));
    }

    private static string RenderMarkdown(NativeReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# SimCity 4 native executable inventory");
        text.AppendLine();
        text.AppendLine($"Generated: {report.GeneratedUtc}");
        text.AppendLine($"Executable: `{report.ExecutablePath}` ({report.SizeBytes:N0} bytes)");
        text.AppendLine($"SHA-256: `{report.Sha256}`");
        text.AppendLine($"File version: `{report.FileVersion.FileVersion ?? "unavailable"}`; product version: `{report.FileVersion.ProductVersion ?? "unavailable"}`");
        text.AppendLine();
        text.AppendLine("## PE headers");
        text.AppendLine();
        text.AppendLine($"- Machine: {report.Pe.Machine} (`0x{report.Pe.MachineCode:X4}`); format: {report.Pe.Bitness} (`0x{report.Pe.OptionalHeaderMagic:X4}`)");
        text.AppendLine($"- Sections: {report.Pe.Sections.Count}; COFF timestamp: `{report.Pe.CoffTimestamp}`; subsystem: {report.Pe.Subsystem}");
        text.AppendLine($"- Entry point RVA: `{Hex((uint)report.Pe.EntryPointRva)}`; image base: `{Hex(report.Pe.ImageBase)}`; image size: {report.Pe.SizeOfImage:N0} bytes");
        text.AppendLine($"- COFF symbol table: pointer `{report.Pe.SymbolTablePointer}`; records `{report.Pe.SymbolCount}`; in-file range valid: {report.Pe.SymbolTableRangeValid}");
        text.AppendLine();
        text.AppendLine("| # | Section | RVA | Virtual bytes | Raw pointer | Raw bytes | Raw range valid | Flags |");
        text.AppendLine("|---:|---|---:|---:|---:|---:|:---:|---|");
        foreach (var section in report.Pe.Sections)
            text.AppendLine($"| {section.Index} | `{EscapeCell(section.Name)}` | `{section.VirtualAddress}` | {section.VirtualSize:N0} | `{section.RawPointer}` | {section.RawSize:N0} | {section.RawRangeValid} | {EscapeCell(section.Flags)} |");

        text.AppendLine();
        text.AppendLine("## Debug directory and symbols");
        text.AppendLine();
        if (report.DebugDirectory.Count == 0)
            text.AppendLine("No debug-directory entries were found.");
        else
        {
            text.AppendLine("| Type | Data RVA | Data pointer | Data bytes | Range valid | PDB name | GUID | Age |");
            text.AppendLine("|---|---:|---:|---:|:---:|---|---|---:|");
            foreach (var entry in report.DebugDirectory)
                text.AppendLine($"| {entry.Type} | `{entry.DataRva}` | `{entry.DataPointer}` | {entry.DataSize} | {entry.DataRangeValid} | {EscapeCell(entry.PdbName ?? "")} | `{entry.Guid ?? ""}` | {entry.Age?.ToString() ?? ""} |");
        }

        text.AppendLine();
        text.AppendLine("## Imports and exports");
        text.AppendLine();
        text.AppendLine($"Imports: {report.Imports.Modules.Count} module(s), {report.Imports.TotalSymbols} symbol(s); table truncated: {report.Imports.Truncated}.");
        if (report.Imports.Modules.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("| Module | Imported symbols | Examples |");
            text.AppendLine("|---|---:|---|");
            foreach (var module in report.Imports.Modules)
            {
                var names = module.Symbols.Take(12).Select(symbol => symbol.ByOrdinal ? $"ordinal {symbol.Ordinal}" : symbol.Name ?? "(unnamed)");
                var examples = string.Join(", ", names);
                if (module.Symbols.Count > 12) examples += ", …";
                text.AppendLine($"| `{EscapeCell(module.Name)}` | {module.Symbols.Count} | {EscapeCell(examples)} |");
            }
        }
        text.AppendLine();
        text.AppendLine($"Exports: directory present {report.Exports.Present}; function slots {report.Exports.FunctionCount}; named exports {report.Exports.NameCount}; table truncated {report.Exports.Truncated}.");
        if (report.Exports.NamedEntries.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("| Name | Ordinal | RVA |");
            text.AppendLine("|---|---:|---:|");
            foreach (var entry in report.Exports.NamedEntries)
                text.AppendLine($"| `{EscapeCell(entry.Name)}` | {entry.Ordinal} | `{entry.Rva}` |");
        }

        text.AppendLine();
        text.AppendLine("## Bounded keyword strings");
        text.AppendLine();
        var keywordSummary = string.Join(", ", report.Strings.KeywordMatchCounts.Select(pair => $"{pair.Key}: {pair.Value:N0}"));
        text.AppendLine($"Scanned {report.Strings.BytesScanned:N0} bytes and found {report.Strings.CandidateCount:N0} matching candidates. Stored {report.Strings.Matches.Count} (collection truncated: {report.Strings.Truncated}). Candidate counts by term: {keywordSummary}.");
        text.AppendLine();
        text.AppendLine("| File offset | Encoding | Candidate string |");
        text.AppendLine("|---:|---|---|");
        foreach (var match in report.Strings.Matches)
            text.AppendLine($"| `{match.Offset}` | {match.Encoding} | `{EscapeCell(match.Text)}` |");

        if (report.Issues.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Bounds and parse notes");
            text.AppendLine();
            foreach (var issue in report.Issues)
                text.AppendLine($"- {issue}");
        }

        text.AppendLine();
        text.AppendLine(report.MethodNote);
        return text.ToString();
    }

    private static string EscapeCell(string value) => value.Replace("|", "\\|").Replace("`", "'").Replace("\r", " ").Replace("\n", " ");

    private sealed record PeSection(string Name, uint VirtualAddress, uint VirtualSize, uint RawPointer, uint RawSize, uint Characteristics);
    private sealed record FileVersionReport(string? FileVersion, string? ProductVersion, string? ProductName, string? CompanyName);
    private sealed record SectionReport(int Index, string Name, string VirtualAddress, uint VirtualSize, string RawPointer, uint RawSize, string VirtualEndExclusive, string RawEndExclusive, bool RawRangeValid, string Characteristics, string Flags);
    private sealed record PeReport(string Machine, ushort MachineCode, string Bitness, ushort OptionalHeaderMagic, int CoffTimestamp, int SectionCount, string CoffCharacteristics, int EntryPointRva, ulong ImageBase, int SectionAlignment, int FileAlignment, int SizeOfImage, int SizeOfHeaders, string Subsystem, int SymbolTablePointer, int SymbolCount, bool SymbolTableRangeValid, IReadOnlyList<SectionReport> Sections);
    private sealed record ImportSymbolReport(string? Name, ushort? Hint, int? Ordinal, bool ByOrdinal);
    private sealed record ImportModuleReport(string Name, IReadOnlyList<ImportSymbolReport> Symbols, bool Terminated);
    private sealed record ImportReport(bool Present, int DirectoryRva, int DirectorySize, bool Truncated, IReadOnlyList<ImportModuleReport> Modules, int TotalSymbols = 0);
    private sealed record ExportEntryReport(string Name, ulong Ordinal, string Rva);
    private sealed record ExportReport(bool Present, int DirectoryRva, int DirectorySize, string? ModuleName, uint FunctionCount, uint NameCount, bool Truncated, IReadOnlyList<ExportEntryReport> NamedEntries);
    private sealed record DebugEntryReport(string Type, int TimeDateStamp, ushort MajorVersion, ushort MinorVersion, int DataRva, int DataPointer, int DataSize, bool DataRangeValid, string? PdbName, string? Guid, int? Age);
    private sealed record StringMatchReport(string Offset, string Encoding, string Text);
    private sealed record StringReport(long BytesScanned, int CandidateCount, IReadOnlyList<StringMatchReport> Matches, bool Truncated, IReadOnlyList<string> SearchTerms, IReadOnlyDictionary<string, int> KeywordMatchCounts);
    private sealed record NativeReport(string GeneratedUtc, string ExecutablePath, long SizeBytes, string Sha256, FileVersionReport FileVersion, PeReport Pe, ImportReport Imports, ExportReport Exports, IReadOnlyList<DebugEntryReport> DebugDirectory, StringReport Strings, IReadOnlyList<string> Issues, string MethodNote);
}
