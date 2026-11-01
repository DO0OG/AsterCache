# AsterCache v0.7 Windows 전용 확인 (2026-11-01)

환경: Windows 11 Pro 10.0.26200, Python 3.11.4, SQLite 3.42.0, 비관리자(IsAdmin=False), 개발자 모드 켜짐(AllowDevelopmentWithoutDevLicense=1).
장치: D: = Disk 0 WDC WD20EZAZ (Get-PhysicalDisk MediaType=HDD, SATA) / C: = Disk 1 SAMSUNG MZVLB1T0HBLR (SSD, NVMe) / E: = Disk 2 Micron Crucial X8 (SSD, USB).
스크립트: 저장소 밖 임시 폴더의 win_checks.py. 원본 출력: win_checks_raw.txt, win_checks.json.

## a. _fingerprint (NTFS, D:)
[st_dev, st_ino, size, mtime_ns, ctime_ns, ChangeTime(FILETIME)] = [73177880, 1970324838219528, 196613, 1793531318525156400, 1793531318525156400, 134380049185251564]
- 새 파일에서 st_ctime_ns == 생성 시각(CreationTime) 변환값: True. 즉 Windows(Python 3.11)의 st_ctime 은 생성 시각이다.
- 같은 파일에 내용을 쓰면 st_ctime_ns 는 변하지 않고(changed: False), ChangeTime 은 변함(True), mtime 도 변함.
- 따라서 Windows 에서 변경 감지에 실제로 기여하는 것은 mtime/size 와 6번째 값 ChangeTime 이다. fp[4](ctime_ns)는 변경 감지에 쓸모없음.

## b. test_104 Windows 판 (내용 변경 후 os.utime 으로 mtime 복원)
- size 동일, mtime_ns 복원됨, st_ctime_ns 동일, ChangeTime 만 다름(134380049185901627 -> 134380049187554995).
- c.read('test.bin',0,1) = b'Z' -> 새 바이트 반환: True (통과). stats: block_hits=0, block_misses=2.

## c. 대소문자 다른 경로
- _source() 의 resolve(strict=True)가 'test.bin' / 'Test.bin' / 'TEST.BIN' 모두 실제 경로 D:\...\c\test.bin 으로 정규화.
- 세 번 읽기 후: hits=2, misses=1, block_count=1 -> 같은 캐시 키, 중복 적재 없음.
- origin_root 를 소문자/대문자 문자열로 열어도 root 가 실제 대소문자로 정규화되어 기존 캐시에 hits=1, misses=0 (중복 없음).

## d. 서로 다른 드라이브 (origin D:, cache C:)
- 생성 시 ValueError 없음. cache dir = C:\AsterCacheTest\d\astercache-v07-private. 읽기 바이트 일치: True. 두 번째 읽기 hits=2/misses=2, on_disk_db_bytes=205816.

## 5. 참고용 벤치 (bench_cross_drive.json) - OS 페이지 캐시를 비우지 않음
64KiB x 256 블록(16MiB), 원본 D:(HDD), 캐시 C:\AsterCacheTest(NVMe). 블록 단위 read() 256회.
| 시도 | 콜드(캐시 비어 있음) | 재읽기 | 단순 open/read(원본) |
|---|---|---|---|
| 1 | 5.6367 s | 0.3542 s | 0.0028 s |
| 2 | 5.3620 s | 0.3413 s | 0.0037 s |
| 3 | 4.8904 s | 0.3454 s | 0.0027 s |
- 임시 파일을 막 써서 원본 읽기는 OS 페이지 캐시에서 나왔을 가능성이 높음. "콜드"는 캐시 DB 가 빈 상태일 뿐 디스크 콜드가 아님.
- 콜드 ≈ 블록당 약 19-22 ms: read() 한 번마다 synchronous=FULL 트랜잭션이 커밋되기 때문으로 추정(원인 미측정).
- 정리 확인: C:\AsterCacheTest, D:\astercache_chk_* 모두 삭제됨.
