# Testing in the real AutomationML Editor

Scripts that drive a running AutomationML Editor with the plugin installed, the
way a person would, and take the pictures of the user manual
(`docs/manual/`). They were used for the editor rounds of October and
November 2026 (import timing, instances after Apply, the manual).

| File | What it does |
|---|---|
| `uia.ps1` | UI Automation helpers: find, invoke, select, expand, pictures of a window |
| `open-file.ps1` | File > Open > Open AML File; fills the file dialog by window messages. A message "The PlugIn AMLOpcUa raised an exception!" ends it with exit code 3 |
| `editor.ps1` | The plugin's controls by name (`ById`), dialogs, file dialogs, pictures cut to an element (`Shot`, `PluginShot`), starting the editor, hiding its side panes, and saving and restoring what a session changes |

## A session

```powershell
. .\test\editor\editor.ps1
Save-EditorState C:\temp\editor-state       # before anything else
Start-Editor -English                        # AMLOPCUA_LANGUAGE=en for English pictures
Open-Doc C:\temp\Plant.aml                   # a CAEX 3.0 document
Hide-SidePanes; Show-Plugin; Tab 'NamespacesTab'
PluginShot 'namespaces'                      # docs/manual/namespaces.png
# ... close the editor ...
Restore-EditorState C:\temp\editor-state
```

The editor is `C:\Dev\91_AMLEditor\AMLEditor.exe` unless `AML_EDITOR` names
another one.

## What a session changes in the editor

The editor keeps state of its own, and a session changes it:

- **Layout** (`customLayout.xml`): hidden side panes are saved when the editor
  is closed normally.
- **Settings** (`config.xml`): the CAEX version follows the last document
  opened.
- **Backup files**: an editor stopped hard leaves one per open document, and
  the next start asks about them.
- **The plugin's settings** (`%APPDATA%\AMLOpcUa\settings.json`).

`Save-EditorState` and `Restore-EditorState` put all of it back; backup files
that were there before the session stay.

## Limits

- The editor does not take a selection in its own trees from UI Automation,
  only from a real mouse click, and Windows may refuse to bring it to the front
  for one.
- The overflow of a WPF tool bar is not reachable through UI Automation.
- The plugin's dialogs are windows of their own; `Shot` takes their picture
  from their own handle.
- Editor 6.4.3.8 has the file dialogs no longer among the windows UI
  Automation lists; `Dialog` then finds them among the process's own windows
  by their title.
- When the PlugIn Manager lists a plugin folder that is gone (an update half
  done), the editor loads no plugin at all; see the PlugIn Manager first when
  the AMLOpcUa tab is missing.
