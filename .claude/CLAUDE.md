# Project notes for Claude

Goal: find a Slay the Spire 2 (v0.111.0) Necrobinder seed where Act 1 forces every route into an early elite with
only weak cards, relics and potions on the way, as evidence that an unwinnable seed exists. See README.md for usage.

## Ground rules

- **Never re-implement game logic.** The whole point is exact fidelity: call the game's own methods from `sts2.dll`
  (referenced via `GameDir` in `SeedSearch.csproj`). When the game's code touches the engine, stub that call in
  `Headless.cs` with Harmony instead of copying the logic.
- **Don't commit game files** (DLLs, decompiled source). To read game code, decompile locally into a scratch folder:
  `ilspycmd -p -o <scratch>/sts2src "<GameDir>/sts2.dll"` (namespaces like `MegaCrit.Sts2.Core.Map`).
- **Verify against the live game** after any change to generation, and after any game update. Use `describe <SEED>`
  and ask the user to compare in game (map, Neow options, first fight's rewards). Verified so far: `AGRTQKUWEH84`
  (A0) and `V0ZRJNLYX2BN` (A1, including Kaleidoscope picks). A10 has not been checked in game.
- Only fights draw from the per-player Rewards RNG *unless* noted below; anything that draws from it shifts every
  later reward, so it must be replayed in order and, if it's a player choice, both ways.

## Architecture (SeedSearch/)

- `Headless.cs`: boots ModelDb with vanilla models only, stubs Godot-native calls (logging, LocString text, hover
  tips, SaveManager, SfxCmd, relic flash VFX, card-reward alternative buttons) and replaces `RewardsCmd.OfferCustom`
  with "generate exactly as Offer would, record, skip the screen".
- `Sim.cs`: run setup in game order; `PickAct1`, `NewRun`, `Act1Map`, `NeowOptions`, `Fight`, `Obtain`.
- `MapCheck.cs`: route enumeration to the first elite, then a replay per (Neow option × route × choice branch).
  Routes with the same room-type signature replay identically and are deduplicated.
- `FastPruning.cs`: exact DFS replacement for `MapPathPruning.FindMatchingSegments`. Any change must pass
  `SeedSearch validatemap --count 300` with 0 mismatches.
- `Program.cs`: CLI, config (validated against ModelDb IDs), funnel reporting.
- Filter order is cheapest first: act → first elite → Neow → first fight → map (expensive) → full replay.

## Game facts learned from the code

- Seed string → `XxHash64` → per-stream `Rng` (Xoshiro256**). Act pick uses `Rng(hash, "act_selection")`, the act 1
  map `Rng(hash, "act_1_map")`, events `seed + slot + hash(eventId)`.
- Act 1 encounters/events are pre-rolled from the `UpFront` RNG at run start, after the relic grab bags, so unlock
  state changes everything (assume `UnlockState.all`; the user ran `/unlock all`).
- Elites can't be on map rows < 6 (`StandardActMap.IsValidForLower`); floor = row + 1, Neow is row 0.
- Potion drop chance 40%, +10% per miss / −10% per drop (`PotionRewardOdds`).
- Draws from the Rewards RNG outside fights: chest gold, shop relic rarities (`RelicFactory.RollRarity(player)`),
  This or That's Ornate relic, Brain Leech, Room Full of Cheese (Gorge), Wellspring (Bottle), Whispering Hollow
  (Gold), and Neow pickups such as Arcane Scroll, Scroll Boxes, Kaleidoscope. Whetstone/Wing Charm use `Niche`.
- Chest relic: rarity from `TreasureRoomRelics`, relic from the shared bag. Shops always stock one Shop-rarity relic.
- Pickups that crash natively and are skipped: `LEAFY_POULTICE` (MASSIVE_SCROLL is multiplayer-only).
- Winged Boots allows free travel (any node in the next row) 3 times, which breaks forced routes; it's excluded via
  `forbiddenNeowRelics`.

## Running

- Build: `cd SeedSearch && dotnet build -c Release`. Background searches lock the DLL; build to another folder
  (`-o bin/dev`) while they run.
- `run_search.ps1` starts one BelowNormal-priority search per core; results go to `SeedSearch/results/`.
