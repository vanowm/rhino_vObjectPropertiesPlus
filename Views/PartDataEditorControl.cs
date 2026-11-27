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
  private const string PlacedFieldTip = "Current placement of a nested copy. Edit the placement in vNest.";
  private const string PlacementUnavailable = "Unavailable"; // No table frame or saved pose metadata was found.
  private const string PlacedNumberFormat = "0.######"; // Table-local coordinates and source-relative angles, up to six decimal places.
  private const string PatternSmithBrushKey = "vTools.PatternSmithMaterialText"; // Local WPF resource key; color is shared with vSettings when vTools is loaded.
  private readonly DropDown _parts = new() { Height = RowHeight, Width = FieldWidth };
  private readonly Label _error = new() { Wrap = WrapMode.Word, TextColor = Color.FromArgb(175, 40, 35), Visible = false };
  private readonly GridView _grid = new() { AllowMultipleSelection = true, ShowHeader = true };
  private readonly TextBox _search = new() { PlaceholderText = "Search", Height = RowHeight };
  private readonly TabControl _tabs;
  private readonly Panel _nestHost;
  private readonly Panel _customHost;
  private readonly Panel _fieldHost = new();
  private readonly PartFieldCatalog _partFields = new();
  private readonly Func<string, Image?> _icon;
  private string[] _fieldSignature = Array.Empty<string>();
  private (string Key, string Label)[] _partChoices = Array.Empty<(string, string)>();
  private readonly List<Action<IReadOnlyList<(PartDataTarget Target, JsonObject Record)>>> _fieldRefreshers = new();
  private readonly List<Action> _fieldAvailability = new();
  private readonly Button _add;
  private readonly Button _delete;
  private readonly Button _reset;
  private readonly Bitmap _resetIcon = PictureEditorControl.CreateResetIcon(true);
  private readonly Bitmap _disabledResetIcon = PictureEditorControl.CreateResetIcon(false);
  private readonly TextBox _name = new() { Height = RowHeight, Width = FieldWidth,
    ToolTip = "Part display name only; empty restores the derived name. Drawing labels and group names are unchanged." };
  private bool _nameDirty;
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
  private bool _hasPlacedSelection;
  private bool _customValid;
  private string _editingKey = "";
  private string _editingValue = "";
  private string[] _editingTargets = Array.Empty<string>();
  private uint _editingDocSerial;
  private sealed class MaterialEditor(ComboBox input, Button manager)
  {
    internal ComboBox Input { get; } = input;
    internal Button Manager { get; } = manager;
    internal Dictionary<string, JsonObject> Presets { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal string[] Names { get; set; } = [];
    internal Action? RefreshPicker { get; set; }
    internal System.Windows.Controls.ComboBox? Native { get; set; }
    internal System.Windows.Media.Brush? OrdinaryBrush { get; set; }
    internal System.Windows.FontWeight OrdinaryWeight { get; set; }
  }

  private sealed class FieldRow(string key, string value)
  {
    public string Key { get; set; } = key;
    public string Value { get; set; } = value;
  }

  internal PartDataEditorControl(int height, Func<string, Image?> icon)
  {
    _icon = icon;
    _tabs = new TabControl { Height = height };
    _parts.Width = FieldWidth - 25;
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
      TargetsChanged?.Invoke(this, EventArgs.Empty);
    };

    _nestHost = new Panel { Content = new Scrollable { Border = BorderType.None, Content = _fieldHost, ExpandContentWidth = true } };

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
  internal RhinoObject? FocusedPartBoundary => _started && _active && _doc != null
    && _focusedPartKey != null
    && _targets.FirstOrDefault(target => target.Key == _focusedPartKey) is { } target
      ? _doc.Objects.FindId(target.BoundaryId) : null;

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

  private static string Common<T>(IReadOnlyList<T> records, Func<T, string> value)
  {
    if (records.Count == 0) return "";
    string first = value(records[0]);
    return records.All(data => value(data) == first) ? first : PartDataStore.MixedValue;
  }

  private bool? SourceMark(PartDataTarget target, PartFieldCatalog.Field field) =>
    field.SourceAttribute == null ? null : bool.TryParse(_doc?.Objects.FindId(target.BoundaryId)?.Attributes.GetUserString(field.SourceAttribute),
      out bool marked) && marked;

  private void RefreshFieldEditors(IReadOnlyList<PartFieldCatalog.Field> fields, IReadOnlyList<(PartDataTarget Target, JsonObject Record)> records)
  {
    var signature = fields.Select(field => field.Signature).ToArray();
    if (!_fieldSignature.SequenceEqual(signature))
    {
      _fieldSignature = signature;
      _fieldRefreshers.Clear();
      _fieldAvailability.Clear();
      var layout = new TableLayout { Spacing = new Size(4, 2), Padding = new Padding(2, 5) };
      foreach (var field in fields)
        layout.Rows.Add(Row(field.Label, CreateFieldEditor(field)));
      var previous = _fieldHost.Content;
      _fieldHost.Content = layout;
      previous?.Dispose();
    }
    foreach (var refresh in _fieldRefreshers) refresh(records);
  }

  private Control CreateFieldEditor(PartFieldCatalog.Field field)
  {
    if (field.Type == PartFieldCatalog.Kind.Material)
      return CreateMaterialEditor(field);
    if (field.Type == PartFieldCatalog.Kind.VectorGroup)
    {
      var edit = new Button { Text = "Edit...", Height = RowHeight, Width = FieldWidth, ToolTip = field.Tip };
      edit.Click += (_, _) => Run(() => EditVectorGroup(field));
      _fieldAvailability.Add(() => edit.Enabled = CurrentTargets().Count == 1);
      return edit;
    }
    bool placementField = field.Key is "Table" or "Angle" or "X" or "Y" or "Flipped";
    bool ReadOnlyPlacement() => placementField && _hasPlacedSelection;
    void Save(JsonNode? value)
    {
      if (!ReadOnlyPlacement()) Apply(false, (target, data) => field.Write(data, value, SourceMark(target, field)));
    }
    JsonNode? Read((PartDataTarget Target, JsonObject Record) part) => field.Read(part.Record, SourceMark(part.Target, field));
    string FieldValue((PartDataTarget Target, JsonObject Record) part) => Read(part)?.ToJsonString() ?? "null";
    if (field.Type == PartFieldCatalog.Kind.Boolean && !field.Nullable)
    {
      var check = new CheckBox { Height = RowHeight, ToolTip = field.Tip };
      check.CheckedChanged += (_, _) =>
      {
        if (!_updating && check.Checked is bool value) Save(JsonValue.Create(value));
      };
      _fieldRefreshers.Add(records =>
      {
        string common = Common(records, FieldValue);
        check.ThreeState = common == PartDataStore.MixedValue || ReadOnlyPlacement() && common == "null";
        check.Checked = check.ThreeState ? null : common == "true";
      });
      if (placementField) _fieldAvailability.Add(() =>
      {
        check.Enabled = !ReadOnlyPlacement();
        check.ToolTip = ReadOnlyPlacement() ? PlacedFieldTip : field.Tip;
      });
      return check;
    }
    if (field.Type is PartFieldCatalog.Kind.Boolean or PartFieldCatalog.Kind.Choice)
    {
      var values = new List<PartFieldCatalog.Choice>();
      if (field.Nullable) values.Add(new("Default", null));
      if (field.Type == PartFieldCatalog.Kind.Boolean)
      {
        values.Add(new("No", JsonValue.Create(false)));
        values.Add(new("Yes", JsonValue.Create(true)));
      }
      else values.AddRange(field.Choices);
      var drop = new DropDown { Width = FieldWidth, Height = RowHeight, ToolTip = field.Tip,
        DataStore = values.Select(item => item.Label).Append(PartDataStore.MixedValue).ToArray() };
      drop.SelectedIndexChanged += (_, _) =>
      {
        if (!_updating && drop.SelectedIndex >= 0 && drop.SelectedIndex < values.Count)
          Save(values[drop.SelectedIndex].Value);
      };
      _fieldRefreshers.Add(records =>
      {
        string common = Common(records, FieldValue);
        int index = values.FindIndex(item => (item.Value?.ToJsonString() ?? "null") == common);
        drop.SelectedIndex = index < 0 ? values.Count : index;
      });
      return drop;
    }
    var box = new TextBox { Width = FieldWidth, Height = RowHeight, PlaceholderText = field.Nullable ? "Default" : "", ToolTip = field.Tip };
    bool dirty = false;
    box.TextChanged += (_, _) => { if (!_updating) dirty = true; };
    void Commit()
    {
      if (_updating || !dirty || box.Text == PartDataStore.MixedValue) return;
      dirty = false;
      Save(PartFieldCatalog.Parse(field, box.Text));
    }
    box.LostFocus += (_, _) => Run(Commit);
    box.KeyDown += (_, e) => { if (e.Key == Keys.Enter) { Run(Commit); e.Handled = true; } };
    _fieldRefreshers.Add(records => { box.Text = Common(records, part => PartFieldCatalog.Display(field, Read(part))); dirty = false; });
    if (placementField) _fieldAvailability.Add(() =>
    {
      box.ReadOnly = ReadOnlyPlacement();
      box.ToolTip = ReadOnlyPlacement() ? PlacedFieldTip : field.Tip;
    });
    return box;
  }

  private Control CreateMaterialEditor(PartFieldCatalog.Field field)
  {
    var input = new ComboBox { Width = FieldWidth - 25, Height = RowHeight, ToolTip = field.Tip };
    MaterialEditor? editor = null;
    void OpenManager()=>Run(()=>OpenMaterialManager(editor!,field));
    var manager = MaterialAppearance.TryCreateManagerButton(OpenManager)
      ?? ToolButton(_icon("Edit"), "Open Material Manager", () => OpenMaterialManager(editor!, field));
    var state = editor = new MaterialEditor(input, manager);
    input.DropDownOpening += (_, _) => Run(() => { RefreshMaterialChoices(state, field); ConfigureMaterialChoices(state); });
    input.Load += (_, _) => ConfigureMaterialChoices(state);
    bool dirty = false;
    input.TextChanged += (_, _) => { if (!_updating) dirty = true; RefreshMaterialItemStyles(state); };
    void Commit()
    {
      if (_updating || !dirty || input.Text == PartDataStore.MixedValue) return;
      dirty = false;
      string name = PartDataStore.NormalizeMaterial(input.Text);
      Apply(false, data =>
      {
        if ((field.Read(data)?.GetValue<string>() ?? "") == name) return;
        field.Write(data, name.Length == 0 ? null : JsonValue.Create(name));
        if (field.RecipeKey is {} recipeKey)
          data[recipeKey] = name.Length > 0 && state.Presets.TryGetValue(name, out var recipe) ? PartDataStore.Clone(recipe) : null;
      });
    }
    input.LostFocus += (_, _) => Run(Commit);
    input.KeyDown += (_, e) => { if (e.Key == Keys.Enter) { Run(Commit); e.Handled = true; } };
    _fieldRefreshers.Add(records =>
    {
      LoadMaterials(state, field, records.Select(part => part.Record).ToList());
      input.Text = Common(records, part => field.Read(part.Record)?.GetValue<string>() ?? "");
      dirty = false;
    });
    _fieldAvailability.Add(() => state.Manager.Enabled = _nestValid && IsMaterialManagerAvailable());
    return new StackLayout { Orientation = Orientation.Horizontal, Spacing = 2,
      Items = { new StackLayoutItem(input, true), manager } };
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
    TargetsChanged?.Invoke(this, EventArgs.Empty);
  }

  internal void SetActive(bool active)
  {
    _active = active;
    if (active) { _recordSignature = ""; Update(_doc, _selection); }
    else TargetsChanged?.Invoke(this, EventArgs.Empty);
  }

  internal void Update(RhinoDoc? doc, IReadOnlyList<RhinoObject> selection)
  {
    bool newDocument = _doc?.RuntimeSerialNumber != doc?.RuntimeSerialNumber;
    bool newSingleObject = selection.Count == 1 && (_selection.Count != 1 || _selection[0].Id != selection[0].Id);
    _doc = doc;
    _selection = selection.ToList();
    if (newDocument) _targetsDirty = true;
    if (newDocument || newSingleObject) _focusedPartKey = null;
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
      _targetSignature = signature;
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
    var targets = CurrentTargets();
    var keys = targets.Select(target => target.Key).ToArray();
    var nest = new List<JsonObject>();
    var custom = new List<JsonObject>();
    string signature = string.Join("|", _targets.Select(target => target.Key + ":"
      + _doc.Strings.GetValue(PartDataStore.NestSection, target.Key) + ":"
      + _doc.Strings.GetValue(PartDataStore.CustomSection, target.Key) + ":"
      + _doc.Strings.GetValue(PartDataStore.NamesSection, target.Key))) + ":" + _focusedPartKey;
    bool schemaChanged = _partFields.RefreshSchema();
    if (!schemaChanged && signature == _recordSignature && _recordSignature.Length > 0) return;
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
      _name.Text = targets.Count == 1 ? targets[0].Name : PartDataStore.MixedValue;
      _name.Enabled = keys.Length > 0;
      _name.ReadOnly = keys.Length != 1;
      _nameDirty = false;
      var fields = _partFields.Discover(nest);
      RefreshPartChoices(fields);
      var placements = PartDataTargets.ReadPlacements(_doc, targets);
      _hasPlacedSelection = placements.Count > 0;
      if (_nestValid)
        RefreshFieldEditors(fields, targets.Zip(nest, (target, record) =>
          (target, placements.TryGetValue(target.Key, out var placed) ? PlacedFields(record, placed) : record)).ToArray());
      _fieldHost.Enabled = _nestValid;
      foreach (var refresh in _fieldAvailability) refresh();
      _reset.Enabled = keys.Any(key => !string.IsNullOrEmpty(_doc.Strings.GetValue(PartDataStore.NestSection, key)));
      _reset.Image = _reset.Enabled ? _resetIcon : _disabledResetIcon;
      _add.Enabled = _customValid; _grid.Enabled = _customValid; _search.Enabled = _customValid;
      _error.Text = string.Join(Environment.NewLine, errors.Distinct());
      _error.Visible = errors.Count > 0;
      RefreshCustomRows(custom);
    }
    finally { _updating = false; }
  }

  private static JsonObject PlacedFields(JsonObject settings, PartDataTargets.PlacedPart placed)
  {
    string Number(double? value) => value?.ToString(PlacedNumberFormat, CultureInfo.CurrentCulture) ?? PlacementUnavailable;
    var display = PartDataStore.Clone(settings);
    display["Table"] = string.IsNullOrWhiteSpace(placed.Table) ? PlacementUnavailable : placed.Table;
    display["Angle"] = Number(placed.Angle);
    display["X"] = Number(placed.X);
    display["Y"] = Number(placed.Y);
    display["Flipped"] = placed.Flipped.HasValue ? JsonValue.Create(placed.Flipped.Value) : null;
    return display;
  }

  private void RefreshPartChoices(IReadOnlyList<PartFieldCatalog.Field> fields)
  {
    if (_doc == null) return;
    var typeFields = fields.Where(field => field.PartType != null).ToArray();
    var choices = _targets.Select(target =>
    {
      var labels = new List<string>();
      string? explicitType = _doc.Objects.FindId(target.BoundaryId)?.Attributes.GetUserString(PartFieldCatalog.PartTypeAttribute);
      if (!string.IsNullOrWhiteSpace(explicitType)) labels.Add(explicitType);
      try
      {
        var record = PartDataStore.ReadNest(_doc.Strings.GetValue(PartDataStore.NestSection, target.Key));
        foreach (var field in typeFields)
          if (field.Read(record, SourceMark(target, field)) is JsonValue value && value.TryGetValue<bool>(out bool marked) && marked)
            labels.Add(field.PartType!);
      }
      catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { }
      string label = labels.Count == 0 ? target.Name : target.Name + " (" + string.Join(", ", labels.Distinct()) + ")";
      return (target.Key, Label: label);
    }).ToList();
    if (choices.Count > 1) choices.Insert(0, ("", "All"));
    if (!_partChoices.SequenceEqual(choices))
    {
      _partChoices = choices.ToArray();
      _parts.DataStore = choices.Select(choice => new ListItem { Key = choice.Key, Text = choice.Label }).ToArray();
    }
    _parts.SelectedIndex = choices.Count == 0 ? -1 : Math.Max(0, choices.FindIndex(choice => choice.Key == _focusedPartKey));
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

  private void Apply(bool custom, Action<JsonObject> edit) => Apply(custom, (_, data) => edit(data));

  private void Apply(bool custom, Action<PartDataTarget, JsonObject> edit)
  {
    Run(() =>
    {
      if (_updating || _doc == null || (custom ? !_customValid : !_nestValid)) return;
      var targets = CurrentTargets().ToDictionary(target => target.Key);
      PartDataStore.Write(_doc, targets.Keys.ToArray(), custom, (key, data) => edit(targets[key], data),
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

  private static bool IsMaterialManagerAvailable()
  {
    var info = Rhino.PlugIns.PlugIn.GetPlugInInfo(new Guid("2607512e-a1fc-4cf9-9329-a293431437a0"));
    return info != null && (info.IsLoaded || !info.IsLoadProtected(out _))
      && Rhino.Commands.Command.LookupCommandId("vMaterialManager", true) != Guid.Empty;
  }

  private void OpenMaterialManager(MaterialEditor editor, PartFieldCatalog.Field field)
  {
    if (_doc == null) return;
    if (RhinoApp.InCommand > 0)
      throw new InvalidOperationException("Finish the current Rhino command before opening the material manager.");
    if (!IsMaterialManagerAvailable())
      throw new InvalidOperationException("vMaterialManager is unavailable. Install or enable a vTools version that includes it.");
    if (!RhinoApp.RunScript("_vMaterialManager", false))
      throw new InvalidOperationException("Rhino could not open vMaterialManager.");
    RefreshMaterialChoices(editor, field);
  }

  private void EditVectorGroup(PartFieldCatalog.Field definition)
  {
    if (_doc == null || CurrentTargets().Count != 1)
      throw new InvalidOperationException("Choose one part to edit " + definition.Label.ToLowerInvariant() + ".");
    var data = PartDataStore.ReadNest(_doc.Strings.GetValue(PartDataStore.NestSection, CurrentTargets()[0].Key));
    var boxes = new Dictionary<string, TextBox>();
    foreach (var vector in definition.Vectors)
    {
      string key = vector.Key;
      boxes[key] = new TextBox { Width = 190, PlaceholderText = "Default",
        Text = data[key] is JsonArray coordinates ? string.Join(", ", coordinates.Select(item => PartDataStore.FiniteNumber(item, key)
          .ToString("G12", CultureInfo.InvariantCulture))) : "", ToolTip = "X, Y, Z in the part's local frame; empty restores the default" };
    }
    if (!EditDialog(definition.Label, definition.Vectors.Select(vector => (vector.Label, (Control)boxes[vector.Key])))) return;
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
    var paired = definition.Vectors.Where(vector => vector.Paired).Select(vector => values[vector.Key] != null).Distinct();
    if (paired.Count() > 1)
      throw new InvalidOperationException("Fill in the paired coordinates together, or leave them all empty.");
    Apply(false, record => { foreach (var field in values) record[field.Key] = field.Value == null ? null
      : JsonNode.Parse(field.Value.ToJsonString()); });
  }

  private void LoadMaterials(MaterialEditor editor, PartFieldCatalog.Field field, IReadOnlyList<JsonObject> records)
  {
    editor.Presets.Clear();
    bool sharedLibraryLoaded = LoadMaterialPresets(editor, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
      "vTools", "materials.json"), sharedLibrary: true);
    try
    {
      if (!sharedLibraryLoaded)
      {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item => item.GetName().Name == "vTools");
        string? directory = assembly == null ? null : Path.GetDirectoryName(assembly.Location);
        string? path = directory == null ? null : Path.Combine(directory, "vTools.config.json");
        LoadMaterialPresets(editor, path, sharedLibrary: false);
      }
    }
    catch (Exception ex) { Log.Write("PartData", $"Material presets unavailable: {ex.Message}"); }
    foreach (var data in records)
      if (field.RecipeKey is {} recipeKey && data[recipeKey] is JsonObject recipe && recipe["Name"] is JsonValue value && value.TryGetValue<string>(out string? name))
        editor.Presets.TryAdd(name, PartDataStore.Clone(recipe));
    editor.Names = editor.Presets.Keys.Concat(records.Select(data => field.Read(data)?.GetValue<string>() ?? ""))
      .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
      .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    editor.Input.DataStore = editor.Names;
    RefreshMaterialItemStyles(editor);
  }

  private bool LoadMaterialPresets(MaterialEditor editor, string? path, bool sharedLibrary)
  {
    if (path == null || !File.Exists(path)) return false;
    try
    {
      if (new FileInfo(path).Length > 1048576)
        throw new InvalidDataException("The material library exceeds its size limit.");
      var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
        ?? throw new InvalidDataException("The material library must contain a JSON object.");
      if (sharedLibrary && PartDataStore.Number(root, "version") != 1)
        throw new InvalidDataException("The material library version is not supported.");
      var section = sharedLibrary ? root : root["vNest"] as JsonObject;
      if (section == null) return false;
      if (section["materialPresets"] == null) return true;
      if (section["materialPresets"] is not JsonArray presets)
        throw new InvalidDataException("The material preset list must contain a JSON array.");
      foreach (var node in presets.OfType<JsonObject>())
      {
        try
        {
          string name = PartDataStore.NormalizeMaterial(node["name"]?.GetValue<string>() ?? "");
          if (name.Length == 0 || name.Equals("Custom", StringComparison.OrdinalIgnoreCase)) continue;
          var recipe = new JsonObject { ["Name"] = name, ["Width"] = PartDataStore.Number(node, "width"),
            ["Gap"] = PartDataStore.Number(node, "gap"),
            ["GrainDeviation"] = node["grainDeviation"] == null ? 0 : PartDataStore.Number(node, "grainDeviation") };
          if ((node["grain"] ?? section["grain"]) is { } grain)
          {
            string mode = grain.GetValue<string>();
            int index = Array.FindIndex(new[] { "Auto", "Force", "On", "Off" },
              value => value.Equals(mode, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new InvalidDataException("Unknown material grain mode.");
            recipe["Grain"] = index;
          }
          foreach (var field in new[] { ("obeyGrain", "ObeyGrain"), ("allowReverse", "AllowReverse") })
            if ((node[field.Item1] ?? section[field.Item1]) is { } value)
              recipe[field.Item2] = value.GetValue<bool>();
          if (ReadPatternSmithReference(node["patternSmith"], lowerCase: true) is {} reference)
            recipe["PatternSmith"] = reference;
          var record = PartDataStore.Defaults();
          record["Recipe"] = recipe;
          PartDataStore.ValidateNest(record);
          editor.Presets.TryAdd(name, PartDataStore.Clone(recipe));
        }
        catch (Exception ex) { Log.Write("PartData", $"Skipping invalid material preset: {ex.Message}"); }
      }
      return true;
    }
    catch (Exception ex) { Log.Write("PartData", $"Material presets unavailable ({Path.GetFileName(path)}): {ex.Message}"); }
    return false;
  }

  private static JsonObject? ReadPatternSmithReference(JsonNode? node, bool lowerCase = false)
  {
    if (node is not JsonObject source) return null;
    string Text(string field) => source[lowerCase ? char.ToLowerInvariant(field[0]) + field[1..] : field]
      is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    string id = Text("Id"), name = Text("Name"), catalog = Text("Catalog");
    return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(catalog)
      ? null : new JsonObject { ["Id"] = id, ["Name"] = name, ["Group"] = Text("Group"), ["Catalog"] = catalog };
  }

  private void ConfigureMaterialChoices(MaterialEditor editor)
  {
    if (editor.Input.ControlObject is not System.Windows.Controls.ComboBox native) return;
    if(editor.RefreshPicker==null&&!ReferenceEquals(editor.Native,native))
      editor.RefreshPicker=MaterialAppearance.TryAttachPicker(editor.Input,
        index=>index>=0&&index<editor.Names.Length?LinkedMaterialDescription(editor,editor.Names[index]):null,
        ()=>LinkedMaterialDescription(editor,editor.Input.Text));
    if (editor.RefreshPicker==null&&!ReferenceEquals(editor.Native, native))
    {
      editor.Native = native;
      editor.OrdinaryBrush=native.Foreground;editor.OrdinaryWeight=native.FontWeight;
      native.ItemContainerGenerator.StatusChanged += (_, _) => RefreshMaterialItemStyles(editor);
      var basis = native.ItemContainerStyle ?? native.TryFindResource(typeof(System.Windows.Controls.ComboBoxItem)) as System.Windows.Style;
      var style = new System.Windows.Style(typeof(System.Windows.Controls.ComboBoxItem), basis);
      if(editor.OrdinaryBrush!=null)style.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty,editor.OrdinaryBrush));
      style.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.FontWeightProperty,editor.OrdinaryWeight));
      var linked = new System.Windows.Trigger { Property = System.Windows.FrameworkElement.TagProperty, Value = true };
      linked.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.FontWeightProperty, System.Windows.FontWeights.SemiBold));
      style.Triggers.Add(linked);
      var tint = new System.Windows.MultiTrigger();
      tint.Conditions.Add(new System.Windows.Condition(System.Windows.FrameworkElement.TagProperty, true));
      tint.Conditions.Add(new System.Windows.Condition(System.Windows.Controls.ComboBoxItem.IsHighlightedProperty, false));
      tint.Conditions.Add(new System.Windows.Condition(System.Windows.Controls.ComboBoxItem.IsSelectedProperty, false));
      tint.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty, new System.Windows.DynamicResourceExtension(PatternSmithBrushKey)));
      style.Triggers.Add(tint);
      var inactive=new System.Windows.MultiTrigger();
      inactive.Conditions.Add(new System.Windows.Condition(System.Windows.FrameworkElement.TagProperty,true));
      inactive.Conditions.Add(new System.Windows.Condition(System.Windows.Controls.ComboBoxItem.IsHighlightedProperty,false));
      inactive.Conditions.Add(new System.Windows.Condition(System.Windows.Controls.Primitives.Selector.IsSelectionActiveProperty,false));
      inactive.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty,new System.Windows.DynamicResourceExtension(PatternSmithBrushKey)));
      style.Triggers.Add(inactive);
      native.ItemContainerStyle = style;
    }
    RefreshMaterialItemStyles(editor);
  }

  private void RefreshMaterialItemStyles(MaterialEditor editor)
  {
    if(editor.RefreshPicker is {} refresh){refresh();return;}
    if (editor.Input.IsDisposed || editor.Native == null || editor.Native.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.GeneratingContainers) return;
    var brush=MaterialAppearance.LinkedBrush();editor.Native.Resources[PatternSmithBrushKey]=brush;
    for (int index = 0; editor.Native.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated && index < editor.Names.Length; index++)
    {
      if (editor.Native.ItemContainerGenerator.ContainerFromIndex(index) is not System.Windows.Controls.ComboBoxItem item) continue;
      var description=LinkedMaterialDescription(editor,editor.Names[index]);
      item.Tag=description!=null;
      item.ToolTip=description??"Custom material";
    }
    bool selected=LinkedMaterialDescription(editor,editor.Input.Text)!=null;
    editor.Native.SetCurrentValue(System.Windows.Controls.Control.ForegroundProperty,selected?brush:editor.OrdinaryBrush);
    editor.Native.SetCurrentValue(System.Windows.Controls.Control.FontWeightProperty,selected?System.Windows.FontWeights.SemiBold:editor.OrdinaryWeight);
  }

  private static string? LinkedMaterialDescription(MaterialEditor editor,string? name)=>
    editor.Presets.TryGetValue(name??string.Empty,out var recipe)&&ReadPatternSmithReference(recipe["PatternSmith"]) is {} reference?
      $"PatternSmith: {reference["Name"]!.GetValue<string>()}\n{reference["Group"]!.GetValue<string>()}\nID: {reference["Id"]!.GetValue<string>()}":null;

  private void RefreshMaterialChoices(MaterialEditor editor, PartFieldCatalog.Field field)
  {
    if (_updating || _doc == null || !_nestValid) return;
    string text = editor.Input.Text;
    bool wasUpdating = _updating;
    _updating = true;
    try
    {
      LoadMaterials(editor, field, TargetKeys().Select(key => PartDataStore.ReadNest(
        _doc.Strings.GetValue(PartDataStore.NestSection, key))).ToList());
      editor.Input.Text = text;
    }
    finally { _updating = wasUpdating; }
  }

  private void OnUserStringChanged(object? sender, RhinoDoc.UserStringChangedArgs e)
  {
    if (_doc?.RuntimeSerialNumber == e.Document.RuntimeSerialNumber
      && (e.Key.StartsWith(PartDataStore.NestSection, StringComparison.Ordinal)
        || e.Key.StartsWith(PartDataStore.CustomSection, StringComparison.Ordinal)
        || e.Key.StartsWith(PartDataStore.NamesSection, StringComparison.Ordinal))
      && _targets.Any(target => e.Key.EndsWith("\\" + target.Key, StringComparison.Ordinal)))
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
  {
    foreach (var refresh in _fieldAvailability) refresh();
    if (_pending) QueueRefresh();
  }
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
