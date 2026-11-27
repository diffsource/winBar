# winBar

winBar passt die Windows-11-Taskleiste an. Du kannst ihren Hintergrund gestalten, Elemente ein- oder ausblenden, eine eigene Uhr anzeigen und einzelne Programme von der Taskleiste nehmen. Die App läuft im Infobereich weiter und hält die gewählten Einstellungen aktiv.

## Schnellstart

1. Installiere die [.NET 10 Desktop Runtime für x64](https://dotnet.microsoft.com/download/dotnet/10.0), falls sie noch fehlt.
2. Lade `TaskbarTweaker.zip` aus den [Releases](../../releases) herunter und entpacke sie vollständig.
3. Starte `TaskbarTweaker.exe`. Die Datei `TaskbarTap.dll` muss im selben Ordner liegen.

**Einstellungen öffnen:** Halte **Alt** gedrückt und bewege die Maus auf die Taskleiste. Über der Taskleiste erscheint **Einstellungen**. Alternativ klicke auf das winBar-Symbol im Infobereich oder drücke **Strg+Alt+S**. Ein Klick auf das Schließen-Symbol des Einstellungsfensters lässt winBar im Hintergrund weiterlaufen. Über das Kontextmenü des Infobereich-Symbols → **Beenden** schließt du die App und stellst die Taskleiste wieder her.

## Funktionen

| Bereich | Möglichkeiten |
| --- | --- |
| Aussehen | Standard, transparent, getönte Farbe, Blur oder Acrylic; Farbe und Deckkraft einstellen |
| Uhr | Eine kompakte Uhr mit Uhrzeit und Datum rechts auf der Taskleiste einblenden. Sie liegt über der Windows-Uhr; Klicks erreichen weiterhin Windows. |
| Programme | Laufende Programme oder eine EXE-Datei auswählen und deren Taskleisten-Schaltflächen ausblenden. Die Fenster bleiben geöffnet und sind weiterhin über **Alt+Tab** erreichbar. |
| Verhalten | Taskleiste automatisch ausblenden, vollständig verstecken oder winBar mit Windows starten |
| Windows-Elemente | Ausrichtung, Suche, Gruppierung, Aufgabenansicht, Widgets, Copilot, Chat/Teams, Badges, blinkende Apps, Sekunden, Desktop-Ecke und weitere Schalter |
| Infobereich | Uhr/Datum, Schnelleinstellungen, Tray-Symbole, Sprachanzeige oder den gesamten Bereich ausblenden |

Die Einstellungen für ausgeblendete Programme beziehen sich auf den **vollständigen Pfad der EXE-Datei** und gelten auch beim nächsten Start von winBar. Angeheftete Symbole bleiben angeheftet. Manche Anwendungen verwalten ihre Taskleisten-Schaltflächen selbst; bei ihnen kann das Ausblenden wirkungslos sein. Es werden keine Programme beendet oder Dateien verändert.

Unter **Aktionen** kannst du den Explorer neu starten, die Einstellungen zurücksetzen oder alle angehefteten Apps lösen. Beim Lösen der angehefteten Apps legt winBar vorher ein Backup an und zeigt dir anschließend den Speicherort und die Schritte zur Wiederherstellung.

## Hinweise

- winBar ist für **Windows 11 x64** ausgelegt. Die Oberfläche folgt dem hellen oder dunklen Windows-Modus.
- Einige Änderungen an Windows-Elementen werden erst nach einem Explorer-Neustart sichtbar.
- Die Optionen für den Infobereich und den transparenten Hintergrund greifen in die XAML-Oberfläche der Taskleiste ein. Windows-Updates können diese Funktionen beeinträchtigen. Nach einem winBar-Update solltest du den Explorer einmal neu starten, damit die neue `TaskbarTap.dll` geladen wird.
- Wenn die Taskleiste vollständig versteckt ist, öffne die Einstellungen mit **Strg+Alt+S** und deaktiviere die Option dort wieder.
- Die Einstellungen liegen in `%AppData%\TaskbarTweaker\settings.json`.

## Selbst bauen

Benötigt werden das [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) und Visual Studio mit dem **Desktopentwicklung mit C++**-Workload (MSVC x64). Führe im Projektordner aus:

```powershell
dotnet publish -c Release -o publish -p:PublishSingleFile=true --self-contained false
```

Der Build kompiliert `tap\TaskbarTap.cpp` automatisch zu `TaskbarTap.dll`. Beide Dateien aus `publish\` werden für die fertige App benötigt.

## Technik

winBar setzt den Taskleisten-Stil mit `SetWindowCompositionAttribute` und aktualisiert ihn regelmäßig, weil der Explorer ihn überschreiben kann. `TaskbarTap.dll` blendet den XAML-Hintergrund und auf Wunsch Teile des Infobereichs aus. Windows-Elemente werden über Einstellungen unter `HKCU` gesteuert. Für einzelne Programm-Schaltflächen nutzt winBar `ITaskbarList`. Das Alt-Menü und die Design-Uhr sind kleine Fenster ohne eigenen Taskleisten-Eintrag.

Die DLL wird in `explorer.exe` geladen. Sicherheitssoftware kann darauf reagieren. Die verwendeten undokumentierten Windows-Schnittstellen können sich mit Updates ändern.
