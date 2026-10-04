using System.Net;
using System.Security.Cryptography;

namespace CommPanel.Voice;

/// <summary>
/// Just enough of RFC 5389 to ask a public server "what address do you see me coming from?".
///
/// This is a binding request and its reply - twenty bytes out, one attribute back. A whole
/// STUN library would be several megabytes to answer one question, and CommPanel has no
/// third-party dependencies to spend.
///
/// Deliberately socket-free: the caller sends these through the very socket the voice link
/// uses, because a router's port mapping belongs to one socket. Discovering some other
/// socket's mapping would hand the peer an address that nothing is listening on.
/// </summary>
internal static class StunClient
{
    /// <summary>Public servers that answer binding requests. Used only to learn our own address.</summary>
    public static readonly (string Host, int Port)[] Servers =
    {
        ("stun.l.google.com", 19302),
        ("stun1.l.google.com", 19302),
        ("stun.cloudflare.com", 3478)
    };

    private const int HeaderLength = 20;
    private const uint MagicCookie = 0x2112A442;

    public static byte[] BuildRequest(out byte[] transactionId)
    {
        var request = new byte[HeaderLength];
        request[0] = 0x00; request[1] = 0x01;   // Binding Request
        request[2] = 0x00; request[3] = 0x00;   // body length

        request[4] = 0x21; request[5] = 0x12;   // magic cookie
        request[6] = 0xA4; request[7] = 0x42;

        transactionId = new byte[12];
        RandomNumberGenerator.Fill(transactionId);
        transactionId.CopyTo(request, 8);

        return request;
    }

    /// <summary>True when a datagram looks like STUN rather than voice, so it can be routed.</summary>
    public static bool LooksLikeStun(byte[] buffer, int length) =>
        length >= HeaderLength &&
        (buffer[0] & 0xC0) == 0 &&
        buffer[4] == 0x21 && buffer[5] == 0x12 && buffer[6] == 0xA4 && buffer[7] == 0x42;

    /// <summary>
    /// Reads the mapped address out of a binding success response, or null if this is not
    /// one, is not ours, or carries no address we understand.
    /// </summary>
    public static IPEndPoint? ParseResponse(byte[] buffer, int length, byte[] expectedTransactionId)
    {
        if (length < HeaderLength) return null;
        if (buffer[0] != 0x01 || buffer[1] != 0x01) return null; // Binding Success Response

        for (int i = 0; i < 12; i++)
            if (buffer[8 + i] != expectedTransactionId[i]) return null;

        int bodyLength = (buffer[2] << 8) | buffer[3];
        int offset = HeaderLength;
        int end = Math.Min(length, HeaderLength + bodyLength);

        while (offset + 4 <= end)
        {
            int type = (buffer[offset] << 8) | buffer[offset + 1];
            int attributeLength = (buffer[offset + 2] << 8) | buffer[offset + 3];
            int value = offset + 4;
            if (value + attributeLength > end) break;

            const int xorMappedAddress = 0x0020;
            const int mappedAddress = 0x0001;

            if ((type == xorMappedAddress || type == mappedAddress) &&
                attributeLength >= 8 &&
                buffer[value + 1] == 0x01) // IPv4
            {
                int port = (buffer[value + 2] << 8) | buffer[value + 3];
                var octets = new byte[4];
                Array.Copy(buffer, value + 4, octets, 0, 4);

                if (type == xorMappedAddress)
                {
                    port ^= (int)(MagicCookie >> 16);
                    octets[0] ^= 0x21; octets[1] ^= 0x12; octets[2] ^= 0xA4; octets[3] ^= 0x42;
                }

                return new IPEndPoint(new IPAddress(octets), port);
            }

            offset = value + ((attributeLength + 3) & ~3); // attributes are 4-byte aligned
        }

        return null;
    }
}
