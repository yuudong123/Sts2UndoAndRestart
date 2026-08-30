# C# 파일 명세

## 진입점과 패치

| 파일 | 책임 |
| --- | --- |
| `MainFile.cs` | 모드 초기화 진입점입니다. Harmony 패치를 등록하고 설정을 로드하며 전투 이벤트를 구독합니다. |
| `UndoRedoPatches.cs` | Harmony 패치 모음입니다. 키보드 및 선택형 M4/M5 입력, 액션 경계, 입력 설정, 사용 기록, 비동기 카드 VFX 소유권 등록을 담당합니다. |
| `ModSettingsPanelPatch.cs` | 모드 정보 화면에 스냅샷 개수, 사용 기록 탭 표시, 카드 선택창 체크포인트, M4/M5 조작 설정 UI를 추가합니다. |
| `FeatureAnnouncement.cs` | 메인 메뉴 준비 후 최초 1회 기능 안내창을 표시하고 확인 여부를 저장합니다. |
| `NecrobinderVfxSafetyPatches.cs` | `NNecrobinderVfx`의 머리 표시와 낫불꽃 콜백을 안전하게 처리합니다. 복원 중 이미 정리된 Godot 노드 때문에 VFX 콜백이 예외를 내지 않게 막습니다. |

## 스냅샷 엔진

| 파일 | 책임 |
| --- | --- |
| `UndoRedoManager.cs` | 스냅샷 스택, 커서, 타임라인 세대, 직렬화된 이동 게이트, 복원 실행 잠금, 턴 전환 스냅샷, 정확한 액션별 사용 기록 연결을 관리합니다. |
| `CardChoiceCheckpointService.cs` | 수동 카드 선택창 체크포인트를 캡처하고 오래된 선택기 세대를 무효화하며, 선택 중 타임라인 이동을 위해 현재 선택을 중단하고 원래 액션을 재실행합니다. |
| `ObjectGraphSnapshot.cs` | 임의 객체의 필드 그래프를 reflection으로 깊은 복제하고 같은 루트 객체에 복원합니다. 중첩된 mutable `AbstractModel` 탐색과 `Rng`, `CardEnergyCost`, `DynamicVarSet`, 컬렉션 복원도 담당합니다. |
| `CombatSnapshot.cs` | 핵심 전투 스냅샷입니다. 외부 실행 잠금을 유지하면서 이전 프레젠테이션 세대를 격리하고 크리처, 플레이어, 모델, 카드, 더미, 포션, 유물, 오브, 히스토리, UI를 다음 렌더 전에 복원합니다. |
| `RunStateSnapshot.cs` | 전투 중에도 영향을 받는 런 상태 일부를 저장하고 복원합니다. |
| `RunHistorySnapshot.cs` | undo/restart가 런 기록과 피해 통계에 누적 오염을 만들지 않도록 런 히스토리를 저장하고 복원합니다. |
| `CombatVisualSnapshot.cs` | 크리처 위치와 형태·루프·종료 포즈 같은 의미 기반 시각 상태를 복원하며, 애니메이션 재생 시간은 복원하지 않습니다. |
| `SnapshotValidator.cs` | 복원 후 손패 홀더/더미/전투 카드 목록/타게팅이 플레이 가능한 상태인지 검사하고, 불변식 위반 시 복원 롤백을 유도합니다. |

## UI와 입력

| 파일 | 책임 |
| --- | --- |
| `ActionHistoryOverlay.cs` | 우상단 사용 기록 탭을 만들고 렌더링합니다. 카드/포션 이미지, 턴 구분선, 현재 스냅샷 표시, 클릭 이동을 처리합니다. |
| `UndoInputBindings.cs` | 게임 입력 설정에 undo/redo/restart 액션을 등록하고 사용자 지정 단축키와 기본 fallback 키를 연결합니다. |
| `UndoText.cs` | 언어 파일에서 UI 문구를 읽고 현재 게임 언어에 맞는 번역을 선택하며 누락 시 영어로 대체합니다. |
| `UndoAndRestartConfig.cs` | 스냅샷 개수, 사용 기록 탭 표시, 카드 선택창 체크포인트, M4/M5 조작, 기능 안내 확인 여부를 설정 파일로 저장하고 로드합니다. |
| `language/*.json` | 영어, 한국어, 일본어, 중국어 간체, 중국어 번체 사용자 문구를 저장합니다. |

## 복원 안정화 보조

| 파일 | 책임 |
| --- | --- |
| `CombatRuntimeStateCleanup.cs` | 복원이나 F5 재시작 후 남을 수 있는 전투 런타임 플래그, 액션 blocker, 턴 종료 상태를 정리합니다. |
| `TransientCardVfxCleanup.cs` | 비동기 카드 VFX 소유자를 추적하고 이전 타임라인 노드를 즉시 숨겨 격리하며, `NodePool` 소유권을 어기지 않도록 나머지 일시 카드 노드를 안전하게 반납합니다. |
| `SovereignBladeVfxSync.cs` | 군주의 칼날처럼 별도 VFX가 카드 개수와 동기화되어야 하는 카드를 복원 상태에 맞춰 정리합니다. |
| `CreaturePresentationLifecycle.cs` | 되감을 수 없는 사망 애니메이션 노드를 엔진 취소 경계에서 폐기하고, 복원 후 새 프레젠테이션 노드로 재생성되게 합니다. |
| `ReflectionUtil.cs` | private 필드와 메소드 접근을 단일 경로로 모읍니다. 게임 업데이트 시 reflection 실패 지점을 추적하기 쉽게 합니다. |

## F5 재시작

| 파일 | 책임 |
| --- | --- |
| `FloorRestartService.cs` | 현재 방을 저장 데이터 기준으로 다시 로드합니다. 전투, 이벤트, 보상 화면 재시작과 보상 보존/제거 규칙을 처리합니다. |

## 데이터 타입

| 파일 | 책임 |
| --- | --- |
| `UndoRedoManager.cs` 내부 `ActionHistoryEntry` | 사용 기록 UI에 표시되는 카드/포션/턴 전환 항목입니다. 대상 스냅샷 인덱스와 턴 번호를 포함합니다. |
| `UndoRedoManager.cs` 내부 `ActionHistoryEntryKind` | 사용 기록 항목 종류입니다. 카드, 포션, 포션 버림, 카드 선택, 턴 전환을 구분합니다. |
| `CombatRuntimeStateCleanup.cs` 내부 `RuntimeBlockerKind` | 런타임 blocker가 어떤 종류로 남았는지 구분해 로그와 복구 판단에 사용합니다. |

## 유지보수 규칙

- 새 스냅샷 항목을 추가하면 캡처와 복원을 같은 파일 안에서 최대한 붙여서 관리합니다.
- reflection 접근은 직접 흩뿌리지 말고 `ReflectionUtil`을 우선 사용합니다.
- 새 UI 문구는 `UndoText`에 모읍니다.
- 새 설정 값은 `UndoAndRestartConfig`에서 저장/로드/기본값을 함께 관리합니다.
- 전투 중 상태를 바꾸는 기능은 멀티플레이어 차단 여부를 먼저 확인합니다.
