"""Generate the Germany water/city overlay from BKG WFS and existing GeoNames points."""
import json, math, os, tempfile, time, urllib.parse, urllib.request
from pathlib import Path
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
CACHE = Path(tempfile.gettempdir()) / "meshtastic-germany-geodata"
CACHE.mkdir(exist_ok=True)
DATE = "2026-09-30"
NS = 'xmlns="http://www.opengis.net/ogc"'
def equal(prop, val):
    return f"<PropertyIsEqualTo><PropertyName>{prop}</PropertyName><Literal>{escape(val)}</Literal></PropertyIsEqualTo>"
def filt(body):
    return f"<Filter {NS}>{body}</Filter>"
def fetch(service, layer, name, filter_text=""):
    cache = CACHE / (name+".json")
    if cache.exists():
        return json.loads(cache.read_text(encoding="utf-8"))
    features = []
    start = 0
    while True:
        params = dict(service="WFS",version="2.0.0",request="GetFeature",typeNames=layer,
                      srsName="EPSG:4326",outputFormat="application/json",count=5000,startIndex=start)
        if filter_text: params["filter"] = filter_text
        url = "https://sgx.geodatenzentrum.de/"+service+"?"+urllib.parse.urlencode(params)
        for attempt in range(3):
            try:
                with urllib.request.urlopen(url,timeout=90) as response:
                    data = json.load(response)
                break
            except Exception:
                if attempt == 2: raise
                time.sleep(2)
        batch = data.get("features")
        if batch is None: raise RuntimeError(str(data)[:300])
        features.extend(batch)
        print(f"{name}: {len(features)} features",flush=True)
        if len(batch)<5000 or len(features)>=int(data.get("numberMatched",10**9)): break
        start += len(batch)
    cache.write_text(json.dumps(features,ensure_ascii=False),encoding="utf-8")
    return features

def polygons(g):
    return [g["coordinates"]] if g["type"]=="Polygon" else g["coordinates"] if g["type"]=="MultiPolygon" else []
def lines(g):
    return [g["coordinates"]] if g["type"]=="LineString" else g["coordinates"] if g["type"]=="MultiLineString" else []
def area(ring):
    scale=111320*math.cos(math.radians(sum(p[1] for p in ring)/len(ring)))
    return abs(sum(a[0]*scale*b[1]*111320-b[0]*scale*a[1]*111320 for a,b in zip(ring,ring[1:])))/2
def polygon_area(poly):
    return max(0,area(poly[0])-sum(area(r) for r in poly[1:]))
def simplify(points,tolerance=350):
    if len(points)<3:return points
    scale=111320*math.cos(math.radians(points[0][1]))
    xy=[(p[0]*scale,p[1]*111320) for p in points]
    keep={0,len(points)-1};stack=[(0,len(points)-1)]
    while stack:
        a,b=stack.pop();ax,ay=xy[a];dx=xy[b][0]-ax;dy=xy[b][1]-ay;length=dx*dx+dy*dy
        best=None; maximum=tolerance*tolerance
        for i in range(a+1,b):
            px=xy[i][0]-ax;py=xy[i][1]-ay;t=max(0,min(1,(px*dx+py*dy)/length)) if length else 0
            d=(px-t*dx)**2+(py-t*dy)**2
            if d>maximum:maximum=d;best=i
        if best is not None:keep.add(best);stack.extend(((a,best),(best,b)))
    if points[0]==points[-1] and len(keep)<4 and len(points)>=4:keep.update(((len(points)-1)//3,2*(len(points)-1)//3))
    return [points[i] for i in sorted(keep)]
river_names = ["Rhein","Elbe","Donau","Weser","Ems","Oder","Main","Mosel","Neckar","Havel","Spree",
               "Saale","Mulde","Ruhr","Lippe","Aller","Leine","Werra","Fulda","Lahn","Nahe","Saar",
               "Isar","Inn","Lech","Iller","Altmühl","Regnitz","Eider","Trave","Peene","Hunte",
               "Wupper","Sieg","Ahr","Unstrut","Bode","Schwarze Elster","Weiße Elster","Neiße","Lausitzer Neiße"]
river_filter=filt("<Or>"+"".join(equal("nam",n) for n in river_names)+"</Or>")
rivers=[]
for i in range(0,len(river_names),8):
    batch_filter=filt("<Or>"+"".join(equal("nam",n) for n in river_names[i:i+8])+"</Or>")
    rivers.extend(fetch("wfs_dlm250","dlm250:objart_44004_l",f"major-rivers-{i}",batch_filter))
river_surfaces=[]
for i in range(0,len(river_names),8):
    batch_filter=filt("<Or>"+"".join(equal("nam",n) for n in river_names[i:i+8])+"</Or>")
    river_surfaces.extend(fetch("wfs_dlm250","dlm250:objart_44001_f",f"major-river-surfaces-{i}",batch_filter))
lakes=fetch("wfs_dlm250","dlm250:objart_44006_f","lakes-all")
# Preserve the full names from Description; ShortName was shortened in the old overlay.
city_points={}
for place in ET.parse(ROOT/"src/ConsoleClient/map/german-cities.xml").getroot().findall("Place"):
    desc=place.findtext("Description","")
    if ", population " not in desc: continue
    name,pop=desc.rsplit(", population ",1)
    if int(pop)<100000:continue
    aliases={"Munich":"München","Nuremberg":"Nürnberg","Mülheim":"Mülheim an der Ruhr","Freiburg":"Freiburg im Breisgau","Oldenburg":"Oldenburg (Oldb)","Offenbach":"Offenbach am Main"}
    name=aliases.get(name,name)
    city_points[name]=(float(place.findtext("Longitude")),float(place.findtext("Latitude")),int(pop))
city_filter=filt("<And>"+equal("gf","4")+"<Or>"+"".join(equal("gen",n) for n in city_points)+"</Or></And>")
municipalities=[]
city_names=list(city_points)
for i in range(0,len(city_names),8):
    batch_filter=filt("<And>"+equal("gf","4")+"<Or>"+"".join(equal("gen",n) for n in city_names[i:i+8])+"</Or></And>")
    municipalities.extend(fetch("wfs_vg250","vg250:vg250_gem",f"major-city-areas-v2-{i}",batch_filter))

root=ET.Element("MapData",Name="Deutschland – Gewässer und Städte",writeProtected="true",selectable="true")
counts={"rivers":0,"lakes":0,"cities":0,"small_cities":0}
names={key:set() for key in counts}; seen=set()
def item(shape,color,symbol,name,a,b,filled=False):
    attrs=dict(shape=shape,referenceLatitude=f"{b[1]:.6f}",referenceLongitude=f"{b[0]:.6f}")
    if filled:attrs.update(filled="true",fillDensity="1",fillSymbol=".")
    p=ET.SubElement(root,"Place",attrs)
    for key,val in [("Latitude",f"{a[1]:.6f}"),("Longitude",f"{a[0]:.6f}"),("ShortName",symbol),("Description",name),("Color",color)]:
        ET.SubElement(p,key).text=val
def add_lines(points,color,symbol,name,kind):
    pts=simplify(points)
    for a,b in zip(pts,pts[1:]):
        a=tuple(round(v,6) for v in a[:2]);b=tuple(round(v,6) for v in b[:2])
        if a==b:continue
        key=(kind,min(a,b),max(a,b))
        if key in seen:continue
        seen.add(key);item("Line",color,symbol,name,a,b);counts[kind]+=1;names[kind].add(name)
# Cities first, then water so broad filled circles do not cover linear features.
def inside(point,ring):
    x,y=point;found=False
    for a,b in zip(ring,ring[1:]):
        if (a[1]>y)!=(b[1]>y) and x < (b[0]-a[0])*(y-a[1])/(b[1]-a[1])+a[0]:found=not found
    return found
# Exact municipality containment excludes same-name villages and city-district GeoNames points.
matching=[]
for feature in municipalities:
    name=feature["properties"]["gen"]
    lon,lat,_=city_points[name]
    if any(inside((lon,lat),poly[0]) and not any(inside((lon,lat),hole) for hole in poly[1:]) for poly in polygons(feature["geometry"])):
        matching.append(feature)
for f in matching:
    props=f["properties"];name=props["gen"]
    if name not in city_points:continue
    lon,lat,pop=city_points[name]
    square_meters=sum(polygon_area(poly) for poly in polygons(f["geometry"]))
    radius=math.sqrt(square_meters/math.pi)
    if radius<=0:continue
    description=f"{name}; {pop} Einwohner (GeoNames); Stadtkreis-Radius ca. {radius/1000:.1f} km, aus Gemeindegebietsfläche abgeleitet"
    item("Circle","Brown",".",description,(lon,lat),(lon,lat+radius/111320),True)
    counts["cities"]+=1;names["cities"].add(name)
for f in rivers:
    name=f["properties"].get("nam")
    for line in lines(f["geometry"]):add_lines(line,"Blue","w",name,"rivers")
for f in river_surfaces:
    name=f["properties"].get("nam")
    for poly in polygons(f["geometry"]):
        for ring in poly:add_lines(ring,"Blue","w",name,"rivers")
for f in lakes:
    name=f["properties"].get("nam")
    if not name:continue
    polys=polygons(f["geometry"])
    if sum(polygon_area(p) for p in polys)<5_000_000:continue
    for poly in polys:
        for ring in poly:add_lines(ring,"Blue","w",name,"lakes")
# Keep the original filename so existing overlay configuration remains valid.
# Smaller populated places come from the existing cities500 data; use their full names.
point_keys=set()
for place in ET.parse(ROOT/"src/ConsoleClient/map/german-cities.xml").getroot().findall("Place"):
    desc=place.findtext("Description","")
    if ", population " not in desc:continue
    name,population=desc.rsplit(", population ",1)
    if int(population)>=100000:continue
    latitude=place.findtext("Latitude")
    longitude=place.findtext("Longitude")
    key=(name,latitude,longitude)
    if key in point_keys:continue
    point_keys.add(key)
    p=ET.SubElement(root,"Place",shape="Point")
    for field,value in [("Latitude",latitude),("Longitude",longitude),("ShortName",name),("Description",f"{name}; {population} Einwohner (GeoNames)"),("Color","Green")]:
        ET.SubElement(p,field).text=value
    counts["small_cities"]+=1;names["small_cities"].add(name)
if counts["cities"]<60 or counts["small_cities"]<1000 or counts["rivers"]<500 or not counts["lakes"]:
    raise RuntimeError("Unexpectedly incomplete data: "+str(counts))
root.insert(0,ET.Comment(" © BKG 2026, dl-de/by-2-0 https://www.govdata.de/dl-de/by-2-0 ; https://sgx.geodatenzentrum.de/wfs_dlm250 ; https://sgx.geodatenzentrum.de/wfs_vg250 ; DLM250-Datenquellen: https://sgx.geodatenzentrum.de/web_public/gdz/datenquellen/datenquellen_dlm250.pdf ; VG250-Datenquellen: https://sgx.geodatenzentrum.de/web_public/gdz/datenquellen/datenquellen_vg_nuts.pdf ; Städte: GeoNames CC BY 4.0 https://www.geonames.org/ . Abruf "+DATE+". Bearbeitung: benannte Hauptflüsse, Seen ab 5 km²; Linien auf 350 m vereinfacht und auf 6 Dezimalstellen gerundet; Städte ab 100000 Einwohnern als flächengleiche Gemeindegebietskreise; kleinere GeoNames-Orte als grüne Punkte mit vollständigem Namen. Keine Autobahnen. "))
ET.indent(root,space="  ")
target=ROOT/"src/ConsoleClient/map/germany-water-cities-motorways.xml"
ET.ElementTree(root).write(target,encoding="utf-8",xml_declaration=True)
summary={"elements":counts,"named_features":{k:sorted(v) for k,v in names.items()},"file":str(target)}
(ROOT/"tools/germany-overlay-summary.json").write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding="utf-8")
print(json.dumps({"elements":counts,"named_features":{k:len(v) for k,v in names.items()}},ensure_ascii=False),flush=True)
