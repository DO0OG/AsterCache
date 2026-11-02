namespace AsterCache.Cli.Commands;

/// aster prewarm: `prefetch --execute --method pvm` 의 별칭 (핫 집합을 존 순서로 RAM 에 올림). 다른 옵션은 prefetch 와 같습니다.
internal static class PrewarmCommand
{
    public static readonly ArgOption[] Options = PrefetchCommand.Options;

    public static int Run(ParsedArgs a, TextWriter o)
    {
        a.SetFlag("execute");
        if (!a.Given("method"))
        {
            a.Set("method", "pvm", false);
        }

        return PrefetchCommand.Run(a, o);
    }
}
