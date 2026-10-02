# 구현 검토와 유지보수 변경

## 검토 기준과 판단

DrillFlow의 목적은 Windows용 Workflow 편집기에서 장비 Action을 조합하고,
correlation ID로 식별한 XML 파일 교환을 통해 실행하며, Live 영상으로 위치를 확인하는 것이다.
Core의 순수 모델·표현식, Application의 실행과 포트, Infrastructure의 파일·XML·HTTP,
Desktop의 WPF 조립이라는 의존 방향은 이 목적에 맞게 구성되어 있다.

전체 계층을 검토하면서 안정된 실행 계약을 유지하고, 비동기 작업의 경계와 중복 검증에
집중했다. 주요 Action 매핑, Stop과 Abort 구분, 결과 세션 보존, 장비 파일의 소유권 정리,
템플릿 필드 검증, 이미지 크기 제한·decode·스냅샷 소유권은 기존 설계를 유지한다.

## 수정한 경계 문제

| 영역 | 변경과 결과 |
|---|---|
| 표현식과 복사 | `e` 같은 Action 별칭이 `1e-3` 지수 표기를 손상시키지 않는다. 문자열 인덱스 `action['result']`에도 기존 참조 검증을 적용한다. 파싱·평가 깊이 128과 노드 수 4096을 넘는 입력은 검증 오류가 된다. |
| Workflow 알림 | 각 이벤트 구독자의 오류를 격리해 Stop 취소와 실제 Action 결과 처리를 방해하지 못하게 한다. |
| Live 명령 | Stage/Camera/Lens/촬영 입력을 작업 시작 시 고정한다. 이동·렌즈 명령 전송 후 새 Live 프레임까지 오래된 이미지의 위치 보정을 잠근다. Workflow 상태 전환 중 Live 재개 의도를 보존한다. |
| Live 응답 | Lens 성공 응답에 실제 `current_lens_mode`가 반드시 있어야 한다. |
| 문서 작업 | New/Open/Save/Close 작업이 문서 소유권을 공유한다. 저장은 독립 snapshot을 사용하며 성공 후 이름·경로를 적용한다. 늦은 모델 변경은 dirty로 남고 New/Open/Close의 승인으로 처리되지 않는다. |
| 설정 | 빈 Live 이미지 폴더는 exchange 폴더를 따르는 fallback으로 유지한다. 화면의 최종 검증은 Infrastructure 검증기를 재사용한다. URL path/query의 대소문자 변경을 설정 변경으로 인식한다. |
| ViewModel 수명 | 버린 Action/Parameter/Branch 트리가 singleton 언어 이벤트 때문에 남지 않도록 약한 구독을 사용한다. |
| 응답 시간 예산 | 파일 안정화 대기에 응답 단계의 남은 timeout을 전달한다. 시간 예산 만료와 operator 취소를 구분하여 파일 정리 정책을 유지한다. |
| 파일명 | Win32에서 같은 경로가 되는 끝 공백·점, 예약 장치 이름을 request/response 파일명으로 사용하지 못하게 한다. |
| 구버전 문서 | v1 변환을 Action 루트와 실제 parameter/result 컨테이너로 제한한다. `results.last`, 동적 인덱스, 문자열 인덱스와 괄호를 처리하고 중첩 HTTP JSON이나 일반 문자열을 바꾸지 않는다. |
| HTTP | URL 정보 제거를 공통화하고 username/password/query/fragment를 로그에서 제외한다. timeout과 취소를 응답 본문까지 적용하고 늦은 응답·fault를 정리한다. JSON은 전체 본문을 검사하며 큰 정수도 표현식의 double 모델로 변환한다. |

응답 비교 예외는 정확한 `NamedTypedObject` 태그의 비접두사 `Type` 속성 값에 적용한다.
`Version` 및 다른 태그의 `Type`은 계속 비교하고, 출력은 제공된 템플릿 양식을 사용한다.
실행 폴더의 전체 `Templates`를 시작 시 읽으므로 XML 교체 후 앱 재시작으로 반영된다.
외부 파일 오류는 기본 XML로 대체하지 않는다.
외부 vendor 형식의 request·성공/실패 response 원문 보존, 교체 후 재시작, 파일 누락·인코딩·XML·placeholder 오류 및 Type 비교 경계를 회귀 테스트로 검증했다.
Desktop 빌드와 publish 폴더에 XML 25개가 소스와 동일하게 복사되는 것도 확인했다.

이미지 경로 기본값은 설정의 공통 공유 폴더를 사용한다. 새 Integration/Live Frame/OM은 현재 설정에서 기본 파일 경로를 만들고, Live 화면의 촬영도 같은 폴더를 사용한다. 빈 이미지 폴더 설정은 통신 폴더를 따라가며 기존의 명시 경로는 보존한다. Live 화면·새 Integration·새 Live Frame의 기본 HFW는 5 µm이다.

## 검증과 확인 범위

현재 검증 결과는 전체 솔루션 빌드 경고 0·오류 0, Linux 테스트 458개 통과·실패 0·skip 0이다.
100ms 응답 제한과 2초 안정화 대기의 주입 reader 재현에서, 응답 대기와 기존 응답 baseline
검사는 변경 전 약 2011/2004ms에서 변경 후 120/102ms로 줄었다. 시간 예산에 의해 reader
토큰만 취소되며 operator 토큰은 유지되는 것을 확인했다.

전체 `DrillFlow.Designer.sln` 빌드에는 WPF 앱과 원본 net48 테스트 프로젝트가 포함된다.
Linux에서는 원본 Core/Application/Infrastructure 테스트와 WPF에 의존하지 않는
RealtimeVideo 설정 ViewModel 테스트를 외부 net10 실행 프로젝트로 검증한다.
원본 테스트의 assertion을 Linux 환경에 맞춰 완화하지 않는다.

문서 I/O, Live 화면과 약한 이벤트 구독의 새 WPF 회귀 테스트는 전체 솔루션에서 컴파일한다.
실행 검증은 Windows/.NET Framework 4.8에서 다음 명령으로 수행해야 한다.

```powershell
dotnet restore DrillFlow.Designer.sln --configfile NuGet.Config
dotnet build DrillFlow.Designer.sln --no-restore
dotnet test tests\DrillFlow.Tests\DrillFlow.Tests.csproj --no-build
```

실제 장비와 로컬/SMB 파일 공유의 동작은 [배포 점검](deployment.md)을 따라 확인한다.
협조적인 파일 안정화 대기의 timeout과 operator 취소는 구분할 수 있지만,
.NET Framework의 동기 UNC 커널 호출 자체를 강제로 중단할 수는 없다.

SignalR는 현재 문서에 명시된 설정·화면 확장 지점이며 실제 연결 client는 없다.
Live 영상의 현재 동작 경로는 XML 파일 교환이다. 기존 OM 촬영 구현도 유지하고,
이를 미지원이라고 설명하던 문서 내용을 현재 코드와 맞췄다.

외부 Action의 `parameters`는 작성된 literal 또는 이전 실행에서 평가한 값을 노출한다.
아직 실행하지 않은 Action의 expression 파라미터를 재귀 평가하는 기능은 제공하지 않는다.
