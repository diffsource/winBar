# Taskbar Tweaker

Kleines Windows-11-Tool, um die Taskleiste anzupassen: transparent, getönt, Blur oder Acrylic,
Elemente ein- und ausblenden, automatisch ausblenden und mehr. Läuft im Tray und hält den Stil aktiv.

## Funktionen

**Aussehen**
- Stil: Standard, komplett transparent, Farbe (getönt), Blur, Acrylic
- Farbe und Deckkraft wählbar

**Verhalten**
- Taskleiste automatisch ausblenden
- Taskleiste komplett verstecken, solange die App läuft
- Mit Windows starten (im Tray)

**Elemente**
- Ausrichtung (links/Mitte), Suche, Gruppieren/Beschriftung
- Aufgabenansicht, Widgets, Copilot, Chat/Teams ein/aus
- Badges, blinkende Apps, Sekunden in der Uhr, Desktop-anzeigen-Ecke
- Taskleiste auf allen Monitoren, „Task beenden“ im Rechtsklickmenü

**Aktionen**
- Explorer neu starten
- Alles auf Standard zurücksetzen
- Alle angehefteten Apps lösen (mit Backup)

Die Oberfläche folgt dem hellen bzw. dunklen Modus von Windows.

## Installation

1. [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) installieren, falls noch nicht vorhanden.
2. `TaskbarTweaker.zip` aus den [Releases](../../releases) herunterladen und entpacken.
3. `TaskbarTweaker.exe` starten. `TaskbarTap.dll` muss im selben Ordner liegen.

Schließen des Fensters minimiert in den Tray. Beenden über Rechtsklick auf das Tray-Icon → „Beenden“;
dabei wird die Taskleiste wiederhergestellt.

## Wie es funktioniert

- Der Stil wird über die undokumentierte API `SetWindowCompositionAttribute` gesetzt (wie bei TranslucentTB)
  und alle 100 ms erneut angewendet, weil Explorer ihn oft zurücksetzt.
- Windows 11 zeichnet den Taskleisten-Hintergrund in XAML über das Fenster. `TaskbarTap.dll` wird dafür
  per `InitializeXamlDiagnosticsEx` in `explorer.exe` geladen und blendet diesen Hintergrund aus.
- Die Elemente-Einstellungen sind normale Registry-Werte unter `HKCU`. Manche greifen erst nach einem
  Explorer-Neustart.

Hinweis: Weil eine DLL in den Explorer geladen wird, kann Antivirus-Software anschlagen.
Undokumentierte APIs können mit Windows-Updates aufhören zu funktionieren.

## Selbst bauen

Voraussetzungen: .NET 10 SDK und Visual Studio mit C++-Workload (MSVC x64), für `TaskbarTap.dll`.

```
dotnet publish -c Release -o publish -p:PublishSingleFile=true --self-contained false
```

Der Build ruft `tap\build.cmd` automatisch auf, um die DLL zu kompilieren.
Ergebnis liegt in `publish\`.
