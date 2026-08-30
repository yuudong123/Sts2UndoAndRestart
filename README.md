# Undo And Restart

## English

`Undo And Restart` is a Slay the Spire 2 C# mod that adds undo, redo, floor restart, and an action history tab for combat.

The current source targets STS2 `0.111.0` only. Older game versions must use a matching historical mod release.

Mod versions follow `<game version>.<mod patch number>`. The first release for
STS2 `0.111.0` is `0.111.0.1`; additional mod-only fixes increment the final
number. Every Workshop update note must state the supported game version. Before
publishing an update note, verify the latest public and beta game versions and
attach direct download links to their matching GitHub releases.

Workshop update notes use this format:

```text
Game version : x.x.x
Mod version : x.x.x.x

- Change 1
- Change 2

For <latest public version> -> <matching GitHub release download URL>
For <latest beta version> -> <matching GitHub release download URL>
```

### Features

- Undo: default key is left arrow.
- Redo: default key is right arrow.
- Restart floor: default key is `F5`.
- The three hotkeys can be changed from the game's input settings screen.
- An optional mod setting adds mouse side button controls alongside the keyboard hotkeys (`M4`: undo, `M5`: redo).
- Used cards and potions can be viewed in a grid-based action history tab during combat.
- Clicking an item in the action history tab restores the corresponding snapshot.
- Card-selection screens can optionally be stored as undo/redo checkpoints. The option is enabled by default.
- The mod settings screen lets players change the maximum snapshot count, show or hide the action history tab, enable or disable card-selection checkpoints, and add M4/M5 controls.
- User-facing mod text is loaded from separate English, Korean, Japanese, Simplified Chinese, and Traditional Chinese language files.

### Multiplayer Policy

This mod is distributed with `affects_gameplay=false`. Players should still be able to join multiplayer lobbies with the mod installed, but the actual undo and restart features are designed for singleplayer. The code blocks snapshot capture and F5 restart in normal multiplayer runs.

### Documentation

- STS2 0.111 snapshot audit: [SNAPSHOT_AUDIT_0.111.md](SNAPSHOT_AUDIT_0.111.md)
- STS2 0.111 restore pipeline audit: [RESTORE_PIPELINE_AUDIT.md](RESTORE_PIPELINE_AUDIT.md)
- Previous STS2 0.110 snapshot audit: [SNAPSHOT_AUDIT_0.110.md](SNAPSHOT_AUDIT_0.110.md)
- Previous STS2 0.109 snapshot audit: [SNAPSHOT_AUDIT_0.109.md](SNAPSHOT_AUDIT_0.109.md)
- Architecture notes: [docs/ARCHITECTURE.en.md](docs/ARCHITECTURE.en.md)
- C# file specification: [docs/CS_FILE_SPEC.en.md](docs/CS_FILE_SPEC.en.md)

### Project Structure

```text
Undo.csproj
UndoAndRestart.json
UndoAndRestartCode/
docs/
```

- `Undo.csproj`: C# project file and STS2 runtime DLL references.
- `UndoAndRestart.json`: STS2 mod manifest.
- `UndoAndRestartCode/`: mod source code.
- `docs/ARCHITECTURE.en.md`: snapshot engine and flow documentation.
- `docs/CS_FILE_SPEC.en.md`: responsibility list for each `.cs` file.

### Development Note

The broad refactoring pass, code specifications, and README were drafted with Codex and uploaded after developer review.

## 한국어

`Undo And Restart`는 Slay the Spire 2 전투 중 되돌리기, 다시 실행, 층 다시 시작, 사용 기록 탭을 추가하는 C# 모드입니다.

현재 소스는 STS2 `0.111.0`만 지원합니다. 이전 게임 버전에서는 해당 버전에 맞는 과거 모드 릴리스를 사용해야 합니다.

모드 버전은 `<게임 버전>.<모드 패치 번호>` 형식을 사용합니다. STS2
`0.111.0`의 첫 배포 버전은 `0.111.0.1`이며, 게임 버전이 그대로인 상태에서
모드만 추가 수정하면 마지막 번호를 1씩 올립니다. 모든 창작마당 업데이트
노트에는 지원하는 게임 버전을 명시합니다. 업데이트 노트를 게시하기 전에
최신 public 및 beta 게임 버전을 확인하고, 각 버전에 맞는 GitHub 릴리스의
직접 다운로드 링크를 함께 추가합니다.

창작마당 업데이트 노트는 다음 형식을 사용합니다.

```text
Game version : x.x.x
Mod version : x.x.x.x

- 변경 내용 1
- 변경 내용 2

For <최신 public 버전> -> <해당 GitHub 릴리스 다운로드 URL>
For <최신 beta 버전> -> <해당 GitHub 릴리스 다운로드 URL>
```

### 기능

- 되돌리기: 기본값은 왼쪽 방향키입니다.
- 다시 실행: 기본값은 오른쪽 방향키입니다.
- 층 다시 시작: 기본값은 `F5`입니다.
- 게임의 입력 설정 화면에서 세 기능의 단축키를 직접 변경할 수 있습니다.
- 모드 설정에서 키보드 단축키와 함께 사용할 마우스 옆 버튼 조작을 선택적으로 추가할 수 있습니다(`M4`: 되돌리기, `M5`: 다시 실행).
- 전투 중 사용한 카드와 포션을 격자형 사용 기록 탭으로 볼 수 있습니다.
- 사용 기록 탭에서 특정 항목을 클릭하면 해당 스냅샷으로 이동합니다.
- 카드 선택창을 선택적으로 undo/redo 체크포인트에 포함할 수 있으며 기본값은 켜짐입니다.
- 모드 설정에서 최대 스냅샷 수, 사용 기록 탭 표시 여부, 카드 선택창 체크포인트 사용 여부, M4/M5 조작 추가 여부를 조절할 수 있습니다.
- 사용자에게 표시되는 모드 문구는 영어, 한국어, 일본어, 중국어 간체, 중국어 번체 언어 파일로 분리되어 있습니다.

### 멀티플레이어 정책

이 모드는 `affects_gameplay=false`로 배포됩니다. 멀티플레이어 방에는 접속할 수 있어야 하지만, 전투 상태를 직접 되돌리는 기능은 싱글플레이 중심으로 설계되어 있습니다. 코드에서는 일반 멀티플레이어 런에서 스냅샷 캡처와 F5 재시작을 차단합니다.

### 문서

- STS2 0.111 스냅샷 감사: [SNAPSHOT_AUDIT_0.111.md](SNAPSHOT_AUDIT_0.111.md)
- STS2 0.111 복원 파이프라인 감사: [RESTORE_PIPELINE_AUDIT.md](RESTORE_PIPELINE_AUDIT.md)
- 이전 STS2 0.110 스냅샷 감사: [SNAPSHOT_AUDIT_0.110.md](SNAPSHOT_AUDIT_0.110.md)
- 이전 STS2 0.109 스냅샷 감사: [SNAPSHOT_AUDIT_0.109.md](SNAPSHOT_AUDIT_0.109.md)
- 한국어 구조 명세: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- 한국어 C# 파일 명세: [docs/CS_FILE_SPEC.md](docs/CS_FILE_SPEC.md)

### 프로젝트 구조

```text
Undo.csproj
UndoAndRestart.json
UndoAndRestartCode/
docs/
```

- `Undo.csproj`: C# 프로젝트와 STS2 런타임 DLL 참조를 정의합니다.
- `UndoAndRestart.json`: STS2 모드 매니페스트입니다.
- `UndoAndRestartCode/`: 실제 모드 소스입니다.
- `docs/ARCHITECTURE.md`: 스냅샷 엔진과 주요 흐름 설명입니다.
- `docs/CS_FILE_SPEC.md`: `.cs` 파일별 책임 명세입니다.

### 개발 노트

전반적인 리팩토링, 코드 명세서, README는 Codex에서 초안을 작성했고 개발자가 검토를 마친 후 업로드되었습니다.
