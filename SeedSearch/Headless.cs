using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves;
using System.Runtime.CompilerServices;

/// <summary>
/// Boots the game's model registry without the Godot engine. Anything that would call into native Godot
/// (logging, OS queries) is patched out with Harmony; generation code itself runs unmodified.
/// </summary>
static class Headless
{
    public static void Boot()
    {
        var harmony = new Harmony("seedsearch.headless");
        var logger = typeof(Logger);
        harmony.Patch(AccessTools.Method(logger, "GetIsRunningFromGodotEditor"), prefix: new HarmonyMethod(typeof(Headless), nameof(ReturnFalse)));
        harmony.Patch(AccessTools.Method(logger, nameof(Logger.LogMessage), new[] { typeof(LogLevel), typeof(LogType), typeof(string), typeof(int) }),
            prefix: new HarmonyMethod(typeof(Headless), nameof(Skip)));
        // Localization tables are never loaded; text lookups don't influence any RNG.
        harmony.Patch(AccessTools.Method(typeof(LocString), nameof(LocString.Exists), new[] { typeof(string), typeof(string) }),
            prefix: new HarmonyMethod(typeof(Headless), nameof(ReturnFalse)));
        harmony.Patch(AccessTools.Method(typeof(LocString), nameof(LocString.Exists), Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Headless), nameof(ReturnFalse)));
        foreach (var name in new[] { nameof(LocString.GetFormattedText), nameof(LocString.GetRawText) })
            harmony.Patch(AccessTools.Method(typeof(LocString), name), prefix: new HarmonyMethod(typeof(Headless), nameof(ReturnEmpty)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(RelicModel), nameof(RelicModel.HoverTipsExcludingRelic)), prefix: new HarmonyMethod(typeof(Headless), nameof(NoHoverTips)));
        // Sound effects (e.g. a relic flashing when its hook fires) go to the audio engine; no gameplay effect.
        foreach (var method in typeof(MegaCrit.Sts2.Core.Commands.SfxCmd).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.ReturnType == typeof(void)))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Headless), nameof(Skip)));
        // Custom reward screens (Kaleidoscope's picks, ...): generate the rewards exactly as Offer would, record them,
        // and skip the screen.
        harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.RewardsCmd), nameof(MegaCrit.Sts2.Core.Commands.RewardsCmd.OfferCustom)),
            prefix: new HarmonyMethod(typeof(Headless), nameof(RecordCustomRewards)));
        // A relic flash only plays its sound and visual effect.
        harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Players.Player), "OnRelicFlashed"), prefix: new HarmonyMethod(typeof(Headless), nameof(Skip)));
        // Skip/Reroll buttons on the card reward screen: UI only, no RNG, but their constructor touches controller input.
        harmony.Patch(AccessTools.Method(typeof(CardRewardAlternative), nameof(CardRewardAlternative.Generate)), prefix: new HarmonyMethod(typeof(Headless), nameof(NoAlternatives)));

        // Vanilla models only: bypass the mod loader so no mod content can leak into the pools.
        var vanillaTypes = AbstractModelSubtypes.All.ToArray();
        typeof(ModelDb).GetField("_allAbstractModelSubtypes", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, vanillaTypes);
        ModelDb.Init(vanillaTypes);
        AssemblyInfo.Init();
        ModelIdSerializationCache.Init();
        ModelDb.InitIds();

        // A bare SaveManager that never touches disk: generation only reads progress stats and marks things as seen.
        var saves = (SaveManager)RuntimeHelpers.GetUninitializedObject(typeof(SaveManager));
        _progress = (ProgressState)RuntimeHelpers.GetUninitializedObject(typeof(ProgressState));
        harmony.Patch(AccessTools.PropertyGetter(typeof(SaveManager), nameof(SaveManager.Progress)), prefix: new HarmonyMethod(typeof(Headless), nameof(GetProgress)));
        foreach (var name in new[] { nameof(SaveManager.MarkPotionAsSeen), nameof(SaveManager.MarkCardAsSeen), nameof(SaveManager.MarkRelicAsSeen) })
            harmony.Patch(AccessTools.Method(typeof(SaveManager), name), prefix: new HarmonyMethod(typeof(Headless), nameof(Skip)));
        harmony.Patch(AccessTools.Method(typeof(ProgressState), nameof(ProgressState.GetStatsForCharacter)), prefix: new HarmonyMethod(typeof(Headless), nameof(ReturnNull)));
        SaveManager.MockInstanceForTesting(saves);
    }

    static ProgressState _progress = null!;

    static bool GetProgress(ref ProgressState __result)
    {
        __result = _progress;
        return false;
    }

    static bool ReturnNull(ref object? __result)
    {
        __result = null;
        return false;
    }

    static bool ReturnFalse(ref bool __result)
    {
        __result = false;
        return false;
    }

    static bool Skip() => false;

    static bool NoHoverTips(ref IEnumerable<MegaCrit.Sts2.Core.HoverTips.IHoverTip> __result)
    {
        __result = Array.Empty<MegaCrit.Sts2.Core.HoverTips.IHoverTip>();
        return false;
    }

    /// <summary>What custom reward screens offered since the last <see cref="TakeOffered"/>.</summary>
    public record Offered(List<List<string>> CardRewards, List<string> Potions)
    {
        public IEnumerable<string> Cards => CardRewards.SelectMany(c => c);

        public List<string> Display() =>
            CardRewards.Select(c => "[" + string.Join(" / ", c) + "]").Concat(Potions.Select(p => "potion " + p)).ToList();
    }

    static Offered _offered = new(new(), new());

    public static Offered TakeOffered()
    {
        var taken = _offered;
        _offered = new(new(), new());
        return taken;
    }

    static bool RecordCustomRewards(MegaCrit.Sts2.Core.Entities.Players.Player player, List<MegaCrit.Sts2.Core.Rewards.Reward> rewards, ref Task __result)
    {
        var set = new MegaCrit.Sts2.Core.Rewards.RewardsSet(player).WithCustomRewards(rewards);
        set.GenerateWithoutOffering().GetAwaiter().GetResult();
        foreach (var reward in set.Rewards)
        {
            if (reward is MegaCrit.Sts2.Core.Rewards.CardReward cards)
                _offered.CardRewards.Add(cards.Cards.Select(c => c.Id.Entry).ToList());
            else if (reward is MegaCrit.Sts2.Core.Rewards.PotionReward potion && potion.Potion != null)
                _offered.Potions.Add(potion.Potion.Id.Entry);
        }
        __result = Task.CompletedTask;
        return false;
    }

    static bool NoAlternatives(ref IReadOnlyList<CardRewardAlternative> __result)
    {
        __result = Array.Empty<CardRewardAlternative>();
        return false;
    }

    static bool ReturnEmpty(ref string __result)
    {
        __result = "";
        return false;
    }
}
