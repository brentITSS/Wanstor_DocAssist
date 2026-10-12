namespace DocumentTextFunction;

public static class ContentDecoder
{
    public static byte[] Decode(string value)
    {
        if (value.Contains('%', StringComparison.Ordinal))
        {
            try { value = Uri.UnescapeDataString(value); }
            catch (UriFormatException) { }
        }

        var comma = value.IndexOf(',');
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            value = value[(comma + 1)..];

        var buffer = new char[value.Length];
        var length = 0;
        foreach (var character in value)
        {
            var mapped = character switch
            {
                '-' or ' ' => '+',
                '_' => '/',
                '\\' or '\r' or '\n' or '\t' => '\0',
                _ => character
            };

            if (mapped is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/')
                buffer[length++] = mapped;
        }

        var remainder = length % 4;
        if (remainder == 1)
            throw new DocumentTextException("The file content is not valid base64.");

        var padded = remainder == 0 ? length : length + (4 - remainder);
        var decoded = new char[padded];
        Array.Copy(buffer, decoded, length);
        for (var i = length; i < padded; i++)
            decoded[i] = '=';

        try
        {
            return Convert.FromBase64String(new string(decoded));
        }
        catch (FormatException)
        {
            throw new DocumentTextException("The file content is not valid base64.");
        }
    }
}
