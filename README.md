# OpenHD FlightLog Studio

OpenHD FlightLog Studio ist eine Avalonia-Desktopanwendung zum Importieren,
Dekodieren und Anzeigen von OpenHD-/MAVLink-Logdateien. Die Anwendung liest rohe
MAVLink-Frames aus Logdateien, speichert sie in einer lokalen SQLite-Datenbank
und zeigt Logs, Nachrichten, dekodierte Felder, automatische Variablen,
manuelle Notizen, MAVLink-Definitionen und Debug-Ereignisse in mehreren Tabs an.

## Was das Programm macht

- Importiert Logdateien wie `.oLog`, `.olog`, `.tlog`, `.bin`, `.log` und
  `.mavlink`.
- Erkennt MAVLink v1 Frames mit Startbyte `0xFE`.
- Erkennt MAVLink v2 Frames mit Startbyte `0xFD`, inklusive optionaler
  Signaturlaenge.
- Liest OpenHD-MAVLink-Header aus einem lokalen OpenHD-Repository und speichert
  daraus Message- und Felddefinitionen.
- Dekodiert importierte MAVLink-Payloads mit diesen Definitionen.
- Faellt fuer einige bekannte Standard-MAVLink-Nachrichten auf einen eingebauten
  Decoder zurueck.
- Speichert alle importierten Logs, Messages, Felder und Definitionen lokal in
  SQLite.
- Erzeugt eine OSD-Replay-Ansicht aus bekannten OpenHD-Feldern wie RSSI, SNR,
  Link Quality, Bitrate, Paketverlust und Temperatur.
- Erlaubt das Bearbeiten und Loeschen dekodierter Felder.
- Erlaubt manuelle Variablen und Notizen, die unabhaengig von einem Log
  gespeichert werden.
- Zeigt interne Import- und SQL-Schritte im Debug-Tab an.

## Projektaufbau

```text
openhd-flightLog.sln
OpenHdFlightLog/
  Program.cs                         Avalonia-Startpunkt
  App.axaml                          Globale App-Konfiguration
  Views/MainWindow.axaml             UI-Layout
  Views/MainWindow.axaml.cs          Dateiauswahl und View-spezifische Logik
  ViewModels/MainWindowViewModel.cs  Commands, UI-Zustand und Importsteuerung
  Services/FlightLogDatabase.cs      SQLite-Schema, Import und Abfragen
  Services/MavlinkParser.cs          MAVLink-v1/v2-Frameparser
  Services/DynamicMavlinkDecoder.cs  Dekodierung anhand gespeicherter Feldlayouts
  Services/MavlinkDefinitionLoader.cs Parser fuer OpenHD-MAVLink-C-Header
  Services/MavlinkMessageDecoder.cs  Kleiner Fallback-Decoder
  Services/OLogDebugSidecar.cs       Zeitdaten aus .debug.jsonl-Dateien
  Models/                            Datenobjekte fuer UI und Datenbank
```

## Voraussetzungen

- .NET SDK 9.0 oder neuer
- Windows, Linux oder macOS mit Avalonia-kompatibler Desktopumgebung
- Optional: lokaler OpenHD-Checkout, wenn OpenHD-spezifische MAVLink-Definitionen
  geladen werden sollen

Der aktuell eingestellte Standardpfad fuer OpenHD ist in
`OpenHdFlightLog/Services/MavlinkDefinitionLoader.cs` definiert:

```csharp
public const string DefaultOpenHdRoot = @"C:\Users\Raphael\Documents\GitHub\drivers_\OpenHD";
```

## Wiederherstellen, Build und Start

Im Repository-Stamm ausfuehren:

```powershell
dotnet restore
dotnet build .\openhd-flightLog.sln
dotnet run --project .\OpenHdFlightLog\OpenHdFlightLog.csproj
```

Release-Build:

```powershell
dotnet build .\openhd-flightLog.sln -c Release
```

Publish fuer Windows x64:

```powershell
dotnet publish .\OpenHdFlightLog\OpenHdFlightLog.csproj -c Release -r win-x64 --self-contained false
```

Self-contained Publish fuer Windows x64:

```powershell
dotnet publish .\OpenHdFlightLog\OpenHdFlightLog.csproj -c Release -r win-x64 --self-contained true
```

Die gebauten Dateien liegen danach unter:

```text
OpenHdFlightLog/bin/Release/net9.0/
OpenHdFlightLog/bin/Release/net9.0/win-x64/publish/
```

## Bedienung

1. Anwendung starten.
2. Optional `Load OpenHD MAVLink` klicken, damit OpenHD-spezifische Header in die
   Datenbank geladen werden.
3. `Import Log` klicken und eine Logdatei auswaehlen.
4. Im Tab `Flight Logs` ein importiertes Log auswaehlen.
5. Die Frames erscheinen rechts, die Felder zur ausgewaehlten Nachricht unten.
6. Im Tab `Variables` erscheinen dekodierte Logvariablen und manuelle Notizen.
7. Im Tab `MAVLink Definitions` koennen importierte Definitionen und Feldlayouts
   angesehen und bearbeitet werden.
8. Im Tab `Debug` sind Import-, SQL- und Definitionsschritte nachvollziehbar.

## Import-Ablauf

1. `MainWindow.axaml.cs` oeffnet den nativen Dateiauswahldialog.
2. `MainWindowViewModel.ImportLogAsync` startet den Import.
3. Falls noch keine Definitionen geladen sind, versucht das ViewModel automatisch,
   OpenHD-Header einzulesen.
4. `FlightLogDatabase.ImportLogAsync` liest die Datei als Bytes.
5. `MavlinkParser.Parse` sucht MAVLink-v1/v2-Frames.
6. `FlightLogDatabase` laedt Felddefinitionen aus SQLite.
7. `DynamicMavlinkDecoder` dekodiert jedes Paket.
8. Logdatei, Messages und Felder werden in einer SQLite-Transaktion gespeichert.
9. Die UI wird neu geladen und zeigt das importierte Log an.

## Datenbank

Die Datenbank liegt nicht im Projektordner, sondern im lokalen Benutzerprofil:

```text
%LOCALAPPDATA%\OpenHdFlightLog\flightlogs.sqlite
```

Eine sehr detaillierte Beschreibung der Datenbankanbindung, Tabellen,
Fremdschluessel, Importtransaktion und Abfragewege steht in
[`docs/DATABASE.md`](docs/DATABASE.md).

## Beispiel-Logs

Im Repository liegen Beispiel-Dateien:

- `sample_openhd.oLog`
- `sample_openhd_replay.oLog`
- `sample_openhd_drone_replay.oLog`

Zu einigen Dateien gibt es `.debug.jsonl` Sidecars. Diese enthalten Replay-
Zeitdaten und werden automatisch verwendet, wenn sie neben der Logdatei liegen.
