using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;

namespace RimBridgeServer;

internal sealed class MapThingsCapabilityModule
{
    private static readonly Regex RichText = new("<[^>]+>", RegexOptions.CultureInvariant);

    public object FindMapThings(string name = null, string defName = null, string kind = "any",
        int? x = null, int? z = null, int? radius = null, bool fuzzy = true, int limit = 40, int offset = 0)
    {
        var map = RimWorldState.CurrentMapOrThrow();
        if (kind is not ("any" or "item" or "mineable" or "building" or "plant" or "pawn"))
            throw new ArgumentException("Unknown kind.");
        if (x.HasValue != z.HasValue || (radius.HasValue && (!x.HasValue || radius < 0 || radius > 1000)))
            throw new ArgumentException("Provide x and z together. Radius requires a center and must be between 0 and 1000.");
        if (limit < 1 || limit > 100 || offset < 0 || name?.Length > 120)
            throw new ArgumentException("Limit must be 1–100, offset nonnegative and name at most 120 characters.");
        if (x.HasValue && !new IntVec3(x.Value, 0, z.Value).InBounds(map))
            throw new ArgumentException("The search center must be on the current map.");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var query = Normalize(name);
        var hasCenter = x.HasValue;
        var maxDistance = (long)(radius ?? 1000) * (radius ?? 1000);
        var defScores = new Dictionary<ThingDef, int>();
        var matches = new List<(Thing Thing, int Score, long Distance)>();
        foreach (var thing in map.listerThings.AllThings)
        {
            if (!thing.Spawned || thing.Position.Fogged(map) || !MatchesKind(thing, kind))
                continue;
            if (!string.IsNullOrWhiteSpace(defName) && !string.Equals(thing.def.defName, defName.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            long distance = 0;
            if (hasCenter)
            {
                long dx = thing.Position.x - x.Value;
                long dz = thing.Position.z - z.Value;
                distance = dx * dx + dz * dz;
                if (radius.HasValue && distance > maxDistance)
                    continue;
            }
            if (!defScores.TryGetValue(thing.def, out var score))
            {
                score = Math.Max(MatchScore(query, Normalize(thing.def.defName), fuzzy), MatchScore(query, Normalize(thing.def.label), fuzzy));
                defScores.Add(thing.def, score);
            }
            if (query.Length != 0 && (thing is Pawn || thing.Stuff != null))
                score = Math.Max(score, MatchScore(query, Normalize(thing.LabelNoCount), fuzzy));
            if (score >= 0)
                matches.Add((thing, score, distance));
        }

        var rows = matches.OrderByDescending(entry => entry.Score).ThenBy(entry => entry.Distance)
            .ThenBy(entry => entry.Thing.thingIDNumber).Skip(offset).Take(limit)
            .Select(entry => new
            {
                thingId = RimWorldState.GetThingId(entry.Thing),
                defName = entry.Thing.def.defName,
                label = entry.Thing.LabelNoCount,
                x = entry.Thing.Position.x,
                z = entry.Thing.Position.z,
                quantity = entry.Thing.stackCount,
                matchScore = entry.Score
            }).ToArray();
        return new
        {
            success = true,
            mapId = RimWorldState.GetMapId(map),
            total = matches.Count,
            nextOffset = (long)offset + rows.Length < matches.Count && rows.Length > 0 ? (int?)(offset + rows.Length) : null,
            searchMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            things = rows
        };
    }

    public object DesignateMapThings(string mapId, string[] thingIds, string action, bool dryRun = false)
    {
        var map = RimWorldState.CurrentMapOrThrow();
        if (!string.Equals(mapId, RimWorldState.GetMapId(map), StringComparison.Ordinal))
            throw new ArgumentException("The current map changed. Search again before designating.");
        if (thingIds == null || thingIds.Length == 0 || thingIds.Length > 100 || thingIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Specify 1–100 nonempty thing IDs.");

        Designator tool = action switch
        {
            "mine" => DesignatorFor<Designator_Mine>(),
            "haul" => DesignatorFor<Designator_Haul>(),
            "harvest" => DesignatorFor<Designator_PlantsHarvest>(),
            _ => throw new ArgumentException("Supported actions: mine, haul, harvest.")
        };
        var wanted = new HashSet<string>(thingIds, StringComparer.Ordinal);
        var targets = new Dictionary<string, Thing>(StringComparer.Ordinal);
        foreach (var thing in map.listerThings.AllThings)
        {
            var id = RimWorldState.GetThingId(thing);
            if (id != null && wanted.Contains(id))
                targets.Add(id, thing);
        }

        var rows = thingIds.Distinct(StringComparer.Ordinal)
            .Select(id => DesignateOne(map, targets.TryGetValue(id, out var target) ? target : null, id, tool, action, dryRun))
            .ToArray();
        return new { success = rows.All(row => row.Status is "applied" or "already_designated" or "accepted"), mapId, action, dryRun, results = rows };
    }

    private static Designator DesignatorFor<T>() where T : Designator
    {
        var tool = DefDatabase<DesignationCategoryDef>.GetNamedSilentFail("Orders")?.ResolvedAllowedDesignators
            .FirstOrDefault(designator => designator.GetType() == typeof(T));
        return tool ?? throw new InvalidOperationException($"The {typeof(T).Name} order is unavailable.");
    }

    private static DesignationResult DesignateOne(Map map, Thing target, string id, Designator tool, string action, bool dryRun)
    {
        if (target == null || !target.Spawned || target.Map != map || target.Position.Fogged(map))
            return new DesignationResult(id, "rejected", "The target is no longer visible on this map.");
        var cell = target.Position;
        var manager = map.designationManager;
        var designation = tool.Designation;
        bool mining = action == "mine";
        if (mining && cell.GetFirstMineable(map) != target)
            return new DesignationResult(id, "rejected", "The target is not the mineable object at its cell.");
        bool HasDesignation() => mining
            ? manager.DesignationAt(cell, designation) != null
            : manager.DesignationOn(target, designation) != null;
        if (HasDesignation())
            return new DesignationResult(id, "already_designated", null);
        var report = mining ? tool.CanDesignateCell(cell) : tool.CanDesignateThing(target);
        if (!report.Accepted)
            return new DesignationResult(id, "rejected", report.Reason ?? "The game rejected the order.");
        if (dryRun)
            return new DesignationResult(id, "accepted", null);

        // The native mine tool can instantly destroy rock in god mode.
        if (DebugSettings.godMode)
            return new DesignationResult(id, "rejected", "Turn off god mode to apply an ordinary work designation.");
        if (mining)
            tool.DesignateSingleCell(cell);
        else
            tool.DesignateThing(target);
        return HasDesignation()
            ? new DesignationResult(id, "applied", null)
            : new DesignationResult(id, "rejected", "The order returned without creating a designation.");
    }

    private sealed class DesignationResult(string thingId, string status, string reason)
    {
        public string ThingId { get; } = thingId;
        public string Status { get; } = status;
        public string Reason { get; } = reason;
    }

    private static bool MatchesKind(Thing thing, string kind) => kind switch
    {
        "item" => thing.def.category == ThingCategory.Item,
        "mineable" => thing.def.mineable,
        "building" => thing is Building && !thing.def.mineable,
        "plant" => thing is Plant,
        "pawn" => thing is Pawn,
        _ => true
    };

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var result = new StringBuilder(value.Length);
        foreach (char c in RichText.Replace(value, string.Empty).Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(c))
                result.Append(char.ToLowerInvariant(c));
        return result.ToString();
    }

    private static int MatchScore(string query, string candidate, bool fuzzy)
    {
        if (query.Length == 0 || query == candidate)
            return 100;
        if (candidate.StartsWith(query, StringComparison.Ordinal))
            return 85;
        if (candidate.Contains(query))
            return 70;
        if (!fuzzy || query.Length < 3)
            return -1;

        var allowance = query.Length < 7 ? 1 : 2;
        if (Math.Abs(candidate.Length - query.Length) > allowance)
            return -1;
        var previous = new int[candidate.Length + 1];
        var current = new int[candidate.Length + 1];
        for (int j = 0; j < previous.Length; j++)
            previous[j] = j;
        for (int i = 1; i <= query.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= candidate.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (query[i - 1] == candidate[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[candidate.Length] <= allowance ? 60 - previous[candidate.Length] : -1;
    }
}
