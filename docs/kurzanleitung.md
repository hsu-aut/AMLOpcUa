# AMLOpcUa: Kurzanleitung

AMLOpcUa ist ein Plugin für den AutomationML Editor. Es holt OPC-UA-Modelle als
AutomationML-Bibliotheken ins Dokument, legt Instanzen ihrer Typen an und
prüft sie, verbindet das Dokument mit laufenden OPC-UA-Servern, stellt das
Dokument selbst als Server bereit und schreibt es als NodeSet zurück. Diese
Seite zeigt die häufigsten Wege. Alles Weitere steht im
[Handbuch](manual.md) (englisch).

Neu bei OPC UA? Lies [OPC UA for AutomationML users](opcua-for-aml-users.md).
Neu bei AutomationML? Lies [AutomationML for OPC UA developers](aml-for-opcua-developers.md).

## Installieren

1. Schließe alle anderen Fenster des AutomationML Editors (Version 6.4 oder
   neuer).
2. Öffne **PlugIns** › **PlugIn Manager**, wähle die Quelle
   **PublicSource**, markiere **AMLOpcUa** und installiere es.
3. Starte den Editor neu und schalte das Plugin einmal ein: **PlugIns** ›
   **AMLOpcUa** › **Activate**.

Das Plugin spricht Deutsch, wenn Windows Deutsch spricht. Umstellen geht über
**Einstellungen ▾** › **Sprache**. Es braucht Dokumente in CAEX 3.0; von
einem CAEX-2.15-Dokument speichert **CAEX-3.0-Kopie speichern…** eine Kopie.

## Erst das Tutorial

**Tutorial** unten rechts in der Statusleiste öffnet sechs kurze Lektionen.
Jeder Schritt rahmt den Knopf ein, um den es geht, und hakt sich selbst ab,
sobald er erledigt ist. Nimm dafür ein neues, leeres Dokument; der erste
Schritt bietet **Übungsdokument speichern…** an.

1. Modelle ins Dokument: DI importieren, ansehen, einen Typ zeichnen.
2. Instanzen und Prüfen.
3. Die Übungsanlage: die MPS 500 als Server starten, verbinden, ihre
   Stationen übernehmen, Werte live verfolgen.
4. Eigene Elemente binden: ein Element deines Modells an eine Variable der
   Anlage binden und den Wert lesen.
5. Dein Dokument als Server.
6. Ein eigenes Modell im Modeler.

Jedes **?** im Plugin erklärt den Teil daneben in wenigen Sätzen, mit
Begriffen zum Anklicken und Verweisen auf Lektion und Handbuch.
**Anleitung** in der Statusleiste öffnet diese Seite.

## Ein OPC-UA-Modell ins Dokument holen

- **Companion Specs…** listet alle veröffentlichten Companion Specifications
  der OPC Foundation, ohne Konto. Suche nach dem Namen oder dem Kürzel:
  "Device Integration" oder "DI", "Machinery", "Robotics". Dann
  **Herunterladen und importieren**.
- **NodeSet importieren…** nimmt eine NodeSet-Datei von der Platte. Fehlen
  Modelle, die sie braucht, bietet das Plugin an, sie zu holen.

Jeder Namespace wird zu bis zu vier Bibliotheken: `ATL_`, `ICL_`, `RCL_` und
`SUC_`, je nachdem, welche Arten von Knoten er festlegt.
Ein zweiter Import desselben Namespace aktualisiert sie.

## Eine Instanz anlegen und prüfen

1. **Neue Instanz…**, Typ suchen, Namen geben, **Anlegen**. Alle
   Mandatory-Kindelemente entstehen mit, Optionale nur angehakt.
2. **Prüfen** vergleicht jede Instanz mit ihrem Typ. Ein Doppelklick auf
   einen Befund wählt das Element im Editor.

Für ein Gerät taugt DI allein nicht: DeviceType und ComponentType sind
abstrakt, und die Prüfung meldet eine Instanz davon als Fehler UA002. Nimm
den konkreten Typ einer Companion Specification für deine Geräteart, etwa
MotionDeviceType aus Robotics, oder leite im Modeler einen eigenen Typ von
DeviceType ab.

## Werte einer SPS ins vorhandene Anlagenmodell

1. Lege jeden Wert als eigenes Element an, nicht als Attribut: zum Beispiel
   ein InternalElement `Drehzahl` unter `Motor1`. Eine OPC-UA-Variable ist
   ein Knoten, und das Plugin bindet Elemente.
2. Reiter **Server**: Endpunkt eintragen, etwa `opc.tcp://sps:4840`,
   **Verbinden**, das Zertifikat des Servers prüfen und ihm vertrauen.
3. Im Adressraum die Variable wählen, **An Element binden…**, dein Element
   wählen. Das Element bekommt die Attribute NodeId und Value.
4. **Werte ins Dokument lesen** liest einmal, **Dokumentwerte live halten**
   folgt jeder Änderung. Strg+S speichert.

**Ins Dokument übernehmen** ist der andere Weg: Es legt eine Kopie der
angehakten Knoten in der Hierarchie aus **Knoten in** an und lässt dein Modell
unverändert. Namen werden dabei nie verglichen, nur NodeIds.

Lässt die SPS nur bekannte Clients zu, braucht ihr Administrator das
Zertifikat des Plugins:
`%LOCALAPPDATA%\AMLOpcUa\pki\own\certs\AMLOpcUa [<Fingerabdruck>].der`.

Keine Anlage zur Hand? **Übungsanlage** startet die MPS 500 mit simulierten
Werten auf diesem Rechner.

## Das Dokument als Server, das Dokument weitergeben

- **Bereitstellen** macht die InstanceHierarchies des Dokuments zu einem
  OPC-UA-Server, nur lesbar, standardmäßig nur für diesen Rechner
  (`opc.tcp://127.0.0.1:48400/AMLOpcUa`). **Werte simulieren** lässt die Werte
  schwanken wie in einer laufenden Anlage. Mit **im Netzwerk** erreichen ihn
  andere Rechner; einen abgewiesenen Client lässt du unter **Clients…** zu.
- **Kopie ohne Serveradressen…** speichert eine Kopie ohne Endpunkte und
  Server-URIs, zum Weitergeben. Das Protokoll nennt jede Stelle, an der noch
  ein Rechnername steht.
- **NodeSet exportieren…** schreibt das Dokument als NodeSet, oder ein
  importiertes Modell so zurück, wie es war.

## Wenn etwas nicht klappt

- **Strg+Z nimmt die Änderungen des Plugins nicht zurück.** Speichere vor
  größeren Schritten.
- **Protokoll** in der Statusleiste zeigt, was passiert ist; es zählt
  Warnungen, die du noch nicht gesehen hast.
- Ein Knopf ist grau: Meist fehlt ein Dokument in CAEX 3.0, eine Verbindung
  zum Server oder eine Auswahl im Adressraum, oder das Plugin arbeitet noch
  (der Fortschrittsbalken läuft).
- Weitere Fälle stehen im Handbuch unter
  [Troubleshooting](manual.md#troubleshooting).
