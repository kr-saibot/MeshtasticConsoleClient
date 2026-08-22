using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Meshtastic.Protobufs;

namespace Meshtastic.Client
{
    public sealed class ConnectionStateChangedEventArgs : EventArgs
    {
        public ConnectionStateChangedEventArgs(ConnectionState state, Exception error) { State = state; Error = error; }
        public ConnectionState State { get; private set; }
        public Exception Error { get; private set; }
    }
    public sealed class NodeEventArgs : EventArgs { public NodeEventArgs(MeshNode node) { Node = node; } public MeshNode Node { get; private set; } }
    public sealed class MeshMessageEventArgs : EventArgs { public MeshMessageEventArgs(MeshMessage message) { Message = message; } public MeshMessage Message { get; private set; } }
    public sealed class MeshTelemetryEventArgs : EventArgs { public MeshTelemetryEventArgs(MeshTelemetry telemetry) { Telemetry = telemetry; } public MeshTelemetry Telemetry { get; private set; } }
    public sealed class PacketEventArgs : EventArgs { public PacketEventArgs(MeshPacket packet) { Packet = packet; } public MeshPacket Packet { get; private set; } }
    public sealed class TransportErrorEventArgs : EventArgs { public TransportErrorEventArgs(Exception error) { Error = error; } public Exception Error { get; private set; } }
    public sealed class SerialTrafficEventArgs : EventArgs
    {
        public SerialTrafficEventArgs(DateTime timestampUtc, string direction, byte[] data, string comment) { TimestampUtc = timestampUtc; Direction = direction; Data = data; Comment = comment; }
        public DateTime TimestampUtc { get; private set; }
        public string Direction { get; private set; }
        public byte[] Data { get; private set; }
        public string Comment { get; private set; }
    }

    /// <summary>Thread-safe native Meshtastic Protobuf client for Serial and TCP transports.</summary>
    public sealed class MeshtasticClient : IDisposable
    {
        public const int MaximumTextPayloadBytes = 233;
        private const uint BroadcastNode = UInt32.MaxValue;
        private readonly object _gate = new object();
        private readonly SemaphoreSlim _writer = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<uint, MeshNode> _nodes = new ConcurrentDictionary<uint, MeshNode>();
        private readonly ConcurrentDictionary<int, MeshChannel> _channels = new ConcurrentDictionary<int, MeshChannel>();
        private readonly ConcurrentQueue<MeshMessage> _messages = new ConcurrentQueue<MeshMessage>();
        private readonly ConcurrentDictionary<uint, bool> _pendingDeliveries = new ConcurrentDictionary<uint, bool>();
        private readonly Random _random = new Random();
        private Func<ITransport> _transportFactory;
        private ITransport _transport;
        private CancellationTokenSource _lifetime;
        private Task _connectionLoop;
        private int _connectionGeneration;
        private Exception _lastConnectionError;
        private TaskCompletionSource<bool> _configComplete;
        private uint _expectedConfigId;
        private bool _manualDisconnect;
        private ConnectionState _state = ConnectionState.Disconnected;
        private DateTime? _nextReconnectUtc;
        private string _transportKind;
        private string _transportEndpoint;
        private DateTime? _connectedSinceUtc;
        private DateTime? _lastSentUtc;
        private DateTime? _lastReceivedUtc;
        private TimeSpan _totalConnectedDuration;
        private int _reconnectCount;
        private long _sentFrameCount;
        private long _receivedFrameCount;

        public MeshtasticClient()
        {
            Device = new DeviceInfo();
            ReconnectDelay = TimeSpan.FromSeconds(5);
            HeartbeatInterval = TimeSpan.FromSeconds(15);
            TransportOperationTimeout = TimeSpan.FromSeconds(10);
            DisconnectTimeout = TimeSpan.FromSeconds(3);
            ConfigurationTimeout = TimeSpan.FromSeconds(20);
            ReceiveDebugInterval = TimeSpan.FromSeconds(30);
            MaximumMessageHistory = 200;
        }

        public event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;
        public event EventHandler<NodeEventArgs> NodeDiscovered;
        public event EventHandler<NodeEventArgs> NodeUpdated;
        public event EventHandler<MeshMessageEventArgs> MessageReceived;
        public event EventHandler<MeshTelemetryEventArgs> TelemetryReceived;
        /// <summary>Reports when the device queues a sent packet and when the mesh returns an ACK or NAK.</summary>
        public event EventHandler<MessageDeliveryEventArgs> MessageDeliveryChanged;
        /// <summary>Raised for every packet delivered by the local Meshtastic device, including non-text packets.</summary>
        public event EventHandler<PacketEventArgs> PacketReceived;
        public event EventHandler DeviceInfoUpdated;
        public event EventHandler ChannelsChanged;
        public event EventHandler ConfigurationReceived;
        public event EventHandler<TransportErrorEventArgs> TransportError;
        public event EventHandler<SerialTrafficEventArgs> SerialTraffic;

        public ConnectionState State { get { lock (_gate) return _state; } }
        public bool IsConnected { get { return State == ConnectionState.Connected; } }
        public DateTime? NextReconnectUtc { get { lock (_gate) return _nextReconnectUtc; } }
        public TimeSpan ReconnectDelay { get; set; }
        /// <summary>Regular native API heartbeat. A failed write makes the connection loop reconnect.</summary>
        public TimeSpan HeartbeatInterval { get; set; }
        /// <summary>Maximum time a single native transport write may take before the transport is closed.</summary>
        public TimeSpan TransportOperationTimeout { get; set; }
        /// <summary>Maximum time to wait for a native transport to close.</summary>
        public TimeSpan DisconnectTimeout { get; set; }
        /// <summary>Maximum time to wait for ConfigComplete after RequestFullStateAsync.</summary>
        public TimeSpan ConfigurationTimeout { get; set; }
        /// <summary>Maximum consecutive connection attempts before stopping. Set to 0 for unlimited retries.</summary>
        public int MaximumConnectionAttempts { get; set; }
        public int MaximumMessageHistory { get; set; }
        /// <summary>Optional diagnostic sink for the native receive path.</summary>
        public Action<string> DebugOutput { get; set; }
        public bool VerboseReceiveDebug { get; set; }
        /// <summary>Interval for a diagnostic "still waiting" message while an RX stream read is pending.</summary>
        public TimeSpan ReceiveDebugInterval { get; set; }
        public DeviceInfo Device { get; private set; }
        public ConnectionInfo ConnectionInfo
        {
            get
            {
                lock (_gate) return new ConnectionInfo(_transportKind, _transportEndpoint, _connectedSinceUtc, _totalConnectedDuration, _reconnectCount, _sentFrameCount, _receivedFrameCount, _lastSentUtc, _lastReceivedUtc);
            }
        }
        public IReadOnlyCollection<MeshNode> Nodes { get { return _nodes.Values.OrderBy(n => n.LongName).ToArray(); } }
        public IReadOnlyCollection<MeshChannel> Channels { get { return _channels.Values.OrderBy(c => c.Index).ToArray(); } }
        public IReadOnlyCollection<MeshMessage> MessageHistory { get { return _messages.ToArray(); } }

        /// <summary>Returns the great-circle distance from the local device position to a node, in metres.</summary>
        public double? GetDistanceToNodeMeters(MeshNode node)
        {
            if (node == null) throw new ArgumentNullException("node");
            return GetDistanceMeters(Device.Latitude, Device.Longitude, node.Latitude, node.Longitude);
        }

        /// <summary>Returns the initial geographic bearing from the local device position to a node (0° = north).</summary>
        public double? GetBearingToNodeDegrees(MeshNode node)
        {
            if (node == null) throw new ArgumentNullException("node");
            return GetInitialBearingDegrees(Device.Latitude, Device.Longitude, node.Latitude, node.Longitude);
        }

        /// <summary>Returns a 16-wind compass direction such as N, ENE or SW from the local device to a node.</summary>
        public string GetDirectionToNode(MeshNode node)
        {
            var bearing = GetBearingToNodeDegrees(node);
            return bearing.HasValue ? GetCompassDirection(bearing.Value) : null;
        }

        public static double? GetDistanceMeters(double? fromLatitude, double? fromLongitude, double? toLatitude, double? toLongitude)
        {
            if (!fromLatitude.HasValue || !fromLongitude.HasValue || !toLatitude.HasValue || !toLongitude.HasValue) return null;
            const double earthRadiusMeters = 6371008.8;
            var latitudeDelta = ToRadians(toLatitude.Value - fromLatitude.Value);
            var longitudeDelta = ToRadians(toLongitude.Value - fromLongitude.Value);
            var a = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2) + Math.Cos(ToRadians(fromLatitude.Value)) * Math.Cos(ToRadians(toLatitude.Value)) * Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
            return earthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        public static double? GetInitialBearingDegrees(double? fromLatitude, double? fromLongitude, double? toLatitude, double? toLongitude)
        {
            if (!fromLatitude.HasValue || !fromLongitude.HasValue || !toLatitude.HasValue || !toLongitude.HasValue) return null;
            var longitudeDelta = ToRadians(toLongitude.Value - fromLongitude.Value);
            var y = Math.Sin(longitudeDelta) * Math.Cos(ToRadians(toLatitude.Value));
            var x = Math.Cos(ToRadians(fromLatitude.Value)) * Math.Sin(ToRadians(toLatitude.Value)) - Math.Sin(ToRadians(fromLatitude.Value)) * Math.Cos(ToRadians(toLatitude.Value)) * Math.Cos(longitudeDelta);
            return (ToDegrees(Math.Atan2(y, x)) + 360d) % 360d;
        }

        public static string GetCompassDirection(double bearingDegrees)
        {
            var directions = new[] { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
            var normalized = ((bearingDegrees % 360d) + 360d) % 360d;
            return directions[(int)Math.Floor((normalized + 11.25d) / 22.5d) % directions.Length];
        }

        public Task ConnectSerialAsync(string portName, int baudRate = 115200, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (String.IsNullOrWhiteSpace(portName)) throw new ArgumentException("A serial port is required.", "portName");
            portName = portName.Trim();
            if (!System.IO.Ports.SerialPort.GetPortNames().Any(p => String.Equals(p, portName, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Serial port " + portName + " is not available. Check the connection settings and whether the device is connected.");
            return StartAsync(delegate { return new SerialTransport(portName, baudRate); }, cancellationToken);
        }

        public Task ConnectTcpAsync(string host, int port = 4403, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (String.IsNullOrWhiteSpace(host)) throw new ArgumentException("A host is required.", "host");
            return StartAsync(delegate { return new TcpTransport(host, port); }, cancellationToken);
        }

        private Task StartAsync(Func<ITransport> transportFactory, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_lifetime != null) throw new InvalidOperationException("The client is already started. Call DisconnectAsync first.");
                var generation = ++_connectionGeneration;
                _transportFactory = transportFactory; _manualDisconnect = false;
                _lastConnectionError = null;
                _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _connectionLoop = RunConnectionLoopAsync(_lifetime.Token, generation);
            }
            return WaitUntilConnectedAsync(cancellationToken);
        }

        // Starts the connection loop and returns after its first successful configuration sync.
        public async Task ConnectAndSynchronizeSerialAsync(string portName, int baudRate = 115200, CancellationToken cancellationToken = default(CancellationToken))
        {
            await ConnectSerialAsync(portName, baudRate, cancellationToken).ConfigureAwait(false);
            await RequestFullStateAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task DisconnectAsync()
        {
            Task loop;
            ITransport transport;
            lock (_gate)
            {
                _manualDisconnect = true; loop = _connectionLoop;
                if (_lifetime != null) _lifetime.Cancel();
                transport = _transport;
            }
            if (transport != null)
            {
                var close = Task.Run(delegate { try { transport.Close(); } catch { } });
                await Task.WhenAny(close, Task.Delay(DisconnectTimeout)).ConfigureAwait(false);
            }
            if (loop != null)
            {
                try
                {
                    await Task.WhenAny(loop, Task.Delay(DisconnectTimeout)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                if (!loop.IsCompleted)
                {
                    lock (_gate)
                    {
                        if (Object.ReferenceEquals(_connectionLoop, loop))
                        {
                            _connectionGeneration++;
                            _connectionLoop = null;
                            _lifetime = null;
                            _transport = null;
                            _nextReconnectUtc = null;
                        }
                    }
                }
            }
            if (State != ConnectionState.Disconnected) SetState(ConnectionState.Disconnected, null);
        }

        public async Task RequestFullStateAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            var nonce = NextUInt();
            _expectedConfigId = nonce;
            // ConfigComplete arrives on the RX thread. Do not run the caller's /refresh
            // continuation synchronously on that thread: a console caller may immediately
            // enter Console.ReadLine and would then freeze all further receives.
            _configComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(ConfigurationTimeout);
                try
                {
                    await SendAsync(new ToRadio { WantConfigId = nonce }, timeout.Token).ConfigureAwait(false);
                    using (timeout.Token.Register(delegate { _configComplete.TrySetCanceled(); }))
                        await _configComplete.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    CloseCurrentTransport();
                    throw new TimeoutException("The Meshtastic device did not respond to the configuration request.");
                }
            }
        }

        public Task SendBroadcastAsync(string text, int channelIndex = 0, bool wantAck = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendTextAsync(BroadcastNode, text, channelIndex, wantAck, cancellationToken);
        }
        public Task<uint> SendBroadcastWithIdAsync(string text, int channelIndex = 0, bool wantAck = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendTextWithIdAsync(BroadcastNode, text, channelIndex, wantAck, cancellationToken);
        }

        /// <summary>Sends a message to a Meshtastic channel/group by its local channel index.</summary>
        public Task SendTextToChannelAsync(int channelIndex, string text, bool wantAck = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (channelIndex < 0) throw new ArgumentOutOfRangeException("channelIndex");
            return SendTextAsync(BroadcastNode, text, channelIndex, wantAck, cancellationToken);
        }
        public Task<uint> SendTextToChannelWithIdAsync(int channelIndex, string text, bool wantAck = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (channelIndex < 0) throw new ArgumentOutOfRangeException("channelIndex");
            return SendTextWithIdAsync(BroadcastNode, text, channelIndex, wantAck, cancellationToken);
        }

        /// <summary>Activates packet streaming on firmware that starts forwarding packets only after a local MeshPacket.</summary>
        public Task ActivatePacketStreamingAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (Device.MyNode == null) throw new InvalidOperationException("Local node information has not been received yet.");
            var packet = new MeshPacket
            {
                Id = NextPacketId(), To = Device.MyNode.MyNodeNum,
                Decoded = new Data { Portnum = PortNum.PrivateApp, Payload = ByteString.Empty }
            };
            return SendAsync(new ToRadio { Packet = packet }, cancellationToken);
        }

        public Task SendTextToNodeAsync(uint nodeNumber, string text, bool wantAck = true, CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendTextAsync(nodeNumber, text, 0, wantAck, cancellationToken);
        }

        public Task SendTextAsync(uint destination, string text, int channelIndex = 0, bool wantAck = true, CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendTextWithIdAsync(destination, text, channelIndex, wantAck, cancellationToken);
        }
        public async Task<uint> SendTextWithIdAsync(uint destination, string text, int channelIndex = 0, bool wantAck = true, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (String.IsNullOrEmpty(text)) throw new ArgumentException("Text must not be empty.", "text");
            var payloadLength = Encoding.UTF8.GetByteCount(text);
            if (payloadLength > MaximumTextPayloadBytes) throw new ArgumentException("Text messages are limited to " + MaximumTextPayloadBytes + " UTF-8 bytes (current: " + payloadLength + ").", "text");
            var data = new Data { Portnum = PortNum.TextMessageApp, Payload = ByteString.CopyFromUtf8(text) };
            var packet = new MeshPacket { Id = NextPacketId(), To = destination, Channel = (uint)channelIndex, WantAck = wantAck, Decoded = data };
            // QueueStatus confirms local acceptance even for broadcasts; only direct/reliable packets wait for a later ACK/NAK.
            _pendingDeliveries[packet.Id] = wantAck;
            await SendAsync(new ToRadio { Packet = packet }, cancellationToken).ConfigureAwait(false);
            return packet.Id;
        }

        /// <summary>Broadcasts a position. Meshtastic firmware also adopts this as the local position.</summary>
        public Task SetLocalPositionAsync(double latitude, double longitude, int? altitudeMeters = null, int channelIndex = 0, CancellationToken cancellationToken = default(CancellationToken))
        {
            ValidateCoordinates(latitude, longitude);
            var position = CreatePosition(latitude, longitude, altitudeMeters);
            var packet = new MeshPacket { To = BroadcastNode, Channel = (uint)channelIndex, Decoded = new Data { Portnum = PortNum.PositionApp, Payload = position.ToByteString() } };
            return SendAsync(new ToRadio { Packet = packet }, cancellationToken);
        }

        /// <summary>Stores a fixed, manual position on the locally connected device (firmware with Admin API support required).</summary>
        public Task SetFixedPositionAsync(double latitude, double longitude, int? altitudeMeters = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            ValidateCoordinates(latitude, longitude);
            if (Device.MyNode == null) throw new InvalidOperationException("Local node information has not been received yet.");
            var admin = new AdminMessage { SetFixedPosition = CreatePosition(latitude, longitude, altitudeMeters) };
            var packet = new MeshPacket
            {
                To = Device.MyNode.MyNodeNum, WantAck = true,
                Decoded = new Data { Portnum = PortNum.AdminApp, WantResponse = true, Payload = admin.ToByteString() }
            };
            return SendAsync(new ToRadio { Packet = packet }, cancellationToken);
        }

        public Task ClearFixedPositionAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (Device.MyNode == null) throw new InvalidOperationException("Local node information has not been received yet.");
            var admin = new AdminMessage { RemoveFixedPosition = true };
            var packet = new MeshPacket { To = Device.MyNode.MyNodeNum, WantAck = true, Decoded = new Data { Portnum = PortNum.AdminApp, WantResponse = true, Payload = admin.ToByteString() } };
            return SendAsync(new ToRadio { Packet = packet }, cancellationToken);
        }

        private async Task SendAsync(ToRadio message, CancellationToken cancellationToken)
        {
            ITransport transport;
            lock (_gate) transport = _transport;
            if (transport == null || transport.Stream == null || !IsConnected) throw new InvalidOperationException("Meshtastic is not connected.");
            var payload = message.ToByteArray();
            if (payload.Length > UInt16.MaxValue) throw new InvalidOperationException("Protobuf frame is too large.");
            var frame = new byte[payload.Length + 4];
            frame[0] = 0x94; frame[1] = 0xC3; frame[2] = (byte)(payload.Length >> 8); frame[3] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            var writerEntered = false;
            using (var writerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                writerTimeout.CancelAfter(TransportOperationTimeout);
                try { await _writer.WaitAsync(writerTimeout.Token).ConfigureAwait(false); writerEntered = true; }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    transport.Close();
                    throw new TimeoutException("The Meshtastic transport is blocked by a previous write.");
                }
            }
            try
            {
                var write = transport.UseSynchronousWrites
                    ? Task.Run(delegate { cancellationToken.ThrowIfCancellationRequested(); transport.Write(frame, 0, frame.Length); }, cancellationToken)
                    : transport.Stream.WriteAsync(frame, 0, frame.Length, cancellationToken);
                if (await Task.WhenAny(write, Task.Delay(TransportOperationTimeout, cancellationToken)).ConfigureAwait(false) != write)
                {
                    transport.Close();
                    throw new TimeoutException("Writing to the Meshtastic transport timed out.");
                }
                await write.ConfigureAwait(false);
                if (!transport.UseSynchronousWrites) await transport.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (String.Equals(transport.Kind, "Serial", StringComparison.OrdinalIgnoreCase)) RaiseSerialTraffic("TX", frame, DescribeToRadio(message));
                lock (_gate) { _sentFrameCount++; _lastSentUtc = DateTime.UtcNow; }
            }
            finally { if (writerEntered) _writer.Release(); }
        }

        private async Task RunConnectionLoopAsync(CancellationToken token, int generation)
        {
            var first = true;
            var failedAttempts = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    lock (_gate) _nextReconnectUtc = null;
                    SetState(first ? ConnectionState.Connecting : ConnectionState.Reconnecting, null);
                    var connectedThisAttempt = false;
                    try
                    {
                        var transport = _transportFactory();
                        lock (_gate) _transport = transport;
                        Debug("Opening transport " + transport.Kind + " at " + transport.Endpoint + ".");
                        await transport.OpenAsync(token).ConfigureAwait(false);
                        connectedThisAttempt = true;
                        failedAttempts = 0;
                        lock (_gate)
                        {
                            _transportKind = transport.Kind; _transportEndpoint = transport.Endpoint; _connectedSinceUtc = DateTime.UtcNow;
                            if (!first) _reconnectCount++;
                        }
                        if (String.Equals(transport.Kind, "Serial", StringComparison.OrdinalIgnoreCase)) RaiseSerialTraffic("TX", new byte[] { 0x94, 0x94, 0x94, 0x94 }, "serial wake/resynchronization");
                        SetState(ConnectionState.Connected, null);
                        Debug("Transport open; starting heartbeat and RX reader.");
                        using (var connectionToken = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            var heartbeat = HeartbeatLoopAsync(transport, connectionToken.Token);
                            // On reconnect the firmware has discarded the previous client session. Start the reader
                            // first, then repeat the normal config handshake and local packet-stream activation.
                            var reader = ReadLoopAsync(transport.Stream, connectionToken.Token);
                            try
                            {
                                if (!first)
                                {
                                    await RequestFullStateAsync(connectionToken.Token).ConfigureAwait(false);
                                    await ActivatePacketStreamingAsync(connectionToken.Token).ConfigureAwait(false);
                                }
                                await reader.ConfigureAwait(false);
                            }
                            finally { connectionToken.Cancel(); try { await heartbeat.ConfigureAwait(false); } catch { } }
                        }
                        throw new IOException("The Meshtastic transport closed.");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        Debug("Connection loop error: " + ex.GetType().Name + ": " + ex.Message);
                        Raise(TransportError, new TransportErrorEventArgs(ex));
                        lock (_gate)
                        {
                            if (generation == _connectionGeneration)
                            {
                                _lastConnectionError = ex;
                                if (_connectedSinceUtc.HasValue) { _totalConnectedDuration += DateTime.UtcNow - _connectedSinceUtc.Value; _connectedSinceUtc = null; }
                                if (_transport != null) { _transport.Dispose(); _transport = null; }
                            }
                        }
                        if (_manualDisconnect || token.IsCancellationRequested) break;
                        // Retrying cannot resolve an invalid or already opened serial port.
                        // Stop the initial connection promptly and report the original error.
                        if (!connectedThisAttempt && (ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException))
                        {
                            DebugStatus("The serial port cannot be opened; reconnecting has stopped.");
                            break;
                        }
                        failedAttempts++;
                        if (MaximumConnectionAttempts > 0 && failedAttempts >= MaximumConnectionAttempts)
                        {
                            DebugStatus("Connection attempt limit reached; reconnecting has stopped.");
                            break;
                        }
                        SetState(ConnectionState.Reconnecting, ex);
                        if (!connectedThisAttempt)
                        {
                            lock (_gate) _nextReconnectUtc = DateTime.UtcNow + ReconnectDelay;
                            await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
                        }
                    }
                    first = false;
                }
            }
            finally
            {
                var isCurrentGeneration = false;
                lock (_gate)
                {
                    if (generation == _connectionGeneration)
                    {
                        isCurrentGeneration = true;
                        if (_connectedSinceUtc.HasValue) { _totalConnectedDuration += DateTime.UtcNow - _connectedSinceUtc.Value; _connectedSinceUtc = null; }
                        if (_transport != null) { _transport.Dispose(); _transport = null; } _nextReconnectUtc = null; _lifetime = null; _connectionLoop = null;
                    }
                }
                if (isCurrentGeneration) SetState(ConnectionState.Disconnected, null);
            }
        }

        private async Task ReadLoopAsync(Stream stream, CancellationToken token)
        {
            Debug("RX reader started.");
            while (!token.IsCancellationRequested)
            {
                var start = await ReadByteAsync(stream, token).ConfigureAwait(false);
                Debug("RX raw byte: 0x" + start.ToString("X2"));
                if (start != 0x94) { Debug("RX ignored: expected frame start 0x94."); continue; }
                var marker = await ReadByteAsync(stream, token).ConfigureAwait(false);
                Debug("RX frame marker: 0x" + marker.ToString("X2"));
                if (marker != 0xC3) { Debug("RX ignored: expected frame marker 0xC3."); continue; }
                var high = await ReadByteAsync(stream, token).ConfigureAwait(false);
                var low = await ReadByteAsync(stream, token).ConfigureAwait(false);
                var length = (high << 8) | low;
                Debug("RX frame header: length=" + length);
                if (length == 0 || length > 8192) { Debug("RX ignored: invalid frame length."); continue; }
                var bytes = await ReadExactAsync(stream, length, token).ConfigureAwait(false);
                lock (_gate) { _receivedFrameCount++; _lastReceivedUtc = DateTime.UtcNow; }
                Debug("RX complete frame #" + _receivedFrameCount + ", parsing FromRadio.");
                FromRadio radio;
                try { radio = FromRadio.Parser.ParseFrom(bytes); }
                catch (Exception ex)
                {
                    RaiseSerialTraffic("RX", CreateFrame(bytes), "invalid FromRadio: " + ex.GetType().Name);
                    Debug("RX processing failed: " + ex.GetType().Name + ": " + ex.Message); throw;
                }
                RaiseSerialTraffic("RX", CreateFrame(bytes), DescribeFromRadio(radio));
                try { Handle(radio); Debug("RX frame processing completed; waiting for next frame."); }
                catch (Exception ex) { Debug("RX processing failed: " + ex.GetType().Name + ": " + ex.Message); throw; }
            }
        }

        private static byte[] CreateFrame(byte[] payload)
        {
            var frame = new byte[payload.Length + 4];
            frame[0] = 0x94; frame[1] = 0xC3; frame[2] = (byte)(payload.Length >> 8); frame[3] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            return frame;
        }
        private static string DescribeToRadio(ToRadio radio)
        {
            if (radio == null) return "unknown ToRadio";
            return radio.PayloadVariantCase == ToRadio.PayloadVariantOneofCase.Packet ? "packet " + DescribeMeshPacket(radio.Packet) : radio.PayloadVariantCase.ToString();
        }
        private static string DescribeFromRadio(FromRadio radio)
        {
            if (radio == null) return "unknown FromRadio";
            return radio.PayloadVariantCase == FromRadio.PayloadVariantOneofCase.Packet ? "packet " + DescribeMeshPacket(radio.Packet) : radio.PayloadVariantCase.ToString();
        }
        private static string DescribeMeshPacket(MeshPacket packet)
        {
            if (packet == null) return "(empty)";
            var description = "id=" + packet.Id.ToString("X8") + " from=!" + packet.From.ToString("X8") + " to=!" + packet.To.ToString("X8");
            if (packet.PayloadVariantCase == MeshPacket.PayloadVariantOneofCase.Decoded && packet.Decoded != null) description += " " + packet.Decoded.Portnum;
            else description += " " + packet.PayloadVariantCase;
            return description;
        }
        private void RaiseSerialTraffic(string direction, byte[] data, string comment)
        {
            string kind; lock (_gate) kind = _transportKind;
            if (!String.Equals(kind, "Serial", StringComparison.OrdinalIgnoreCase)) return;
            Raise(SerialTraffic, new SerialTrafficEventArgs(DateTime.UtcNow, direction, data == null ? new byte[0] : (byte[])data.Clone(), comment ?? ""));
        }

        private void Handle(FromRadio radio)
        {
            Debug("RX FromRadio: " + radio.PayloadVariantCase);
            switch (radio.PayloadVariantCase)
            {
                case FromRadio.PayloadVariantOneofCase.MyInfo:
                    Device.MyNode = radio.MyInfo; UpdateLocalNode(); Raise(DeviceInfoUpdated, EventArgs.Empty); break;
                case FromRadio.PayloadVariantOneofCase.NodeInfo: UpdateNode(radio.NodeInfo); break;
                case FromRadio.PayloadVariantOneofCase.Channel: UpdateChannel(radio.Channel); break;
                case FromRadio.PayloadVariantOneofCase.Metadata:
                    Device.Metadata = radio.Metadata; Raise(DeviceInfoUpdated, EventArgs.Empty); break;
                case FromRadio.PayloadVariantOneofCase.ConfigCompleteId:
                    if (_configComplete != null && radio.ConfigCompleteId == _expectedConfigId)
                    {
                        Debug("RX ConfigComplete matches request; completing refresh without blocking RX thread.");
                        _configComplete.TrySetResult(true);
                    }
                    Raise(ConfigurationReceived, EventArgs.Empty); break;
                case FromRadio.PayloadVariantOneofCase.QueueStatus: HandleQueueStatus(radio.QueueStatus); break;
                case FromRadio.PayloadVariantOneofCase.Packet: HandlePacket(radio.Packet); break;
            }
        }

        private void UpdateNode(NodeInfo value)
        {
            MeshNode node; var discovered = false;
            if (!_nodes.TryGetValue(value.Num, out node)) { node = new MeshNode(value); _nodes[value.Num] = node; discovered = true; }
            else node.Update(value);
            UpdateLocalNode(); Raise(discovered ? NodeDiscovered : NodeUpdated, new NodeEventArgs(node));
        }
        private void UpdateLocalNode()
        {
            if (Device.MyNode == null) return;
            MeshNode node; if (_nodes.TryGetValue(Device.MyNode.MyNodeNum, out node)) { Device.LocalNode = node; Raise(DeviceInfoUpdated, EventArgs.Empty); }
        }
        private void UpdateChannel(Channel value)
        {
            MeshChannel channel; if (!_channels.TryGetValue(value.Index, out channel)) _channels[value.Index] = new MeshChannel(value); else channel.Update(value);
            Raise(ChannelsChanged, EventArgs.Empty);
        }
        private void HandlePacket(MeshPacket packet)
        {
            Debug("RX MeshPacket: id=" + packet.Id.ToString("x8") + ", from=!" + packet.From.ToString("x8") + ", to=!" + packet.To.ToString("x8") + ", payload=" + packet.PayloadVariantCase);
            Raise(PacketReceived, new PacketEventArgs(packet));
            MeshNode signalNode;
            if (_nodes.TryGetValue(packet.From, out signalNode)) { signalNode.UpdateRadioMetrics(packet.RxRssi, packet.RxSnr); Raise(NodeUpdated, new NodeEventArgs(signalNode)); }
            if (packet.PayloadVariantCase != MeshPacket.PayloadVariantOneofCase.Decoded) { Debug("RX packet ignored: not decoded."); return; }
            var data = packet.Decoded;
            Debug("RX decoded port: " + data.Portnum + ", payload bytes=" + data.Payload.Length);
            if (data.Portnum == PortNum.RoutingApp) { Debug("RX routing response."); HandleRoutingResponse(data); return; }
            if (data.Portnum == PortNum.TextMessageApp)
            {
                var message = new MeshMessage(packet, data.Payload.ToStringUtf8());
                Debug("RX text message: channel=" + (message.ChannelIndex.HasValue ? message.ChannelIndex.Value.ToString() : "direct") + ", text length=" + message.Text.Length);
                _messages.Enqueue(message);
                MeshMessage excess; while (_messages.Count > MaximumMessageHistory && _messages.TryDequeue(out excess)) { }
                Debug("RX raising MessageReceived.");
                Raise(MessageReceived, new MeshMessageEventArgs(message));
                Debug("RX MessageReceived completed.");
            }
            else if (data.Portnum == PortNum.PositionApp)
            {
                MeshNode node; if (_nodes.TryGetValue(packet.From, out node)) { node.Value.Position = Position.Parser.ParseFrom(data.Payload); Raise(NodeUpdated, new NodeEventArgs(node)); }
            }
            else if (data.Portnum == PortNum.NodeinfoApp)
            {
                MeshNode node; if (_nodes.TryGetValue(packet.From, out node)) { node.Value.User = User.Parser.ParseFrom(data.Payload); Raise(NodeUpdated, new NodeEventArgs(node)); }
            }
            else if (data.Portnum == PortNum.TelemetryApp)
            {
                var telemetry = Telemetry.Parser.ParseFrom(data.Payload);
                MeshNode node; if (_nodes.TryGetValue(packet.From, out node) && telemetry.DeviceMetrics != null) { node.Value.DeviceMetrics = telemetry.DeviceMetrics; Raise(NodeUpdated, new NodeEventArgs(node)); }
                Raise(TelemetryReceived, new MeshTelemetryEventArgs(new MeshTelemetry(packet, telemetry)));
            }
        }

        private void HandleQueueStatus(QueueStatus status)
        {
            bool awaitingAck;
            if (_pendingDeliveries.TryGetValue(status.MeshPacketId, out awaitingAck))
            {
                var state = status.Res == 0 ? MessageDeliveryState.QueuedAtDevice : MessageDeliveryState.Failed;
                Raise(MessageDeliveryChanged, new MessageDeliveryEventArgs(status.MeshPacketId, state, Routing.Types.Error.None));
                if (status.Res != 0 || !awaitingAck) _pendingDeliveries.TryRemove(status.MeshPacketId, out awaitingAck);
            }
        }

        private void HandleRoutingResponse(Data data)
        {
            bool ignored;
            if (!_pendingDeliveries.TryRemove(data.RequestId, out ignored)) return;
            var routing = Routing.Parser.ParseFrom(data.Payload);
            var error = routing.VariantCase == Routing.VariantOneofCase.ErrorReason ? routing.ErrorReason : Routing.Types.Error.None;
            Raise(MessageDeliveryChanged, new MessageDeliveryEventArgs(data.RequestId, error == Routing.Types.Error.None ? MessageDeliveryState.Delivered : MessageDeliveryState.Failed, error));
        }

        private async Task WaitUntilConnectedAsync(CancellationToken token)
        {
            while (!IsConnected)
            {
                token.ThrowIfCancellationRequested();
                Task loop; lock (_gate) loop = _connectionLoop;
                if (loop == null || loop.IsCompleted)
                {
                    Exception error; lock (_gate) error = _lastConnectionError;
                    if (error != null) throw new IOException("Meshtastic could not be connected: " + error.Message, error);
                    throw new IOException("Meshtastic could not be connected.");
                }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
        private async Task HeartbeatLoopAsync(ITransport transport, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await SendAsync(new ToRadio { Heartbeat = new Heartbeat() }, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch { transport.Close(); return; }
                await Task.Delay(HeartbeatInterval, token).ConfigureAwait(false);
            }
        }
        private void CloseCurrentTransport()
        {
            ITransport transport;
            lock (_gate) transport = _transport;
            if (transport != null) transport.Close();
        }
        private void Debug(string message)
        {
            if (!VerboseReceiveDebug) return;
            WriteDebug(message);
        }
        private void DebugStatus(string message)
        {
            WriteDebug(message);
        }
        private void WriteDebug(string message)
        {
            var output = DebugOutput;
            if (output == null) return;
            try { output("[Meshtastic RX " + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message); }
            catch { }
        }
        private async Task<int> ReadByteAsync(Stream stream, CancellationToken token) { var b = await ReadExactAsync(stream, 1, token).ConfigureAwait(false); return b[0]; }
        private async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken token)
        {
            var result = new byte[length]; var offset = 0;
            while (offset < length)
            {
                Debug("RX waiting for " + (length - offset) + " byte(s).");
                var pendingRead = stream.ReadAsync(result, offset, length - offset, token);
                if (DebugOutput != null && ReceiveDebugInterval > TimeSpan.Zero)
                {
                    while (!pendingRead.IsCompleted)
                    {
                        var completed = await Task.WhenAny(pendingRead, Task.Delay(ReceiveDebugInterval, token)).ConfigureAwait(false);
                        if (completed == pendingRead) break;
                        token.ThrowIfCancellationRequested();
                        DebugStatus("RX reader is still active and waiting for " + (length - offset) + " byte(s) from the stream.");
                    }
                }
                var read = await pendingRead.ConfigureAwait(false);
                Debug("RX stream returned " + read + " byte(s).");
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return result;
        }
        private uint NextUInt() { lock (_random) { var bytes = new byte[4]; _random.NextBytes(bytes); return BitConverter.ToUInt32(bytes, 0); } }
        private uint NextPacketId()
        {
            // Packet IDs are used by the device's queue and the routing ACK/NAK response.
            var id = NextUInt();
            return id == 0 ? 1u : id;
        }
        private static Position CreatePosition(double latitude, double longitude, int? altitudeMeters)
        {
            var position = new Position { LatitudeI = (int)Math.Round(latitude * 10000000d), LongitudeI = (int)Math.Round(longitude * 10000000d) };
            if (altitudeMeters.HasValue) position.Altitude = altitudeMeters.Value;
            return position;
        }
        private static void ValidateCoordinates(double latitude, double longitude)
        {
            if (latitude < -90 || latitude > 90) throw new ArgumentOutOfRangeException("latitude");
            if (longitude < -180 || longitude > 180) throw new ArgumentOutOfRangeException("longitude");
        }
        private static double ToRadians(double degrees) { return degrees * Math.PI / 180d; }
        private static double ToDegrees(double radians) { return radians * 180d / Math.PI; }
        private void SetState(ConnectionState state, Exception error)
        {
            var changed = false; lock (_gate) { if (_state != state) { _state = state; changed = true; } }
            if (changed) Raise(ConnectionStateChanged, new ConnectionStateChangedEventArgs(state, error));
        }
        // User callbacks run beside the transport reader. One faulty callback must not
        // terminate that reader and make later radio packets appear only after a refresh.
        private void Raise(EventHandler handler, EventArgs args)
        {
            if (handler == null) return;
            foreach (EventHandler callback in handler.GetInvocationList())
            {
                try { callback(null, args); }
                catch (Exception ex) { Debug("Event callback failed: " + ex.GetType().Name + ": " + ex.Message); }
            }
        }
        private void Raise<T>(EventHandler<T> handler, T args) where T : EventArgs
        {
            if (handler == null) return;
            foreach (EventHandler<T> callback in handler.GetInvocationList())
            {
                try { callback(null, args); }
                catch (Exception ex) { Debug("Event callback failed: " + ex.GetType().Name + ": " + ex.Message); }
            }
        }
        public void Dispose() { DisconnectAsync().GetAwaiter().GetResult(); _writer.Dispose(); }
    }
}
