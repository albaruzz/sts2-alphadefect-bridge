using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Headless;

/// <summary>
/// Lightweight real-game shims in namespace Sts2Headless so Observation.cs + Engine/Obs/** + Catalog.cs
/// compile directly into Sts2Bridge without pulling in headless-only message pumps or screen stubs.
/// </summary>
public sealed record SelectInfo(
    string? PromptTable,
    string? PromptKey,
    string Method,
    EnchantmentModel? Enchantment,
    AbstractModel? Source = null);

public sealed class PendingCardChoice(
    IReadOnlyList<CardModel> options,
    int min,
    int max,
    bool cancelable,
    SelectInfo? info = null)
{
    public SelectInfo? Info { get; } = info;
    public IReadOnlyList<CardModel> Options { get; } = options;
    public int Min { get; } = min;
    public int Max { get; } = max;
    public bool Cancelable { get; } = cancelable;
}

public static class AgentCardSelector
{
    public static PotionModel? UsingPotion { get; set; }
}

public sealed class PendingRewards(RewardsSet set, IReadOnlySet<Reward> attempted)
{
    public RewardsSet Set { get; } = set;
    public List<Reward> Available => Set.Rewards
        .Where(r => !r.SuccessfullySelected && !attempted.Contains(r))
        .Where(r => r is not PotionReward || Set.Player.HasOpenPotionSlots)
        .ToList();
    public bool IsTerminal => Set.Room is CombatRoom;
}

public static class RewardsFlow
{
    public static PendingRewards? Pending { get; set; }
    public static RewardsSet? ActiveSet { get; set; }
    public static Reward? ActiveChoice { get; set; }
    public static IReadOnlySet<Reward>? ActiveAttempted { get; set; }

    static HashSet<string>? _noDownside;

    public static HashSet<string> NoDownside
    {
        get
        {
            if (_noDownside != null) return _noDownside;
            var path = AppContext.GetData("Sts2NoDownsideRelics") as string
                       ?? Path.Combine(Sts2Bridge.BridgeConfig.ModDir, "no_downside_relics.txt");
            try
            {
                _noDownside = File.Exists(path)
                    ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet()
                    : new HashSet<string>();
            }
            catch
            {
                _noDownside = new HashSet<string>();
            }
            return _noDownside;
        }
    }

    public static void LoadNoDownside(string path)
    {
        if (!File.Exists(path))
        {
            Sts2Bridge.BridgeLog.Warn($"no_downside_relics.txt not found at {path}; free relic auto-claim disabled.");
            _noDownside = new HashSet<string>();
            return;
        }
        _noDownside = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet();
    }

    public static bool IsNoDownside(RelicModel? r) => r != null && NoDownside.Contains(r.Id.Entry);

    public static IEnumerable<Reward> Unclaimed(RewardsSet set, IReadOnlySet<Reward>? attempted, Reward? except) =>
        set.Rewards.Where(r => !r.SuccessfullySelected && r != except && (attempted == null || !attempted.Contains(r)));

    public static void Reset()
    {
        Pending = null;
        ActiveSet = null;
        ActiveChoice = null;
        ActiveAttempted = null;
    }
}

public static class FakeScreens
{
    public static CrystalSphereMinigame? CrystalSphere { get; set; }
}

public static class UiFlows
{
    public static int CombatsWon { get; set; }
}

public sealed class RunSession
{
    public static RunState State => RunManager.Instance.DebugOnlyGetState()!;
    public bool CombatOnly => false;
    public bool IsVictory { get; set; }
    public int Steps { get; set; }
    internal PendingCardChoice? Selecting { get; set; }
    internal IReadOnlyList<int> SelectedIndices { get; set; } = Array.Empty<int>();

    internal static (int actual, int nominal, int extra) RestHealInfo(Player p)
    {
        var cr = p.Creature;
        int nominal = (int)HealRestSiteOption.GetHealAmount(p);
        int actual = Math.Clamp(nominal, 0, Math.Max(0, cr.MaxHp - cr.CurrentHp));
        int extra = 0;
        foreach (var m in p.RunState.IterateHookListeners(null))
        {
            int n = m.ModifyExtraRestSiteHealText(p, Array.Empty<LocString>()).Count;
            if (n > 0 && m.ModifyRestSiteHealAmount(cr, 100m) == 100m) extra += n;
        }
        return (actual, nominal, extra);
    }
}

public static class RunSessionMap
{
    public static IEnumerable<MapPoint> Travelable(RunState s)
    {
        var map = s.Map;
        if (s.VisitedMapCoords.Count == 0) return new[] { map.StartingMapPoint };
        MapCoord last = s.VisitedMapCoords[^1];
        if (map.SecondBossMapPoint != null && last == map.BossMapPoint.coord) return new[] { map.SecondBossMapPoint };
        if (last.row == map.GetRowCount() - 1) return new[] { map.BossMapPoint };
        var point = map.GetPoint(last);
        return point == null ? new[] { map.StartingMapPoint } : MapTravel.GetTravelablePointsFrom(s, point);
    }
}

public static class VirtualFs
{
    public static string ResDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "res");
}

public static class IlScan
{
    public static IEnumerable<MethodBase> Callees(byte[] il, Module module, MethodBase owner)
    {
        Type[]? typeArgs = owner.DeclaringType?.IsGenericType == true ? owner.DeclaringType.GetGenericArguments() : null;
        Type[]? methodArgs = owner is MethodInfo { IsGenericMethod: true } mi ? mi.GetGenericArguments() : null;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            byte op = il[i];
            bool isCall = op == 0x28 || op == 0x6F || op == 0x73;
            bool isFtn = op == 0xFE && i + 5 < il.Length && il[i + 1] == 0x06;
            if (!isCall && !isFtn) continue;
            int token = BitConverter.ToInt32(il, isFtn ? i + 2 : i + 1);
            if ((token >> 24) is not (0x06 or 0x0A or 0x2B)) continue;
            MethodBase? m = null;
            try { m = module.ResolveMethod(token, typeArgs, methodArgs); } catch { }
            if (m != null) yield return m;
        }
    }
}
