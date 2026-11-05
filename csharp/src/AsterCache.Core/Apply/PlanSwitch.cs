using System.Globalization;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;

namespace AsterCache.Core.Apply;

/// Commit 이 끝낸 교체의 결과. ArchivedJournal: 보관한 저널 경로 (보관할 저널이 없었으면 null), Resumed: 중단된 교체를 이어서 끝냈는가.
public sealed record SwitchResult(string? ArchivedJournal, string NewPlanSha, bool Resumed);

/// 활성 계획 교체의 내구 절차: 저널 검증 → 후보 준비·검증 → 복구 기록 저장 → 저널 보관 → 후보 승격 → 기록 제거.
/// 각 단계는 다시 해도 같은 결과라서, 어느 단계 뒤에 멈춰도 <see cref="Resume"/> 이 기록대로 끝까지 진행합니다. 저널은 지우지 않고 항상 옮깁니다 (보관).
/// 코드가 지우는 것은 자기 복구 기록 (.switch) 과 승격을 마친 후보 (.next) 뿐입니다.
public static class PlanSwitch
{
    public const string RecordSuffix = ".switch";
    public const string NextSuffix = ".next";

    /// <저널>.<yyyyMMddTHHmmssZ>, 이미 있으면 .1, .2 ... 를 붙여 고유 이름.
    public static string ArchiveName(string journalPath, DateTime utc)
    {
        string stamp = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string name = $"{journalPath}.{stamp}";
        for (int n = 1; File.Exists(name); n++)
        {
            name = $"{journalPath}.{stamp}.{n}";
        }

        return name;
    }

    /// 후보를 활성 계획으로 바꿉니다. 저널에 행이 있고 후보의 header 가 아니면 보관 전에 hooks 로 미검증 sha_before 를 검증합니다 (불일치·읽기 불가면 PlanRejectedException, 아무것도 바꾸지 않음).
    /// 이전에 중단된 교체가 있으면 먼저 끝냅니다. savePlan 은 계획을 쓰는 방법 (기본 plan.Save). checkpoint 는 단계를 마칠 때마다 단계 이름으로 불림 (시험이 여기서 중단을 흉내).
    public static SwitchResult Commit(PlanFile candidate, string planPath, string journalPath, Func<DateTime> utcNow, Action<PlanFile, string>? savePlan = null,
        ApplyHooks? hooks = null, Action<string>? checkpoint = null)
    {
        _ = Resume(planPath, journalPath, savePlan, checkpoint);
        string sha = candidate.Sha();
        (bool hasRows, string? journalSha) = ReadJournal(journalPath);
        string? archive = null;
        if (hasRows && journalSha != sha)
        {
            _ = Applier.VerifyPending(journalPath, hooks ?? throw new ArgumentException("hooks are required to verify the journal being archived"));
            archive = Path.GetFileName(ArchiveName(journalPath, utcNow()));
        }

        checkpoint?.Invoke("verified");
        string next = planPath + NextSuffix;
        candidate.Save(next);
        if (PlanFile.Load(next).Sha() != sha)
        {
            throw new InvalidDataException("candidate plan differs after saving");
        }

        checkpoint?.Invoke("prepared");
        var rec = new JObject
        {
            { "version", 1L },
            { "prev_plan_sha", ShaOf(planPath) },
            { "journal_plan_sha", journalSha },
            { "archive", archive },
            { "candidate_sha", sha },
        };
        string tmp = planPath + RecordSuffix + ".tmp";
        DurableFile.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(PyJson.ToCrlf(PyJson.Dumps(rec, indent: 1))));
        File.Move(tmp, planPath + RecordSuffix, true);
        checkpoint?.Invoke("recorded");
        return Finish(archive, sha, planPath, journalPath, savePlan, checkpoint, resumed: false);
    }

    /// 중단된 교체가 있으면 기록대로 끝까지 진행하고 결과를, 없으면 null 을 돌려줍니다.
    public static SwitchResult? Resume(string planPath, string journalPath, Action<PlanFile, string>? savePlan = null, Action<string>? checkpoint = null)
    {
        string recPath = planPath + RecordSuffix;
        if (!File.Exists(recPath))
        {
            return null;
        }

        JObject rec = JsonRead.ReadFile(recPath) as JObject ?? throw new InvalidDataException("malformed plan switch record");
        string sha = rec.TryGetValue("candidate_sha", out object? s) && s is string cs ? cs : throw new InvalidDataException("plan switch record has no candidate_sha");
        string? archive = rec.TryGetValue("archive", out object? a) ? a as string : null;
        return Finish(archive, sha, planPath, journalPath, savePlan, checkpoint, resumed: true);
    }

    // 기록 뒤의 단계: 저널 보관 (이미 옮겼으면 건너뜀) → 후보 승격 (다시 써도 같음) → .next 와 기록 제거. 보관이 실패하면 예외가 그대로 올라가고 계획·저널은 그대로.
    private static SwitchResult Finish(string? archive, string sha, string planPath, string journalPath, Action<PlanFile, string>? savePlan, Action<string>? checkpoint, bool resumed)
    {
        string? archived = null;
        if (archive is not null)
        {
            archived = Path.Combine(Path.GetDirectoryName(journalPath) ?? string.Empty, archive);
            if (File.Exists(journalPath) && !File.Exists(archived))
            {
                File.Move(journalPath, archived);
            }
        }

        checkpoint?.Invoke("archived");
        string next = planPath + NextSuffix;
        if (File.Exists(next))
        {
            PlanFile candidate = PlanFile.Load(next);
            if (candidate.Sha() != sha)
            {
                throw new InvalidDataException("candidate plan does not match the switch record");
            }

            (savePlan ?? ((p, path) => p.Save(path)))(candidate, planPath);
            checkpoint?.Invoke("promoted");
            File.Delete(next);
        }
        else if (ShaOf(planPath) != sha)
        {
            throw new InvalidDataException("switch record has no candidate plan to promote");
        }

        checkpoint?.Invoke("next_removed");
        File.Delete(planPath + RecordSuffix);
        return new SwitchResult(archived, sha, resumed);
    }

    // 저널에 행이 있는가와 그 header 의 plan_sha.
    private static (bool HasRows, string? Sha) ReadJournal(string journalPath)
    {
        if (!File.Exists(journalPath))
        {
            return (false, null);
        }

        using var jr = new Journal(journalPath);
        JObject? header = jr.Rows.FirstOrDefault(r => Equals(r["type"], "header"));
        return (jr.Rows.Count > 0, header is not null && header.TryGetValue("plan_sha", out object? v) ? v as string : null);
    }

    private static string? ShaOf(string planPath)
    {
        try
        {
            return PlanFile.Load(planPath).Sha();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }
}
