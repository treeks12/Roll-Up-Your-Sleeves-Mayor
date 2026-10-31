using System.Buffers.Binary;

namespace SC4Data;

public static class QfsDecoder
{
    public static byte[] DecodeSc4(ReadOnlySpan<byte> storedData, uint indexedStoredSize, uint directoryDecodedSize, int maxOutputBytes)
    {
        if (maxOutputBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        }

        if (storedData.Length < 10)
        {
            throw new InvalidDataException("SC4 QFS resource is shorter than its 9-byte header and an end marker.");
        }

        if (indexedStoredSize != storedData.Length)
        {
            throw new InvalidDataException($"QFS input has {storedData.Length} bytes but index declares {indexedStoredSize}.");
        }

        // Installed SC4 data has a little-endian total stored-size prefix followed by 10 FB.
        var declaredStoredSize = BinaryPrimitives.ReadUInt32LittleEndian(storedData[..4]);
        if (declaredStoredSize != indexedStoredSize)
        {
            throw new InvalidDataException($"QFS prefix declares {declaredStoredSize} stored bytes, index declares {indexedStoredSize}.");
        }

        if (storedData[4] != 0x10 || storedData[5] != 0xFB)
        {
            throw new InvalidDataException($"Unsupported SC4 compression marker {storedData[4]:X2}{storedData[5]:X2}; expected 10FB.");
        }

        var decodedSize = (uint)((storedData[6] << 16) | (storedData[7] << 8) | storedData[8]);
        if (decodedSize != directoryDecodedSize)
        {
            throw new InvalidDataException($"QFS header declares {decodedSize} decoded bytes but DIR declares {directoryDecodedSize}.");
        }

        if (decodedSize > maxOutputBytes)
        {
            throw new InvalidDataException($"QFS output size {decodedSize} exceeds the configured limit {maxOutputBytes}.");
        }

        var output = new byte[(int)decodedSize];
        var inputPosition = 9;
        var outputPosition = 0;
        var sawEndMarker = false;

        while (inputPosition < storedData.Length)
        {
            var control = storedData[inputPosition++];
            if (control >= 0xFC)
            {
                var literalCount = control & 0x03;
                EnsureInput(storedData, inputPosition, literalCount, "QFS end-marker literals");
                EnsureOutput(output, outputPosition, literalCount, "QFS end-marker literals");
                storedData.Slice(inputPosition, literalCount).CopyTo(output.AsSpan(outputPosition));
                inputPosition += literalCount;
                outputPosition += literalCount;
                if (outputPosition != output.Length)
                {
                    throw new InvalidDataException($"QFS end marker produced {outputPosition} bytes; expected {output.Length}.");
                }

                if (inputPosition != storedData.Length)
                {
                    throw new InvalidDataException("QFS data has bytes after its end marker.");
                }

                sawEndMarker = true;
                break;
            }

            int plainCount;
            int copyCount;
            int distance;
            if (control < 0x80)
            {
                var second = ReadByte(storedData, ref inputPosition, "short back-reference");
                plainCount = control & 0x03;
                copyCount = ((control & 0x1C) >> 2) + 3;
                distance = (((control & 0x60) << 3) | second) + 1;
            }
            else if (control < 0xC0)
            {
                var second = ReadByte(storedData, ref inputPosition, "medium back-reference");
                var third = ReadByte(storedData, ref inputPosition, "medium back-reference");
                plainCount = (second & 0xC0) >> 6;
                copyCount = (control & 0x3F) + 4;
                distance = (((second & 0x3F) << 8) | third) + 1;
            }
            else if (control < 0xE0)
            {
                var second = ReadByte(storedData, ref inputPosition, "long back-reference");
                var third = ReadByte(storedData, ref inputPosition, "long back-reference");
                var fourth = ReadByte(storedData, ref inputPosition, "long back-reference");
                plainCount = control & 0x03;
                copyCount = (((control & 0x0C) << 6) | fourth) + 5;
                distance = (((control & 0x10) << 12) | (second << 8) | third) + 1;
            }
            else
            {
                plainCount = ((control & 0x1F) << 2) + 4;
                copyCount = 0;
                distance = 0;
            }

            EnsureInput(storedData, inputPosition, plainCount, "QFS literal run");
            EnsureOutput(output, outputPosition, plainCount, "QFS literal run");
            storedData.Slice(inputPosition, plainCount).CopyTo(output.AsSpan(outputPosition));
            inputPosition += plainCount;
            outputPosition += plainCount;

            if (copyCount == 0)
            {
                continue;
            }

            if (distance > outputPosition)
            {
                throw new InvalidDataException($"QFS back-reference distance {distance} exceeds {outputPosition} bytes already decoded.");
            }

            EnsureOutput(output, outputPosition, copyCount, "QFS back-reference");
            for (var i = 0; i < copyCount; i++)
            {
                output[outputPosition] = output[outputPosition - distance];
                outputPosition++;
            }
        }

        if (!sawEndMarker)
        {
            throw new InvalidDataException("QFS input ended without an end marker.");
        }

        return output;
    }

    private static byte ReadByte(ReadOnlySpan<byte> input, ref int position, string description)
    {
        if (position >= input.Length)
        {
            throw new InvalidDataException($"Truncated {description} control bytes.");
        }

        return input[position++];
    }

    private static void EnsureInput(ReadOnlySpan<byte> input, int position, int count, string description)
    {
        if (count < 0 || position < 0 || count > input.Length - position)
        {
            throw new InvalidDataException($"Truncated {description}.");
        }
    }

    private static void EnsureOutput(byte[] output, int position, int count, string description)
    {
        if (count < 0 || position < 0 || count > output.Length - position)
        {
            throw new InvalidDataException($"{description} exceeds the declared decoded size.");
        }
    }
}
