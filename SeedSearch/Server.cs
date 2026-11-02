using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models;

/// <summary>
/// `SeedSearch serve`: a local web UI that starts, stops and resumes search workers, shows their progress, matches and
/// near-misses, describes seeds and edits the config. Workers are independent processes that save their state to a
/// results folder, so they keep running (and can be resumed) regardless of whether this server is open.
///
/// Profiles let the UI switch between searches (e.g. one per character): `config.json` pairs with `results/`, and
/// each `config_NAME.json` with `results_NAME/`. API calls pick one with `?profile=NAME` (empty for config.json).
/// </summary>
static class Server
{
    static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
    static readonly JsonSerializerOptions _pretty = new() { WriteIndented = true };

    static string Dir => Directory.GetCurrentDirectory();
    static ProcessPriorityClass _priority = ProcessPriorityClass.BelowNormal;

    record Profile(string Id, string ConfigFile, string Results)
    {
        public string StopFile => Path.Combine(Results, "STOP");
        public string SearchFile => Path.Combine(Results, "search.json");
        /// <summary>Your tags and notes per matched seed. Only the UI writes it; workers never touch it.</summary>
        public string NotesFile => Path.Combine(Results, "notes.json");
    }

    class MatchNote
    {
        /// <summary>"", "winnable" or "unwinnable": what playing the seed showed.</summary>
        public string Verdict { get; set; } = "";
        public string Note { get; set; } = "";
    }

    static readonly string[] _verdicts = { "", "winnable", "unwinnable" };

    static Dictionary<string, MatchNote> LoadNotes(Profile profile) =>
        File.Exists(profile.NotesFile) ? JsonSerializer.Deserialize<Dictionary<string, MatchNote>>(File.ReadAllText(profile.NotesFile)) ?? new() : new();

    static object SaveNote(Profile profile, JsonElement body)
    {
        string seed = body.GetProperty("seed").GetString() ?? "";
        string verdict = body.TryGetProperty("verdict", out var v) ? v.GetString() ?? "" : "";
        string note = body.TryGetProperty("note", out var n) ? (n.GetString() ?? "").Trim() : "";
        if (seed == "" || !_verdicts.Contains(verdict))
            return new { ok = false, error = $"Bad seed or verdict '{verdict}'." };
        var notes = LoadNotes(profile);
        if (verdict == "" && note == "")
            notes.Remove(seed);
        else
            notes[seed] = new MatchNote { Verdict = verdict, Note = note };
        Directory.CreateDirectory(profile.Results);
        File.WriteAllText(profile.NotesFile + ".tmp", JsonSerializer.Serialize(new SortedDictionary<string, MatchNote>(notes), _pretty));
        File.Move(profile.NotesFile + ".tmp", profile.NotesFile, overwrite: true);
        return new { ok = true };
    }

    static List<Profile> Profiles()
    {
        var list = new List<Profile>();
        if (File.Exists(Path.Combine(Dir, "config.json")))
            list.Add(new("", Path.Combine(Dir, "config.json"), Path.Combine(Dir, "results")));
        foreach (var file in Directory.GetFiles(Dir, "config_*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            string id = Path.GetFileNameWithoutExtension(file)["config_".Length..];
            if (id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                list.Add(new(id, file, Path.Combine(Dir, "results_" + id)));
        }
        return list;
    }

    static Profile GetProfile(HttpListenerRequest req)
    {
        string id = req.QueryString["profile"] ?? "";
        return Profiles().FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException($"No profile '{id}' (expected config_{id}.json).");
    }

    static string Name(string id) => string.Join(" ", id.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

    static List<object> ProfileList() => Profiles().Select(p =>
    {
        Config? config = null;
        try { config = Config.Load(p.ConfigFile); } catch { }
        var workers = Workers(p);
        return (object)new
        {
            id = p.Id,
            label = p.Id == "" ? (config != null ? Name(config.character) : "config.json") : Name(p.Id),
            configFile = Path.GetFileName(p.ConfigFile),
            resultsDir = Path.GetFileName(p.Results),
            character = config?.character,
            ascension = config?.ascension,
            running = workers.Count(w => w.Process != null),
            @checked = workers.Sum(w => w.State.Checked),
        };
    }).ToList();

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
            case ("GET", "/api/profiles"):
                SendJson(ctx, ProfileList());
                break;
            case ("GET", "/api/status"):
                SendJson(ctx, Status(GetProfile(req)));
                break;
            case ("POST", "/api/start"):
                SendJson(ctx, Start(GetProfile(req), JsonDocument.Parse(body).RootElement, fresh: false));
                break;
            case ("POST", "/api/new"):
                SendJson(ctx, Start(GetProfile(req), JsonDocument.Parse(body).RootElement, fresh: true));
                break;
            case ("POST", "/api/stop"):
            {
                var profile = GetProfile(req);
                Directory.CreateDirectory(profile.Results);
                File.WriteAllText(profile.StopFile, "stop");
                SendJson(ctx, new { ok = true });
                break;
            }
            case ("POST", "/api/kill"):
                foreach (var w in Workers(GetProfile(req)).Where(w => w.Process != null))
                    w.Process!.Kill();
                SendJson(ctx, new { ok = true });
                break;
            case ("GET", "/api/config"):
                Send(ctx, 200, "application/json", File.ReadAllText(GetProfile(req).ConfigFile));
                break;
            case ("POST", "/api/config"):
                SendJson(ctx, SaveConfig(GetProfile(req), body));
                break;
            case ("GET", "/api/catalog"):
                SendJson(ctx, Catalog());
                break;
            case ("POST", "/api/note"):
                SendJson(ctx, SaveNote(GetProfile(req), JsonDocument.Parse(body).RootElement));
                break;
            case ("GET", "/api/describe"):
                Send(ctx, 200, "text/plain; charset=utf-8", Describe(GetProfile(req), req.QueryString["seed"] ?? ""));
                break;
            default:
                Send(ctx, 404, "text/plain", "not found");
                break;
        }
    }

    // Workers append to and replace these files while the UI polls them. Windows blocks a replace or append while a
    // reader holds the file without matching share flags (File.ReadAllText only allows other readers).
    static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    static List<string> ReadSharedLines(string path)
    {
        var reader = new StringReader(ReadShared(path));
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    record Worker(int Index, WorkerState State, Process? Process);

    // A worker is running if the PID in its state file is a live dotnet process.
    static List<Worker> Workers(Profile profile)
    {
        var list = new List<Worker>();
        if (!Directory.Exists(profile.Results))
            return list;
        foreach (var file in Directory.GetFiles(profile.Results, "state_*.json"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file)["state_".Length..], out int index))
                continue;
            WorkerState? state;
            try { state = JsonSerializer.Deserialize<WorkerState>(ReadShared(file)); }
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

    static object Status(Profile profile)
    {
        var workers = Workers(profile);
        foreach (var w in workers.Where(w => w.Process != null))
            try { if (w.Process!.PriorityClass != _priority) w.Process.PriorityClass = _priority; } catch { }
        string configHash = Config.Load(profile.ConfigFile).Hash();
        var funnel = new SortedDictionary<string, long>();
        foreach (var w in workers)
            foreach (var (k, v) in w.State.Funnel)
                funnel[k] = funnel.GetValueOrDefault(k) + v;
        var search = File.Exists(profile.SearchFile) ? JsonSerializer.Deserialize<SearchInfo>(File.ReadAllText(profile.SearchFile)) : null;
        return new
        {
            profiles = ProfileList(),
            running = workers.Count(w => w.Process != null),
            stopRequested = File.Exists(profile.StopFile),
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
            matches = Matches(profile),
            nearMisses = NearMisses(profile),
        };
    }

    static List<object> Matches(Profile profile)
    {
        var matches = new List<object>();
        if (!Directory.Exists(profile.Results)) return matches;
        var notes = LoadNotes(profile);
        foreach (var file in Directory.GetFiles(profile.Results, "matches_*.txt"))
        {
            var block = new StringBuilder();
            string? seed = null;
            foreach (var line in ReadSharedLines(file).Append("MATCH"))
            {
                if (line.StartsWith("MATCH"))
                {
                    if (seed != null)
                    {
                        var note = notes.GetValueOrDefault(seed);
                        matches.Add(new { seed, text = block.ToString(), verdict = note?.Verdict ?? "", note = note?.Note ?? "" });
                    }
                    block.Clear();
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    seed = parts.Length > 1 ? parts[1] : null;
                }
                block.AppendLine(line);
            }
        }
        return matches;
    }

    static List<object> NearMisses(Profile profile)
    {
        var near = new List<object>();
        if (!Directory.Exists(profile.Results)) return near;
        foreach (var file in Directory.GetFiles(profile.Results, "near_*.txt"))
            foreach (var line in ReadSharedLines(file).TakeLast(40))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2) near.Add(new { seed = parts[0], reason = parts[1] });
            }
        return near.TakeLast(60).Reverse().ToList();
    }

    static object Start(Profile profile, JsonElement body, bool fresh)
    {
        string results = profile.Results;
        int processes = body.TryGetProperty("processes", out var p) ? p.GetInt32() : Environment.ProcessorCount;
        if (body.TryGetProperty("priority", out var pr) && Enum.TryParse<ProcessPriorityClass>(pr.GetString(), out var priority))
            _priority = priority;
        var workers = Workers(profile);
        if (fresh)
        {
            if (workers.Any(w => w.Process != null))
                return new { ok = false, error = "Stop the running search before starting a new one." };
            // Keep earlier matches: move the whole results folder aside.
            if (Directory.Exists(results))
                Directory.Move(results, $"{results}_{DateTime.Now:yyyyMMdd_HHmmss}");
            workers = new();
        }
        Directory.CreateDirectory(results);
        if (File.Exists(profile.StopFile))
            File.Delete(profile.StopFile);
        if (!File.Exists(profile.SearchFile))
            File.WriteAllText(profile.SearchFile, JsonSerializer.Serialize(new SearchInfo { FromBase = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Created = DateTime.UtcNow }));
        var search = JsonSerializer.Deserialize<SearchInfo>(File.ReadAllText(profile.SearchFile))!;

        int started = 0;
        for (int i = 0; i < processes; i++)
        {
            if (workers.Any(w => w.Index == i && (w.Process != null || w.State.Finished)))
                continue;
            StartWorker(profile, i, search.FromBase + (ulong)i);
            started++;
        }
        return new { ok = true, started };
    }

    // Runs through cmd so the worker's output goes straight to its log file and it doesn't depend on this server.
    static void StartWorker(Profile profile, int index, ulong from)
    {
        string dll = typeof(Server).Assembly.Location;
        string r(string name) => Path.Combine(profile.Results, name);
        string args = $"search --config \"{profile.ConfigFile}\" --count 1000000000000 --from {from} " +
                      $"--state \"{r($"state_{index}.json")}\" --stop \"{profile.StopFile}\" " +
                      $"--out \"{r($"matches_{index}.txt")}\" --near \"{r($"near_{index}.txt")}\"";
        var psi = new ProcessStartInfo("cmd.exe", $"/c \"dotnet \"{dll}\" {args} >> \"{r($"log_{index}.txt")}\" 2>&1\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Dir,
        };
        Process.Start(psi);
    }

    static object SaveConfig(Profile profile, string body)
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
        File.WriteAllText(profile.ConfigFile, JsonSerializer.Serialize(config, _pretty));
        return new { ok = true, hash = config.Hash() };
    }

    static object Catalog()
    {
        var cardPools = ModelDb.AllCharacters.Select(ch => (pool: ch.CardPool, group: Name(ch.Id.Entry)))
            .Append((ModelDb.CardPool<MegaCrit.Sts2.Core.Models.CardPools.ColorlessCardPool>(), "Colorless"));
        var acts1 = ModelDb.ActsByIndex[0];
        return new
        {
            characters = ModelDb.AllCharacters.Select(ch => new { id = ch.Id.Entry, name = Name(ch.Id.Entry) }),
            cards = cardPools.SelectMany(p => p.pool.AllCards.Where(c => c.Rarity.ToString() is not ("Basic" or "Ancient"))
                .Select(c => new { id = c.Id.Entry, name = Name(c.Id.Entry), group = $"{p.group} {c.Rarity}" }))
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

    static string Describe(Profile profile, string seed)
    {
        seed = MegaCrit.Sts2.Core.Helpers.SeedHelper.CanonicalizeSeed(seed);
        if (seed.Length == 0) return "Enter a seed.";
        var config = Config.Load(profile.ConfigFile);
        Sim.SetAscension(config.ascension);
        Sim.SetCharacter(config.character);
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
