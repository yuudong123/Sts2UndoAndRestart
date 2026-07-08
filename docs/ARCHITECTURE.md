# Undo And Restart 구조 명세

## 목표

이 모드는 STS2 전투 상태를 직접 스냅샷으로 저장하고 복원합니다. QuickRestart처럼 전투 시작부터 재생하는 방식이 아니라, 현재 객체 그래프의 필드와 UI 상태를 되돌려 즉시 이전 상태로 이동하는 방식을 사용합니다.

## 전체 구성

```text
MainFile
  -> Harmony 패치 등록
  -> 설정 로드
  -> CombatManager 이벤트 구독

UndoRedoPatches
  -> 입력 처리
  -> 게임 액션 경계 감지
  -> 입력 설정 항목 주입

UndoRedoManager
  -> 스냅샷 목록 관리
  -> undo/redo 커서 관리
  -> 플레이어 조작 가능 시점 캡처

CombatSnapshot
  -> 전투, 런, 카드, 플레이어, 크리처, UI 상태 캡처/복원

ActionHistoryOverlay
  -> 사용 기록 UI 렌더링

FloorRestartService
  -> F5 층 다시 시작 처리
```

## 스냅샷 캡처 흐름

1. 카드, 포션, 턴 종료 같은 주요 액션이 시작되면 `UndoRedoPatches`가 `UndoRedoManager.CaptureBeforeAction`을 호출합니다.
2. 액션 Task가 완료되면 `CaptureAfterActionAsync`가 플레이어 조작 가능 시점 캡처를 예약합니다.
3. `ActionExecutor.AfterActionFinished`, `ActionQueueSynchronizer.PlayPhase`, `CombatManager.PlayerActionsDisabledChanged` 같은 경계에서 안정화 여부를 다시 확인합니다.
4. 실제 캡처 가능 조건을 만족하면 `CombatSnapshot.Capture`가 호출됩니다.
5. 같은 상태의 중복 스냅샷은 fingerprint 비교로 제거합니다.
6. redo 브랜치가 남아 있는 상태에서 새 행동이 들어오면 현재 커서 뒤쪽 스냅샷과 사용 기록을 잘라냅니다.

## 복원 흐름

`CombatSnapshot.Restore`는 다음 순서로 복원합니다.

1. 공중에 남은 카드/생성 카드 같은 일시 VFX를 정리합니다.
2. 크리처 생명주기와 전투 참여 목록을 복원합니다.
3. 런 상태와 모델 필드를 복원합니다.
4. 전투 필드, 플레이어 상태, 카드 더미, 포션, 유물, 오브를 복원합니다.
5. 전투 히스토리와 런 히스토리를 복원합니다.
6. 카드 런타임 상태와 유물 활성 표시 상태를 정리합니다.
7. UI를 갱신하고 스냅샷 검증을 실행합니다.

복원은 게임의 공식 save/load와 다르게 현재 객체를 최대한 재사용합니다. 그래서 Godot 노드, Spine 애니메이션, 카드 플레이 노드, 포션 슬롯, 유물 홀더 같은 UI 상태를 별도로 정리합니다.

## F5 층 다시 시작 흐름

`FloorRestartService`는 현재 싱글플레이 런 저장 데이터를 읽어 현재 방 입장 상태로 돌아갑니다.

- 멀티플레이어에서는 실행하지 않습니다.
- 액션 큐가 진행 중이면 실행하지 않습니다.
- 전투 보상 화면에서 이미 완성된 추가 보상은 보존합니다.
- 전투 진행 중 생성된 미완료 추가 보상은 재시작 전에 제거합니다.
- 런 씬을 정리한 뒤 저장된 방을 다시 로드합니다.

## 설정과 입력

- 설정 파일: `OS.GetUserDataDir()/mod_configs/UndoAndRestart.json`
- 입력 액션:
  - `undo_and_restart_undo`
  - `undo_and_restart_redo`
  - `undo_and_restart_restart`
- 기본 fallback 키:
  - 되돌리기: 왼쪽 방향키
  - 다시 실행: 오른쪽 방향키
  - 층 다시 시작: `F5`

입력 설정에 사용자가 직접 단축키를 지정하면 기본 fallback 키보다 사용자 설정을 우선합니다. 콘솔, `LineEdit`, `TextEdit` 입력 중에는 undo/redo 단축키를 무시합니다.

## 멀티플레이어 차단

모드 매니페스트는 `affects_gameplay=false`입니다. 대신 코드에서 실제 상태 변경 기능을 제한합니다.

- `UndoRedoManager.CanCapture`는 일반 멀티플레이어 런에서 캡처를 차단합니다.
- `FloorRestartService`는 `NetGameType.Singleplayer`가 아니면 F5 재시작을 차단합니다.
- 사용 기록 UI는 싱글플레이 또는 fake multiplayer 조건에서만 표시됩니다.

## 업데이트 시 점검할 부분

- STS2 내부 필드명: reflection으로 접근하는 필드는 게임 업데이트 후 이름과 타입을 다시 확인해야 합니다.
- 액션 안정화 타이밍: 카드 사용, 카드 생성, 포션 사용, 턴 종료 경계에서 플레이어 조작 가능 시점 캡처가 유지되는지 확인해야 합니다.
- 카드 비용과 런타임 상태: 비용 변경 효과, 이번 턴 한정 비용, 카드 UI 갱신이 함께 복원되는지 확인해야 합니다.
- 유물 스택 표시: 실제 모델 값과 UI 표시 값이 복원 후 같은 상태로 갱신되는지 확인해야 합니다.
- 보상/런 히스토리: F5와 undo가 통계, 피해 기록, 보상 계산에 누적 영향을 남기지 않는지 확인해야 합니다.
