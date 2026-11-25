using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;

namespace CairnMultiplayerMod.Internal.Networking;

internal abstract class DirectTransportSession : INetworkTransportSession
{
    private readonly ConcurrentQueue<(ulong peerId, byte[] payload)> _appIncoming = new();
    private readonly object _membersLock = new();
    private readonly List<LobbyMember> _members = new();
    private bool _membersChanged;
    protected bool _active;
    protected string _lastError;
    protected MultiplayerMode _mode = MultiplayerMode.RopeTeam;
    protected ClimbWorld _world = ClimbWorld.Kami;

    protected readonly ConcurrentQueue<(ulong peerId, byte[] payload)> SessionIncoming = new();

    public bool IsActive => _active;
    public bool IsInLobby => _active;
    public bool IsHost { get; protected set; }
    public ulong LocalPeerId { get; set; }
    public abstract ulong HostPeerId { get; }
    public string DisconnectReason => _lastError ?? (_active ? "" : "Connection closed");
    public string CurrentRoomCode { get; protected set; } = "";
    public string CurrentLobbyName { get; protected set; } = "";
    public string LocalPersonaName { get; protected set; } = "Player";
    public int MaxMembers { get; protected set; } = 8;
    public MultiplayerModeRules CurrentModeRules => MultiplayerModes.RulesFor(_mode);
    public ClimbWorld CurrentWorld => _world;

    public IReadOnlyList<LobbyMember> Members
    {
        get { lock (_membersLock) return _members.ToArray(); }
    }

    public abstract void Send(ulong peerId, byte[] payload, bool reliable = true);
    public abstract void Broadcast(byte[] payload, bool reliable = true);
    public abstract void Pump();
    protected abstract void Close();

    protected void QueueForApp(ulong peerId, byte[] payload) => _appIncoming.Enqueue((peerId, payload));

    protected void QueueForSession(ulong peerId, byte[] payload) => SessionIncoming.Enqueue((peerId, payload));

    protected void SetMember(ulong peerId, string name, bool isHost, bool isSelf, bool worldUnavailable, int playerId = 0)
    {
        lock (_membersLock)
        {
            _members.RemoveAll(m => m.SteamId == peerId || (isSelf && m.IsSelf));
            _members.Add(new LobbyMember
            {
                SteamId = peerId,
                PlayerId = playerId != 0 ? playerId : NetworkManager.StablePeerPlayerId(peerId),
                Name = name,
                IsHost = isHost,
                IsSelf = isSelf,
                WorldUnavailable = worldUnavailable,
            });
        }
        _membersChanged = true;
    }

    protected void RemoveMemberByPeer(ulong peerId)
    {
        lock (_membersLock) _members.RemoveAll(m => m.SteamId == peerId);
        _membersChanged = true;
    }

    protected void RemoveMemberByPlayer(int playerId)
    {
        lock (_membersLock) _members.RemoveAll(m => m.PlayerId == playerId && !m.IsSelf && !m.IsHost);
        _membersChanged = true;
    }

    protected static byte[] PacketPayload(PacketId id, IPacket packet)
    {
        var frame = PacketCodec.Frame(id, packet);
        var payload = new byte[frame.Length - 2];
        Buffer.BlockCopy(frame, 2, payload, 0, payload.Length);
        return payload;
    }

    public bool TryRead(out ulong peerId, out byte[] payload)
    {
        if (_appIncoming.TryDequeue(out var packet))
        {
            peerId = packet.peerId;
            payload = packet.payload;
            return true;
        }

        peerId = 0;
        payload = null;
        return false;
    }

    public bool ConsumeMembersChanged()
    {
        if (!_membersChanged) return false;
        _membersChanged = false;
        return true;
    }

    public virtual bool IsPeerAllowed(ulong peerId) => peerId == LocalPeerId || peerId == HostPeerId;

    public virtual void SetLocalSessionInfo(ServerDirectSessionInfo info) { }

    public virtual bool BroadcastStart(ServerStartGame start)
    {
        if (!IsHost) return false;
        Broadcast(PacketPayload(PacketId.ServerStartGame, start));
        return true;
    }

    public virtual void PublishWorldAvailability(bool available)
    {
        if (!IsHost) return;
        SetMember(LocalPeerId, LocalPersonaName, true, true, !available);
    }

    public void Dispose() => Close();
}

internal sealed class DirectConnection
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly object _sendLock = new();
    private readonly Thread _reader;
    private volatile bool _closed;
    private bool _reading;

    internal ulong PeerId { get; }
    internal string Name { get; set; } = "";
    internal bool HandshakeDone { get; set; }
    internal bool WorldUnavailable { get; set; }

    internal event Action<DirectConnection, byte[]> FrameReceived;
    internal event Action<DirectConnection> Closed;

    internal DirectConnection(TcpClient client, ulong peerId)
    {
        _client = client;
        PeerId = peerId;
        _client.NoDelay = true;
        _stream = _client.GetStream();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"CairnMP-Direct-{peerId}" };
    }

    internal void StartReading()
    {
        if (_reading) return;
        _reading = true;
        _reader.Start();
    }

    internal void Send(byte[] payload)
    {
        if (_closed || payload == null || payload.Length == 0) return;
        var frame = new byte[payload.Length + 2];
        frame[0] = (byte)(payload.Length & 0xFF);
        frame[1] = (byte)((payload.Length >> 8) & 0xFF);
        Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);

        lock (_sendLock)
        {
            if (_closed) return;
            try
            {
                _stream.Write(frame, 0, frame.Length);
                _stream.Flush();
            }
            catch (Exception exception) { ModLog.SuppressedException("direct-transport.send", exception); }
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (!_closed)
            {
                var header = new byte[2];
                if (!ReadExactly(header, 2)) break;
                var length = header[0] | (header[1] << 8);
                if (length < 1 || length > ushort.MaxValue) break;
                var payload = new byte[length];
                if (!ReadExactly(payload, length)) break;
                FrameReceived?.Invoke(this, payload);
            }
        }
        catch (Exception exception) { ModLog.SuppressedException("direct-transport.read", exception); }
        finally
        {
            _closed = true;
            Closed?.Invoke(this);
        }
    }

    private bool ReadExactly(byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var chunk = _stream.Read(buffer, read, count - read);
            if (chunk <= 0) return false;
            read += chunk;
        }
        return true;
    }

    internal void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try { _client.Close(); }
        catch (Exception exception) { ModLog.SuppressedException("direct-transport.close", exception); }
    }
}

internal sealed class DirectHostSession : DirectTransportSession
{
    private readonly ConcurrentDictionary<ulong, DirectConnection> _connections = new();
    private readonly ConcurrentQueue<TcpClient> _pending = new();
    private readonly ConcurrentQueue<DirectConnection> _closed = new();
    private readonly int _port;
    private TcpListener _listener;
    private Thread _acceptThread;
    private ulong _nextPeerId = 1;

    internal DirectHostSession(HostConfig config, string hostName)
    {
        IsHost = true;
        LocalPeerId = 1;
        LocalPersonaName = string.IsNullOrWhiteSpace(hostName) ? "Host" : hostName.Trim();
        CurrentLobbyName = string.IsNullOrWhiteSpace(config.LobbyName) ? "Direct climb" : config.LobbyName;
        CurrentRoomCode = $"port {config.Port}";
        MaxMembers = Math.Max(2, config.MaxPlayers);
        _port = config.Port;
        _mode = config.Mode;
        _world = config.World;
        SetMember(LocalPeerId, LocalPersonaName, true, true, false);
    }

    public override ulong HostPeerId => LocalPeerId;

    internal void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _active = true;

        try
        {
            var address = Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (address != null) CurrentRoomCode = $"{address}:{_port}";
        }
        catch (Exception exception) { ModLog.SuppressedException("direct-transport.local-address", exception); }

        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "CairnMP-Direct-Listen" };
        _acceptThread.Start();
    }

    private void AcceptLoop()
    {
        try
        {
            while (_active)
            {
                var client = _listener.AcceptTcpClient();
                if (!_active) { client.Dispose(); break; }
                _pending.Enqueue(client);
            }
        }
        catch (Exception exception) { ModLog.SuppressedException("direct-transport.accept", exception); }
    }

    public override void Pump()
    {
        while (_pending.TryDequeue(out var client)) Register(client);
        while (_closed.TryDequeue(out var connection)) Remove(connection);

        while (SessionIncoming.TryDequeue(out var item))
        {
            if (!_connections.TryGetValue(item.peerId, out var connection)) continue;
            if (!connection.HandshakeDone) HandleHandshake(connection, item.payload);
            else QueueForApp(item.peerId, item.payload);
        }
    }

    private void Register(TcpClient client)
    {
        var peerId = Interlocked.Increment(ref _nextPeerId);
        if (peerId < 2) peerId = 2;
        var connection = new DirectConnection(client, peerId);
        _connections[peerId] = connection;
        connection.FrameReceived += (conn, payload) => QueueForSession(conn.PeerId, payload);
        connection.Closed += conn => _closed.Enqueue(conn);
        connection.StartReading();
        SetMember(peerId, "Connecting", false, false, false);
        ModLog.Info($"[Direct] Client connected from {client.Client.RemoteEndPoint} as peer {peerId}");
    }

    private void HandleHandshake(DirectConnection connection, byte[] payload)
    {
        if (payload.Length == 0 || (PacketId)payload[0] != PacketId.ClientDirectHandshake) return;

        var handshake = new ClientDirectHandshake();
        try
        {
            using var stream = new System.IO.MemoryStream(payload, 1, payload.Length - 1, writable: false);
            using var reader = new System.IO.BinaryReader(stream);
            handshake.Deserialize(reader);
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[Direct] Bad handshake: {exception.Message}");
            Reject(connection, "Invalid direct handshake.");
            return;
        }

        if (handshake.ProtocolVersion != Protocol.Version)
        {
            Reject(connection, $"Protocol mismatch: host {Protocol.Version}, client {handshake.ProtocolVersion}.");
            return;
        }

        var playerId = NetworkManager.StablePeerPlayerId(connection.PeerId);
        var name = string.IsNullOrWhiteSpace(handshake.PlayerName) ? $"Player{playerId}" : handshake.PlayerName.Trim();
        connection.Name = name;
        connection.WorldUnavailable = handshake.WorldUnavailable;
        connection.HandshakeDone = true;
        SetMember(connection.PeerId, name, false, false, handshake.WorldUnavailable, playerId);

        var info = new ServerDirectSessionInfo
        {
            AssignedPlayerId = playerId,
            ServerName = CurrentLobbyName,
            RoomCode = CurrentRoomCode,
            MaxPlayers = MaxMembers,
            Mode = (byte)_mode,
            World = (int)_world,
        };
        connection.Send(PacketPayload(PacketId.ServerDirectSessionInfo, info));

        var joinedPayload = PacketPayload(PacketId.ServerPlayerJoined, new ServerPlayerJoined
        {
            PlayerId = playerId,
            PlayerName = name,
        });
        foreach (var peer in _connections.Values)
            if (peer != connection && peer.HandshakeDone) peer.Send(joinedPayload);

        foreach (var peer in _connections.Values)
        {
            if (peer == connection || !peer.HandshakeDone) continue;
            connection.Send(PacketPayload(PacketId.ServerPlayerJoined, new ServerPlayerJoined
            {
                PlayerId = NetworkManager.StablePeerPlayerId(peer.PeerId),
                PlayerName = peer.Name,
            }));
        }

        ModLog.Info($"[Direct] Player admitted: [{playerId}] {name}");
    }

    private void Reject(DirectConnection connection, string reason)
    {
        connection.Send(PacketPayload(PacketId.ServerHandshakeReject, new ServerHandshakeReject { Reason = reason }));
        connection.Dispose();
    }

    private void Remove(DirectConnection connection)
    {
        if (!_connections.TryRemove(connection.PeerId, out _)) return;
        var playerId = NetworkManager.StablePeerPlayerId(connection.PeerId);
        RemoveMemberByPeer(connection.PeerId);
        if (connection.HandshakeDone)
        {
            QueueForApp(connection.PeerId, Array.Empty<byte>());
            _connections.Values.Where(p => p.HandshakeDone).ToList().ForEach(p =>
                p.Send(PacketPayload(PacketId.ServerPlayerLeft, new ServerPlayerLeft { PlayerId = playerId })));
        }
    }

    public override void Send(ulong peerId, byte[] payload, bool reliable = true)
    {
        if (_connections.TryGetValue(peerId, out var connection)) connection.Send(payload);
    }

    public override void Broadcast(byte[] payload, bool reliable = true)
    {
        foreach (var connection in _connections.Values) connection.Send(payload);
    }

    public override bool IsPeerAllowed(ulong peerId)
        => peerId == LocalPeerId || (_connections.TryGetValue(peerId, out var connection) && connection.HandshakeDone);

    protected override void Close()
    {
        if (!_active && _listener == null) return;
        _active = false;
        try { _listener?.Stop(); }
        catch (Exception exception) { ModLog.SuppressedException("direct-transport.listener-stop", exception); }
        foreach (var connection in _connections.Values) connection.Dispose();
        _connections.Clear();
        _listener = null;
    }
}

internal sealed class DirectClientSession : DirectTransportSession
{
    private readonly DirectConnectConfig _config;
    private readonly bool _worldUnavailable;
    private DirectConnection _host;

    internal DirectClientSession(DirectConnectConfig config, string playerName, bool worldUnavailable)
    {
        IsHost = false;
        LocalPeerId = 2;
        _config = config;
        _worldUnavailable = worldUnavailable;
        LocalPersonaName = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim();
        CurrentRoomCode = $"{config.Address}:{config.Port}";
    }

    public override ulong HostPeerId => 1;

    internal void Connect()
    {
        var client = new TcpClient();
        var task = client.ConnectAsync(_config.Address, _config.Port);
        try
        {
            if (!task.Wait(TimeSpan.FromSeconds(5)))
            {
                client.Dispose();
                throw new TimeoutException($"Timed out connecting to {_config.Address}:{_config.Port}");
            }
        }
        catch (AggregateException exception)
        {
            client.Dispose();
            throw new Exception($"Could not reach {_config.Address}:{_config.Port}: {exception.InnerException?.Message ?? exception.Message}");
        }

        _host = new DirectConnection(client, LocalPeerId);
        _host.FrameReceived += OnFrame;
        _host.Closed += _ => _active = false;
        _active = true;
        _host.StartReading();
        SetMember(LocalPeerId, LocalPersonaName, false, true, false);
        SetMember(HostPeerId, "Host", true, false, false);
        _host.Send(PacketPayload(PacketId.ClientDirectHandshake, new ClientDirectHandshake
        {
            ProtocolVersion = Protocol.Version,
            PlayerName = LocalPersonaName,
            RoomCode = CurrentRoomCode,
            WorldUnavailable = _worldUnavailable,
        }));
    }

    private void OnFrame(DirectConnection connection, byte[] payload)
    {
        if (payload.Length == 0) return;
        var id = (PacketId)payload[0];

        if (id == PacketId.ServerDirectSessionInfo)
        {
            try
            {
                using var stream = new System.IO.MemoryStream(payload, 1, payload.Length - 1, writable: false);
                using var reader = new System.IO.BinaryReader(stream);
                var info = new ServerDirectSessionInfo();
                info.Deserialize(reader);
                SetLocalSessionInfo(info);
            }
            catch (Exception exception)
            {
                ModLog.Warning($"[Direct] Bad session info: {exception.Message}");
                _lastError = "Host sent an invalid session description.";
                _active = false;
                return;
            }
        }

        if (id == PacketId.ServerPlayerJoined)
        {
            try
            {
                using var stream = new System.IO.MemoryStream(payload, 1, payload.Length - 1, writable: false);
                using var reader = new System.IO.BinaryReader(stream);
                var joined = new ServerPlayerJoined();
                joined.Deserialize(reader);
                SetMember((ulong)joined.PlayerId, joined.PlayerName, false, false, false, joined.PlayerId);
            }
            catch (Exception exception) { ModLog.Warning($"[Direct] Bad player join: {exception.Message}"); }
        }
        else if (id == PacketId.ServerPlayerLeft)
        {
            try
            {
                using var stream = new System.IO.MemoryStream(payload, 1, payload.Length - 1, writable: false);
                using var reader = new System.IO.BinaryReader(stream);
                var left = new ServerPlayerLeft();
                left.Deserialize(reader);
                RemoveMemberByPlayer(left.PlayerId);
            }
            catch (Exception exception) { ModLog.Warning($"[Direct] Bad player leave: {exception.Message}"); }
        }
        else if (id == PacketId.ServerHandshakeReject)
        {
            try
            {
                using var stream = new System.IO.MemoryStream(payload, 1, payload.Length - 1, writable: false);
                using var reader = new System.IO.BinaryReader(stream);
                var reject = new ServerHandshakeReject();
                reject.Deserialize(reader);
                _lastError = reject.Reason;
                QueueForApp(HostPeerId, payload);
                _active = false;
                return;
            }
            catch (Exception exception) { ModLog.Warning($"[Direct] Bad reject: {exception.Message}"); }
        }

        QueueForApp(HostPeerId, payload);
    }

    public override void SetLocalSessionInfo(ServerDirectSessionInfo info)
    {
        LocalPeerId = (ulong)info.AssignedPlayerId;
        CurrentLobbyName = info.ServerName;
        CurrentRoomCode = string.IsNullOrWhiteSpace(info.RoomCode) ? CurrentRoomCode : info.RoomCode;
        MaxMembers = info.MaxPlayers;
        _mode = MultiplayerModes.Parse(info.Mode).Mode;
        _world = Enum.IsDefined(typeof(ClimbWorld), info.World) ? (ClimbWorld)info.World : ClimbWorld.Kami;
        SetMember(LocalPeerId, LocalPersonaName, false, true, false, (int)LocalPeerId);
        SetMember(HostPeerId, info.ServerName, true, false, false, NetworkManager.StablePeerPlayerId(HostPeerId));
    }

    public override void Pump()
    {
        if (!_active && _host != null) return;
        if (!_active) return;
    }

    public override void Send(ulong peerId, byte[] payload, bool reliable = true)
    {
        if (peerId != HostPeerId) return;
        _host?.Send(payload);
    }

    public override void Broadcast(byte[] payload, bool reliable = true) => _host?.Send(payload);

    public override bool IsPeerAllowed(ulong peerId) => peerId == LocalPeerId || peerId == HostPeerId;

    public override void PublishWorldAvailability(bool available) { }

    protected override void Close()
    {
        _active = false;
        _host?.Dispose();
        _host = null;
    }
}
