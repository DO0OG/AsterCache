E3 result folder: D:\Git\AsterCache\results\relayout_e3\20261101_225446

Undo (not run by the script; run from an elevated PowerShell in D:\Git\AsterCache):
python -m relayout.cli revert --journal "D:\Git\AsterCache\results\relayout_e3\20261101_225446\journal.jsonl" --allowed-root D:\AsterBench --execute

The relayout only moved the dataset files' data clusters inside D: , the file contents are identical
(SHA-256 manifest compared). The data is safe without undoing; undo only restores the old physical layout.
