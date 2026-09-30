using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Serialization;

namespace ConsoleClient
{
    [XmlRoot("MapData")]
    public sealed class MapOverlayData
    {
        [XmlAttribute] public string Name { get; set; }
        [XmlAttribute("writeProtected")] public bool WriteProtected { get; set; }
        [XmlAttribute("selectable")] public bool Selectable { get; set; }
        [XmlElement("Place")] public List<MapOverlayPoint> Places { get; set; }
        public MapOverlayData() { Name = ""; Selectable = true; Places = new List<MapOverlayPoint>(); }
    }

    public sealed class MapOverlayPoint
    {
        [XmlAttribute("uuid")] public string Uuid { get; set; }
        [XmlAttribute("creator")] public string Creator { get; set; }
        [XmlAttribute("shape")] public string Shape { get; set; }
        [XmlAttribute("filled")] public bool Filled { get; set; }
        [XmlAttribute("fillDensity")] public int FillDensity { get; set; }
        [XmlAttribute("fillSymbol")] public string FillSymbol { get; set; }
        [XmlIgnore] public double? ReferenceLatitude { get; set; }
        [XmlIgnore] public double? ReferenceLongitude { get; set; }
        [XmlAttribute("referenceLatitude")] public string ReferenceLatitudeXml { get { return ReferenceLatitude.HasValue ? ReferenceLatitude.Value.ToString("R", CultureInfo.InvariantCulture) : null; } set { double parsed; ReferenceLatitude = Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? (double?)parsed : null; } }
        [XmlAttribute("referenceLongitude")] public string ReferenceLongitudeXml { get { return ReferenceLongitude.HasValue ? ReferenceLongitude.Value.ToString("R", CultureInfo.InvariantCulture) : null; } set { double parsed; ReferenceLongitude = Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? (double?)parsed : null; } }
        public bool ShouldSerializeReferenceLatitudeXml() { return ReferenceLatitude.HasValue; }
        public bool ShouldSerializeReferenceLongitudeXml() { return ReferenceLongitude.HasValue; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string ShortName { get; set; }
        public string Description { get; set; }
        public string Color { get; set; }
        [XmlIgnore] public string SourceFile { get; set; }
        [XmlIgnore] public string OverlayName { get; set; }
        [XmlIgnore] public bool Selectable { get; set; }
        [XmlIgnore] public bool IsTelemetryPosition { get; set; }
        [XmlIgnore] public uint TelemetryNodeNumber { get; set; }
        [XmlIgnore] public DateTime? TelemetryReceivedAtUtc { get; set; }
        [XmlIgnore] public int? TelemetryAltitude { get; set; }
        [XmlIgnore] public int EffectiveFillDensity { get { return Math.Max(0, Math.Min(9, FillDensity > 0 ? FillDensity : Filled ? 1 : 0)); } }
        public MapOverlayPoint() { Uuid = ""; Creator = ""; Shape = "Point"; FillSymbol = "*"; ShortName = ""; Description = ""; Color = "Green"; Selectable = true; }
    }

    internal sealed class MapOverlayFile
    {
        public string FileName;
        public string DisplayName;
        public bool Enabled;
        public MapOverlayData Data;
    }
}



