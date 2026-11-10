[English](CONFIGURATION.md) | **한국어**

# 설정

> `config.json` 의 키와 감시기, 화면 언어를 설명합니다. 자동 모드 키는 [AUTO_MODE.ko.md](AUTO_MODE.ko.md) 에 있습니다.

설정 파일은 `%ProgramData%\AsterCache\config.json` 입니다. 관리 창에서 편집하는 것을 권장합니다. 직접 고칠 때는 JSON 의 백슬래시를 `\\` 로 씁니다.

ORBIT 는 `config.json` 이 아니라 CLI 로 켜고 끕니다(`aster orbit mode off|shadow|active`). [ORBIT: 예열 순서 조정](ORBIT.ko.md) 을 보십시오.

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

| 키 | 뜻 |
|---|---|
| `language` | 화면 언어. `auto`(기본), `ko`, `en`. `auto` 는 Windows 표시 언어를 따르며 한국어가 아니면 영어를 씁니다. |
| `watcher` | 게임 시작 감지 방식. `poll`(기본) 또는 `etw`. `etw` 는 관리자 권한이 필요하며 실패하면 폴링으로 내려갑니다. |
| `games[].name`, `exe_names` | 게임 이름과 실행 파일 이름 목록 |
| `trace`, `plan` | 추적 파일과 계획 파일 경로 |
| `origin_root` | 게임 설치 폴더 |
| `allowed_roots` | 재배치를 허용하는 폴더 |
| `volume_label` | 대상 볼륨 레이블. 다르면 재배치하지 않습니다. |
| `max_stream_mib` | 한 번의 스트림·예열이 읽는 상한(기본: 4096과 존 상한 중 큰 값, 명시값 우선) |
| `prewarm.on_service_start` | 서비스 시작 때 예열 실행(기본 `true`) |
| `prewarm.trigger_exes` | 이 프로그램이 뜨면 예열 실행(예: 런처). 기본은 비어 있음 |
| `prewarm.min_free_ram_mib` | 예열 뒤에도 남겨 둘 가용 RAM(기본 4096) |
| `prewarm.cooldown_min` | 예열 사이의 최소 간격(기본 30분) |
| `auto` | 자동 모드 설정. 생략한 키는 기본값을 씁니다. [자동 모드](AUTO_MODE.ko.md) 를 보십시오. |
| `max_zone_mib` | 자동 계획의 존 크기 상한(MiB, 기본 2048), 또는 `"auto"`(물리 RAM ÷ 4, 2048~8192 MiB 사이이며, 디스크 종류를 모르거나 USB 이면 2048 상한). 32 GB PC 는 고정 2048 보다 `"auto"` 를 권장합니다. |
| `games[].source` | 자동으로 찾은 게임은 `auto`, 직접 등록한 게임은 `manual` |
| `games[].anticheat` | 설치 폴더에서 찾은 안티치트 흔적(예: `EasyAntiCheat`). 게임 탭에 표시되며 동작은 바꾸지 않습니다. 안티치트 게임의 정책과 이 PC 의 사용 기록은 [안티치트 호환](ANTICHEAT.ko.md)을 보십시오. |
| `games[].display_name` | 화면에 보일 게임 이름(선택). 없으면 서비스가 남긴 런처 이름, 그것도 없으면 키의 밑줄을 공백으로 바꾼 이름을 씁니다. 내부 키는 그대로입니다. |
| `games[].display_names` | 언어별 화면 이름(선택). 예: `{"ko": "발로란트", "en": "VALORANT"}`. 키는 `ko`, `en` 만 됩니다. |
| `auto.ssd_tier` | 선택 기능인 SSD 보조 저장: `enabled`(기본 `false`), `budget_gib`(기본 8), `root`(기본 `"auto"`). [SSD 보조 저장 (선택)](AUTO_MODE.ko.md#ssd-보조-저장-선택)을 보십시오. |
| `auto.trace_runs` | 기본 3. 플레이 횟수가 이 값 미만이면 다시 트레이스해 핫셋을 누적하고 존을 확장합니다. |
| `auto.trace_target_coverage` | 기본 70. 더 이상 추가 수집 판단에 쓰지 않으며 호환을 위해 남은 키입니다. `trace_runs` 뒤에도, 직전 병합이 새 블록을 `trace_merge_min_mib` 이상 더했고 유효 회차가 `trace_runs_max` 미만이면 추가 수집합니다. |
| `auto.trace_runs_max` | 기본 8. 추가 수집 규칙으로 모을 수 있는 유효 트레이스 회차 상한(회차당 64 MiB 이상 읽은 것만 셈). |
| `auto.restream_cooldown_min` | 기본 10. 같은 게임의 시작 스트림을 이 시간(분) 안에는 다시 돌리지 않습니다. 게임 프로세스가 이미 실행 중이거나 디스크가 바쁠 때도 건너뜁니다. |
| `auto.rewarm_after_exit_min` | 기본 2. 게임 종료를 감지하면 재예열 점검을 이 시간(분) 뒤로 당깁니다. |
| `auto.start_stream_cap_seconds` | 기본 120. 시작 스트림의 시간 상한(초)입니다. 로딩 경계(10초 이동 창 읽기량이 최고치의 10% 아래로 떨어지는 시점, 최대 60초)에서도 멈춥니다. 0이면 시간 상한이 없습니다(옛 동작). |
| `auto.trace_late_interval_min` | 기본 10. 트레이스 첫 창(`trace_cap_seconds`)이 끝난 뒤에도 게임이 실행 중이면, 이 간격(분)마다 추가 창을 엽니다. |
| `auto.trace_late_window_seconds` | 기본 90. 추가 트레이스 창 하나의 수집 시간(초)입니다. |
| `auto.trace_late_windows` | 기본 6. 첫 창 뒤에 여는 추가 창의 최대 횟수입니다. 0이면 추가 수집을 끕니다. 읽기 상한은 세션 누적에 적용되고, 저장·병합은 게임이 끝난 뒤에만 합니다. |
| `auto.load_boundary` | 기본 `false`. `true` 면 게임 시작 때 로딩 앞부분(tier0)이 RAM 에 들어온 시각을 잽니다. 디스크를 읽으므로 게임이 끝나면 멈춥니다. |
| `auto.max_relocate_gib_per_day` | 기본 8. 하루에 재배치로 쓸 수 있는 최대 GiB(0 = 무제한, 0~1048576). 8 은 기본값으로 취급되어 SMR 디스크는 여전히 2 GiB 를 씁니다. |
| `riot.champ_select_prewarm` | 기본 `false`. Riot 게임의 챔피언 선택 중 로컬 리그 오브 레전드 클라이언트 API로 고른 챔피언·맵 파일을 미리 RAM에 읽습니다. 한국 계정 LCU 정책은 미확인입니다. |
| `riot.champ_select_budget_mib` | 기본 2048. 챔피언 선택 선읽기가 읽을 수 있는 최대 MiB. |
| `prewarm.pin` | 예열한 데이터를 서비스가 읽기 전용 매핑으로 열어 둡니다. 설정에 적지 않으면 물리 RAM 24 GiB 이상에서 자동으로 켜지고, `false` 는 끕니다. 명시한 `true` 는 RAM 16 GiB 이상에서 동작합니다. 효과 미확인(실험 필요). [안티치트 호환](ANTICHEAT.ko.md) 을 보십시오. |

서비스는 `config.json` 전체를 5초마다 다시 읽습니다(크기나 수정 시각이 바뀔 때). 그래서 게임을 등록·제거하거나 감시기를 바꿔도 다시 시작하지 않아도 반영됩니다. 새 설정이 검증이나 폴더 권한 검사에 실패하면 옛 설정을 유지하고 이유를 로그에 남깁니다.

`games[].display_name` 과 `display_names` 는 손으로 넣을 수 있는 키입니다. 이 키가 든 설정을 beta.24 로 되돌리면 그 버전이 설정을 거부하므로(종료 코드 2), 되돌리기 전에 두 키를 지워야 합니다.

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
