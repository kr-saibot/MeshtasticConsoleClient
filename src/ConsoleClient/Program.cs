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
        private readonly List<StoredMeshMessage> _messageItems = new List<StoredMeshMessage>();
        private int _scrollOffset;
        private int _selectedEmojiIndex = -1;
        private Guid? _selectedMessageId;
        private sealed class EmojiPosition
        {
            public int Index;
            public int Row;
            public int Column;
            public int Width;
            public string Token;
        }
        private sealed class SelectablePosition
        {
            public int? EmojiIndex;
            public Guid? MessageId;
            public int Row;
            public int Column;
        }
        public event Action ScrollPositionChanged;
        public event Action SelectionChanged;
        public Func<string, string> EmojiResolver { get; set; }
        public Action<string> EmojiActivated { get; set; }
        public Action<string> EmojiUnavailable { get; set; }
        public Action<StoredMeshMessage> MessageActivated { get; set; }
        public Color IncomingColor { get; set; } = Color.BrightCyan;
        public Color OutgoingColor { get; set; } = Color.BrightYellow;
        public Color EmojiTextColor { get; set; } = Color.BrightMagenta;
        public Color BackgroundColor { get; set; } = Color.Black;
        public Color NormalTextColor { get; set; } = Color.Gray;

        public void SetMessages(IEnumerable<StoredMeshMessage> messages, Func<StoredMeshMessage, string> formatter)
        {
            _lines.Clear();
            _messageItems.Clear();
            foreach (var message in messages)
            {
                _lines.Add(Tuple.Create(formatter(message), message.Direction == MessageDirection.Outgoing));
                _messageItems.Add(message);
            }
            _scrollOffset = 0;
            _selectedEmojiIndex = -1;
            _selectedMessageId = null;
            SetNeedsDisplay();
            NotifyScrollPositionChanged();
            NotifySelectionChanged();
        }

        public int MessageCount { get { return _lines.Count; } }
        public string SelectedEmojiToken { get { return GetSelectedEmojiToken(); } }
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
            var displayLines = BuildDisplayLines(width);
            List<StoredMeshMessage> displayOwners;
            List<bool> displayStarts;
            BuildDisplayMetadata(width, out displayOwners, out displayStarts);
            var emojiCount = CountEmojis(displayLines);
            if (HasFocus && _selectedEmojiIndex < 0 && !_selectedMessageId.HasValue && emojiCount > 0) _selectedEmojiIndex = emojiCount - 1;
            if (_selectedEmojiIndex >= emojiCount) _selectedEmojiIndex = emojiCount - 1;
            for (var row = 0; row < height; row++)
            {
                Move(0, row);
                Application.Driver.SetAttribute(Application.Driver.MakeAttribute(NormalTextColor, BackgroundColor));
                Application.Driver.AddStr(new string(' ', width));
            }
            var maximumOffset = Math.Max(0, displayLines.Count - height);
            if (_scrollOffset > maximumOffset) _scrollOffset = maximumOffset;
            var start = Math.Max(0, displayLines.Count - height - _scrollOffset);
            var emojiIndex = CountEmojis(displayLines.Take(start));
            for (var index = start; index < displayLines.Count && index - start < height; index++)
            {
                var row = index - start;
                var line = displayLines[index];
                Move(0, row);
                DrawMessageLine(line.Item1, line.Item2 ? OutgoingColor : IncomingColor, ref emojiIndex, HasFocus && displayStarts[index] && _selectedMessageId == displayOwners[index].Id);
            }
            if (displayLines.Count > height && height > 0)
            {
                var thumb = Math.Min(height - 1, (int)Math.Round((double)(height - 1) * (displayLines.Count - height - _scrollOffset) / Math.Max(1, displayLines.Count - height)));
                Move(width - 1, thumb);
                Application.Driver.SetAttribute(Application.Driver.MakeAttribute(Color.White, BackgroundColor));
                Application.Driver.AddStr("█");
            }
        }


        public override void PositionCursor()
        {
            Application.Driver.SetCursorVisibility(CursorVisibility.Invisible);
        }

        public override bool OnLeave(View view)
        {
            _selectedEmojiIndex = -1;
            _selectedMessageId = null;
            SetNeedsDisplay();
            NotifySelectionChanged();
            return base.OnLeave(view);
        }
        public override bool ProcessKey(KeyEvent keyEvent)
        {
            if (keyEvent.Key == Key.Enter)
            {
                if (_selectedMessageId.HasValue)
                {
                    var message = _messageItems.FirstOrDefault(item => item.Id == _selectedMessageId.Value);
                    if (message != null && MessageActivated != null) MessageActivated(message);
                    return true;
                }
                ActivateSelectedEmoji();
                return true;
            }
            if (keyEvent.Key == Key.CursorLeft) { MoveEmojiSelection(-1); return true; }
            if (keyEvent.Key == Key.CursorRight) { MoveEmojiSelection(1); return true; }
            if (keyEvent.Key == Key.CursorUp) { MoveEmojiSelectionByRow(-1); return true; }
            if (keyEvent.Key == Key.CursorDown) { MoveEmojiSelectionByRow(1); return true; }
            if (keyEvent.Key == Key.PageUp) { _scrollOffset += Math.Max(1, Bounds.Height - 1); SetNeedsDisplay(); NotifyScrollPositionChanged(); return true; }
            if (keyEvent.Key == Key.PageDown) { _scrollOffset = Math.Max(0, _scrollOffset - Math.Max(1, Bounds.Height - 1)); SetNeedsDisplay(); NotifyScrollPositionChanged(); return true; }
            return base.ProcessKey(keyEvent);
        }

        public override bool MouseEvent(MouseEvent mouseEvent)
        {
            var isClick = mouseEvent.Flags.HasFlag(MouseFlags.Button1Clicked) || mouseEvent.Flags.HasFlag(MouseFlags.Button1Pressed) || mouseEvent.Flags.HasFlag(MouseFlags.Button1DoubleClicked);
            if (!isClick) return base.MouseEvent(mouseEvent);
            SetFocus();
            var lines = BuildDisplayLines(Math.Max(2, Bounds.Width));
            List<StoredMeshMessage> owners;
            List<bool> starts;
            BuildDisplayMetadata(Math.Max(2, Bounds.Width), out owners, out starts);
            var height = Math.Max(1, Bounds.Height);
            var start = Math.Max(0, lines.Count - height - Math.Min(_scrollOffset, Math.Max(0, lines.Count - height)));
            var clickedRow = start + mouseEvent.Y;
            var clickedEmoji = GetEmojiPositions(lines).FirstOrDefault(item => item.Row == clickedRow && mouseEvent.X >= item.Column && mouseEvent.X < item.Column + item.Width);
            if (clickedEmoji != null)
            {
                _selectedEmojiIndex = clickedEmoji.Index;
                _selectedMessageId = null;
                if (mouseEvent.Flags.HasFlag(MouseFlags.Button1DoubleClicked)) ActivateSelectedEmoji();
            }
            else if (clickedRow >= 0 && clickedRow < owners.Count && starts[clickedRow] && mouseEvent.X >= 0 && mouseEvent.X < 7)
            {
                _selectedMessageId = owners[clickedRow].Id;
                _selectedEmojiIndex = -1;
                if (mouseEvent.Flags.HasFlag(MouseFlags.Button1DoubleClicked) && MessageActivated != null) MessageActivated(owners[clickedRow]);
            }
            else if (_selectedEmojiIndex < 0 && !_selectedMessageId.HasValue) SelectLastEmoji();
            SetNeedsDisplay();
            NotifySelectionChanged();
            mouseEvent.Handled = true;
            return true;
        }

        private void ActivateSelectedEmoji()
        {
            var token = GetSelectedEmojiToken();
            if (token == null) return;
            var emoji = EmojiResolver == null ? null : EmojiResolver(token);
            if (!String.IsNullOrEmpty(emoji) && EmojiActivated != null) EmojiActivated(emoji);
            else if (EmojiUnavailable != null) EmojiUnavailable(token);
        }
        private List<Tuple<string, bool>> BuildDisplayLines(int width)
        {
            var displayLines = new List<Tuple<string, bool>>();
            foreach (var line in _lines) AddWrappedLines(displayLines, line, width - 1);
            return displayLines;
        }

        private void BuildDisplayMetadata(int width, out List<StoredMeshMessage> owners, out List<bool> starts)
        {
            owners = new List<StoredMeshMessage>();
            starts = new List<bool>();
            for (var index = 0; index < _lines.Count; index++)
            {
                var wrapped = new List<Tuple<string, bool>>();
                AddWrappedLines(wrapped, _lines[index], width - 1);
                for (var row = 0; row < wrapped.Count; row++)
                {
                    owners.Add(_messageItems[index]);
                    starts.Add(row == 0);
                }
            }
        }
        private void SelectLastEmoji()
        {
            var count = CountEmojis(BuildDisplayLines(Math.Max(2, Bounds.Width)));
            _selectedEmojiIndex = count - 1;
            _selectedMessageId = null;
            SetNeedsDisplay();
        }

        private void MoveEmojiSelection(int direction)
        {
            var lines = BuildDisplayLines(Math.Max(2, Bounds.Width));
            var positions = GetSelectablePositions(lines);
            if (positions.Count == 0) return;
            var current = GetCurrentSelectable(positions);
            var currentIndex = current == null ? positions.Count - 1 : positions.IndexOf(current);
            var targetIndex = Math.Max(0, Math.Min(positions.Count - 1, currentIndex + direction));
            ApplySelection(positions[targetIndex], lines.Count);
        }

        private void MoveEmojiSelectionByRow(int direction)
        {
            var lines = BuildDisplayLines(Math.Max(2, Bounds.Width));
            var positions = GetSelectablePositions(lines);
            if (positions.Count == 0) return;
            var current = GetCurrentSelectable(positions) ?? positions[positions.Count - 1];
            var targetRows = positions.Where(item => direction < 0 ? item.Row < current.Row : item.Row > current.Row).Select(item => item.Row);
            if (!targetRows.Any()) return;
            var targetRow = direction < 0 ? targetRows.Max() : targetRows.Min();
            var target = positions.Where(item => item.Row == targetRow).OrderBy(item => Math.Abs(item.Column - current.Column)).First();
            ApplySelection(target, lines.Count);
        }

        private List<SelectablePosition> GetSelectablePositions(IList<Tuple<string, bool>> lines)
        {
            var result = GetEmojiPositions(lines).Select(item => new SelectablePosition { EmojiIndex = item.Index, Row = item.Row, Column = item.Column }).ToList();
            List<StoredMeshMessage> owners;
            List<bool> starts;
            BuildDisplayMetadata(Math.Max(2, Bounds.Width), out owners, out starts);
            for (var row = 0; row < starts.Count; row++)
            {
                if (starts[row]) result.Add(new SelectablePosition { MessageId = owners[row].Id, Row = row, Column = 0 });
            }
            return result.OrderBy(item => item.Row).ThenBy(item => item.Column).ToList();
        }

        private SelectablePosition GetCurrentSelectable(IEnumerable<SelectablePosition> positions)
        {
            if (_selectedMessageId.HasValue) return positions.FirstOrDefault(item => item.MessageId == _selectedMessageId);
            if (_selectedEmojiIndex >= 0) return positions.FirstOrDefault(item => item.EmojiIndex == _selectedEmojiIndex);
            return null;
        }

        private void ApplySelection(SelectablePosition target, int lineCount)
        {
            _selectedEmojiIndex = target.EmojiIndex.HasValue ? target.EmojiIndex.Value : -1;
            _selectedMessageId = target.MessageId;
            EnsureEmojiVisible(target.Row, lineCount);
            SetNeedsDisplay();
            NotifyScrollPositionChanged();
            NotifySelectionChanged();
        }

        private void EnsureEmojiVisible(int row, int lineCount)
        {
            var height = Math.Max(1, Bounds.Height);
            var maximumOffset = Math.Max(0, lineCount - height);
            var start = Math.Max(0, lineCount - height - Math.Min(_scrollOffset, maximumOffset));
            if (row < start) _scrollOffset = Math.Min(maximumOffset, lineCount - height - row);
            else if (row >= start + height) _scrollOffset = Math.Max(0, lineCount - row - 1);
        }
        private static List<EmojiPosition> GetEmojiPositions(IList<Tuple<string, bool>> lines)
        {
            var result = new List<EmojiPosition>();
            var index = 0;
            for (var row = 0; row < lines.Count; row++)
            {
                var text = lines[row].Item1;
                var position = 0;
                while (position < text.Length)
                {
                    var start = text.IndexOf("[:", position, StringComparison.Ordinal);
                    if (start < 0) break;
                    var end = text.IndexOf(":]", start + 2, StringComparison.Ordinal);
                    if (end < 0) break;
                    var token = text.Substring(start, end - start + 2);
                    result.Add(new EmojiPosition { Index = index++, Row = row, Column = GetTextDisplayWidth(text.Substring(0, start)), Width = token.Length, Token = token });
                    position = end + 2;
                }
            }
            return result;
        }

        private static int GetTextDisplayWidth(string text)
        {
            var width = 0;
            var elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext()) width += GetDisplayWidth((string)elements.Current);
            return width;
        }
        private string GetSelectedEmojiToken()
        {
            if (_selectedEmojiIndex < 0) return null;
            var current = 0;
            foreach (var line in BuildDisplayLines(Math.Max(2, Bounds.Width)))
            {
                var position = 0;
                while (position < line.Item1.Length)
                {
                    var start = line.Item1.IndexOf("[:", position, StringComparison.Ordinal);
                    if (start < 0) break;
                    var end = line.Item1.IndexOf(":]", start + 2, StringComparison.Ordinal);
                    if (end < 0) break;
                    if (current++ == _selectedEmojiIndex) return line.Item1.Substring(start, end - start + 2);
                    position = end + 2;
                }
            }
            return null;
        }

        private static int CountEmojis(IEnumerable<Tuple<string, bool>> lines)
        {
            var count = 0;
            foreach (var line in lines)
            {
                var position = 0;
                while (position < line.Item1.Length)
                {
                    var start = line.Item1.IndexOf("[:", position, StringComparison.Ordinal);
                    if (start < 0) break;
                    var end = line.Item1.IndexOf(":]", start + 2, StringComparison.Ordinal);
                    if (end < 0) break;
                    count++;
                    position = end + 2;
                }
            }
            return count;
        }

        private void NotifyScrollPositionChanged()
        {
            var handler = ScrollPositionChanged;
            if (handler != null) handler();
        }

        private void NotifySelectionChanged()
        {
            var handler = SelectionChanged;
            if (handler != null) handler();
        }

        private void DrawMessageLine(string text, Color messageColor, ref int emojiIndex, bool selectedTimestamp)
        {
            var position = 0;
            if (selectedTimestamp && text.Length >= 7 && text[0] == '[' && text[6] == ']')
            {
                DrawText(text.Substring(0, 7), messageColor, true);
                position = 7;
            }
            while (position < text.Length)
            {
                var emojiStart = text.IndexOf("[:", position, StringComparison.Ordinal);
                if (emojiStart < 0) { DrawText(text.Substring(position), messageColor, false); break; }
                if (emojiStart > position) DrawText(text.Substring(position, emojiStart - position), messageColor, false);
                var emojiEnd = text.IndexOf(":]", emojiStart + 2, StringComparison.Ordinal);
                if (emojiEnd < 0) { DrawText(text.Substring(emojiStart), messageColor, false); break; }
                DrawText(text.Substring(emojiStart, emojiEnd - emojiStart + 2), EmojiTextColor, HasFocus && emojiIndex == _selectedEmojiIndex);
                emojiIndex++;
                position = emojiEnd + 2;
            }
        }

        private void DrawText(string text, Color foreground, bool selected)
        {
            if (String.IsNullOrEmpty(text)) return;
            Application.Driver.SetAttribute(selected
                ? Application.Driver.MakeAttribute(BackgroundColor, foreground)
                : Application.Driver.MakeAttribute(foreground, BackgroundColor));
            Application.Driver.AddStr(text);
        }

        private static void AddWrappedLines(ICollection<Tuple<string, bool>> target, Tuple<string, bool> line, int width)
        {
            foreach (var part in line.Item1.Replace("\r", "").Split('\n'))
            {
                if (part.Length == 0) { target.Add(Tuple.Create("", line.Item2)); continue; }
                var current = new StringBuilder();
                var currentWidth = 0;
                var position = 0;
                while (position < part.Length)
                {
                    string element;
                    if (part.IndexOf("[:", position, StringComparison.Ordinal) == position)
                    {
                        var tokenEnd = part.IndexOf(":]", position + 2, StringComparison.Ordinal);
                        element = tokenEnd < 0 ? StringInfo.GetNextTextElement(part, position) : part.Substring(position, tokenEnd - position + 2);
                    }
                    else element = StringInfo.GetNextTextElement(part, position);
                    var elementWidth = element.StartsWith("[:", StringComparison.Ordinal) && element.EndsWith(":]", StringComparison.Ordinal) ? element.Length : GetDisplayWidth(element);
                    if (currentWidth > 0 && currentWidth + elementWidth > width)
                    {
                        target.Add(Tuple.Create(current.ToString(), line.Item2));
                        current.Clear();
                        currentWidth = 0;
                    }
                    current.Append(element);
                    currentWidth += elementWidth;
                    position += element.Length;
                }
                if (current.Length > 0) target.Add(Tuple.Create(current.ToString(), line.Item2));
            }
        }

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

    [XmlRoot("emojiBlockText")]
    public sealed class EmojiBlockFile
    {
        [XmlElement("glyph")]
        public List<EmojiBlockEntry> Items { get; set; } = new List<EmojiBlockEntry>();
    }

    public sealed class EmojiBlockEntry
    {
        [XmlAttribute("character")]
        public string Character { get; set; }
        [XmlAttribute("name")]
        public string Name { get; set; }
        [XmlElement("text")]
        public string Text { get; set; }
    }
    internal static class Program
    {
        private static MeshtasticClient _mesh;
        private static MeshtasticMessageStore _store;
        private static MeshtasticApplicationSettings _settings;
        private static MeshtasticTelegramGatewayManager _telegramGateways;
        private static readonly HttpClient HttpBotHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly HttpClient AlertHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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
        private static readonly Dictionary<string, string> EmojiByReplacement = new Dictionary<string, string>();
        private static readonly Dictionary<string, EmojiBlockEntry> EmojiBlocksFull = new Dictionary<string, EmojiBlockEntry>();
        private static readonly Dictionary<string, EmojiBlockEntry> EmojiBlocksHalf = new Dictionary<string, EmojiBlockEntry>();
        private static string _lastChatBotRequest = "No local bot has run yet.";
        private static string _lastChatBotOutput = "No local bot has run yet.";
        private static string _lastHttpBotRequest = "No HTTP bot has run yet.";
        private static string _lastHttpBotOutput = "No HTTP bot has run yet.";
        private static string _lastAlertHttpRequest = "No alert HTTP request has run yet.";
        private static string _lastAlertHttpOutput = "No alert HTTP request has run yet.";
        private static string _lastAlertProcessRequest = "No alert shell command has run yet.";
        private static string _lastAlertProcessOutput = "No alert shell command has run yet.";
        private static DateTime _lastAlertBeepUtc = DateTime.MinValue;
        private static DateTime _nextRepeatedAlertCheckUtc = DateTime.MinValue;
        private static int _lastKnownUnreadCount = -1;
        private static bool _disconnectedStatusVisible = true;
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
        private static int _connectionAttemptId;
        private static Window _chatPage;
        private static Window _nodesPage;
        private static Window _mapPage;
        private static Window _telemetryPage;
        private static NodeMapView _nodeMap;
        private static Label _mapInfo;
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
        private static Label _messageCharacterCounter;
        private const string MessageInputTitle = "New message [F5] emoji [F6]";
        private static Label _logoText;
        private static readonly List<string> LogoFiles = new List<string>();
        private static int _logoIndex;
        private static DateTime _nextLogoChangeUtc;
        private static readonly List<ChatItem> Chats = new List<ChatItem>();
        private static ChatItem _selected;
        private static readonly List<FrameView> Frames = new List<FrameView>();
        private static readonly List<Window> PageFrames = new List<Window>();
        private static readonly List<Button> MainButtons = new List<Button>();
        private static readonly List<MapOverlayFile> MapOverlayFiles = new List<MapOverlayFile>();
        private static MenuBarItem _mapMenu;
        private static ListView _mapOverlayOrderList;
        private static string _lastMapPointFile = "";
        private static string _lastMapPointColor = "Green";
        private static string _lastMapPointShortName = "";
        private static string _lastMapPointDescription = "";
        private static bool _mapFollowGps;

        private static void Main(string[] args)
        {
            try { Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = Encoding.UTF8; } catch { }
            NormalizeUnixTerminalType();
            LoadEmojiReplacements();
            LoadEmojiBlocks("emoji-blocks-full.xml", EmojiBlocksFull);
            LoadEmojiBlocks("emoji-blocks-12line-full.xml", EmojiBlocksHalf);
            _settings = MeshtasticSettingsStore.Load("meshtastic-settings.xml");
            MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings);
            _store = MeshtasticMessageStore.CreateSqlite("meshtastic-messages.db");
            _store.Initialize();
            _lastKnownUnreadCount = _store.CountNewMessages();
            _mesh = new MeshtasticClient();
            ApplyConnectionReconnectSettings();
            SubscribeMeshEvents();

            Application.Init();
            BuildUi();
            // Start only after the first UI cycle so the waiting window can be drawn first.
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(500), delegate(MainLoop loop) { StartConnect(); return false; });
            Application.Run();
            try { SaveMapViewState(); SaveNodeListState(); MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings); } catch { }
            if (_telegramGateways != null) _telegramGateways.StopAsync().GetAwaiter().GetResult();
            _mesh.DisconnectAsync().GetAwaiter().GetResult();
            _mesh.Dispose();
            _store.Dispose();
            Application.Shutdown();
        }

        private static void NormalizeUnixTerminalType()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) return;
            var term = Environment.GetEnvironmentVariable("TERM");
            if (String.Equals(term, "xterm", StringComparison.OrdinalIgnoreCase) || String.Equals(term, "xterm-color", StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("TERM", "xterm-256color");

        }

        private static void BuildUi()
        {
            var top = Application.Top;
            LoadMapOverlayFiles();
            RestoreNodeListState();
            _mapMenu = new MenuBarItem("_Map", BuildMapMenuItems());
            var menu = new MenuBar(new[]
            {
                new MenuBarItem("_Chats", new[] { new MenuItem("_Show chats", "", ShowChatPage), new MenuItem("_Refresh", "", RefreshChats), new MenuItem("_Send", "", SendCurrentMessage), new MenuItem("_Delete active chat", "", DeleteActiveChat) }),
                new MenuBarItem("_Nodes", new[] { new MenuItem("_Show nodes", "", ShowNodesPage), new MenuItem("_Copy node list", "", CopyNodeList), new MenuItem("_Create node", "", CreateNode), new MenuItem("_Refresh from device", "", RefreshNodesFromDevice), new MenuItem("_Delete all nodes", "", DeleteAllNodes) }),
                _mapMenu,
                new MenuBarItem("_Telemetry", new[] { new MenuItem("_Show telemetry", "", ShowAllTelemetry), new MenuItem("_Delete current node telemetry", "", DeleteCurrentNodeTelemetry), new MenuItem("_Delete all telemetry", "", DeleteAllTelemetry) }),
                new MenuBarItem("_Connection", new[] { new MenuItem("_Connect", "", StartConnect), new MenuItem("_Disconnect", "", Disconnect), new MenuItem("Connection _status", "", ShowConnectionStatus) }),
                new MenuBarItem("_Settings", new[] { new MenuItem("_Connection settings", "", ShowSettings), new MenuItem("_Telemetry storage", "", ShowTelemetryStorageSettings), new MenuItem("_GPS position", "", ShowPositionSettings), new MenuItem("_Use device GPS", "", UseDeviceGps), new MenuItem("_Telegram Gateways", "", ShowTelegramGateways), new MenuItem("_Alerts", "", ShowAlertSettings), new MenuItem("_Appearance", "", ShowAppearanceSettings), new MenuItem("_Logo", "", ShowLogoSettings), new MenuItem("_Chat bots", "", ShowChatBots), new MenuItem("_HTTP bots", "", ShowHttpBots) }),
                new MenuBarItem("_Debug", new[] { new MenuItem("_Telegram gateway status", "", ShowTelegramGatewayStatus), new MenuItem("_Chat bot", "", ShowChatBotDebug), new MenuItem("_HTTP bot", "", ShowHttpBotDebug), new MenuItem("_Alert HTTP", "", ShowAlertHttpDebug), new MenuItem("Alert shell _command", "", ShowAlertProcessDebug) }),
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
                else if (e.KeyEvent.Key == Key.F9) { ShowMapPage(); e.Handled = true; }
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
            _channelList.KeyPress += e => { if (e.KeyEvent.Key != Key.Enter) return; e.Handled = true; var index = _channelList.SelectedItem; if (index < 0 || index >= ChannelChats.Count) return; _selected = ChannelChats[index]; ActivateSelectedChat(); };
            _channelsFrame.Add(_channelList);
            _directChatsFrame = new FrameView("Direct chats [F2]") { X = 0, Y = Pos.Bottom(_channelsFrame), Width = sidebarOuterWidth, Height = Dim.Fill() };
            Frames.Add(_directChatsFrame);
            _directList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            _directList.SelectedItemChanged += delegate(ListViewItemEventArgs e) { if (!_refreshingChatLists && e.Item >= 0 && e.Item < DirectChats.Count) { _selected = DirectChats[e.Item]; ShowSelectedChat(); } };
            _directList.KeyPress += e => { if (e.KeyEvent.Key != Key.Enter) return; e.Handled = true; var index = _directList.SelectedItem; if (index < 0 || index >= DirectChats.Count) return; _selected = DirectChats[index]; ActivateSelectedChat(); };
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
            _messages.EmojiResolver = ResolveEmojiReplacement;
            _messages.EmojiActivated = ShowEmojiPreview;
            _messages.EmojiUnavailable = ShowEmojiUnavailable;
            _messages.MessageActivated = ShowMessageDetails;
            _messages.ScrollPositionChanged += UpdateMessageScrollPosition;
            _messages.SelectionChanged += ShowCurrentLogo;
            _messagesFrame.Add(_messages);
            _inputFrame = new FrameView(MessageInputTitle) { X = sidebarOuterWidth + 1, Y = Pos.AnchorEnd(4), Width = Dim.Fill(10), Height = 4 };
            Frames.Add(_inputFrame);
            _input = new MessageInputView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), WordWrap = true };
            _input.TextChanged += delegate { UpdateMessageCharacterCounter(); };
            _input.KeyPress += e => { if (e.KeyEvent.Key == Key.F6) { ShowEmojiPicker(); e.Handled = true; } };
            _inputFrame.Add(_input);
            _messageCharacterCounter = new Label("") { X = Pos.Right(_inputFrame) - 1, Y = Pos.Top(_inputFrame), Width = 0 };
            UpdateMessageCharacterCounter();
            var send = new Button("Send") { X = Pos.AnchorEnd(9), Y = Pos.AnchorEnd(2) };
            MainButtons.Add(send);
            send.Clicked += SendCurrentMessage;
            _chatPage.Add(_logoFrame, _channelsFrame, _directChatsFrame, _nodeInfoFrame, _messagesFrame, _inputFrame, _messageCharacterCounter, send);
            top.Add(_chatPage);

            _nodesPage = new Window("Nodes") { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1), Visible = false };
            PageFrames.Add(_nodesPage);
            var nodeListFrame = new FrameView("Known nodes [F3]") { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(3) };
            Frames.Add(nodeListFrame);
            _nodeList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            _nodeList.SelectedItemChanged += delegate { UpdateNodeScrollInfo(); };
            _nodeList.KeyPress += e => { if (e.KeyEvent.Key == Key.Enter) { ShowSelectedNodeDetails(); e.Handled = true; } };
            _nodeList.OpenSelectedItem += delegate { ShowSelectedNodeDetails(); };
            var favoriteFilter = new Button(_favoritesOnly ? "Favorites only: on" : "Favorites only: off") { X = 0, Y = Pos.AnchorEnd(2) };
            MainButtons.Add(favoriteFilter);
            favoriteFilter.Clicked += delegate { _favoritesOnly = !_favoritesOnly; favoriteFilter.Text = _favoritesOnly ? "Favorites only: on" : "Favorites only: off"; SaveNodeListState(); RefreshNodePage(); };
            _sortButton = new Button("Sort: " + NodeSortLabel()) { X = Pos.Right(favoriteFilter) + 2, Y = Pos.AnchorEnd(2) };
            MainButtons.Add(_sortButton);
            _sortButton.Clicked += CycleNodeSort;
            _nodeSortDirectionButton = new Button(_nodeSortAscending ? "Order: asc" : "Order: desc") { X = Pos.Right(_sortButton) + 2, Y = Pos.AnchorEnd(2) };
            MainButtons.Add(_nodeSortDirectionButton);
            _nodeSortDirectionButton.Clicked += ToggleNodeSortDirection;
            var searchLabel = new Label("Search:") { X = Pos.Right(_nodeSortDirectionButton) + 2, Y = Pos.AnchorEnd(2) };
            _nodeSearch = new TextField(_settings.Nodes == null ? "" : _settings.Nodes.SearchText ?? "") { X = Pos.Right(searchLabel) + 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill() };
            _nodeSearch.TextChanged += delegate { SaveNodeListState(); RefreshNodePage(); };
            _nodeScrollInfo = new Label("") { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
            nodeListFrame.Add(_nodeList);
            _nodesPage.Add(nodeListFrame, favoriteFilter, _sortButton, _nodeSortDirectionButton, searchLabel, _nodeSearch, _nodeScrollInfo);
            top.Add(_nodesPage);

            _mapPage = new Window("Node map [F9]  +/- zoom | arrows select | WASD pan | WASD uppercase 2 grids | C center | N new point | Enter activate") { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1), Visible = false };
            PageFrames.Add(_mapPage);
            _nodeMap = new NodeMapView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(3) };
            _nodeMap.NodeActivated = ShowNodeDetails;
            _nodeMap.SelectionChanged = UpdateMapInfo;
            _nodeMap.OverlaySelectionChanged = UpdateMapOverlayInfo;
            _nodeMap.OverlayActivated = ShowMapOverlayDetails;
            _nodeMap.NewOverlayPointRequested = CreateMapOverlayPoint;
            _nodeMap.ViewChanged = SaveMapViewState;
            var mapInfoFrame = new FrameView("Selected Item") { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 3 };
            Frames.Add(mapInfoFrame);
            _mapInfo = new Label("No node selected") { X = 0, Y = 0, Width = Dim.Fill() };
            mapInfoFrame.Add(_mapInfo);
            _mapPage.Add(_nodeMap, mapInfoFrame);
            top.Add(_mapPage);

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
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(500), delegate(MainLoop loop) { UpdateStatus(); UpdateMapGpsPosition(); CheckRepeatingAlertBeep(); return true; });
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
            _mesh.DeviceInfoUpdated += delegate { Ui(delegate { UpdateStatus(); UpdateChatPageTitle(); UpdateMapGpsPosition(); }); };
            _mesh.NodeDiscovered += delegate(object sender, NodeEventArgs e) { Save(delegate { _store.AddOrUpdateNode(e.Node); Ui(QueueNodePageRefresh); }); };
            _mesh.NodeUpdated += delegate(object sender, NodeEventArgs e) { Save(delegate { _store.AddOrUpdateNode(e.Node); Ui(QueueNodePageRefresh); }); };
            _mesh.MessageReceived += delegate(object sender, MeshMessageEventArgs e)
            {
                var node = _mesh.Nodes.FirstOrDefault(n => n.Number == e.Message.From);
                var ownEcho = _mesh.Device.MyNode != null && e.Message.From == _mesh.Device.MyNode.MyNodeNum;
                Save(delegate
                {
                    _store.AddIncoming(e.Message, node == null ? (double?)null : node.Latitude, node == null ? (double?)null : node.Longitude, !ownEcho);
                    if (!ownEcho) NotifyAlertStateChanged(e.Message);
                    RefreshChatsUi();
                });
                if (!ownEcho) Task.Run(async delegate { await RunMatchingChatBotsAsync(e.Message); await RunMatchingHttpBotsAsync(e.Message); });
            };
            _mesh.MessageDeliveryChanged += delegate(object sender, MessageDeliveryEventArgs e)
            {
                Save(delegate { _store.UpdateDeliveryStatus(e.PacketId, e.State, e.Error == Meshtastic.Protobufs.Routing.Types.Error.None ? null : e.Error.ToString()); Ui(ShowSelectedChat); });
            };
            _mesh.TelemetryReceived += delegate(object sender, MeshTelemetryEventArgs e)
            {
                if (!_settings.StoreTelemetryData) return;
                Save(delegate { _store.AddTelemetry(e.Telemetry); Ui(delegate { if (_telemetryPage != null && _telemetryPage.Visible) RefreshTelemetryPage(); if (_chatPage != null && _chatPage.Visible && _selected != null && _selected.Kind == ChatKind.Direct && _selected.Id == e.Telemetry.From) ShowSelectedChat(); }); });
            };
        }

        private static void UpdateChatPageTitle()
        {
            if (_chatPage == null) return;
            var localNode = _mesh == null ? null : _mesh.Device.LocalNode;
            var nodeName = localNode == null ? null : (localNode.LongName ?? localNode.ShortName);
            _chatPage.Title = String.IsNullOrWhiteSpace(nodeName) ? "Chat" : "Chat (" + nodeName + ")";
        }

        private static void ApplyConnectionReconnectSettings()
        {
            if (_mesh == null || _settings == null || _settings.Connection == null) return;
            var seconds = Math.Max(1, Math.Min(86400, _settings.Connection.ReconnectIntervalSeconds));
            _mesh.ReconnectDelay = TimeSpan.FromSeconds(seconds);
            _mesh.MaximumConnectionAttempts = _settings.Connection.AutomaticReconnect ? 0 : 1;
        }

        private static void StartConnect()
        {
            if (_mesh.State != ConnectionState.Disconnected) { MessageBox.Query("Connection", "A connection is already active.", "OK"); return; }
            ApplyConnectionReconnectSettings();
            var attemptId = ++_connectionAttemptId;
            ShowPleaseWait("Connecting to Meshtastic...");
            // Give Terminal.Gui a complete drawing cycle before the transport starts its
            // potentially expensive initial node/configuration synchronization.
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(150), delegate(MainLoop loop)
            {
                Task.Run(async delegate
                {
                    try
                    {
                        if (attemptId != _connectionAttemptId) return;
                        var c = _settings.Connection;
                        if (c.Transport == MeshtasticTransportType.Serial) await _mesh.ConnectSerialAsync(c.SerialPort, c.SerialBaudRate);
                        else await _mesh.ConnectTcpAsync(c.TcpHost, c.TcpPort);
                        if (attemptId != _connectionAttemptId) return;
                        Ui(delegate { if (attemptId == _connectionAttemptId) ShowPleaseWait("Loading nodes and device data..."); });
                        await _mesh.RequestFullStateAsync();
                        if (attemptId != _connectionAttemptId) return;
                        await _mesh.ActivatePacketStreamingAsync();
                        if (attemptId != _connectionAttemptId) return;
                        await RestartTelegramGatewaysAsync();
                        Ui(delegate { if (attemptId == _connectionAttemptId) { HidePleaseWait(); RefreshChats(); } });
                    }
                    catch (Exception ex)
                    {
                        try { await _mesh.DisconnectAsync(); } catch { }
                        if (attemptId != _connectionAttemptId) return;
                        Ui(delegate { if (attemptId == _connectionAttemptId) { HidePleaseWait(); MessageBox.ErrorQuery("Connection", ex.Message, "OK"); } });
                    }
                });
                return false;
            });
        }

        private static void ShowPleaseWait(string text)
        {
            if (_pleaseWait == null)
            {
                _pleaseWait = new Dialog("Please wait", 58, 7) { Modal = true };
                _pleaseWaitText = new Label("") { X = 2, Y = 1, Width = Dim.Fill(4), TextAlignment = TextAlignment.Centered };
                _pleaseWaitProgress = new ProgressBar { X = 2, Y = 3, Width = Dim.Fill(4), ProgressBarStyle = ProgressBarStyle.MarqueeBlocks };
                var cancel = new Button("Cancel") { X = Pos.Center(), Y = 5 };
                cancel.Clicked += CancelConnectionAttempt;
                _pleaseWait.Add(_pleaseWaitText, _pleaseWaitProgress, cancel);
                // Do not call Application.Run(dialog) here. A nested Terminal.Gui run loop
                // leaves a stale Unix event descriptor under Mono and can busy-spin at 100% CPU.
                Application.Top.Add(_pleaseWait);
                _pleaseWait.SetFocus();
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
            Application.Top.Remove(waitDialog);
            _chatPage.SetFocus();
            Application.Top.SetNeedsDisplay();
        }

        private static void CancelConnectionAttempt()
        {
            _connectionAttemptId++;
            HidePleaseWait();
            Task.Run(async delegate
            {
                try
                {
                    if (_telegramGateways != null) await _telegramGateways.StopAsync();
                    await _mesh.DisconnectAsync();
                }
                catch { }
                Ui(UpdateStatus);
            });
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
            if (_store.CountNewMessages() == 0) NotifyAlertStateChanged(null);
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
            NotifyAlertStateChanged(null);
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
            EmojiByReplacement.Clear();
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
                                if (!EmojiByReplacement.ContainsKey(item.Text)) EmojiByReplacement[item.Text] = item.Value;
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
                    if (!EmojiByReplacement.ContainsKey(item.Value)) EmojiByReplacement[item.Value] = item.Key;
                    EmojiPickerEntries.Add(new EmojiReplacementEntry { Value = item.Key, Text = item.Value, Category = "Other" });
                }
            }
        }

        private static string ReplaceGraphicalSymbols(string text)
        {
            var result = new StringBuilder();
            var elements = StringInfo.GetTextElementEnumerator(text ?? "");
            while (elements.MoveNext())
            {
                var element = (string)elements.Current;
                var category = CharUnicodeInfo.GetUnicodeCategory(element, 0);
                var graphical = ContainsEmoji(element) || category == UnicodeCategory.OtherSymbol || category == UnicodeCategory.MathSymbol || category == UnicodeCategory.ModifierSymbol || Char.IsSurrogate(element[0]);
                result.Append(graphical ? "?" : element);
            }
            return result.ToString();
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
            return "@ " + chat.Name + " | " + (node.NodeId ?? "!" + node.NodeNumber.ToString("x8")) + " | " + (String.IsNullOrWhiteSpace(node.ShortName) ? "-" : node.ShortName) + " | " + distanceText + " " + directionText;
        }

        private static void ShowEmojiPicker()
        {
            if (_input == null) return;
            var terminalWidth = Math.Max(40, Application.Driver.Cols);
            var terminalHeight = Math.Max(16, Application.Driver.Rows);
            var useFullPreview = terminalWidth >= 106 && terminalHeight >= 34 && EmojiBlocksFull.Count > 0;
            var previewColumns = useFullPreview ? 48 : 24;
            var previewRows = useFullPreview ? 24 : 12;
            var previewBlocks = useFullPreview ? EmojiBlocksFull : EmojiBlocksHalf;
            var dialogWidth = Math.Min(terminalWidth - 2, useFullPreview ? 112 : 86);
            var dialogHeight = Math.Min(terminalHeight - 2, useFullPreview ? 34 : 21);
            var dialog = new Dialog("Emoji picker", dialogWidth, dialogHeight);
            var categories = new[] { "All" }.Concat(EmojiPickerEntries.Select(entry => String.IsNullOrWhiteSpace(entry.Category) ? "Other" : entry.Category).Distinct().OrderBy(category => category)).ToList();
            var selectedCategory = "All";
            var visibleEntries = new List<EmojiReplacementEntry>();
            var showCategories = dialogWidth >= 80;
            var categoryWidth = showCategories ? 20 : 0;
            var pickerX = showCategories ? categoryWidth + 3 : 1;
            var previewWidth = previewColumns + 2;
            var previewX = dialogWidth - previewWidth - 4;
            var categoryLabel = new Label("Category") { X = 1, Y = 1, Visible = showCategories };
            var categoryList = new ListView(categories) { X = 1, Y = 2, Width = categoryWidth, Height = Dim.Fill(4), Visible = showCategories };
            var searchLabel = new Label("Search") { X = pickerX, Y = 1 };
            var search = new TextField("") { X = pickerX + 8, Y = 1, Width = previewX - pickerX - 9 };
            var list = new ListView { X = pickerX, Y = 3, Width = previewX - pickerX - 1, Height = Dim.Fill(5) };
            var previewFrame = new FrameView("Preview " + previewColumns + "x" + previewRows) { X = previewX, Y = 2, Width = previewWidth + 2, Height = previewRows + 2 };
            var preview = new Label("") { X = 0, Y = 0, Width = previewColumns, Height = previewRows };
            var description = new Label("") { X = previewX, Y = Pos.Bottom(previewFrame), Width = previewWidth + 2, Height = 2 };
            previewFrame.Add(preview);
            Action updatePreview = delegate
            {
                if (list.SelectedItem < 0 || list.SelectedItem >= visibleEntries.Count)
                {
                    preview.Text = "";
                    description.Text = "";
                    return;
                }
                EmojiBlockEntry block;
                if (previewBlocks.TryGetValue(visibleEntries[list.SelectedItem].Value, out block))
                {
                    preview.Text = block.Text;
                    description.Text = block.Name ?? "";
                }
                else
                {
                    preview.Text = "No ASCII preview";
                    description.Text = "";
                }
            };
            Action refresh = delegate
            {
                var query = search.Text == null ? "" : search.Text.ToString().Trim();
                visibleEntries.Clear();
                visibleEntries.AddRange(EmojiPickerEntries.Where(entry =>
                    (selectedCategory == "All" || String.Equals(String.IsNullOrWhiteSpace(entry.Category) ? "Other" : entry.Category, selectedCategory, StringComparison.Ordinal)) &&
                    (query.Length == 0 || (entry.Text ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (entry.Category ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (entry.SubCategory ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderBy(entry => entry.Text));
                list.SetSource(visibleEntries.Select(entry => entry.Text).ToList());
                if (visibleEntries.Count > 0) list.SelectedItem = 0;
                updatePreview();
            };
            categoryList.SelectedItemChanged += delegate(ListViewItemEventArgs e)
            {
                if (e.Item < 0 || e.Item >= categories.Count) return;
                selectedCategory = categories[e.Item];
                refresh();
            };
            search.TextChanged += delegate { refresh(); };
            list.SelectedItemChanged += delegate { updatePreview(); };
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
            dialog.Add(categoryLabel, categoryList, searchLabel, search, list, previewFrame, description);
            dialog.AddButton(insertButton);
            dialog.AddButton(cancel);
            Application.Run(dialog);
            _input.SetFocus();
        }

        private static string ResolveEmojiReplacement(string replacement)
        {
            string emoji;
            return EmojiByReplacement.TryGetValue(replacement, out emoji) ? emoji : null;
        }

        private static void ShowMessageDetails(StoredMeshMessage message)
        {
            if (message == null) return;
            var details = new StringBuilder();
            details.AppendLine("Message:");
            details.AppendLine(message.Text ?? "");
            details.AppendLine();
            details.AppendLine("Metadata:");
            details.AppendLine("ID: " + message.Id);
            details.AppendLine("Occurred (local): " + message.OccurredUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            details.AppendLine("Occurred (UTC): " + message.OccurredUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
            details.AppendLine("Created (UTC): " + message.CreatedUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
            details.AppendLine("Direction: " + message.Direction);
            details.AppendLine("Kind: " + message.Kind);
            details.AppendLine("Packet ID: " + (message.PacketId.HasValue ? message.PacketId.Value.ToString(CultureInfo.InvariantCulture) : "-"));
            details.AppendLine("From node: " + (message.FromNode.HasValue ? "!" + message.FromNode.Value.ToString("x8") : "-"));
            details.AppendLine("To node: " + (message.ToNode.HasValue ? "!" + message.ToNode.Value.ToString("x8") : "-"));
            details.AppendLine("Channel index: " + (message.ChannelIndex.HasValue ? message.ChannelIndex.Value.ToString(CultureInfo.InvariantCulture) : "-"));
            details.AppendLine("Channel name: " + (String.IsNullOrEmpty(message.ChannelName) ? "-" : message.ChannelName));
            details.AppendLine("Delivery status: " + (String.IsNullOrEmpty(message.DeliveryStatus) ? "-" : message.DeliveryStatus));
            details.AppendLine("Delivery error: " + (String.IsNullOrEmpty(message.DeliveryError) ? "-" : message.DeliveryError));
            details.AppendLine("Unread: " + message.IsNew);
            details.AppendLine("Latitude: " + (message.Latitude.HasValue ? message.Latitude.Value.ToString("F6", CultureInfo.InvariantCulture) : "-"));
            details.AppendLine("Longitude: " + (message.Longitude.HasValue ? message.Longitude.Value.ToString("F6", CultureInfo.InvariantCulture) : "-"));
            details.AppendLine("Raw metadata: " + (String.IsNullOrEmpty(message.RawMetadata) ? "-" : message.RawMetadata));

            var width = Math.Min(Math.Max(54, Application.Driver.Cols - 4), 100);
            var height = Math.Min(Math.Max(18, Application.Driver.Rows - 4), 32);
            var dialog = new Dialog("Message details", width, height);
            var view = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2), ReadOnly = true, WordWrap = true, CanFocus = true, Text = details.ToString() };
            var deleted = false;
            var copy = new Button("Copy message");
            copy.Clicked += delegate
            {
                CopyTextWithFallback(message.Text ?? "", "Message copied.");
            };
            var copyChat = new Button("Copy chat");
            copyChat.Clicked += delegate { CopyActiveChatToClipboard(); };
            var delete = new Button("Delete");
            delete.Clicked += delegate
            {
                if (MessageBox.Query("Delete message", "Delete this single message?", "Delete", "Cancel") != 0) return;
                _store.Delete(message.Id);
                deleted = true;
                Application.RequestStop();
            };
            var close = new Button("Close", true);
            close.Clicked += delegate { Application.RequestStop(); };
            dialog.Add(view);
            dialog.AddButton(copy);
            dialog.AddButton(copyChat);
            dialog.AddButton(delete);
            dialog.AddButton(close);
            Application.Run(dialog);
            if (deleted)
            {
                NotifyAlertStateChanged(null);
                ShowSelectedChat();
            }
            if (_messages != null) _messages.SetFocus();
        }
        private static void CopyMapToClipboard()
        {
            if (_nodeMap == null) return;
            CopyTextWithFallback(_nodeMap.GetMapText(), "Current map copied.");
        }

        private static void CopyTextWithFallback(string text, string successMessage, string title = "Clipboard")
        {
            text = text ?? "";
            if (Clipboard.TrySetClipboardData(text)) { MessageBox.Query(title, successMessage, "OK"); return; }
            var fileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clipboard.txt");
            try { File.WriteAllText(fileName, text, new UTF8Encoding(false)); MessageBox.Query(title, "The clipboard is not available. The information was written to:\n" + fileName, "OK"); }
            catch (Exception ex) { MessageBox.ErrorQuery(title, "The clipboard is not available and clipboard.txt could not be written:\n" + ex.Message, "OK"); }
        }
        private static void CopyActiveChatToClipboard()
        {
            if (_selected == null)
            {
                MessageBox.Query("Clipboard", "Please select a chat first.", "OK");
                return;
            }

            IList<StoredMeshMessage> entries = _selected.Kind == ChatKind.Channel
                ? _store.GetChannelMessages((int)_selected.Id)
                : _store.GetDirectMessages(_selected.Id);
            var transcript = String.Join(Environment.NewLine, entries.Select(FormatMessageForClipboard));
            CopyTextWithFallback(transcript, entries.Count == 0 ? "The empty chat was copied." : "Chat history copied.");
        }

        private static string FormatMessageForClipboard(StoredMeshMessage message)
        {
            var who = message.Direction == MessageDirection.Outgoing ? "You" : DisplayNodeName(message.FromNode.GetValueOrDefault());
            var delivery = message.Direction == MessageDirection.Outgoing && !String.IsNullOrEmpty(message.DeliveryStatus) ? " [" + message.DeliveryStatus + "]" : "";
            var prefix = "[" + message.OccurredUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "] " + who + delivery + ": ";
            return prefix + (message.Text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine + new string(' ', prefix.Length));
        }

        private static void ShowEmojiUnavailable(string emoji)
        {
            MessageBox.Query("Emoji", "No ASCII representation is available for " + (String.IsNullOrEmpty(emoji) ? "this emoji." : emoji + "."), "OK");
        }
        private static void ShowEmojiPreview(string emoji)
        {
            if (String.IsNullOrEmpty(emoji)) { ShowEmojiUnavailable(emoji); return; }
            var terminalWidth = Math.Max(32, Application.Driver.Cols);
            var terminalHeight = Math.Max(18, Application.Driver.Rows);
            var useFullPreview = terminalWidth >= 56 && terminalHeight >= 34 && EmojiBlocksFull.ContainsKey(emoji);
            var blocks = useFullPreview ? EmojiBlocksFull : EmojiBlocksHalf;
            var columns = useFullPreview ? 48 : 24;
            var rows = useFullPreview ? 24 : 12;
            EmojiBlockEntry block;
            if (!blocks.TryGetValue(emoji, out block))
            {
                if (!EmojiBlocksFull.TryGetValue(emoji, out block) && !EmojiBlocksHalf.TryGetValue(emoji, out block))
                {
                    ShowEmojiUnavailable(emoji);
                    return;
                }
            }
            var dialogWidth = Math.Min(terminalWidth - 2, columns + 6);
            var dialogHeight = Math.Min(terminalHeight - 2, rows + 8);
            var dialog = new Dialog("", dialogWidth, dialogHeight);
            var frame = new FrameView("") { X = 1, Y = 1, Width = columns + 2, Height = rows + 2 };
            var preview = new Label(block.Text ?? "") { X = 0, Y = 0, Width = columns, Height = rows };
            var description = new Label(block.Name ?? "") { X = 1, Y = Pos.Bottom(frame), Width = Dim.Fill(1), Height = 2, TextAlignment = TextAlignment.Centered };
            frame.Add(preview);
            var close = new Button("Close", true);
            close.Clicked += delegate { Application.RequestStop(); };
            dialog.Add(frame, description);
            dialog.AddButton(close);
            Application.Run(dialog);
            if (_messages != null) _messages.SetFocus();
        }
        private static void LoadEmojiBlocks(string fileName, Dictionary<string, EmojiBlockEntry> target)
        {
            target.Clear();
            var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            if (!File.Exists(filePath)) return;
            try
            {
                var serializer = new XmlSerializer(typeof(EmojiBlockFile));
                using (var stream = File.OpenRead(filePath))
                {
                    var file = serializer.Deserialize(stream) as EmojiBlockFile;
                    if (file == null) return;
                    foreach (var item in file.Items)
                    {
                        if (String.IsNullOrEmpty(item.Character) || String.IsNullOrEmpty(item.Text)) continue;
                        item.Text = item.Text.Replace("\r\n", "\n").Trim('\r', '\n');
                        target[item.Character] = item;
                    }
                }
            }
            catch { target.Clear(); }
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

        private static void UpdateMessageCharacterCounter()
        {
            if (_inputFrame == null || _input == null || _messageCharacterCounter == null) return;
            var text = _input.Text == null ? "" : _input.Text.ToString();
            var characterCount = new StringInfo(text).LengthInTextElements;
            var byteCount = Encoding.UTF8.GetByteCount(text);
            var counter = " " + characterCount + " chars | " + byteCount + "/" + MeshtasticClient.MaximumTextPayloadBytes + " bytes ";
            _messageCharacterCounter.Text = counter;
            _messageCharacterCounter.Width = counter.Length;
            _messageCharacterCounter.X = Pos.Right(_inputFrame) - counter.Length - 1;
            _messageCharacterCounter.SetNeedsDisplay();
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
            _mapPage.Visible = false;
            _telemetryPage.Visible = false;
            _chatPage.Visible = true;
            _chatPage.SetFocus();
        }
        private static void ShowNodesPage()
        {
            RefreshNodePage();
            _chatPage.Visible = false;
            _mapPage.Visible = false;
            _telemetryPage.Visible = false;
            _nodesPage.Visible = true;
            _nodesPage.SetFocus();
        }
        private static void ShowMapPage()
        {
            if (_settings.Map == null) _settings.Map = new MeshtasticMapSettings();
            var savedLatitude = _settings.Map.CenterLatitude;
            var savedLongitude = _settings.Map.CenterLongitude;
            var savedScale = _settings.Map.MetersPerRow;
            _nodeMap.SetData(_store.GetNodes(StoredNodeSort.Name), _mesh.Device.Latitude, _mesh.Device.Longitude);
            RefreshMapOverlays();
            if (savedLatitude.HasValue && savedLongitude.HasValue) _nodeMap.RestoreView(savedLatitude.Value, savedLongitude.Value, savedScale);
            SaveMapViewState();
            _chatPage.Visible = false; _nodesPage.Visible = false; _telemetryPage.Visible = false; _mapPage.Visible = true;
            _nodeMap.SetFocus();
        }
        private static void UpdateMapGpsPosition()
        {
            if (_nodeMap == null || _mesh == null) return;
            _nodeMap.UpdateOwnPosition(_mesh.Device.Latitude, _mesh.Device.Longitude, _mapFollowGps);
        }
        private static MenuItem[] BuildMapMenuItems()
        {
            MenuItem followGps = null;
            followGps = new MenuItem("_Follow GPS position", "", delegate { _mapFollowGps = !_mapFollowGps; followGps.Checked = _mapFollowGps; UpdateMapGpsPosition(); });
            followGps.CheckType = MenuItemCheckStyle.Checked;
            followGps.Checked = _mapFollowGps;
            return new[]
            {
                new MenuItem("_Show map", "", ShowMapPage),
                followGps,
                new MenuItem("_Overlays", "", ShowMapOverlayOrderPopup),
                new MenuItem("_Copy current map", "", CopyMapToClipboard)
            };
        }

        private static void RefreshMapMenuFiles()
        {
            LoadMapOverlayFiles();
            if (_mapMenu != null) _mapMenu.Children = BuildMapMenuItems();
            RefreshMapOverlays();
        }

        private static void CreateMapOverlayFile()
        {
            var dialog = new Dialog("Create map XML", 62, 12);
            var fileName = new TextField("") { X = 16, Y = 1, Width = 38 };
            var displayName = new TextField("") { X = 16, Y = 2, Width = 38 };
            var writeProtected = new CheckBox("Write protected") { X = 16, Y = 4 };
            var selectable = new CheckBox("Items selectable") { X = 16, Y = 5, Checked = true };
            dialog.Add(new Label("File name:") { X = 1, Y = 1 }, fileName, new Label("Display name:") { X = 1, Y = 2 }, displayName, writeProtected, selectable,
                new Label("An empty MapData XML file will be created.") { X = 1, Y = 6 });
            var create = new Button("Create", true);
            create.Clicked += delegate
            {
                var entered = fileName.Text.ToString().Trim();
                if (!entered.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) entered += ".xml";
                if (String.IsNullOrWhiteSpace(entered) || entered != Path.GetFileName(entered) || entered.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    MessageBox.ErrorQuery("Create map XML", "Enter a valid file name without a directory.", "OK"); return;
                }
                var directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "map");
                var target = Path.Combine(directory, entered);
                if (File.Exists(target)) { MessageBox.ErrorQuery("Create map XML", "This file already exists.", "OK"); return; }
                try
                {
                    Directory.CreateDirectory(directory);
                    var data = new MapOverlayData { Name = String.IsNullOrWhiteSpace(displayName.Text.ToString()) ? Path.GetFileNameWithoutExtension(entered) : displayName.Text.ToString().Trim(), WriteProtected = writeProtected.Checked, Selectable = selectable.Checked };
                    var serializer = new XmlSerializer(typeof(MapOverlayData));
                    var xmlSettings = new System.Xml.XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
                    using (var writer = System.Xml.XmlWriter.Create(target, xmlSettings)) serializer.Serialize(writer, data);
                    SaveMapOverlayState(); RefreshMapMenuFiles(); Application.RequestStop();
                }
                catch (Exception ex) { MessageBox.ErrorQuery("Create map XML", ex.Message, "OK"); }
            };
            var cancel = new Button("Cancel"); cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.AddButton(create); dialog.AddButton(cancel); Application.Run(dialog);
        }

        private static void CreateMapOverlayPoint(double latitudeValue, double longitudeValue)
        {
            var writableFiles = MapOverlayFiles.Where(file => file.Data != null && !file.Data.WriteProtected).ToList();
            if (writableFiles.Count == 0) { MessageBox.Query("New map point", "Create or unlock a map XML file first.", "OK"); return; }
            var dialog = new Dialog("New map point", 92, 22);
            var latitude = new TextField(latitudeValue.ToString("F6", CultureInfo.InvariantCulture)) { X = 16, Y = 1, Width = 22 };
            var longitude = new TextField(longitudeValue.ToString("F6", CultureInfo.InvariantCulture)) { X = 16, Y = 2, Width = 22 };
            var files = new ListView(writableFiles.Select(file => file.DisplayName + " (" + Path.GetFileName(file.FileName) + ")").ToList()) { X = 16, Y = 4, Width = 38, Height = 5 };
            var previousFile = writableFiles.FindIndex(file => String.Equals(Path.GetFileName(file.FileName), _lastMapPointFile, StringComparison.OrdinalIgnoreCase));
            files.SelectedItem = previousFile >= 0 ? previousFile : 0;
            var colorNames = new[] { "Black", "Blue", "Green", "Cyan", "Red", "Magenta", "Brown", "Gray", "DarkGray", "BrightBlue", "BrightGreen", "BrightCyan", "BrightRed", "BrightMagenta", "BrightYellow", "White" };
            var color = new RadioGroup(new Rect(60, 1, 27, colorNames.Length), colorNames.Select(name => (ustring)name).ToArray());
            var previousColor = Array.FindIndex(colorNames, name => String.Equals(name, _lastMapPointColor, StringComparison.OrdinalIgnoreCase));
            color.SelectedItem = previousColor >= 0 ? previousColor : Array.IndexOf(colorNames, "Green");
            var shortName = new TextField(_lastMapPointShortName) { X = 16, Y = 10, Width = 38 };
            var description = new MessageInputView { X = 16, Y = 12, Width = 38, Height = 6, WordWrap = true, Text = _lastMapPointDescription };
            dialog.Add(new Label("Latitude:") { X = 1, Y = 1 }, latitude,
                new Label("Longitude:") { X = 1, Y = 2 }, longitude,
                new Label("XML file:") { X = 1, Y = 4 }, files,
                new Label("Colors:") { X = 60, Y = 0 }, color,
                new Label("Short name:") { X = 1, Y = 10 }, shortName,
                new Label("Description:") { X = 1, Y = 12 }, description);
            var save = new Button("Save", true);
            save.Clicked += delegate
            {
                double parsedLatitude, parsedLongitude;
                if (!TryParseCoordinate(latitude.Text.ToString(), out parsedLatitude) || !TryParseCoordinate(longitude.Text.ToString(), out parsedLongitude) || parsedLatitude < -90d || parsedLatitude > 90d || parsedLongitude < -180d || parsedLongitude > 180d)
                { MessageBox.ErrorQuery("New map point", "Enter valid latitude and longitude values.", "OK"); return; }
                if (files.SelectedItem < 0 || files.SelectedItem >= writableFiles.Count) { MessageBox.ErrorQuery("New map point", "Select an XML file.", "OK"); return; }
                if (String.IsNullOrWhiteSpace(shortName.Text.ToString())) { MessageBox.ErrorQuery("New map point", "Enter a short name.", "OK"); return; }
                if (color.SelectedItem < 0 || color.SelectedItem >= colorNames.Length) { MessageBox.ErrorQuery("New map point", "Select a color.", "OK"); return; }
                var selectedColor = colorNames[color.SelectedItem];
                var target = writableFiles[files.SelectedItem];
                var point = new MapOverlayPoint { Latitude = parsedLatitude, Longitude = parsedLongitude, Color = selectedColor, ShortName = shortName.Text.ToString().Trim(), Description = description.Text.ToString().Trim(), SourceFile = target.FileName, OverlayName = target.DisplayName };
                try
                {
                    target.Data.Places.Add(point); SaveMapOverlayFile(target); target.Enabled = true;
                    _lastMapPointFile = Path.GetFileName(target.FileName); _lastMapPointColor = point.Color; _lastMapPointShortName = point.ShortName; _lastMapPointDescription = point.Description;
                    SaveMapOverlayState(); RefreshMapOverlays(); if (_mapMenu != null) _mapMenu.Children = BuildMapMenuItems(); Application.RequestStop();
                }
                catch (Exception ex) { MessageBox.ErrorQuery("New map point", ex.Message, "OK"); }
            };
            var cancel = new Button("Cancel"); cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.AddButton(save); dialog.AddButton(cancel); Application.Run(dialog); _nodeMap.SetFocus();
        }
        private static void ChangeMapOverlayProtection()
        {
            if (MapOverlayFiles.Count == 0) { MessageBox.Query("Map XML protection", "No map XML files are available.", "OK"); return; }
            var dialog = new Dialog("Map XML protection", 70, 16);
            var list = new ListView(MapOverlayFiles.Select(file => (file.Data != null && file.Data.WriteProtected ? "[protected] " : "[writable] ") + file.DisplayName + " (" + Path.GetFileName(file.FileName) + ")").ToList()) { X = 1, Y = 1, Width = Dim.Fill(2), Height = Dim.Fill(4) };
            if (_mapOverlayOrderList != null && _mapOverlayOrderList.SelectedItem >= 0 && _mapOverlayOrderList.SelectedItem < MapOverlayFiles.Count) list.SelectedItem = _mapOverlayOrderList.SelectedItem;
            dialog.Add(list);
            var change = new Button("Change", true);
            change.Clicked += delegate
            {
                if (list.SelectedItem < 0 || list.SelectedItem >= MapOverlayFiles.Count) return;
                var file = MapOverlayFiles[list.SelectedItem];
                try { file.Data.WriteProtected = !file.Data.WriteProtected; SaveMapOverlayFile(file); RefreshMapMenuFiles(); Application.RequestStop(); }
                catch (Exception ex) { MessageBox.ErrorQuery("Map XML protection", ex.Message, "OK"); }
            };
            var cancel = new Button("Cancel"); cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.AddButton(change); dialog.AddButton(cancel); Application.Run(dialog);
        }
        private static void DeleteMapOverlayFile()
        {
            if (MapOverlayFiles.Count == 0) { MessageBox.Query("Delete map XML", "No map XML files are available.", "OK"); return; }
            var dialog = new Dialog("Delete map XML", 70, 16);
            var list = new ListView(MapOverlayFiles.Select(file => (file.Data != null && file.Data.WriteProtected ? "[protected] " : "") + file.DisplayName + " (" + Path.GetFileName(file.FileName) + ")").ToList()) { X = 1, Y = 1, Width = Dim.Fill(2), Height = Dim.Fill(4) };
            if (_mapOverlayOrderList != null && _mapOverlayOrderList.SelectedItem >= 0 && _mapOverlayOrderList.SelectedItem < MapOverlayFiles.Count) list.SelectedItem = _mapOverlayOrderList.SelectedItem;
            dialog.Add(list);
            var delete = new Button("Delete", true);
            delete.Clicked += delegate
            {
                if (list.SelectedItem < 0 || list.SelectedItem >= MapOverlayFiles.Count) return;
                var selectedFile = MapOverlayFiles[list.SelectedItem];
                if (selectedFile.Data != null && selectedFile.Data.WriteProtected) { MessageBox.ErrorQuery("Delete map XML", "This XML file is write protected and cannot be deleted.", "OK"); return; }
                if (MessageBox.Query("Delete map XML", "Delete " + Path.GetFileName(selectedFile.FileName) + "?", "Delete", "Cancel") != 0) return;
                try { File.Delete(selectedFile.FileName); MapOverlayFiles.Remove(selectedFile); SaveMapOverlayState(); RefreshMapMenuFiles(); Application.RequestStop(); }
                catch (Exception ex) { MessageBox.ErrorQuery("Delete map XML", ex.Message, "OK"); }
            };
            var cancel = new Button("Cancel"); cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.AddButton(delete); dialog.AddButton(cancel); Application.Run(dialog);
        }
        private static void LoadMapOverlayFiles()
        {
            MapOverlayFiles.Clear();
            var directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "map");
            if (!Directory.Exists(directory)) return;
            var serializer = new XmlSerializer(typeof(MapOverlayData));
            foreach (var fileName in Directory.GetFiles(directory, "*.xml").OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using (var stream = File.OpenRead(fileName))
                    {
                        var data = serializer.Deserialize(stream) as MapOverlayData;
                        if (data == null) continue;
                        foreach (var point in data.Places ?? new List<MapOverlayPoint>()) { point.SourceFile = fileName; point.OverlayName = String.IsNullOrWhiteSpace(data.Name) ? Path.GetFileNameWithoutExtension(fileName) : data.Name; point.Selectable = data.Selectable; }
                        var enabled = _settings.Map != null && _settings.Map.ActiveOverlayFiles != null ? _settings.Map.ActiveOverlayFiles.Any(saved => String.Equals(saved, Path.GetFileName(fileName), StringComparison.OrdinalIgnoreCase)) : false;
                        MapOverlayFiles.Add(new MapOverlayFile { FileName = fileName, DisplayName = String.IsNullOrWhiteSpace(data.Name) ? Path.GetFileNameWithoutExtension(fileName) : data.Name, Enabled = enabled, Data = data });
                    }
                }
                catch { }
            }
            var savedOrder = _settings.Map != null && _settings.Map.ActiveOverlayFiles != null ? _settings.Map.ActiveOverlayFiles : new List<string>();
            MapOverlayFiles.Sort(delegate(MapOverlayFile left, MapOverlayFile right)
            {
                var leftIndex = savedOrder.FindIndex(name => String.Equals(name, Path.GetFileName(left.FileName), StringComparison.OrdinalIgnoreCase));
                var rightIndex = savedOrder.FindIndex(name => String.Equals(name, Path.GetFileName(right.FileName), StringComparison.OrdinalIgnoreCase));
                if (leftIndex >= 0 && rightIndex >= 0) return leftIndex.CompareTo(rightIndex);
                if (leftIndex >= 0) return -1;
                if (rightIndex >= 0) return 1;
                return StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
            });
        }

        private static void SaveMapOverlayFile(MapOverlayFile file)
        {
            if (file == null || file.Data == null || String.IsNullOrWhiteSpace(file.FileName)) throw new InvalidOperationException("The map XML file is unavailable.");
            var temporary = file.FileName + ".tmp";
            var serializer = new XmlSerializer(typeof(MapOverlayData));
            var xmlSettings = new System.Xml.XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
            try
            {
                using (var writer = System.Xml.XmlWriter.Create(temporary, xmlSettings)) serializer.Serialize(writer, file.Data);
                File.Copy(temporary, file.FileName, true);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
        private static void ShowMapOverlayOrderPopup()
        {
            var dialog = new Dialog("Overlays", 96, 22);
            _mapOverlayOrderList = new ListView { X = 1, Y = 1, Width = Dim.Fill(2), Height = Dim.Fill(1) };
            _mapOverlayOrderList.KeyPress += e =>
            {
                if (e.KeyEvent.Key == (Key.CursorUp | Key.AltMask)) { MoveActiveMapOverlay(-1); e.Handled = true; }
                else if (e.KeyEvent.Key == (Key.CursorDown | Key.AltMask)) { MoveActiveMapOverlay(1); e.Handled = true; }
                else if (e.KeyEvent.Key == (Key)' ') { ToggleSelectedMapOverlay(); e.Handled = true; }
            };
            _mapOverlayOrderList.OpenSelectedItem += delegate { ToggleSelectedMapOverlay(); };
            dialog.Add(_mapOverlayOrderList);
            var toggle = new Button("Enable/Disable"); toggle.Clicked += ToggleSelectedMapOverlay;
            var up = new Button("Up"); up.Clicked += delegate { MoveActiveMapOverlay(-1); };
            var down = new Button("Down"); down.Clicked += delegate { MoveActiveMapOverlay(1); };
            var create = new Button("New XML"); create.Clicked += CreateMapOverlayFile;
            var protection = new Button("Protection"); protection.Clicked += ChangeMapOverlayProtection;
            var delete = new Button("Delete"); delete.Clicked += DeleteMapOverlayFile;
            var close = new Button("Close", true); close.Clicked += delegate { Application.RequestStop(); };
            dialog.AddButton(toggle); dialog.AddButton(up); dialog.AddButton(down); dialog.AddButton(create); dialog.AddButton(protection); dialog.AddButton(delete); dialog.AddButton(close);
            RefreshMapOverlayOrderBox();
            _mapOverlayOrderList.SetFocus();
            Application.Run(dialog);
            _mapOverlayOrderList = null;
            if (_mapPage != null && _mapPage.Visible && _nodeMap != null) _nodeMap.SetFocus();
        }

        private static void RefreshMapOverlayOrderBox()
        {
            if (_mapOverlayOrderList == null) return;
            var selectedFile = _mapOverlayOrderList.SelectedItem >= 0 && _mapOverlayOrderList.SelectedItem < MapOverlayFiles.Count ? MapOverlayFiles[_mapOverlayOrderList.SelectedItem].FileName : null;
            var active = MapOverlayFiles.Where(file => file.Enabled).ToList();
            _mapOverlayOrderList.SetSource(MapOverlayFiles.Select(file =>
                (file.Enabled ? "[x] " + (active.IndexOf(file) + 1).ToString().PadLeft(2) : "[ ]  -") + "  " + file.DisplayName + " (" + Path.GetFileName(file.FileName) + ")" +
                (file.Data != null && file.Data.WriteProtected ? " [protected]" : "") + (file.Data != null && !file.Data.Selectable ? " [display only]" : "")).ToList());
            var selectedIndex = MapOverlayFiles.FindIndex(file => String.Equals(file.FileName, selectedFile, StringComparison.OrdinalIgnoreCase));
            if (MapOverlayFiles.Count > 0) _mapOverlayOrderList.SelectedItem = selectedIndex >= 0 ? selectedIndex : Math.Min(Math.Max(0, _mapOverlayOrderList.SelectedItem), MapOverlayFiles.Count - 1);
        }

        private static void ToggleSelectedMapOverlay()
        {
            if (_mapOverlayOrderList == null || _mapOverlayOrderList.SelectedItem < 0 || _mapOverlayOrderList.SelectedItem >= MapOverlayFiles.Count) return;
            var selected = MapOverlayFiles[_mapOverlayOrderList.SelectedItem];
            selected.Enabled = !selected.Enabled;
            RefreshMapOverlays();
            _mapOverlayOrderList.SelectedItem = MapOverlayFiles.IndexOf(selected);
            SaveMapOverlayState();
        }

        private static void MoveActiveMapOverlay(int direction)
        {
            if (_mapOverlayOrderList == null || _mapOverlayOrderList.SelectedItem < 0 || _mapOverlayOrderList.SelectedItem >= MapOverlayFiles.Count) return;
            var selectedFile = MapOverlayFiles[_mapOverlayOrderList.SelectedItem];
            if (!selectedFile.Enabled) return;
            var active = MapOverlayFiles.Where(file => file.Enabled).ToList();
            var selected = active.IndexOf(selectedFile);
            var target = selected + direction;
            if (target < 0 || target >= active.Count) return;
            var firstIndex = MapOverlayFiles.IndexOf(selectedFile);
            var secondIndex = MapOverlayFiles.IndexOf(active[target]);
            MapOverlayFiles[firstIndex] = MapOverlayFiles[secondIndex];
            MapOverlayFiles[secondIndex] = selectedFile;
            RefreshMapOverlays();
            _mapOverlayOrderList.SelectedItem = MapOverlayFiles.IndexOf(selectedFile);
            SaveMapOverlayState();
        }

        private static void RefreshMapOverlays()
        {
            if (_nodeMap != null) _nodeMap.SetOverlayPoints(MapOverlayFiles.Where(file => file.Enabled && file.Data != null && file.Data.Places != null).SelectMany(file => file.Data.Places));
            RefreshMapOverlayOrderBox();
        }
        private static void SaveMapOverlayState()
        {
            if (_settings == null) return;
            if (_settings.Map == null) _settings.Map = new MeshtasticMapSettings();
            _settings.Map.ActiveOverlayFiles = MapOverlayFiles.Where(file => file.Enabled).Select(file => Path.GetFileName(file.FileName)).ToList();
        }
        private static void SaveMapViewState()
        {
            if (_settings == null || _nodeMap == null) return;
            if (_settings.Map == null) _settings.Map = new MeshtasticMapSettings();
            _settings.Map.CenterLatitude = _nodeMap.CenterLatitude;
            _settings.Map.CenterLongitude = _nodeMap.CenterLongitude;
            _settings.Map.MetersPerRow = _nodeMap.MetersPerRow;
            SaveMapOverlayState();
        }
        private static void UpdateMapInfo(StoredMeshNode node)
        {
            if (_mapInfo == null) return;
            if (node == null) { _mapInfo.Text = "Multiple nodes selected - press Enter to center"; return; }
            var distance = MeshtasticClient.GetDistanceMeters(_mesh.Device.Latitude, _mesh.Device.Longitude, node.Latitude, node.Longitude);
            var bearing = MeshtasticClient.GetInitialBearingDegrees(_mesh.Device.Latitude, _mesh.Device.Longitude, node.Latitude, node.Longitude);
            var distanceText = !distance.HasValue ? "-" : distance.Value < 1000d ? Math.Round(distance.Value) + " m" : (distance.Value / 1000d).ToString("F1", CultureInfo.InvariantCulture) + " km";
            var directionText = bearing.HasValue ? Math.Round(bearing.Value) + "° " + MeshtasticClient.GetCompassDirection(bearing.Value) : "-";
            _mapInfo.Text = "Node | " + (String.IsNullOrWhiteSpace(node.LongName) ? node.NodeId ?? "!" + node.NodeNumber.ToString("x8") : node.LongName) + " | Direction " + directionText + " | Distance " + distanceText + " | Hops " + (node.HopsAway.HasValue ? node.HopsAway.Value.ToString(CultureInfo.InvariantCulture) : "-");
        }
        private static void UpdateMapOverlayInfo(MapOverlayPoint point)
        {
            if (_mapInfo == null || point == null) return;
            var distance = MeshtasticClient.GetDistanceMeters(_mesh.Device.Latitude, _mesh.Device.Longitude, point.Latitude, point.Longitude);
            var bearing = MeshtasticClient.GetInitialBearingDegrees(_mesh.Device.Latitude, _mesh.Device.Longitude, point.Latitude, point.Longitude);
            var distanceText = !distance.HasValue ? "-" : distance.Value < 1000d ? Math.Round(distance.Value) + " m" : (distance.Value / 1000d).ToString("F1", CultureInfo.InvariantCulture) + " km";
            var directionText = bearing.HasValue ? Math.Round(bearing.Value) + "° " + MeshtasticClient.GetCompassDirection(bearing.Value) : "-";
            _mapInfo.Text = "Map overlay | " + Path.GetFileName(point.SourceFile ?? "-") + " | Direction " + directionText + " | Distance " + distanceText + " | Description " + (String.IsNullOrWhiteSpace(point.Description) ? point.ShortName ?? "?" : point.Description);
        }
        private static void ShowMapOverlayDetails(MapOverlayPoint point)
        {
            if (point == null) return;
            var source = MapOverlayFiles.FirstOrDefault(file => String.Equals(file.FileName, point.SourceFile, StringComparison.OrdinalIgnoreCase));
            var text = "Type: Map overlay" +
                "\nOverlay: " + (point.OverlayName ?? "-") +
                "\nFile: " + Path.GetFileName(point.SourceFile ?? "-") +
                "\nDescription: " + (String.IsNullOrWhiteSpace(point.Description) ? "-" : point.Description) +
                "\nShort name: " + (String.IsNullOrWhiteSpace(point.ShortName) ? "-" : point.ShortName) +
                "\nPosition: " + point.Latitude.ToString("F6", CultureInfo.InvariantCulture) + ", " + point.Longitude.ToString("F6", CultureInfo.InvariantCulture) +
                "\nColor: " + (point.Color ?? "-") +
                "\nWrite protected: " + (source != null && source.Data != null && source.Data.WriteProtected ? "yes" : "no");
            var choice = MessageBox.Query("Map item details", text, "Center", "Delete item", "Close");
            if (choice == 0) _nodeMap.CenterOn(point.Latitude, point.Longitude);
            else if (choice == 1)
            {
                if (source == null || source.Data == null) { MessageBox.ErrorQuery("Delete map item", "The source XML file is unavailable.", "OK"); return; }
                if (source.Data.WriteProtected) { MessageBox.ErrorQuery("Delete map item", "The source XML file is write protected.", "OK"); return; }
                if (MessageBox.Query("Delete map item", "Delete this item from " + Path.GetFileName(source.FileName) + "?", "Delete", "Cancel") != 0) return;
                try { source.Data.Places.Remove(point); SaveMapOverlayFile(source); RefreshMapOverlays(); _mapInfo.Text = "No item selected"; }
                catch (Exception ex) { MessageBox.ErrorQuery("Delete map item", ex.Message, "OK"); }
            }
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
            _mapPage.Visible = false;
            _nodesPage.Visible = false;
            _mapPage.Visible = false;
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
            CopyTextWithFallback(String.Join(Environment.NewLine, rows), excelFormat ? "Telemetry was copied in spreadsheet format." : "Telemetry was copied as CSV.");
        }
        private static string NullableText(uint? value) { return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : ""; }
        private static string NullableText(double? value, bool useLocalDecimalSeparator) { return value.HasValue ? value.Value.ToString(useLocalDecimalSeparator ? CultureInfo.CurrentCulture : CultureInfo.InvariantCulture) : ""; }
        private static string ClipboardField(string value, bool excelFormat)
        {
            value = value ?? "";
            if (excelFormat) return value.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        private static void CopyNodeList()
        {
            RefreshNodePage();
            CopyTextWithFallback(String.Join(Environment.NewLine, NodeItems.Select(FormatNode)), NodeItems.Count == 0 ? "The empty node list was copied." : "Node list copied.");
        }

        private static void RestoreNodeListState()
        {
            if (_settings.Nodes == null) _settings.Nodes = new MeshtasticNodeListSettings();
            _favoritesOnly = _settings.Nodes.FavoritesOnly;
            _nodeSortAscending = _settings.Nodes.SortAscending;
            NodeSortMode parsed;
            _nodeSort = Enum.TryParse(_settings.Nodes.SortMode ?? "Name", true, out parsed) ? parsed : NodeSortMode.Name;
        }

        private static void SaveNodeListState()
        {
            if (_settings == null) return;
            if (_settings.Nodes == null) _settings.Nodes = new MeshtasticNodeListSettings();
            _settings.Nodes.FavoritesOnly = _favoritesOnly;
            _settings.Nodes.SortAscending = _nodeSortAscending;
            _settings.Nodes.SortMode = _nodeSort.ToString();
            if (_nodeSearch != null) _settings.Nodes.SearchText = _nodeSearch.Text.ToString();
        }

        private static string NodeSortLabel()
        {
            return _nodeSort == NodeSortMode.Name ? "name" : _nodeSort == NodeSortMode.NodeId ? "ID" : _nodeSort == NodeSortMode.LastReceived ? "last" : _nodeSort == NodeSortMode.Distance ? "distance" : _nodeSort == NodeSortMode.Hops ? "hops" : "signal";
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
            CopyTextWithFallback(position, "GPS position copied: " + position, "Copy GPS");
        }
        private static string FormatNode(StoredMeshNode node)
        {
            var distance = MeshtasticClient.GetDistanceMeters(_mesh.Device.Latitude, _mesh.Device.Longitude, node.Latitude, node.Longitude);
            var bearing = MeshtasticClient.GetInitialBearingDegrees(_mesh.Device.Latitude, _mesh.Device.Longitude, node.Latitude, node.Longitude);
            var direction = bearing.HasValue ? MeshtasticClient.GetCompassDirection(bearing.Value) : "-";
            var distanceText = distance.HasValue ? (distance.Value < 1000 ? Math.Round(distance.Value) + "m" : (distance.Value / 1000d).ToString("F1", CultureInfo.InvariantCulture) + "km") : "-";
            var id = (node.NodeId ?? "!" + node.NodeNumber.ToString("x8")).PadRight(12).Substring(0, 12);
            var shortName = ReplaceGraphicalSymbols(String.IsNullOrWhiteSpace(node.ShortName) ? "-" : node.ShortName).PadRight(5).Substring(0, 5);
            var name = ReplaceGraphicalSymbols(node.LongName ?? "unknown").PadRight(22).Substring(0, 22);
            var liveNode = _mesh.Nodes.FirstOrDefault(item => item.Number == node.NodeNumber);
            var rssi = liveNode == null || !liveNode.LastRssi.HasValue ? "-" : liveNode.LastRssi.Value.ToString(CultureInfo.InvariantCulture);
            var snr = liveNode == null || !liveNode.LastSnr.HasValue ? "-" : liveNode.LastSnr.Value.ToString("F1", CultureInfo.InvariantCulture);
            return (node.IsFavorite ? "* " : "  ") + id + " " + shortName + " | " + node.LastReceivedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " | " + distanceText.PadLeft(8) + " " + direction.PadRight(5) + " | hops " + (node.HopsAway.HasValue ? node.HopsAway.Value.ToString() : "-") + " | RSSI " + rssi.PadLeft(4) + " SNR " + snr.PadLeft(5) + " | " + name;
        }
        private static void CycleNodeSort()
        {
            _nodeSort = _nodeSort == NodeSortMode.Name ? NodeSortMode.NodeId :
                _nodeSort == NodeSortMode.NodeId ? NodeSortMode.LastReceived :
                _nodeSort == NodeSortMode.LastReceived ? NodeSortMode.Distance :
                _nodeSort == NodeSortMode.Distance ? NodeSortMode.Hops :
                _nodeSort == NodeSortMode.Hops ? NodeSortMode.Signal : NodeSortMode.Name;
            _sortButton.Text = "Sort: " + NodeSortLabel();
            SaveNodeListState();
            RefreshNodePage();
        }
        private static void ToggleNodeSortDirection()
        {
            _nodeSortAscending = !_nodeSortAscending;
            _nodeSortDirectionButton.Text = _nodeSortAscending ? "Order: asc" : "Order: desc";
            SaveNodeListState();
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
            var dialog = new Dialog("Logo settings", 66, 16);
            var enabled = new CheckBox("Show logo on the chat page") { X = 1, Y = 1, Checked = logo.ShowLogo };
            var showFrame = new CheckBox("Show logo frame") { X = 1, Y = 2, Checked = logo.ShowFrame };
            var automatic = new CheckBox("Automatically change logos") { X = 1, Y = 3, Checked = logo.AutomaticRotation };
            var showEmoji = new CheckBox("Show selected message emoji in logo frame") { X = 1, Y = 4, Checked = logo.ShowSelectedEmoji };
            var interval = new TextField(logo.RotationIntervalSeconds.ToString(CultureInfo.InvariantCulture)) { X = 34, Y = 5, Width = 8 };
            var width = new TextField(logo.InnerWidth.ToString(CultureInfo.InvariantCulture)) { X = 34, Y = 6, Width = 8 };
            var height = new TextField(logo.InnerHeight.ToString(CultureInfo.InvariantCulture)) { X = 34, Y = 7, Width = 8 };
            dialog.Add(enabled, showFrame, automatic, showEmoji, new Label("Change logo every (seconds):") { X = 1, Y = 5 }, interval, new Label("Logo and chat width:") { X = 1, Y = 6 }, width, new Label("Logo height:") { X = 1, Y = 7 }, height, new Label("F7: Previous logo | F8: Next logo") { X = 1, Y = 9 });
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
                logo.ShowFrame = showFrame.Checked;
                logo.AutomaticRotation = automatic.Checked;
                logo.ShowSelectedEmoji = showEmoji.Checked;
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
            _logoFrame.Border.BorderStyle = _settings.Logo.ShowFrame ? BorderStyle.Single : BorderStyle.None;
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
            if (TryShowSelectedEmojiInLogo()) return;
            if (LogoFiles.Count == 0) _logoText.Text = "No logo files found.\nPlace *.txt files in .\\logo.";
            else
            {
                try { _logoText.Text = File.ReadAllText(LogoFiles[_logoIndex]); _settings.Logo.LastLogoFileName = Path.GetFileName(LogoFiles[_logoIndex]); }
                catch { _logoText.Text = "Could not read logo file."; }
            }
            _nextLogoChangeUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, _settings.Logo.RotationIntervalSeconds));
            _logoText.SetNeedsDisplay();
        }

        private static bool TryShowSelectedEmojiInLogo()
        {
            if (_settings == null || _settings.Logo == null || !_settings.Logo.ShowSelectedEmoji || _messages == null) return false;
            var token = _messages.SelectedEmojiToken;
            if (String.IsNullOrEmpty(token)) return false;
            var emoji = ResolveEmojiReplacement(token);
            if (String.IsNullOrEmpty(emoji)) return false;

            var innerWidth = _settings == null || _settings.Logo == null ? 32 : Math.Max(1, _settings.Logo.InnerWidth);
            var innerHeight = _settings == null || _settings.Logo == null ? 8 : Math.Max(1, _settings.Logo.InnerHeight);
            var useFull = innerWidth >= 48 && innerHeight >= 24 && EmojiBlocksFull.ContainsKey(emoji);
            EmojiBlockEntry block;
            if (!(useFull ? EmojiBlocksFull : EmojiBlocksHalf).TryGetValue(emoji, out block)
                && !EmojiBlocksHalf.TryGetValue(emoji, out block)
                && !EmojiBlocksFull.TryGetValue(emoji, out block)) return false;
            _logoText.Text = block.Text ?? "";
            _logoText.SetNeedsDisplay();
            return true;
        }
        private static void ShowSettings()
        {
            var c = _settings.Connection;
            var dialog = new Dialog("Connection settings", 70, 17);
            var transport = new RadioGroup(new Rect(1, 1, 20, 2), new ustring[] { "Serial", "TCP" }) { SelectedItem = c.Transport == MeshtasticTransportType.Serial ? 0 : 1 };
            var serial = new TextField(c.SerialPort) { X = 16, Y = 4, Width = 20 };
            var baud = new TextField(c.SerialBaudRate.ToString(CultureInfo.InvariantCulture)) { X = 16, Y = 5, Width = 20 };
            var host = new TextField(c.TcpHost) { X = 16, Y = 7, Width = 35 };
            var port = new TextField(c.TcpPort.ToString(CultureInfo.InvariantCulture)) { X = 16, Y = 8, Width = 20 };
            var automaticReconnect = new CheckBox("Automatic reconnect") { X = 1, Y = 10, Checked = c.AutomaticReconnect };
            var reconnectInterval = new TextField(c.ReconnectIntervalSeconds.ToString(CultureInfo.InvariantCulture)) { X = 30, Y = 11, Width = 8 };
            dialog.Add(transport, new Label("Serial port:") { X = 1, Y = 4 }, serial, new Label("Baud rate:") { X = 1, Y = 5 }, baud, new Label("TCP host:") { X = 1, Y = 7 }, host, new Label("TCP port:") { X = 1, Y = 8 }, port, automaticReconnect, new Label("Reconnect interval (seconds):") { X = 1, Y = 11 }, reconnectInterval);
            var save = new Button("Save", true);
            save.Clicked += delegate
            {
                int parsedBaud, parsedPort, parsedReconnectInterval;
                if (!Int32.TryParse(baud.Text.ToString(), out parsedBaud) || !Int32.TryParse(port.Text.ToString(), out parsedPort)) { MessageBox.ErrorQuery("Settings", "Baud rate and port must be numbers.", "OK"); return; }
                if (!Int32.TryParse(reconnectInterval.Text.ToString(), out parsedReconnectInterval) || parsedReconnectInterval < 1 || parsedReconnectInterval > 86400) { MessageBox.ErrorQuery("Settings", "The reconnect interval must be between 1 and 86400 seconds.", "OK"); return; }
                c.Transport = transport.SelectedItem == 0 ? MeshtasticTransportType.Serial : MeshtasticTransportType.Tcp;
                c.SerialPort = serial.Text.ToString(); c.SerialBaudRate = parsedBaud; c.TcpHost = host.Text.ToString(); c.TcpPort = parsedPort;
                c.AutomaticReconnect = automaticReconnect.Checked; c.ReconnectIntervalSeconds = parsedReconnectInterval;
                ApplyConnectionReconnectSettings();
                try { MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings); Application.RequestStop(); } catch (Exception ex) { MessageBox.ErrorQuery("Settings", ex.Message, "OK"); }
            };
            var cancel = new Button("Cancel"); cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.AddButton(save); dialog.AddButton(cancel); Application.Run(dialog);
        }

        private static void ShowAlertSettings()
        {
            var dialog = new Dialog("Alert settings", 92, 16);
            var beep = new CheckBox("Beep for new incoming messages") { X = 1, Y = 1, Checked = _settings.EnableNewMessageBeep };
            var interval = new TextField(_settings.Alerts.RepeatBeepIntervalSeconds.ToString(CultureInfo.InvariantCulture)) { X = 42, Y = 2, Width = 8 };
            var httpEnabled = new CheckBox("Enable alert HTTP GET") { X = 1, Y = 4, Checked = _settings.Alerts.EnableHttpGet };
            var httpUrl = new TextField(_settings.Alerts.HttpGetUrl ?? "") { X = 22, Y = 5, Width = 65 };
            var executableEnabled = new CheckBox("Enable alert shell command") { X = 1, Y = 7, Checked = _settings.Alerts.EnableExecutable };
            var executable = new TextField(_settings.Alerts.ExecutablePath ?? "") { X = 22, Y = 8, Width = 65 };
            var save = new Button("Save", true);
            save.Clicked += delegate
            {
                int parsedInterval;
                if (!Int32.TryParse(interval.Text.ToString(), out parsedInterval) || parsedInterval < 0) { MessageBox.ErrorQuery("Alert settings", "The repeat interval must be zero or a positive number of seconds.", "OK"); return; }
                _settings.EnableNewMessageBeep = beep.Checked;
                _settings.Alerts.RepeatBeepIntervalSeconds = parsedInterval;
                _settings.Alerts.EnableHttpGet = httpEnabled.Checked;
                _settings.Alerts.HttpGetUrl = httpUrl.Text.ToString().Trim();
                _settings.Alerts.EnableExecutable = executableEnabled.Checked;
                _settings.Alerts.ExecutablePath = executable.Text.ToString().Trim();
                MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings); Application.RequestStop();
            };
            var cancel = new Button("Cancel");
            cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.Add(beep,
                new Label("Repeat beep interval (seconds, 0 = off):") { X = 1, Y = 2 }, interval,
                httpEnabled, new Label("HTTP GET URL:") { X = 1, Y = 5 }, httpUrl,
                executableEnabled, new Label("Shell command:") { X = 1, Y = 8 }, executable);
            dialog.AddButton(save); dialog.AddButton(cancel);
            Application.Run(dialog);
        }

        private static void ShowTelemetryStorageSettings()
        {
            var dialog = new Dialog("Telemetry storage", 62, 10);
            var enabled = new CheckBox("Store received telemetry data in the database") { X = 1, Y = 1, Checked = _settings.StoreTelemetryData };
            dialog.Add(enabled, new Label("Existing telemetry records are not deleted when disabled.") { X = 1, Y = 3 });
            var save = new Button("Save", true);
            save.Clicked += delegate { _settings.StoreTelemetryData = enabled.Checked; MeshtasticSettingsStore.Save("meshtastic-settings.xml", _settings); Application.RequestStop(); };
            var cancel = new Button("Cancel");
            cancel.Clicked += delegate { Application.RequestStop(); };
            dialog.AddButton(save); dialog.AddButton(cancel);
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

        private static void ShowChatBotDebug() { ShowBotDebug("Chat bot Debug", delegate { return _lastChatBotRequest; }, delegate { return _lastChatBotOutput; }); }
        private static void ShowHttpBotDebug() { ShowBotDebug("HTTP bot Debug", delegate { return _lastHttpBotRequest; }, delegate { return _lastHttpBotOutput; }); }
        private static void ShowAlertHttpDebug() { ShowBotDebug("Alert HTTP Debug", delegate { return _lastAlertHttpRequest; }, delegate { return _lastAlertHttpOutput; }); }
        private static void ShowAlertProcessDebug() { ShowBotDebug("Alert shell command Debug", delegate { return _lastAlertProcessRequest; }, delegate { return _lastAlertProcessOutput; }); }
        private static void ShowBotDebug(string title, Func<string> requestProvider, Func<string> outputProvider)
        {
            var dialog = new Dialog(title, 108, 30);
            var requestFrame = new FrameView("Last complete request") { X = 1, Y = 1, Width = Dim.Fill(2), Height = 12 };
            var requestView = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), WordWrap = true, CanFocus = true, ReadOnly = true, Text = requestProvider() ?? "" };
            requestFrame.Add(requestView);
            var outputFrame = new FrameView("Last bot output") { X = 1, Y = 13, Width = Dim.Fill(2), Height = Dim.Fill(4) };
            var outputView = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), WordWrap = true, CanFocus = true, ReadOnly = true, Text = outputProvider() ?? "" };
            outputFrame.Add(outputView);
            var refresh = new Button("Refresh", true); refresh.Clicked += delegate { requestView.Text = requestProvider() ?? ""; outputView.Text = outputProvider() ?? ""; requestView.SetNeedsDisplay(); outputView.SetNeedsDisplay(); };
            var copyRequest = new Button("Copy request"); copyRequest.Clicked += delegate { CopyDebugText(requestView.Text.ToString()); };
            var copyOutput = new Button("Copy output"); copyOutput.Clicked += delegate { CopyDebugText(outputView.Text.ToString()); };
            var close = new Button("Close"); close.Clicked += delegate { Application.RequestStop(); };
            dialog.Add(requestFrame, outputFrame); dialog.AddButton(refresh); dialog.AddButton(copyRequest); dialog.AddButton(copyOutput); dialog.AddButton(close); Application.Run(dialog);
        }

        private static void CopyDebugText(string text)
        {
            CopyTextWithFallback(text ?? "", "Debug text copied.");
        }

        private static void ShowInfo()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            var buildDate = assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute)).OfType<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == "BuildDateUtc");
            MessageBox.Query("Info", "Meshtastic ConsoleClient\n\nCreated by Tobias Krista with support from AI.\nBuild: " + (version == null ? "-" : version.ToString()) + "\nCompiled: " + (buildDate == null ? "-" : buildDate.Value + " UTC") + "\n\nSource code:\nhttps://github.com/kr-saibot/MeshtasticConsoleClient\n\nThis program is published under the GNU General Public License v3.0 (GPLv3).", "OK");
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

        private static void NotifyAlertStateChanged(MeshMessage message)
        {
            int unreadCount;
            try { unreadCount = _store.CountNewMessages(); }
            catch { return; }

            var becameFullyRead = message == null && unreadCount == 0 && _lastKnownUnreadCount > 0;
            _lastKnownUnreadCount = unreadCount;

            if (message != null && _settings.EnableNewMessageBeep) PlayAlertBeep();
            _nextRepeatedAlertCheckUtc = unreadCount > 0
                ? DateTime.UtcNow.AddSeconds(Math.Max(1, _settings.Alerts.RepeatBeepIntervalSeconds))
                : DateTime.MinValue;

            if (message == null && !becameFullyRead) return;
            Task.Run(async delegate { await RunAlertActionsAsync(unreadCount, message); });
        }

        private static void CheckRepeatingAlertBeep()
        {
            if (!_settings.EnableNewMessageBeep || _settings.Alerts.RepeatBeepIntervalSeconds <= 0) return;
            var now = DateTime.UtcNow;
            if (now < _nextRepeatedAlertCheckUtc) return;
            _nextRepeatedAlertCheckUtc = now.AddSeconds(_settings.Alerts.RepeatBeepIntervalSeconds);
            try
            {
                if (_store.CountNewMessages() > 0) PlayAlertBeep();
            }
            catch { }
        }

        private static void PlayAlertBeep()
        {
            _lastAlertBeepUtc = DateTime.UtcNow;
            try { Console.Beep(); }
            catch { }
        }

        private static async Task RunAlertActionsAsync(int unreadCount, MeshMessage message)
        {
            var alerts = _settings.Alerts;
            if (!alerts.EnableHttpGet)
            {
                _lastAlertHttpRequest = "Alert HTTP GET is disabled in Settings > Alerts.";
                _lastAlertHttpOutput = "No HTTP request was sent.";
            }
            else if (String.IsNullOrWhiteSpace(alerts.HttpGetUrl))
            {
                _lastAlertHttpRequest = "Alert HTTP GET has no URL configured.";
                _lastAlertHttpOutput = "No HTTP request was sent.";
            }
            else
            {
                try
                {
                    var url = BuildAlertUrl(alerts.HttpGetUrl, unreadCount, message);
                    _lastAlertHttpRequest = url;
                    _lastAlertHttpOutput = await AlertHttpClient.GetStringAsync(url) ?? "";
                }
                catch (Exception ex) { _lastAlertHttpOutput = ex.ToString(); }
            }
            if (!alerts.EnableExecutable)
            {
                _lastAlertProcessRequest = "Alert shell command is disabled in Settings > Alerts.";
                _lastAlertProcessOutput = "No shell command was started.";
            }
            else if (String.IsNullOrWhiteSpace(alerts.ExecutablePath))
            {
                _lastAlertProcessRequest = "Alert shell command has no command configured.";
                _lastAlertProcessOutput = "No shell command was started.";
            }
            else
            {
                var arguments = new[] { unreadCount.ToString(CultureInfo.InvariantCulture), message == null ? "" : (message.Text ?? "") };
                _lastAlertProcessRequest = BuildAlertShellDebugCommand(alerts.ExecutablePath, arguments);
                _lastAlertProcessOutput = await RunAlertProcessAsync(alerts.ExecutablePath, arguments);
            }
        }

        private static string BuildAlertUrl(string baseUrl, int unreadCount, MeshMessage message)
        {
            var node = message == null ? null : _mesh.Nodes.FirstOrDefault(item => item.Number == message.From);
            var query = new Dictionary<string, string>
            {
                { "event", message == null ? "allRead" : "newMessage" },
                { "unreadCount", unreadCount.ToString(CultureInfo.InvariantCulture) },
                { "message", message == null ? "" : (message.Text ?? "") },
                { "deviceId", _mesh.Device.NodeId ?? "" }
            };
            if (message != null)
            {
                query["packetId"] = message.Packet.Id.ToString(CultureInfo.InvariantCulture);
                query["from"] = message.From.ToString(CultureInfo.InvariantCulture);
                query["to"] = message.To.ToString(CultureInfo.InvariantCulture);
                query["isChannelMessage"] = message.IsChannelMessage ? "true" : "false";
                query["isDirectMessage"] = message.IsDirectMessage ? "true" : "false";
                query["channel"] = message.ChannelIndex.HasValue ? message.ChannelIndex.Value.ToString(CultureInfo.InvariantCulture) : "";
                var channel = message.ChannelIndex.HasValue ? _mesh.Channels.FirstOrDefault(item => item.Index == (int)message.ChannelIndex.Value) : null;
                query["channelName"] = channel == null ? "" : (channel.Name ?? "");
                query["receivedUtc"] = message.ReceivedAtUtc.ToString("o", CultureInfo.InvariantCulture);
                query["rssi"] = message.Packet.RxRssi.ToString(CultureInfo.InvariantCulture);
                query["snr"] = message.Packet.RxSnr.ToString(CultureInfo.InvariantCulture);
                query["hopLimit"] = message.Packet.HopLimit.ToString(CultureInfo.InvariantCulture);
                query["nodeId"] = node == null ? "!" + message.From.ToString("x8") : (node.Id ?? "");
                query["nodeName"] = node == null ? "" : (node.LongName ?? "");
                query["nodeShortName"] = node == null ? "" : (node.ShortName ?? "");
                query["latitude"] = node == null || !node.Latitude.HasValue ? "" : node.Latitude.Value.ToString(CultureInfo.InvariantCulture);
                query["longitude"] = node == null || !node.Longitude.HasValue ? "" : node.Longitude.Value.ToString(CultureInfo.InvariantCulture);
                query["battery"] = node == null || !node.BatteryLevel.HasValue ? "" : node.BatteryLevel.Value.ToString(CultureInfo.InvariantCulture);
                query["hops"] = node == null || !node.HopsAway.HasValue ? "" : node.HopsAway.Value.ToString(CultureInfo.InvariantCulture);
            }
            var separator = baseUrl.Contains("?") ? "&" : "?";
            return baseUrl + separator + String.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value ?? "")));
        }

        private static string BuildAlertShellDebugCommand(string command, IEnumerable<string> arguments)
        {
            var commandArguments = arguments.ToArray();
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") + " /C " + command + " " + String.Join(" ", commandArguments.Select(QuoteProcessArgument));
            return "/bin/sh -c " + QuoteShellArgument(command + " \"$@\"") + " -- " + String.Join(" ", commandArguments.Select(QuoteShellArgument));
        }

        private static async Task<string> RunAlertProcessAsync(string command, IEnumerable<string> arguments)
        {
            var commandArguments = arguments.ToArray();
            try
            {
                using (var process = new Process())
                {
                    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    {
                        process.StartInfo = new ProcessStartInfo { FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", Arguments = "/C " + command + " " + String.Join(" ", commandArguments.Select(QuoteProcessArgument)), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true };
                    }
                    else
                    {
                        process.StartInfo = new ProcessStartInfo { FileName = "/bin/sh", Arguments = "-c " + QuoteShellArgument(command + " \"$@\"") + " -- " + String.Join(" ", commandArguments.Select(QuoteShellArgument)), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true };
                    }
                    process.Start();
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(30000)) { try { process.Kill(); } catch { } return "Alert shell command timed out."; }
                    var output = (await stdout).Trim();
                    var error = (await stderr).Trim();
                    return String.IsNullOrWhiteSpace(error) ? output : (String.IsNullOrWhiteSpace(output) ? error : output + Environment.NewLine + error);
                }
            }
            catch (Exception ex) { return "Alert shell command error: " + ex.Message; }
        }

        private static string QuoteShellArgument(string value)
        {
            return "'" + (value ?? "").Replace("'", "'\"'\"'") + "'";
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
            var propertyNames = new[] { "Background", "Frame", "Page frame", "Normal text", "Sent messages", "Received messages", "Status text", "Input background", "Input text", "Menu background", "Menu text", "Button background", "Button text", "Logo frame", "Logo text", "Emoji text", "Map nodes", "Map clusters" };
            var values = new[] { appearance.BackgroundColor, appearance.FrameColor, appearance.PageFrameColor, appearance.TextColor, appearance.SentMessageColor, appearance.ReceivedMessageColor, appearance.StatusTextColor, appearance.InputBackgroundColor, appearance.InputTextColor, appearance.MenuBackgroundColor, appearance.MenuTextColor, appearance.ButtonBackgroundColor, appearance.ButtonTextColor, appearance.LogoFrameColor, appearance.LogoTextColor, appearance.EmojiTextColor, appearance.MapNodeColor, appearance.MapClusterColor };
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
                appearance.BackgroundColor = values[0]; appearance.FrameColor = values[1]; appearance.PageFrameColor = values[2]; appearance.TextColor = values[3]; appearance.SentMessageColor = values[4]; appearance.ReceivedMessageColor = values[5]; appearance.StatusTextColor = values[6]; appearance.InputBackgroundColor = values[7]; appearance.InputTextColor = values[8]; appearance.MenuBackgroundColor = values[9]; appearance.MenuTextColor = values[10]; appearance.ButtonBackgroundColor = values[11]; appearance.ButtonTextColor = values[12]; appearance.LogoFrameColor = values[13]; appearance.LogoTextColor = values[14]; appearance.EmojiTextColor = values[15]; appearance.MapNodeColor = values[16]; appearance.MapClusterColor = values[17];
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
            var mapNode = ParseColor(appearance.MapNodeColor, Color.BrightCyan);
            var mapCluster = ParseColor(appearance.MapClusterColor, Color.BrightMagenta);
            var normal = Application.Driver.MakeAttribute(text, background);
            var focus = Application.Driver.MakeAttribute(background, frame);
            var statusScheme = new ColorScheme { Normal = Application.Driver.MakeAttribute(status, background), Focus = normal, HotNormal = normal, HotFocus = focus, Disabled = normal };
            var inputScheme = new ColorScheme { Normal = Application.Driver.MakeAttribute(inputText, inputBackground), Focus = Application.Driver.MakeAttribute(inputText, inputBackground), HotNormal = normal, HotFocus = normal, Disabled = normal };
            var buttonScheme = new ColorScheme { Normal = Application.Driver.MakeAttribute(buttonText, buttonBackground), Focus = Application.Driver.MakeAttribute(buttonBackground, buttonText), HotNormal = Application.Driver.MakeAttribute(buttonText, buttonBackground), HotFocus = Application.Driver.MakeAttribute(buttonBackground, buttonText), Disabled = normal };
            Colors.Base.Normal = normal; Colors.Base.Focus = focus; Colors.Base.HotNormal = normal; Colors.Base.HotFocus = focus; Colors.Base.Disabled = normal;
            Colors.TopLevel.Normal = normal; Colors.TopLevel.Focus = focus; Colors.TopLevel.HotNormal = normal; Colors.TopLevel.HotFocus = focus; Colors.TopLevel.Disabled = normal;
            Colors.Menu.Normal = Application.Driver.MakeAttribute(menuText, menuBackground); Colors.Menu.Focus = Application.Driver.MakeAttribute(menuBackground, menuText); Colors.Menu.HotNormal = Application.Driver.MakeAttribute(menuText, menuBackground); Colors.Menu.HotFocus = Application.Driver.MakeAttribute(menuBackground, menuText); Colors.Menu.Disabled = normal;
            foreach (var view in Frames) { view.Border.BorderBrush = frame; view.Border.Background = background; view.SetNeedsDisplay(); }
            if (_messageCharacterCounter != null) _messageCharacterCounter.ColorScheme = new ColorScheme { Normal = Application.Driver.MakeAttribute(frame, background), Focus = normal, HotNormal = normal, HotFocus = focus, Disabled = normal };
            foreach (var page in PageFrames) { page.Border.BorderBrush = pageFrame; page.Border.Background = background; page.SetNeedsDisplay(); }
            foreach (var button in MainButtons) button.ColorScheme = buttonScheme;
            if (_logoFrame != null) { _logoFrame.Border.BorderBrush = logoFrame; _logoFrame.Border.Background = background; _logoFrame.SetNeedsDisplay(); }
            if (_logoText != null) _logoText.ColorScheme = new ColorScheme { Normal = Application.Driver.MakeAttribute(logoText, background), Focus = normal, HotNormal = normal, HotFocus = focus, Disabled = normal };
            _messages.BackgroundColor = background; _messages.NormalTextColor = text; _messages.OutgoingColor = sent; _messages.IncomingColor = received; _messages.EmojiTextColor = emojiText;
            if (_nodeMap != null) { _nodeMap.NodeColor = mapNode; _nodeMap.ClusterColor = mapCluster; _nodeMap.SetNeedsDisplay(); }
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
            UpdateMessageCharacterCounter();
            var info = _mesh.ConnectionInfo;
            var device = _mesh.Device;
            var position = device.Latitude.HasValue && device.Longitude.HasValue ? device.Latitude.Value.ToString("F5", CultureInfo.InvariantCulture) + ", " + device.Longitude.Value.ToString("F5", CultureInfo.InvariantCulture) : "-";
            var connectionState = _mesh.State.ToString();
            if (_mesh.State == ConnectionState.Disconnected)
            {
                connectionState = _disconnectedStatusVisible ? "Disconnected" : "            ";
                _disconnectedStatusVisible = !_disconnectedStatusVisible;
            }
            else _disconnectedStatusVisible = true;
            var reconnectStatus = "";
            var nextReconnectUtc = _mesh.NextReconnectUtc;
            if (_settings.Connection.AutomaticReconnect && nextReconnectUtc.HasValue)
            {
                var remaining = Math.Max(0, (int)Math.Ceiling((nextReconnectUtc.Value - DateTime.UtcNow).TotalSeconds));
                reconnectStatus = " | reconnect in " + remaining.ToString(CultureInfo.InvariantCulture) + "s";
            }
            _status.Text = " F10 Menu | " + DateTime.Now.ToString("HH:mm:ss") + " | Connection: " + connectionState + reconnectStatus + " | " + (info.Transport ?? "No connection") + " | Node: " + (device.NodeId ?? "-") + " | Battery: " + (device.BatteryLevel.HasValue ? device.BatteryLevel.Value + "%" : "-") + " | GPS: " + position;
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
