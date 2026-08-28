# DrillFlow Designer 제품·구현 가이드

> 기준일: 2026-08-28
>
> 대상: 사용자, 유지보수 개발자, 후속 구현 에이전트
>
> 관련 문서: 장비 메시지 계약은 `contract.md`, 배포 절차는 `docs/deployment.md`

## 1. 제품 목적과 확정된 범위

DrillFlow Designer는 철판 위 드릴 장비의 단위 동작과 디자이너 내부 로직을 위에서 아래 순서로 조합하고, 저장·검증·실행하는 WPF 워크플로 편집기다. 장비 동작은 지정 폴더의 XML request 파일로 전달하고 같은 폴더의 XML response를 양의 Int32 `correlation_id`와 동일한 `action`으로 연결한다. 실행 결과는 현재 Run 동안 Action 객체의 `result`/`results`로 유지되어 이후 Action의 Expression에서 참조할 수 있다.

확정된 제품 결정은 다음과 같다.

- Windows 7 SP1 이상과 .NET Framework 4.8(`net48`)을 지원한다. 현대 .NET 8 런타임은 Windows 7을 지원하지 않으므로 사용하지 않는다.
- WPF-UI FluentWindow와 Compact Navigation 형태를 사용한다. 디자이너, 라이브 인터랙션, 설정의 세 페이지를 제공한다.
- 중앙 편집기는 물리적인 X/Y 도면이 아니라 **실행 순서**를 정의한다.
- 장비 response의 `result`는 `0`(성공) 또는 `1`(실패)이며, 실패 response는 Action별 성공 필드 없이 공통 필드만 있어도 된다. 실패 결과를 Action에 보존한 뒤 Workflow를 `Faulted`로 중단한다.
- `correlation_id`는 실행 순서가 아니라 요청과 응답을 연결하는 양의 Int32 식별자다.
- Repeat의 내부 결과는 마지막 값만 덮지 않고 모든 iteration을 보존한다.
- 실행 이력 archive는 만들지 않고 현재 결과 세션만 보유한다. “이 Action만 실행”은 같은 세션에 누적하고, 새 전체 Workflow Run을 시작하면 이전 결과를 비운다.
- UI는 한국어와 영어를 제공한다.

## 2. 화면 구조

### App shell

`MainWindow`는 WPF-UI `FluentWindow`다. `ExtendsContentIntoTitleBar=True`로 사용자 정의 TitleBar 영역까지 확장하되 최소화·최대화·닫기 버튼을 계속 표시한다. 왼쪽 Compact Navigation에는 디자이너, 라이브 인터랙션, 설정 페이지가 있고, 페이지 콘텐츠는 Navigation Pane을 제외한 전 영역으로 늘어난다. MainPage의 `CanContentScroll=False`와 함께 NavigationView presenter의 외부 DynamicScrollViewer를 명시적으로 끄므로 툴바와 상태 표시줄은 고정된다.

### Designer 페이지

상단 Command Bar에는 문서 새로 만들기·열기·저장·다른 이름으로 저장, Undo/Redo, 유효성 검사, Run/Continue/Step/Stop, 브레이크포인트 전환/전체 제거, 모든 결과 접기/모든 결과 초기화, Response 테스트, 장비 통신 폴더 열기, Canvas 확대·축소·100%, View Reset이 있다. 각 명령은 의미를 나타내는 Fluent Symbol 아이콘과 툴팁을 가진다. View Reset은 좌·중·우 폭, 왼쪽 상·하 목록 높이, 각 영역의 스크롤 위치, Inspector 첫 탭과 Canvas 100% 배율을 함께 기본값으로 되돌린다.

본문은 다음 세 영역으로 나뉘며 각 영역만 독립적으로 스크롤한다.

1. 왼쪽: 장비 동작과 디자이너 동작을 구분한 아이템 목록
2. 중앙: 시작과 끝 사이의 세로 실행 순서 편집기
3. 오른쪽: WPF-UI Fluent 스타일 탭으로 전환하는 선택 Action의 파라미터와 현재 Run 결과

Action 앞의 붉은 점은 브레이크포인트다. 시작 pill 아래부터 끝 pill 위까지만 이어지는 수직 실행선이 Action의 위→아래 실행 순서를 표시하며 Canvas 전체를 가르는 선은 사용하지 않는다. 실행선은 가장 낮은 layer에 있고, 불투명 Action 카드나 빈 워크플로 안내 박스가 놓인 구간에서는 그 뒤로 완전히 가려진다. 시작·끝·Action 사이에는 삽입 여백을 확보하고, 루트와 Repeat·조건 분기 내부를 포함해 Action을 넣을 수 있는 모든 위치에는 작은 `+`가 항상 보인다. drag/hover 시 같은 위치가 accent bar로 강조된다. 파라미터와 결과 레이블은 Expression에서 사용하는 변수명을 먼저 보여주고, 괄호 안에 사용자 설명을 표시한다.

### Settings 페이지

설정 페이지는 다음 값을 저장하고 실행 중인 singleton Options 객체에도 반영한다.

- 로컬 또는 UNC 통신 폴더
- Live request 이미지용 로컬 또는 UNC 공유 폴더(`LiveImageFolder`; 비어 있으면 통신 폴더 아래 `.drillflow-live` 사용)
- 확장자를 포함한 request/response 파일명
- 장비가 request를 삭제하는 방식 또는 유지·덮어쓰기 방식
- matching response 이후 앱이 request를 삭제하는 방식(기본값) 또는 유지·덮어쓰기 방식
- 앱이 response를 읽고 삭제하는 방식 또는 유지·덮어쓰기 방식
- 초 단위 소수로 입력하는 response timeout·polling 간격, request 첫 게시 전 대기(기본 0.1초)
- retry 사용 여부·횟수·간격
- 한국어/영어/시스템 언어
- 시스템/라이트/다크 앱 테마

통신 폴더와 Live 이미지 공유 폴더 행에는 각각 폴더 선택과 현재 입력 경로 열기를 분리한 버튼이 있다. .NET Framework 4.8에는 현대 WPF의 `Microsoft.Win32.OpenFolderDialog`가 없으므로, Windows 7부터 제공되는 Windows Shell `IFileOpenDialog`를 폴더 선택 모드로 사용한다. 이 선택기는 MainWindow를 owner로 가지며 로컬·UNC 초기 경로를 지원한다. Desktop의 `LiveImageFolder`는 application option `EquipmentCommunication.LiveImageDirectory`에 적용된다. 값이 비어 있거나 이전 `settings.json`에 없으면 실행 시 `<ExchangeDirectory>\.drillflow-live`로 해석하므로 기존 설치의 동작을 유지한다.

테마는 선택 즉시 WPF-UI 컨트롤과 앱 전용 surface/text brush에 함께 적용된다. 이미 렌더링된 WPF brush는 frozen 상태일 수 있으므로 기존 객체의 색을 수정하지 않고 테마마다 새 불변 brush 리소스로 교체한다. 따라서 열린 페이지와 드래그·삽입 강조 상태도 Light/Dark 전환을 즉시 따라간다. 시스템 모드는 OS 변경 이벤트를 따라가며 Windows 7에서는 Mica 없이 Light fallback을 사용한다. 설정은 사용자 LocalAppData의 `settings.json`에 저장되며, 다음 실행·파일 교환·Response 테스트 Dialog가 최신 값을 사용한다.

### Live Interaction 페이지

페이지에 들어가면 `action: "live"` request를 한 번에 하나씩 보내고, 같은 `correlation_id`와 `action`을 가진 성공 response의 `image_path` 파일을 완전히 메모리에 읽은 다음 화면을 갱신한다. 각 request에는 `hfw`, 고정 `frame_count: 1`, `<resolved LiveImageDirectory>\live-<correlation_id>.bmp` 형식의 앱 소유 이미지 경로가 들어가며, decode가 끝난 뒤에만 다음 Live request를 만든다. 설정 폴더는 로컬 또는 UNC가 가능하고, 비어 있으면 `<ExchangeDirectory>\.drillflow-live`로 fallback한다. 이 설정은 Live Action에만 적용되며 Integration의 기존 correlation별 요청 경로는 바꾸지 않는다. response가 요청 경로와 다른 경로를 돌려주면 장비 소유 파일로 간주해 보존한다. 같은 경로라면 소비 후 best-effort로 삭제하되 실패가 다음 frame을 막지 않는다. 하나의 장비·통신 폴더에는 active controller 하나만 허용하고, 일반 Workflow Action의 결과 이미지는 현재 결과 세션에서 표시하는 동안 불변이어야 한다. 신규 설치의 polling 기본값은 0.05초, 새 Exchange의 request 게시 전 대기는 0.1초이며 설정에서 소수 초 단위로 조절할 수 있다. 이 공통 게시 전 대기는 Live frame마다 적용되므로 0.1초일 때 장비·파일 처리 시간을 제외해도 최대 갱신률은 약 10fps다. WIC 메타데이터 확인과 미리보기 decode는 UI Dispatcher가 아니라 앱 전용 background STA queue에서 수행하고 결과를 Freeze해 전달한다. Action 카드와 Inspector의 실행 결과 이미지도 같은 stable read/STA decode 경로를 사용하며 표시 실패가 workflow 성공을 바꾸지는 않는다. 취소된 대기 작업은 decode하지 않으며, 64MiB 파일·축당 16,384 pixel·총 6,400만 pixel을 넘는 비정상 입력은 명확한 오류로 차단한다. response 이후 이미지 I/O에는 현재 response timeout(최소 1초)을 별도 예산으로 적용한다. 기본 사후 정리 정책은 matching response마다 완료된 Live request를 삭제해 고빈도 요청 파일이 남지 않게 한다. 라이브 정지나 페이지 이동은 현재 response 대기를 즉시 취소하고 Designer 잠금을 해제한다. transport는 게시 byte가 그대로인 request만 background에서 제한 시간 내 best-effort로 지우며 timeout까지 UI를 붙잡지 않는다. 앱 종료 시에는 이미 예약된 정리 task만 기존 2초 deadline의 남은 시간 동안 drain해 정상 로컬 request가 프로세스와 함께 남지 않게 하되, 중단 불가능한 UNC 호출 때문에 deadline을 넘기지는 않는다. 일시적 실패나 image timeout 시 마지막 정상 화면을 유지하면서 500ms~5초 범위의 backoff로 재시도한다.

모든 `live` request에는 metre 기준의 필수 `hfw`가 포함된다. UI 기본값은 편집 가능한 `1 mm`이며 `0 < hfw < 2.4E-3 m`인 유한값만 허용한다. 이미지 위 마우스 휠 또는 입력 컨트롤 밖의 `+`/`-` 키와 버튼으로 범위 안에서 HFW를 절반(확대)/2배(축소) 조절한다. 유효한 Pixel Pitch는 같은 비율로 함께 보정된다. 유효 HFW가 바뀌면 이전 값으로 게시된 Live request를 즉시 취소·회수하고 새 값으로 재요청한다. 텍스트 편집은 300ms debounce하고 휠/키/버튼은 즉시 적용한다. 새 HFW 응답 이미지를 decode하기 전에는 이전 이미지를 보정 대기로 표시하고 이미지 지점 이동을 잠가 stale 이미지·새 Pixel Pitch 조합의 오이동을 막는다.

라이브 페이지의 오른쪽 파라미터·장비 상태·동작·결과 영역은 `CanContentScroll=False`인 독립 수직 ScrollViewer를 사용한다. 현재 상태, 최근 목표, Stage, Camera, 프레임 배율, Pixel Pitch, Focus, Lens, ACB, 촬영 결과를 포함한 모든 항목은 WPF-UI expandable card이며 모두 접힌 상태로 시작한다. 상단에는 설정된 통신 폴더 열기, 현재 active `live` request 하나에 768×512 모자이크 XML 응답을 만드는 “1프레임 테스트 생성”, 이후 각 Live `correlation_id`에 새 모자이크 응답을 만드는 “연속 response 결과 생성” 토글이 있다. 테스트 생성기는 같은 correlation의 response가 이미 있으면 실제 장비 결과일 수 있으므로 덮어쓰지 않는다. 연속 모드의 직전 이미지는 다음 request를 관찰한 뒤 해제하여 LocalAppData 파일 수를 제한하고, 남은 앱 소유 이미지는 종료 또는 다음 시작 때 정리한다.

이미지를 더블클릭하거나 오른쪽 클릭 메뉴에서 “해당 위치로 이동”을 선택하면 동일한 좌표 mapper가 원본 pixel 크기와 X/Y DPI를 함께 사용해 WPF `Stretch=Uniform`의 실제 표시 영역과 letterbox를 계산한 뒤 원본 pixel 좌표로 되돌린다. 따라서 X/Y DPI가 다른 이미지도 화면 지점과 이동 지점이 일치한다. 이미지 중심을 이동량 `(0, 0)`으로 하고, 사용자가 입력한 pixel pitch와 m/mm/µm/nm 단위를 metre로 환산해 `action: "stage"`, `move_mode: "relative"`, `stage_x`, `stage_y` request를 만든다. 기본 축은 오른쪽 +X, 아래쪽 +Y이며 설치 방향에 맞춰 각 축을 반전할 수 있다. 계산 결과는 NaN/Infinity가 아닌 유한 signed number인지 검사하며 별도의 ±이동거리 제한은 두지 않는다.

Stage/Camera 이동, Focus, Integration 촬영, Lens 변경 또는 ACB를 시작하면 현재 Live exchange를 즉시 취소하고, transport가 그 exchange가 게시한 정확한 request를 회수해 gate를 놓을 때까지 기다린 뒤 interactive Action을 하나만 보낸다. 이미지 지점 Stage 이동은 matching 성공 response 뒤 페이지가 활성 상태이면 이전 Stop 여부와 관계없이 Live streaming을 자동 재개하지만, 실패·취소·페이지 이탈 때는 오류 확인을 위해 멈춘 상태를 유지한다. Integration, Lens, ACB는 이전 재생 상태를 복원하며, Lens 성공 뒤에는 새 Live frame이 decode될 때까지 기존 이미지의 이동 보정을 오래된 상태로 표시한다. Lens request는 `lens1`, `lens2`, `no_change` 중 하나이고 성공 response는 실제 `lens1` 또는 `lens2` 상태를 갱신한다. ACB는 현재 유효한 HFW를 request에 넣는다. Live Interaction에는 OM과 Abort를 제공하지 않는다. 페이지를 이탈하면 진행 중인 앱 소유 Stage/Camera/Focus/Integration/Lens/ACB도 취소하고 Live를 재개하지 않는다. `촬영`은 `action: "integration"`과 선택한 1/2/4/8/16/32/64 `frame_count`로 고화질 `image_path`를 받은 즉시 원본 바이트 그대로 전용 LocalAppData 스냅샷에 확보한다. 스냅샷 확보까지만 현재 response timeout의 image I/O 예산을 적용하고 저장 Dialog에서 사용자가 결정하는 시간에는 timer를 적용하지 않는다. 미리보기와 Windows 저장 Dialog의 로컬 복사는 모두 이 동일 스냅샷을 사용하므로 장비가 원본을 덮어쓰거나 삭제해도 결과가 바뀌지 않는다. 작업이 끝난 스냅샷은 즉시 지우며 종료/다음 시작에서도 남은 전용 파일을 정리한다. 스트리밍·장비 인터랙션 중에는 일반 워크플로 실행, Response 테스트, 통신 설정 변경을 막고, 반대로 워크플로 실행 중에는 라이브 명령을 막는다.

## 3. Action 모델

모든 Action은 안정적인 GUID `Id`, Expression 식별자 `Key`, 표시 이름, 활성화 여부, 브레이크포인트 여부를 가진다. `Key`는 대소문자를 구분하지 않는 고유 별칭이며 저장된 워크플로에서 유지된다.

### 장비 동작

장비 동작만 request 파일을 생성하고 correlated response를 기다린다.

| Action | `action` | 입력 | response 추가 필드 |
| --- | --- | --- | --- |
| Stage Move | `stage` | `move_mode`, `stage_x`, `stage_y` | `current_stage_x`, `current_stage_y` |
| Camera Move | `camera` | `move_mode`, `camera_x`, `camera_y` | `current_camera_x`, `current_camera_y` |
| Auto Focus | `focus` | `hfw`, `range`, `steps` | `z_to_sharpness_2d` |
| Integration | `integration` | `hfw`, `frame_count`, `image_path` | `hfw`, `frame_count`, `image_path` |
| Live | `live` | `hfw`, 고정 `frame_count = 1`, `image_path` | `hfw`, 고정 `frame_count = 1`, `image_path` |
| Abort | `abort` | 없음 | 없음 |
| OM | `om` | `image_path` | `image_path` |
| Lens Change | `lens` | `lens_mode` | `current_lens_mode` |
| Auto Contrast/Brightness | `acb` | `hfw` | 없음 |

Stage와 Camera의 relative·absolute 좌표는 모두 음수·0·양수를 허용하며, absolute는 각 장비의 home `(0, 0)`을 기준으로 한다. Focus 결과의 `z_to_sharpness_2d`는 `null`, 빈 배열 또는 양의 유한 `[z, sharpness]` pair 배열이다. Integration/Live/OM의 `image_path`는 장비가 저장한 이미지의 절대 로컬/UNC 파일 경로이며 장비가 request와 다른 최종 경로를 반환할 수도 있다. Lens request의 `lens_mode`는 `lens1`, `lens2`, `no_change` 중 하나이고, 성공 response의 실제 `current_lens_mode`는 `lens1` 또는 `lens2`다. ACB의 `hfw`는 다른 HFW와 같은 범위를 사용한다. Abort request는 공통 `type`, `correlation_id`, `action`만, response는 여기에 `result`만 더한 정확한 shape다.

모든 장비 response는 공통으로 `type: "response"`, matching `correlation_id`, matching `action`, `result`를 가진다. `result = 0`은 성공이며 Action별 response 필드가 모두 유효해야 한다. `result = 1`은 실패이며 성공 전용 필드를 생략할 수 있다. 장비가 실패에도 성공 shape를 보내면 읽을 수 있지만 성공 전용 값은 검증하거나 결과로 사용하지 않는다. 공통 실패 결과는 현재 결과 세션에 보존한 뒤 Workflow를 `Faulted`로 중단한다.

### 디자이너 동작

디자이너 동작은 장비 request/response 파일 교환을 사용하지 않는다.

- Delay: 지정한 `0..29999 ms` 동안 취소 가능한 로컬 대기
- Repeat: 내부 Action 배열을 `1..Int32.MaxValue`회 실행하고 모든 iteration 결과 보존
- If / Else if / Else: 위에서부터 첫 true 분기를 실행하고, 없으면 Else 실행
- HTTP: 외부 HTTP GET/POST를 호출하고 정형·비정형 응답을 현재 Run의 동적 결과로 제공

Repeat와 Conditional의 컨테이너 자체 결과도 correlation ID `0`의 로컬 결과로 기록한다. 로컬 제어 Action은 장비 request/response 파일을 만들거나 기다리지 않는다.

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

- `stage_x`, `stage_y`, `camera_x`, `camera_y`: 제한 없는 유한 signed metre 값
- `hfw`: `0 < hfw < 2.4E-3 m`
- Focus `range`: 0보다 큰 유한 metre 값, `steps`: `4..Int32.MaxValue`
- Integration `frame_count`: `1`, `2`, `4`, `8`, `16`, `32`, `64` 중 하나
- Live `frame_count`: 정확히 `1`
- Integration/Live `image_path`: 파일명을 포함한 절대 Windows 로컬 또는 UNC 경로
- OM `image_path`: 파일명을 포함한 절대 Windows 로컬 또는 UNC 경로
- Lens `lens_mode`: `lens1`, `lens2`, `no_change` 중 하나
- Repeat `count`: `1..Int32.MaxValue`
- Delay: `0..29999 ms`
- 수치는 NaN이나 Infinity가 아닌 유한값

`=`로 시작하는 값은 임의 C#이 아닌 sandboxed Expression으로 평가한다.

```text
=stage_1.result.current_stage_y
=camera_1.parameters.camera_x + 2.5E-4
=focus_1.result.z_to_sharpness_2d[0][0]
=repeat_1.results[0].count
```

Action 객체의 접근 형태는 다음과 같다.

```text
action_key.parameters.field
action_key.result.field
action_key.results[iteration_index].field
action_key.results.last.field
action_key.last.field
```

미래 Action, 실행될 수 없는 분기, 순환 참조는 저장/실행 전 검증에서 거부한다. Expression TextBox에서 `Ctrl+Space`를 누르면 현재 token과 caret 위치를 분석해 접근 가능한 이전 Action 및 `parameters`/`result` 멤버를 ComboBox 팝업으로 보여준다. Enter/Tab은 후보를 입력하고 Esc는 닫는다. 아홉 장비 Action의 계약상 response 필드와 런타임에서 발견한 HTTP 동적 필드도 자동완성 후보에 합쳐진다.

## 5. 실행 엔진

Runner 상태는 `Idle → Validating → Running`으로 진행하며 실행 중 `Paused` 또는 `Stopping`, 종료 시 `Completed`·`Stopped`·`Faulted`가 된다.

- Run: 문서 전체를 deep snapshot한 뒤 검증하고 현재 Run 결과를 초기화해 실행한다.
- Canvas의 `시작` 표시는 Run과 같은 실제 실행 버튼이며, `끝` 표시는 toolbar Stop과 같은 로컬 정지 버튼이다.
- 이 Action만 실행: 전체 문서를 expression context로 유지한 동일 ID snapshot에서 선택 subtree만 실행하며, 해당 subtree의 authored breakpoint는 무시한다. 기존 current-run 결과 세션을 이어 쓰므로 앞서 실행한 다른 Action의 결과와 decoded image가 유지되고 expression에서도 계속 참조할 수 있다. 결과 세션이 아직 없을 때만 새 세션을 만든다.
- Breakpoint: Action 실행 직전에 `Paused`가 된다.
- Continue: Command Bar 또는 `F10`으로 다음 breakpoint까지 계속한다.
- Step: Command Bar에서 한 실행 단위를 완료한 뒤 다음 Action 앞에서 다시 멈춘다.
- Stop 첫 클릭: 현재 Delay/HTTP/파일 응답 대기 또는 breakpoint pause를 즉시 취소하고 로컬 실행을 `Stopped`로 끝낸다. 이미 게시한 request는 같은 exchange lock 아래 앱이 게시한 byte와 현재 파일이 일치할 때만 제한 시간 내 best-effort로 삭제한다.
- Stop 재클릭: 첫 클릭과 같은 idempotent 정지 요청이다. 어떤 경우에도 toolbar·Canvas 끝·Context Menu Stop은 Abort Action을 게시하지 않는다.
- Abort Action: 명시적인 장비 `action: "abort"` request/response를 수행한 뒤 배열을 종료한다.

Breakpoint, Stop, 활성화 여부와 실행 상태는 runner 이벤트로 카드 ViewModel에 반영된다. Action이 `Running`인 동안, 특히 request 게시 후 matching response를 기다리는 동안 카드 아래에 indeterminate loading indicator와 명시적인 응답 대기 상태를 표시한다. 정상 실행에서는 matching response Task가 완료되기 전 Action을 완료하거나 다음 Action으로 진행하지 않으며, 설정한 timeout과 retry를 모두 소진하면 `Faulted`가 된다. 선택한 현재 장비 Action이 응답 대기 중이면 Context Menu에도 즉시 Stop이 나타난다. Stop이 response보다 먼저 이기면 runner는 파일 I/O 종료를 기다리지 않고 `Stopped`가 되며, transport가 ownership-safe request 정리를 이어간다. 취소를 무시하는 사용자 HTTP executor나 body read도 동일하게 background 관찰로 분리되어 runner 종료를 막지 않으며, 늦은 진단에는 URL 비밀값이나 예외 원문을 남기지 않는다. 완료 후 카드에는 기본으로 펼쳐진 가장 최근 response 필드를 표시하고, Integration/Live의 유효한 `image_path`는 수평 가운데 정렬된 이미지로 함께 표시한다. `+`/`-`는 텍스트 편집 중이 아닐 때 선택 Action 이미지를 기본 100%에서 25%씩 50~300% 범위로 조절한다. 경로가 없거나 파일 읽기·decode가 실패하면 카드와 Inspector에 원인을 구분해 안내하고, Inspector의 별도 이미지 탭과 Windows 기본 연결 앱 열기 기능도 제공한다. UI는 실행 중 편집을 잠그므로 검증 이후 물리 Action이 바뀌지 않는다.

## 6. 파일 통신과 Response 테스트

XML 인코딩은 템플릿·request·response 모두 공통 strict UTF-8 입력 정책을 사용한다. BOM 없음과 정확히 하나의 선두 BOM을 허용해 메모리에서 정규화하고, 앱 출력은 UTF-8 BOM 없음으로 고정한다. 발생했던 ACB/Lens 템플릿 BOM 문제의 원인, 허용/거부 범위, writer-close 감지와 장비 측 atomic 게시 절차는 [`xml-encoding-and-file-handshake.md`](xml-encoding-and-file-handshake.md)를 따른다.

request와 response는 같은 통신 폴더의 서로 다른 설정 파일명을 사용하며 기본값은 `request.xml`, `response.xml`이다. 워크플로·Dialog·runner는 JSON과 같은 논리 message 객체를 다루지만 중간 JSON 파일은 만들지 않는다. 앱이 생성하는 request와 테스트 response는 UTF-8(BOM 없음) XML이다. 장비 response는 UTF-8 BOM 유무를 모두 허용하며, 표준 3-byte BOM이 있으면 장비 파일을 다시 쓰지 않고 메모리에서 제외한 뒤 파싱한다. 아홉 장비 Action의 request/정상-response 템플릿 18개와, 성공 전용 response 필드가 있는 일곱 Action의 공통 `failure-response.xml`을 합친 25개 템플릿을 일반 텍스트로 취급해 정확한 `{{{field_name}}}` placeholder만 치환하거나 추출한다. 일반 필드명 문자열은 그대로 두고, 같은 placeholder가 반복되면 모두 같은 값으로 치환하며 파싱 시에도 모든 위치의 값이 같아야 한다. `type`과 `action`은 placeholder 대신 Action/방향별 고정 텍스트로 표현할 수 있다. 독립적인 payload 판별에서는 둘 이상의 Action 템플릿과 동시에 일치하면 거부하지만, 실제 파일 exchange는 이미 대기 중인 request의 Action을 알고 있으므로 해당 Action의 response 템플릿만 적용한다. 현재 request와 동일한 `correlation_id`와 `action`, `0|1`인 `result`를 요구한다. `result = 0`이면 Action별 필수 필드를 모두 검증하고, `result = 1`이면 공통 필드만으로 받아들이며 성공 전용 필드를 사용하지 않는다. 안정적으로 읽힌 파일이 이 조건을 통과하지 못하면 동일한 응답 대기 구간에서 같은 바이트에 대한 경고를 한 번만 기록해 단순 미감지와 계약 불일치를 구분한다.

파일 게시에는 같은 디렉터리의 temp 파일과 atomic replace/move를 사용한다. `.drillflow.exchange.lock`을 `FileShare.None`으로 열어 로컬 프로세스와 SMB 클라이언트의 전체 exchange를 직렬화한다. 설정한 request lifecycle의 게시 전 조건(장비 삭제 방식이면 기존 request 소멸)이 충족된 뒤 quiet interval만큼 기다리고, 기본 `DeleteAfterRead` 모드에서는 남아 있는 이전 response를 정리한 다음 첫 request를 게시한다. 이 대기는 취소 가능하며 취소되면 request/temp 파일을 만들지 않고, matching response 대기는 실제 게시 뒤에 온전한 timeout 예산으로 새로 시작한다. response 감지는 로컬/SMB 변경 알림에 의존하지 않고 polling을 기준으로 하며, 파일 크기와 수정 시간이 안정된 뒤 읽고 일시적인 share violation을 재시도한다. 기본 모드에서는 안정적으로 읽힌 파일을 현재 request의 Action과 `correlation_id`로 판별하므로 같은 내용의 테스트 파일을 다시 붙여넣어도 누락되지 않는다. `RetainUntilOverwritten` 모드에서는 다른 PC나 복원된 ID 상태가 남긴 응답까지 방어하기 위해 게시 전 response 바이트를 baseline으로 보존하며, 장비가 새 correlation을 포함한 payload로 실제 교체해야 한다.

request에는 서로 독립적인 두 lifecycle이 있다. 장비 lifecycle은 장비가 읽은 파일을 즉시 삭제하는지 유지하는지를 나타내고, 앱 lifecycle은 matching response를 받은 뒤 남은 request를 정리할지를 나타낸다. 앱의 기본값은 `DeleteAfterResponse`다. 기본 handshake는 stable XML response snapshot 파싱과 correlation/action 검증, 완료 request 삭제 시도, 메모리에 확보한 결과 materialize, response 삭제 시도 순서다. request 정리는 response 파일 정리와 결과 반환보다 항상 먼저 시도한다. 파일이 이미 없으면 정리된 것으로 보며, 권한·공유 문제 등으로 삭제하지 못해도 warning만 기록하고 정상 response와 다음 실행을 계속한다. `RetainUntilOverwritten`을 선택하면 앱은 request를 남기고 이후 요청이 원자적으로 교체한다. response도 `DeleteAfterRead`가 기본이며 설정에서 `RetainUntilOverwritten`으로 바꿀 수 있다.

timeout retry는 기본으로 꺼져 있다. 켜면 같은 XML payload와 `correlation_id`를 다시 게시하므로 장비가 correlation ID를 내구성 있는 idempotency key로 처리하지 않는 한 물리 동작은 at-least-once다. 게시 전 quiet interval은 새 Exchange의 최초 게시에 한 번만 적용하며 retry는 별도의 retry interval을 사용한다.

Response 테스트는 선택한 **장비 Action**에만 제공된다. WPF-UI ContentDialog를 열 때마다 최신 설정의 `ExchangeDirectory + ResponseFileName`을 기본 경로로 표시하고, 감지한 request의 `correlation_id`/`action`과 Action별 기본 response 필드를 편집 가능한 논리 JSON 초안으로 제안한다. 이 초안은 UI 표현일 뿐 게시 시 해당 Action의 XML response 템플릿으로 렌더링된다. Stage/Camera/Focus/Abort/Lens/ACB에는 이미지 생성 UI가 없고, Integration/Live/OM에서 768×512 모자이크 PNG를 LocalAppData의 앱 전용 임시 폴더에 자동 생성하고 frozen bitmap을 메모리에 유지해 Dialog에서 바로 미리 본다. `다른 이미지`는 사용자가 편집한 필드를 보존한 채 이미지와 `image_path`만 함께 교체한다. 생성 중에는 게시를 막아 화면의 이미지와 실제 response 경로가 어긋나지 않게 한다. 생성한 파일은 앱 종료 시 삭제하고 비정상 종료 잔여물도 다음 시작 때 정리한다. 게시 시 Action별 스키마를 검증하고 원자적으로 XML response를 생성한다. 읽기 전용 경로·결과 TextBox는 명시적인 OneWay binding을 사용해 WPF가 getter-only 속성에 값을 되쓰지 않는다.

세부 장비 계약과 XML 템플릿 교체 지점은 루트 `contract.md`가 source of truth다. HTTP Action은 장비 XML envelope를 따르지 않으며 이 계약의 범위 밖이다.

## 7. 편집 이벤트와 사용자 피드백

| 사용자 이벤트 | 앱 반응 |
| --- | --- |
| 아이템 더블클릭 | 루트 실행 순서 끝에 Action 추가 |
| 아이템을 삽입 bar/빈 Canvas에 drag | 해당 컬렉션의 표시된 삽입 위치에 새 Action 생성 |
| Action 클릭 | 단일 선택, Ctrl+클릭은 선택 토글, Shift+클릭은 anchor부터 범위 선택 |
| Action 카드 헤더 drag | 선택된 최상위 Action 묶음을 표시 순서대로 같은 레벨 또는 허용된 중첩 컬렉션으로 이동 |
| Action drag 중 Ctrl | 선택 묶음을 새 GUID와 고유 별칭으로 deep copy하고 묶음 내부 Expression 참조도 새 별칭으로 변경 |
| 삽입 위치의 `+` MouseOver | 놓기 가능한 위치를 accent horizontal bar로 표시 |
| 삽입 bar MouseDown | 붙여넣기 target을 저장하고 짧게 pulse한 뒤 bar를 숨김 |
| Ctrl+V | 마지막으로 클릭한 target이 유효하면 그 위치, 아니면 주 선택 Action 다음 위치에 선택 묶음을 순서대로 붙여넣기 |
| Action 우클릭 | 이미 선택된 Action이면 묶음을 유지하고, 선택 밖의 Action이면 단일 선택한 뒤 Context Menu command 갱신 |
| Context Menu | 이 Action만 실행 / Response 테스트, 복사·잘라내기·붙여넣기·삭제, 활성화, 브레이크포인트를 separator로 분류 |
| Ctrl+C/Ctrl+X/Delete | 선택 묶음의 최상위 Action들을 순서대로 복사/잘라내기/삭제 |
| Ctrl+A / Esc | 모든 Action을 선택 / 선택을 모두 해제 |
| Ctrl++ / Ctrl+- / Ctrl+0 | Canvas를 10% 단위로 확대·축소 / 100%로 복원(60~160%) |
| View Reset | splitter 크기, 독립 스크롤, Inspector 탭과 Canvas 배율을 초기 상태로 복원 |
| F10 | Breakpoint에서 Paused 상태일 때 Continue 실행 |
| F9 | 주 선택 Action의 브레이크포인트 전환 |
| 파라미터 편집 시작 | Undo snapshot 생성, 입력마다 유효성/Expression 표시 갱신 |
| Ctrl+Space | 현재 범위의 Expression completion popup 표시 |
| 통신 폴더 버튼 | 최신 설정 경로를 Windows Explorer로 열고 상태 표시 |
| Window 닫기 | 실행 중이면 Stop을 요청하고, 미저장 변경은 저장/폐기/취소 확인 |
| Live 페이지 진입 / 이탈 | 순차 `live` loop 시작 / 앱 소유 활성 요청을 취소하고 재개 없이 정지 |
| Live Start / Stop | 이미지 연속 갱신 시작 / 활성 Live exchange 취소 및 소유 request 회수 |
| Live 이미지 더블클릭·오른쪽 클릭 이동 | 동일 mapper로 원본 중심 대비 pixel 이동량을 계산해 상대 Stage 이동, 완료 후 Live 재개 |
| Live 촬영 | Live 일시정지, 고화질 Integration 응답 수신, 로컬 저장 Dialog, 이전 재생 상태 복원 |

구조 변경은 실행 전 serialized snapshot을 Undo stack에 넣고 Redo stack을 비운다. 붙여넣기·Ctrl-drag·재정렬·중첩 이동도 같은 경로를 사용해 저장 모델과 화면 컬렉션을 함께 갱신한다. 자기 자신의 하위 Repeat/Conditional 컬렉션으로 이동하는 순환 구조는 허용하지 않는다.

## 8. 저장과 현재 Run 결과

워크플로는 schema version을 가진 `*.drillflow.json`으로 저장한다. Node type, GUID, Key, 파라미터의 literal/Expression 원문, 중첩 body/branch, enabled와 breakpoint를 보존한다. 런타임 result, request/response payload, 실행 중 위치는 저장하지 않는다.

각 실행 결과에는 Action ID/Key, correlation ID, Repeat iteration path, 완료 시각, 동적 값 dictionary가 있다. Repeat 내부 Action은 iteration마다 별도 결과를 추가한다. 결과와 이미지는 선택 변경, 파라미터 편집, 저장, drag/reorder, Undo/Redo, 동일 ID의 잘라내기→붙여넣기 및 다른 Action의 단독 실행 뒤에도 Action ID 기준으로 메모리에 유지된다. 일반 복사/붙여넣기는 새 Action ID를 만들므로 런타임 결과를 복제하지 않는다. 카드별 결과는 기본 펼침이며 “모든 결과 접기”는 표시만 접고, “모든 결과 초기화”만 UI 결과·decoded image·core result store를 함께 비운다. 결과 필드는 singleton 언어 이벤트를 직접 구독하지 않아 초기화 뒤 값과 큰 JSON 문자열이 남지 않는다. 새 전체 Run, 새 문서/다른 workflow 열기, 명시적인 전체 결과 초기화, 앱 종료가 이전 current-run 결과의 수명 경계다. 런타임 결과는 workflow 파일에 저장하지 않으며 crash 이후 불확실한 물리 동작을 자동 재개하지 않는다.

## 9. 애플리케이션 아키텍처

### 부팅과 공통 서비스

`App.xaml.cs`는 Generic Host를 구성하고 Microsoft.Extensions Configuration, DI, Options를 연결한다. 창을 직접 `new`하지 않고 Host service provider에서 singleton Window/Page/ViewModel을 가져온다. Serilog bootstrap logger는 Host 완성 전 오류를 Debug와 rolling file에 기록하고, Host 이후 정식 logger로 교체된다. 시작 실패·두 번째 인스턴스·미저장 변경·일반 메시지는 Fluent ContentDialog로 통일하며, singleton dialog gate가 여러 ContentDialog가 동시에 열리지 않도록 직렬화한다. 파일·폴더 선택창도 현재 MainWindow를 owner로 사용하고 Shell COM 자원은 호출마다 해제한다. `ApplicationThemeService`는 저장된 테마를 시작 시 복원하고 런타임 변경과 시스템 테마 이벤트를 UI Dispatcher에서 적용한다.

HTTP 실행 로그는 method, query를 제거한 URL path, timeout, status만 남긴다. 인증 헤더·request body·response body와 URL query는 rolling log에 기록하지 않는다.

### 계층

| 프로젝트 | 책임 |
| --- | --- |
| `DrillFlow.Core` | Workflow 모델, 안전한 Expression 값/파서, validation, 현재 Run result store |
| `DrillFlow.Application` | Runner orchestration, 장비/HTTP/저장소 abstraction, 실행 이벤트 계약 |
| `DrillFlow.Infrastructure` | JSON workflow 저장, XML-template 장비 codec, 파일 transport, atomic publisher, correlation ID, HTTP client 구현 |
| `DrillFlow.Desktop` | WPF-UI View, ViewModel, drag/drop·키보드 behavior, Dialog와 Windows shell 연동 |
| `DrillFlow.Tests` | Core/Application/Infrastructure 회귀·통합 테스트 |

의존성 방향은 Desktop과 Infrastructure가 Application/Core을 향하며, Core는 UI·파일·HTTP를 모른다. Runner는 interface만 받아 테스트에서 deterministic fake transport/client로 교체할 수 있다.

### 주요 이벤트 흐름

```text
WPF routed event / ICommand
  ├─ Designer → MainPageViewModel / WorkflowExecutionFacade
  │    → WorkflowRunner
  │       ├─ 장비 Action → IEquipmentFileTransport → request/response 폴더
  │       ├─ HTTP Action → HTTP abstraction → 원격 endpoint
  │       └─ Control Flow → 로컬 delay/loop/branch
  │    → RunResultStore + 실행 이벤트 → Action 카드/Inspector
  └─ Live → 원본 image 좌표 해석 / LiveInteractionPageViewModel
       → ILiveInteractionSession → 같은 transport/correlation/폴더
       → image 메모리 decode + stage 상태 → Live 화면
```

code-behind는 hit testing, mouse 좌표, drag payload, animation처럼 WPF visual tree에 종속된 일만 담당한다. 저장/복사/이동/실행 판단은 ViewModel/Core 서비스로 보낸다.

## 10. 변경 요청 반영 이력

초기 구현에서 Fluent shell, Compact Navigation, 3-pane designer, 장비 Action 기본 구조, Delay/Repeat/Conditional, Generic Host/DI/Options/Serilog, 파일 lifecycle·timeout/retry, 저장/불러오기, 현재 Run result와 Expression 참조를 구성했다.

후속 수정에서 다음을 반영했다.

- TitleBar의 최소화·최대화·닫기 버튼과 설정 페이지 stretch
- 첫 Stop부터 현재 로컬 작업·응답 대기를 즉시 취소하고 소유권을 확인한 게시 request 정리
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
- Ctrl/Shift 다중 선택과 순서 보존 그룹 복사·잘라내기·붙여넣기·삭제·드래그
- 선택 묶음 내부 Action 간 Expression 참조를 복사본의 새 별칭으로 일괄 재작성
- Inspector의 파라미터/결과 탭 전환과 Fluent ContentDialog 기반 시작·오류·미저장 확인 통일
- MainPage 외부 스크롤 제거와 좌측 목록·Canvas·Inspector의 독립 스크롤
- WPF-UI Fluent TabControl/TabItem 스타일과 시작~끝 중앙 실행선
- 설정에서 즉시 전환하고 다음 실행에 복원되는 시스템/라이트/다크 테마
- 좌·중·우 및 왼쪽 상·하 splitter를 한 번에 복원하는 View Reset
- 시작·끝 표시 사이에만 존재하는 실행선과 모든 유효 삽입 위치의 작은 `+`
- Canvas 60~160% 확대·축소와 `Ctrl+A` 전체 선택·`Esc` 선택 해제
- 이미 열린 창·스타일·Expression 자동완성 Popup까지 즉시 갱신하는 런타임 테마 전환
- `F10` Continue 단축키, 넓어진 삽입 여백과 선을 완전히 가리는 불투명 Action/빈 안내 surface
- Windows 7/net48 호환 Explorer형 통신 폴더 선택기와 현재 입력 폴더 열기 버튼
- 이미 렌더링되어 frozen된 brush 때문에 부분 전환되던 Light/Dark 런타임 오류 수정
- 장비 Action을 Stage/Camera/Focus/Integration/Live/Abort/OM/Lens/ACB 아홉 종류와 Action별 response 결과로 갱신
- Action 카드의 최신 결과·이미지 썸네일·실행중 indicator와 Inspector 이미지 전용 탭
- 결과 기본 펼침·전체 접기/초기화, 편집/Undo/Redo 간 in-memory 결과 유지, 선택 이미지 `+`/`-` 배율 조절
- Response 테스트의 768×512 임의 LocalAppData 이미지 생성 및 종료/다음 시작 정리
- 순차 `live` Action으로 갱신하는 Live Interaction 카메라 화면
- pixel pitch·단위·letterbox·축 반전을 반영한 이미지 더블클릭 상대 이동
- Stage/Camera/Focus/Integration 동안 Live 자동 일시정지/재개와 Integration 이미지의 안전한 로컬 저장
- Live 전체 세션과 일반 Workflow 실행·Response 테스트·통신 설정의 상호 배제
- matching response 뒤 request를 기본 best-effort 삭제하고 삭제 실패를 비치명 warning으로 격리하는 앱 사후 정리 lifecycle
- Action별 결과를 선택 실행 사이에도 누적하고 새 전체 Run/New/Open/명시적 초기화에서만 비우는 결과 세션
- Live 오른쪽 독립 스크롤, 통신 폴더 열기, 1프레임/연속 테스트 response 생성과 즉시 Stop cleanup
- 논리 message와 UTF-8 XML wire를 분리한 Action별 template codec, workflow schema v2와 v1 Move→Stage migration
- `result = 1` 공통-only 실패 response와 Action별 failure template, OM/Lens/ACB Designer Action
- OM/Abort를 제외한 Live Lens/ACB 독점 동작과 처음에는 모두 접힌 expandable control card

## 11. 확장 시 체크리스트

장비 request/response 구조를 바꿀 때는 먼저 `contract.md`를 갱신하고 message model, runner mapping, XML template/field adapter, simulator, Inspector field catalog, 테스트를 함께 변경한다. 실제 장비 XML 정답지가 바뀌면 파일 확장자나 template text만 임의로 고치지 말고 placeholder 집합, codec adapter, fixture round-trip과 invalid-response 테스트를 함께 갱신한다.

새 Action을 추가할 때는 다음을 함께 확인한다.

1. `WorkflowNodeKind`와 concrete node/기본 파라미터
2. deep copy와 serializer type discriminator
3. Core validation과 Expression visibility
4. Runner의 실행 분기 및 결과 shape
5. DI abstraction/implementation
6. Toolbox/아이콘/한·영 리소스/Inspector
7. completion의 parameter/result member catalog
8. 저장 round-trip, runner, cancellation, dynamic result 테스트

배포 전에는 Release 빌드와 전체 테스트뿐 아니라 Windows 7 SP1 x86/x64 VM에서 Fluent fallback, 런타임 Light/Dark/System 전환, DPI, 한·영 리소스, 실제 SMB lifecycle, drag/drop, breakpoint/Step/Stop, HTTP TLS 호환성을 확인한다.
