using System.Globalization;

namespace SC4Data;

public readonly record struct Tgi(uint Type, uint Group, uint Instance)
{
    public static Tgi Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var fields = value.Split(':');
        if (fields.Length != 3)
        {
            throw new FormatException("TGI must be TYPE:GROUP:INSTANCE, with each field as up to eight hexadecimal digits.");
        }

        return new Tgi(ParseHex(fields[0]), ParseHex(fields[1]), ParseHex(fields[2]));
    }

    public override string ToString() => $"{Type:X8}:{Group:X8}:{Instance:X8}";

    private static uint ParseHex(string value)
    {
        var digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (digits.Length is < 1 or > 8 || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new FormatException($"Invalid 32-bit hexadecimal TGI field '{value}'.");
        }

        return parsed;
    }
}
