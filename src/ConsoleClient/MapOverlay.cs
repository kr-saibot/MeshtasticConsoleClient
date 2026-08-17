using System;
using System.Collections.Generic;
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
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string ShortName { get; set; }
        public string Description { get; set; }
        public string Color { get; set; }
        [XmlIgnore] public string SourceFile { get; set; }
        [XmlIgnore] public string OverlayName { get; set; }
        [XmlIgnore] public bool Selectable { get; set; }
        public MapOverlayPoint() { ShortName = ""; Description = ""; Color = "Green"; Selectable = true; }
    }

    internal sealed class MapOverlayFile
    {
        public string FileName;
        public string DisplayName;
        public bool Enabled;
        public MapOverlayData Data;
    }
}



