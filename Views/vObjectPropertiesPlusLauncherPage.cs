using System;
using System.Runtime.Versioning;
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

  public override string EnglishPageTitle => "Object+";
  public override object PageControl => _control;

  public override bool ShouldDisplay(ObjectPropertiesPageEventArgs e) => true;

  public override bool OnActivate(bool active)
  {
    if (active)
      try { Panels.OpenPanel(PanelGuid); } catch { }
    return base.OnActivate(active);
  }
}
