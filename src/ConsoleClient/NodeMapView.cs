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
            public MapPositionMarker Position;
            public Color Color;
        }

        private readonly Dictionary<uint, MapPositionMarker> _positionMarkers = new Dictionary<uint, MapPositionMarker>();
        private readonly List<StoredMeshNode> _nodes = new List<StoredMeshNode>();
        private readonly List<MapOverlayPoint> _overlayPoints = new List<MapOverlayPoint>();
        private readonly List<Marker> _markers = new List<Marker>();
        private readonly Dictionary<string, MapOverlayPoint> _shapeHitCells = new Dictionary<string, MapOverlayPoint>();
        private readonly OfflineMapProvider _offlineMap = OfflineMapProvider.TryOpenDefault();
        private double? _ownLatitude, _ownLongitude;
        private double _centerLatitude, _centerLongitude, _metersPerRow = 1000d;
        private double? _measurementLatitude, _measurementLongitude;
        private int _measurementMode;
        private char[][] _mapTextRows;
        private string _selectedKey;
        private MapOverlayPoint _selectedShapeOverlay;
        private bool _selectionSuppressed;
        private bool _selectCrosshairAfterViewChange;
        private Color[,] _backgroundColors;
        private int _backgroundWidth, _backgroundHeight;
        private double _backgroundLatitude, _backgroundLongitude, _backgroundMetersPerRow;

        public Action PositionMarkerListRequested { get; set; }
        public Action<double, double> OwnPositionMarkerRequested { get; set; }
        public Action<MapPositionMarker> PositionMarkerSelected { get; set; }
        public Action<MapPositionMarker> PositionMarkerActivated { get; set; }
        public Action<StoredMeshNode> NodeActivated { get; set; }
        public Action<IReadOnlyList<StoredMeshNode>> ClusterActivated { get; set; }
        public Action<StoredMeshNode> SelectionChanged { get; set; }
        public Action<MapOverlayPoint> OverlaySelectionChanged { get; set; }
        public Action<MapOverlayPoint> OverlayActivated { get; set; }
        public Action<string> MapFeatureSelectionChanged { get; set; }
        public Action ViewChanged { get; set; }
        public Action HelpRequested { get; set; }
        public Action<int> OverlayToggleRequested { get; set; }
        public Action BackgroundMapToggleRequested { get; set; }
        public Action PositionHistoryToggleRequested { get; set; }
        public Action KnownNodesToggleRequested { get; set; }
        public Action<double, double> NewOverlayPointRequested { get; set; }
        public double CenterLatitude { get { return _centerLatitude; } }
        public double CenterLongitude { get { return _centerLongitude; } }
        public double MetersPerRow { get { return _metersPerRow; } }
        public bool HasMeasurementPoint { get { return _measurementLatitude.HasValue && _measurementLongitude.HasValue; } }
        public string MeasurementShape { get { return _measurementMode == 1 ? "Line" : _measurementMode == 2 ? "Circle" : _measurementMode == 3 ? "Rectangle" : "Point"; } }
        public double MeasurementLatitude { get { return _measurementLatitude.GetValueOrDefault(); } }
        public double MeasurementLongitude { get { return _measurementLongitude.GetValueOrDefault(); } }
        public Color NodeColor { get; set; } = Color.BrightCyan;
        public Color ClusterColor { get; set; } = Color.BrightMagenta;
        public Color GridColor { get; set; } = Color.DarkGray;
        public bool ShowKnownNodes { get; set; } = true;
        public bool HopLimitEnabled { get; set; }
        public int HopLimit { get; set; }
        public bool ShowBackgroundMap { get; set; } = true;
        public bool HasBackgroundMap { get { return _offlineMap != null; } }
        public NodeMapView() { CanFocus = true; }

        public void SetData(IEnumerable<StoredMeshNode> source, double? latitude, double? longitude)
        {
            _nodes.Clear();
            _nodes.AddRange(source.Where(HasPosition).Where(node => !HopLimitEnabled || (node.HopsAway.HasValue && node.HopsAway.Value <= HopLimit)));
            _ownLatitude = ValidLatitude(latitude) ? latitude : null;
            _ownLongitude = ValidLongitude(longitude) ? longitude : null;
            Center(false);
        }

        public void SetOverlayPoints(IEnumerable<MapOverlayPoint> points)
        {
            _overlayPoints.Clear();
            if (points != null) _overlayPoints.AddRange(points.Where(point => point != null && ValidLatitude(point.Latitude) && ValidLongitude(point.Longitude)));
            SetNeedsDisplay();
        }
        public void Center() { Center(true); }
        private void Center(bool updateCrosshairSelection)
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
            if (updateCrosshairSelection) ClearSelectionAndShowCrosshair();
            NotifyViewChanged();
            SetNeedsDisplay();
        }

        public override bool ProcessKey(KeyEvent e)
        {
            if (e.Key == (Key)'+' || e.Key == (Key)'=') { _metersPerRow = Math.Max(1d, _metersPerRow / 2d); NotifyViewChanged(); SetNeedsDisplay(); return true; }
            if (e.Key == (Key)'-') { _metersPerRow = Math.Min(2000000d, _metersPerRow * 2d); NotifyViewChanged(); SetNeedsDisplay(); return true; }
            if (e.Key == (Key)'c' || e.Key == (Key)'C') { Center(); return true; }
            if (e.Key == Key.Enter) { ActivateSelection(); return true; }
            if (e.Key == (Key)' ') { CenterSelectedItem(); return true; }
            if (e.Key == (Key)'l' || e.Key == (Key)'L') { if (PositionMarkerListRequested != null) PositionMarkerListRequested(); return true; }
            if (e.Key == (Key)'o' || e.Key == (Key)'O') { if (OwnPositionMarkerRequested != null) OwnPositionMarkerRequested(_centerLatitude, _centerLongitude); return true; }
            if (e.Key == (Key)'n' || e.Key == (Key)'N') { if (NewOverlayPointRequested != null) NewOverlayPointRequested(_centerLatitude, _centerLongitude); return true; }
            if (e.Key == (Key)'m' || e.Key == (Key)'M')
            {
                if (_measurementMode == 0) { _measurementLatitude = _centerLatitude; _measurementLongitude = _centerLongitude; }
                _measurementMode = (_measurementMode + 1) % 4;
                if (_measurementMode == 0) ResetMeasurement();
                ShowMeasurementInfo();
                SetNeedsDisplay(); return true;
            }
            if (e.Key == (Key)'h' || e.Key == (Key)'H') { if (HelpRequested != null) HelpRequested(); return true; }
            if (e.Key >= (Key)'1' && e.Key <= (Key)'7') { if (OverlayToggleRequested != null) OverlayToggleRequested((int)e.Key - (int)(Key)'1'); return true; }
            if (e.Key == (Key)'8') { if (BackgroundMapToggleRequested != null) BackgroundMapToggleRequested(); return true; }
            if (e.Key == (Key)'9') { if (PositionHistoryToggleRequested != null) PositionHistoryToggleRequested(); return true; }
            if (e.Key == (Key)'0') { if (KnownNodesToggleRequested != null) KnownNodesToggleRequested(); return true; }
            if (e.Key == (Key)'f' || e.Key == (Key)'F') return SelectAdjacentTelemetryPosition(1) || base.ProcessKey(e);
            if (e.Key == (Key)'r' || e.Key == (Key)'R') return SelectAdjacentTelemetryPosition(-1) || base.ProcessKey(e);

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
            var marker = _markers.LastOrDefault(item => item.Position != null && ((item.Y == e.Y && Math.Abs(item.X - e.X) <= 2) || (item.X == e.X && Math.Abs(item.Y - e.Y) == 1))) ??
                _markers.Where(item => item.Overlay == null || item.Overlay.Selectable).OrderBy(item => Math.Abs(item.X - e.X) + Math.Abs(item.Y - e.Y) * 2).FirstOrDefault();
            if (marker == null || Math.Abs(marker.Y - e.Y) > 1 || Math.Abs(marker.X - e.X) > Math.Max(2, marker.Label.Length))
            {
                double latitude, longitude;
                Unproject(e.X, e.Y, Bounds.Width, Bounds.Height, out latitude, out longitude);
                SelectMapFeature(latitude, longitude, "Offline map");
                SetFocus();
                return true;
            }
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
            DrawBackground(width, height);
            if (width < 8 || height < 4) return;

            for (var y = height / 2 % 5; y < height; y += 5)
                for (var x = width / 2 % 10; x < width; x += 10) Draw(x, y, ".", GridColor, Color.Black, width);

            DrawCenterCrosshair(width, height);
            DrawOverlayShapes(width, height);
            BuildVisibleMarkers(width, height);
            AddOverlayMarkers(width, height);
            AddPositionMarkers(width, height);
            if (_selectCrosshairAfterViewChange) SelectCrosshairItem(width, height);
            EnsureVisibleSelection(width, height);
            foreach (var marker in _markers.Where(item => item.Position == null).OrderBy(item => item.Overlay != null && String.Equals(item.Overlay.Shape, "Point", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            {
                var selected = marker.Key == _selectedKey;
                var markerColor = marker.Overlay != null ? marker.Color : marker.Nodes.Count > 1 ? ClusterColor : NodeColor;
                if (selected) DrawSelected(marker.X, marker.Y, marker.Label, markerColor, Color.Black, width);
                else Draw(marker.X, marker.Y, marker.Label, markerColor, Color.Black, width);
            }

            if (_ownLatitude.HasValue && _ownLongitude.HasValue)
            {
                int x, y;
                Project(_ownLatitude.Value, _ownLongitude.Value, width, height, out x, out y);
                Draw(x, y, "[YOU]", Color.BrightGreen, Color.Black, width);
            }
            DrawMeasurementPreview(width, height);
            if (HasMeasurementPoint) ShowMeasurementInfo();

            var columns = Math.Min(12, Math.Max(4, width / 6));
            var meters = columns * _metersPerRow * .5d;
            var distance = meters < 1000d ? Math.Round(meters) + " m" : (meters / 1000d).ToString(meters < 10000d ? "F1" : "F0", CultureInfo.InvariantCulture) + " km";
            var scaleText = "|" + new string('-', columns - 2) + "| " + distance;
            var scaleLeft = Math.Max(0, width - scaleText.Length - 1);
            var coordinates = _centerLatitude.ToString("F5", CultureInfo.InvariantCulture) + ", " + _centerLongitude.ToString("F5", CultureInfo.InvariantCulture);
            var coordinatesLeft = Math.Max(0, scaleLeft - coordinates.Length - 1);
            Draw(coordinatesLeft + coordinates.Length / 2, height - 1, coordinates, Color.Gray, Color.Black, Math.Max(0, scaleLeft - 1));
            Draw(scaleLeft + scaleText.Length / 2, height - 1, scaleText, Color.Gray, Color.Black, width);
            DrawPositionMarkers(width, height);
        }



        public IReadOnlyList<MapPositionMarker> GetPositionMarkers()
        {
            return _positionMarkers.Values.OrderBy(marker => marker.CallSign, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        public MapPositionMarker GetPositionMarker(uint number)
        {
            MapPositionMarker marker; return _positionMarkers.TryGetValue(number, out marker) ? marker : null;
        }
        public void RemovePositionMarker(uint number)
        {
            _positionMarkers.Remove(number);
            _markers.RemoveAll(marker => marker.Position != null && marker.Position.NodeNumber == number);
            if (_selectedKey == "p:" + number) { ClearSelectionAndShowCrosshair(); if (MapFeatureSelectionChanged != null) MapFeatureSelectionChanged(""); }
            SetNeedsDisplay();
        }
        public void SelectPositionMarker(uint number)
        {
            var marker = GetPositionMarker(number); if (marker == null) return;
            _centerLatitude = marker.Latitude; _centerLongitude = marker.Longitude;
            _selectedKey = "p:" + number; _selectedShapeOverlay = null;
            _selectionSuppressed = false; _selectCrosshairAfterViewChange = false;
            NotifyViewChanged(); RefreshPositionMarkerInfo(); SetNeedsDisplay();
        }
        public void RefreshPositionMarkerInfo()
        {
            var marker = _positionMarkers.Values.FirstOrDefault(item => _selectedKey == "p:" + item.NodeNumber);
            if (marker != null && PositionMarkerSelected != null) PositionMarkerSelected(marker);
        }

        public void SetPositionMarker(MapPositionMarker marker)
        {
            if (marker == null) return;
            _positionMarkers[marker.NodeNumber] = marker;
            if (_selectedKey == "p:" + marker.NodeNumber && PositionMarkerSelected != null) PositionMarkerSelected(marker);
            SetNeedsDisplay();
        }
        private void AddPositionMarkers(int width, int height)
        {
            foreach (var position in _positionMarkers.Values)
            {
                int x, y; Project(position.Latitude, position.Longitude, width, height, out x, out y);
                if (x < -2 || x >= width + 2 || y < -1 || y >= height - 1) continue;
                _markers.Add(new Marker { X = x, Y = y, Latitude = position.Latitude, Longitude = position.Longitude,
                    Label = position.Symbol, Key = "p:" + position.NodeNumber, Position = position });
            }
        }
        private void DrawPositionMarkers(int width, int height)
        {
            foreach (var position in _positionMarkers.Values.OrderBy(item => "p:" + item.NodeNumber == _selectedKey ? 1 : 0))
            {
                int x, y; Project(position.Latitude, position.Longitude, width, height, out x, out y);
                Color color; if (!Enum.TryParse(position.Color, true, out color)) color = Color.BrightYellow;
                Action<int, int, string> draw = (px, py, symbol) => { if (py >= 0 && py < height - 1) Draw(px, py, symbol, color, Color.Black, width); };
                draw(x, y - 1, "|"); draw(x - 1, y, "--"); draw(x + 2, y, "--"); draw(x, y + 1, "|");
                if (y >= 0 && y < height - 1)
                {
                    if ("p:" + position.NodeNumber == _selectedKey) DrawSelected(x, y, position.Symbol, color, Color.Black, width);
                    else draw(x, y, position.Symbol);
                }
            }
        }

        private void DrawMeasurementPreview(int width, int height)
        {
            if (!HasMeasurementPoint) return;
            int mx, my;
            Project(MeasurementLatitude, MeasurementLongitude, width, height, out mx, out my);
            var cx = width / 2; var cy = height / 2;
            Action<int, int> draw = (x, y) =>
            {
                if (x < 0 || x >= width || y < 0 || y >= height - 1) return;
                if ((y == cy && Math.Abs(x - cx) <= 2) || (x == cx && Math.Abs(y - cy) == 1)) return;
                Draw(x, y, "M", Color.BrightYellow, Color.Black, width);
            };
            Action<int, int, int, int> line = (x1, y1, x2, y2) =>
            {
                if (ClipLine(ref x1, ref y1, ref x2, ref y2, width, height - 1)) AddLineCells(x1, y1, x2, y2, draw);
            };
            if (_measurementMode == 1) line(mx, my, cx, cy);
            else if (_measurementMode == 3)
            {
                line(mx, my, cx, my); line(cx, my, cx, cy);
                line(cx, cy, mx, cy); line(mx, cy, mx, my);
            }
            else if (_measurementMode == 2)
            {
                var radius = MeshtasticClient.GetDistanceMeters(MeasurementLatitude, MeasurementLongitude, _centerLatitude, _centerLongitude).GetValueOrDefault();
                var rx = radius / (_metersPerRow * .5d); var ry = radius / _metersPerRow;
                var previousX = mx + (int)Math.Round(rx); var previousY = my;
                for (var degree = 1; degree <= 360; degree++)
                {
                    var angle = degree * Math.PI / 180d;
                    var x = mx + (int)Math.Round(Math.Cos(angle) * rx);
                    var y = my + (int)Math.Round(Math.Sin(angle) * ry);
                    line(previousX, previousY, x, y); previousX = x; previousY = y;
                }
            }
            Draw(mx, my, "M", Color.BrightYellow, Color.Black, width);
        }

        private void DrawBackground(int width, int height)
        {
            if (!ShowBackgroundMap || _offlineMap == null)
            {
                Application.Driver.SetAttribute(Application.Driver.MakeAttribute(Color.Gray, Color.Black));
                for (var y = 0; y < height; y++) { Move(0, y); Application.Driver.AddStr(new string(' ', width)); }
                return;
            }
            EnsureBackgroundCache(width, height);
            for (var y = 0; y < height; y++)
            {
                Move(0, y);
                for (var x = 0; x < width; x++)
                {
                    Application.Driver.SetAttribute(Application.Driver.MakeAttribute(Color.Gray, _backgroundColors[x, y]));
                    Application.Driver.AddRune(' ');
                }
            }
        }

        private void EnsureBackgroundCache(int width, int height)
        {
            if (_backgroundColors != null && _backgroundWidth == width && _backgroundHeight == height &&
                _backgroundLatitude == _centerLatitude && _backgroundLongitude == _centerLongitude && _backgroundMetersPerRow == _metersPerRow) return;
            var colors = new Color[width, height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    double latitude, longitude;
                    Unproject(x, y, width, height, out latitude, out longitude);
                    colors[x, y] = _offlineMap.GetBackground(latitude, longitude, _metersPerRow);
                }
            _backgroundColors = colors; _backgroundWidth = width; _backgroundHeight = height;
            _backgroundLatitude = _centerLatitude; _backgroundLongitude = _centerLongitude; _backgroundMetersPerRow = _metersPerRow;
        }

        private void DrawOverlayShapes(int width, int height)
        {
            _shapeHitCells.Clear();
            foreach (var point in _overlayPoints.Where(item => item.ReferenceLatitude.HasValue && item.ReferenceLongitude.HasValue && !String.Equals(item.Shape, "Point", StringComparison.OrdinalIgnoreCase)))
            {
                int x1, y1, x2, y2;
                Project(point.Latitude, point.Longitude, width, height, out x1, out y1);
                Project(point.ReferenceLatitude.Value, point.ReferenceLongitude.Value, width, height, out x2, out y2);
                Color color; if (!Enum.TryParse(point.Color ?? "Green", true, out color)) color = Color.Green;
                var fillDensity = point.EffectiveFillDensity;
                var outlineCells = new HashSet<string>(); var fillCells = new HashSet<string>();
                Action<HashSet<string>, int, int> add = delegate(HashSet<string> set, int x, int y) { if (x >= 0 && x < width && y >= 0 && y < height - 1) set.Add(x + ":" + y); };
                if (String.Equals(point.Shape, "Line", StringComparison.OrdinalIgnoreCase)) { if (ClipLine(ref x1, ref y1, ref x2, ref y2, width, height - 1)) AddLineCells(x1, y1, x2, y2, (x, y) => add(outlineCells, x, y)); }
                else if (String.Equals(point.Shape, "Rectangle", StringComparison.OrdinalIgnoreCase))
                {
                    var left = Math.Min(x1, x2); var right = Math.Max(x1, x2); var top = Math.Min(y1, y2); var bottom = Math.Max(y1, y2);
                    for (var y = Math.Max(0, top); y <= Math.Min(height - 2, bottom); y++) for (var x = Math.Max(0, left); x <= Math.Min(width - 1, right); x++) if (x == left || x == right || y == top || y == bottom) add(outlineCells, x, y); else if (fillDensity > 0) add(fillCells, x, y);
                }
                else if (String.Equals(point.Shape, "Circle", StringComparison.OrdinalIgnoreCase))
                {
                    var radiusMeters = MeshtasticClient.GetDistanceMeters(point.Latitude, point.Longitude, point.ReferenceLatitude, point.ReferenceLongitude).GetValueOrDefault(_metersPerRow);
                    var radiusX = Math.Max(1, (int)Math.Round(radiusMeters / (_metersPerRow * .5d))); var radiusY = Math.Max(1, (int)Math.Round(radiusMeters / _metersPerRow));
                    if (fillDensity > 0)
                    {
                        for (var y = Math.Max(-radiusY, -y1); y <= Math.Min(radiusY, height - 2 - y1); y++) for (var x = Math.Max(-radiusX, -x1); x <= Math.Min(radiusX, width - 1 - x1); x++) if ((x * x) / (double)(radiusX * radiusX) + (y * y) / (double)(radiusY * radiusY) <= 1.0) add(fillCells, x1 + x, y1 + y);
                    }
                    for (var degree = 0; degree < 360; degree += 2) add(outlineCells, x1 + (int)Math.Round(Math.Cos(degree * Math.PI / 180d) * radiusX), y1 + (int)Math.Round(Math.Sin(degree * Math.PI / 180d) * radiusY));
                }
                var outlineSymbol = String.IsNullOrEmpty(point.ShortName) ? "?" : StringInfo.GetNextTextElement(point.ShortName);
                var fillSymbol = String.IsNullOrEmpty(point.FillSymbol) ? "*" : StringInfo.GetNextTextElement(point.FillSymbol);
                foreach (var cell in fillCells)
                {
                    var parts = cell.Split(':'); var fillX = Int32.Parse(parts[0], CultureInfo.InvariantCulture); var fillY = Int32.Parse(parts[1], CultureInfo.InvariantCulture);
                    var crosshairX = width / 2; var crosshairY = height / 2;
                    var occupiesCrosshair = (fillY == crosshairY && Math.Abs(fillX - crosshairX) <= 2) || (fillX == crosshairX && Math.Abs(fillY - crosshairY) == 1);
                    if (occupiesCrosshair) continue;
                    if (fillDensity > 1 && Math.Abs((fillX - x1) + (fillY - y1) * 131) % fillDensity != 0) continue;
                    Draw(fillX, fillY, fillSymbol, color, Color.Black, width);
                }
                foreach (var cell in outlineCells) { var parts = cell.Split(':'); Draw(Int32.Parse(parts[0], CultureInfo.InvariantCulture), Int32.Parse(parts[1], CultureInfo.InvariantCulture), outlineSymbol, color, Color.Black, width); }
                foreach (var cell in fillCells) _shapeHitCells[cell] = point;
                foreach (var cell in outlineCells) _shapeHitCells[cell] = point;
            }
        }

        private static void AddLineCells(int x1, int y1, int x2, int y2, Action<int, int> add)
        {
            var dx = Math.Abs(x2 - x1); var sx = x1 < x2 ? 1 : -1; var dy = -Math.Abs(y2 - y1); var sy = y1 < y2 ? 1 : -1; var error = dx + dy;
            while (true) { add(x1, y1); if (x1 == x2 && y1 == y2) break; var twice = 2 * error; if (twice >= dy) { error += dy; x1 += sx; } if (twice <= dx) { error += dx; y1 += sy; } }
        }

        private static bool ClipLine(ref int x1, ref int y1, ref int x2, ref int y2, int width, int height)
        {
            double t0 = 0d, t1 = 1d; var dx = x2 - x1; var dy = y2 - y1;
            var p = new[] { -dx, dx, -dy, dy }; var q = new[] { x1, width - 1 - x1, y1, height - 1 - y1 };
            for (var index = 0; index < 4; index++)
            {
                if (p[index] == 0) { if (q[index] < 0) return false; continue; }
                var ratio = q[index] / (double)p[index];
                if (p[index] < 0) { if (ratio > t1) return false; if (ratio > t0) t0 = ratio; }
                else { if (ratio < t0) return false; if (ratio < t1) t1 = ratio; }
            }
            var originalX = x1; var originalY = y1;
            x1 = (int)Math.Round(originalX + t0 * dx); y1 = (int)Math.Round(originalY + t0 * dy);
            x2 = (int)Math.Round(originalX + t1 * dx); y2 = (int)Math.Round(originalY + t1 * dy);
            return true;
        }

        private void AddOverlayMarkers(int width, int height)
        {
            var occupied = new HashSet<string>();
            foreach (var marker in _markers)
                for (var column = marker.X - marker.Label.Length / 2 - 1; column <= marker.X + marker.Label.Length / 2 + 1; column++) occupied.Add(marker.Y + ":" + column);
            var index = 0;
            foreach (var point in _overlayPoints.OrderBy(item => String.Equals(item.Shape, "Point", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
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
                if (collision && (!point.IsTelemetryPosition || !point.TelemetryReceivedAtUtc.HasValue || TelemetryPositionKey(point) != _selectedKey)) { index++; continue; }
                for (var column = left - 1; column <= left + label.Length; column++) occupied.Add(y + ":" + column);
                Color color;
                if (!Enum.TryParse(point.Color ?? "Green", true, out color)) color = Color.Green;
                var key = point.IsTelemetryPosition && point.TelemetryReceivedAtUtc.HasValue ? TelemetryPositionKey(point) : "o:" + index;
                _markers.Add(new Marker { X = x, Y = y, Latitude = point.Latitude, Longitude = point.Longitude, Label = label, Key = key, Overlay = point, Color = color });
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
            _markers.Clear();
            if (!ShowKnownNodes) return;
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
            if (_selectionSuppressed) return;
            if (_selectedShapeOverlay != null) return;
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

        private bool SelectAdjacentTelemetryPosition(int direction)
        {
            var positions = _overlayPoints.Where(point => point.IsTelemetryPosition && point.TelemetryReceivedAtUtc.HasValue).OrderBy(point => point.TelemetryReceivedAtUtc.Value).ToList();
            if (positions.Count == 0) return false;
            var currentIndex = positions.FindIndex(point => TelemetryPositionKey(point) == _selectedKey);
            var nextIndex = currentIndex < 0 ? (direction > 0 ? 0 : positions.Count - 1) : (currentIndex + direction + positions.Count) % positions.Count;
            var selected = positions[nextIndex];
            _centerLatitude = selected.Latitude;
            _centerLongitude = selected.Longitude;
            _selectionSuppressed = false;
            _selectedKey = TelemetryPositionKey(selected);
            NotifySelection(null);
            if (OverlaySelectionChanged != null) OverlaySelectionChanged(selected);
            NotifyViewChanged();
            SetNeedsDisplay();
            return true;
        }
        private static string TelemetryPositionKey(MapOverlayPoint point)
        {
            return "t:" + point.TelemetryNodeNumber + ":" + point.TelemetryReceivedAtUtc.Value.Ticks;
        }

        private void CenterSelectedItem()
        {
            if (_selectedShapeOverlay != null)
            {
                CenterOn(_selectedShapeOverlay.Latitude, _selectedShapeOverlay.Longitude);
                return;
            }
            var marker = _markers.FirstOrDefault(item => item.Key == _selectedKey);
            if (marker != null) CenterOn(marker.Latitude, marker.Longitude);
        }

        private void ActivateSelection()
        {
            if (_selectedShapeOverlay != null) { if (OverlayActivated != null) OverlayActivated(_selectedShapeOverlay); return; }
            var marker = _markers.FirstOrDefault(item => item.Key == _selectedKey);
            if (marker == null) return;
            if (marker.Position != null) { if (PositionMarkerActivated != null) PositionMarkerActivated(marker.Position); }
            else if (marker.Nodes.Count > 1)
            {
                if (ClusterActivated != null) ClusterActivated(marker.Nodes.ToList());
            }
            else if (marker.Overlay != null && OverlayActivated != null) OverlayActivated(marker.Overlay);
            else if (marker.Nodes.Count == 1 && NodeActivated != null) NodeActivated(marker.Nodes[0]);
        }

        private void SetSelection(Marker marker)
        {
            _selectedShapeOverlay = null;
            _selectionSuppressed = false;
            _selectedKey = marker.Key;
            if (marker.Position != null)
            {
                if (PositionMarkerSelected != null) PositionMarkerSelected(marker.Position);
            }
            else if (marker.Overlay != null)
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
            _centerLatitude = latitude; _centerLongitude = longitude; ClearSelectionAndShowCrosshair(); NotifyViewChanged(); SetNeedsDisplay();
        }
        public bool CenterOnNode(uint nodeNumber)
        {
            var node = _nodes.FirstOrDefault(item => item.NodeNumber == nodeNumber);
            if (node == null || !HasPosition(node)) return false;
            _centerLatitude = node.Latitude.Value; _centerLongitude = node.Longitude.Value;
            _selectedKey = "n:" + node.NodeNumber; _selectedShapeOverlay = null; _selectionSuppressed = false; _selectCrosshairAfterViewChange = false;
            if (OverlaySelectionChanged != null) OverlaySelectionChanged(null);
            NotifySelection(node); NotifyViewChanged(); SetNeedsDisplay();
            return true;
        }
        public void RestoreView(double latitude, double longitude, double metersPerRow)
        {
            if (latitude < -90d || latitude > 90d || longitude < -180d || longitude > 180d) return;
            _centerLatitude = latitude; _centerLongitude = longitude; _metersPerRow = Math.Max(1d, Math.Min(2000000d, metersPerRow)); SetNeedsDisplay();
        }

        private void NotifyViewChanged() { if (ViewChanged != null) ViewChanged(); if (HasMeasurementPoint) ShowMeasurementInfo(); }

        public void ResetMeasurement()
        {
            _measurementMode = 0;
            _measurementLatitude = null;
            _measurementLongitude = null;
            ClearSelectionAndShowCrosshair();
            if (MapFeatureSelectionChanged != null) MapFeatureSelectionChanged("");
            SetNeedsDisplay();
        }

        private void ShowMeasurementInfo()
        {
            if (MapFeatureSelectionChanged == null) return;
            if (!HasMeasurementPoint) return;
            var distance = MeshtasticClient.GetDistanceMeters(_measurementLatitude, _measurementLongitude, _centerLatitude, _centerLongitude).GetValueOrDefault();
            var vertical = (_centerLatitude - _measurementLatitude.Value) * 111320d;
            var horizontal = (_centerLongitude - _measurementLongitude.Value) * 111320d * Math.Cos((_centerLatitude + _measurementLatitude.Value) * .5d * Math.PI / 180d);
            Func<double, string> meters = value => Math.Abs(value) < 1000d ? Math.Abs(value).ToString("F0", CultureInfo.InvariantCulture) + " m" : (Math.Abs(value) / 1000d).ToString("F2", CultureInfo.InvariantCulture) + " km";
            Func<double, string> area = value => value < 1000000d ? value.ToString("F0", CultureInfo.InvariantCulture) + " m²" : (value / 1000000d).ToString("F2", CultureInfo.InvariantCulture) + " km²";
            if (_measurementMode == 2) { MapFeatureSelectionChanged("Measurement: Circle | Radius " + meters(distance) + " | Area " + area(Math.PI * distance * distance)); return; }
            if (_measurementMode == 3) { MapFeatureSelectionChanged("Measurement: Rectangle | Width " + meters(horizontal) + " | Height " + meters(vertical) + " | Area " + area(Math.Abs(horizontal * vertical))); return; }
            MapFeatureSelectionChanged("Measurement: Line from M | Distance " + meters(distance) + " | Horizontal " + meters(horizontal) + " " + (horizontal < 0 ? "W" : "E") + " | Vertical " + meters(vertical) + " " + (vertical < 0 ? "S" : "N"));
        }
        private void Pan(int dx, int dy, int horizontalCells, int verticalCells)
        {
            _centerLatitude -= dy * _metersPerRow * verticalCells / 111320d;
            _centerLongitude += dx * _metersPerRow * .5d * horizontalCells / (111320d * Math.Max(.01d, Math.Cos(_centerLatitude * Math.PI / 180d)));
            ClearSelectionAndShowCrosshair();
            NotifyViewChanged();
            SetNeedsDisplay();
        }

        private void ClearSelectionAndShowCrosshair()
        {
            _selectedKey = null; _selectedShapeOverlay = null; _selectionSuppressed = true; _selectCrosshairAfterViewChange = true;
        }

        private void SelectCrosshairItem(int width, int height)
        {
            _selectCrosshairAfterViewChange = false;
            var centerX = width / 2; var centerY = height / 2;
            var marker = _markers.Where(item => item.Overlay == null || item.Overlay.Selectable)
                .LastOrDefault(item => item.Y == centerY && centerX >= item.X - item.Label.Length / 2 && centerX < item.X - item.Label.Length / 2 + item.Label.Length);
            if (marker != null) { SetSelection(marker); return; }
            MapOverlayPoint shapePoint;
            if (_shapeHitCells.TryGetValue(centerX + ":" + centerY, out shapePoint) && shapePoint.Selectable)
            {
                _selectedKey = null; _selectedShapeOverlay = shapePoint; _selectionSuppressed = false;
                NotifySelection(null); if (OverlaySelectionChanged != null) OverlaySelectionChanged(shapePoint); return;
            }
            if (HasMeasurementPoint) { ShowMeasurementInfo(); return; }
            SelectMapFeature(_centerLatitude, _centerLongitude, "Offline map", false);
        }

        private void SelectMapFeature(double latitude, double longitude, string emptyText, bool redraw = true)
        {
            _selectedKey = null; _selectedShapeOverlay = null; _selectionSuppressed = true;
            var description = ShowBackgroundMap && _offlineMap != null ? _offlineMap.GetFeatureDescription(latitude, longitude, _metersPerRow) : null;
            if (MapFeatureSelectionChanged != null) MapFeatureSelectionChanged(String.IsNullOrWhiteSpace(description) ? emptyText : description);
            if (redraw) SetNeedsDisplay();
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
            _mapTextRows = rows;
            try { DrawOverlayShapes(width, height); }
            finally { _mapTextRows = null; }
            foreach (var marker in _markers) putCentered(marker.X, marker.Y, marker.Label);
            if (_ownLatitude.HasValue && _ownLongitude.HasValue) { int x, y; Project(_ownLatitude.Value, _ownLongitude.Value, width, height, out x, out y); putCentered(x, y, "[YOU]"); }
            _mapTextRows = rows;
            try { DrawMeasurementPreview(width, height); }
            finally { _mapTextRows = null; }
            var columns = Math.Min(12, Math.Max(4, width / 6)); var meters = columns * _metersPerRow * .5d;
            var distance = meters < 1000d ? Math.Round(meters) + " m" : (meters / 1000d).ToString(meters < 10000d ? "F1" : "F0", CultureInfo.InvariantCulture) + " km";
            var scaleText = "|" + new string('-', columns - 2) + "| " + distance; var scaleLeft = Math.Max(0, width - scaleText.Length - 1);
            var coordinates = _centerLatitude.ToString("F5", CultureInfo.InvariantCulture) + ", " + _centerLongitude.ToString("F5", CultureInfo.InvariantCulture);
            var coordinatesLeft = Math.Max(0, scaleLeft - coordinates.Length - 1);
            for (var index = 0; index < coordinates.Length && coordinatesLeft + index < Math.Max(0, scaleLeft - 1); index++) rows[height - 1][coordinatesLeft + index] = coordinates[index];
            for (var index = 0; index < scaleText.Length && scaleLeft + index < width; index++) rows[height - 1][scaleLeft + index] = scaleText[index];
            _mapTextRows = rows;
            try { DrawPositionMarkers(width, height); }
            finally { _mapTextRows = null; }
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
            if (_mapTextRows != null)
            {
                if (y < 0 || y >= _mapTextRows.Length) return;
                for (var index = 0; index < text.Length; index++)
                    if (x + index >= 0 && x + index < width) _mapTextRows[y][x + index] = text[index];
                return;
            }
            if (y < 0 || y >= Bounds.Height || x >= width) return;
            if (x < 0) { if (-x >= text.Length) return; text = text.Substring(-x); x = 0; }
            if (x + text.Length > width) text = text.Substring(0, width - x);
            if (!ShowBackgroundMap || _offlineMap == null)
            {
                Move(x, y);
                Application.Driver.SetAttribute(Application.Driver.MakeAttribute(foreground, background));
                Application.Driver.AddStr(text);
                return;
            }
            for (var index = 0; index < text.Length; index++)
            {
                EnsureBackgroundCache(Bounds.Width, Bounds.Height);
                var mapBackground = _backgroundColors[x + index, y];
                var visibleForeground = foreground == mapBackground ? InvertColor(foreground) : foreground;
                Move(x + index, y);
                Application.Driver.SetAttribute(Application.Driver.MakeAttribute(visibleForeground, mapBackground));
                Application.Driver.AddRune(text[index]);
            }
        }

        private void DrawSelected(int x, int y, string text, Color normalForeground, Color normalBackground, int width)
        {
            x -= text.Length / 2;
            if (y < 0 || y >= Bounds.Height || x >= width) return;
            if (x < 0) { if (-x >= text.Length) return; text = text.Substring(-x); x = 0; }
            if (x + text.Length > width) text = text.Substring(0, width - x);
            if (!ShowBackgroundMap || _offlineMap == null)
            {
                var selectedForeground = normalBackground == normalForeground ? InvertColor(normalForeground) : normalBackground;
                Move(x, y);
                Application.Driver.SetAttribute(Application.Driver.MakeAttribute(selectedForeground, normalForeground));
                Application.Driver.AddStr(text);
                return;
            }
            EnsureBackgroundCache(Bounds.Width, Bounds.Height);
            for (var index = 0; index < text.Length; index++)
            {
                var mapBackground = _backgroundColors[x + index, y];
                var visibleNormalForeground = normalForeground == mapBackground ? InvertColor(normalForeground) : normalForeground;
                Move(x + index, y);
                Application.Driver.SetAttribute(Application.Driver.MakeAttribute(mapBackground, visibleNormalForeground));
                Application.Driver.AddRune(text[index]);
            }
        }

        private static Color InvertColor(Color color)
        {
            if (color == Color.Black) return Color.White;
            if (color == Color.White) return Color.Black;
            if (color == Color.Blue || color == Color.BrightBlue) return Color.BrightYellow;
            if (color == Color.BrightYellow || color == Color.Brown) return Color.Blue;
            if (color == Color.Green || color == Color.BrightGreen) return Color.BrightMagenta;
            if (color == Color.Magenta || color == Color.BrightMagenta) return Color.BrightGreen;
            if (color == Color.Cyan || color == Color.BrightCyan) return Color.BrightRed;
            if (color == Color.Red || color == Color.BrightRed) return Color.BrightCyan;
            return color == Color.Gray ? Color.Black : Color.White;
        }

        private static string SafeLabel(string text)
        {
            var result = new System.Text.StringBuilder();
            var elements = StringInfo.GetTextElementEnumerator(text ?? "");
            while (elements.MoveNext())
            {
                var element = (string)elements.Current;
                var category = CharUnicodeInfo.GetUnicodeCategory(element, 0);
                var unsupported = category == UnicodeCategory.Control || category == UnicodeCategory.Format ||
                    category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator || Char.IsSurrogate(element[0]);
                result.Append(unsupported ? "?" : element);
            }
            return result.ToString();
        }
        private static bool HasPosition(StoredMeshNode node) { return node != null && ValidLatitude(node.Latitude) && ValidLongitude(node.Longitude); }
        private static bool ValidLatitude(double? value) { return value.HasValue && !Double.IsNaN(value.Value) && value.Value >= -90d && value.Value <= 90d; }
        private static bool ValidLongitude(double? value) { return value.HasValue && !Double.IsNaN(value.Value) && value.Value >= -180d && value.Value <= 180d; }
    }
}




