using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Meshtastic.Client;
using Terminal.Gui;

namespace ConsoleClient
{
    internal sealed class NodeMapView : View
    {
        private sealed class Marker
        {
            public readonly List<StoredMeshNode> Nodes = new List<StoredMeshNode>();
            public int X, Y;
            public double Latitude, Longitude;
            public string Label, Key;
            public MapOverlayPoint Overlay;
            public Color Color;
        }

        private readonly List<StoredMeshNode> _nodes = new List<StoredMeshNode>();
        private readonly List<MapOverlayPoint> _overlayPoints = new List<MapOverlayPoint>();
        private readonly List<Marker> _markers = new List<Marker>();
        private double? _ownLatitude, _ownLongitude;
        private double _centerLatitude, _centerLongitude, _metersPerRow = 1000d;
        private string _selectedKey;

        public Action<StoredMeshNode> NodeActivated { get; set; }
        public Action<StoredMeshNode> SelectionChanged { get; set; }
        public Action<MapOverlayPoint> OverlaySelectionChanged { get; set; }
        public Action<MapOverlayPoint> OverlayActivated { get; set; }
        public Action ViewChanged { get; set; }
        public Action<double, double> NewOverlayPointRequested { get; set; }
        public double CenterLatitude { get { return _centerLatitude; } }
        public double CenterLongitude { get { return _centerLongitude; } }
        public double MetersPerRow { get { return _metersPerRow; } }
        public Color NodeColor { get; set; } = Color.BrightCyan;
        public Color ClusterColor { get; set; } = Color.BrightMagenta;
        public Color GridColor { get; set; } = Color.DarkGray;
        public NodeMapView() { CanFocus = true; }

        public void SetData(IEnumerable<StoredMeshNode> source, double? latitude, double? longitude)
        {
            _nodes.Clear();
            _nodes.AddRange(source.Where(HasPosition));
            _ownLatitude = ValidLatitude(latitude) ? latitude : null;
            _ownLongitude = ValidLongitude(longitude) ? longitude : null;
            Center();
        }

        public void SetOverlayPoints(IEnumerable<MapOverlayPoint> points)
        {
            _overlayPoints.Clear();
            if (points != null) _overlayPoints.AddRange(points.Where(point => point != null && ValidLatitude(point.Latitude) && ValidLongitude(point.Longitude)));
            SetNeedsDisplay();
        }
        public void Center()
        {
            if (_ownLatitude.HasValue && _ownLongitude.HasValue)
            {
                _centerLatitude = _ownLatitude.Value;
                _centerLongitude = _ownLongitude.Value;
            }
            else if (_nodes.Count > 0)
            {
                _centerLatitude = _nodes.Average(node => node.Latitude.Value);
                _centerLongitude = _nodes.Average(node => node.Longitude.Value);
            }
            NotifyViewChanged();
            SetNeedsDisplay();
        }

        public override bool ProcessKey(KeyEvent e)
        {
            if (e.Key == (Key)'+' || e.Key == (Key)'=') { _metersPerRow = Math.Max(1d, _metersPerRow / 2d); NotifyViewChanged(); SetNeedsDisplay(); return true; }
            if (e.Key == (Key)'-') { _metersPerRow = Math.Min(2000000d, _metersPerRow * 2d); NotifyViewChanged(); SetNeedsDisplay(); return true; }
            if (e.Key == (Key)'c' || e.Key == (Key)'C') { Center(); return true; }
            if (e.Key == Key.Enter) { ActivateSelection(); return true; }
            if (e.Key == (Key)'n' || e.Key == (Key)'N') { if (NewOverlayPointRequested != null) NewOverlayPointRequested(_centerLatitude, _centerLongitude); return true; }

            // Plain letter controls are transported consistently by SSH clients.
            // Upper-case letters use the larger step.
            var characterKey = e.Key & ~Key.ShiftMask & ~Key.CtrlMask & ~Key.AltMask;
            var character = (char)characterKey;
            var panDx = character == 'a' || character == 'A' ? -1 : character == 'd' || character == 'D' ? 1 : 0;
            var panDy = character == 'w' || character == 'W' ? -1 : character == 's' || character == 'S' ? 1 : 0;
            if (panDx != 0 || panDy != 0)
            {
                var largeStep = Char.IsUpper(character);
                Pan(panDx, panDy, largeStep ? 20 : 1, largeStep ? 10 : 1);
                return true;
            }
            var dx = e.Key == Key.CursorLeft ? -1 : e.Key == Key.CursorRight ? 1 : 0;
            var dy = e.Key == Key.CursorUp ? -1 : e.Key == Key.CursorDown ? 1 : 0;
            if (dx == 0 && dy == 0) return base.ProcessKey(e);
            SelectVisibleMarker(dx, dy);
            return true;
        }

        public override bool MouseEvent(MouseEvent e)
        {
            if (e.Flags.HasFlag(MouseFlags.WheeledUp)) { _metersPerRow = Math.Max(1d, _metersPerRow / 2d); NotifyViewChanged(); SetNeedsDisplay(); return true; }
            if (e.Flags.HasFlag(MouseFlags.WheeledDown)) { _metersPerRow = Math.Min(2000000d, _metersPerRow * 2d); NotifyViewChanged(); SetNeedsDisplay(); return true; }
            if (e.Flags.HasFlag(MouseFlags.Button3Clicked))
            {
                double latitude, longitude; Unproject(e.X, e.Y, Bounds.Width, Bounds.Height, out latitude, out longitude); CenterOn(latitude, longitude); return true;
            }
            if (!e.Flags.HasFlag(MouseFlags.Button1Clicked) && !e.Flags.HasFlag(MouseFlags.Button1DoubleClicked)) return base.MouseEvent(e);
            var marker = _markers.Where(item => item.Overlay == null || item.Overlay.Selectable).OrderBy(item => Math.Abs(item.X - e.X) + Math.Abs(item.Y - e.Y) * 2).FirstOrDefault();
            if (marker == null || Math.Abs(marker.Y - e.Y) > 1 || Math.Abs(marker.X - e.X) > Math.Max(2, marker.Label.Length)) return true;
            SetSelection(marker);
            SetFocus();
            SetNeedsDisplay();
            if (e.Flags.HasFlag(MouseFlags.Button1DoubleClicked)) ActivateSelection();
            return true;
        }

        public override void Redraw(Rect bounds)
        {
            base.Redraw(bounds);
            var width = Bounds.Width;
            var height = Bounds.Height;
            Application.Driver.SetAttribute(Application.Driver.MakeAttribute(Color.Gray, Color.Black));
            for (var y = 0; y < height; y++) { Move(0, y); Application.Driver.AddStr(new string(' ', width)); }
            if (width < 8 || height < 4) return;

            Application.Driver.SetAttribute(Application.Driver.MakeAttribute(GridColor, Color.Black));
            for (var y = height / 2 % 5; y < height; y += 5)
                for (var x = width / 2 % 10; x < width; x += 10) { Move(x, y); Application.Driver.AddRune('.'); }

            DrawCenterCrosshair(width, height);
            BuildVisibleMarkers(width, height);
            AddOverlayMarkers(width, height);
            EnsureVisibleSelection(width, height);
            foreach (var marker in _markers)
            {
                var selected = marker.Key == _selectedKey;
                var markerColor = marker.Overlay != null ? marker.Color : marker.Nodes.Count > 1 ? ClusterColor : NodeColor;
                Draw(marker.X, marker.Y, marker.Label, selected ? Color.Black : markerColor, selected ? markerColor : Color.Black, width);
            }

            if (_ownLatitude.HasValue && _ownLongitude.HasValue)
            {
                int x, y;
                Project(_ownLatitude.Value, _ownLongitude.Value, width, height, out x, out y);
                Draw(x, y, "[YOU]", Color.BrightGreen, Color.Black, width);
            }

            var columns = Math.Min(12, Math.Max(4, width / 6));
            var meters = columns * _metersPerRow * .5d;
            var distance = meters < 1000d ? Math.Round(meters) + " m" : (meters / 1000d).ToString(meters < 10000d ? "F1" : "F0", CultureInfo.InvariantCulture) + " km";
            var scaleText = "|" + new string('-', columns - 2) + "| " + distance;
            var scaleLeft = Math.Max(0, width - scaleText.Length - 1);
            var coordinates = _centerLatitude.ToString("F5", CultureInfo.InvariantCulture) + ", " + _centerLongitude.ToString("F5", CultureInfo.InvariantCulture);
            var coordinatesLeft = Math.Max(0, scaleLeft - coordinates.Length - 1);
            Draw(coordinatesLeft + coordinates.Length / 2, height - 1, coordinates, Color.Gray, Color.Black, Math.Max(0, scaleLeft - 1));
            Draw(scaleLeft + scaleText.Length / 2, height - 1, scaleText, Color.Gray, Color.Black, width);
        }

        private void AddOverlayMarkers(int width, int height)
        {
            var occupied = new HashSet<string>();
            foreach (var marker in _markers)
                for (var column = marker.X - marker.Label.Length / 2 - 1; column <= marker.X + marker.Label.Length / 2 + 1; column++) occupied.Add(marker.Y + ":" + column);
            var index = 0;
            foreach (var point in _overlayPoints)
            {
                int x, y;
                Project(point.Latitude, point.Longitude, width, height, out x, out y);
                var label = SafeLabel(String.IsNullOrWhiteSpace(point.ShortName) ? "?" : point.ShortName.Trim());
                var left = x - label.Length / 2;
                if (left < 0 || left + label.Length >= width || y <= 0 || y >= height - 1) { index++; continue; }
                var collision = false;
                for (var row = y - 1; row <= y + 1 && !collision; row++)
                    for (var column = left - 1; column <= left + label.Length; column++)
                        if (occupied.Contains(row + ":" + column)) { collision = true; break; }
                if (collision) { index++; continue; }
                for (var column = left - 1; column <= left + label.Length; column++) occupied.Add(y + ":" + column);
                Color color;
                if (!Enum.TryParse(point.Color ?? "Green", true, out color)) color = Color.Green;
                _markers.Add(new Marker { X = x, Y = y, Latitude = point.Latitude, Longitude = point.Longitude, Label = label, Key = "o:" + index, Overlay = point, Color = color });
                index++;
            }
        }

        private void DrawCenterCrosshair(int width, int height)
        {
            var x = width / 2;
            var y = height / 2;
            Draw(x, y - 1, "|", GridColor, Color.Black, width);
            Draw(x, y, "--+--", GridColor, Color.Black, width);
            Draw(x, y + 1, "|", GridColor, Color.Black, width);
        }
        private void BuildVisibleMarkers(int width, int height)
        {
            var points = new List<Marker>();
            foreach (var node in _nodes)
            {
                int x, y;
                Project(node.Latitude.Value, node.Longitude.Value, width, height, out x, out y);
                if (x < 0 || x >= width || y <= 0 || y >= height - 1) continue;
                var marker = new Marker { X = x, Y = y, Latitude = node.Latitude.Value, Longitude = node.Longitude.Value, Label = String.IsNullOrWhiteSpace(node.ShortName) ? "!" + node.NodeNumber.ToString("x8") : SafeLabel(node.ShortName.Trim()), Key = "n:" + node.NodeNumber };
                marker.Nodes.Add(node);
                points.Add(marker);
            }

            _markers.Clear();
            while (points.Count > 0)
            {
                var seed = points[0];
                var group = points.Where(item => Math.Abs(item.Y - seed.Y) <= 1 && Math.Abs(item.X - seed.X) <= Math.Max(3, (item.Label.Length + seed.Label.Length) / 2 + 1)).ToList();
                foreach (var item in group) points.Remove(item);
                if (group.Count > 1 && _metersPerRow <= 1d)
                {
                    var stackX = (int)Math.Round(group.Average(item => item.X));
                    var stackY = Math.Max(1, Math.Min(height - group.Count - 1, (int)Math.Round(group.Average(item => item.Y)) - group.Count / 2));
                    for (var index = 0; index < group.Count; index++) { group[index].X = stackX; group[index].Y = stackY + index; _markers.Add(group[index]); }
                    continue;
                }
                if (group.Count == 1) { _markers.Add(seed); continue; }
                var cluster = new Marker
                {
                    X = (int)Math.Round(group.Average(item => item.X)),
                    Y = (int)Math.Round(group.Average(item => item.Y)),
                    Latitude = group.SelectMany(item => item.Nodes).Average(node => node.Latitude.Value),
                    Longitude = group.SelectMany(item => item.Nodes).Average(node => node.Longitude.Value),
                    Label = "[" + group.Sum(item => item.Nodes.Count) + "]"
                };
                cluster.Nodes.AddRange(group.SelectMany(item => item.Nodes));
                cluster.Key = "c:" + String.Join(",", cluster.Nodes.Select(node => node.NodeNumber).OrderBy(number => number));
                _markers.Add(cluster);
            }
        }

        private void EnsureVisibleSelection(int width, int height)
        {
            var selectableMarkers = _markers.Where(marker => marker.Overlay == null || marker.Overlay.Selectable).ToList();
            if (selectableMarkers.Any(marker => marker.Key == _selectedKey)) return;
            var centerX = width / 2;
            var centerY = height / 2;
            var nearest = selectableMarkers.OrderBy(marker => Math.Abs(marker.X - centerX) + Math.Abs(marker.Y - centerY) * 2).FirstOrDefault();
            if (nearest == null) { _selectedKey = null; NotifySelection(null); } else SetSelection(nearest);
        }

        private void SelectVisibleMarker(int dx, int dy)
        {
            var selectableMarkers = _markers.Where(marker => marker.Overlay == null || marker.Overlay.Selectable).ToList();
            if (selectableMarkers.Count == 0) return;
            var current = selectableMarkers.FirstOrDefault(marker => marker.Key == _selectedKey) ?? selectableMarkers[0];
            Marker best = null;
            var bestScore = Double.MaxValue;
            foreach (var candidate in selectableMarkers.Where(marker => marker != current))
            {
                var x = candidate.X - current.X;
                var y = candidate.Y - current.Y;
                var forward = x * dx + y * dy;
                if (forward <= 0) continue;
                var score = forward + Math.Abs(x * dy - y * dx) * 3d;
                if (score < bestScore) { bestScore = score; best = candidate; }
            }
            if (best != null) { SetSelection(best); SetNeedsDisplay(); }
        }

        private void ActivateSelection()
        {
            var marker = _markers.FirstOrDefault(item => item.Key == _selectedKey);
            if (marker == null) return;
            if (marker.Nodes.Count > 1)
            {
                _centerLatitude = marker.Latitude;
                _centerLongitude = marker.Longitude;
                NotifySelection(null);
                NotifyViewChanged();
                SetNeedsDisplay();
            }
            else if (marker.Overlay != null && OverlayActivated != null) OverlayActivated(marker.Overlay);
            else if (marker.Nodes.Count == 1 && NodeActivated != null) NodeActivated(marker.Nodes[0]);
        }

        private void SetSelection(Marker marker)
        {
            _selectedKey = marker.Key;
            if (marker.Overlay != null)
            {
                NotifySelection(null);
                if (OverlaySelectionChanged != null) OverlaySelectionChanged(marker.Overlay);
            }
            else
            {
                if (OverlaySelectionChanged != null) OverlaySelectionChanged(null);
                NotifySelection(marker.Nodes.Count == 1 ? marker.Nodes[0] : null);
            }
        }

        private void NotifySelection(StoredMeshNode node)
        {
            if (SelectionChanged != null) SelectionChanged(node);
        }
        public void UpdateOwnPosition(double? latitude, double? longitude, bool follow)
        {
            if (!ValidLatitude(latitude) || !ValidLongitude(longitude)) return;
            var changed = !_ownLatitude.HasValue || !_ownLongitude.HasValue || Math.Abs(_ownLatitude.Value - latitude.Value) > .0000001d || Math.Abs(_ownLongitude.Value - longitude.Value) > .0000001d;
            _ownLatitude = latitude; _ownLongitude = longitude;
            if (follow && (changed || Math.Abs(_centerLatitude - latitude.Value) > .0000001d || Math.Abs(_centerLongitude - longitude.Value) > .0000001d)) Center(); else if (changed) SetNeedsDisplay();
        }
        public void CenterOn(double latitude, double longitude)
        {
            if (!ValidLatitude(latitude) || !ValidLongitude(longitude)) return;
            _centerLatitude = latitude; _centerLongitude = longitude; NotifyViewChanged(); SetNeedsDisplay();
        }
        public void RestoreView(double latitude, double longitude, double metersPerRow)
        {
            if (latitude < -90d || latitude > 90d || longitude < -180d || longitude > 180d) return;
            _centerLatitude = latitude; _centerLongitude = longitude; _metersPerRow = Math.Max(1d, Math.Min(2000000d, metersPerRow)); SetNeedsDisplay();
        }

        private void NotifyViewChanged() { if (ViewChanged != null) ViewChanged(); }
        private void Pan(int dx, int dy, int horizontalCells, int verticalCells)
        {
            _centerLatitude -= dy * _metersPerRow * verticalCells / 111320d;
            _centerLongitude += dx * _metersPerRow * .5d * horizontalCells / (111320d * Math.Max(.01d, Math.Cos(_centerLatitude * Math.PI / 180d)));
            NotifyViewChanged();
            SetNeedsDisplay();
        }

        public string GetMapText()
        {
            var width = Math.Max(8, Bounds.Width);
            var height = Math.Max(4, Bounds.Height);
            var rows = new char[height][];
            for (var y = 0; y < height; y++) rows[y] = Enumerable.Repeat(' ', width).ToArray();
            Action<int, int, string> putCentered = delegate(int x, int y, string text)
            {
                if (String.IsNullOrEmpty(text) || y < 0 || y >= height) return;
                var left = x - text.Length / 2;
                for (var index = 0; index < text.Length; index++) if (left + index >= 0 && left + index < width) rows[y][left + index] = text[index];
            };
            for (var y = height / 2 % 5; y < height; y += 5) for (var x = width / 2 % 10; x < width; x += 10) rows[y][x] = '.';
            putCentered(width / 2, height / 2 - 1, "|"); putCentered(width / 2, height / 2, "--+--"); putCentered(width / 2, height / 2 + 1, "|");
            foreach (var marker in _markers) putCentered(marker.X, marker.Y, marker.Label);
            if (_ownLatitude.HasValue && _ownLongitude.HasValue) { int x, y; Project(_ownLatitude.Value, _ownLongitude.Value, width, height, out x, out y); putCentered(x, y, "[YOU]"); }
            var columns = Math.Min(12, Math.Max(4, width / 6)); var meters = columns * _metersPerRow * .5d;
            var distance = meters < 1000d ? Math.Round(meters) + " m" : (meters / 1000d).ToString(meters < 10000d ? "F1" : "F0", CultureInfo.InvariantCulture) + " km";
            var scaleText = "|" + new string('-', columns - 2) + "| " + distance; var scaleLeft = Math.Max(0, width - scaleText.Length - 1);
            var coordinates = _centerLatitude.ToString("F5", CultureInfo.InvariantCulture) + ", " + _centerLongitude.ToString("F5", CultureInfo.InvariantCulture);
            var coordinatesLeft = Math.Max(0, scaleLeft - coordinates.Length - 1);
            for (var index = 0; index < coordinates.Length && coordinatesLeft + index < Math.Max(0, scaleLeft - 1); index++) rows[height - 1][coordinatesLeft + index] = coordinates[index];
            for (var index = 0; index < scaleText.Length && scaleLeft + index < width; index++) rows[height - 1][scaleLeft + index] = scaleText[index];
            return String.Join(Environment.NewLine, rows.Select(row => new string(row).TrimEnd()));
        }
        private void Unproject(int x, int y, int width, int height, out double latitude, out double longitude)
        {
            latitude = _centerLatitude + (height / 2d - y) * _metersPerRow / 111320d;
            longitude = _centerLongitude + (x - width / 2d) * _metersPerRow * .5d / (111320d * Math.Max(.01d, Math.Cos(_centerLatitude * Math.PI / 180d)));
        }
        private void Project(double latitude, double longitude, int width, int height, out int x, out int y)
        {
            var east = (longitude - _centerLongitude) * 111320d * Math.Max(.01d, Math.Cos(_centerLatitude * Math.PI / 180d));
            x = width / 2 + (int)Math.Round(east / (_metersPerRow * .5d));
            y = height / 2 - (int)Math.Round((latitude - _centerLatitude) * 111320d / _metersPerRow);
        }

        private void Draw(int x, int y, string text, Color foreground, Color background, int width)
        {
            x -= text.Length / 2;
            if (y < 0 || y >= Bounds.Height || x >= width) return;
            if (x < 0) { if (-x >= text.Length) return; text = text.Substring(-x); x = 0; }
            if (x + text.Length > width) text = text.Substring(0, width - x);
            Move(x, y);
            Application.Driver.SetAttribute(Application.Driver.MakeAttribute(foreground, background));
            Application.Driver.AddStr(text);
        }

        private static string SafeLabel(string text)
        {
            var result = new System.Text.StringBuilder();
            var elements = StringInfo.GetTextElementEnumerator(text ?? "");
            while (elements.MoveNext())
            {
                var element = (string)elements.Current;
                var category = CharUnicodeInfo.GetUnicodeCategory(element, 0);
                var graphical = category == UnicodeCategory.OtherSymbol || category == UnicodeCategory.MathSymbol || category == UnicodeCategory.ModifierSymbol || Char.IsSurrogate(element[0]);
                result.Append(graphical ? "?" : element);
            }
            return result.ToString();
        }
        private static bool HasPosition(StoredMeshNode node) { return node != null && ValidLatitude(node.Latitude) && ValidLongitude(node.Longitude); }
        private static bool ValidLatitude(double? value) { return value.HasValue && !Double.IsNaN(value.Value) && value.Value >= -90d && value.Value <= 90d; }
        private static bool ValidLongitude(double? value) { return value.HasValue && !Double.IsNaN(value.Value) && value.Value >= -180d && value.Value <= 180d; }
    }
}




