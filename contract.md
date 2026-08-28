# DrillFlow 장비 파일 통신 계약

> 상태: 구현 기준 문서 · 계약 버전 5 · 최종 확인 2026-08-28
> 범위: Designer/Live Interaction과 장비 사이의 단일 Action request/response
> 비범위: 워크플로 저장 파일(`*.drillflow.json`), HTTP 및 로컬 Control Flow

이 문서는 장비 데이터 구조를 바꿀 개발자나 에이전트가 가장 먼저 읽어야 하는 source of truth다. 앱은 **메모리 안에서 JSON과 같은 논리 객체**를 사용하지만 중간 `.json` 파일은 만들지 않는다. 장비에 게시하고 장비에서 읽는 파일은 **UTF-8(BOM 없음) XML**이다.

XML은 일반 객체 직렬화 결과가 아니다. Action별 정답 템플릿을 일반 텍스트로 취급하며 정확히 `{{{field_name}}}`인 자리만 XML-safe 값으로 치환하거나 추출한다. 태그·속성·주석·본문에 있는 일반 `field_name` 문자열과 `{{{{field_name}}}}` 같은 근접 표기는 placeholder가 아니다. 실제 장비 XML이 확정되면 [템플릿 폴더](#8-xml-템플릿과-변경-방법)의 12개 Dummy 파일을 실제 양식으로 바꾸고 placeholder 이름과 의미를 유지한다.

## 1. 공통 처리 순서

한 exchange에는 request 하나와 response 하나만 존재한다.

1. 앱이 양의 `Int32` `correlation_id`를 발급한다. 실행 순서가 아니라 correlation 전용 값이다.
2. 앱이 Action의 논리 request 객체를 만든다.
3. Action별 XML request 템플릿의 placeholder를 치환해 설정된 request 파일명으로 원자적으로 게시한다.
4. 장비가 request를 읽고 동작을 완료한다.
5. 장비가 같은 `correlation_id`와 같은 `action`을 가진 XML response를 게시한다.
6. 앱은 안정적으로 기록된 response snapshot을 읽고 해당 Action의 response 템플릿으로 파싱한다. 다른 correlation/action, 깨진 XML, 유효하지 않은 필드는 현재 응답으로 인정하지 않는다.
7. matching response가 확인되면 기본 설정에서 request를 먼저 best-effort 삭제한다.
8. response를 런타임 결과로 materialize한 다음 기본 설정에서 response도 best-effort 삭제한다.
9. `result = 0`이면 성공한다. `result = 1`이면 해당 Action을 `Faulted`로 기록하고 이후 Workflow를 즉시 중단한다.

request/response 삭제가 이미 완료되었거나 공유·권한 문제로 실패해도 유효한 응답 처리와 다음 동작은 계속된다. 삭제 실패는 warning으로 기록한다. 사용자가 Stop하면 response timeout을 기다리지 않고 현재 대기를 취소하며, 앱이 게시한 byte와 현재 request가 정확히 같을 때만 그 request를 회수한다. 이는 장비가 이미 시작한 물리 동작을 취소하지 않는다. 물리 중단이 필요하면 명시적인 `abort` Action을 사용한다.

Correlation ID 제공자는 영구 high-water mark 블록을 예약하므로 정상 발급된 값은 프로세스 재시작 뒤에도 재사용하지 않는다. timeout retry를 켜면 동일한 `correlation_id`와 동일 payload를 재게시한다. 장비가 이를 idempotency key로 처리하지 않으면 물리 동작은 at-least-once일 수 있으므로 retry 기본값은 꺼져 있다.

## 2. 공통 논리 envelope

아래 JSON은 파일이 아니라 메모리 객체와 디버깅 표현이다.

### Request

~~~json
{
  "type": "request",
  "correlation_id": 1,
  "action": "stage"
}
~~~

| 필드 | 타입 | 제약 |
|---|---|---|
| `type` | string | 항상 `request` |
| `correlation_id` | integer | 양의 `Int32`; retry에도 동일 값 사용 |
| `action` | string | `stage`, `camera`, `focus`, `integration`, `live`, `abort` 중 하나 |

### Response

~~~json
{
  "type": "response",
  "correlation_id": 1,
  "action": "stage",
  "result": 0
}
~~~

| 필드 | 타입 | 제약 |
|---|---|---|
| `type` | string | 항상 `response` |
| `correlation_id` | integer | 현재 request와 정확히 같아야 함 |
| `action` | string | 현재 request와 정확히 같아야 함 |
| `result` | integer | `0`: Success, `1`: Fail/Fault |

숫자는 논리적으로 JSON number이며 XML에서는 invariant culture의 과학적 표기법으로 기록한다. 모든 metre 값은 `NaN`, `Infinity`, `-Infinity`가 아닌 유한한 `double`이어야 한다.

## 3. Action 계약

Designer의 장비 Action은 아래 여섯 개뿐이다. 예전 `move`는 `stage`로 schema migration하고, 기존 Expression의 `parameters.move_x/y`, `result/last.stage_x/y`, `results[n].stage_x/y`, `index`, `command` 참조도 각각 `stage_x/y`, `current_stage_x/y`, `correlation_id`, `type`으로 갱신한다. 문자열 literal 안의 텍스트는 바꾸지 않는다. `measure`와 `drill`은 더 이상 지원하지 않는다. Delay/Repeat/Conditional/HTTP는 Designer 내부 Action이며 장비 파일을 만들지 않는다.

### 3.1 Stage Move — `stage`

~~~json
{
  "type": "request",
  "correlation_id": 1,
  "action": "stage",
  "move_mode": "relative",
  "stage_x": 1E-6,
  "stage_y": -2.56E-3
}
~~~

~~~json
{
  "type": "response",
  "correlation_id": 1,
  "action": "stage",
  "result": 0,
  "current_stage_x": -3.2E-6,
  "current_stage_y": 4.12E-4
}
~~~

- `move_mode`: 정확히 `relative` 또는 `absolute`.
- `stage_x`, `stage_y`: metre 기준 유한 signed number. 음수/0/양수 모두 허용한다.
- `absolute`: 장비 home `(0, 0)` 기준 위치. `relative`: 현재 위치 기준 변위.
- `current_stage_x`, `current_stage_y`: 동작 후 home 기준 절대 좌표이며 유한 signed number.

### 3.2 Camera Move — `camera`

Request parameter는 `move_mode`, `camera_x`, `camera_y`이고 Stage와 같은 유한 signed-number 규칙을 사용한다. Response는 `current_camera_x`, `current_camera_y`를 필수로 가진다.

~~~json
{
  "type": "request",
  "correlation_id": 2,
  "action": "camera",
  "move_mode": "absolute",
  "camera_x": -1E-6,
  "camera_y": 8.2E-3
}
~~~

~~~json
{
  "type": "response",
  "correlation_id": 2,
  "action": "camera",
  "result": 0,
  "current_camera_x": -3.2E-9,
  "current_camera_y": 7.62E-6
}
~~~

### 3.3 Auto Focus — `focus`

~~~json
{
  "type": "request",
  "correlation_id": 3,
  "action": "focus",
  "hfw": 3.02E-6,
  "range": 50E-6,
  "steps": 13
}
~~~

~~~json
{
  "type": "response",
  "correlation_id": 3,
  "action": "focus",
  "result": 0,
  "z_to_sharpness_2d": [[1E-7, 500], [1.5E-6, 600], [2.1E-6, 1200]]
}
~~~

- `hfw`: `0 < hfw < 2.4E-3` m.
- `range`: `range > 0` m인 유한 number.
- `steps`: 3보다 큰 정수, 즉 `4..Int32.MaxValue`.
- `z_to_sharpness_2d`: `null`, 빈 배열, 또는 `[z, sharpness]` pair 배열.
- 각 pair는 정확히 두 값이며 Z와 sharpness 모두 유한하고 `> 0`이다. Z의 단위는 metre다.

Dummy XML은 `z_to_sharpness_2d`를 한 placeholder 문자열로 다룬다. 현재 codec은 `null`, `[]`, `[[z,sharpness],...]` 모양의 invariant 숫자 문자열을 사용한다. 실제 장비 표기가 다르면 Focus response 템플릿과 codec의 해당 field adapter를 함께 바꾼다.

### 3.4 Integration — `integration`

~~~json
{
  "type": "request",
  "correlation_id": 4,
  "action": "integration",
  "hfw": 3.02E-6,
  "frame_count": 8,
  "image_path": "C:\\EquipmentImages\\integration-4.bmp"
}
~~~

Response는 같은 이름의 `hfw`, `frame_count`, `image_path`와 공통 `result`를 가진다. 장비는 request와 다른 절대 경로를 반환할 수 있고 앱은 response 경로를 최종 결과로 사용한다.

- `hfw`: `0 < hfw < 2.4E-3` m.
- `frame_count`: 1, 2, 4, 8, 16, 32, 64 중 하나.
- `image_path`: 파일명을 포함한 절대 Windows 로컬 경로 또는 UNC 경로. 빈 값과 상대 경로는 금지한다.

### 3.5 Live frame — `live`

~~~json
{
  "type": "request",
  "correlation_id": 5,
  "action": "live",
  "hfw": 1E-3,
  "frame_count": 1,
  "image_path": "C:\\Exchange\\.drillflow-live\\live-5.bmp"
}
~~~

Response는 공통 envelope/result와 `hfw`, `frame_count`, `image_path`를 가진다.

- `hfw`: `0 < hfw < 2.4E-3` m. UI 기본값은 `1E-3` m(1mm).
- `frame_count`: request와 response 모두 항상 `1`.
- `image_path`: Integration과 같은 절대 파일 경로 규칙.

Live Interaction은 한 번에 하나의 frame만 요청한다. response 이미지를 file handle 없이 메모리에 완전히 decode한 뒤 다음 request를 만든다. 앱이 요청 경로로 만든 `.drillflow-live/<action>-<correlation_id>.bmp`와 response 경로가 정확히 같을 때만 앱 소유 임시 파일로 간주해 사용 후 best-effort 삭제한다. 장비가 다른 경로를 반환하면 장비 소유로 간주해 보존한다. 실제 장비가 별도 내부 프레임 파일을 더 만든다면 그 파일의 정리는 장비가 책임지는 것이 안전하다.

### 3.6 Abort — `abort`

추가 request parameter가 없고 response도 공통 `result`만 가진다.

~~~json
{
  "type": "request",
  "correlation_id": 6,
  "action": "abort"
}
~~~

툴바 Stop과 Abort는 다르다. Stop은 앱의 현재 실행/response 대기를 즉시 취소하고 다음 Action을 시작하지 않지만 장비에 abort를 보내지 않는다. Canvas의 Abort Action만 `action = abort` request를 전송한다.

## 4. Designer validation과 Expression

파라미터 입력은 literal 또는 첫 non-whitespace 문자가 `=`인 Expression이다. 실행 시 평가 결과에도 동일한 validation을 다시 적용한다.

| Action | 입력 필드 |
|---|---|
| Stage | `move_mode`, `stage_x`, `stage_y` |
| Camera | `move_mode`, `camera_x`, `camera_y` |
| Focus | `hfw`, `range`, `steps` |
| Integration | `hfw`, `frame_count`, `image_path` |
| Live | `hfw`, 고정 `frame_count = 1`, `image_path` |
| Abort | 없음 |

각 Action은 `parameters`, `result`, `last`, `results`를 가진 Expression 객체로 노출된다.

~~~text
=stage_1.parameters.stage_x
=stage_1.result.current_stage_x
=focus_1.result.z_to_sharpness_2d[0][0]
=integration_1.last.image_path
~~~

장비 result에는 `type`, `correlation_id`, `action`, `result`와 Action별 response field가 포함된다. Repeat 내부 결과는 iteration마다 `results`에 모두 보존한다. response `result = 1`도 해당 Action 결과로 먼저 보존한 뒤 Workflow를 Fault로 종료한다. 런타임 결과는 새 전체 Run, New/Open, 명시적 전체 초기화 또는 프로세스 종료 전까지 메모리에 남는다.

## 5. Live Interaction의 독점 동작

Live 페이지는 `live`, `integration`, `stage`, `camera`, `focus`를 사용할 수 있고 `abort`는 제공하지 않는다.

- frame streaming 중 다른 장비 동작을 시작하면 active live request를 즉시 취소하고 자신이 게시한 request만 회수한다.
- Stage/Camera/Focus/Integration response가 올 때까지 새 live request를 만들지 않는다.
- 성공 후 사용자가 streaming을 원했던 상태이면 자동으로 live frame을 재개한다.
- 이미지 왼쪽 더블클릭 또는 “해당 위치로 이동”은 이미지 중심과 pixel pitch로 metre 단위 상대 X/Y를 계산해 `stage` request를 보낸다. 계산 좌표는 유한 signed number만 검사한다.
- 마우스 휠 또는 `+`/`-`는 HFW를 절반/2배로 바꾸되 `0 < hfw < 2.4mm` 범위를 지킨다. 이후 frame도 새 HFW를 유지한다.

Designer Workflow 실행과 Live Interaction 장비 동작은 같은 파일명을 공유하므로 동시에 실행하지 않는다.

## 6. 파일 설정과 수명주기

| 설정 | 의미 |
|---|---|
| `ExchangeDirectory` | request/response가 함께 존재하는 절대 로컬 또는 UNC 폴더. 입력한 `/`는 wire image path와 일관되도록 `\`로 정규화 |
| `RequestFileName` | 확장자를 포함한 leaf filename. 기본 `request.xml` |
| `ResponseFileName` | 확장자를 포함한 leaf filename. 기본 `response.xml` |
| `EquipmentRequestLifecycle` | 장비가 request를 읽고 삭제하는지, 다음 request가 덮어쓰는지 |
| `ApplicationRequestLifecycle` | matching response 뒤 앱이 request를 삭제(기본)하거나 보존하는지 |
| `ApplicationResponseLifecycle` | materialize 뒤 앱이 response를 삭제(기본)하거나 보존하는지 |
| `ResponseTimeout` | matching response 대기 시간 |
| `RetryEnabled`, `MaximumRetryCount`, `RetryDelay` | timeout 재시도 정책 |
| `PollingInterval`, `StableReadDelay` | 로컬/UNC stable polling 간격 |

request와 response filename은 서로 달라야 하고 경로가 아닌 leaf name이어야 한다. 게시에는 같은 폴더의 임시 파일과 atomic replace/move를 사용한다. 폴더별 `.drillflow.exchange.lock`과 프로세스 내부 gate가 exchange를 직렬화한다. 이 lock은 개별 exchange 충돌을 막지만 여러 운영자의 장기 장비 소유권까지 보장하지 않으므로 물리 장비/폴더에는 한 active controller만 연결한다.

## 7. Response 테스트

Designer의 “Response 테스트”와 Live의 1회/연속 테스트는 편집 가능한 **논리 JSON 초안**을 보여 주지만 게시 시에는 Action별 XML response 템플릿을 사용한다.

- 현재 request가 있으면 그 `correlation_id`와 `action`을 사용한다.
- Stage/Camera/Focus/Abort는 이미지 생성 UI를 표시하지 않는다.
- Integration/Live는 768×512 모자이크 bitmap을 LocalAppData의 앱 전용 임시 폴더에 만들고 `image_path` 기본값으로 사용한다.
- “다른 이미지”는 메모리 preview와 경로를 함께 교체한다.
- 앱 전용 테스트 이미지는 정상 종료와 다음 정상 시작에서 정리한다.
- 실제 response XML은 설정된 `ResponseFileName`으로 원자적으로 게시한다.

시뮬레이터 초안의 `PayloadFormat` 문자열은 UI 호환을 위해 현재 `JSON`이지만 이는 중간 파일을 뜻하지 않는다.

## 8. XML 템플릿과 변경 방법

Dummy 템플릿은 다음 위치에 Embedded Resource로 포함된다.

~~~text
src/DrillFlow.Infrastructure/Communication/Templates/
├─ Stage/request.xml, response.xml
├─ Camera/request.xml, response.xml
├─ Focus/request.xml, response.xml
├─ Integration/request.xml, response.xml
├─ Live/request.xml, response.xml
└─ Abort/request.xml, response.xml
~~~

템플릿과 실제 request/response wire payload는 각각 UTF-8 기준 최대 **4 MiB**다. 앱은 이보다 큰 response 파일을 배열로 할당하기 전에 무시하며, 유효한 response가 제한 시간 안에 오지 않은 것과 동일하게 timeout 처리한다. 템플릿 파일은 UTF-8 **BOM 없이** 저장해야 하며 BOM/U+FEFF가 있으면 앱 시작 시 계약 오류로 즉시 거부한다.

현재 예시는 사람이 바로 찾을 수 있는 placeholder를 사용한다.

~~~xml
<?xml version="1.0" encoding="utf-8"?>
<request>
  <type>{{{type}}}</type>
  <correlation_id>{{{correlation_id}}}</correlation_id>
  <action>{{{action}}}</action>
  <move_mode>{{{move_mode}}}</move_mode>
  <stage_x>{{{stage_x}}}</stage_x>
  <stage_y>{{{stage_y}}}</stage_y>
</request>
~~~

실제 양식으로 교체할 때:

1. Action/방향에 맞는 소스 파일 하나를 바꾸고 앱을 다시 빌드한다. 템플릿은 assembly Embedded Resource이므로 빌드 산출물 옆의 XML을 수정해도 적용되지 않는다.
2. `correlation_id`와 Action별 동적 request/response 필드는 정확한 `{{{field_name}}}` 토큰으로 적어도 한 번 남긴다. response의 `result`도 필수다. `type`과 `action`은 토큰으로 둘 수도 있고 해당 Action/방향 템플릿의 고정 문자열로 표현할 수도 있다.
3. 같은 논리 값을 XML 여러 위치에 넣어야 하면 동일 placeholder를 반복해도 된다. 렌더링 시 모두 같은 값으로 치환하며, response/request 파싱 시 반복 위치의 XML-unescape 결과가 하나라도 다르면 전체 payload를 거부한다. 값 경계를 알 수 없는 인접 placeholder는 허용하지 않으며, 한 템플릿은 재귀 깊이와 처리량을 제한하기 위해 placeholder 출현을 최대 256개까지 허용한다.
4. 임의 placeholder나 공백·대문자가 섞인 잘못된 토큰을 추가하지 않는다. 일반 `correlation_id` 같은 텍스트는 개수와 관계없이 그대로 유지된다.
5. 고정 태그·namespace·속성·주석은 실제 장비 정답지와 같게 작성하고, UTF-8 BOM 없이 4 MiB 미만으로 저장한다. 파서는 XML DOM을 해석하지 않고 placeholder 사이의 고정 텍스트를 비교한다. 단, response는 요소와 요소 사이의 서식 전용 공백·탭·CR/LF 및 문서 바깥 공백을 무시하므로 들여쓴 템플릿과 장비의 동등한 한 줄 XML이 호환된다. placeholder 값 내부의 의미 있는 공백은 제거하지 않는다. request 파싱에는 이 완화를 적용하지 않는다. 고정 구분 문자열이 값 안에도 나타나면 뒤쪽 경계까지 탐색하되, 논리 계약까지 통과하는 해석이 정확히 하나일 때만 수락하고 탐색·비교·문자열 생성 예산을 넘는 비정상 payload는 거부한다.
6. 문자 필드는 codec이 XML escaping한다. 숫자는 invariant 대문자 scientific notation 및 최소 두 자리 지수부(`1E-06`, `-2.56E-03`)로 치환한다. 입력 파서는 기존 `1E-6`과 장비 표기 `1E-06`을 모두 허용한다. 삽입한 값 안의 `{{{...}}}` 문자열은 다시 치환하지 않는다.
7. `InfrastructureXmlTemplateEquipmentMessageCodecTests`에 실제 fixture round-trip, 반복값 일치 및 잘못된 응답 rejection 사례를 추가한다.
8. 하나의 논리 값을 서로 다른 여러 field로 분해·결합해야 한다면 `XmlTemplateEquipmentMessageCodec`의 field adapter와 이 문서를 함께 바꾼다.

## 9. 코드 변경 지도

| 변경 대상 | 주요 위치 |
|---|---|
| 논리 request/response | `DrillFlow.Application/Communication/Equipment*Message.cs` |
| Action 이름/parameter/result 모델 | `DrillFlow.Core/Workflows/EquipmentNodes.cs` |
| 입력 validation | `DrillFlow.Core/Validation/ParameterValueValidator.cs`, `WorkflowValidator.cs` |
| XML placeholder 렌더/파싱 | `DrillFlow.Infrastructure/Communication/XmlTemplateEquipmentMessageCodec.cs` |
| 12개 XML 정답지 | `DrillFlow.Infrastructure/Communication/Templates/` |
| atomic 파일 exchange/lifecycle/retry | `DrillFlow.Infrastructure/Communication/FileEquipmentTransport.cs` |
| 테스트 response 생성 | `JsonEquipmentResponseSimulator.cs`, Desktop dialog/service |
| Workflow request/result mapping | `DrillFlow.Application/Execution/WorkflowRunner.cs` |
| Live request와 임시 이미지 경로 | `DrillFlow.Application/LiveInteraction/` |
| Toolbox/Inspector/result UI | `DrillFlow.Desktop/ViewModels/` 및 `Views/` |
| workflow schema migration | `JsonWorkflowDocumentSerializer.cs` |

장비 계약을 바꿀 때는 템플릿만 보고 끝내지 말고 모델, validator, runner, simulator, Expression completion/result 표시, Live 사용 여부, serialization migration, 한·영 리소스와 관련 테스트를 함께 검토한다.
