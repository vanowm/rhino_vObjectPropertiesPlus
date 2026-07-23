# vObjectProperties+  ·  v26.7.23.1803

vObjectProperties+ is a Rhino 8 and Rhino 9 plug-in that adds a per-document **Properties+** panel for inspecting and editing object geometry, attributes, annotation, rendering, and display settings.

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
| `vObjectPropertiesPlus` | Toggle the per-document **Properties+** panel. |

The panel is also available from Rhino's Panels menu and from the **Properties+** page in Object Properties.

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

- `bin/Release/net7.0-windows/vObjectPropertiesPlus.dll` for Rhino 8
- `bin/Release/net10.0-windows/vObjectPropertiesPlus.dll` for Rhino 9 Load it with Rhino's Plug-in Manager, then open **Properties+** from the Panels menu or run `vObjectPropertiesPlus`.

Runtime diagnostics are written to `vObjectPropertiesPlus.log` beside the loaded DLL. The file is cleared at startup and begins with the Rhino and plug-in versions.

## Versioning

Build versions use `yy.m.d.hmm`, derived from the newest C# source file rather than the compile time.

## License

Released under the [MIT License](LICENSE).
