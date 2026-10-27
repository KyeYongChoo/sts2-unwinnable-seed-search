using HarmonyLib;
using MegaCrit.Sts2.Core.Map;

/// <summary>
/// Drop-in replacement for MapPathPruning.FindMatchingSegments, which dominates map generation (~350 ms per map).
///
/// The original visits every segment of every start-to-boss path and, for each, builds a string key and checks it
/// against the dictionary. A segment whose exact point sequence was already processed is always a no-op the second
/// time: its key is present, and either it was added (so it overlaps itself) or it overlapped an entry that is still
/// in the list. Skipping those repeats therefore yields the same dictionary, with the same entries in the same order,
/// so the later RNG shuffles see identical input. Verified map-for-map with `SeedSearch validatemap`.
/// </summary>
static class FastPruning
{
    static readonly Func<IReadOnlyList<MapPoint>, string> _segmentKey =
        AccessTools.MethodDelegate<Func<IReadOnlyList<MapPoint>, string>>(AccessTools.Method(typeof(MapPathPruning), "GenerateSegmentKey"));

    static Harmony? _harmony;

    public static void Enable()
    {
        _harmony ??= new Harmony("seedsearch.fastpruning");
        _harmony.Patch(AccessTools.Method(typeof(MapPathPruning), nameof(MapPathPruning.FindMatchingSegments)),
            prefix: new HarmonyMethod(typeof(FastPruning), nameof(Prefix)));
    }

    public static void Disable() => _harmony?.UnpatchAll("seedsearch.fastpruning");

    static bool Prefix(MapPoint startingMapPoint, ref List<List<MapPoint[]>> __result)
    {
        __result = FindMatchingSegmentsDfs(startingMapPoint);
        return false;
    }

    /// <summary>
    /// Same result without enumerating whole paths. Segments that share a key share their first and last point, and
    /// the original meets them in the depth-first order (over Children, as FindAllPaths iterates) of their middle part:
    /// the first path containing a segment is the DFS-first way into its start, the segment, then the DFS-first way out
    /// of its end, so two such segments' first paths differ only inside the segment. So a DFS from each valid start
    /// point, restricted to points that lie on some start-to-boss path, produces every list in the original order.
    /// </summary>
    public static List<List<MapPoint[]>> FindMatchingSegmentsDfs(MapPoint startingMapPoint)
    {
        // Points on some start-to-boss path: reachable from the start and able to reach a boss.
        var reachesBoss = new Dictionary<MapPoint, bool>();
        bool ReachesBoss(MapPoint p)
        {
            if (p.PointType == MapPointType.Boss) return true;
            if (reachesBoss.TryGetValue(p, out var v)) return v;
            bool r = false;
            foreach (var c in p.Children)
                r |= ReachesBoss(c);
            return reachesBoss[p] = r;
        }
        var onPath = new List<MapPoint>();
        var visited = new HashSet<MapPoint>();
        void Collect(MapPoint p)
        {
            if (!visited.Add(p) || !ReachesBoss(p)) return;
            onPath.Add(p);
            if (p.PointType == MapPointType.Boss) return;
            foreach (var c in p.Children) Collect(c);
        }
        Collect(startingMapPoint);

        var segments = new SortedDictionary<string, List<MapPoint[]>>(StringComparer.Ordinal);
        var stack = new List<MapPoint>(20);
        void Extend(MapPoint p)
        {
            if (p.PointType == MapPointType.Boss) return; // FindAllPaths stops at the boss
            foreach (var c in p.Children)
            {
                if (!ReachesBoss(c)) continue;
                stack.Add(c);
                if (stack.Count >= 3 && c.parents.Count >= 2)
                {
                    var array = stack.ToArray();
                    string key = _segmentKey(array);
                    if (!segments.TryGetValue(key, out var list))
                        segments[key] = new List<MapPoint[]> { array };
                    else if (!list.Any(existing => Overlapping(existing, array)))
                        list.Add(array);
                }
                Extend(c);
                stack.RemoveAt(stack.Count - 1);
            }
        }
        foreach (var start in onPath)
        {
            if (start.Children.Count <= 1 && start.coord.row != 0) continue;
            if (start.PointType == MapPointType.Boss) continue;
            stack.Clear();
            stack.Add(start);
            Extend(start);
        }
        return segments.Values.Where(l => l.Count > 1).ToList();
    }

    public static List<List<MapPoint[]>> FindMatchingSegments(MapPoint startingMapPoint)
    {
        var paths = MapPathPruning.FindAllPaths(startingMapPoint);
        var segments = new SortedDictionary<string, List<MapPoint[]>>(StringComparer.Ordinal);
        var seen = new HashSet<(ulong, ulong, int)>();
        foreach (var path in paths)
        {
            int n = path.Count;
            for (int i = 0; i < n - 1; i++)
            {
                var start = path[i];
                // IsValidSegmentStartMapPoint
                if (start.Children.Count <= 1 && start.coord.row != 0)
                    continue;
                ulong h1 = Mix(14695981039346656037UL, start), h2 = Mix(1099511628211UL, start, 31);
                h1 = Mix(h1, path[i + 1]);
                h2 = Mix(h2, path[i + 1], 31);
                for (int j = 2; j < n - i; j++)
                {
                    var end = path[i + j];
                    h1 = Mix(h1, end);
                    h2 = Mix(h2, end, 31);
                    // IsValidSegmentEndMapPoint
                    if (end.parents.Count < 2)
                        continue;
                    if (!seen.Add((h1, h2, j)))
                        continue;
                    var array = new MapPoint[j + 1];
                    path.CopyTo(i, array, 0, j + 1);
                    string key = _segmentKey(array);
                    if (!segments.TryGetValue(key, out var list))
                        segments[key] = new List<MapPoint[]> { array };
                    else if (!list.Any(existing => Overlapping(existing, array)))
                        list.Add(array);
                }
            }
        }
        return segments.Values.Where(l => l.Count > 1).ToList();
    }

    // Same as MapPathPruning.OverlappingSegment (points compare by coord).
    static bool Overlapping(MapPoint[] a, MapPoint[] b)
    {
        if (a.Length < 3 || b.Length < 3)
            return false;
        for (int i = 1; i <= a.Length - 2; i++)
            if (Equals(a[i], b[i]))
                return true;
        return false;
    }

    static ulong Mix(ulong h, MapPoint p, int salt = 0)
    {
        ulong v = (ulong)(p.coord.col * 64 + p.coord.row + 1 + salt * 4096);
        h ^= v;
        h *= 0x100000001B3UL ^ (ulong)salt;
        return (h ^ (h >> 29)) * 0xBF58476D1CE4E5B9UL;
    }
}
