# DrillFlow 장비 파일 통신 계약

> 상태: 구현 기준 문서 · 계약 버전 1 · 최종 확인 2026-08-26
> 범위: 드릴 장비와 주고받는 단일 동작의 request/response 파일
> 비범위: 디자이너 워크플로 파일(`*.drillflow.json`)의 저장 스키마

이 문서는 장비 입·출력 구조를 변경하는 개발자나 에이전트가 현재 계약과 변경 지점을 빠르게 찾기 위한 기준 문서다. 현재 전송 표현은 **UTF-8(BOM 없음) JSON**으로 고정되어 있다. 설정의 파일 확장자는 파일명일 뿐 codec을 선택하지 않는다. 예를 들어 파일명을 `request.xml`로 바꾸더라도 현재 구현은 JSON을 기록한다. XML 또는 다른 형식을 실제로 지원하려면 [포맷 변경 지점](#json--xml-등-포맷-변경-지점)을 함께 수정해야 한다.

Designer 내부의 Delay/Repeat/Conditional 및 HTTP Action은 이 장비 파일 계약의 범위 밖이다. 특히 HTTP 응답은 `command: "return"` 구조를 요구하지 않으며 HTTP Action의 동적 런타임 결과로 별도 처리한다.

## 1. 공통 처리 모델

한 번에 하나의 Action만 다음 순서로 처리한다.

1. 앱이 양의 `Int32` correlation ID를 발급한다.
2. 앱이 request 파일을 임시 파일에 완전히 기록한 후 설정된 request 파일명으로 원자적으로 게시한다.
3. 장비가 request를 감지하고 동작한다.
4. 장비가 같은 `index`의 response 파일을 만든다.
5. 앱은 안정적으로 기록된 response 중 `index`가 일치하고 `command`가 정확히 `return`인 파일만 현재 요청의 응답으로 인정한다.
6. response의 확장 필드는 현재 Run의 해당 Action 결과로 보존된다.

현재 장비 동작은 모두 성공한다고 가정한다. 오류 코드, 성공 여부, 오류 응답 및 보상 동작은 계약에 없다. 툴바의 Stop은 장비 명령이 아니며 실행기를 멈춘다. Canvas의 명시적인 Abort Action만 `command: "abort"` request를 전송한다.

### 공통 request envelope

| 필드 | JSON 타입 | 필수 | 의미/제약 |
|---|---:|:---:|---|
| `index` | integer | 예 | 양의 `Int32` correlation ID. retry에도 **동일한 값**과 동일 payload를 사용한다. |
| `command` | string | 예 | 현재 `move`, `measure`, `drill`, `abort` 중 하나. 대소문자는 현재 소문자 고정이다. |
| 그 밖의 필드 | command별 | 조건부 | 아래 command 표를 따른다. `index`, `command`는 파라미터 이름으로 사용할 수 없다. |

### 공통 response envelope

| 필드 | JSON 타입 | 필수 | 의미/제약 |
|---|---:|:---:|---|
| `index` | integer | 예 | 처리한 request의 `index`와 정확히 같아야 한다. 현재 요청과 다르면 stale/다른 응답으로 무시한다. |
| `command` | string | 예 | 정확히 소문자 `return`이어야 한다. |
| `drill_result_path` | string | Drill 응답에서 정의됨 | 장비가 Drill 결과 CSV를 저장한 경로. 장비가 돌려주는 결과값이다. |
| 임의 확장 필드 | JSON 값 | 아니요 | 향후 필드를 허용한다. string/number/integer/boolean/null/array/object를 손실 없이 런타임 결과에 보존한다. |

`drill_result_path` 외 response 필드는 command별로 폐쇄되어 있지 않다. `EquipmentResponseMessage.Properties`는 알 수 없는 필드를 의도적으로 보존하며 Expression에서 접근할 수 있다. 현재 parser는 `index`와 `command`의 JSON property name을 정확한 소문자로 기대한다.

## 2. command별 request

모든 길이의 논리 단위는 **metre(m)**다. JSON에는 문자열이 아니라 number로 기록하며 serializer는 유한 `double`을 대문자 `E` 과학 표기법으로 출력한다. `1E-3`, `2.56E-4`, `0.0256E-2`처럼 같은 수를 표현하는 입력은 동일한 수로 평가된다. NaN과 ±Infinity는 허용하지 않는다.

### 2.1 Move — 드릴 헤드 이동

| 필드 | JSON 타입 | 필수 | 범위/의미 |
|---|---:|:---:|---|
| `index` | integer | 예 | 공통 envelope |
| `command` | string | 예 | `move` |
| `move_mode` | string | 예 | `relative` 또는 `absolute` |
| `move_x` | number | 예 | `-0.5 < move_x < 0.5` m. 음수 가능 |
| `move_y` | number | 예 | `-0.5 < move_y < 0.5` m. 음수 가능 |

`absolute`는 장비 home position `(0, 0)`을 중심으로 한 좌표이며, `relative`는 현재 위치 기준 변위다. 두 모드 모두 좌/우 및 상/하를 나타내기 위해 음수를 허용한다.

```json
{
  "index": 101,
  "command": "move",
  "move_mode": "relative",
  "move_x": 3E-3,
  "move_y": -2.56E-4
}
```

### 2.2 Measure — 철판과 헤드 사이 거리 측정

| 필드 | JSON 타입 | 필수 | 범위/의미 |
|---|---:|:---:|---|
| `index` | integer | 예 | 공통 envelope |
| `command` | string | 예 | `measure` |
| `thickness` | number | 예 | `0 < thickness <= 2.4E-3` m |

```json
{
  "index": 102,
  "command": "measure",
  "thickness": 1E-3
}
```

### 2.3 Drill — 구멍 가공

| 필드 | JSON 타입 | 필수 | 범위/의미 |
|---|---:|:---:|---|
| `index` | integer | 예 | 공통 envelope |
| `command` | string | 예 | `drill` |
| `thickness` | number | 예 | `0 < thickness <= 2.4E-3` m |
| `drill_result_path` | string | 예 | 장비가 가공 결과 CSV를 기록할 목적지 경로. 빈 문자열 불가 |

```json
{
  "index": 103,
  "command": "drill",
  "thickness": 2.4E-3,
  "drill_result_path": "C:\\DrillResults\\hole-103.csv"
}
```

### 2.4 Abort — 장비 중단

추가 파라미터가 없다. 이 command는 Canvas에 Abort Action이 있을 때만 전송한다.

```json
{
  "index": 104,
  "command": "abort"
}
```

## 3. response 예시와 런타임 표현

현재 최소 예시는 다음과 같다.

```json
{
  "index": 103,
  "command": "return",
  "drill_result_path": "C:\\DrillResults\\hole-103.csv"
}
```

확장 필드도 허용된다.

```json
{
  "index": 103,
  "command": "return",
  "drill_result_path": "C:\\DrillResults\\hole-103.csv",
  "measured_distance": 1.82E-3,
  "metadata": {
    "head": "A"
  },
  "samples": [0.12, 0.13]
}
```

Expression에서 이전 Action alias가 `drill_1`이라면 다음 객체를 제공한다.

| 접근 경로 | 값 |
|---|---|
| `drill_1.parameters` | 실행 시 평가된 request 파라미터. `index`와 `command`는 포함하지 않는다. |
| `drill_1.result` | 현재 Run의 가장 최근 결과. 실행 전에는 `null` |
| `drill_1.last` | `result`와 동일 |
| `drill_1.results` | Repeat를 포함한 현재 Run의 모든 결과 배열 |
| `drill_1.results.last` | 결과 배열의 마지막 값 또는 빈 배열이면 `null` |
| `drill_1.results.count` / `.length` | 결과 개수 |
| `drill_1.results[0]` | 0-based 결과 접근 |

각 result에는 response 확장 필드 외에 `index`와 `iteration_path`가 노출된다. 장비 Action에는 `command: "return"`도 노출된다. 예: `=drill_1.last.drill_result_path`, `=measure_1.result.measured_distance`.

### 디자이너의 테스트 Response 기본값

MainPage의 **Response 테스트** ContentDialog는 commissioning 편의를 위해 선택한 장비 Action별 편집 가능한 JSON 초안을 만든다.

| Action | 초안의 동적 결과 필드 |
|---|---|
| Move | `position_x: 0`, `position_y: 0` |
| Measure | `measured_distance: 1E-3` |
| Drill | `drill_result_path` (리터럴 입력 경로 또는 샘플 CSV 경로) |
| Abort | 추가 필드 없음 |

이 표는 **시뮬레이터의 편집 시작값**이며 장비가 반드시 반환해야 하는 폐쇄 스키마는 아니다. 실제 response의 추가 필드는 계속 보존되고, 한 번 관찰된 필드는 현재 Run에서 Ctrl+Space 자동완성 후보에도 합쳐진다. 테스트 게시 시 현재 request 파일이 있으면 그 `index`를 우선 사용한다. `EquipmentDeletesAfterRead` 모드에서는 response `index`와 같은 request만 장비처럼 삭제한 후 response를 원자적으로 게시한다.

Control flow Action은 장비 파일을 만들지 않지만 동일한 Expression 객체를 가진다.

| Action | parameters | result 필드 |
|---|---|---|
| Delay | `milliseconds` (`0..29999` ms) | `elapsed_milliseconds`, `index`(0), `iteration_path` |
| Repeat | `count` (`1..Int32.MaxValue`) | `count`, `index`(0), `iteration_path` |
| Conditional | 없음 | `branch_index`(선택 없음은 -1), `branch_kind`(`if`/`elseif`/`else`/`none`), `index`(0), `iteration_path` |

Expression은 앞의 첫 non-whitespace 문자가 `=`일 때만 적용된다. 안전 parser는 산술, 비교, 논리, member/index 접근만 지원하며 C# 실행, reflection, method call은 허용하지 않는다. 참조할 수 있는 alias는 해당 위치보다 앞서 실행되고 결과가 보장되는 활성 Action뿐이다.

- Repeat 본문의 다음 Action은 같은 본문의 앞선 활성 Action을 볼 수 있다.
- 활성 Repeat는 count가 최소 1이므로 본문의 활성 alias를 Repeat 다음 위치에서 볼 수 있다.
- Conditional branch 내부에서는 같은 branch의 앞선 alias를 볼 수 있다.
- 특정 branch가 선택된다는 보장이 없으므로 branch 내부 alias는 Conditional 바깥으로 나오지 않는다.
- 비활성 Action과 현재/뒤쪽 Action은 참조할 수 없다.

## 4. 파일 및 재시도 계약

설정은 `EquipmentCommunicationOptions`에 매핑된다.

| 설정 | 현재 동작 |
|---|---|
| `ExchangeDirectory` | request와 response가 함께 존재하는 절대 로컬 또는 UNC 폴더 |
| `RequestFileName` / `ResponseFileName` | 경로 없는 leaf name이며 확장자 필수. 서로 달라야 함 |
| `EquipmentRequestLifecycle = EquipmentDeletesAfterRead` | 게시 전/응답 후/재시도 전 장비가 request를 삭제할 때까지 bounded wait. 늦은 삭제가 다음 request를 지우지 않게 한다. |
| `EquipmentRequestLifecycle = RetainUntilOverwritten` | 다음 게시에서 완료된 request를 원자적으로 교체 |
| `ApplicationResponseLifecycle = DeleteAfterRead` | 정상 matching response를 읽은 후 앱이 삭제(기본 UX 선택) |
| `ApplicationResponseLifecycle = RetainUntilOverwritten` | response를 남기고 다음 장비 응답이 교체하도록 허용 |
| `ResponseTimeout` | matching response 대기 시간 및 관련 bounded I/O 대기 기준 |
| `RetryEnabled`, `MaximumRetryCount`, `RetryDelay` | timeout 시 동일 `index` 및 payload를 재게시. MaximumRetryCount는 최초 시도 외 추가 횟수 |
| `PollingInterval`, `StableReadDelay` | 로컬/SMB 모두 polling을 source of truth로 사용하고 크기/수정시간이 안정된 파일만 읽음 |

동일 폴더의 전체 exchange는 `.drillflow.exchange.lock`을 `FileShare.None`으로 열어 프로세스/워크스테이션 간 직렬화한다. 이미 존재하는 response와 byte-for-byte 같은 내용은 새 응답으로 인정하지 않는다.

## 5. 코드의 현재 Source of Truth

### 논리 message와 실행 매핑

- `src/DrillFlow.Application/Communication/EquipmentRequestMessage.cs`
  `index`, `command`, 동적 request parameters 및 예약 필드 검사
- `src/DrillFlow.Application/Communication/EquipmentResponseMessage.cs`
  `index`, `command`, 임의 response properties 보존
- `src/DrillFlow.Application/Execution/WorkflowRunner.cs`
  Action→command 매핑, `EvaluateMove/Measure/Drill`, response→런타임 result, control flow result
- `src/DrillFlow.Core/Workflows/EquipmentNodes.cs`
  command별 authored `ParameterBinding`과 Expression 변수명
- `src/DrillFlow.Core/Validation/ParameterValueValidator.cs`
  단위, 타입, 수치 범위의 실행/도메인 검증
- `src/DrillFlow.Core/Validation/WorkflowValidator.cs`
  저장 전 전체 workflow 및 이전 Action 참조 규칙 검증
- `src/DrillFlow.Core/Expressions/ExpressionContext.cs`
  `parameters/result/results/last` 런타임 object shape
- `src/DrillFlow.Core/Expressions/ExpressionCompletionProvider.cs`
  Ctrl+Space 후보의 알려진 parameter/result field catalog

### 파일 표현과 I/O

- `src/DrillFlow.Application/Communication/IEquipmentFileTransport.cs`
  실행 계층이 의존하는 논리 exchange 경계
- `src/DrillFlow.Infrastructure/Communication/FileEquipmentTransport.cs`
  현재 JSON serialization/deserialization, stable polling, correlation match, lifecycle/retry
- `src/DrillFlow.Application/Communication/IEquipmentResponseSimulator.cs` 및
  `src/DrillFlow.Infrastructure/Communication/JsonEquipmentResponseSimulator.cs`
  선택 Action의 테스트 response 초안/검증/원자적 게시 경계. UI는 format-neutral interface만
  사용하므로 XML 전환 시 이 구현과 DI 등록을 함께 교체한다.
- `src/DrillFlow.Infrastructure/IO/AtomicFilePublisher.cs`
  완료된 temp 파일의 원자적 publish/replace와 UNC fallback
- `src/DrillFlow.Application/Communication/EquipmentCommunicationOptions.cs` 및
  `src/DrillFlow.Infrastructure/Communication/EquipmentCommunicationOptionsValidator.cs`
  폴더/파일명/timeout/lifecycle 설정 계약
- `src/DrillFlow.Infrastructure/Persistence/PersistentCorrelationIdProvider.cs`
  양의 단조 증가 Int32 correlation ID

### UI 및 테스트

- `src/DrillFlow.Desktop/ViewModels/ActionParameterViewModel.cs`
  입력 즉시 검증과 변수명/설명 표시
- `src/DrillFlow.Desktop/Views/MainPage.xaml`
  authored parameter 및 현재 Run result 표시
- `src/DrillFlow.Desktop/Services/ResponseSimulationDialogService.cs` 및
  `src/DrillFlow.Desktop/Views/ResponseSimulationDialogContent.xaml`
  WPF-UI ContentDialog 기반 테스트 response 편집/게시 UI
- `tests/DrillFlow.Tests/ApplicationWorkflowRunnerTests.cs`
  command/parameter 매핑, dynamic response, Repeat/Stop/Breakpoint 실행
- `tests/DrillFlow.Tests/InfrastructureFileTransportTests.cs`
  실제 request JSON, response parser, stale/mismatch, timeout/retry/lifecycle
- `tests/DrillFlow.Tests/CoreWorkflowValidatorTests.cs`
  값 범위와 Expression visibility
- `tests/DrillFlow.Tests/CoreExpressionCompletionProviderTests.cs`
  자동완성 후보/중첩 visibility/token replacement
- `tests/DrillFlow.Tests/InfrastructureResponseSimulatorTests.cs`
  Action별 초안, validation, atomic publish, matching request 삭제 및 실제 transport 연동

워크플로 문서 serializer인 `JsonWorkflowDocumentSerializer`는 장비 통신 codec과 별개다. 장비 protocol을 XML로 바꿀 때 `.drillflow.json`까지 바꿀 필요는 없다.

## 6. JSON → XML 등 포맷 변경 지점

현재 JSON codec은 `FileEquipmentTransport`의 private `SerializeRequest`, `TryParseMatchingResponse`, `ConvertToken`, `ScientificNotationJsonTextWriter`에 결합되어 있다. 포맷을 하나 더 추가하거나 전환할 때 권장하는 변경 순서는 다음과 같다.

1. Application 계층의 `EquipmentRequestMessage`/`EquipmentResponseMessage`를 포맷 독립 logical contract로 유지한다.
2. Infrastructure에 `IEquipmentMessageCodec` 같은 경계를 만들고 다음을 분리한다.
   - `SerializeRequest(EquipmentRequestMessage) -> byte[]`
   - `TryDeserializeResponse(byte[], expectedIndex) -> EquipmentResponseMessage`
   - content/encoding 및 선택된 format 정보
3. 현재 코드를 `JsonEquipmentMessageCodec`으로 이동하고, XML이면 `XmlEquipmentMessageCodec`을 추가한다.
4. `FileEquipmentTransport`는 byte publish/stable read/lifecycle/retry만 담당하고 codec을 DI로 받게 한다.
5. `EquipmentCommunicationOptions`에 `MessageFormat` enum(`Json`, `Xml`)을 명시적으로 추가한다. **확장자로 format을 암묵 추론하지 않는 것**을 권장한다.
6. Settings UI와 `DesignerOptions`/`UserSettingsStore`에 format 선택을 추가한다.
7. format별 golden request/response, 잘못된 root/envelope/type, encoding, namespace, correlation mismatch 테스트를 추가한다.

논리 XML 예시는 다음과 같이 설계할 수 있지만, 아직 구현된 계약은 아니다. element/attribute, namespace, 숫자 직렬화 규칙은 장비 담당자와 합의 후 이 문서의 계약 버전을 올려야 한다.

```xml
<?xml version="1.0" encoding="utf-8"?>
<request index="103" command="drill">
  <thickness>2.4E-3</thickness>
  <drill_result_path>C:\DrillResults\hole-103.csv</drill_result_path>
</request>
```

```xml
<?xml version="1.0" encoding="utf-8"?>
<response index="103" command="return">
  <drill_result_path>C:\DrillResults\hole-103.csv</drill_result_path>
</response>
```

## 7. 필드 변경 체크리스트

request/response 구조를 바꾸는 작업은 최소한 다음을 모두 확인한다.

- [ ] 이 문서의 계약 버전, 표, 예시, Expression result shape 갱신
- [ ] `EquipmentNodes.cs`의 ParameterBinding 추가/삭제/이름 변경
- [ ] `WorkflowRunner`의 command 매핑 및 `Evaluate*` 결과 dictionary 갱신
- [ ] `ParameterValueValidator`와 `WorkflowValidator` 타입/단위/범위 갱신
- [ ] `FileEquipmentTransport` 또는 분리된 codec의 serialization/parser 갱신
- [ ] `ActionParameterViewModel`의 변수명 우선 label, 설명, 즉시 validation 갱신
- [ ] 한·영 resource 갱신
- [ ] `ExpressionContext` object shape와 `ExpressionCompletionProvider` 후보 갱신
- [ ] workflow persistence의 구/신 문서 migration 또는 schemaVersion 정책 검토
- [ ] request golden test와 response 확장/오류/mismatch test 갱신
- [ ] runner와 Expression 참조 regression test 갱신
- [ ] 로컬 폴더 및 실제 UNC/SMB에서 atomic publish, stable read, lifecycle 재검증

호환성을 깨는 변경(필수 필드 삭제/이름 변경, 타입/단위 변경, envelope 변경)은 계약 버전을 올리고 장비와 앱을 함께 배포해야 한다. 선택적 response 필드 추가처럼 기존 parser가 보존할 수 있는 변경도 알려진 자동완성/기본 테스트 response가 필요하면 catalog와 테스트를 함께 갱신한다.
