using System.Globalization;
using System.Reflection;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace vObjectPropertiesPlus.Helpers;

internal sealed record PartDataTarget(string Key, string Name, Guid BoundaryId, Plane Frame);

internal static class PartDataTargets
{
  private const int BoundarySamples = 17;
  private const double BoundaryToleranceFactor = 2.0;
  private const string ReferenceLayer = "Reference";
  private const string TableGroupIdKey = "vNest.tableGroupId"; // Identifies the actual owning table group, even in a larger selection.

  internal sealed record PlacedPart(string Table, double? Angle, double? X, double? Y, bool? Flipped);
  private sealed record PlacedTable(string Name, Plane Frame);

  internal static IReadOnlyDictionary<string, PlacedPart> ReadPlacements(RhinoDoc doc, IReadOnlyList<PartDataTarget> targets)
  {
    var result = new Dictionary<string, PlacedPart>(StringComparer.Ordinal);
    var tables = new Dictionary<int, PlacedTable?>();
    MethodInfo? readFrame = null;
    bool lookedUpFrame = false;
    foreach (var target in targets)
    {
      if (doc.Objects.FindId(target.BoundaryId) is not {} boundary
        || string.IsNullOrEmpty(boundary.Attributes.GetUserString("vNest.part"))) continue;
      var table = (boundary.Attributes.GetGroupList() ?? Array.Empty<int>()).Select(ReadTable).OfType<PlacedTable>().FirstOrDefault();
      if (table == null) continue;
      var bounds = table.Frame.IsValid && boundary.Geometry is Curve { IsClosed: true } outline
        ? outline.GetBoundingBox(table.Frame) : BoundingBox.Unset;
      string? rotation = boundary.Attributes.GetUserString("vNest.rotation");
      double? angle = double.TryParse(rotation, NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees)
        && double.IsFinite(degrees) ? degrees : null;
      bool? flipped = bool.TryParse(boundary.Attributes.GetUserString("vNest.flipped"), out bool mirror) ? mirror : null;
      result[target.Key] = new(table.Name, angle, bounds.IsValid ? bounds.Min.X : null, bounds.IsValid ? bounds.Min.Y : null, flipped);
    }
    return result;

    PlacedTable? ReadTable(int index)
    {
      if (tables.TryGetValue(index, out var known)) return known;
      tables[index] = null;
      if (index < 0 || index >= doc.Groups.Count || doc.Groups[index] is not { IsDeleted: false } group) return null;
      var border = (doc.Groups.GroupMembers(index) ?? Array.Empty<RhinoObject>()).FirstOrDefault(obj =>
        obj.Geometry is Curve && (obj.Attributes.GetUserString("vNest.role") == "table-border" || obj.Attributes.Name == "NestTable")
        && (!Guid.TryParse(obj.Attributes.GetUserString(TableGroupIdKey), out var owner) || owner == group.Id));
      if (border == null) return null;

      if (!lookedUpFrame)
      {
        readFrame = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "vTools")
          ?.GetType("vTools.Commands.vNest")?.GetMethod("TryGetPlacedTableFrame",
            new[] { typeof(RhinoDoc), typeof(RhinoObject), typeof(Plane).MakeByRefType() });
        lookedUpFrame = true;
      }
      var frame = Plane.Unset;
      if (readFrame != null)
      {
        try
        {
          var args = new object[] { doc, border, Plane.Unset };
          if (readFrame.Invoke(null, args) is true && args[2] is Plane placedFrame) frame = placedFrame;
        }
        catch (TargetInvocationException ex) { Log.Write("PartData", $"Cannot read table coordinates: {ex.GetBaseException().Message}"); }
      }
      string? name = border.Attributes.GetUserString("vNest.tableName");
      return tables[index] = new(string.IsNullOrWhiteSpace(name) ? group.Name : name, frame);
    }
  }

  private sealed record Region(Curve Outline, Plane Plane, IReadOnlyList<RhinoObject> Objects)
  {
    internal BoundingBox Bounds { get; } = Outline.GetBoundingBox(Plane);
  }

  internal static bool IsCandidate(RhinoObject obj)
  {
    if (obj.IsDeleted || IsTableDecoration(obj)) return false;
    var groups = obj.Attributes.GetGroupList() ?? Array.Empty<int>();
    if (Supported(obj) && (groups.Length > 0 || obj.Geometry is Curve { IsClosed: true } || IsSeamAllowance(obj))) return true;
    if (HasType(obj)) return true;
    var doc = obj.Document;
    return doc != null && (HasRecord(doc, BoundaryKey(obj.Id)) || groups.Any(index =>
      index >= 0 && index < doc.Groups.Count && doc.Groups[index] is { IsDeleted: false } group && HasRecord(doc, GroupKey(group.Id))));
  }

  private static bool HasType(RhinoObject obj) =>
    !string.IsNullOrWhiteSpace(obj.Attributes.GetUserString(PartFieldCatalog.PartTypeAttribute));

  private static string BoundaryKey(Guid id) => "boundary-" + id.ToString("N");
  private static string GroupKey(Guid id) => "group-" + id.ToString("N");
  private static bool HasRecord(RhinoDoc doc, string key) =>
    !string.IsNullOrEmpty(doc.Strings.GetValue(PartDataStore.NestSection, key))
    || !string.IsNullOrEmpty(doc.Strings.GetValue(PartDataStore.NamesSection, key))
    || !string.IsNullOrEmpty(doc.Strings.GetValue(PartDataStore.CustomSection, key));

  private static bool IsSeamAllowance(RhinoObject obj) => obj.Geometry is Curve
    && (obj.Attributes.GetUserString("vSeam.role") is "envelope" or "edge" or "join" or "chamfer" or "cap"
      || string.Equals(obj.Attributes.Name, "SeamAllowance", StringComparison.OrdinalIgnoreCase));

  private static bool Supported(RhinoObject obj) => !obj.IsDeleted && !IsTableDecoration(obj)
    && obj.Geometry is Curve or AnnotationBase or Rhino.Geometry.Point or TextDot;

  private static bool IsTableDecoration(RhinoObject obj) =>
    obj.Attributes.GetUserString("vNest.role") is "table-border" or "table-label" or "table-label-frame"
      or "table-label-divider" or "table-material-frame" or "table-header-fill" or "table-solver-info"
    || obj.Attributes.Name == "NestTable";

  internal static IReadOnlyList<PartDataTarget> Resolve(RhinoDoc doc, IReadOnlyList<RhinoObject> selection)
  {
    var geometry = ResolveGeometry(doc, selection);
    var stored = ResolveStored(doc, selection);
    if (stored.Count == 0) return geometry;
    var resolved = geometry.ToDictionary(target => target.Key, StringComparer.Ordinal);
    var byBoundary = geometry.ToLookup(target => target.BoundaryId);
    var explicitParts = stored.Select(target => resolved.GetValueOrDefault(target.Key)
      ?? (!HasRecord(doc, target.Key) ? byBoundary[target.BoundaryId].FirstOrDefault() : null) ?? target).ToArray();
    var boundaries = explicitParts.Select(target => target.BoundaryId).ToHashSet();
    return explicitParts.Concat(geometry.Where(target => !boundaries.Contains(target.BoundaryId)))
      .DistinctBy(target => target.Key).ToArray();
  }

  private static List<PartDataTarget> ResolveStored(RhinoDoc doc, IReadOnlyList<RhinoObject> selection)
  {
    var result = new Dictionary<string, PartDataTarget>(StringComparer.Ordinal);
    var groupRecords = new Dictionary<int, (string Key, RhinoObject Boundary, string Name, int Count)?>();
    foreach (var obj in selection.Where(obj => !obj.IsDeleted && !IsTableDecoration(obj)))
    {
      var groups = new List<(string Key, RhinoObject Boundary, string Name, int Count)>();
      foreach (int index in obj.Attributes.GetGroupList() ?? Array.Empty<int>())
      {
        if (!groupRecords.TryGetValue(index, out var record))
        {
          record = null;
          if (index >= 0 && index < doc.Groups.Count && doc.Groups[index] is { IsDeleted: false } group)
          {
            string key = GroupKey(group.Id);
            if (HasRecord(doc, key))
            {
              var members = (doc.Groups.GroupMembers(index) ?? Array.Empty<RhinoObject>()).Where(member => !member.IsDeleted).ToArray();
              if (members.Length > 0 && !members.Any(IsTableDecoration))
              {
                var boundary = members.OrderByDescending(HasType).ThenByDescending(IsSeamAllowance)
                  .ThenByDescending(member => member.Geometry is Curve { IsClosed: true }).First();
                record = (key, boundary, group.Name, members.Length);
              }
            }
          }
          groupRecords[index] = record;
        }
        if (record.HasValue) groups.Add(record.Value);
      }
      if (groups.Count > 0)
      {
        var group = groups.OrderBy(item => item.Count).First();
        Add(group.Key, group.Boundary, group.Name);
      }
      else
      {
        string key = BoundaryKey(obj.Id);
        if (HasRecord(doc, key) || HasType(obj)) Add(key, obj, obj.Attributes.Name);
      }
    }
    return result.Values.ToList();

    void Add(string key, RhinoObject boundary, string name)
    {
      if (string.IsNullOrWhiteSpace(name)) name = "Part " + boundary.Id.ToString("N")[..8];
      result.TryAdd(key, new PartDataTarget(key, PartDataStore.PartName(doc, key, name), boundary.Id, Plane.Unset));
    }
  }

  private static IReadOnlyList<PartDataTarget> ResolveGeometry(RhinoDoc doc, IReadOnlyList<RhinoObject> selection)
  {
    var originals = selection.Where(Supported).ToList();
    var originalIds = originals.Select(obj => obj.Id).ToHashSet();
    var objects = originals.ToDictionary(obj => obj.Id);
    var members = new Dictionary<int, RhinoObject[]>();
    RhinoObject[] Members(int index)
    {
      if (!members.TryGetValue(index, out var result))
        members[index] = result = doc.Groups.GroupMembers(index) ?? Array.Empty<RhinoObject>();
      return result;
    }
    var groups = originals.Select(obj => (obj.Attributes.GetGroupList() ?? Array.Empty<int>())
      .Where(index => index >= 0 && index < doc.Groups.Count && !doc.Groups[index].IsDeleted)
      .OrderBy(index => Members(index).Length).ToArray()).ToArray();

    // vNest identifies the smallest complete part group, not an enclosing assembly.
    int levels = Math.Max(1, groups.Select(list => list.Length).DefaultIfEmpty().Max());
    for (int level = 0; level < levels; level++)
    {
      if (groups.Any(list => list.Length > level))
      {
        foreach (var list in groups.Where(list => list.Length > level))
          foreach (var member in Members(list[level]).Where(Supported))
            objects.TryAdd(member.Id, member);
      }
      var targets = Capture(doc, objects.Values.ToList(), originalIds, Members);
      if (targets.Count > 0)
        return targets;
    }
    return Array.Empty<PartDataTarget>();
  }

  private static List<PartDataTarget> Capture(RhinoDoc doc, List<RhinoObject> objects,
    HashSet<Guid> originalIds, Func<int, RhinoObject[]> members)
  {
    double tolerance = doc.ModelAbsoluteTolerance;
    var regions = new List<Region>();
    var targets = new List<PartDataTarget>();
    try
    {
      var buckets = objects.GroupBy(obj => (obj.Attributes.GetGroupList() ?? Array.Empty<int>())
        .Where(index => index >= 0 && index < doc.Groups.Count && !doc.Groups[index].IsDeleted)
        .OrderBy(index => members(index).Length).FirstOrDefault(-1));
      foreach (var bucket in buckets)
        AddRegions(doc, bucket.ToList(), regions, tolerance);
      if (regions.Count == 0)
        AddRegions(doc, objects, regions, tolerance);
      if (regions.Count == 0)
        return targets;

      var assignments = regions.Select(_ => new List<RhinoObject>()).ToArray();
      foreach (var obj in objects)
      {
        var point = RepresentativePoint(obj.Geometry);
        var bucket = Enumerable.Range(0, regions.Count).Where(index => regions[index].Objects.Contains(obj)).ToArray();
        var choices = bucket.Where(index => Contains(regions[index], point, tolerance)).ToArray();
        if (choices.Length == 0)
          choices = Enumerable.Range(0, regions.Count).Where(index => !bucket.Contains(index)
            && Contains(regions[index], point, tolerance)).ToArray();
        if (choices.Length == 0)
          choices = bucket.Length > 0 ? bucket : Enumerable.Range(0, regions.Count).ToArray();
        int best = choices.OrderBy(index => Distance(regions[index].Outline, point)).First();
        assignments[best].Add(obj);
      }

      for (int i = 0; i < regions.Count; i++)
      {
        var items = assignments[i];
        if (!items.Any(obj => originalIds.Contains(obj.Id)))
          continue;
        var region = regions[i];
        var boundary = items.Where(obj => obj.Geometry is Curve curve && Coincident(curve, region.Outline, tolerance))
          .OrderByDescending(IsSeamAllowance)
          .ThenByDescending(obj => ((Curve)obj.Geometry).GetLength()).FirstOrDefault();
        if (boundary == null)
          continue;
        var ids = items.Select(obj => obj.Id).ToHashSet();
        int groupIndex = (boundary.Attributes.GetGroupList() ?? Array.Empty<int>())
          .Where(index => index >= 0 && index < doc.Groups.Count && !doc.Groups[index].IsDeleted)
          .OrderBy(index => members(index).Length)
          .FirstOrDefault(index => members(index).Length > 0 && members(index).All(obj => ids.Contains(obj.Id)), -1);
        string key = groupIndex >= 0 ? GroupKey(doc.Groups[groupIndex].Id) : BoundaryKey(boundary.Id);
        var label = items.Where(obj => obj.Geometry is TextEntity text && !string.IsNullOrWhiteSpace(text.PlainText))
          .OrderByDescending(obj => LeafLayer(doc, obj) == ReferenceLayer)
          .ThenByDescending(obj =>
          {
            var bounds = obj.Geometry.GetBoundingBox(region.Plane);
            return (bounds.Max.X - bounds.Min.X) * (bounds.Max.Y - bounds.Min.Y);
          }).FirstOrDefault();
        string name = groupIndex >= 0 ? doc.Groups[groupIndex].Name : boundary.Attributes.Name;
        if (label?.Geometry is TextEntity textLabel)
          name = System.Text.RegularExpressions.Regex.Replace(textLabel.PlainText, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(name))
          name = "Part " + boundary.Id.ToString("N")[..8];
        name = PartDataStore.PartName(doc, key, name);
        var x = region.Outline.TangentAtStart;
        x -= region.Plane.Normal * (x * region.Plane.Normal);
        if (!x.Unitize())
          x = region.Plane.XAxis;
        var frame = new Plane(region.Outline.PointAtStart, x, Vector3d.CrossProduct(region.Plane.Normal, x));
        targets.Add(new PartDataTarget(key, name, boundary.Id, frame));
      }
      return targets.DistinctBy(target => target.Key).ToList();
    }
    catch (Exception ex)
    {
      Log.Write("PartData", $"Part discovery failed: {ex.Message}");
      return new List<PartDataTarget>();
    }
    finally
    {
      foreach (var region in regions)
        region.Outline.Dispose();
    }
  }

  private static void AddRegions(RhinoDoc doc, List<RhinoObject> objects, List<Region> regions, double tolerance)
  {
    var curves = objects.Select(obj => obj.Geometry).OfType<Curve>().ToArray();
    if (curves.Length == 0)
      return;
    var closed = objects.Where(obj => obj.Geometry is Curve { IsClosed: true } curve
      && curve.TryGetPlane(out _, tolerance)).ToArray();
    var cutting = closed.Where(obj => !LeafLayer(doc, obj).Equals(ReferenceLayer, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (cutting.Length > 0)
      closed = cutting;
    var loops = closed.Select(obj => ((Curve)obj.Geometry).DuplicateCurve()).ToList();
    try
    {
      if (objects.Any(IsSeamAllowance))
        loops.AddRange(SeamLoops(doc, objects, tolerance));
      if (loops.Count == 0)
        loops.AddRange(BooleanLoops(curves, tolerance));
      foreach (var candidate in loops)
      {
        if (!candidate.IsClosed || !candidate.TryGetPlane(out var plane, tolerance))
          continue;
        using var mass = AreaMassProperties.Compute(candidate);
        if (mass == null || mass.Area <= tolerance * tolerance)
          continue;
        if (loops.Any(other => !ReferenceEquals(candidate, other) && ContainsCurve(other, candidate, tolerance)))
          continue;
        var reference = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? plane;
        var normal = plane.Normal;
        if (normal * reference.Normal < 0)
          normal = -normal;
        var x = reference.XAxis - (reference.XAxis * normal) * normal;
        if (!x.Unitize())
        {
          x = reference.YAxis - (reference.YAxis * normal) * normal;
          x.Unitize();
        }
        regions.Add(new Region(candidate.DuplicateCurve(), new Plane(plane.Origin, x,
          Vector3d.CrossProduct(normal, x)), objects));
      }
    }
    finally
    {
      foreach (var loop in loops)
        loop.Dispose();
    }
  }

  private static List<Curve> SeamLoops(RhinoDoc doc, List<RhinoObject> objects, double tolerance)
  {
    var seamCurves = objects.Where(IsSeamAllowance).Select(obj => (Curve)obj.Geometry).ToArray();
    var joined = Curve.JoinCurves(seamCurves, tolerance) ?? Array.Empty<Curve>();
    if (joined.Length > 0 && joined.All(curve => curve.IsClosed && curve.TryGetPlane(out _, tolerance)))
      return joined.ToList();
    foreach (var curve in joined)
      curve.Dispose();

    // Partial allowances close against finished edges, including sections with zero allowance.
    var cuttingCurves = objects.Where(obj => IsSeamAllowance(obj)
      || !string.IsNullOrEmpty(obj.Attributes.GetUserString("vSeam.sourceOwner"))
      || !LeafLayer(doc, obj).Equals(ReferenceLayer, StringComparison.OrdinalIgnoreCase))
      .Select(obj => obj.Geometry).OfType<Curve>().ToArray();
    var regions = BooleanLoops(cuttingCurves, tolerance);
    try
    {
      return regions.Count > 0
        ? (Curve.CreateBooleanUnion(regions, tolerance) ?? Array.Empty<Curve>()).ToList()
        : new List<Curve>();
    }
    finally
    {
      foreach (var region in regions)
        region.Dispose();
    }
  }

  private static List<Curve> BooleanLoops(Curve[] curves, double tolerance)
  {
    var points = curves.SelectMany(curve => new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }
      .Select(fraction => curve.PointAt(curve.Domain.ParameterAt(fraction))));
    if (Plane.FitPlaneToPoints(points, out var plane) != PlaneFitResult.Success
      || curves.Any(curve => !curve.IsInPlane(plane, tolerance)))
      return new List<Curve>();
    using var regions = Curve.CreateBooleanRegions(curves, plane, true, tolerance);
    return regions == null ? new List<Curve>() : Enumerable.Range(0, regions.RegionCount)
      .SelectMany(regions.RegionCurves).ToList();
  }

  private static bool ContainsCurve(Curve outer, Curve inner, double tolerance)
  {
    if (!outer.TryGetPlane(out var plane, tolerance))
      return false;
    using var projection = Curve.ProjectToPlane(inner, plane);
    using var outerMass = AreaMassProperties.Compute(outer);
    using var innerMass = AreaMassProperties.Compute(projection ?? inner);
    return outerMass != null && innerMass != null && outerMass.Area > innerMass.Area + tolerance * tolerance
      && Enumerable.Range(0, BoundarySamples).All(index => outer.Contains(
        (projection ?? inner).PointAtNormalizedLength(index / (double)(BoundarySamples - 1)), plane, tolerance)
          is PointContainment.Inside or PointContainment.Coincident);
  }

  private static bool Contains(Region region, Point3d point, double tolerance)
  {
    var delta = point - region.Plane.Origin;
    double x = delta * region.Plane.XAxis, y = delta * region.Plane.YAxis;
    double padding = tolerance * BoundaryToleranceFactor;
    return x >= region.Bounds.Min.X - padding && x <= region.Bounds.Max.X + padding
      && y >= region.Bounds.Min.Y - padding && y <= region.Bounds.Max.Y + padding
      && region.Outline.Contains(point, region.Plane, padding) is PointContainment.Inside or PointContainment.Coincident;
  }

  private static bool Coincident(Curve curve, Curve boundary, double tolerance) =>
    Enumerable.Range(0, BoundarySamples).All(index =>
    {
      var point = curve.PointAtNormalizedLength(index / (double)(BoundarySamples - 1));
      return boundary.ClosestPoint(point, out var parameter)
        && point.DistanceTo(boundary.PointAt(parameter)) <= tolerance * BoundaryToleranceFactor;
    });

  private static string LeafLayer(RhinoDoc doc, RhinoObject obj) => obj.Attributes.LayerIndex >= 0
    && obj.Attributes.LayerIndex < doc.Layers.Count ? doc.Layers[obj.Attributes.LayerIndex].FullPath.Split("::").Last() : "";

  private static Point3d RepresentativePoint(GeometryBase geometry) => geometry switch
  {
    Rhino.Geometry.Point point => point.Location,
    TextDot dot => dot.Point,
    Curve curve => curve.PointAtNormalizedLength(0.5),
    _ => geometry.GetBoundingBox(true).Center
  };

  private static double Distance(Curve curve, Point3d point) => curve.ClosestPoint(point, out var parameter)
    ? point.DistanceTo(curve.PointAt(parameter)) : double.PositiveInfinity;
}
