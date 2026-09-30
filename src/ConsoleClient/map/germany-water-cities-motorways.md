# Deutschland: Gewässer und Städte

Overlay: germany-water-cities-motorways.xml

Der bestehende Dateiname bleibt für gespeicherte Overlay-Einstellungen erhalten. Autobahnen sind vollständig entfernt. Anzeigename: Deutschland – Gewässer und Städte. Kleinere Orte stammen aus GeoNames cities500 (Orte über 500 Einwohner und Verwaltungssitze); darunter sind auch Dörfer und Ortsteile.

- Flüsse und Ufer größerer Flüsse: blaue w-Linien; 40 benannte Flüsse aus einer Auswahl der wichtigsten Flüsse.
- Große benannte Seen, Talsperren und weitere stehende Gewässer ab etwa 5 km²: blaue w-Uferlinien; 80 Namen.
- 80 Großstädte mit mindestens 100.000 Einwohnern in den vorhandenen GeoNames-Daten: braune Kreise, Kontur und vollständige Füllung aus Punkten (.).
- Kleinere Städte und Orte unter 100.000 Einwohnern aus den vorhandenen GeoNames-Daten: grüne Punkte; ShortName ist der vollständige Ortsname.
- Description enthält immer den Objektnamen. Bei Städten enthält sie zusätzlich Einwohnerzahl und Näherungsradius.

Die Datei ist schreibgeschützt, ihre Elemente sind auswählbar. Stadtflächen werden zuerst gezeichnet, danach Gewässer. Die Beschreibung lässt sich durch Auswahl auf der Karte anzeigen.

## Stadtradien und Genauigkeit

Stadtpunkte und Einwohnerzahlen stammen aus dem bereits enthaltenen GeoNames-Overlay (Abruf 2026-08-17). Namen werden bei Bedarf auf amtliche Gemeindenamen abgebildet; Stadtbezirke und gleichnamige andere Gemeinden werden über die räumliche Lage ausgeschlossen.

Der Radius ist sqrt(Gemeindegebietsfläche / pi). Die Fläche wird aus den amtlichen Gemeindeumrissen einschließlich Inseln und abzüglich Löchern näherungsweise berechnet. Der Kreis ersetzt den tatsächlichen Stadtumriss; er umfasst auch unbebaute Teile des Gemeindegebiets. Er ist keine exakte Abgrenzung der bebauten Stadt. Die Radiusreferenz wird nördlich des Stadtpunkts gespeichert.

Linien wurden mit Douglas-Peucker und etwa 350 m Toleranz vereinfacht und auf sechs Dezimalstellen gerundet. Die Karte ist zur Übersicht im Terminal gedacht. Flüsse erscheinen als Achsen oder Uferlinien; Seen als Uferkonturen. Gewässerpolygonflächen werden nicht flächig gefüllt. Sie ist keine vollständige Karte aller Flüsse, Seen und Städte.

## Quellen und Lizenz

Geodatenabruf: 2026-09-30.

© BKG 2026, [Datenlizenz Deutschland – Namensnennung 2.0](https://www.govdata.de/dl-de/by-2-0).

- [BKG DLM250](https://gdz.bkg.bund.de/index.php/default/webdienste/digitale-landschaftsmodelle/wfs-digitales-landschaftsmodell-1-250-000-wfs-dlm250.html): Gewässerachsen, Fließgewässerflächen und stehende Gewässer.
- [BKG VG250](https://gdz.bkg.bund.de/index.php/default/open-data/wfs-verwaltungsgebiete-1-250-000-stand-01-01-wfs-vg250.html): Gemeindegebiete.
- [DLM250-Datenquellen](https://sgx.geodatenzentrum.de/web_public/gdz/datenquellen/datenquellen_dlm250.pdf).
- [VG250-Datenquellen](https://sgx.geodatenzentrum.de/web_public/gdz/datenquellen/datenquellen_vg_nuts.pdf).
- [GeoNames](https://www.geonames.org/), [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/): Stadtpunkte und Einwohnerzahlen.

Bearbeitung: thematische Auswahl, Vereinfachung, Umwandlung in XML-Liniensegmente und flächengleiche Stadtkreise. Quellenvermerk auch im XML-Kommentar.

## Erzeugung

tools/New-GermanyLandscapeOverlay.py erzeugt das Overlay und tools/germany-overlay-summary.json mit Mengen und enthaltenen Namen. Es verwendet ausschließlich die Python-Standardbibliothek. Quelldaten werden im temporären Verzeichnis meshtastic-germany-geodata zwischengespeichert. Für einen neuen Datenabruf einen anderen Cachepfad verwenden oder diese konkreten Cachedateien vorher entfernen. Die XML-Datei benötigt im Betrieb keinen Netzwerkzugriff.

Im Map-Menü die Overlay-Dateien neu laden beziehungsweise das Programm neu starten und das Overlay aktivieren.
