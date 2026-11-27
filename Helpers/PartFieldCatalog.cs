using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace vObjectPropertiesPlus.Helpers;

internal sealed class PartFieldCatalog
{
  private const int MaximumAdditionalFields = 64; // Bounds control creation when many different records are selected.
  private static readonly HashSet<string> InternalKeys = new(StringComparer.Ordinal) { "Version", "Recipe", "GuideFrom", "GuideTo" };
  private static readonly IReadOnlyList<Field> Legacy = LegacyFields();
  internal const string PartTypeAttribute = "vTools.partType"; // Optional type name on a source object with an explicit part identity.
  internal enum Kind { Boolean, Integer, Number, Text, Choice, Material, VectorGroup }
  internal sealed record Choice(string Label, JsonNode? Value);
  internal sealed record WriteRule(string Key, bool? Constant = null, string? Unless = null);
  internal sealed record VectorField(string Key, string Label, bool Paired = false);
  internal sealed record Field(string Key, Kind Type, bool Nullable, JsonNode? Default, IReadOnlyList<Choice> Choices,
    long Minimum = long.MinValue, long Maximum = long.MaxValue)
  {
    internal string? Caption { get; init; }
    internal string? Tip { get; init; }
    internal string? SourceAttribute { get; init; }
    internal string? PartType { get; init; }
    internal string? RecipeKey { get; init; }
    internal IReadOnlyList<VectorField> Vectors { get; init; } = Array.Empty<VectorField>();
    internal WriteRule? Rule { get; init; }
    internal int Offset { get; init; }
    internal double MinimumNumber { get; init; } = double.MinValue;
    internal double MaximumNumber { get; init; } = double.MaxValue;
    internal string Label => Caption ?? Friendly(Key);
    internal string Signature => Key + ":" + Type + ":" + Nullable + ":" + Default?.ToJsonString() + ":" + Label
      + ":" + SourceAttribute + ":" + PartType
      + ":" + Minimum + ":" + Maximum + ":" + string.Join("|", Choices.Select(item => item.Label + ":" + item.Value?.ToJsonString()));
    internal JsonNode? Read(JsonObject record, bool? source = null)
    {
      if (record.TryGetPropertyValue(Key, out var value) && (value != null || SourceAttribute == null)) return value;
      return SourceAttribute == null ? Default : JsonValue.Create(source ?? false);
    }

    internal void Write(JsonObject record, JsonNode? value, bool? source = null)
    {
      bool inherits = SourceAttribute != null && value is JsonValue flag && flag.TryGetValue<bool>(out bool marked)
        && marked == (source ?? false);
      record[Key] = inherits || value == null ? null : JsonNode.Parse(value.ToJsonString());
      if (Rule is {} rule && (rule.Unless == null || record[rule.Unless]?.GetValue<bool>() != true))
        record[rule.Key] = rule.Constant.HasValue ? JsonValue.Create(rule.Constant.Value)
          : value == null ? null : JsonNode.Parse(value.ToJsonString());
    }
  }

  private static IReadOnlyList<Field> LegacyFields()
  {
    var defaults = PartDataStore.Defaults();
    Field Make(string key, Kind kind, bool nullable = false, string[]? labels = null) =>
      new(key, kind, nullable, defaults[key], labels?.Select((label, index) => new Choice(label, JsonValue.Create(index))).ToArray()
        ?? Array.Empty<Choice>());
    var lockPlacement = new WriteRule("Fixed", true);
    return new Field[]
    {
      Make("Included", Kind.Boolean),
      Make(PartDataStore.FacingOverrideKey, Kind.Boolean) with { Caption = "Facing", PartType = "Facing", SourceAttribute = "vTools.facing" },
      Make(PartDataStore.ReinforcementKey, Kind.Boolean) with { PartType = "Reinforcement" },
      Make("Copies", Kind.Integer) with { Minimum = 1, Maximum = PartDataStore.MaximumCopies },
      Make("StrictCopies", Kind.Boolean) with { Default = JsonValue.Create(false), Tip = "Nest only the copies still missing from the requested count." },
      Make("Material", Kind.Material, true) with { RecipeKey = "Recipe", Tip = "Blank uses the current vNest material." },
      Make("FaceDown", Kind.Boolean) with { Tip = "Cut face down without mirroring the part." },
      Make("Grain", Kind.Choice, true, new[] { "Auto", "Force", "On", "Off" }),
      Make("Guide", Kind.VectorGroup, true) with { Caption = "Grain guide", Vectors = new[]
        { new VectorField("Guide", "Direction"), new VectorField("GuideFrom", "Start", true), new VectorField("GuideTo", "End", true) } },
      Make("Obey", Kind.Boolean, true) with { Caption = "Obey grain" },
      Make("Rotation", Kind.Choice, true, new[] { "No rotation", "Any rotation", "45", "90", "180", "Custom" }),
      Make("Reverse", Kind.Boolean, true),
      Make("Step", Kind.Number, true) with { MinimumNumber = 0.1, MaximumNumber = 360 },
      Make("Deviation", Kind.Number, true) with { MinimumNumber = 0, MaximumNumber = 90 },
      Make("Flip", Kind.Boolean, true),
      Make("ManualRotation", Kind.Boolean) with { Caption = "Manual angle" },
      Make("Angle", Kind.Number) with { Rule = new WriteRule("ManualRotation", true) },
      Make("Fixed", Kind.Boolean),
      Make("Table", Kind.Integer) with { Minimum = 0, Maximum = PartDataStore.MaximumTables - 1, Offset = 1, Rule = lockPlacement },
      Make("X", Kind.Number) with { MinimumNumber = 0, Rule = lockPlacement, Tip = "Document units" },
      Make("Y", Kind.Number) with { MinimumNumber = 0, Rule = lockPlacement, Tip = "Document units" },
      Make("Flipped", Kind.Boolean) with { Rule = new WriteRule("Flip", Unless: "Fixed") }
    };
  }

  private Assembly? _checkedAssembly;
  private JsonObject? _schema;

  internal bool RefreshSchema()
  {
    if (_checkedAssembly != null) return false;
    _checkedAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "vTools");
    if (_checkedAssembly == null) return false;
    try
    {
      var discover = _checkedAssembly.GetType("vTools.Commands.vNest")?.GetMethod("GetPartFieldCatalog", Type.EmptyTypes);
      if (discover?.Invoke(null, null) is string json && JsonNode.Parse(json) is JsonObject schema)
      { _schema = schema; return true; }
    }
    catch (Exception ex) { Log.Write("PartData", $"vNest field catalog unavailable: {ex.Message}"); }
    return false;
  }

  internal IReadOnlyList<Field> Discover(IReadOnlyList<JsonObject> records) => Discover(records, _schema);

  internal static IReadOnlyList<Field> Discover(IReadOnlyList<JsonObject> records, JsonObject? schema)
  {
    var fields = Legacy.ToDictionary(field => field.Key, StringComparer.Ordinal);
    if (schema != null)
      foreach (var (key, node) in schema)
        if (!InternalKeys.Contains(key) && node is JsonObject entry
            && entry["kind"] is JsonValue tag && tag.TryGetValue<string>(out string? name)
            && Enum.TryParse<Kind>(name, true, out var kind) && Enum.IsDefined(kind))
        {
          string? Text(string name) => entry[name] is JsonValue text && text.TryGetValue<string>(out var value) ? value : null;
          if (fields.TryGetValue(key, out var known))
          {
            fields[key] = known with { Default = entry["default"], Caption = Text("label") ?? known.Caption,
              PartType = Text("partType") ?? known.PartType, SourceAttribute = Text("sourceAttribute") ?? known.SourceAttribute };
            continue;
          }
          var choices = entry["choices"] is JsonArray options
            ? options.OfType<JsonObject>().Where(option => option["label"] is JsonValue && option["value"] is JsonValue)
              .Select(option => new Choice(Friendly(PartDataStore.Text(option["label"])), option["value"])).ToArray()
            : Array.Empty<Choice>();
          if (kind == Kind.Choice && choices.Length == 0) continue;
          bool nullable = entry["nullable"] is JsonValue optional && optional.TryGetValue<bool>(out bool value) && value;
          fields[key] = new Field(key, kind, nullable, entry["default"], choices,
            WholeNumber(entry["minimum"]) ?? long.MinValue, WholeNumber(entry["maximum"]) ?? long.MaxValue)
            { Caption = Text("label"), PartType = Text("partType"), SourceAttribute = Text("sourceAttribute") };
        }

    foreach (var group in records.SelectMany(record => record).Where(pair => !InternalKeys.Contains(pair.Key))
      .GroupBy(pair => pair.Key, StringComparer.Ordinal))
    {
      if (fields.ContainsKey(group.Key)) continue;
      var observed = group.Where(pair => pair.Value != null).Select(pair => Infer(pair.Value)).Distinct().ToArray();
      Kind? kind = observed.Length == 1 ? observed[0]
        : observed.Length == 2 && observed.Contains(Kind.Integer) && observed.Contains(Kind.Number) ? Kind.Number : null;
      if (kind != null)
        fields.Add(group.Key, new Field(group.Key, kind.Value, group.Count() != records.Count || group.Any(pair => pair.Value == null),
          null, Array.Empty<Choice>()));
    }
    var knownKeys = Legacy.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
    return Legacy.Select(field => fields[field.Key]).Concat(fields.Values.Where(field => !knownKeys.Contains(field.Key))
      .OrderBy(field => field.Label, StringComparer.CurrentCultureIgnoreCase).Take(MaximumAdditionalFields)).ToArray();
  }

  private static string Friendly(string key) => Regex.Replace(key.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ");

  private static long? WholeNumber(JsonNode? node) => node is JsonValue value
    ? value.TryGetValue<long>(out long wide) ? wide : value.TryGetValue<int>(out int narrow) ? narrow : null
    : null;

  private static Kind? Infer(JsonNode? node)
  {
    if (node is not JsonValue value) return null;
    if (value.TryGetValue<bool>(out _)) return Kind.Boolean;
    if (value.TryGetValue<string>(out _)) return Kind.Text;
    if (WholeNumber(node).HasValue) return Kind.Integer;
    if (value.TryGetValue<double>(out double number) && double.IsFinite(number)) return Kind.Number;
    return null;
  }

  internal static string Display(Field field, JsonNode? value) =>
    value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text
    : field.Type == Kind.Integer && WholeNumber(value) is long integer
      ? (integer + field.Offset).ToString(CultureInfo.CurrentCulture)
    : field.Type == Kind.Number && value is JsonValue number && number.TryGetValue<double>(out double real)
      ? real.ToString("G", CultureInfo.CurrentCulture)
      : value?.ToJsonString() ?? "";

  internal static JsonNode? Parse(Field field, string text)
  {
    if (field.Nullable && string.IsNullOrWhiteSpace(text)) return null;
    if (field.Type == Kind.Text) return JsonValue.Create(text);
    if (field.Type == Kind.Integer && (long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out long integer)
      || long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)))
    {
      if (integer >= field.Minimum + field.Offset && integer <= field.Maximum + field.Offset)
      {
        long stored = integer - field.Offset;
        return stored is >= int.MinValue and <= int.MaxValue ? JsonValue.Create((int)stored) : JsonValue.Create(stored);
      }
      throw new InvalidOperationException($"{field.Label} is outside its supported range.");
    }
    if (field.Type == Kind.Number && PartDataStore.TryNumber(text, out double number))
    {
      if (number >= field.MinimumNumber && number <= field.MaximumNumber) return JsonValue.Create(number);
      throw new InvalidOperationException($"{field.Label} must be between {field.MinimumNumber} and {field.MaximumNumber}.");
    }
    throw new InvalidOperationException(field.Type == Kind.Integer ? $"{field.Label} must be a whole number." : $"{field.Label} must be a finite number.");
  }
}
