using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rhino;

namespace vObjectPropertiesPlus.Helpers;

internal static class PartDataStore
{
  internal const string NestSection = "vNest.PartSettings";
  internal const string CustomSection = "vObjectPropertiesPlus.PartData";
  internal const string NamesSection = "vTools.PartNames";
  internal const int SchemaVersion = 1;
  internal const int MaximumNestCharacters = 16384;
  internal const int MaximumCustomCharacters = 1048576;
  internal const int MaximumCopies = 1000;
  internal const int MaximumTables = 128;
  internal const string MixedValue = "(varies)";

  internal static JsonObject Defaults() => new()
  {
    ["Version"] = SchemaVersion, ["Copies"] = 1, ["Included"] = true,
    ["Fixed"] = false, ["ManualRotation"] = false, ["Flipped"] = false,
    ["Flip"] = null, ["Grain"] = null, ["Rotation"] = null, ["Step"] = null,
    ["Obey"] = null, ["Reverse"] = null, ["Deviation"] = null,
    ["Angle"] = 0.0, ["Table"] = 0, ["X"] = 0.0, ["Y"] = 0.0,
    ["Material"] = null, ["Recipe"] = null, ["Guide"] = null,
    ["GuideFrom"] = null, ["GuideTo"] = null
  };

  internal static JsonObject ReadNest(string? json)
  {
    if (string.IsNullOrEmpty(json))
      return Defaults();
    if (json.Length > MaximumNestCharacters)
      throw new InvalidOperationException("The saved nesting record is too large.");
    var data = JsonNode.Parse(json) as JsonObject
      ?? throw new InvalidOperationException("The nesting record is not a JSON object.");
    ValidateNest(data);
    return data;
  }

  internal static JsonObject ReadCustom(string? json)
  {
    if (string.IsNullOrEmpty(json))
      return new JsonObject();
    if (json.Length > MaximumCustomCharacters)
      throw new InvalidOperationException("The saved custom fields are too large.");
    var data = JsonNode.Parse(json) as JsonObject
      ?? throw new InvalidOperationException("The custom fields are not a JSON object.");
    foreach (var field in data)
    {
      ValidateKey(field.Key);
      if (field.Value is not JsonValue value || !value.TryGetValue<string>(out _))
        throw new InvalidOperationException($"Custom field '{field.Key}' is not text.");
    }
    return data;
  }

  internal static JsonObject Clone(JsonObject data) => JsonNode.Parse(data.ToJsonString())!.AsObject();

  internal static void ValidateKey(string key)
  {
    if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || key.Contains('\0'))
      throw new InvalidOperationException("Use a nonempty field name of at most 256 characters.");
  }

  internal static string NormalizeMaterial(string value)
  {
    string name = System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
    if (name.Length > 80 || name.Contains('\0'))
      throw new InvalidOperationException("Material names may contain at most 80 characters.");
    return name;
  }

  internal static string PartName(RhinoDoc doc, string key, string fallback)
  {
    string? name = doc.Strings.GetValue(NamesSection, key);
    return string.IsNullOrWhiteSpace(name) ? fallback : name;
  }

  internal static void Rename(RhinoDoc doc, string key, string value)
  {
    if (RhinoApp.InCommand > 0)
      throw new InvalidOperationException("Finish the current Rhino command before renaming a part.");
    string name = System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
    if (name.Length > 256 || name.Contains('\0'))
      throw new InvalidOperationException("Part names may contain at most 256 characters.");
    if ((doc.Strings.GetValue(NamesSection, key) ?? "") == name) return;
    uint undo = doc.BeginUndoRecord("Properties+ Rename Part");
    try
    {
      if (name.Length == 0) doc.Strings.Delete(NamesSection, key);
      else doc.Strings.SetString(NamesSection, key, name);
      doc.Modified = true;
    }
    finally
    {
      if (undo != 0) doc.EndUndoRecord(undo);
    }
  }

  internal static void ValidateNest(JsonObject data)
  {
    if (Integer(data, "Version") != SchemaVersion)
      throw new InvalidOperationException("This nesting-record version is not supported.");
    Range(Integer(data, "Copies"), 1, MaximumCopies, "Copies");
    Range(Integer(data, "Table"), 0, MaximumTables - 1, "Table");
    foreach (string key in new[] { "Included", "Fixed", "ManualRotation", "Flipped" })
      Boolean(data, key, false);
    foreach (string key in new[] { "Flip", "Obey", "Reverse" })
      Boolean(data, key, true);
    foreach (string key in new[] { "Angle", "X", "Y" })
    {
      double value = Number(data, key);
      if (key != "Angle" && value < 0)
        throw new InvalidOperationException($"{key} must not be negative.");
    }
    NullableRange(data, "Step", 0.1, 360);
    NullableRange(data, "Deviation", 0, 90);
    NullableEnum(data, "Grain", 3);
    NullableEnum(data, "Rotation", 5);
    foreach (string key in new[] { "Guide", "GuideFrom", "GuideTo" })
      ValidateVector(data[key], key);
    if (data["Material"] != null)
      NormalizeMaterial(Text(data["Material"]));
    if (data["Recipe"] is JsonObject recipe)
    {
      NormalizeMaterial(Text(recipe["Name"]));
      if (Number(recipe, "Width") <= 0 || Number(recipe, "Gap") < 0)
        throw new InvalidOperationException("Material width must be positive and gap nonnegative.");
      Range(recipe["GrainDeviation"] == null ? 0 : Number(recipe, "GrainDeviation"),
        0, 90, "Material grain deviation");
    }
    else if (data["Recipe"] != null)
      throw new InvalidOperationException("The material recipe is invalid.");
  }

  internal static void ValidateVector(JsonNode? node, string name)
  {
    if (node == null)
      return;
    if (node is not JsonArray vector || vector.Count != 3)
      throw new InvalidOperationException($"{name} must contain three coordinates.");
    foreach (var item in vector)
      FiniteNumber(item, name);
  }

  internal static double Number(JsonObject data, string name) => FiniteNumber(data[name], name);

  internal static double FiniteNumber(JsonNode? node, string name)
  {
    if (node is not JsonValue value)
      throw new InvalidOperationException($"{name} must be a finite number.");
    double number;
    if (!value.TryGetValue<double>(out number))
    {
      if (value.TryGetValue<int>(out int integer))
        number = integer;
      else if (value.TryGetValue<long>(out long longInteger))
        number = longInteger;
      else
        throw new InvalidOperationException($"{name} must be a finite number.");
    }
    if (!double.IsFinite(number))
      throw new InvalidOperationException($"{name} must be a finite number.");
    return number;
  }

  internal static int Integer(JsonObject data, string name)
  {
    if (data[name] is JsonValue value && value.TryGetValue<int>(out int integer))
      return integer;
    throw new InvalidOperationException($"{name} must be a whole number.");
  }

  internal static string Text(JsonNode? node)
  {
    if (node is JsonValue value && value.TryGetValue<string>(out var text))
      return text;
    throw new InvalidOperationException("The stored text value is invalid.");
  }

  internal static bool? Boolean(JsonObject data, string key, bool nullable)
  {
    if (nullable && data[key] == null)
      return null;
    if (data[key] is JsonValue value && value.TryGetValue<bool>(out bool result))
      return result;
    throw new InvalidOperationException($"{key} must be a Boolean value.");
  }

  private static void NullableRange(JsonObject data, string key, double minimum, double maximum)
  {
    if (data[key] != null)
      Range(Number(data, key), minimum, maximum, key);
  }

  private static void NullableEnum(JsonObject data, string key, int maximum)
  {
    if (data[key] != null)
      Range(Integer(data, key), 0, maximum, key);
  }

  private static void Range(double value, double minimum, double maximum, string name)
  {
    if (value < minimum || value > maximum)
      throw new InvalidOperationException($"{name} must be between {minimum} and {maximum}.");
  }

  internal static bool TryNumber(string text, out double value) =>
    (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
      || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
      && double.IsFinite(value);

  internal static bool Write(RhinoDoc doc, IReadOnlyList<string> keys, bool custom,
    Action<JsonObject> edit, string description)
  {
    if (RhinoApp.InCommand > 0)
      throw new InvalidOperationException("Finish the current Rhino command before editing part data.");
    string section = custom ? CustomSection : NestSection;
    var changes = new List<(string Key, string Json)>();
    foreach (string key in keys.Distinct())
    {
      string? raw = doc.Strings.GetValue(section, key);
      var data = custom ? ReadCustom(raw) : ReadNest(raw);
      string before = data.ToJsonString();
      edit(data);
      if (custom)
        ReadCustom(data.ToJsonString());
      else
        ValidateNest(data);
      string after = data.ToJsonString();
      int limit = custom ? MaximumCustomCharacters : MaximumNestCharacters;
      if (after.Length > limit)
        throw new InvalidOperationException("The edited part record exceeds its storage limit.");
      if (after != before || !custom && string.IsNullOrEmpty(raw))
        changes.Add((key, after));
    }
    if (changes.Count == 0)
      return false;
    uint undo = doc.BeginUndoRecord(description);
    try
    {
      foreach (var change in changes)
        doc.Strings.SetString(section, change.Key, change.Json);
      doc.Modified = true;
      return true;
    }
    finally
    {
      if (undo != 0)
        doc.EndUndoRecord(undo);
    }
  }

  internal static void ResetNest(RhinoDoc doc, IReadOnlyList<string> keys)
  {
    if (RhinoApp.InCommand > 0)
      throw new InvalidOperationException("Finish the current Rhino command before editing part data.");
    uint undo = doc.BeginUndoRecord("Properties+ Reset Part Nesting");
    try
    {
      foreach (string key in keys.Distinct())
        doc.Strings.Delete(NestSection, key);
      doc.Modified = true;
    }
    finally
    {
      if (undo != 0)
        doc.EndUndoRecord(undo);
    }
  }
}
