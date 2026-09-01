using System;
using Meshtastic.Protobufs;

namespace Meshtastic.Client
{
    public sealed class ConnectionInfo
    {
        internal ConnectionInfo(string transport, string endpoint, DateTime? connectedSinceUtc, TimeSpan totalConnectedDuration, int reconnectCount, long sentFrameCount, long receivedFrameCount, DateTime? lastSentUtc, DateTime? lastReceivedUtc)
        {
            Transport = transport; Endpoint = endpoint; ConnectedSinceUtc = connectedSinceUtc; TotalConnectedDuration = totalConnectedDuration;
            ReconnectCount = reconnectCount; SentFrameCount = sentFrameCount; ReceivedFrameCount = receivedFrameCount;
            LastSentUtc = lastSentUtc; LastReceivedUtc = lastReceivedUtc;
        }
        public string Transport { get; private set; }
        public string Endpoint { get; private set; }
        public DateTime? ConnectedSinceUtc { get; private set; }
        public TimeSpan CurrentConnectionDuration { get { return ConnectedSinceUtc.HasValue ? DateTime.UtcNow - ConnectedSinceUtc.Value : TimeSpan.Zero; } }
        public TimeSpan TotalConnectedDuration { get; private set; }
        public int ReconnectCount { get; private set; }
        public long SentFrameCount { get; private set; }
        public long ReceivedFrameCount { get; private set; }
        public DateTime? LastSentUtc { get; private set; }
        public DateTime? LastReceivedUtc { get; private set; }
    }

    public sealed class MeshNode
    {
        private int? _lastRssi;
        private float? _lastSnr;
        internal MeshNode(NodeInfo value) { Value = value; }
        public NodeInfo Value { get; private set; }
        internal void Update(NodeInfo value) { Value = value; }
        public uint Number { get { return Value.Num; } }
        public string Id { get { return Value.User == null ? "!" + Number.ToString("x8") : Value.User.Id; } }
        public string LongName { get { return Value.User == null ? null : Value.User.LongName; } }
        public string ShortName { get { return Value.User == null ? null : Value.User.ShortName; } }
        public DateTime? LastHeardUtc { get { return Value.LastHeard == 0 ? (DateTime?)null : UnixTime(Value.LastHeard); } }
        public double? Latitude { get { return Value.Position == null || !Value.Position.HasLatitudeI ? (double?)null : Value.Position.LatitudeI / 10000000d; } }
        public double? Longitude { get { return Value.Position == null || !Value.Position.HasLongitudeI ? (double?)null : Value.Position.LongitudeI / 10000000d; } }
        public uint? BatteryLevel { get { return Value.DeviceMetrics == null || !Value.DeviceMetrics.HasBatteryLevel ? (uint?)null : Value.DeviceMetrics.BatteryLevel; } }
        /// <summary>Number of mesh hops reported by the local Meshtastic node; null when not supplied by firmware.</summary>
        public uint? HopsAway { get { return Value.HasHopsAway ? (uint?)Value.HopsAway : null; } }
        /// <summary>Latest RSSI reported by the local device for a packet from this node.</summary>
        public int? LastRssi { get { return _lastRssi; } }
        /// <summary>Latest SNR reported by the local device for a packet from this node.</summary>
        public float? LastSnr { get { return _lastSnr; } }
        internal void UpdateRadioMetrics(int rssi, float snr) { if (rssi != 0) _lastRssi = rssi; _lastSnr = snr; }
        private static DateTime UnixTime(uint seconds) { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds); }
    }

    public sealed class MeshChannel
    {
        internal MeshChannel(Channel value) { Value = value; }
        public Channel Value { get; private set; }
        internal void Update(Channel value) { Value = value; }
        public int Index { get { return Value.Index; } }
        public string Name { get { return Value.Settings == null ? null : Value.Settings.Name; } }
        public Channel.Types.Role Role { get { return Value.Role; } }
    }

    public sealed class MeshMessage
    {
        internal MeshMessage(MeshPacket packet, string text)
        {
            Packet = packet; Text = text;
            ReceivedAtUtc = packet != null && packet.HasRxTime && packet.RxTime != 0
                ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(packet.RxTime)
                : DateTime.UtcNow;
        }
        public MeshPacket Packet { get; private set; }
        public string Text { get; private set; }
        public uint From { get { return Packet.From; } }
        public uint To { get { return Packet.To; } }
        public bool IsChannelMessage { get { return Packet.To == UInt32.MaxValue; } }
        public bool IsDirectMessage { get { return !IsChannelMessage; } }
        public uint? ChannelIndex { get { return IsChannelMessage ? (uint?)Packet.Channel : null; } }
        public DateTime ReceivedAtUtc { get; private set; }
    }

    public sealed class MeshTelemetry
    {
        internal MeshTelemetry(MeshPacket packet, Telemetry telemetry)
        {
            Packet = packet; Telemetry = telemetry; ReceivedAtUtc = DateTime.UtcNow;
        }
        public MeshPacket Packet { get; private set; }
        public Telemetry Telemetry { get; private set; }
        public uint From { get { return Packet.From; } }
        public DateTime ReceivedAtUtc { get; private set; }
        public DateTime? TelemetryTimeUtc { get { return Telemetry.Time == 0 ? (DateTime?)null : new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Telemetry.Time); } }
        public string Type { get { return Telemetry.VariantCase.ToString(); } }
    }

    public enum MessageDeliveryState { QueuedAtDevice, Delivered, Failed }

    public sealed class MessageDeliveryEventArgs : EventArgs
    {
        internal MessageDeliveryEventArgs(uint packetId, MessageDeliveryState state, Routing.Types.Error error)
        {
            PacketId = packetId; State = state; Error = error;
        }
        public uint PacketId { get; private set; }
        public MessageDeliveryState State { get; private set; }
        public Routing.Types.Error Error { get; private set; }
    }

    public sealed class DeviceInfo
    {
        public MyNodeInfo MyNode { get; internal set; }
        public DeviceMetadata Metadata { get; internal set; }
        public MeshNode LocalNode { get; internal set; }
        public string NodeId { get { return LocalNode == null ? null : LocalNode.Id; } }
        public uint? BatteryLevel { get { return LocalNode == null ? (uint?)null : LocalNode.BatteryLevel; } }
        public double? Latitude { get { return LocalNode == null ? (double?)null : LocalNode.Latitude; } }
        public double? Longitude { get { return LocalNode == null ? (double?)null : LocalNode.Longitude; } }
    }
}
