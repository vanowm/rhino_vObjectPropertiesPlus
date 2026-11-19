using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace vObjectPropertiesPlus.Helpers;

internal static class MaterialAppearance
{
  // Appearance defaults
  private static readonly Brush FallbackLinkedBrush = SystemColors.HotTrackBrush; // Use the system link color when vTools and its configurable material color are unavailable.
  internal static Brush LinkedBrush()
  {
    var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item=>item.GetName().Name=="vTools");
    var property=assembly?.GetType("vTools.MaterialAppearance")?.GetProperty("PatternSmithColor",BindingFlags.Static|BindingFlags.NonPublic);
    if(property?.GetValue(null) is System.Drawing.Color color)
      return new SolidColorBrush(Color.FromArgb(color.A,color.R,color.G,color.B));
    return FallbackLinkedBrush;
  }
  internal static void Grid(Eto.Forms.Control control,int column,Func<object?,bool> linked)
  {
    void Install()
    {
      if(control.ControlObject is not DataGrid grid||column<0||column>=grid.Columns.Count)return;
      var cell=grid.Columns[column];
      var style=new Style(typeof(DataGridCell),cell.CellStyle??grid.CellStyle??grid.TryFindResource(typeof(DataGridCell)) as Style);
      var identity=new DataTrigger {Binding=new Binding {Converter=new CatalogConverter(linked)},Value=true};
      identity.Setters.Add(new Setter(System.Windows.Controls.Control.FontWeightProperty,FontWeights.SemiBold));style.Triggers.Add(identity);
      var color=new MultiDataTrigger();
      color.Conditions.Add(new Condition(new Binding {Converter=new CatalogConverter(linked)},true));
      color.Conditions.Add(new Condition(new Binding("IsSelected"){RelativeSource=RelativeSource.Self},false));
      color.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty,LinkedBrush()));style.Triggers.Add(color);
      var inactive=new MultiDataTrigger();inactive.Conditions.Add(new Condition(new Binding {Converter=new CatalogConverter(linked)},true));
      inactive.Conditions.Add(new Condition(new Binding("IsSelected"){RelativeSource=RelativeSource.Self},true));
      inactive.Conditions.Add(new Condition(new Binding {Path=new PropertyPath(System.Windows.Controls.Primitives.Selector.IsSelectionActiveProperty),RelativeSource=RelativeSource.Self},false));
      inactive.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty,LinkedBrush()));style.Triggers.Add(inactive);
      cell.CellStyle=style;
    }
    control.Load+=(_,_)=>Install();Install();
  }
  private sealed class CatalogConverter(Func<object?,bool> read) : IValueConverter
  {
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>read(value);
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
  }
}
