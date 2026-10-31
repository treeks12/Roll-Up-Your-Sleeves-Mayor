using System.Text;

namespace SC4Data;

public static class LTextDecoder
{
    public const uint TypeId = 0x2026960B;
    private static readonly UnicodeEncoding Utf16LittleEndian = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Decode(ReadOnlySpan<byte> payload) => DecodeWithInfo(payload).Text;

    public static ushort? ReadControlFieldIfPresent(ReadOnlySpan<byte> payload) =>
        payload.Length < 4 ? null : (ushort)((payload[2] << 8) | payload[3]);

    public static LTextInfo DecodeWithInfo(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            throw new InvalidDataException("LTEXT resource is shorter than its count and control fields.");
        }

        var count = (ushort)(payload[0] | (payload[1] << 8));
        var control = ReadControlFieldIfPresent(payload)!.Value;

        var textBytes = checked(count * 2);
        if (payload.Length - 4 < textBytes)
        {
            throw new InvalidDataException($"LTEXT declares {count} UTF-16 code units but only {(payload.Length - 4) / 2} are present.");
        }

        return new LTextInfo(count, control, Utf16LittleEndian.GetString(payload.Slice(4, textBytes)));
    }

    public static byte[] EncodeUtf8(ReadOnlySpan<byte> payload) => Utf8WithoutBom.GetBytes(Decode(payload));
}

public sealed record LTextInfo(ushort CodeUnitCount, ushort Control, string Text);
