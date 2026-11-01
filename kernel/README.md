# kernel/ -- AsterFlt 미니필터 PoC (K1 관찰)

설계 근거는 `docs/design_v1_relayout.md` 2절(권한 분리, 가로채기 없음)과 4절(안전 설계), 선행 조사
`docs/prior_art_and_windows_api.md` B절(재배치 API)·C절(ETW 한계)입니다.

## 1. 목적과 범위

| 단계 | 내용 | 상태 |
|---|---|---|
| K1 관찰 | 지정 볼륨의 지정 루트 아래 파일의 `IRP_MJ_READ` 를 Pre-Read 에서 **기록만** 합니다 (파일 ID, 오프셋, 길이, 페이징·비캐시·fast I/O 플래그, 요청 PID, 시각) | 소스 작성 (이 폴더). 컴파일 여부는 아래 "빌드 상태" |
| K2 비교 | VM 안에서 K1 트레이스와 ETW(Tracer) 트레이스를 같은 재생으로 비교 | 절차 문서만 (5절) |
| K3 SSD 캐시 리다이렉션 | 정책 표에 걸린 비캐시·페이징 읽기를 SSD 사본에서 채워 완료 | 설계 문서만 (6절). 코드 없음 |

왜 K1 인가: ETW 는 관리자 권한과 세션이 필요하고, 캐시 적중·fast I/O 가 이벤트에 포함되는지 불확실하며(조사 C1), 지연이 약 1초대일 수 있습니다. 드라이버는 같은 읽기를 IRP 단계에서 직접 봅니다. 다만 "가로채기 없음"이 v1 설계의 핵심 장점이므로 K1 은 **관찰만** 하고 IRP 를 바꾸지 않습니다.

드라이버가 하지 않는 일: 읽기 대체·완료, 쓰기/삭제/이름 변경 관찰, 파일 내용 접근, 자동 적재(StartType=3 수동), 계획 해석 (사용자 모드 서비스가 계산한 정책만 받음).

## 2. 파일

| 파일 | 내용 |
|---|---|
| `AsterFlt/AsterFlt.c` | DriverEntry, 등록, InstanceSetup(볼륨 GUID), PostCreate(파일 ID·루트 일치 -> stream context), Pre-Read, 링 버퍼, 통신 포트, FilterUnload |
| `AsterFlt/inc/asterflt_proto.h` | 사용자 모드와 공유하는 메시지 구조체 (고정 크기, 8바이트 정렬, 크기 정적 검사) |
| `AsterFlt/AsterFlt.inf` | 설치 정보. 임시 altitude 365990, **배포 전 Microsoft 할당 필요** |
| `AsterFlt/AsterFlt.vcxproj` | WDM 기반 미니필터, x64, 서명 끔 |
| `.gitignore` | `x64/`, `*.sys`, `*.pdb`, `*.cer` 등 제외 |

## 3. 인터페이스 (통신 포트 하나, 메시지 세 종류)

포트 이름 `\AsterFltPort`, 연결 1개, 보안 서술자는 `FltBuildDefaultSecurityDescriptor(FLT_PORT_ALL_ACCESS)` (Administrators·SYSTEM). 사용자 모드는 `FilterConnectCommunicationPort` + `FilterSendMessage` 요청-응답만 씁니다 (드라이버가 먼저 밀어내지 않음).

| 명령 | 요청 | 응답 |
|---|---|---|
| `ASTER_CMD_SET_POLICY` | 볼륨 GUID(`{...}`), 볼륨 상대 루트(`\Games\Foo`), Arm 0/1 | 없음 |
| `ASTER_CMD_GET_RECORDS` | 명령 헤더 | `ASTER_BATCH_HEADER` + 기록 N건(가져간 만큼 링에서 제거) |
| `ASTER_CMD_GET_STATS` | 명령 헤더 | `ASTER_STATS` (누적 기록, 누적 버림, 대기 수, 세대) |

동작 규칙
- 정책은 연결이 끊기거나 언로드되면 자동으로 꺼집니다 (관찰 중단).
- 정책을 바꾸면 세대(Generation)가 올라가 이미 만든 stream context 가 낡은 것으로 취급되고, 새 정책은 **이후에 열리는 파일**부터 적용됩니다. 정책 설정 전에 이미 열려 있던 핸들의 읽기는 기록되지 않습니다 (PostCreate 에서만 이름을 조회하기 때문).
- 링 버퍼는 65536건(40바이트, 약 2.5 MiB 비페이지 풀). 가득 차면 새 기록을 버리고 버린 수만 셉니다.
- 볼륨 비교는 InstanceSetup 에서 GUID 를 인스턴스 컨텍스트에 저장해 두고, 정책이 이미 켜진 상태에서 붙을 때 다르면 부착을 거부합니다. 정책이 나중에 켜지면 이미 붙은 다른 NTFS 볼륨에도 필터가 붙어 있고, PostCreate 에서 GUID 로 걸러집니다 (읽기 경로 비용은 컨텍스트 조회 실패 한 번).
- 파일 ID 는 NTFS 의 64비트 파일 참조 번호입니다 (`FileInternalInformation`). NTFS 외 파일 시스템에는 붙지 않습니다.
- 커널 모드 요청(`RequestorMode == KernelMode`)으로 열린 파일은 관찰하지 않습니다.

## 4. 위험과 절대 하지 않을 것

파일 시스템 필터는 모든 I/O 경로에 끼어드는 커널 코드입니다. 버그 한 줄이 BSOD, 데이터 손상, 부팅 불가로 이어질 수 있고, 안티치트(Ricochet, Vanguard 등) 는 알 수 없는 커널 드라이버를 의심할 수 있습니다.

**이 PC(개발·게임 PC)에서 절대 하지 않을 것**
1. `bcdedit` 의 모든 변경 (testsigning, nointegritychecks, debug, hypervisorlaunchtype 포함).
2. Secure Boot, BIOS/UEFI, 메모리 무결성(HVCI), VBS 설정 변경.
3. 드라이버 적재·등록: `fltmc load`, `sc create ... type= kernel` 또는 `type= filesys`, `pnputil /add-driver`, INF 우클릭 설치, `rundll32 setupapi` 설치, `System32\drivers` 로 복사.
4. 호스트에서 Driver Verifier(`verifier`) 켜기.
5. 인증서를 Root·TrustedPublisher 저장소에 넣기, 호스트 인증서로 `signtool` 서명.
6. Hyper-V·Windows 기능 켜기/끄기, 안티치트 서비스 중지·변경.
7. 사용자 확인 없는 WDK 설치·EWDK 다운로드.

이 PC 에서 허용되는 것은 소스 편집과 빌드(`msbuild`)뿐이고, 빌드 결과 `.sys` 는 서명하지 않으며 `.gitignore` 로 저장소에서 제외됩니다. 서명되지 않은 `.sys` 는 일반 부팅 상태에서는 적재 자체가 거부됩니다 (그래도 적재를 시도하지 않습니다).

## 5. VM 절차 (K2, 사용자가 결정한 뒤 사용자가 실행)

호스트 변경이 없는 환경이 전제입니다. Hyper-V 를 켜는 것 자체가 호스트 변경이므로 사용자 결정이 먼저이고, 다른 PC 가 있으면 그쪽을 권장합니다.

1. 별도 VM 준비 (Windows 11 x64, 시험용 NTFS 볼륨 하나 추가). 호스트에서 VM 으로 게임 데이터를 공유하지 않고 합성 데이터를 씁니다.
2. VM 설정에서 Secure Boot 끔.
3. **VM 안에서만** 테스트 서명 켜기 (`bcdedit /set testsigning on`, 재부팅). 호스트에서는 하지 않습니다.
4. VM 체크포인트 생성.
5. 빌드된 `AsterFlt.sys`/`AsterFlt.inf` 를 VM 으로 복사하고 VM 안에서 테스트 인증서로 서명, `fltmc load AsterFlt`.
6. VM 안에서 Driver Verifier 켜기 (`AsterFlt.sys` 대상, 재부팅).
7. 정책 설정 후 같은 재생(예: `D:\AsterBench` 와 같은 합성 데이터)을 실행하고 K1 트레이스와 ETW 트레이스를 동시에 수집.
8. 비교 항목: 읽기 건수, 캐시 적중·fast I/O 포함 여부(`ASTER_REC_FASTIO`), 오프셋·길이 일치, 시각 지연(ETW 대비), 링 버림 수.
9. 언로드 시험: 클라이언트 연결 중 `fltmc unload`(거부되어야 함), 연결 해제 후 언로드(성공), 부하 중 언로드.
10. `fltmc unload AsterFlt`, 체크포인트로 복귀.

## 6. K3 설계 (코드 없음, K1·K2 결과와 사용자 결정 뒤에만)

착수 조건: E4/E5 실측에서 (1) 핫 존 밖 긴 꼬리 미스가 로딩 시간의 대부분이거나, (2) 동시 모드에서 게임이 스트림을 앞질러 헤드를 흔드는데 ETW 지연으로는 속도 조절이 늦을 때. 조건이 실측되기 전에는 만들지 않습니다.

- 정책 표: (볼륨 GUID, 파일 ID, VCN 범위, SSD 사본 위치). 서비스가 계산해 통신 포트로 내려보내고, 드라이버는 계획을 해석하지 않습니다.
- 걸리는 읽기: 비캐시·페이징 읽기만. Pre-Read 에서 표를 조회해 맞으면 `FltReadFile` 로 SSD 사본에서 읽어 사용자 버퍼를 채우고 `FLT_PREOP_COMPLETE`. 못 채우거나 어긋나면 원래 경로로 보냄(`SUCCESS_NO_CALLBACK`).
- 일관성: 쓰기·잘라내기(set EOF/allocation)·이름 바꾸기·삭제·FSCTL_MOVE_FILE 이 오면 그 파일의 정책을 즉시 무효화. 무효화와 진행 중 리다이렉션의 경합은 파일별 푸시 락/refcount 로 직렬화.
- 캐시·oplock: 캐시된 읽기는 건드리지 않음. oplock 중단, 파일 변경 시 SSD 사본 폐기. MDL 읽기(`IRP_MN_MDL`)와 페이징 읽기는 버퍼 종류별 처리(시스템 버퍼 매핑 또는 MDL)를 명시.
- 실패 시 항상 원래 디스크 경로로 내려보내 데이터 정확성이 SSD 사본에 의존하지 않게 함 (사본은 검증된 해시와 일치할 때만 유효).
- 위험이 K1 보다 한 단계 큼: 데이터 손상 가능성이 생기므로 VM + 합성 데이터 + 해시 대조 없이는 시험하지 않습니다.

## 7. 빌드

WDK(Windows Driver Kit)와 VS 의 WDK 확장이 필요합니다. 설치·EWDK 다운로드는 사용자 결정 사항입니다 (관리자 권한, 수 GB).

```
msbuild kernel\AsterFlt\AsterFlt.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild kernel\AsterFlt\AsterFlt.vcxproj /p:Configuration=Release /p:Platform=x64 /p:RunCodeAnalysis=true
```

출력은 `kernel\AsterFlt\x64\Release\` (또는 `kernel\x64\Release\`) 아래이며 서명하지 않고 적재하지 않습니다.

빌드 상태: 이 문서를 쓴 시점에 WDK 가 설치되어 있지 않아 **미실행**입니다 (`Windows Kits\10\Include\*\km` 없음). 소스는 컴파일된 적이 없으므로 첫 빌드에서 오류·경고가 나오면 그것부터 수정합니다.

## 8. 소스 검토 체크리스트 (WDK 없이 작성한 코드의 자체 검토)

| 항목 | 점검 결과 | 첫 빌드·VM 에서 다시 볼 것 |
|---|---|---|
| Pre-Read IRQL | 이름 조회·페이지드 풀·대기 없음. `FltGetStreamContext`(<= DISPATCH), 스핀락, `KeQueryInterruptTime` 만 사용 | `FltGetRequestorProcessId` 의 IRQL 상한 문서 재확인 (지금은 APC 이하에서만 호출, 아니면 PID 불명 플래그) |
| Pre-Read 반환 | 모든 경로 `FLT_PREOP_SUCCESS_NO_CALLBACK`, IRP 수정·완료 없음 | - |
| PostCreate IRQL | `FltGetFileNameInformation` 은 PASSIVE 요구. PostCreate 는 요청 스레드 문맥으로 가정 | IRP_MJ_CREATE 의 post-op IRQL 보장 문서 재확인 |
| 풀 종류 | 링 버퍼: `ExAllocatePool2(POOL_FLAG_NON_PAGED)`. 컨텍스트: `NonPagedPoolNx`. 임시 GUID 버퍼: 페이지드, PASSIVE 에서만 사용 후 해제 | 최소 대상 Windows 10 2004 (`ExAllocatePool2`) |
| 해제 경로 | InstanceSetup `Cleanup:` 에서 컨텍스트 참조와 임시 풀 해제, PostCreate `Done:` 에서 stream/name/instance 참조 해제, DriverEntry 실패 시 ring·filter·port 역순 정리 | Driver Verifier 풀 추적 |
| 링 버퍼 동시성 | 생산자·소비자 모두 같은 스핀락 안에서 Head/Tail 갱신. 사용자 버퍼 복사는 락 밖. `Draining` 으로 소비자 1명 보장 | 부하 시험에서 Dropped·Pending 합산이 맞는지 |
| 사용자 버퍼 | 입출력 모두 `ProbeForRead/Write` + `__try`. 길이·버전·정렬·GUID/루트 형식 검증 | 잘못된 입력 퍼징은 VM 에서 |
| 연결 끊김 | `AsterDisconnect` 에서 정책 끄기, 세대 증가, `FltCloseClientPort`. 연결은 1개 | - |
| 언로드 경합 | 클라이언트가 붙어 있으면 비강제 언로드 거부. 서버 포트 닫기 -> 정책 끄기 -> Rundown 대기 -> `FltUnregisterFilter` -> 그다음에 ring 해제 | 강제 언로드 중 늦게 오는 Disconnect 에서 `g.Filter` 가 NULL 이면 클라이언트 포트를 닫지 못함 (가드만 있음). VM 에서 확인 |
| stream context | 루트 밖 파일은 만들지 않음. 세대 불일치는 무시 | 같은 FCB 를 여는 서로 다른 핸들의 컨텍스트 공유 동작 |
| 알려진 한계 | 정책 설정 전에 열린 파일, 커널 모드 요청, 비NTFS 는 관찰하지 않음 | K2 비교에서 누락률로 확인 |
| 미검증 | WDK 없음: 컴파일, 정적 분석(`/p:RunCodeAnalysis`), INF 검증(`infverif`), vcxproj 속성 이름(`SignMode`, `EnableInf2cat`, `Driver_SpectreMitigation`) | 첫 빌드에서 확인 |
