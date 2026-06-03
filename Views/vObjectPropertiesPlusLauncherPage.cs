using System.Runtime.Versioning;
using Rhino.UI;

namespace vObjectPropertiesPlus.Views;

[SupportedOSPlatform("windows")]
internal class vObjectPropertiesPlusLauncherPage : ObjectPropertiesPage
{
  private readonly vObjectPropertiesPlusPanel _embeddedPanel = new vObjectPropertiesPlusPanel();

  public override string EnglishPageTitle => "Object+";
  public override object PageControl => _embeddedPanel;

  public override System.Drawing.Icon PageIcon(System.Drawing.Size sizeInPixels)
    => vObjectPropertiesPlusPlugIn.LoadPanelIcon();

  public override bool ShouldDisplay(ObjectPropertiesPageEventArgs e) => true;
}
