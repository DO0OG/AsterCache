using System.Text;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Volume;
using AsterCache.Windows;

namespace AsterCache.Cli.Commands;

/// relayout/cli.py 의 probe, apply, verify, revert.
internal static class VolumeCommands
{
    public static readonly ArgOption[] ProbeOptions =
    [
        new("volume", Required: true),
        new("plan"),
        new("zone-mib", Type: ArgType.Int, Default: "300"),
        new("require-volume-label"),
    ];

    public static readonly ArgOption[] ApplyOptions =
    [
        new("plan", Required: true),
        new("journal"),
        new("allowed-root", ArgKind.Many, Required: true),
        new("dry-run", ArgKind.Flag),
        new("require-volume-label"),
        new("execute", ArgKind.Flag),
        new("chunk-mib", Type: ArgType.Int, Default: "4"),
        new("write-cap-mib", Type: ArgType.Int, Default: "2048"),
        new("verify", Default: VerifyMode.Sample, Choices: [VerifyMode.All, VerifyMode.Sample, VerifyMode.Off]),
        new("order", Default: RelocateOrder.Target, Choices: [RelocateOrder.Target, RelocateOrder.Source]),
        new("parallel", Type: ArgType.Int, Default: AsterCache.Core.Settings.AutoSettings.DefaultRelocateParallel.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ];

    public static readonly ArgOption[] VerifyOptions = [new("plan", Required: true)];

    public static readonly ArgOption[] RevertOptions =
    [
        new("journal", Required: true),
        new("allowed-root", ArgKind.Many, Required: true),
        new("require-volume-label"),
        new("execute", ArgKind.Flag),
        new("chunk-mib", Type: ArgType.Int, Default: "4"),
        new("write-cap-mib", Type: ArgType.Int, Default: "2048"),
    ];

    private const long Mib = 1L << 20;

    public static int Probe(ParsedArgs a, TextWriter o)
    {
        long? zoneClusters = a.Given("plan") ? (long)PlanFile.Load(a.Text("plan")!).Stats["zone_used_clusters"]! : null;
        using NtfsVolume vol = OpenChecked(a.Text("volume")!, a.Text("require-volume-label"));
        VolumeBitmap bm = vol.Bitmap();
        long free = bm.FreeRuns().Sum(r => r.Length);
        long need = zoneClusters is > 0 ? zoneClusters.Value : a.Int("zone-mib")!.Value * Mib / vol.ClusterBytes;
        return AsterCli.Emit(o, new JObject
        {
            { "volume", vol.Letter },
            { "label", VolumeInfo.Label(vol.Letter) },
            { "cluster_bytes", (long)vol.ClusterBytes },
            { "total_clusters", vol.TotalClusters },
            { "free_clusters_ntfs", vol.FreeClusters },
            { "free_clusters_bitmap", free },
            { "bitmap_bits", bm.NBits },
            { "free_match", Math.Abs(free - vol.FreeClusters) <= Math.Max(4096, vol.TotalClusters / 1000) },
            { "mft_zone", new List<object?> { vol.MftZone.Start, vol.MftZone.End } },
            { "zone_clusters_needed", need },
            { "zone_candidate_lcn", bm.FindFreeRun(need, preferLowLcn: true, align: PlanGuards.ZoneAlign) },
        });
    }

    public static int Apply(ParsedArgs a, TextWriter o)
    {
        bool execute = a.Flag("execute");
        bool dry = a.Flag("dry-run");
        if (execute && dry)
        {
            throw new PyValueException("--execute and --dry-run are exclusive");
        }

        string planPath = a.Text("plan")!;
        PlanFile plan = PlanFile.Load(planPath);
        using NtfsVolume vol = OpenChecked(plan.Volume, a.Text("require-volume-label"));
        string journal = a.Text("journal") is { } j ? PathText.Str(j) : PathText.Str(planPath) + ".journal.jsonl";
        var options = new ApplyOptions(execute, a.Int("chunk-mib")!.Value * Mib, a.Int("write-cap-mib")!.Value * Mib, planPath, dry, Verify: a.Text("verify") ?? VerifyMode.Sample, Order: a.Text("order") ?? RelocateOrder.Target, Parallel: (int)a.Int("parallel")!.Value);
        return AsterCli.Emit(o, Applier.Apply(plan, vol, Roots(a), journal, Hooks(), options).ToJson());
    }

    public static int Verify(ParsedArgs a, TextWriter o)
    {
        LayoutReport report = LayoutVerifier.Verify(PlanFile.Load(a.Text("plan")!), FileLayout.Extents);
        return AsterCli.Emit(o, Verdict(report));
    }

    public static int Revert(ParsedArgs a, TextWriter o)
    {
        string journal = a.Text("journal")!;
        string text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(journal));
        string firstStep = text.ReplaceLineEndings("\n").Split('\n').FirstOrDefault(x => x.Contains("\"type\": \"step\"", StringComparison.Ordinal))
            ?? throw new PyValueException("journal has no steps");
        string path = (JsonRead.Parse(firstStep) as JObject)?["path"] as string ?? throw new PyValueException("journal step has no path");
        using NtfsVolume vol = OpenChecked(PlanFile.SplitDrive(path), a.Text("require-volume-label"));
        var options = new ApplyOptions(a.Flag("execute"), a.Int("chunk-mib")!.Value * Mib, a.Int("write-cap-mib")!.Value * Mib);
        return AsterCli.Emit(o, Reverter.Revert(journal, vol, Roots(a), Hooks(), options).ToJson());
    }

    /// verify 결과에 verdict 를 덧붙입니다 (status 도 씁니다).
    internal static JObject Verdict(LayoutReport report)
    {
        JObject r = report.ToJson();
        r.Add("verdict", report.Contiguous ? "contiguous" : report.ResumeNeeded ? "resume-needed" : "non-contiguous");
        return r;
    }

    // 볼륨을 열고 (관리자만), 요구한 라벨과 다르면 아무것도 건드리지 않고 거부합니다.
    private static NtfsVolume OpenChecked(string letter, string? label)
    {
        NtfsVolume vol = NtfsVolume.Open(letter);
        if (label is not null)
        {
            string found = VolumeInfo.Label(vol.Letter);
            if (found != label)
            {
                vol.Dispose();
                throw new PlanRejectedException($"volume label {PyRepr.Str(found)} != required {PyRepr.Str(label)} on {letter}");
            }
        }

        return vol;
    }

    private static List<string> Roots(ParsedArgs a) => a.List("allowed-root").Select(PathText.Str).ToList();

    private static ApplyHooks Hooks() => new()
    {
        Extents = FileLayout.Extents,
        Digest = (path, size, ranges) => SampleDigest.Compute(path, size, ranges),
        EdgeDigest = (path, size, ranges) => SampleDigest.Compute(path, size, ranges, span: 0),
        SkipReason = path => FileGuards.SkipReason(path),
        RealPath = RealPaths.Real,
    };
}
