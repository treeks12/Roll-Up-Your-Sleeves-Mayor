using System.Buffers.Binary;
using System.Text;

namespace SC4Data;

public static class ExemplarDecoder
{
    public const uint TypeId = 0x6534284A;
    public const uint ParentMarker = 0x23232331;
    public const int MaximumPropertyCount = 65_535;
    public const int MaximumValuesPerProperty = 1_000_000;
    public const int MaximumStringBytes = 1_048_576;

    private const uint FormatMask = 0x0000000F;
    private const uint RepetitionMask = 0x0000F000;
    private const uint SupportedTypeMask = FormatMask | RepetitionMask;
    private const uint NamePropertyId = 0x00000020;
    private const uint TypePropertyId = 0x00000010;

    public static ExemplarRecord Decode(ReadOnlySpan<byte> payload)
    {
        var reader = new ExemplarReader(payload);
        var signature = reader.ReadSignature();
        if (signature is not ("EQZB" or "CQZB"))
        {
            throw new UnsupportedExemplarFormatException($"Unsupported Exemplar/Cohort signature '{signature}'.");
        }

        var parentMarker = reader.ReadUInt32("parent cohort marker");
        if (parentMarker != ParentMarker)
        {
            throw new UnsupportedExemplarFormatException($"Unsupported {signature} parent marker 0x{parentMarker:X8}; expected 0x{ParentMarker:X8}.");
        }

        var parentCohort = new Tgi(
            reader.ReadUInt32("parent cohort type"),
            reader.ReadUInt32("parent cohort group"),
            reader.ReadUInt32("parent cohort instance"));
        var propertyCount = reader.ReadUInt32("property count");
        if (propertyCount > MaximumPropertyCount)
        {
            throw new InvalidDataException($"{signature} declares {propertyCount} properties; limit is {MaximumPropertyCount}.");
        }

        var properties = new List<ExemplarProperty>((int)propertyCount);
        var unsupportedVariants = new List<string>();
        for (var propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
        {
            var propertyId = reader.ReadUInt32($"property {propertyIndex} ID");
            var padding = reader.ReadByte($"property {propertyIndex} reserved byte");
            if (padding != 0)
            {
                throw new UnsupportedExemplarFormatException($"Property 0x{propertyId:X8} uses unsupported reserved byte 0x{padding:X2}.");
            }

            var typeCode = reader.ReadUInt32($"property 0x{propertyId:X8} type code");
            if ((typeCode & ~SupportedTypeMask) != 0)
            {
                throw new UnsupportedExemplarFormatException($"Property 0x{propertyId:X8} uses unsupported type-code bits 0x{typeCode & ~SupportedTypeMask:X8} (type 0x{typeCode:X8}).");
            }

            var formatCode = (byte)(typeCode & FormatMask);
            var repetitionTag = (ushort)(typeCode & RepetitionMask);
            var isRepeated = repetitionTag != 0;
            if (formatCode == 0xC)
            {
                if (!isRepeated)
                {
                    throw new UnsupportedExemplarFormatException($"Property 0x{propertyId:X8} uses an unsupported scalar string type 0x{typeCode:X8}.");
                }

                var byteLength = reader.ReadUInt32($"property 0x{propertyId:X8} string byte length");
                if (byteLength > MaximumStringBytes)
                {
                    throw new InvalidDataException($"Property 0x{propertyId:X8} string has {byteLength} bytes; limit is {MaximumStringBytes}.");
                }

                var rawString = reader.ReadBytes(checked((int)byteLength), $"property 0x{propertyId:X8} string");
                string? unsupportedStringEncoding = null;
                for (var index = 0; index < rawString.Length; index++)
                {
                    if (rawString[index] > 0x7F)
                    {
                        unsupportedStringEncoding = $"Property 0x{propertyId:X8} contains non-ASCII string byte 0x{rawString[index]:X2} at relative offset {index}; string encoding is unsupported.";
                        break;
                    }
                }

                if (unsupportedStringEncoding is not null)
                {
                    unsupportedVariants.Add(unsupportedStringEncoding);
                }

                properties.Add(new ExemplarProperty(
                    propertyId,
                    typeCode,
                    formatCode,
                    repetitionTag,
                    isRepeated,
                    byteLength,
                    "String",
                    unsupportedStringEncoding is null ? Encoding.ASCII.GetString(rawString) : null,
                    null,
                    unsupportedStringEncoding,
                    unsupportedStringEncoding is null ? null : Convert.ToBase64String(rawString)));
                continue;
            }

            var (formatName, width) = GetNumericFormat(propertyId, typeCode, formatCode);
            if (isRepeated)
            {
                var count = reader.ReadUInt32($"property 0x{propertyId:X8} repetition count");
                if (count > MaximumValuesPerProperty)
                {
                    throw new InvalidDataException($"Property 0x{propertyId:X8} declares {count} values; limit is {MaximumValuesPerProperty}.");
                }

                var requiredBytes = checked((ulong)count * (uint)width);
                reader.EnsureRemaining(requiredBytes, $"property 0x{propertyId:X8} values");
                var values = new object[(int)count];
                for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
                {
                    values[valueIndex] = reader.ReadNumeric(formatCode, $"property 0x{propertyId:X8} value {valueIndex}");
                }

                properties.Add(new ExemplarProperty(propertyId, typeCode, formatCode, repetitionTag, true, count, formatName, null, values, null, null));
            }
            else
            {
                properties.Add(new ExemplarProperty(
                    propertyId,
                    typeCode,
                    formatCode,
                    repetitionTag,
                    false,
                    null,
                    formatName,
                    reader.ReadNumeric(formatCode, $"property 0x{propertyId:X8} value"),
                    null,
                    null,
                    null));
            }
        }

        if (!reader.AtEnd)
        {
            throw new InvalidDataException($"{signature} has {reader.Remaining} trailing bytes after its declared {propertyCount} properties.");
        }

        return new ExemplarRecord(signature, parentMarker, parentCohort, properties, unsupportedVariants);
    }

    private static (string Name, int Width) GetNumericFormat(uint propertyId, uint typeCode, byte formatCode) => formatCode switch
    {
        0x1 => ("UInt8", 1),
        0x2 => ("UInt16", 2),
        0x3 => ("UInt32", 4),
        0x4 => ("UInt64", 8),
        0x5 => ("Int8", 1),
        0x6 => ("Int16", 2),
        0x7 => ("Int32", 4),
        0x8 => ("Int64", 8),
        0x9 => ("Float32", 4),
        0xA => ("Float64", 8),
        0xB => ("Boolean", 1),
        _ => throw new UnsupportedExemplarFormatException($"Property 0x{propertyId:X8} uses unsupported format code 0x{formatCode:X} (type 0x{typeCode:X8}).")
    };

    private ref struct ExemplarReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public ExemplarReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
        }

        public bool AtEnd => _offset == _data.Length;
        public int Remaining => _data.Length - _offset;

        public string ReadSignature()
        {
            var bytes = ReadBytes(4, "signature");
            return Encoding.ASCII.GetString(bytes);
        }

        public byte ReadByte(string field)
        {
            EnsureRemaining(1, field);
            return _data[_offset++];
        }

        public uint ReadUInt32(string field)
        {
            EnsureRemaining(4, field);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_offset, 4));
            _offset += 4;
            return value;
        }

        public byte[] ReadBytes(int count, string field)
        {
            if (count < 0)
            {
                throw new InvalidDataException($"{field} has a negative byte count.");
            }

            EnsureRemaining((ulong)count, field);
            var result = _data.Slice(_offset, count).ToArray();
            _offset += count;
            return result;
        }

        public object ReadNumeric(byte formatCode, string field)
        {
            var width = formatCode switch
            {
                0x1 or 0x5 or 0xB => 1,
                0x2 or 0x6 => 2,
                0x3 or 0x7 or 0x9 => 4,
                0x4 or 0x8 or 0xA => 8,
                _ => throw new InvalidOperationException("Numeric format was not validated.")
            };
            EnsureRemaining((ulong)width, field);
            var valueBytes = _data.Slice(_offset, width);
            _offset += width;
            return formatCode switch
            {
                0x1 => valueBytes[0],
                0x2 => BinaryPrimitives.ReadUInt16LittleEndian(valueBytes),
                0x3 => BinaryPrimitives.ReadUInt32LittleEndian(valueBytes),
                0x4 => BinaryPrimitives.ReadUInt64LittleEndian(valueBytes),
                0x5 => unchecked((sbyte)valueBytes[0]),
                0x6 => BinaryPrimitives.ReadInt16LittleEndian(valueBytes),
                0x7 => BinaryPrimitives.ReadInt32LittleEndian(valueBytes),
                0x8 => BinaryPrimitives.ReadInt64LittleEndian(valueBytes),
                0x9 => BinaryPrimitives.ReadSingleLittleEndian(valueBytes),
                0xA => BinaryPrimitives.ReadDoubleLittleEndian(valueBytes),
                0xB => valueBytes[0] != 0,
                _ => throw new InvalidOperationException("Numeric format was not validated.")
            };
        }

        public void EnsureRemaining(ulong count, string field)
        {
            if (count > (ulong)Remaining)
            {
                throw new InvalidDataException($"Unexpected end of Exemplar data while reading {field}: need {count} bytes, have {Remaining}.");
            }
        }
    }
}

public sealed record ExemplarProperty(
    uint id,
    uint typeCode,
    byte formatCode,
    ushort repetitionTag,
    bool isRepeated,
    uint? encodedCount,
    string formatName,
    object? value,
    object[]? values,
    string? unsupportedReason,
    string? rawBytesBase64);

public sealed record ExemplarRecord(
    string signature,
    uint parentMarker,
    Tgi parentCohort,
    IReadOnlyList<ExemplarProperty> properties,
    IReadOnlyList<string> unsupportedVariants)
{
    public string? Name
    {
        get
        {
            var matches = properties.Where(property => property.id == 0x20 && property.formatCode == 0xC && property.value is string).Take(2).ToArray();
            return matches.Length == 1 ? (string)matches[0].value! : null;
        }
    }

    public object? Type
    {
        get
        {
            var matches = properties.Where(property => property.id == 0x10 && !property.isRepeated && property.formatCode is >= 1 and <= 0xA && property.value is not null).Take(2).ToArray();
            return matches.Length == 1 ? matches[0].value : null;
        }
    }
}

public sealed class UnsupportedExemplarFormatException(string message) : Exception(message);
