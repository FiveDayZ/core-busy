using System.IO;
using System.Text.Json;
using CoreBusy.App.Services;
using CoreBusy.Core.Models;
using CoreBusy.Core.Progression;

internal static partial class Program
{
    private static void ProgressionRules()
    {
        var light = new[] { new CpuCoreSnapshot { Id = "C0", UsagePercent = 5 } };
        var busy = new[] { light[0] with { UsagePercent = 100 } };
        var accumulator = new CoreExpAccumulator();
        accumulator.Accumulate(light, 10 * 3600);
        Equal(90, accumulator.ExpCoreMinutes("C0"), "ten light hours earn ninety EXP");
        accumulator.Accumulate(busy, 3600);
        Equal(150, accumulator.ExpCoreMinutes("C0"), "busy hour after light history still earns sixty EXP");

        var mixed = new CoreExpAccumulator();
        mixed.Accumulate([light[0] with { UsagePercent = 8 }], 3600);
        mixed.Accumulate(busy, 3600);
        Equal(69, mixed.ExpCoreMinutes("C0"), "each interval awards max before accumulating");
        var split = new CoreExpAccumulator();
        for (var i = 0; i < 60; i++) split.Accumulate([light[0] with { UsagePercent = 8 }], 60);
        for (var i = 0; i < 60; i++) split.Accumulate(busy, 60);
        Equal(mixed.ExpCoreMinutes("C0"), split.ExpCoreMinutes("C0"), "sample partition does not change awarded EXP");
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -5.0 })
            Equal(0, CoreExpAccumulator.AwardCoreMinutes(value, 60), "invalid usage cannot generate EXP");
        foreach (var seconds in new[] { double.NaN, double.PositiveInfinity, -1.0, 0.0 })
            Equal(0, CoreExpAccumulator.AwardCoreMinutes(100, seconds), "invalid elapsed time cannot generate EXP");
        Equal(1, CoreExpAccumulator.AwardCoreMinutes(150, 60), "usage is capped at full physical-core load");
        foreach (var count in new[] { 8, 12, 24, 32 })
        {
            var cores = Enumerable.Range(0, count).Select(i => new CpuCoreSnapshot { Id = "C" + i, UsagePercent = 30 }).ToArray();
            var monitor = new CoreExpAccumulator();
            monitor.Accumulate(cores, 3600);
            Check(cores.All(c => Math.Abs(monitor.ExpCoreMinutes(c.Id) - 18) < 1e-7), count + " cores do not dilute per-core EXP");
        }
        Equal(876, CoreLevelCurve.CumulativeExp(99) / 60, "daily eight-hour thirty-percent use reaches max in one year");
        Check(CoreLevelCurve.CumulativeExp(10) / 144 <= 1 && CoreLevelCurve.CumulativeExp(40) / 144 is >= 10 and <= 14
            && CoreLevelCurve.CumulativeExp(75) / 144 is >= 90 and <= 100, "visual evolutions give day, fortnight, quarter and year milestones");
        for (var level = 1; level <= 99; level++)
        {
            var threshold = CoreLevelCurve.CumulativeExp(level);
            Check(CoreLevelCurve.LevelOf(threshold) == level, "new curve roundtrip Lv " + level);
            if (level > 1)
                Check(CoreLevelCurve.LevelOf(threshold * (1 - 1e-10)) == level - 1, "new curve lower boundary Lv " + level);
            var historical = 12 * (Math.Pow(1.1347921802051704, level - 1) - 1) / (1.1347921802051704 - 1);
            Check(CoreLevelCurve.LevelOf(historical) >= level, "new curve never reduces an old level " + level);
        }
    }

    private static void AwardedExpPersistence()
    {
        // 真正不含新增字段的旧账本：不能借 Save 生成它，否则无法验证缺字段兼容。
        var path = Path.Combine(EnergyHistoryStore.DataDirectory, "core-exp.json");
        File.WriteAllText(path, "{\"caliber\":\"load-core-minute/v1\",\"cores\":{\"C0\":{\"loadCoreMinutes\":30,\"floorCoreMinutes\":90,\"totalLevels\":1}}}");
        Equal(90, CoreExpStore.Load().Cores["C0"].ExpCoreMinutes, "legacy ledger retains earned EXP without reset");
        var (vm, samples) = Session();
        Accumulate(vm, samples, 3600);
        Equal(150, DisplayedExp(vm, "C0"), "legacy balance plus new busy interval");
        SaveExp(vm); vm.Stop();
        Equal(150, CoreExpStore.Load().Cores["C0"].ExpCoreMinutes, "awarded EXP survives persistence");
        var (restarted, next) = Session();
        var light = next.Select(s => s with { UsagePercent = 5 }).ToArray();
        Accumulate(restarted, light, 3600);
        Equal(159, DisplayedExp(restarted, "C0"), "light award remains additive after restart");
        SaveExp(restarted); SaveExp(restarted); restarted.Stop();
        Equal(159, CoreExpStore.Load().Cores["C0"].ExpCoreMinutes, "repeated saves cannot duplicate interval awards");
        CoreExpStore.Save(new Dictionary<string, CoreExpEntry> { ["C0"] = new() { LoadCoreMinutes = 10 } });
        Equal(159, CoreExpStore.Load().Cores["C0"].ExpCoreMinutes, "stale ledger cannot erase new awarded balance");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Equal(159, json.RootElement.GetProperty("cores").GetProperty("C0").GetProperty("awardedCoreMinutes").GetDouble(), "earned balance is explicitly written to ledger");
    }
}
