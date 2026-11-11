[English](AUTO_MODE.md) | **한국어**

# 자동 모드

> README 의 [시작하기](../README.ko.md#시작하기)에서 이어지는 상세 설명입니다. 서비스가 자동으로 하는 일의 단계, 지원 런처, 설정 키가 들어 있습니다.

자동 모드는 기본으로 켜져 있습니다. 보통은 게임을 등록하거나 따로 설정할 필요가 없습니다. 각 부분은 설정 탭의 **자동 모드** 묶음이나 `config.json` 에서 끌 수 있습니다. 여기서는 설정 이름만 적고, 뜻과 기본값은 [CONFIGURATION.ko.md](CONFIGURATION.ko.md)에 있습니다. 접힌 블록(눌러서 펼침)에 조건, 예외, 수치가 들어 있습니다.

## 단계 한눈에 보기

| 단계 | 한 줄 설명 |
|---|---|
| 1. 발견 | 런처 라이브러리에서 HDD 게임을 찾아 등록합니다. |
| 2. 읽기 기록 | 게임이 로딩 중에 읽는 것을 기록합니다(트레이스). |
| 3. 재배치 | PC 가 한가할 때 게임이 읽는 조각을 한 구역에 모읍니다. |
| 4. 예열 | 게임을 켜기 전에 그 구역을 RAM 에 읽어 둡니다. |
| 5. 유지 | 재예열, 보충, 재시도, 배치 재검사를 합니다. |
| 6. 게임 실행 중 | 디스크를 게임에 양보하고, 디스크가 어떻게 움직였는지 기록합니다. |
| 7. 드라이브 회복 대기 | 대량 쓰기 뒤 SMR 드라이브가 안정되기를 기다립니다. |

"한가함"은 [한가함의 뜻](#한가함의-뜻)에 정의돼 있습니다.

## 첫 실행 전에 쓰는 자료

기록이 없는 게임은 공유 프로필, Windows 프리페치 기록, 엔진 프로필, 폴더 전체 중 해당하는 첫 번째 것으로 처음 배치 정보를 얻습니다. 첫 실제 트레이스가 생기면 그것이 대신합니다.

<details>
<summary>세부: 네 가지 자료</summary>

첫 실행에서 실제 기록(트레이스)이 생기면 그것으로 만든 계획이 앞의 계획을 대신하며 같은 존 위치를 먼저 시도하고, 옛 계획은 `.stale` 로 남깁니다.

- **공유 프로필** (`auto.shared_profiles`, 기본 켬). 주 1회 공유 프로필 묶음 zip 하나를 HTTPS 로 받고, Steam 앱 ID와 디포 매니페스트 ID, Epic 앱 이름과 버전, 또는 exe 크기와 표본 SHA-256 으로 게임을 찾습니다.
- 이 PC 의 파일 크기와 표본 해시가 맞을 때만 씁니다. 오프라인이거나 404 면 조용히 건너뜁니다. 내려받기는 64 MiB 로 제한합니다. 공유 저장소는 아직 준비 중입니다.
- **Windows 프리페치 기록** (`C:\Windows\Prefetch\*.pf`). SysMain 서비스가 돌고 `EnablePrefetcher` 의 응용 프로그램 비트가 켜져 있을 때 씁니다. 서비스는 게임의 `.pf` 파일만 읽습니다.
- 설치 폴더 안의 파일로 부트 트레이스를 만들며 "미리 읽지 않음" 표시가 있는 파일은 뺍니다. 기록이 참조한 범위와 그 앞뒤 64 MiB(겹치면 합침, 파일 크기 상한 없음)를 물리 순서로 구역에 모읍니다.
- 구역이 플레이에서 읽은 양의 30% 미만만 덮으면 상태 detail 에 `partial (N%)` 로 표시합니다. 관리 창에는 "재배치됨(프리페치)"로 표시됩니다.
- **엔진 프로필** (Unity, Unreal, Source). 기록 없이 예열합니다.
- **폴더 전체.** 폴더가 RAM 예산 안에 들면 기록 없이 예열합니다.

</details>

## 단계

### 1. 발견

- **언제.** 서비스가 시작한 10초 뒤와 그 뒤 1시간마다.
- **무엇을.** [지원 런처](#지원-런처)를 읽어 HDD 게임을 `"source": "auto"` 로 등록합니다. 시작 뒤 많이 읽는 프로세스는 [휴리스틱](#휴리스틱-감지)으로 잡습니다.
- **하지 않는 것.** SSD 게임, 상주 유틸리티, 직접 등록한 게임과 같은 폴더의 게임은 등록하지 않습니다. Windows 드라이브의 게임은 예열만 합니다.
- **설정.** `auto.discover`, `auto.ignore_exes`.
- **로그.** `auto <런처>: registered <이름> (<exe>)`

<details>
<summary>세부: 상한, 건너뛰는 경우, 상주 유틸리티</summary>

**상한과 건너뛰기**

- 게임 목록 상한은 32개입니다. 가득 차면 런처 발견은 멈추고, 실제로 실행되어 잡힌 게임이 가장 오래 쓰지 않은 자동 항목을 밀어냅니다.
- 볼륨 레이블이 비었거나 32자를 넘으면(로그 한 줄), 같은 exe 이름이나 겹치는 폴더가 이미 등록되어 있으면 건너뜁니다.
- SSD 의 게임은 자동 등록·트레이스·재배치 대상이 아닙니다. Windows 가 있는 드라이브가 HDD 이면 그 게임은 발견과 예열만 합니다(상태 "prewarm only (system drive)").
- 관리 창에서 지운 게임은 exe 가 `auto.ignore_exes` 에 들어가 다시 자동 등록되지 않습니다(최대 64개, 넘으면 오래된 것부터 빠짐).
- 다른 로그 줄: `auto <런처>: skipped <이름>: volume has no usable label`, `auto discovery: game list is full, skipping the rest`.

**상주 유틸리티**

- 게임이 아니고 한 번 띄우면 계속 켜져 있는 데스크톱 유틸리티(Wallpaper Engine, YoloMouse, Lossless Scaling, OBS Studio, SteamVR 등)는 자동 등록하지 않습니다.
- 고정된 Steam appid·exe 이름 목록이 런처 발견과 휴리스틱 양쪽에서 이들을 제외합니다. 이 목록은 `auto.ignore_exes` 와 별개입니다.
- 이미 자동 등록된 항목은 서비스가 시작된 뒤 실행 중이어도 한 번 등록 해제합니다(파일을 옮기는 중이면 다음 발견 때). 직접 등록한 항목은 건드리지 않습니다.
- 이미 옮긴 파일은 그대로 둡니다. SSD 보조 저장(`auto.ssd_tier`)을 쓰고 있었다면 등록이 사라진 게임의 링크는 되돌립니다. 등록이 사라진 게임은 예열이나 고정도 하지 않습니다.
- 로그: `auto: unregistered <게임> (resident utility, not a game)`.

</details>

### 2. 읽기 기록(트레이스)

- **언제.** 트레이스가 없는 등록 게임이 처음 시작될 때(감지한 실행은 이미 로딩이 시작된 뒤라 다음 실행부터 기록). 몇 번의 플레이에 걸쳐 되풀이합니다.
- **무엇을.** 실시간 ETW 세션으로 게임의 읽기를 기록해 `plans\<이름>.trace.json` 에 저장합니다. 수동 **트레이스 수집** 버튼과 같은 파일입니다.
- **하지 않는 것.** 안티치트가 금지한 게임은 기록하지 않고([ANTICHEAT.ko.md](ANTICHEAT.ko.md)), 게임 프로세스는 열지 않습니다. ETW 로만 관찰합니다.
- **설정.** `auto.trace`, `trace_cap_seconds`, `trace_cap_mib`, `trace_runs`, `trace_merge_min_mib`, `trace_late_*`.
- **로그.** `<게임>: auto trace started (run 2/3)`, `<게임>: auto trace ended (<사유>), N read events saved`

<details>
<summary>세부: 상한, 여러 번의 플레이, 존 확장, 첫 실행 학습</summary>

**기록**

- 세션 이름은 `AsterCacheAutoTrace` 입니다. 게임 종료, 1800초(`trace_cap_seconds`), 6144 MiB와 존 상한의 2배 중 큰 값(`trace_cap_mib`) 중 먼저 오는 때 멈춥니다.
- 상한에 닿았는데 게임이 아직 실행 중이면 정리·저장·병합은 게임이 끝난 뒤에 합니다.
- 트레이스에는 디스크까지 간 읽기만 기록되므로 이미 캐시에서 나간 읽기는 빠질 수 있고, 세션이 시작되기 전의 첫 읽기 일부도 빠질 수 있습니다.

**추가 창**

- 첫 창이 끝났는데도 게임이 실행 중이면 `auto.trace_late_interval_min`분마다 `auto.trace_late_window_seconds`초 동안 다시 수집하며, 최대 `auto.trace_late_windows`회입니다.
- 읽기 상한은 세션 누적에 적용합니다.

**여러 번의 플레이**

- 이후 플레이도 `trace_runs`번이 모일 때까지 같은 방식으로 기록해 `plans\<이름>.trace.runN.json` 으로 저장합니다.
- `trace.json` 은 그 블록들의 합집합(첫 번째 플레이의 접근 순서 뒤에 이후 플레이에서 처음 본 블록)으로 한 번에 바꿔 씁니다. 그동안 상태 열에 `trace 2/3` 처럼 표시합니다.
- run 파일 번호 상한에 닿으면 가장 오래된 회차를 바꿔 쓰고 병합본 블록은 유지합니다.
- 병합 증가량이 기준에 못 미쳐 수집을 멈춘 뒤에도, 마지막 유효 회차 이후 5회 실행, 7일 경과, 게임 업데이트 감지 중 하나라도 있으면 한 번 더 수집합니다.

**존 확장**

- 병합한 핫 집합이 계획보다 `trace_merge_min_mib` 이상 커졌고 게임이 꺼져 있으면 존을 제자리에서 확장합니다.
- 이미 존에 있는 블록은 옮기지 않고 새 블록만 뒤에 붙이며, `max_zone_mib` 안에서 앞선 플레이의 블록을 우선하고, 유휴·일일 상한·쓰기 예산 검사는 일반 재배치와 같습니다.
- 존 뒤가 비어 있지 않으면 확장을 건너뛰고 이전 계획을 유지하며, 새 플레이로 트레이스가 바뀔 때만 다시 시도합니다. 로그: `<게임>: zone extension blocked: ...`.

**첫 실행 학습(실험 기능, 기본 꺼짐)**

- 트레이스를 기록하는 동안 서비스는 선행 읽기를 할 수 있습니다. 기본으로 꺼져 있습니다(`auto.learn.enabled` 가 `false`, E7~E7f 측정에서 이득 없음, [RESULTS_SUMMARY.ko.md](RESULTS_SUMMARY.ko.md#e7-첫-실행-선행-읽기) 참고).
- 켜면 게임이 파일을 순차로 읽는 경우에만 읽습니다. 직전 읽기 끝과 간격 4 MiB 이하로 앞으로 이어지는 읽기가 6회 연속이고 그 파일을 8 MiB 이상 읽었으면 순차로 판정합니다.
- 그런 파일은 게임 읽기의 틈을 기다리지 않고 현재 위치부터 16 MiB 단위로 `auto.learn.window_mib` 까지 앞서 읽습니다(`auto.learn.mode` `"stream"`, 기본). `"gap"` 이면 게임 읽기가 100 ms 동안 조용할 때만 1 MiB 씩 읽습니다.
- 읽기는 서비스 자기 프로세스에서 하며 읽기 스레드는 1개, 선행 읽기 I/O 는 낮은 우선순위, 방금 읽힌 파일부터입니다. 게임이 배타적으로 연 파일은 건너뜁니다.
- 총량은 게임 읽기량 × 1.5 + 64 MiB 를 넘지 않고, `auto.learn.max_total_mib` 와 가용 RAM 여유(`min_free_ram_mib`)를 지킵니다. HDD 이면서 시스템 드라이브가 아닌 드라이브의 게임만 대상입니다.
- `"bounded"`, `"adaptive"` 는 실험용 변형입니다. [CONFIGURATION.ko.md](CONFIGURATION.ko.md)를 보십시오.

</details>

### 3. 재배치

- **언제.** 트레이스가 생기고 게임이 꺼져 있으며 PC 가 한가할 때, 한 번에 게임 하나씩.
- **무엇을.** 존을 계획하고, 조각을 옮기고(저널과 SHA-256 표본 검사), 배치를 확인한 뒤 그 존을 예열합니다.
- **하지 않는 것.** 게임이 실행되는 동안 하지 않고(이동 중이면 지금 조각을 끝내고 멈춤), 스파스·압축·암호화 파일은 옮기지 않으며, 파일 내용을 바꾸지 않습니다. 검사가 실패하면 저널대로 되돌립니다.
- **설정.** `auto.relocate`, `idle_seconds`, `max_relocations_per_day`, `max_zone_mib`, `max_relocate_gib_per_day`. 쓰는 양은 [하드디스크에 미치는 영향](GAME_GUIDE.ko.md#하드디스크에-미치는-영향)에 있습니다.
- **로그.** `<게임>: auto relocation done`, `[ERROR] <게임>: auto relocation stopped: ...`

<details>
<summary>세부: 상한, 중단되거나 나뉜 이동, 게임 업데이트</summary>

**상한**

- 존 크기는 핫 데이터 × 1.1 이며 `max_zone_mib` 를 넘지 않습니다.
- 24시간에 최대 `max_relocations_per_day`번만 재배치하며 재배치를 시작한 시각부터 셉니다. 64 MiB 미만 회차는 세지 않지만 바이트 예산에는 더합니다.
- 게임마다 자동 첫 재배치는 한 번입니다. 이후 유지는 [유지](#5-유지)의 주기 검사가 맡습니다.
- `auto.relocate` 는 수동 등록 게임에도 적용됩니다. 원치 않으면 끄거나 그 exe 를 `auto.ignore_exes` 에 넣으십시오.

**중단되거나 나뉜 이동**

- 이동 중 게임이 시작되면 이동 중이던 조각을 끝내고 멈추며, 나중에 저널로 이어갑니다.
- 옮길 구역을 다른 파일이 차지했거나 계획이 바뀌면, 이미 옮긴 부분은 다시 옮기지 않고 남은 부분만 새 빈 영역으로 다시 계획합니다.
- 존이 이미 최대 크기인 계획은 다시 계획하지 않으며, 화면에는 작업이 자동으로 이어진다고 표시합니다.
- 게임이 꺼진 상태에서 데이터 검사가 실패하거나 오류가 나면 저널대로 되돌리고 실패로 기록합니다. 되돌리는 것은 그 자동 적용분만입니다.

**게임 업데이트**

- 재배치 뒤 게임 파일의 크기나 수정 시각이 바뀌면 트레이스와 계획을 `.stale` 로 치우고(10분마다 확인) 단계를 발견으로 되돌립니다.
- 다음 실행 때 이전 존 위치를 먼저 시도하며 다시 트레이스·재배치합니다.

**그 밖에**

- 옮기는 I/O 에는 낮은 I/O 우선순위 힌트(`FileIoPriorityHintInfo` Low)를 걸었습니다. 문서가 커널의 이동 복사 I/O 에 이 힌트가 적용된다고 보장하지 않으며, 아직 측정하지 않았습니다.
- 옮긴 뒤에는(기록이 있으면 실패했어도) 곧바로 그 구역을 예열합니다.
- 다른 로그 줄: `<게임>: partially applied plan replanned, the parts already moved are kept`.

</details>

### 4. 예열

- **언제.** 서비스가 시작할 때, 지원 런처가 뜰 때, 게임이 끝난 뒤.
- **무엇을.** 최근에 플레이한 게임의 존을 RAM 예산 안에서 64 MiB 묶음으로 RAM 에 읽어 둡니다.
- **하지 않는 것.** 게임에 맞서 디스크를 잡고 있지 않습니다. 어느 게임이든 시작되면 지금 예열 중인 게임도 포함해 다음 묶음 경계에서 멈춥니다.
- **설정.** `auto.full_prewarm`, `auto.full_prewarm_max_mib`, `auto.engine_profiles`, `prewarm_recent`, `prewarm.max_cache_mib`, `prewarm.min_free_ram_mib`, `auto.startup_warm_max_mib`, `auto.rewarm_after_exit_min`.
- **로그.** `<게임>: prewarm done (<사유>): N MiB requested, ... in S s = R hint MB/s, ...`

<details>
<summary>세부: 시점, 순서, 예산, 읽기, 기록이 없는 게임</summary>

**정확한 시점**

- 게임 종료는 등록 실행 파일이 모두 사라진 뒤 60초 동안 다시 뜨지 않을 때로 봅니다(런처에서 게임 본체로 넘어가는 틈을 보호). 설치 폴더 아래 미등록 프로세스는 종료를 미루지 않습니다.
- 게임이 끝난 뒤 같은 물리 디스크에서는 재배치, SSD 계층 적용, 새 트레이스에 따른 예열과 재예열을 60초와 `auto.rewarm_after_exit_min`(분) 중 긴 값이 지난 뒤에 시작합니다.

**순서와 예산**

- 최근에 플레이한 게임부터, 지금 시간대에 자주 한 게임을 앞세웁니다. 런처 등으로 최근 트리거된 게임은 6시간 동안 먼저 예열합니다.
- `prewarm_recent` 가 0 이면 예산 안에 드는 만큼의 최근 게임을, 1~32 면 그 개수만큼만 예열합니다.
- 예산은 물리 RAM 의 절반(또는 `prewarm.max_cache_mib`)과, 가용 RAM 에서 `min_free_ram_mib` 을 뺀 값 중 작은 값입니다.
- 읽기 직전에 가용 메모리를 다시 잽니다. 목록이 더는 들어가지 않으면 256 MiB 여유를 두고 들어가는 앞부분을 읽고 나머지는 나중에 보충합니다.

**읽기**

- 디스크 물리 위치 순서로 읽습니다. 묶음에 게임이 둘 이상이면 각 게임의 로딩 앞부분(tier0)을 최근 순서로 먼저 올리고, 그다음 게임별 구역 전체를 읽습니다.
- 예열 목록의 각 범위는 실제 플레이에서 읽힌 범위인지 엔진 프로필로 추측한 범위인지 표시합니다. 예산이 모자라면 모든 게임의 관찰된 범위를 먼저 채우고 남는 예산을 추측 범위에 씁니다.
- 서비스 시작 때는 앞부분만 `auto.startup_warm_max_mib` 까지 읽고, 나머지는 디스크가 한가해진 뒤에 읽습니다.
- 관리 창에 `예열 중 x/y MB` 가 나오고, 예열이 끝나면 트레이 알림을 보냅니다(게임당 하루 1회).
- 다른 게임이 시작되면 묶음 경계에서 멈춥니다. 지금 예열 중인 게임이 시작되어도 멈추고 양보합니다(`auto.keep_stream_on_game_start`, 기본 `false`, `true` 면 끝까지 읽음, 실험용).

**기록이 없는 게임**

- 작은 게임(폴더가 RAM 예산 안)은 폴더 전체를 `auto.full_prewarm_max_mib` 까지, 큰 게임은 엔진 프로필의 파일을 예열합니다.
- 등록이 사라진 게임은 예열하거나 고정하지 않습니다.
- 다른 로그 줄: `prewarm cancelled <사유>`, `<게임>: prewarm skipped (...)`, 단계별 시간을 담은 `prewarm phases` 줄. 부분 예열이면 앞에 `partial A/B MiB, the rest does not fit in memory:` 가 붙습니다.

</details>

### 5. 유지

- **언제.** 재예열 점검은 `rewarm_minutes` 마다, 보충과 재시도는 디스크가 한가한 동안, 배치 검사는 주기적으로.
- **무엇을.** 존의 8곳을 재어 식었으면 다시 읽고, 예산에 안 들어간 핫 집합의 나머지를 읽고, 끊긴 예열을 재시도하고, 흐트러진 배치를 복구합니다.
- **하지 않는 것.** 예열 예산 밖 게임을 다시 예열하지 않고, 게임이 실행되는 동안 배치를 건드리지 않습니다.
- **설정.** `rewarm_minutes`, `min_free_ram_mib`, `post_update_delay_min`.
- **로그.** `<게임>: rewarm check skipped (...)`, `<게임>: layout degraded while source files are unchanged (...)`, `<게임>: reapplied, moved N bytes, N failed`

<details>
<summary>세부: 재예열, 보충, 재시도, 배치 재검사와 재적용</summary>

**재예열(예열 유지)**

- 존의 8곳(접근 순서 앞 4곳과 균등한 4곳)을 1 MiB씩 읽어 잽니다. 그중 2곳 이상이 2 ms 를 넘으면 파일 캐시에서 밀려난 것이므로, `min_free_ram_mib` 여유를 지키며 다시 예열합니다.
- 예열 예산 안에 드는 게임만, 예열 점수(최근 실행·시간대 습관) 순서로 합니다. 예산 밖 게임은 식어도 다시 예열하지 않고 사유를 로그에 남깁니다.
- 실행 중인 게임이 하나 있다고 다른 게임의 점검을 멈추지 않고 대상별로 판단합니다.

**보충 예열**

- 핫 집합 전체가 예산에 안 들어가면, 디스크가 한가한 동안 들어가는 창만 읽습니다. 남은 핫 집합, 가용 RAM 에서 예비를 뺀 값, 예열 예산 중 가장 작은 값이며 최소 256 MiB 입니다.
- 보충이 중간에 끊기면 읽은 위치를 서비스가 켜져 있는 동안 기억해 다음 보충에서 이어 읽습니다. 틈 메우기가 섞인 보충이나 앞부분이 식은 경우에는 처음부터 다시 읽습니다.
- 읽기 오류가 나거나 설정이 바뀌면 위치를 버리고, 서비스를 재시작하면 위치는 사라집니다.
- 로그: `<게임>: rewarm check: the rest of the hot set is not read yet, supplementing once the drive has stayed idle`, `<게임>: supplement prewarm skipped (...)`.

**끝내지 못한 예열의 재시도**

- 트레이스가 있는 게임의 예열이 끝나기 전에 취소됐거나 서비스 시작 상한에 밀렸으면 대기 목록에 남고, 디스크가 60초 동안 한가하고 그 디스크에서 실행 중인 게임이 없고 캐시 예산에 여유가 있으면 다시 요청합니다.
- 트레이스가 없는 게임은 이렇게 재시도하지 않습니다.
- 취소된 예열은 드라이브가 재배치에서 회복하는 중이어도 다시 읽습니다. 다음 재배치는 여전히 회복을 기다립니다.

**배치 재검사와 재적용**

- 주기 검사(기본 1440분)가 원본 파일은 그대로인데 배치가 흐트러진 것을 찾으면 계획을 다시 적용합니다.
- 일시적 조건(실행 중인 게임, 바쁜 디스크, 드라이브 회복, 배터리 전원, 한가한 시간 부족, 업데이트 뒤 지연)이 검사를 막으면 5분마다 반복합니다. 회차 쓰기 상한에서 멈춘 재적용도 5분 재검사를 유지합니다.
- 다른 파일이 존 일부를 차지해 재적용이 거절되면(`zone_not_free`), 그 게임을 자동 재배치로 넘깁니다. 자동 재배치는 옮긴 부분을 유지하고 남은 부분만 다시 계획하며, 기존 보류와 일일 상한을 따릅니다.
- 자동 재배치가 켜져 있고 트레이스가 있으며 데이터 검사 실패로 멈춘 게 아닐 때만 넘기고, 아니면 전처럼 실패로 끝납니다. 옮기기 전의 거절은 0바이트로 기록합니다.
- 이 검사(extent·지문 조회)는 진행 중에도 약 1초마다 다시 확인해 게임이 시작되면 멈춥니다.
- 다른 로그 줄: `<게임>: reapply blocked by zone_not_free, handing over to auto relocation to replan the remaining part`.

</details>

### 6. 게임 실행 중

- **언제.** 게임이 시작될 때부터 종료까지(짧은 유예 시간 포함).
- **무엇을.** 그 디스크의 재배치·재적용·계층 쓰기를 막고, 예열은 양보하며, 디스크가 어떻게 움직였는지 기록합니다(`play record:`).
- **하지 않는 것.** 게임 파일이나 프로세스를 읽지 않습니다. 플레이 기록은 디스크 장치 카운터만 쓰며 그 자체로 게임을 빠르게 하지는 않습니다.
- **설정.** `auto.start_stream_cap_seconds`, `auto.restream_cooldown_min`, `auto.keep_awake`.
- **로그.** `<게임>: start stream skipped (<사유>)`, `play record: ...`

<details>
<summary>세부: 같은 디스크 규칙, 시작 스트림, 플레이 기록</summary>

**같은 디스크**

- 실행 중인 게임 자신의 예열과 재배치·재배치 재적용·계층 이동(쓰기)은 막습니다.
- 같은 디스크의 다른 게임 예열은 실행 중인 게임이 초당 1 MiB 미만으로 읽고 디스크 처리량도 바쁨 기준 미만인 상태가 60초 이어질 때만 합니다.
- 읽는 도중에는 묶음 사이마다 그 게임의 읽기 증가율을 보고, 약 10초마다 1초씩 멈춰 디스크 처리량을 다시 재어 바쁘면 양보합니다. 읽기량 조회가 실패하면 바쁨으로 봅니다.
- 게임이 실행 중이라 예열을 건너뛴 로그 줄에는 실행 중인 게임으로 판정한 프로세스의 이름과 pid 가 남고, 실행 경과 시간은 서비스가 시작을 관측한 시각부터 셉니다.

**시작 스트림**

- 게임이 시작되면 서비스가 그 게임의 핫 집합을 읽을 수 있습니다(시작 스트림). 로딩 경계(10초 이동 읽기율이 최고의 10% 아래로 내려간 지점, 시작 뒤 최대 60초)나 `auto.start_stream_cap_seconds` 에서 멈춥니다.
- `auto.restream_cooldown_min` 안에는 반복하지 않습니다. 로그: `<게임>: start stream stopped at load boundary after N bytes, S s (R MB/s)`.

**작은 점검**

- HDD 깨워 두기 읽기, 핀 생성, 부팅 점검은 시작 전에 게임 실행 여부를 확인합니다.
- 깨워 두기 읽기(`auto.keep_awake`)는 런처가 떠 있거나 평소 플레이하던 시간대에 10분마다 4 KiB 를 읽어 HDD 가 잠들지 않게 하는 것입니다. 전원 설정은 바꾸지 않습니다.

**플레이 기록**

- 서비스는 물리 디스크의 누적 읽기 카운터를 1초 간격으로 표본 내고, 시작 뒤 30·60·120·300·600초와 종료 시점에 `play record:` 로그 줄과 `play_samples.json` 을 남깁니다.
- 게임 파일이나 프로세스는 읽지 않고 디스크 장치 카운터만 조회합니다. 로딩과 끊김을 비교하기 위한 계측입니다.

</details>

### 7. 드라이브 회복 대기

- **언제.** SMR 디스크에서 재배치를 한 뒤, 그리고 게임 업데이트를 감지한 뒤.
- **무엇을.** 짧은 시험 읽기로 드라이브가 보통 속도로 돌아온 것을 확인할 때까지 기다린 뒤 다음 작업을 허용합니다.
- **하지 않는 것.** 예열은 막지 않습니다. 재배치를 막고, 게임 업데이트 뒤에는 그 게임의 재계획과 예열을 미룹니다.
- **설정.** `post_relocate_recovery`, `recovery_max_min`, `recovery_hard_max_min`, `post_update_delay_min`.
- **로그.** `drive D: recovering after relocation (...)`, `drive D: recovered after N min`

<details>
<summary>세부: probe 규칙, 상한, 다른 방식, 멈춤</summary>

**probe(기본)**

- 기준은 쓰기 직전에 표본이 도는 세 위치에서 재고 그 중앙값을 씁니다.
- 2분마다 짧게 읽어 기준의 90% 이상이 연속 2회면 회복으로 봅니다. 측정값이 흔들려도, 최근 두 바퀴(6회) 가운데 4회 이상이 기준의 90% 이상이면 회복으로 봅니다.
- 미달이 이어지면 첫 두 바퀴 뒤부터 간격을 4분, 8분으로 늘립니다. 표본마다 위치, 속도와 기준, 판정과 사유를 로그에 남깁니다.
- 캐시 적중 상한을 넘는 표본이 돌아가며 고르는 세 위치에서 연속으로 나와도 회복으로 봅니다. 판정할 수 없는 표본은 최대 8분 간격으로 벌려 읽고, 버린 표본은 한 줄로 요약해 남깁니다.
- 최대 `recovery_max_min`분 기다립니다. 그래도 회복을 확인하지 못하면, 회복되거나 `recovery_hard_max_min`분이 지날 때까지 다음 재배치를 계속 보류합니다.
- `"fixed"`는 `post_update_delay_min`분을 고정으로 기다리고, `"off"`는 끕니다.

**게임 업데이트 뒤**

- 드라이브가 흩어진 쓰기를 아직 정리하는 중일 수 있어, 그 게임의 재계획·재배치·예열을 `post_update_delay_min`분 미룹니다(E9).

**멈춤**

- 같은 디스크의 게임이 실행 중이면 대기를 멈추고, 게임이 끝난 뒤 남은 시간부터 이어갑니다. 그동안 회복 표본 읽기도 하지 않습니다.
- 서비스를 재시작해도 멈춘 상태와 남은 시간을 이어받습니다.
- 다른 로그 줄: `drive D: recovery clock paused (game running)`, `drive D: recovery discarded N cache-suspect samples ...`.

</details>

### 부팅 직후

서비스가 부팅 뒤 5분 안에 뜨면 스스로 시작하는 디스크 작업을 부팅 5분 경과와 게임 드라이브 60초 유휴 뒤까지 보류합니다. 보류 구간은 늦어도 부팅 15분 뒤에 끝납니다.

<details>
<summary>세부: 무엇을 보류하나</summary>

- 보류하는 작업: 런처 계기 예열, 재배치, 새 트레이스 예열, 배치 검사와 재적용, HDD 깨워 두기. 그 뒤 기존 조건으로 실행합니다.
- 부팅 예열은 최근에 플레이한 게임만 대상으로 합니다. 재시작 전에 이미 예열한 트레이스는 새 트레이스로 다시 치지 않습니다.
- 디스크가 계속 바쁘거나 읽을 수 없어도 부팅 15분 뒤에는 구간을 끝냅니다.
- 로그: `<무엇> deferred: boot quiet period`, `boot quiet period ended: ...`.

</details>

### 고정(선택)

`prewarm.pin` 을 켜면 예열한 페이지를 서비스가 읽기 전용 매핑으로 열어 두어 캐시에서 덜 밀려나게 합니다. 효과 미확인(실험 필요).

<details>
<summary>세부: 언제 켜지고 언제 풀리나</summary>

- 설정에 적지 않으면 물리 RAM 24 GiB 이상에서 자동으로 켜짐, `false` 는 끔, 명시한 `true` 는 RAM 16 GiB 이상에서 동작합니다.
- 가용 RAM 이 여유 아래로 내려가거나 게임·런처가 시작되면 즉시 해제하므로, 런처 업데이트가 파일을 고칠 수 있습니다. 트리거 프로그램으로 뜬 예열은 고정하지 않습니다.

</details>

## 한가함의 뜻

"한가하다"는 아래를 모두 만족하는 상태입니다.

- 옮길 게임과 같은 드라이브에 설치된 등록 게임이 돌지 않습니다(다른 드라이브의 게임은 보지 않습니다).
- 게임 드라이브 처리량이 30초 동안 연속으로 4 MiB/s 미만입니다.
- AC 전원이 연결됐거나 배터리가 50% 이상입니다.

CPU 사용률은 보지 않습니다. 재배치는 낮은 I/O 우선순위로 대상 드라이브에만 영향을 줍니다. 6시간을 넘게 한가하지 않으면 `service.log` 에 이유(`game running`, `on battery`, `disk busy`) 한 줄을 남깁니다. 게임마다 지금 어느 단계인지는 관리 창의 **상태** 탭과 **게임** 탭의 단계 열에서 볼 수 있습니다.

## 지원 런처

Steam, Epic Games Store, GOG Galaxy, Ubisoft Connect, Riot Client, Windows 제거 목록, Xbox 앱 폴더를 읽습니다. Battle.net 과 EA 앱의 라이브러리는 읽지 않으며, 그 게임은 제거 목록이나 휴리스틱으로 잡힙니다.

<details>
<summary>세부: 런처별 읽는 방법</summary>

**Steam** (예열 트리거 `steam.exe`)

- `libraryfolders.vdf` 와 `appmanifest` 파일. 실행 파일은 게임 폴더에서 가장 큰 `.exe` 이며, 설치 프로그램·재배포 파일·크래시 도구는 제외합니다.

**Epic Games Store** (트리거 `EpicGamesLauncher.exe`)

- `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item` 의 `LaunchExecutable`.

**GOG Galaxy** (트리거 `GalaxyClient.exe`)

- 레지스트리 `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games`.

**Ubisoft Connect** (트리거 `upc.exe`, `UbisoftConnect.exe`)

- 레지스트리 `Launcher\Installs` 의 `InstallDir` 값.

**Riot Client** (트리거 `RiotClientServices.exe`)

- `%ProgramData%\Riot Games\Metadata\<제품>.live\*.product_settings.yaml` 의 `product_install_full_path`. Vanguard 안티치트로 표시합니다. `.live` 가 아닌 항목(예: PBE)은 제외합니다.
- 리그 오브 레전드(`LeagueClient.exe`, `League of Legends.exe`)와 전략적 팀 전투는 같은 실행 파일 이름을 쓰며, 같은 폴더면 합칩니다.
- 발로란트는 `VALORANT.exe`, `VALORANT-Win64-Shipping.exe` 를 씁니다. 그 밖의 제품은 폴더에서 가장 큰 `.exe` 입니다.

**Windows 제거 목록** (트리거 없음)

- HKLM·HKCU 의 `Uninstall` 레지스트리(32비트 포함). 설치 폴더가 HDD(시스템 드라이브가 아님)이고 1 GiB 이상이며, Microsoft·Intel·NVIDIA 등 일반 소프트웨어 게시자가 아니고, 런처 본체 폴더가 아닌 항목을 씁니다.
- exe 는 폴더에서 가장 큰 `.exe` 입니다. Battle.net·EA·넥슨·호요버스 등 독립 설치 게임이 여기로 잡힙니다.
- 런처 제외 단어에 걸려 건너뛴 항목은 서비스가 시작할 때 현재 규칙으로 exe 를 다시 고르며, 로그에 한 줄을 남깁니다.
- 한계: 런처형 폴더는 엉뚱한 exe 가 골라질 수 있고, 게임을 실제로 실행하면 휴리스틱이 실제 exe 를 더합니다.

**Xbox(Microsoft Store)** (트리거 없음)

- 각 HDD 의 `XboxGames\<게임>\Content` 폴더. exe 는 가장 큰 `.exe` 입니다. 읽을 수 없는 폴더는 건너뜁니다.

**확인한 것과 확인하지 못한 것**

- GOG 와 Ubisoft 읽기는 아직 실제 PC 에서 확인하지 않았습니다. Riot 읽기는 시험 PC 의 D: (HDD) 에 있는 리그 오브 레전드가 잡히는 것을 확인했습니다. Xbox 읽기는 시험 PC 에 해당 폴더가 없어 확인하지 못했습니다.
- `Battle.net.exe`, `EADesktop.exe` 는 예열 트리거 프로그램으로만 적혀 있으며, 서비스가 이 런처들의 라이브러리를 읽지는 않습니다.

</details>

## 휴리스틱 감지

등록되지 않은 프로세스가 HDD 볼륨의 exe 로 시작해 20초 안에 256 MiB 이상을 읽고, 폴더가 게임처럼 보이면 게임으로 보고 등록합니다. 트레이스는 다음 실행부터 기록합니다.

<details>
<summary>세부: 제외 대상과 폴더 검사</summary>

- **제외 대상.** Windows 폴더, AsterCache 자신, 런처와 도구 프로그램, `auto.ignore_exes` 의 exe.
- 경로에 `\bin\Debug\`, `\bin\Release\`, `\obj\`, `\.git\`, `\node_modules\`, `\AppData\Local\Temp\` 가 있는 exe 도 제외합니다.
- 내장 도구 exe(`testhost*`, `dotnet`, `msbuild`, `vstest*`, `python*`, `node`, `java*`, `powershell`, `pwsh`, `cmd`, `conhost`, `explorer`, `7z*`, `winrar`)도 제외합니다. 이 고정 목록은 `auto.ignore_exes` 와 별개입니다.
- **폴더도 게임처럼 보여야 합니다.** 256 MiB 이상 파일이 있거나 런처 흔적(`steam_appid.txt`, `.egstore`, `goggame-*.info`, Steam 매니페스트)이 있어야 합니다. 그래서 `AppData\Local\Temp` 아래 설치된 게임은 잡히지 않습니다.

</details>

## 엔진 기본 프로필

게임 폴더 안의 파일 이름으로 엔진(Unity, Unreal, Source, Riot, MapleStory, CASC, SCS, EA FC Online)을 감지하고, 로딩 초반에 읽을 파일을 고릅니다. TFT 설치는 Unreal 로 감지됩니다.

<details>
<summary>세부: 엔진별 감지 마커와 읽기 규칙</summary>

맞는 프로필이 여럿이면 규칙을 모두 합치고 먼저 맞은 규칙이 이기며, 예산을 넘는 뒤쪽은 자릅니다.

| 프로필 | 감지 마커 | 주요 읽기 규칙 |
|---|---|---|
| Unity | `UnityPlayer.dll`, `<이름>_Data/globalgamemanagers` | globalgamemanagers·resources.assets·sharedassets0·level0~9 전체, Managed dll, StreamingAssets 앞 256 MiB, il2cpp·MonoBleedingEdge dll |
| Unreal | `<게임>/Content/Paks/*.pak`, `*.utoc` | pakchunk0 전체, Binaries 와 Engine/Binaries dll, 나머지 pak 은 앞 256 MiB 와 끝 64 MiB, utoc 전체 |
| Source | `**/*_dir.vpk` | `*_dir.vpk` 전체, pak01 청크 앞 256 MiB, bin dll |
| Riot | `Game/DATA/FINAL/*.wad.client` | Global·UI·Bootstrap·DATA·ShaderCache·Scripts, 맵 11·12·30·453 전체, 루트 exe·dll, 챔피언 앞 64 MiB, 맵 22(TFT)·Companions 전체, 그 밖의 맵 앞 64 MiB |
| MapleStory | 루트 `*.wz`, `Data/*.wz` | `Data.wz` 전체, 나머지 wz·img 앞 1 GiB, 루트 exe·dll |
| CASC (Overwatch, StarCraft) | `data/casc/data/*.idx` | 인덱스 전체, `data.###` 각 앞 256 MiB, 루트 exe·dll |
| SCS (ETS2) | 루트 `base.scs` | base·def·core·effect·locale.scs 각 앞 512 MiB |
| EA FC Online | 루트 `fczf.exe`, `data/*.big` | `data_default.big` 전체, 루트 exe·dll, 나머지 `*.big` 각 앞 256 MiB |

</details>

## 설정

모든 `auto.*` 키의 뜻과 기본값은 [CONFIGURATION.ko.md](CONFIGURATION.ko.md)에 있습니다.

- `"auto": {"enabled": false}` 는 자동 모드 전체를 끕니다. `discover`, `trace`, `relocate` 는 각 단계만 끕니다.
- 상태는 `%ProgramData%\AsterCache\auto_status.json` 에 쓰입니다(서비스만 씁니다). `aster status` 출력에 `"auto"` 키가 있고, `aster discover [--json]` 은 런처 게임 목록을 읽기 전용으로 보여 줍니다.
- 수동 버튼(등록, 트레이스 수집, 계획 만들기, 재배치 실행, 지금 예열)은 전과 같이 동작합니다.

<details>
<summary>세부: 화면에 나오는 것</summary>

- 관리 창 상태 탭에는 게임마다 "자동: Frostpunk 발견 → 트레이스 수집 중 → 재배치 대기 → 재배치 중 → [완료]" 형태의 줄이 나오고, 게임 탭에는 출처·단계·안티치트 열이 있습니다.
- 학습 단계는 "첫 실행: 학습 중 (선행 읽기 n MiB)", 예열 뒤에는 "예열됨" 으로 표시됩니다.
- 게임별 상태 줄에는 `예열 직후 적중 N%`(예열이 끝난 직후에 잰 적중률)과 `SSD 보조 N GB`(게임의 SSD 사본 크기)도 나옵니다.
- 트레이 알림은 발견, 재배치 완료, 첫 실행 완료("다음 실행부터 빨라집니다: <게임>") 때 보냅니다.

</details>

## 효과

자동 모드가 게임 전체 로딩 시간을 얼마나 줄이는지는 재지 않았습니다. 시험 PC 의 서비스 로그(2026-11-02 ~ 2026-11-10)를 집계한 실사용 기록이 있으며, 통제된 벤치마크가 아니고 예열이 없을 때와 비교하지 않았습니다.

- League of Legends(Vanguard): 게임 시작 감지 10회, 트레이스 2회 수집 뒤 8회 건너뜀, 재배치 완료 4회, 예열 직후 적중률 100%(마지막 값 1개).
- Call of Duty(Ricochet): 게임 시작 감지 62회, 트레이스 수집 14회 시작, 재배치 완료 5회, 예열 직후 적중률 75%(마지막 값 1개).

집계 방법과 표본 수는 [실사용 기록](RESULTS_SUMMARY.ko.md#실사용-기록), 안티치트 게임에서의 정책은 [안티치트 호환](ANTICHEAT.ko.md)에 있습니다. 계정 제재 여부는 보증하지 않습니다.

## SSD 보조 저장 (선택)

기본으로 꺼져 있습니다(`auto.ssd_tier.enabled`). 꺼져 있으면 AsterCache 는 파일을 건드리지 않습니다. 다만 이전 실행이 남긴 링크는 되돌립니다. 켜면 HDD 게임의 가장 뜨거운 파일을 예산 안에서 SSD 에 복사하고 그 자리에 심볼릭 링크를 둡니다.

<details>
<summary>세부: 고르기, 적용, 유지, 되돌리기</summary>

**고르기와 적용**

- 파일마다 핫 바이트(트레이스가 읽은 부분)를 파일 크기로 나눈 값을 매깁니다. 값이 높은 파일부터 예산(`budget_gib`, 게임당 기본 8 GiB, 1~4096) 안에 들어가는 만큼 고릅니다. 게임의 `.exe`·`.dll` 파일과 `allowed_roots` 밖의 파일은 고르지 않습니다.
- 파일을 SSD 에 복사하고 크기와 표본 SHA-256 을 확인합니다. 저널에 기록한 뒤 원본을 같은 폴더의 `<이름>.aster-orig` 로 이름을 바꾸고, 그 자리에 심볼릭 링크를 둡니다. 링크로 크기를 다시 확인합니다. 한 파일이 실패하면 그 파일만 되돌립니다.
- `root` 는 `"auto"`(시스템 드라이브가 SSD 면 그곳, 아니면 여유가 가장 큰 다른 SSD, 폴더는 `AsterCache\tier`) 또는 SSD 위의 절대 경로입니다.

**언제 하나**

- 한 번에 게임 하나씩, 게임이 꺼져 있고 HDD 이며 시스템 드라이브가 아니고 안티치트 흔적이 없으며 재배치가 끝난(또는 재배치가 꺼진) 게임에만 합니다.
- 재배치와 같이 한가함을 기다리며, 끝내지 못한 게임은 6시간 뒤에 다시 시도합니다.

**유지**

- 서비스 틱마다 저널을 확인합니다. 사본이 없거나 크기가 다르면 바로 되돌립니다. 파일이 일반 파일로 바뀌었거나 사본이 링크를 통해 바뀌었으면 stale 로 표시하고 그대로 둡니다.
- SSD 여유가 1 GiB 아래면 새 사본을 만들지 않고, 가장 오래된 게임부터 되돌립니다. 예산이나 폴더 설정을 바꾸면 그 게임을 되돌린 뒤 새 설정으로 다시 적용합니다.

**되돌리기와 명령**

- `aster tier revert --game <이름>` 또는 `--all` 은 링크를 지우고 원본 이름을 복구합니다. 사본은 `aster tier clean` 이 쓰지 않는 것을 지울 때까지 tier 폴더에 남습니다. 제거할 때는 모든 게임을 먼저 되돌립니다.
- 저널은 `%ProgramData%\AsterCache\tier\<게임>.json` 입니다. 저널을 읽을 수 없으면 그 게임은 아무것도 건드리지 않고 건너뛰며, 로그에 한 줄을 남깁니다.
- 명령: `aster tier status`, `aster tier apply --game <이름>`, `aster tier revert --game <이름>` 또는 `--all`, `aster tier clean` (apply·revert·clean 은 관리자 권한 필요).
- 설정 탭의 자동 모드 항목에 체크박스와 예산이 있고, 게임별 상태 줄에 `SSD 보조 N GB` 가 나옵니다.

</details>
