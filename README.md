# vObjectProperties+  ·  v26.7.23.125

vObjectProperties+ is a Rhino 8 plug-in that adds a per-document **Properties+** panel for inspecting and editing object geometry, attributes, annotation, rendering, and display settings.

## Features

- Reports context-sensitive geometry values such as length, total length, perimeter, radius, diameter, polygon sides, and document unit precision.
- Edits common attributes across single or multiple selections, with mixed values shown as varying.
- Provides Name, Layer, Display Color, Display Mode, Linetype, Linetype Scale, Print Color, Print Width, Section Style, and Hyperlink controls.
- Adds text font, height, alignment, style, and content controls for annotation objects.
- Exposes custom render-mesh settings, shadow controls, and surface isocurve density.
- Includes Match and Details actions and stores unit-display preferences per document.
- Wraps object edits in Rhino undo records.

## Command And Panel

| Command | Purpose |
| --- | --- |
| `ObjectPlusPanel` | Toggle the per-document **Properties+** panel. |

The panel is also available from Rhino's Panels menu and from the **Properties+** page in Object Properties.

## Requirements

- Rhino 8 for Windows
- .NET 7 SDK or newer to build

## Build

From the repository folder:

```powershell
.\build.ps1
```

The default Release build does not require Git and never commits or pushes. Maintainers can use `.\build.ps1 -Publish` to build, create a signed semantic commit when the DLL changes, push `master`, and publish a GitHub release containing the DLL and any generated `.rui` files.

## Installation

The Release plug-in is `bin/Release/net7.0-windows/vObjectPropertiesPlus.dll`. Load it with Rhino's Plug-in Manager, then open **Properties+** from the Panels menu or run `ObjectPlusPanel`.

Diagnostics are written to `logs/vObjectPropertiesPlus_debug.log` in a source checkout, with a deployed plug-in falling back to a `logs` folder beside the DLL.

## Versioning

Build versions use `yy.m.d.hmm`, derived from the newest C# source file rather than the compile time.

## License

Released under the [MIT License](LICENSE).