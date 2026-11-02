S1 undo (not run by the script)
revert: & "D:\Git\AsterCache\csharp\publish\cli\aster.exe" revert --journal "D:\Git\AsterCache\results\csharp_20261102_103146\journal.jsonl" --allowed-root "Z:\AsterBench" --require-volume-label AsterCsTrial --execute
detach: diskpart, select vdisk file="C:\AsterVHDX\cs_trial3.vhdx", detach vdisk
delete the throw-away disk by hand when done: C:\AsterVHDX\cs_trial3.vhdx (the script never deletes it)
