using System;
using System.Linq;
using System.Runtime.Versioning;
using Rhino;
using Rhino.DocObjects;
using Rhino.UI;

namespace vObjectPropertiesPlus.Views;

[SupportedOSPlatform("windows")]
internal class vObjectPropertiesPlusLauncherPage : ObjectPropertiesPage
{
  private static readonly Guid PanelGuid = typeof(vObjectPropertiesPlusPanel).GUID;

  private readonly Eto.Forms.Panel _control = new Eto.Forms.Panel
  {
    Content = new Eto.Forms.Label { Text = "↗ Object+ Panel" }
  };

  public vObjectPropertiesPlusLauncherPage()
  {
    RhinoDoc.DeselectAllObjects += OnDeselectAll;
    RhinoDoc.DeselectObjects    += OnDeselect;
  }

  public override string EnglishPageTitle => "Object+";
  public override object PageControl => _control;

  public override System.Drawing.Icon PageIcon(System.Drawing.Size sizeInPixels)
    => vObjectPropertiesPlusPlugIn.LoadPanelIcon();

  public override bool ShouldDisplay(ObjectPropertiesPageEventArgs e) => true;

  public override bool OnActivate(bool active)
  {
    if (active)
      try { Panels.OpenPanel(PanelGuid); } catch { }
    return base.OnActivate(active);
  }

  private void OnDeselectAll(object? sender, RhinoDeselectAllObjectsEventArgs e)
  {
    CloseIfNothingSelected(e.Document);
  }

  private void OnDeselect(object? sender, RhinoObjectSelectionEventArgs e)
  {
    CloseIfNothingSelected(e.Document);
  }

  private static void CloseIfNothingSelected(RhinoDoc? doc)
  {
    if (doc == null || doc.Objects.GetSelectedObjects(false, false).Any()) return;
    try { Panels.ClosePanel(PanelGuid); } catch { }
  }
}

