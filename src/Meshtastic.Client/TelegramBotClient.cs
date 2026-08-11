using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Meshtastic.Client
{
    public enum TelegramConnectionState { Stopped, Connecting, Connected, Reconnecting }

    public sealed class TelegramChat
    {
        public long Id { get; internal set; }
        public string Type { get; internal set; }
        public string Title { get; internal set; }
        public string Username { get; internal set; }
    }

    public sealed class TelegramMessage
    {
        public long ChatId { get; internal set; }
        public string ChatTitle { get; internal set; }
        public long MessageId { get; internal set; }
        public long FromUserId { get; internal set; }
        public string FromName { get; internal set; }
        public string FromUsername { get; internal set; }
        public string Text { get; internal set; }
        public DateTime ReceivedAtUtc { get; internal set; }
        public bool IsGroupMessage { get; internal set; }
    }

    public sealed class TelegramMessageEventArgs : EventArgs
    {
        public TelegramMessageEventArgs(TelegramMessage message) { Message = message; }
        public TelegramMessage Message { get; private set; }
    }

    public sealed class TelegramConnectionStateChangedEventArgs : EventArgs
    {
        public TelegramConnectionStateChangedEventArgs(TelegramConnectionState state, Exception error) { State = state; Error = error; }
        public TelegramConnectionState State { get; private set; }
        public Exception Error { get; private set; }
    }

    public sealed class TelegramPollingErrorEventArgs : EventArgs
    {
        public TelegramPollingErrorEventArgs(Exception error) { Error = error; }
        public Exception Error { get; private set; }
    }

    /// <summary>
    /// Small Telegram Bot API client using HTTPS long polling. It receives text messages
    /// through MessageReceived and can send replies to private chats and groups.
    /// </summary>
    public sealed class TelegramBotClient : IDisposable
    {
        private const string ApiBaseUrl = "https://api.telegram.org/bot";
        private readonly object _gate = new object();
        private readonly HttpClient _http;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private CancellationTokenSource _pollCancellation;
        private Task _pollTask;
        private long? _nextUpdateId;
        private TelegramConnectionState _state = TelegramConnectionState.Stopped;

        public TelegramBotClient(string botToken)
        {
            if (String.IsNullOrWhiteSpace(botToken)) throw new ArgumentException("A Telegram bot token is required.", "botToken");
            BotToken = botToken.Trim();
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        }

        public string BotToken { get; private set; }
        public string BotUsername { get; private set; }
        public int ReconnectDelayMilliseconds { get; set; } = 3000;
        public TelegramConnectionState State { get { lock (_gate) return _state; } }
        public bool IsRunning { get { return State != TelegramConnectionState.Stopped; } }

        public event EventHandler<TelegramMessageEventArgs> MessageReceived;
        public event EventHandler<TelegramConnectionStateChangedEventArgs> ConnectionStateChanged;
        public event EventHandler<TelegramPollingErrorEventArgs> PollingError;

        /// <summary>Validates the token and starts background long polling. Do not use this together with a Telegram webhook.</summary>
        public async Task StartAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            lock (_gate)
            {
                if (_pollCancellation != null) return;
                _pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }
            try
            {
                SetState(TelegramConnectionState.Connecting, null);
                await GetMeAsync(_pollCancellation.Token).ConfigureAwait(false);
                SetState(TelegramConnectionState.Connected, null);
                lock (_gate) _pollTask = Task.Run(() => PollAsync(_pollCancellation.Token));
            }
            catch
            {
                lock (_gate) { _pollCancellation.Dispose(); _pollCancellation = null; }
                SetState(TelegramConnectionState.Stopped, null);
                throw;
            }
        }

        public async Task StopAsync()
        {
            CancellationTokenSource cancellation;
            Task pollTask;
            lock (_gate) { cancellation = _pollCancellation; pollTask = _pollTask; _pollCancellation = null; _pollTask = null; }
            if (cancellation == null) return;
            cancellation.Cancel();
            try { if (pollTask != null) await pollTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { cancellation.Dispose(); SetState(TelegramConnectionState.Stopped, null); }
        }

        public async Task<TelegramMessage> SendTextAsync(long chatId, string text, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (String.IsNullOrWhiteSpace(text)) throw new ArgumentException("A message text is required.", "text");
            if (text.Length > 4096) throw new ArgumentException("Telegram text messages are limited to 4096 characters.", "text");
            var result = await PostAsync("sendMessage", new Dictionary<string, string> { { "chat_id", chatId.ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "text", text } }, cancellationToken).ConfigureAwait(false);
            return ReadMessage(AsDictionary(result));
        }

        public async Task<TelegramChat> GetChatAsync(long chatId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = await PostAsync("getChat", new Dictionary<string, string> { { "chat_id", chatId.ToString(System.Globalization.CultureInfo.InvariantCulture) } }, cancellationToken).ConfigureAwait(false);
            return ReadChat(AsDictionary(result));
        }

        private async Task GetMeAsync(CancellationToken cancellationToken)
        {
            var result = AsDictionary(await PostAsync("getMe", null, cancellationToken).ConfigureAwait(false));
            BotUsername = Text(result, "username");
        }

        private async Task PollAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var values = new Dictionary<string, string> { { "timeout", "25" }, { "allowed_updates", "[\"message\"]" } };
                    if (_nextUpdateId.HasValue) values.Add("offset", _nextUpdateId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var result = await PostAsync("getUpdates", values, cancellationToken).ConfigureAwait(false);
                    foreach (var update in AsEnumerable(result)) ProcessUpdate(AsDictionary(update));
                    SetState(TelegramConnectionState.Connected, null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    SetState(TelegramConnectionState.Reconnecting, ex);
                    Raise(PollingError, new TelegramPollingErrorEventArgs(ex));
                    try { await Task.Delay(Math.Max(250, ReconnectDelayMilliseconds), cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private void ProcessUpdate(IDictionary<string, object> update)
        {
            if (update == null) return;
            var updateId = Number(update, "update_id");
            if (updateId.HasValue) _nextUpdateId = updateId.Value + 1;
            var message = ReadMessage(AsDictionary(Value(update, "message")));
            if (message != null && !String.IsNullOrEmpty(message.Text)) Raise(MessageReceived, new TelegramMessageEventArgs(message));
        }

        private async Task<object> PostAsync(string method, IDictionary<string, string> values, CancellationToken cancellationToken)
        {
            using (var content = new FormUrlEncodedContent(values ?? new Dictionary<string, string>()))
            using (var response = await _http.PostAsync(ApiBaseUrl + BotToken + "/" + method, content, cancellationToken).ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                IDictionary<string, object> envelope;
                try { envelope = AsDictionary(_json.DeserializeObject(body)); }
                catch (Exception ex) { throw new InvalidDataException("Telegram returned invalid JSON.", ex); }
                if (!response.IsSuccessStatusCode || envelope == null || !BooleanValue(envelope, "ok"))
                    throw new InvalidOperationException("Telegram " + method + " failed: " + (Text(envelope, "description") ?? response.ReasonPhrase));
                return Value(envelope, "result");
            }
        }

        private static TelegramMessage ReadMessage(IDictionary<string, object> source)
        {
            if (source == null) return null;
            var chat = ReadChat(AsDictionary(Value(source, "chat")));
            if (chat == null) return null;
            var from = AsDictionary(Value(source, "from"));
            var date = Number(source, "date");
            return new TelegramMessage
            {
                ChatId = chat.Id, ChatTitle = chat.Title, MessageId = Number(source, "message_id").GetValueOrDefault(),
                FromUserId = Number(from, "id").GetValueOrDefault(), FromName = JoinName(from), FromUsername = Text(from, "username"),
                Text = Text(source, "text"), ReceivedAtUtc = date.HasValue ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(date.Value) : DateTime.UtcNow,
                IsGroupMessage = chat.Type == "group" || chat.Type == "supergroup"
            };
        }

        private static TelegramChat ReadChat(IDictionary<string, object> source)
        {
            var id = Number(source, "id");
            if (source == null || !id.HasValue) return null;
            var title = Text(source, "title");
            if (String.IsNullOrEmpty(title)) title = JoinName(source);
            return new TelegramChat { Id = id.Value, Type = Text(source, "type"), Title = title, Username = Text(source, "username") };
        }

        private static string JoinName(IDictionary<string, object> source)
        {
            var first = Text(source, "first_name"); var last = Text(source, "last_name");
            return String.IsNullOrEmpty(first) ? last : String.IsNullOrEmpty(last) ? first : first + " " + last;
        }
        private static IDictionary<string, object> AsDictionary(object value) { return value as IDictionary<string, object>; }
        private static IEnumerable AsEnumerable(object value) { return value as IEnumerable ?? new object[0]; }
        private static object Value(IDictionary<string, object> source, string name) { object value; return source != null && source.TryGetValue(name, out value) ? value : null; }
        private static string Text(IDictionary<string, object> source, string name) { var value = Value(source, name); return value == null ? null : Convert.ToString(value); }
        private static long? Number(IDictionary<string, object> source, string name) { var value = Value(source, name); return value == null ? (long?)null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture); }
        private static bool BooleanValue(IDictionary<string, object> source, string name) { var value = Value(source, name); return value != null && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture); }
        private void SetState(TelegramConnectionState state, Exception error) { bool changed; lock (_gate) { changed = _state != state; _state = state; } if (changed || error != null) Raise(ConnectionStateChanged, new TelegramConnectionStateChangedEventArgs(state, error)); }
        private static void Raise<T>(EventHandler<T> handler, T args) where T : EventArgs { if (handler != null) handler(null, args); }
        public void Dispose() { StopAsync().GetAwaiter().GetResult(); _http.Dispose(); }
    }
}
