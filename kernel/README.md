**English** | [한국어](README.ko.md)

# kernel/ -- AsterFlt minifilter PoC (K1 observation)

> **Status.** The minifilter has been through source, build, static analysis, and INF verification. It has **not been signed, loaded, or tested on real hardware, and it is not part of the release.** Automatic warm-up right after boot is handled by the user-mode service, not by this driver.

The design rationale is in section 2 of `docs/design_v1_relayout.md` (privilege separation, no interception) and section 4 (safety design), and in sections B (relocation API) and C (ETW limits) of the prior-art survey `docs/prior_art_and_windows_api.md`. Those research notes are written in Korean.

## 1. Purpose and scope

| Stage | Content | Status |
|---|---|---|
| K1 observation | In Pre-Read, **only records** `IRP_MJ_READ` for files under a given root on a given volume (file ID, offset, length, paging / non-cached / fast I/O flags, requesting PID, time) | Source written (this folder). Compile, static analysis, and INF verification confirmed (section 7). Not loaded or run |
| K2 comparison | Inside a VM, compare the K1 trace with an ETW (Tracer) trace of the same replay | Procedure document only (section 5) |
| K3 SSD cache redirection | Complete non-cached and paging reads that hit the policy table from an SSD copy | Design document only (section 6). No code |

Why K1: ETW needs administrator rights and a session, it is uncertain whether cache hits and fast I/O are included in its events (survey C1), and latency can be around one second. A driver sees the same reads directly at the IRP stage. However, "no interception" is the core advantage of the v1 design, so K1 **only observes** and does not modify IRPs.

What the driver does not do: replace or complete reads, observe writes/deletes/renames, access file contents, load automatically (StartType=3, manual), or interpret plans (it only receives the policy computed by the user-mode service).

## 2. Files

| File | Content |
|---|---|
| `AsterFlt/AsterFlt.c` | DriverEntry, registration, InstanceSetup (volume GUID), PostCreate (file ID / root match -> stream context), Pre-Read, ring buffer, communication port, FilterUnload |
| `AsterFlt/inc/asterflt_proto.h` | Message structures shared with user mode (fixed size, 8-byte aligned, static size checks) |
| `AsterFlt/AsterFlt.inf` | Installation information. Temporary altitude 365990, **must be assigned by Microsoft before distribution** |
| `AsterFlt/AsterFlt.vcxproj` | WDM-based minifilter, x64, signing off, NuGet WDK references |
| `AsterFlt/packages.config` | Pins the NuGet WDK packages (`Microsoft.Windows.WDK.x64`, `SDK.CPP`, `SDK.CPP.x64` 10.0.26100.6584) |
| `build.ps1` | Restore -> msbuild -> infverif in one go. `-Analyze` includes static analysis |
| `toolset-shim/` | Replacement for the `WindowsKernelModeDriver10.0` toolset, used only when Visual Studio lacks the "Windows Driver Kit" component |
| `.gitignore` | Excludes `x64/`, `packages/`, `*.sys`, `*.pdb`, `*.cer`, and so on |

## 3. Interface (one communication port, three message types)

Port name `\AsterFltPort`, one connection, security descriptor `FltBuildDefaultSecurityDescriptor(FLT_PORT_ALL_ACCESS)` (Administrators and SYSTEM). User mode uses only `FilterConnectCommunicationPort` + `FilterSendMessage` request-response (the driver never pushes first).

| Command | Request | Response |
|---|---|---|
| `ASTER_CMD_SET_POLICY` | Volume GUID (`{...}`), volume-relative root (`\Games\Foo`), Arm 0/1 | None |
| `ASTER_CMD_GET_RECORDS` | Command header | `ASTER_BATCH_HEADER` + N records (removed from the ring as they are taken) |
| `ASTER_CMD_GET_STATS` | Command header | `ASTER_STATS` (total records, total drops, pending count, generation) |

Behavior rules
- The policy turns itself off when the connection is closed or the driver is unloaded (observation stops).
- Changing the policy bumps the generation, so stream contexts already created are treated as stale, and the new policy applies **from files opened afterward**. Reads on handles that were already open before the policy was set are not recorded (the name is looked up only in PostCreate).
- The ring buffer holds 65536 records (40 bytes each, about 2.5 MiB of non-paged pool). When full, new records are dropped and only the drop count is kept.
- For volume comparison, InstanceSetup stores the GUID in the instance context, and attachment is refused if it differs when the policy is already on. If the policy is turned on later, the filter is already attached to other NTFS volumes and PostCreate filters them out by GUID (the cost on the read path is one failed context lookup).
- The file ID is NTFS's 64-bit file reference number (`FileInternalInformation`). The filter does not attach to file systems other than NTFS.
- Files opened by kernel-mode requests (`RequestorMode == KernelMode`) are not observed.

## 4. Risks and what is never done

A file system filter is kernel code that sits in every I/O path. One buggy line can cause a BSOD, data corruption, or an unbootable system, and anti-cheat software (Ricochet, Vanguard, and so on) may be suspicious of unknown kernel drivers. The usage records of playing a Vanguard game (League of Legends) and a Ricochet game (Call of Duty) on this PC cover only the user-mode service. This driver has never been loaded, so its relation to anti-cheat has not been checked ([Anti-cheat compatibility](../docs/ANTICHEAT.md#usage-records-on-this-pc)).

**Never do these on the development / gaming PC**
1. Any `bcdedit` change (including testsigning, nointegritychecks, debug, hypervisorlaunchtype).
2. Changing Secure Boot, BIOS/UEFI, memory integrity (HVCI), or VBS settings.
3. Loading or registering the driver: `fltmc load`, `sc create ... type= kernel` or `type= filesys`, `pnputil /add-driver`, right-click INF install, `rundll32 setupapi` install, copying to `System32\drivers`.
4. Turning on Driver Verifier (`verifier`) on the host.
5. Putting certificates into the Root or TrustedPublisher stores, or signing with a host certificate using `signtool`.
6. Turning Hyper-V or Windows features on or off, or stopping or changing anti-cheat services.
7. Installing the WDK or downloading the EWDK without user confirmation.

On this PC only source editing and building (`msbuild`) are allowed; the resulting `.sys` is not signed and is excluded from the repository by `.gitignore`. An unsigned `.sys` is refused outright on a normally booted system (the load is still not attempted).

## 5. VM procedure (K2, run by the user after the user decides)

This assumes an environment where the host is not changed. Turning Hyper-V on is itself a host change, so the user decides first, and using another PC is recommended if one is available.

1. Prepare a separate VM (Windows 11 x64, with one extra NTFS volume for testing). Do not share game data from the host to the VM; use synthetic data.
2. Turn Secure Boot off in the VM settings.
3. Turn test signing on **only inside the VM** (`bcdedit /set testsigning on`, reboot). Never on the host.
4. Create a VM checkpoint.
5. Copy the built `AsterFlt.sys`/`AsterFlt.inf` into the VM, sign with a test certificate inside the VM, and run `fltmc load AsterFlt`.
6. Turn on Driver Verifier inside the VM (targeting `AsterFlt.sys`, reboot).
7. After setting the policy, run the same replay (for example, the same synthetic data as `D:\AsterBench`) and collect the K1 trace and the ETW trace at the same time.
8. Comparison items: read count, whether cache hits and fast I/O are included (`ASTER_REC_FASTIO`), offset and length agreement, time delay (relative to ETW), ring drop count.
9. Unload tests: `fltmc unload` while a client is connected (must be refused), unload after disconnecting (succeeds), unload under load.
10. `fltmc unload AsterFlt`, then revert to the checkpoint.

## 6. K3 design (no code; only after the K1 and K2 results and the user's decision)

Start conditions: from the E4/E5 measurements, either (1) long-tail misses outside the hot zone account for most of the load time, or (2) in concurrent mode the game outruns the stream and shakes the head, while rate control based on ETW latency would be too late. It is not built until these conditions are measured.

- Policy table: (volume GUID, file ID, VCN range, SSD copy location). The service computes it and sends it down the communication port; the driver does not interpret the plan.
- Reads it catches: only non-cached and paging reads. Pre-Read looks up the table, and on a match reads from the SSD copy with `FltReadFile`, fills the user buffer, and returns `FLT_PREOP_COMPLETE`. If it cannot fill the buffer or things do not line up, it sends the read down the original path (`SUCCESS_NO_CALLBACK`).
- Consistency: when a write, truncation (set EOF/allocation), rename, delete, or FSCTL_MOVE_FILE arrives, that file's policy is invalidated immediately. The race between invalidation and an in-flight redirection is serialized with a per-file push lock / refcount.
- Cache and oplock: cached reads are left alone. The SSD copy is discarded on oplock break or file change. MDL reads (`IRP_MN_MDL`) and paging reads must state explicitly how each buffer kind is handled (system buffer mapping or MDL).
- On failure it always passes the read down to the original disk path, so data correctness never depends on the SSD copy (a copy is valid only when it matches the verified hash).
- The risk is one step higher than K1: data corruption becomes possible, so it is not tested without a VM, synthetic data, and hash comparison.

## 7. Build

The WDK is not installed; it is fetched as NuGet packages (`Microsoft.Windows.WDK.x64` + `SDK.CPP` + `SDK.CPP.x64`, 10.0.26100.6584) into `kernel\packages\` (no administrator rights needed, about 1.4 GB extracted, excluded from git). Visual Studio 2022 with the C++ workload and `nuget.exe` are required.

```
powershell -File kernel\build.ps1 -Configuration Release -Analyze
```

What `build.ps1` does: `nuget restore` -> `MSBuild.exe` (64-bit) -> `infverif /u` (stops on failure) -> `infverif /w` (informational). To do the same by hand, run `nuget restore AsterFlt\packages.config -PackagesDirectory packages`, then `msbuild AsterFlt\AsterFlt.vcxproj /p:Configuration=Release /p:Platform=x64` (for static analysis add `/p:RunCodeAnalysis=true`). With 32-bit MSBuild, the WDK tools are looked up in the x86 folder and ApiValidator and InfVerif fail, so use `MSBuild\Current\Bin\amd64\MSBuild.exe`.

Even the NuGet WDK needs Visual Studio's "Windows Driver Kit" component (platform toolset `WindowsKernelModeDriver10.0`) for the vcxproj to load. That component writes into the VS install folder, so it needs administrator rights, and this PC does not have it. So only when the component is missing, `build.ps1` copies VS's `MSBuild\Microsoft\VC\v170` folder (about 4 MB) to `kernel\packages\vctargets\`, lays `toolset-shim\` over it, and points to it with `/p:VCTargetsPath` (the VS installation is not touched). When the component is present, the shim is not used. The shim fills in only path properties that the NuGet packages do not define, such as `KM_IncludePath` and `DDK_LIB_PATH`, so whether the result equals that of the real component must be checked separately on a PC that has the component.

Output goes under `kernel\AsterFlt\x64\<configuration>\`; it is not signed and not loaded. No catalog (.cat) is produced either (`SignMode=Off`, `EnableInf2cat=false`).

Build status (2026-11-02, Visual Studio 2022 MSVC 14.44.35207, WDK/SDK 10.0.26100.6584, Windows 11 26200 x64):

| Item | Result |
|---|---|
| Release\|x64 build (`/W4 /WX`) | Succeeded, 0 warnings, 0 errors. `AsterFlt.sys` 13,312 bytes |
| Debug\|x64 build | Succeeded, 0 warnings, 0 errors. `AsterFlt.sys` 13,824 bytes |
| Static analysis (`RunCodeAnalysis`, `DriverRecommendedRules.ruleset`, warnings as errors) | 0 warnings in both Release and Debug (before the fix, `DriverMinimumRules` gave 2 C6001; see below) |
| ApiValidator (Universal) | Passed (`Driver is 'Universal'`) |
| `infverif /v /u` (target platform Universal) | `INF is VALID` |
| `infverif /v /w` (Windows Driver rules) | Failed: 5 errors 1323 (`DebugFlags`, `SupportedFeatures`, and `DefaultInstance`, `Altitude`, `Flags` under `Instances` are not subkeys of Parameters), 1 error 1322 (destination path is DIRID 12, not the isolated DIRID 13) |
| vcxproj properties | `SignMode=Off`, `EnableInf2cat=false`, `Driver_SpectreMitigation=false` are all values defined in the NuGet WDK's property definitions (`Sign.xml`, `Inf2cat.xml`, `drivercl.xml`), and the real build was confirmed to produce no signing, catalog, or `/Qspectre` |

Fixed in the first build
- `asterflt_proto.h`: `ASTER_PORT_NAME` was `L"\AsterFltPort"`, which gave C4129 (unknown escape) -> `L"\\AsterFltPort"`. Left as it was, the port name would have been garbled and communication port creation would have failed.
- `AsterFlt.c`: the output buffer SAL of `AsterDoGetRecords`/`AsterDoGetStats` was changed to `_Inout_updates_bytes_(OutLen)`, and the `ProbeForWrite` length got an `OutLen` bound (removes 2 static analysis C6001; `n` was already limited by `maxFit`, so behavior is unchanged).
- `AsterFlt.c`: `AsterDisconnect`/`AsterUnload` (PAGED_CODE) took the spin lock directly, which raised driver analysis C28150 (IRQL ceiling exceeded) -> moved into the non-paged function `AsterDisarm()`. This rule was not visible in the first local analysis because `toolset-shim` was missing the analysis plug-in (`WindowsPrefast.dll`); it was found in CI (after the shim fix it reproduces and passes locally).
- `AsterFlt.inf`: `CopyFiles` and `DelFiles` pointed to the same section, giving warning 1205 -> split out a separate deletion section `MiniFilter.DelFiles`.
- `AsterFlt.vcxproj`: imported the NuGet package props, pinned `WindowsTargetPlatformVersion` to `10.0.26100.0`, and set the tools folder of the NuGet WDK (x64 only) to x64.

Unresolved (decision needed)
- `infverif /w` (driver isolation) 1322 and 1323: 1322 can be fixed by changing the INF to DIRID 13 (run from the driver store), but that changes how installation and removal work, so it was not changed before the VM tests. 1323 is inherently not fixable by moving the minifilter `Instances` key under Parameters, since FltMgr reads it directly under the service key, so whether this rule is waived must be checked in Microsoft documentation when doing formal signing (the WHQL path). With the current target platform setting (Universal), `/u` passes.
- A build in an environment with the real `WindowsKernelModeDriver10.0` component has not been tried on this PC (CI is the first chance to confirm).

## 8. Source review checklist (self-review of code written without the WDK)

| Item | Check result | What to look at again in the first build / VM |
|---|---|---|
| Pre-Read IRQL | No name lookup, paged pool, or waiting. Uses only `FltGetStreamContext` (<= DISPATCH), the spin lock, and `KeQueryInterruptTime` | Re-check the documented IRQL ceiling of `FltGetRequestorProcessId` (currently called only at APC level or below; otherwise the PID-unknown flag is set) |
| Pre-Read return | Every path returns `FLT_PREOP_SUCCESS_NO_CALLBACK`; no IRP modification or completion | - |
| PostCreate IRQL | `FltGetFileNameInformation` requires PASSIVE. PostCreate is assumed to run in the requesting thread context | Re-check the documented post-op IRQL guarantee for IRP_MJ_CREATE |
| Pool types | Ring buffer: `ExAllocatePool2(POOL_FLAG_NON_PAGED)`. Context: `NonPagedPoolNx`. Temporary GUID buffer: paged, used only at PASSIVE and then freed | Minimum target Windows 10 2004 (`ExAllocatePool2`) |
| Release paths | In InstanceSetup `Cleanup:`, the context reference and temporary pool are released; in PostCreate `Done:`, stream/name/instance references are released; when DriverEntry fails, ring, filter, and port are cleaned up in reverse order | Driver Verifier pool tracking |
| Ring buffer concurrency | Producer and consumer both update Head/Tail inside the same spin lock. User buffer copy is outside the lock. `Draining` guarantees a single consumer | Whether Dropped + Pending add up in a load test |
| User buffers | Both input and output use `ProbeForRead/Write` + `__try`. Length, version, alignment, and GUID/root format are validated | Fuzzing of bad input belongs in the VM |
| Disconnect | `AsterDisconnect` turns the policy off, bumps the generation, and calls `FltCloseClientPort`. One connection | - |
| Unload race | A non-forced unload is refused while a client is attached. Close the server port -> turn the policy off -> wait for rundown -> `FltUnregisterFilter` -> only then free the ring | In a late Disconnect during forced unload, the client port cannot be closed if `g.Filter` is NULL (only a guard exists). Check in the VM |
| stream context | Not created for files outside the root. A generation mismatch is ignored | How contexts are shared across different handles that open the same FCB |
| Known limits | Files opened before the policy was set, kernel-mode requests, and non-NTFS are not observed | Check the miss rate in the K2 comparison |
| Compile, static analysis, INF verification | Confirmed on 2026-11-02 (table in section 7): `/W4 /WX` 0 warnings, static analysis 0 warnings, `infverif /u` VALID. vcxproj properties (`SignMode`, `EnableInf2cat`, `Driver_SpectreMitigation`) actually verified | `infverif /w` 1322 and 1323 (unresolved in section 7). Run and load behavior only in the VM |
