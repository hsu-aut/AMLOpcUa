# AutomationML editions in a plugin

How AMLOpcUa tells which AutomationML edition a document is written for, works
with the one it was made for and refuses the others, and what it took. Written
for authors of AutomationML Editor plugins and of tools on Aml.Engine who want
to do the same; the code is MIT licensed like the rest of this repository.

## Why

The AutomationML Editor for AutomationML Edition 3 references by ID instead of
by path. A plugin made for Edition 2 keeps working in it at first sight, but
everything it writes is Edition 2: importing the OPC UA types into an Edition 3
document gave 150796 violations in the editor's beta (Rainer Drath, November
2026). Every plugin should therefore

1. find out which edition the open document is written for,
2. show it,
3. work only with the edition it was made for, and say so for any other.

## Telling the editions apart

| Edition | CAEX `SchemaVersion` | `SuperiorStandardVersion` |
|---|---|---|
| 1 | `2.15` | (any) |
| 2 | `3.0` | `AutomationML 2.x`, or none |
| other, Edition 3 among them | anything else | `AutomationML 3.x` and later |

What we met in 847 documents of seven repositories (examples, test data,
documents of the AutomationML Editor and of Aml.Engine):

- **Several spellings of Edition 2.** The AutomationML Editor writes
  `AutomationML 2.10`, Aml.Engine `AutomationML 2.1` for a new document, and
  some documents carry `AutomationML 2.11`. Compare the major version, never
  the whole text.
- **No `SuperiorStandardVersion` at all.** Some tools leave it out. CAEX 3.0
  without it is Edition 2's schema; we count it as Edition 2.
- **Several `SuperiorStandardVersion` elements.** One of another AutomationML
  major version is enough to refuse. Standards other than AutomationML beside
  it do not count.
- **Spacing and case.** Trim, and match `AutomationML` without regard to case.

The rule in code (`dotnet/OpcUaAml.Core/Import/AmlEditions.cs`):

```csharp
public enum AmlEdition { Edition1, Edition2, Other }

public static AmlEditionInfo Of(CAEXDocument document)
{
    var file = document.CAEXFile;
    var caex = file.SchemaVersion ?? "";
    var standards = file.Node.Elements()
        .Where(e => e.Name.LocalName == "SuperiorStandardVersion")
        .Select(e => e.Value.Trim())
        .Where(s => s.Length > 0)
        .ToList();
    var edition = caex switch
    {
        "2.15" => AmlEdition.Edition1,
        "3.0" when standards.All(s => MajorOf(s) is null or 2) => AmlEdition.Edition2,
        _ => AmlEdition.Other,
    };
    return new AmlEditionInfo(edition, caex, standards);
}

// "AutomationML 2.10" -> 2; null for a standard other than AutomationML.
static int? MajorOf(string standard)
{
    var m = Regex.Match(standard.Trim(), @"^AutomationML\s+(\d+)", RegexOptions.IgnoreCase);
    return m.Success && int.TryParse(m.Groups[1].Value, out var major) ? major : null;
}
```

Read the elements from the XML (`CAEXFile.Node`) rather than through a typed
property: CAEX 2.15 and 3.0 model `SuperiorStandardVersion` differently, and
the XML is the same in both.

Once the engine of Edition 3 tells the edition itself, ask it instead and keep
this rule for older engines.

## Where to check in an editor plugin

**Find every way a document reaches the plugin.** In AMLOpcUa there are two:

- `DocumentLoaded`, which the editor calls when it opens a document;
- a plugin view created after the document was opened never hears of it, and
  looks the document up itself (`DocumentDiscoverer`).

Both end in one method, `Attach`. The check sits there and nowhere else.

**Treat a document of another edition as no document.** AMLOpcUa keeps no
reference to it for its commands (`_document` stays null). Every command that
reads or changes the document then stays off by the same condition it already
had for "no document open": import, new instance, alarms, check, export,
UAFX Descriptor, take into document, binding, live values, serving the
document. Commands that need no document (connect to a server, browse, watch,
write, call) keep working. This is safer than checking the edition in each
command: a command added later is covered without anyone remembering.

**Stop what belonged to the document before.** Live values and the document
server were bound to the previous document; opening one of another edition
stops them, as closing would.

**Keep Edition 1 as it was.** AMLOpcUa already refused to put OPC UA
libraries into a CAEX 2.15 document and offers a CAEX 3.0 copy; the edition
line now names it as Edition 1.

## What the user sees

A line above the tabs names the edition of the open document:

```
AutomationML Edition 2 (AutomationML 2.10)
```

For another edition the line becomes a warning band, and the empty Namespaces
tab says what to do:

```
This document is written for AutomationML 3.0. AMLOpcUa works with
AutomationML Edition 2 (CAEX 3.0, AutomationML 2.10) only and leaves it alone.
```

The log has one line for it, so a support request can be answered from the
log. Without a document the line is hidden. The warning band takes its colour
from the editor's theme (light and dark), like the rest of the plugin.

## Outside the editor

**One gate where documents are loaded.** The command line `uaaml` loads every
document through one function (`Documents.Load`); it refuses another edition
there, with exit code 1 and a message that names the edition. Edition 1 still
loads: the commands that need CAEX 3.0 convert it or refuse it themselves.

**A guard where the document is changed.** `LibraryMerger.Merge`, which puts
the OPC UA libraries into a document, calls `AmlEditions.Require` first. Code
that uses the library directly cannot write Edition 2 libraries into another
edition either.

## Tests

| Case | Expected |
|---|---|
| CAEX 3.0, `AutomationML 2.10` / `2.1` / ` AutomationML 2.10 ` / `automationml 2.10` | Edition 2 |
| a new document of Aml.Engine | Edition 2 |
| CAEX 3.0 without `SuperiorStandardVersion` | Edition 2 |
| CAEX 3.0, `AutomationML 3.0` / `3` / `4.1` | other |
| `AutomationML 2.10` and `AutomationML 3.0` | other |
| `AutomationML 2.10` and another standard | Edition 2 |
| CAEX 2.15 | Edition 1 |
| merge into another edition | refused, message names the edition and Edition 2 |
| command line on another edition | exit code 1, same message |
| plugin, Edition 2 document | line names it, commands on |
| plugin, another edition | warning, document commands off, server commands on |
| plugin, Edition 2 after another edition | commands on again |
| plugin, Edition 1 document | line names Edition 1 |
| plugin, document closed | no line |

Before releasing, run the rule over every document you have: a plugin that
refuses an Edition 2 document is worse than one that accepts an Edition 3 one.
AMLOpcUa's 847 documents gave 544 of Edition 2, 302 of Edition 1 and one
other: a deliberately broken test file with CAEX 3.1.

## Checklist for another plugin

1. Copy the rule (`AmlEditions.cs`, about 40 lines) or call it from
   `OpcUaAml.Core` if your plugin uses that library.
2. Find every way a document reaches the plugin and route them through one
   method; put the check there.
3. For another edition keep no reference to the document; let the existing
   "no document" conditions switch the commands off.
4. Stop anything still bound to the document before.
5. Show the edition, and a warning for another one; log it once.
6. Check where a headless tool loads documents and where it changes them.
7. Translate the new texts (AMLOpcUa: `Localization/De.cs`).
8. Write the tests above, and run the rule over all documents you have.

## What changed in AMLOpcUa (0.5.2)

| File | Change |
|---|---|
| `dotnet/OpcUaAml.Core/Import/AmlEditions.cs` | new: the rule, `Require` with the message |
| `dotnet/OpcUaAml.Core/Import/LibraryMerger.cs` | `Merge` calls `AmlEditions.Require` instead of checking the CAEX version |
| `dotnet/OpcUaAml.Tool/Program.cs` | `Documents.Load` refuses another edition for every command |
| `Aml.Editor.Plugin.OpcUa/OpcUaPlugin.xaml.cs` | `Attach` is the gate; `UpdateEdition` shows the line; closing clears it |
| `Aml.Editor.Plugin.OpcUa/OpcUaPlugin.xaml` | the line above the tabs (`EditionBand`, `EditionText`) |
| `Aml.Editor.Plugin.OpcUa/ThemePalette.cs` | `Ua.Warning`, the warning band's colour for both themes |
| `Aml.Editor.Plugin.OpcUa/Localization/De.cs` | five German texts |
| `dotnet/OpcUaAml.Tests/AmlEditionsTests.cs`, `CliTests.cs` | the rule, the merge guard, the command line |
| `Aml.Editor.Plugin.OpcUa.Tests/EditionTests.cs` | the plugin's line and commands |
