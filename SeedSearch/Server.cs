using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models;

/// <summary>
/// `SeedSearch serve`: a local web UI that starts, stops and resumes search workers, shows their progress, matches and
/// near-misses, describes seeds and edits config.json. Workers are independent processes that save their state to
/// results/, so they keep running (and can be resumed) regardless of whether this server is open.
/// </summary>
static class Server
{
    static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
    static readonly JsonSerializerOptions _pretty = new() { WriteIndented = true };

    static string Dir => Directory.GetCurrentDirectory();
    static string Results => Path.Combine(Dir, "results");
    static string StopFile => Path.Combine(Results, "STOP");
    static string SearchFile => Path.Combine(Results, "search.json");
    static string ConfigFile => Path.Combine(Dir, "config.json");
    static ProcessPriorityClass _priority = ProcessPriorityClass.BelowNormal;

    class SearchInfo
    {
        public ulong FromBase { get; set; }
        public DateTime Created { get; set; }
    }

    public static void Run(Dictionary<string, string> opts)
    {
        int port = int.Parse(opts.GetValueOrDefault("port", "5057"));
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        string url = $"http://localhost:{port}/";
        Console.WriteLine($"SeedSearch UI on {url}  (Ctrl+C to close the UI; running searches keep going)");
        if (!opts.ContainsKey("no-browser"))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        while (true)
        {
            var ctx = listener.GetContext();
            try
            {
                Handle(ctx);
            }
            catch (Exception e)
            {
                Send(ctx, 500, "text/plain", e.Message);
            }
        }
    }

    static void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        string path = req.Url!.AbsolutePath;
        string body = req.HasEntityBody ? new StreamReader(req.InputStream).ReadToEnd() : "";
        switch (req.HttpMethod, path)
        {
            case ("GET", "/"):
                Send(ctx, 200, "text/html; charset=utf-8", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html")));
                break;
            case ("GET", "/api/status"):
                SendJson(ctx, Status());
                break;
            case ("POST", "/api/start"):
                SendJson(ctx, Start(JsonDocument.Parse(body).RootElement, fresh: false));
                break;
            case ("POST", "/api/new"):
                SendJson(ctx, Start(JsonDocument.Parse(body).RootElement, fresh: true));
                break;
            case ("POST", "/api/stop"):
                Directory.CreateDirectory(Results);
                File.WriteAllText(StopFile, "stop");
                SendJson(ctx, new { ok = true });
                break;
            case ("POST", "/api/kill"):
                foreach (var w in Workers().Where(w => w.Process != null))
                    w.Process!.Kill();
                SendJson(ctx, new { ok = true });
                break;
            case ("GET", "/api/config"):
                Send(ctx, 200, "application/json", File.ReadAllText(ConfigFile));
                break;
            case ("POST", "/api/config"):
                SendJson(ctx, SaveConfig(body));
                break;
            case ("GET", "/api/catalog"):
                SendJson(ctx, Catalog());
                break;
            case ("GET", "/api/describe"):
                Send(ctx, 200, "text/plain; charset=utf-8", Describe(req.QueryString["seed"] ?? ""));
                break;
            default:
                Send(ctx, 404, "text/plain", "not found");
                break;
        }
    }

    record Worker(int Index, WorkerState State, Process? Process);

    // A worker is running if the PID in its state file is a live dotnet process.
    static List<Worker> Workers()
    {
        var list = new List<Worker>();
        if (!Directory.Exists(Results))
            return list;
        foreach (var file in Directory.GetFiles(Results, "state_*.json"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file)["state_".Length..], out int index))
                continue;
            WorkerState? state;
            try { state = JsonSerializer.Deserialize<WorkerState>(File.ReadAllText(file)); }
            catch { continue; }
            if (state == null) continue;
            Process? process = null;
            try
            {
                var p = Process.GetProcessById(state.Pid);
                if (!p.HasExited && p.ProcessName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    process = p;
            }
            catch (ArgumentException) { }
            list.Add(new Worker(index, state, process));
        }
        return list.OrderBy(w => w.Index).ToList();
    }

    static object Status()
    {
        var workers = Workers();
        foreach (var w in workers.Where(w => w.Process != null))
            try { if (w.Process!.PriorityClass != _priority) w.Process.PriorityClass = _priority; } catch { }
        string configHash = Config.Load(ConfigFile).Hash();
        var funnel = new SortedDictionary<string, long>();
        foreach (var w in workers)
            foreach (var (k, v) in w.State.Funnel)
                funnel[k] = funnel.GetValueOrDefault(k) + v;
        var search = File.Exists(SearchFile) ? JsonSerializer.Deserialize<SearchInfo>(File.ReadAllText(SearchFile)) : null;
        return new
        {
            running = workers.Count(w => w.Process != null),
            stopRequested = File.Exists(StopFile),
            cores = Environment.ProcessorCount,
            priority = _priority.ToString(),
            search = search == null ? null : new { search.FromBase, created = search.Created },
            configHash,
            configChanged = workers.Any(w => w.State.ConfigHash != "" && w.State.ConfigHash != configHash),
            totals = new
            {
                @checked = workers.Sum(w => w.State.Checked),
                rate = workers.Where(w => w.Process != null).Sum(w => w.State.Rate),
                funnel,
            },
            workers = workers.Select(w => new
            {
                index = w.Index,
                running = w.Process != null,
                @checked = w.State.Checked,
                rate = w.Process != null ? w.State.Rate : 0,
                updated = w.State.Updated,
                finished = w.State.Finished,
                from = w.State.From,
            }),
            matches = Matches(),
            nearMisses = NearMisses(),
        };
    }

    static List<object> Matches()
    {
        var matches = new List<object>();
        if (!Directory.Exists(Results)) return matches;
        foreach (var file in Directory.GetFiles(Results, "matches_*.txt"))
        {
            var block = new StringBuilder();
            string? seed = null;
            foreach (var line in File.ReadLines(file).Append("MATCH"))
            {
                if (line.StartsWith("MATCH"))
                {
                    if (seed != null) matches.Add(new { seed, text = block.ToString() });
                    block.Clear();
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    seed = parts.Length > 1 ? parts[1] : null;
                }
                block.AppendLine(line);
            }
        }
        return matches;
    }

    static List<object> NearMisses()
    {
        var near = new List<object>();
        if (!Directory.Exists(Results)) return near;
        foreach (var file in Directory.GetFiles(Results, "near_*.txt"))
            foreach (var line in File.ReadLines(file).TakeLast(40))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2) near.Add(new { seed = parts[0], reason = parts[1] });
            }
        return near.TakeLast(60).Reverse().ToList();
    }

    static object Start(JsonElement body, bool fresh)
    {
        int processes = body.TryGetProperty("processes", out var p) ? p.GetInt32() : Environment.ProcessorCount;
        if (body.TryGetProperty("priority", out var pr) && Enum.TryParse<ProcessPriorityClass>(pr.GetString(), out var priority))
            _priority = priority;
        var workers = Workers();
        if (fresh)
        {
            if (workers.Any(w => w.Process != null))
                return new { ok = false, error = "Stop the running search before starting a new one." };
            // Keep earlier matches: move the whole results folder aside.
            if (Directory.Exists(Results))
                Directory.Move(Results, Path.Combine(Dir, $"results_{DateTime.Now:yyyyMMdd_HHmmss}"));
            workers = new();
        }
        Directory.CreateDirectory(Results);
        if (File.Exists(StopFile))
            File.Delete(StopFile);
        if (!File.Exists(SearchFile))
            File.WriteAllText(SearchFile, JsonSerializer.Serialize(new SearchInfo { FromBase = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Created = DateTime.UtcNow }));
        var search = JsonSerializer.Deserialize<SearchInfo>(File.ReadAllText(SearchFile))!;

        int started = 0;
        for (int i = 0; i < processes; i++)
        {
            if (workers.Any(w => w.Index == i && (w.Process != null || w.State.Finished)))
                continue;
            StartWorker(i, search.FromBase + (ulong)i);
            started++;
        }
        return new { ok = true, started };
    }

    // Runs through cmd so the worker's output goes straight to its log file and it doesn't depend on this server.
    static void StartWorker(int index, ulong from)
    {
        string dll = typeof(Server).Assembly.Location;
        string r(string name) => Path.Combine(Results, name);
        string args = $"search --count 1000000000000 --from {from} --state \"{r($"state_{index}.json")}\" --stop \"{StopFile}\" " +
                      $"--out \"{r($"matches_{index}.txt")}\" --near \"{r($"near_{index}.txt")}\"";
        var psi = new ProcessStartInfo("cmd.exe", $"/c \"dotnet \"{dll}\" {args} >> \"{r($"log_{index}.txt")}\" 2>&1\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Dir,
        };
        Process.Start(psi);
    }

    static object SaveConfig(string body)
    {
        Config config;
        try
        {
            config = JsonSerializer.Deserialize<Config>(body)!;
            config.Validate();
        }
        catch (Exception e)
        {
            return new { ok = false, error = e.Message };
        }
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, _pretty));
        return new { ok = true, hash = config.Hash() };
    }

    static object Catalog()
    {
        static string Name(string id) => string.Join(" ", id.Split('_').Select(w => w.Length == 0 ? w : w[0] + w[1..].ToLowerInvariant()));
        var player = Sim.NewRun("0", allActsRooms: false).Players[0];
        var cardPools = new[] { player.Character.CardPool, ModelDb.CardPool<MegaCrit.Sts2.Core.Models.CardPools.ColorlessCardPool>() };
        var acts1 = ModelDb.ActsByIndex[0];
        return new
        {
            cards = cardPools.SelectMany(pool => pool.AllCards.Where(c => c.Rarity.ToString() is not ("Basic" or "Ancient"))
                .Select(c => new { id = c.Id.Entry, name = Name(c.Id.Entry), group = $"{(pool == player.Character.CardPool ? "Necrobinder" : "Colorless")} {c.Rarity}" }))
                .OrderBy(c => c.group).ThenBy(c => c.name),
            relics = ModelDb.AllRelics.Select(r => new { id = r.Id.Entry, name = Name(r.Id.Entry), group = r.Rarity.ToString() })
                .OrderBy(r => r.group).ThenBy(r => r.name),
            potions = ModelDb.AllPotions.Where(p => p.Id.Entry != "DEPRECATED_POTION")
                .Select(p => new { id = p.Id.Entry, name = Name(p.Id.Entry), group = p.Rarity.ToString() }).OrderBy(p => p.group).ThenBy(p => p.name),
            events = acts1.SelectMany(a => a.AllEvents).Select(e => (e, group: "Act 1"))
                .Concat(ModelDb.AllSharedEvents.Select(e => (e, group: "Shared")))
                .Select(x => new { id = x.e.Id.Entry, name = Name(x.e.Id.Entry), x.group }).DistinctBy(e => e.id).OrderBy(e => e.group).ThenBy(e => e.name),
            acts = acts1.Select(a => new { id = a.Id.Entry, name = Name(a.Id.Entry) }),
            elites = acts1.SelectMany(a => a.AllEliteEncounters.Select(e => new { id = e.Id.Entry, name = Name(e.Id.Entry), group = Name(a.Id.Entry) })),
            rooms = new[] { "Shop", "RestSite" },
        };
    }

    static string Describe(string seed)
    {
        seed = MegaCrit.Sts2.Core.Helpers.SeedHelper.CanonicalizeSeed(seed);
        if (seed.Length == 0) return "Enter a seed.";
        var config = Config.Load(ConfigFile);
        Sim.SetAscension(config.ascension);
        var output = new StringWriter();
        var old = Console.Out;
        Console.SetOut(output);
        try
        {
            Report.Describe(seed, config);
            var verdict = Search.Check(seed, config);
            Console.WriteLine();
            Console.WriteLine(verdict.Passed ? "RESULT: MATCH with the current config" : $"RESULT: fails the current config at: {verdict.FailedAt}");
        }
        finally
        {
            Console.SetOut(old);
        }
        return output.ToString();
    }

    static void SendJson(HttpListenerContext ctx, object value) => Send(ctx, 200, "application/json", JsonSerializer.Serialize(value, _json));

    static void Send(HttpListenerContext ctx, int status, string type, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }
}
