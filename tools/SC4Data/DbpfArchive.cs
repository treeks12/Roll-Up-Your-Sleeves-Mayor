using System.Buffers.Binary;
using System.Text;

namespace SC4Data;

public sealed class DbpfArchive : IDisposable
{
    public const int MaximumEntries = 1_000_000;
    public const int MaximumResourceBytes = 64 * 1024 * 1024;
    public static readonly Tgi DirectoryTgi = new(0xE86B1EEF, 0xE86B1EEF, 0x286B1F03);

    private readonly FileStream _stream;
    private readonly Dictionary<Tgi, List<DbpfEntry>> _entriesByTgi;
    private readonly Dictionary<Tgi, uint> _compressedEntries;
    private bool _disposed;

    private DbpfArchive(string path, FileStream stream, uint indexVersion, List<DbpfEntry> entries)
    {
        Path = path;
        _stream = stream;
        IndexVersion = indexVersion;
        Entries = entries;
        _entriesByTgi = entries.GroupBy(entry => entry.Tgi).ToDictionary(group => group.Key, group => group.ToList());
        _compressedEntries = LoadCompressionDirectory();
    }

    public string Path { get; }
    public uint IndexVersion { get; }
    public IReadOnlyList<DbpfEntry> Entries { get; }

    public static DbpfArchive Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            if (stream.Length < 96)
            {
                throw new InvalidDataException("DBPF header is shorter than 96 bytes.");
            }

            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            var magic = Encoding.ASCII.GetString(ReadExactly(reader, 4));
            if (!string.Equals(magic, "DBPF", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Expected DBPF magic, found '{magic}'.");
            }

            var majorVersion = reader.ReadUInt32();
            var minorVersion = reader.ReadUInt32();
            if (majorVersion != 1 || minorVersion != 0)
            {
                throw new InvalidDataException($"Unsupported DBPF version {majorVersion}.{minorVersion}; SC4 1.0 is required.");
            }

            stream.Position = 32;
            var indexTypeVersion = reader.ReadUInt32();
            var indexCount = reader.ReadUInt32();
            var indexOffset = reader.ReadUInt32();
            var indexSize = reader.ReadUInt32();
            var holeCount = reader.ReadUInt32();
            var holeOffset = reader.ReadUInt32();
            var holeSize = reader.ReadUInt32();
            var indexVersion = reader.ReadUInt32();

            if (indexTypeVersion != 7)
            {
                throw new InvalidDataException($"Unsupported DBPF index type version {indexTypeVersion}; expected 7.");
            }

            if (indexVersion is not (0 or 2))
            {
                throw new InvalidDataException($"Unsupported DBPF index version {indexVersion}; expected 0 or 2.");
            }

            if (indexCount > MaximumEntries)
            {
                throw new InvalidDataException($"DBPF index has {indexCount} entries; limit is {MaximumEntries}.");
            }

            var entrySize = indexVersion == 2 ? 24UL : 20UL;
            var requiredIndexBytes = checked((ulong)indexCount * entrySize);
            if (requiredIndexBytes > indexSize)
            {
                throw new InvalidDataException($"DBPF index is too small for {indexCount} entries: need {requiredIndexBytes}, declared {indexSize}.");
            }

            ValidateRange(indexOffset, indexSize, stream.Length, "DBPF index");
            if (holeCount > 0)
            {
                ValidateRange(holeOffset, holeSize, stream.Length, "DBPF hole record");
            }

            var entries = new List<DbpfEntry>((int)indexCount);
            stream.Position = indexOffset;
            for (var i = 0; i < indexCount; i++)
            {
                var type = reader.ReadUInt32();
                var group = reader.ReadUInt32();
                var instance = reader.ReadUInt32();
                uint? resourceId = indexVersion == 2 ? reader.ReadUInt32() : null;
                var offset = reader.ReadUInt32();
                var size = reader.ReadUInt32();

                ValidateRange(offset, size, stream.Length, $"DBPF resource {i} ({new Tgi(type, group, instance)})");
                if (size > 0 && offset < 96)
                {
                    throw new InvalidDataException($"DBPF resource {i} overlaps the 96-byte header.");
                }

                var indexEnd = (ulong)indexOffset + indexSize;
                var resourceEnd = (ulong)offset + size;
                if (size > 0 && offset < indexEnd && resourceEnd > indexOffset)
                {
                    throw new InvalidDataException($"DBPF resource {i} overlaps the declared index range.");
                }

                entries.Add(new DbpfEntry(new Tgi(type, group, instance), offset, size, resourceId));
            }

            return new DbpfArchive(System.IO.Path.GetFullPath(path), stream, indexVersion, entries);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public bool IsCompressed(DbpfEntry entry) => _compressedEntries.ContainsKey(entry.Tgi);

    public uint? DecodedSize(DbpfEntry entry) => _compressedEntries.TryGetValue(entry.Tgi, out var size) ? size : null;

    public DbpfEntry FindUnique(Tgi tgi)
    {
        if (!_entriesByTgi.TryGetValue(tgi, out var matches))
        {
            throw new KeyNotFoundException($"No DBPF resource has TGI {tgi}.");
        }

        if (matches.Count != 1)
        {
            throw new InvalidDataException($"TGI {tgi} appears {matches.Count} times; selection is ambiguous.");
        }

        return matches[0];
    }

    public LoadedResource ReadResource(Tgi tgi)
    {
        var entry = FindUnique(tgi);
        if (entry.Size > MaximumResourceBytes)
        {
            throw new InvalidDataException($"Resource {tgi} has {entry.Size} stored bytes; per-resource limit is {MaximumResourceBytes}.");
        }

        var storedBytes = ReadEntryBytes(entry);
        if (_compressedEntries.TryGetValue(tgi, out var finalSize))
        {
            var data = QfsDecoder.DecodeSc4(storedBytes, entry.Size, finalSize, MaximumResourceBytes);
            return new LoadedResource(entry, true, data);
        }

        return new LoadedResource(entry, false, storedBytes);
    }

    public byte[] ReadEntryBytes(DbpfEntry entry)
    {
        ThrowIfDisposed();
        if (entry.Size > MaximumResourceBytes)
        {
            throw new InvalidDataException($"Resource {entry.Tgi} has {entry.Size} stored bytes; per-resource limit is {MaximumResourceBytes}.");
        }

        ValidateRange(entry.Offset, entry.Size, _stream.Length, $"DBPF resource {entry.Tgi}");
        var bytes = new byte[(int)entry.Size];
        _stream.Position = entry.Offset;
        _stream.ReadExactly(bytes);
        return bytes;
    }

    private Dictionary<Tgi, uint> LoadCompressionDirectory()
    {
        var map = new Dictionary<Tgi, uint>();
        if (!_entriesByTgi.TryGetValue(DirectoryTgi, out var directoryEntries))
        {
            return map;
        }

        if (directoryEntries.Count != 1)
        {
            throw new InvalidDataException($"Compression directory TGI {DirectoryTgi} appears {directoryEntries.Count} times.");
        }

        var directory = directoryEntries[0];
        if (directory.Size % 16 != 0)
        {
            throw new InvalidDataException($"Compression directory size {directory.Size} is not a multiple of 16 bytes.");
        }

        if (directory.Size > MaximumResourceBytes)
        {
            throw new InvalidDataException($"Compression directory size {directory.Size} exceeds the per-resource limit.");
        }

        var bytes = ReadEntryBytes(directory);
        using var memory = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(memory);
        while (memory.Position < memory.Length)
        {
            var tgi = new Tgi(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
            var finalSize = reader.ReadUInt32();
            if (tgi == DirectoryTgi)
            {
                throw new InvalidDataException("Compression directory lists itself as a compressed resource.");
            }

            if (!_entriesByTgi.TryGetValue(tgi, out var entries) || entries.Count != 1)
            {
                var detail = entries is null ? "does not exist in the archive" : $"appears {entries.Count} times in the archive";
                throw new InvalidDataException($"Compression directory TGI {tgi} {detail}.");
            }

            if (!map.TryAdd(tgi, finalSize))
            {
                throw new InvalidDataException($"Compression directory repeats TGI {tgi}.");
            }
        }

        return map;
    }

    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new EndOfStreamException("Unexpected end of DBPF header.");
        }

        return bytes;
    }

    private static void ValidateRange(uint offset, uint size, long fileLength, string description)
    {
        if (offset > fileLength || size > (ulong)fileLength - offset)
        {
            throw new InvalidDataException($"{description} range (offset {offset}, size {size}) extends past file length {fileLength}.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _stream.Dispose();
        _disposed = true;
    }
}

public readonly record struct DbpfEntry(Tgi Tgi, uint Offset, uint Size, uint? ResourceId);

public sealed record LoadedResource(DbpfEntry Entry, bool IsCompressed, byte[] Data);
