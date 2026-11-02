E4 result folder: D:\Git\AsterCache\results\csharp_e4_20261102_111921

Undo (not run by the script; run from an elevated PowerShell, only while Frostpunk is closed).
Preview first (no --execute, moves nothing):
& "D:\Git\AsterCache\csharp\publish\cli\aster.exe" revert --journal "D:\Git\AsterCache\results\csharp_e4_20261102_111921\journal.jsonl" --allowed-root "D:\Frostpunk" --require-volume-label "로컬 디스크"
Then the real undo:
& "D:\Git\AsterCache\csharp\publish\cli\aster.exe" revert --journal "D:\Git\AsterCache\results\csharp_e4_20261102_111921\journal.jsonl" --allowed-root "D:\Frostpunk" --require-volume-label "로컬 디스크" --execute

4단계(probe, apply, verify, SHA 재계산) 중에는 Ctrl+C 를 누르지 마십시오. 이동 도중 중단되면 위 undo 로 저널을 되돌립니다.
트레이스(1단계)를 강제 종료했다면 남은 ETW 세션을 닫으십시오: logman stop AsterCacheTrace -ets

The relayout only moves data clusters of files inside D:\Frostpunk on D: , file contents stay identical (SHA-256 of the planned files compared).
The data is safe without undoing; undo only restores the old physical layout. Without a journal file nothing was moved.
