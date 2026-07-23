# vPropertiesPlus  ·  v26.7.20.1501

vPropertiesPlus is a Rhino plug-in that adds a custom section to the built-in Properties panel.

It displays extra metrics for current object selection:

- Selected object count
- Curve count
- Total curve length
- Circle count
- Radius and diameter (when selected circles share the same radius)

## Build

1. Open `vPropertiesPlus.csproj` in Visual Studio 2022 or newer.
2. Build in `Release` mode.
3. Output is `vPropertiesPlus.dll` (and a versioned `vPropertiesPlus.<version>.dll`) in the build output folder.

## Install in Rhino 8

1. In Rhino, run `_PluginManager`.
2. Click Install and choose `vPropertiesPlus.dll`.
3. Select one or more objects and open the Properties panel.
4. Click the `Properties+` page.

## Notes

- The project references RhinoCommon from `$(ProgramFiles)\Rhino 8\System\RhinoCommon.dll`.
- If Rhino is installed in a different path, update the hint path in `vPropertiesPlus.csproj`.
