[English](USAGE.md) | **한국어**

# 기능 목록과 직접 사용하기

> README 에서 옮긴 기능 목록과, 자동 모드를 쓰지 않고 단계를 직접 다루는 방법입니다.

## 주요 기능

- **드라이브 목록.** HDD/SSD 구분, 용량, 모델, 등록된 게임 수를 보여 줍니다.
- **게임 등록.** 게임 이름, exe 이름, 설치 폴더, 허용 폴더, 볼륨 레이블을 정하고 예열 트리거 프로그램을 지정합니다.
- **트레이스 수집.** 게임을 한 번 실행해 첫 맵까지 로딩하면 읽기 블록을 ETW 실시간 세션으로 기록합니다(관리자 권한).
- **계획, 재배치, 되돌리기.** 기록으로 배치 계획을 만들고 적용합니다. 적용 기록(저널)을 거꾸로 적용해 예전 배치로 되돌릴 수 있습니다.
- **사전 예열.** 자동(서비스 시작, 트리거 프로그램 실행)으로 또는 "지금 예열"로 실행합니다.
- **자동 모드(기본 켬).** 지원 런처 라이브러리와 게임 시작 감지로 HDD 게임을 찾아 첫 실행을 트레이스하고, PC 가 쉬는 동안 재배치합니다. [자동 모드](AUTO_MODE.ko.md) 를 보십시오.
- **서비스.** 게임 시작을 감지해 동시 선읽기를 실행하고, 예열과 재적용을 관리합니다. Windows 서비스(LocalSystem, 자동 시작)로 상주합니다.
- **상태와 로그.** 서비스와 감시기 상태, 마지막 예열 결과, 게임별 배치 상태, 로그를 탭에서 확인합니다.
- **측정.** 게임별로 재배치 전후 콜드 재생 시간을 잽니다.
- **프로필 내보내기/가져오기.** 기록한 트레이스를 `aster profile export`·`aster profile import` 로 다른 PC 와 주고받습니다. 가져올 때는 파일 크기와 표본 해시가 같을 때만 받습니다.
- **관리 창.** 관리자 권한이 없으면 상단 안내 띠가 나타나며, 관리자 권한으로 다시 실행할 수 있습니다.
- **로그온 때 트레이.** Program Files 아래에 설치하면 설치 프로그램이 로그온 예약 작업 `AsterCache` 를 등록해 트레이를 관리자 권한으로 시작하므로 확인 창(UAC)이 뜨지 않습니다(표준 사용자 계정은 일반 권한으로 시작). 그 밖의 폴더에 설치하면 시작프로그램 바로가기를 씁니다.
- **명령행 `aster`.** 관리 창의 같은 작업을 명령으로 실행합니다. 자동화에 씁니다.
- **한국어·영어 화면.** 관리 창과 명령행이 두 언어를 지원합니다([언어](CONFIGURATION.ko.md#언어) 참고).

## 직접 하려면(선택)

단계를 직접 다루고 싶거나, 자동 모드가 건너뛴 게임을 시험할 때 씁니다. 관리 창을 관리자 권한으로 열고 **게임** 탭에서 아래 버튼을 순서대로 누릅니다.

| 순서 | 버튼 | 하는 일 |
|---|---|---|
| 1 | 등록 | exe 파일을 골라 게임을 추가합니다. |
| 2 | 트레이스 수집 | 게임을 실행해 첫 맵까지 로딩하고 끕니다. 읽기 순서를 기록합니다. |
| 3 | 계획 만들기 | 조각을 어디에 둘지 정합니다. |
| 4 | 재배치 실행 | 조각을 옮깁니다. 저널이 남습니다. |
| 5 | 지금 예열 | 그 구역을 바로 RAM 에 올립니다. |
| 6 | 측정 | 구역을 한 번 읽는 시간을 잽니다. |

같은 단계를 명령행(`aster`)으로도 할 수 있습니다. 예를 들면 다음과 같습니다.

```bat
aster discover
aster tier status
aster trace --live --exe Frostpunk.exe --origin-root D:\Games\Frostpunk --out trace.json --seconds 180
aster plan --trace trace.json --origin-root D:\Games\Frostpunk --zone-mib 1536 --out plan.json
aster apply --plan plan.json --allowed-root D:\Games --require-volume-label GAMES --dry-run
aster apply --plan plan.json --allowed-root D:\Games --require-volume-label GAMES --execute
aster prewarm --plan plan.json --origin-root D:\Games\Frostpunk
```

- `discover` 는 런처 라이브러리에서 찾은 게임 목록을 읽기 전용으로 보여 줍니다.
- `tier status` 는 SSD 보조 저장 상태를 보여 줍니다. `tier apply --game <이름>`, `tier revert --game <이름>` 또는 `--all`, `tier clean` 은 관리자 권한이 필요합니다.
- `trace --live`, `apply --execute` 는 관리자 PowerShell 에서 실행합니다.
- `--dry-run` 은 아무것도 옮기지 않고 계획만 보여 줍니다. `--require-volume-label` 이 실제 볼륨 레이블과 다르면 아무것도 옮기지 않습니다.
- `--zone-mib` 값은 예시입니다. 게임의 핫 데이터 크기에 맞춰 정하십시오. 전역 옵션 `--lang ko|en` 으로 출력 언어를 정합니다([언어](CONFIGURATION.ko.md#언어) 참고).
- 모든 명령과 옵션은 [csharp/README.ko.md](../csharp/README.ko.md) 에 있습니다.
