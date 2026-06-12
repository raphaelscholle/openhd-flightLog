# Datenbankanbindung im Detail

Dieses Dokument beschreibt kleinmaschig, wie OpenHD FlightLog Studio mit der
lokalen MySQL-Datenbank arbeitet. Der relevante Code liegt in
`OpenHdFlightLog/Services/FlightLogDatabase.cs`.

## Grundprinzip

Die Anwendung verwendet `MySqlConnector`. Beim Start prueft die App, ob ein
MySQL-Server erreichbar ist. Wenn nicht, versucht sie automatisch den
Docker-Container `openhd-flightlog-mysql` mit dem Image `mysql:8.4` zu starten
oder anzulegen.

Standardverbindung:

```text
Host: 127.0.0.1
Port: 3306
Database: openhd_flightlog
User: root
Password: openhd
```

Die Werte koennen ueber `OPENHD_MYSQL_HOST`, `OPENHD_MYSQL_PORT`,
`OPENHD_MYSQL_DATABASE`, `OPENHD_MYSQL_USER` und `OPENHD_MYSQL_PASSWORD`
ueberschrieben werden.

Beim Erzeugen von `FlightLogDatabase` passiert Folgendes:

1. Die MySQL-Verbindungsdaten werden aus Umgebungsvariablen oder Standardwerten
   aufgebaut.
2. `MySqlServerManager.EnsureServerStarted()` prueft die Serververbindung und
   startet bei Bedarf Docker.
3. `EnsureDatabase()` legt die Datenbank an, falls sie fehlt.
4. `EnsureSchema()` wird aufgerufen.
5. `EnsureSchema()` legt Tabellen, Indizes und einfache Migrationen an.

## Verbindungsoeffnung

Jede Datenbankoperation oeffnet eine neue Verbindung:

```csharp
var connection = new MySqlConnection(connectionStringBuilder.ConnectionString);
connection.Open();
```

Foreign Keys laufen ueber InnoDB. Das Schema erzeugt die Tabellen deshalb mit
`ENGINE=InnoDB`, damit `ON DELETE CASCADE` Regeln greifen.

Die Verbindung wird in den meisten Methoden mit `using var connection` erzeugt.
Dadurch wird sie am Ende der Methode automatisch geschlossen.

## Schema-Erzeugung

`EnsureSchema()` fuehrt ein grosses `CREATE TABLE IF NOT EXISTS` Skript aus.
Das bedeutet:

- Fehlt eine Tabelle, wird sie angelegt.
- Existiert eine Tabelle bereits, bleibt sie erhalten.
- Bereits importierte Daten werden beim Programmstart nicht geloescht.

Danach fuehrt `EnsureSchema()` mehrere `TryAddColumn` Aufrufe aus. Diese sind
einfache Migrationen fuer lokale Datenbanken, die mit einer aelteren Version der
App angelegt wurden.

Beispiel:

```csharp
TryAddColumn(connection, "mavlink_messages", "route", "VARCHAR(255) NOT NULL DEFAULT ''");
```

Wenn die Spalte noch fehlt, wird sie per `ALTER TABLE` hinzugefuegt. Wenn sie
schon existiert, wirft MySQL einen Fehler. Dieser wird abgefangen und nur im
Debug-Log gemeldet.

## Tabellenuebersicht

### log_files

Speichert einen Datensatz pro importierter Logdatei.

Spalten:

- `id`: Primary Key
- `file_name`: Dateiname ohne Ordner
- `path`: kompletter Pfad zur importierten Datei
- `imported_at`: Zeitpunkt des Imports als Text
- `message_count`: Anzahl erkannter MAVLink-Frames

Diese Tabelle ist die Wurzel fuer importierte Daten. Wird ein Log geloescht,
werden die zugehoerigen Nachrichten und Felder ueber Cascade-Regeln entfernt.

### message_types

Normalisiert MAVLink-Message-Typen.

Spalten:

- `id`: Primary Key
- `message_id`: fachliche MAVLink-ID, eindeutig
- `name`: Name der MAVLink-Message
- `dialect`: Dialekt, zum Beispiel `openhd`
- `description`: aktuell reserviert fuer Beschreibungstext

Warum diese Tabelle existiert: Viele importierte Pakete koennen dieselbe
MAVLink-Message-ID haben. Name und Dialekt werden deshalb einmal gespeichert und
von `mavlink_messages` referenziert.

### mavlink_messages

Speichert jeden erkannten MAVLink-Frame.

Spalten:

- `id`: Primary Key
- `log_file_id`: Foreign Key auf `log_files.id`
- `message_type_id`: Foreign Key auf `message_types.id`
- `packet_index`: laufender Index im importierten Log
- `packet_time_ms`: Replay-/Ersatzzeit in Millisekunden
- `packet_timestamp`: Replay-/Importzeit als Text
- `byte_offset`: Byteposition des Frames in der Originaldatei
- `mavlink_version`: 1 oder 2
- `sequence`: MAVLink-Sequence-Feld
- `system_id`: MAVLink-System-ID
- `component_id`: MAVLink-Component-ID
- `route`: lesbarer Ursprung, zum Beispiel `OpenHD Ground`
- `payload_length`: Laenge des Payloads
- `checksum`: CRC als Text, zum Beispiel `0x1234`
- `raw_packet_hex`: komplettes Paket als Hex-String

Wichtige Foreign Keys:

```sql
FOREIGN KEY (log_file_id) REFERENCES log_files(id) ON DELETE CASCADE
FOREIGN KEY (message_type_id) REFERENCES message_types(id)
```

Wenn ein Log geloescht wird, verschwinden seine Messages automatisch.

### message_fields

Speichert dekodierte Felder einer MAVLink-Message.

Spalten:

- `id`: Primary Key
- `message_id`: Foreign Key auf `mavlink_messages.id`
- `field_name`: Name des Feldes
- `value_text`: Wert als Text
- `numeric_value`: optionaler Zahlenwert
- `unit`: Einheit, falls bekannt

Wichtiger Foreign Key:

```sql
FOREIGN KEY (message_id) REFERENCES mavlink_messages(id) ON DELETE CASCADE
```

Wenn eine Message geloescht wird, verschwinden ihre Felder automatisch.

### user_variables

Speichert manuelle Variablen und Notizen, unabhaengig von importierten Logs.

Spalten:

- `id`: Primary Key
- `name`: Name der Variable
- `value_text`: Wert als Text
- `data_type`: frei gepflegter Typ, Standard `text`
- `notes`: Notizen

Diese Tabelle hat keine Foreign Keys. Sie bleibt erhalten, auch wenn Logs
geloescht werden.

### message_definitions

Speichert MAVLink-Message-Definitionen, die aus OpenHD-Headern gelesen wurden.

Spalten:

- `id`: Primary Key
- `message_id`: MAVLink-ID, eindeutig
- `name`: Message-Name aus dem Header
- `dialect`: Dialektordner aus dem Headerpfad
- `payload_length`: erwartete Payload-Laenge
- `crc_extra`: MAVLink CRC extra
- `source_file`: relativer Headerpfad
- `notes`: manuelle Notizen

Diese Tabelle ist die Grundlage fuer die dynamische Dekodierung.

### field_definitions

Speichert das Feldlayout einer Message-Definition.

Spalten:

- `id`: Primary Key
- `definition_id`: Foreign Key auf `message_definitions.id`
- `field_name`: Feldname
- `value_type`: MAVLink-Typ, zum Beispiel `uint8_t`, `float`, `char`
- `array_length`: Arraylaenge, `0` oder `1` fuer Einzelwerte
- `payload_offset`: Byteposition im Payload
- `unit`: Einheit
- `description`: Beschreibung aus dem Header, falls vorhanden

Wichtiger Foreign Key:

```sql
FOREIGN KEY (definition_id) REFERENCES message_definitions(id) ON DELETE CASCADE
```

Wenn eine Message-Definition geloescht wird, verschwinden ihre Felddefinitionen
automatisch.

## Indizes

Das Schema legt mehrere Indizes an:

```sql
CREATE INDEX ix_mavlink_messages_log ON mavlink_messages(log_file_id);
CREATE INDEX ix_mavlink_messages_type ON mavlink_messages(message_type_id);
CREATE INDEX ix_message_fields_message ON message_fields(message_id);
CREATE INDEX ix_message_fields_name ON message_fields(field_name);
CREATE INDEX ix_field_definitions_definition ON field_definitions(definition_id);
```

Wenn ein Index bereits existiert, faengt `EnsureIndex` den MySQL-Fehler fuer
doppelte Indexnamen ab und schreibt nur einen Debug-Eintrag.

Diese Indizes beschleunigen die typischen UI-Abfragen:

- Alle Messages zu einem Log laden.
- Alle Felder zu einer Message laden.
- Alle Logvariablen ueber Message/Feld-Joins laden.
- Alle Felddefinitionen zu einer Message-Definition laden.

## Importtransaktion Schritt fuer Schritt

Der Import startet in `ImportLogAsync(string path)`.

### 1. Datei lesen

```csharp
var bytes = await File.ReadAllBytesAsync(path);
```

Die gesamte Logdatei wird als Bytearray geladen.

### 2. MAVLink-Frames extrahieren

```csharp
var frames = MavlinkParser.Parse(bytes);
```

Der Parser sucht Startbytes und erstellt `MavlinkFrame` Objekte. Noch wird
nichts in die Datenbank geschrieben.

### 3. Felddefinitionen laden

```csharp
var definitions = GetFieldDefinitionsByMessageId();
```

Die Anwendung laedt alle gespeicherten Definitionen aus
`message_definitions JOIN field_definitions` und gruppiert sie nach
`message_id`. Dadurch kann jedes Paket spaeter direkt ueber seine Message-ID
dekodiert werden.

### 4. Sidecar-Zeitdaten laden

```csharp
var packetTimings = OLogDebugSidecar.LoadPacketTimings(path);
```

Wenn neben der Logdatei eine `.debug.jsonl` Datei liegt, werden Paketzeiten
geladen. Gibt es keine Sidecar-Datei, ist das Dictionary leer.

### 5. Verbindung und Transaktion oeffnen

```csharp
using var connection = OpenConnection();
using var transaction = connection.BeginTransaction();
```

Ab hier werden alle Inserts als Einheit behandelt.

### 6. log_files Eintrag schreiben

```csharp
var logId = InsertLogFile(connection, path, frames.Count, importStarted);
```

Die Methode schreibt Dateiname, Pfad, Importzeit und Frameanzahl. Die neue
`log_files.id` kommt ueber `LastInsertedId` zurueck.

### 7. Pro Frame message_types sicherstellen

```csharp
var messageTypeId = EnsureMessageType(connection, frame.MessageId);
```

`EnsureMessageType` schaut zuerst nach einer passenden Definition in
`message_definitions`. Wenn eine Definition existiert, werden Name und Dialekt
daraus genutzt. Sonst wird ein Fallback-Name wie `MSG_123` verwendet.

Der Insert ist ein Upsert:

```sql
INSERT INTO message_types (...)
ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id), ...
```

Dadurch gibt es pro MAVLink-Message-ID nur einen Typdatensatz.

### 8. mavlink_messages Eintrag schreiben

```csharp
var messageId = InsertMessage(connection, logId, messageTypeId, frame, importStarted, timing);
```

Die Methode schreibt Paketindex, Zeit, Headerfelder, Route, Payload-Laenge,
Checksumme und das Rohpaket als Hex-String.

Zeitlogik:

- Gibt es Sidecar-Zeitdaten, werden `ElapsedMs` und `Timestamp` daraus genutzt.
- Gibt es keine Sidecar-Zeitdaten, wird `packet_index` als Ersatz fuer
  `packet_time_ms` genutzt.
- Der Ersatz-Timestamp ist Importzeit plus Paketindex in Millisekunden.

### 9. Payload dekodieren

```csharp
foreach (var field in DynamicMavlinkDecoder.Decode(frame, fieldDefinitions ?? []))
```

Wenn Felddefinitionen fuer die Message-ID vorhanden sind:

1. Felder werden nach `payload_offset` sortiert.
2. Jeder Wert wird anhand von Typ, Offset und Arraylaenge aus dem Payload
   gelesen.
3. Zahlen kommen in `value_text` und `numeric_value`.
4. Arrays und Strings kommen mindestens in `value_text`.

Wenn keine Definition vorhanden ist:

- Der Fallback-Decoder dekodiert einige bekannte Standard-MAVLink-Nachrichten.
- Wenn auch das nicht geht, wird ein Feld `payload_hex` erzeugt.

### 10. message_fields schreiben

```csharp
InsertField(connection, messageId, field);
```

Jedes dekodierte Feld wird mit Foreign Key auf `mavlink_messages.id`
gespeichert.

### 11. Commit

```csharp
transaction.Commit();
```

Erst jetzt ist der Import dauerhaft in der Datenbank. Wenn vorher eine Exception
auftritt, wird die Transaktion verworfen.

## Definitionen importieren

Definitionen werden in `ImportDefinitions` geschrieben.

Der Ablauf:

1. Verbindung oeffnen.
2. Transaktion starten.
3. Fuer jede geladene Definition `UpsertDefinition` ausfuehren.
4. Alte Felddefinitionen dieser Message loeschen.
5. Neue Felddefinitionen einfuegen.
6. Passenden `message_types` Eintrag sicherstellen.
7. Transaktion committen.

Warum Felddefinitionen geloescht und neu eingefuegt werden:

Header koennen sich aendern. Ein Feld kann einen anderen Offset, Typ oder Namen
bekommen. Ein vollstaendiger Ersatz ist einfacher und verhindert alte,
widerspruechliche Feldlayouts.

## Lesewege fuer die UI

### Logs

`GetLogs()` liest aus `log_files`, sortiert nach Importzeit und ID absteigend.
Das Ergebnis landet in `MainWindowViewModel.Logs`.

### Messages

`GetMessages(logId)` liest aus:

```sql
mavlink_messages m
JOIN message_types t ON t.id = m.message_type_id
```

Es werden maximal 5000 Messages pro Log angezeigt. Die Sortierung erfolgt nach
`packet_index`.

### Felder einer Message

`GetFields(messageId)` liest `message_fields` und joint ueber
`mavlink_messages` zu `message_types`, damit der Message-Name angezeigt werden
kann.

### Automatische Logvariablen

`GetLogVariables(logId)` liest alle Felder eines Logs ueber:

```sql
message_fields
JOIN mavlink_messages
JOIN message_types
```

Das Ergebnis wird nach Zeit, Paketindex, Message-ID und Feldname sortiert.
Diese Daten treiben auch die OSD-Replay-Aggregation.

### OSD-Replay

`GetOsdReplayFrames(logId)` erzeugt keine SQL-Tabelle. Stattdessen:

1. `GetLogVariables(logId)` laden.
2. Nach `TimeMs` und `Timestamp` gruppieren.
3. Pro Gruppe ein Dictionary aus `Route.MessageName.FieldName` bauen.
4. Bekannte OpenHD-Felder herausziehen.
5. Nur Frames behalten, die mindestens einen OSD-Wert enthalten.

## Schreibwege aus der UI

### Felder bearbeiten

`SaveField` aktualisiert `message_fields`:

- `field_name`
- `value_text`
- `numeric_value`
- `unit`

`DeleteField` loescht einen einzelnen `message_fields` Datensatz.

### Manuelle Variablen

`SaveVariable` entscheidet anhand von `Id`:

- `Id == 0`: Insert in `user_variables`
- `Id != 0`: Update in `user_variables`

Beim Insert verwendet die Methode `LastInsertedId`; beim Update bleibt die
vorhandene ID erhalten.

### Definitionen bearbeiten

`SaveDefinition` aktualisiert `message_definitions`.

`SaveDefinitionField` aktualisiert `field_definitions`.

Loeschen einer Definition entfernt wegen Cascade auch alle zugehoerigen
Felddefinitionen.

### Log loeschen

`DeleteLog(logId)` loescht nur aus `log_files`.

Durch:

```sql
FOREIGN KEY (log_file_id) REFERENCES log_files(id) ON DELETE CASCADE
```

entfernt MySQL automatisch:

- alle `mavlink_messages` dieses Logs
- ueber die naechste Cascade-Stufe alle `message_fields` dieser Messages

## Debug-Ausgaben

`FlightLogDatabase` bekommt optional einen Delegate:

```csharp
Action<DebugEventRecord>? debug
```

Bei wichtigen Schritten ruft der Service `Log(category, detail)` auf. Das
ViewModel fuegt diese Eintraege oben in `DebugEvents` ein und begrenzt die Liste
auf 1000 Eintraege.

Typische Kategorien:

- `SQL`
- `SQL READ`
- `SQL JOIN`
- `SQL WRITE`
- `IMPORT`
- `MAVLINK`

## Wichtige Eigenschaften der aktuellen Implementierung

- Es gibt keine globale Langzeitverbindung. Jede Operation oeffnet und schliesst
  ihre eigene MySQL-Verbindung.
- Foreign Keys laufen ueber InnoDB.
- Imports und Definitionsimporte laufen in Transaktionen.
- Lokale Schema-Migrationen sind einfach gehalten und bestehen aus
  `ALTER TABLE ADD COLUMN`.
- Es gibt kein Versionsfeld fuer das Datenbankschema.
- Die Datenbank wird nicht automatisch bereinigt, ausser durch explizites
  Loeschen in der UI.
- `raw_packet_hex` kann die Datenbank bei grossen Logs deutlich vergroessern,
  ist aber fuer Debugging und Nachvollziehbarkeit hilfreich.
- Einige UI-Abfragen haben Limits (`GetMessages`: 5000, `GetLogVariables`:
  20000), damit sehr grosse Logs die Ansicht nicht sofort ueberlasten.
