# Thanks for 3000 subs in steam workshop

# Undo And Restart

## English

`Undo And Restart` is a Slay the Spire 2 C# mod that adds undo, redo, floor restart, and an action history tab for combat.

The current source targets STS2 `0.109.0` only. Older game versions must use a matching historical mod release.

Mod versions follow `<game version>.<mod patch number>`. The first release for
STS2 `0.109.0` is `0.109.0.1`; additional mod-only fixes increment the final
number. Every Workshop update note must state the supported game version.

Workshop update notes use this format:

```text
Game version : x.x.x
Mod version : x.x.x.x

- Change 1
- Change 2
```

### Features

- Undo: default key is left arrow.
- Redo: default key is right arrow.
- Restart floor: default key is `F5`.
- The three hotkeys can be changed from the game's input settings screen.
- Used cards and potions can be viewed in a grid-based action history tab during combat.
- Clicking an item in the action history tab restores the corresponding snapshot.
- The mod settings screen lets players change the maximum snapshot count and show or hide the action history tab.

### Multiplayer Policy

This mod is distributed with `affects_gameplay=false`. Players should still be able to join multiplayer lobbies with the mod installed, but the actual undo and restart features are designed for singleplayer. The code blocks snapshot capture and F5 restart in normal multiplayer runs.

### Documentation

- STS2 0.109 snapshot audit: [SNAPSHOT_AUDIT_0.109.md](SNAPSHOT_AUDIT_0.109.md)
- Architecture notes: [docs/ARCHITECTURE.en.md](docs/ARCHITECTURE.en.md)
- C# file specification: [docs/CS_FILE_SPEC.en.md](docs/CS_FILE_SPEC.en.md)

### Build Requirements

- Windows
- .NET SDK 9.0
- Slay the Spire 2 installation
- Game runtime DLLs: `0Harmony.dll`, `GodotSharp.dll`, `sts2.dll`

The default build path assumes this Steam install location:

```powershell
D:\Games\Steam\steamapps\common\Slay the Spire 2
```

If the game is installed somewhere else, pass `Sts2InstallDir` or set the `STS2_INSTALL_DIR` environment variable.

```powershell
dotnet build .\Undo.csproj -c Release /p:Sts2InstallDir="C:\Path\To\Slay the Spire 2"
```

Or:

```powershell
$env:STS2_INSTALL_DIR = "C:\Path\To\Slay the Spire 2"
dotnet build .\Undo.csproj -c Release
```

The built DLL is generated here:

```text
bin\Release\net9.0\UndoAndRestart.dll
```

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

### Repository Notes

- `bin/` and `obj/` are build outputs and should not be committed.
- STS2 runtime DLLs are part of the game installation and are not included in this repository.
- Steam Workshop upload packages are managed separately.

### Development Note

The broad refactoring pass, code specifications, and README were drafted with Codex and uploaded after developer review.

## 한국어

`Undo And Restart`는 Slay the Spire 2 전투 중 되돌리기, 다시 실행, 층 다시 시작, 사용 기록 탭을 추가하는 C# 모드입니다.

현재 소스는 STS2 `0.109.0`만 지원합니다. 이전 게임 버전에서는 해당 버전에 맞는 과거 모드 릴리스를 사용해야 합니다.

모드 버전은 `<게임 버전>.<모드 패치 번호>` 형식을 사용합니다. STS2
`0.109.0`의 첫 배포 버전은 `0.109.0.1`이며, 게임 버전이 그대로인 상태에서
모드만 추가 수정하면 마지막 번호를 1씩 올립니다. 모든 창작마당 업데이트
노트에는 지원하는 게임 버전을 명시합니다.

창작마당 업데이트 노트는 다음 형식을 사용합니다.

```text
Game version : x.x.x
Mod version : x.x.x.x

- 변경 내용 1
- 변경 내용 2
```

### 기능

- 되돌리기: 기본값은 왼쪽 방향키입니다.
- 다시 실행: 기본값은 오른쪽 방향키입니다.
- 층 다시 시작: 기본값은 `F5`입니다.
- 게임의 입력 설정 화면에서 세 기능의 단축키를 직접 변경할 수 있습니다.
- 전투 중 사용한 카드와 포션을 격자형 사용 기록 탭으로 볼 수 있습니다.
- 사용 기록 탭에서 특정 항목을 클릭하면 해당 스냅샷으로 이동합니다.
- 모드 설정에서 최대 스냅샷 수와 사용 기록 탭 표시 여부를 조절할 수 있습니다.

### 멀티플레이어 정책

이 모드는 `affects_gameplay=false`로 배포됩니다. 멀티플레이어 방에는 접속할 수 있어야 하지만, 전투 상태를 직접 되돌리는 기능은 싱글플레이 중심으로 설계되어 있습니다. 코드에서는 일반 멀티플레이어 런에서 스냅샷 캡처와 F5 재시작을 차단합니다.

### 문서

- STS2 0.109 스냅샷 감사: [SNAPSHOT_AUDIT_0.109.md](SNAPSHOT_AUDIT_0.109.md)
- 한국어 구조 명세: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- 한국어 C# 파일 명세: [docs/CS_FILE_SPEC.md](docs/CS_FILE_SPEC.md)

### 빌드 요구 사항

- Windows
- .NET SDK 9.0
- Slay the Spire 2 설치본
- 게임 런타임 DLL: `0Harmony.dll`, `GodotSharp.dll`, `sts2.dll`

기본 빌드 경로는 다음 Steam 설치 경로를 사용합니다.

```powershell
D:\Games\Steam\steamapps\common\Slay the Spire 2
```

다른 경로에 설치되어 있다면 `Sts2InstallDir` 속성이나 `STS2_INSTALL_DIR` 환경 변수를 사용합니다.

```powershell
dotnet build .\Undo.csproj -c Release /p:Sts2InstallDir="C:\Path\To\Slay the Spire 2"
```

또는:

```powershell
$env:STS2_INSTALL_DIR = "C:\Path\To\Slay the Spire 2"
dotnet build .\Undo.csproj -c Release
```

빌드 결과물은 다음 위치에 생성됩니다.

```text
bin\Release\net9.0\UndoAndRestart.dll
```

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

### 공개 저장소 주의 사항

- `bin/`, `obj/`는 빌드 산출물이므로 커밋하지 않습니다.
- STS2 런타임 DLL은 게임 설치본의 파일이므로 저장소에 포함하지 않습니다.
- Steam Workshop 업로드 패키지는 별도 폴더에서 관리합니다.

### 개발 노트

전반적인 리팩토링, 코드 명세서, README는 Codex에서 초안을 작성했고 개발자가 검토를 마친 후 업로드되었습니다.
