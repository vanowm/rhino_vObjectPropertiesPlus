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

  private sealed record Region(Curve Outline, Plane Plane, IReadOnlyList<RhinoObject> Objects)
  {
    internal BoundingBox Bounds { get; } = Outline.GetBoundingBox(Plane);
  }

  internal static bool IsCandidate(RhinoObject obj) => Supported(obj)
    && ((obj.Attributes.GetGroupList()?.Length ?? 0) > 0 || obj.Geometry is Curve { IsClosed: true });

  private static bool Supported(RhinoObject obj) => !obj.IsDeleted && !IsTableDecoration(obj)
    && obj.Geometry is Curve or AnnotationBase or Rhino.Geometry.Point or TextDot;

  private static bool IsTableDecoration(RhinoObject obj) =>
    obj.Attributes.GetUserString("vNest.role") is "table-border" or "table-label" or "table-label-frame"
    || obj.Attributes.Name == "NestTable";

  internal static IReadOnlyList<PartDataTarget> Resolve(RhinoDoc doc, IReadOnlyList<RhinoObject> selection)
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
          .OrderByDescending(obj => ((Curve)obj.Geometry).GetLength()).FirstOrDefault();
        if (boundary == null)
          continue;
        var ids = items.Select(obj => obj.Id).ToHashSet();
        int groupIndex = (boundary.Attributes.GetGroupList() ?? Array.Empty<int>())
          .Where(index => index >= 0 && index < doc.Groups.Count && !doc.Groups[index].IsDeleted)
          .OrderBy(index => members(index).Length)
          .FirstOrDefault(index => members(index).Length > 0 && members(index).All(obj => ids.Contains(obj.Id)), -1);
        string key = groupIndex >= 0 ? "group-" + doc.Groups[groupIndex].Id.ToString("N")
          : "boundary-" + boundary.Id.ToString("N");
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
    var loops = closed.Length > 0
      ? closed.Select(obj => ((Curve)obj.Geometry).DuplicateCurve()).ToList()
      : BooleanLoops(curves, tolerance);
    try
    {
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
