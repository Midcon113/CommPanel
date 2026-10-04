using System.Net;
using System.Net.Sockets;

namespace CommPanel.Voice;

internal enum VoiceLinkState
{
    /// <summary>Not calling anyone.</summary>
    Idle,

    /// <summary>Sending to the peer, nothing heard back yet.</summary>
    Calling,

    /// <summary>Packets arriving from the peer.</summary>
    Connected
}

/// <summary>
/// The voice link's transport: one UDP socket, a packet format, hole punching, and a jitter
/// buffer. It knows nothing about audio devices - it moves 20 ms frames of 16-bit samples
/// between two machines and can be exercised end to end without a microphone in the room.
///
/// Audio goes straight from one machine to the other. Nothing relays it, so nobody else can
/// hear it and there is no server to pay for. The cost of that choice is NAT: two home
/// routers will only pass a direct link if both ends start talking at roughly the same time,
/// which is what the repeated hello packets below are for.
/// </summary>
internal sealed class VoiceLink : IDisposable
{
    private const byte TypeHello = 0;
    private const byte TypeAudio = 1;
    private const byte TypeBye = 2;
    private const byte TypeReport = 3;

    private const int HeaderLength = 11;
    private static readonly byte[] Magic = "CPV1"u8.ToArray();

    /// <summary>How often each end tells the other what it can hear and play.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(1000);

    /// <summary>Hellos double as keepalive and as the hole-punching knock.</summary>
    private static readonly TimeSpan HelloInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>Silence from the peer for this long means the call has dropped.</summary>
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Frames held before playback starts. Three frames is 60 ms of slack - enough to ride
    /// out ordinary jitter, little enough that nobody notices the delay.
    /// </summary>
    private const int PrebufferFrames = 3;

    /// <summary>Beyond this the buffer is stale latency rather than resilience.</summary>
    private const int MaxBufferedFrames = 12;

    private readonly Socket _socket;
    private readonly uint _linkId;
    private readonly Queue<short[]> _received = new();
    private readonly object _bufferGate = new();
    private readonly byte[] _sendBuffer = new byte[HeaderLength + VoiceFormat.BytesPerFrame];
    private readonly object _sendGate = new();

    private Thread? _thread;
    private volatile bool _stopping;
    private volatile int _state;
    private IPEndPoint? _peer;
    private byte[]? _stunTransactionId;

    private ushort _sequence;
    private long _lastPeerTicks;
    private DateTime _lastHello = DateTime.MinValue;
    private DateTime _lastReport = DateTime.MinValue;
    private float _incomingPeak;
    private long _audioFramesReceived;
    private bool _prebuffering = true;

    public VoiceLink(int listenPort = 0, string? linkCode = null)
    {
        _linkId = LinkIdFrom(linkCode);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Any, listenPort));
        _socket.ReceiveTimeout = 200;

        // A router that drops our mapping mid-call would otherwise strand us; a modest
        // buffer also stops a burst from being lost while the UI thread is busy painting.
        _socket.ReceiveBufferSize = 1 << 17;

        LocalPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "CommPanel voice link",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    /// <summary>The port we are listening on, which is what the peer must send to.</summary>
    public int LocalPort { get; }

    /// <summary>Our address as the outside world sees it, once STUN has answered.</summary>
    public IPEndPoint? PublicEndPoint { get; private set; }

    public VoiceLinkState State => (VoiceLinkState)_state;

    public IPEndPoint? Peer => _peer;

    public int PacketsSent { get; private set; }
    public int PacketsReceived { get; private set; }
    public int FramesDropped { get; private set; }

    public int BufferedFrames
    {
        get { lock (_bufferGate) return _received.Count; }
    }

    /// <summary>
    /// What this end wants the far end to know about it. Set by the session once a second;
    /// the link puts it on the wire so each side can see the other's half of the call.
    /// </summary>
    public LinkReport LocalReport { get; set; }

    /// <summary>The far end's last report, and when it arrived.</summary>
    public LinkReport? PeerReport { get; private set; }

    public DateTime PeerReportAt { get; private set; } = DateTime.MinValue;

    /// <summary>Audio frames carrying real signal, counted since the call started.</summary>
    public long AudioFramesReceived => Interlocked.Read(ref _audioFramesReceived);

    /// <summary>
    /// Loudest sample that has arrived from the peer since the last read. Read by the health
    /// monitor: signal arriving here while nothing comes out of the speakers is what tells
    /// one-way audio apart from the other person simply being quiet.
    /// </summary>
    public float TakeIncomingPeak()
    {
        float peak = _incomingPeak;
        _incomingPeak = 0f;
        return peak;
    }

    /// <summary>Raised on the link's own thread whenever the state changes.</summary>
    public event Action<VoiceLinkState>? StateChanged;

    /// <summary>Raised on the link's own thread when STUN reports our public address.</summary>
    public event Action<IPEndPoint>? PublicEndPointDiscovered;

    /// <summary>Starts calling a peer. Both ends should do this at about the same time.</summary>
    public void Call(IPEndPoint peer)
    {
        _peer = peer;
        _lastPeerTicks = 0;
        _lastHello = DateTime.MinValue;

        lock (_bufferGate)
        {
            _received.Clear();
            _prebuffering = true;
        }

        SetState(VoiceLinkState.Calling);
    }

    public void HangUp()
    {
        var peer = _peer;
        if (peer is not null && State == VoiceLinkState.Connected)
        {
            try { SendControl(TypeBye, peer); } catch { /* going away anyway */ }
        }

        _peer = null;
        SetState(VoiceLinkState.Idle);

        lock (_bufferGate)
        {
            _received.Clear();
            _prebuffering = true;
        }
    }

    /// <summary>Asks the public STUN servers for our address. Replies arrive on the link thread.</summary>
    public void DiscoverPublicEndPoint()
    {
        byte[] request = StunClient.BuildRequest(out byte[] transactionId);
        _stunTransactionId = transactionId;

        foreach (var (host, port) in StunClient.Servers)
        {
            try
            {
                var address = Dns.GetHostAddresses(host)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (address is null) continue;

                _socket.SendTo(request, new IPEndPoint(address, port));
            }
            catch
            {
                // One unreachable server is not a failure; the others usually answer.
            }
        }
    }

    /// <summary>Sends one 20 ms frame. Safe to call from the capture thread.</summary>
    public void SendVoice(short[] frame)
    {
        var peer = _peer;
        if (peer is null || frame is null || frame.Length != VoiceFormat.SamplesPerFrame) return;

        lock (_sendGate)
        {
            WriteHeader(_sendBuffer, TypeAudio, ++_sequence);

            int offset = HeaderLength;
            foreach (short sample in frame)
            {
                _sendBuffer[offset++] = (byte)(sample & 0xFF);
                _sendBuffer[offset++] = (byte)((sample >> 8) & 0xFF);
            }

            try
            {
                _socket.SendTo(_sendBuffer, peer);
                PacketsSent++;
            }
            catch (SocketException)
            {
                // A transient send failure is not worth tearing the call down for; the peer
                // timeout will catch a genuine outage.
            }
        }
    }

    /// <summary>
    /// Takes the next frame for playback, or null when there is nothing to play.
    ///
    /// Returns null while the buffer refills after an underrun. Playing the moment a single
    /// frame arrives just underruns again on the next packet, which sounds far worse than
    /// 60 ms of silence at the start of a sentence.
    /// </summary>
    public short[]? TakeVoice()
    {
        lock (_bufferGate)
        {
            if (_prebuffering)
            {
                if (_received.Count < PrebufferFrames) return null;
                _prebuffering = false;
            }

            if (_received.Count == 0)
            {
                _prebuffering = true;
                return null;
            }

            return _received.Dequeue();
        }
    }

    private void Run()
    {
        var buffer = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);

        while (!_stopping)
        {
            try
            {
                int read = _socket.ReceiveFrom(buffer, ref from);
                if (read > 0) Handle(buffer, read, (IPEndPoint)from);
            }
            catch (SocketException)
            {
                // Receive timeout: the expected way round this loop when nobody is talking.
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Maintain();
        }
    }

    /// <summary>Sends keepalives and notices when the peer has gone quiet.</summary>
    private void Maintain()
    {
        var peer = _peer;
        if (peer is null) return;

        var now = DateTime.UtcNow;
        if (now - _lastHello >= HelloInterval)
        {
            _lastHello = now;
            try { SendControl(TypeHello, peer); } catch { /* retried next tick */ }
        }

        // Only worth sending once there is a call to describe.
        if (State == VoiceLinkState.Connected && now - _lastReport >= ReportInterval)
        {
            _lastReport = now;
            try { SendReport(peer); } catch { /* retried next tick */ }
        }

        if (State == VoiceLinkState.Connected)
        {
            long last = Interlocked.Read(ref _lastPeerTicks);
            if (last != 0 && DateTime.UtcNow.Ticks - last > PeerTimeout.Ticks)
            {
                lock (_bufferGate)
                {
                    _received.Clear();
                    _prebuffering = true;
                }
                SetState(VoiceLinkState.Calling);
            }
        }
    }

    private void Handle(byte[] buffer, int length, IPEndPoint from)
    {
        if (StunClient.LooksLikeStun(buffer, length))
        {
            var transactionId = _stunTransactionId;
            if (transactionId is null) return;

            var mapped = StunClient.ParseResponse(buffer, length, transactionId);
            if (mapped is null || PublicEndPoint is not null) return;

            PublicEndPoint = mapped;
            PublicEndPointDiscovered?.Invoke(mapped);
            return;
        }

        if (length < HeaderLength) return;
        for (int i = 0; i < Magic.Length; i++)
            if (buffer[i] != Magic[i]) return;

        uint linkId = (uint)(buffer[7] | (buffer[8] << 8) | (buffer[9] << 16) | (buffer[10] << 24));
        if (linkId != _linkId) return;

        if (!IsAcceptable(from)) return;

        // Adopt the address we actually heard from. A router can map the peer to a different
        // port than the one they advertised, and the address that reaches us is by definition
        // the one that works.
        _peer = from;
        Interlocked.Exchange(ref _lastPeerTicks, DateTime.UtcNow.Ticks);
        PacketsReceived++;

        byte type = buffer[4];

        if (type == TypeBye)
        {
            HangUp();
            return;
        }

        if (State != VoiceLinkState.Connected) SetState(VoiceLinkState.Connected);

        if (type == TypeReport)
        {
            if (length >= HeaderLength + 4)
            {
                PeerReport = LinkReport.Decode(buffer, HeaderLength);
                PeerReportAt = DateTime.UtcNow;
            }
            return;
        }

        if (type != TypeAudio) return;

        int payload = length - HeaderLength;
        if (payload < VoiceFormat.BytesPerFrame) return;

        var frame = new short[VoiceFormat.SamplesPerFrame];
        int offset = HeaderLength;
        for (int i = 0; i < frame.Length; i++)
        {
            frame[i] = (short)(buffer[offset] | (buffer[offset + 1] << 8));
            offset += 2;
        }

        float peak = 0f;
        foreach (short sample in frame)
        {
            float magnitude = Math.Abs(sample / 32768f);
            if (magnitude > peak) peak = magnitude;
        }

        if (peak > _incomingPeak) _incomingPeak = peak;
        if (peak > VoiceFormat.SignalFloor) Interlocked.Increment(ref _audioFramesReceived);

        lock (_bufferGate)
        {
            _received.Enqueue(frame);

            // Drop the oldest rather than the newest: stale audio is of no use to anyone,
            // and keeping it would only add permanent delay.
            while (_received.Count > MaxBufferedFrames)
            {
                _received.Dequeue();
                FramesDropped++;
            }
        }
    }

    /// <summary>
    /// Decides whether a packet is allowed to reach us.
    ///
    /// Once connected, only the peer may speak. Before that, a packet is accepted from the
    /// address we are calling - or, when a link code is set, from anyone who knows it, which
    /// is what lets the call connect even if the peer's router remapped their port.
    /// </summary>
    private bool IsAcceptable(IPEndPoint from)
    {
        var peer = _peer;

        if (State == VoiceLinkState.Connected)
            return peer is not null && from.Equals(peer);

        if (peer is not null && from.Equals(peer)) return true;

        return _linkId != 0 && peer is not null && from.Address.Equals(peer.Address);
    }

    private void SendReport(IPEndPoint peer)
    {
        lock (_sendGate)
        {
            var packet = new byte[HeaderLength + 4];
            WriteHeader(packet, TypeReport, ++_sequence);
            LocalReport.Encode(packet, HeaderLength);
            _socket.SendTo(packet, peer);
            PacketsSent++;
        }
    }

    private void SendControl(byte type, IPEndPoint peer)
    {
        lock (_sendGate)
        {
            var packet = new byte[HeaderLength];
            WriteHeader(packet, type, ++_sequence);
            _socket.SendTo(packet, peer);
            PacketsSent++;
        }
    }

    private void WriteHeader(byte[] packet, byte type, ushort sequence)
    {
        Magic.CopyTo(packet, 0);
        packet[4] = type;
        packet[5] = (byte)(sequence & 0xFF);
        packet[6] = (byte)((sequence >> 8) & 0xFF);
        packet[7] = (byte)(_linkId & 0xFF);
        packet[8] = (byte)((_linkId >> 8) & 0xFF);
        packet[9] = (byte)((_linkId >> 16) & 0xFF);
        packet[10] = (byte)((_linkId >> 24) & 0xFF);
    }

    private void SetState(VoiceLinkState state)
    {
        if ((VoiceLinkState)_state == state) return;
        _state = (int)state;
        StateChanged?.Invoke(state);
    }

    /// <summary>
    /// Turns a spoken-aloud link code into a 32-bit tag carried by every packet. Not secrecy -
    /// it is a filter, so a stray or probing datagram cannot inject audio into a call.
    /// </summary>
    private static uint LinkIdFrom(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return 0;

        uint hash = 2166136261;
        foreach (char c in code.Trim().ToUpperInvariant())
        {
            hash ^= c;
            hash *= 16777619;
        }

        return hash == 0 ? 1 : hash;
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;

        try { HangUp(); } catch { /* shutting down */ }

        try { _socket.Close(); } catch { }
        try { _thread?.Join(500); } catch { }

        _socket.Dispose();
        _thread = null;
    }
}
