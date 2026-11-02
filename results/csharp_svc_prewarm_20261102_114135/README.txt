Service prewarm live test (administrator). cs_service.ps1 install with this config (plans copied from results/csharp_e4_20261102_111921), 75 s wait, then uninstall -PurgeData.
Prewarm on service start streamed the 2 GiB zone of D:\Frostpunk; the data was already in RAM from the preceding comparison run, so 0.22 s is a warm number (cold prefetch of the same zone measured 15.0-15.2 s).
The 'layout degraded (zone hit 10.5%)' warning is a false positive of the reapply checker on a plan with preserved segments; fixed afterwards (see the next commit).
