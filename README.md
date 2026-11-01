# AsterCache v0.7 — SSD 영속 블록 캐시 실험기

**현재 상태: 사용자 모드 / 명시적 호출 API / Linux 컨테이너 검증.**

> **아직 기존 Windows 게임을 자동으로 가속하지 않습니다.** Python API `SQLiteBlockCache.read()`를 통해 접근하는 프로그램만 이 SSD 캐시를 사용할 수 있습니다. `ram-warm`은 OS 파일 캐시가 채워질 가능성을 이용하지만, RAM 상주나 게임 로딩 개선을 보장하지 않습니다. Windows 드라이버/실제 HDD/SSD 벤치마크는 포함되지 않습니다.

## v0.6에서 바뀐 점

- 파일 블록마다 `.bin + .json`을 만드는 방식 대신 하나의 SQLite 파일에서 블록과 SHA-256을 **동일 트랜잭션**으로 저장합니다. WAL + `synchronous=FULL`을 사용합니다.
- 캐시 데이터에 오류가 있으면 원본에서 다시 읽으며, 디스크 여유가 부족하거나 캐시 쓰기 오류가 나도 **원본 읽기 경로를 우선** 사용합니다.
- 읽은 원본 파일의 파일 정체성/크기/시간을 열려 있는 핸들과 경로에서 읽기 전후 비교합니다. 외부 쓰기의 모든 경우를 탐지하는 일관성 프로토콜은 아닙니다.
- 최근 읽은 블록을 다음 적재 트랜잭션 시 함께 갱신하여 단순 FIFO로 축출되는 문제를 완화했습니다.
- 학습 계획에서 RAM(운영체제 파일 캐시) 또는 API 전용 SSD 캐시로 예열할 수 있는 명령을 분리했습니다.
- 저장 공간 제한은 **논리적 캐시 페이로드 바이트** 기준입니다. SQLite 데이터베이스·WAL·파일시스템 오버헤드로 물리 공간 사용량이 예산을 초과할 수 있습니다.

## 실행 요구조건

- Python 3.10 이상, pip 의존성 없음
- 소스와 캐시 경로는 **별도 디렉터리 트리**여야 합니다. 실제 속도 검증 시 서로 다른 물리 HDD/SSD가 필요합니다.
- Windows용 실기 검증 이전에는 중요 게임 데이터나 시스템 드라이브의 설정 변경에 사용하지 마세요. 테스트 폴더에서 시작하세요.
- Linux 파일 IO와 Windows NTFS I/O는 동등하지 않습니다.

## 검증 실행

```bash
python -m unittest discover -s tests -v
python repeat_validation.py
python stress.py
python integration_smoke.py
python bench_api.py
```

`repeat_validation.py`는 35개 검사를 **새 Python 프로세스에서 10번 반복**합니다. `stress.py`는 50개 시드 × 시드당 250개의 파일/캐시 연산을 실행하며 읽기 바이트를 대조합니다. `bench_api.py`는 OS 페이지 캐시가 통제되지 않은 컨테이너 마이크로벤치마크입니다. **물리 HDD/SSD의 속도 비교가 아닙니다.**

## Windows CLI 개념 실험

Microsoft Process Monitor에서 게임 실행마다 `ReadFile` 성공 이벤트를 CSV로 내보낸 다음, 기존 실행 3개로 학습하고 다음 실행 CSV로 따로 평가합니다. 파일 경로가 실제 Windows PC에 존재하는지 확인한 후 테스트용으로 사용합니다.

```powershell
# Windows에서만 실제 실행 경로로 바꿔주세요.
python astercache_v07.py plan --csv .\run1.csv .\run2.csv .\run3.csv --prefix "D:\Games" --budget-mib 256 --out .\plan.json
python astercache_v07.py evaluate --plan .\plan.json --csv .\run4.csv --prefix "D:\Games"
python astercache_v07.py inspect --plan .\plan.json --origin-root "D:\Games" --budget-mib 256

# 원본을 변경하지 않는 RAM 예열: 실제 OS 캐시 효과는 Windows에서 측정해야 함
python astercache_v07.py ram-warm --plan .\plan.json --origin-root "D:\Games" --budget-mib 256 --execute

# 테스트용 SSD 캐시: 이 데이터를 기존 게임 프로세스가 자동으로 읽지는 않음
python astercache_v07.py ssd-warm --plan .\plan.json --origin-root "D:\Games" --cache-dir "C:\AsterCacheTest" --budget-mib 256 --execute
```

중요: `--execute`가 없으면 예열하지 않으며 계획만 표시합니다. Windows의 파일 수집·경로 규칙은 아직 실기 검증 전이므로, **처음에는 비중요 테스트 파일에서만 실행**하세요. `--execute`는 원본 파일을 수정하지 않지만 디스크 읽기 부하와 SSD 캐시 파일 쓰기가 발생합니다. 사용 가능한 SSD 공간을 충분히 남겨두세요.

PC 환경 정보는 별도 읽기 전용 `windows_diagnostics.ps1`에서 확인할 수 있습니다.

## 새 파일

- `sqlite_cache.py` — 데이터/해시 트랜잭션, 블록 캐싱, 원본 검증, 축출, 장애 폴백
- `astercache_v07.py` — 추적 계획·검증·RAM/SSD 예열 명령
- `session_planner.py`, `procmon_import.py` — 이전 v0.6 알고리즘을 가져온 구성요소, 실제 Windows 호환성 미확인
- `blockcache_v06.py` — 이전 방식과의 비교용 보존 코드
- `tests/` — 신규 명명된 검사 86~120 (35개)
- `stress.py`, `integration_smoke.py`, `bench_api.py`, `repeat_validation.py` — 검증·마이크로벤치마크
- `results/` — 이번 실행 결과

## 다음 단계와 남은 위험

1. **Windows OS 위에서** 파일 ID/ChangeTime 조회, SQLite DB의 SSD 성능, Procmon CSV 원본 파서 확인.
2. 동일 게임의 과거 3~5회·다음 실행 기록을 분리하고, 사전 예열 소요시간을 포함한 로딩 시간을 비교.
3. 별도 VM/VHDX에서 OS 필터·블록 읽기 대체와 쓰기 무효화, 메모리 매핑/페이징 I/O/DirectStorage, BitLocker 호환성 검증.
4. 파일/게임 원본 위치를 건드리지 않고 투명하게 가속하려면 아직 미구현인 실제 Windows I/O 경로가 필요함.

**보안/정확성 경고:** timestamp, 크기, inode, Windows ChangeTime은 모두 파일 변경 판정에 유용하지만, 외부의 악의적 수정 또는 모든 동시 쓰기를 탐지하는 완전한 방어책이 아닙니다. SHA-256은 캐시 자체의 우발적 손상 검출용이며 원본 파일과의 강제 재해시를 수행하지 않습니다. 캐시 DB가 시작부터 심각하게 손상된 경우 초기화가 실패할 수 있습니다. 쓰기 중 SSD 손상/전원 차단을 실제 하드웨어에서 시험한 것은 아닙니다.

## 기술 근거

- Microsoft: https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching
- Microsoft: https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/bypassio
- Microsoft: https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/filter-manager-concepts
- SQLite WAL: https://sqlite.org/wal.html
- SQLite synchronous: https://sqlite.org/pragma.html#pragma_synchronous
