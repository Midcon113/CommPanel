using System.Net;

namespace CommPanel.Voice;

/// <summary>
/// Packs an address and port into a short code two people can read to each other.
///
/// The alternative is asking someone to type "73.162.14.201:47821" over the phone without
/// a mistake. Six bytes in Crockford base32 is ten characters, has no ambiguous glyphs
/// (no I, L, O or U), and is case-insensitive.
/// </summary>
internal static class VoiceAddress
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string ToCode(IPEndPoint endPoint)
    {
        byte[] address = endPoint.Address.MapToIPv4().GetAddressBytes();
        if (address.Length != 4) throw new ArgumentException("IPv4 only", nameof(endPoint));

        Span<byte> raw = stackalloc byte[6];
        address.CopyTo(raw);
        raw[4] = (byte)(endPoint.Port >> 8);
        raw[5] = (byte)(endPoint.Port & 0xFF);

        // 48 bits -> ten base32 characters, split for readability.
        ulong value = 0;
        foreach (byte b in raw) value = (value << 8) | b;

        Span<char> chars = stackalloc char[10];
        for (int i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return string.Concat(chars[..5], "-", chars[5..]);
    }

    public static bool TryParse(string? code, out IPEndPoint endPoint)
    {
        endPoint = new IPEndPoint(IPAddress.None, 0);
        if (string.IsNullOrWhiteSpace(code)) return false;

        // Accept a plain host:port too, since on a LAN that is easier than a code.
        string trimmed = code.Trim();
        if (trimmed.Contains(':') || trimmed.Contains('.'))
        {
            if (TryParseHostPort(trimmed, out endPoint)) return true;
        }

        string cleaned = new(trimmed.ToUpperInvariant()
            .Where(c => c != '-' && c != ' ')
            .Select(c => c switch { 'I' or 'L' => '1', 'O' => '0', 'U' => 'V', _ => c })
            .ToArray());

        if (cleaned.Length != 10) return false;

        ulong value = 0;
        foreach (char c in cleaned)
        {
            int index = Alphabet.IndexOf(c);
            if (index < 0) return false;
            value = (value << 5) | (uint)index;
        }

        var raw = new byte[6];
        for (int i = 5; i >= 0; i--)
        {
            raw[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        int port = (raw[4] << 8) | raw[5];
        if (port == 0) return false;

        endPoint = new IPEndPoint(new IPAddress(raw.AsSpan(0, 4).ToArray()), port);
        return true;
    }

    private static bool TryParseHostPort(string text, out IPEndPoint endPoint)
    {
        endPoint = new IPEndPoint(IPAddress.None, 0);

        int colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return false;

        string host = text[..colon];
        if (!int.TryParse(text[(colon + 1)..], out int port) || port is < 1 or > 65535) return false;

        if (IPAddress.TryParse(host, out var parsed))
        {
            endPoint = new IPEndPoint(parsed, port);
            return true;
        }

        try
        {
            var resolved = Dns.GetHostAddresses(host)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (resolved is null) return false;

            endPoint = new IPEndPoint(resolved, port);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
