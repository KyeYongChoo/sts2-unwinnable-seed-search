using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

/// <summary>
/// Reproduces the start of a singleplayer Necrobinder run by calling the game's own generation code in the same
/// order the game does (NGame.StartNewSingleplayerRun -> RunManager.InitializeNewRun/GenerateRooms/GenerateMap).
/// Assumes every act, event and relic is unlocked (verified in-game after /unlock all).
/// </summary>
static class Sim
{
    static readonly Action<RunManager, RunState?> _setState =
        AccessTools.MethodDelegate<Action<RunManager, RunState?>>(AccessTools.PropertySetter(typeof(RunManager), "State"));
    static readonly Action<RunManager, AscensionManager> _setAscension =
        AccessTools.MethodDelegate<Action<RunManager, AscensionManager>>(AccessTools.PropertySetter(typeof(RunManager), nameof(RunManager.AscensionManager)));

    public static int Ascension { get; private set; }

    public static void SetAscension(int level)
    {
        Ascension = level;
        _setAscension(RunManager.Instance, new AscensionManager(level));
    }

    // Mirrors StartRunLobby.BeginRunLocally + ActModel.GetRandomList with every act unlocked and discovered.
    public static ActModel PickAct1(string seed)
    {
        var rng = new Rng(StringHelper.GetDeterministicHashCode(seed), "act_selection");
        return rng.NextItem(ModelDb.ActsByIndex[0].ToList())!;
    }

    public static List<ActModel> PickActs(string seed)
    {
        var rng = new Rng(StringHelper.GetDeterministicHashCode(seed), "act_selection");
        return ModelDb.ActsByIndex.Select(acts => rng.NextItem(acts.ToList())!).ToList();
    }

    // Mirrors StandardActMap.CreateFor for act 1 in singleplayer, without needing a RunState.
    public static ActMap Act1Map(string seed, ActModel act1)
    {
        EnsureRunInProgress();
        return new StandardActMap(new Rng(StringHelper.GetDeterministicHashCode(seed), "act_1_map"), act1, isMultiplayer: false,
            shouldReplaceTreasureWithElites: false, hasSecondBoss: false); // Double Boss only ever applies to the last act
    }

    /// <param name="createMap">The act 1 map has its own RNG stream and costs ~350 ms (MapPathPruning), so the search
    /// skips it here and checks it last via <see cref="Act1Map"/>.</param>
    public static RunState NewRun(string seed, bool allActsRooms = true, bool createMap = false)
    {
        var player = Player.CreateForNewRun(ModelDb.Character<Necrobinder>(), UnlockState.all, 1uL);
        var acts = PickActs(seed).Select(a => (ActModel)a.ToMutable()).ToList();
        var state = RunState.CreateForNewRun(new[] { player }, acts, Array.Empty<ModifierModel>(), GameMode.Standard, Ascension, seed);
        _setState(RunManager.Instance, state);
        SetAscension(Ascension);

        // RunManager.InitializeNewRun
        state.SharedRelicGrabBag.Populate(ModelDb.RelicPool<SharedRelicPool>().GetUnlockedRelics(state.UnlockState), state.Rng.UpFront);
        foreach (var p in state.Players)
            p.PopulateRelicGrabBagIfNecessary(state.Rng.UpFront);
        state.ExtraFields.StartedWithNeow = state.UnlockState.IsEpochRevealed<MegaCrit.Sts2.Core.Timeline.Epochs.NeowEpoch>();
        foreach (var p in state.Players)
            RunManager.Instance.AscensionManager.ApplyEffectsTo(p);

        // RunManager.GenerateRooms
        var ancients = state.UnlockState.SharedAncients.ToList().UnstableShuffle(state.Rng.UpFront);
        foreach (var act in state.Acts.Skip(1))
        {
            int count = state.Rng.UpFront.NextInt(ancients.Count + 1);
            var subset = ancients.Take(count).ToList();
            ancients = ancients.Except(subset).ToList();
            act.SetSharedAncientSubset(subset);
        }
        // Later acts draw from UpFront after act 1, so skipping them never changes act 1.
        foreach (var act in allActsRooms ? state.Acts : state.Acts.Take(1))
        {
            act.GenerateRooms(state.Rng.UpFront, state.UnlockState, false);
            act.ApplyDiscoveryOrderModifications(state.UnlockState);
        }

        if (createMap)
            state.Map = state.Act.CreateMap(state, replaceTreasureWithElites: false);
        return state;
    }

    static void EnsureRunInProgress()
    {
        // AscensionHelper.HasAscension only answers while a run is "in progress" (RunManager.State != null).
        if (!RunManager.Instance.IsInProgress)
            NewRun("0", allActsRooms: false);
    }

    // Mirrors EventModel.BeginEvent: owner + per-event RNG, then the event's own GenerateInitialOptions.
    public static List<EventOption> NeowOptions(RunState state)
    {
        var player = state.Players[0];
        var neow = (EventModel)ModelDb.AncientEvent<Neow>().ToMutable();
        AccessTools.PropertySetter(typeof(EventModel), nameof(EventModel.Owner)).Invoke(neow, new object[] { player });
        var rng = new Rng(state.Rng.Seed + (ulong)(neow.IsShared ? 0 : state.GetPlayerSlotIndex(player)) + StringHelper.GetDeterministicHashCode(neow.Id.Entry));
        AccessTools.PropertySetter(typeof(EventModel), nameof(EventModel.Rng)).Invoke(neow, new object[] { rng });
        return ((IEnumerable<EventOption>)AccessTools.Method(typeof(Neow), "GenerateInitialOptions").Invoke(neow, null)!).ToList();
    }

    // Found with `SeedSearch relictest <RELIC>` over every Neow relic.
    static readonly HashSet<string> _pickupCrashesHeadless = new() { "LEAFY_POULTICE", "MASSIVE_SCROLL" };

    public record CombatResult(string Encounter, RoomType RoomType, List<string> Cards, string? Potion, int Gold);

    /// <summary>
    /// Takes the given Neow relic, then fights the next <paramref name="hallwayFights"/> hallway encounters in order and
    /// generates each reward screen with the game's RewardsSet (so relic reward hooks apply). Card rewards do not depend
    /// on the deck, so the result is the same whichever cards are picked along the way.
    /// </summary>
    public static List<CombatResult> RewardsAfterNeow(string seed, string? neowRelic, int hallwayFights, bool includeFirstElite)
    {
        var state = NewRun(seed, allActsRooms: false);
        var player = state.Players[0];
        Obtain(player, neowRelic);
        var results = new List<CombatResult>();
        for (int i = 0; i < hallwayFights; i++)
            results.Add(Fight(state, player, RoomType.Monster));
        if (includeFirstElite)
            results.Add(Fight(state, player, RoomType.Elite));
        return results;
    }

    static readonly HashSet<string> _noted = new();

    /// <summary>
    /// RelicCmd.Obtain, as the game does for Neow, chests and events. Returns what the pickup offered on a custom
    /// reward screen (Kaleidoscope's cards, ...), which is generated with the game's own RNG draws but not shown.
    /// </summary>
    public static Headless.Offered Obtain(Player player, string? relicId)
    {
        if (relicId == null)
            return new(new(), new());
        Headless.TakeOffered();
        var relic = ModelDb.AllRelics.First(r => r.Id.Entry == relicId).ToMutable();
        if (_pickupCrashesHeadless.Contains(relicId))
        {
            // Their pickup effects hit native engine code, which kills the process. Own the relic without them.
            player.AddRelicInternal(relic, silent: true);
            Note($"{relicId}'s pickup effect was skipped (needs the engine)");
            return new(new(), new());
        }
        try
        {
            RelicCmd.Obtain(relic, player).GetAwaiter().GetResult();
        }
        catch (NullReferenceException)
        {
            // Pickup effects that open a choice screen (Scroll Boxes, ...) can't run headlessly. The relic is already
            // owned by then, so its reward hooks still apply; only the pickup choice itself is skipped.
            Note($"{relicId}'s pickup stopped at a UI call (choice screen, HP bar, ...); anything after it was skipped");
        }
        return Headless.TakeOffered();
    }

    static void Note(string text)
    {
        if (_noted.Add(text))
            Console.Error.WriteLine("  note: " + text);
    }

    /// <summary>Fights the next encounter of this type and generates its reward screen; marks the room visited.</summary>
    public static CombatResult Fight(RunState state, Player player, RoomType type)
    {
        var encounter = state.Act.PullNextEncounter(type).ToMutable();
        var room = new CombatRoom(encounter, state);
        var rewards = new RewardsSet(player).WithRewardsFromRoom(room);
        rewards.GenerateWithoutOffering().GetAwaiter().GetResult();
        state.Act.MarkRoomVisited(type);

        var cards = rewards.Rewards.OfType<CardReward>().SelectMany(r => r.Cards).Select(c => c.Id.Entry + (c.IsUpgraded ? "+" : "")).ToList();
        var potion = rewards.Rewards.OfType<PotionReward>().Select(p => p.Potion?.Id.Entry).FirstOrDefault();
        var gold = rewards.Rewards.OfType<GoldReward>().Sum(g => g.Amount);
        return new CombatResult(encounter.Id.Entry, type, cards, potion, gold);
    }
}
