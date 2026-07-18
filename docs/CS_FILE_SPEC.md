# C# 파일 명세

## 진입점과 패치

| 파일 | 책임 |
| --- | --- |
| `MainFile.cs` | 모드 초기화 진입점입니다. Harmony 패치를 등록하고 설정을 로드하며 전투 이벤트를 구독합니다. |
| `UndoRedoPatches.cs` | Harmony 패치 모음입니다. 입력 처리, 액션 경계 감지, 입력 설정 항목 주입, 사용 기록 엔트리 생성을 담당합니다. |
| `ModSettingsPanelPatch.cs` | 모드 정보 화면에 스냅샷 제한과 사용 기록 탭 표시 설정 UI를 추가합니다. |
| `NecrobinderVfxSafetyPatches.cs` | `NNecrobinderVfx`의 머리 표시와 낫불꽃 콜백을 안전하게 처리합니다. 복원 중 이미 정리된 Godot 노드 때문에 VFX 콜백이 예외를 내지 않게 막습니다. |

## 스냅샷 엔진

| 파일 | 책임 |
| --- | --- |
| `UndoRedoManager.cs` | 스냅샷 스택과 커서를 관리합니다. 캡처 가능 조건, undo/redo 이동, 턴 전환 스냅샷, 사용 기록 엔트리 연결을 처리합니다. |
| `ObjectGraphSnapshot.cs` | 임의 객체의 필드 그래프를 reflection으로 깊은 복제하고 같은 루트 객체에 복원합니다. 중첩된 mutable `AbstractModel` 탐색과 `Rng`, `CardEnergyCost`, `DynamicVarSet`, 컬렉션 복원도 담당합니다. |
| `CombatSnapshot.cs` | 핵심 전투 스냅샷입니다. 크리처, 플레이어, 모델, 카드, 더미, 포션, 유물, 오브, 전투 히스토리, UI 상태를 캡처하고 복원하며 카드 UI 갱신 신호도 보냅니다. |
| `RunStateSnapshot.cs` | 전투 중에도 영향을 받는 런 상태 일부를 저장하고 복원합니다. |
| `RunHistorySnapshot.cs` | undo/restart가 런 기록과 피해 통계에 누적 오염을 만들지 않도록 런 히스토리를 저장하고 복원합니다. |
| `CombatVisualSnapshot.cs` | 크리처 위치, 표시 상태, Spine 애니메이션 같은 전투 시각 상태를 저장하고 복원합니다. |
| `SnapshotValidator.cs` | 복원 후 손패 홀더/더미/전투 카드 목록/타게팅이 플레이 가능한 상태인지 검사하고, 불변식 위반 시 복원 롤백을 유도합니다. |

## UI와 입력

| 파일 | 책임 |
| --- | --- |
| `ActionHistoryOverlay.cs` | 우상단 사용 기록 탭을 만들고 렌더링합니다. 카드/포션 이미지, 턴 구분선, 현재 스냅샷 표시, 클릭 이동을 처리합니다. |
| `UndoInputBindings.cs` | 게임 입력 설정에 undo/redo/restart 액션을 등록하고 사용자 지정 단축키와 기본 fallback 키를 연결합니다. |
| `UndoText.cs` | 한국어, 영어, 중국어 UI 문구를 현재 게임 언어에 맞춰 반환합니다. |
| `UndoAndRestartConfig.cs` | 스냅샷 제한과 사용 기록 탭 표시 여부를 설정 파일로 저장하고 로드합니다. |

## 복원 안정화 보조

| 파일 | 책임 |
| --- | --- |
| `CombatRuntimeStateCleanup.cs` | 복원이나 F5 재시작 후 남을 수 있는 전투 런타임 플래그, 액션 blocker, 턴 종료 상태를 정리합니다. |
| `TransientCardVfxCleanup.cs` | 카드 사용/생성 중 화면 중앙에 남은 일시 카드 노드와 선택 트윈을 정리합니다. |
| `SovereignBladeVfxSync.cs` | 군주의 칼날처럼 별도 VFX가 카드 개수와 동기화되어야 하는 카드를 복원 상태에 맞춰 정리합니다. |
| `ParkedCreatureNodeRegistry.cs` | 복원 중 일시적으로 전투 목록에서 빠져야 하는 `NCreature` 노드를 숨겨서 보관하고, 다시 필요해지면 전투방 목록에 되돌립니다. |
| `ReflectionUtil.cs` | private 필드와 메소드 접근을 단일 경로로 모읍니다. 게임 업데이트 시 reflection 실패 지점을 추적하기 쉽게 합니다. |

## F5 재시작

| 파일 | 책임 |
| --- | --- |
| `FloorRestartService.cs` | 현재 방을 저장 데이터 기준으로 다시 로드합니다. 전투, 이벤트, 보상 화면 재시작과 보상 보존/제거 규칙을 처리합니다. |

## 데이터 타입

| 파일 | 책임 |
| --- | --- |
| `UndoRedoManager.cs` 내부 `ActionHistoryEntry` | 사용 기록 UI에 표시되는 카드/포션/턴 전환 항목입니다. 대상 스냅샷 인덱스와 턴 번호를 포함합니다. |
| `UndoRedoManager.cs` 내부 `ActionHistoryEntryKind` | 사용 기록 항목 종류입니다. 카드, 포션, 포션 버림, 턴 전환을 구분합니다. |
| `CombatRuntimeStateCleanup.cs` 내부 `RuntimeBlockerKind` | 런타임 blocker가 어떤 종류로 남았는지 구분해 로그와 복구 판단에 사용합니다. |

## 유지보수 규칙

- 새 스냅샷 항목을 추가하면 캡처와 복원을 같은 파일 안에서 최대한 붙여서 관리합니다.
- reflection 접근은 직접 흩뿌리지 말고 `ReflectionUtil`을 우선 사용합니다.
- 새 UI 문구는 `UndoText`에 모읍니다.
- 새 설정 값은 `UndoAndRestartConfig`에서 저장/로드/기본값을 함께 관리합니다.
- 전투 중 상태를 바꾸는 기능은 멀티플레이어 차단 여부를 먼저 확인합니다.
