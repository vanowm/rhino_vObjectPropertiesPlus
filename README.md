# vObjectProperties+  ·  v26.10.30.1305

vObjectProperties+ is a Rhino 8 and Rhino 9 plug-in that adds a per-document **Properties+** panel for inspecting and editing object geometry, attributes, annotation, rendering, and display settings.

## Features

- Reports context-sensitive geometry values such as length, total length, perimeter, radius, diameter, polygon sides, and document unit precision.
- Edits common attributes across single or multiple selections, with mixed values shown as varying.
- Provides Name, Layer, Display Color, Display Mode, Linetype, Linetype Scale, Print Color, Print Width, Section Style, and Hyperlink controls.
- Adds text font, height, alignment, style, and content controls for annotation objects.
- Edits document-stored vNest settings and custom key/value fields for individual parts or multiple selected parts, with stable group/boundary identities, display-only part-name overrides, preserved grain guides and material recipes, and resizable editors. Part renaming leaves drawing labels and group names unchanged.
- Reports occupied yardage and nested-copy counts for selected vNest tables in a material/table tree, with totals on expandable material parents and outline previews for selected materials or individual tables. Label tabs and unused stock tails do not contribute to yardage.
- Edits picture image, brightness, contrast, saturation, sharpness, transparency, alpha, and color-mask settings from either the selected-object properties or a document-wide Background images panel, using Rhino value-in-slider controls, named hide/show for contained objects, and native-image or two-point calibrated scaling that can include contained objects.
- Exposes custom render-mesh settings, shadow controls, and surface isocurve density.
- Offers a SelSame launcher when the vTools `vSelSame` command is available; matching, part-name lookup, and checked-criteria previews are handled by vTools.
- Includes Match and Details actions and stores unit-display preferences per document.
- Wraps object edits in Rhino undo records.

## Command And Panel

| Command | Purpose |
| --- | --- |
| `vObjectPropertiesPlus` | Toggle the per-document **Properties+** panel. |

The **Properties+** and document-wide **Background images** panels are available from Rhino's Panels menu. **Properties+** also appears as a page in Object Properties.

## Requirements

- Rhino 8 and Rhino 9 for Windows
- .NET 10 SDK with the .NET 7 targeting pack to build

## Build

From the repository folder:

```powershell
.\build.ps1
```

The default Release build does not require Git and never commits or pushes. Maintainers can use `.\build.ps1 -Publish` to build, create a signed semantic commit when the DLL changes, push `master`, and publish a GitHub release containing separate Rhino 8/.NET 7 and Rhino 9/.NET 10 DLLs, plus any generated `.rui` files.

## Installation

The Release plug-ins are:

- `bin/Release/Rhino8/net7.0-windows/vObjectPropertiesPlus.dll` for Rhino 8
- `bin/Release/Rhino9/net10.0-windows/vObjectPropertiesPlus.dll` for Rhino 9 Load it with Rhino's Plug-in Manager, then open **Properties+** from the Panels menu or run `vObjectPropertiesPlus`.

Runtime diagnostics are written to `vObjectPropertiesPlus.log` beside the loaded DLL. The file is cleared at startup and begins with the Rhino and plug-in versions.

## Versioning

Build versions use `yy.m.d.hmm`, derived from the newest C# source file rather than the compile time.

## License

Released under the [MIT License](LICENSE).

## Host-Isolated Installation

The installed DLLs, settings, logs and companion files live directly in bin/Release/Rhino8/net7.0-windows, bin/Release/Rhino8/net10.0-windows and bin/Release/Rhino9/net10.0-windows. Rhino 8 variants reference the Rhino 8 SDK; Rhino 9 references Rhino 9. There is no separate deployment directory or post-build copying step. Debug outputs and compiler caches remain separate by host/runtime.

For existing installations, Register.ps1 -Register validates all three DLLs, restores the per-user startup registration request, and updates plug-in/toolbar paths silently. Use -IncludeMachine as an administrator to repair existing machine-wide entries too. Previous values are backed up under backups/registration. Run registration from ordinary Windows PowerShell or from Rhino, not a Codex Windows sandbox: its registry view can differ from Rhino, so sandbox writes are refused. Confirm discovery after a normal Rhino restart; an in-process test does not verify desktop startup. Subsequent builds update the same registered files. Existing configuration values are preserved while missing defaults are added; existing toolbars are never overwritten. Close Rhino before updating a locked Release DLL, run build.ps1 once, then reopen Rhino. Use Debug for verification while Release is in use. The global material database and Rhino-native settings remain in their existing stores.

build.ps1 is a standalone Release build without Git operations; -Rebuild forces compilation when C#-derived versions are unchanged. Publishing current Release outputs runs metadata-only targets without recompiling loaded runtime variants. When compilation is required and an output is locked, the build exits successfully with a warning and preserves the pending message. Publishing remains an explicit user action. GitHub version-tag workflows validate three prebuilt DLLs and package separate Rhino 8/9 ZIPs with repository defaults, help, toolbars and notices. They never ship installed profiles or logs, and retries add only missing assets without overwriting published files.
