using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Mapbox.Vector.Tile;
using Microsoft.Data.Sqlite;
using Terminal.Gui;
using Color = Terminal.Gui.Color;

namespace ConsoleClient
{
    /// <summary>Reads raster or Mapbox-vector tiles from the first MBTiles file in the optional osm directory.</summary>
    internal sealed class OfflineMapProvider : IDisposable
    {
        private sealed class CachedTile
        {
            public Bitmap Bitmap;
            public List<VectorTileLayer> VectorLayers;
            public List<VectorFeatureHit>[] VectorBuckets;
            public long LastUsed;
        }

        private sealed class VectorFeatureHit
        {
            public VectorTileFeature Feature;
            public double MinimumX, MinimumY, MaximumX, MaximumY;
            public double Extent;
            public string LayerName;
            public Color Color;
        }

        private readonly SqliteConnection _connection;
        private readonly Dictionary<string, CachedTile> _tiles = new Dictionary<string, CachedTile>();
        private readonly HashSet<string> _missingTiles = new HashSet<string>();
        private readonly int _minimumZoom, _maximumZoom;
        private readonly bool _vector;
        private long _usageCounter;
        public string FileName { get; private set; }
        public bool IsVector { get { return _vector; } }

        private OfflineMapProvider(string fileName, SqliteConnection connection, int minimumZoom, int maximumZoom, bool vector)
        {
            FileName = fileName; _connection = connection; _minimumZoom = minimumZoom; _maximumZoom = maximumZoom; _vector = vector;
        }

        public static OfflineMapProvider TryOpenDefault()
        {
            try
            {
                var directory = Path.Combine(AppContext.BaseDirectory, "osm");
                if (!Directory.Exists(directory)) return null;
                var files = Directory.GetFiles(directory, "*.mbtiles");
                if (files.Length == 0) return null;
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                var connection = new SqliteConnection("Data Source=" + files[0] + ";Mode=ReadOnly");
                connection.Open();
                var format = ReadMetadata(connection, "format") ?? "";
                var vector = String.Equals(format, "pbf", StringComparison.OrdinalIgnoreCase) ||
                    (ReadMetadata(connection, "json") ?? "").IndexOf("vector_layers", StringComparison.OrdinalIgnoreCase) >= 0;
                return new OfflineMapProvider(files[0], connection, ReadMetadataInteger(connection, "minzoom", 0),
                    ReadMetadataInteger(connection, "maxzoom", vector ? 14 : 18), vector);
            }
            catch { return null; }
        }

        public Color GetBackground(double latitude, double longitude, double metersPerRow)
        {
            if (latitude < -85.05112878d || latitude > 85.05112878d || longitude < -180d || longitude > 180d) return Color.Black;
            var zoom = SelectZoom(latitude, metersPerRow);
            var scale = Math.Pow(2d, zoom);
            var tileXValue = (longitude + 180d) / 360d * scale;
            var latitudeRadians = latitude * Math.PI / 180d;
            var tileYValue = (1d - Math.Log(Math.Tan(latitudeRadians) + 1d / Math.Cos(latitudeRadians)) / Math.PI) * .5d * scale;
            var tileX = (int)Math.Floor(tileXValue);
            var xyzY = (int)Math.Floor(tileYValue);
            if (tileX < 0 || tileX >= (int)scale || xyzY < 0 || xyzY >= (int)scale) return Color.Black;
            var tile = GetTile(zoom, tileX, xyzY);
            if (tile == null) return Color.Black;
            var partX = tileXValue - tileX;
            var partY = tileYValue - xyzY;
            if (_vector) return GetVectorBackground(tile.VectorBuckets, partX, partY, latitude, zoom, metersPerRow);
            if (tile.Bitmap == null) return Color.Black;
            var pixelX = Math.Max(0, Math.Min(tile.Bitmap.Width - 1, (int)(partX * tile.Bitmap.Width)));
            var pixelY = Math.Max(0, Math.Min(tile.Bitmap.Height - 1, (int)(partY * tile.Bitmap.Height)));
            return Quantize(tile.Bitmap.GetPixel(pixelX, pixelY));
        }

        public string GetFeatureDescription(double latitude, double longitude, double metersPerRow)
        {
            if (!_vector || latitude < -85.05112878d || latitude > 85.05112878d || longitude < -180d || longitude > 180d) return null;
            var zoom = SelectZoom(latitude, metersPerRow);
            var scale = Math.Pow(2d, zoom);
            var tileXValue = (longitude + 180d) / 360d * scale;
            var latitudeRadians = latitude * Math.PI / 180d;
            var tileYValue = (1d - Math.Log(Math.Tan(latitudeRadians) + 1d / Math.Cos(latitudeRadians)) / Math.PI) * .5d * scale;
            var tileX = (int)Math.Floor(tileXValue); var xyzY = (int)Math.Floor(tileYValue);
            if (tileX < 0 || tileX >= (int)scale || xyzY < 0 || xyzY >= (int)scale) return null;
            var tile = GetTile(zoom, tileX, xyzY);
            if (tile == null || tile.VectorBuckets == null) return null;
            var partX = tileXValue - tileX; var partY = tileYValue - xyzY;
            const int divisions = 32;
            var column = Math.Max(0, Math.Min(divisions - 1, (int)(partX * divisions)));
            var row = Math.Max(0, Math.Min(divisions - 1, (int)(partY * divisions)));
            var candidates = tile.VectorBuckets[row * divisions + column];
            if (candidates == null) return null;
            var tileWidthMeters = 40075016.68557849d * Math.Max(.01d, Math.Cos(latitude * Math.PI / 180d)) / Math.Pow(2d, zoom);
            string fallback = null;
            foreach (var item in candidates)
            {
                var x = partX * item.Extent; var y = partY * item.Extent;
                var lineTolerance = Math.Max(2d, metersPerRow * .35d / tileWidthMeters * item.Extent);
                var tolerance = item.Feature.GeometryType == Tile.GeomType.LineString ? lineTolerance : 0d;
                if (x < item.MinimumX - tolerance || x > item.MaximumX + tolerance || y < item.MinimumY - tolerance || y > item.MaximumY + tolerance) continue;
                if (!Contains(item.Feature, x, y, lineTolerance)) continue;
                var description = FeatureDescription(item.LayerName, item.Feature.Attributes, false);
                if (!String.IsNullOrWhiteSpace(description)) return description;
                if (fallback == null) fallback = FeatureDescription(item.LayerName, item.Feature.Attributes, true);
            }
            return fallback;
        }

        private int SelectZoom(double latitude, double metersPerRow)
        {
            var desiredResolution = Math.Max(.25d, metersPerRow * .5d);
            var numerator = 156543.03392804097d * Math.Max(.01d, Math.Cos(latitude * Math.PI / 180d));
            var zoom = (int)Math.Round(Math.Log(numerator / desiredResolution, 2d));
            return Math.Max(_minimumZoom, Math.Min(_maximumZoom, zoom));
        }

        private CachedTile GetTile(int zoom, int x, int xyzY)
        {
            var key = zoom + ":" + x + ":" + xyzY;
            CachedTile cached;
            if (_tiles.TryGetValue(key, out cached)) { cached.LastUsed = ++_usageCounter; return cached; }
            if (_missingTiles.Contains(key)) return null;
            try
            {
                var tmsY = (1 << zoom) - 1 - xyzY;
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "SELECT tile_data FROM tiles WHERE zoom_level=$z AND tile_column=$x AND tile_row=$y LIMIT 1";
                    command.Parameters.AddWithValue("$z", zoom); command.Parameters.AddWithValue("$x", x); command.Parameters.AddWithValue("$y", tmsY);
                    var bytes = command.ExecuteScalar() as byte[];
                    if (bytes == null || bytes.Length == 0) { _missingTiles.Add(key); return null; }
                    cached = new CachedTile { LastUsed = ++_usageCounter };
                    if (_vector)
                    {
                        using (var source = OpenVectorStream(bytes)) cached.VectorLayers = VectorTileParser.Parse(source);
                        cached.VectorBuckets = BuildVectorBuckets(BuildVectorIndex(cached.VectorLayers));
                    }
                    else
                    {
                        using (var stream = new MemoryStream(bytes, false))
                        using (var source = new Bitmap(stream)) cached.Bitmap = new Bitmap(source);
                    }
                    _tiles[key] = cached; TrimCache(); return cached;
                }
            }
            catch { _missingTiles.Add(key); return null; }
        }

        private static Stream OpenVectorStream(byte[] bytes)
        {
            var stream = new MemoryStream(bytes, false);
            if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b) return new GZipStream(stream, CompressionMode.Decompress);
            return stream;
        }

        private static List<VectorFeatureHit> BuildVectorIndex(List<VectorTileLayer> layers)
        {
            var result = new List<VectorFeatureHit>();
            if (layers == null) return result;
            var orderedNames = new[] { "transportation", "transportation_name", "road", "railway", "boundary", "building", "waterway", "water", "landcover", "landuse", "park" };
            foreach (var name in orderedNames)
            {
                foreach (var layer in layers.Where(item => String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var feature in layer.VectorTileFeatures)
                    {
                        if (feature == null || feature.Geometry == null) continue;
                        var minimumX = Double.MaxValue; var minimumY = Double.MaxValue;
                        var maximumX = Double.MinValue; var maximumY = Double.MinValue;
                        foreach (var part in feature.Geometry)
                        {
                            var array = part.Array;
                            if (array == null) continue;
                            for (var index = part.Offset; index < part.Offset + part.Count; index++)
                            {
                                minimumX = Math.Min(minimumX, array[index].X); minimumY = Math.Min(minimumY, array[index].Y);
                                maximumX = Math.Max(maximumX, array[index].X); maximumY = Math.Max(maximumY, array[index].Y);
                            }
                        }
                        if (minimumX == Double.MaxValue) continue;
                        result.Add(new VectorFeatureHit { Feature = feature, MinimumX = minimumX, MinimumY = minimumY, MaximumX = maximumX, MaximumY = maximumY, Extent = layer.Extent == 0 ? 4096d : layer.Extent, LayerName = layer.Name, Color = FeatureColor(layer.Name, feature.Attributes) });
                    }
                }
            }
            return result;
        }

        private static List<VectorFeatureHit>[] BuildVectorBuckets(List<VectorFeatureHit> index)
        {
            const int divisions = 32;
            var buckets = new List<VectorFeatureHit>[divisions * divisions];
            foreach (var item in index)
            {
                // Include one neighbouring grid cell so thick roads and boundaries
                // remain candidates when their visible stroke crosses a bucket edge.
                var padding = item.Extent / divisions;
                var minimumColumn = Math.Max(0, Math.Min(divisions - 1, (int)Math.Floor((item.MinimumX - padding) / item.Extent * divisions)));
                var maximumColumn = Math.Max(0, Math.Min(divisions - 1, (int)Math.Floor((item.MaximumX + padding) / item.Extent * divisions)));
                var minimumRow = Math.Max(0, Math.Min(divisions - 1, (int)Math.Floor((item.MinimumY - padding) / item.Extent * divisions)));
                var maximumRow = Math.Max(0, Math.Min(divisions - 1, (int)Math.Floor((item.MaximumY + padding) / item.Extent * divisions)));
                for (var row = minimumRow; row <= maximumRow; row++)
                    for (var column = minimumColumn; column <= maximumColumn; column++)
                    {
                        var bucketIndex = row * divisions + column;
                        if (buckets[bucketIndex] == null) buckets[bucketIndex] = new List<VectorFeatureHit>();
                        buckets[bucketIndex].Add(item);
                    }
            }
            return buckets;
        }

        private static Color GetVectorBackground(List<VectorFeatureHit>[] buckets, double partX, double partY, double latitude, int zoom, double metersPerRow)
        {
            if (buckets == null) return Color.Black;
            const int divisions = 32;
            var column = Math.Max(0, Math.Min(divisions - 1, (int)(partX * divisions)));
            var row = Math.Max(0, Math.Min(divisions - 1, (int)(partY * divisions)));
            var candidates = buckets[row * divisions + column];
            if (candidates == null) return Color.Black;
            var tileWidthMeters = 40075016.68557849d * Math.Max(.01d, Math.Cos(latitude * Math.PI / 180d)) / Math.Pow(2d, zoom);
            foreach (var item in candidates)
            {
                var x = partX * item.Extent; var y = partY * item.Extent;
                var lineTolerance = Math.Max(2d, metersPerRow * .35d / tileWidthMeters * item.Extent);
                var tolerance = item.Feature.GeometryType == Tile.GeomType.LineString ? lineTolerance : 0d;
                if (x < item.MinimumX - tolerance || x > item.MaximumX + tolerance || y < item.MinimumY - tolerance || y > item.MaximumY + tolerance) continue;
                if (Contains(item.Feature, x, y, lineTolerance)) return item.Color;
            }
            return Color.Black;
        }

        private static bool Contains(VectorTileFeature feature, double x, double y, double lineTolerance)
        {
            if (feature == null || feature.Geometry == null) return false;
            if (feature.GeometryType == Tile.GeomType.Polygon) return PointInPolygon(feature.Geometry, x, y);
            if (feature.GeometryType == Tile.GeomType.LineString)
            {
                var limit = lineTolerance * lineTolerance;
                foreach (var part in feature.Geometry)
                {
                    var array = part.Array;
                    if (array == null) continue;
                    for (var index = part.Offset + 1; index < part.Offset + part.Count; index++)
                        if (SegmentDistanceSquared(x, y, array[index - 1], array[index]) <= limit) return true;
                }
            }
            return false;
        }

        private static bool PointInPolygon(List<ArraySegment<Coordinate>> rings, double x, double y)
        {
            var inside = false;
            foreach (var ring in rings)
            {
                var array = ring.Array;
                if (array == null || ring.Count < 3) continue;
                var previous = array[ring.Offset + ring.Count - 1];
                for (var index = ring.Offset; index < ring.Offset + ring.Count; index++)
                {
                    var current = array[index];
                    if ((current.Y > y) != (previous.Y > y) &&
                        x < (double)(previous.X - current.X) * (y - current.Y) / (previous.Y - current.Y) + current.X) inside = !inside;
                    previous = current;
                }
            }
            return inside;
        }

        private static double SegmentDistanceSquared(double x, double y, Coordinate start, Coordinate end)
        {
            double dx = end.X - start.X; double dy = end.Y - start.Y;
            if (dx == 0 && dy == 0) { dx = x - start.X; dy = y - start.Y; return dx * dx + dy * dy; }
            var ratio = Math.Max(0d, Math.Min(1d, ((x - start.X) * dx + (y - start.Y) * dy) / (dx * dx + dy * dy)));
            var nearestX = start.X + ratio * dx; var nearestY = start.Y + ratio * dy;
            dx = x - nearestX; dy = y - nearestY; return dx * dx + dy * dy;
        }

        private static Color FeatureColor(string layerName, List<KeyValuePair<string, object>> attributes)
        {
            var classification = Attribute(attributes, "class");
            var subclass = Attribute(attributes, "subclass");
            if (String.Equals(layerName, "water", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(layerName, "waterway", StringComparison.OrdinalIgnoreCase)) return Color.Blue;
            if (String.Equals(layerName, "building", StringComparison.OrdinalIgnoreCase)) return Color.DarkGray;
            if (String.Equals(layerName, "boundary", StringComparison.OrdinalIgnoreCase)) return Color.Magenta;
            if (String.Equals(layerName, "railway", StringComparison.OrdinalIgnoreCase) || classification == "rail" || subclass == "rail") return Color.Magenta;
            if (String.Equals(layerName, "transportation", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(layerName, "transportation_name", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(layerName, "road", StringComparison.OrdinalIgnoreCase))
            {
                return classification == "motorway" || classification == "trunk" || classification == "primary" || classification == "secondary"
                    ? Color.BrightYellow : Color.Gray;
            }
            if (classification == "farmland" || classification == "grass" || classification == "meadow" || classification == "park") return Color.BrightGreen;
            return Color.Green;
        }

        private static string Attribute(List<KeyValuePair<string, object>> attributes, string name)
        {
            if (attributes == null) return "";
            foreach (var attribute in attributes)
                if (String.Equals(attribute.Key, name, StringComparison.OrdinalIgnoreCase))
                    return Convert.ToString(attribute.Value, CultureInfo.InvariantCulture).ToLowerInvariant();
            return "";
        }

        private static string RawAttribute(List<KeyValuePair<string, object>> attributes, string name)
        {
            if (attributes == null) return "";
            foreach (var attribute in attributes)
                if (String.Equals(attribute.Key, name, StringComparison.OrdinalIgnoreCase))
                    return Convert.ToString(attribute.Value, CultureInfo.InvariantCulture);
            return "";
        }

        private static string FeatureDescription(string layerName, List<KeyValuePair<string, object>> attributes, bool allowFallback)
        {
            var name = RawAttribute(attributes, "name:en");
            if (String.IsNullOrWhiteSpace(name)) name = RawAttribute(attributes, "name");
            if (String.IsNullOrWhiteSpace(name)) name = RawAttribute(attributes, "name:de");
            var reference = RawAttribute(attributes, "ref");
            if (!String.IsNullOrWhiteSpace(name)) return "OSM " + layerName + " | " + name + (String.IsNullOrWhiteSpace(reference) ? "" : " | " + reference);
            if (!String.IsNullOrWhiteSpace(reference)) return "OSM " + layerName + " | " + reference;
            if (!allowFallback) return null;
            var classification = RawAttribute(attributes, "class");
            if (String.IsNullOrWhiteSpace(classification)) classification = RawAttribute(attributes, "subclass");
            return "OSM " + layerName + (String.IsNullOrWhiteSpace(classification) ? "" : " | " + classification);
        }

        private void TrimCache()
        {
            while (_tiles.Count > 64)
            {
                string oldestKey = null; var oldestUsage = Int64.MaxValue;
                foreach (var pair in _tiles) if (pair.Value.LastUsed < oldestUsage) { oldestUsage = pair.Value.LastUsed; oldestKey = pair.Key; }
                if (oldestKey == null) return;
                if (_tiles[oldestKey].Bitmap != null) _tiles[oldestKey].Bitmap.Dispose();
                _tiles.Remove(oldestKey);
            }
        }

        private static Color Quantize(System.Drawing.Color pixel)
        {
            var candidates = new[]
            {
                new { R = 242, G = 239, B = 233, Color = Color.Black },
                new { R = 170, G = 211, B = 223, Color = Color.Blue },
                new { R = 173, G = 209, B = 158, Color = Color.Green },
                new { R = 217, G = 208, B = 201, Color = Color.DarkGray },
                new { R = 255, G = 255, B = 255, Color = Color.Gray },
                new { R = 248, G = 190, B = 100, Color = Color.BrightYellow },
                new { R = 180, G = 110, B = 170, Color = Color.Magenta },
                new { R = 205, G = 225, B = 180, Color = Color.BrightGreen }
            };
            var best = candidates[0].Color; var bestDistance = Int64.MaxValue;
            foreach (var candidate in candidates)
            {
                var red = pixel.R - candidate.R; var green = pixel.G - candidate.G; var blue = pixel.B - candidate.B;
                var distance = red * red + green * green + blue * blue;
                if (distance < bestDistance) { bestDistance = distance; best = candidate.Color; }
            }
            return best;
        }

        private static string ReadMetadata(SqliteConnection connection, string name)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT value FROM metadata WHERE name=$name LIMIT 1"; command.Parameters.AddWithValue("$name", name);
                    return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
            }
            catch { return null; }
        }

        private static int ReadMetadataInteger(SqliteConnection connection, string name, int fallback)
        {
            int result; return Int32.TryParse(ReadMetadata(connection, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }

        public void Dispose()
        {
            foreach (var tile in _tiles.Values) if (tile.Bitmap != null) tile.Bitmap.Dispose();
            _tiles.Clear(); _connection.Dispose();
        }
    }
}
