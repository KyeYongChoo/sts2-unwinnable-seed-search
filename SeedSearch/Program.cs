using System.Diagnostics;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

Headless.Boot();

var opts = Cli.Parse(args);
var config = Config.Load(opts.GetValueOrDefault("config", File.Exists("config.json") ? "config.json" : Path.Combine(AppContext.BaseDirectory, "config.json")));
if (opts.TryGetValue("ascension", out var asc))
    config.ascension = int.Parse(asc);
Sim.SetAscension(config.ascension);
if (!opts.ContainsKey("slowmap"))
    FastPruning.Enable();
config.Validate();

switch (opts.GetValueOrDefault("command", "help"))
{
    case "describe":
        Report.Describe(SeedHelper.CanonicalizeSeed(opts["arg"]), config);
        break;
    case "check":
        var verdict = Search.Check(SeedHelper.CanonicalizeSeed(opts["arg"]), config);
        Console.WriteLine(verdict.Passed ? Report.Summary(SeedHelper.CanonicalizeSeed(opts["arg"]), verdict) : $"FAIL at: {verdict.FailedAt}");
        break;
    case "search":
        Search.Run(config, opts);
        break;
    case "stats":
        Search.Stats(config, opts);
        break;
    case "cards":
        Report.ListCards();
        break;
    case "validatemap":
    {
        // Builds each map with the game's own pruning and with FastPruning, and compares every point and edge.
        var act = ModelDb.Act<Overgrowth>();
        var rng = new Rng(ulong.Parse(opts.GetValueOrDefault("from", "99")));
        int n = int.Parse(opts.GetValueOrDefault("count", "100")), mismatches = 0;
        var slow = new Stopwatch();
        var fast = new Stopwatch();
        for (int i = 0; i < n; i++)
        {
            string seed = SeedHelper.GetRandomSeed(rng);
            FastPruning.Disable();
            slow.Start(); var a = Report.MapSignature(Sim.Act1Map(seed, act)); slow.Stop();
            FastPruning.Enable();
            fast.Start(); var b = Report.MapSignature(Sim.Act1Map(seed, act)); fast.Stop();
            if (a != b) { mismatches++; Console.WriteLine($"MISMATCH {seed}"); }
        }
        Console.WriteLine($"{n} maps, {mismatches} mismatches. original {slow.Elapsed.TotalMilliseconds / n:F1} ms/map, fast {fast.Elapsed.TotalMilliseconds / n:F1} ms/map");
        break;
    }
    case "relics":
    {
        // Relics a chest (shared bag) or This or That (player bag) can give, with the hooks each overrides.
        var player = Sim.NewRun("0", allActsRooms: false).Players[0];
        var pools = new[] { ModelDb.RelicPool<MegaCrit.Sts2.Core.Models.RelicPools.SharedRelicPool>(), player.Character.RelicPool };
        foreach (var pool in pools)
            foreach (var relic in pool.AllRelics.OrderBy(r => r.Rarity).ThenBy(r => r.Id.Entry))
            {
                var hooks = relic.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)
                    .Where(m => m.GetBaseDefinition().DeclaringType != m.DeclaringType && !m.IsSpecialName)
                    .Select(m => m.Name).Where(n => !n.Contains("HoverTip") && !n.StartsWith("get_") && n != "DeepCloneFields").Distinct();
                var vars = string.Join(" ", relic.DynamicVars.Values.Select(v => $"{v.Name}={v.BaseValue}"));
                Console.WriteLine($"{pool.Id.Entry,-24} {relic.Rarity,-9} {relic.Id.Entry,-24} {vars,-28} {string.Join(",", hooks)}");
            }
        break;
    }
    case "relictest":
        // Finds which relics' pickup effects can run without the engine (a native crash kills the process).
        Sim.RewardsAfterNeow("AGRTQKUWEH84", opts["arg"], 1, includeFirstElite: false);
        Console.WriteLine("OK");
        break;
    default:
        Console.WriteLine("""
            Usage:
              SeedSearch describe <SEED>        map, encounters, Neow options, forced-elite routes and every replay
              SeedSearch check <SEED>           run every filter in config.json against one seed
              SeedSearch search [--count N] [--from N] [--out results.txt]
                                                random seeds; --from makes the seed sequence reproducible, so run
                                                several processes with different --from values to use every core
              SeedSearch stats [--count N] [--maps N]   pass rate of each filter on its own
              SeedSearch cards                  Necrobinder cards that can appear in combat card rewards
              SeedSearch validatemap [--count N]   check the fast map pruning against the game's own
            Options: --config path.json  --ascension N  --slowmap (use the game's own map pruning)
            """);
        break;
}

class Config
{
    public int ascension { get; set; }
    public string act1 { get; set; } = "OVERGROWTH";
    /// <summary>The act's first elite (the one every route is forced into). Empty allows any.</summary>
    public string firstElite { get; set; } = "BYGONE_EFFIGY_ELITE";
    /// <summary>Last map row (game floor = row + 1) where the forced elite may sit. Row 9 is the act 1 chest row,
    /// row 15 the rest sites before the boss.</summary>
    public int latestEliteRow { get; set; } = 14;
    /// <summary>Events a "?" room may turn into on the way to the elite.</summary>
    public List<string> allowedEvents { get; set; } = new();
    /// <summary>Map room types allowed before the elite besides fights, "?" rooms and the chest, e.g. "RestSite".</summary>
    public List<string> extraRoomsBeforeElite { get; set; } = new();
    /// <summary>Relics a chest or This or That may give on the way. Empty allows any.</summary>
    public List<string> allowedRelics { get; set; } = new();
    /// <summary>Neow may offer only these. Empty allows any (each offered option is replayed separately).</summary>
    public List<string> allowedNeowRelics { get; set; } = new();
    /// <summary>A seed fails if Neow offers any of these.</summary>
    public List<string> forbiddenNeowRelics { get; set; } = new();
    /// <summary>Potions that may not be obtainable before the elite (fight rewards or a Neow pickup).</summary>
    public List<string> forbiddenPotions { get; set; } = new();
    /// <summary>Every fight before the elite must offer only these cards. Empty disables the card filter.</summary>
    public List<string> allowedCards { get; set; } = new();

    public static Config Load(string path) => JsonSerializer.Deserialize<Config>(File.ReadAllText(path))!;

    /// <summary>Catches typos: every ID in the config must exist in the game.</summary>
    public void Validate()
    {
        var problems = new List<string>();
        void Check(IEnumerable<string> ids, IEnumerable<AbstractModel> models, string what)
        {
            var known = models.Select(m => m.Id.Entry).ToHashSet();
            problems.AddRange(ids.Where(id => !known.Contains(id)).Select(id => $"unknown {what} {id}"));
        }
        Check(allowedCards, ModelDb.AllCards, "card");
        Check(allowedRelics.Concat(allowedNeowRelics).Concat(forbiddenNeowRelics), ModelDb.AllRelics, "relic");
        Check(allowedEvents, ModelDb.AllEvents, "event");
        Check(forbiddenPotions, ModelDb.AllPotions, "potion");
        Check(new[] { firstElite }.Where(e => e != ""), ModelDb.AllEncounters, "encounter");
        Check(new[] { act1 }, ModelDb.Acts, "act");
        if (problems.Count > 0)
            throw new InvalidDataException("config.json: " + string.Join("; ", problems));
    }
}

static class Cli
{
    public static Dictionary<string, string> Parse(string[] args)
    {
        var d = new Dictionary<string, string>();
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
                d[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
            else
                positional.Add(args[i]);
        }
        if (positional.Count > 0) d["command"] = positional[0];
        if (positional.Count > 1) d["arg"] = positional[1];
        return d;
    }
}

static class Search
{
    public record Verdict(bool Passed, string? FailedAt, List<string>? Neow = null, MapCheck.Result? Map = null);

    /// <summary>
    /// Cheapest filters first: act pick, first elite, Neow, the first fight's cards (every route starts with that
    /// fight), then the map and a full replay of every route. The map has its own RNG stream and is the slow step.
    /// </summary>
    public static Verdict Check(string seed, Config config)
    {
        var act1 = Sim.PickAct1(seed);
        if (act1.Id.Entry != config.act1)
            return new(false, $"act 1 is {act1.Id.Entry}");

        var state = Sim.NewRun(seed, allActsRooms: false);
        // An empty firstElite allows any elite (the replay still lists which one it is).
        var firstElite = state.Act.PullNextEncounter(RoomType.Elite).Id.Entry;
        if (config.firstElite != "" && firstElite != config.firstElite)
            return new(false, $"first elite is {firstElite}");

        var neow = Sim.NeowOptions(state).Select(o => o.Relic?.Id.Entry ?? o.TextKey).ToList();
        var badNeow = neow.Where(n => config.forbiddenNeowRelics.Contains(n) || (config.allowedNeowRelics.Count > 0 && !config.allowedNeowRelics.Contains(n))).ToList();
        if (badNeow.Count > 0)
            return new(false, $"Neow offers {string.Join(", ", badNeow)}", neow);

        foreach (var relic in neow)
        {
            var first = Sim.RewardsAfterNeow(seed, relic, 1, includeFirstElite: false)[0];
            var bad = config.allowedCards.Count == 0 ? new() : first.Cards.Where(c => !config.allowedCards.Contains(c.TrimEnd('+'))).ToList();
            if (first.Potion != null && config.forbiddenPotions.Contains(first.Potion))
                bad.Add("potion " + first.Potion);
            if (bad.Count > 0)
                return new(false, $"first fight offers {string.Join(", ", bad)} (Neow: {relic})", neow);
        }

        var analysis = MapCheck.Analyze(seed, Sim.Act1Map(seed, act1), config, neow);
        if (analysis.Problem != null)
            return new(false, "map: " + analysis.Problem, neow, analysis);
        return new(true, null, neow, analysis);
    }

    public static void Run(Config config, Dictionary<string, string> opts)
    {
        long count = long.Parse(opts.GetValueOrDefault("count", "100000"));
        ulong from = opts.TryGetValue("from", out var f) ? ulong.Parse(f) : (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var outPath = opts.GetValueOrDefault("out", "results.txt");
        var seedRng = new Rng(from);
        var funnel = new SortedDictionary<string, long>();
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"Searching {count:N0} random seeds (--from {from}), ascension {config.ascension}. Matches go to {Path.GetFullPath(outPath)}");

        for (long i = 1; i <= count; i++)
        {
            string seed = SeedHelper.GetRandomSeed(seedRng);
            Verdict v;
            try
            {
                v = Check(seed, config);
            }
            catch (Exception e)
            {
                v = new(false, "error: " + e.GetType().Name);
                Console.WriteLine($"{seed}: {e.GetType().Name}: {e.Message}");
            }
            string stage = v.Passed ? "PASS" : Stage(v.FailedAt!);
            funnel[stage] = funnel.GetValueOrDefault(stage) + 1;
            if (v.Passed)
            {
                var text = Report.Summary(seed, v);
                Console.WriteLine(text);
                File.AppendAllText(outPath, text + Environment.NewLine);
            }
            else if (stage == "7 replay")
                Console.WriteLine($"near-miss {seed}: {v.FailedAt}");
            if (i % 10000 == 0 || i == count)
                Console.WriteLine($"[{i:N0} seeds, {i / sw.Elapsed.TotalSeconds:N0}/s] " + string.Join("  ", funnel.Select(kv => $"{kv.Key}: {kv.Value:N0}")));
        }
    }

    // Groups failure reasons into funnel stages in filter order.
    static string Stage(string failedAt) => failedAt switch
    {
        var s when s.StartsWith("act 1") => "1 act",
        var s when s.StartsWith("first elite") => "2 elite",
        var s when s.StartsWith("Neow") => "3 neow",
        var s when s.StartsWith("first fight") => "4 fight1",
        var s when s.StartsWith("map: a route") => "5 map",
        var s when s.StartsWith("map") => "7 replay",
        _ => "error",
    };

    /// <summary>
    /// Measures filters on random seeds, to estimate how rare a full match is. Map structure is sampled on Overgrowth
    /// seeds only, since replaying "?" rooms needs that seed's own run.
    /// </summary>
    public static void Stats(Config config, Dictionary<string, string> opts)
    {
        long count = long.Parse(opts.GetValueOrDefault("count", "20000"));
        int mapSamples = int.Parse(opts.GetValueOrDefault("maps", "300"));
        var seedRng = new Rng(opts.TryGetValue("from", out var f) ? ulong.Parse(f) : 1uL);
        long overgrowth = 0, effigy = 0, neowOk = 0;
        var okThrough = new long[11];
        for (long i = 0; i < count; i++)
        {
            string seed = SeedHelper.GetRandomSeed(seedRng);
            if (Sim.PickAct1(seed).Id.Entry == config.act1) overgrowth++;
            var state = Sim.NewRun(seed, allActsRooms: false);
            if (state.Act.PullNextEncounter(RoomType.Elite).Id.Entry == config.firstElite) effigy++;
            var neow = Sim.NeowOptions(state).Select(o => o.Relic?.Id.Entry ?? o.TextKey).ToList();
            if (!neow.Any(config.forbiddenNeowRelics.Contains)) neowOk++;
            var results = Sim.RewardsAfterNeow(seed, null, okThrough.Length - 1, includeFirstElite: false);
            int ok = 0;
            while (ok < results.Count && results[ok].Cards.All(c => config.allowedCards.Count == 0 || config.allowedCards.Contains(c.TrimEnd('+')))) ok++;
            for (int k = 0; k <= ok; k++) okThrough[k]++;
        }
        Console.WriteLine($"{count:N0} random seeds:");
        Console.WriteLine($"  act 1 = {config.act1}: {overgrowth / (double)count:P2}");
        Console.WriteLine($"  first elite = {config.firstElite} (all seeds, any act): {effigy / (double)count:P2}");
        Console.WriteLine($"  Neow offers nothing forbidden: {neowOk / (double)count:P2}");
        Console.WriteLine("  first k fights in a row offer only allowed cards (no Neow relic, nothing in between):");
        for (int k = 1; k < okThrough.Length; k++)
            Console.WriteLine($"    k={k,2}: {okThrough[k]}/{count} = {okThrough[k] / (double)count:P4}");

        var act = ModelDb.Act<Overgrowth>();
        var outcomes = new SortedDictionary<string, int>();
        var fightsHist = new SortedDictionary<int, int>();
        var permissive = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(config))!;
        permissive.allowedCards = new();
        permissive.allowedRelics = new();
        for (int i = 0; i < mapSamples;)
        {
            string seed = SeedHelper.GetRandomSeed(seedRng);
            if (Sim.PickAct1(seed).Id.Entry != config.act1) continue;
            i++;
            var analysis = MapCheck.Analyze(seed, Sim.Act1Map(seed, act), permissive, new[] { "" });
            string key = analysis.Problem == null ? "FORCED" : System.Text.RegularExpressions.Regex.Replace(analysis.Problem, @"\d+|[A-Z_]{4,}", "#");
            outcomes[key] = outcomes.GetValueOrDefault(key) + 1;
            if (analysis.Problem == null)
            {
                fightsHist[analysis.MaxFights] = fightsHist.GetValueOrDefault(analysis.MaxFights) + 1;
                Console.WriteLine($"    forced: {seed}  {analysis.Routes} distinct routes, {analysis.MinFights}-{analysis.MaxFights} fights before the elite");
            }
        }
        Console.WriteLine($"  Overgrowth maps ({mapSamples} sampled), ignoring card and relic filters, forced into an elite by floor {config.latestEliteRow + 1}:");
        foreach (var (k, v) in outcomes.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"    {v,5}  {k}");
        Console.WriteLine("    most fights before the elite on forced maps: " + string.Join("  ", fightsHist.Select(kv => $"{kv.Key}: {kv.Value}")));
    }
}

static class Report
{
    public static string MapSignature(ActMap map) =>
        string.Join(";", map.GetAllMapPoints().OrderBy(p => p.coord.row).ThenBy(p => p.coord.col)
            .Select(p => $"{p.coord.col},{p.coord.row}:{p.PointType}>" + string.Join("|", p.Children.Select(c => $"{c.coord.col},{c.coord.row}").Order())));

    public static void Describe(string seed, Config config)
    {
        var state = Sim.NewRun(seed, createMap: true);
        Console.WriteLine($"Seed {seed}  (hash {state.Rng.Seed}, ascension {config.ascension}, Necrobinder)");
        Console.WriteLine("Acts: " + string.Join(", ", state.Acts.Select(a => a.Id.Entry)));
        var map = state.Map;
        Console.WriteLine("Act 1 map (game floor = row + 1; M fight, E elite, ? unknown, R rest, $ shop, T treasure, A ancient):");
        for (int r = map.GetRowCount() - 1; r >= 0; r--)
        {
            var cells = Enumerable.Range(0, 7).Select(c => map.GetPoint(c, r) is { } p ? Glyph(p.PointType) : '.');
            Console.WriteLine($"  row {r,2} / floor {r + 1,2}   {string.Join(" ", cells)}");
        }
        var act = state.Act;
        var elites = new List<string>();
        for (int i = 0; i < 3; i++) { elites.Add(act.PullNextEncounter(RoomType.Elite).Id.Entry); act.MarkRoomVisited(RoomType.Elite); }
        var fights = new List<string>();
        for (int i = 0; i < 6; i++) { fights.Add(act.PullNextEncounter(RoomType.Monster).Id.Entry); act.MarkRoomVisited(RoomType.Monster); }
        Console.WriteLine("Elites in order: " + string.Join(", ", elites));
        Console.WriteLine("Hallway fights in order: " + string.Join(", ", fights));
        Console.WriteLine("Boss: " + act.PullNextEncounter(RoomType.Boss).Id.Entry);
        var neow = Sim.NeowOptions(state).Select(o => o.Relic?.Id.Entry ?? o.TextKey).ToList();
        Console.WriteLine("Neow options: " + string.Join(", ", neow));
        foreach (var pick in neow)
        {
            Console.WriteLine($"First 6 fights in a row if you take {pick}:");
            var offered = Sim.Obtain(Sim.NewRun(seed, allActsRooms: false).Players[0], pick).Display();
            if (offered.Count > 0)
                Console.WriteLine($"  pickup offers: {string.Join(", ", offered)}");
            foreach (var (r, i) in Sim.RewardsAfterNeow(seed, pick, 6, includeFirstElite: false).Select((r, i) => (r, i)))
                Console.WriteLine($"  fight {i + 1} {r.Encounter,-28} gold {r.Gold,3}  potion {r.Potion ?? "-",-18} cards {string.Join(", ", r.Cards)}");
        }
        var analysis = MapCheck.Analyze(seed, map, config, neow);
        Console.WriteLine($"Forced elite by floor {config.latestEliteRow + 1}: {analysis.Problem ?? "yes"}");
        foreach (var replay in analysis.Replays)
            Console.WriteLine(ReplayText(replay));
    }

    static string ReplayText(MapCheck.Replay r) =>
        $"  Neow {r.Neow}{(r.Choices == "" ? "" : $" (choices: {r.Choices})")}, {r.Fights} fights: {string.Join(" > ", r.Rooms)}\n" +
        string.Concat(r.Rooms.Where(x => x.Detail != null).Select(x => $"      f{x.Floor} {x.What}: {x.Detail}\n")) +
        (r.NeowOffered.Count == 0 ? "" : $"      Neow pickup offers (not filtered, check by hand): {string.Join(", ", r.NeowOffered)}\n") +
        string.Join("\n", r.Rooms.Where(x => x.IsFight).Select(x => $"      f{x.Floor} {x.What}: {string.Join(", ", x.Cards!)}" + (x.Potion != null ? $"  + potion {x.Potion}" : "")));

    public static string Summary(string seed, Search.Verdict v)
    {
        var lines = new List<string>
        {
            $"MATCH {seed}  Neow: {string.Join(", ", v.Neow ?? new())}  {v.Map?.Routes} distinct routes, {v.Map?.MinFights}-{v.Map?.MaxFights} fights before the elite",
        };
        foreach (var replay in v.Map?.Replays ?? new())
            lines.Add(ReplayText(replay));
        var potions = (v.Map?.Replays ?? new()).SelectMany(r => r.Rooms).Where(r => r.Potion != null).Select(r => r.Potion!).Distinct().ToList();
        var relics = (v.Map?.Replays ?? new()).SelectMany(r => r.Rooms).Where(r => r.Relic != null).Select(r => r.Relic!).Distinct().ToList();
        lines.Add($"  potions seen: {(potions.Count == 0 ? "none" : string.Join(", ", potions))}   relics seen: {(relics.Count == 0 ? "none" : string.Join(", ", relics))}");
        return string.Join(Environment.NewLine, lines);
    }

    public static void ListCards()
    {
        var state = Sim.NewRun("0", allActsRooms: false);
        var player = state.Players[0];
        var options = CardCreationOptions.ForRoom(player, RoomType.Monster).WithFlags(CardCreationFlags.IsFromCombat);
        foreach (var group in options.GetPossibleCards(player).GroupBy(c => c.Rarity).OrderBy(g => g.Key))
        {
            Console.WriteLine($"{group.Key} ({group.Count()}):");
            foreach (var c in group.OrderBy(c => c.Id.Entry))
                Console.WriteLine($"  {c.Id.Entry,-28} {c.Type}  pool {c.Pool.Id.Entry}");
        }
    }

    static char Glyph(MapPointType t) => t switch
    {
        MapPointType.Monster => 'M',
        MapPointType.Elite => 'E',
        MapPointType.Unknown => '?',
        MapPointType.RestSite => 'R',
        MapPointType.Shop => '$',
        MapPointType.Treasure => 'T',
        MapPointType.Ancient => 'A',
        MapPointType.Boss => 'B',
        _ => 'x',
    };
}
