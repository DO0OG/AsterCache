[English](CONFIGURATION.md) | **한국어**

# 설정

> `config.json` 의 모든 설정과 기본값, 감시기, 화면 언어를 설명합니다. 기본값은 이 문서가 유일한 출처이며, 다른 문서는 여기를 가리킵니다.

설정 파일은 `%ProgramData%\AsterCache\config.json` 입니다. 관리 창에서 편집하는 것을 권장합니다. 직접 고칠 때는 JSON 의 백슬래시를 `\\` 로 씁니다.

- 서비스는 파일 전체를 5초마다 다시 읽습니다(크기나 수정 시각이 바뀔 때). 그래서 다시 시작하지 않아도 반영됩니다. 새 설정이 검증이나 폴더 권한 검사에 실패하면 옛 설정을 유지하고 이유를 로그에 남깁니다.
- 모르는 키는 거부합니다. 생략한 키는 아래 기본값을 씁니다.
- ORBIT 는 `config.json` 이 아니라 CLI 로 켜고 끕니다(`aster orbit mode off|shadow|active`). [ORBIT](ORBIT.ko.md)을 보십시오.
- 관리 창의 설정 탭과 게임 탭이 이 키들의 대부분을 씁니다. 설정 탭의 **고급** 구역(기본으로 접힘)에는 숫자 값이 있습니다: `prewarm.max_cache_mib`, `auto.startup_warm_games`, `auto.startup_warm_max_mib`, `auto.rewarm_after_exit_min`, `auto.max_relocations_per_day`, `auto.idle_seconds`, `auto.start_stream_cap_seconds`. 창이 기본값과 같은 값을 쓰면 파일에서 그 키를 지웁니다.
- 알림 창 켜기·끄기는 `config.json` 이 아니라 `tray.json` 의 `notifications.enabled` 입니다.

```json
{
  "language": "auto",
  "watcher": "poll",
  "games": [
    {
      "name": "frostpunk",
      "exe_names": ["Frostpunk.exe"],
      "trace": "C:\\ProgramData\\AsterCache\\plans\\frostpunk.prefetch.json",
      "plan": "C:\\ProgramData\\AsterCache\\plans\\frostpunk.plan.json",
      "origin_root": "D:\\Games\\Frostpunk",
      "allowed_roots": ["D:\\Games"],
      "volume_label": "GAMES",
      "max_stream_mib": 4096,
      "prewarm": {
        "on_service_start": true,
        "trigger_exes": ["Steam.exe"],
        "min_free_ram_mib": 4096,
        "cooldown_min": 30
      }
    }
  ]
}
```

## 최상위

| 키 | 기본값 | 뜻 |
|---|---|---|
| `language` | `auto` | 화면 언어. `auto`, `ko`, `en`. `auto` 는 Windows 표시 언어를 따르며 한국어가 아니면 영어를 씁니다. |
| `watcher` | `poll` | 게임 시작 감지 방식. `poll` 또는 `etw`. `etw` 는 관리자 권한이 필요하며 실패하면 폴링으로 내려갑니다. [감시기](#감시기)를 보십시오. |
| `poll_ms` | 50 | `poll` 감시기의 확인 간격(ms, 50~5000). |
| `max_zone_mib` | 2048 | 자동 계획의 존 크기 상한(MiB, 64~65536), 또는 `"auto"`(물리 RAM ÷ 4, 2048~8192 MiB 사이이며, 디스크 종류를 모르거나 USB 이면 2048 상한). 32 GB PC 는 고정 2048 보다 `"auto"` 를 권장합니다. |
| `riot.champ_select_prewarm` | `false` | Riot 게임의 챔피언 선택 중 로컬 리그 오브 레전드 클라이언트 API로 고른 챔피언·맵 파일을 미리 RAM에 읽습니다. Riot 이 2019-01-24 에 한국 플레이어는 League Client API 를 쓰는 앱을 쓸 수 없다고 공지했고 철회는 찾지 못했으므로, 한국 계정에서는 켜지 마십시오. [League of Legends 참고](GAME_GUIDE.ko.md#참고-league-of-legends-vanguard)를 보십시오. |
| `riot.champ_select_budget_mib` | 2048 | 챔피언 선택 선읽기가 읽을 수 있는 최대 MiB(64~16384). |
| `memory.background_yield` | `true` | 게임 세션 동안 게임과 무관한 프로그램이 메모리를 양보합니다. 그 프로그램의 메모리 우선순위를 낮추고, 메모리가 모자라거나 캐시가 실제로 재사용될 때만 작업 집합을 비우며, 세션이 끝나면 우선순위를 되돌립니다. 게임·런처·안티치트·서비스 프로세스는 건드리지 않습니다. `false` 면 끕니다. 설정 탭에는 이 스위치가 없습니다. [배경 메모리 양보](AUTO_MODE.ko.md#배경-메모리-양보)를 보십시오. |
| `memory.standby_ladder` | `true` | 예열 뒤 그 게임의 캐시를 읽은 순서의 역순으로 메모리 안에서 다시 만져, 로딩이 가장 먼저 필요로 하는 데이터가 가장 나중에 밀려나게 하고, 다른 프로그램이 밀어내기 전에 다시 만집니다. 일부러 디스크에서 읽지 않으며, 게임이 실행 중인 디스크에서는 돌지 않습니다. `false` 면 끕니다. 설정 탭에는 이 스위치가 없습니다. [SOLAR](SOLAR.ko.md)를 보십시오. |
| `games` | `[]` | 게임 목록(최대 32개). 게임 하나의 키는 아래에 있습니다. |

## 게임 하나: `games[]`

| 키 | 기본값 | 뜻 |
|---|---|---|
| `name` | 필수 | 내부 이름. 영문자·숫자·`.`·`_`·`-` 로 1~64자. 계획·저널·상태 파일의 이름이 됩니다. |
| `exe_names` | 필수 | 실행 파일 이름 목록(1~16개, 모두 `.exe` 로 끝남). |
| `trace`, `plan` | 필수 | 추적 파일과 계획 파일 경로. 계획 폴더 안의 `.json` 파일이어야 합니다. |
| `origin_root` | 필수 | 게임 설치 폴더. `allowed_roots` 안에 있어야 합니다. |
| `allowed_roots` | 필수 | 재배치를 허용하는 폴더(1~16개, `origin_root` 와 같은 드라이브, 드라이브 전체는 불가). |
| `volume_label` | 필수 | 대상 볼륨 레이블. 다르면 재배치하지 않습니다. |
| `reapply_interval_minutes` | 1440 | 정기 배치 검사 간격(분, 5~10080). |
| `max_stream_mib` | 4096과 존 크기 중 큰 값 | 한 번의 스트림·예열이 읽는 상한(MiB). 명시값이 우선합니다. |
| `source` | `manual` | 자동으로 찾은 게임은 `auto`, 직접 등록한 게임은 `manual`. |
| `anticheat` | 없음 | 설치 폴더에서 찾은 안티치트: `eac`, `battleye`, `vanguard`, `kernel`, `ricochet`, `unknown`. 게임 탭에 표시되며 동작은 바꾸지 않습니다. 정책은 [안티치트 호환](ANTICHEAT.ko.md)을 보십시오. |
| `display_name` | 없음 | 화면에 보일 게임 이름. 없으면 서비스가 남긴 런처 이름, 그것도 없으면 키의 밑줄을 공백으로 바꾼 이름을 씁니다. 내부 키는 그대로입니다. 게임 탭의 게임 설정 창(기본·한국어·영어 이름 칸)에서도 고칠 수 있습니다. |
| `display_names` | 없음 | 언어별 화면 이름. 예: `{"ko": "발로란트", "en": "VALORANT"}`. 키는 `ko`, `en` 만 됩니다. |
| `mode` | `inherit` | 이 게임에 서비스가 하는 일의 범위: `inherit`(전부), `prewarm_only`(재배치·정기 재적용 제외), `relocate_only`(예열·재예열·보충·시작 스트림·고정 제외), `off`(아무것도 안 함). 추적은 게임이 `off` 이거나 `paused` 가 아니면 계속 허용됩니다. 게임 탭의 게임 설정 창에서 정책을 고르는 것으로도 바꿉니다. |
| `paused` | `false` | `true` 면 `mode` 와 무관하게 서비스가 이 게임의 새 작업을 시작하지 않습니다. 학습 데이터와 기록은 그대로 둡니다. 게임 탭의 게임 설정 창에서 일시 중지 체크 상자로도 바꿉니다. |

`display_name` 과 `display_names` 는 손으로 넣을 수 있는 선택 키입니다. 이 키가 든 설정을 beta.24 로 되돌리면 그 버전이 설정을 거부하므로(종료 코드 2), 되돌리기 전에 두 키를 지워야 합니다.

### 예열: `games[].prewarm`

| 키 | 기본값 | 뜻 |
|---|---|---|
| `on_service_start` | `true` | 서비스 시작 때 예열을 실행합니다. |
| `trigger_exes` | `[]` | 이 프로그램이 뜨면 예열을 실행합니다(예: 런처, 최대 16개). |
| `min_free_ram_mib` | 4096 | 예열 뒤에도 남겨 둘 가용 RAM. 설정 창에는 없고 파일에서 고칩니다. |
| `cooldown_min` | 30 | 예열 사이의 최소 간격(분). 쿨다운 안이어도 표본 읽기가 캐시가 식었음을 보이면(예: 재부팅 뒤) 그 게임을 예열합니다. 캐시가 따뜻하거나 표본을 읽을 수 없으면 쿨다운이 적용됩니다. [자동 모드](AUTO_MODE.ko.md#4-예열)를 보십시오. |
| `max_cache_mib` | 0(자동) | 예열이 파일 캐시에 올려 둘 합계(MiB), 모든 게임이 나눠 씁니다. `0` 이거나 없으면 물리 RAM 의 절반이고, 양수면 그 값이며 물리 RAM 을 넘지 못합니다. 여러 게임이 적으면 가장 큰 값을 씁니다. `min_free_ram_mib` 여유 검사는 그대로 적용됩니다. 고급 구역에서 저장하면 모든 등록 게임에 같은 값을 씁니다(0 이면 키를 지움). |
| `pin` | 자동 | 예열한 데이터를 서비스가 읽기 전용 매핑으로 열어 둡니다. 설정에 적지 않으면 물리 RAM 24 GiB 이상에서 자동으로 켜지고, `false` 는 끕니다. 명시한 `true` 는 RAM 16 GiB 이상에서 동작합니다. 효과 미확인(실험 필요). [안티치트 호환](ANTICHEAT.ko.md)을 보십시오. |

## 자동 모드: `auto`

각 단계가 하는 일은 [자동 모드](AUTO_MODE.ko.md)에 있습니다. 괄호 안은 허용 범위입니다.

**켜고 끄기와 목록**

| 키 | 기본값 | 뜻 |
|---|---|---|
| `enabled` | `true` | 자동 모드 전체를 켜거나 끕니다. |
| `discover`, `trace`, `relocate` | `true` | 단계 하나씩 끕니다: 발견, 트레이스, 재배치. |
| `ignore_exes` | `[]` | 자동으로 등록하지 않을 실행 파일(최대 64개). 관리 창에서 제거한 게임이 여기에 들어갑니다. |
| `ssd_tier` | `enabled: false`, `budget_gib: 8`, `root: "auto"` | 선택 기능인 SSD 보조 저장. `budget_gib`(1~4096)는 게임 하나가 쓸 수 있는 최대 SSD 공간입니다. `root` 는 `"auto"`(시스템 드라이브가 SSD 이면 그곳, 아니면 여유가 가장 큰 SSD) 또는 SSD 위의 절대 경로입니다. |

**첫 실행 전**

| 키 | 기본값 | 뜻 |
|---|---|---|
| `shared_profiles` | `enabled: true`, `url`: 프로젝트의 프로필 릴리스 | 공유 프로필을 주 1회 HTTPS 로 내려받습니다(URL 은 https 만). 공유 저장소는 아직 준비 중입니다. |
| `full_prewarm` | `true` | 기록이 없는 게임은 설치 폴더가 RAM 예산 안이면 폴더 전체를 예열합니다. |
| `full_prewarm_max_mib` | 0 | 그 예열의 폴더 크기 상한(MiB, 0~1048576). `0` 은 1024 MiB 입니다. |
| `engine_profiles` | `true` | 기록이 없는 더 큰 게임은 엔진(Unity, Unreal, Source 등)의 파일만 예열합니다. |
| `learn` | `enabled: false`, `mode: "stream"`, `window_mib: 512`, `max_total_mib: 0`, `saturation_mbps: 80` | 실험 기능인 첫 실행 선행 읽기. E7~E7f 측정에서 이득이 없어 꺼져 있습니다. `mode` 는 `"stream"`, `"gap"`, `"bounded"`, `"adaptive"`. `max_total_mib` 0 은 물리 RAM 의 4분의 1과 4096 MiB 중 작은 값입니다. |

**트레이스**

| 키 | 기본값 | 뜻 |
|---|---|---|
| `trace_cap_seconds` | 1800 | 첫 트레이스 창의 시간 상한(초, 30~7200). |
| `trace_cap_mib` | 6144 | 트레이스 읽기 상한(MiB, 64~65536). 기본값이면 6144 와 존 상한의 2배 중 큰 값이고, 다른 값은 적은 대로 씁니다. |
| `trace_runs` | 3 | 플레이 횟수가 이 값(1~32) 미만이면 다시 트레이스해 핫 집합을 누적하고 존을 확장합니다. |
| `trace_runs_max` | 8 | 추가 수집 규칙으로 모을 수 있는 유효 트레이스 회차(회차당 64 MiB 이상 읽은 것) 상한(1~32). |
| `trace_merge_min_mib` | 64 | `trace_runs` 뒤에도, 직전 병합이 새 블록을 이 MiB 이상 더했으면 추가 수집합니다(1~65536). |
| `trace_target_coverage` | 70 | 더 이상 추가 수집 판단에 쓰지 않으며 호환을 위해 남은 키입니다. |
| `trace_late_interval_min` | 10 | 첫 창이 끝난 뒤에도 게임이 실행 중이면 이 간격(분)마다 추가 창을 엽니다(1~1440). |
| `trace_late_window_seconds` | 90 | 추가 창 하나의 수집 시간(초, 1~7200). |
| `trace_late_windows` | 6 | 첫 창 뒤에 여는 추가 창의 최대 횟수(0~32). 0이면 추가 수집을 끕니다. |

**재배치**

| 키 | 기본값 | 뜻 |
|---|---|---|
| `idle_seconds` | 30 | 재배치 전에 PC 가 한가해야 하는 시간(초, 10~86400). 예전 키 `idle_minutes`(기본 3)는 적혀 있고 `idle_seconds` 가 없을 때만 씁니다. |
| `relocate_hours` | `[]` | 자동 재배치와 정기 재적용을 시작해도 되는 로컬 시각(시, 0~23, 중복 없음, 최대 24개). 비어 있으면 시간 제한이 없습니다. 설정 탭에서는 자정을 넘길 수 있는 시작~끝 시각 구간 하나로 고칩니다. 파일에 구간이 여러 개 적혀 있으면 그대로 보존하고 "사용자 지정(파일에서 편집)"으로 표시합니다. |
| `max_relocations_per_day` | 4 | 24시간 동안의 재배치 최대 횟수(0~24). |
| `max_relocate_gib_per_day` | 8 | 하루에 재배치로 쓸 수 있는 최대 GiB(0 = 무제한, 최대 1048576). 8 은 기본값으로 취급되어 SMR 디스크는 여전히 2 GiB 를 씁니다. |
| `relocate_chunk_mib` | 4 | 한 번에 옮기는 조각 크기(MiB, 1~256). |
| `relocate_parallel` | 자동 | 동시에 옮기는 파일 수(1~8). 적지 않으면 SMR 로 보이는 드라이브는 1, USB 이거나 디스크 종류를 모르면 2, 그 밖에는 8 입니다. |
| `relocate_io_priority` | `low` | 이동의 I/O 우선순위 힌트: `low` 또는 `normal`. |
| `relocate_verify` | `sample` | 이동 전후 내용 비교: `sample`, `all`, `off`. |
| `relocate_order` | `auto` | 이동 순서: `auto`(`target` 과 같음), `target`, `source`(실험용). |
| `post_update_delay_min` | 15 | 게임 업데이트를 감지한 뒤 그 게임의 재계획·재배치·예열을 이만큼(분, 0~1440) 미룹니다. 0이면 지연 없음. |
| `post_relocate_recovery` | `probe` | 재배치 뒤 같은 드라이브의 다음 작업을 미루는 방식: `probe`(2분마다 짧은 표본을 읽어, 기준 속도의 90% 이상이 연속 2번이면 회복으로 봄), `fixed`(`post_update_delay_min` 분 고정), `off`. |
| `recovery_max_min` | 60 | `probe` 가 회복을 기다리는 최대 시간(분, 1~1440). |
| `recovery_hard_max_min` | 180 | `recovery_max_min` 에서도 회복을 확인하지 못하면 추가 재배치를 이 시간(분, 1~1440)까지 계속 보류합니다. 예열에는 영향이 없습니다. |

**예열 유지와 시작**

| 키 | 기본값 | 뜻 |
|---|---|---|
| `prewarm_recent` | 0 | `0` 은 예산 안에서 가능한 만큼 최근 게임을 예열하고, `1`~`32` 는 게임 수 상한이며, `-1` 은 최근 게임 예열을 끕니다. |
| `startup_warm_games` | 2 | 서비스 시작 때 최근 14일 안에 플레이한 게임 중 이 개수만큼 예열합니다(0~32, 0이면 서비스 시작 예열 없음). |
| `startup_warm_max_mib` | 1024 | 서비스 시작 예열이 한 번에 읽는 합계 상한(MiB, 1~1048576). 넘는 게임은 건너뜁니다. |
| `rewarm_minutes` | 10 | 데이터가 식었는지 보고 다시 예열하는 캐시 점검 간격(0~1440, 0이면 끔). |
| `rewarm_after_exit_min` | 2 | 게임 종료를 감지하면 캐시 점검을 이 시간(분) 뒤로 당깁니다(0~1440, 0이면 끔). |
| `start_stream_enabled` | `true` | `false` 면 게임이 시작될 때 시작 스트림을 하지 않습니다. 설정 탭의 스위치로도 바꿉니다. |
| `restream_cooldown_min` | 10 | 같은 게임의 시작 스트림을 이 시간(분) 안에는 다시 돌리지 않습니다(0~1440). 게임이 이미 실행 중이거나 디스크가 바쁠 때도 건너뜁니다. |
| `start_stream_cap_seconds` | 120 | 시작 스트림의 시간 상한(초, 0~7200). 로딩 경계(10초 이동 창 읽기량이 최고치의 10% 아래로 떨어지는 시점, 최대 60초)에서도 멈춥니다. 0이면 시간 상한이 없습니다. |
| `keep_awake` | `true` | 런처가 떠 있거나 평소 플레이하는 시간대에 10분마다 4 KiB 를 작게 읽어 HDD 가 꺼지지 않게 합니다. Windows 전원 설정은 바꾸지 않습니다. |
| `keep_stream_on_game_start` | `false` | `true` 면 예열 중인 게임이 시작돼도 끝까지 읽습니다(실험용). 평소에는 다음 64 MiB 묶음 경계에서 멈춰 게임에 디스크를 양보합니다. |
| `load_boundary` | `false` | `true` 면 게임 시작 때 로딩 앞부분(tier0)이 RAM 에 들어온 시각을 잽니다. 디스크를 읽으므로 게임이 끝나면 멈춥니다. |

## 업데이트: `update`

| 키 | 기본값 | 뜻 |
|---|---|---|
| `mode` | `auto` | 업데이트 방식: `auto`, `notify`, `off`. |
| `prerelease` | `false` | `true` 면 프리릴리스도 대상으로 합니다. |

설정 탭의 **업데이트** 카드가 `update.mode` 와 `update.prerelease` 를 정합니다.

이전 버전은 `games[].mode`, `games[].paused`, `auto.start_stream_enabled`, `auto.relocate_hours`, `update`, `memory` 키를 거부하므로, 되돌리기 전에 지워야 합니다. `memory` 는 1.0.3 부터 있는 키이며, 1.0.2 이하 버전은 이 키가 파일에 있으면 값과 상관없이 설정 파일 전체를 거부합니다. `memory.standby_ladder` 는 그보다 새 키입니다. 1.0.3 이하 버전은 이 키가 든 설정 파일을 거부하므로 되돌리기 전에 지우십시오.

## 감시기

| 감시기 | 방식 | 권한 | 감지 지연 (S4 실측) | 부담 | 권장 |
|---|---|---|---|---|---|
| `poll` | 50 ms 마다 PID 목록만 받고 새 PID 의 이름만 조회 | 관리자 불필요 | 중앙값 27 ms, p95 58 ms | 유휴 때 한 코어의 약 1% | 기본·권장 |
| `etw` | 커널 프로세스 이벤트를 실시간 세션으로 구독하고, 50 ms 마다 `FlushTrace` 로 버퍼를 내림 | 관리자 필요(서비스는 LocalSystem 이라 가능) | p95 119 ms (플러시 전 1.65 초) | 세션 버퍼 메모리 | 실패하면 폴링으로 자동 복귀 |

설정 탭에서 바꾸며, 저장 값은 `poll`/`etw`, 화면 라벨은 `poll (권장)`/`etw` 입니다.

## 언어

관리 창과 `aster` 명령행은 한국어와 영어로 쓸 수 있습니다.

- 언어는 `config.json` 의 `"language"` 로 정합니다. `"auto"`(기본, Windows 표시 언어를 따르며 한국어가 아니면 영어), `"ko"`, `"en"` 입니다. 관리 창 **설정 탭의 언어** 항목에서도 바꿀 수 있습니다.
- 명령행은 전역 옵션 `--lang ko|en` 과 환경변수 `ASTER_LANG` 을 받습니다. 우선순위는 `--lang` > `ASTER_LANG` > `config.json` > OS 언어 입니다.
- JSON 출력은 언어와 무관합니다.
