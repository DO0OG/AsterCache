namespace AsterCache.Tray;

/// TrayViewModel 의 옛 업데이트 경로가 아직 이 형식을 참조해서 남긴 빈 자리입니다 (항상 지원 안 함). 실제 업데이트는 Update\AutoUpdater 이고 메뉴가 직접 부릅니다.
/// 통합 단계에서 TrayViewModel 의 CheckUpdateAsync·ApplyUpdateAsync·PendingVersion 과 함께 지웁니다.
public sealed class UpdateService
{
    public bool IsSupported => false;

    public Task<string?> CheckAsync() => Task.FromResult<string?>(null);

    public Task DownloadAndApplyAsync(Action<int> progress, CancellationToken cancellationToken) => Task.CompletedTask;
}
