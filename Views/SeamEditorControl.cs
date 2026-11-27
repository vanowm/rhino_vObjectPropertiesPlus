using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using vObjectPropertiesPlus.Helpers;

namespace vObjectPropertiesPlus.Views;

internal sealed class SeamEditorControl : Panel
{
  private const int LabelWidth = 67; // Width in layout pixels for the seam field captions.
  private const int RowSpacing = 3; // Spacing in layout pixels between seam settings.
  private const int IconButtonWidth = 28; // Width in layout pixels for the icon-only Flip Side button.
  private const bool DefaultWholePart = false; // false changes picked edge widths; true changes every edge on a linked boundary.
  private readonly TextBox _width = new() { ToolTip = "Allowance width in drawing units" };
  private readonly TextBox _angle = new() { ToolTip = "Maximum corner angle to chamfer, in degrees" };
  private readonly CheckBox _chamfer = new() { Text = "Chamfer", ThreeState = true, ToolTip = "Clip corners up to the angle shown. Mixed keeps each allowance's setting." };
  private readonly Button _flip = new() { Width = IconButtonWidth, ToolTip = "Flip side of the selected open seam allowances" };
  private readonly CheckBox _wholePart = new() { Text = "Whole part", Checked = DefaultWholePart, ToolTip = "Include all edges of each selected seam" };
  private readonly Label _stateLabel = new();
  private readonly Label _message = new() { Wrap = WrapMode.Word, Visible = false };
  private readonly Button _layer = new() { ToolTip = "Choose the seam output layer" };
  private readonly Button _enable = new() { Text = "Enable", ToolTip = "Add a seam allowance to unlinked curves" };
  private readonly Button _apply = new() { Text = "Apply", ToolTip = "Apply only the edited settings" };
  private readonly Button _detach = new() { Text = "Detach", ToolTip = "Keep the allowance but stop following its source" };
  private readonly Button _reattach = new() { Text = "Reattach", ToolTip = "Rebuild a detached allowance and resume tracking" };
  private readonly Button _remove = new() { Text = "Remove", ToolTip = "Delete the allowance and keep the source curves" };
  private RhinoDoc? _doc;
  private Guid[] _ids = [];
  private string? _layerPath;
  private string? _stateJson;
  private SelectionState? _state;
  private Assembly? _toolsAssembly;
  private Func<RhinoDoc, Guid[], string>? _read;
  private Func<RhinoDoc, Guid[], string, string, string>? _write;
  private Func<RhinoDoc, string, string?>? _pickLayer;
  private bool _collapsed;
  private bool _busy;

  private sealed record SelectionState(int Available, int Attached, int Detached, bool Open,
    double? Width, bool? Chamfer, double? Angle, string? Layer, bool? CanFlip = null);
  private sealed record EditResult(int Changed, string Message);

  internal SeamEditorControl(Image? flipIcon)
  {
    _flip.Image = flipIcon;
    _flip.Text = flipIcon == null ? "\u21C4" : string.Empty;
    var angle = new TableLayout
    {
      Spacing = new Size(4, 0),
      Rows = { new TableRow(new TableCell(_angle, true), new TableCell(new Label { Text = "\u00B0", VerticalAlignment = VerticalAlignment.Center })) }
    };
    var settings = new TableLayout
    {
      Spacing = new Size(4, RowSpacing),
      Rows =
      {
        Setting("Width", _width),
        Setting("Layer", _layer),
        new TableRow(new TableCell(_chamfer), new TableCell(angle, true))
      }
    };
    Content = new StackLayout
    {
      Padding = new Padding(4, 0, 4, 4),
      Spacing = RowSpacing,
      HorizontalContentAlignment = HorizontalAlignment.Stretch,
      Items =
      {
        _stateLabel,
        settings,
        Row(_wholePart),
        Row(_enable, _apply, _remove),
        Row(_detach, _reattach, _flip),
        _message
      }
    };
    _chamfer.CheckedChanged += (_, _) => UpdateChamferAngle();
    _flip.Click += (_, _) => ExecuteRequest("Apply", "{\"Flip\":true}", refreshFields: false);
    _layer.Click += (_, _) => ChooseLayer();
    foreach (var button in new[] { _enable, _apply, _remove, _detach, _reattach })
      button.Click += (_, _) => Execute(button.Text);
  }

  private static TableRow Setting(string caption, Control control) =>
    new(new TableCell(new Label { Text = caption, Width = LabelWidth }), new TableCell(control, true));

  private TableLayout Row(params Control[] controls)
  {
    var row = new TableRow(controls.Select(control => new TableCell(control, control != _flip)));
    return new TableLayout { Spacing = new Size(4, RowSpacing), Rows = { row } };
  }

  internal bool Update(RhinoDoc? doc, IReadOnlyList<RhinoObject> objects)
  {
    if (_busy) return _ids.Length > 0 && _read != null;
    var ids = objects.Where(obj => obj.Geometry is Curve).Select(obj => obj.Id).Distinct().Order().ToArray();
    if (!ReferenceEquals(doc, _doc) || !_ids.SequenceEqual(ids))
    {
      _stateJson = null;
      _message.Visible = false;
    }
    _doc = doc;
    _ids = ids;
    if (doc == null || ids.Length == 0 || !Connect()) return false;
    if (!_collapsed) Refresh();
    return true;
  }

  internal void SetCollapsed(bool collapsed)
  {
    _collapsed = collapsed;
    if (!collapsed) Refresh();
  }

  private bool Connect()
  {
    if (_toolsAssembly != null) return _read != null && _write != null && _pickLayer != null;
    _toolsAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "vTools");
    var bridge = _toolsAssembly?.GetType("vTools.Commands.vSeam");
    try
    {
      _read = bridge?.GetMethod("GetSeamSelection", [typeof(RhinoDoc), typeof(Guid[])])
        ?.CreateDelegate<Func<RhinoDoc, Guid[], string>>();
      _write = bridge?.GetMethod("ApplySeamSelection", [typeof(RhinoDoc), typeof(Guid[]), typeof(string), typeof(string)])
        ?.CreateDelegate<Func<RhinoDoc, Guid[], string, string, string>>();
      _pickLayer = bridge?.GetMethod("PickSeamOutputLayer", [typeof(RhinoDoc), typeof(string)])
        ?.CreateDelegate<Func<RhinoDoc, string, string?>>();
    }
    catch (Exception ex) { Log.Write("Seam", $"Cannot connect to vSeam: {ex.Message}"); }
    return _read != null && _write != null && _pickLayer != null;
  }

  private void Refresh(bool force = false)
  {
    if (_doc == null || _read == null || _ids.Length == 0 || _busy) return;
    try
    {
      string json = _read(_doc, _ids);
      if (!force && json == _stateJson) return;
      _state = JsonSerializer.Deserialize<SelectionState>(json);
      if (_state == null) return;
      _stateJson = json;
      _stateLabel.Text = _state.Attached + " linked" + (_state.Detached > 0 ? $" | {_state.Detached} detached" : "");
      _width.Text = Format(_state.Width);
      _angle.Text = Format(_state.Angle);
      _width.PlaceholderText = _state.Width == null ? "Varies" : "";
      _angle.PlaceholderText = _state.Angle == null ? "Varies" : "";
      _layerPath = _state.Layer;
      _layer.Text = _layerPath ?? "Varies";
      _chamfer.Checked = _state.Chamfer;
      UpdateChamferAngle();
      _flip.Enabled = _state.CanFlip ?? (_state.Attached > 0 && _state.Open);
      _enable.Enabled = _state.Available > 0;
      _apply.Enabled = _detach.Enabled = _state.Attached > 0;
      _reattach.Enabled = _state.Detached > 0;
      _remove.Enabled = _state.Attached + _state.Detached > 0;
      _wholePart.Enabled = _remove.Enabled;
    }
    catch (Exception ex) { ShowError(ex); }
  }

  private void UpdateChamferAngle() => _angle.Enabled = _chamfer.Checked != false;

  private static string Format(double? value) => value?.ToString("G", CultureInfo.CurrentCulture) ?? "";

  private void ChooseLayer()
  {
    if (_doc == null || _pickLayer == null || _busy) return;
    _busy = true;
    try
    {
      var path = _pickLayer(_doc, _layerPath ?? "");
      if (path == null) return;
      _layer.Text = _layerPath = path;
    }
    catch (Exception ex) { ShowError(ex); }
    finally { _busy = false; }
  }

  private void Execute(string action)
  {
    if (_doc == null || _state == null || _write == null || _busy) return;
    bool settings = action is "Enable" or "Reattach" or "Apply";
    bool validWidth = TryRead(_width, _state.Width, out var width);
    double? angle = _state.Angle;
    bool validAngle = !_angle.Enabled || TryRead(_angle, _state.Angle, out angle);
    if (!validWidth || !validAngle)
    {
      if (settings) { ShowMessage("Enter valid numbers for Width and Angle."); return; }
    }
    string request = JsonSerializer.Serialize(new
    {
      Width = !settings || width == _state.Width ? null : width,
      Angle = !settings || angle == _state.Angle ? null : angle,
      Chamfer = settings && _chamfer.Checked != _state.Chamfer ? _chamfer.Checked : null,
      Layer = settings && _layerPath != _state.Layer ? _layerPath : null,
      WholeBoundary = _wholePart.Checked == true
    });
    ExecuteRequest(action, request);
  }

  private void ExecuteRequest(string action, string request, bool refreshFields = true)
  {
    if (_doc == null || _state == null || _write == null || _busy) return;
    _busy = true;
    try
    {
      var result = JsonSerializer.Deserialize<EditResult>(_write(_doc, _ids, action, request));
      if (result == null) return;
      ShowMessage(result.Message);
      if (result.Changed > 0 && refreshFields)
      {
        _busy = false;
        Refresh(force: true);
      }
    }
    catch (Exception ex) { ShowError(ex); }
    finally { _busy = false; }
  }

  private static bool TryRead(TextBox box, double? original, out double? value)
  {
    value = original;
    if (string.IsNullOrWhiteSpace(box.Text)) return true;
    if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double number) ||
      !double.IsFinite(number)) return false;
    value = number;
    return true;
  }

  private void ShowError(Exception ex)
  {
    Log.Write("Seam", ex.ToString());
    ShowMessage(ex.Message);
  }

  private void ShowMessage(string message)
  {
    _message.Text = message;
    _message.Visible = message.Length > 0;
  }
}
