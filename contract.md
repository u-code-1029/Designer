# DrillFlow 장비 파일 통신 계약

> 상태: 구현 기준 문서 · 계약 버전 4 · 최종 확인 2026-08-27
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
5. 앱은 안정적으로 기록된 response를 메모리에 확보하고, `index`가 일치하며 `command`가 정확히 `return`이고 필수 필드가 유효한 파일만 현재 요청의 응답으로 인정한다. correlation을 확인하려면 response를 읽어야 하므로 이 stable snapshot 확보는 다음 파일 정리보다 먼저 일어난다.
6. `ApplicationRequestLifecycle = DeleteAfterResponse`이면 앱은 matching response를 감지한 직후 처리 완료된 request 파일을 먼저 best-effort로 삭제한다. 이미 없거나 삭제할 수 없어도 정상 response 처리는 실패시키지 않는다.
7. 앱은 확보해 둔 response snapshot을 런타임 결과로 materialize한 뒤, `ApplicationResponseLifecycle = DeleteAfterRead`이면 response 파일을 best-effort로 삭제하고 materialize한 결과를 호출자에게 반환한다. 따라서 기본 순서는 **matching response 감지 → request 삭제 시도 → 결과 materialize → response 삭제 시도 → 결과 반환**이며, 두 삭제는 각각 설정에서 retain/overwrite 방식으로 바꿀 수 있다.
8. response의 확장 필드는 현재 Run의 해당 Action 결과 또는 Live Interaction의 최신 상태로 보존된다. “이 Action만 실행”은 현재 Run 결과 세션을 이어 쓰며, 새 전체 Workflow Run·New/Open·명시적 전체 결과 초기화에서만 이전 결과 세션을 비운다.
9. 사용자가 실행을 정지하면 현재 response 대기를 즉시 취소한다. request가 이미 게시되었다면 앱은 process/SMB exchange lock을 유지한 채 **게시한 byte와 현재 request가 정확히 같은 경우에만** 해당 파일을 best-effort로 삭제한다. 이미 없으면 성공으로 보고, 내용이 다르면 다른 주체의 파일로 간주해 보존하며, 잠금·권한 문제는 제한 시간 동안 재시도한 뒤 경고만 남긴다. 이 취소 정리는 `ApplicationRequestLifecycle` 설정과 무관하게 적용된다. 일반 Stop/HFW 전환은 UI를 먼저 반환하지만, 앱 종료 시에는 이미 예약된 정리 작업을 그 작업의 원래 2초 deadline 중 남은 시간까지만 join해 프로세스 종료가 정상 로컬 정리보다 앞서지 않게 한다. 중단할 수 없는 UNC/SMB OS 호출이 deadline을 넘으면 종료를 계속한다.

현재 프로토콜에는 별도의 완료 신호 파일이나 상태 비트가 없다. 같은 `index`를 가진 유효한 response가 안정적으로 게시되는 것이 해당 request의 완료 신호다.

현재 장비 동작은 모두 성공한다고 가정한다. 오류 코드, 성공 여부, 오류 응답 및 보상 동작은 계약에 없다. 툴바의 Stop은 장비 명령이 아니며 실행기를 즉시 멈춘다. request 삭제도 아직 장비가 읽지 않은 파일의 회수일 뿐, 이미 시작한 물리 동작을 취소하지 않는다. Canvas의 명시적인 Abort Action만 `command: "abort"` request를 전송한다.

Correlation ID 저장소는 마지막 발급값이 아니라 **영구 예약된 high-water mark**를 기록한다. 각 프로바이더는 프로세스 간 파일 잠금 아래 최대 256개의 양의 `Int32` ID 블록을 원자적으로 예약한 뒤 메모리에서 소비한다. 따라서 정상 발급된 ID는 재시작이나 여러 프로바이더 인스턴스 사이에서도 재사용되지 않지만, 비정상 종료 후 미사용 예약분은 건너뛰며 여러 인스턴스에서 관찰한 ID 순서에는 간격이나 교차가 생길 수 있다. `index`는 실행 순서 번호가 아니라 correlation 전용 값이다. `Int32.MaxValue`까지 소진되면 stale response를 다시 받아들이는 wrap/reset 대신 명시적으로 실패한다.

### 공통 request envelope

| 필드 | JSON 타입 | 필수 | 의미/제약 |
|---|---:|:---:|---|
| `index` | integer | 예 | 양의 `Int32` correlation ID. retry에도 **동일한 값**과 동일 payload를 사용한다. |
| `command` | string | 예 | 현재 `move`, `measure`, `drill`, `abort`, `frame`, `capture` 중 하나. 대소문자는 현재 소문자 고정이다. |
| 그 밖의 필드 | command별 | 조건부 | 아래 command 표를 따른다. `index`, `command`는 파라미터 이름으로 사용할 수 없다. |

### 공통 response envelope

| 필드 | JSON 타입 | 필수 | 의미/제약 |
|---|---:|:---:|---|
| `index` | integer | 예 | 처리한 request의 `index`와 정확히 같아야 한다. 현재 요청과 다르면 stale/다른 응답으로 무시한다. |
| `command` | string | 예 | 정확히 소문자 `return`이어야 한다. |
| `stage_x` | number | 예 | 응답 시점 stage의 home `(0, 0)` 기준 절대 X 좌표. 단위는 metre이며 유한 `double`이어야 한다. |
| `stage_y` | number | 예 | 응답 시점 stage의 home `(0, 0)` 기준 절대 Y 좌표. 단위는 metre이며 유한 `double`이어야 한다. |
| `image_path` | string | 조건부 | 장비가 저장한 결과 이미지의 경로. `frame`/`capture` 응답에는 필수이고 그 밖의 응답에서는 선택적이다. 존재할 때 빈 문자열일 수 없으며 앱에서 접근 가능한 절대 로컬 또는 UNC 경로여야 한다. |
| 임의 확장 필드 | JSON 값 | 아니요 | 향후 필드를 허용한다. string/number/integer/boolean/null/array/object를 손실 없이 런타임 결과에 보존한다. |

`image_path`는 촬영 결과가 없는 Action에서는 생략할 수 있다. 단, `frame`과 `capture`는 이미지 획득 자체가 동작 목적이므로 이 필드가 없으면 해당 Live 요청은 실패로 처리한다. 경로가 존재하지만 파일이 없거나 읽을 수 없는 경우에도 response 자체는 수신된 것으로 보존하되 UI는 이전 정상 이미지를 유지하고 오류 상태를 표시한다. response 필드는 위의 알려진 필드로 폐쇄되어 있지 않다. `EquipmentResponseMessage.Properties`는 알 수 없는 필드를 의도적으로 보존하며 Expression에서 접근할 수 있다. 현재 parser는 `index`와 `command`의 JSON property name을 정확한 소문자로 기대한다.

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

### 2.5 Frame — Live 저지연 1프레임

| 필드 | JSON 타입 | 필수 | 범위/의미 |
|---|---:|:---:|---|
| `index` | integer | 예 | 공통 envelope |
| `command` | string | 예 | `frame` |
| `hfw` | number | 예 | metre 기준 Horizontal Field Width. 0보다 큰 유한값이며, 작을수록 높은 배율 |

Live Interaction 페이지가 동영상과 유사한 미리보기를 만들기 위해 한 번에 한 요청씩 순차적으로 사용한다. 장비는 요청된 `hfw`로 프레임 파일 기록을 완료한 뒤 `image_path`가 포함된 공통 response를 게시해야 한다.

```json
{
  "index": 105,
  "command": "frame",
  "hfw": 1E-2
}
```

### 2.6 Capture — 고화질 정지 이미지 촬영

추가 파라미터가 없다. Live 미리보기보다 시간이 더 걸리더라도 저장용 고화질 정지 이미지를 만든다. 장비는 이미지 파일 기록을 완료한 뒤 `image_path`가 포함된 공통 response를 게시해야 한다.

```json
{
  "index": 106,
  "command": "capture"
}
```

## 3. response 예시와 런타임 표현

현재 최소 예시는 다음과 같다.

```json
{
  "index": 103,
  "command": "return",
  "stage_x": 3E-3,
  "stage_y": -2.56E-4
}
```

### Live Interaction 순서와 좌표 계약

Live 페이지는 `frame response 수신 → image_path 파일을 완전히 메모리에 로드하고 파일 handle 해제 → 화면 갱신 → 다음 frame request` 순서를 지킨다. 장비가 매 프레임 같은 이미지 경로를 덮어쓸 수 있으므로 이미지 로드 전에 다음 request를 게시하지 않는다.

Designer의 HFW 기본값은 편집 가능한 `10 mm`이며 metre 환산 결과가 0보다 큰 유한값일 때만 frame request에 사용한다. 이미지 위 마우스 휠 또는 편집 컨트롤 밖의 `+`/`-` 키는 HFW를 각각 절반(확대)/2배(축소)로 변경한다. 유효한 Pixel Pitch가 입력되어 있으면 같은 HFW 비율로 자동 보정한다. HFW가 바뀌면 이전 HFW로 이미 게시된 frame exchange를 즉시 취소·회수하고 최신 HFW로 다시 요청한다. 화면에 남은 이전 프레임은 새 HFW 프레임이 수신·디코딩될 때까지 보정 대기 상태이며, 이 동안 더블클릭/오른쪽 클릭 Stage 이동을 차단한다.

하나의 물리 장비와 `ExchangeDirectory`에는 동시에 **하나의 active controller만** 연결해야 한다. `.drillflow.exchange.lock`은 개별 exchange가 섞이는 것만 막으며 여러 운전자가 장비를 번갈아 움직이는 장기 session ownership을 대신하지 않는다. 일반 Workflow Action의 `image_path`는 현재 Run이 끝날 때까지 correlation별로 고유하거나 내용이 변하지 않아야 한다. Live `frame`만 위의 순차 읽기 경계 안에서 같은 경로 덮어쓰기를 허용한다.

이미지 더블클릭 또는 오른쪽 클릭 메뉴의 “해당 위치로 이동”은 마지막 정상 이미지의 **중심**을 카메라 기준점으로 사용한다. 두 입력 경로는 동일한 좌표 mapper를 사용한다. 원본 이미지의 오른쪽이 기본 `+X`, 아래쪽이 기본 `+Y`이며 장비의 카메라 설치 방향에 따라 각 축을 UI에서 반전할 수 있다. WPF가 BitmapSource의 pixel 크기뿐 아니라 X/Y DPI로 자연 DIP 크기를 정한다는 점까지 반영해 `Stretch=Uniform` letterbox를 제거하며, letterbox 영역은 이동 지점이 아니다. 원본 pixel 좌표 차이에 사용자가 입력한 pixel pitch를 metre로 환산해 곱하고, 기존 `move`의 `relative` request로 전송한다. 계산된 각 축 이동량도 `-0.5 m < value < 0.5 m` 범위를 만족해야 하며 범위를 넘으면 clamp하지 않고 요청을 차단한다.

이동 또는 고화질 촬영을 시작할 때는 새 frame 예약을 멈추고 현재 frame exchange를 즉시 취소한다. transport가 자신이 게시한 correlation·command·payload와 현재 request가 모두 일치할 때만 그 request를 회수하며, 이 cleanup이 소유한 exchange gate가 풀린 뒤에만 `move` 또는 `capture` 하나를 게시한다. 따라서 frame과 interactive request는 파일 경로에서 겹치지 않는다. 이미지 지점 이동은 matching move response가 성공한 뒤 페이지가 활성 상태이면 이전 Stop 상태와 관계없이 frame 루프를 시작하고, 실패·취소·페이지 이탈 때는 재개하지 않는다. Capture는 직전 Live 상태가 재생 중이었을 때 후속 처리가 끝난 뒤 frame 루프를 복원한다. Stop은 활성 frame을, 페이지 이탈과 앱 종료는 앱이 소유한 활성 frame/move/capture를 취소하지만 장비가 이미 읽어 실행한 명령을 되돌리는 `abort`는 게시하지 않는다.

`capture` response를 받으면 앱은 장비 소유 `image_path`를 bounded stable-read로 전용 LocalAppData 스냅샷에 원본 바이트 그대로 확보한다. WIC 검증에 실패하면 장비 경로에서 다시 스냅샷을 얻는 과정까지 bounded retry하며, 미리보기와 사용자 저장은 검증된 동일 스냅샷만 사용한다. 장비가 이후 원본을 교체·삭제해도 저장 내용은 변하지 않는다. UNC 파일 open 자체는 Windows 네트워크 공급자에 의해 OS timeout까지 지연될 수 있어 UI thread 밖에서 수행한다. 취소 시 앱은 그 worker를 더 기다리지 않아 UI 종료를 진행하지만, 이미 시작된 open 자체가 즉시 중단된다고 보장하지 않으며 뒤늦게 남은 staging 파일은 worker 완료 또는 다음 앱 시작에서 정리한다.

이미지와 확장 필드가 있는 응답도 허용된다.

```json
{
  "index": 103,
  "command": "return",
  "stage_x": 3E-3,
  "stage_y": -2.56E-4,
  "image_path": "C:\\DrillImages\\hole-103.png",
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

각 result에는 response 확장 필드 외에 `index`와 `iteration_path`가 노출된다. 장비 Action에는 `command: "return"`도 노출된다. 예: `=drill_1.last.image_path`, `=move_1.result.stage_x`, `=measure_1.result.stage_y`.

### 디자이너의 테스트 Response 기본값

MainPage의 **Response 테스트** ContentDialog는 commissioning 편의를 위해 선택한 장비 Action별 편집 가능한 JSON 초안을 만든다.

| Action | 초안의 결과 필드 |
|---|---|
| Move / Measure / Drill / Abort | `stage_x: 0`, `stage_y: 0`, 선택적 `image_path` |

ContentDialog를 열 때 앱은 768×512 모자이크 PNG를 사용자 LocalAppData의 전용 임시 폴더에 만들고 `image_path` 초깃값으로 제안하며, 같은 bitmap을 메모리에 유지해 미리보기로 표시한다. `다른 이미지`를 선택하면 사용자가 편집한 나머지 JSON 필드는 그대로 두고 새 이미지와 `image_path`만 함께 바꾼다. 게시할 때는 화면에 보이는 생성 이미지 경로를 다시 동기화하므로 미리보기와 실제 테스트 response가 항상 같은 이미지를 가리킨다. 앱이 만든 임시 이미지는 프로세스 종료 시 모두 삭제하며, 다음 정상 시작에서도 이전 비정상 종료가 남긴 전용 임시 파일을 정리한다.

이 표는 **시뮬레이터의 편집 시작값**이며 response의 확장 필드를 폐쇄하지 않는다. 실제 response의 추가 필드는 계속 보존되고, 한 번 관찰된 필드는 현재 Run에서 Ctrl+Space 자동완성 후보에도 합쳐진다. 테스트 게시 시 현재 request 파일이 있으면 그 `index`를 우선 사용한다. `EquipmentDeletesAfterRead` 모드에서는 response `index`와 같은 request만 장비처럼 삭제한 후 response를 원자적으로 게시한다.

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
| `ApplicationRequestLifecycle = DeleteAfterResponse` | matching response를 정상 수신한 뒤 앱이 해당 request를 best-effort로 삭제(기본값). 파일이 이미 없거나 권한·공유 등으로 삭제에 실패해도 warning만 기록하고 response 반환 및 다음 처리를 계속한다. |
| `ApplicationRequestLifecycle = RetainUntilOverwritten` | response 수신 뒤에도 앱이 request를 남기며 다음 게시가 원자적으로 교체하도록 허용 |
| `ApplicationResponseLifecycle = DeleteAfterRead` | 정상 matching response를 읽은 후 앱이 삭제(기본 UX 선택) |
| `ApplicationResponseLifecycle = RetainUntilOverwritten` | response를 남기고 다음 장비 응답이 교체하도록 허용 |
| `ResponseTimeout` | matching response 대기 시간 및 관련 bounded I/O 대기 기준 |
| `RetryEnabled`, `MaximumRetryCount`, `RetryDelay` | timeout 시 동일 `index` 및 payload를 재게시. MaximumRetryCount는 최초 시도 외 추가 횟수 |
| `PollingInterval`, `StableReadDelay` | 로컬/SMB 모두 polling을 source of truth로 사용하고 크기/수정시간이 안정된 파일만 읽음. 신규 설치의 polling 기본값은 Live 응답성을 위해 50ms이며 설정에서 조절 가능 |

동일 폴더의 전체 exchange는 `.drillflow.exchange.lock`을 `FileShare.None`으로 열어 프로세스/워크스테이션 간 직렬화한다. 이미 존재하는 response와 byte-for-byte 같은 내용은 새 응답으로 인정하지 않는다.

`EquipmentRequestLifecycle`은 장비가 request를 언제 제거하는지에 관한 handshake 정책이고, `ApplicationRequestLifecycle`은 matching response가 확인된 뒤 앱이 남은 request를 정리하는 정책이다. 두 설정은 독립적이다. 기본 조합은 장비가 request를 유지·덮어쓰는 환경에서도 라이브 `frame` 요청 파일이 계속 남지 않도록 앱이 응답마다 request를 삭제한다. 사후 삭제는 이미 완료된 물리 동작의 성공 여부를 바꾸지 않으므로 실패를 실행 오류로 승격하지 않는다. 남은 파일은 다음 원자적 게시에서 교체할 수 있다.

## 5. 코드의 현재 Source of Truth

### 논리 message와 실행 매핑

- `src/DrillFlow.Application/Communication/EquipmentRequestMessage.cs`
  `index`, `command`, 동적 request parameters 및 예약 필드 검사
- `src/DrillFlow.Application/Communication/EquipmentResponseMessage.cs`
  `index`, `command`, 임의 response properties 보존
- `src/DrillFlow.Application/Execution/WorkflowRunner.cs`
  Action→command 매핑, `EvaluateMove/Measure/Drill`, response→런타임 result, control flow result
- `src/DrillFlow.Application/LiveInteraction/ILiveInteractionSession.cs`,
  `LiveInteractionSession.cs`, `LiveInteractionProtocol.cs`
  `frame`/`capture`/상대 `move` logical contract, correlation 검증, Live 호출 직렬화
- `src/DrillFlow.Application/LiveInteraction/LiveImageCoordinateMapper.cs`
  원본 pixel·Uniform letterbox·중심·pixel pitch·축 방향의 상대 이동 계산
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
- `src/DrillFlow.Application/Communication/ApplicationRequestFileLifecycle.cs`,
  `src/DrillFlow.Application/Communication/EquipmentCommunicationOptions.cs` 및
  `src/DrillFlow.Infrastructure/Communication/EquipmentCommunicationOptionsValidator.cs`
  폴더/파일명/timeout, 장비 request lifecycle, 앱 request 정리 lifecycle, response lifecycle 설정 계약
- `src/DrillFlow.Infrastructure/Persistence/PersistentCorrelationIdProvider.cs`
  원자적 high-water 블록 예약, 재사용 없는 양의 Int32 correlation ID

### UI 및 테스트

- `src/DrillFlow.Desktop/ViewModels/ActionParameterViewModel.cs`
  입력 즉시 검증과 변수명/설명 표시
- `src/DrillFlow.Desktop/Views/MainPage.xaml`
  authored parameter 및 현재 Run result 표시
- `src/DrillFlow.Desktop/ViewModels/LiveInteractionPageViewModel.cs` 및
  `src/DrillFlow.Desktop/Views/LiveInteractionPage.xaml(.cs)`
  순차 frame loop, 이미지 로드, 이동/촬영 전환, Live 화면과 pointer hit testing
- `src/DrillFlow.Desktop/Services/ILiveCaptureSnapshotStore.cs`
  장비 capture 파일의 stable 원본-byte 스냅샷, 취소, 전용 LocalAppData 수명/잔여물 정리
- `src/DrillFlow.Desktop/Services/ILiveImageDecoder.cs`
  전용 STA decode queue, frozen preview, 취소된 queued work 생략, image byte/pixel 안전 상한
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
- `tests/DrillFlow.Tests/ApplicationLiveInteractionSessionTests.cs`,
  `ApplicationLiveImageCoordinateMapperTests.cs`, `InfrastructureLiveInteractionSessionTests.cs`
  Live payload/correlation/직렬화, 좌표·letterbox 변환, workflow와의 transport 상호 배제
- `tests/DrillFlow.Tests/DesktopLiveCaptureSnapshotStoreTests.cs`
  capture 원본 교체 이후에도 보존되는 snapshot byte와 안정성 판정
- `tests/DrillFlow.Tests/DesktopLiveImageDecoderTests.cs`
  STA WIC decode, byte/pixel 상한, queued 취소 및 decoder 종료
- `tests/DrillFlow.Tests/DesktopRuntimeResultImageTests.cs`
  Action 결과 이미지의 공용 stable-read/STA decode 사용 및 표시 실패 비치명 처리

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
  <stage_x>3E-3</stage_x>
  <stage_y>-2.56E-4</stage_y>
  <image_path>C:\DrillImages\hole-103.png</image_path>
</response>
```

## 7. 필드 변경 체크리스트

request/response 구조를 바꾸는 작업은 최소한 다음을 모두 확인한다.

- [ ] 이 문서의 계약 버전, 표, 예시, Expression result shape 갱신
- [ ] `EquipmentNodes.cs`의 ParameterBinding 추가/삭제/이름 변경
- [ ] `WorkflowRunner`의 command 매핑 및 `Evaluate*` 결과 dictionary 갱신
- [ ] `ParameterValueValidator`와 `WorkflowValidator` 타입/단위/범위 갱신
- [ ] `FileEquipmentTransport` 또는 분리된 codec의 serialization/parser 갱신
- [ ] `LiveInteractionProtocol`/`LiveInteractionSession`의 frame·capture·move payload와 필수 응답 갱신
- [ ] `ActionParameterViewModel`의 변수명 우선 label, 설명, 즉시 validation 갱신
- [ ] 한·영 resource 갱신
- [ ] `ExpressionContext` object shape와 `ExpressionCompletionProvider` 후보 갱신
- [ ] workflow persistence의 구/신 문서 migration 또는 schemaVersion 정책 검토
- [ ] request golden test와 response 확장/오류/mismatch test 갱신
- [ ] runner와 Expression 참조 regression test 갱신
- [ ] Live frame decode, 원본 좌표 변환, 이동/촬영 pause-resume 회귀 테스트 갱신
- [ ] 로컬 폴더 및 실제 UNC/SMB에서 atomic publish, stable read, lifecycle 재검증

호환성을 깨는 변경(필수 필드 삭제/이름 변경, 타입/단위 변경, envelope 변경)은 계약 버전을 올리고 장비와 앱을 함께 배포해야 한다. 선택적 response 필드 추가처럼 기존 parser가 보존할 수 있는 변경도 알려진 자동완성/기본 테스트 response가 필요하면 catalog와 테스트를 함께 갱신한다.
