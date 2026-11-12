using AsterCache.Core.I18n;
using AsterCache.Core.Logging;
using AsterCache.Tray.Update;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AsterCache.Tray;

/// 트레이 앱의 자동 업데이트 연결: 예약 실행, 메뉴 항목, 새 버전·설치 알림. 업데이트 로직 자체는 AutoUpdater 에 있습니다.
internal sealed partial class App
{
    public static AutoUpdater? Updater { get; set; }

    private CancellationTokenSource? _updateCts;
    private string _updateToasted = string.Empty;

    private void StartUpdater()
    {
        if (Updater is null || Smoke)
        {
            return; // --smoke 는 네트워크에 접근하지 않음
        }

        Updater.StatusChanged += () => Dispatcher.UIThread.Post(OnUpdateStatus);
        _updateCts = new CancellationTokenSource();
        _ = Task.Run(() => Updater.RunAsync(_updateCts.Token)); // 해시 계산·파일 쓰기가 UI 스레드에서 돌지 않게 스레드 풀에서 실행
    }

    private void StopUpdater() => _updateCts?.Cancel();

    private void OnUpdateStatus()
    {
        UpdateStatus s = Updater!.Status;
        string key = $"{s.State}|{s.Version}";
        if (s.State is UpdateState.Available or UpdateState.Installing && _updateToasted != key)
        {
            _updateToasted = key;
            Toast(s.Text(), StatusTone.Busy);
        }
        else
        {
            Rebuild();
        }
    }

    /// 상태 줄 (있을 때), "업데이트 확인", 새 버전이 있으면 "업데이트 설치 (버전)".
    private void AddUpdateItems(NativeMenu menu)
    {
        UpdateStatus s = Updater?.Status ?? UpdateStatus.Idle;
        if (s.State != UpdateState.Idle)
        {
            menu.Items.Add(new NativeMenuItem(s.Text()) { IsEnabled = false });
        }

        menu.Items.Add(Item(L.T("Menu_CheckUpdate"), () => RunUpdate(u => u.CheckNowAsync(CancellationToken.None)), Updater is not null && s.State is not (UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing)));
        if (s.State is UpdateState.Available or UpdateState.WaitingForGame && s.Version is { } version)
        {
            menu.Items.Add(Item(L.T("Menu_InstallUpdate", version), () => RunUpdate(u => u.InstallNowAsync(CancellationToken.None))));
        }
    }

    private async void RunUpdate(Func<AutoUpdater, Task<UpdateStatus>> work)
    {
        try
        {
            await Task.Run(() => work(Updater!)); // 무거운 일은 스레드 풀에서, 상태 표시는 StatusChanged 가 UI 스레드로 넘김
        }
        catch (Exception e)
        {
            // 최상위 경계: 예상하지 못한 예외도 트레이를 끝내지 않고 로그만 남김
            Log.Error("update action failed", e);
        }
    }
}
