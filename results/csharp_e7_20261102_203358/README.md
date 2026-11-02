# E7 첫 실행 실시간 선행 읽기 (`aster learn`, 0.9.0-beta.4) - 합성 데이터셋

실행 2026-11-02 20:33~20:40. 코드는 수정하지 않았고 커밋하지 않았습니다. 원자료: `summary.json`, `progress.log`, `A*/B*-replay.txt`, `B*-learn.txt`.

## 판정 (먼저)
1. **선행 읽기는 도움이 되지 않았고 오히려 재생을 약 2배 느리게 했습니다.** A(없음) 중앙값 21.67 s, B(learn) 중앙값 44.90 s, 배수(A/B) **0.48**.
2. **ETW 실시간 경로는 절반만 동작합니다.** 열기 알림(OnOpen)은 오고 읽기 알림(OnRead)은 한 번도 오지 않습니다 (`game_read_bytes` 0, 적중률 0).
   - 선행 읽기 1.67~1.84 GB 는 전부 "열린 파일(64 MiB 이하) 통째로 읽기" 규칙에서 나왔습니다. 재생이 64개 파일을 모두 열기 때문에 데이터셋 2.1 GB 전체를 읽어 재생의 실제 읽기(262 MB)와 디스크를 다퉜습니다.
3. **원인 위치(코드는 고치지 않음):**
   - `csharp/src/AsterCache.Tracer/KernelCollector.cs` 104~119행 `OnRead`: 읽는 순간 파일 이름이 안 풀리면 `_pending` 에만 넣고 리스너를 부르지 않음.
   - `csharp/src/AsterCache.Tracer/FileNames.cs` `AtRead`: FileKey 이름(FileIOName/FileCreate/FileRundown)이 있어야 즉시 해석. 실시간 세션에서는 이 이름이 정리(rundown) 때에야 와서 읽기가 모두 보류됨. FileObject 이름(FileIOCreate)은 보조로만 쓰는 설계(파일 객체 재사용 오분류 때문)이라 이 경로에서도 쓰이지 않음.
   - `csharp/src/AsterCache.Prefetch/ReadAhead.cs` `Step()`: 열기 알림이 오면 크기 64 MiB 이하 파일을 통째로 큐에 넣음. 읽기 알림이 없으면 이것이 유일한 선행 읽기가 됨.
   - 증거: 단독 실행(`--json --out`)에서 `events` 65 (= 열기 64 + 1), `trace_events` 4699(읽기 기록은 4000건 넘게 있음, 종료 때 이름 해석된 3722행이 `diag_learn_trace.json` 에 저장), 그러나 `game_read_bytes` 0.

## 측정 (콜드 purge, 교대 A,B,B,A,A,B, 재생 4,000건/262 MB)

| 구분 | 1회 | 2회 | 3회 | 중앙값 |
|---|---|---|---|---|
| A 직접 재생 | 21.67 s | 21.49 s | 23.83 s | **21.67 s** |
| B learn 병행 | 45.35 s | 44.90 s | 43.44 s | **44.90 s** |

| B | read_ahead_bytes | game_read_bytes | hit_ratio | events (열기 알림) | events_lost |
|---|---|---|---|---|---|
| 1 | 1,775,239,168 | 0 | 0.0 | 65 | 0 |
| 2 | 1,673,913,923 | 0 | 0.0 | 65 | 0 |
| 3 | 1,839,202,304 | 0 | 0.0 | 65 | 0 |

- 파일 이름 해석 비율: 읽기 이벤트가 실시간으로 해석돼 엔진에 전달된 비율은 0%. 종료 때(Stop) 해석된 읽기는 `trace_events` 4699 중 3722행이 root 아래 파일(재생 읽기 4000건, 연속 읽기 병합 가능성 있음)이었음. 해석 실패 건수는 CLI 출력에 없음.
- 지정 root 밖의 python 읽기는 무시됨. 측정 중 다른 python 프로세스 11개가 떠 있었음(대시보드, AriTTS 등). learn 이 "프로세스 모두 종료"로 끝나지 못해 `--seconds 45` 에서 끝났음. 양쪽 팔에 같은 영향.
- 설치된 서비스(beta.3)는 중지하지 않았고, `service.log` 에 python 이나 `AsterBench_e7` 관련 줄이 없음(게임으로 오인하지 않음).

## DiskIo 캡처 (B 1회, `diskio_B.etl`, 디스크 0, 창 46.5 s)
- 읽기 3,803건, 2,187.8 MB. 재생이 요청한 양은 262 MB 이므로 물리 읽기의 약 88%가 learn 의 선행 읽기(및 캐시 관리자 확대 읽기)입니다.
- 크기: 64 KiB 이하 1,482건(79 MB), 64 KiB~1 MiB 773건(217 MB), 정확히 1 MiB 1,272건(1,334 MB), 1 MiB 초과 276건(558 MB).
- 프로세스별 분리는 신뢰할 수 없어 싣지 않습니다(ReadInit 와 연결되는 이벤트가 전체의 약 6%, 시스템(PID 4)이 1.14 GB 발행).
- `cs_e7_learn.ps1` 안의 wpr 캡처는 경로 문자열 오류(`\a` 이스케이프)로 실패해 스크립트를 고쳤고, 같은 캡처를 `cs_e7_diag.ps1` 로 따로 실행했습니다(learn `--out` 포함, 재생 37.4 s).

## 데이터셋 (`D:\AsterBench_e7`, 남겨 둠)
- `bench/dataset_scatter.py`: 기존 생성기와 같은 파일 구성(64개, 2,135,949,312 B)·같은 트레이스 형식으로 1 MiB 씩 번갈아 써서 만들었습니다.
- 조각: 파일당 4~7개, 전체 350개. 트레이스 핫 구간(합친 연속 구간) 948개, 핫 243.5 MB, LCN 15.5~79.9% 범위에 흩어짐(`dataset_fragmentation.json`). `aster probe` 대신 `FSCTL_GET_RETRIEVAL_POINTERS` 로 확인.
- 디스크 여유 430 GB.

## 못 한 것·위험
- 합성 데이터셋은 무작위 읽기라서 앞쪽 읽기 이득이 원래 작습니다. 실제 게임(Frostpunk)은 쓰지 않았으므로 "실제 게임에서도 느려진다"는 결론은 아닙니다. 다만 OnRead 미동작은 데이터셋과 무관한 결함으로 보입니다(실제 `.etl` 의 읽기 이벤트가 이름 없이 도착하는 구조).
- 이름 해석을 고친 뒤 재측정하면 결과가 달라집니다(적중이 생길 수 있음). 이번 수치는 beta.4 미커밋 코드 기준입니다.
- 측정 중 다른 python 프로세스가 떠 있어 A/B 에 같은 잡음이 있을 수 있습니다.

## 만든 파일
`bench/dataset_scatter.py`, `csharp/scripts/cs_e7_diag.ps1`, `csharp/scripts/cs_e7_learn.ps1`(수정: 다른 python 허용, wpr 캡처 추가, 경로 오류 수정).
