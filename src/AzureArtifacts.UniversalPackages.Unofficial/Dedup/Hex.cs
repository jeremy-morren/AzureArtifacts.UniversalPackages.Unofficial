namespace AzureArtifacts.UniversalPackages.Unofficial.Dedup;

/// <summary>Upper-case hexadecimal (netstandard2.0 has no Convert.ToHexString)</summary>
internal static class Hex
{
    /// <summary>The characters of one byte</summary>
    public const int CharsPerByte = 2;

    /// <summary>The number base</summary>
    public const int Base = 16;

    /// <summary>The format of one byte</summary>
    public const string ByteFormat = "X2";

    private const string Digits = "0123456789ABCDEF";
    private const int BitsPerDigit = 4;
    private const int DigitMask = 0x0F;

    public static string Encode(byte[] bytes, int offset, int count)
    {
        var chars = new char[count * CharsPerByte];
        for (var i = 0; i < count; i++)
        {
            var b = bytes[offset + i];
            chars[i * CharsPerByte] = Digits[b >> BitsPerDigit];
            chars[i * CharsPerByte + 1] = Digits[b & DigitMask];
        }
        return new string(chars);
    }

    public static bool IsHex(string value) =>
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');
}
