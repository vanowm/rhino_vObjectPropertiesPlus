using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.Versioning;
using Rhino;
using Rhino.UI;

namespace vObjectPropertiesPlus.Views;

[SupportedOSPlatform("windows")]
internal sealed class vObjectPropertiesPlusObjectPropertiesPage : ObjectPropertiesPage
{
  private vObjectPropertiesPlusPanel? _panel;
  private static Icon? _icon;

  public override string EnglishPageTitle => "Properties+";

  public override string PageIconEmbeddedResourceString => "vObjectPropertiesPlus.png";

  [Obsolete]
  public override Icon Icon => _icon ??= LoadIcon();

  public override PropertyPageType PageType => PropertyPageType.ObjectProperties;

  public override object PageControl => _panel ??= new vObjectPropertiesPlusPanel();

  public override bool ShouldDisplay(ObjectPropertiesPageEventArgs e) => true;

  public override void UpdatePage(ObjectPropertiesPageEventArgs e)
  {
    if (_panel == null)
      return;

    _panel.UpdateFromSelection(e.Document, e.Objects.ToList());
  }

  public override bool OnActivate(bool active) => true;

  public override void OnCreateParent(IntPtr parent)
  {
    base.OnCreateParent(parent);
  }

  private static Icon LoadIcon()
    => vObjectPropertiesPlusPlugIn.LoadPanelIcon();
}
