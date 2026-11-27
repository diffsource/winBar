# winBar — eine ruhige Taskleiste für Windows 11

[![Build](https://github.com/diffsource/winBar/actions/workflows/build.yml/badge.svg)](https://github.com/diffsource/winBar/actions/workflows/build.yml)

winBar macht die Windows-11-Taskleiste klarer: weniger Ablenkung, ein passender Hintergrund und nur die Elemente, die du gerade brauchst. Die kleine App läuft lokal im Infobereich. Es gibt kein Konto und keine Telemetrie.

## In 30 Sekunden startklar

1. Installiere bei Bedarf die [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Lade `TaskbarTweaker.zip` aus den [Releases](../../releases) herunter und entpacke die ZIP-Datei vollständig.
3. Starte `TaskbarTweaker.exe`. `TaskbarTap.dll` muss daneben liegen.

Halte **Alt** und bewege die Maus auf die Taskleiste: Ein kleiner **Einstellungen**-Button erscheint. Du kannst das Fenster auch mit **Strg+Alt+S** oder über das winBar-Symbol im Infobereich öffnen. Das Schließen des Fensters lässt winBar weiterlaufen; **Beenden** im Kontextmenü des Infobereich-Symbols stellt die Taskleiste wieder her.

## Die wichtigsten Funktionen

| Funktion | So hilft sie dir |
| --- | --- |
| **Clean-Profil** | Blendet Suche, Widgets, Aufgabenansicht, Badges und Tray-Symbole mit einem Klick aus. Ein zweiter Klick stellt deine vorherigen Werte wieder her. |
| **Angeheftete Apps auf Abruf** | Verbirgt angeheftete Apps ohne laufendes Fenster. Mit **Alt** erscheinen sie; nach dem Loslassen bleiben sie sichtbar, bis die Maus die Taskleiste verlässt. Laufende Apps bleiben sichtbar. |
| **Drei Schnell-Looks** | **Clear** für Transparenz, **Frost** für Blur und **Solid** für einen ruhigen, getönten Hintergrund. Farbe und Deckkraft lassen sich anschließend frei ändern. |
| **Kontext-Stil** | Wähle einen anderen Taskleisten-Stil für maximierte Fenster, damit Icons auf jedem Hintergrund lesbar bleiben. |
| **Design-Uhr** | Zeige rechts nur die Uhrzeit oder Uhrzeit und Datum. Auf Wunsch erscheint sie nur auf dem Hauptmonitor. Klicks erreichen weiterhin die Windows-Uhr. |
| **Programme ausblenden** | Wähle laufende Programme oder eine EXE-Datei aus. Ihre Fenster bleiben geöffnet und sind über **Alt+Tab** erreichbar. |

Dazu kommen die vorhandenen Schalter für Ausrichtung, Gruppierung, Suche, Widgets, Copilot, Chat/Teams, Sekunden, automatische Ausblendung und weitere Windows-Elemente. Unter **Aktionen** kannst du den Explorer neu starten, Einstellungen zurücksetzen oder angeheftete Apps mit vorherigem Backup lösen.

## Gut zu wissen

- **Angeheftete Apps auf Abruf** ist experimentell. winBar erkennt die Schaltflächen über den XAML-Zustand der Windows-11-Taskleiste. Ändert Windows diesen Aufbau, kann die Funktion ausfallen. Dann hilft oft ein Explorer-Neustart nach einem winBar-Update.
- Das Ausblenden **einzelner Programme** betrifft laufende Fenster anhand des vollständigen EXE-Pfads. Angeheftete Symbole bleiben angeheftet. Manche Apps verwalten ihre Schaltflächen selbst und lassen sich darüber nicht ausblenden.
- Die **Design-Uhr** liegt über der Windows-Uhr. Bei Vollbildanwendungen wird sie ausgeblendet.
- Das **Clean-Profil** merkt sich deine vorherigen Werte auch über einen Neustart von winBar. Solange es aktiv ist, sind die betroffenen Einzelschalter gesperrt.
- Bei vollständig versteckter Taskleiste öffnet **Strg+Alt+S** die Einstellungen, damit du sie wieder einblenden kannst.
- Einige Windows-Schalter werden erst nach einem Explorer-Neustart sichtbar. Die Einstellungen liegen unter `%AppData%\TaskbarTweaker\settings.json`.

## Selbst bauen

Du brauchst das [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) und Visual Studio mit **Desktopentwicklung mit C++** (MSVC x64). Im Projektordner:

```powershell
dotnet publish -c Release -o publish -p:PublishSingleFile=true --self-contained false
```

Der Build erstellt `TaskbarTap.dll` aus `tap\TaskbarTap.cpp`. Die fertige App besteht aus `publish\TaskbarTweaker.exe` und `publish\TaskbarTap.dll`. GitHub Actions baut beide Dateien auf Windows und stellt sie als Build-Artefakt bereit.

## Wie winBar arbeitet

winBar setzt den Hintergrund mit `SetWindowCompositionAttribute`. Eine kleine DLL im Explorer passt die XAML-Ebene der Taskleiste an und blendet auf Wunsch Infobereich- oder angeheftete App-Elemente aus. Die normalen Windows-Schalter liegen unter `HKCU`; für einzelne laufende App-Schaltflächen nutzt winBar `ITaskbarList`.

Die App prüft die Alt-Interaktion alle 100 ms. Hintergrund-Stil und Explorer-DLL werden nur bei Änderungen oder als kurze Rückfallprüfung aktualisiert; laufende Programme werden alle zwei Sekunden geprüft. Die DLL wird in `explorer.exe` geladen und kann deshalb Sicherheitssoftware auslösen. Undokumentierte Windows-Schnittstellen können sich mit Updates ändern.
