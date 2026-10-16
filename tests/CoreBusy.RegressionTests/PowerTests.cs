using System.IO;
using System.Reflection;
using CoreBusy.Sensor;
using CoreBusy.App.Views;
using CoreBusy.Core.Energy;
using CoreBusy.Core.Models;
using CoreBusy.App.ViewModels.Dashboard;

internal static partial class Program
{
    private static void PowerRules()
    {
        var input = new SystemPowerInputs { CpuPackageWatt = 30, MemoryTotalGb = 32, MemoryGeneration = MemoryGeneration.Ddr4 };
        var low = SystemPowerEstimator.Estimate(input with { MemoryLoadPercent = 10 }, SystemPowerSettings.Default);
        var high = SystemPowerEstimator.Estimate(input with { MemoryLoadPercent = 90 }, SystemPowerSettings.Default);
        Equal(low.MemoryWatt, high.MemoryWatt, "allocated RAM is not DRAM access activity");
        Equal(low.MemoryWatt, SystemPowerEstimator.Estimate(input with { MemoryGeneration = MemoryGeneration.Ddr5 }, SystemPowerSettings.Default).MemoryWatt, "DDR generation alone cannot invent module power differences");
        Check(low.MemoryWatt > 0 && low.MemoryWatt < 10, "32 GiB DRAM uses a disclosed baseline estimate");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1.0, 0.0, 1001.0 })
            Check(!SystemPowerEstimator.Estimate(input with { CpuPackageWatt = invalid }, SystemPowerSettings.Default).IsUsable, "invalid CPU power never becomes a total");
        var igpu = SystemPowerEstimator.Estimate(input with { HasGpu = true, GpuName = "Intel UHD Graphics" }, SystemPowerSettings.Default);
        Equal(0, igpu.GpuWatt, "integrated GPU is not double counted");
        Check(igpu.GpuSource == GpuPowerSource.IntegratedInPackage, "integrated GPU scope stays explicit");
        var mixedInput = input with
        {
            GpuMeasuredWatt = 1, GpuLoadPercent = 100,
            DiscreteGpuReadings = [new("NVIDIA GeForce RTX 3060", 60, 80), new("NVIDIA GeForce RTX 3060", null, 50)],
        };
        var mixed = SystemPowerEstimator.Estimate(mixedInput, SystemPowerSettings.Default);
        Equal(155, mixed.GpuWatt, "partial multi-GPU power adds measured card and own-load estimate");
        Check(mixed.GpuSource == GpuPowerSource.Mixed, "partial multi-GPU source stays mixed");
        var sleeping = SystemPowerEstimator.Estimate(mixedInput with
        {
            DiscreteGpuReadings = [new("NVIDIA GeForce RTX 3060", 60, 80), new("NVIDIA GeForce RTX 3060", 0, 0)],
        }, SystemPowerSettings.Default);
        Equal(60, sleeping.GpuWatt, "busy primary card does not wake zero-watt secondary in the model");
        Check(sleeping.GpuSource == GpuPowerSource.Measured, "measured zero watt remains measured");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1.0, 1001.0, 0.0 })
        {
            var badCard = SystemPowerEstimator.Estimate(input with
            {
                DiscreteGpuReadings = [new("NVIDIA GeForce RTX 3060", invalid, 100)],
            }, SystemPowerSettings.Default);
            Equal(170, badCard.GpuWatt, "invalid or contradictory per-card reading falls back to its own model");
        }
        var unknownLoad = SystemPowerEstimator.Estimate(input with
        {
            DiscreteGpuReadings = [new("NVIDIA GeForce RTX 3060", null, null)],
        }, SystemPowerSettings.Default);
        Equal(95, unknownLoad.GpuWatt, "unknown GPU load uses disclosed interval midpoint, not pretend idle");
        var idleHighTgp = SystemPowerEstimator.Estimate(input with
        {
            DiscreteGpuReadings = [new("NVIDIA GeForce RTX 4090", null, 0)],
        }, SystemPowerSettings.Default);
        Equal(20, idleHighTgp.GpuWatt, "high-TGP model does not manufacture a 54-watt idle baseline");
        var legacy = SystemPowerEstimator.Estimate(input with { DiscreteGpuNames = ["NVIDIA GeForce RTX 3060"], GpuMeasuredWatt = 52 }, SystemPowerSettings.Default);
        Equal(52, legacy.GpuWatt, "legacy complete GPU aggregate remains compatible");
        var calibrated = SystemPowerEstimator.Estimate(mixedInput, SystemPowerSettings.Default with { Calibration = 1.2 });
        Equal((mixed.CpuWatt + mixed.GpuWatt + mixed.MemoryWatt + mixed.StorageWatt + mixed.BoardWatt + mixed.FanWatt) * 1.2, calibrated.TotalWatt, "total uses each component exactly once and calibration once");
        var storageInput = input with { DriveCount = 1, StorageKinds = [StorageMediaKind.Nvme], StorageBusyPercent = 100 };
        var oneDrive = SystemPowerEstimator.Estimate(storageInput, SystemPowerSettings.Default);
        var extraKinds = SystemPowerEstimator.Estimate(storageInput with { StorageKinds = [StorageMediaKind.Nvme, StorageMediaKind.Hdd] }, SystemPowerSettings.Default);
        Equal(oneDrive.StorageWatt, extraKinds.StorageWatt, "extra media metadata cannot invent another physical disk");
        var sensorMethod = typeof(LibreHardwareMonitorSensorService).GetMethod("IsWholeCardPowerSensor", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var name in new[] { "GPU Core", "GPU Memory", "GPU PPT" })
            Check(!(bool)sensorMethod.Invoke(null, [name])!, "partial GPU domain cannot masquerade as total: " + name);
        foreach (var name in new[] { "GPU Board", "GPU Package", "GPU Total" })
            Check((bool)sensorMethod.Invoke(null, [name])!, "aggregate GPU domain accepted: " + name);
        var tracker = new CumulativeEnergyTracker();
        foreach (var seconds in new[] { double.NaN, double.PositiveInfinity, -1.0, 0.0 })
            Check(!tracker.Accumulate(50, seconds), "nonfinite or nonpositive integration interval rejected");
        Equal(0, tracker.Joules, "invalid intervals cannot poison the energy total");
        InDirectory(Path.Combine(Path.GetTempPath(), "corebusy-power-" + Guid.NewGuid().ToString("N")), "integration", PowerIntegration);
    }

    private static void PowerIntegration()
    {
        var (vm, samples) = Session();
        var valid = new CpuSnapshot { PackagePowerW = 30 };
        Call(vm, "AccumulateSample", samples, valid, SystemHardwareInfo.Empty, 1.0);
        Call(vm, "UpdateEnergyReadout");
        Check(vm.EnergyIsLive && vm.SystemEnergyIsLive, "valid sample makes both energy streams live");
        Call(vm, "AccumulateSample", samples, valid with { PackagePowerW = double.NaN }, SystemHardwareInfo.Empty, 2.0);
        Call(vm, "UpdateEnergyReadout");
        Check(!vm.EnergyIsLive && !vm.SystemEnergyIsLive, "CPU sensor loss marks both energy streams stale");
        Equal(40, Field<CumulativeEnergyTracker>(vm, "_systemEnergy").Joules, "missing CPU sample does not add stale power");
        var dual = valid with
        {
            DiscreteGpuReadings = [new("NVIDIA GeForce RTX 3060", 60, 80), new("NVIDIA GeForce RTX 3060", null, 50)],
        };
        var hardware = SystemHardwareInfo.Empty with { HasGpu = true, GpuName = "NVIDIA GeForce RTX 3060", GpuIsDiscrete = true };
        var inputs = (SystemPowerInputs)typeof(MainWindow).Assembly.GetType("CoreBusy.App.ViewModels.Dashboard.DashboardViewModel")!
            .GetMethod("BuildPowerInputs", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [dual, hardware])!;
        Check(inputs.DiscreteGpuReadings.Count == 2, "snapshot to dashboard mapping preserves per-card missing data");
        Call(vm, "AccumulateSample", samples, dual, hardware, 3.0);
        Call(vm, "UpdateEnergyReadout");
        Equal(235, Field<CumulativeEnergyTracker>(vm, "_systemEnergy").Joules, "multi-GPU estimate integrates through the real dashboard path");
        Check(vm.SystemEnergyHintText.Contains("部分传感器 + 部分估算") && vm.SystemEnergyHintText.Contains("非插座实测"), "real readout explains mixed sources and DC scope");
        Call(vm, "AccumulateSample", samples, valid, hardware, 1000.0);
        Call(vm, "UpdateEnergyReadout");
        Check(!vm.EnergyIsLive && !vm.SystemEnergyIsLive, "sleep-sized gap marks both streams stale");
        Equal(235, Field<CumulativeEnergyTracker>(vm, "_systemEnergy").Joules, "sleep gap adds no energy");
        Call(vm, "AccumulateSample", samples, valid, SystemHardwareInfo.Empty, 1001.0);
        Call(vm, "UpdateEnergyReadout");
        Check(vm.EnergyIsLive && vm.SystemEnergyIsLive, "energy collection resumes after missing sample and sleep gap");
        vm.Stop();
    }
}
