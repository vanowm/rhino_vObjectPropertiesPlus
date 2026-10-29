using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using vObjectPropertiesPlus.Helpers;
using Environment = System.Environment;

namespace vObjectPropertiesPlus.Views;

internal sealed class PartDataEditorControl : Panel
{
  private const int LabelWidth = 80;
  private const int FieldWidth = 90;
  private const int RowHeight = 20;
  private readonly DropDown _parts = new() { Height = RowHeight, Width = FieldWidth };
  private readonly Label _error = new() { Wrap = WrapMode.Word, TextColor = Color.FromArgb(175, 40, 35), Visible = false };
  private readonly GridView _grid = new() { AllowMultipleSelection = true, ShowHeader = true };
  private readonly TextBox _search = new() { PlaceholderText = "Search", Height = RowHeight };
  private readonly TabControl _tabs;
  private readonly Panel _nestHost;
  private readonly Panel _customHost;
  private readonly List<Action<IReadOnlyList<JsonObject>>> _fieldRefreshers = new();
  private readonly List<Control> _nestControls = new();
  private readonly Button _add;
  private readonly Button _delete;
  private readonly Button _reset;
  private readonly Bitmap _resetIcon = PictureEditorControl.CreateResetIcon(true);
  private readonly Bitmap _disabledResetIcon = PictureEditorControl.CreateResetIcon(false);
  private readonly TextBox _name = new() { Height = RowHeight, Width = FieldWidth,
    ToolTip = "Part display name only; empty restores the derived name. Drawing labels and group names are unchanged." };
  private bool _nameDirty;
  private readonly ComboBox _material = new() { Width = FieldWidth, Height = RowHeight };
  private List<RhinoObject> _selection = new();
  private IReadOnlyList<PartDataTarget> _targets = Array.Empty<PartDataTarget>();
  private RhinoDoc? _doc;
  private string? _focusedPartKey;
  private string _targetSignature = "";
  private string _recordSignature = "";
  private bool _targetsDirty = true;
  private bool _started;
  private bool _active;
  private bool _updating;
  private bool _queued;
  private bool _pending;
  private bool _nestValid;
  private bool _customValid;
  private string _editingKey = "";
  private string _editingValue = "";
  private string[] _editingTargets = Array.Empty<string>();
  private uint _editingDocSerial;
  private readonly Dictionary<string, JsonObject> _materials = new(StringComparer.OrdinalIgnoreCase);

  private sealed class FieldRow(string key, string value)
  {
    public string Key { get; set; } = key;
    public string Value { get; set; } = value;
  }

  internal PartDataEditorControl(int height, Func<string, Image?> icon)
  {
    _tabs = new TabControl { Height = height };
    _parts.Width = FieldWidth - 25;
    _material.Width = FieldWidth - 25;
    _add = ToolButton(icon("New"), "Add custom field", AddField);
    _delete = ToolButton(icon("Delete"), "Delete selected custom fields", DeleteFields);
    _reset = ToolButton(_resetIcon, "Reset nesting settings; keep part name and custom fields", ResetNesting);
    _name.TextChanged += (_, _) => { if (!_updating) _nameDirty = true; };
    _name.LostFocus += (_, _) => Run(CommitName);
    _name.KeyDown += (_, e) => { if (e.Key == Keys.Enter) { Run(CommitName); e.Handled = true; } };
    _parts.SelectedIndexChanged += (_, _) =>
    {
      if (_updating) return;
      _focusedPartKey = _parts.SelectedKey;
      if (string.IsNullOrEmpty(_focusedPartKey)) _focusedPartKey = null;
      _recordSignature = "";
      RefreshRecords();
    };

    var nest = new TableLayout { Spacing = new Size(4, 2), Padding = new Padding(2, 5) };
    AddBoolean(nest, "Included", "Included");
    AddNumber(nest, "Copies", "Copies", 1, PartDataStore.MaximumCopies, integer: true);
    _material.ToolTip = "Material name; empty uses the current vNest material";
    bool materialDirty = false;
    _material.TextChanged += (_, _) => { if (!_updating) materialDirty = true; };
    void CommitMaterial()
    {
      if (_updating || !materialDirty || _material.Text == PartDataStore.MixedValue) return;
      materialDirty = false;
      string name = PartDataStore.NormalizeMaterial(_material.Text);
      Apply(false, data =>
      {
        if ((data["Material"] == null ? "" : PartDataStore.Text(data["Material"])) == name) return;
        data["Material"] = name.Length == 0 ? null : JsonValue.Create(name);
        data["Recipe"] = name.Length > 0 && _materials.TryGetValue(name, out var recipe)
          ? PartDataStore.Clone(recipe) : null;
      });
    }
    _material.LostFocus += (_, _) => Run(CommitMaterial);
    _material.KeyDown += (_, e) => { if (e.Key == Keys.Enter) { Run(CommitMaterial); e.Handled = true; } };
    _fieldRefreshers.Add(records => { _material.Text = Common(records, data => data["Material"] == null ? "" : PartDataStore.Text(data["Material"])); materialDirty = false; });
    var recipeButton = ToolButton(icon("Edit"), "Edit the document material fallback; global presets take priority", EditRecipe);
    nest.Rows.Add(Row("Material", new StackLayout { Orientation = Orientation.Horizontal, Spacing = 2,
      Items = { new StackLayoutItem(_material, true), recipeButton } }));
    _nestControls.Add(_material); _nestControls.Add(recipeButton);
    AddChoice(nest, "Grain", "Grain", new[] { "Auto", "Force", "On", "Off" });
    var guideButton = new Button { Text = "Edit...", Height = RowHeight, Width = FieldWidth };
    guideButton.Click += (_, _) => Run(EditGuide);
    _nestControls.Add(guideButton); nest.Rows.Add(Row("Grain guide", guideButton));
    AddNullableBoolean(nest, "Obey grain", "Obey");
    AddChoice(nest, "Rotation", "Rotation", new[] { "No rotation", "Any rotation", "45", "90", "180", "Custom" });
    AddNullableBoolean(nest, "Reverse", "Reverse");
    AddNumber(nest, "Step", "Step", 0.1, 360, nullable: true);
    AddNumber(nest, "Deviation", "Deviation", 0, 90, nullable: true);
    AddNullableBoolean(nest, "Flip", "Flip");
    AddBoolean(nest, "Manual angle", "ManualRotation");
    AddNumber(nest, "Angle", "Angle", double.MinValue, double.MaxValue,
      extra: data => data["ManualRotation"] = true);
    AddBoolean(nest, "Fixed", "Fixed");
    AddNumber(nest, "Table", "Table", 1, PartDataStore.MaximumTables, integer: true, offset: 1,
      extra: data => data["Fixed"] = true);
    AddNumber(nest, "X", "X", 0, double.MaxValue, extra: data => data["Fixed"] = true);
    AddNumber(nest, "Y", "Y", 0, double.MaxValue, extra: data => data["Fixed"] = true);
    AddBoolean(nest, "Flipped", "Flipped", data =>
    {
      if (PartDataStore.Boolean(data, "Fixed", false) == false)
        data["Flip"] = PartDataStore.Boolean(data, "Flipped", false);
    });
    _nestHost = new Panel { Content = new Scrollable { Border = BorderType.None, Content = nest, ExpandContentWidth = true } };

    _grid.Columns.Add(new GridColumn { HeaderText = "Key", Width = 95, Editable = true,
      DataCell = new TextBoxCell { Binding = Binding.Property<FieldRow, string>(row => row.Key) } });
    _grid.Columns.Add(new GridColumn { HeaderText = "Value", Width = 95, Expand = true, Editable = true,
      DataCell = new TextBoxCell { Binding = Binding.Property<FieldRow, string>(row => row.Value) } });
    _grid.CellEditing += (_, e) =>
    {
      if (e.Item is not FieldRow row) return;
      _editingKey = row.Key; _editingValue = row.Value;
      _editingTargets = TargetKeys(); _editingDocSerial = _doc?.RuntimeSerialNumber ?? 0;
    };
    _grid.CellEdited += (_, e) => Run(() => CommitCell(e));
    _grid.SelectionChanged += (_, _) => _delete.Enabled = _customValid && _grid.SelectedItems.Any();
    _search.TextChanged += (_, _) => { if (!_updating) RefreshCustomRows(); };
    var copy = new ButtonMenuItem { Text = "Copy key and value" };
    copy.Click += (_, _) => Clipboard.Instance.Text = string.Join(Environment.NewLine,
      _grid.SelectedItems.OfType<FieldRow>().Select(row => row.Key + "\t" + row.Value));
    var newItem = new ButtonMenuItem { Text = "New" }; newItem.Click += (_, _) => Run(AddField);
    var deleteItem = new ButtonMenuItem { Text = "Delete" }; deleteItem.Click += (_, _) => Run(DeleteFields);
    _grid.ContextMenu = new ContextMenu { Items = { newItem, deleteItem, new SeparatorMenuItem(), copy } };
    var toolbar = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 2,
      Items = { _add, _delete, new StackLayoutItem(_search, true) } };
    var fields = new TableLayout { Padding = new Padding(5), Spacing = new Size(2, 4),
      Rows = { new TableRow(toolbar), new TableRow(_grid) { ScaleHeight = true } } };
    _customHost = new Panel { Content = fields };
    _tabs.Pages.Add(new TabPage { Text = "Nesting", Content = _nestHost });
    _tabs.Pages.Add(new TabPage { Text = "Custom Fields", Content = _customHost });
    Content = new StackLayout { Spacing = 3, HorizontalContentAlignment = HorizontalAlignment.Stretch,
      Padding = new Padding(10, 2, 6, 2), Items =
      {
        new TableLayout { Spacing = new Size(4, 1), Rows =
        { Row("Part", new StackLayout { Orientation = Orientation.Horizontal, Spacing = 2,
          Items = { new StackLayoutItem(_parts, true), _reset } }), Row("Name", _name) } },
        _tabs, _error
      } };
  }

  internal Control ResizeTarget => _tabs;
  internal bool HasParts { get; private set; }
  internal event EventHandler? TargetsChanged;

  private Button ToolButton(Image? image, string tooltip, Action action)
  {
    var button = new Button { Image = image, Width = 23, Height = RowHeight, ToolTip = tooltip };
    if (image == null)
      button.Text = tooltip.StartsWith("Add", StringComparison.Ordinal) ? "+"
        : tooltip.StartsWith("Delete", StringComparison.Ordinal) ? "x" : "...";
    button.Click += (_, _) => Run(action);
    return button;
  }

  private static TableRow Row(string label, Control control) => new(
    new TableCell(new Label { Text = label, Width = LabelWidth, Wrap = WrapMode.Word }, false),
    new TableCell(control, true));

  private static string Common(IReadOnlyList<JsonObject> records, Func<JsonObject, string> value)
  {
    if (records.Count == 0) return "";
    string first = value(records[0]);
    return records.All(data => value(data) == first) ? first : PartDataStore.MixedValue;
  }

  private void AddBoolean(TableLayout table, string label, string key, Action<JsonObject>? extra = null)
  {
    var check = new CheckBox { ThreeState = true, Height = RowHeight };
    check.CheckedChanged += (_, _) =>
    {
      if (!_updating && check.Checked.HasValue)
        Apply(false, data => { data[key] = check.Checked.Value; extra?.Invoke(data); });
    };
    _fieldRefreshers.Add(records =>
    {
      string common = Common(records, data => PartDataStore.Boolean(data, key, false).ToString()!);
      check.Checked = common == PartDataStore.MixedValue ? null : common == "True";
    });
    _nestControls.Add(check); table.Rows.Add(Row(label, check));
  }

  private void AddNullableBoolean(TableLayout table, string label, string key)
  {
    var drop = new DropDown { Width = FieldWidth, Height = RowHeight,
      DataStore = new[] { "Default", "No", "Yes", PartDataStore.MixedValue } };
    drop.SelectedIndexChanged += (_, _) =>
    {
      if (!_updating && drop.SelectedIndex is >= 0 and <= 2)
        Apply(false, data => data[key] = drop.SelectedIndex == 0 ? null : JsonValue.Create(drop.SelectedIndex == 2));
    };
    _fieldRefreshers.Add(records =>
    {
      string common = Common(records, data => PartDataStore.Boolean(data, key, true)?.ToString() ?? "Default");
      drop.SelectedIndex = common == "Default" ? 0 : common == "False" ? 1 : common == "True" ? 2 : 3;
    });
    _nestControls.Add(drop); table.Rows.Add(Row(label, drop));
  }

  private void AddChoice(TableLayout table, string label, string key, string[] choices)
  {
    var drop = new DropDown { Width = FieldWidth, Height = RowHeight,
      DataStore = new[] { "Default" }.Concat(choices).Append(PartDataStore.MixedValue).ToArray() };
    drop.SelectedIndexChanged += (_, _) =>
    {
      if (!_updating && drop.SelectedIndex >= 0 && drop.SelectedIndex <= choices.Length)
        Apply(false, data => data[key] = drop.SelectedIndex == 0 ? null : JsonValue.Create(drop.SelectedIndex - 1));
    };
    _fieldRefreshers.Add(records =>
    {
      string common = Common(records, data => data[key]?.ToJsonString() ?? "Default");
      drop.SelectedIndex = common == "Default" ? 0 : common == PartDataStore.MixedValue ? choices.Length + 1
        : int.Parse(common, CultureInfo.InvariantCulture) + 1;
    });
    _nestControls.Add(drop); table.Rows.Add(Row(label, drop));
  }

  private void AddNumber(TableLayout table, string label, string key, double minimum, double maximum,
    bool integer = false, bool nullable = false, int offset = 0, Action<JsonObject>? extra = null)
  {
    var box = new TextBox { Width = FieldWidth, Height = RowHeight,
      PlaceholderText = nullable ? "Default" : "", ToolTip = key is "X" or "Y" ? "Document units" : label };
    bool dirty = false;
    box.TextChanged += (_, _) => { if (!_updating) dirty = true; };
    void Commit()
    {
      if (_updating || !dirty || box.Text == PartDataStore.MixedValue) return;
      dirty = false;
      if (nullable && string.IsNullOrWhiteSpace(box.Text))
      { Apply(false, data => data[key] = null); return; }
      if (!PartDataStore.TryNumber(box.Text, out double value) || value < minimum || value > maximum
        || integer && value != Math.Truncate(value))
        throw new InvalidOperationException($"Enter a valid {label.ToLowerInvariant()} between {minimum} and {maximum}.");
      Apply(false, data => { data[key] = integer ? JsonValue.Create((int)value - offset)
        : JsonValue.Create(value - offset); extra?.Invoke(data); });
    }
    box.LostFocus += (_, _) => Run(Commit);
    box.KeyDown += (_, e) => { if (e.Key == Keys.Enter) { Run(Commit); e.Handled = true; } };
    _fieldRefreshers.Add(records =>
    {
      box.Text = Common(records, data => data[key] == null ? ""
        : (PartDataStore.Number(data, key) + offset).ToString("G12", CultureInfo.CurrentCulture));
      dirty = false;
    });
    _nestControls.Add(box); table.Rows.Add(Row(label, box));
  }

  internal void Start()
  {
    if (_started) return;
    _started = true;
    RhinoDoc.UserStringChanged += OnUserStringChanged;
    RhinoDoc.GroupTableEvent += OnGroupChanged;
    RhinoDoc.AddRhinoObject += OnObjectChanged;
    RhinoDoc.DeleteRhinoObject += OnObjectChanged;
    RhinoDoc.UndeleteRhinoObject += OnObjectChanged;
    RhinoDoc.ReplaceRhinoObject += OnObjectReplaced;
    RhinoDoc.ModifyObjectAttributes += OnAttributesChanged;
    Rhino.Commands.Command.EndCommand += OnCommandEnded;
    _targetsDirty = true;
  }

  internal void Stop()
  {
    if (!_started) return;
    _started = false;
    RhinoDoc.UserStringChanged -= OnUserStringChanged;
    RhinoDoc.GroupTableEvent -= OnGroupChanged;
    RhinoDoc.AddRhinoObject -= OnObjectChanged;
    RhinoDoc.DeleteRhinoObject -= OnObjectChanged;
    RhinoDoc.UndeleteRhinoObject -= OnObjectChanged;
    RhinoDoc.ReplaceRhinoObject -= OnObjectReplaced;
    RhinoDoc.ModifyObjectAttributes -= OnAttributesChanged;
    Rhino.Commands.Command.EndCommand -= OnCommandEnded;
    _pending = false;
  }

  internal void SetActive(bool active)
  {
    _active = active;
    if (active) { _recordSignature = ""; Update(_doc, _selection); }
  }

  internal void Update(RhinoDoc? doc, IReadOnlyList<RhinoObject> selection)
  {
    bool newDocument = _doc?.RuntimeSerialNumber != doc?.RuntimeSerialNumber;
    _doc = doc;
    _selection = selection.ToList();
    if (newDocument) { _targetsDirty = true; _focusedPartKey = null; }
    string signature = (doc?.RuntimeSerialNumber ?? 0).ToString(CultureInfo.InvariantCulture) + ":"
      + string.Join(",", selection.Select(obj => obj.Id.ToString("N") + ":" + obj.RuntimeSerialNumber));
    if (!_active)
    {
      HasParts = selection.Any(PartDataTargets.IsCandidate);
      TargetsChanged?.Invoke(this, EventArgs.Empty);
      return;
    }
    if (doc == null || selection.Count == 0)
    {
      _targets = Array.Empty<PartDataTarget>();
      _focusedPartKey = null;
    }
    else if (_targetsDirty || signature != _targetSignature)
    {
      var timer = Stopwatch.StartNew();
      _targets = PartDataTargets.Resolve(doc, selection);
      if (timer.ElapsedMilliseconds >= 100)
        Log.Write("PartData", $"Discovery: objects={selection.Count}, parts={_targets.Count}, total={timer.Elapsed.TotalMilliseconds:0.0}ms");
      _targetsDirty = false;
      _targetSignature = signature;
      _recordSignature = "";
    }
    HasParts = _targets.Count > 0;
    _updating = true;
    try
    {
      if (_focusedPartKey != null && !_targets.Any(target => target.Key == _focusedPartKey))
        _focusedPartKey = null;
      var choices = _targets.Select(target => new ListItem { Key = target.Key, Text = target.Name }).ToList();
      if (choices.Count > 1) choices.Insert(0, new ListItem { Key = "", Text = "All" });
      _parts.DataStore = choices;
      _parts.SelectedIndex = Math.Max(0, choices.FindIndex(item => item.Key == _focusedPartKey));
      _parts.Enabled = HasParts;
    }
    finally { _updating = false; }
    RefreshRecords();
    TargetsChanged?.Invoke(this, EventArgs.Empty);
  }

  private IReadOnlyList<PartDataTarget> CurrentTargets() => _focusedPartKey == null ? _targets
    : _targets.Where(target => target.Key == _focusedPartKey).ToArray();
  private string[] TargetKeys() => CurrentTargets().Select(target => target.Key).ToArray();

  private void RefreshRecords()
  {
    if (_doc == null) return;
    var keys = TargetKeys();
    var nest = new List<JsonObject>();
    var custom = new List<JsonObject>();
    string signature = string.Join("|", keys.Select(key => key + ":"
      + _doc.Strings.GetValue(PartDataStore.NestSection, key) + ":"
      + _doc.Strings.GetValue(PartDataStore.CustomSection, key) + ":"
      + _doc.Strings.GetValue(PartDataStore.NamesSection, key)));
    if (signature == _recordSignature && _recordSignature.Length > 0) return;
    _recordSignature = signature;
    var errors = new List<string>();
    _nestValid = keys.Length > 0; _customValid = keys.Length > 0;
    foreach (string key in keys)
    {
      try { nest.Add(PartDataStore.ReadNest(_doc.Strings.GetValue(PartDataStore.NestSection, key))); }
      catch (Exception ex) { _nestValid = false; errors.Add(ex.Message); }
      try { custom.Add(PartDataStore.ReadCustom(_doc.Strings.GetValue(PartDataStore.CustomSection, key))); }
      catch (Exception ex) { _customValid = false; errors.Add(ex.Message); }
    }
    _updating = true;
    try
    {
      _name.Text = CurrentTargets().Count == 1 ? CurrentTargets()[0].Name : PartDataStore.MixedValue;
      _name.Enabled = keys.Length > 0;
      _name.ReadOnly = keys.Length != 1;
      _nameDirty = false;
      if (_nestValid)
      {
        LoadMaterials(nest);
        foreach (var refresh in _fieldRefreshers) refresh(nest);
      }
      foreach (var control in _nestControls) control.Enabled = _nestValid;
      _reset.Enabled = keys.Any(key => !string.IsNullOrEmpty(_doc.Strings.GetValue(PartDataStore.NestSection, key)));
      _reset.Image = _reset.Enabled ? _resetIcon : _disabledResetIcon;
      _add.Enabled = _customValid; _grid.Enabled = _customValid; _search.Enabled = _customValid;
      _error.Text = string.Join(Environment.NewLine, errors.Distinct());
      _error.Visible = errors.Count > 0;
      RefreshCustomRows(custom);
    }
    finally { _updating = false; }
  }

  private void RefreshCustomRows(IReadOnlyList<JsonObject>? records = null)
  {
    if (_doc == null || !_customValid) { _grid.DataStore = Array.Empty<FieldRow>(); _delete.Enabled = false; return; }
    records ??= TargetKeys().Select(key => PartDataStore.ReadCustom(_doc.Strings.GetValue(PartDataStore.CustomSection, key))).ToList();
    var keys = records.SelectMany(data => data.Select(field => field.Key)).Distinct(StringComparer.Ordinal)
      .OrderBy(key => key, StringComparer.CurrentCultureIgnoreCase);
    string search = _search.Text ?? "";
    var rows = keys.Select(key => new FieldRow(key, Common(records,
      data => data[key] == null ? PartDataStore.MixedValue : PartDataStore.Text(data[key]))))
      .Where(row => search.Length == 0 || row.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
        || row.Value.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
    _grid.DataStore = rows;
    _delete.Enabled = false;
  }

  private void Apply(bool custom, Action<JsonObject> edit)
  {
    Run(() =>
    {
      if (_updating || _doc == null || (custom ? !_customValid : !_nestValid)) return;
      PartDataStore.Write(_doc, TargetKeys(), custom, edit,
        custom ? "Properties+ Part Fields" : "Properties+ Part Nesting");
      _recordSignature = "";
      RefreshRecords();
    });
  }

  private void Run(Action action)
  {
    try { action(); }
    catch (Exception ex)
    {
      Log.Write("PartData", ex.Message);
      _recordSignature = "";
      try { RefreshRecords(); } catch (Exception refreshError) { Log.Write("PartData", refreshError.Message); }
      _error.Text = ex.Message; _error.Visible = true;
    }
  }

  private void AddField()
  {
    if (!_customValid) return;
    var key = new TextBox { Width = 180 };
    var value = new TextArea { Height = 75, Width = 180, AcceptsReturn = true };
    if (!EditDialog("New Part Field", new[] { ("Key", (Control)key), ("Value", (Control)value) })) return;
    string name = key.Text.Trim();
    PartDataStore.ValidateKey(name);
    Apply(true, data =>
    {
      if (data.ContainsKey(name)) throw new InvalidOperationException($"Field '{name}' already exists on a targeted part.");
      data[name] = value.Text ?? "";
    });
  }

  private void DeleteFields()
  {
    var names = _grid.SelectedItems.OfType<FieldRow>().Select(row => row.Key).ToArray();
    if (names.Length == 0) return;
    Apply(true, data => { foreach (string name in names) data.Remove(name); });
  }

  private void CommitCell(GridViewCellEventArgs e)
  {
    if (_updating || e.Item is not FieldRow row || _doc?.RuntimeSerialNumber != _editingDocSerial
      || !TargetKeys().SequenceEqual(_editingTargets)) return;
    if (e.Column == 0 && row.Key != _editingKey)
    {
      string name = row.Key.Trim();
      PartDataStore.ValidateKey(name);
      Apply(true, data =>
      {
        if (!data.ContainsKey(_editingKey)) return;
        if (data.ContainsKey(name)) throw new InvalidOperationException($"Field '{name}' already exists.");
        var value = data[_editingKey]; data.Remove(_editingKey); data[name] = value;
      });
    }
    else if (e.Column == 1 && row.Value != _editingValue)
      Apply(true, data => data[_editingKey] = row.Value ?? "");
    else RefreshCustomRows();
  }

  private void ResetNesting()
  {
    if (_doc == null || !HasParts) return;
    PartDataStore.ResetNest(_doc, TargetKeys());
    _recordSignature = ""; RefreshRecords();
  }

  private void CommitName()
  {
    if (_updating || !_nameDirty || _doc == null || CurrentTargets().Count != 1) return;
    _nameDirty = false;
    PartDataStore.Rename(_doc, CurrentTargets()[0].Key, _name.Text);
    _targetsDirty = true; _recordSignature = "";
    Update(_doc, _selection);
  }

  private static bool EditDialog(string title, IEnumerable<(string Label, Control Field)> fields)
  {
    var dialog = new Dialog<bool> { Title = title, Resizable = false, Padding = 10 };
    var table = new TableLayout { Spacing = new Size(6, 5) };
    foreach (var field in fields) table.Rows.Add(Row(field.Label, field.Field));
    var ok = new Button { Text = "OK" }; var cancel = new Button { Text = "Cancel" };
    ok.Click += (_, _) => dialog.Close(true); cancel.Click += (_, _) => dialog.Close(false);
    dialog.DefaultButton = ok; dialog.AbortButton = cancel;
    dialog.Content = new StackLayout { Spacing = 8, Items = { table,
      new StackLayout { Orientation = Orientation.Horizontal, HorizontalContentAlignment = HorizontalAlignment.Right,
        Spacing = 5, Items = { ok, cancel } } } };
    return dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow);
  }

  private void EditRecipe()
  {
    string name = PartDataStore.NormalizeMaterial(_material.Text);
    if (name.Length == 0 || name == PartDataStore.MixedValue)
      throw new InvalidOperationException("Choose or enter one material name first.");
    JsonObject? stored = _doc != null && CurrentTargets().Count == 1
      ? PartDataStore.ReadNest(_doc.Strings.GetValue(PartDataStore.NestSection, CurrentTargets()[0].Key))["Recipe"] as JsonObject : null;
    var recipe = stored != null ? PartDataStore.Clone(stored)
      : _materials.TryGetValue(name, out var existing) ? PartDataStore.Clone(existing)
      : new JsonObject { ["Name"] = name, ["Width"] = 60.0, ["Gap"] = 0.0, ["GrainDeviation"] = 0.0 };
    var width = new NumericStepper { MinValue = 0.001, MaxValue = double.MaxValue, DecimalPlaces = 3, Width = 150,
      Value = PartDataStore.Number(recipe, "Width") };
    var gap = new NumericStepper { MinValue = 0, MaxValue = double.MaxValue, DecimalPlaces = 3, Width = 150,
      Value = PartDataStore.Number(recipe, "Gap") };
    var deviation = new NumericStepper { MinValue = 0, MaxValue = 90, DecimalPlaces = 2, Width = 150,
      Value = recipe["GrainDeviation"] == null ? 0 : PartDataStore.Number(recipe, "GrainDeviation") };
    if (!EditDialog("Part Material", new[] { ("Width", (Control)width), ("Gap", (Control)gap), ("Deviation", (Control)deviation) })) return;
    recipe["Name"] = name; recipe["Width"] = width.Value; recipe["Gap"] = gap.Value; recipe["GrainDeviation"] = deviation.Value;
    Apply(false, data => { data["Material"] = name; data["Recipe"] = PartDataStore.Clone(recipe); });
  }

  private void EditGuide()
  {
    if (_doc == null || CurrentTargets().Count != 1)
      throw new InvalidOperationException("Choose one part to edit its grain guide.");
    var data = PartDataStore.ReadNest(_doc.Strings.GetValue(PartDataStore.NestSection, CurrentTargets()[0].Key));
    var boxes = new Dictionary<string, TextBox>();
    foreach (string key in new[] { "Guide", "GuideFrom", "GuideTo" })
      boxes[key] = new TextBox { Width = 190, PlaceholderText = "Default",
        Text = data[key] is JsonArray vector ? string.Join(", ", vector.Select(item => PartDataStore.FiniteNumber(item, key)
          .ToString("G12", CultureInfo.InvariantCulture))) : "", ToolTip = "X, Y, Z in the part's local frame; empty restores the default" };
    if (!EditDialog("Part Grain Guide", new[] { ("Direction", (Control)boxes["Guide"]),
      ("Start", (Control)boxes["GuideFrom"]), ("End", (Control)boxes["GuideTo"]) })) return;
    var values = new Dictionary<string, JsonArray?>();
    foreach (var field in boxes)
    {
      if (string.IsNullOrWhiteSpace(field.Value.Text)) { values[field.Key] = null; continue; }
      string[] components = field.Value.Text.Split(',');
      if (components.Length != 3 || components.Any(value => !double.TryParse(value.Trim(), NumberStyles.Float,
        CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)))
        throw new InvalidOperationException("Enter three finite coordinates separated by commas.");
      string original = data[field.Key] is JsonArray originalVector ? string.Join(", ", originalVector.Select(item =>
        PartDataStore.FiniteNumber(item, field.Key).ToString("G12", CultureInfo.InvariantCulture))) : "";
      values[field.Key] = field.Value.Text == original ? JsonNode.Parse(data[field.Key]!.ToJsonString())!.AsArray()
        : new JsonArray(components.Select(value => (JsonNode?)JsonValue.Create(double.Parse(value,
          CultureInfo.InvariantCulture))).ToArray());
    }
    if ((values["GuideFrom"] == null) != (values["GuideTo"] == null))
      throw new InvalidOperationException("Provide both guide endpoints, or leave both empty.");
    Apply(false, record => { foreach (var field in values) record[field.Key] = field.Value == null ? null
      : JsonNode.Parse(field.Value.ToJsonString()); });
  }

  private void LoadMaterials(IReadOnlyList<JsonObject> records)
  {
    _materials.Clear();
    try
    {
      var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item => item.GetName().Name == "vTools");
      string? directory = assembly == null ? null : Path.GetDirectoryName(assembly.Location);
      string? path = directory == null ? null : Path.Combine(directory, "vTools.config.json");
      if (path != null && File.Exists(path) && new FileInfo(path).Length <= 1048576)
      {
        var root = JsonNode.Parse(File.ReadAllText(path));
        if (root?["vNest"]?["materialPresets"] is JsonArray presets)
          foreach (var node in presets.OfType<JsonObject>())
          {
            string name = node["name"]?.GetValue<string>() ?? "";
            if (name.Length == 0) continue;
            var recipe = new JsonObject { ["Name"] = name, ["Width"] = node["width"]?.GetValue<double>() ?? 60,
              ["Gap"] = node["gap"]?.GetValue<double>() ?? 0,
              ["GrainDeviation"] = node["grainDeviation"]?.GetValue<double>() ?? 0 };
            _materials[name] = recipe;
          }
      }
    }
    catch (Exception ex) { Log.Write("PartData", $"Material presets unavailable: {ex.Message}"); }
    foreach (var data in records)
      if (data["Recipe"] is JsonObject recipe && recipe["Name"] is JsonValue value && value.TryGetValue<string>(out string? name))
        _materials.TryAdd(name, PartDataStore.Clone(recipe));
    _material.DataStore = _materials.Keys.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
  }

  private void OnUserStringChanged(object? sender, RhinoDoc.UserStringChangedArgs e)
  {
    if (_doc?.RuntimeSerialNumber == e.Document.RuntimeSerialNumber
      && (e.Key.StartsWith(PartDataStore.NestSection, StringComparison.Ordinal)
        || e.Key.StartsWith(PartDataStore.CustomSection, StringComparison.Ordinal)
        || e.Key.StartsWith(PartDataStore.NamesSection, StringComparison.Ordinal))
      && TargetKeys().Any(key => e.Key.EndsWith("\\" + key, StringComparison.Ordinal)))
    {
      if (e.Key.StartsWith(PartDataStore.NamesSection, StringComparison.Ordinal)) _targetsDirty = true;
      _recordSignature = ""; QueueRefresh();
    }
  }

  private void OnGroupChanged(object? sender, Rhino.DocObjects.Tables.GroupTableEventArgs e)
    => Invalidate(e.Document);
  private void OnObjectChanged(object? sender, RhinoObjectEventArgs e)
  {
    var doc = e.TheObject?.Document ?? _doc;
    if (doc != null) Invalidate(doc);
  }
  private void OnObjectReplaced(object? sender, RhinoReplaceObjectEventArgs e) => Invalidate(e.Document);
  private void OnAttributesChanged(object? sender, RhinoModifyObjectAttributesEventArgs e) => Invalidate(e.Document);
  private void Invalidate(RhinoDoc doc)
  {
    if (_doc?.RuntimeSerialNumber != doc.RuntimeSerialNumber) return;
    _targetsDirty = true; QueueRefresh();
  }
  private void OnCommandEnded(object? sender, Rhino.Commands.CommandEventArgs e)
  { if (_pending) QueueRefresh(); }
  private void QueueRefresh()
  {
    if (!_started || !_active || _queued) return;
    _queued = true;
    Application.Instance.AsyncInvoke(() =>
    {
      _queued = false;
      if (!_started || !_active || _doc == null) return;
      if (RhinoApp.InCommand > 0) { _pending = true; return; }
      _pending = false;
      var fresh = _selection.Select(obj => _doc.Objects.FindId(obj.Id)).Where(obj => obj != null).Cast<RhinoObject>().ToList();
      Update(_doc, fresh);
    });
  }
}
