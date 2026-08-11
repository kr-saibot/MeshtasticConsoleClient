using System;
using System.Globalization;
using System.Threading.Tasks;
using Meshtastic.Client;

namespace Meshtastic.Sample
{
    internal static class Program
    {
        private static async Task MainAsync()
        {
            using (var store = MeshtasticMessageStore.CreateSqlite("meshtastic-messages.db"))
            using (var mesh = new MeshtasticClient())
            {
                store.Initialize();
                mesh.DebugOutput = Console.WriteLine;
                mesh.ConnectionStateChanged += delegate(object sender, ConnectionStateChangedEventArgs e)
                {
                    Console.WriteLine("Connection: " + e.State + (e.Error == null ? "" : " (" + e.Error.Message + ")"));
                };
                mesh.MessageReceived += delegate(object sender, MeshMessageEventArgs e)
                {
                    var destination = e.Message.IsDirectMessage ? "DM" : "Channel " + ChannelName(mesh, e.Message.ChannelIndex.GetValueOrDefault());
                    Console.WriteLine("[" + e.Message.ReceivedAtUtc.ToLocalTime().ToString("T") + "][" + destination + "] " + NodeName(mesh, e.Message.From) + ": " + e.Message.Text);
                    var node = FindNode(mesh, e.Message.From);
                    var isOwnEcho = mesh.Device.MyNode != null && e.Message.From == mesh.Device.MyNode.MyNodeNum;
                    // The native reader must never wait for disk I/O. In particular, a locked or
                    // unavailable SQLite file must not stop delivery of subsequent radio packets.
                    SaveToDatabase(delegate { store.AddIncoming(e.Message, node == null ? (double?)null : node.Latitude, node == null ? (double?)null : node.Longitude, !isOwnEcho); });
                };
                mesh.MessageDeliveryChanged += delegate(object sender, MessageDeliveryEventArgs e)
                {
                    Console.WriteLine("Send " + e.PacketId.ToString("x8") + ": " + e.State + (e.Error == Meshtastic.Protobufs.Routing.Types.Error.None ? "" : " (" + e.Error + ")"));
                    SaveToDatabase(delegate { store.UpdateDeliveryStatus(e.PacketId, e.State, e.Error == Meshtastic.Protobufs.Routing.Types.Error.None ? null : e.Error.ToString()); });
                };
                mesh.PacketReceived += delegate(object sender, PacketEventArgs e)
                {
                    if (e.Packet.PayloadVariantCase == Meshtastic.Protobufs.MeshPacket.PayloadVariantOneofCase.Decoded &&
                        e.Packet.Decoded.Portnum != Meshtastic.Protobufs.PortNum.TextMessageApp)
                        Console.WriteLine("Packet from " + NodeName(mesh, e.Packet.From) + ": " + e.Packet.Decoded.Portnum);
                };
                mesh.NodeDiscovered += delegate(object sender, NodeEventArgs e) { Console.WriteLine("Node: " + e.Node.Id + " " + e.Node.LongName); SaveToDatabase(delegate { store.AddOrUpdateNode(e.Node); }); };
                mesh.NodeUpdated += delegate(object sender, NodeEventArgs e) { SaveToDatabase(delegate { store.AddOrUpdateNode(e.Node); }); };
                mesh.TelemetryReceived += delegate(object sender, MeshTelemetryEventArgs e)
                {
                    Console.WriteLine("Telemetry from " + NodeName(mesh, e.Telemetry.From) + ": " + e.Telemetry.Type);
                    SaveToDatabase(delegate { store.AddTelemetry(e.Telemetry); });
                };
                mesh.TransportError += delegate(object sender, TransportErrorEventArgs e) { Console.WriteLine("Transport: " + e.Error.Message); };

                const string settingsFile = "meshtastic-settings.xml";
                var settings = MeshtasticSettingsStore.Load(settingsFile);
                MeshtasticSettingsStore.Save(settingsFile, settings); // creates a readable template on first start
                mesh.MaximumConnectionAttempts = 5;
                Task connectingTask = ConnectUsingSettingsAsync(mesh, settings);
                PrintHelp();

                while (true)
                {
                    Console.Write("> "); var line = Console.ReadLine();
                    if (line == null || line.Equals("/quit", StringComparison.OrdinalIgnoreCase)) break;
                    if (String.IsNullOrWhiteSpace(line) || line.Equals("/help", StringComparison.OrdinalIgnoreCase)) { PrintHelp(); continue; }
                    if (line.Equals("/status", StringComparison.OrdinalIgnoreCase)) { PrintConnectionStatus(mesh); continue; }
                    if (line.Equals("/settings", StringComparison.OrdinalIgnoreCase)) { PrintSettings(settings); continue; }
                    if (line.StartsWith("/settings serial ", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Substring(17).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); int baud = 0;
                        if (parts.Length >= 1 && parts.Length <= 2 && (parts.Length == 1 || Int32.TryParse(parts[1], out baud))) { settings.Connection.Transport = MeshtasticTransportType.Serial; settings.Connection.SerialPort = parts[0]; if (parts.Length == 2) settings.Connection.SerialBaudRate = baud; MeshtasticSettingsStore.Save(settingsFile, settings); Console.WriteLine("Serial settings saved. Use /connect."); } else Console.WriteLine("Use: /settings serial COM5 [115200]");
                        continue;
                    }
                    if (line.StartsWith("/settings tcp ", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Substring(14).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); int port;
                        if (parts.Length == 2 && Int32.TryParse(parts[1], out port)) { settings.Connection.Transport = MeshtasticTransportType.Tcp; settings.Connection.TcpHost = parts[0]; settings.Connection.TcpPort = port; MeshtasticSettingsStore.Save(settingsFile, settings); Console.WriteLine("TCP settings saved. Use /connect."); } else Console.WriteLine("Use: /settings tcp 192.168.1.10 4403");
                        continue;
                    }
                    if (line.Equals("/disconnect", StringComparison.OrdinalIgnoreCase)) { await mesh.DisconnectAsync(); Console.WriteLine("Disconnected."); continue; }
                    if (line.Equals("/connect", StringComparison.OrdinalIgnoreCase))
                    {
                        if (mesh.State != ConnectionState.Disconnected) Console.WriteLine("A connection attempt is already active. Use /disconnect first.");
                        else { connectingTask = ConnectUsingSettingsAsync(mesh, settings); Console.WriteLine("Connecting in background (maximum 5 attempts)..."); }
                        continue;
                    }
                    if (line.Equals("/db stats", StringComparison.OrdinalIgnoreCase)) { PrintDatabaseStatistics(store, mesh); continue; }
                    if (line.Equals("/db nodes", StringComparison.OrdinalIgnoreCase)) { PrintDatabaseNodes(store.GetNodes(StoredNodeSort.Name)); continue; }
                    if (line.Equals("/db nodes id", StringComparison.OrdinalIgnoreCase)) { PrintDatabaseNodes(store.GetNodes(StoredNodeSort.NodeId)); continue; }
                    if (line.Equals("/db nodes last", StringComparison.OrdinalIgnoreCase)) { PrintDatabaseNodes(store.GetNodes(StoredNodeSort.LastReceived)); continue; }
                    if (line.StartsWith("/db telemetry ", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Substring(14).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); uint node; int maximum = 20;
                        if (parts.Length >= 1 && parts.Length <= 2 && TryNodeNumber(parts[0], out node) && (parts.Length == 1 || Int32.TryParse(parts[1], out maximum))) PrintTelemetry(store.GetTelemetry(node, maximum)); else Console.WriteLine("Use: /db telemetry !a1b2c3d4 [count]");
                        continue;
                    }
                    if (line.Equals("/db read-all", StringComparison.OrdinalIgnoreCase)) { store.MarkAllMessagesRead(); Console.WriteLine("All messages marked as read."); continue; }
                    if (line.Equals("/db delete-all", StringComparison.OrdinalIgnoreCase)) { store.DeleteAllMessages(); Console.WriteLine("All messages deleted."); continue; }
                    if (line.Equals("/db delete-nodes", StringComparison.OrdinalIgnoreCase)) { store.DeleteAllNodes(); Console.WriteLine("All nodes deleted."); continue; }
                    if (line.StartsWith("/db delete-node ", StringComparison.OrdinalIgnoreCase))
                    {
                        uint node; if (TryNodeNumber(line.Substring(16), out node)) { store.DeleteNode(node); Console.WriteLine("Node deleted."); } else Console.WriteLine("Use: /db delete-node !a1b2c3d4");
                        continue;
                    }
                    if (line.StartsWith("/db favorite ", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Substring(13).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); uint node;
                        if (parts.Length == 2 && TryNodeNumber(parts[0], out node) && (parts[1].Equals("on", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("off", StringComparison.OrdinalIgnoreCase))) { store.SetNodeFavorite(node, parts[1].Equals("on", StringComparison.OrdinalIgnoreCase)); Console.WriteLine("Favorite updated."); } else Console.WriteLine("Use: /db favorite !a1b2c3d4 on|off");
                        continue;
                    }
                    if (line.Equals("/db channel", StringComparison.OrdinalIgnoreCase)) { PrintDatabaseMessages(store.GetChannelMessages()); continue; }
                    if (line.StartsWith("/db channel ", StringComparison.OrdinalIgnoreCase))
                    {
                        int channel; if (Int32.TryParse(line.Substring(12), out channel)) PrintDatabaseMessages(store.GetChannelMessages(channel)); else Console.WriteLine("Use: /db channel <index>");
                        continue;
                    }
                    if (line.StartsWith("/db dm ", StringComparison.OrdinalIgnoreCase))
                    {
                        uint node; if (TryNodeNumber(line.Substring(7), out node)) PrintDatabaseMessages(store.GetDirectMessages(node)); else Console.WriteLine("Use: /db dm !a1b2c3d4");
                        continue;
                    }
                    if (line.StartsWith("/db unread channel ", StringComparison.OrdinalIgnoreCase))
                    {
                        int channel; if (Int32.TryParse(line.Substring(19), out channel)) Console.WriteLine("New messages in channel " + channel + ": " + store.CountNewChannelMessages(channel)); else Console.WriteLine("Use: /db unread channel <index>");
                        continue;
                    }
                    if (line.StartsWith("/db unread dm ", StringComparison.OrdinalIgnoreCase))
                    {
                        uint node; if (TryNodeNumber(line.Substring(14), out node)) Console.WriteLine("New direct messages for !" + node.ToString("x8") + ": " + store.CountNewDirectMessages(node)); else Console.WriteLine("Use: /db unread dm !a1b2c3d4");
                        continue;
                    }
                    if (line.StartsWith("/db read-channel ", StringComparison.OrdinalIgnoreCase))
                    {
                        int channel; if (Int32.TryParse(line.Substring(17), out channel)) { store.MarkChannelMessagesRead(channel); Console.WriteLine("Channel " + channel + " marked as read."); } else Console.WriteLine("Use: /db read-channel <index>");
                        continue;
                    }
                    if (line.StartsWith("/db read-dm ", StringComparison.OrdinalIgnoreCase))
                    {
                        uint node; if (TryNodeNumber(line.Substring(12), out node)) { store.MarkDirectMessagesRead(node); Console.WriteLine("Direct messages marked as read."); } else Console.WriteLine("Use: /db read-dm !a1b2c3d4");
                        continue;
                    }
                    if (line.Equals("/db delete-channel", StringComparison.OrdinalIgnoreCase)) { store.DeleteChannelMessages(); Console.WriteLine("All channel messages deleted."); continue; }
                    if (line.StartsWith("/db delete-channel ", StringComparison.OrdinalIgnoreCase))
                    {
                        int channel; if (Int32.TryParse(line.Substring(19), out channel)) { store.DeleteChannelMessages(channel); Console.WriteLine("Channel " + channel + " messages deleted."); } else Console.WriteLine("Use: /db delete-channel <index>");
                        continue;
                    }
                    if (line.StartsWith("/db delete-dm ", StringComparison.OrdinalIgnoreCase))
                    {
                        uint node; if (TryNodeNumber(line.Substring(14), out node)) { store.DeleteDirectMessages(node); Console.WriteLine("Direct messages deleted."); } else Console.WriteLine("Use: /db delete-dm !a1b2c3d4");
                        continue;
                    }
                    if (line.Equals("/nodes", StringComparison.OrdinalIgnoreCase)) { PrintNodes(mesh); continue; }
                    if (line.Equals("/channels", StringComparison.OrdinalIgnoreCase)) { PrintChannels(mesh); continue; }
                    if (line.Equals("/info", StringComparison.OrdinalIgnoreCase)) { PrintDevice(mesh); continue; }
                    if (line.Equals("/refresh", StringComparison.OrdinalIgnoreCase))
                    {
                        try { await mesh.RequestFullStateAsync(); PrintDevice(mesh); PrintChannels(mesh); PrintNodes(mesh); }
                        catch (TimeoutException ex) { Console.WriteLine("Refresh failed: " + ex.Message); }
                        continue;
                    }
                    if (line.StartsWith("/setpos ", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Substring(8).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        double latitude, longitude; int altitude;
                        if (parts.Length >= 2 && Double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out latitude) && Double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out longitude))
                        {
                            int? optionalAltitude = parts.Length > 2 && Int32.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out altitude) ? (int?)altitude : null;
                            await mesh.SetFixedPositionAsync(latitude, longitude, optionalAltitude);
                            Console.WriteLine("Fixed position sent. Use /refresh after a few seconds to display it.");
                            continue;
                        }
                    }
                    if (line.StartsWith("/all ", StringComparison.OrdinalIgnoreCase))
                    {
                        var text = line.Substring(5); var id = await mesh.SendBroadcastWithIdAsync(text); StoreOutgoing(store, mesh, id, UInt32.MaxValue, text, 0); continue;
                    }
                    if (line.StartsWith("/group ", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Substring(7).Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        int channelIndex;
                        if (parts.Length == 2 && Int32.TryParse(parts[0], out channelIndex)) { var id = await mesh.SendTextToChannelWithIdAsync(channelIndex, parts[1]); StoreOutgoing(store, mesh, id, UInt32.MaxValue, parts[1], channelIndex); continue; }
                    }
                    if (line.StartsWith("/to ", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Substring(4).Split(new[] { ' ' }, 2);
                        uint id;
                        if (parts.Length == 2 && TryNodeNumber(parts[0], out id)) { var packetId = await mesh.SendTextWithIdAsync(id, parts[1]); StoreOutgoing(store, mesh, packetId, id, parts[1], null); continue; }
                    }
                    Console.WriteLine("Unknown command.");
                }
                await mesh.DisconnectAsync();
            }
        }

        private static void PrintDevice(MeshtasticClient mesh)
        {
            var d = mesh.Device;
            Console.WriteLine("Device: " + (d.Metadata == null ? "(metadata pending)" : d.Metadata.FirmwareVersion) + ", node " + (d.NodeId ?? "(pending)") + ", battery " + (d.BatteryLevel.HasValue ? d.BatteryLevel + "%" : "n/a"));
            if (d.Latitude.HasValue) Console.WriteLine("Position: " + d.Latitude.Value.ToString(CultureInfo.InvariantCulture) + ", " + d.Longitude.Value.ToString(CultureInfo.InvariantCulture));
        }
        private static async Task ConnectUsingSettingsAsync(MeshtasticClient mesh, MeshtasticApplicationSettings settings)
        {
            try
            {
                if (settings.Connection.Transport == MeshtasticTransportType.Serial)
                {
                    Console.WriteLine("Connecting to " + settings.Connection.SerialPort + " (maximum 5 attempts)...");
                    await mesh.ConnectSerialAsync(settings.Connection.SerialPort, settings.Connection.SerialBaudRate);
                }
                else
                {
                    Console.WriteLine("Connecting to " + settings.Connection.TcpHost + ":" + settings.Connection.TcpPort + " (maximum 5 attempts)...");
                    await mesh.ConnectTcpAsync(settings.Connection.TcpHost, settings.Connection.TcpPort);
                }
                await mesh.RequestFullStateAsync();
                await mesh.ActivatePacketStreamingAsync();
                PrintDevice(mesh); PrintChannels(mesh); PrintNodes(mesh);
            }
            catch (Exception ex)
            {
                try { await mesh.DisconnectAsync(); } catch { }
                Console.WriteLine("Connection stopped: " + ex.Message);
            }
        }
        private static void PrintSettings(MeshtasticApplicationSettings settings)
        {
            var c = settings.Connection;
            Console.WriteLine("Settings: " + c.Transport + ", serial " + c.SerialPort + " at " + c.SerialBaudRate + ", TCP " + c.TcpHost + ":" + c.TcpPort);
        }
        private static void PrintHelp()
        {
            Console.WriteLine("Commands: /connect, /disconnect, /settings, /settings serial COM5 [115200], /settings tcp <IP> <port>");
            Console.WriteLine("Mesh: /status, /nodes, /channels, /info, /refresh, /setpos <lat> <lon> [alt], /all <text>, /group <index> <text>, /to !a1b2c3d4 <text>");
            Console.WriteLine("Database: /db stats, /db read-all, /db delete-all, /db channel <index>, /db dm !node, /db unread channel <index>, /db unread dm !node, /db read-channel <index>, /db read-dm !node, /db delete-channel <index>, /db delete-dm !node");
            Console.WriteLine("Nodes DB: /db nodes [id|last], /db favorite !node on|off, /db delete-node !node, /db delete-nodes, /db telemetry !node [count], /quit");
            Console.WriteLine("Press Enter or use /help to show this text again.");
        }
        private static void PrintChannels(MeshtasticClient mesh)
        {
            Console.WriteLine("Channels:"); foreach (var channel in mesh.Channels) Console.WriteLine("  " + channel.Index + ": " + channel.Name + " (" + channel.Role + ")");
        }
        private static void PrintConnectionStatus(MeshtasticClient mesh)
        {
            var info = mesh.ConnectionInfo;
            Console.WriteLine("State: " + mesh.State);
            Console.WriteLine("Transport: " + (info.Transport ?? "n/a") + " (" + (info.Endpoint ?? "n/a") + ")");
            Console.WriteLine("Connected: " + info.CurrentConnectionDuration.ToString(@"d\.hh\:mm\:ss") + ", reconnects: " + info.ReconnectCount);
            Console.WriteLine("Frames: sent " + info.SentFrameCount + ", received " + info.ReceivedFrameCount);
            Console.WriteLine("Last I/O: sent " + (info.LastSentUtc.HasValue ? info.LastSentUtc.Value.ToLocalTime().ToString("T") : "n/a") + ", received " + (info.LastReceivedUtc.HasValue ? info.LastReceivedUtc.Value.ToLocalTime().ToString("T") : "n/a"));
        }
        private static void PrintDatabaseMessages(System.Collections.Generic.IList<StoredMeshMessage> messages)
        {
            if (messages.Count == 0) { Console.WriteLine("No database entries."); return; }
            foreach (var message in messages)
            {
                var target = message.Kind == StoredMessageKind.Channel ? "channel " + (message.ChannelName ?? Convert.ToString(message.ChannelIndex)) : "DM " + (message.Direction == MessageDirection.Incoming ? "from !" + (message.FromNode.HasValue ? message.FromNode.Value.ToString("x8") : "unknown") : "to !" + (message.ToNode.HasValue ? message.ToNode.Value.ToString("x8") : "unknown"));
                Console.WriteLine(message.OccurredUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " [" + message.Direction + "][" + target + "][" + (message.IsNew ? "NEW" : "read") + "][" + (message.DeliveryStatus ?? "-") + "] " + message.Text);
            }
        }
        private static void PrintDatabaseStatistics(MeshtasticMessageStore store, MeshtasticClient mesh)
        {
            Console.WriteLine("Direct messages by node:");
            var direct = store.GetDirectMessageStatistics();
            if (direct.Count == 0) Console.WriteLine("  none");
            foreach (var item in direct) Console.WriteLine("  " + NodeName(mesh, item.NodeNumber) + ": sent " + item.SentCount + ", received " + item.ReceivedCount + ", new " + item.NewCount);
            Console.WriteLine("Channel messages by channel:");
            var channels = store.GetChannelMessageStatistics();
            if (channels.Count == 0) Console.WriteLine("  none");
            foreach (var item in channels)
            {
                var name = item.ChannelName;
                if (String.IsNullOrEmpty(name) && item.ChannelIndex.HasValue) name = ChannelName(mesh, (uint)item.ChannelIndex.Value);
                Console.WriteLine("  " + (item.ChannelIndex.HasValue ? item.ChannelIndex.Value.ToString() : "unknown") + " (" + (name ?? "unnamed") + "): sent " + item.SentCount + ", received " + item.ReceivedCount + ", new " + item.NewCount);
            }
        }
        private static void PrintDatabaseNodes(System.Collections.Generic.IList<StoredMeshNode> nodes)
        {
            if (nodes.Count == 0) { Console.WriteLine("No stored nodes."); return; }
            foreach (var node in nodes)
            {
                Console.WriteLine((node.IsFavorite ? "* " : "  ") + (node.LongName ?? node.NodeId ?? "unknown") + " (" + (node.NodeId ?? "!" + node.NodeNumber.ToString("x8")) + "), last received " + node.LastReceivedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + (node.HopsAway.HasValue ? ", hops " + node.HopsAway.Value : ", hops n/a") + (node.BatteryLevel.HasValue ? ", battery " + node.BatteryLevel.Value + "%" : ""));
            }
        }
        private static void PrintTelemetry(System.Collections.Generic.IList<StoredTelemetry> telemetry)
        {
            if (telemetry.Count == 0) { Console.WriteLine("No telemetry entries."); return; }
            foreach (var item in telemetry)
            {
                Console.WriteLine(item.ReceivedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " [" + item.Type + "]" + (item.BatteryLevel.HasValue ? " battery " + item.BatteryLevel.Value + "%" : "") + (item.Voltage.HasValue ? " voltage " + item.Voltage.Value.ToString("F2", CultureInfo.InvariantCulture) + "V" : "") + (item.Temperature.HasValue ? " temperature " + item.Temperature.Value.ToString("F1", CultureInfo.InvariantCulture) + "C" : "") + (item.RelativeHumidity.HasValue ? " humidity " + item.RelativeHumidity.Value.ToString("F1", CultureInfo.InvariantCulture) + "%" : ""));
            }
        }
        private static void SaveToDatabase(Action action)
        {
            Task.Run(delegate
            {
                try { action(); }
                catch (Exception ex) { Console.WriteLine("Database error: " + ex.Message); }
            });
        }
        private static void PrintNodes(MeshtasticClient mesh)
        {
            Console.WriteLine("Nodes:");
            foreach (var node in mesh.Nodes)
            {
                var position = node.Latitude.HasValue ? node.Latitude.Value.ToString("F6", CultureInfo.InvariantCulture) + ", " + node.Longitude.Value.ToString("F6", CultureInfo.InvariantCulture) : "no GPS";
                var distance = mesh.GetDistanceToNodeMeters(node);
                var direction = mesh.GetDirectionToNode(node);
                Console.WriteLine("  " + node.Id + "  " + node.LongName + " (battery " + (node.BatteryLevel.HasValue ? node.BatteryLevel + "%" : "n/a") + ", GPS " + position + ", distance " + (distance.HasValue ? FormatDistance(distance.Value) : "n/a") + (direction == null ? "" : ", direction " + direction + " (" + mesh.GetBearingToNodeDegrees(node).Value.ToString("F0", CultureInfo.InvariantCulture) + "°)") + ")");
            }
        }
        private static string NodeName(MeshtasticClient mesh, uint number)
        {
            foreach (var node in mesh.Nodes) if (node.Number == number) return node.LongName ?? node.Id;
            return "!" + number.ToString("x8");
        }
        private static MeshNode FindNode(MeshtasticClient mesh, uint number)
        {
            foreach (var node in mesh.Nodes) if (node.Number == number) return node;
            return null;
        }
        private static void StoreOutgoing(MeshtasticMessageStore store, MeshtasticClient mesh, uint packetId, uint destination, string text, int? channelIndex)
        {
            string channelName = null;
            if (channelIndex.HasValue) channelName = ChannelName(mesh, (uint)channelIndex.Value);
            store.AddOutgoing(packetId, mesh.Device.MyNode == null ? 0u : mesh.Device.MyNode.MyNodeNum, destination, text, channelIndex, channelName, mesh.Device.Latitude, mesh.Device.Longitude);
        }
        private static string ChannelName(MeshtasticClient mesh, uint index)
        {
            foreach (var channel in mesh.Channels) if (channel.Index == index) return String.IsNullOrEmpty(channel.Name) ? index.ToString() : channel.Name;
            return index.ToString();
        }
        private static bool TryNodeNumber(string text, out uint value)
        {
            text = text.TrimStart('!'); return UInt32.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
        private static string FormatDistance(double meters) { return meters < 1000 ? Math.Round(meters) + " m" : (meters / 1000d).ToString("F2", CultureInfo.InvariantCulture) + " km"; }
        private static void Main(string[] args) { MainAsync().GetAwaiter().GetResult(); }
    }
}
