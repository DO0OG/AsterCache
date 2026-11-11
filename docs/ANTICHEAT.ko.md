[English](ANTICHEAT.md) | **한국어**

# 안티치트 호환

> 안티치트 게임과 관련해 AsterCache 가 하는 일과 하지 않는 일입니다. 데이터를 지키는 방법은 [안전 장치](DEVELOPMENT.ko.md#안전-장치)에 있습니다.

요즘 게임 대부분은 커널 수준 안티치트를 씁니다. 이 문서는 AsterCache 가 하는 일과 하지 않는 일을 적어, 게임에 쓸지 판단할 수 있게 합니다. 안티치트 정책은 게임마다 다르고 바뀔 수 있습니다. 한 PC 에서 Vanguard 게임(League of Legends)과 Ricochet 게임(Call of Duty)을 설치본으로 플레이한 사용 기록이 있습니다([이 PC 의 사용 기록](#이-pc-의-사용-기록)). 통제된 검증은 아니며, 계정 제재 여부는 보증하지 않습니다.

## 하는 일

- 서비스가 자기 프로세스 안에서 게임 파일을 읽기 전용, 공유 모드(읽기·쓰기·삭제 공유)로 읽거나 매핑만 합니다.
- 첫 실행의 선행 읽기도 서비스 자기 프로세스의 파일 읽기입니다. 게임의 읽기는 실시간 ETW 커널 세션으로 관찰만 하며, 게임 프로세스에 대한 핸들은 필요하지 않습니다.
- Windows 프리페치 기록(`C:\Windows\Prefetch\*.pf`)을 읽기 전용으로 읽고, 공유 프로필 zip 을 주 1회 HTTPS 로 내려받습니다. 둘 다 게임 프로세스에는 손대지 않습니다.
- 게임이 완전히 종료된 뒤에만 NTFS 조각 모음 API(`FSCTL_MOVE_FILE`)로 클러스터 위치를 옮깁니다. 파일 내용·크기·수정 시각은 바뀌지 않으며, 옮기기 전에 계획의 지문(크기와 수정 시각)을 확인합니다.

## 하지 않는 일

- 게임 프로세스에 코드 주입, 스레드 생성, 메모리 읽기·쓰기, 디버그 권한, 후킹, 오버레이, 핸들 복제를 하지 않습니다.
- 게임 프로세스는 `PROCESS_QUERY_LIMITED_INFORMATION`(조회 전용)으로만 열어 이름·경로·읽은 바이트를 봅니다. 보호된 프로세스가 이것마저 거부하면 ETW 이벤트로만 판정하고, 그 프로세스를 다시 열지 않습니다. 이 경우 거부가 확인된 뒤 20초 동안의 읽기로 판정하며, 앞 20초의 읽기는 세지 않습니다.
- 커널 드라이버를 쓰지 않습니다.

## 건너뛰거나 표시하는 파일

- 게임이 배타적으로 연 파일은 건너뜁니다.
- 자동 발견 때 설치 폴더 아래 깊이 4까지의 파일·폴더 이름만 봅니다. EasyAntiCheat, BattlEye, Vanguard(`vgc`, `vgk`), Ricochet 는 인식하고, 그 밖에 anticheat, xigncode, nprotect, gameguard 가 들어간 이름은 `unknown` 으로 표시합니다. 그런 이름이 있는 게임은 게임 탭과 `anticheat` 설정에 표시되고 로그 한 줄이 남습니다. 동작은 달라지지 않습니다. 예를 들어 `aster discover` 에서 Steam 게임 The Outlast Trials 가 `eac` 로 표시되었습니다.

| 안티치트 | 마커 | 정책 |
|---|---|---|
| EasyAntiCheat | `EasyAntiCheat` 로 시작하는 `.exe`, `EasyAntiCheat_EOS`, `start_protected_game.exe` | 자동 트레이스·고정 금지, 예열·재배치 허용 |
| BattlEye | `BattlEye`, `BEService*.exe`, `BEClient*.dll` | 자동 트레이스·고정 금지, 예열·재배치 허용 |
| Vanguard | `vgc*`, `vgk*` (`.exe`·`.sys`), `Riot Vanguard` | 자동 트레이스·고정 금지, 예열·재배치 허용 |
| Ricochet | `ricochet` 이름, `randgrid.sys` | 자동 트레이스 허용, 고정 금지, 예열·재배치 허용 |
| BlackCipher | `BlackCipher` 폴더, `BlackCall64.aes` | ETW 트레이스·고정·tier 금지, 예열·재배치 허용 |
| nProtect GameGuard | `GameGuard` 폴더, `npgg*.dll`, `GameMon.des` | ETW 트레이스·고정·tier 금지, 예열·재배치 허용 |
| XIGNCODE3 | `xigncode`·`XIGNCODE3` 폴더, `x3.xem`, `xcorona*` | ETW 트레이스·고정·tier 금지, 예열·재배치 허용 |
| Wellbia | `uncheater.exe`, `wellbia` | ETW 트레이스·고정·tier 금지, 예열·재배치 허용 |
| HackShield | `HShield` 폴더 | ETW 트레이스·고정·tier 금지, 예열·재배치 허용 |
| HoYoverse mhyprot | `mhyprot2.sys`, `mhyprot3.sys` | ETW 트레이스·고정·tier 금지, 예열·재배치 허용 |
| Denuvo Anti-Cheat | `denuvo-anti-cheat*` | ETW 트레이스·고정·tier 금지, 예열·재배치 허용 |
| FACEIT | `faceit*` | ETW 트레이스·고정·tier 금지, 예열·재배치 허용 |

## 속도 기능별로 안전하다고 보는 이유

아래는 각 기능을 안전하다고 보는 이유이며, 검증된 결과가 아닙니다.

- **예열:** 서비스 자기 프로세스에서 일반 파일 읽기를 합니다.
- **예열 유지:** 1 MiB 일반 파일 읽기 표본을 잽니다.
- **고정:** 서비스 자기 프로세스의 읽기 전용 매핑이며, 게임이나 런처가 시작되면 해제합니다.
- **재배치:** 게임이 종료된 뒤에만 하며, 파일 내용은 바뀌지 않습니다.
- **프리페치 기록과 공유 프로필:** `.pf` 파일은 서비스만 읽고, 내려받기는 주 1회 파일 하나입니다.
- **감지:** 조회 전용 프로세스 핸들 또는 ETW 이벤트로 합니다.

## 서명

실행 파일에는 코드 서명이 없어 SmartScreen 경고가 뜰 수 있습니다. 지금까지 확인된 영향은 이 경고뿐입니다. 서명 여부와 안티치트 판정의 관계는 확인하지 않았습니다.

## 이 PC 의 사용 기록

한 PC 의 2026-11-02 ~ 2026-11-10 서비스 로그를 집계한 것입니다. 통제된 검증이 아니며, 계정 제재 여부는 로그로 알 수 없고 보증하지 않습니다. 표본 수와 줄 형식은 [RESULTS_SUMMARY.ko.md](RESULTS_SUMMARY.ko.md#실사용-기록)에 있습니다.

- **League of Legends.** `vanguard` 로 판정됐습니다. Vanguard 가 있는 PC 의 Riot 게임이라는 조건으로 판정하며 설치 폴더 이름으로 잡은 것이 아닙니다. 게임 시작 감지 10회 동안 트레이스 2회 수집 뒤 8회 건너뜀, 고정 건너뜀 4회, 재배치 완료 4회였습니다.
- **Call of Duty.** 설치 폴더의 `ricochet` 이름으로 `ricochet` 판정됐습니다. 게임 시작 감지 62회 동안 트레이스 수집 14회 시작, 고정 건너뜀 7회, 재배치 완료 5회였습니다.
- 예열은 두 게임 모두 적용했습니다.

## 한계와 권고

- 자동 모드는 기본으로 켜져 있습니다. 안티치트가 있는 게임은 플레이 전에 설정 탭에서 자동 모드를 끄거나, 그 게임의 exe 를 `auto.ignore_exes` 에 넣으십시오.
- 자동 트레이스도 수동 방식과 같은 종류의 ETW 수집을 씁니다. 그래서 [안내서](GAME_GUIDE.ko.md#안티치트-게임)의 Ricochet·Vanguard 주의(와 League of Legends 참고 절)가 그대로 적용됩니다.
- SSD 보조 저장은 안티치트 폴더가 있는 게임을 건너뛰며, 게임의 exe·dll 파일은 복사하지 않습니다.
- 그 밖의 안티치트 게임은 시험하지 않았습니다.
