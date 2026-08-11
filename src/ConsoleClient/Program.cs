using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Meshtastic.Client;
using NStack;
using Terminal.Gui;

namespace ConsoleClient
{
    internal enum ChatKind { Channel, Direct }
    internal enum NodeSortMode { Name, NodeId, LastReceived, Distance, Hops, Signal }
    internal sealed class ChatItem
    {
        public ChatKind Kind;
        public uint Id;
        public string Name;
        public int NewCount;
        public override string ToString() { return (Kind == ChatKind.Channel ? "# " : "@ ") + Name + (NewCount > 0 ? " [" + NewCount + "]" : ""); }
    }

    internal sealed class ChatTranscriptView : View
    {
        private readonly List<Tuple<string, bool>> _lines = new List<Tuple<string, bool>>();
        private int _scrollOffset;
        public event Action ScrollPositionChanged;
        // These two values are deliberately separate so they can be bound to program settings later.
        public Color IncomingColor { get; set; } = Color.BrightCyan;
        public Color OutgoingColor { get; set; } = Color.BrightYellow;
        public Color EmojiTextColor { get; set; } = Color.BrightMagenta;
        public Color BackgroundColor { get; set; } = Color.Black;
        public Color NormalTextColor { get; set; } = Color.Gray;
        public void SetMessages(IEnumerable<StoredMeshMessage> messages, Func<StoredMeshMessage, string> formatter)
        {
            _lines.Clear();
            foreach (var message in messages) _lines.Add(Tuple.Create(formatter(message), message.Direction == MessageDirection.Outgoing));
            _scrollOffset = 0;
            SetNeedsDisplay();
            NotifyScrollPositionChanged();
        }
        public int MessageCount { get { return _lines.Count; } }
        public int FirstVisibleMessageNumber
        {
            get
            {
                if (_lines.Count == 0) return 0;
                var width = Math.Max(2, Bounds.Width);
                var height = Math.Max(1, Bounds.Height);
                var displayLines = new List<Tuple<string, bool>>();
                var messageNumbers = new List<int>();
                for (var messageIndex = 0; messageIndex < _lines.Count; messageIndex++)
                {
                    var lineCountBefore = displayLines.Count;
                    AddWrappedLines(displayLines, _lines[messageIndex], width - 1);
                    for (var lineIndex = lineCountBefore; lineIndex < displayLines.Count; lineIndex++) messageNumbers.Add(messageIndex);
                }
                var maximumOffset = Math.Max(0, displayLines.Count - height);
                var offset = Math.Min(_scrollOffset, maximumOffset);
                var start = Math.Max(0, displayLines.Count - height - offset);
                return messageNumbers.Count == 0 ? 0 : messageNumbers[Math.Min(start, messageNumbers.Count - 1)] + 1;
            }
        }
        public override void Redraw(Rect bounds)
        {
            base.Redraw(bounds);
            var width = Math.Max(2, Bounds.Width);
            var height = Math.Max(0, Bounds.Height);
            var displayLines = new List<Tuple<string, bool>>();
            foreach (var line in _lines) AddWrappedLines(displayLines, line, width - 1);
            for (var row = 0; row < height; row++)
            {
                Move(0, row);
                Application.Driver.SetAttribute(Application.Driver.MakeAttribute(NormalTextColor, BackgroundColor));
                Application.Driver.AddStr(new string(' ', width));
            }
            var maximumOffset = Math.Max(0, displayLines.Count - height);
            if (_scrollOffset > maximumOffset) _scrollOffset = maximumOffset;
            var start = Math.Max(0, displayLines.Count - height - _scrollOffset);
            for (var index = start; index < displayLines.Count && index - start < height; index++)
            {
                var row = index - start;
                var line = displayLines[index];
                Move(0, row);
                DrawMessageLine(line.Item1, line.Item2 ? OutgoingColor : IncomingColor);
            }
            if (displayLines.Count > height && height > 0)
            {
                var thumb = Math.Min(height - 1, (int)Math.Round((double)(height - 1) * (displayLines.Count - height - _scrollOffset) / Math.Max(1, displayLines.Count - height)));
                Move(width - 1, thumb); Application.Driver.SetAttribute(Application.Driver.MakeAttribute(Color.White, BackgroundColor)); Application.Driver.AddStr("█");
            }
        }
        public override bool ProcessKey(KeyEvent keyEvent)
        {
            if (keyEvent.Key == Key.CursorUp) { _scrollOffset++; SetNeedsDisplay(); NotifyScrollPositionChanged(); return true; }
            if (keyEvent.Key == Key.CursorDown) { _scrollOffset = Math.Max(0, _scrollOffset - 1); SetNeedsDisplay(); NotifyScrollPositionChanged(); return true; }
            if (keyEvent.Key == Key.PageUp) { _scrollOffset += Math.Max(1, Bounds.Height - 1); SetNeedsDisplay(); NotifyScrollPositionChanged(); return true; }
            if (keyEvent.Key == Key.PageDown) { _scrollOffset = Math.Max(0, _scrollOffset - Math.Max(1, Bounds.Height - 1)); SetNeedsDisplay(); NotifyScrollPositionChanged(); return true; }
            return base.ProcessKey(keyEvent);
        }
        private void NotifyScrollPositionChanged()
        {
            var handler = ScrollPositionChanged;
            if (handler != null) handler();
        }
        private void DrawMessageLine(string text, Color messageColor)
        {
            var position = 0;
            while (position < text.Length)
            {
                var emojiStart = text.IndexOf("[:", position, StringComparison.Ordinal);
                if (emojiStart < 0)
                {
                    DrawText(text.Substring(position), messageColor);
                    break;
                }
                if (emojiStart > position) DrawText(text.Substring(position, emojiStart - position), messageColor);
                var emojiEnd = text.IndexOf(":]", emojiStart + 2, StringComparison.Ordinal);
                if (emojiEnd < 0)
                {
                    DrawText(text.Substring(emojiStart), messageColor);
                    break;
                }
                DrawText(text.Substring(emojiStart, emojiEnd - emojiStart + 2), EmojiTextColor);
                position = emojiEnd + 2;
            }
        }
        private void DrawText(string text, Color foreground)
        {
            if (String.IsNullOrEmpty(text)) return;
            Application.Driver.SetAttribute(Application.Driver.MakeAttribute(foreground, BackgroundColor));
            Application.Driver.AddStr(text);
        }
        private static void AddWrappedLines(ICollection<Tuple<string, bool>> target, Tuple<string, bool> line, int width)
        {
            foreach (var part in line.Item1.Replace("\r", "").Split('\n'))
            {
                if (part.Length == 0) { target.Add(Tuple.Create("", line.Item2)); continue; }
                var current = new StringBuilder();
                var currentWidth = 0;
                var elements = StringInfo.GetTextElementEnumerator(part);
                while (elements.MoveNext())
                {
                    var element = (string)elements.Current;
                    var elementWidth = GetDisplayWidth(element);
                    if (currentWidth > 0 && currentWidth + elementWidth > width)
                    {
                        target.Add(Tuple.Create(current.ToString(), line.Item2));
                        current.Clear();
                        currentWidth = 0;
                    }
                    current.Append(element);
                    currentWidth += elementWidth;
                }
                if (current.Length > 0) target.Add(Tuple.Create(current.ToString(), line.Item2));
            }
        }

        // Terminal columns are not UTF-16 string positions. Emoji are often surrogate
        // pairs and normally occupy two terminal cells.
        private static int GetDisplayWidth(string textElement)
        {
            var hasVisibleCharacter = false;
            for (var index = 0; index < textElement.Length; index++)
            {
                var codePoint = Char.ConvertToUtf32(textElement, index);
                if (Char.IsHighSurrogate(textElement[index])) index++;
                if (codePoint == 0x200D || (codePoint >= 0xFE00 && codePoint <= 0xFE0F) || (codePoint >= 0x1F3FB && codePoint <= 0x1F3FF)) continue;
                var category = CharUnicodeInfo.GetUnicodeCategory(Char.ConvertFromUtf32(codePoint), 0);
                if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark || category == UnicodeCategory.Format) continue;
                hasVisibleCharacter = true;
                if ((codePoint >= 0x1100 && codePoint <= 0x115F) ||
                    (codePoint >= 0x2E80 && codePoint <= 0xA4CF) ||
                    (codePoint >= 0xAC00 && codePoint <= 0xD7A3) ||
                    (codePoint >= 0xF900 && codePoint <= 0xFAFF) ||
                    (codePoint >= 0xFE10 && codePoint <= 0xFE6F) ||
                    (codePoint >= 0xFF01 && codePoint <= 0xFF60) ||
                    (codePoint >= 0x1F000 && codePoint <= 0x1FAFF)) return 2;
            }
            return hasVisibleCharacter ? 1 : 0;
        }
    }

    internal sealed class MessageInputView : TextView
    {
        public override bool ProcessKey(KeyEvent keyEvent)
        {
            // Let the parent view process Tab so keyboard focus can leave the multiline editor.
            if (keyEvent.Key == Key.Tab) return false;
            return base.ProcessKey(keyEvent);
        }
    }

    [XmlRoot("EmojiReplacements")]
    public sealed class EmojiReplacementFile
    {
        [XmlElement("Emoji")]
        public List<EmojiReplacementEntry> Items { get; set; } = new List<EmojiReplacementEntry>();
    }

    public sealed class EmojiReplacementEntry
    {
        [XmlAttribute("value")]
        public string Value { get; set; }
        [XmlAttribute("text")]
        public string Text { get; set; }
        [XmlAttribute("category")]
        public string Category { get; set; }
        [XmlAttribute("subCategory")]
        public string SubCategory { get; set; }
    }

    internal static class Program
    {
        private static MeshtasticClient _mesh;
        private static MeshtasticMessageStore _store;
        private static MeshtasticApplicationSettings _settings;
        private static MeshtasticTelegramGatewayManager _telegramGateways;
        private static readonly HttpClient HttpBotHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly Dictionary<string, string> DefaultEmojiReplacements = new Dictionary<string, string>
        {
            { "\U0001F600", "[:grin:]" }, { "\U0001F603", "[:smile:]" }, { "\U0001F604", "[:smile:]" }, { "\U0001F60A", "[:smile:]" },
            { "\U0001F602", "[:laugh:]" }, { "\U0001F923", "[:rofl:]" }, { "\U0001F622", "[:sad:]" }, { "\U0001F62D", "[:cry:]" },
            { "\U0001F44D", "[:thumbs-up:]" }, { "\U0001F44E", "[:thumbs-down:]" }, { "\u2764\uFE0F", "[:heart:]" }, { "\u2764", "[:heart:]" },
            { "\U0001F525", "[:fire:]" }, { "\U0001F680", "[:rocket:]" }, { "\u2705", "[:ok:]" }, { "\u274C", "[:no:]" },
            { "\u26A0\uFE0F", "[:warning:]" }, { "\u26A0", "[:warning:]" }, { "\U0001F4CD", "[:location:]" }, { "\U0001F4E1", "[:antenna:]" }
        };
        private static readonly Dictionary<string, string> EmojiReplacements = new Dictionary<string, string>();
        private static readonly List<EmojiReplacementEntry> EmojiPickerEntries = new List<EmojiReplacementEntry>();
        private static string _lastChatBotRequest = "No local bot has run yet.";
        private static string _lastChatBotOutput = "No local bot has run yet.";
        private static string _lastHttpBotRequest = "No HTTP bot has run yet.";
        private static string _lastHttpBotOutput = "No HTTP bot has run yet.";
        private static ListView _channelList;
        private static ListView _directList;
        private static ChatTranscriptView _messages;
        private static FrameView _messagesFrame;
        private static MessageInputView _input;
        private static Label _header;
        private static Label _latestTelemetry;
        private static Label _nodeConnectionInfo;
        private static Label _status;
        private static Dialog _pleaseWait;
        private static Label _pleaseWaitText;
        private static ProgressBar _pleaseWaitProgress;
        private static Window _chatPage;
        private static Window _nodesPage;
        private static Window _telemetryPage;
        private static ListView _nodeList;
        private static ListView _telemetryList;
        private static Label _telemetryHeader;
        private static Label _telemetryScrollInfo;
        private static uint? _telemetryNodeFilter;
        private static readonly List<StoredTelemetry> TelemetryItems = new List<StoredTelemetry>();
        private static readonly List<StoredMeshNode> NodeItems = new List<StoredMeshNode>();
        private static readonly List<ChatItem> ChannelChats = new List<ChatItem>();
        private static readonly List<ChatItem> DirectChats = new List<ChatItem>();
        private static NodeSortMode _nodeSort = NodeSortMode.Name;
        private static bool _nodeSortAscending = true;
        private static bool _refreshingChatLists;
        private static bool _favoritesOnly;
        private static bool _nodeRefreshPending;
        private static Label _nodeScrollInfo;
        private static Button _sortButton;
        private static Button _nodeSortDirectionButton;
        private static TextField _nodeSearch;
        private static FrameView _logoFrame;
        private static FrameView _channelsFrame;
        private static FrameView _directChatsFrame;
        private static FrameView _nodeInfoFrame;
        private static FrameView _inputFrame;
        private static Label _logoText;
        private static readonly List<string> LogoFiles = new List<string>();
        private static int _logoIndex;
        private static DateTime _nextLogoChangeUtc;
        private static readonly List<ChatItem> Chats = new List<ChatItem>();
        private static ChatItem _selected;
        private static readonly List<FrameView> Frames = new List<FrameView>();
        private static readonly List<Window> PageFrames = new List<Window>();
        private static readonly List<Button> MainButtons = new List<Button>();

        private static void Main(string[] args)
        {
            try { Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = Encoding.UTF8; } catch { }
            LoadEmojiReplacements();
            _settings = MeshtasticSettingsStore.Load("meshtastic-settings.xml");
            MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings);
            _store = MeshtasticMessageStore.CreateSqlite("meshtastic-messages.db");
            _store.Initialize();
            _mesh = new MeshtasticClient { MaximumConnectionAttempts = 5 };
            SubscribeMeshEvents();

            Application.Init();
            BuildUi();
            // Start only after the first UI cycle so the waiting window can be drawn first.
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(500), delegate(MainLoop loop) { StartConnect(); return false; });
            Application.Run();
            try { MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings); } catch { }
            if (_telegramGateways != null) _telegramGateways.StopAsync().GetAwaiter().GetResult();
            _mesh.DisconnectAsync().GetAwaiter().GetResult();
            _mesh.Dispose();
            _store.Dispose();
            Application.Shutdown();
        }

        private static void BuildUi()
        {
            var top = Application.Top;
            var menu = new MenuBar(new[]
            {
                new MenuBarItem("_Chats", new[] { new MenuItem("_Show chats", "", ShowChatPage), new MenuItem("_Refresh", "", RefreshChats), new MenuItem("_Send", "", SendCurrentMessage), new MenuItem("_Delete active chat", "", DeleteActiveChat) }),
                new MenuBarItem("_Nodes", new[] { new MenuItem("_Show nodes", "", ShowNodesPage), new MenuItem("_Create node", "", CreateNode), new MenuItem("_Refresh from device", "", RefreshNodesFromDevice), new MenuItem("_Delete all nodes", "", DeleteAllNodes) }),
                new MenuBarItem("_Telemetry", new[] { new MenuItem("_Show telemetry", "", ShowAllTelemetry), new MenuItem("_Delete current node telemetry", "", DeleteCurrentNodeTelemetry), new MenuItem("_Delete all telemetry", "", DeleteAllTelemetry) }),
                new MenuBarItem("_Connection", new[] { new MenuItem("_Connect", "", StartConnect), new MenuItem("_Disconnect", "", Disconnect), new MenuItem("Connection _status", "", ShowConnectionStatus), new MenuItem("_Telegram gateway status", "", ShowTelegramGatewayStatus), new MenuItem("_Chat bot Debug", "", ShowChatBotDebug), new MenuItem("_HTTP bot Debug", "", ShowHttpBotDebug) }),
                new MenuBarItem("_Settings", new[] { new MenuItem("_Connection settings", "", ShowSettings), new MenuItem("_GPS position", "", ShowPositionSettings), new MenuItem("_Use device GPS", "", UseDeviceGps), new MenuItem("_Telegram Gateways", "", ShowTelegramGateways), new MenuItem("_Alerts", "", ShowAlertSettings), new MenuItem("_Appearance", "", ShowAppearanceSettings), new MenuItem("_Logo", "", ShowLogoSettings), new MenuItem("_Chat bots", "", ShowChatBots), new MenuItem("_HTTP bots", "", ShowHttpBots) }),
                new MenuBarItem("_Info", new[] { new MenuItem("_About", "", ShowInfo) }),
                new MenuBarItem("_Quit", new[] { new MenuItem("_Exit", "", RequestQuit) })
            }) { Key = Key.F10 };
            top.Add(menu);
            top.KeyPress += e =>
            {
                if (e.KeyEvent.Key == Key.F1)
                {
                    ShowChatPage();
                    _channelList.SetFocus();
                    e.Handled = true;
                }
                else if (e.KeyEvent.Key == Key.F2)
                {
                    ShowChatPage();
                    _directList.SetFocus();
                    e.Handled = true;
                }
                else if (e.KeyEvent.Key == Key.F3)
                {
                    ShowNodesPage();
                    _nodeList.SetFocus();
                    e.Handled = true;
                }
                else if (e.KeyEvent.Key == Key.F4)
                {
                    ShowAllTelemetry();
                    _telemetryList.SetFocus();
                    e.Handled = true;
                }
                else if (e.KeyEvent.Key == Key.F5)
                {
                    ShowChatPage();
                    _input.SetFocus();
                    e.Handled = true;
                }
                else if (e.KeyEvent.Key == Key.F6)
                {
                    ShowEmojiPicker();
                    e.Handled = true;
                }
                else if (e.KeyEvent.Key == Key.F10)
                {
                    menu.OpenMenu();
                    e.Handled = true;
                }
                else if (e.KeyEvent.Key == Key.F7)
                {
                    ShowPreviousLogo();
                    e.Handled = true;
                }
                else if (e.KeyEvent.Key == Key.F8)
                {
                    ShowNextLogo();
                    e.Handled = true;
                }
            };

            _chatPage = new Window("Chat") { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1) };
            UpdateChatPageTitle();
            PageFrames.Add(_chatPage);
            var sidebarOuterWidth = GetSidebarOuterWidth();
            _logoFrame = new FrameView("") { X = 0, Y = 0, Width = sidebarOuterWidth, Height = GetLogoOuterHeight() };
            _logoText = new Label("") { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            _logoFrame.Add(_logoText);
            Frames.Add(_logoFrame);
            _channelsFrame = new FrameView("Channels [F1]") { X = 0, Y = GetLogoOuterHeight(), Width = sidebarOuterWidth, Height = 10 };
            Frames.Add(_channelsFrame);
            _channelList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            _channelList.SelectedItemChanged += delegate(ListViewItemEventArgs e) { if (!_refreshingChatLists && e.Item >= 0 && e.Item < ChannelChats.Count) { _selected = ChannelChats[e.Item]; ShowSelectedChat(); } };
            _channelList.KeyPress += e => { if (e.KeyEvent.Key == Key.Enter && _channelList.SelectedItem >= 0) { _selected = ChannelChats[_channelList.SelectedItem]; ActivateSelectedChat(); e.Handled = true; } };
            _channelsFrame.Add(_channelList);
            _directChatsFrame = new FrameView("Direct chats [F2]") { X = 0, Y = Pos.Bottom(_channelsFrame), Width = sidebarOuterWidth, Height = Dim.Fill() };
            Frames.Add(_directChatsFrame);
            _directList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            _directList.SelectedItemChanged += delegate(ListViewItemEventArgs e) { if (!_refreshingChatLists && e.Item >= 0 && e.Item < DirectChats.Count) { _selected = DirectChats[e.Item]; ShowSelectedChat(); } };
            _directList.KeyPress += e => { if (e.KeyEvent.Key == Key.Enter && _directList.SelectedItem >= 0) { _selected = DirectChats[_directList.SelectedItem]; ActivateSelectedChat(); e.Handled = true; } };
            _directChatsFrame.Add(_directList);
            _nodeInfoFrame = new FrameView("Chat / node info") { X = sidebarOuterWidth + 1, Y = 0, Width = Dim.Fill(), Height = 5 };
            Frames.Add(_nodeInfoFrame);
            _header = new Label("Select a chat") { X = 0, Y = 0, Width = Dim.Fill(14) };
            _latestTelemetry = new Label("") { X = 0, Y = 1, Width = Dim.Fill() };
            _nodeConnectionInfo = new Label("") { X = 0, Y = 2, Width = Dim.Fill() };
            var chatInfo = new Button("Info") { X = Pos.AnchorEnd(8), Y = 0 };
            MainButtons.Add(chatInfo);
            chatInfo.Clicked += ShowCurrentChatNodeDetails;
            _nodeInfoFrame.Add(_header, _latestTelemetry, _nodeConnectionInfo, chatInfo);
            _messagesFrame = new FrameView("Messages") { X = sidebarOuterWidth + 1, Y = Pos.Bottom(_nodeInfoFrame), Width = Dim.Fill(), Height = Dim.Fill(4) };
            Frames.Add(_messagesFrame);
            _messages = new ChatTranscriptView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
            _messages.ScrollPositionChanged += UpdateMessageScrollPosition;
            _messagesFrame.Add(_messages);
            _inputFrame = new FrameView("New message [F5] emoji [F6]") { X = sidebarOuterWidth + 1, Y = Pos.AnchorEnd(4), Width = Dim.Fill(10), Height = 4 };
            Frames.Add(_inputFrame);
            _input = new MessageInputView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), WordWrap = true };
            _input.KeyPress += e => { if (e.KeyEvent.Key == Key.F6) { ShowEmojiPicker(); e.Handled = true; } };
            _inputFrame.Add(_input);
            var send = new Button("Send") { X = Pos.AnchorEnd(9), Y = Pos.AnchorEnd(2) };
            MainButtons.Add(send);
            send.Clicked += SendCurrentMessage;
            _chatPage.Add(_logoFrame, _channelsFrame, _directChatsFrame, _nodeInfoFrame, _messagesFrame, _inputFrame, send);
            top.Add(_chatPage);

            _nodesPage = new Window("Nodes") { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1), Visible = false };
            PageFrames.Add(_nodesPage);
            var nodeListFrame = new FrameView("Known nodes [F3]") { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(3) };
            Frames.Add(nodeListFrame);
            _nodeList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            _nodeList.SelectedItemChanged += delegate { UpdateNodeScrollInfo(); };
            _nodeList.KeyPress += e => { if (e.KeyEvent.Key == Key.Enter) { ShowSelectedNodeDetails(); e.Handled = true; } };
            _nodeList.OpenSelectedItem += delegate { ShowSelectedNodeDetails(); };
            var favoriteFilter = new Button("Favorites only: off") { X = 0, Y = Pos.AnchorEnd(2) };
            MainButtons.Add(favoriteFilter);
            favoriteFilter.Clicked += delegate { _favoritesOnly = !_favoritesOnly; favoriteFilter.Text = _favoritesOnly ? "Favorites only: on" : "Favorites only: off"; RefreshNodePage(); };
            _sortButton = new Button("Sort: name") { X = Pos.Right(favoriteFilter) + 2, Y = Pos.AnchorEnd(2) };
            MainButtons.Add(_sortButton);
            _sortButton.Clicked += CycleNodeSort;
            _nodeSortDirectionButton = new Button("Order: asc") { X = Pos.Right(_sortButton) + 2, Y = Pos.AnchorEnd(2) };
            MainButtons.Add(_nodeSortDirectionButton);
            _nodeSortDirectionButton.Clicked += ToggleNodeSortDirection;
            var searchLabel = new Label("Search:") { X = Pos.Right(_nodeSortDirectionButton) + 2, Y = Pos.AnchorEnd(2) };
            _nodeSearch = new TextField("") { X = Pos.Right(searchLabel) + 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill() };
            _nodeSearch.TextChanged += delegate { RefreshNodePage(); };
            _nodeScrollInfo = new Label("") { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
            nodeListFrame.Add(_nodeList);
            _nodesPage.Add(nodeListFrame, favoriteFilter, _sortButton, _nodeSortDirectionButton, searchLabel, _nodeSearch, _nodeScrollInfo);
            top.Add(_nodesPage);

            _telemetryPage = new Window("Telemetry") { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1), Visible = false };
            PageFrames.Add(_telemetryPage);
            _telemetryHeader = new Label("") { X = 0, Y = 0, Width = Dim.Fill() };
            var telemetryFrame = new FrameView("Stored telemetry [F4]") { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(3) };
            Frames.Add(telemetryFrame);
            _telemetryList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            _telemetryList.OpenSelectedItem += delegate { ShowSelectedTelemetryDetails(); };
            _telemetryList.SelectedItemChanged += delegate { UpdateTelemetryScrollInfo(); };
            telemetryFrame.Add(_telemetryList);
            var telemetryRefresh = new Button("Refresh") { X = 0, Y = Pos.AnchorEnd(2) };
            MainButtons.Add(telemetryRefresh);
            telemetryRefresh.Clicked += RefreshTelemetryPage;
            var copyCsv = new Button("Copy CSV") { X = Pos.Right(telemetryRefresh) + 2, Y = Pos.AnchorEnd(2) };
            MainButtons.Add(copyCsv);
            copyCsv.Clicked += delegate { CopyTelemetryToClipboard(false); };
            var copyExcel = new Button("Copy for spreadsheet") { X = Pos.Right(copyCsv) + 2, Y = Pos.AnchorEnd(2) };
            MainButtons.Add(copyExcel);
            copyExcel.Clicked += delegate { CopyTelemetryToClipboard(true); };
            _telemetryScrollInfo = new Label("") { X = Pos.Right(copyExcel) + 2, Y = Pos.AnchorEnd(2), Width = Dim.Fill() };
            _telemetryPage.Add(_telemetryHeader, telemetryFrame, telemetryRefresh, copyCsv, copyExcel, _telemetryScrollInfo);
            top.Add(_telemetryPage);

            _status = new Label("") { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
            top.Add(_status);
            RefreshChats();
            UpdateStatus();
            ApplyAppearanceSettings();
            ApplyLogoSettings();
            Application.MainLoop.AddTimeout(TimeSpan.FromSeconds(1), delegate(MainLoop loop) { UpdateStatus(); return true; });
            Application.MainLoop.AddTimeout(TimeSpan.FromSeconds(1), delegate(MainLoop loop) { RotateLogoIfDue(); return true; });
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(200), delegate(MainLoop loop)
            {
                if (_pleaseWait != null)
                {
                    _pleaseWaitProgress.Pulse();
                    _pleaseWaitProgress.SetNeedsDisplay();
                }
                return true;
            });
        }

        private static void SubscribeMeshEvents()
        {
            _mesh.ConnectionStateChanged += delegate { Ui(UpdateStatus); };
            _mesh.DeviceInfoUpdated += delegate { Ui(delegate { UpdateStatus(); UpdateChatPageTitle(); }); };
            _mesh.NodeDiscovered += delegate(object sender, NodeEventArgs e) { Save(delegate { _store.AddOrUpdateNode(e.Node); Ui(QueueNodePageRefresh); }); };
            _mesh.NodeUpdated += delegate(object sender, NodeEventArgs e) { Save(delegate { _store.AddOrUpdateNode(e.Node); Ui(QueueNodePageRefresh); }); };
            _mesh.MessageReceived += delegate(object sender, MeshMessageEventArgs e)
            {
                var node = _mesh.Nodes.FirstOrDefault(n => n.Number == e.Message.From);
                var ownEcho = _mesh.Device.MyNode != null && e.Message.From == _mesh.Device.MyNode.MyNodeNum;
                Save(delegate { _store.AddIncoming(e.Message, node == null ? (double?)null : node.Latitude, node == null ? (double?)null : node.Longitude, !ownEcho); if (!ownEcho && _settings.EnableNewMessageBeep) { try { Console.Beep(); } catch { } } RefreshChatsUi(); });
                if (!ownEcho) Task.Run(async delegate { await RunMatchingChatBotsAsync(e.Message); await RunMatchingHttpBotsAsync(e.Message); });
            };
            _mesh.MessageDeliveryChanged += delegate(object sender, MessageDeliveryEventArgs e)
            {
                Save(delegate { _store.UpdateDeliveryStatus(e.PacketId, e.State, e.Error == Meshtastic.Protobufs.Routing.Types.Error.None ? null : e.Error.ToString()); Ui(ShowSelectedChat); });
            };
            _mesh.TelemetryReceived += delegate(object sender, MeshTelemetryEventArgs e) { Save(delegate { _store.AddTelemetry(e.Telemetry); Ui(delegate { if (_telemetryPage != null && _telemetryPage.Visible) RefreshTelemetryPage(); if (_chatPage != null && _chatPage.Visible && _selected != null && _selected.Kind == ChatKind.Direct && _selected.Id == e.Telemetry.From) ShowSelectedChat(); }); }); };
        }

        private static void UpdateChatPageTitle()
        {
            if (_chatPage == null) return;
            var localNode = _mesh == null ? null : _mesh.Device.LocalNode;
            var nodeName = localNode == null ? null : (localNode.LongName ?? localNode.ShortName);
            _chatPage.Title = String.IsNullOrWhiteSpace(nodeName) ? "Chat" : "Chat (" + nodeName + ")";
        }

        private static void StartConnect()
        {
            if (_mesh.State != ConnectionState.Disconnected) { MessageBox.Query("Connection", "A connection is already active.", "OK"); return; }
            ShowPleaseWait("Connecting to Meshtastic...");
            // Give Terminal.Gui a complete drawing cycle before the transport starts its
            // potentially expensive initial node/configuration synchronization.
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(150), delegate(MainLoop loop)
            {
                Task.Run(async delegate
                {
                    try
                    {
                        var c = _settings.Connection;
                        if (c.Transport == MeshtasticTransportType.Serial) await _mesh.ConnectSerialAsync(c.SerialPort, c.SerialBaudRate);
                        else await _mesh.ConnectTcpAsync(c.TcpHost, c.TcpPort);
                        Ui(delegate { ShowPleaseWait("Loading nodes and device data..."); });
                        await _mesh.RequestFullStateAsync();
                        await _mesh.ActivatePacketStreamingAsync();
                        await RestartTelegramGatewaysAsync();
                        Ui(delegate { HidePleaseWait(); RefreshChats(); });
                    }
                    catch (Exception ex) { try { await _mesh.DisconnectAsync(); } catch { } Ui(delegate { HidePleaseWait(); MessageBox.ErrorQuery("Connection", ex.Message, "OK"); }); }
                });
                return false;
            });
            Application.Run(_pleaseWait);
            UpdateStatus();
        }

        private static void ShowPleaseWait(string text)
        {
            if (_pleaseWait == null)
            {
                _pleaseWait = new Dialog("Please wait", 58, 7);
                _pleaseWaitText = new Label("") { X = 2, Y = 1, Width = Dim.Fill(4), TextAlignment = TextAlignment.Centered };
                _pleaseWaitProgress = new ProgressBar { X = 2, Y = 3, Width = Dim.Fill(4), ProgressBarStyle = ProgressBarStyle.MarqueeBlocks };
                _pleaseWait.Add(_pleaseWaitText, _pleaseWaitProgress);
            }
            _pleaseWaitText.Text = text;
            _pleaseWait.SetNeedsDisplay();
        }
        private static void UpdateNodeLoadingProgress()
        {
            if (_pleaseWait != null && _pleaseWait.Visible) _pleaseWaitText.Text = "Loading nodes and device data... " + _mesh.Nodes.Count + " nodes received";
        }
        private static void QueueNodePageRefresh()
        {
            UpdateNodeLoadingProgress();
            if (_nodeRefreshPending) return;
            _nodeRefreshPending = true;
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(250), delegate(MainLoop loop)
            {
                _nodeRefreshPending = false;
                RefreshNodePage();
                UpdateNodeLoadingProgress();
                if (_chatPage != null && _chatPage.Visible && _selected != null && _selected.Kind == ChatKind.Direct) _nodeConnectionInfo.Text = BuildNodeConnectionLine(_selected);
                return false;
            });
        }
        private static void HidePleaseWait()
        {
            if (_pleaseWait == null) return;
            var waitDialog = _pleaseWait;
            _pleaseWait = null;
            _chatPage.SetFocus();
            Application.RequestStop(waitDialog);
        }

        private static void Disconnect() { Task.Run(async delegate { if (_telegramGateways != null) await _telegramGateways.StopAsync(); await _mesh.DisconnectAsync(); Ui(UpdateStatus); }); }

        private static void ShowConnectionStatus()
        {
            var info = _mesh.ConnectionInfo;
            var configured = _settings.Connection;
            var text =
                "State: " + _mesh.State + "\n" +
                "Transport: " + (info.Transport ?? "-") + "\n" +
                "Endpoint: " + (info.Endpoint ?? "-") + "\n" +
                "Configured transport: " + configured.Transport + "\n" +
                "Configured serial port: " + (configured.SerialPort ?? "-") + "\n" +
                "Configured baud rate: " + configured.SerialBaudRate + "\n" +
                "Configured TCP host: " + (configured.TcpHost ?? "-") + "\n" +
                "Configured TCP port: " + configured.TcpPort + "\n\n" +
                "Connected since: " + FormatConnectionTime(info.ConnectedSinceUtc) + "\n" +
                "Current connection duration: " + info.CurrentConnectionDuration.ToString(@"d\.hh\:mm\:ss") + "\n" +
                "Total connected duration: " + info.TotalConnectedDuration.ToString(@"d\.hh\:mm\:ss") + "\n" +
                "Reconnects: " + info.ReconnectCount + "\n\n" +
                "Sent frames: " + info.SentFrameCount + "\n" +
                "Received frames: " + info.ReceivedFrameCount + "\n" +
                "Last sent frame: " + FormatConnectionTime(info.LastSentUtc) + "\n" +
                "Last received frame: " + FormatConnectionTime(info.LastReceivedUtc);

            var dialog = new Dialog("Connection status", 68, 24);
            var details = new TextView { X = 1, Y = 1, Width = Dim.Fill(2), Height = Dim.Fill(3), ReadOnly = true, WordWrap = true, Text = text };
            var close = new Button("Close", true) { X = 1, Y = Pos.AnchorEnd(2) };
            close.Clicked += delegate { Application.RequestStop(); };
            dialog.Add(details, close);
            Application.Run(dialog);
        }

        private static string FormatConnectionTime(DateTime? value)
        {
            return value.HasValue ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "-";
        }

        private static void RefreshChatsUi() { Ui(RefreshChats); }
        private static void RefreshChats()
        {
            var current = _selected == null ? (ChatItem)null : _selected;
            _refreshingChatLists = true;
            Chats.Clear();
            ChannelChats.Clear();
            DirectChats.Clear();
            var channelStatistics = _store.GetChannelMessageStatistics().Where(x => x.ChannelIndex.HasValue).ToDictionary(x => x.ChannelIndex.Value, x => x);
            foreach (var configuredChannel in _mesh.Channels)
            {
                ChannelMessageStatistics stat; channelStatistics.TryGetValue(configuredChannel.Index, out stat);
                var name = String.IsNullOrEmpty(configuredChannel.Name) ? "Channel " + configuredChannel.Index : configuredChannel.Name;
                ChannelChats.Add(new ChatItem { Kind = ChatKind.Channel, Id = (uint)configuredChannel.Index, Name = name, NewCount = stat == null ? 0 : stat.NewCount });
            }
            foreach (var stat in _store.GetDirectMessageStatistics().OrderBy(x => DisplayNodeName(x.NodeNumber))) DirectChats.Add(new ChatItem { Kind = ChatKind.Direct, Id = stat.NodeNumber, Name = DisplayNodeName(stat.NodeNumber), NewCount = stat.NewCount });
            Chats.AddRange(ChannelChats); Chats.AddRange(DirectChats);
            _channelList.SetSource(ChannelChats);
            _directList.SetSource(DirectChats);
            if (current != null) _selected = Chats.FirstOrDefault(c => c.Kind == current.Kind && c.Id == current.Id);
            if (_selected == null && Chats.Count > 0) _selected = Chats[0];
            if (_selected != null && _selected.Kind == ChatKind.Channel)
            {
                var index = ChannelChats.IndexOf(_selected);
                if (index >= 0) _channelList.SelectedItem = index;
            }
            if (_selected != null && _selected.Kind == ChatKind.Direct)
            {
                var index = DirectChats.IndexOf(_selected);
                if (index >= 0) _directList.SelectedItem = index;
            }
            _refreshingChatLists = false;
            ShowSelectedChat();
        }

        private static void ShowSelectedChat()
        {
            if (_selected == null) { _header.Text = "No chat selected"; _latestTelemetry.Text = ""; _nodeConnectionInfo.Text = ""; _messages.SetMessages(new StoredMeshMessage[0], FormatMessage); return; }
            _header.Text = BuildChatHeader(_selected);
            IList<StoredMeshMessage> entries;
            if (_selected.Kind == ChatKind.Channel) { entries = _store.GetChannelMessages((int)_selected.Id); _store.MarkChannelMessagesRead((int)_selected.Id); }
            else { entries = _store.GetDirectMessages(_selected.Id); _store.MarkDirectMessagesRead(_selected.Id); }
            _selected.NewCount = 0;
            _messages.SetMessages(entries, FormatMessage);
            _latestTelemetry.Text = BuildLatestTelemetryLine(_selected);
            _nodeConnectionInfo.Text = BuildNodeConnectionLine(_selected);
        }

        private static void UpdateMessageScrollPosition()
        {
            if (_messagesFrame == null || _messages == null) return;
            _messagesFrame.Title = _messages.MessageCount == 0 ? "Messages" : "Messages [" + _messages.FirstVisibleMessageNumber + "/" + _messages.MessageCount + "]";
            _messagesFrame.SetNeedsDisplay();
        }

        private static string BuildLatestTelemetryLine(ChatItem chat)
        {
            if (chat.Kind != ChatKind.Direct) return "";
            var telemetry = _store.GetTelemetry(chat.Id, 1).FirstOrDefault();
            return telemetry == null ? "" : "Last telemetry: " + FormatTelemetrySummary(telemetry, false);
        }

        private static string BuildNodeConnectionLine(ChatItem chat)
        {
            if (chat.Kind != ChatKind.Direct) return "";
            var storedNode = _store.GetNodes(StoredNodeSort.Name).FirstOrDefault(node => node.NodeNumber == chat.Id);
            var liveNode = _mesh.Nodes.FirstOrDefault(node => node.Number == chat.Id);
            var hops = liveNode != null && liveNode.HopsAway.HasValue ? liveNode.HopsAway : (storedNode == null ? (uint?)null : storedNode.HopsAway);
            var rssi = liveNode == null || !liveNode.LastRssi.HasValue ? "-" : liveNode.LastRssi.Value.ToString(CultureInfo.InvariantCulture) + " dBm";
            var snr = liveNode == null || !liveNode.LastSnr.HasValue ? "-" : liveNode.LastSnr.Value.ToString("F1", CultureInfo.InvariantCulture) + " dB";
            var battery = storedNode == null || !storedNode.BatteryLevel.HasValue ? "-" : storedNode.BatteryLevel.Value.ToString(CultureInfo.InvariantCulture) + "%";
            return "Connection: Battery " + battery + " | Hops " + (hops.HasValue ? hops.Value.ToString(CultureInfo.InvariantCulture) : "-") + " | SNR " + snr + " | RSSI " + rssi;
        }

        private static void DeleteActiveChat()
        {
            if (_selected == null)
            {
                MessageBox.Query("Delete messages", "Please select a chat first.", "OK");
                return;
            }
            var chatName = _selected.Kind == ChatKind.Channel ? "channel #" + _selected.Name : "direct chat with " + _selected.Name;
            if (MessageBox.Query("Delete messages", "Delete all messages in the " + chatName + "?", "Delete", "Cancel") != 0) return;
            if (_selected.Kind == ChatKind.Channel) _store.DeleteChannelMessages((int)_selected.Id);
            else _store.DeleteDirectMessages(_selected.Id);
            RefreshChats();
        }

        private static string FormatMessage(StoredMeshMessage m)
        {
            var who = m.Direction == MessageDirection.Outgoing ? "You" : DisplayNodeName(m.FromNode.GetValueOrDefault());
            var delivery = m.Direction == MessageDirection.Outgoing && !String.IsNullOrEmpty(m.DeliveryStatus) ? " [" + m.DeliveryStatus + "]" : "";
            var timePrefix = "[" + m.OccurredUtc.ToLocalTime().ToString("HH:mm") + "] ";
            return timePrefix + who + delivery + ": " + ConvertEmojiForTerminal(m.Text ?? "").Replace("\r\n", "\n").Replace("\n", "\n" + new string(' ', timePrefix.Length));
        }

        private static string ConvertEmojiForTerminal(string text)
        {
            var result = new StringBuilder();
            var elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                var element = (string)elements.Current;
                string replacement;
                if (EmojiReplacements.TryGetValue(element, out replacement)) result.Append(replacement);
                else if (ContainsEmoji(element)) result.Append("[:emoji:]");
                else result.Append(element);
            }
            return result.ToString();
        }

        private static void LoadEmojiReplacements()
        {
            EmojiReplacements.Clear();
            EmojiPickerEntries.Clear();
            var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "emoji-replacements.xml");
            try
            {
                if (File.Exists(filePath))
                {
                    var serializer = new XmlSerializer(typeof(EmojiReplacementFile));
                    using (var stream = File.OpenRead(filePath))
                    {
                        var file = serializer.Deserialize(stream) as EmojiReplacementFile;
                        if (file != null && file.Items != null)
                        {
                            foreach (var item in file.Items.Where(item => item != null && !String.IsNullOrEmpty(item.Value) && !String.IsNullOrEmpty(item.Text)))
                            {
                                EmojiReplacements[item.Value] = item.Text;
                                EmojiPickerEntries.Add(item);
                            }
                        }
                    }
                }
            }
            catch { EmojiReplacements.Clear(); }
            if (EmojiReplacements.Count == 0)
            {
                foreach (var item in DefaultEmojiReplacements)
                {
                    EmojiReplacements[item.Key] = item.Value;
                    EmojiPickerEntries.Add(new EmojiReplacementEntry { Value = item.Key, Text = item.Value, Category = "Other" });
                }
            }
        }

        private static bool ContainsEmoji(string textElement)
        {
            for (var index = 0; index < textElement.Length; index++)
            {
                var codePoint = Char.ConvertToUtf32(textElement, index);
                if (Char.IsHighSurrogate(textElement[index])) index++;
                if ((codePoint >= 0x1F000 && codePoint <= 0x1FAFF) || (codePoint >= 0x2600 && codePoint <= 0x27BF)) return true;
            }
            return false;
        }

        private static string BuildChatHeader(ChatItem chat)
        {
            if (chat.Kind == ChatKind.Channel) return "# " + chat.Name;
            var node = _store.GetNodes(StoredNodeSort.Name).FirstOrDefault(n => n.NodeNumber == chat.Id);
            if (node == null) return "@ " + chat.Name + " | !" + chat.Id.ToString("x8");
            var distance = MeshtasticClient.GetDistanceMeters(_mesh.Device.Latitude, _mesh.Device.Longitude, node.Latitude, node.Longitude);
            var bearing = MeshtasticClient.GetInitialBearingDegrees(_mesh.Device.Latitude, _mesh.Device.Longitude, node.Latitude, node.Longitude);
            var distanceText = distance.HasValue ? (distance.Value < 1000 ? Math.Round(distance.Value) + " m" : (distance.Value / 1000d).ToString("F1", CultureInfo.InvariantCulture) + " km") : "-";
            var direction = bearing.HasValue ? MeshtasticClient.GetCompassDirection(bearing.Value) : "-";
            var directionText = bearing.HasValue ? Math.Round(bearing.Value) + "° " + direction : "-";
            return "@ " + chat.Name + " | " + (node.NodeId ?? "!" + node.NodeNumber.ToString("x8")) + " | " + distanceText + " " + directionText;
        }

        private static void ShowEmojiPicker()
        {
            if (_input == null) return;
            var dialog = new Dialog("Emoji picker", 82, 25);
            var categories = new[] { "All" }.Concat(EmojiPickerEntries.Select(entry => String.IsNullOrWhiteSpace(entry.Category) ? "Other" : entry.Category).Distinct().OrderBy(category => category)).ToList();
            var selectedCategory = "All";
            var visibleEntries = new List<EmojiReplacementEntry>();
            var categoryLabel = new Label("Category") { X = 1, Y = 1 };
            var categoryList = new ListView(categories) { X = 1, Y = 2, Width = 25, Height = Dim.Fill(4) };
            var searchLabel = new Label("Search") { X = Pos.Right(categoryList) + 2, Y = 1 };
            var search = new TextField("") { X = Pos.Right(categoryList) + 10, Y = 1, Width = Dim.Fill(2) };
            var list = new ListView { X = Pos.Right(categoryList) + 2, Y = 3, Width = Dim.Fill(2), Height = Dim.Fill(5) };
            Action refresh = delegate
            {
                var query = search.Text == null ? "" : search.Text.ToString().Trim();
                visibleEntries.Clear();
                visibleEntries.AddRange(EmojiPickerEntries.Where(entry =>
                    (selectedCategory == "All" || String.Equals(String.IsNullOrWhiteSpace(entry.Category) ? "Other" : entry.Category, selectedCategory, StringComparison.Ordinal)) &&
                    (query.Length == 0 || (entry.Text ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (entry.Category ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (entry.SubCategory ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderBy(entry => entry.Text));
                // Emoji may consume one or two terminal cells depending on the font and
                // terminal driver. Showing the stable text representation keeps every
                // picker row aligned; the actual emoji is still inserted on selection.
                list.SetSource(visibleEntries.Select(entry => entry.Text).ToList());
                if (visibleEntries.Count > 0) list.SelectedItem = 0;
            };
            categoryList.SelectedItemChanged += delegate(ListViewItemEventArgs e)
            {
                if (e.Item < 0 || e.Item >= categories.Count) return;
                selectedCategory = categories[e.Item];
                refresh();
            };
            search.TextChanged += delegate { refresh(); };
            refresh();
            Action insert = delegate
            {
                if (list.SelectedItem < 0 || list.SelectedItem >= visibleEntries.Count) return;
                InsertEmojiIntoMessage(visibleEntries[list.SelectedItem].Value);
                Application.RequestStop();
            };
            list.KeyPress += e => { if (e.KeyEvent.Key == Key.Enter) { insert(); e.Handled = true; } };
            list.OpenSelectedItem += delegate { insert(); };
            var insertButton = new Button("Insert", true);
            insertButton.Clicked += delegate { insert(); };
            var cancel = new Button("Cancel");
            cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.Add(categoryLabel, categoryList, searchLabel, search, list); dialog.AddButton(insertButton); dialog.AddButton(cancel);
            Application.Run(dialog);
            _input.SetFocus();
        }

        private static void InsertEmojiIntoMessage(string emoji)
        {
            var text = _input.Text.ToString().Replace("\r\n", "\n");
            var cursor = _input.CursorPosition;
            var lines = text.Split('\n');
            var row = Math.Max(0, Math.Min(cursor.Y, lines.Length - 1));
            var column = Math.Max(0, Math.Min(cursor.X, lines[row].Length));
            var index = column;
            for (var line = 0; line < row; line++) index += lines[line].Length + 1;
            _input.Text = text.Insert(index, emoji);
            _input.CursorPosition = new Point(column + emoji.Length, row);
        }

        private static void SendCurrentMessage()
        {
            var text = _input.Text == null ? "" : _input.Text.ToString();
            if (_selected == null || String.IsNullOrWhiteSpace(text)) return;
            Task.Run(async delegate
            {
                try
                {
                    uint packetId = _selected.Kind == ChatKind.Channel ? await _mesh.SendTextToChannelWithIdAsync((int)_selected.Id, text) : await _mesh.SendTextWithIdAsync(_selected.Id, text);
                    _store.AddOutgoing(packetId, _mesh.Device.MyNode == null ? 0u : _mesh.Device.MyNode.MyNodeNum, _selected.Kind == ChatKind.Channel ? UInt32.MaxValue : _selected.Id, text, _selected.Kind == ChatKind.Channel ? (int?)_selected.Id : null, _selected.Kind == ChatKind.Channel ? _selected.Name : null);
                    Ui(delegate { _input.Text = ""; ShowSelectedChat(); _input.SetFocus(); });
                }
                catch (Exception ex) { Ui(delegate { MessageBox.ErrorQuery("Send failed", ex.Message, "OK"); }); }
            });
        }

        private static void ActivateSelectedChat()
        {
            ShowChatPage();
            ShowSelectedChat();
            _input.SetFocus();
        }

        private static void ShowChatPage()
        {
            _nodesPage.Visible = false;
            _telemetryPage.Visible = false;
            _chatPage.Visible = true;
            _chatPage.SetFocus();
        }
        private static void ShowNodesPage()
        {
            RefreshNodePage();
            _chatPage.Visible = false;
            _telemetryPage.Visible = false;
            _nodesPage.Visible = true;
            _nodesPage.SetFocus();
        }
        private static void ShowAllTelemetry()
        {
            ShowTelemetryPage(null);
        }
        private static void DeleteCurrentNodeTelemetry()
        {
            if (!_telemetryNodeFilter.HasValue)
            {
                MessageBox.Query("Delete telemetry", "Open telemetry for a specific node first.", "OK");
                return;
            }
            var nodeNumber = _telemetryNodeFilter.Value;
            if (MessageBox.Query("Delete telemetry", "Delete all telemetry records for " + DisplayNodeName(nodeNumber) + "?", "Delete", "Cancel") != 0) return;
            _store.DeleteTelemetry(nodeNumber);
            RefreshTelemetryPage();
        }
        private static void DeleteAllTelemetry()
        {
            if (MessageBox.Query("Delete all telemetry", "Delete all telemetry records from the database?", "Delete", "Cancel") != 0) return;
            _store.DeleteAllTelemetry();
            RefreshTelemetryPage();
        }
        private static void ShowTelemetryForCurrentChatNode()
        {
            if (_selected == null || _selected.Kind != ChatKind.Direct)
            {
                MessageBox.Query("Telemetry", "Telemetry can only be shown for a direct chat.", "OK");
                return;
            }
            ShowTelemetryPage(_selected.Id);
        }
        private static void ShowTelemetryPage(uint? nodeNumber)
        {
            _telemetryNodeFilter = nodeNumber;
            RefreshTelemetryPage();
            _chatPage.Visible = false;
            _nodesPage.Visible = false;
            _telemetryPage.Visible = true;
            _telemetryPage.SetFocus();
            _telemetryList.SetFocus();
        }
        private static void RefreshTelemetryPage()
        {
            if (_telemetryPage == null) return;
            IList<StoredTelemetry> entries = _telemetryNodeFilter.HasValue ? _store.GetTelemetry(_telemetryNodeFilter.Value, Int32.MaxValue) : _store.GetTelemetry(Int32.MaxValue);
            _telemetryHeader.Text = _telemetryNodeFilter.HasValue ? "Node: " + DisplayNodeName(_telemetryNodeFilter.Value) + " (" + _telemetryNodeFilter.Value.ToString("x8") + ")" : "All nodes";
            TelemetryItems.Clear();
            TelemetryItems.AddRange(entries);
            _telemetryList.SetSource(TelemetryItems.Select(t => FormatTelemetrySummary(t, !_telemetryNodeFilter.HasValue)).ToList());
            if (TelemetryItems.Count > 0) _telemetryList.SelectedItem = 0;
            UpdateTelemetryScrollInfo();
        }
        private static void UpdateTelemetryScrollInfo()
        {
            if (_telemetryScrollInfo == null) return;
            _telemetryScrollInfo.Text = TelemetryItems.Count == 0 ? "No telemetry" : "Position " + Math.Min(TelemetryItems.Count, _telemetryList.SelectedItem + 1) + "/" + TelemetryItems.Count;
        }
        private static string FormatTelemetrySummary(StoredTelemetry telemetry, bool includeNode)
        {
            var values = new List<string>();
            if (telemetry.BatteryLevel.HasValue) values.Add("Battery: " + telemetry.BatteryLevel.Value + "%");
            if (telemetry.Voltage.HasValue) values.Add("Voltage: " + telemetry.Voltage.Value.ToString("F2", CultureInfo.InvariantCulture) + " V");
            if (telemetry.ChannelUtilization.HasValue) values.Add("Channel utilization: " + telemetry.ChannelUtilization.Value.ToString("F1", CultureInfo.InvariantCulture) + "%");
            if (telemetry.AirUtilTx.HasValue) values.Add("Air utilization TX: " + telemetry.AirUtilTx.Value.ToString("F1", CultureInfo.InvariantCulture) + "%");
            if (telemetry.UptimeSeconds.HasValue) values.Add("Uptime: " + TimeSpan.FromSeconds(telemetry.UptimeSeconds.Value).ToString());
            if (telemetry.Temperature.HasValue) values.Add("Temperature: " + telemetry.Temperature.Value.ToString("F1", CultureInfo.InvariantCulture) + " °C");
            if (telemetry.RelativeHumidity.HasValue) values.Add("Humidity: " + telemetry.RelativeHumidity.Value.ToString("F1", CultureInfo.InvariantCulture) + "%");
            if (telemetry.BarometricPressure.HasValue) values.Add("Pressure: " + telemetry.BarometricPressure.Value.ToString("F1", CultureInfo.InvariantCulture) + " hPa");
            return telemetry.ReceivedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") +
                (includeNode ? " | " + DisplayNodeName(telemetry.NodeNumber) : "") +
                " | " + (values.Count == 0 ? telemetry.Type : String.Join(" | ", values));
        }
        private static void ShowSelectedTelemetryDetails()
        {
            var index = _telemetryList.SelectedItem;
            if (index < 0 || index >= TelemetryItems.Count) return;
            var telemetry = TelemetryItems[index];
            var values = new List<string>();
            if (telemetry.BatteryLevel.HasValue) values.Add("Battery: " + telemetry.BatteryLevel.Value + "%");
            if (telemetry.Voltage.HasValue) values.Add("Voltage: " + telemetry.Voltage.Value.ToString("F2", CultureInfo.InvariantCulture) + " V");
            if (telemetry.ChannelUtilization.HasValue) values.Add("Channel utilization: " + telemetry.ChannelUtilization.Value.ToString("F1", CultureInfo.InvariantCulture) + "%");
            if (telemetry.AirUtilTx.HasValue) values.Add("Air utilization TX: " + telemetry.AirUtilTx.Value.ToString("F1", CultureInfo.InvariantCulture) + "%");
            if (telemetry.UptimeSeconds.HasValue) values.Add("Uptime: " + TimeSpan.FromSeconds(telemetry.UptimeSeconds.Value));
            if (telemetry.Temperature.HasValue) values.Add("Temperature: " + telemetry.Temperature.Value.ToString("F1", CultureInfo.InvariantCulture) + " °C");
            if (telemetry.RelativeHumidity.HasValue) values.Add("Humidity: " + telemetry.RelativeHumidity.Value.ToString("F1", CultureInfo.InvariantCulture) + "%");
            if (telemetry.BarometricPressure.HasValue) values.Add("Pressure: " + telemetry.BarometricPressure.Value.ToString("F1", CultureInfo.InvariantCulture) + " hPa");
            var text = "Node: " + DisplayNodeName(telemetry.NodeNumber) + " (!" + telemetry.NodeNumber.ToString("x8") + ")" +
                "\nReceived: " + telemetry.ReceivedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") +
                "\nMeasurement time: " + (telemetry.TelemetryTimeUtc.HasValue ? telemetry.TelemetryTimeUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "-") +
                "\nType: " + telemetry.Type +
                "\n" + (values.Count == 0 ? "No decoded values." : String.Join("\n", values)) +
                (String.IsNullOrEmpty(telemetry.RawTelemetry) ? "" : "\nRaw: " + telemetry.RawTelemetry);
            MessageBox.Query("Telemetry details", text, "OK");
        }
        private static void CopyTelemetryToClipboard(bool excelFormat)
        {
            var separator = excelFormat ? "\t" : ",";
            var rows = new List<string>();
            foreach (var telemetry in TelemetryItems)
            {
                var fields = new[]
                {
                    telemetry.ReceivedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    NullableText(telemetry.BatteryLevel), NullableText(telemetry.Voltage, excelFormat), NullableText(telemetry.ChannelUtilization, excelFormat), NullableText(telemetry.AirUtilTx, excelFormat), NullableText(telemetry.UptimeSeconds), NullableText(telemetry.Temperature, excelFormat), NullableText(telemetry.RelativeHumidity, excelFormat), NullableText(telemetry.BarometricPressure, excelFormat)
                };
                rows.Add(String.Join(separator, fields.Select(value => ClipboardField(value, excelFormat))));
            }
            if (!Clipboard.TrySetClipboardData(String.Join(Environment.NewLine, rows))) MessageBox.ErrorQuery("Clipboard", "The clipboard is not available.", "OK");
            else MessageBox.Query("Clipboard", excelFormat ? "Telemetry was copied in spreadsheet format." : "Telemetry was copied as CSV.", "OK");
        }
        private static string NullableText(uint? value) { return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : ""; }
        private static string NullableText(double? value, bool useLocalDecimalSeparator) { return value.HasValue ? value.Value.ToString(useLocalDecimalSeparator ? CultureInfo.CurrentCulture : CultureInfo.InvariantCulture) : ""; }
        private static string ClipboardField(string value, bool excelFormat)
        {
            value = value ?? "";
            if (excelFormat) return value.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        private static void RefreshNodePage()
        {
            if (_nodeList == null) return;
            var search = _nodeSearch == null ? "" : _nodeSearch.Text.ToString().Trim();
            IEnumerable<StoredMeshNode> nodes = _store.GetNodes(GetDatabaseNodeSort(_nodeSort)).Where(n => !_favoritesOnly || n.IsFavorite);
            if (!String.IsNullOrEmpty(search)) nodes = nodes.Where(n => NodeMatchesSearch(n, search));
            nodes = SortNodes(nodes);
            NodeItems.Clear();
            NodeItems.AddRange(nodes);
            _nodeList.SetSource(NodeItems.Select(FormatNode).ToList());
            if (NodeItems.Count > 0) _nodeList.SelectedItem = Math.Min(Math.Max(0, _nodeList.SelectedItem), NodeItems.Count - 1);
            UpdateNodeScrollInfo();
        }

        private static StoredNodeSort GetDatabaseNodeSort(NodeSortMode sort)
        {
            return sort == NodeSortMode.NodeId ? StoredNodeSort.NodeId : sort == NodeSortMode.LastReceived ? StoredNodeSort.LastReceived : StoredNodeSort.Name;
        }

        private static bool NodeMatchesSearch(StoredMeshNode node, string search)
        {
            return (!String.IsNullOrEmpty(node.LongName) && node.LongName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (!String.IsNullOrEmpty(node.ShortName) && node.ShortName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (!String.IsNullOrEmpty(node.NodeId) && node.NodeId.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static IEnumerable<StoredMeshNode> SortNodes(IEnumerable<StoredMeshNode> nodes)
        {
            switch (_nodeSort)
            {
                case NodeSortMode.Name: return _nodeSortAscending ? nodes.OrderBy(node => node.LongName ?? node.NodeId ?? "") : nodes.OrderByDescending(node => node.LongName ?? node.NodeId ?? "");
                case NodeSortMode.NodeId: return _nodeSortAscending ? nodes.OrderBy(node => node.NodeId ?? "") : nodes.OrderByDescending(node => node.NodeId ?? "");
                case NodeSortMode.LastReceived: return _nodeSortAscending ? nodes.OrderBy(node => node.LastReceivedUtc) : nodes.OrderByDescending(node => node.LastReceivedUtc);
                case NodeSortMode.Distance: return SortNodesWithMissingValues(nodes, node => MeshtasticClient.GetDistanceMeters(_mesh.Device.Latitude, _mesh.Device.Longitude, node.Latitude, node.Longitude));
                case NodeSortMode.Hops: return SortNodesWithMissingValues(nodes, node => node.HopsAway.HasValue ? (double?)node.HopsAway.Value : null);
                case NodeSortMode.Signal: return SortNodesWithMissingValues(nodes, node => { var live = _mesh.Nodes.FirstOrDefault(item => item.Number == node.NodeNumber); return live == null || !live.LastRssi.HasValue ? (double?)null : live.LastRssi.Value; });
                default: return nodes;
            }
        }

        private static IEnumerable<StoredMeshNode> SortNodesWithMissingValues(IEnumerable<StoredMeshNode> nodes, Func<StoredMeshNode, double?> selector)
        {
            var known = nodes.Where(node => selector(node).HasValue);
            var unknown = nodes.Where(node => !selector(node).HasValue);
            return _nodeSortAscending ? known.OrderBy(node => selector(node).Value).Concat(unknown) : known.OrderByDescending(node => selector(node).Value).Concat(unknown);
        }
        private static void CreateNode()
        {
            var dialog = new Dialog("Create node", 60, 10);
            var id = new TextField("") { X = 18, Y = 1, Width = 20 };
            var name = new TextField("") { X = 18, Y = 2, Width = 32 };
            dialog.Add(new Label("Meshtastic ID:") { X = 1, Y = 1 }, id, new Label("Name:") { X = 1, Y = 2 }, name);
            var save = new Button("Create", true);
            save.Clicked += delegate
            {
                var enteredId = id.Text.ToString().Trim();
                var hexId = enteredId.TrimStart('!');
                uint nodeNumber;
                if (!UInt32.TryParse(hexId, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out nodeNumber) || String.IsNullOrWhiteSpace(name.Text.ToString()))
                {
                    MessageBox.ErrorQuery("Create node", "Enter a Meshtastic ID (for example !a1b2c3d4) and a name.", "OK");
                    return;
                }
                _store.AddOrUpdateManualNode(nodeNumber, "!" + hexId.ToLowerInvariant(), name.Text.ToString().Trim());
                RefreshNodePage();
                Application.RequestStop();
            };
            var cancel = new Button("Cancel");
            cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.AddButton(save); dialog.AddButton(cancel);
            Application.Run(dialog);
        }
        private static void RefreshNodesFromDevice()
        {
            if (_mesh.State != ConnectionState.Connected) { MessageBox.ErrorQuery("Nodes", "Connect to a Meshtastic device first.", "OK"); return; }
            Task.Run(async delegate
            {
                try
                {
                    await _mesh.RequestFullStateAsync();
                    foreach (var node in _mesh.Nodes) _store.AddOrUpdateNode(node);
                    Ui(RefreshNodePage);
                }
                catch (Exception ex) { Ui(delegate { MessageBox.ErrorQuery("Nodes", ex.Message, "OK"); }); }
            });
        }
        private static void DeleteAllNodes()
        {
            if (MessageBox.Query("Delete all nodes", "Delete all nodes from the database?", "Delete", "Cancel") != 0) return;
            _store.DeleteAllNodes();
            RefreshNodePage();
        }
        private static void StartChatForSelectedNode()
        {
            var index = _nodeList.SelectedItem;
            if (index < 0 || index >= NodeItems.Count) return;
            StartChatForNode(NodeItems[index]);
        }
        private static void StartChatForNode(StoredMeshNode node)
        {
            _selected = new ChatItem { Kind = ChatKind.Direct, Id = node.NodeNumber, Name = String.IsNullOrEmpty(node.LongName) ? node.NodeId : node.LongName };
            ShowChatPage();
            ShowSelectedChat();
            _input.SetFocus();
        }
        private static void ShowSelectedNodeDetails()
        {
            var index = _nodeList.SelectedItem;
            if (index < 0 || index >= NodeItems.Count) return;
            ShowNodeDetails(NodeItems[index]);
        }
        private static void ShowCurrentChatNodeDetails()
        {
            if (_selected == null || _selected.Kind != ChatKind.Direct)
            {
                MessageBox.Query("Node details", "Select a direct chat first.", "OK");
                return;
            }
            var node = _store.GetNodes(StoredNodeSort.Name).FirstOrDefault(item => item.NodeNumber == _selected.Id);
            if (node == null)
            {
                MessageBox.Query("Node details", "No stored details are available for this node yet.", "OK");
                return;
            }
            ShowNodeDetails(node);
        }
        private static void ShowNodeDetails(StoredMeshNode node)
        {
            var distance = MeshtasticClient.GetDistanceMeters(_mesh.Device.Latitude, _mesh.Device.Longitude, node.Latitude, node.Longitude);
            var bearing = MeshtasticClient.GetInitialBearingDegrees(_mesh.Device.Latitude, _mesh.Device.Longitude, node.Latitude, node.Longitude);
            var distanceText = distance.HasValue ? (distance.Value < 1000 ? Math.Round(distance.Value) + " m" : (distance.Value / 1000d).ToString("F1", CultureInfo.InvariantCulture) + " km") : "not available";
            var direction = bearing.HasValue ? Math.Round(bearing.Value) + "° " + MeshtasticClient.GetCompassDirection(bearing.Value) : "not available";
            var text = "Meshtastic ID: " + (node.NodeId ?? "!" + node.NodeNumber.ToString("x8")) +
                "\nNode number: " + node.NodeNumber +
                "\nName: " + (node.LongName ?? "-") +
                "\nShort name: " + (node.ShortName ?? "-") +
                "\nFavorite: " + (node.IsFavorite ? "yes" : "no") +
                "\nLast received: " + node.LastReceivedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") +
                "\nPosition: " + (node.Latitude.HasValue && node.Longitude.HasValue ? node.Latitude.Value.ToString("F6", CultureInfo.InvariantCulture) + ", " + node.Longitude.Value.ToString("F6", CultureInfo.InvariantCulture) : "not available") +
                "\nDistance: " + distanceText +
                "\nDirection: " + direction +
                "\nBattery: " + (node.BatteryLevel.HasValue ? node.BatteryLevel.Value + "%" : "not available") +
                "\nHops: " + (node.HopsAway.HasValue ? node.HopsAway.Value.ToString() : "not available");
            var selectedButton = MessageBox.Query("Node details", text, "Start chat", "Telemetry", node.IsFavorite ? "Remove favorite" : "Add favorite", "Copy GPS", "Delete", "Close");
            if (selectedButton == 0) StartChatForNode(node);
            else if (selectedButton == 1) ShowTelemetryPage(node.NodeNumber);
            else if (selectedButton == 2) { _store.SetNodeFavorite(node.NodeNumber, !node.IsFavorite); RefreshNodePage(); }
            else if (selectedButton == 3) CopyNodePositionToClipboard(node);
            else if (selectedButton == 4 && MessageBox.Query("Delete node", "Delete this node from the database?", "Delete", "Cancel") == 0) { _store.DeleteNode(node.NodeNumber); RefreshNodePage(); }
        }
        private static void CopyNodePositionToClipboard(StoredMeshNode node)
        {
            if (!node.Latitude.HasValue || !node.Longitude.HasValue)
            {
                MessageBox.Query("Copy GPS", "No GPS position is available for this node.", "OK");
                return;
            }
            var position = node.Latitude.Value.ToString("F6", CultureInfo.InvariantCulture) + "," + node.Longitude.Value.ToString("F6", CultureInfo.InvariantCulture);
            if (!Clipboard.TrySetClipboardData(position)) MessageBox.ErrorQuery("Copy GPS", "The clipboard is not available.", "OK");
            else MessageBox.Query("Copy GPS", "GPS position copied: " + position, "OK");
        }
        private static string FormatNode(StoredMeshNode node)
        {
            var distance = MeshtasticClient.GetDistanceMeters(_mesh.Device.Latitude, _mesh.Device.Longitude, node.Latitude, node.Longitude);
            var bearing = MeshtasticClient.GetInitialBearingDegrees(_mesh.Device.Latitude, _mesh.Device.Longitude, node.Latitude, node.Longitude);
            var direction = bearing.HasValue ? MeshtasticClient.GetCompassDirection(bearing.Value) : "-";
            var distanceText = distance.HasValue ? (distance.Value < 1000 ? Math.Round(distance.Value) + "m" : (distance.Value / 1000d).ToString("F1", CultureInfo.InvariantCulture) + "km") : "-";
            var id = (node.NodeId ?? "!" + node.NodeNumber.ToString("x8")).PadRight(12).Substring(0, 12);
            var name = (node.LongName ?? "unknown").PadRight(22).Substring(0, 22);
            var liveNode = _mesh.Nodes.FirstOrDefault(item => item.Number == node.NodeNumber);
            var rssi = liveNode == null || !liveNode.LastRssi.HasValue ? "-" : liveNode.LastRssi.Value.ToString(CultureInfo.InvariantCulture);
            var snr = liveNode == null || !liveNode.LastSnr.HasValue ? "-" : liveNode.LastSnr.Value.ToString("F1", CultureInfo.InvariantCulture);
            return (node.IsFavorite ? "* " : "  ") + id + " | " + node.LastReceivedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " | " + distanceText.PadLeft(8) + " " + direction.PadRight(5) + " | hops " + (node.HopsAway.HasValue ? node.HopsAway.Value.ToString() : "-") + " | RSSI " + rssi.PadLeft(4) + " SNR " + snr.PadLeft(5) + " | " + name;
        }
        private static void CycleNodeSort()
        {
            _nodeSort = _nodeSort == NodeSortMode.Name ? NodeSortMode.NodeId :
                _nodeSort == NodeSortMode.NodeId ? NodeSortMode.LastReceived :
                _nodeSort == NodeSortMode.LastReceived ? NodeSortMode.Distance :
                _nodeSort == NodeSortMode.Distance ? NodeSortMode.Hops :
                _nodeSort == NodeSortMode.Hops ? NodeSortMode.Signal : NodeSortMode.Name;
            _sortButton.Text = "Sort: " + (_nodeSort == NodeSortMode.Name ? "name" : _nodeSort == NodeSortMode.NodeId ? "ID" : _nodeSort == NodeSortMode.LastReceived ? "last" : _nodeSort == NodeSortMode.Distance ? "distance" : _nodeSort == NodeSortMode.Hops ? "hops" : "signal");
            RefreshNodePage();
        }
        private static void ToggleNodeSortDirection()
        {
            _nodeSortAscending = !_nodeSortAscending;
            _nodeSortDirectionButton.Text = _nodeSortAscending ? "Order: asc" : "Order: desc";
            RefreshNodePage();
        }
        private static void UpdateNodeScrollInfo()
        {
            if (_nodeScrollInfo == null) return;
            _nodeScrollInfo.Text = NodeItems.Count == 0 ? "No nodes" : "Position " + Math.Min(NodeItems.Count, _nodeList.SelectedItem + 1) + "/" + NodeItems.Count;
        }

        private static void ShowLogoSettings()
        {
            var logo = _settings.Logo ?? (_settings.Logo = new MeshtasticLogoSettings());
            var dialog = new Dialog("Logo settings", 58, 14);
            var enabled = new CheckBox("Show logo frame on the chat page") { X = 1, Y = 1, Checked = logo.ShowLogo };
            var automatic = new CheckBox("Automatically change logos") { X = 1, Y = 2, Checked = logo.AutomaticRotation };
            var interval = new TextField(logo.RotationIntervalSeconds.ToString(CultureInfo.InvariantCulture)) { X = 34, Y = 3, Width = 8 };
            var width = new TextField(logo.InnerWidth.ToString(CultureInfo.InvariantCulture)) { X = 34, Y = 4, Width = 8 };
            var height = new TextField(logo.InnerHeight.ToString(CultureInfo.InvariantCulture)) { X = 34, Y = 5, Width = 8 };
            dialog.Add(enabled, automatic, new Label("Change logo every (seconds):") { X = 1, Y = 3 }, interval, new Label("Logo and chat width:") { X = 1, Y = 4 }, width, new Label("Logo height:") { X = 1, Y = 5 }, height, new Label("F7: Previous logo | F8: Next logo") { X = 1, Y = 7 });
            dialog.KeyPress += e =>
            {
                if (e.KeyEvent.Key == Key.F7) { ShowPreviousLogo(); e.Handled = true; }
                else if (e.KeyEvent.Key == Key.F8) { ShowNextLogo(); e.Handled = true; }
            };
            var save = new Button("Save", true);
            save.Clicked += delegate
            {
                int seconds, innerWidth, innerHeight;
                if (!Int32.TryParse(interval.Text.ToString(), out seconds) || seconds < 1)
                {
                    MessageBox.ErrorQuery("Logo settings", "The interval must be at least one second.", "OK");
                    return;
                }
                if (!Int32.TryParse(width.Text.ToString(), out innerWidth) || innerWidth < 20)
                {
                    MessageBox.ErrorQuery("Logo settings", "The logo and chat width must be at least 20 characters.", "OK");
                    return;
                }
                if (!Int32.TryParse(height.Text.ToString(), out innerHeight) || innerHeight < 1)
                {
                    MessageBox.ErrorQuery("Logo settings", "The logo height must be at least one character.", "OK");
                    return;
                }
                logo.ShowLogo = enabled.Checked;
                logo.AutomaticRotation = automatic.Checked;
                logo.RotationIntervalSeconds = seconds;
                logo.InnerWidth = innerWidth;
                logo.InnerHeight = innerHeight;
                MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings);
                ApplyLogoSettings();
                Application.RequestStop();
            };
            var cancel = new Button("Cancel");
            cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.AddButton(save); dialog.AddButton(cancel);
            Application.Run(dialog);
        }

        private static void ApplyLogoSettings()
        {
            if (_logoFrame == null || _channelsFrame == null || _directChatsFrame == null) return;
            if (_settings.Logo == null) _settings.Logo = new MeshtasticLogoSettings();
            var visible = _settings.Logo.ShowLogo;
            var sidebarOuterWidth = GetSidebarOuterWidth();
            _logoFrame.Width = sidebarOuterWidth;
            _logoFrame.Height = GetLogoOuterHeight();
            _channelsFrame.Width = sidebarOuterWidth;
            _directChatsFrame.Width = sidebarOuterWidth;
            if (_nodeInfoFrame != null) _nodeInfoFrame.X = sidebarOuterWidth + 1;
            if (_messagesFrame != null) _messagesFrame.X = sidebarOuterWidth + 1;
            if (_inputFrame != null) _inputFrame.X = sidebarOuterWidth + 1;
            _logoFrame.Visible = visible;
            _channelsFrame.Y = visible ? GetLogoOuterHeight() : 0;
            _directChatsFrame.Y = Pos.Bottom(_channelsFrame);
            if (visible)
            {
                LoadLogoFiles();
                ShowCurrentLogo();
            }
            _chatPage.LayoutSubviews();
            _chatPage.SetNeedsDisplay();
        }

        private static int GetSidebarOuterWidth()
        {
            var innerWidth = _settings == null || _settings.Logo == null ? 32 : Math.Max(20, _settings.Logo.InnerWidth);
            return innerWidth + 2;
        }

        private static int GetLogoOuterHeight()
        {
            var innerHeight = _settings == null || _settings.Logo == null ? 8 : Math.Max(1, _settings.Logo.InnerHeight);
            return innerHeight + 2;
        }

        private static void LoadLogoFiles()
        {
            LogoFiles.Clear();
            var applicationFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo");
            var workingFolder = Path.Combine(Environment.CurrentDirectory, "logo");
            var folder = Directory.Exists(applicationFolder) ? applicationFolder : workingFolder;
            if (Directory.Exists(folder)) LogoFiles.AddRange(Directory.GetFiles(folder, "*.txt").OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase));
            _logoIndex = LogoFiles.FindIndex(file => String.Equals(Path.GetFileName(file), _settings.Logo.LastLogoFileName, StringComparison.OrdinalIgnoreCase));
            if (_logoIndex < 0) _logoIndex = 0;
            _nextLogoChangeUtc = DateTime.MinValue;
        }

        private static void RotateLogoIfDue()
        {
            if (_settings == null || _settings.Logo == null || !_settings.Logo.ShowLogo || !_settings.Logo.AutomaticRotation || _logoFrame == null || !_logoFrame.Visible || DateTime.UtcNow < _nextLogoChangeUtc) return;
            if (LogoFiles.Count > 1) _logoIndex = (_logoIndex + 1) % LogoFiles.Count;
            ShowCurrentLogo();
        }

        private static void ShowNextLogo()
        {
            if (_settings == null || _settings.Logo == null || !_settings.Logo.ShowLogo || _logoFrame == null || !_logoFrame.Visible) return;
            if (LogoFiles.Count == 0) LoadLogoFiles();
            if (LogoFiles.Count > 1) _logoIndex = (_logoIndex + 1) % LogoFiles.Count;
            ShowCurrentLogo();
        }

        private static void ShowPreviousLogo()
        {
            if (_settings == null || _settings.Logo == null || !_settings.Logo.ShowLogo || _logoFrame == null || !_logoFrame.Visible) return;
            if (LogoFiles.Count == 0) LoadLogoFiles();
            if (LogoFiles.Count > 1) _logoIndex = (_logoIndex - 1 + LogoFiles.Count) % LogoFiles.Count;
            ShowCurrentLogo();
        }

        private static void ShowCurrentLogo()
        {
            if (_logoText == null) return;
            if (LogoFiles.Count == 0) _logoText.Text = "No logo files found.\nPlace *.txt files in .\\logo.";
            else
            {
                try { _logoText.Text = File.ReadAllText(LogoFiles[_logoIndex]); _settings.Logo.LastLogoFileName = Path.GetFileName(LogoFiles[_logoIndex]); }
                catch { _logoText.Text = "Could not read logo file."; }
            }
            _nextLogoChangeUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, _settings.Logo.RotationIntervalSeconds));
            _logoText.SetNeedsDisplay();
        }

        private static void ShowSettings()
        {
            var c = _settings.Connection;
            var dialog = new Dialog("Connection settings", 70, 14);
            var transport = new RadioGroup(new Rect(1, 1, 20, 2), new ustring[] { "Serial", "TCP" }) { SelectedItem = c.Transport == MeshtasticTransportType.Serial ? 0 : 1 };
            var serial = new TextField(c.SerialPort) { X = 16, Y = 4, Width = 20 };
            var baud = new TextField(c.SerialBaudRate.ToString()) { X = 16, Y = 5, Width = 20 };
            var host = new TextField(c.TcpHost) { X = 16, Y = 7, Width = 35 };
            var port = new TextField(c.TcpPort.ToString()) { X = 16, Y = 8, Width = 20 };
            dialog.Add(transport, new Label("Serial port:") { X = 1, Y = 4 }, serial, new Label("Baud rate:") { X = 1, Y = 5 }, baud, new Label("TCP host:") { X = 1, Y = 7 }, host, new Label("TCP port:") { X = 1, Y = 8 }, port);
            var save = new Button("Save", true);
            save.Clicked += delegate { int parsedBaud, parsedPort; if (!Int32.TryParse(baud.Text.ToString(), out parsedBaud) || !Int32.TryParse(port.Text.ToString(), out parsedPort)) { MessageBox.ErrorQuery("Settings", "Baud rate and port must be numbers.", "OK"); return; } c.Transport = transport.SelectedItem == 0 ? MeshtasticTransportType.Serial : MeshtasticTransportType.Tcp; c.SerialPort = serial.Text.ToString(); c.SerialBaudRate = parsedBaud; c.TcpHost = host.Text.ToString(); c.TcpPort = parsedPort; try { MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings); Application.RequestStop(); } catch (Exception ex) { MessageBox.ErrorQuery("Settings", ex.Message, "OK"); } };
            var cancel = new Button("Cancel");
            cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.AddButton(save);
            dialog.AddButton(cancel);
            Application.Run(dialog);
        }

        private static void ShowAlertSettings()
        {
            var dialog = new Dialog("Alert settings", 55, 8);
            var beep = new CheckBox("Beep for new incoming messages") { X = 1, Y = 1, Checked = _settings.EnableNewMessageBeep };
            var save = new Button("Save", true);
            save.Clicked += delegate { _settings.EnableNewMessageBeep = beep.Checked; MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings); Application.RequestStop(); };
            var cancel = new Button("Cancel");
            cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.Add(beep); dialog.AddButton(save); dialog.AddButton(cancel);
            Application.Run(dialog);
        }

        private static void ShowTelegramGateways()
        {
            var dialog = new Dialog("Telegram Gateways", 112, 30);
            var gateways = _settings.TelegramGateways.OrderBy(gateway => gateway.ChannelIndex).ToList();
            var list = new ListView { X = 1, Y = 1, Width = 41, Height = Dim.Fill(4) };
            var enabled = new CheckBox("Enabled") { X = 49, Y = 2 };
            var meshToTelegram = new CheckBox("Forward Meshtastic -> Telegram") { X = 49, Y = 3 };
            var telegramToMesh = new CheckBox("Forward Telegram -> Meshtastic") { X = 49, Y = 4 };
            var token = new TextField("") { X = 64, Y = 6, Width = 43 };
            var chatId = new TextField("") { X = 64, Y = 8, Width = 28 };
            var feedbackFrame = new FrameView("Telegram server feedback") { X = 46, Y = 11, Width = Dim.Fill(1), Height = Dim.Fill(4) };
            var feedback = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), WordWrap = true, CanFocus = true, ReadOnly = true };
            feedbackFrame.Add(feedback);
            Action refresh = delegate { list.SetSource(gateways.Select(FormatTelegramGateway).ToList()); };
            Action updateFeedback = delegate
            {
                if (list.SelectedItem < 0 || list.SelectedItem >= gateways.Count) { feedback.Text = "Select a channel to see its Telegram connection feedback."; return; }
                var gateway = gateways[list.SelectedItem];
                var gatewayStatus = _telegramGateways == null ? null : _telegramGateways.GetStatuses().FirstOrDefault(item => item.ChannelIndex == gateway.ChannelIndex);
                feedback.Text = BuildTelegramGatewayFeedback(gateway, gatewayStatus);
            };
            Action load = delegate
            {
                if (list.SelectedItem < 0 || list.SelectedItem >= gateways.Count) return;
                var gateway = gateways[list.SelectedItem]; enabled.Checked = gateway.Enabled; meshToTelegram.Checked = gateway.ForwardMeshtasticToTelegram; telegramToMesh.Checked = gateway.ForwardTelegramToMeshtastic; token.Text = gateway.BotToken ?? ""; chatId.Text = gateway.ChatId == 0 ? "" : gateway.ChatId.ToString(CultureInfo.InvariantCulture); updateFeedback();
            };
            Func<bool> save = delegate
            {
                if (list.SelectedItem < 0 || list.SelectedItem >= gateways.Count) return false;
                long parsedChatId;
                if (enabled.Checked && (String.IsNullOrWhiteSpace(token.Text.ToString()) || !Int64.TryParse(chatId.Text.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedChatId) || parsedChatId == 0))
                {
                    MessageBox.ErrorQuery("Telegram Gateway", "An enabled gateway needs a bot token and a numeric Telegram chat ID.", "OK");
                    return false;
                }
                if (!Int64.TryParse(chatId.Text.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedChatId)) parsedChatId = 0;
                var gateway = gateways[list.SelectedItem]; gateway.Enabled = enabled.Checked; gateway.ForwardMeshtasticToTelegram = meshToTelegram.Checked; gateway.ForwardTelegramToMeshtastic = telegramToMesh.Checked; gateway.BotToken = token.Text.ToString().Trim(); gateway.ChatId = parsedChatId;
                MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings); refresh(); updateFeedback(); return true;
            };
            list.SelectedItemChanged += delegate { load(); };
            dialog.Add(new Label("Channel gateways") { X = 1, Y = 0 }, list,
                new Label("Selected channel:") { X = 46, Y = 1 }, enabled, meshToTelegram, telegramToMesh,
                new Label("Bot API token:") { X = 46, Y = 6 }, token,
                new Label("Telegram chat ID:") { X = 46, Y = 8 }, chatId, feedbackFrame);
            var saveButton = new Button("Save channel", true) { X = 1, Y = Pos.AnchorEnd(2) };
            saveButton.Clicked += delegate { save(); };
            var restart = new Button("Apply / reconnect") { X = Pos.Right(saveButton) + 2, Y = Pos.AnchorEnd(2) };
            restart.Clicked += delegate
            {
                if (!save()) return;
                Task.Run(async delegate { await RestartTelegramGatewaysAsync(); Ui(delegate { MessageBox.Query("Telegram Gateways", "Gateway configuration applied.", "OK"); }); });
            };
            var status = new Button("Connection status") { X = Pos.Right(restart) + 2, Y = Pos.AnchorEnd(2) };
            status.Clicked += ShowTelegramGatewayStatus;
            var close = new Button("Close") { X = Pos.Right(status) + 2, Y = Pos.AnchorEnd(2) };
            close.Clicked += delegate { Application.RequestStop(); };
            dialog.AddButton(saveButton); dialog.AddButton(restart); dialog.AddButton(status); dialog.AddButton(close);
            refresh(); if (gateways.Count > 0) { list.SelectedItem = 0; load(); }
            Application.Run(dialog);
        }

        private static string FormatTelegramGateway(TelegramGatewaySettings gateway)
        {
            var directions = (gateway.ForwardMeshtasticToTelegram ? "M>T" : "") + (gateway.ForwardTelegramToMeshtastic ? (gateway.ForwardMeshtasticToTelegram ? ", T>M" : "T>M") : "");
            return "Ch " + gateway.ChannelIndex + "  " + (gateway.Enabled ? "[enabled] " : "[disabled]") + (gateway.ChatId == 0 ? "not configured" : "chat " + gateway.ChatId.ToString(CultureInfo.InvariantCulture)) + "  " + directions;
        }

        private static string BuildTelegramGatewayFeedback(TelegramGatewaySettings gateway, TelegramGatewayStatus status)
        {
            if (!gateway.Enabled) return "Gateway disabled.";
            if (status == null) return "Gateway has not been started. Connect Meshtastic, then choose Apply / reconnect.";
            return "Connection: " + status.State + "\nBot: " + (status.BotUsername ?? "-") + "\nChat ID: " + status.ChatId.ToString(CultureInfo.InvariantCulture) + "\nMeshtastic -> Telegram: " + status.ForwardedToTelegramCount + "\nTelegram received: " + status.ReceivedFromTelegramCount + "\nTelegram -> Meshtastic: " + status.ForwardedToMeshtasticCount + "\n\nServer feedback:\n" + (String.IsNullOrWhiteSpace(status.ServerFeedback) ? "No feedback received yet." : status.ServerFeedback);
        }

        private static void ShowTelegramGatewayStatus()
        {
            var dialog = new Dialog("Telegram Gateway Status", 112, 28);
            var list = new ListView { X = 1, Y = 1, Width = Dim.Fill(2), Height = 8 };
            var feedbackFrame = new FrameView("Telegram server feedback") { X = 1, Y = 10, Width = Dim.Fill(2), Height = Dim.Fill(4) };
            var feedback = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), WordWrap = true, CanFocus = true, ReadOnly = true };
            feedbackFrame.Add(feedback);
            var statuses = new List<TelegramGatewayStatus>();
            Action updateFeedback = delegate
            {
                var gateways = _settings.TelegramGateways.OrderBy(item => item.ChannelIndex).ToList();
                if (list.SelectedItem < 0 || list.SelectedItem >= gateways.Count) { feedback.Text = "Select a channel to see its Telegram connection feedback."; return; }
                var gateway = gateways[list.SelectedItem];
                feedback.Text = BuildTelegramGatewayFeedback(gateway, statuses.FirstOrDefault(item => item.ChannelIndex == gateway.ChannelIndex));
            };
            Action refresh = delegate
            {
                statuses = _telegramGateways == null ? new List<TelegramGatewayStatus>() : _telegramGateways.GetStatuses().ToList();
                list.SetSource(_settings.TelegramGateways.OrderBy(gateway => gateway.ChannelIndex).Select(gateway => FormatTelegramGatewayStatus(gateway, statuses.FirstOrDefault(status => status.ChannelIndex == gateway.ChannelIndex))).ToList());
                updateFeedback();
            };
            list.SelectedItemChanged += delegate { updateFeedback(); };
            var refreshButton = new Button("Refresh", true) { X = 1, Y = Pos.AnchorEnd(2) };
            refreshButton.Clicked += delegate { refresh(); };
            var close = new Button("Close") { X = Pos.Right(refreshButton) + 2, Y = Pos.AnchorEnd(2) };
            close.Clicked += delegate { Application.RequestStop(); };
            dialog.Add(list, feedbackFrame); dialog.AddButton(refreshButton); dialog.AddButton(close); refresh(); Application.Run(dialog);
        }

        private static string FormatTelegramGatewayStatus(TelegramGatewaySettings gateway, TelegramGatewayStatus status)
        {
            if (!gateway.Enabled) return "Channel " + gateway.ChannelIndex + "  disabled";
            if (status == null) return "Channel " + gateway.ChannelIndex + "  not started";
            var error = String.IsNullOrWhiteSpace(status.LastError) ? "" : " | " + status.LastError;
            return "Channel " + gateway.ChannelIndex + "  " + status.State + "  chat " + status.ChatId.ToString(CultureInfo.InvariantCulture) + "  M->T " + status.ForwardedToTelegramCount + " / Telegram RX " + status.ReceivedFromTelegramCount + " / T->M " + status.ForwardedToMeshtasticCount + error;
        }

        private static async Task RestartTelegramGatewaysAsync()
        {
            var previous = _telegramGateways;
            _telegramGateways = null;
            if (previous != null) { await previous.StopAsync(); previous.Dispose(); }
            var manager = new MeshtasticTelegramGatewayManager(_mesh, _settings.TelegramGateways);
            manager.StatusChanged += delegate { Ui(UpdateStatus); };
            manager.TelegramMessageForwarded += delegate(object sender, TelegramGatewayMessageForwardedEventArgs e)
            {
                Save(delegate
                {
                    var channel = _mesh.Channels.FirstOrDefault(item => item.Index == e.ChannelIndex);
                    _store.AddOutgoing(e.PacketId, _mesh.Device.MyNode == null ? 0u : _mesh.Device.MyNode.MyNodeNum, UInt32.MaxValue, e.Text, e.ChannelIndex, channel == null ? null : channel.Name);
                    Ui(RefreshChats);
                });
            };
            _telegramGateways = manager;
            await manager.StartAsync();
        }

        private static void ShowChatBots()
        {
            var dialog = new Dialog("Chat bots", 90, 22);
            var list = new ListView { X = 1, Y = 1, Width = Dim.Fill(2), Height = Dim.Fill(4) };
            Action refresh = delegate { list.SetSource(_settings.ChatBots.Select(FormatChatBot).ToList()); if (_settings.ChatBots.Count > 0) list.SelectedItem = Math.Min(Math.Max(0, list.SelectedItem), _settings.ChatBots.Count - 1); };
            refresh();
            var add = new Button("Add") { X = 1, Y = Pos.AnchorEnd(2) };
            add.Clicked += delegate { EditChatBot(null); refresh(); };
            var edit = new Button("Edit") { X = Pos.Right(add) + 2, Y = Pos.AnchorEnd(2) };
            edit.Clicked += delegate { if (list.SelectedItem >= 0 && list.SelectedItem < _settings.ChatBots.Count) { EditChatBot(_settings.ChatBots[list.SelectedItem]); refresh(); } };
            var toggle = new Button("Enable/disable") { X = Pos.Right(edit) + 2, Y = Pos.AnchorEnd(2) };
            toggle.Clicked += delegate { if (list.SelectedItem >= 0 && list.SelectedItem < _settings.ChatBots.Count) { _settings.ChatBots[list.SelectedItem].Enabled = !_settings.ChatBots[list.SelectedItem].Enabled; MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings); refresh(); } };
            var delete = new Button("Delete") { X = Pos.Right(toggle) + 2, Y = Pos.AnchorEnd(2) };
            delete.Clicked += delegate { if (list.SelectedItem >= 0 && list.SelectedItem < _settings.ChatBots.Count && MessageBox.Query("Delete bot", "Delete the selected chat bot?", "Delete", "Cancel") == 0) { _settings.ChatBots.RemoveAt(list.SelectedItem); MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings); refresh(); } };
            var close = new Button("Close") { X = Pos.Right(delete) + 2, Y = Pos.AnchorEnd(2) };
            close.Clicked += delegate { Application.RequestStop(); };
            dialog.Add(list, add, edit, toggle, delete, close);
            Application.Run(dialog);
        }

        private static void ShowHttpBots()
        {
            var dialog = new Dialog("HTTP bots", 90, 18); var list = new ListView { X = 1, Y = 1, Width = Dim.Fill(2), Height = Dim.Fill(4) };
            Action refresh = delegate { list.SetSource(_settings.HttpBots.Select(bot => (bot.Enabled ? "[on] " : "[off] ") + bot.Command + " | " + bot.Url).ToList()); };
            var add = new Button("Add") { X = 1, Y = Pos.AnchorEnd(2) }; add.Clicked += delegate { var bot = new MeshtasticHttpBotSettings(); EditHttpBot(bot); _settings.HttpBots.Add(bot); MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings); refresh(); };
            var edit = new Button("Edit") { X = Pos.Right(add) + 2, Y = Pos.AnchorEnd(2) }; edit.Clicked += delegate { if (list.SelectedItem >= 0 && list.SelectedItem < _settings.HttpBots.Count) { EditHttpBot(_settings.HttpBots[list.SelectedItem]); MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings); refresh(); } };
            var close = new Button("Close") { X = Pos.Right(edit) + 2, Y = Pos.AnchorEnd(2) }; close.Clicked += delegate { Application.RequestStop(); }; dialog.Add(list, add, edit, close); refresh(); Application.Run(dialog);
        }
        private static void EditHttpBot(MeshtasticHttpBotSettings bot)
        {
            var dialog = new Dialog("HTTP bot", 92, 30); var command = new TextField(bot.Command) { X = 20, Y = 1, Width = 25 }; var url = new TextField(bot.Url) { X = 20, Y = 2, Width = 65 }; var max = new TextField(bot.MaximumReplyLength.ToString()) { X = 20, Y = 3, Width = 8 }; var limit = new TextField(bot.MaximumParameterCount.ToString()) { X = 20, Y = 4, Width = 8 }; var enabled = new CheckBox("Enabled") { X = 1, Y = 6, Checked = bot.Enabled }; var direct = new CheckBox("React to direct messages") { X = 24, Y = 6, Checked = bot.ReactToDirectMessages }; var favorites = new CheckBox("Favorites only") { X = 24, Y = 7, Checked = bot.FavoritesOnly };
            dialog.Add(new Label("Command:") { X = 1, Y = 1 }, command, new Label("GET URL:") { X = 1, Y = 2 }, url, new Label("Max reply:") { X = 1, Y = 3 }, max, new Label("Max params:") { X = 1, Y = 4 }, limit, enabled, direct, favorites, new Label("React to channels:") { X = 1, Y = 8 }, new Label("Custom GET parameters (variables allowed):") { X = 1, Y = 12 });
            var channels = new List<CheckBox>(); for (var i = 0; i < 8; i++) { var check = new CheckBox("Channel " + i) { X = 1 + (i % 4) * 20, Y = 9 + i / 4, Checked = bot.ReactToChannels != null && bot.ReactToChannels.Contains(i) }; channels.Add(check); dialog.Add(check); }
            var names = new List<TextField>(); var values = new List<TextField>(); for (var i = 0; i < 5; i++) { var name = new TextField(bot.QueryParameters[i].Name) { X = 1, Y = 13 + i, Width = 20 }; var value = new TextField(bot.QueryParameters[i].Value) { X = 24, Y = 13 + i, Width = 60 }; names.Add(name); values.Add(value); dialog.Add(name, value); }
            var save = new Button("Save", true); save.Clicked += delegate { int reply, parameters; if (!Int32.TryParse(max.Text.ToString(), out reply) || !Int32.TryParse(limit.Text.ToString(), out parameters) || String.IsNullOrWhiteSpace(command.Text.ToString()) || String.IsNullOrWhiteSpace(url.Text.ToString())) { MessageBox.ErrorQuery("HTTP bot", "Command, URL and numeric limits are required.", "OK"); return; } bot.Command = command.Text.ToString(); bot.Url = url.Text.ToString(); bot.MaximumReplyLength = reply; bot.MaximumParameterCount = parameters; bot.Enabled = enabled.Checked; bot.ReactToDirectMessages = direct.Checked; bot.FavoritesOnly = favorites.Checked; bot.ReactToChannels = channels.Select((check, index) => new { check, index }).Where(item => item.check.Checked).Select(item => item.index).ToList(); for (var i = 0; i < 5; i++) { bot.QueryParameters[i].Name = names[i].Text.ToString(); bot.QueryParameters[i].Value = values[i].Text.ToString(); } Application.RequestStop(); }; var cancel = new Button("Cancel"); cancel.Clicked += delegate { Application.RequestStop(); }; dialog.AddButton(save); dialog.AddButton(cancel); Application.Run(dialog);
        }

        private static void ShowChatBotDebug() { ShowBotDebug("Chat bot Debug", _lastChatBotRequest, _lastChatBotOutput); }
        private static void ShowHttpBotDebug() { ShowBotDebug("HTTP bot Debug", _lastHttpBotRequest, _lastHttpBotOutput); }
        private static void ShowBotDebug(string title, string request, string output)
        {
            var dialog = new Dialog(title, 108, 30);
            var requestFrame = new FrameView("Last complete request") { X = 1, Y = 1, Width = Dim.Fill(2), Height = 12 };
            var requestView = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), WordWrap = true, CanFocus = true, ReadOnly = true, Text = request ?? "" };
            requestFrame.Add(requestView);
            var outputFrame = new FrameView("Last bot output") { X = 1, Y = 13, Width = Dim.Fill(2), Height = Dim.Fill(4) };
            var outputView = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), WordWrap = true, CanFocus = true, ReadOnly = true, Text = output ?? "" };
            outputFrame.Add(outputView);
            var copyRequest = new Button("Copy request", true); copyRequest.Clicked += delegate { CopyDebugText(request ?? ""); };
            var copyOutput = new Button("Copy output"); copyOutput.Clicked += delegate { CopyDebugText(output ?? ""); };
            var close = new Button("Close"); close.Clicked += delegate { Application.RequestStop(); };
            dialog.Add(requestFrame, outputFrame); dialog.AddButton(copyRequest); dialog.AddButton(copyOutput); dialog.AddButton(close); Application.Run(dialog);
        }

        private static void CopyDebugText(string text)
        {
            if (!Clipboard.TrySetClipboardData(text ?? "")) MessageBox.ErrorQuery("Clipboard", "The clipboard is not available.", "OK");
        }

        private static void ShowInfo()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            var buildDate = assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute)).OfType<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == "BuildDateUtc");
            MessageBox.Query("Info", "Meshtastic ConsoleClient\n\nCreated by Tobias Krista with support from AI.\nBuild: " + (version == null ? "-" : version.ToString()) + "\nCompiled: " + (buildDate == null ? "-" : buildDate.Value + " UTC") + "\n\nThis program is published under the GNU General Public License v3.0 (GPLv3).", "OK");
        }

        private static string FormatChatBot(MeshtasticChatBotSettings bot)
        {
            return (bot.Enabled ? "[on]  " : "[off] ") + bot.Command + "  | " + (bot.CaseSensitive ? "case-sensitive" : "ignore case") + "  | " + bot.ExecutablePath;
        }

        private static void EditChatBot(MeshtasticChatBotSettings existing)
        {
            var isNew = existing == null;
            var bot = existing ?? new MeshtasticChatBotSettings();
            var dialog = new Dialog(isNew ? "Add chat bot" : "Edit chat bot", 96, 30);
            var command = new TextField(bot.Command) { X = 30, Y = 1, Width = 30 };
            var executable = new TextField(bot.ExecutablePath) { X = 30, Y = 2, Width = 45 };
            var maximum = new TextField(bot.MaximumReplyLength.ToString()) { X = 30, Y = 3, Width = 10 };
            var parameterLimit = new TextField(bot.MaximumParameterCount.ToString()) { X = 30, Y = 4, Width = 10 };
            var prefix = new TextField(bot.ArgumentPrefix) { X = 30, Y = 5, Width = 45 };
            var suffix = new TextField(bot.ArgumentSuffix) { X = 30, Y = 6, Width = 45 };
            var enabled = new CheckBox("Enabled") { X = 1, Y = 8, Checked = bot.Enabled };
            var caseSensitive = new CheckBox("Case-sensitive command") { X = 1, Y = 9, Checked = bot.CaseSensitive };
            var directMessages = new CheckBox("React to direct messages") { X = 30, Y = 8, Checked = bot.ReactToDirectMessages };
            var favoritesOnly = new CheckBox("Favorites only") { X = 30, Y = 9, Checked = bot.FavoritesOnly };
            var channelChecks = new List<CheckBox>();
            for (var channel = 0; channel < 8; channel++)
            {
                var check = new CheckBox("Channel " + channel) { X = 1 + (channel % 4) * 20, Y = 12 + channel / 4, Checked = bot.ReactToChannels != null && bot.ReactToChannels.Contains(channel) };
                channelChecks.Add(check);
            }
            var variables = new Button("?") { X = 77, Y = 5 };
            variables.Clicked += ShowChatBotVariableHelp;
            var previewFrame = new FrameView("Example command line") { X = 1, Y = 15, Width = Dim.Fill(2), Height = Dim.Fill(4) };
            var preview = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), WordWrap = true, CanFocus = true, ReadOnly = true };
            previewFrame.Add(preview);
            Action updatePreview = delegate
            {
                int count; if (!Int32.TryParse(parameterLimit.Text.ToString(), out count)) count = 0;
                var commandParts = new List<string>(); var before = ExpandChatBotVariableExamples(prefix.Text.ToString()); var after = ExpandChatBotVariableExamples(suffix.Text.ToString());
                if (!String.IsNullOrWhiteSpace(before)) commandParts.Add(before);
                for (var parameter = 1; parameter <= Math.Min(Math.Max(0, count), 10); parameter++) commandParts.Add("P" + parameter.ToString("D2"));
                if (!String.IsNullOrWhiteSpace(after)) commandParts.Add(after);
                preview.Text = (String.IsNullOrWhiteSpace(executable.Text.ToString()) ? "<executable>" : executable.Text.ToString()) + " " + String.Join(" ", commandParts);
            };
            prefix.TextChanged += delegate { updatePreview(); }; suffix.TextChanged += delegate { updatePreview(); }; parameterLimit.TextChanged += delegate { updatePreview(); }; executable.TextChanged += delegate { updatePreview(); };
            dialog.Add(new Label("Command (first word):") { X = 1, Y = 1 }, command, new Label("Executable path:") { X = 1, Y = 2 }, executable, new Label("Maximum reply length:") { X = 1, Y = 3 }, maximum, new Label("Maximum parameters:") { X = 1, Y = 4 }, parameterLimit, new Label("Argument before parameters:") { X = 1, Y = 5 }, prefix, new Label("Argument after parameters:") { X = 1, Y = 6 }, suffix, variables, enabled, caseSensitive, directMessages, favoritesOnly, new Label("React to channels:") { X = 1, Y = 11 });
            dialog.Add(channelChecks.ToArray()); dialog.Add(previewFrame); updatePreview();
            var save = new Button("Save", true);
            save.Clicked += delegate
            {
                int replyLength, maxParameters;
                if (String.IsNullOrWhiteSpace(command.Text.ToString()) || String.IsNullOrWhiteSpace(executable.Text.ToString()) || !Int32.TryParse(maximum.Text.ToString(), out replyLength) || replyLength < 1 || !Int32.TryParse(parameterLimit.Text.ToString(), out maxParameters) || maxParameters < 0)
                {
                    MessageBox.ErrorQuery("Chat bot", "Command, executable path, maximum reply length and parameter limit are required.", "OK");
                    return;
                }
                bot.Command = command.Text.ToString().Trim(); bot.ExecutablePath = executable.Text.ToString().Trim(); bot.MaximumReplyLength = replyLength; bot.MaximumParameterCount = maxParameters; bot.ArgumentPrefix = prefix.Text.ToString(); bot.ArgumentSuffix = suffix.Text.ToString(); bot.Enabled = enabled.Checked; bot.CaseSensitive = caseSensitive.Checked; bot.ReactToDirectMessages = directMessages.Checked; bot.FavoritesOnly = favoritesOnly.Checked; bot.ReactToChannels = channelChecks.Select((check, channel) => new { check, channel }).Where(item => item.check.Checked).Select(item => item.channel).ToList();
                if (isNew) _settings.ChatBots.Add(bot);
                MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings);
                Application.RequestStop();
            };
            var cancel = new Button("Cancel");
            cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.AddButton(save); dialog.AddButton(cancel);
            Application.Run(dialog);
        }

        private static void ShowChatBotVariableHelp()
        {
            MessageBox.Query("Bot variables", "The following variables can be used in the argument fields:\n\n{id}, {number}, {name}, {shortname}\n{latitude}, {longitude}, {battery}, {hops}\n{channel}, {channelName}, {from}, {to}\n{packetId}, {rssi}, {snr}, {hopLimit}\n{message}, {receivedUtc}, {deviceId}\n\nUnavailable node values are replaced by an empty string.", "OK");
        }

        private static string ExpandChatBotVariableExamples(string template)
        {
            if (String.IsNullOrEmpty(template)) return template;
            var values = new Dictionary<string, string> { { "{id}", "!a1b2c3d4" }, { "{number}", "2712847316" }, { "{name}", "Example Node" }, { "{shortname}", "EXMP" }, { "{latitude}", "48.137154" }, { "{longitude}", "11.576124" }, { "{battery}", "87" }, { "{hops}", "2" }, { "{channel}", "0" }, { "{channelName}", "LongFast" }, { "{from}", "2712847316" }, { "{to}", "4294967295" }, { "{packetId}", "12345678" }, { "{rssi}", "-97" }, { "{snr}", "7.5" }, { "{hopLimit}", "3" }, { "{message}", "!command example" }, { "{receivedUtc}", "2026-08-10T12:00:00.0000000Z" }, { "{deviceId}", "!11223344" } };
            foreach (var value in values) template = template.Replace(value.Key, value.Value);
            return template;
        }

        private static async Task RunMatchingChatBotsAsync(MeshMessage message)
        {
            var parts = (message.Text ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            foreach (var bot in _settings.ChatBots.Where(x => x.Enabled && !String.IsNullOrWhiteSpace(x.Command) && !String.IsNullOrWhiteSpace(x.ExecutablePath)).ToList())
            {
                if (message.IsChannelMessage)
                {
                    var channelIndex = (int)message.ChannelIndex.GetValueOrDefault();
                    if (bot.ReactToChannels == null || !bot.ReactToChannels.Contains(channelIndex)) continue;
                }
                else if (!bot.ReactToDirectMessages) continue;

                var sourceNode = _mesh.Nodes.FirstOrDefault(node => node.Number == message.From);
                if (bot.FavoritesOnly)
                {
                    var storedNode = _store.GetNodes(StoredNodeSort.NodeId).FirstOrDefault(node => node.NodeNumber == message.From);
                    if (storedNode == null || !storedNode.IsFavorite) continue;
                }
                var comparison = bot.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                if (!String.Equals(parts[0], bot.Command, comparison)) continue;
                var arguments = new List<string>();
                var prefix = ExpandChatBotVariables(bot.ArgumentPrefix, message, sourceNode);
                var suffix = ExpandChatBotVariables(bot.ArgumentSuffix, message, sourceNode);
                if (!String.IsNullOrWhiteSpace(prefix)) arguments.Add(prefix);
                arguments.AddRange(parts.Skip(1).Take(Math.Max(0, bot.MaximumParameterCount)));
                if (!String.IsNullOrWhiteSpace(suffix)) arguments.Add(suffix);
                _lastChatBotRequest = bot.ExecutablePath + " " + String.Join(" ", arguments.Select(QuoteProcessArgument));
                var output = await RunChatBotProcessAsync(bot, arguments);
                _lastChatBotOutput = output ?? "";
                if (String.IsNullOrWhiteSpace(output)) continue;
                if (output.Length > bot.MaximumReplyLength) output = output.Substring(0, bot.MaximumReplyLength);
                uint packetId;
                if (message.IsChannelMessage)
                {
                    var channel = (int)message.ChannelIndex.GetValueOrDefault();
                    packetId = await _mesh.SendTextToChannelWithIdAsync(channel, output);
                    var channelName = _mesh.Channels.FirstOrDefault(x => x.Index == channel);
                    _store.AddOutgoing(packetId, _mesh.Device.MyNode == null ? 0u : _mesh.Device.MyNode.MyNodeNum, UInt32.MaxValue, output, channel, channelName == null ? null : channelName.Name);
                }
                else
                {
                    packetId = await _mesh.SendTextWithIdAsync(message.From, output);
                    _store.AddOutgoing(packetId, _mesh.Device.MyNode == null ? 0u : _mesh.Device.MyNode.MyNodeNum, message.From, output, null);
                }
                Ui(RefreshChats);
            }
        }

        private static string ExpandChatBotVariables(string template, MeshMessage message, MeshNode node)
        {
            if (String.IsNullOrEmpty(template)) return template;
            var channelIndex = message.IsChannelMessage ? (int?)message.ChannelIndex.GetValueOrDefault() : null;
            var channel = channelIndex.HasValue ? _mesh.Channels.FirstOrDefault(item => item.Index == channelIndex.Value) : null;
            var nodeId = node == null || String.IsNullOrWhiteSpace(node.Id) ? "!" + message.From.ToString("x8") : node.Id;
            var values = new Dictionary<string, string>
            {
                { "{id}", nodeId }, { "{number}", message.From.ToString(CultureInfo.InvariantCulture) },
                { "{name}", node == null ? "" : (node.LongName ?? "") }, { "{shortname}", node == null ? "" : (node.ShortName ?? "") },
                { "{latitude}", node == null || !node.Latitude.HasValue ? "" : node.Latitude.Value.ToString(CultureInfo.InvariantCulture) }, { "{longitude}", node == null || !node.Longitude.HasValue ? "" : node.Longitude.Value.ToString(CultureInfo.InvariantCulture) },
                { "{battery}", node == null || !node.BatteryLevel.HasValue ? "" : node.BatteryLevel.Value.ToString(CultureInfo.InvariantCulture) }, { "{hops}", node == null || !node.HopsAway.HasValue ? "" : node.HopsAway.Value.ToString(CultureInfo.InvariantCulture) },
                { "{channel}", channelIndex.HasValue ? channelIndex.Value.ToString(CultureInfo.InvariantCulture) : "" }, { "{channelName}", channel == null ? "" : (channel.Name ?? "") },
                { "{from}", message.From.ToString(CultureInfo.InvariantCulture) }, { "{to}", message.To.ToString(CultureInfo.InvariantCulture) },
                { "{packetId}", message.Packet.Id.ToString(CultureInfo.InvariantCulture) }, { "{rssi}", message.Packet.RxRssi.ToString(CultureInfo.InvariantCulture) }, { "{snr}", message.Packet.RxSnr.ToString(CultureInfo.InvariantCulture) }, { "{hopLimit}", message.Packet.HopLimit.ToString(CultureInfo.InvariantCulture) },
                { "{message}", message.Text ?? "" }, { "{receivedUtc}", message.ReceivedAtUtc.ToString("o", CultureInfo.InvariantCulture) }, { "{deviceId}", _mesh.Device.NodeId ?? "" }
            };
            foreach (var value in values) template = template.Replace(value.Key, value.Value);
            return template;
        }

        private static async Task RunMatchingHttpBotsAsync(MeshMessage message)
        {
            var parts = (message.Text ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries); if (parts.Length == 0) return;
            var node = _mesh.Nodes.FirstOrDefault(item => item.Number == message.From);
            foreach (var bot in _settings.HttpBots.Where(item => item.Enabled && !String.IsNullOrWhiteSpace(item.Command) && !String.IsNullOrWhiteSpace(item.Url)).ToList())
            {
                if (message.IsChannelMessage ? bot.ReactToChannels == null || !bot.ReactToChannels.Contains((int)message.ChannelIndex.GetValueOrDefault()) : !bot.ReactToDirectMessages) continue;
                if (bot.FavoritesOnly && !_store.GetNodes().Any(item => item.NodeNumber == message.From && item.IsFavorite)) continue;
                if (!String.Equals(parts[0], bot.Command, bot.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) continue;
                var query = new Dictionary<string, string> { { "id", ExpandChatBotVariables("{id}", message, node) }, { "number", ExpandChatBotVariables("{number}", message, node) }, { "name", ExpandChatBotVariables("{name}", message, node) }, { "shortname", ExpandChatBotVariables("{shortname}", message, node) }, { "latitude", ExpandChatBotVariables("{latitude}", message, node) }, { "longitude", ExpandChatBotVariables("{longitude}", message, node) }, { "battery", ExpandChatBotVariables("{battery}", message, node) }, { "hops", ExpandChatBotVariables("{hops}", message, node) }, { "channel", ExpandChatBotVariables("{channel}", message, node) }, { "channelName", ExpandChatBotVariables("{channelName}", message, node) }, { "message", message.Text ?? "" } };
                for (var index = 0; index < Math.Min(bot.MaximumParameterCount, parts.Length - 1); index++) query["p" + (index + 1).ToString("D2")] = parts[index + 1];
                foreach (var parameter in bot.QueryParameters.Where(parameter => !String.IsNullOrWhiteSpace(parameter.Name))) query[parameter.Name] = ExpandChatBotVariables(parameter.Value ?? "", message, node);
                try
                {
                    var separator = bot.Url.Contains("?") ? "&" : "?"; var url = bot.Url + separator + String.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))); _lastHttpBotRequest = url;
                    var output = await HttpBotHttpClient.GetStringAsync(url); _lastHttpBotOutput = output ?? ""; if (String.IsNullOrWhiteSpace(output)) continue; if (output.Length > bot.MaximumReplyLength) output = output.Substring(0, bot.MaximumReplyLength);
                    uint packetId = message.IsChannelMessage ? await _mesh.SendTextToChannelWithIdAsync((int)message.ChannelIndex.GetValueOrDefault(), output) : await _mesh.SendTextWithIdAsync(message.From, output);
                    _store.AddOutgoing(packetId, _mesh.Device.MyNode == null ? 0u : _mesh.Device.MyNode.MyNodeNum, message.IsChannelMessage ? UInt32.MaxValue : message.From, output, message.IsChannelMessage ? (int?)message.ChannelIndex.GetValueOrDefault() : null);
                    Ui(RefreshChats);
                }
                catch (Exception ex) { _lastHttpBotOutput = ex.ToString(); }
            }
        }

        private static async Task<string> RunChatBotProcessAsync(MeshtasticChatBotSettings bot, IEnumerable<string> arguments)
        {
            if (!File.Exists(bot.ExecutablePath)) return "Bot executable not found.";
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo { FileName = bot.ExecutablePath, Arguments = String.Join(" ", arguments.Select(QuoteProcessArgument)), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true };
                    process.Start();
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(30000)) { try { process.Kill(); } catch { } return "Bot execution timed out."; }
                    var output = (await stdout).Trim();
                    var error = (await stderr).Trim();
                    return String.IsNullOrWhiteSpace(output) ? error : output;
                }
            }
            catch (Exception ex) { return "Bot error: " + ex.Message; }
        }

        private static string QuoteProcessArgument(string value)
        {
            if (String.IsNullOrEmpty(value)) return "\"\"";
            if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return value;
            var result = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { backslashes++; continue; }
                if (character == '"') result.Append('\\', backslashes * 2 + 1);
                else result.Append('\\', backslashes);
                result.Append(character); backslashes = 0;
            }
            result.Append('\\', backslashes * 2); result.Append('"');
            return result.ToString();
        }

        private static void ShowAppearanceSettings()
        {
            var appearance = _settings.Appearance;
            var propertyNames = new[] { "Background", "Frame", "Page frame", "Normal text", "Sent messages", "Received messages", "Status text", "Input background", "Input text", "Menu background", "Menu text", "Button background", "Button text", "Logo frame", "Logo text", "Emoji text" };
            var values = new[] { appearance.BackgroundColor, appearance.FrameColor, appearance.PageFrameColor, appearance.TextColor, appearance.SentMessageColor, appearance.ReceivedMessageColor, appearance.StatusTextColor, appearance.InputBackgroundColor, appearance.InputTextColor, appearance.MenuBackgroundColor, appearance.MenuTextColor, appearance.ButtonBackgroundColor, appearance.ButtonTextColor, appearance.LogoFrameColor, appearance.LogoTextColor, appearance.EmojiTextColor };
            var colorNames = new[] { "Black", "Blue", "Green", "Cyan", "Red", "Magenta", "Brown", "Gray", "DarkGray", "BrightBlue", "BrightGreen", "BrightCyan", "BrightRed", "BrightMagenta", "BrightYellow", "White" };
            var dialog = new Dialog("Appearance", 68, 22);
            var properties = new ListView(propertyNames) { X = 1, Y = 1, Width = 23, Height = colorNames.Length };
            var colors = new RadioGroup(new Rect(27, 1, 28, colorNames.Length), colorNames.Select(x => (ustring)x).ToArray());
            var changingSelection = false;
            Action selectColor = delegate
            {
                var propertyIndex = properties.SelectedItem;
                if (propertyIndex < 0 || propertyIndex >= values.Length) return;
                var colorIndex = Array.IndexOf(colorNames, values[propertyIndex]);
                changingSelection = true; colors.SelectedItem = colorIndex < 0 ? 0 : colorIndex; changingSelection = false;
            };
            properties.SelectedItemChanged += delegate { selectColor(); };
            colors.SelectedItemChanged += delegate
            {
                if (changingSelection) return;
                var propertyIndex = properties.SelectedItem;
                if (propertyIndex >= 0 && propertyIndex < values.Length && colors.SelectedItem >= 0 && colors.SelectedItem < colorNames.Length) values[propertyIndex] = colorNames[colors.SelectedItem];
            };
            properties.SelectedItem = 0;
            selectColor();
            dialog.Add(properties, colors);
            var save = new Button("Save", true);
            save.Clicked += delegate
            {
                appearance.BackgroundColor = values[0]; appearance.FrameColor = values[1]; appearance.PageFrameColor = values[2]; appearance.TextColor = values[3]; appearance.SentMessageColor = values[4]; appearance.ReceivedMessageColor = values[5]; appearance.StatusTextColor = values[6]; appearance.InputBackgroundColor = values[7]; appearance.InputTextColor = values[8]; appearance.MenuBackgroundColor = values[9]; appearance.MenuTextColor = values[10]; appearance.ButtonBackgroundColor = values[11]; appearance.ButtonTextColor = values[12]; appearance.LogoFrameColor = values[13]; appearance.LogoTextColor = values[14]; appearance.EmojiTextColor = values[15];
                MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings);
                ApplyAppearanceSettings();
                Application.RequestStop();
            };
            var cancel = new Button("Cancel");
            cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.AddButton(save); dialog.AddButton(cancel);
            Application.Run(dialog);
        }

        private static void ApplyAppearanceSettings()
        {
            if (_settings.Appearance == null) _settings.Appearance = new MeshtasticAppearanceSettings();
            var appearance = _settings.Appearance;
            var background = ParseColor(appearance.BackgroundColor, Color.Black);
            var frame = ParseColor(appearance.FrameColor, Color.Gray);
            var pageFrame = ParseColor(appearance.PageFrameColor, Color.Gray);
            var text = ParseColor(appearance.TextColor, Color.Gray);
            var sent = ParseColor(appearance.SentMessageColor, Color.BrightYellow);
            var received = ParseColor(appearance.ReceivedMessageColor, Color.BrightCyan);
            var status = ParseColor(appearance.StatusTextColor, Color.White);
            var inputBackground = ParseColor(appearance.InputBackgroundColor, Color.Black);
            var inputText = ParseColor(appearance.InputTextColor, Color.White);
            var menuBackground = ParseColor(appearance.MenuBackgroundColor, Color.Blue);
            var menuText = ParseColor(appearance.MenuTextColor, Color.White);
            var buttonBackground = ParseColor(appearance.ButtonBackgroundColor, Color.Black);
            var buttonText = ParseColor(appearance.ButtonTextColor, Color.BrightCyan);
            var logoFrame = ParseColor(appearance.LogoFrameColor, Color.Gray);
            var logoText = ParseColor(appearance.LogoTextColor, Color.BrightCyan);
            var emojiText = ParseColor(appearance.EmojiTextColor, Color.BrightMagenta);
            var normal = Application.Driver.MakeAttribute(text, background);
            var focus = Application.Driver.MakeAttribute(background, frame);
            var statusScheme = new ColorScheme { Normal = Application.Driver.MakeAttribute(status, background), Focus = normal, HotNormal = normal, HotFocus = focus, Disabled = normal };
            var inputScheme = new ColorScheme { Normal = Application.Driver.MakeAttribute(inputText, inputBackground), Focus = Application.Driver.MakeAttribute(inputText, inputBackground), HotNormal = normal, HotFocus = normal, Disabled = normal };
            var buttonScheme = new ColorScheme { Normal = Application.Driver.MakeAttribute(buttonText, buttonBackground), Focus = Application.Driver.MakeAttribute(buttonBackground, buttonText), HotNormal = Application.Driver.MakeAttribute(buttonText, buttonBackground), HotFocus = Application.Driver.MakeAttribute(buttonBackground, buttonText), Disabled = normal };
            Colors.Base.Normal = normal; Colors.Base.Focus = focus; Colors.Base.HotNormal = normal; Colors.Base.HotFocus = focus; Colors.Base.Disabled = normal;
            Colors.TopLevel.Normal = normal; Colors.TopLevel.Focus = focus; Colors.TopLevel.HotNormal = normal; Colors.TopLevel.HotFocus = focus; Colors.TopLevel.Disabled = normal;
            Colors.Menu.Normal = Application.Driver.MakeAttribute(menuText, menuBackground); Colors.Menu.Focus = Application.Driver.MakeAttribute(menuBackground, menuText); Colors.Menu.HotNormal = Application.Driver.MakeAttribute(menuText, menuBackground); Colors.Menu.HotFocus = Application.Driver.MakeAttribute(menuBackground, menuText); Colors.Menu.Disabled = normal;
            foreach (var view in Frames) { view.Border.BorderBrush = frame; view.Border.Background = background; view.SetNeedsDisplay(); }
            foreach (var page in PageFrames) { page.Border.BorderBrush = pageFrame; page.Border.Background = background; page.SetNeedsDisplay(); }
            foreach (var button in MainButtons) button.ColorScheme = buttonScheme;
            if (_logoFrame != null) { _logoFrame.Border.BorderBrush = logoFrame; _logoFrame.Border.Background = background; _logoFrame.SetNeedsDisplay(); }
            if (_logoText != null) _logoText.ColorScheme = new ColorScheme { Normal = Application.Driver.MakeAttribute(logoText, background), Focus = normal, HotNormal = normal, HotFocus = focus, Disabled = normal };
            _messages.BackgroundColor = background; _messages.NormalTextColor = text; _messages.OutgoingColor = sent; _messages.IncomingColor = received; _messages.EmojiTextColor = emojiText;
            _input.ColorScheme = inputScheme;
            _status.ColorScheme = statusScheme;
            Application.Top.SetNeedsDisplay();
        }

        private static Color ParseColor(string value, Color fallback)
        {
            Color result;
            return Enum.TryParse(value, true, out result) ? result : fallback;
        }

        private static void ShowPositionSettings()
        {
            var dialog = new Dialog("Set GPS position", 60, 12);
            var latitude = new TextField(_mesh.Device.Latitude.HasValue ? _mesh.Device.Latitude.Value.ToString("F6", CultureInfo.InvariantCulture) : "") { X = 16, Y = 1, Width = 25 };
            var longitude = new TextField(_mesh.Device.Longitude.HasValue ? _mesh.Device.Longitude.Value.ToString("F6", CultureInfo.InvariantCulture) : "") { X = 16, Y = 2, Width = 25 };
            var altitude = new TextField("") { X = 16, Y = 3, Width = 12 };
            dialog.Add(new Label("Latitude:") { X = 1, Y = 1 }, latitude,
                new Label("Longitude:") { X = 1, Y = 2 }, longitude,
                new Label("Altitude (m):") { X = 1, Y = 3 }, altitude,
                new Label("Sets the device's fixed GPS position.") { X = 1, Y = 5 });
            var save = new Button("Set position", true);
            save.Clicked += delegate
            {
                double parsedLatitude, parsedLongitude;
                int parsedAltitude;
                if (!TryParseCoordinate(latitude.Text.ToString(), out parsedLatitude) || !TryParseCoordinate(longitude.Text.ToString(), out parsedLongitude) || parsedLatitude < -90 || parsedLatitude > 90 || parsedLongitude < -180 || parsedLongitude > 180)
                {
                    MessageBox.ErrorQuery("GPS position", "Please enter valid latitude and longitude values.", "OK");
                    return;
                }
                int? altitudeMeters = String.IsNullOrWhiteSpace(altitude.Text.ToString()) ? (int?)null : (Int32.TryParse(altitude.Text.ToString(), out parsedAltitude) ? (int?)parsedAltitude : null);
                if (!String.IsNullOrWhiteSpace(altitude.Text.ToString()) && !altitudeMeters.HasValue)
                {
                    MessageBox.ErrorQuery("GPS position", "Altitude must be a whole number.", "OK");
                    return;
                }
                Task.Run(async delegate
                {
                    try
                    {
                        await _mesh.SetFixedPositionAsync(parsedLatitude, parsedLongitude, altitudeMeters);
                        Ui(delegate { UpdateStatus(); Application.RequestStop(); });
                    }
                    catch (Exception ex) { Ui(delegate { MessageBox.ErrorQuery("GPS position", ex.Message, "OK"); }); }
                });
            };
            var cancel = new Button("Cancel");
            cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.AddButton(save);
            dialog.AddButton(cancel);
            Application.Run(dialog);
        }

        private static bool TryParseCoordinate(string text, out double result)
        {
            return Double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out result) || Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        private static void UseDeviceGps()
        {
            if (MessageBox.Query("Use device GPS", "Remove the fixed position and use the device's internal GPS receiver again?", "Use device GPS", "Cancel") != 0) return;
            Task.Run(async delegate
            {
                try
                {
                    await _mesh.ClearFixedPositionAsync();
                    Ui(delegate
                    {
                        UpdateStatus();
                        MessageBox.Query("Use device GPS", "The fixed position was removed. The device can use its internal GPS receiver again.", "OK");
                    });
                }
                catch (Exception ex) { Ui(delegate { MessageBox.ErrorQuery("Use device GPS", ex.Message, "OK"); }); }
            });
        }

        private static void UpdateStatus()
        {
            var info = _mesh.ConnectionInfo;
            var device = _mesh.Device;
            var position = device.Latitude.HasValue && device.Longitude.HasValue ? device.Latitude.Value.ToString("F5", CultureInfo.InvariantCulture) + ", " + device.Longitude.Value.ToString("F5", CultureInfo.InvariantCulture) : "-";
            _status.Text = " F10 Menu | " + DateTime.Now.ToString("HH:mm:ss") + " | Connection: " + _mesh.State + " | " + (info.Transport ?? "No connection") + " | Node: " + (device.NodeId ?? "-") + " | Battery: " + (device.BatteryLevel.HasValue ? device.BatteryLevel.Value + "%" : "-") + " | GPS: " + position;
            _status.SetNeedsDisplay();
        }
        private static string DisplayNodeName(uint number)
        {
            var n = _mesh.Nodes.FirstOrDefault(x => x.Number == number);
            if (n != null) return String.IsNullOrEmpty(n.LongName) ? n.Id : n.LongName;
            var stored = _store.GetNodes(StoredNodeSort.Name).FirstOrDefault(x => x.NodeNumber == number);
            return stored == null ? "!" + number.ToString("x8") : (String.IsNullOrEmpty(stored.LongName) ? stored.NodeId : stored.LongName);
        }
        private static void Save(Action action) { Task.Run(delegate { try { action(); } catch { } }); }
        private static void Ui(Action action) { Application.MainLoop.Invoke(action); }
        private static void RequestQuit() { if (MessageBox.Query("Exit", "Close ConsoleClient?", "Yes", "No") == 0) Application.RequestStop(); }
    }
}
