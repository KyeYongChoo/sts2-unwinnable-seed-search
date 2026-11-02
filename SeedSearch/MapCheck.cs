using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

/// <summary>
/// Decides whether the act 1 map forces every route into an elite, and replays every route room by room with the
/// game's own logic: "?" rolls in that route's visit order, fight rewards, chests, shops and events. Wherever a choice
/// changes later RNG (This or That, two-option events, buying a reward-changing relic) both outcomes are replayed.
/// Every replay, for every Neow option, must pass the card, relic and potion filters.
/// </summary>
static class MapCheck
{
    /// <summary>Relic hooks that can change later rewards or "?" rolls. Buying such a relic in a shop is replayed both ways.</summary>
    static readonly HashSet<string> _rewardHooks = new()
    {
        "AfterObtained", "TryModifyRewards", "ModifyCardRewardCreationOptions", "TryModifyCardRewardOptions",
        "TryModifyCardRewardOptionsLate", "ShouldForcePotionReward", "ModifyUnknownMapPointRoomTypes",
    };

    static readonly AccessTools.FieldRef<ActModel, RoomSet> _rooms = AccessTools.FieldRefAccess<ActModel, RoomSet>("_rooms");
    static readonly Func<RelicModel[]> _trashHeapRelics =
        AccessTools.MethodDelegate<Func<RelicModel[]>>(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Models.Events.TrashHeap), "Relics"));
    static readonly Func<CardModel[]> _trashHeapCards =
        AccessTools.MethodDelegate<Func<CardModel[]>>(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Models.Events.TrashHeap), "Cards"));

    public record Room(int Floor, string What, List<string>? Cards = null, string? Potion = null, string? Relic = null, string? Detail = null)
    {
        public bool IsFight => Cards != null;

        public override string ToString() =>
            $"f{Floor}:{What}" + (Relic != null ? $"[{Relic}]" : "") + (Potion != null ? $"{{{Potion}}}" : "");
    }

    /// <summary>One replay: a Neow pick, a route, and the choices made on it.</summary>
    public record Replay(string Neow, List<Room> Rooms, string Choices, List<string> NeowOffered)
    {
        public int Fights => Rooms.Count(r => r.IsFight);
    }

    public record Result(string? Problem, List<Replay> Replays, int Routes)
    {
        public int MaxFights => Replays.Count == 0 ? 0 : Replays.Max(r => r.Fights);
        public int MinFights => Replays.Count == 0 ? 0 : Replays.Min(r => r.Fights);
    }

    public static Result Analyze(string seed, ActMap map, Config config, IReadOnlyList<string> neowOptions)
    {
        var passable = new HashSet<MapPointType> { MapPointType.Monster, MapPointType.Unknown, MapPointType.Treasure };
        passable.UnionWith(config.extraRoomsBeforeElite.Select(Enum.Parse<MapPointType>));

        var routes = new List<List<MapPoint>>();
        var problem = Walk(map.StartingMapPoint, new List<MapPoint>(), routes, config.latestEliteRow, passable);
        if (problem != null)
            return new(problem, new(), 0);

        // What a route produces depends only on its sequence of room types (plus whether a "?" leads only to shops,
        // which blacklists a shop roll), so routes with the same signature replay identically. Keep one of each.
        var distinct = routes.GroupBy(Signature).Select(g => g.First()).ToList();

        var replays = new List<Replay>();
        foreach (var neow in neowOptions)
            foreach (var route in distinct)
            {
                var routeProblem = Explore(seed, map, neow, route, config, new List<bool>(), replays);
                if (routeProblem != null)
                    return new(routeProblem + (neow == "" ? "" : $" (Neow: {neow})"), replays, distinct.Count);
            }
        return new(null, replays, distinct.Count);
    }

    static string Signature(List<MapPoint> route) =>
        string.Join(",", route.Select(p => $"{(int)p.PointType}{(p.Children.Count > 0 && p.Children.All(c => c.PointType == MapPointType.Shop) ? "s" : "")}"));

    // Collects every route from Neow up to its first elite. Fails on the first route that avoids an elite through
    // latestEliteRow or passes a room that isn't allowed before the elite.
    static string? Walk(MapPoint point, List<MapPoint> prefix, List<List<MapPoint>> routes, int latestEliteRow, HashSet<MapPointType> passable)
    {
        foreach (var child in point.Children.OrderBy(c => c.coord.col))
        {
            var path = new List<MapPoint>(prefix) { child };
            if (child.PointType == MapPointType.Elite)
            {
                routes.Add(path);
                continue;
            }
            if (!passable.Contains(child.PointType))
                return $"a route passes a {child.PointType} on floor {child.coord.row + 1}";
            if (child.coord.row >= latestEliteRow)
                return $"a route reaches floor {child.coord.row + 1} without an elite";
            var problem = Walk(child, path, routes, latestEliteRow, passable);
            if (problem != null) return problem;
        }
        return null;
    }

    // Replays the route with the given choices, then branches on every later choice it met (taking the other option).
    static string? Explore(string seed, ActMap map, string neow, List<MapPoint> route, Config config, List<bool> plan, List<Replay> replays)
    {
        var (replay, decisions, problem) = Play(seed, map, neow, route, plan, config);
        if (problem != null)
            return problem;
        replays.Add(replay!);
        for (int k = plan.Count; k < decisions.Count; k++)
        {
            var branch = decisions.Take(k).Append(true).ToList();
            var branchProblem = Explore(seed, map, neow, route, config, branch, replays);
            if (branchProblem != null)
                return branchProblem;
        }
        return null;
    }

    /// <summary>State of one replay. Choices default to false; plan[k] overrides the k-th choice met.</summary>
    sealed class Ctx(RunState state, Config config, List<bool> plan)
    {
        public RunState State { get; } = state;
        public Player Player { get; } = state.Players[0];
        public Config Config { get; } = config;
        public List<bool> Decisions { get; } = new();
        public List<string> Labels { get; } = new();
        public List<string> SeenCards { get; } = new();
        public bool VisitedShop { get; set; }
        /// <summary>Lowest and highest gold the player could have here: fight and event gold can be skipped, chest gold
        /// can't, and a shop or a paid event option can spend it all.</summary>
        public int MinGold { get; set; }
        public int MaxGold { get; set; }

        public bool Decide(string label)
        {
            bool choice = Decisions.Count < plan.Count && plan[Decisions.Count];
            Decisions.Add(choice);
            if (choice) Labels.Add(label);
            return choice;
        }

        public string? BadCards(IEnumerable<string> cards, string where)
        {
            var list = cards.ToList();
            SeenCards.AddRange(list);
            var bad = list.Where(c => !Config.CardOk(c)).Distinct().ToList();
            return bad.Count == 0 ? null : $"{where} offers {string.Join(", ", bad)}";
        }

        public string? BadPotions(IEnumerable<string> potions, string where)
        {
            var bad = potions.Where(Config.forbiddenPotions.Contains).Distinct().ToList();
            return bad.Count == 0 ? null : $"{where} offers potion {string.Join(", ", bad)}";
        }

        public string? BadRelic(string relic, string where) =>
            Config.allowedRelics.Count == 0 || Config.allowedRelics.Contains(relic) ? null : $"{where} offers {relic}";
    }

    // Mirrors RunManager.EnterMapPointInternal for each room: blacklist, unknown roll, room creation, rewards, history.
    static (Replay?, List<bool>, string?) Play(string seed, ActMap map, string neow, List<MapPoint> route, List<bool> plan, Config config)
    {
        var ctx = new Ctx(Sim.NewRun(seed, allActsRooms: false), config, plan);
        var state = ctx.State;
        var player = ctx.Player;
        var neowOffered = Sim.Obtain(player, neow == "" ? null : neow);
        var neowProblem = ctx.BadPotions(neowOffered.Potions, "Neow pickup");
        if (neowProblem != null)
            return (null, ctx.Decisions, neowProblem);
        ctx.MinGold = ctx.MaxGold = player.Gold;
        state.Map = map;
        state.AppendToMapPointHistory(MapPointType.Ancient, RoomType.Event, ModelDb.AncientEvent<MegaCrit.Sts2.Core.Models.Events.Neow>().Id);
        var act = state.Act;
        var rooms = new List<Room>();

        foreach (var point in route.Take(route.Count - 1))
        {
            int floor = point.coord.row + 1;
            state.AddVisitedMapCoord(point.coord);
            state.ActFloor = floor;
            bool unknown = point.PointType == MapPointType.Unknown;
            string q = unknown ? "?" : "";
            RoomType roomType = point.PointType switch
            {
                MapPointType.Monster => RoomType.Monster,
                MapPointType.Treasure => RoomType.Treasure,
                MapPointType.RestSite => RoomType.RestSite,
                MapPointType.Shop => RoomType.Shop,
                _ => state.Odds.UnknownMapPoint.Roll(RunManager.BuildRoomTypeBlacklist(state.CurrentMapPointHistoryEntry, point.Children), state),
            };

            ModelId? modelId = null;
            string? problem = null;
            switch (roomType)
            {
                case RoomType.Monster:
                {
                    var fight = Sim.Fight(state, player, RoomType.Monster);
                    string where = $"fight on floor {floor} ({fight.Encounter})";
                    problem = ctx.BadCards(fight.Cards, where) ?? (fight.Potion == null ? null : ctx.BadPotions(new[] { fight.Potion }, where));
                    rooms.Add(new Room(floor, q + fight.Encounter, fight.Cards, fight.Potion));
                    modelId = ModelDb.AllEncounters.First(e => e.Id.Entry == fight.Encounter).Id;
                    ctx.MaxGold += fight.Gold;
                    break;
                }
                case RoomType.Treasure:
                {
                    // TreasureRoom: gold from the Rewards RNG (OneOffSynchronizer.DoTreasureRoomRewards), relic rarity
                    // from TreasureRoomRelics and the relic from the shared bag (TreasureRoomRelicSynchronizer).
                    string? relicId = null;
                    if (Hook.ShouldGenerateTreasure(state, player))
                    {
                        double gold = player.PlayerRng.Rewards.NextInt(42, 53);
                        if (AscensionHelper.HasAscension(AscensionLevel.Poverty))
                            gold *= AscensionHelper.PovertyAscensionGoldMultiplier;
                        ctx.MinGold += (int)gold;
                        ctx.MaxGold += (int)gold;
                        var rarity = RelicFactory.RollRarity(state.Rng.TreasureRoomRelics);
                        relicId = (state.SharedRelicGrabBag.PullFromFront(rarity, state) ?? RelicFactory.FallbackRelic).Id.Entry;
                        problem = ctx.BadRelic(relicId, $"chest on floor {floor}");
                        if (problem == null)
                            Sim.Obtain(player, relicId);
                    }
                    rooms.Add(new Room(floor, q + "CHEST", Relic: relicId));
                    break;
                }
                case RoomType.Shop when config.extraRoomsBeforeElite.Contains("Shop"):
                    problem = Shop(ctx, floor, q, rooms);
                    break;
                case RoomType.Event:
                {
                    var (index, eventProblem) = NextForcedEvent(ctx);
                    if (eventProblem != null)
                        return (null, ctx.Decisions, $"\"?\" on floor {floor} {eventProblem}");
                    // ActModel.PullNextEvent, with the skips decided above instead of the sim's own gold and HP.
                    var set = _rooms(act);
                    set.eventsVisited = index;
                    var ev = Hook.ModifyNextEvent(state, set.NextEvent);
                    state.AddVisitedEvent(ev);
                    if (!config.allowedEvents.Contains(ev.Id.Entry))
                        return (null, ctx.Decisions, $"\"?\" on floor {floor} becomes event {ev.Id.Entry}");
                    modelId = ev.Id;
                    var (detail, relicId, evProblem) = Event(ctx, ev, floor);
                    ctx.MaxGold += 400; // more than any allowed event's gold option
                    problem = evProblem;
                    rooms.Add(new Room(floor, "?" + ev.Id.Entry, Relic: relicId, Detail: detail));
                    break;
                }
                default:
                    if (!config.extraRoomsBeforeElite.Contains(roomType.ToString()))
                        return (null, ctx.Decisions, $"\"?\" on floor {floor} becomes {roomType}");
                    rooms.Add(new Room(floor, q + roomType.ToString().ToUpperInvariant()));
                    break;
            }
            if (problem != null)
                return (null, ctx.Decisions, problem);
            state.AppendToMapPointHistory(point.PointType, roomType, modelId);
            if (roomType != RoomType.Monster) // Sim.Fight already marked the fight visited
                act.MarkRoomVisited(roomType);
        }
        rooms.Add(new Room(route[^1].coord.row + 1, act.PullNextEncounter(RoomType.Elite).Id.Entry));
        return (new Replay(neow, rooms, string.Join(", ", ctx.Labels), neowOffered.Display()), ctx.Decisions, null);
    }

    // MerchantRoom: the game's own inventory (its relic rarities come from the Rewards RNG). Every item on sale must
    // pass the filters; buying a relic that changes later rewards is replayed both ways.
    static string? Shop(Ctx ctx, int floor, string q, List<Room> rooms)
    {
        ctx.VisitedShop = true;
        ctx.MinGold = 0;
        var inventory = MerchantInventory.CreateForNormalMerchant(ctx.Player);
        var cards = inventory.CardEntries.Where(e => e.CreationResult != null).Select(e => e.CreationResult!.Card.Id.Entry).ToList();
        var relics = inventory.RelicEntries.Where(e => e.Model != null).Select(e => e.Model!.Id.Entry).ToList();
        var potions = inventory.PotionEntries.Where(e => e.Model != null).Select(e => e.Model!.Id.Entry).ToList();
        string where = $"shop on floor {floor}";
        rooms.Add(new Room(floor, q + "SHOP", Detail: $"cards {string.Join(", ", cards)}; relics {string.Join(", ", relics)}; potions {string.Join(", ", potions)}"));
        var problem = ctx.BadCards(cards, where) ?? ctx.BadPotions(potions, where) ?? relics.Select(r => ctx.BadRelic(r, where)).FirstOrDefault(p => p != null);
        if (problem != null)
            return problem;
        foreach (var relic in relics.Where(ChangesRewards))
            if (ctx.Decide($"buy {relic} on floor {floor}"))
                Sim.Obtain(ctx.Player, relic);
        return null;
    }

    static readonly Dictionary<string, bool> _changesRewards = new();

    static bool ChangesRewards(string relicId)
    {
        if (!_changesRewards.TryGetValue(relicId, out var result))
        {
            var type = ModelDb.AllRelics.First(r => r.Id.Entry == relicId).GetType();
            result = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Any(m => _rewardHooks.Contains(m.Name) && m.GetBaseDefinition().DeclaringType != type);
            _changesRewards[relicId] = result;
        }
        return result;
    }

    // Reproduces the parts of each allowed event that touch the Rewards RNG or hand out cards, potions or relics.
    // Returns (what it offered, relic obtained, problem).
    static (string?, string?, string?) Event(Ctx ctx, EventModel ev, int floor)
    {
        var player = ctx.Player;
        string where = $"{ev.Id.Entry} on floor {floor}";
        switch (ev.Id.Entry)
        {
            case "THIS_OR_THAT":
                // Ornate: next relic from the player's bag, rarity rolled on the Rewards RNG, plus Clumsy. Plain: gold.
                if (!ctx.Decide($"This or That relic on floor {floor}"))
                    return ("Plain", null, null);
                var (relic, relicProblem) = BagRelic(ctx, where);
                if (relicProblem != null)
                    return (null, relic, relicProblem);
                CardPileCmd.AddCurseToDeck<Clumsy>(player).GetAwaiter().GetResult();
                return ("Ornate", relic, null);

            case "BRAIN_LEECH":
            {
                if (!ctx.Decide($"Brain Leech Rip on floor {floor}"))
                {
                    // Share Knowledge: pick 1 of 5 class cards.
                    var cards = CardFactory.CreateForReward(player, 5, CardCreationOptions.ForNonCombatWithDefaultOdds(new[] { player.Character.CardPool }))
                        .Select(r => r.Card.Id.Entry).ToList();
                    return ("Share Knowledge: " + string.Join(", ", cards), null, ctx.BadCards(cards, where));
                }
                // Rip: a 3-card colorless reward.
                var options = CardCreationOptions.ForNonCombatWithDefaultOdds(new[] { ModelDb.CardPool<ColorlessCardPool>() })
                    .WithFlags(CardCreationFlags.NoRarityModification | CardCreationFlags.NoCardPoolModifications);
                Headless.TakeOffered();
                RewardsCmd.OfferCustom(player, new List<Reward> { new CardReward(options, 3, player) }).GetAwaiter().GetResult();
                var colorless = Headless.TakeOffered().Cards.ToList();
                return ("Rip: " + string.Join(", ", colorless), null, ctx.BadCards(colorless, where));
            }

            case "ROOM_FULL_OF_CHEESE":
            {
                if (ctx.Decide($"Room Full of Cheese Search on floor {floor}"))
                {
                    Sim.Obtain(player, "CHOSEN_CHEESE");
                    return ("Search", "CHOSEN_CHEESE", null);
                }
                // Gorge: pick 2 of 8 common class cards.
                var options = CardCreationOptions.ForNonCombatWithUniformOdds(new[] { player.Character.CardPool }, c => c.Rarity == CardRarity.Common)
                    .WithFlags(CardCreationFlags.NoRarityModification);
                var cards = CardFactory.CreateForReward(player, 8, options).Select(r => r.Card.Id.Entry).ToList();
                return ("Gorge: " + string.Join(", ", cards), null, ctx.BadCards(cards, where));
            }

            case "WELLSPRING":
            {
                if (ctx.Decide($"Wellspring Bathe on floor {floor}"))
                    return ("Bathe", null, null);
                // Bottle: a random potion picked with the Rewards RNG.
                var items = player.Character.PotionPool.GetUnlockedPotions(player.UnlockState)
                    .Concat(ModelDb.PotionPool<SharedPotionPool>().GetUnlockedPotions(player.UnlockState));
                var potion = player.PlayerRng.Rewards.NextItem(items)?.Id.Entry;
                return ("Bottle: " + potion, null, potion == null ? null : ctx.BadPotions(new[] { potion }, where));
            }

            case "WHISPERING_HOLLOW":
            {
                if (!ctx.Decide($"Whispering Hollow Hug on floor {floor}"))
                {
                    // Gold: pay for two random potions (populated with the Rewards RNG).
                    Headless.TakeOffered();
                    RewardsCmd.OfferCustom(player, new List<Reward> { new PotionReward(player), new PotionReward(player) }).GetAwaiter().GetResult();
                    var potions = Headless.TakeOffered().Potions;
                    return ("Gold: " + string.Join(", ", potions), null, ctx.BadPotions(potions, where));
                }
                // Hug: transform a card with the event's own RNG. Try every card the player could hold by now.
                var results = new List<string>();
                var candidates = player.Deck.Cards.Where(c => c.IsTransformable).Select(c => c.Id.Entry).Concat(ctx.SeenCards.Select(c => c.TrimEnd('+'))).Distinct();
                foreach (var id in candidates)
                {
                    var canonical = ModelDb.AllCards.First(c => c.Id.Entry == id);
                    var original = ctx.State.CreateCard(canonical, player);
                    if (!original.IsTransformable) continue;
                    var rng = EventRng(ctx, ev);
                    rng.NextInt(-9, 10); // CalculateVars
                    var replacement = CardFactory.CreateRandomCardForTransform(original, false, rng);
                    results.Add($"{id}->{replacement.Id.Entry}");
                    if (!ctx.Config.CardOk(replacement.Id.Entry))
                        return (null, null, $"{where} Hug can turn {id} into {replacement.Id.Entry}");
                }
                return ("Hug: " + string.Join(", ", results), null, null);
            }

            case "LUMINOUS_CHOIR":
            {
                // Offer Tribute: pay the gold, then a relic from the player's bag (rarity on the Rewards RNG).
                if (!ctx.Decide($"Luminous Choir tribute on floor {floor}"))
                    return ("Reach Into the Flesh", null, null);
                ctx.MinGold = 0;
                var (tribute, problem) = BagRelic(ctx, where);
                return ("Offer Tribute", tribute, problem);
            }

            case "UNREST_SITE":
            {
                // Kill: lose max HP, then a relic from the player's bag (rarity on the Rewards RNG). Rest: heal + Poor Sleep.
                if (!ctx.Decide($"Unrest Site kill on floor {floor}"))
                    return ("Rest", null, null);
                var (killRelic, problem) = BagRelic(ctx, where);
                return ("Kill", killRelic, problem);
            }

            case "THE_LEGENDS_WERE_TRUE":
            {
                // Slowly Find an Exit: a random potion picked with the Rewards RNG. Nab the Map: a Spoils Map (chest gold).
                if (!ctx.Decide($"Legends Were True exit on floor {floor}"))
                    return ("Nab the Map", null, null);
                var items = player.Character.PotionPool.GetUnlockedPotions(player.UnlockState)
                    .Concat(ModelDb.PotionPool<SharedPotionPool>().GetUnlockedPotions(player.UnlockState));
                var potion = player.PlayerRng.Rewards.NextItem(items)?.Id.Entry;
                return ("Find an Exit: " + potion, null, potion == null ? null : ctx.BadPotions(new[] { potion }, where));
            }

            case "DENSE_VEGETATION":
            {
                // Rest: a rest-site heal, then a fight with normal hallway rewards (cards, potion roll, gold).
                if (!ctx.Decide($"Dense Vegetation rest + fight on floor {floor}"))
                    return ("Trudge On", null, null);
                var fight = Sim.FightEncounter(ctx.State, player, ModelDb.Encounter<MegaCrit.Sts2.Core.Models.Encounters.DenseVegetationEventEncounter>());
                ctx.MaxGold += fight.Gold;
                string fightWhere = $"{where} fight";
                var problem = ctx.BadCards(fight.Cards, fightWhere) ?? (fight.Potion == null ? null : ctx.BadPotions(new[] { fight.Potion }, fightWhere));
                return ($"Rest + fight: {string.Join(", ", fight.Cards)}" + (fight.Potion != null ? $" + potion {fight.Potion}" : ""), null, problem);
            }

            case "TRASH_HEAP":
            {
                // Dive In: a relic picked with the event's own RNG. Grab: 100 gold and a colorless card from the same RNG.
                var rng = EventRng(ctx, ev);
                if (ctx.Decide($"Trash Heap Dive In on floor {floor}"))
                {
                    var heapRelic = rng.NextItem(_trashHeapRelics())!.Id.Entry;
                    var problem = ctx.BadRelic(heapRelic, where);
                    if (problem == null)
                        Sim.Obtain(player, heapRelic);
                    return ("Dive In: " + heapRelic, heapRelic, problem);
                }
                var heapCard = rng.NextItem(_trashHeapCards())!.Id.Entry;
                return ("Grab: " + heapCard, null, ctx.BadCards(new[] { heapCard }, where));
            }

            default:
                // Sunken Treasury, Jungle Maze, Byrdonis Nest, Self Help Book, Slippery Bridge, Sunken Statue, Doors of Light
                // and Dark, Wood Carvings, Abyssal Baths, Spiraling Whirlpool, Waterlogged Scriptorium: their choices only
                // use the event's own RNG and give gold, HP, upgrades, enchantments, removals or fixed cards/relics.
                return (null, null, null);
        }
    }

    // RelicFactory.PullNextRelicFromFront(player): rarity rolled on the Rewards RNG, relic from the player's bag.
    static (string relic, string? problem) BagRelic(Ctx ctx, string where)
    {
        var relic = RelicFactory.PullNextRelicFromFront(ctx.Player).Id.Entry;
        var problem = ctx.BadRelic(relic, where);
        if (problem == null)
            Sim.Obtain(ctx.Player, relic);
        return (relic, problem);
    }

    static Rng EventRng(Ctx ctx, EventModel ev) =>
        new(ctx.State.Rng.Seed + (ulong)(ev.IsShared ? 0 : ctx.State.GetPlayerSlotIndex(ctx.Player)) + StringHelper.GetDeterministicHashCode(ev.Id.Entry));

    // Mirrors RoomSet.EnsureNextEventIsValid: the first unseen event in the queue that is allowed. Several events are
    // allowed only at some gold or HP, which depends on play (gold rewards taken or skipped, shopping, damage taken), so
    // where an event could either appear or be skipped, both cases are replayed. Returns the queue index of the pick.
    static (int index, string? problem) NextForcedEvent(Ctx ctx)
    {
        var set = _rooms(ctx.State.Act);
        for (int i = 0; i < set.events.Count; i++)
        {
            int index = set.eventsVisited + i;
            var ev = set.events[index % set.events.Count];
            if (ctx.State.VisitedEventIds.Contains(ev.Id))
                continue;
            var (canAppear, canSkip) = CanAppear(ctx, ev);
            if (!canAppear)
                continue;
            if (canSkip && ctx.Decide($"{ev.Id.Entry} skipped (its gold/HP condition not met)"))
                continue;
            return (index, null);
        }
        return (-1, "has no event left");
    }

    static (bool appear, bool skip) CanAppear(Ctx ctx, EventModel ev)
    {
        switch (ev.Id.Entry)
        {
            case "WHISPERING_HOLLOW":
                return Gold(ctx, 44);
            case "WATERLOGGED_SCRIPTORIUM":
                return Gold(ctx, 55);
            case "ENDLESS_CONVEYOR":
                return Gold(ctx, 120);
            case "TRASH_HEAP":
                return (true, true); // needs more than 5 HP, which depends on damage taken
            case "MORPHIC_GROVE":
                return Gold(ctx, 100); // also needs 2 transformable cards, which the deck always has this early
            case "LUMINOUS_CHOIR":
                return Gold(ctx, 149 - EventRng(ctx, ev).NextInt(0, 50)); // its CalculateVars
            case "UNREST_SITE":
                return (true, true); // needs HP at most 70%, which depends on damage taken
            case "THE_LEGENDS_WERE_TRUE":
            {
                bool allowed = ev.IsAllowed(ctx.State); // act 1 and full HP here; under 10 HP would skip it
                return (allowed, allowed);
            }
            default:
                return (ev.IsAllowed(ctx.State), false);
        }
    }

    static (bool, bool) Gold(Ctx ctx, int needed) => (ctx.MaxGold >= needed, ctx.MinGold < needed);
}
