# DrillFlow 코드베이스 지도

이 문서는 현재 소스 트리의 역할, 허용된 의존 방향, 장비 파일 통신의 불변 순서, 설정 적용 시점과 확장 지점을 한곳에 정리한 유지보수용 지도다. 장비 XML의 필드 정의 자체는 [`contract.md`](../contract.md), 제품 동작은 [`product-and-implementation.md`](product-and-implementation.md), 인코딩과 파일 교환의 운영 규칙은 [`xml-encoding-and-file-handshake.md`](xml-encoding-and-file-handshake.md)를 기준으로 한다.

솔루션은 C# 13을 언어 기준으로 사용하지만, 실행 호환성은 `net48`/Windows 7 SP1과 `netstandard2.0` API 범위를 지킨다. 최신 문법을 쓰는 것과 최신 런타임 API를 호출하는 것은 별개의 결정이다.

## 0. 기술 기준과 의도적으로 추가하지 않은 의존성

- 컴파일 언어 기준은 `C# 13`으로 고정했다. `latest`처럼 개발 PC의 SDK에 따라 의미가
  달라지는 값을 쓰지 않는다.
- 실행 기준은 기존 제품 계약인 WPF `net48`/Windows 7 SP1이다. Generic Host, DI,
  Options와 logging의 설계 방식은 최신 Microsoft.Extensions 관례를 따르되, 이번 구조
  정리에서는 검증된 Microsoft.Extensions 8 package major를 10으로 함께 올리지 않았다.
  package major upgrade와 통신 핵심 리팩터링을 한 변경에 섞으면 assembly binding 및 구형
  OS 회귀 원인을 분리하기 어려우므로, 업그레이드는 Windows 7 실기동·publish·binding
  redirect 검증을 포함한 별도 변경으로 진행한다.
- Serilog는 bootstrap logger와 Host logger를 분리하고 둘 다 Debug/File sink를 사용한다.
  lifecycle 코드에는 logger 조립을 두지 않는다.
- 현재 영속 데이터는 사람이 편집하는 Workflow/설정 파일과 현재 Run의 메모리 상태다.
  관계형 query, transaction 또는 migration 요구가 없으므로 EF Core를 단지 기술 선호만으로
  추가하지 않았다. Run archive나 장비 이력 DB가 제품 요구가 되면 Application port와 별도
  Infrastructure persistence 구현으로 도입한다.
- 실제 SignalR client도 아직 없으므로 Hub package와 인증 provider를 선행 추가하지 않았다.
  대신 Options/validator/UI/비밀값 경계를 먼저 고정했고, 구체 client는 그 계약을 소비하는
  독립 기능 slice로 추가한다.

## 1. 프로젝트와 의존 방향

```text
DrillFlow.Core (netstandard2.0, 외부 프로젝트 의존 없음)
        ↑
DrillFlow.Application (netstandard2.0, Core의 사용 사례와 포트)
        ↑
DrillFlow.Infrastructure (netstandard2.0, Application 포트 구현)
        ↑
DrillFlow.Desktop (net48, WPF UI와 유일한 composition root)

DrillFlow.Tests (net48, 위 네 프로젝트를 모두 참조하는 검증 전용 프로젝트)
```

허용되는 방향은 `Desktop → Infrastructure → Application → Core`다. `Desktop`은 화면 조립을 위해 네 계층을 직접 참조할 수 있지만, 그 참조가 안쪽 계층에서 다시 UI로 돌아가면 안 된다.

금지 규칙은 다음과 같다.

- `Core`는 WPF, 파일 시스템, 네트워크, 로깅, DI, Options, JSON/XML 구현을 참조하지 않는다.
- `Application`은 WPF와 구체 파일/XML/HTTP 구현을 참조하지 않는다. 외부 효과는 `IEquipmentFileTransport`, `IEquipmentMessageCodec`, `IHttpActionExecutor`, `IWorkflowDocumentSerializer` 같은 포트로 표현한다.
- `Infrastructure`는 `Desktop`의 ViewModel, 다이얼로그, 리소스 또는 Dispatcher를 참조하지 않는다.
- `Desktop`의 View/XAML은 `Infrastructure`의 구체 타입을 직접 생성하지 않는다. 객체 생성과 수명은 `Bootstrap/DesktopServiceCollectionExtensions.cs`에 모은다.
- 프로젝트 간 양방향 참조, 서비스 로케이터, 정적 전역 컨테이너를 추가하지 않는다.
- 장비 계약 타입을 WPF 편의를 위해 바꾸지 않는다. UI 전용 상태는 Desktop ViewModel에 두고 wire/domain 값과 변환한다.
- 장비 파일 교환 코드에서 `Task.Run`, 취소, 파일 삭제 또는 잠금 소유권을 임의로 재배치하지 않는다. 이 부분은 아래 핸드셰이크 순서를 함께 검토해야 한다.

## 2. 실제 파일 트리와 책임

### 루트 빌드 자산

```text
DrillFlow.Designer.sln                         — 네 제품 프로젝트와 단일 테스트 프로젝트를 묶는 솔루션이다.
Directory.Build.props                          — C# 13, nullable, deterministic build 등 공통 컴파일 정책을 정의한다.
Directory.Packages.props                       — 중앙 패키지 버전의 단일 출처다.
NuGet.Config                                   — 복원 시 사용할 패키지 소스를 고정한다.
contract.md                                    — request/response 필드와 9개 장비 Action의 현재 논리 계약이다.
README.md                                      — 플랫폼, 빌드, 핵심 사용자 동작의 진입 문서다.
docs/architecture.md                           — 합의된 상위 구조와 안전 경계를 설명한다.
docs/deployment.md                             — Windows 7 배포와 릴리스 점검 절차다.
docs/product-and-implementation.md             — 요구사항부터 UI 이벤트 처리까지의 제품 구현 설명서다.
docs/xml-encoding-and-file-handshake.md         — BOM/UTF-8 처리와 파일 게시·수신 안전 규칙이다.
samples/basic-drilling.drillflow.json          — import 가능한 예제 Workflow다.
```

### `src/DrillFlow.Core`

프레임워크와 장비 I/O를 모르는 순수 모델·계산 계층이다.

```text
DrillFlow.Core.csproj                          — 외부 프로젝트를 참조하지 않는 netstandard2.0 도메인 프로젝트다.
Expressions/
  ExpressionCompletionProvider.cs             — 현재 노드에서 접근 가능한 Action/parameters/result 자동완성 후보를 만든다.
  ExpressionContext.cs                        — 표현식 평가에 노출할 Action 별칭, 파라미터, 반복 결과를 보관한다.
  ExpressionEngine.cs                         — `=` 표현식을 제한된 문법으로 파싱하고 계산하며 임의 C# 실행을 허용하지 않는다.
  ExpressionExceptions.cs                     — 토큰화·구문·평가 오류를 호출자에게 구분해 전달한다.
  ExpressionValue.cs                          — 스칼라, 객체, 배열 값과 멤버/인덱스 접근을 통일한다.
Runtime/
  ActionExecutionResult.cs                    — 한 Action 실행의 correlation ID, iteration 경로와 결과 값을 나타낸다.
  RunResultStore.cs                           — 현재 Run의 Action/반복별 결과를 메모리에 기록하고 표현식 조회를 제공한다.
Validation/
  ParameterValueValidator.cs                  — 장비 Action과 디자이너 Action의 리터럴 값 범위·형식 규칙을 검사한다.
  ValidationIssue.cs                          — 노드/필드/메시지 단위 유효성 문제와 전체 결과 모델이다.
  WorkflowValidator.cs                        — 별칭, 구조, 조건 분기, 파라미터와 표현식 참조를 전체 Workflow 또는 선택 Action 실행 범위에서 검사한다.
Workflows/
  WorkflowNode.cs                             — 공통 ID, 별칭, 활성화, breakpoint와 `WorkflowNodeKind`를 정의하는 기반 모델이다.
  ParameterBinding.cs                         — 원문 리터럴 또는 `=` 표현식을 손실 없이 보존한다.
  WorkflowDocument.cs                         — 저장·실행 단위인 Workflow와 루트 Action 배열을 나타낸다.
  EquipmentNodes.cs                           — Stage/Camera/Focus/Integration/Live/OM/Lens/ACB/Abort 노드와 파라미터를 정의한다.
  ControlFlowNodes.cs                         — Delay, Repeat, If/Else If/Else와 중첩 자식 구조를 정의한다.
  DesignerActionNodes.cs                      — 장비 파일을 사용하지 않는 HTTP Action 모델을 정의한다.
  WorkflowNodeCopy.cs                         — 중첩 구조를 포함한 노드 deep copy와 새 ID/별칭 참조 재매핑을 수행한다.
```

### `src/DrillFlow.Application`

Workflow와 라이브 상호작용의 사용 사례를 실행하고, 파일·HTTP·영속화 구현이 따라야 할 인터페이스를 정의한다.

```text
DrillFlow.Application.csproj                   — Core만 프로젝트 참조하는 netstandard2.0 사용 사례 프로젝트다.
ApplicationServiceCollectionExtensions.cs      — Runner, 결과 저장소, Live session 등 Application 서비스 등록 진입점이다.
Communication/
  EquipmentCommunicationOptions.cs            — 교환 폴더/파일명, 생명주기, timeout, polling, retry, 안정화 지연의 런타임 옵션이다.
  EquipmentCommunicationSnapshot.cs           — 한 번의 전체 exchange가 사용할 옵션을 불변 복사해 중간 설정 변경을 격리한다.
  EquipmentRequestMessage.cs                   — correlation ID, action, 파라미터와 `EquipmentActionNames`를 가진 논리 request다.
  EquipmentResponseMessage.cs                  — result와 action별 결과 값을 가진 논리 response이며 JSON은 메모리 보조 표현만 제공한다.
  EquipmentResponseSimulationModels.cs         — 테스트 response 초안, request snapshot과 시뮬레이션 검증 결과를 정의한다.
  ApplicationRequestFileLifecycle.cs           — 앱이 완료 request를 삭제할지 유지할지 정의한다.
  ApplicationResponseFileLifecycle.cs          — 앱이 읽은 response를 삭제할지 유지할지 정의한다.
  EquipmentRequestFileLifecycle.cs             — 장비가 request를 삭제하는지 덮어쓰는지 나타낸다.
  EquipmentResponseTimeoutException.cs         — 대기와 허용된 retry를 모두 소진한 exchange 실패다.
  ICorrelationIdProvider.cs                    — 양의 correlation ID 발급 포트다.
  IEquipmentFileTransport.cs                   — 논리 request 하나와 matching response 하나를 교환하는 포트다.
  IEquipmentMessageCodec.cs                    — 논리 메시지와 wire byte payload 사이 직렬화/파싱 포트 및 4 MiB 상한이다.
  IEquipmentResponseSimulator.cs               — 테스트 response 생성·검증 포트다.
  IEquipmentExchangeTraceSink.cs               — publish/match/stop 이벤트를 UI 터미널로 전달하는 관찰 포트다.
Execution/
  IWorkflowRunner.cs                           — 전체/선택 실행, Continue/Step/Stop과 상태 이벤트의 공개 사용 사례다.
  WorkflowRunner.cs                            — 선행 검증, 표현식 평가, 제어 흐름, 장비/HTTP 실행, breakpoint와 결과 기록을 조정한다.
  WorkflowExecutionEventArgs.cs                — Run 및 노드 상태 변경 이벤트 payload다.
  WorkflowExecutionException.cs                — 사용자에게 노출할 실행 오류와 장비 `result: 1` fault를 표현한다.
  WorkflowRunState.cs                          — Run/노드 상태와 디버그 재개 모드를 정의한다.
Http/
  IHttpActionExecutor.cs                       — HTTP request/response 모델과 실행 포트다.
LiveInteraction/
  ILiveInteractionSession.cs                   — Live frame과 Stage/Camera/Focus/Integration/Lens/ACB 명령의 직렬 세션 API다.
  LiveInteractionSession.cs                    — correlation 발급, exclusive gate, 논리 request 생성과 response fault 판정을 조정한다.
  LiveInteractionProtocol.cs                   — HFW, frame count, 경로 등 Live 통신 상수를 모은다.
  LiveImageExchangeResult.cs                   — image response와 앱이 요청한 원래 경로를 함께 반환한다.
  LiveImageCoordinateMapper.cs                 — DPI/Uniform letterbox를 고려해 화면 좌표를 원본 pixel 좌표로 매핑한다.
  LiveEquipmentActionFailedException.cs        — Live 화면의 비-Live 명령에서 `result: 1`을 명확히 전달한다.
Persistence/
  IWorkflowDocumentSerializer.cs               — Workflow 저장/로드 포트다.
  CorrelationIdStoreOptions.cs                 — 재사용 방지를 위한 ID 저장 파일과 예약 block 크기 옵션이다.
RealtimeVideo/
  RealtimeVideoOptions.cs                      — 실시간 영상 enable flag와 하위 SignalR/auth/retry/frame 설정을 묶는 section root다.
  RealtimeVideoSignalROptions.cs               — Hub endpoint/method, Win7 호환 transport, server timeout과 keep-alive 설정이다.
  RealtimeVideoAuthenticationOptions.cs        — None/JWT mode와 credential·token 환경 변수 참조 설정이다.
  RealtimeVideoRetryOptions.cs                 — 재연결 사용 여부, 시도 횟수와 지수 지연 범위 설정이다.
  RealtimeVideoFrameOptions.cs                 — 수신 frame byte 상한과 bounded buffer capacity 설정이다.
  RealtimeVideoOptionsValidator.cs             — endpoint, 시간, retry, buffer와 인증 설정의 교차 필드 검증기다.
  RealtimeVideoServiceCollectionExtensions.cs  — typed options와 validator 타입만 등록하며 host별 바인딩 정책은 요구하지 않는다.
```

### `src/DrillFlow.Infrastructure`

Application 포트를 파일 시스템, XML 템플릿, HTTP와 JSON 저장소로 구현한다. 장비 handshake의 핵심 소유 계층이다.

```text
DrillFlow.Infrastructure.csproj                — Core/Application을 참조하고 XML 템플릿 25개를 embedded resource로 묶는다.
InfrastructureServiceCollectionExtensions.cs   — codec, transport, 안정 파일 reader, serializer, simulator와 HTTP 구현을 등록한다.
Communication/
  EquipmentCommunicationOptionsValidator.cs   — 경로, leaf 파일명, lifecycle, timeout/retry/delay 조합을 원자적으로 검증한다.
  FileEquipmentTransport.cs                    — 프로세스/디렉터리 잠금, atomic publish, stable poll, matching, cleanup/retry/cancel을 소유한다.
  XmlTemplateEquipmentMessageCodec.cs          — 정확한 `{{{field}}}` 치환/추출, UTF-8 BOM 정규화, 공백 무관 구조 matching과 값 검증을 수행한다.
  JsonEquipmentResponseSimulator.cs            — 사용자가 입력한 논리 값을 codec으로 XML response에 렌더링해 atomic publish한다.
  EquipmentExchangeLockTimeoutException.cs     — `.drillflow.exchange.lock` 획득 실패를 진단 가능한 timeout으로 전달한다.
  EquipmentRequestDeletionTimeoutException.cs  — delete-after-read 장비의 request 소유권 회수 대기 단계와 timeout을 전달한다.
  FileExchange/
    EquipmentFilePresence.cs                   — 경로 상태를 Absent/Present/Unknown으로 구분해 일시적 I/O 실패를 부재로 오인하지 않게 한다.
    EquipmentFileSnapshot.cs                   — 파일 길이와 마지막 수정 시각을 안정성 비교용으로 묶는다.
    IStableEquipmentFileReader.cs              — 쓰기 중 파일을 거부하고 immutable byte snapshot을 얻는 내부 seam이다.
    StableEquipmentFileReader.cs               — 전/후 metadata, 안정화 지연, writer 공유 잠금, 정확한 길이 read로 local/UNC 파일을 확인한다.
  Templates/{Action}/                          — 장비별 XML 정답지 디렉터리이며 일반 XML 변환기가 아니라 placeholder 기반 고정 계약이다.
    Abort/{request.xml,response.xml}            — Abort request와 공통 결과 response 템플릿이다.
    Acb/{request.xml,response.xml}              — ACB request와 공통 결과 response 템플릿이다.
    Camera/{request.xml,response.xml,failure-response.xml} — Camera 성공 필드와 최소 fault envelope 템플릿이다.
    Focus/{request.xml,response.xml,failure-response.xml}  — Focus matrix 성공 필드와 최소 fault envelope 템플릿이다.
    Integration/{request.xml,response.xml,failure-response.xml} — Integration image 성공 필드와 fault 템플릿이다.
    Lens/{request.xml,response.xml,failure-response.xml}   — Lens mode 성공 필드와 fault 템플릿이다.
    Live/{request.xml,response.xml,failure-response.xml}   — 단일 frame 성공 필드와 fault 템플릿이다.
    Om/{request.xml,response.xml,failure-response.xml}     — OM image 성공 필드와 fault 템플릿이다.
    Stage/{request.xml,response.xml,failure-response.xml}  — Stage 위치 성공 필드와 fault 템플릿이다.
Http/
  HttpActionExecutor.cs                        — 제한된 timeout, header/body 처리와 동적 JSON 결과를 갖는 HTTP 포트 구현이다.
IO/
  AtomicFilePublisher.cs                       — 같은 디렉터리의 완료된 temp 파일을 replace/move해 부분 파일 노출을 막는다.
Persistence/
  JsonWorkflowDocumentSerializer.cs            — polymorphic Workflow JSON 저장/로드와 구버전 형식 호환을 담당한다.
  PersistentCorrelationIdProvider.cs           — LocalAppData high-water block 예약으로 재시작 후에도 ID 재사용을 방지한다.
  CorrelationIdStoreOptionsValidator.cs        — correlation 저장 경로와 block 옵션을 시작 시 검증한다.
Properties/AssemblyInfo.cs                     — 테스트가 필요한 제한된 internal 구현을 friend assembly에 노출한다.
```

### `src/DrillFlow.Desktop`

유일한 실행 파일, WPF-UI 화면과 Generic Host composition root다. 장비 통신 규칙은 직접 구현하지 않고 Application 사용 사례를 호출한다.

```text
DrillFlow.Desktop.csproj                       — net48 WPF 실행 파일, 패키지/프로젝트 참조와 배포 문서 복사를 정의한다.
App.xaml / App.xaml.cs                         — WPF resource 시작점, Host lifecycle과 최상위 예외 경계를 관리하고 logging/DI 구성은 Bootstrap에 위임한다.
appsettings.json                               — 배포 기본값인 DrillFlow/EquipmentCommunication/CorrelationIdStore 설정을 노출한다.
app.manifest                                   — Windows 7+, DPI awareness와 실행 권한 호환성을 선언한다.
Assets/DrillFlow.ico, DrillFlow.png            — 작은 크기에서도 식별되는 굵은 B3 통신·제어 아이콘의 실행 파일/TitleBar 자산이다.
Bootstrap/
  DesktopApplicationPaths.cs                  — LocalAppData의 사용자 설정, legacy 설정, correlation, log 경로를 한곳에서 계산한다.
  DesktopHostFactory.cs                       — Generic Host와 `appsettings.json`, `DRILLFLOW_CONFIG_` 구성 환경 변수, Serilog를 조립한다.
  DesktopLogging.cs                           — 시작 전 bootstrap logger와 Host의 Debug/rolling File sink를 만든다.
  DesktopServiceCollectionExtensions.cs       — RealtimeVideo section bind/ValidateOnStart를 포함해 각 계층 서비스와 WPF View/ViewModel 수명을 조립한다.
  StartupSettingsLoader.cs                    — user/legacy JSON을 읽고 유효한 그룹만 시작 옵션에 병합한다.
Behaviors/
  ExpressionCompletionBehavior.cs             — TextBox의 Ctrl+Space/버튼 완성 목록, caret 삽입과 최초 focus 선택 동작을 연결한다.
  ImmediateToolTipPolicy.cs                    — ToolTip이 있는 모든 WPF 요소에 즉시 표시·긴 유지 시간을 전역 적용한다.
Controls/
  EquipmentActivityStatus.xaml / EquipmentActivityStatus.xaml.cs — InfoBar 우측의 대기 건수와 실시간 연결 상태 표시 및 code-behind다.
  EquipmentCommunicationPanel.xaml / EquipmentCommunicationPanel.xaml.cs — request/response 구조화 terminal과 클릭 가능한 파일 경로 패널이다.
  WorkflowValidationPanel.xaml / WorkflowValidationPanel.xaml.cs — Workflow 유효성 문제 목록 패널이다.
  FocusScatterChart.cs                        — Z/Sharpness 점을 데이터 범위에 맞춰 그리는 경량 WPF scatter chart다.
Converters/
  NullToVisibilityConverter.cs                — null 여부를 Visibility로 바꾼다.
  StringToVisibilityConverter.cs              — 빈 문자열 여부를 Visibility로 바꾼다.
Features/LiveInteraction/Support/
  FocusSamplePoint.cs                         — Focus chart가 표시할 Z/Sharpness 한 점이다.
  LiveImageTarget.cs                          — image 위 선택 위치와 화면 marker 상태다.
  LiveCaptureLoadResult.cs                    — capture 파일 load 성공/실패 결과를 ViewModel에 전달한다.
  LiveImageFileLoader.cs                      — 장비가 저장한 image 파일을 UI와 분리된 helper로 안전하게 load한다.
  LiveImageIoTimeout.cs                       — image file I/O의 짧고 취소 가능한 timeout 경계를 제공한다.
  LiveInteractionCancellation.cs              — active frame/non-Live 작업 취소와 제한 시간 대기를 공통화한다.
  LiveInteractionShutdownDrain.cs             — 페이지/앱 종료 시 남은 비동기 작업을 제한 시간 안에 회수한다.
Models/
  DesignerOptions.cs                          — `DrillFlow` 설정 section의 언어/테마/검증과 RealtimeVideo 기본값을 나타낸다.
  ThemeSelection.cs                           — System/Light/Dark 값의 상수와 정규화 규칙을 제공한다.
  CommunicationSettings.cs                    — 모든 사용자 timing을 `double Seconds`로 저장하고 runtime `TimeSpan`으로 바꾸며, 구버전 milliseconds 키는 읽기 alias로만 받는다.
  UserPreferences.cs                          — 사용자 JSON에 영속화되는 appearance, validation, communication, realtime 설정의 루트다.
Resources/
  Strings.ko-KR.xaml, Strings.en-US.xaml       — 한국어/영어 UI 문자열 사전이다.
  Styles.xaml                                 — Light/Dark에 반응하는 WPF-UI 보조 스타일과 공통 spacing을 정의한다.
Services/
  IApplicationThemeService.cs / ApplicationThemeService.cs — System/Light/Dark를 즉시 적용하고 OS 테마 변화를 반영한다.
  ILocalizationService.cs / LocalizationService.cs         — 언어 resource dictionary를 런타임 교체한다.
  IUserSettingsStore.cs / UserSettingsStore.cs              — user JSON load, legacy migration과 atomic save를 담당한다.
  IWorkflowDocumentService.cs / WorkflowDocumentService.cs  — serializer를 UI save/open 명령에 맞게 감싼다.
  IWorkflowExecutionFacade.cs / WorkflowExecutionFacade.cs  — Runner 명령과 이벤트를 MainPage가 쓰는 좁은 API로 노출한다.
  IWorkflowValidationPolicy.cs / WorkflowValidationPolicy.cs — 변경 시 자동 검증 flag를 화면과 분리한다.
  IExpressionCompletionSource.cs               — 현재 문서/Action 상태에서 expression completion 후보를 제공하는 UI 포트다.
  WorkflowNodeFactory.cs                     — Toolbox kind에서 기본값이 채워진 Core 노드를 생성한다.
  IFileDialogService.cs / FileDialogService.cs              — Workflow/image 파일 선택·저장 대화상자를 추상화한다.
  ShellFolderPicker.cs                        — net48/Windows 7 Shell dialog를 폴더 선택 모드로 연다.
  IExchangeFolderLauncher.cs / ExchangeFolderLauncher.cs    — 현재 교환 폴더를 Explorer로 연다.
  IEquipmentExchangePathLauncher.cs / EquipmentExchangePathLauncher.cs — terminal 항목의 파일을 Explorer에서 선택한다.
  IDefaultFileLauncher.cs / DefaultFileLauncher.cs          — 결과 image 등 기존 파일을 기본 연결 프로그램으로 연다.
  IUserDialogService.cs / UserDialogService.cs               — 확인, 오류, unsaved changes UI를 한곳에 모은다.
  IContentDialogGate.cs                       — WPF-UI ContentDialog의 중복 표시를 직렬화한다.
  IResponseSimulationDialogService.cs / ResponseSimulationDialogService.cs — 선택 Action의 테스트 response 편집·게시를 조정한다.
  IEquipmentScreenPopOutService.cs / EquipmentScreenPopOutService.cs — 실시간 설비 화면을 별도 Window로 전환한다.
  Capture/
    ILiveCaptureSnapshotStore.cs              — 고화질 capture의 메모리 snapshot 저장 포트다.
    LiveCaptureSnapshot.cs                    — freeze된 BitmapSource와 원본 경로의 dispose 가능한 snapshot이다.
    LiveCaptureSnapshotStore.cs               — 최신 snapshot 교체·복사·수명 관리를 담당한다.
  Imaging/
    ILiveImageDecoder.cs                      — byte/file image decode 포트다.
    LiveImageDecoder.cs                       — 파일 lock을 남기지 않는 decode와 WPF freeze를 수행한다.
    LiveImageDecodeResult.cs                  — decode된 BitmapSource와 원본 크기 정보를 반환한다.
    LiveImageSafetyLimits.cs                  — frame byte/pixel/dimension 상한을 정의한다.
    LiveImageLimitExceededException.cs        — 안전 상한 초과를 사용자 오류로 구분한다.
  ResponseSimulation/
    ITemporaryResponseImageService.cs         — 테스트용 임시 image 생성 포트다.
    TemporaryResponseImage.cs                 — 경로와 preview bitmap을 함께 가진 생성 결과다.
    TemporaryResponseImageService.cs          — 768×512 mosaic image를 LocalAppData에 만들고 종료 시 정리한다.
ViewModels/
  MainWindowViewModel.cs                       — Navigation item과 shell-level 표시 상태를 제공한다.
  MainPageViewModel.cs                         — Designer의 문서, selection, clipboard, drag/drop, 명령, panel과 실행 상태를 조정한다.
  WorkflowActionViewModel.cs                   — Action 카드의 파라미터, 결과, 실행/검증/선택/확대 상태를 표현한다.
  WorkflowBranchViewModel.cs                   — Repeat/Conditional 중첩 branch와 자식 카드 컬렉션을 표현한다.
  ActionParameterViewModel.cs                  — 편집 값, enum 후보, validation과 expression completion 입력 상태다.
  RuntimeResultViewModel.cs                    — 카드/inspector의 response 필드, image preview와 펼침 상태를 만든다.
  RuntimeResultFieldViewModel.cs               — 결과의 한 변수명·설명·표시 값을 표현한다.
  WorkflowValidationIssueViewModel.cs          — Core validation issue를 UI 목록 항목으로 바꾼다.
  ToolboxItemViewModel.cs                      — 검색 가능한 표시명/action 이름과 장비/Designer 분류를 제공한다.
  LiveInteractionPageViewModel.cs              — Live loop, exclusive 명령, image marker, OM/CCD, Focus chart와 capture UI 상태를 조정한다.
  EquipmentCommunicationMonitorViewModel.cs    — trace sink 구현으로 terminal 항목, pending count와 연결 표시를 관리한다.
  EquipmentCommunicationEntryViewModel.cs      — terminal 한 줄의 방향/상태/action/correlation/path/구조화 payload다.
  ResponseSimulationDialogViewModel.cs         — 테스트 response 필드, 경로, preview 재생성과 검증 상태다.
  RealtimeVideoSettingsViewModel.cs             — SignalR/JWT/retry/frame 설정을 편집 문자열과 typed options 사이에서 변환한다.
  SettingsPageViewModel.cs                     — 설정 그룹 검증·저장, 즉시 적용과 restart-required 알림을 조정한다.
Views/
  MainWindow.xaml / MainWindow.xaml.cs         — FluentWindow, compact navigation, TitleBar layout toggle과 상태 영역 shell이다.
  MainPage.xaml / MainPage.xaml.cs             — Designer의 CommandBar, toolbox, Canvas, inspector, terminal/validation/video layout이다.
  LiveInteractionPage.xaml / LiveInteractionPage.xaml.cs — Designer와 정렬을 맞춘 Live 명령 bar, OM/CCD image와 expandable 설정 카드 화면이다.
  SettingsPage.xaml / SettingsPage.xaml.cs     — Appearance, file communication, realtime/auth/retry 설정 그룹 화면이다.
  ResponseSimulationDialogContent.xaml / ResponseSimulationDialogContent.xaml.cs — 테스트 response 입력과 image preview ContentDialog content다.
  EquipmentScreenWindow.xaml / EquipmentScreenWindow.xaml.cs — pop-out realtime equipment image window다.
  StartupDialogWindow.xaml / StartupDialogWindow.xaml.cs — MainWindow가 준비되기 전 startup error를 표시하는 독립 창이다.
  IEquipmentPanelLayoutHost.cs                 — shell의 terminal/validation/video visibility를 페이지 layout에 전달하는 UI 계약이다.
Properties/AssemblyInfo.cs                     — WPF theme/assembly 메타데이터와 test friend 접근을 정의한다.
```

`MainPageViewModel`과 `LiveInteractionPageViewModel`은 여전히 큰 coordinator다. 다음 분리는 화면 동작을 바꾸지 않는 범위에서 각각 `DesignerClipboardCoordinator`, `DesignerDragDropCoordinator`, `DesignerLayoutState`, `LiveFrameLoopCoordinator`, `LiveExclusiveCommandCoordinator`로 추출하는 순서가 안전하다. 먼저 기존 테스트가 호출하는 공개 API를 유지하는 facade를 남기고, 장비 transport와 취소 소유권은 ViewModel에서 직접 재구현하지 않는다.

### `tests/DrillFlow.Tests`

현재 단일 net48 테스트 프로젝트로 모든 계층과 Windows 7 호환 경계를 같은 런타임에서 검증한다.

```text
DrillFlow.Tests.csproj                         — Core/Application/Infrastructure/Desktop를 참조하는 xUnit 테스트 프로젝트다.
CoreExpressionCompletionProviderTests.cs       — 접근 범위와 자동완성 member 후보를 검증한다.
CoreExpressionEngineTests.cs                   — 식 파싱, 연산, member/index 접근과 오류를 검증한다.
CoreRunResultStoreTests.cs                     — Run 교체와 반복 결과 보존/조회 규칙을 검증한다.
CoreWorkflowModelTests.cs                      — 노드 기본값과 트리 모델 불변식을 검증한다.
CoreWorkflowNodeCopyTests.cs                   — deep copy, 새 ID/별칭과 내부 expression 참조 재작성 규칙을 검증한다.
CoreWorkflowValidatorTests.cs                  — 구조/파라미터/expression의 실행 전 오류 탐지를 검증한다.
ApplicationEquipmentResponseMessageTests.cs    — response result/properties와 JSON 보조 표현을 검증한다.
ApplicationLiveImageCoordinateMapperTests.cs   — DPI-independent viewport와 원본 pixel 좌표 매핑을 검증한다.
ApplicationLiveInteractionSessionTests.cs      — Live session request 구성, 직렬화와 fault 동작을 검증한다.
ApplicationRealtimeVideoOptionsTests.cs        — RealtimeVideo clone과 option validation 규칙을 검증한다.
ApplicationRequestLifecycleOptionsTests.cs     — request/response lifecycle 기본값과 의미를 검증한다.
ApplicationWorkflowRunnerTests.cs              — 실행 순서, 표현식, 제어 흐름, breakpoint/stop/fault와 결과 기록을 검증한다.
InfrastructureAtomicFilePublisherTests.cs      — temp 완성 후 replace/move 게시와 실패 안전성을 검증한다.
InfrastructureCorrelationIdTests.cs            — persisted block 예약, 동시 발급과 재시작 비재사용을 검증한다.
InfrastructureFileTransportTests.cs             — lock/publish/poll/matching/retry/cleanup/cancel handshake를 통합 검증한다.
InfrastructureStableEquipmentFileReaderTests.cs — 쓰기 중/변경 중 파일 거부와 안정 byte snapshot을 검증한다.
InfrastructureXmlTemplateEquipmentMessageCodecTests.cs — 25개 템플릿, placeholder, BOM/공백/과학 표기와 fault parsing을 검증한다.
InfrastructureResponseSimulatorTests.cs         — 테스트 response 검증과 atomic XML 생성을 검증한다.
InfrastructureWorkflowSerializationTests.cs     — Workflow polymorphic JSON round-trip과 호환성을 검증한다.
InfrastructureHttpActionExecutorTests.cs        — HTTP method/header/body/JSON/timeout 동작을 검증한다.
InfrastructureLiveInteractionSessionTests.cs    — 실제 codec/transport seam을 통한 Live 명령 교환을 검증한다.
InfrastructureOptionsTests.cs                   — communication/correlation 옵션 validator와 DI 구성을 검증한다.
DesktopActionParameterViewModelTests.cs         — parameter 입력, enum 편집과 validation UI 상태를 검증한다.
DesktopCommunicationTimingTests.cs              — `double Seconds` 사용자 설정, runtime `TimeSpan` 변환과 legacy milliseconds 읽기 alias를 검증한다.
DesktopDefaultFileLauncherTests.cs               — 안전한 기본 프로그램 실행 조건을 검증한다.
DesktopEquipmentCommunicationMonitorViewModelTests.cs — terminal trace와 pending 상태를 검증한다.
DesktopExpressionCompletionBehaviorTests.cs      — completion 삽입 후 caret/selection 보존을 검증한다.
DesktopFocusScatterChartTests.cs                 — Focus point 자동 scale/렌더 경계를 검증한다.
DesktopLiveCaptureSnapshotStoreTests.cs          — capture bitmap 복사와 교체/dispose를 검증한다.
DesktopLiveImageDecoderTests.cs                  — image 상한, 완전 메모리 decode와 file lock 해제를 검증한다.
DesktopLiveInteractionCancellationTests.cs       — frame/exclusive 명령 취소와 timeout helper를 검증한다.
DesktopLiveInteractionPageViewModelTests.cs      — Live loop, marker, HFW/pitch, exclusive 명령과 UI 상태를 검증한다.
DesktopResponseSimulationPreviewTests.cs         — mosaic preview 생성·교체와 임시 경로를 검증한다.
DesktopRuntimeResultImageTests.cs                — result image 표시, 확대/축소와 lifetime을 검증한다.
DesktopStartupSettingsLoaderTests.cs             — 배포/환경 기본값과 user/legacy 설정의 그룹별 startup 병합을 검증한다.
DesktopToolboxItemViewModelTests.cs              — 표시명/action/검색 분류를 검증한다.
DesktopUserSettingsStoreTests.cs                 — user 설정 우선순위, legacy migration과 atomic save를 검증한다.
DesktopWorkflowActionValidationStateTests.cs     — 오류 Action 카드의 상태와 실행 차단 표시를 검증한다.
DesktopWorkflowValidationPolicyTests.cs          — 변경 시 자동 검증 설정 flag를 검증한다.
StringCompatibilityExtensions.cs                 — net48에 없는 최신 string overload를 테스트에서 보완한다.
TaskTimeoutExtensions.cs                         — 비동기 테스트의 유한 timeout helper다.
```

## 3. 장비 요청부터 결과 표시까지의 호출 흐름

```text
MainPageViewModel / LiveInteractionPageViewModel
  → WorkflowExecutionFacade / ILiveInteractionSession
  → WorkflowRunner / LiveInteractionSession
      1. 전체 Run은 Core WorkflowValidator로 전체 문서를, 선택 Action Run은 해당 subtree와 직접 참조를 검증
      2. expression을 평가해 typed parameter dictionary 생성
      3. PersistentCorrelationIdProvider에서 양의 correlation ID 발급
      4. EquipmentRequestMessage 생성
  → IEquipmentFileTransport.ExchangeAsync
  → FileEquipmentTransport
      5. EquipmentCommunicationSnapshot으로 이번 exchange 설정 고정
      6. in-process gate와 `.drillflow.exchange.lock` 획득
      7. XmlTemplateEquipmentMessageCodec.SerializeRequest
      8. 완료된 temp 파일을 AtomicFilePublisher로 request.xml에 게시
      9. StableEquipmentFileReader로 response.xml이 writer에게 닫히고 안정될 때까지 polling
     10. codec이 expected action/correlation/계약/result와 일치하는 immutable bytes만 승인
     11. request 정리 → response 객체 materialize → response 정리
  → EquipmentResponseMessage
      12. `result: 1`이면 Action fault와 Workflow 중단
      13. `result: 0`이면 RunResultStore에 Action/iteration 결과 기록
      14. runner/trace 이벤트가 카드, inspector, terminal과 image view를 갱신
```

논리 JSON 객체는 디버깅, UI 구조화 표시와 내부 처리 편의를 위한 메모리 표현이다. 중간 `request.json`/`response.json` 파일을 만들지 않는다. 실제 wire payload는 embedded XML 정답지에 정확한 placeholder 값을 넣거나 그 자리의 값을 추출해 처리한다.

## 4. 파일 handshake에서 바꾸면 안 되는 순서

다음 순서는 단순 구현 세부가 아니라 동일 파일명을 공유하는 장비와 컨트롤러 사이의 소유권 protocol이다. 순서를 바꾸려면 `InfrastructureFileTransportTests`, `InfrastructureStableEquipmentFileReaderTests`, `InfrastructureAtomicFilePublisherTests`에 먼저 실패하는 회귀 테스트를 추가해야 한다.

1. `EquipmentCommunicationSnapshot`을 먼저 캡처해 폴더, 파일명, 시간과 lifecycle이 exchange 도중 섞이지 않게 한다.
2. 프로세스 내부 `_exchangeGate`를 잡은 뒤 교환 디렉터리의 `.drillflow.exchange.lock`을 `FileShare.None`으로 잡는다. 두 잠금은 cleanup이 끝날 때까지 다음 게시자를 막는다.
3. 장비가 request를 delete-after-read하는 모드라면 이전 request 경로가 실제로 사라질 때까지 기다린다. 남아 있는 파일을 stale이라고 추측해 덮어쓰지 않는다.
4. 첫 게시 전에 설정된 quiet delay를 두 잠금을 소유한 상태로 기다린다. 취소되면 어떤 request도 게시하지 않는다.
5. response 기본 삭제 모드에서는 이전 response를 먼저 지운다. 유지 모드이거나 삭제 실패 시에는 기존 안정 bytes를 baseline으로 잡아 같은 낡은 파일을 새 response로 오인하지 않는다.
6. XML 전체를 temp 파일에 쓰고 flush한 뒤 같은 디렉터리에서 atomic replace/move로 최종 request 이름을 게시한다. 최종 경로에 직접 streaming write하지 않는다.
7. response polling은 크기/수정 시각 전후가 같고, 안정화 지연을 통과했으며, `FileShare.Read` open과 정확한 길이 read가 끝난 immutable bytes만 codec에 넘긴다. 일부 작성 중인 파일은 “아직 없음”으로 취급하고 다음 poll에서 재시도한다.
8. retained baseline과 byte-identical payload는 새 response로 인정하지 않는다. 그 다음 템플릿 구조, action과 현재 request의 correlation ID, result 및 action별 필드 검증을 모두 통과해야 matching response다.
9. matching bytes를 메모리에 확보한 후, 장비 delete-after-read 모드의 request 소유권 대기를 먼저 끝내고 앱 lifecycle이 허용하면 완료 request를 best-effort 삭제한다.
10. 같은 immutable bytes를 `EquipmentResponseMessage`로 materialize한 다음 trace/result 소비자가 파일 수명에 의존하지 않게 한다.
11. 그 다음에만 response를 best-effort 삭제한다. request 삭제보다 response 삭제를 앞으로 옮기지 않는다.
12. 성공/fault 결과를 호출자에게 반환한 뒤 cross-process lock과 in-process gate를 해제한다.

추가 불변 규칙:

- timeout 뒤에도 delete-after-read request가 사라질 때까지 기다리고 마지막 late response를 한 번 확인한 뒤 retry/실패를 결정한다.
- retry는 같은 논리 명령과 같은 correlation ID의 동일 XML bytes를 다시 게시한다. 물리 동작의 정확히 한 번 실행은 장비의 durable deduplication 계약 없이는 보장되지 않는다.
- Stop/cancel이 게시 후 발생하면 request 정리 작업이 sidecar와 in-process gate 소유권을 넘겨받는다. 정리 task가 자신이 게시한 bytes와 현재 파일이 같은지 확인하기 전에는 삭제하지 않는다.
- cleanup의 sharing/permission 오류는 유효한 response를 버리거나 다음 Workflow 결과 materialization을 실패시키지 않는 best-effort 경고다.
- UTF-8 BOM은 0개 또는 1개만 허용하고 메모리에서 제거한다. 비교를 위해 텍스트 공백 view를 만들 수 있지만, 값 추출은 원본 XML의 의미 있는 `image_path`/matrix 공백을 보존한다.
- wire payload 4 MiB 상한과 image decode byte/pixel 상한은 메모리 고갈 방어이므로 우회하지 않는다.

## 5. 설정의 출처와 적용 시점

### 출처와 우선순위

```text
배포 기본값
  src/DrillFlow.Desktop/appsettings.json
        ↓ Host configuration
  DRILLFLOW_CONFIG_ 구성 환경 변수 (`__`로 중첩 키 구분)
        ↓ StartupSettingsLoader
사용자 설정
  %LocalAppData%\DrillFlow\appsettings.user.json
        ↓ 없을 때만 읽고 새 파일로 migration
legacy 사용자 설정
  %LocalAppData%\DrillFlow\settings.json
```

Host configuration 안에서는 `DRILLFLOW_CONFIG_` 환경 변수가 `appsettings.json`보다 나중에 추가된다. 예를 들어 `DRILLFLOW_CONFIG_EquipmentCommunication__ResponseTimeout`처럼 사용한다. JWT bearer secret은 구성 prefix에 넣지 않고 별도 `DRILLFLOW_SIGNALR_JWT` 환경 변수에서 읽어 구성 진단이나 일반 설정 파일에 섞이지 않게 한다. Desktop의 사용자 파일은 그 위에 별도 startup override로 병합된다. 유효한 사용자 communication 그룹은 `EquipmentCommunication` 기본값/환경 변수를 덮어쓰며, 그룹 전체가 invalid이면 부분 적용하지 않고 기본값으로 돌아간다. RealtimeVideo 그룹은 별도로 검증하므로 invalid realtime 설정이 파일 통신 설정까지 무효화하지 않는다.

`Host.CreateDefaultBuilder`가 원래 추가하는 무접두사 application environment source는
`DesktopHostFactory`가 제거한 뒤 위 두 명시적 source만 다시 구성한다. 따라서 실제 JWT 값은
`IConfiguration`을 열거하거나 dump하는 코드에 나타나지 않는다. JSON에는 token 값이 아니라
credential 이름 또는 token 환경 변수의 이름만 남는다.

| 설정 그룹 | 저장 위치 | Save 후 현재 프로세스 | 완전 적용 시점 |
|---|---|---|---|
| 언어 | `appsettings.user.json` | 즉시 resource dictionary 교체 | 즉시 |
| System/Light/Dark 테마 | `appsettings.user.json` | 선택 순간 즉시 테마 적용, Save로 영속화 | 즉시 |
| Action 변경 시 자동 검증 | `appsettings.user.json` | policy flag 즉시 반영 | 즉시 |
| 교환 폴더/파일명/lifecycle/timeout/poll/retry/quiet/stable delay | `appsettings.user.json`; timing은 `double Seconds` | Save가 공유 `EquipmentCommunicationOptions` 인스턴스를 `TimeSpan` 값으로 갱신 | **다음 exchange**가 새 snapshot을 캡처할 때 |
| SignalR endpoint/transport/JWT 참조/retry/frame 제한 | `appsettings.user.json` | 저장만 하고 restart-required 표시 | 앱 재시작 후 startup validation |
| correlation ID 저장 경로/block | `appsettings.json` 또는 환경 변수 | provider가 이미 열린 뒤에는 변경하지 않음 | 앱 재시작 |
| Serilog log 경로/Host 조립 | 코드와 startup configuration | runtime 재조립 없음 | 앱 재시작 |

현재 파일 통신 설정 화면은 Workflow 또는 Live 작업 중 편집을 막는다. 그럼에도 transport가 mutable options를 직접 여러 번 읽지 않고 각 exchange 시작 시 snapshot을 쓰는 이유는 미래의 외부 변경이나 다른 호출자의 race에서도 한 요청이 두 폴더/파일명 설정을 섞어 쓰지 않게 하기 위해서다.

`appsettings.json`은 `reloadOnChange: false`이고 사용자 파일도 watcher로 reload하지 않는다. 사용자가 앱 밖에서 파일을 직접 수정했다면 재시작해야 한다. 사용자 communication timing의 현재 키는 모두 `*Seconds`이고 소수 입력을 허용한다. `*Milliseconds` 구버전 키는 migration을 위한 deserialization-only alias라서 다시 저장되지 않는다. JWT secret/token 원문은 사용자 JSON에 저장하지 않고 credential 이름 또는 환경 변수 이름만 저장하며, 기본 token 환경 변수는 `DRILLFLOW_SIGNALR_JWT`다. 현재 RealtimeVideo는 설정/검증/UI 경계이며 실제 SignalR frame client가 연결될 때도 credential resolver를 통해 런타임에 secret을 얻어야 한다.

## 6. 새 Equipment Action을 추가하는 변경 지도

새 장비 Action은 아래 순서로 추가한다. 한 위치만 추가해 부분 계약이 생기지 않도록 같은 PR/commit에서 전체 지점을 갱신한다.

1. **계약 결정:** `contract.md`에 action 문자열, request/성공 response/fault response 필드, 단위, 범위와 null/배열 규칙을 먼저 적는다.
2. **Core 모델:** `Workflows/WorkflowNode.cs`의 `WorkflowNodeKind`, `EquipmentNodes.cs`의 노드와 기본 `ParameterBinding`, `WorkflowNodeCopy.cs`의 deep-copy case를 추가한다.
3. **Core 검증:** `ParameterValueValidator.cs`에 리터럴/평가 결과 형식·범위를 넣고 `WorkflowValidator.cs`에서 표현식과 구조 검증이 새 노드에 도달하는지 확인한다.
4. **Application 계약:** `EquipmentRequestMessage.cs`의 `EquipmentActionNames`, request/response field 접근과 simulator draft 모델을 갱신한다.
5. **실행 mapping:** `WorkflowRunner.ExecuteNodeAsync`와 장비 request parameter 생성/result field materialization을 추가한다. `result: 1`은 계속 fault로 Workflow를 중단해야 한다.
6. **Live 노출 여부:** Live 화면에서도 실행할 명령이면 `ILiveInteractionSession`, `LiveInteractionSession`과 `LiveInteractionProtocol`에 exclusive 실행 API를 추가한다. 연속 frame과 동시에 게시하지 않는다.
7. **XML 정답지:** `Infrastructure/Communication/Templates/{Action}/request.xml`, `response.xml` 및 성공 전용 필드가 있으면 `failure-response.xml`을 추가하고 csproj embedded-resource glob에 포함되는지 확인한다.
8. **Codec:** `XmlTemplateEquipmentMessageCodec`의 template descriptor, expected placeholder 집합, request field 렌더링, 성공/fault response parsing과 과학 표기/경로/matrix validator를 추가한다.
9. **Simulator:** `JsonEquipmentResponseSimulator`의 기본값·입력 검증과 새 response XML 게시를 추가한다.
10. **Workflow persistence:** `JsonWorkflowDocumentSerializer`의 discriminator/생성/migration과 round-trip을 추가한다.
11. **Desktop 생성/표시:** `WorkflowNodeFactory`, `ToolboxItemViewModel`/`MainPageViewModel` toolbox, `ActionParameterViewModel`, `WorkflowActionViewModel`과 결과 field 설명을 갱신한다.
12. **리소스/UI:** `Strings.ko-KR.xaml`, `Strings.en-US.xaml`, 필요한 XAML DataTemplate/아이콘과 expression completion member 설명을 추가한다.
13. **검증:** 최소한 Core model/validator/copy, WorkflowRunner, XML codec 전체 template, simulator, serializer, transport matching, Desktop parameter/toolbox 테스트를 추가한다. Live 노출 시 Application/Infrastructure/Desktop Live 테스트도 추가한다.

장비 Action이 아니라 Designer 내부 Action이라면 XML 템플릿, correlation ID와 transport를 사용하지 않는다. HTTP/Delay/Repeat/Conditional과 같은 경로로 Application executor 또는 Core 제어 흐름에 구현한다.

## 7. 지금 프로젝트를 더 나누지 않은 이유

현재 네 제품 프로젝트는 변경 이유가 분명한 계층 경계를 이미 제공한다. 더 작은 assembly로 즉시 분리하지 않은 이유는 다음과 같다.

- 가장 중요한 파일 handshake가 codec, stable reader, atomic publisher와 transport의 시간·소유권 규칙으로 결합되어 있다. 단순히 DLL을 나누면 안전성이 높아지지 않고 internal seam과 검증 범위만 넓어진다.
- Desktop 기능을 WPF class library 여러 개로 나누면 ResourceDictionary pack URI, localization, WPF-UI theme resource, View navigation과 디자인 타임 metadata가 동시에 이동해 UI 회귀 위험이 현재 이득보다 크다.
- 실행 파일과 composition root가 하나이고 별도 장비 서비스/서버가 아직 없어서 contracts 전용 package의 소비자가 없다.
- RealtimeVideo는 옵션/검증/UI만 구현된 상태다. 구체 SignalR client와 인증/재연결 정책이 안정되기 전에 assembly 경계를 고정하면 잘못된 abstraction을 영구화할 가능성이 크다.
- 단일 Tests 프로젝트는 net48/WPF와 netstandard 계층을 같은 CI 명령으로 검증한다. 현재 규모에서는 프로젝트별 process 시작 비용보다 검색 가능한 파일명 prefix가 충분한 구분을 제공한다.

새 프로젝트 분리를 검토할 조건은 다음과 같다.

- 별도 장비 에뮬레이터, 서비스 또는 다른 앱이 동일 논리 request/response 모델을 참조하게 되면 `DrillFlow.Contracts`를 추가한다. 이 프로젝트는 WPF와 파일 transport를 참조하지 않는다.
- 실제 SignalR client가 구현되고 독립 package, 인증 provider, 재연결 state machine과 상당한 테스트를 가지면 `DrillFlow.Infrastructure.RealtimeVideo`로 분리한다. Application에는 session/frame port만 남긴다.
- 파일 handshake가 다른 UI 또는 headless runner에서도 재사용되거나 독립 배포/버전 관리가 필요해지면 현재 Infrastructure의 Communication을 별도 assembly로 분리한다. 그 전에는 internal stable-reader seam을 유지한다.
- MainPage/LiveInteraction 기능이 서로의 ViewModel/리소스를 참조하지 않고 공개 UI contract와 resource merge 전략이 안정되면 `DrillFlow.Desktop.Designer`와 `DrillFlow.Desktop.LiveInteraction` WPF library를 검토한다.
- 테스트 수와 실행 시간이 커져 계층별 병렬 CI 또는 명확한 ownership이 실질 이득을 주면 Tests를 Core/Application/Infrastructure/Desktop 프로젝트로 나눈다.

그 전까지는 프로젝트 수를 늘리기보다 큰 coordinator의 책임을 같은 assembly 안의 기능 폴더·내부 서비스로 먼저 분리한다. 이 방식은 public API와 DI 경계를 그대로 유지하며 되돌리기 쉽고, net48/WPF resource와 핵심 통신 동작의 회귀 위험이 가장 낮다.
