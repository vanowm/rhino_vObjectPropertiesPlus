using System;
using System.Runtime.Versioning;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace vObjectPropertiesPlus.Commands;

[SupportedOSPlatform("windows")]
public class ObjectPlusPanelCommand : Command
{
  public override string EnglishName => "vObjectPropertiesPlus";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var panelGuid = typeof(Views.vObjectPropertiesPlusPanel).GUID;
    var wasVisible = Panels.IsPanelVisible(panelGuid);
    
    if (!wasVisible)
    {
      Panels.OpenPanel(panelGuid);
    }
    else
    {
      Panels.ClosePanel(panelGuid);
    }
    
    return Result.Success;
  }
}
