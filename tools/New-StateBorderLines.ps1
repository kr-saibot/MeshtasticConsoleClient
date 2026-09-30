param(
    [string] $InputGeoJson,
    [double] $ToleranceMeters = 300
)
$ErrorActionPreference = "Stop"
if (!$InputGeoJson) {
    $InputGeoJson = Join-Path $env:TEMP "meshtastic-state-borders.geojson"
    $url = "https://sgx.geodatenzentrum.de/wfs_vg250?service=WFS&version=2.0.0&request=GetFeature&typeNames=vg250:vg250_lan&srsName=EPSG:4326&outputFormat=application/json"
    Invoke-WebRequest -Uri $url -OutFile $InputGeoJson -TimeoutSec 60
}
if ($ToleranceMeters -le 0) { throw "Tolerance must be positive." }
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
public static class StateBorderSimplifier {
    public static int[] Simplify(double[] lon, double[] lat, double tolerance) {
        int n = lon.Length;
        var keep = new bool[n]; keep[0] = true; keep[n-1] = true;
        var pending = new Stack<int[]>(); pending.Push(new[] {0,n-1});
        double scale = 111320 * Math.Cos(lat[0] * Math.PI / 180);
        while (pending.Count > 0) {
            var range = pending.Pop(); int a = range[0], b = range[1], best = -1;
            double ax = lon[a]*scale, ay = lat[a]*111320;
            double dx = (lon[b]-lon[a])*scale, dy = (lat[b]-lat[a])*111320;
            double length = dx*dx+dy*dy, maximum = tolerance*tolerance;
            for (int i=a+1; i<b; i++) {
                double px = lon[i]*scale-ax, py = lat[i]*111320-ay;
                double t = length == 0 ? 0 : Math.Max(0,Math.Min(1,(px*dx+py*dy)/length));
                double ex = px-t*dx, ey = py-t*dy, distance = ex*ex+ey*ey;
                if (distance > maximum) { maximum = distance; best = i; }
            }
            if (best >= 0) { keep[best] = true; pending.Push(new[]{a,best}); pending.Push(new[]{best,b}); }
        }
        var result = new List<int>();
        for (int i=0;i<n;i++) if (keep[i]) result.Add(i);
        // Keep small closed island outlines rather than reducing them to a zero-length edge.
        if (result.Count < 4 && n >= 4) return new[]{0,(n-1)/3,2*(n-1)/3,n-1};
        return result.ToArray();
    }
}
"@
$data = Get-Content -Raw -LiteralPath $InputGeoJson | ConvertFrom-Json
$features = @($data.features | Where-Object { $_.properties.GF -eq 4 })
if (@($features.properties.GEN | Sort-Object -Unique).Count -ne 16) { throw "Expected all 16 states." }
$target = Join-Path (Split-Path -Parent $PSScriptRoot) "src\ConsoleClient\map\german-federal-state-borders.xml"
$settings = [Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$writer = [Xml.XmlWriter]::Create($target,$settings)
$seen = [Collections.Generic.HashSet[string]]::new()
$count = 0
$culture = [Globalization.CultureInfo]::InvariantCulture
try {
    $writer.WriteStartDocument()
    $writer.WriteComment("Quelle: © BKG 2026, dl-de/by-2-0 https://www.govdata.de/dl-de/by-2-0 ; VG250 Stand 01.01.2025. Datenquellen: https://sgx.geodatenzentrum.de/web_public/gdz/datenquellen/datenquellen_vg_nuts.pdf . Bearbeitung: Außenringe in Liniensegmente umgewandelt; Douglas-Peucker-Vereinfachung $ToleranceMeters m; auf 6 Dezimalstellen gerundet; identische Segmente zusammengeführt.")
    $writer.WriteStartElement("MapData")
    $writer.WriteAttributeString("Name","Bundesländergrenzen – rote Linien")
    $writer.WriteAttributeString("writeProtected","true")
    $writer.WriteAttributeString("selectable","false")
    foreach ($feature in $features) {
        foreach ($polygon in $feature.geometry.coordinates) {
            $ring = $polygon[0]
            if ($ring.Count -lt 4) { continue }
            $lon = [double[]]@($ring | ForEach-Object { $_[0] })
            $lat = [double[]]@($ring | ForEach-Object { $_[1] })
            $indices = [StateBorderSimplifier]::Simplify($lon,$lat,$ToleranceMeters)
            for ($i=1; $i -lt $indices.Length; $i++) {
                $a = $indices[$i-1]; $b = $indices[$i]
                $aLat = $lat[$a].ToString("F6",$culture); $aLon = $lon[$a].ToString("F6",$culture)
                $bLat = $lat[$b].ToString("F6",$culture); $bLon = $lon[$b].ToString("F6",$culture)
                $aKey = "$aLat,$aLon"; $bKey = "$bLat,$bLon"
                if ($aKey -eq $bKey) { continue }
                $key = if ([string]::CompareOrdinal($aKey,$bKey) -lt 0) { "$aKey|$bKey" } else { "$bKey|$aKey" }
                if (!$seen.Add($key)) { continue }
                $writer.WriteStartElement("Place")
                $writer.WriteAttributeString("shape","Line")
                $writer.WriteAttributeString("referenceLatitude",$bLat)
                $writer.WriteAttributeString("referenceLongitude",$bLon)
                $writer.WriteElementString("Latitude",$aLat)
                $writer.WriteElementString("Longitude",$aLon)
                $writer.WriteElementString("ShortName","*")
                $writer.WriteElementString("Description","Außengrenze " + $feature.properties.GEN + "; © BKG 2026, dl-de/by-2-0; vereinfacht $ToleranceMeters m")
                $writer.WriteElementString("Color","Red")
                $writer.WriteEndElement()
                $count++
            }
        }
    }
    $writer.WriteEndElement()
    $writer.WriteEndDocument()
}
finally { $writer.Dispose() }
Write-Host "$count red line segments from 16 states created: $target"
