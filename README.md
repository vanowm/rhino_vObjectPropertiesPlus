# vPropertiesPlus  ·  v26.7.23.125

vPropertiesPlus is a Rhino plug-in that adds a custom section to the built-in Properties panel.

It displays extra metrics for current object selection:

- Selected object count
- Curve count
- Total curve length
- Circle count
- Radius and diameter (when selected circles share the same radius)

## Build

From this folder:

```powershell
.\build.ps1
```

This standalone Release build does not require Git and never commits or pushes. Repository maintainers can use `.\build.ps1 -Publish` for the semantic-message, signed-commit, and push workflow used by the normal VS Code build task. Output is written to `bin/Release/net7.0-windows/`.

## Install in Rhino 8

1. In Rhino, run `_PluginManager`.
2. Click Install and choose `vPropertiesPlus.dll`.
3. Select one or more objects and open the Properties panel.
4. Click the `Properties+` page.

## Notes

- The project references RhinoCommon from `$(ProgramFiles)\Rhino 8\System\RhinoCommon.dll`.
- If Rhino is installed in a different path, update the hint path in `vPropertiesPlus.csproj`.
