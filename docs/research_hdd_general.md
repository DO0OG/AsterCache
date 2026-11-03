# HDD 자체 속도: 게임 밖 일반 I/O 에서 사용자 모드 서비스가 할 수 있는 것

조사일: 2026-11-03. 대상: 게임 밖 일반 파일 I/O 를 다루는 사용자 모드 서비스. 게임 콜드 로딩 노트(research_cold_limit.md)와 짝을 이룹니다.
전제: WD20EZAZ(2 TB, DM-SMR) 순차 약 135~140 MB/s(사용자 실측). 고대역 존이 빈 영역보다 1.6배 느렸습니다(E6 실측, 빈 영역 300 MB/s 수치는 잘못된 측정으로 판정). 재배치는 Low I/O·유휴 때만 실행합니다.

## 조사 방법과 한계 (먼저 읽을 것)

- 이 노트는 조사 결과(항목 1~6과 출처 목록)를 표로 정리한 것입니다. 이번 작성에서 출처 원문을 다시 열어 확인하지는 않았습니다. 수치를 인용하기 전에 원문을 확인하십시오.
- 판정: 바로 = 위험이 낮아 바로 넣습니다. 측정 = 이 디스크에서 실측 후 정합니다. 제외 = 효과가 없거나 위험이 커서 쓰지 않습니다. 미확인 = 근거를 찾지 못했습니다.
- 용어: MFT = 마스터 파일 테이블, ZBR = 존 비트 기록, DM-SMR = 장치 관리형 기와식 기록, NCQ = 네이티브 명령 큐, LCN = 논리 클러스터 번호.

## 핵심 결론 (3줄)

1. 일반 I/O 의 순차 읽기 바닥은 읽는 존의 속도가 정합니다. 사용자 모드로 할 수 있는 것은 읽기 방식과 쓰기 간섭 회피 같은 조정이며, 큰 배수 이득은 기대하기 어렵습니다(추정).
2. Windows 설정 중 바로 쓸 것은 거의 없습니다. 마지막 접근 시간과 8.3 이름은 측정 후 정하고, 플러시 끄기·SysMain 끄기·MFT 영역 예약은 제외합니다.
3. 가장 큰 변수는 디스크 자체입니다. 존 위치와 DM-SMR 쓰기 후 정리 상태를 이 디스크에서 직접 측정해야 합니다.

## 1. Windows 설정

| 항목 | 근거 | 판정 | 비고 |
|---|---|---|---|
| NTFS 마지막 접근 시간 끄기 (`fsutil behavior set disablelastaccess`) | MS 문서: 속도 향상이라고만 설명, 수치 없음 | 측정 | 백업류 증분 판정에 영향 가능 |
| 8.3 짧은 이름 끄기 | MS 문서: 디렉터리 조회 비용만 설명 | 측정 | 구형 프로그램 호환성 위험 |
| MFT 영역 예약 (200 MB 단위 1~4) | MS 문서: 평균 파일 2 KB 이하일 때만 권장 | 제외 | 게임·미디어 파일에는 해당 없음 |
| 쓰기 캐시 버퍼 플러시 끄기 | MS 경고: 정전 시 데이터 손실 | 제외 | 안전 문제 |
| SysMain 끄기 | 언론 실측 2대: 부팅 차이 없음, 콜드 앱 실행은 느려짐 | 제외 | 유지 |
| 조각 모음 일정, 전원 관리, 클러스터 크기 | 출처 없음 | 미확인 | 후속 조사 |

## 2. DM-SMR 디스크 (WD20EZAZ 등)

| 항목 | 근거 | 판정 | 비고 |
|---|---|---|---|
| 영구(미디어) 캐시 크기와 위치 | Seagate 5 TB 20 GiB, 8 TB 25 GiB(FAST 2015 인용). WD20EZAZ 는 미확인 | 미확인 | Seagate 값은 용량의 약 0.3~0.4% 참고치일 뿐 |
| 지속 쓰기 후 성능 급락 | 커뮤니티 최악 시험: 4K 랜덤 쓰기 90분 뒤 120 → 1.5 IOPS, 백로그 수 시간 | 측정 | 커뮤니티 단일 시험 |
| TRIM 지원 | WD 공식: 내장 SMR HDD 는 TRIM 지원. NTFS 기본 켜짐 | 바로 | 활성 상태 확인. 실제 전달 여부는 측정 |
| 유휴 확보 후 쓰기 (몰아치기 완화 간격) | 논리상 맞음, 실측 없음 | 측정 | 정리 완료 시간 측정 필요 |
| 재배치 조건 (Low I/O·유휴 때만) | 현재 구현 | 바로 | 유지 |

## 3. 존 배치와 조각 모음

| 항목 | 근거 | 판정 | 비고 |
|---|---|---|---|
| `defrag /B` (부팅 최적화) | MS defrag 문서 | 측정 | 효과 수치 없음 |
| `defrag /X` (여유 공간 통합) | MS defrag 문서 | 측정 | 재배치 후 빈 영역 정리 용도 확인 |
| `defrag /K`, `/L` | MS defrag 문서 | 제외 | HDD 에서 의미 없다고 판정 |
| 바깥 트랙이 빠른 이유 | 위키백과 Zone bit recording (ZBR 원리) | 바로 | 배치 원칙으로 사용 |
| 바깥·안쪽 속도비 | 출처마다 다름: 100:60, 1.45~1.9배(1997), 일부 모델 5% | 측정 | "1.5~2배 보편" 주장은 지지 안 됨 |
| LBA 0 이 바깥인지 | 통념은 ZBR 근거. SMR 은 단조 아닐 수 있음 | 측정 | 고정이 아님 |
| 이 디스크 구간별 처리량 | E6 실측: 고대역 존 1.6배 느림 | 측정 | 1차 확인 완료, 구간별 재확인 필요 |

## 4. 메모리 캐시

| 항목 | 근거 | 판정 | 비고 |
|---|---|---|---|
| 스탠바이 리스트 우선순위 0~7 (낮은 쪽부터 회수) | 직접 대응 출처 없음(조사 결과 기재) | 측정 | RAMMap Priority Summary 로 확인. 서비스 우선순위는 NORMAL 유지 |
| ReadyBoost | 위키백과: Windows 11 22H2 이후 제거 보고(출처 엇갈림). 시스템 SSD 면 자동 비활성 | 제외 | |
| LargeSystemCache | 근거 없음 | 미확인 | |
| 메모리 압축 | 근거 없음 | 미확인 | |
| `fsutil behavior set memoryusage 2` | MS fsutil behavior 문서 | 측정 | 파일 열기·닫기가 매우 많을 때만 의미 |

## 5. 읽기 패턴

| 항목 | 근거 | 판정 | 비고 |
|---|---|---|---|
| 캐시 슬롯 256 KB | MS File Caching 문서 | 바로 | 읽기 크기 정렬 기준 |
| FILE_FLAG_SEQUENTIAL_SCAN | MS CreateFile 문서: 미리 읽기 확대 힌트 | 바로 | 순방향 읽기에만 사용, 역방향 금지 |
| 다음 블록 비동기 선발행 (최대 처리량) | Raymond Chen 블로그 3건(출처 목록) | 바로 | 서비스 읽기 경로에 적용 |
| 레지스트리 미리 읽기 크기 | 근거 없음 | 미확인 | |
| NCQ 큐 깊이 16 의 랜덤 읽기 | 2005 LKML 게시물, bit-tech 기사. 보고치 85%, 편차 큼 | 측정 | 이 디스크 E6 에서는 큐 깊이 효과 없음 |
| 큰 I/O 가 유리 (64 KB 까지 상승) | 출처 목록의 bit-tech 기사 | 측정 | 4~16 MiB 비버퍼드 읽기는 별도 측정 |

## 6. 기타

| 항목 | 근거 | 판정 | 비고 |
|---|---|---|---|
| SysMain·Prefetch 의 부팅·앱 추적, 미리 읽기, Layout.ini 재배치 | 위키백과 Prefetcher | 제외 | Windows 가 이미 함. 중복 구현 안 함 |
| 최근 문서·탐색기 폴더 메타데이터 예열 | 선행 도구·측정치 없음 | 측정 | E8 실험 진행 중 |
| 자주 쓰는 폴더를 한 존에 모으기 | 선행 도구·측정치 없음 | 측정 | Prefetch 재배치와 비교 필요 |
| 불필요한 flush·WRITE_THROUGH 회피 | MS File Caching 문서 | 바로 | 쓰기 경로 점검 |

## 결론

1. 바로 적용: SEQUENTIAL_SCAN 과 비동기 선읽기(예열이 이미 디스크 한계 142~154 MB/s 이므로 추가 이득은 작을 것으로 추정), 불필요한 flush 회피, TRIM 활성 확인, 재배치의 Low I/O·유휴 조건 유지.
2. 측정 필요: 이 디스크 구간별 처리량, `disablelastaccess`, `memoryusage 2`, E8, 쓰기 후 정리 대기 시간.
3. 제외: 쓰기 플러시 끄기, SysMain 끄기, ReadyBoost, MFT 영역 예약, `defrag /K`·`/L`.

## 다음 측정

1. 외곽, 중간, 안쪽 LCN 에서 각 1 GiB 순차 읽기를 두 번씩 측정하고 E6 값과 비교합니다.
2. 대량 쓰기 직후와 유휴 30분 이상 뒤에 같은 존을 순차 읽기로 비교합니다.
3. `disablelastaccess`와 `memoryusage 2`를 켠 상태와 끈 상태에서 백업과 파일 열기 많은 작업 시간을 비교합니다.
4. `fsutil behavior query DisableDeleteNotify` 로 TRIM 상태를 확인하고, 삭제 뒤 쓰기 지연 변화를 봅니다.
5. E8 메타데이터 예열 결과를 이 노트에 추가합니다.

## E8: 메타데이터 예열 실험 (2026-11-03)

- 가설: 파일이 수천 개인 게임은 파일을 열 때마다 MFT 를 찾으러 헤드가 이동해 콜드 로딩에 숨은 비용이 든다. 메타데이터만 미리 올려 두면 이 비용이 줄어드는가.
- 데이터: 합성 3000 파일 × 1 MiB(3 GB), 디스크 전체에 흩뿌림, 트레이스 6000 항목. 대상은 HDD WD20EZAZ(DM-SMR).

E8(스탠바이 비움만), 단위 s:

| 구분 | 측정 |
|---|---|
| 라운드 A (전부 콜드) | 58.8 / 24.3 / 24.8 |
| 라운드 B (메타 예열 뒤) | 24.3 / 25.2 / 28.6 |
| 메타 예열 비용 | B1 30.3 → B2 0.47 → B3 0.42 |

해석:
1. 3000 파일을 처음 여는 비용 30 s 는 파일당 약 10 ms(MFT 탐색 1회)로 확인된다.
2. 스탠바이 비움은 MFT 캐시를 지우지 못한다. 2 라운드부터 A와 B 모두 메타데이터가 웜 상태였으므로 이 비교는 무효다.
3. A1 58.8 s 는 데이터셋을 쓴 직후라서, SMR 디스크의 쓰기 뒤 정리(미디어 캐시 비우기)가 읽기를 약 2.4배 늦춘 것으로 해석한다(이후 라운드는 24 s). 재배치(쓰기) 직후 바로 예열하는 순서는 SMR 에서 손해일 수 있으므로 확인 항목이다.

E8b(깊은 비움: 시스템 파일 캐시 크기 설정 + 작업 집합 비움 + 스탠바이 비움):
- 가용 RAM 이 14.3 → 23.6 GB 로 늘었고, 모든 프로세스의 작업 집합까지 비워져 PC 전체가 페이지 폴트로 흔들렸다.
- 콜드 재생은 37 / 63 / 69 / 92 s 로 라운드마다 늘어 측정이 오염되었다. 메타 예열 비용도 0.9~1.7 s 로 여전히 웜이어서 메타데이터 콜드 상태를 만들지 못했다.
- 결론: MFT 캐시는 사용자 모드 비움으로 재현되지 않고 재부팅으로만 콜드가 된다. 이 방법은 폐기한다.

결론:
1. 메타데이터 콜드 비용은 실재하고 크다. 파일당 10 ms, 3000 파일이면 30 s 로 데이터 콜드 24 s 보다 크다.
2. 기존 RAM 예열은 핫 파일을 열어 읽으므로 그 파일들의 메타데이터도 함께 웜이 된다. 별도 메타 예열이 더해 줄 것은 예열이 돌지 않은 콜드 시작뿐이며, 그때는 데이터도 콜드라서 해법은 같다(예열이 제때 돌게 하기).
3. 실제 재부팅 뒤 게임 시작의 파일 열기 지연을 재야 한다. 서비스에 "열기 지연" 표본 기록을 후보로 둔다.

다음 측정 추가:
- 재부팅 직후 예열 전과 후의 파일 열기 지연
- SMR 쓰기 뒤 읽기 저하가 지속되는 시간(재배치 뒤 예열을 N 분 늦추는 것이 이득인지)

## 출처

- https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/fsutil-behavior
- https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/defrag
- https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching
- https://devblogs.microsoft.com/oldnewthing/20221130-00/?p=107505
- https://devblogs.microsoft.com/oldnewthing/20130416-00/?p=4643
- https://devblogs.microsoft.com/oldnewthing/20100909-00/?p=12913
- https://support-en.wd.com/app/answers/detail/a_id/25185
- https://technet.microsoft.com/magazine/cc162480.aspx
- https://en.wikipedia.org/wiki/ReadyBoost
- https://sites.google.com/site/samilehtinenps/blog/external-smr-drive-worst-case-write-performance-test (커뮤니티)
- https://www.usenix.net/system/files/conference/fast15/fast15-paper-aghayev.pdf
- https://blocksandfiles.com/2020/04/15/seagate-2-4-and-8tb-barracuda-and-desktop-hdd-smr/
- https://www.windowslatest.com/2026/10/26/everyone-says-disabling-sysmain-speeds-up-windows-11-so-i-benchmarked-it-on-two-pcs-k4l9/
- https://macperformanceguide.com/Storage-WhyYouNeedMoreThanYouNeed.html (커뮤니티)
- https://www.pythian.com/blog/hard-drive-inner-or-outer (커뮤니티)
- https://lkml.rescloud.iu.edu/0505.3/1540.html
- https://bit-tech.net/reviews/tech/storage/the-facts-4k-advanced-format-hard-disks/4/
- https://nascompares.com/answer/list-of-wd-cmr-and-smr-hard-drives-hdd/
