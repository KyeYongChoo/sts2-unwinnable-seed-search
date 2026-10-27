# Searching for an Unwinnable Seed (Slay the Spire 2)

A seed search tool for **Slay the Spire 2** that looks for a Necrobinder seed that can't be won: one where Act 1
forces every route into an early elite (the working hypothesis is Bygone Effigy) with nothing on the way strong
enough to beat it.

Rather than re-implementing the game's generation, the tool **loads the game's own `sts2.dll` and calls its real
generation code** headlessly: act selection, map generation, encounters, Neow, card/potion rewards, chests, shops
and events. Results have been checked against the live game.

> Not affiliated with Mega Crit. No game files are included; you need your own copy of the game.

## How it works

- `Headless.cs` boots the game's model database without the Godot engine. Anything that would call native engine
  code (logging, localization text, sound/visual effects, save files, reward screens) is stubbed out with
  [Harmony](https://github.com/pardeike/Harmony); the generation code itself runs unmodified.
- `Sim.cs` reproduces the start of a singleplayer run in the same order the game does
  (`NGame.StartNewSingleplayerRun` → `RunManager.InitializeNewRun` / `GenerateRooms` / `GenerateMap`).
- `MapCheck.cs` finds every route from Neow to its first elite and replays each one room by room with the game's own
  logic, once per Neow option. Wherever a choice changes later RNG (This or That, two-option events, buying a
  reward-changing relic in a shop) both outcomes are replayed. A seed only matches if **every** replay passes.
- `FastPruning.cs` replaces the game's slowest map-generation step (`MapPathPruning.FindMatchingSegments`, about
  200–350 ms per map) with an exact equivalent about 5× faster. `SeedSearch validatemap` checks it map-for-map
  against the original.

### Verified in game (v0.111.0)

On seed `AGRTQKUWEH84` (A0) and `V0ZRJNLYX2BN` (A1): Act 1 pick, map layout, encounter order, Neow options, card
rewards, potion drops and Kaleidoscope's off-class picks all matched the live game.

## Requirements

- Windows, [.NET 9 SDK](https://dotnet.microsoft.com/download)
- Slay the Spire 2 **v0.111.0** (other versions may generate differently)
- The tool assumes **everything is unlocked** (run `/unlock all` in game) and **no gameplay mods**; both change what
  the game generates for a seed.

## Build

```
cd SeedSearch
dotnet build -c Release
```

If the game isn't in the default Steam folder:

```
dotnet build -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64"
```

## Use

### Web UI (easiest)

```
cd SeedSearch
dotnet bin\Release\net9.0\SeedSearch.dll serve
```

This opens `http://localhost:5057/` with:

- **Start / Resume** and **Stop**: workers save their progress, and resume continues each seed sequence exactly where
  it stopped. Choose the number of processes and the priority (Below normal keeps the PC usable).
- **New search**: moves the current results to a dated `results_...` folder and starts fresh sequences.
- Live progress: seeds checked, speed, the filter funnel and each worker's status.
- **Matches** with their full route, card, potion and relic breakdown, and recent **near-misses**.
- **Inspect a seed**: map, Neow options, rewards, every route replay, and whether it passes the current config.
- **Filters editor**: drop-down lists for cards, relics, potions and events, saved to `config.json` and validated.

Workers are separate processes that write to `SeedSearch\results\`, so they keep running if you close the page or
the UI server, and the UI picks them up again when reopened.

### Command line

From the `SeedSearch` folder:

| Command | What it does |
|---|---|
| `dotnet bin\Release\net9.0\SeedSearch.dll describe <SEED>` | Map, encounters, Neow options, rewards per Neow pick, and every route replay |
| `dotnet bin\Release\net9.0\SeedSearch.dll check <SEED>` | Runs every filter in `config.json` against one seed |
| `dotnet bin\Release\net9.0\SeedSearch.dll search --count N --from N` | Searches random seeds; `--from` makes the sequence reproducible. Add `--state file.json` to save/resume progress and `--stop file` to stop when that file appears |
| `dotnet bin\Release\net9.0\SeedSearch.dll stats` | Pass rate of each filter on its own |
| `dotnet bin\Release\net9.0\SeedSearch.dll cards` | Necrobinder cards that can appear in combat rewards |
| `dotnet bin\Release\net9.0\SeedSearch.dll validatemap` | Checks the fast map pruning against the game's own |

Options: `--config path.json`, `--ascension N`, `--slowmap` (use the game's own map pruning).

To search on every core without the UI (uses the same `results\` files, so it resumes the UI's search and vice versa):

```
.\run_search.ps1                   # start, or resume where the workers stopped
.\run_search.ps1 -Stop             # stop; workers save progress and exit within a few seconds
.\run_search.ps1 -Processes 6      # leave two cores free
.\run_search.ps1 -Priority Normal  # full speed when idle (default BelowNormal)
```

To start a fresh search from the command line, stop it and rename or delete `results\`.

## Configuration (`SeedSearch/config.json`)

IDs are the game's internal names (`SEANCE`, `FROZEN_EGG`, ...). Typos are rejected at startup.

| Key | Meaning |
|---|---|
| `ascension` | Ascension level. A1+ adds elites to the map, which makes forced elites far more common. |
| `act1` | Required Act 1 (`OVERGROWTH` or `UNDERDOCKS`). |
| `firstElite` | Required first elite of the act (the one routes are forced into). Empty allows any. |
| `latestEliteRow` | Last map row (floor = row + 1) where the forced elite may be. Row 9 is the chest row, 15 the rest row before the boss. |
| `allowedEvents` | Events a "?" room may turn into on the way. |
| `extraRoomsBeforeElite` | Room types allowed on the way besides fights, "?" and the chest, e.g. `"Shop"`. |
| `allowedRelics` | Relics a chest, shop or This or That may offer. Empty allows any. |
| `allowedNeowRelics` / `forbiddenNeowRelics` | Neow restrictions. An empty allow-list allows any option (each is replayed). |
| `forbiddenPotions` | Potions that may not be obtainable on the way (fights, events, shops, Neow pickups). |
| `allowedCards` | Every card offered on the way (fights, events, shops) must be on this list. Empty disables the filter. |

## Findings so far

- **Elites can't spawn below map row 6** (floor 7; Neow is floor 1), so the earliest possible elite is floor 7.
- **Potion pity**: the drop chance starts at 40% and rises 10% per miss, so a potion is guaranteed by the 7th fight.
- **Forced elites are rare**: about 95% of Act 1 maps let you reach the boss without fighting any elite. Ascension 1+
  (more elites) and allowing shops make forced routes much more common.
- **Some choices shift every later reward**: the Rewards RNG is shared by fight rewards, chest gold, shop relic
  rarities and several events and Neow pickups (Arcane Scroll, Scroll Boxes, Kaleidoscope, This or That's relic,
  Wellspring's potion...), which is why those choices are replayed both ways.

## Assumptions and limits

- "?" rooms whose next event depends on gold or HP (Luminous Choir, Morphic Grove, Unrest Site, The Legends Were True)
  are treated as escapable. Whispering Hollow (needs 44 gold) is treated as forced unless a shop came first.
- Neow relics whose pickup opens a choice screen are simulated up to that screen; Leafy Poultice's pickup effect is
  skipped (it needs the engine). Kaleidoscope's off-class picks are listed for review rather than filtered.
- Combat itself isn't simulated. The search finds seeds whose routes are forced and whose rewards are all on your
  lists; whether the elite is actually unwinnable is judged separately.
