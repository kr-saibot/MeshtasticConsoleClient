# Synchronisierungsprotokoll für Kartenelemente

Diese Beschreibung entspricht der aktuellen Implementierung im ConsoleClient 1.4.0 einschließlich der lokalen MAP-Erweiterungen. Sie beschreibt den Austausch einzelner Kartenelemente über Meshtastic-Kanalnachrichten.

## Transport und Konfiguration

Jedes Kommando wird als normale Meshtastic-Textnachricht an den konfigurierten Kanal gesendet. Es gibt keinen eigenen binären Port und keine Aufteilung auf mehrere Nachrichten. Die vollständige Nachricht darf höchstens **233 UTF-8-Bytes** umfassen, einschließlich Präfix, Trennzeichen und kodierter Texte.

Im Map-Menü wird die Freigabe konfiguriert. Die entsprechenden Felder in `meshtastic-settings.xml` unter `Map` sind:

| Einstellung | Bedeutung | Vorgabe |
| --- | --- | --- |
| `MapPointSharingEnabled` | Senden über die Oberfläche ermöglichen und Empfang verarbeiten | `false` |
| `MapPointSharingChannelIndex` | Lokaler Kanalindex für den Austausch | `0` |
| `MapPointSharingOverlayFile` | Dateiname des lokalen Ziel-Overlays | leer |
| `ReceiveSharedMapPointDeletions` | Empfangene Löschkommandos anwenden | `true` |
| `CenterMapOnReceivedSharedPoint` | Nach erfolgreichem Import auf die Hauptkoordinate zentrieren | `false` |

Die Teilnehmer benötigen einen gemeinsam nutzbaren Meshtastic-Kanal. Der Kanalindex ist eine lokale Konfiguration und steht nicht im Protokolltext. Die Ziel-XML-Datei muss bereits geladen und beschreibbar sein (`writeProtected="false"`). Sie wird anhand ihres Dateinamens ohne Verzeichnis ausgewählt; der Vergleich ignoriert Groß-/Kleinschreibung. Die Teilnehmer können unterschiedliche Ziel-Dateinamen verwenden.

Direktnachrichten, Nachrichten auf anderen Kanalindizes und eigene zurückempfangene Nachrichten werden nicht importiert. Protokollnachrichten werden weiterhin als normale Chatnachrichten gespeichert; sie durchlaufen auch die üblichen Benachrichtigungs- und Bot-Pfade.

## Neues Element: `///MAP+`

Das Präfix ist exakt und unterscheidet Groß-/Kleinschreibung. Unmittelbar danach folgt die Versionsnummer, ohne zusätzliches Trennzeichen:

```text
///MAP+1|UUID|LAT|LON|COLOR|SHAPE|FILL|NAME_B64|DESCRIPTION_B64|FILL_SYMBOL_B64|REF_LAT|REF_LON
```

Nach Entfernen von `///MAP+` müssen genau zwölf durch `|` getrennte Felder vorliegen. Leere Felder bleiben beim Zerlegen erhalten. Insbesondere dürfen die beiden abschließenden Referenzfelder bei Punkten nicht weggelassen werden.

| Index | Feld | Format und Bedeutung |
| --- | --- | --- |
| 0 | Version | Exakt `1`; andere Versionen werden verworfen |
| 1 | UUID | Sender: 32 Hexzeichen ohne Bindestriche; Empfänger akzeptiert die von .NET `Guid.TryParse` unterstützten Schreibweisen und normalisiert zu UUID mit Bindestrichen |
| 2 | LAT | Breitengrad der Hauptkoordinate, −90 bis +90 |
| 3 | LON | Längengrad der Hauptkoordinate, −180 bis +180 |
| 4 | COLOR | Hexadezimaler Farbcode `0` bis `f` |
| 5 | SHAPE | Dezimaler Formcode `0` bis `3` |
| 6 | FILL | Dezimaler Füllwert `0` bis `9` |
| 7 | NAME_B64 | UTF-8-Kurzname/Symbol als Base64 ohne abschließendes `=`; nach Dekodierung und Trim darf er nicht leer sein |
| 8 | DESCRIPTION_B64 | Beschreibung, ebenso kodiert; darf leer sein; Empfang entfernt äußere Leerzeichen und Zeilenumbrüche mittels Trim |
| 9 | FILL_SYMBOL_B64 | Füllsymbol, ebenso kodiert; Empfang übernimmt es ohne Trim |
| 10 | REF_LAT | Referenz-Breitengrad; für Linien, Rechtecke und Kreise erforderlich |
| 11 | REF_LON | Referenz-Längengrad; für Linien, Rechtecke und Kreise erforderlich |

### Koordinaten und Textkodierung

Der Sender verwendet Dezimalgrad mit Punkt als Dezimaltrennzeichen, rundet auf sechs Nachkommastellen und entfernt abschließende Nullen und einen überflüssigen Dezimalpunkt. Negatives Null wird als `0` gesendet. Der Parser liest mit `InvariantCulture` und `NumberStyles.Float`; für interoperable Sender sollte stets das oben beschriebene Zahlenformat verwendet werden.

Textfelder werden zuerst in UTF-8 umgewandelt und danach mit **normalem Base64** kodiert. Abschließende Paddingzeichen `=` werden entfernt. Es handelt sich nicht um Base64url: `+` und `/` bleiben unverändert. Der Empfänger ergänzt das Padding auf ein Vielfaches von vier und dekodiert. Ungültiges Base64 verwirft die Nachricht.

Ein leerer Text ergibt ein leeres Feld. Unicode und innere Zeilenumbrüche sind über diese Kodierung möglich. Das Größenlimit gilt für den fertigen Protokolltext; Base64 vergrößert die Textfelder.

### Farbcodes

| Code | Farbe | Code | Farbe |
| --- | --- | --- | --- |
| `0` | Black | `8` | DarkGray |
| `1` | Blue | `9` | BrightBlue |
| `2` | Green | `a` | BrightGreen |
| `3` | Cyan | `b` | BrightCyan |
| `4` | Red | `c` | BrightRed |
| `5` | Magenta | `d` | BrightMagenta |
| `6` | Brown | `e` | BrightYellow |
| `7` | Gray | `f` | White |

Der Sender schreibt Kleinbuchstaben. Der Parser liest den Farbcode als Hexzahl. Eine im lokalen Objekt unbekannte Farbe wird beim Senden auf `0` abgebildet.

### Formen und Referenzkoordinate

| Code | XML-Wert | Geometrie |
| --- | --- | --- |
| `0` | `Point` | Punkt an LAT/LON; Referenzfelder werden beim Empfang ignoriert |
| `1` | `Line` | Linie von LAT/LON zur Referenzkoordinate |
| `2` | `Rectangle` | Gegenüberliegende Ecken: LAT/LON und Referenzkoordinate; achsenparallel in der Kartenprojektion |
| `3` | `Circle` | Mittelpunkt LAT/LON; Radius ist die geografische Entfernung zur Referenzkoordinate |

Für Formen `1`–`3` müssen beide Referenzkoordinaten parsbar und im gleichen Wertebereich wie die Hauptkoordinate sein. In der Oberfläche wird die Referenz vorher mit `M` auf der Karte gesetzt. Der Kreis wird zur Anpassung an die Terminalzellen mit unterschiedlichen horizontalen und vertikalen Zellradien gezeichnet. Eine unbekannte lokale Form wird beim Senden als Punkt kodiert.

### Füllung und Symbole

`FILL=0` bedeutet keine Flächenfüllung. `1` füllt alle verfügbaren Innenzellen. Werte `2` bis `9` zeichnen zunehmend weniger Füllzellen: Die Implementierung wählt Zellen über eine Modulo-Bedingung aus. Ein größerer Wert ergibt daher eine dünnere Füllung.

Flächenfüllung wird für Rechtecke und Kreise verwendet. Linien werden als Kontur gezeichnet. Für die Kontur verwendet der Renderer das erste Unicode-Textelement des Kurznamens, für die Füllung das erste Textelement des Füllsymbols. Bei leerem Füllsymbol fällt der Renderer auf `*` zurück. Die Oberfläche verlangt bei aktivierter Füllung genau ein Textelement als Füllsymbol; der Nachrichtenparser prüft diese Länge nicht.

### Beispiel: Punkt

Kurzname `A`, Beschreibung `Test`, Füllsymbol `*`, Farbe Green, Position 52.5/13.4:

```text
///MAP+1|00112233445566778899aabbccddeeff|52.5|13.4|2|0|0|QQ|VGVzdA|Kg||
```

Die letzten beiden Felder sind leer. `QQ` dekodiert zu `A`, `VGVzdA` zu `Test` und `Kg` zu `*`.

### Beispiel: gefülltes Rechteck

Kurzname `R`, leere Beschreibung, Farbe BrightYellow, Füllwert 2 und Referenz 52.51/13.42:

```text
///MAP+1|112233445566778899aabbccddeeff00|52.5|13.4|e|2|2|Ug||Kg|52.51|13.42
```

## Empfang und lokale Speicherung

Nach erfolgreichem Parsen wird die UUID mit Bindestrichen normalisiert. Ist diese UUID bereits in **irgendeinem geladenen Overlay** vorhanden, wird die neue Nachricht ignoriert. Das gilt auch bei abweichenden Koordinaten oder Texten: `///MAP+` aktualisiert vorhandene Elemente nicht.

Neue Elemente werden im konfigurierten Ziel-Overlay ergänzt und als XML gespeichert. Das Overlay wird aktiviert und die Kartenansicht aktualisiert. `creator` wird lokal aus dem tatsächlichen Meshtastic-Absender gebildet, beispielsweise `Node name (!12345678)` oder nur `!12345678`; dieses Feld wird nicht im Protokolltext übertragen. Auswahlfähigkeit und Overlayname werden ebenfalls lokal aus dem Ziel-Overlay übernommen.

Alle Formen bleiben im XML als `Place` gespeichert. Beispiel zur Rechtecknachricht oben (der Creator ist nur ein Beispiel):

```xml
<MapData Name="Shared items" writeProtected="false" selectable="true">
  <Place uuid="11223344-5566-7788-99aa-bbccddeeff00"
         creator="!12345678" shape="Rectangle" filled="true"
         fillDensity="2" fillSymbol="*"
         referenceLatitude="52.51" referenceLongitude="13.42">
    <Latitude>52.5</Latitude>
    <Longitude>13.4</Longitude>
    <ShortName>R</ShortName>
    <Description />
    <Color>BrightYellow</Color>
  </Place>
</MapData>
```

`filled` wird beim Empfang aus `FILL > 0` abgeleitet. Beim Senden wird der effektive Füllwert übertragen: positiver `fillDensity` wird auf maximal 9 begrenzt; andernfalls ergibt das ältere XML-Attribut `filled="true"` den Wert 1, sonst 0. Dateiname, Overlayname, Schreibschutz und Auswahlfähigkeit sind keine übertragenen Eigenschaften.

## Löschung: `///MAP-`

```text
///MAP-00112233445566778899aabbccddeeff
```

Unmittelbar nach dem Präfix folgt ausschließlich die UUID, ohne Versionsnummer oder `|`. Der Sender verwendet 32 Hexzeichen ohne Bindestriche. Der Empfänger trimmt den Rest, liest ihn mit `Guid.TryParse` und normalisiert ihn.

Die Löschung wird nur angewendet, wenn die Freigabe und `ReceiveSharedMapPointDeletions` aktiviert sind und Kanal und Ziel-Overlay passen. Es wird der erste passende Eintrag **ausschließlich im konfigurierten, beschreibbaren Ziel-Overlay** entfernt. Einträge in anderen Overlays bleiben bestehen. Unbekannte UUIDs werden ignoriert. Bei einem Speicherfehler wird der entfernte Eintrag wieder in die lokale Liste aufgenommen.

Beim Löschen in der Oberfläche kann optional eine Löschmeldung gesendet werden. Die lokale Löschung wird zuerst gespeichert. Scheitert das Senden, bleibt das Element lokal gelöscht und eine Fehlermeldung wird angezeigt. Beim Erstellen gilt entsprechend: zuerst lokal speichern, dann optional senden; ein Sendefehler entfernt das lokal gespeicherte Element nicht.

## Grenzen der Synchronisierung

Das Protokoll tauscht Erstellungs- und Löschereignisse aus. Es enthält keine Bestandsabfrage, Vollsynchronisierung, Änderungsnachricht, Zeitstempel, Reihenfolgenummer, Konfliktauflösung oder eigene Empfangsbestätigung. Ein erfolgreicher Sendeaufruf bestätigt nicht, dass andere Clients das Element in ihre XML-Datei übernommen haben.

Es gibt keine protokolleigene Wiederholung oder Fragmentierung. Ein während der Übertragung nicht erreichbarer Client kann Ereignisse verpassen. Löschungen hinterlassen keine dauerhaften Löschmarkierungen: Trifft später erneut eine passende Erstellungsnachricht ein und existiert die UUID nicht mehr, kann das Element wieder angelegt werden.

Der Empfänger prüft keine Eigentümerschaft: Eine empfangene Löschmeldung kann ein Element des Ziel-Overlays anhand seiner UUID löschen, auch wenn ihr Absender vom ursprünglichen Creator abweicht. Die Zugriffsmöglichkeit ergibt sich aus dem verwendeten Meshtastic-Kanal; das Protokoll ergänzt keine eigene Signatur oder Autorisierung.

## Implementierungsstellen

- `src/ConsoleClient/Program.cs`: `SharedMapPointPrefix`, `SharedMapPointDeletionPrefix`, `BuildSharedMapPointMessage`, `TryParseSharedMapPoint`, `SendSharedMapMessage`, `TryReceiveSharedMapPoint`, Erstellen und Löschen in der Oberfläche.
- `src/ConsoleClient/MapOverlay.cs`: XML-Datenmodell und effektiver Füllwert.
- `src/ConsoleClient/NodeMapView.cs`: Geometrie, Kontur und Flächenfüllung.
- `src/Meshtastic.Client/MeshtasticSettings.cs`: Konfiguration unter `MeshtasticMapSettings`.
- `src/Meshtastic.Client/MeshtasticClient.cs`: `MaximumTextPayloadBytes = 233`.

## Temporäre Positionsmarker: ///MAPPOS+

Positionsmarker sind ein zusätzlicher, ausschließlich im Arbeitsspeicher gehaltener Kartentyp. Nach einem Neustart ist die Markerliste leer. Historische Chatnachrichten werden nicht zum Wiederherstellen der Marker ausgewertet. Normale Nachrichtenspeicherung bleibt aktiv; eine empfangene Protokollnachricht kann daher im Chatverlauf stehen, ohne dass der Marker als Kartenelement gespeichert wird.

Map → Position Markers speichert nur Rufname, Farbe und Mittelsymbol in den Map-Einstellungen (PositionMarkerCallSign, PositionMarkerColor, PositionMarkerSymbol). O setzt den eigenen Marker am aktuellen Fadenkreuz. Dazu muss die Meshtastic-ID des lokalen Geräts bekannt sein. Ist Map point sharing aktiviert, wird die Nachricht auf dem dort konfigurierten Kanal gesendet. Der Empfänger benötigt dieselbe Sharing-Konfiguration; eine beschreibbare Overlay-Datei wird für den Markerimport selbst nicht verwendet.

Nachrichtenformat:

    ///MAPPOS+2|NODE_HEX|LAT|LON|COLOR_HEX|SYMBOL_B64|CALLSIGN_B64|INFO_B64

Version 2 enthält exakt acht Felder. Version 1 mit sieben Feldern wird weiterhin gelesen und besitzt einen leeren Infotext. Ältere Clients, die nur Version 1 unterstützen, können Version-2-Marker nicht importieren.

| Feld | Bedeutung |
| --- | --- |
| 1 | Protokollversion: aktueller Sender 2; Empfänger akzeptiert 1 und 2 |
| NODE_HEX | Sender schreibt die Meshtastic-Node-Nummer als acht Hexzeichen ohne führendes Ausrufezeichen; der Parser akzeptiert hexadezimale UInt32-Werte |
| LAT / LON | Dezimalgrad mit Punkt; Sender schreibt sechs Nachkommastellen |
| COLOR_HEX | Farbcode 0–f, gleiche Tabelle wie bei MAP+ |
| SYMBOL_B64 | UTF-8-Mittelsymbol als normales Base64 ohne Padding |
| CALLSIGN_B64 | UTF-8-Rufname als normales Base64 ohne Padding |
| INFO_B64 | Freitext in UTF-8 als normales Base64 ohne Padding; darf leer sein; Zeilenumbrüche bleiben erhalten |

Die deklarierte Node-ID muss der tatsächlichen Meshtastic-Absender-ID entsprechen und darf nicht null sein. Koordinaten müssen endlich und innerhalb −90/+90 beziehungsweise −180/+180 liegen. Der Rufname wird getrimmt, muss 1–80 Zeichen enthalten und darf keine Zeilenumbrüche oder Tabs enthalten. Das Mittelsymbol muss ein einzelnes, nicht steuerndes BMP-Zeichen unter U+2E80 sein; Surrogate, kombinierende Nichtabstandszeichen und Formatzeichen werden verworfen. Dies umfasst auch die zusätzlich angebotenen Symbole U+2302, U+2295–U+22A1 und U+29E8–U+29F3.

Die komplette Nachricht unterliegt weiterhin dem Limit von 233 UTF-8-Bytes. Bei zu langem Rufnamen muss dieser zum Teilen gekürzt werden.

Je Meshtastic-ID existiert höchstens ein Marker. Ein weiterer Empfang für dieselbe ID ersetzt Position, Farbe, Symbol und Rufname. Es zählt die zuletzt empfangene Nachricht; es gibt keinen übertragenen Zeitstempel, keine automatische Ablaufzeit und keine spezielle Löschmeldung. Die Delete-Aktion entfernt den Marker nur lokal; ein späteres Positionsupdate kann ihn erneut anlegen.

Darstellung: ein Fadenkreuz aus zwei horizontalen Strichen je Seite und je einem senkrechten Strich darüber und darunter, mit konfigurierbarem Mittelsymbol. Positionsmarker werden nach allen anderen Kartenebenen gezeichnet und sind mit Maus und Pfeiltasten auswählbar. Leertaste zentriert den Marker; Enter oder Doppelklick öffnet dessen Details, einschließlich einer Aktion zum Öffnen gespeicherter Node-Details.

Implementierung: src/ConsoleClient/MapPositionMarker.cs, NodeMapView.cs und Program.cs.

### Erweiterte Markerbedienung

PositionMarkerInfo speichert den eigenen Freitext in den Map-Einstellungen und wird beim nächsten O-Update als INFO_B64 übertragen. Das gemeinsame Limit von 233 UTF-8-Bytes gilt für Rufname und Infotext inklusive Base64 und aller übrigen Felder. Zu lange Nachrichten werden vor dem Senden abgewiesen; es gibt keine Fragmentierung.

Die Infobox zeigt Freitext, Alter und lokale Empfangszeit des Updates sowie Entfernung und Richtung. Bezugspunkt ist der eigene manuell gesetzte Positionsmarker, andernfalls die Geräteposition. Ist keine eigene Position bekannt, erscheinen Entfernung und Richtung als nicht verfügbar. Die Altersanzeige bezieht sich auf den lokalen Empfang beziehungsweise das lokale Setzen, nicht auf einen vom Sender übertragenen Zeitpunkt.

Die Infobox bietet Center, Node details, Delete (lokal), Direct message und Close. Direct message öffnet einen Direktchat zur Marker-ID, auch ohne gespeicherten Node-Eintrag; eine Nachricht wird erst durch das normale Absenden im Chat übertragen.

L öffnet die Liste aller Marker mit Rufname, Entfernung und Update-Alter. Die Auswahl zentriert und markiert den Marker; Enter, Doppelklick oder Details öffnet die Infobox. Liste und Infobox werden einmal pro Sekunde aktualisiert; die Auswahl bleibt beim Aktualisieren anhand der Node-ID erhalten.

### Empfangsfilter, Positionsquelle und erneutes Senden

Im Position-Markers-Dialog lässt sich der Empfang von Markern separat deaktivieren.
Map point sharing muss für den Empfang weiterhin aktiv sein. Vorhandene Marker
bleiben beim Abschalten erhalten. Die eigene Position wird je nach Einstellung
vom Fadenkreuz oder vom aktuellen Geräte-GPS übernommen; ohne gültige GPS-Daten
wird im GPS-Modus kein Marker gesetzt. Die Bestätigung vor dem Setzen ist
konfigurierbar und standardmäßig aktiv. Lokales Löschen erfordert eine Bestätigung.

Send again in Map item details sendet das bestehende Kartenobjekt mit derselben
UUID erneut. Der Empfänger fügt es nur hinzu, wenn diese UUID noch nicht vorhanden
ist; das MAP+-Protokoll aktualisiert bestehende Overlay-Objekte nicht.

Suppress alerts for ///MAP messages unter Alert settings unterdrückt alle
Alert-Ausgaben für Nachrichten mit diesem Präfix, einschließlich MAP+, MAP- und
MAPPOS+. Die Nachrichten bleiben gespeichert und werden weiterhin ausgewertet.
Für wiederholte Signaltöne und Logo-Blinken werden diese Nachrichten aus der
Alert-Zählung ausgeschlossen. Diese Option ist standardmäßig deaktiviert.