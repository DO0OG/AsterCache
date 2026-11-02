Undo (NOT run by the script; elevated PowerShell, only while Frostpunk is closed). Preview first:
& "<scratch>\e6cli\aster.exe" revert --journal "D:\Git\AsterCache\results\cold_limit_20261102_155900\journal.jsonl" --allowed-root "D:\Frostpunk" --require-volume-label "로컬 디스크"
Real undo:
& "<scratch>\e6cli\aster.exe" revert --journal "D:\Git\AsterCache\results\cold_limit_20261102_155900\journal.jsonl" --allowed-root "D:\Frostpunk" --require-volume-label "로컬 디스크" --execute
