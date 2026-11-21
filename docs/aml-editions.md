# AutomationML editions in AMLOpcUa

AMLOpcUa works with documents of AutomationML Edition 2 and leaves documents
of other editions alone (since 0.5.2). The AutomationML Editor for Edition 3
references by ID instead of by path; what AMLOpcUa writes is Edition 2, and
importing the OPC UA types into an Edition 3 document gave 150796 violations in
the editor's beta (Rainer Drath, November 2026). For users:
[the manual](manual.md#automationml-editions).

## The rule

`dotnet/OpcUaAml.Core/Import/AmlEditions.cs`:

| Edition | CAEX `SchemaVersion` | `SuperiorStandardVersion` |
|---|---|---|
| 1 | `2.15` | any |
| 2 | `3.0` | `AutomationML 2.x`, or none |
| other | anything else | `AutomationML 3.x` and later |

- The major version counts, not the text: the editor writes
  `AutomationML 2.10`, Aml.Engine `AutomationML 2.1`, some files
  `AutomationML 2.11`.
- CAEX 3.0 without `SuperiorStandardVersion` is Edition 2.
- One `SuperiorStandardVersion` of another AutomationML major version is
  enough for "other"; standards other than AutomationML do not count.
- The elements are read from the XML (`CAEXFile.Node`), trimmed, without
  regard to case.

Run over 847 documents of seven repositories, the rule found 544 of Edition 2,
302 of Edition 1 and one other, a deliberately broken test file with CAEX 3.1.

## Where it is checked

| Place | What happens for another edition |
|---|---|
| `OpcUaPlugin.Attach`, which both `DocumentLoaded` and the lookup of a view created later go through | The plugin keeps no reference to the document, so every command that needs one is off; live values and the document server of the document before stop; one log line |
| `OpcUaPlugin.UpdateEdition` | The line above the tabs names the edition, or warns on the band `Ua.Warning` (`ThemePalette`) |
| `LibraryMerger.Merge` | `AmlEditions.Require`: importing into the document is refused |
| `Documents.Load` of `uaaml` | Every command refuses the document with exit code 1 |

Edition 1 keeps its handling: the line names it, the plugin offers a CAEX 3.0
copy, and the command line converts or refuses it per command.

## Tests

`dotnet/OpcUaAml.Tests/AmlEditionsTests.cs` (the rule and the merge),
`CliTests.A_document_of_another_AutomationML_edition_is_refused`,
`Aml.Editor.Plugin.OpcUa.Tests/EditionTests.cs` (the line and the commands
for Edition 2, Edition 1, another edition, another edition followed by
Edition 2, and a closed document).
