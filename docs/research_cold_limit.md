# 예열 없는 콜드 로딩의 한계 조사

조사일: 2026-11-02. 대상: AsterCache(핫 집합 물리 재배치 + 존 순서 선읽기 + RAM 예열).
전제 실측(README): Frostpunk 핫 집합 2.02 GB, WD20EZAZ(2 TB, DM-SMR) 순차 약 135~140 MB/s, 예열 없는 첫 실행 바닥 15.1 s, 예열 후 0.48 s, NVMe 콜드 3.8 s. 안티치트 규칙은 게임 프로세스 무접촉, 드라이버 없음.

## 조사 방법과 한계 (먼저 읽을 것)

- 이번 조사에서는 검색 엔진을 쓸 수 없었습니다(자동 조회가 봇 확인 화면에서 막힘). 그래서 주소를 알고 있는 1차 자료(Microsoft Learn, 논문 PDF, GitHub 저장소와 API, 위키백과 API, 학술 색인 API)를 직접 읽었습니다.
- 그 결과 확인된 수치는 "확인"으로, 근거를 못 찾은 것은 "추정" 또는 "미확인"으로 표시했습니다. 리뷰 사이트의 HD Tune 곡선, 제조사 데이터시트의 WD20EZAZ 순차 전송률, HDD/SATA/NVMe 로딩 시간 비교 기사는 접근하지 못해 수치를 싣지 않았습니다.
- 용어: WOF = Windows Overlay Filter(투명 압축 필터). ADS = NTFS 대체 데이터 스트림. DM-SMR = 장치 관리형 기와식 기록.

## 핵심 결론 (3줄)

1. 순차 읽기 바닥(15.1 s)은 "읽는 바이트 수 / 존의 순차 속도"입니다. 이를 내리려면 (가) 바이트를 줄이거나(압축, 중복 제거), (나) 더 빠른 존이나 매체에서 읽거나(바깥 트랙, SSD), (다) 시간을 사용자가 기다리지 않는 구간으로 옮기는 수밖에 없습니다. 읽기 순서 최적화는 이미 바닥에 닿아 있습니다.
2. 사용자 모드, 무드라이버, 게임 무접촉 안에서 배수가 큰 것은 SSD 계층(핫 파일을 SSD로 복사하고 원래 경로를 링크로 연결)뿐이며 최대 약 4배(15.1 s → NVMe 3.8 s 수준)입니다. 그 외는 1.0~1.25배 수준의 조정입니다.
3. Frostpunk 는 WOF 압축률이 거의 0%라서(커뮤니티 데이터 0.99~1.00) 이 게임에서 압축은 효과가 없습니다. 압축은 게임별로 갈립니다.

---

## 1. WOF 투명 압축

요약
- 알고리즘은 XPRESS4K/8K/16K 와 LZX 이며, `compact /exe:<알고리즘>` 으로 적용합니다. 기본은 XPRESS4K(가장 빠름), LZX 가 가장 작습니다(Microsoft compact 명령 문서).
- 압축 파일은 세 부분으로 저장됩니다: WOF 리파스 포인트, 내용이 전부 0인 희소 무명 데이터 스트림(크기=원본 크기), 실제 압축 데이터가 든 이름 있는 스트림 `WofCompressedData`. 압축 데이터는 청크(4K/8K/16K 단위) 연쇄이고 앞에 청크 오프셋 표가 붙어 청크 단위 임의 접근이 가능합니다. 압축되지 않는 청크는 원본 그대로 저장됩니다(ntfs-3g 시스템 압축 플러그인 소스 주석).
- 압축률 데이터(CompactGUI 커뮤니티 데이터베이스 15,656개 항목을 직접 집계, 1 GB 이상 + 제출 3건 이상): 가중 평균 압축 후/전 = XPRESS4K 0.860, XPRESS8K 0.848, XPRESS16K 0.844, LZX 0.821. 즉 평균 14~18% 감소입니다. 분포의 중앙값은 0.81~0.85, 하위 25%(잘 줄어드는 쪽)는 0.56~0.67, 상위 25%는 0.93~0.96 입니다. 게임별 편차가 큽니다.
- I/O 바운드 구간에서 이상적 속도 이득은 1/압축률입니다: 평균 1.16~1.22배. Frostpunk 2.02 GB 를 평균 압축률(0.85)로 가정하면 15.1 s → 약 12.8 s(추정). 단 Frostpunk 자체의 DB 값은 0.99~1.00 이라 실제는 15.0 s 안팎입니다.
- 해제 처리량: 같은 XPRESS-Huffman 형식의 오픈소스 구현이 Core i7-3720QM(2013년 노트북 CPU)에서 약 310 MB/s(단일 스레드, 출력 기준)이며 Windows 내장 구현은 그보다 약 1.28배 빠르다고 보고됩니다(ms-compress 저장소, 속도비 0.78배에서 환산). HDD 135 MB/s 에서 압축률 0.85 라면 출력 기준 약 159 MB/s 가 필요하므로 CPU 가 병목이 되지는 않습니다. LZX 의 해제 속도 수치는 근거를 찾지 못했습니다(미확인).
- 안티치트 보고: 게임이 파일 압축 여부를 확인할 수는 있지만 열기/읽기는 투명하다는 개발자 답변이 있고, 압축 때문에 차단당했다는 보고는 이 저장소 이슈 검색(BattlEye, EasyAntiCheat, Vanguard 로 조회)에서 0건이었습니다. 다만 이는 "보고가 없다"일 뿐 보증이 아닙니다(추정). 알려진 비호환은 Guild Wars 2(실행 시 전체 해제), Secret World Legends, Ghost Recon Wildlands(메인 메뉴에서 멈춤), LOTR Online(패치 멈춤)입니다. DirectStorage 쓰는 게임은 쓰지 말라고 개발자가 경고합니다.
- Steam 상호작용: 파일을 Steam 이 갱신하면 그 파일은 압축이 풀려 "압축 풀림(decay)"이 생깁니다(CompactGUI FAQ). 압축 도중 Steam 업데이트가 겹쳐 파일이 깨진 사례(이슈 #591)가 있고, 사용자는 Steam 무결성 검사를 반복해 복구했습니다.
- 데이터 위치와 이동 가능성: 읽히는 실제 바이트는 `WofCompressedData` 스트림에 있으므로, 트레이스(ETW 디스크 읽기)에는 이 스트림의 물리 클러스터가 잡힙니다. FSCTL_MOVE_FILE 은 문서상 모든 파일에서 `::$DATA`, `::$REPARSE_POINT` 등 속성 유형을 이동할 수 있고, 열 때 `파일:이름:유형` 구문을 쓰도록 되어 있습니다(Microsoft Learn, 조각 모음 문서). 따라서 `파일:WofCompressedData:$DATA` 핸들로 이동이 가능할 것으로 보이나 직접 확인한 근거는 없습니다(추정, 실험 필요). 희소/NTFS 압축 파일은 이동 단위가 16 클러스터라는 제약이 문서에 있는데 WOF 스트림에 적용되는지도 미확인입니다.
- 핫 집합 매핑: 원본 파일 오프셋 → 압축 스트림 위치는 청크 오프셋 표를 읽어 계산해야 합니다(형식은 위 소스 주석에 공개). 트레이스가 물리 위치 기준이면 변환 없이 쓸 수 있습니다(추정).
- 예열: 버퍼드 읽기나 PrefetchVirtualMemory 로 올린 페이지는 해제된 원본 내용으로 캐시에 남는지 직접 확인한 문서가 없습니다. WOF 는 파일 시스템 필터이므로 캐시 매니저는 "원본 파일의 해제된 데이터"를 캐시한다고 보는 것이 자연스럽습니다(추정). 그러므로 예열 시 해제 CPU 비용은 예열 단계에서만 듭니다.

수치표(CompactGUI 커뮤니티 DB, 압축 후/전 비율)

| 게임 | XPRESS4K | XPRESS8K | XPRESS16K | LZX | 제출 수(LZX) |
|---|---|---|---|---|---|
| 전체 가중 평균(1 GB 이상) | 0.860 | 0.848 | 0.844 | 0.821 | 2,073 항목 |
| Frostpunk | 1.00 | 0.99 | 1.00 | 0.99 | 12 |
| Frostpunk 2 | 0.87 | 0.86 | 0.86 | 0.85 | 3 |
| Counter-Strike 2 | 0.86 | 0.86 | 0.83 | 0.84 | 681 |
| Cyberpunk 2077 | 0.99 | 0.97 | 0.98 | 0.97 | 108 |
| Elden Ring | 0.98 | 1.00 | 0.99 | 0.99 | 48 |
| Red Dead Redemption 2 | 0.98 | 0.97 | 0.97 | 0.96 | 106 |
| Rainbow Six Siege | 0.94 | 0.94 | 0.94 | 0.90 | 76 |

(열거값 0~3 이 4K, 8K, 16K, LZX 순이라는 대응은 압축률이 단조롭게 좋아지는 것으로 추정한 것입니다. 소스에서 열거 정의 자체는 확인하지 못했습니다.)

HDD 에서 로딩 시간이 줄었다는 실측 보고: 제작자가 "느린 HDD 에서는 작아진 파일 전송 덕분에 성능이 오를 수도 있다"고 서술하지만(CompactGUI README/FAQ), 구체 시간 측정 자료는 찾지 못했습니다(미확인).

## 2. 드라이브 최대 처리량

요약
- WD20EZAZ 의 제조사 순차 전송률은 데이터시트를 열지 못해 확인하지 못했습니다(미확인). 사용자 실측 135~140 MB/s 를 기준으로 삼습니다.
- 바깥 트랙이 가장 빠르고, 트랙 번호는 바깥쪽에서 안쪽으로 매기며 운영체제는 낮은 번호부터 채웁니다(위키백과 Zone bit recording). 같은 문서가 "새 드라이브나 조각 모음 직후 벤치마크가 최고 성능을 보이고 데이터가 안쪽에 쌓일수록 평균이 떨어진다"고 서술합니다. 바깥/안쪽 비율의 구체 수치(통상 약 2배라는 통념)는 인용 가능한 출처를 못 찾았습니다(추정).
- LBA 0 이 바깥이라는 통념의 근거: 위 문서. 단 기와식(SMR) 드라이브는 LBA 와 반경이 단순 단조가 아닐 수 있습니다. Seagate 데스크톱 SMR 드라이브(ST5000AS0011)를 고속 카메라와 지연 측정으로 분석한 논문은, 헤드가 한 표면에서 바깥에서 중간으로 넓게 이동한 뒤 다음 표면으로 점프하는 "수평 존" 구조와, 처리량이 높은 LBA 로 갈수록 떨어지되 반복 패턴이 나타남을 보였습니다(Skylight, FAST 2015). WD20EZAZ 가 같은 구조인지는 미확인입니다(추정: 달라도 낮은 LBA 가 빠른 경향은 유지).
- SMR 읽기: 같은 논문에서 캐시되지 않은 데이터는 CMR 과 같은 정적 LBA→물리 매핑이라 읽기 특성이 같다고 합니다. 문제는 "최근 기록된 데이터"입니다. 영구 캐시(Seagate 5 TB 20 GiB, 8 TB 25 GiB, 즉 용량의 약 0.3~0.4%)는 바깥 지름(OD)에 있고 저널 형태로 쓰이며, 정리(cleaning)가 끝나기 전의 데이터는 캐시 쪽에서 읽힙니다. 밴드 크기는 15~40 MiB, 한가한 시간에 적극적으로 정리합니다(밴드당 0.6~1.6 s). WD20EZAZ 의 영구 캐시 크기와 위치는 공개 자료를 못 찾았습니다(미확인). 호스트가 LBA 로 그 영역을 지정할 수 있는지도 근거가 없습니다. DM-SMR 은 호스트에게 투명하게 보이도록 설계되어(위키백과 SMR), 지정 수단이 표준으로는 없다고 봅니다(추정).
- 함의(추정): 재배치(FSCTL_MOVE_FILE)가 쓰기를 대량으로 일으키므로, 직후에는 데이터 일부가 영구 캐시에 저널 조각으로 남아 순차 읽기가 느릴 수 있습니다. 유휴로 정리가 끝난 뒤 측정해야 하고, "재배치 직후 바닥"과 "정리 후 바닥"이 다를 수 있습니다.
- 큰 비버퍼드 읽기(4~16 MiB, 큐 2~8)가 1 MiB 보다 빠른가: 근거 문서를 찾지 못했습니다(미확인). 이론상 HDD 순차 처리량은 수백 KiB 이상에서 포화하므로 이득은 수 % 이내로 추정합니다. Microsoft 문서는 캐시 매니저가 256 KB 단위 슬롯으로 읽는다고 설명합니다(File Caching). 직접 벤치마크로 확인하는 것이 빠릅니다.
- FILE_FLAG_SEQUENTIAL_SCAN: 캐시 최적화 힌트이며 역방향 읽기에는 쓰지 말라고 문서가 경고합니다(CreateFile). 선읽기 크기의 상한 수치는 문서에 없습니다.

## 3. SSD 계층화(사용자 모드)

요약
- 방식: 핫 파일을 SSD 로 복사하고 원래 경로에 심볼릭 링크(또는 디렉터리 정션)를 둡니다. 리파스 포인트는 파일 또는 디렉터리 전체에 붙고 필터가 해석하는 사용자 정의 데이터이므로(Microsoft Learn, Reparse points) 파일의 일부 블록 범위만 다른 매체로 대체하는 것은 불가능합니다. 핫 집합이 큰 팩 파일 안의 일부 범위라면 파일 전체(수 GB~수십 GB)를 SSD 에 두어야 합니다.
- 선례: Steam 라이브러리 이동/정션 도구가 존재한다는 점은 알려져 있으나 이번 조사에서 저장소를 확인하지 못했습니다(미확인).
- 안티치트와 Steam 검증: 직접 근거 없음(미확인). 추정: 심볼릭 링크 경로를 안티치트가 파일 경로 정규화 과정에서 거부하거나 파일 무결성 검사에서 문제 삼을 가능성이 있고, Steam 업데이트가 링크를 일반 파일로 덮어써 계층이 풀릴 수 있습니다.
- 기대 효과(추정): 콜드 15.1 s → NVMe 콜드 실측 3.8 s(README) 수준이므로 최대 약 4배. SATA SSD 는 순차 약 500 MB/s 대라 2.02 GB 가 약 4~5 s(추정).
- 대가: SSD 용량 소모, 파일 단위 복사로 "복사 없음, 제자리 이동" 원칙 위배, 게임 업데이트 시 재동기화 필요.

## 4. 그 밖의 사용자 모드 기법

요약
- Windows 프리페처는 응용 시작 후 첫 10초의 접근을 기록해 다음 실행에 쓰고, 3일마다 작업 스케줄러가 디스크 조각 모음을 호출해 부팅/응용 접근 파일 목록을 연속 배치합니다(위키백과 Prefetcher). 즉 AsterCache 의 "접근 순서대로 모으기"와 같은 발상이 Windows 에 이미 있었습니다. SuperFetch/ReadyBoost 는 SSD 가 시스템 디스크면 꺼집니다(위 문서, FAST 2011 논문도 같은 서술).
- 응용 시작 가속 학술 결과(FAST 2011, Joo 외): HDD 에서 응용 시작은 콜드일 때 시간의 약 85% 를 디스크 접근에 쓰며, "연산과 I/O 중첩"만으로는 평균 16% 감소에 그쳤고, LBA 정렬 선읽기(sorted prefetch)는 평균 40% 감소, 응용의 블록을 접근 순서로 재배치하는 조각 모음 도구는 탐색 거리를 줄인다고 설명합니다. 같은 논문에서 SSD 에서는 HDD 용 방식이 7% 감소에 그쳤고 SSD 전용 방식은 28% 감소였습니다.
- 게임 로딩이 디스크 바운드인가 CPU 바운드인가: 콜드 HDD 에서는 I/O 바운드(위 85%)입니다. NVMe 급에서는 CPU 해제가 병목이 됩니다: NVIDIA 기술 블로그는 비압축 스트리밍이 PCIe Gen3 약 3 GB/s 에 막히고, CPU 해제를 쓰면 CPU 가 병목이 되어 비압축보다 처리량이 낮다고 설명합니다. Microsoft DirectStorage 1.1 은 NVMe 설치를 권장합니다. "HDD↔SATA 차이보다 SATA↔NVMe 차이가 작다"는 Digital Foundry 류 근거는 접근하지 못했습니다(미확인). 위 두 자료가 간접 근거입니다.
- 쓰기 캐시 정책, NCQ: 순차 읽기 바닥에는 영향이 없다고 봅니다(추정). 근거 문서는 못 찾았습니다.
- SysMain/ReadyBoot/ReadyBoost: ReadyBoost 는 4 KB 임의 읽기 2.5 MB/s 이상인 플래시를 캐시로 쓰며 시스템 디스크가 SSD 이면 꺼집니다. 게임 콜드 로딩에 대한 실측 보고는 찾지 못했습니다(미확인).

## 5. 기존 제품

| 제품 | 동작 | 콜드 첫 로딩 | AsterCache 와의 차이 |
|---|---|---|---|
| PrimoCache(L1 RAM, L2 SSD 읽기/쓰기 캐시) | 블록 장치 필터 드라이버로 자주 읽은 블록을 SSD/RAM 에 캐시 | 처음 읽는 블록은 HDD 에서 읽어야 함(캐시 원리상, 공식 FAQ 답변 본문은 확인 못 함) | 드라이버 필요(안티치트 규칙 위배), 쓰기 캐시 위험. AsterCache 는 드라이버 없음 |
| Intel Optane / 스마트 응답 / StoreMI | SSD 또는 Optane 에 핫 블록을 승격 | 승격은 사용 이력이 쌓인 뒤. Optane 은 소비자 라인 2021년 1월 단종, 2022년 7월 사업 정리(위키백과 3D XPoint). StoreMI 는 자료 미확인 | 학습 후에야 효과. 모두 드라이버 또는 칩셋 기능 |
| SSHD(FireCuda 등) | 장치 펌웨어가 플래시에 핫 데이터를 저장(위키백과 Hybrid drive) | 학습 전 첫 읽기는 HDD 속도(원리상) | 하드웨어 교체 필요 |

차이점 요약: 이들은 모두 "한 번 읽은 뒤"의 가속이며 첫 콜드는 줄이지 못합니다. AsterCache 의 재배치+존 선읽기는 첫 콜드를 순차 한계까지 내리는 점이 다르고, 예열은 "읽기 이력이 없어도" 가능하다는 점이 다릅니다(트레이스 1회 필요).

---

## 6. 빠른 시작(Fast Startup)과 hiberfil

요약
- 빠른 시작은 사용자 세션을 닫고 커널 세션만 최대 절전으로 저장합니다. 일반 최대 절전보다 저장량이 훨씬 적고, 기본 hiberfil 예약은 RAM 의 75% 지만 빠른 시작 사용 시 실제 사용량은 보통 RAM 의 약 10~15% 입니다. 재개는 다중 코어로 읽기와 압축 해제를 병렬화해 부팅을 30~70% 단축합니다(Microsoft 윈도 8 엔지니어링 블로그, "Delivering fast boot times in Windows 8"). 같은 글에서 SSD(읽기 200 MB/s 이상) 환경에서 2→4코어가 재개 시간을 줄인다고 서술합니다(CPU 압축 해제가 병목일 수 있음).
- 게임 파일 캐시가 보존되는가: 위 자료는 "사용자 세션을 닫고 커널 세션만 저장"이라고만 말합니다. 서비스 프로세스가 매핑해 둔 읽기 전용 파일 페이지가 저장되는지는 문서로 확인하지 못했습니다(미확인). 추정: 빠른 시작에서 서비스는 종료 후 재시작되고(위 글: 전통적 종료에서 서비스를 닫는다고 설명하며 빠른 시작에서는 커널 세션을 보존), 깨끗한 파일 페이지는 보존 대상이 아닙니다. 실험은 간단합니다: 서비스가 파일을 예열 → 빠른 시작 종료 → 켜기 → RAMMap "File Summary"에서 해당 파일의 캐시 잔존량 확인.
- 설령 보존되더라도 이득이 없다고 봅니다(추정): 그 데이터는 hiberfil 에서 순차로 되읽혀야 하므로 HDD 면 같은 135 MB/s 를 부팅 시간에 지불할 뿐이고, SSD 에 hiberfil 이 있을 때만(시스템 디스크가 SSD, 게임은 HDD) 게임 데이터 2 GB 를 SSD 속도로 복원 → 사실상 "SSD 계층"의 변형이 됩니다. 이 경우 전체 최대 절전(사용자 세션 포함)이 필요하고 hiberfil 이 커지며 서비스가 메모리를 잠가 두어야 합니다. 메모리 잠금 2 GB 의 상시 점유 비용이 큽니다.
- hiberfil 압축/복원 속도 수치: 위 글에 SSD 200 MB/s 이상이라는 서술 외 구체 MB/s 는 없습니다(미확인).

## 7. 스탠바이 목록 우선순위

요약
- PrefetchVirtualMemory 로 올린 범위는 대상 프로세스의 작업 집합에 들어가지 않고 물리 메모리에 캐시되며, 이후 프로세스가 접근하면 작업 집합에 추가됩니다. 저메모리에서는 일부 또는 전부 실패할 수 있고 큰 범위는 메모리 압박을 만듭니다(Microsoft Learn). 큰 동시 I/O 로 효율적으로 읽는다고도 서술합니다.
- 프로세스 메모리 우선순위(SetProcessInformation ProcessMemoryPriority): 프로세스의 메모리 우선순위가 그 스레드가 작업 집합에 올리는 물리 페이지의 기본 우선순위를 정하고, 트림할 때 낮은 우선순위가 먼저 잘립니다. 기본값은 MEMORY_PRIORITY_NORMAL 이며 열거형의 최상위가 NORMAL 입니다(Microsoft Learn). 즉 문서상으로는 기본보다 높일 수 없고 낮추기만 됩니다.
- PROCESS_MODE_BACKGROUND_BEGIN 은 "자원 스케줄링 우선순위를 낮춘다"고만 문서에 있습니다. 메모리 우선순위까지 낮아지는지는 문서 문구로 확인하지 못했습니다(미확인, 추정: 낮아짐). 예열 서비스는 백그라운드 모드를 쓰지 않는 편이 안전합니다.
- SysMain 이 쓰는 우선순위와 8단계 스탠바이 목록 세부는 접근 가능한 문서에서 확인하지 못했습니다(미확인). 확인법은 RAMMap 의 "Priority Summary" 탭(우선순위별 스탠바이 목록 크기, Sysinternals 문서)과 "File Summary/File Details" 탭(파일별 캐시 상주량)입니다. 예열 직후와 메모리 압박 후, 게임 실행 직전에 스냅샷을 비교하면 어느 우선순위에 얹히는지 알 수 있습니다.

## 8. DM-SMR 미디어(영구) 캐시

요약은 2번 항목의 SMR 문단과 같습니다. 정리하면:
- 확인된 사례: Seagate 5 TB 20 GiB / 8 TB 25 GiB, 바깥 지름(OD) 위치, 저널 기록, 밴드 15~40 MiB(Skylight, FAST 2015).
- WD20EZAZ: 크기, 위치, 읽기 전송률 차이 모두 미확인(추정: 용량의 1% 안팎, 유휴 정리). 호스트가 영역을 지정할 수 있는지는 근거 없음(추정: 불가).
- 운용 권고(추정): 재배치 후 유휴 정리가 끝나도록 충분히 쉬고(수십 분 이상), 같은 존을 두 번 순차 읽어 두 번째가 빠르거나 안정적인지 비교합니다.

## 9. 게임 자산 중복률

게임 설치 파일의 블록 단위 중복(0 페이지, 반복 텍스처) 비율을 다룬 학술 논문이나 분석 글은 찾지 못했습니다(미확인). 대체 지표는 CompactGUI 압축률(위 표)이며, 평균 14~18% 감소는 중복과 일반 압축을 합친 값이라 중복만의 비율은 그보다 작습니다(추정). 직접 측정이 가장 확실합니다: 핫 집합 2.02 GB 를 4 KiB 블록 해시로 쪼개 0 블록 비율과 동일 블록 비율을 세면 됩니다. 이 비율이 r 이면 읽기량을 r 만큼 줄일 수 있으나, 중복 블록을 한 번만 읽고 두 파일 위치에 채우는 것은 파일 시스템 내부 변경 없이는 불가능합니다. 사용자 모드에서는 (가) 중복 블록의 한 사본만 존에 놓고 선읽기에서 건너뛰기가 가능해도 게임이 다른 사본을 읽는 순간 디스크 접근이 생기므로 효과가 제한적입니다(추정). 결론: 단독 후보로는 기대 이득이 작습니다.

## 10. "순차 한계 아래로 내렸다"는 연구와 도구

| 방법 | 출처 | 내용 | 수치 | 한계 아래인가 |
|---|---|---|---|---|
| 정렬 선읽기(sorted prefetch) | FAST 2011 논문 서술(Windows 프리페처, Linux 구현 포함) | 접근 블록을 LBA 순으로 읽어 탐색 최소화 | HDD 시작 시간 평균 40% 감소 | 아님. 무작위 → 순차로 가는 것 |
| 연산/I/O 중첩 | FAST 2011 | 선읽기와 CPU 연산을 겹침 | HDD 에서 평균 16% 감소(I/O 가 85%라 한계) | 부분적(시간 숨기기) |
| 접근 순서 재배치(조각 모음) | FAST 2011, 위키백과 Prefetcher | 응용 접근 블록을 연속 배치 | 탐색 거리 감소(수치 미기재) | 아님. AsterCache 와 같은 발상 |
| SSD 전용 선읽기(FAST) | FAST 2011 | SSD 병렬성 활용 | SSD 에서 HDD 용 대비 7% → 28% 감소 | 해당 없음(SSD) |
| 사전 로딩 데몬(Preload) | 2006 학위 논문 | 사용 이력 마르코프 모델로 바이너리와 라이브러리를 메모리에 미리 올림 | 수치는 초록에 없음(미확인) | 시간 이동(예열과 동일) |
| Paralfetch | IEEE TPDS 2025 | 시작 관련 읽기 수집, I/O 처리량이 좋게 미리 스케줄, 실행과 선읽기 중첩 | HDD 와 플래시 모두 기존 도구보다 빠르다고만 서술(수치 미확인) | 아님 |
| 압축 | WOF/CompactGUI | 읽는 바이트 감소 | 평균 0.82~0.86 | 예(바이트 감소). 게임별 |
| 플래시 캐시/하이브리드 | FAST 2011, 위키백과 | 핫 데이터를 플래시에 둠 | 장치별 | 예(매체 교체). 학습 이후 |

판단: 문헌과 도구 어디에도 "같은 HDD 에서 같은 바이트를 순차 한계보다 빨리 읽는" 방법은 없습니다. 물리 한계 아래로 가는 길은 바이트 수 감소, 더 빠른 존/매체, 시간 이동 세 가지입니다.

---

## AsterCache 에 적용 가능한 후보 (기대 효과 순 정렬)

기준선: 예열 없는 첫 실행 15.1 s(순차 약 134 MB/s). 배수는 이 값 대비 콜드 시간의 감소 배율입니다.

| 순위 | 후보 | 기대 효과(배수) | 안티치트 위험 | 구현 난이도 | 검증 방법 |
|---|---|---|---|---|---|
| 1 | SSD 계층: 핫 파일을 SSD 로 복사하고 원 경로를 심볼릭 링크로 연결 | 최대 약 4배(15.1 → 약 3.8 s, 추정). 단 파일 전체 복사 필요 | 중간(미확인): 링크 경로 거부, 무결성 검사, Steam 갱신이 링크를 덮어씀 | 중간(복사, 링크, 갱신 감지, 롤백) | 안티치트 게임 1~2종으로 실행/매치 진입 확인, Steam 파일 검증, 업데이트 후 링크 유지 여부, 콜드 시간 측정 |
| 2 | 예열 적중률 확대: 트리거를 더 이른 시점(부팅, 로그온, 런처/Steam 실행, 사용 시간대 학습)으로 확장, 예열 프로세스는 메모리 우선순위 NORMAL 유지, 백그라운드 모드 미사용 | 적중 시 약 31배(15.1 → 0.48 s). 콜드 자체는 불변 | 낮음(게임 프로세스 무접촉) | 낮음 | RAMMap Priority Summary 와 File Summary 로 예열 직후/압박 후/게임 직전 잔존량 비교, 메모리 압박 시 복원율 측정 |
| 3 | 존을 가장 바깥(낮은 LCN) 빈 영역에 배치하고 순서 유지 | 1.0~1.25배(추정, 현재 존 위치와 드라이브 곡선에 의존) | 낮음 | 낮음~중간(이동 대상 빈 영역 탐색) | 원시 순차 읽기를 현재 존 LCN 과 가장 낮은 LCN 에서 비교, 재배치 후 재측정 |
| 4 | 선택적 WOF 압축: 핫 집합 파일 중 압축률이 좋은 파일만(트레이스로 얻은 핫 범위 포함 파일의 사전 측정 후), XPRESS 4K~16K | 평균 1.16~1.22배, 게임별로 1.0(Frostpunk)~1.4+(압축률 0.7). 예열 단계에서는 해제 CPU 비용 | 낮음~중간(보고 없음이나 보증 없음, 일부 게임 비호환) | 중간(ADS 이동, 청크 표 매핑, 갱신 시 풀림 대응) | `WofCompressedData` 의 클러스터를 FSCTL_MOVE_FILE 로 옮길 수 있는지 실험, 압축 전후 콜드 재생, CompactGUI DB 대조 |
| 5 | 재배치 후 DM-SMR 영구 캐시 정리 대기와 재측정, 정리 완료 전 측정 구분 | 0~미확인(정리 전 느린 읽기가 있다면 그만큼 회복) | 없음 | 낮음 | 재배치 직후 vs 유휴 30분 후 같은 존 순차 읽기 두 번씩 비교 |
| 6 | 읽기 크기/큐 깊이 조정(4~16 MiB, 큐 2~8) | 1.0~1.05배(추정) | 없음 | 낮음 | 같은 존에서 크기와 큐 조합별 처리량 스윕(미버퍼드, 예열 없는 상태) |
| 7 | 0 블록과 중복 블록 건너뛰기 | 1.0~1.1배(미확인, 측정 전 판단 불가) | 없음 | 높음 | 핫 집합의 4 KiB 블록 해시로 비율 계측부터 |
| 8 | 빠른 시작/전체 최대 절전으로 캐시 보존 | 이득 없음~SSD 에 hiberfil 이 있을 때만 의미(추정). 메모리 2 GB 상시 점유 필요 | 낮음 | 높음 | RAMMap 으로 빠른 시작 재부팅 후 해당 파일 캐시 잔존 여부 한 번만 확인 후 폐기 판단 |

권고 우선순위: 먼저 3(바깥 배치)과 5(정리 대기)로 기준선을 확정하고, 2(예열 적중률)를 사용자 체감 개선의 주축으로 두며, 콜드 배수를 더 내려야 하면 1(SSD 계층)을 "선택 기능"으로, 4(WOF)는 게임별로 압축률을 측정해 켜는 순서가 합리적입니다.

---

## 출처 목록

Microsoft
- FSCTL_MOVE_FILE 제어 코드: https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_move_file
- 파일 조각 모음(지원 스트림 유형, 16 KB 정렬, 이름 구문): https://learn.microsoft.com/en-us/windows/win32/fileio/defragmenting-files
- compact 명령(/exe 알고리즘): https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/compact
- Compact OS: https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/compact-os
- 파일 캐싱(256 KB 슬롯): https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching
- CreateFile(FILE_FLAG_SEQUENTIAL_SCAN, NO_BUFFERING): https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilea
- PrefetchVirtualMemory: https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-prefetchvirtualmemory
- MEMORY_PRIORITY_INFORMATION: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-memory_priority_information
- SetProcessInformation: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation
- SetPriorityClass: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass
- 작업 집합: https://learn.microsoft.com/en-us/windows/win32/memory/working-set
- 리파스 포인트: https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-points
- RAMMap: https://learn.microsoft.com/en-us/sysinternals/downloads/rammap
- Delivering fast boot times in Windows 8(빠른 시작, hiberfil): https://learn.microsoft.com/en-us/archive/blogs/b8/delivering-fast-boot-times-in-windows-8
- DirectStorage 1.1(GPU 해제, NVMe 권장): https://devblogs.microsoft.com/directx/directstorage-1-1-now-available/
- DirectStorage 공개: https://devblogs.microsoft.com/directx/directstorage-api-available-on-pc/

압축 도구와 형식
- CompactGUI 저장소 README: https://github.com/IridiumIO/CompactGUI (README.md)
- CompactGUI FAQ 위키(Important Information): https://github.com/ImminentFate/CompactGUI/wiki/Important-Information
- CompactGUI 커뮤니티 압축 결과 데이터베이스(JSON, 직접 집계): https://github.com/IridiumIO/CompactGUI/blob/database/database.json
- CompactGUI 이슈 #226(안티치트 질문): https://github.com/IridiumIO/CompactGUI/issues/226
- CompactGUI 이슈 #101(호환 문제 목록): https://github.com/IridiumIO/CompactGUI/issues/101
- CompactGUI 이슈 #591(Steam 업데이트 중 압축으로 파일 손상): https://github.com/IridiumIO/CompactGUI/issues/591
- WOF 파일 구조(WofCompressedData, 청크 표): https://github.com/ebiggers/ntfs-3g-system-compression (src/system_compression.c 주석)
- XPRESS/LZX 형식과 wimlib: https://wimlib.net/compression.html
- XPRESS/Huffman 해제 속도(ms-compress): https://github.com/coderforlife/ms-compress

HDD, SMR
- 위키백과 Zone bit recording: https://en.wikipedia.org/wiki/Zone_bit_recording
- 위키백과 Shingled magnetic recording: https://en.wikipedia.org/wiki/Shingled_magnetic_recording
- 위키백과 Hard disk drive performance characteristics: https://en.wikipedia.org/wiki/Hard_disk_drive_performance_characteristics
- Skylight - A Window on Shingled Disk Operation(FAST 2015): https://www.usenix.org/system/files/conference/fast15/fast15-paper-aghayev.pdf

응용 시작 가속과 캐시
- FAST: Quick Application Launch on Solid-State Drives(FAST 2011): https://www.usenix.org/legacy/event/fast11/tech/full_papers/Joo.pdf
- 위키백과 Prefetcher: https://en.wikipedia.org/wiki/Prefetcher
- 위키백과 ReadyBoost: https://en.wikipedia.org/wiki/ReadyBoost
- 위키백과 Hybrid drive: https://en.wikipedia.org/wiki/Hybrid_drive
- 위키백과 3D XPoint: https://en.wikipedia.org/wiki/3D_XPoint
- Preload - An Adaptive Prefetching Daemon(2006): 학술 색인 OpenAlex 항목
- Paralfetch(IEEE TPDS 2025): https://doi.org/10.1109/tpds.2024.3525337
- NVIDIA GDeflate 블로그: https://developer.nvidia.com/blog/accelerating-load-times-for-directx-games-and-apps-with-gdeflate-for-directstorage/
- BattlEye FAQ: https://www.battleye.com/support/faq/ (파일 압축에 관한 직접 언급 없음)
- PrimoCache: https://www.romexsoftware.com/en-us/primo-cache/

## 확인하지 못한 항목 (후속 조사 필요)

- WD20EZAZ 제조사 순차 전송률, HD Tune 외주/내주 곡선, 영구 캐시 크기.
- 1 MiB 대비 4~16 MiB 비버퍼드 읽기 처리량 비교.
- HDD/SATA/NVMe 게임 로딩 시간 비교 기사의 수치(Digital Foundry 등).
- Steam 정션 도구 선례와 안티치트의 링크 처리, Steam 무결성 검사와 링크의 상호작용.
- LZX 해제 처리량, WOF 압축 파일의 예열 후 캐시 내용(해제본인지).
- `WofCompressedData` 스트림에 FSCTL_MOVE_FILE 을 쓸 수 있는지(문서상 가능성만 확인).
- AMD StoreMI 와 PrimoCache 의 공식 수치.
- 게임 자산의 블록 중복률 연구.
