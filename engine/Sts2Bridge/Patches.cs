using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Sts2Headless;

namespace Sts2Bridge;

/// <summary>
/// Non-invasive Harmony hooks for real-game observation fidelity & lifecycle tracking:
/// 1. Observation.DrawTop (format 20+ known draw-pile top tracking).
/// 2. Observation.RawPreview (v0.114.0+ pre-HpLoss-hook targeted damage preview during Observation.Encode only).
/// 3. CardSelectCmd / PotionModel.OnUseWrapper / NPlayerHand.SelectCards metadata capture for SelectPrompt.
/// 4. NEventOptionButton._Ready variable snapshot so merged event-level DynamicVars can be stripped during Encode
///    (matching Sts2Headless's headless EventOption.Description.Variables).
/// 5. RunManager start/load/end/abandon & CombatManager.CombatWon lifecycle tracking.
/// </summary>
internal static class BridgePatches
{
    public static SelectInfo? LastSelectInfo { get; set; }
    public static List<CardModel>? HandSelectionInitialOptions { get; set; }
    public static Task? PendingEventOptionTask { get; set; }

    public static int LoadedFloor { get; set; }
    public static bool? LastRunVictory { get; set; }
    public static bool RunJustEnded { get; set; }

    static readonly ConditionalWeakTable<EventOption, HashSet<string>> OriginalEventOptionVarKeys = new();

    public static void Install(Harmony h, BridgeConfig cfg)
    {
        ModMenuUi.Install(h, cfg);

        // 1. Known draw-pile top tracking (Regent / Fool / format 20+)
        try
        {
            Observation.DrawTop.Install(h);
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"DrawTop patch skipped: {e.Message}");
        }

        // 2. v0.114.0+ targeted damage preview fidelity (pre-HpLoss-hook basis during Observation.Encode)
#if !STS2_V0111
        try
        {
            if (AccessTools.Method(typeof(CreatureCmd), "GetDamageResultsPreview") != null)
            {
                Observation.RawPreview.Install(h);
                BridgeLog.Info("Installed Observation.RawPreview patch (v0.114.0+ targeted damage preview basis).");
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"RawPreview patch skipped: {e.Message}");
        }
#endif

        // 3. CardSelectCmd prompt/method/enchantment/source capture
        foreach (var m in typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .Where(m => !m.ContainsGenericParameters && m.GetParameters().Any(p => p.ParameterType == typeof(CardSelectorPrefs))))
        {
            h.Patch(m, prefix: Own(nameof(OnCardSelectCmdStart)));
        }

        if (AccessTools.Method(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen)) is { } chooseCard)
            h.Patch(chooseCard, prefix: Own(nameof(OnChooseACardStart)));

        if (AccessTools.Method(typeof(NPlayerHand), nameof(NPlayerHand.SelectCards)) is { } handSelect)
            h.Patch(handSelect, prefix: Own(nameof(OnHandSelectStart)));

        if (AccessTools.Method(typeof(PotionModel), nameof(PotionModel.OnUseWrapper)) is { } potUse)
            h.Patch(potUse, prefix: Own(nameof(OnPotionStart)), postfix: Own(nameof(OnPotionWrap)));

        // 4. EventOption variable snapshot before NEventOptionButton._Ready merges Event.DynamicVars into Option.Description
        if (AccessTools.Method(typeof(NEventOptionButton), nameof(NEventOptionButton._Ready)) is { } optReady)
            h.Patch(optReady, prefix: Own(nameof(BeforeEventOptionButtonReady)));

        if (AccessTools.Method(typeof(EventOption), nameof(EventOption.Chosen)) is { } optChosen)
            h.Patch(optChosen, postfix: Own(nameof(AfterEventOptionChosen)));

        // 5. Run start / load / end / combat-won lifecycle tracking
        if (AccessTools.Method(typeof(RunManager), nameof(RunManager.SetUpNewSingleplayer)) is { } newSp)
            h.Patch(newSp, postfix: Own(nameof(AfterNewSingleplayer)));

        if (AccessTools.Method(typeof(RunManager), nameof(RunManager.SetUpSavedSingleplayer)) is { } savedSp)
            h.Patch(savedSp, prefix: Own(nameof(BeforeSavedSingleplayer)));

        if (AccessTools.Method(typeof(RunManager), nameof(RunManager.OnEnded)) is { } onEnded)
            h.Patch(onEnded, prefix: Own(nameof(BeforeRunEnded)));

        if (AccessTools.Method(typeof(RunManager), nameof(RunManager.CleanUp)) is { } cleanUp)
            h.Patch(cleanUp, prefix: Own(nameof(BeforeRunCleanUp)));

        CombatManager.Instance.CombatWon += OnCombatWon;
    }

    static HarmonyMethod Own(string name) =>
        new(typeof(BridgePatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!);

    static void OnCombatWon(CombatRoom _) => UiFlows.CombatsWon++;

    static void OnCardSelectCmdStart(object[] __args, MethodBase __originalMethod)
    {
        try
        {
            var p = __args.FirstOrDefault(a => a is CardSelectorPrefs) is CardSelectorPrefs pr ? pr : default;
            var ps = __originalMethod.GetParameters();
            int si = Array.FindIndex(ps, q => q.Name == "source");
            LastSelectInfo = new SelectInfo(
                p.Prompt?.LocTable,
                p.Prompt?.LocEntryKey,
                __originalMethod.Name,
                __args.OfType<EnchantmentModel>().FirstOrDefault(),
                si >= 0 && si < __args.Length ? __args[si] as AbstractModel : null);
        }
        catch { }
    }

    static void OnChooseACardStart()
    {
        LastSelectInfo = new SelectInfo(null, null, "FromChooseACardScreen", null);
    }

    static void OnHandSelectStart(Func<CardModel, bool>? filter, AbstractModel? source)
    {
        try
        {
            var s = RunManager.Instance.DebugOnlyGetState();
            var me = s?.Players.FirstOrDefault();
            var hand = me?.PlayerCombatState?.Hand.Cards;
            if (hand != null)
                HandSelectionInitialOptions = hand.Where(c => filter == null || filter(c)).ToList();
            if (source != null && LastSelectInfo != null && LastSelectInfo.Source == null)
                LastSelectInfo = LastSelectInfo with { Source = source };
        }
        catch { }
    }

    static void OnPotionStart(PotionModel __instance) => AgentCardSelector.UsingPotion = __instance;

    static void OnPotionWrap(PotionModel __instance, ref Task __result) => __result = ClearPotionWhenDone(__instance, __result);

    static async Task ClearPotionWhenDone(PotionModel p, Task t)
    {
        try { await t; }
        finally { if (ReferenceEquals(AgentCardSelector.UsingPotion, p)) AgentCardSelector.UsingPotion = null; }
    }

    static void BeforeEventOptionButtonReady(NEventOptionButton __instance)
    {
        try
        {
            var opt = __instance.Option;
            if (opt?.Description?.Variables != null && !OriginalEventOptionVarKeys.TryGetValue(opt, out _))
                OriginalEventOptionVarKeys.Add(opt, new HashSet<string>(opt.Description.Variables.Keys));
        }
        catch { }
    }

    static void AfterEventOptionChosen(Task __result) => PendingEventOptionTask = __result;

    static void AfterNewSingleplayer()
    {
        LoadedFloor = 0;
        LastRunVictory = null;
        RunJustEnded = false;
    }

    static void BeforeSavedSingleplayer(SerializableRun save)
    {
        try { LoadedFloor = save?.FloorReached ?? 1; } catch { LoadedFloor = 1; }
        LastRunVictory = null;
        RunJustEnded = false;
    }

    static void BeforeRunEnded(bool isVictory)
    {
        LastRunVictory = isVictory;
        RunJustEnded = true;
    }

    static void BeforeRunCleanUp()
    {
        if (LastRunVictory == null && RunManager.Instance.IsInProgress)
            RunJustEnded = true;
    }

    /// <summary>
    /// Temporarily strips Event-wide DynamicVars merged by NEventOptionButton._Ready from EventOption.Description.Variables
    /// during Observation.Encode so features 2..7 match Sts2Headless byte-for-byte. Restored immediately on Dispose.
    /// </summary>
    public static IDisposable StripMergedEventVars(Decision d)
    {
        if (d.Kind != DecisionKind.Event) return EmptyScope.Instance;
        List<(IDictionary<string, object> Dict, string Key, object Val)>? stripped = null;
        foreach (var ch in d.Choices)
        {
            if (ch.A is not EventOption opt || opt.Description?.Variables is not IDictionary<string, object> vars) continue;
            if (!OriginalEventOptionVarKeys.TryGetValue(opt, out var origKeys)) continue;
            foreach (var kv in vars.ToList())
            {
                if (!origKeys.Contains(kv.Key))
                {
                    stripped ??= new();
                    stripped.Add((vars, kv.Key, kv.Value));
                    vars.Remove(kv.Key);
                }
            }
        }
        return stripped == null ? EmptyScope.Instance : new RestoreVarsScope(stripped);
    }

    sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }

    sealed class RestoreVarsScope(List<(IDictionary<string, object> Dict, string Key, object Val)> items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (dict, key, val) in items)
            {
                try { dict[key] = val; } catch { }
            }
        }
    }
}
