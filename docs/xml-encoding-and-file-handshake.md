# XML 인코딩 및 파일 인계 정책

> 적용 범위: Embedded XML 템플릿, 장비 request/response XML, 로컬 및 UNC 교환 폴더  
> 최종 확인: 2026-08-28

이 문서는 XML을 어떤 인코딩으로 받아들이고 내보내는지, 장비가 파일을 기록하는 도중 앱이 불완전한 내용을 읽지 않도록 어떻게 인계하는지를 정의한다. 논리 데이터 구조와 placeholder 목록은 [`contract.md`](../contract.md)를 따른다.

## 1. 발생했던 문제와 원인

실제 장비가 만든 UTF-8 BOM XML을 바탕으로 ACB와 Lens 템플릿을 작성했을 때, 템플릿 검증에서 BOM/U+FEFF 오류가 발생했다. 같은 파일을 UTF-8 BOM 없음으로 다시 저장하면 앱 시작과 response 수신이 정상화됐다.

원인은 XML 내용이나 `action`/`correlation_id`가 아니었다. 이전 구현은 입력 경로마다 인코딩 정책이 달랐다.

- 장비 response byte는 선두 `EF BB BF`를 UTF-8 BOM으로 인식해 메모리에서 제거했다.
- Embedded 템플릿은 BOM 자동 감지를 끈 UTF-8 reader로 읽었다. 이 경로에서는 BOM이 선두 문자 `U+FEFF`로 남았다.
- 이후 템플릿 계약 검증기가 `U+FEFF`를 명시적으로 금지했으므로, 내용이 올바른 템플릿도 시작 시 거부됐다.

따라서 Notepad++에서 두 파일 모두 UTF-8 계열로 보이고 XML 구조 검증이 통과했어도 결과가 달라질 수 있었다. BOM을 없앴을 때만 동작한 현상은 이 비대칭 decode 경로로 설명된다.

## 2. 현재 인코딩 정책

모든 XML 입력은 이제 하나의 공통 경로에서 **strict UTF-8 text**로 decode하고 정규화한다. 템플릿과 request/response 사이에 별도 인코딩 규칙을 두지 않는다.

| 입력 | 처리 |
|---|---|
| UTF-8, BOM 없음 | 허용 |
| UTF-8, 정확히 한 개의 선두 BOM (`EF BB BF`) | 허용하고 메모리에서 제거 |
| 선두 `U+FEFF`가 정확히 한 개인 template string | 허용하고 메모리에서 제거 |
| 잘못된 UTF-8 byte sequence | 거부 |
| UTF-16/UTF-32 | 거부 |
| BOM이 두 번 붙었거나 본문에 `U+FEFF`가 있음 | 거부 |
| NUL 문자를 포함한 text | 거부 |

정규화는 읽어 온 메모리 사본에만 적용하며 장비가 만든 원본 파일을 다시 쓰지 않는다. XML 선언은 `encoding="utf-8"`로 유지한다.

앱이 생성하는 request와 테스트 response는 재현 가능한 한 가지 형식을 위해 항상 **UTF-8 BOM 없음**으로 기록한다. 즉, 입력은 현장 호환성을 위해 UTF-8 BOM 유무를 모두 허용하고 출력은 BOM 없는 UTF-8로 고정하는 **관대한 입력/정규 출력** 정책이다.

## 3. XML 비교와 byte 비교의 경계

템플릿과 장비 XML의 호환성은 raw byte 배열을 서로 비교해 판단하지 않는다. 공통 UTF-8 decode와 BOM 제거 후 text로 XML 형식과 placeholder 위치를 해석한다. 고정 템플릿 구간의 ASCII space, tab, CR, LF 차이도 기존 규칙대로 무시한다.

Raw byte 비교는 XML 의미 비교가 아닌 다음 파일 소유권·신선도 보호에만 남겨 둔다.

- `RetainUntilOverwritten`에서 게시 전 response와 완전히 같은 파일을 새 응답으로 오인하지 않기 위한 baseline 비교
- 취소된 exchange가 자신이 게시한 request만 삭제하고, 장비나 다음 exchange가 바꾼 request는 보존하기 위한 소유권 비교
- 동일한 잘못된 response에 대한 중복 경고를 줄이기 위한 진단 비교

따라서 BOM 유무, 줄바꿈 또는 들여쓰기가 템플릿 일치 여부를 byte 수준에서 실패시키지 않는다. 반면 retained response의 byte가 실제로 바뀌지 않았다면 새 게시로 보지 않는 것은 의도된 lifecycle 규칙이다.

## 4. 기록 중인 파일을 피하는 앱 측 절차

response 감지는 파일 변경 알림에 의존하지 않고 다음 순서로 polling한다.

1. 파일 존재 여부, 길이, 마지막 기록 시각을 snapshot으로 얻고 4 MiB 제한을 먼저 확인한다.
2. `StableReadDelay` 동안 기다린 뒤 같은 metadata snapshot인지 다시 확인한다.
3. 파일을 **쓰기 공유 없이** read-only로 연다. 로컬 Windows 파일 시스템과 Windows share-mode semantics를 준수하는 SMB 서버에서는 장비의 writer handle이 아직 열려 있을 때 sharing violation이 발생하며, 앱은 이를 불완전 응답으로 처리하지 않고 다음 polling에서 재시도한다.
4. snapshot 길이만큼 끝까지 읽은 뒤 metadata를 한 번 더 확인한다. 읽는 동안 바뀌었으면 버리고 재시도한다.
5. strict UTF-8 decode와 BOM 정규화, well-formed XML, Action별 템플릿, `type`, `action`, `correlation_id`, `result` 및 Action 필드 검증을 수행한다.
6. 아직 완전하고 유효한 response가 아니면 설정된 timeout까지 polling을 계속한다.

이 방식은 고정 대기 시간을 크게 늘리지 않으면서, 길이와 timestamp가 우연히 잠시 같아진 기록 중 파일을 읽을 가능성을 줄이는 앱 측 보호장치다. 다만 share-mode semantics를 무시하는 서버나 파일 시스템까지 완전히 보증하지는 못한다. 최종 게시 완료 보증은 producer인 장비가 아래의 temp + flush + close + atomic rename 절차를 지킬 때 얻을 수 있다. 장비가 파일 작성을 끝낸 뒤에도 writer handle을 계속 열어 두면 앱은 response를 읽지 못하고 timeout 될 수 있으므로, 장비는 게시 직후 handle을 닫아야 한다.

`RetainUntilOverwritten` 모드이거나 기본 모드에서 기존 response 사전 삭제가 실패한 경우에는, 기존 파일이 보이더라도 writer가 열려 있어 안정적으로 읽을 수 없는 상태에서 새 request를 게시하지 않는다. 앱은 `ResponseTimeout` 안에 기존 response baseline을 안정적으로 읽거나 파일이 사라질 때까지 대기한 뒤에만 새 request를 게시한다. 이는 baseline을 읽지 못한 순간을 “이전 response 없음”으로 오인해, 늦게 완성된 stale response를 새 응답으로 받는 race를 막는다.

`PollingInterval`과 response timeout은 설정 화면에서 초 단위로 조정한다. `StableReadDelay`는 한 snapshot이 잠시 안정적인지 확인하는 내부 간격이며, 값을 과도하게 늘리면 특히 Live frame 처리율이 낮아진다. 현장 문제의 첫 해결책으로 단순히 긴 sleep을 추가하기보다 아래 게시 절차를 우선 적용한다.

## 5. 권장 장비 게시 절차

가장 확실한 producer-side handoff이자 최종 완료 신호는 **임시 파일 완성 후 원자적 이름 변경**이다.

1. 최종 `response.xml`과 같은 폴더에 correlation별 고유 임시 파일을 만든다.
2. UTF-8 BOM 있음 또는 없음 중 장비가 지원하는 형식으로 전체 XML을 기록한다.
3. buffer를 flush하고 필요하면 장치 flush까지 수행한다.
4. 임시 파일의 writer handle을 닫는다.
5. 같은 폴더 안에서 임시 파일을 설정된 response filename으로 atomic move/replace한다.

같은 폴더를 사용해야 rename이 동일 volume/share 안에서 원자적으로 보일 가능성이 가장 높다. SMB 장비 또는 API가 atomic replace를 보장하지 못한다면, 최소한 최종 파일을 한 번에 끝까지 기록하고 flush한 뒤 writer handle을 즉시 닫아야 한다. 최종 파일을 열어 둔 채 여러 번 부분 갱신하거나, request 하나에 대한 response 게시가 끝난 뒤에도 write handle을 유지하지 않는다.

앱의 request 게시도 이미 같은 폴더의 임시 파일을 완성한 뒤 replace/move하는 방식을 사용한다.

## 6. 문제 확인 순서

1. 파일의 첫 byte를 확인한다: UTF-8 BOM은 `EF BB BF`, UTF-16 LE는 `FF FE`, UTF-16 BE는 `FE FF`다.
2. BOM이 있더라도 정확히 한 번만 선두에 있는지 확인한다. `EF BB BF EF BB BF` 또는 본문의 `U+FEFF`는 계약 오류다.
3. 템플릿과 response가 모두 UTF-8인지, XML 선언이 `utf-8`인지 확인한다.
4. 장비가 response 기록 후 writer handle을 닫는지 확인한다. 파일이 Explorer에서 보이는 것만으로 기록 완료를 의미하지는 않는다.
5. 같은 Action 템플릿의 정확한 `{{{field_name}}}` 위치, well-formed XML, `type`, `action`, `correlation_id`, `result`를 확인한다.
6. `RetainUntilOverwritten`을 사용한다면 response가 요청 게시 전 baseline과 실제로 다른 byte로 덮어써졌는지 확인한다.
7. 앱 로그의 template/Action/correlation 불일치 경고와 response timeout을 확인한다. 공유 위반은 polling 경로에서 정상적인 재시도 사유로 처리되며 매번 개별 로그를 남기지 않는다. 필요하면 장비/파일 서버 측 handle 모니터링으로 writer 종료 시점을 확인한다. 민감한 경로나 전체 XML 본문을 운영 로그에 추가로 남기지는 않는다.

UTF-8 BOM 자체는 이제 오류가 아니다. 새 정책에서도 실패한다면 BOM을 무조건 제거하기보다 이 순서대로 이중 BOM, 다른 UTF 인코딩, write handle 유지, XML/placeholder 계약, lifecycle baseline을 구분해 진단한다.
