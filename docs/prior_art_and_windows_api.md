# AsterCache 조사 결과: 선행 기술 · Windows 재배치 제약 · 위치 추적 · HDD 특성 · 게임 로딩 I/O

조사일 2026-11-01. 읽기 전용 웹 조사(저장소 수정 없음).
확인 수준 표기: [확인] = 1차 자료(Microsoft Learn, 논문 원문, 소스 매니페스트)를 직접 읽음 / [2차] = 검색 요약·포럼·벤더·언론 등 간접 / [추측] = 근거 없는 추론 또는 계산 / [미확인] = 찾지 못함.
검색 도구가 요약 형태로 돌려준 항목은 원문 전체를 읽지 못했으므로 [2차]로 낮춰 표기했다.

---

## A. 선행 기술

### A1. Windows 자체 (Prefetcher / Layout.ini / defrag -b / ReadyBoot)
- 동작: Prefetcher 가 앱 시작 최초 최대 10초의 읽기를 파일(.pf)로 기록하고, 72시간마다 유휴 시 Sysmain 이 "디프래거가 파일·디렉터리를 앱 시작 때 참조되는 순서로 물리 배치하도록" 정보를 Prefetch\Layout.ini 에 만든다. Sysmain 자신은 디프래그를 하지 않고 데이터만 제공하며, SSD 는 디프래그 꺼짐이라 해당 없다. [확인] https://learn.microsoft.com/en-my/archive/blogs/chadduffey/what-is-application-launch-prefetching
- 단위는 "파일 단위"다(파일·디렉터리를 참조 순서대로 나열). 파일 내부 블록 범위 단위라는 근거는 찾지 못했다. [확인: 글의 표현이 files and directories / 블록 단위 부재는 추측]
- 범위: 앱 시작(첫 10초)과 부팅 한정. 게임 로딩 전체(수십 초~분, 수 GB)를 대상으로 하지 않는다. [확인(10초 창) / 게임 비대상은 추측]
- `defrag.exe /b` = "부팅 성능 향상을 위한 boot optimization" 이 현행 Microsoft 문서에 있다(관리자 필요). [확인] https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/defrag
- Windows 11 24H2 에서 Layout.ini 가 없어 `rundll32 advapi32.dll,ProcessIdleTasks` 와 `defrag %systemdrive% -b` 로 재생성했다는 사용자 보고가 있다. 즉 현행에서도 개념은 살아 있으나 자동 갱신 여부는 불확실. [2차, 일화] https://learn.microsoft.com/en-us/answers/a/2398999
- ReadyBoot 는 부팅용 메모리 캐시로 Prefetcher 를 대체(RAM 700MB 이상). 재배치가 아니라 RAM 선적재. [2차] https://en.wikipedia.org/wiki/Prefetcher
- 결론: Windows 는 "접근 순서 기반 파일 재배치"를 앱 시작/부팅에 대해 이미 한다(파일 단위, 짧은 창). 게임 로딩 단계 전체·파일 내부 블록 단위는 하지 않는 것으로 보이나, 후자는 부재 증명이 불가능하다.

### A2. 상용 디프래거 (모두 벤더 설명, 독립 검증 약함)
- Diskeeper I-FAAST: 볼륨 벤치마크 후 "파일 접근 빈도"를 지속 감시해 자주 요청되는 파일을 빠른 위치에 순서화. 파일 단위, 빈도 기반. 파일 간 접근 순서·블록 단위 근거 없음. 성능 수치(최대 80%)는 벤더 주장. [2차] https://condusiv.com/comparing-i-faast/ , https://en.wikipedia.org/wiki/Diskeeper
- PerfectDisk SMARTPlacement: 파일 사용/수정 빈도로 배치(자주 접근하는 파일 또는 사용자가 지정한 High Performance 파일은 바깥 트랙). 접근 기반인지 수정 기반인지 자료마다 달라 불명. 파일 단위. [2차] https://www.softpedia.com/reviews/windows/PerfectDisk-Professional-Review-137182.shtml
- UltimateDefrag: 사용자가 고르거나 최근 접근 기준 파일을 디스크 바깥 트랙의 High Performance 존에 배치. 파일 단위, 빈도/지정 기반. 독립 벤치마크 없음, 개발 정체. [2차] https://www.softpedia.com/reviews/windows/UltimateDefrag-66674.shtml
- O&O Defrag COMPLETE/Access: 파일을 "마지막 접근 일자" 순으로 정렬해 디스크 끝쪽에 배치. 파일 단위, 마지막 접근 시각 정렬이며 로딩 중 읽은 순서(트레이스)가 아님. [2차] https://docs.oo-software.com/en/oodefrag-23/standard-defragmentation-methods-ood23/completeaccess-method
- 결론: 상용 제품은 전부 "파일 단위 + 빈도/최근성"이다. 트레이스된 읽기 순서로 파일 내부 블록을 재배치한다는 근거는 이번 조사에서 찾지 못했다([미확인], 부재 증명은 아님).

### A3. 학술 · 오픈소스
- BORG (FAST'09): 블록 단위. 4KB 페이지 단위로 working-set 블록을 전용 파티션(BOPT)에 "복사"하고 원본 FS 배치는 그대로 둔다. blktrace 로 프로세스별 접근 그래프를 만들고 탐욕적 계획기가 "관측된 접근 순서를 보존하는 순차 배치"를 만든다. 디스크 busy 6~50% 감소, 앱 시작 43~67% 개선, VM 부팅 167s→65s. 한계: 작업 집합이 갑자기 바뀌면 악화, 재구성 비용, 복사본 1개 유지. [확인] https://www.usenix.org/legacy/event/fast09/tech/full_papers/bhadkamkar/bhadkamkar_html/index.html
  - 우리 설계와 가장 가까운 선행 연구다. "블록 단위 + 접근 순서 + 트레이스 기반 재배치"라는 개념 자체는 BORG 에 이미 있다. 차이: BORG 는 블록 계층에서 I/O 를 가로채 복사본 영역으로 우회(remap), 워크로드 전체 대상. 우리는 NTFS 안에서 제자리 이동, 가로채기 없음, 게임별 로딩 구간 대상.
- ALIS (Hsu·Smith·Young, TOCS 2005 / UCB 2003): 사용 패턴에 따라 블록을 복제·재배치. 시뮬레이션에서 서버 읽기 최대 50%, PC 약 15% 개선. 시뮬레이터 평가. (BORG 논문 평: 복제본이 낡을 수 있고 쓰기 최적화 없음) [2차: 초록] https://www2.eecs.berkeley.edu/Pubs/TechRpts/2003/5459.html , BORG 논문의 평가는 [확인]
- FS2 (Huang·Hung·Shin, SOSP'05): 파일시스템 빈 공간에 자주 접근하는 블록의 복제본을 만들고, 읽기 시 헤드에 가까운 복제본 선택. Ext2 프로토타입. 디스크 접근 시간 41~68% 감소(저자 수치). [2차] https://dblp.uni-trier.de/rec/conf/sosp/HuangHS05.html
- DiskSeen (USENIX ATC'07): 재배치가 아니라 선읽기. 디스크 블록 위치와 접근 이력을 추적해 디스크 레이아웃 수준에서 선읽기. grep·CVS·TPC-H 실행 시간 20~53% 단축. [2차: 초록] https://static.usenix.org/event/usenix07/tech/ding.html
- C-Miner (FAST'04): 빈발 시퀀스 마이닝으로 블록 상관관계 발견, 캐시 선읽기·데이터 배치에 활용 제안. 재배치 구현이 핵심은 아님. [2차: 초록] https://usenix.org/conference/fast-04/c-miner-mining-block-correlations-storage-systems
- e4rat (Linux ext4): 부팅 중 접근한 파일 목록을 수집(e4rat-collect)해 ext4 온라인 디프래그 ioctl(EXT4_IOC_MOVE_EXT)로 연속 블록에 재배치(e4rat-realloc)하고 seek 없는 순서로 프리로드(e4rat-preload). 파일 단위, 부팅 한정, 일화적 45s→15s. 사실상 "Windows Layout.ini + 선읽기"의 Linux 구현이고, 우리와 같은 계열(FS 자체 디프래그 프리미티브로 이동)이다. 현재 유지보수는 불분명. [2차] https://bbs.archlinux.org/viewtopic.php?pid=913720
- systemd-readahead / ureadahead: 부팅 읽기 기록을 재생. 회전 매체에서는 FS_IOC_FIEMAP 으로 얻은 디스크상 위치 순서로 읽기 요청을 정렬(재배치 없음, 선읽기 순서 정렬만). systemd 219 에서 제거되었다는 서술이 있으나 요약이 잘려 확인 필요. [2차] https://man.he.net/man8/ureadahead , https://lists.freedesktop.org/archives/systemd-devel/2011-October/003604.html
- Apple HFS+ Hot File Clustering: 자주 접근하는 파일(기본 1000개, 10MB 이하)을 "핫 영역"에 이동·고정하는 커널 코드가 공개 소스에 있음. 파일 단위, 온도(빈도) 기반. [확인(소스 헤더)] https://opensource.apple.com/source/xnu/xnu-1228/bsd/hfs/hfs_hotfiles.h.auto.html
- Apple BootCache: 부팅 읽기 패턴을 감시해 정렬된 "playlist"(/var/db/BootCache.playlist)로 저장하고 다음 부팅에 적재(스마트 선읽기), 적중률이 낮으면 비활성화. 블록 읽기 단위 재생이며 재배치 아님. [2차] https://discussions.apple.com/thread/2088848
- AMD StoreMI/FuzeDrive (SSD+HDD 티어링): 4MB 단위 블록 이동으로 자주 쓰는 데이터를 SSD 로 승격. 우리의 "선택적 SSD 캐시"와 겹침. [2차: Tom's Hardware 요약] https://www.tomshardware.com/news/amd-fuzedrive-fuzeram-enmotus-ryzen,36368.html
- PrimoCache: 블록 단위 L1(RAM)/L2(SSD) 캐시. 선읽기는 L1 한정이고 HDD 에서 읽어 L1 에 적재, L2 는 HDD 를 저우선으로 재읽기해 채움. 우리의 "스트림 선읽기 + SSD 캐시"와 겹침. [2차: 포럼] https://forum.romexsoftware.com/en-us/viewtopic.php?p=15505
- 게임 엔진 측 선행 관행: Unity 는 씬 데이터를 한 번에 읽도록 직렬화하고 AssetBundle 을 의존성 저장 순서로 읽어 HDD seek 을 줄인다(엔진이 패키징 단계에서 하는 것). 사용자 PC 에 이미 설치된 게임을 외부에서 고치는 것과는 층위가 다르다. [확인] https://docs.unity3d.com/550/Documentation/Manual/OptimizingDiskSeeks.html

### A 결론: 기존에 그대로 있는가
- 이미 있는 것: (1) 접근 순서로 파일을 물리 배치(Windows Layout.ini, e4rat, 파일 단위·부팅/앱 시작 한정). (2) 블록 단위·접근 순서 보존 재배치(BORG, ALIS, FS2 — 단 복사본+우회 계층 방식, 연구용 프로토타입). (3) 기록한 읽기 패턴을 디스크 순서로 정렬해 선읽기(systemd-readahead, BootCache, DiskSeen). (4) SSD+HDD 블록 티어링/캐시(StoreMI, PrimoCache).
- 이번 조사에서 "게임별 트레이스로 파일 내부 블록 범위를 파일 간에 걸쳐 NTFS 안에서 제자리 이동(FSCTL_MOVE_FILE)해 로딩 구간 전체를 연속 구간으로 만들고, 그 위에 선읽기를 결합"한 단일 제품/논문은 찾지 못했다. [미확인; 검색 한계로 부재 증명은 아님]
- 따라서 새로움은 "개념"이 아니라 "조합과 구현 방식"에 있다고 말하는 편이 정직하다. 블록 단위 접근순서 재배치 개념은 BORG 가 선점했다는 점을 먼저 인정하고 차이를 서술해야 한다.

---

## B. Windows 재배치 API 제약 (FSCTL_MOVE_FILE)

### B1. 호출 조건·입력
- 호출은 볼륨 핸들(예: \\.\D:)에 DeviceIoControl. 입력은 MOVE_FILE_DATA{FileHandle, StartingVcn, StartingLcn, ClusterCount(DWORD)}. [확인] https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-move_file_data
- 권한: 볼륨 핸들은 관리자만 열 수 있다. "Only administrators can obtain a handle to a volume, so only administrators can defragment a volume." [확인] https://learn.microsoft.com/en-us/windows/win32/fileio/defragmenting-files
- 파일 핸들은 읽기 속성 정도의 권한이면 되며 사용 중 파일도 디프래그 가능(드라이버 문서 요약). EFS 암호화 파일은 FILE_READ_DATA/WRITE_DATA/APPEND_DATA/EXECUTE 중 하나가 필요. [확인(EFS 부분) / 사용 중 파일은 2차] https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/file-system-control-processing
- 이동 중 다른 프로세스: 제어 코드가 이동 중 다른 프로세스의 읽기/쓰기를 투명하게 억제·허용한다. 다른 프로세스는 응답이 느려질 뿐 막히지 않는다. [확인] https://learn.microsoft.com/en-us/windows/win32/fileio/defragmenting-files
- 열린 파일 이동 금지 표시: FSCTL_MARK_HANDLE + MARK_HANDLE_PROTECT_CLUSTERS 를 건 핸들의 파일은 이동되지 않는다. [확인] https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_move_file
- 정렬/단위: 압축·스파스 파일은 이동 단위가 16클러스터, 그 외 1클러스터. 섀도 복사본이 켜져 있으면 이동 크기 16KB 이하이거나 원본-대상 거리가 16KB 배수가 아닐 때 섀도 공간 증가·성능 저하(4KB 클러스터면 LCN 차이가 4의 배수). [확인] 위 두 URL.
- 파일 크기를 클러스터로 올림한 끝 너머의 할당 클러스터를 이동하려 하면 오류. [확인] defragmenting-files
- 한 호출 최대 크기: 문서에 상한 없음(ClusterCount 는 DWORD). 실제 한도는 [미확인]. 구현은 수십~수백 MB 청크로 나눠 호출하며 재시도하는 편이 안전 [추측].
- 대상 LCN 이 사용 중일 때: 비트맵은 "읽는 즉시 틀릴 수 있다. 최근 비트맵이 미할당이라 해도 할당된 클러스터로 이동을 시도할 수 있으며 프로그램은 이에 대비해야 한다." 오류 코드는 문서에 없고, 과거 OSR 글에는 간헐적 STATUS_INVALID_PARAMETER 보고가 있다. [확인(경고) / 오류 코드는 2차] https://community.osr.com/t/problems-with-fsctl-move-file/19827
- 이동 가능한 스트림: ::$DATA, $ATTRIBUTE_LIST, $REPARSE_POINT, $EA, $LOGGED_UTILITY_STREAM, 디렉터리 $BITMAP/$INDEX_ALLOCATION, $MFT 의 일부 등. 문서는 "모든 파일이 이동 가능하지는 않다"고 명시. 페이지파일·hiberfil 등 이동 불가 파일 목록은 문서에 없음 [미확인]. 이동 불가 파일은 대부분 게임 데이터가 아니므로 영향은 작을 것 [추측].
- MFT 상주(작은) 파일: 데이터가 MFT 레코드 안에 있어 클러스터가 없으므로 이동 대상이 아님 [추측, NTFS 구조 지식 기반; 문서 근거 못 찾음].
- 더티 볼륨은 디프래그 불가(`fsutil dirty`, chkdsk 선행). [확인] https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/defrag
- 원자성/정전 안전: Microsoft 문서에 이동 연산의 로깅·원자성 설명이 없다. NTFS 가 메타데이터를 $LogFile 에 먼저 기록한다는 일반 설명만 있고 클러스터 이동이 그 트랜잭션에 속한다는 근거는 찾지 못했다. [미확인] 상용 디프래거가 오랫동안 같은 API 를 쓰고 있어 정상 경로에서는 안전하다고 보지만 전원 차단 시험 근거는 없다 [추측]. 설계 대응: 이동 전후 파일 해시 검증, 이동 단위 작게, 이동 계획·진행 지점을 로그에 남겨 재개 가능하게.
- 디스크 쓰기량: 이동은 복사다(읽고 대상에 쓰고 매핑 갱신). 핫 영역 크기 H 를 채우려면 최소 H 의 읽기+쓰기, 대상 영역에 점유 파일이 있으면 그것을 먼저 옮기는 추가 쓰기가 든다. [확인(공식 문서의 "필요하면 다른 파일을 옮겨 공간 확보") / 총량은 추측]
- BitLocker: 볼륨 암호화는 NTFS 아래에서 동작하므로 FSCTL_MOVE_FILE 동작 자체에는 영향이 없을 것으로 본다. 암호화된 비시스템 디스크는 부팅 시점 디프래그만 불가라는 벤더 설명이 있을 뿐이다. [추측; 2차: https://www.oo-software.com/en/docs/usersguide/ood29.pdf ]
- EFS: 위 권한 조건 외에 구체 동작 근거 없음. 게임 폴더는 보통 EFS 가 아니다 [추측].
- 대상 LCN 을 StartingLcn 으로 직접 지정할 수 있다는 점이 설계 전제이며 문서가 이를 뒷받침한다. [확인]

### B2. 빈 클러스터 조회
- FSCTL_GET_VOLUME_BITMAP: 입력 STARTING_LCN_INPUT_BUFFER 의 시작 LCN(파일시스템에 따라 내림될 수 있음)부터 볼륨 끝까지 클러스터당 1비트(1=할당, 0=여유)의 비트맵을 돌려준다. 볼륨 핸들(관리자). 반환 비트맵은 페이지 경계 정렬. 시점 스냅샷이라 쓰기 활동이 있으면 즉시 낡을 수 있다. [확인] https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_volume_bitmap
- 비트맵 크기: 클러스터 수/8 바이트. 2TB(실사용 약 1.86TiB)에서 4KB 클러스터면 약 4.9억 클러스터 = 약 61MB. [계산]
- 특정 LCN 범위를 비우는 방법: 문서의 디프래그 절차가 "필요하면 다른 파일을 옮겨 충분히 큰 공간을 만든다"고 명시한다. 범위 내 점유 클러스터의 소유 파일을 알아내려면 전 파일에 FSCTL_GET_RETRIEVAL_POINTERS 를 돌려 LCN→파일 역매핑을 만들어야 한다(비트맵만으로는 소유자를 모름). 소유자 역조회 단일 API 는 문서에서 못 찾음 [추측]. [확인(절차)] https://learn.microsoft.com/en-us/windows/win32/fileio/defragmenting-files

### B3. LCN ↔ 물리 위치, SMR
- NTFS LCN 0 은 "파티션 시작"이다. 물리 위치는 파티션 오프셋 + LBA→실린더 매핑에 달렸다. 대부분 HDD 는 LBA 0 이 바깥 트랙(가장 빠름)이지만 보장되지 않고 드라이브마다 방향이 다를 수 있다는 커널 개발자 응답이 있다. 가정하지 말고 처음·중간·끝에서 순차 읽기 속도를 재서 확인해야 한다. [2차] https://lore.kernel.org/all/CALjAwxjJXEaDjacbfT5=rS6XzFxDwJOP5Rnktqc1+Y95UKcWsg@mail.gmail.com/T/
- 바깥 트랙이 빠른 이유: 존 비트 기록(ZBR)으로 바깥 존에 트랙당 섹터가 많음. 형제 모델 실측: WD Blue 2TB WD20EZRZ(HD Tune)에서 읽기가 바깥 약 155MB/s → 안쪽 약 70MB/s(약 2.2:1), 평균 117.7MB/s, 접근 시간 15.7ms. [2차: 리뷰] https://www.legitreviews.com/wd-blue-2tb-hard-drive-wd20ezrz-review_205093/4
- WD20EZAZ 는 SMR(드라이브 관리형)로 보인다: WD 가 2020-04 공개한 SMR 목록에 WD20EZAZ 포함으로 다수 보도됨(WD60EZAZ 등 동일 계열). 단 WD 공식 표/PDF 자체를 읽지 못했고 WD 블로그 표는 "3.5인치 Blue 2~6TB 는 SMR/CMR 혼재"로만 나와 모델 특정은 2차 자료에 의존한다. 직접 지속 쓰기 시험으로 확인하는 것이 확실하다. [2차] https://wccftech.com/western-digital-officially-states-which-specific-hdd-models-use-smr-technology/ , https://blog.westerndigital.com/wd-red-nas-drives/ , https://datacenterdisk.com/cmr-or-smr/wd20ezaz
- SMR 의 문제: DM-SMR 은 소량의 CMR 영역/영속 캐시에 무작위 쓰기를 받고 유휴 시 밴드로 재기록한다. 캐시가 차면 쓰기가 급락하고 수 분 정체할 수 있다. 대량 재배치(수 GB~수십 GB 쓰기)는 이를 유발할 수 있다. WD20EZAZ 에서의 실제 수치는 찾지 못함 [미확인]. 큰 순차 청크로 쓰면 영향이 줄 것으로 기대되나 [추측] — 반드시 실측(예: 20GB 를 1MB~64MB 단위로 이동하는 시험). [2차] https://www.storagereview.com/news/methods-of-smr-data-management
- 4Kn/512e: FSCTL_MOVE_FILE 과 직접 관련된 근거는 못 찾음 [미확인]. 클러스터 4KB 정렬이면 큰 문제 없을 것으로 기대 [추측].

### B4. 재배치 후 게임 패치·Steam 검증
- 재배치는 파일의 논리 내용(바이트)을 바꾸지 않고 VCN→LCN 매핑만 바꾼다. 따라서 파일 내용 기준 검증은 영향이 없어야 한다. [확인(API 의미) / Steam 검증 방식 자체는 이번에 조사하지 않음: 추측]
- 반대 방향 영향이 더 중요하다: 게임 업데이트가 파일을 다시 쓰면 새 클러스터가 할당되어 우리가 만든 배치가 깨진다. 재트레이스·재배치 필요 [추측].
- Windows 정기 "드라이브 최적화"(디프래그)가 서브파일 단위로 쪼개진 배치를 단편화로 보고 되돌릴 수 있다. 문서상 스케줄 작업이 주 1회 돈다. 실제로 되돌리는지는 [미확인]; 시험 필요. [확인(스케줄 존재)] https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/defrag

---

## C. 실시간 위치 추적

### C1. ETW Microsoft-Windows-Kernel-File
- 공급자 GUID {edd08927-9cc4-4e65-b970-c2560fb5c289}. [2차] https://winevt-kb.readthedocs.io/en/latest/sources/eventlog-providers/Provider-Microsoft-Windows-Kernel-File.html
- 매니페스트 확인: Read 이벤트(ID 15)와 Write(16)는 필드가 ByteOffset(UInt64), Irp, FileObject, FileKey, IssuingThreadId(v1)/ThreadId(v0), IOSize, IOFlags(v1 은 ExtraFlags 추가). 키워드 마스크: FILENAME 0x10, FILEIO 0x20, OP_END 0x40, CREATE 0x80, READ 0x100, WRITE 0x200. 즉 offset 의 정확한 필드명은 ByteOffset, 크기는 IOSize. [확인] https://github.com/repnz/etw-providers-docs/blob/master/Manifests-Win10-17134/Microsoft-Windows-Kernel-File.xml
- FileKey → 파일명 매핑은 이름 이벤트(FILENAME 키워드)와 FileObject/FileKey 일치로 한다(레거시 FileIo 클래스 문서의 설명). 세션 시작 전에 이미 열린 파일은 이름 이벤트를 놓치므로 별도 조회(열린 핸들 열거·rundown)가 필요할 가능성이 크다. [확인(레거시 클래스 설명) / rundown 필요성은 추측] https://learn.microsoft.com/en-us/windows/win32/etw/fileio-readwrite
- 권한: 세션 제어(StartTrace)는 관리자, Performance Log Users 그룹, LocalSystem/LocalService/NetworkService. NT Kernel Logger(SystemTraceControlGuid)는 관리자·LocalSystem 만. 실시간 소비도 같은 주체만. 세션 수 한계 일반 64(시스템 로거 8). [확인] https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracew , https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_logfilew
  - Velociraptor 문서는 이 공급자에 붙으려면 SYSTEM 권한이 필요하다고 적는다. Performance Log Users 로 Kernel-File 을 enable 할 수 있는지는 직접 시험해야 한다. [2차] https://docs.velociraptor.app/artifact_references/pages/windows.etw.kernelfile
- 지연: 실시간 버퍼 플러시 지연은 이번에 확인 못함 [미확인]. FlushTimer 설정으로 1초 안팎일 가능성 [추측, 측정 필요].
- ByteOffset 은 파일 내부 위치이며 LCN(물리)이 아니다. 물리 위치는 FSCTL_GET_RETRIEVAL_POINTERS 결과로 변환해야 한다. 캐시 적중 읽기가 이벤트에 포함되는지는 [미확인]. 물리 I/O 기준이 필요하면 디스크 계층 공급자가 대안 [추측].
- Python 소비: pywintrace·krabsetw 는 이번 검색에서 직접 확인하지 못함 [미확인]. ctypes 로 StartTrace/EnableTraceEx2/OpenTrace(EVENT_TRACE_LOGFILE, 실시간 모드 + EVENT_RECORD 콜백)/ProcessTrace 를 호출하는 경로는 Microsoft 문서상 표준 방식이다. [확인(API 존재)]
- 세션 남용 주의: 문서가 "고객 PC 에서 세션을 시작하지 말고, 범위를 좁히고 필요할 때만 켜라"고 경고. [확인]

### C2. 비관리자 대안
- ReadDirectoryChangesW 는 읽기를 감지하지 못한다 — 일반 지식, 이번에 출처 확인 안 함 [추측]. LastAccessTime 은 기본 갱신 비활성/지연 [추측]. Procmon 은 관리자 필요 [추측, 일반 지식].
- 관리자 없이 읽기 위치를 얻는 방법은 이번 조사에서 찾지 못함 [미확인]. 현실적 해법: (a) 어차피 관리자가 필요한 재배치 헬퍼(서비스)가 ETW 도 맡기, (b) Performance Log Users 에 1회 등록(시험 필요), (c) 위치 추적 자체를 없애기.
- 설계 시사 [추측이지만 중요]: 핫 블록이 접근 순서대로 연속 배치되면 "게임 시작 시점부터 핫 영역을 앞에서부터 순차로 한 줄로 읽는" 선읽기는 위치 추적이 거의 필요 없다. 필요한 것은 게임 읽기가 선읽기보다 앞서가 헤드를 흔들지 않게 속도를 조절하는 정도뿐이다. 이 경우 ETW 는 보정용·선택 기능으로 낮출 수 있다.

---

## D. HDD 물리 특성

### D1. 바깥/안쪽 속도, WD20EZAZ
- 바깥:안쪽 약 2:1 — 형제 모델(WD20EZRZ) 실측 155→70MB/s(2.2:1). 1997 USENIX 논문은 1.45~1.9배라는 과거 값. [2차] https://www.legitreviews.com/wd-blue-2tb-hard-drive-wd20ezrz-review_205093/4
- WD20EZAZ 자체의 순차·4K·접근시간 리뷰 수치는 찾지 못함 [미확인]. 판매처 스펙에 호스트 지속 180MB/s(인터페이스 쪽 수치, 매체 속도 아님), 256MB 캐시, 5400rpm 급. 한 판매처 사용자 리뷰의 읽기 428MB/s, 접근 0.61ms 는 물리적으로 불가능해 신뢰하지 않음.
- 5400rpm 이면 평균 회전 지연 5.56ms(1회전 11.11ms). 평균 탐색은 스펙시트 유형상 약 12ms 안팎. [2차] https://www.comx-computers.co.za/ST980811AS-view-pdf-296559.php
- 권고: 대상 드라이브에서 직접 LBA 0 / 25% / 50% / 75% / 끝의 순차 읽기 속도를 재서 존 곡선을 얻기(HD Tune 방식).

### D2. NCQ 큐 깊이
- PCPer(WD Black/Red Pro 리뷰): Iometer 무작위에서 QD1→QD32 로 성능이 대략 2배. [2차] https://pcper.com/2015/09/western-digital-6tb-black-and-6tb-red-pro-review-bigger-and-faster/4/
- Jens Axboe(2006, LKML fio 4K 무작위 읽기, 32GB 범위): QD32 NCQ 로 QD1 대비 약 20~45% 개선, I/O 스케줄러 조합 시 36~92%. [2차] https://lkml.iu.edu/hypermail/linux/kernel/0606.0/1082.html
- 결론: HDD 에서 QD32 는 QD1 의 대략 1.3~2배. 4K 무작위 QD1 은 수십~150 IOPS 대(약 0.75~1.5MB/s, 포럼 수치)이므로 QD32 로도 SSD(수만 IOPS)에 근접할 수 없다. [2차/추정]

### D3. 탐색 vs 그냥 읽기 손익분기 (계산, 추측 등급)
- 모델: 건너뛰기 비용 = 탐색 + 회전 지연, 그냥 읽기 비용 = gap / 전송률.
  - 완전 임의 접근: 형제 모델 접근시간 15.7ms × 평균 117.7MB/s ≈ 1.8MB.
  - 짧은 탐색(인접 실린더 1~2ms + 회전 지연 평균 5.56ms, 합 약 7ms): 바깥 155MB/s 에서 약 1.1MB, 안쪽 70MB/s 에서 약 0.5MB.
  - 같은 트랙 안에서의 순방향 소규모 건너뛰기는 헤드가 그대로 기다리므로 그냥 읽기와 동일 비용.
- 따라서 1.2MB 추정은 "짧은 탐색, 바깥 존" 가정과 일치하는 합리적 값이다. 안쪽이나 긴 탐색이면 0.5~1.8MB 로 범위가 퍼진다. 정확한 값은 대상 드라이브에서 "N바이트 읽고 G바이트 건너뛰고 반복" 실험으로 구해야 한다. [계산/추측]

---

## E. 게임 로딩 I/O 자료

- 로딩 중 읽기 패턴(파일 수·블록 크기 분포·랜덤 비율)을 정량 측정한 학술 논문이나 공식 자료는 이번 검색에서 찾지 못했다. [미확인]
- Tom's Hardware 의 SSD 게임 성능 기사: SSD 의 합성 벤치 향상이 로딩 시간 향상에 비례하지 않는다고 지적(로딩 중 디스크가 항상 병목은 아님; CPU 바운드 구간 존재). [2차] https://tomshardware.com/reviews/ssd-gaming-performance,2991-14.html
- DirectStorage: 로딩 중 엔진 오버헤드(CPU)가 지배적이라 빠른 NVMe 에서도 이득이 제한적이라는 포럼·언론 서술(Forspoken). 일부 벤더성 페이지의 NVMe 7.8s→1.9s 수치는 방법론 불명. [2차] https://www.neowin.net/amp/forspoken-on-pcie-50-nvme-hints-at-the-true-power-of-microsoft-directstorage-on-windows-11/
- Steam 하드웨어 조사의 HDD 비율: Valve 원문의 HDD/SSD 구분을 찾지 못함. 2차 보도 하나가 2026-06 조사에서 주 드라이브가 SSD 인 비율 98.3% 라고 쓰나 원문 확인 전이라 신뢰도 낮음. [2차, 낮음] https://www.guru3d.com/story/steam-june-survey-16gb-ram-gains-ground-as-32gb-adoption-slows/
- 시사점(사용자 실측 기반 계산): RAM 선읽기 콜드 17.8s→11.1s, 선읽기 처리량 39MB/s → 약 430MB 읽음(39×11.1). 같은 430MB 를 바깥 존 150MB/s 순차로 읽으면 이론 약 2.9s, SSD 0.8s 와의 격차가 13.9배에서 3~4배 수준으로 줄 뿐이다. 순차 읽기만으로 SSD급(0.8s)은 핫 집합이 약 120MB(=0.8s×150MB/s) 이하일 때만 가능하다. 따라서 HDD 단독 SSD급 목표는 물리적으로 도달하기 어렵고, 나머지는 RAM 보유/SSD 캐시/CPU 바운드 구간 중첩으로 메워야 한다. [계산, 사용자 수치 가정]

---

## 결론

### 독자 설계가 기존과 다르다고 말할 수 있는 지점 3개
1. 가로채기·복제본이 없는 제자리 재배치. BORG/ALIS/FS2 는 복사본을 별도 영역에 두고 블록 계층에서 우회(remap)하므로 낡은 복제본·계층 상주·쓰기 처리 문제가 있다. 설계는 NTFS 자체 프리미티브(FSCTL_MOVE_FILE)로 데이터의 실제 위치를 옮기므로 모든 읽기가 수정되지 않은 NTFS·캐시 경로를 그대로 타고, 도구를 제거해도 데이터는 정상이다. (선행 연구 비교는 [확인], 드라이버 불필요는 B 항목 근거로 [확인])
2. 게임별 로딩 트레이스 → 파일 간·파일 내부 블록 범위 단위의 접근 순서 배치. Windows Layout.ini/e4rat/상용 디프래거는 모두 파일 단위(빈도·최근성·앱 시작 10초 창)이고, BORG 는 블록 단위지만 워크로드 전체·계층 하부다. 특정 타이틀의 로딩 구간 전체를 대상으로 NTFS 파일 매핑 위에서 서브파일 범위를 이어 붙이는 구현은 찾지 못했다. ([미확인] 부재이므로 "찾지 못했다"로 쓰고 "없다"로 쓰지 말 것)
3. 배치와 선읽기의 결합 설계. 접근 순서로 연속 배치되면 선읽기는 위치 추적이 필요 없는 단일 순차 스트림이 되고 SSD 캐시는 잔여 미스에만 쓰면 된다. 기존 도구는 재배치(Layout.ini)와 선읽기(readahead, BootCache, PrimoCache)가 분리돼 있거나, 재배치 없이 정렬된 선읽기만(systemd-readahead)이다. 단 e4rat 은 "재배치 + 선읽기" 결합이라는 점에서 개념적 선점이 있으므로 "파일 단위가 아니라 블록 범위 + 게임 로딩 구간"이라는 한정을 반드시 붙여야 한다. [e4rat 은 2차]

정직한 포지셔닝 한 줄: 블록 단위 접근순서 재배치 개념은 BORG(2009)가 선점했고, 본 설계의 기여는 "드라이버 없이 stock Windows 위에서 사용자 모드 + 공식 디프래그 API 로 게임 타이틀 단위로 구현하고 선읽기와 결합"하는 조합·구현 방식이다.

### 설계를 바꿔야 할 결정적 제약
1. 관리자 권한 필수: 볼륨 핸들은 관리자만 연다. 재배치기(및 Kernel-File ETW)는 상주 관리자 서비스/UAC 승격 헬퍼가 되어야 한다. [확인]
2. 지속성 문제: 게임 업데이트의 파일 재작성, Windows 정기 드라이브 최적화가 배치를 되돌리거나 깨뜨릴 수 있다. 재배치 후 상태를 감시해 필요 시 재적용하는 검증·재적용 루프가 설계에 필요하다. 정기 최적화가 실제로 되돌리는지는 반드시 시험. [확인(스케줄 존재) / 되돌림은 추측]
3. WD20EZAZ 의 SMR: 대량 재배치 쓰기가 영속 캐시를 넘으면 쓰기 급락·장시간 정체 위험. 실측 전까지 수십 GB 규모 재배치를 전제로 하지 말고 핫 집합(수백 MB~수 GB)만 작은 청크로 이동해야 한다. 이동량·소요시간을 먼저 실측할 것. [SMR 모델 판정은 2차, 영향은 추측]
4. 천장 문제(계산): 핫 집합이 430MB 급이면 HDD 순차 한계로 약 3초가 하한이다. SSD급(0.8s)은 RAM 상주/SSD 캐시 없이는 불가능하다. 목표 문구를 "HDD 단독 SSD급"이 아니라 "배치 + 선읽기 + 선택적 SSD 캐시 조합으로 근접"으로 두거나, 핫 집합 축소(정말 필요한 최초 구간만)를 설계 목표에 넣어야 한다. [계산]
5. LCN→물리 위치는 보장되지 않음: "LCN 이 낮을수록 빠르다"를 가정하지 말고 설치 시 존 곡선을 실측해 가장 빠른 구간을 찾아 쓸 것. [2차]

### 이번 조사에서 확인 못한 것 (후속 시험 대상)
- FSCTL_MOVE_FILE 의 정전 시 원자성·로깅, 한 호출 최대 크기, 대상 점유 시 정확한 오류 코드.
- Performance Log Users 만으로 Kernel-File 공급자를 켤 수 있는지, ETW 실시간 지연, 캐시 적중 읽기 포함 여부.
- WD20EZAZ 개별 실측(존 곡선, SMR 지속 쓰기), Windows 정기 최적화의 되돌림 여부.
- 게임 로딩 I/O 정량 측정 논문, Steam 설문의 HDD 비율 원문.
