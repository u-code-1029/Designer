# DrillFlow Designer configuration

이 문서는 배포 기본값, 사용자별 설정, 환경 변수 override와 설정 화면이 어떤
우선순위 및 적용 시점을 가지는지 설명한다. 특히 파일 기반 장비 통신 설정과 향후
SignalR 실시간 영상 설정을 구분한다.

> **현재 구현 상태**
>
> XML request/response 파일 교환과 파일 기반 Live Interaction은 실제 동작한다.
> `DrillFlow:RealtimeVideo`는 향후 실시간 설비 화면을 위한 옵션, 검증, 설정 UI 및
> 표시 surface만 준비된 상태이다. 현재 빌드에는 `HubConnection`을 생성하거나 JWT를
> 읽고 SignalR 프레임을 수신하는 client service가 없다.

## 설정 원본과 우선순위

같은 항목이 여러 곳에 있으면 아래에서 나중에 설명한 값이 우선한다.

1. 옵션 클래스의 코드 기본값
2. 실행 파일 옆의 `appsettings.json`
3. `DRILLFLOW_CONFIG_` 접두사가 붙은 비밀이 아닌 환경 변수
4. `%LocalAppData%\DrillFlow\appsettings.user.json`
5. canonical 사용자 파일이 없을 때만 읽는 `%LocalAppData%\DrillFlow\settings.json`

4번과 5번은 동시에 적용되지 않는다. `appsettings.user.json`이 있으면 legacy
`settings.json`은 읽지 않는다. 사용자 파일은 일반 `IConfiguration` provider로 병합하지
않고 시작 시 별도로 읽는다. 따라서 사용자 파일의 `DrillFlow.Communication` 또는
`DrillFlow.RealtimeVideo` 그룹이 있으면 해당 UI 관리 그룹은 배포 기본값 및 환경 변수보다
우선한다.

사용자 통신 설정은 한 그룹으로 검증한다. 한 필드라도 잘못되면 유효한 필드만 섞어서
적용하지 않고 `EquipmentCommunication` 배포 기본값 전체를 유지한다. 실시간 영상 설정도
그룹 단위로 fallback한다. 잘못된 초안은 설정 화면에는 남겨 사용자가 수정할 수 있다.

`appsettings.json`은 `reloadOnChange: false`로 읽으므로 파일이나 시작 환경 변수 변경은 앱을
재시작해야 적용된다.

## `appsettings.json`

배포 기본값은 다음 두 section에 둔다.

```json
{
  "DrillFlow": {
    "Language": "Auto",
    "Theme": "System",
    "ValidateWorkflowOnEveryChange": true,
    "RealtimeVideo": {
      "Enabled": false
    }
  },
  "EquipmentCommunication": {
    "ExchangeDirectory": "C:\\DrillFlow\\Exchange",
    "LiveImageDirectory": "C:\\DrillFlow\\Exchange\\.drillflow-live",
    "RequestFileName": "request.xml",
    "ResponseFileName": "response.xml",
    "ResponseTimeout": "00:00:30"
  }
}
```

`EquipmentCommunication`의 시간 값은 .NET `TimeSpan` 문자열이다. 반면 설정 화면은 모든
시간을 초 단위 숫자로 표시하고 입력받는다.

## 사용자 설정과 legacy migration

설정 화면에서 저장하면 다음 파일을 임시 파일 및 replace/move 방식으로 갱신한다.

```text
%LocalAppData%\DrillFlow\appsettings.user.json
```

canonical 파일의 최상위 구조는 다음과 같다.

```json
{
  "DrillFlow": {
    "Language": "ko-KR",
    "Theme": "Dark",
    "ValidateWorkflowOnEveryChange": true,
    "Communication": {
      "ExchangeFolder": "C:\\DrillFlow\\Exchange",
      "ResponseTimeoutSeconds": 30.0,
      "RetryDelaySeconds": 1.0,
      "PollingIntervalSeconds": 0.05,
      "RequestPublishDelaySeconds": 0.1,
      "StableReadDelaySeconds": 0.05
    },
    "RealtimeVideo": {
      "Enabled": false
    }
  }
}
```

사용자 파일의 통신 시간 필드는 모두 `*Seconds` 이름과 소수 초 값으로 저장된다. 설정 화면도
`Response timeout`, polling, publish delay, retry delay, stable-read delay를 모두 초 단위로
입력한다. 과거 `*Milliseconds` 키는 이전 파일을 읽어 마이그레이션하기 위한
deserialization-only alias이며 새 파일에는 다시 기록되지 않는다.

canonical 파일이 없고 legacy `%LocalAppData%\DrillFlow\settings.json`이 있으면 이를 읽은 뒤
canonical 파일로 저장한다.

- legacy 파일은 삭제하거나 덮어쓰지 않고 backup source로 보존한다.
- legacy `RetryIntervalMilliseconds`는 `RetryDelaySeconds`로 변환한다.
- 이전 기본 파일명 쌍인 `request.json`/`response.json`은 `request.xml`/`response.xml`로
  migration한다. 사용자가 지정한 다른 파일명은 그대로 둔다.
- legacy JSON은 `DrillFlow` root가 있는 형식과 그 내부 객체만 저장한 형식을 모두 읽는다.
- JSON이 손상되었거나 읽을 수 없으면 로그에 경고를 남기고 유효한 기본값을 사용한다.

## 환경 변수 override와 JWT 분리

일반 설정 override에는 `DRILLFLOW_CONFIG_` 접두사만 사용한다. 접두사는 제거되고 `__`는
nested configuration 구분자로 해석된다.

Generic Host의 기본 무접두사 environment provider는 composition root에서 제거한다. 따라서
`DRILLFLOW_SIGNALR_JWT`를 포함한 임의의 프로세스 환경 변수는 일반 `IConfiguration` 열거,
Options binding 또는 구성 진단에 들어오지 않고, `DRILLFLOW_CONFIG_` 값만 비밀이 아닌 override로
명시적으로 수집된다.

```powershell
$env:DRILLFLOW_CONFIG_EquipmentCommunication__ResponseTimeout = "00:00:45"
$env:DRILLFLOW_CONFIG_EquipmentCommunication__PollingInterval = "00:00:00.100"
$env:DRILLFLOW_CONFIG_DrillFlow__RealtimeVideo__SignalR__HubEndpoint = "https://controller.example.com/equipment-screen"
```

이 override 경로에는 JWT, password 또는 bearer token을 넣지 않는다. 위에서 설명한 것처럼
동일 그룹이 사용자 파일에도 저장되어 있으면 현재 구현에서는 사용자 파일이 우선한다.

JWT 인증을 선택할 경우 JSON과 옵션에는 실제 token이 아니라 다음 참조만 저장한다.

- `CredentialName`: 향후 DPAPI/Credential Manager 기반 저장소에서 사용할 논리 이름
- `TokenEnvironmentVariable`: token을 가진 별도 환경 변수의 **이름**

기본 환경 변수 이름은 `DRILLFLOW_SIGNALR_JWT`이다.

```powershell
# 값은 실제 배포 token이며 appsettings*.json에 복사하지 않는다.
$env:DRILLFLOW_SIGNALR_JWT = "<JWT value>"
```

현재 SignalR client는 placeholder이므로 아직 이 변수를 읽거나 credential을 조회하지 않는다.
향후 client도 token 값을 옵션 객체, 사용자 JSON, Serilog property 또는 오류 메시지에
materialize하지 않고 연결 시점에만 읽어야 한다. `HubEndpoint`에도 token을 query string,
userinfo 또는 fragment로 넣을 수 없도록 검증한다.

## 설정 화면 그룹과 적용 시점

설정 화면은 다음 영역으로 나뉜다.

1. **모양**: Theme, UI language, 변경할 때마다 workflow 검증
2. **장비 파일 통신**
   - 파일 교환 위치: exchange/Live 이미지 폴더, request/response 파일명
   - 파일 lifecycle: 장비 및 앱의 request/response 삭제 정책
   - timing: timeout, polling, request publish delay, stable-read delay
   - retry: 사용 여부, 재전송 횟수, retry delay
3. **실시간 설비 영상**
   - SignalR 연결
   - 인증 참조
   - 최초 연결/reconnect 정책
   - frame 크기 및 buffer

워크플로가 실행 중이거나 Live Interaction의 동작이 진행 중이면 통신 및 실시간 영상 설정의
저장·연결 테스트·폴더 변경을 비활성화한다. `연결 테스트`는 exchange 폴더와 Live 이미지
폴더 각각에 임시 파일을 쓰고 다시 읽고 지워 접근 권한을 확인한다. 장비와 실제 request를
주고받는 테스트는 아니다.

| 설정 | 적용 시점 |
|---|---|
| Theme | 선택 즉시 열린 UI에 적용, 저장해야 다음 실행에도 유지 |
| `ValidateWorkflowOnEveryChange` | 선택 즉시 validation policy에 적용, 저장해야 유지 |
| Language | 저장 시 적용 |
| 장비 파일 통신 | 저장 후 시작하는 **다음 exchange**부터 적용 |
| 실시간 영상/SignalR | 저장되지만 재시작 필요 |
| `appsettings.json`, `DRILLFLOW_CONFIG_...` | 앱 시작 시에만 읽으므로 재시작 필요 |

실시간 영상 설정을 시작 시 값과 다르게 저장하면 설정 페이지가 재시작 필요 InfoBar를
표시한다. 현재는 실제 SignalR client가 없으므로 재시작 후에도 네트워크 연결이 생성되는
것은 아니며, 유효한 옵션이 다음 client 구현을 위해 준비되는 단계이다.

## 장비 파일 통신 설정

`appsettings.json`의 기본값을 기준으로 한다.

| 항목 | 기본값 | 단위 및 검증 | 의미 |
|---|---:|---|---|
| `ExchangeDirectory` | `C:\DrillFlow\Exchange` | 절대 local 또는 UNC 폴더 | request/response와 exchange lock 위치 |
| `LiveImageDirectory` | `C:\DrillFlow\Exchange\.drillflow-live` | 절대 local 또는 UNC 폴더 | `live` request의 correlation별 이미지 경로 기준 |
| `RequestFileName` | `request.xml` | 확장자가 있는 leaf 파일명 | request wire 파일명 |
| `ResponseFileName` | `response.xml` | 확장자가 있는 leaf 파일명 | response wire 파일명 |
| `EquipmentRequestLifecycle` | `RetainUntilOverwritten` | 정의된 enum 값 | 장비가 request를 읽은 뒤 삭제하는지 여부 |
| `ApplicationRequestLifecycle` | `DeleteAfterResponse` | 정의된 enum 값 | matching response 감지 후 앱의 request 정리 정책 |
| `ApplicationResponseLifecycle` | `DeleteAfterRead` | 정의된 enum 값 | response materialize 후 앱의 response 정리 정책 |
| `ResponseTimeout` | 30 s | `> 0`, 최대 `int.MaxValue` ms | response/lock/request 삭제 대기 한도 |
| `RetryEnabled` | `false` | boolean | timeout 후 같은 correlation/request 재전송 여부 |
| `MaximumRetryCount` | 1 | 0 이상; retry 사용 시 1 이상 | 최초 요청 이후의 **재전송 횟수** |
| `RetryDelay` | 1 s | 0 이상, 최대 `int.MaxValue` ms | 재전송 전 대기 |
| `PollingInterval` | 0.05 s | `> 0`, 최대 `int.MaxValue` ms | 파일 상태 polling 간격 |
| `RequestPublishDelay` | 0.1 s | 0 이상, 최대 `int.MaxValue` ms | 논리 action 시작부터 첫 request publish까지의 quiet interval |
| `StableReadDelay` | 0.05 s | `> 0`, 최대 `int.MaxValue` ms | response metadata가 안정적인지 재확인하는 간격 |

추가 규칙은 다음과 같다.

- `/`는 Windows wire contract에 맞춰 `\`로 정규화한다.
- `LiveImageDirectory`를 비우면 `<ExchangeDirectory>\.drillflow-live`로 해석한다.
- request와 response 파일명은 서로 달라야 한다.
- 두 파일명 모두 예약 sidecar인 `.drillflow.exchange.lock`을 사용할 수 없다.
- drive-relative 경로(`C:Exchange`), current-drive 경로(`\Exchange`), server만 있는 UNC
  경로(`\\server`)는 허용하지 않는다.
- 설정 화면의 초 값은 invariant 형식으로 읽는다. millisecond 기반 필드는 가장 가까운
  millisecond로 반올림하며 0이 허용되지 않는 값은 최소 1 ms가 되어야 한다.
- `RequestPublishDelay`는 첫 publish에만 적용한다. retry에는 `RetryDelay`만 적용한다.
- `DeleteAfterResponse`와 `DeleteAfterRead` 정리는 유효한 response를 버리지 않도록
  best-effort로 수행한다. 취소 시에는 해당 exchange가 쓴 request와 byte 단위로 일치할 때만
  삭제한다.

### 교환 단위 immutable snapshot

`FileEquipmentTransport`는 request/response 교환을 시작할 때 다음 값을 한 번 복사하여
검증한 `EquipmentCommunicationSnapshot`을 만든다.

- exchange 및 Live 이미지 경로
- request/response 파일명
- 세 lifecycle 정책
- timeout, retry, polling, publish delay, stable-read delay

이미 시작된 교환은 설정 화면에서 원본 `IOptions.Value`가 바뀌더라도 같은 snapshot을 publish,
response 대기, retry, response/request 정리 및 취소 후 detached cleanup까지 사용한다. 따라서
한 요청이 이전 폴더에 생성된 뒤 응답은 새 폴더에서 찾는 식의 혼합 적용이 발생하지 않는다.
다음 `ExchangeAsync` 호출이 최신 설정을 새 snapshot으로 캡처한다.

Live/Integration/OM처럼 앱 소유 이미지 경로를 만드는 명령도 시작할 때 이미지 디렉터리를
한 번 캡처한다. 폴더 생성과 `image_path` 작성 사이에 설정 객체가 바뀌어도 한 명령 안에서
서로 다른 두 경로가 섞이지 않는다.

## 실시간 설비 영상 설정

`DrillFlow:RealtimeVideo`의 기본값과 검증 계약은 다음과 같다.

| 항목 | 기본값 | 검증 |
|---|---:|---|
| `Enabled` | `false` | 사용 시 endpoint와 stream method도 필수 |
| `SignalR.HubEndpoint` | 빈 값 | 사용 시 absolute HTTP/HTTPS, host 필수, userinfo/query/fragment 금지 |
| `SignalR.StreamMethod` | `StreamFrames` | 사용 시 공백 불가, 최대 128자 |
| `SignalR.Transport` | `LongPolling` | `Auto`, `WebSockets`, `ServerSentEvents`, `LongPolling` 중 하나 |
| `SignalR.ServerTimeoutSeconds` | 30 | 양의 유한한 초 |
| `SignalR.KeepAliveIntervalSeconds` | 15 | 양의 유한한 초이며 server timeout보다 작음 |
| `Authentication.Mode` | `None` | `None` 또는 `Jwt` |
| `Authentication.CredentialName` | 빈 값 | JWT용 보호 credential의 논리 참조; 현재 provider 미구현 |
| `Authentication.TokenEnvironmentVariable` | `DRILLFLOW_SIGNALR_JWT` | 비어 있지 않으면 `[A-Za-z_][A-Za-z0-9_]*` |
| `Retry.Enabled` | `true` | 사용 시 reconnect delay가 하나 이상 필요 |
| `Retry.InitialConnectMaximumAttempts` | 5 | 1 이상 |
| `Retry.ReconnectDelaysSeconds` | `0, 2, 10, 30` | 0 이상의 유한한 초, 최대 20개 |
| `Frames.MaximumFrameBytes` | 8,388,608 | 1~67,108,864 bytes |
| `Frames.BufferCapacity` | 1 | 1~8 frame |

`Enabled=false`이면 빈 endpoint가 허용되지만 enum, timing, retry, frame bounds 자체는 계속
검증한다. `Enabled=true`이고 `Authentication.Mode=Jwt`이면 `CredentialName` 또는
`TokenEnvironmentVariable` 이름 중 하나가 필요하다.

### Windows 7 transport 기본값

지원 하한인 Windows 7에서도 예측 가능한 연결 방식을 사용하도록 기본 transport는
`LongPolling`이다. commissioning 후 endpoint와 운영 환경이 지원하는 경우 `Auto` 또는 다른
transport를 선택할 수 있다. 이는 현재 저장·검증되는 정책이며 아직 실제 transport를 만드는
SignalR client는 없다.

### 최초 연결 retry와 reconnect의 차이

두 값은 서로 다른 연결 단계의 정책이다.

- `InitialConnectMaximumAttempts`는 앱이 아직 한 번도 연결되지 않은 최초 시작 구간에서
  허용할 전체 연결 시도 횟수이다.
- `ReconnectDelaysSeconds`는 연결이 한 번 성립한 뒤 끊겼을 때 사용할 지연 순서이다. 기본
  `0, 2, 10, 30`은 즉시 한 번 시도한 뒤 2초, 10초, 30초를 기다리는 정책을 뜻한다.
- `Retry.Enabled=false`이면 향후 client는 자동 최초 retry/reconnect를 수행하지 않아야 한다.

현재 placeholder는 어느 retry도 실행하지 않는다. 위 설명은 옵션 이름과 validator가 정한
향후 client 계약이며, 실제 연결 서비스가 추가될 때 cancellation, sequence 소진 후 상태,
수동 재연결 동작을 함께 특성화 테스트로 고정해야 한다.

### frame buffer 의도

기본 buffer capacity 1은 오래된 frame을 쌓아 UI latency를 늘리지 않고 최신 frame 위주로
표시하기 위한 값이다. 이 역시 현재는 설정과 화면 surface만 존재하며 SignalR frame producer가
연결되어 있지 않다. 기존 Live Interaction의 CCD/OM 이미지는 계속 XML 파일 교환과
`image_path`를 사용하며 이 SignalR 설정과 독립적이다.

## 비밀값 운영 원칙

- 실제 JWT는 `appsettings.json`, `appsettings.user.json`, legacy `settings.json`에 저장하지
  않는다.
- `DRILLFLOW_CONFIG_` 환경 변수에는 비밀값을 넣지 않는다.
- JSON에는 credential 이름과 token 환경 변수 이름만 저장한다.
- Hub URL에는 token, password, query 또는 userinfo를 넣지 않는다.
- token 원문을 Serilog message template 인자, structured property, exception text 또는
  장비 통신 terminal에 기록하지 않는다.
- 현재 placeholder 단계에서는 token을 읽지 않는다. 향후 연결 구현은 필요 시점에 별도 secret
  source에서 읽고 수명 종료 시 참조를 폐기해야 한다.
