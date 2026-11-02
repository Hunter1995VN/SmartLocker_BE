namespace Infrastructure.Services;

public static class SupportInput
{
    public static bool IsJpeg(string? data, int maxBytes)
    {
        const string prefix = "data:image/jpeg;base64,";
        if (data == null || !data.StartsWith(prefix, StringComparison.Ordinal) || data.Length > prefix.Length + 4 * ((maxBytes + 2) / 3)) return false;
        try
        {
            var bytes = Convert.FromBase64String(data[prefix.Length..]);
            if (bytes.Length < 20 || bytes.Length > maxBytes || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[^2] != 0xff || bytes[^1] != 0xd9) return false;
            var hasDimensions = false;
            for (var i = 2; i + 3 < bytes.Length;)
            {
                if (bytes[i++] != 0xff) return false;
                while (i < bytes.Length && bytes[i] == 0xff) i++;
                if (i + 2 >= bytes.Length) return false;
                var marker = bytes[i++];
                var length = (bytes[i] << 8) | bytes[i + 1];
                if (length < 2 || i + length > bytes.Length) return false;
                if (marker is 0xc0 or 0xc1 or 0xc2)
                {
                    if (length < 8 || ((bytes[i + 3] << 8) | bytes[i + 4]) == 0 || ((bytes[i + 5] << 8) | bytes[i + 6]) == 0) return false;
                    hasDimensions = true;
                }
                if (marker == 0xda) return hasDimensions && i + length < bytes.Length - 2;
                i += length;
            }
            return false;
        }
        catch (FormatException) { return false; }
    }
}
