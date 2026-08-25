# DrillFlow Designer 제품·구현 가이드

> 기준일: 2026-08-26
>
> 대상: 사용자, 유지보수 개발자, 후속 구현 에이전트
>
> 관련 문서: 장비 메시지 계약은 `contract.md`, 배포 절차는 `docs/deployment.md`

## 1. 제품 목적과 확정된 범위

DrillFlow Designer는 철판 위 드릴 장비의 단위 동작과 디자이너 내부 로직을 위에서 아래 순서로 조합하고, 저장·검증·실행하는 WPF 워크플로 편집기다. 장비 동작은 지정 폴더의 request 파일로 전달하고 같은 폴더의 response 파일을 correlation `index`로 연결한다. 실행 결과는 현재 Run 동안 Action 객체의 `result`/`results`로 유지되어 이후 Action의 Expression에서 참조할 수 있다.

확정된 제품 결정은 다음과 같다.

- Windows 7 SP1 이상과 .NET Framework 4.8(`net48`)을 지원한다. 현대 .NET 8 런타임은 Windows 7을 지원하지 않으므로 사용하지 않는다.
- WPF-UI FluentWindow와 Compact Navigation 형태를 사용한다. 디자이너와 설정의 두 페이지를 제공한다.
- 중앙 편집기는 물리적인 X/Y 도면이 아니라 **실행 순서**를 정의한다.
- 장비는 현재 단계에서 모든 명령을 성공적으로 처리한다고 가정하며 별도 장비 오류 코드는 정의하지 않는다.
- `index`는 요청과 응답을 연결하는 양의 Int32 correlation ID다.
- Repeat의 내부 결과는 마지막 값만 덮지 않고 모든 iteration을 보존한다.
- 실행 이력 archive는 만들지 않고 현재 Run 결과만 보유한다. 새 Run을 시작하면 이전 결과를 비운다.
- UI는 한국어와 영어를 제공한다.

## 2. 화면 구조

### App shell

`MainWindow`는 WPF-UI `FluentWindow`다. `ExtendsContentIntoTitleBar=True`로 사용자 정의 TitleBar 영역까지 확장하되 최소화·최대화·닫기 버튼을 계속 표시한다. 왼쪽 Compact Navigation에는 디자이너와 설정 페이지가 있고, 페이지 콘텐츠는 Navigation Pane을 제외한 전 영역으로 늘어난다.

### Designer 페이지

상단 Command Bar에는 문서 새로 만들기·열기·저장·다른 이름으로 저장, Undo/Redo, 유효성 검사, Run/Continue/Step/Stop, 브레이크포인트 전환/전체 제거, Response 테스트, 장비 통신 폴더 열기가 있다. 각 명령은 의미를 나타내는 Fluent Symbol 아이콘과 툴팁을 가진다.

본문은 다음 세 영역으로 나뉜다.

1. 왼쪽: 장비 동작과 디자이너 동작을 구분한 아이템 목록
2. 중앙: 시작과 끝 사이의 세로 실행 순서 편집기
3. 오른쪽 위/아래: 선택 Action의 파라미터와 현재 Run 결과

Action 앞의 붉은 점은 브레이크포인트다. 파라미터와 결과 레이블은 Expression에서 사용하는 변수명을 먼저 보여주고, 괄호 안에 사용자 설명을 표시한다.

### Settings 페이지

설정 페이지는 다음 값을 저장하고 실행 중인 singleton Options 객체에도 반영한다.

- 로컬 또는 UNC 통신 폴더
- 확장자를 포함한 request/response 파일명
- 장비가 request를 삭제하는 방식 또는 유지·덮어쓰기 방식
- 앱이 response를 읽고 삭제하는 방식 또는 유지·덮어쓰기 방식
- response timeout, retry 사용 여부·횟수·간격, polling 간격
- 한국어/영어/시스템 언어

설정은 사용자 LocalAppData의 `settings.json`에 저장되며, 다음 파일 교환과 Response 테스트 Dialog가 최신 값을 사용한다.

## 3. Action 모델

모든 Action은 안정적인 GUID `Id`, Expression 식별자 `Key`, 표시 이름, 활성화 여부, 브레이크포인트 여부를 가진다. `Key`는 대소문자를 구분하지 않는 고유 별칭이며 저장된 워크플로에서 유지된다.

### 장비 동작

장비 동작만 request 파일을 생성하고 correlated response를 기다린다.

| Action | command | 입력 |
| --- | --- | --- |
| Move | `move` | `move_mode`, `move_x`, `move_y` |
| Measure | `measure` | `thickness` |
| Drill | `drill` | `thickness`, `drill_result_path` |
| Abort | `abort` | 없음 |

`drill_result_path`는 Drill 완료 후 장비가 결과 CSV를 기록하는 경로다. Move의 relative와 absolute 모두 음수를 허용하며, absolute는 장비 home `(0, 0)`을 기준으로 한다.

### 디자이너 동작

디자이너 동작은 장비 request/response 파일 교환을 사용하지 않는다.

- Delay: 지정한 `0..29999 ms` 동안 취소 가능한 로컬 대기
- Repeat: 내부 Action 배열을 `1..Int32.MaxValue`회 실행하고 모든 iteration 결과 보존
- If / Else if / Else: 위에서부터 첫 true 분기를 실행하고, 없으면 Else 실행
- HTTP: 외부 HTTP GET/POST를 호출하고 정형·비정형 응답을 현재 Run의 동적 결과로 제공

Repeat와 Conditional의 컨테이너 자체 결과도 correlation ID `0`의 로컬 결과로 기록한다. 로컬 제어 Action 때문에 `response.json`을 기다리는 일은 없다.

HTTP Action의 입력과 결과 shape는 다음과 같다.

| 구분 | 필드 | 의미 |
| --- | --- | --- |
| 입력 | `method` | `GET` 또는 `POST` |
| 입력 | `url` | 절대 `http`/`https` URL |
| 입력 | `headers` | JSON 객체 literal 또는 객체를 반환하는 Expression |
| 입력 | `body` | 문자열 그대로 전송하거나 객체/배열 Expression을 JSON으로 직렬화 |
| 입력 | `timeout_ms` | `1..300000 ms` |
| 결과 | `status_code`, `is_success`, `reason_phrase` | HTTP 상태 정보 |
| 결과 | `headers`, `content_type` | 응답 헤더 객체와 Content-Type |
| 결과 | `body_text` | 파싱 여부와 무관하게 보존한 원문 |
| 결과 | `json` | JSON이면 중첩 dictionary/array/primitive, 아니면 `null` |

예를 들어 JSON 응답이 `{ "machine": { "ready": true } }`이면 다음 Action에서 `=http_1.result.json.machine.ready`로 접근한다. JSON 배열은 `=http_1.result.json[0].id`, 하이픈 같은 특수 문자가 있는 키는 `=http_1.result.json['trace-id']`처럼 접근한다. 4xx/5xx도 HTTP 응답 결과로 기록하며, 네트워크 오류와 timeout은 워크플로를 `Faulted`로 만든다. 첫 Stop은 진행 중인 HTTP 요청을 로컬 취소하고, 장비 request나 abort 파일은 생성하지 않는다.

## 4. 파라미터·검증·Expression

파라미터 텍스트는 사용자가 입력한 과학 표기법을 그대로 저장한다. 예를 들어 `1E-3`, `2.56E-4`, `0.0256E-6`는 같은 수치로 평가된다.

핵심 범위는 다음과 같다.

- `move_x`, `move_y`: `-0.5 m < value < 0.5 m`
- `thickness`: `0 < value <= 2.4E-3 m`
- Repeat `count`: `1..Int32.MaxValue`
- Delay: `0..29999 ms`
- 수치는 NaN이나 Infinity가 아닌 유한값

`=`로 시작하는 값은 임의 C#이 아닌 sandboxed Expression으로 평가한다.

```text
=measure_1.result.measured_distance
=move_1.parameters.move_x + 2.5E-4
=repeat_1.results[0].count
```

Action 객체의 접근 형태는 다음과 같다.

```text
action_key.parameters.field
action_key.result.field
action_key.results[index].field
action_key.results.last.field
action_key.last.field
```

미래 Action, 실행될 수 없는 분기, 순환 참조는 저장/실행 전 검증에서 거부한다. Expression TextBox에서 `Ctrl+Space`를 누르면 현재 token과 caret 위치를 분석해 접근 가능한 이전 Action 및 `parameters`/`result` 멤버를 ComboBox 팝업으로 보여준다. Enter/Tab은 후보를 입력하고 Esc는 닫는다. 런타임에서 발견한 동적 response/HTTP 필드도 이후 자동완성 후보에 합쳐진다.

## 5. 실행 엔진

Runner 상태는 `Idle → Validating → Running`으로 진행하며 실행 중 `Paused` 또는 `Stopping`, 종료 시 `Completed`·`Stopped`·`Faulted`가 된다.

- Run: 문서 전체를 deep snapshot한 뒤 검증하고 현재 Run 결과를 초기화해 실행한다.
- 이 Action만 실행: 선택 subtree만 동일 ID의 snapshot으로 실행하며 해당 subtree의 authored breakpoint는 무시한다.
- Breakpoint: Action 실행 직전에 `Paused`가 된다.
- Continue: 다음 breakpoint까지 계속한다.
- Step: 한 실행 단위를 완료한 뒤 다음 Action 앞에서 다시 멈춘다.
- Stop 첫 클릭: 다음 Action 시작을 막는다. 이미 전송한 장비 요청은 response까지 기다려 기록한 뒤 멈춘다.
- Stop 두 번째 클릭: 현재 파일 응답 대기를 즉시 취소하고 로컬 실행을 끝낸다. 어떤 경우에도 toolbar Stop은 `abort` 명령을 만들지 않는다.
- Abort Action: 명시적인 장비 `abort` request/response를 수행한 뒤 배열을 종료한다.

Breakpoint, Stop, 활성화 여부와 실행 상태는 runner 이벤트로 카드 ViewModel에 반영된다. UI는 실행 중 편집을 잠그므로 검증 이후 물리 명령이 바뀌지 않는다.

## 6. 파일 통신과 Response 테스트

request와 response는 같은 통신 폴더의 서로 다른 설정 파일명을 사용한다. JSON의 `index`가 현재 요청과 같고 `command`가 `return`인 response만 받아들인다. 기존 stale response와 임의의 추가 top-level 필드는 버리지 않는다.

파일 게시에는 같은 디렉터리의 temp 파일과 atomic replace/move를 사용한다. `.drillflow.exchange.lock`을 `FileShare.None`으로 열어 로컬 프로세스와 SMB 클라이언트의 전체 exchange를 직렬화한다. polling은 파일 크기와 수정 시간이 안정된 뒤 읽고 일시적인 share violation을 재시도한다.

timeout retry는 기본으로 꺼져 있다. 켜면 같은 payload와 `index`를 다시 게시하므로 장비가 `index`를 내구성 있는 idempotency key로 처리하지 않는 한 물리 동작은 at-least-once다.

Response 테스트는 선택한 **장비 Action**에만 제공된다. WPF-UI ContentDialog를 열 때마다 최신 설정의 `ExchangeDirectory + ResponseFileName`을 기본 경로로 표시하고, 감지한 request의 index와 Action별 기본 필드를 편집 가능한 JSON으로 제안한다. 게시 시 스키마를 검증하고 원자적으로 response를 생성한다. 읽기 전용 경로·결과 TextBox는 명시적인 OneWay binding을 사용해 WPF가 getter-only 속성에 값을 되쓰지 않는다.

세부 장비 계약과 JSON/XML 포맷 교체 지점은 루트 `contract.md`가 source of truth다. HTTP Action의 응답은 장비 `return` 스키마를 따르지 않으며 이 계약의 범위 밖이다.

## 7. 편집 이벤트와 사용자 피드백

| 사용자 이벤트 | 앱 반응 |
| --- | --- |
| 아이템 더블클릭 | 루트 실행 순서 끝에 Action 추가 |
| 아이템을 삽입 bar/빈 Canvas에 drag | 해당 컬렉션·index에 새 Action 생성 |
| Action 카드 헤더 drag | 같은 레벨 또는 허용된 중첩 컬렉션으로 이동 |
| Action drag 중 Ctrl | subtree를 새 GUID와 고유 별칭으로 deep copy |
| 삽입 bar MouseOver | 놓기 가능한 위치를 accent horizontal bar로 표시 |
| 삽입 bar MouseDown | 붙여넣기 target을 저장하고 짧게 pulse한 뒤 bar를 숨김 |
| Ctrl+V | 마지막으로 클릭한 target이 유효하면 그 위치, 아니면 선택 Action 다음 위치에 붙여넣기 |
| Action 클릭/우클릭 | 선택 변경 후 오른쪽 Inspector와 Context Menu command 갱신 |
| Context Menu | 이 Action만 실행 / Response 테스트, 복사·잘라내기·붙여넣기·삭제, 활성화, 브레이크포인트를 separator로 분류 |
| Ctrl+C/Ctrl+X/Delete/F9 | 선택 Action 복사/잘라내기/삭제/브레이크포인트 전환 |
| 파라미터 편집 시작 | Undo snapshot 생성, 입력마다 유효성/Expression 표시 갱신 |
| Ctrl+Space | 현재 범위의 Expression completion popup 표시 |
| 통신 폴더 버튼 | 최신 설정 경로를 Windows Explorer로 열고 상태 표시 |
| Window 닫기 | 실행 중이면 Stop을 요청하고, 미저장 변경은 저장/폐기/취소 확인 |

구조 변경은 실행 전 serialized snapshot을 Undo stack에 넣고 Redo stack을 비운다. 붙여넣기·Ctrl-drag·재정렬·중첩 이동도 같은 경로를 사용해 저장 모델과 화면 컬렉션을 함께 갱신한다. 자기 자신의 하위 Repeat/Conditional 컬렉션으로 이동하는 순환 구조는 허용하지 않는다.

## 8. 저장과 현재 Run 결과

워크플로는 schema version을 가진 `*.drillflow.json`으로 저장한다. Node type, GUID, Key, 파라미터의 literal/Expression 원문, 중첩 body/branch, enabled와 breakpoint를 보존한다. 런타임 result, request/response payload, 실행 중 위치는 저장하지 않는다.

각 실행 결과에는 Action ID/Key, correlation ID, Repeat iteration path, 완료 시각, 동적 값 dictionary가 있다. Repeat 내부 Action은 iteration마다 별도 결과를 추가한다. 새 전체 Run이나 단일 Action Run을 시작하면 이전 현재 Run 결과를 비우며, crash 이후 불확실한 물리 동작을 자동 재개하지 않는다.

## 9. 애플리케이션 아키텍처

### 부팅과 공통 서비스

`App.xaml.cs`는 Generic Host를 구성하고 Microsoft.Extensions Configuration, DI, Options를 연결한다. 창을 직접 `new`하지 않고 Host service provider에서 singleton Window/Page/ViewModel을 가져온다. Serilog bootstrap logger는 Host 완성 전 오류를 Debug와 rolling file에 기록하고, Host 이후 정식 logger로 교체된다.

HTTP 실행 로그는 method, query를 제거한 URL path, timeout, status만 남긴다. 인증 헤더·request body·response body와 URL query는 rolling log에 기록하지 않는다.

### 계층

| 프로젝트 | 책임 |
| --- | --- |
| `DrillFlow.Core` | Workflow 모델, 안전한 Expression 값/파서, validation, 현재 Run result store |
| `DrillFlow.Application` | Runner orchestration, 장비/HTTP/저장소 abstraction, 실행 이벤트 계약 |
| `DrillFlow.Infrastructure` | JSON 저장, 파일 transport, atomic publisher, correlation ID, HTTP client 구현 |
| `DrillFlow.Desktop` | WPF-UI View, ViewModel, drag/drop·키보드 behavior, Dialog와 Windows shell 연동 |
| `DrillFlow.Tests` | Core/Application/Infrastructure 회귀·통합 테스트 |

의존성 방향은 Desktop과 Infrastructure가 Application/Core을 향하며, Core는 UI·파일·HTTP를 모른다. Runner는 interface만 받아 테스트에서 deterministic fake transport/client로 교체할 수 있다.

### 주요 이벤트 흐름

```text
WPF routed event / ICommand
  → MainPageViewModel 또는 code-behind의 UI 좌표 해석
  → WorkflowDocument/ObservableCollection 변경
  → validation 또는 WorkflowExecutionFacade
  → WorkflowRunner
      ├─ 장비 Action → IEquipmentFileTransport → request/response 폴더
      ├─ HTTP Action → HTTP abstraction → 원격 endpoint
      └─ Control Flow → 로컬 delay/loop/branch
  → RunResultStore + NodeStateChanged/RunStateChanged
  → WorkflowActionViewModel/Inspector 갱신
```

code-behind는 hit testing, mouse 좌표, drag payload, animation처럼 WPF visual tree에 종속된 일만 담당한다. 저장/복사/이동/실행 판단은 ViewModel/Core 서비스로 보낸다.

## 10. 변경 요청 반영 이력

초기 구현에서 Fluent shell, Compact Navigation, 3-pane designer, 장비 Action 네 종류, Delay/Repeat/Conditional, Generic Host/DI/Options/Serilog, 파일 lifecycle·timeout/retry, 저장/불러오기, 현재 Run result와 Expression 참조를 구성했다.

후속 수정에서 다음을 반영했다.

- TitleBar의 최소화·최대화·닫기 버튼과 설정 페이지 stretch
- 첫 Stop은 정상 정지, 두 번째 Stop은 강제 로컬 취소
- 분류된 Action Context Menu와 단일 Action 실행
- Ctrl+C/X/V, Ctrl-drag deep copy, 클릭 가능한 삽입 bar, 빈 Canvas 전체 drop
- 카드 앞쪽 breakpoint 표시
- Ctrl+Space Expression 자동완성
- 변수명 우선 파라미터/결과 표기
- WPF-UI ContentDialog 기반 response 파일 simulator
- 장비 I/O 구조와 포맷 변경 지점을 정리한 `contract.md`

이번 수정에서는 다음을 반영했다.

- 삽입 위치 선택 표시를 pulse 후 자동 숨기고 논리 target만 유지
- Action 카드 헤더 drag의 capture/release와 Canvas 순서 재정렬
- getter-only `ResponsePath`, result field, `ValuesJson`의 OneWay binding 오류 수정
- Response Dialog 기본 경로가 항상 최신 설정 경로/파일명을 사용하도록 회귀 테스트
- Command Bar에서 장비 통신 폴더를 Explorer로 여는 기능
- Control Flow가 장비 response를 기다리지 않는 실행 경계 명문화
- 장비 Action과 분리된 HTTP GET/POST Designer Action 및 동적 결과의 Expression 연결
- 본 제품·이벤트·아키텍처 문서 추가

## 11. 확장 시 체크리스트

장비 request/response 구조를 바꿀 때는 먼저 `contract.md`를 갱신하고 message model, runner mapping, codec, simulator, Inspector field catalog, 테스트를 함께 변경한다. JSON에서 XML로 바꿀 때 파일 확장자만 바꾸면 안 되며 codec abstraction과 설정 format을 명시적으로 추가한다.

새 Action을 추가할 때는 다음을 함께 확인한다.

1. `WorkflowNodeKind`와 concrete node/기본 파라미터
2. deep copy와 serializer type discriminator
3. Core validation과 Expression visibility
4. Runner의 실행 분기 및 결과 shape
5. DI abstraction/implementation
6. Toolbox/아이콘/한·영 리소스/Inspector
7. completion의 parameter/result member catalog
8. 저장 round-trip, runner, cancellation, dynamic result 테스트

배포 전에는 Release 빌드와 전체 테스트뿐 아니라 Windows 7 SP1 x86/x64 VM에서 Fluent fallback, DPI, 한·영 리소스, 실제 SMB lifecycle, drag/drop, breakpoint/Step/Stop, HTTP TLS 호환성을 확인한다.
