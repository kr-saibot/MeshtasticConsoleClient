using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Meshtastic.Client
{
    public sealed class TelegramGatewayStatus
    {
        public int ChannelIndex { get; internal set; }
        public bool Enabled { get; internal set; }
        public long ChatId { get; internal set; }
        public TelegramConnectionState State { get; internal set; }
        public string BotUsername { get; internal set; }
        public string LastError { get; internal set; }
        public string ServerFeedback { get; internal set; }
        public long ForwardedToTelegramCount { get; internal set; }
        public long ReceivedFromTelegramCount { get; internal set; }
        public long ForwardedToMeshtasticCount { get; internal set; }
    }

    public sealed class TelegramGatewayStatusChangedEventArgs : EventArgs
    {
        public TelegramGatewayStatusChangedEventArgs(TelegramGatewayStatus status) { Status = status; }
        public TelegramGatewayStatus Status { get; private set; }
    }

    /// <summary>Raised after a Telegram message has been queued at the local Meshtastic device.</summary>
    public sealed class TelegramGatewayMessageForwardedEventArgs : EventArgs
    {
        public TelegramGatewayMessageForwardedEventArgs(int channelIndex, uint packetId, string text)
        {
            ChannelIndex = channelIndex; PacketId = packetId; Text = text;
        }
        public int ChannelIndex { get; private set; }
        public uint PacketId { get; private set; }
        public string Text { get; private set; }
    }

    /// <summary>Bidirectionally bridges configured Meshtastic channels and Telegram chats.</summary>
    public sealed class MeshtasticTelegramGatewayManager : IDisposable
    {
        private sealed class Route
        {
            public TelegramGatewaySettings Settings;
            public TelegramBotClient Bot;
            public TelegramGatewayStatus Status;
        }

        private readonly object _gate = new object();
        private readonly MeshtasticClient _mesh;
        private readonly IList<TelegramGatewaySettings> _settings;
        private readonly List<Route> _routes = new List<Route>();
        private bool _started;

        public MeshtasticTelegramGatewayManager(MeshtasticClient mesh, IEnumerable<TelegramGatewaySettings> settings)
        {
            _mesh = mesh ?? throw new ArgumentNullException("mesh");
            _settings = (settings ?? Enumerable.Empty<TelegramGatewaySettings>()).Where(x => x != null).ToList();
        }

        public event EventHandler<TelegramGatewayStatusChangedEventArgs> StatusChanged;
        public event EventHandler<TelegramGatewayMessageForwardedEventArgs> TelegramMessageForwarded;

        public IList<TelegramGatewayStatus> GetStatuses()
        {
            lock (_gate) return _routes.Select(route => Copy(route.Status)).OrderBy(status => status.ChannelIndex).ToList();
        }

        public async Task StartAsync()
        {
            lock (_gate) { if (_started) return; _started = true; }
            try
            {
                var botByToken = new Dictionary<string, TelegramBotClient>(StringComparer.Ordinal);
                foreach (var setting in _settings.Where(x => x.Enabled))
                {
                    var status = new TelegramGatewayStatus { ChannelIndex = setting.ChannelIndex, Enabled = true, ChatId = setting.ChatId, State = TelegramConnectionState.Stopped };
                    if (String.IsNullOrWhiteSpace(setting.BotToken) || setting.ChatId == 0)
                    {
                        status.LastError = "Bot token and chat ID are required.";
                        AddRoute(new Route { Settings = setting, Status = status });
                        continue;
                    }
                    TelegramBotClient bot;
                    if (!botByToken.TryGetValue(setting.BotToken, out bot))
                    {
                        bot = new TelegramBotClient(setting.BotToken);
                        bot.ConnectionStateChanged += delegate(object sender, TelegramConnectionStateChangedEventArgs e) { UpdateBotStatus(bot, e.State, e.Error); };
                        bot.MessageReceived += delegate(object sender, TelegramMessageEventArgs e) { _ = ForwardTelegramToMeshtasticAsync(bot, e.Message); };
                        botByToken.Add(setting.BotToken, bot);
                    }
                    AddRoute(new Route { Settings = setting, Bot = bot, Status = status });
                }
                _mesh.MessageReceived += MeshMessageReceived;
                foreach (var bot in botByToken.Values)
                {
                    try { await bot.StartAsync().ConfigureAwait(false); }
                    catch (Exception ex) { UpdateBotStatus(bot, TelegramConnectionState.Stopped, ex); }
                }
            }
            catch
            {
                await StopAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task StopAsync()
        {
            List<TelegramBotClient> bots;
            lock (_gate)
            {
                if (!_started) return;
                _started = false;
                _mesh.MessageReceived -= MeshMessageReceived;
                bots = _routes.Where(route => route.Bot != null).Select(route => route.Bot).Distinct().ToList();
            }
            foreach (var bot in bots) { try { await bot.StopAsync().ConfigureAwait(false); } catch { } bot.Dispose(); }
            lock (_gate) { foreach (var route in _routes) { route.Status.State = TelegramConnectionState.Stopped; RaiseStatus(route.Status); } _routes.Clear(); }
        }

        private void AddRoute(Route route)
        {
            lock (_gate) _routes.Add(route);
            RaiseStatus(route.Status);
        }

        private void MeshMessageReceived(object sender, MeshMessageEventArgs e)
        {
            if (!e.Message.IsChannelMessage || !e.Message.ChannelIndex.HasValue || String.IsNullOrWhiteSpace(e.Message.Text)) return;
            var ownNode = _mesh.Device.MyNode == null ? (uint?)null : _mesh.Device.MyNode.MyNodeNum;
            // The device can echo packets sent by the bridge. Do not send those back to Telegram.
            if (ownNode.HasValue && e.Message.From == ownNode.Value) return;
            List<Route> routes;
            lock (_gate) routes = _routes.Where(route => route.Bot != null && route.Settings.ForwardMeshtasticToTelegram && route.Settings.ChannelIndex == (int)e.Message.ChannelIndex.Value).ToList();
            foreach (var route in routes) _ = ForwardMeshtasticToTelegramAsync(route, e.Message);
        }

        private async Task ForwardMeshtasticToTelegramAsync(Route route, MeshMessage message)
        {
            try
            {
                var node = _mesh.Nodes.FirstOrDefault(candidate => candidate.Number == message.From);
                var id = node == null || String.IsNullOrWhiteSpace(node.Id) ? "!" + message.From.ToString("x8") : node.Id;
                var name = node == null ? null : (!String.IsNullOrWhiteSpace(node.LongName) ? node.LongName : node.ShortName);
                var source = String.IsNullOrWhiteSpace(name) ? id : name + " (" + id + ")";
                await route.Bot.SendTextAsync(route.Settings.ChatId, "[" + source + "]\n" + message.Text).ConfigureAwait(false);
                lock (_gate) { route.Status.ForwardedToTelegramCount++; route.Status.LastError = null; AppendServerFeedback(route.Status, "Telegram accepted sendMessage for chat " + route.Settings.ChatId + "."); RaiseStatus(route.Status); }
            }
            catch (Exception ex) { SetRouteError(route, ex); }
        }

        private async Task ForwardTelegramToMeshtasticAsync(TelegramBotClient bot, TelegramMessage message)
        {
            List<Route> routes;
            lock (_gate) routes = _routes.Where(route => route.Bot == bot && route.Settings.ForwardTelegramToMeshtastic && route.Settings.ChatId == message.ChatId).ToList();
            foreach (var route in routes)
            {
                try
                {
                    lock (_gate) { route.Status.ReceivedFromTelegramCount++; RaiseStatus(route.Status); }
                    var user = !String.IsNullOrWhiteSpace(message.FromUsername) ? "@" + message.FromUsername : (message.FromName ?? "unknown user");
                    var deviceId = _mesh.Device.NodeId ?? (_mesh.Device.MyNode == null ? "unknown device" : "!" + _mesh.Device.MyNode.MyNodeNum.ToString("x8"));
                    var text = "[" + user + " via " + deviceId + "] " + message.Text;
                    var packetId = await _mesh.SendTextToChannelWithIdAsync(route.Settings.ChannelIndex, text).ConfigureAwait(false);
                    lock (_gate) { route.Status.ForwardedToMeshtasticCount++; route.Status.LastError = null; AppendServerFeedback(route.Status, "Telegram message was forwarded to Meshtastic channel " + route.Settings.ChannelIndex + "."); RaiseStatus(route.Status); }
                    var forwarded = TelegramMessageForwarded;
                    if (forwarded != null) forwarded(this, new TelegramGatewayMessageForwardedEventArgs(route.Settings.ChannelIndex, packetId, text));
                }
                catch (Exception ex) { SetRouteError(route, ex); }
            }
        }

        private void UpdateBotStatus(TelegramBotClient bot, TelegramConnectionState state, Exception error)
        {
            lock (_gate)
            {
                foreach (var route in _routes.Where(route => route.Bot == bot))
                {
                    route.Status.State = state; route.Status.BotUsername = bot.BotUsername;
                    if (error != null) { route.Status.LastError = error.Message; AppendServerFeedback(route.Status, error.Message); }
                    else if (state == TelegramConnectionState.Connected) AppendServerFeedback(route.Status, "Telegram connected" + (String.IsNullOrWhiteSpace(bot.BotUsername) ? "." : " as @" + bot.BotUsername + "."));
                    RaiseStatus(route.Status);
                }
            }
        }
        private void SetRouteError(Route route, Exception error) { lock (_gate) { route.Status.LastError = error.Message; AppendServerFeedback(route.Status, error.Message); RaiseStatus(route.Status); } }
        private static void AppendServerFeedback(TelegramGatewayStatus status, string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return;
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
            var lines = ((status.ServerFeedback ?? "") + (String.IsNullOrWhiteSpace(status.ServerFeedback) ? "" : "\n") + line).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            status.ServerFeedback = String.Join("\n", lines.Skip(Math.Max(0, lines.Length - 30)));
        }
        private void RaiseStatus(TelegramGatewayStatus status) { var handler = StatusChanged; if (handler != null) handler(this, new TelegramGatewayStatusChangedEventArgs(Copy(status))); }
        private static TelegramGatewayStatus Copy(TelegramGatewayStatus value) { return new TelegramGatewayStatus { ChannelIndex = value.ChannelIndex, Enabled = value.Enabled, ChatId = value.ChatId, State = value.State, BotUsername = value.BotUsername, LastError = value.LastError, ServerFeedback = value.ServerFeedback, ForwardedToTelegramCount = value.ForwardedToTelegramCount, ReceivedFromTelegramCount = value.ReceivedFromTelegramCount, ForwardedToMeshtasticCount = value.ForwardedToMeshtasticCount }; }
        public void Dispose() { StopAsync().GetAwaiter().GetResult(); }
    }
}
