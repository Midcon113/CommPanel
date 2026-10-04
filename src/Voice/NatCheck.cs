using System.Net;
using System.Net.Sockets;

namespace CommPanel.Voice;

internal enum NatVerdict
{
    /// <summary>Nothing answered: UDP is not getting out at all.</summary>
    Blocked,

    /// <summary>No NAT in the way. Direct calls work.</summary>
    Open,

    /// <summary>One stable mapping for every destination. Direct calls work.</summary>
    Stable,

    /// <summary>A different mapping per destination. Direct calls will not connect.</summary>
    PerDestination,

    /// <summary>Answers disagreed in a way that does not fit the usual patterns.</summary>
    Unclear
}

/// <summary>
/// What a connection check found. Written to be read out over the telephone: the headline is
/// the answer, the detail says what to do about it.
/// </summary>
internal sealed class NatReport
{
    public required NatVerdict Verdict { get; init; }
    public required string Headline { get; init; }
    public required string Detail { get; init; }

    /// <summary>The address the outside world sees, when one was found.</summary>
    public IPEndPoint? PublicEndPoint { get; init; }

    public int ServersAnswered { get; init; }
    public int ServersTried { get; init; }

    /// <summary>
    /// The provider is sharing one public address between customers. Calls can still work,
    /// but this is the single most likely reason for one that never connects.
    /// </summary>
    public bool CarrierGrade { get; init; }

    public bool Good => Verdict is NatVerdict.Open or NatVerdict.Stable;

    /// <summary>One line to read down the phone to the other person.</summary>
    public string Summary =>
        string.Format("{0}{1}  ({2} of {3} servers answered)",
            Headline,
            CarrierGrade ? " — shared provider address" : string.Empty,
            ServersAnswered, ServersTried);
}

/// <summary>
/// Asks several public STUN servers what address they see us coming from, and works out
/// whether a direct call can get through this network.
///
/// The whole question is whether the router gives one stable door to the outside or a
/// different one per destination. Asking three unrelated servers from a single socket answers
/// that: the same address back from all of them means one door, and a direct call will work.
///
/// This runs on its own socket rather than the call's, so it can be used before a call and
/// cannot disturb one in progress. The behaviour it measures belongs to the router, not to
/// any particular socket.
/// </summary>
internal static class NatCheck
{
    /// <summary>Carrier-grade NAT space, RFC 6598. A public address here is not really yours.</summary>
    private static bool IsCarrierGrade(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    private static bool IsPrivate(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        if (b.Length != 4) return false;

        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || b[0] == 127;
    }

    /// <summary>
    /// Runs the check. Blocking, and takes a couple of seconds - callers run it off the UI
    /// thread.
    /// </summary>
    public static NatReport Run(CancellationToken cancel = default)
    {
        var servers = StunClient.Servers;
        var seen = new List<IPEndPoint>();

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.ReceiveTimeout = 1200;
        }
        catch (Exception ex)
        {
            return new NatReport
            {
                Verdict = NatVerdict.Blocked,
                Headline = "COULD NOT OPEN A NETWORK SOCKET",
                Detail = "Windows refused to open a UDP socket: " + ex.Message,
                ServersTried = servers.Length
            };
        }

        byte[] request = StunClient.BuildRequest(out byte[] transactionId);

        // One server at a time, so each reply can be attributed to the destination that
        // produced it. A router that hands out a different port per destination only shows
        // itself that way.
        foreach (var (host, port) in servers)
        {
            if (cancel.IsCancellationRequested) break;

            IPEndPoint? mapped = Ask(socket, request, transactionId, host, port);
            if (mapped is not null) seen.Add(mapped);
        }

        return Classify(seen, servers.Length);
    }

    private static IPEndPoint? Ask(Socket socket, byte[] request, byte[] transactionId,
                                   string host, int port)
    {
        try
        {
            var address = Dns.GetHostAddresses(host)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (address is null) return null;

            socket.SendTo(request, new IPEndPoint(address, port));

            var buffer = new byte[512];
            var deadline = DateTime.UtcNow.AddMilliseconds(1200);

            while (DateTime.UtcNow < deadline)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int read;

                try { read = socket.ReceiveFrom(buffer, ref from); }
                catch (SocketException) { return null; } // receive timeout

                if (read <= 0 || !StunClient.LooksLikeStun(buffer, read)) continue;

                var mapped = StunClient.ParseResponse(buffer, read, transactionId);
                if (mapped is not null) return mapped;
            }
        }
        catch
        {
            // DNS failure or an unreachable server is one missing answer, not a verdict.
        }

        return null;
    }

    private static NatReport Classify(List<IPEndPoint> seen, int tried)
    {
        if (seen.Count == 0)
        {
            return new NatReport
            {
                Verdict = NatVerdict.Blocked,
                Headline = "NO REPLY FROM ANY SERVER",
                Detail = "Nothing answered, which usually means UDP is being blocked - by a "
                       + "firewall, a security suite, or a company or school network. A direct "
                       + "call cannot get out of this network until that is lifted.",
                ServersAnswered = 0,
                ServersTried = tried
            };
        }

        var first = seen[0];
        bool carrierGrade = IsCarrierGrade(first.Address);

        bool sameAddress = seen.All(e => e.Address.Equals(first.Address));
        bool samePort = seen.All(e => e.Port == first.Port);

        if (sameAddress && samePort)
        {
            // No NAT at all: the address the world sees is one of ours.
            bool noNat = !IsPrivate(first.Address) && !carrierGrade && LocalAddresses().Contains(first.Address);

            string detail = noNat
                ? "This machine is on the internet directly, with no router translating "
                + "addresses. Direct calls will connect."
                : "Your router gives out one address and one port no matter who it is talking "
                + "to. That is what a direct call needs, so calls should connect.";

            if (carrierGrade)
            {
                detail += "\r\n\r\nOne caution: your provider is sharing a single public address "
                        + "between customers rather than giving you your own. Calls usually "
                        + "still work, but if one will not connect, this is the likely reason "
                        + "and only the provider can change it.";
            }

            return new NatReport
            {
                Verdict = noNat ? NatVerdict.Open : NatVerdict.Stable,
                Headline = "DIRECT CALLS SHOULD WORK",
                Detail = detail,
                PublicEndPoint = first,
                ServersAnswered = seen.Count,
                ServersTried = tried,
                CarrierGrade = carrierGrade
            };
        }

        if (sameAddress)
        {
            return new NatReport
            {
                Verdict = NatVerdict.PerDestination,
                Headline = "DIRECT CALLS WILL NOT CONNECT",
                Detail = "Your router opens a different port for every place you talk to, so "
                       + "there is no single address the other person can be told to send to. "
                       + "CommPanel cannot work around this without a relay server in the "
                       + "middle, which it does not have.\r\n\r\n"
                       + "Ports seen: " + string.Join(", ", seen.Select(e => e.Port)) + ".",
                PublicEndPoint = first,
                ServersAnswered = seen.Count,
                ServersTried = tried,
                CarrierGrade = carrierGrade
            };
        }

        return new NatReport
        {
            Verdict = NatVerdict.Unclear,
            Headline = "COULD NOT TELL",
            Detail = "The servers reported different public addresses for this machine, which "
                   + "usually means more than one internet connection is active - a VPN "
                   + "alongside ordinary networking, for instance. Turn the VPN off and run "
                   + "the check again.\r\n\r\n"
                   + "Addresses seen: " + string.Join(", ", seen.Select(e => e.Address)) + ".",
            PublicEndPoint = first,
            ServersAnswered = seen.Count,
            ServersTried = tried,
            CarrierGrade = carrierGrade
        };
    }

    private static List<IPAddress> LocalAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToList();
        }
        catch
        {
            return new List<IPAddress>();
        }
    }
}
