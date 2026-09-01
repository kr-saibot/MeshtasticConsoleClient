Optional offline background maps
================================

Place a raster MBTiles file in this directory. At runtime the client opens the
first *.mbtiles file alphabetically and renders it as terminal background
colours. Press 8 in the node map to toggle the background.

Raster MBTiles with PNG/JPEG tiles and OpenMapTiles-compatible vector MBTiles
with PBF tiles are supported. Vector files can be generated directly from a
Geofabrik *.osm.pbf extract with tilemaker. Include the attribution and licence
required by the map data and style. OpenStreetMap data normally requires
attribution to OpenStreetMap contributors.

Eine ausfuehrliche deutschsprachige Anleitung zur Erzeugung und Pruefung der
Kartendateien steht in MBTILES-ERSTELLEN.txt.
An English guide is available in MBTILES-CREATION.txt.
