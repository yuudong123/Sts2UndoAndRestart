# Undo And Restart

## Documentation

- Korean architecture notes: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- English architecture notes: [docs/ARCHITECTURE.en.md](docs/ARCHITECTURE.en.md)
- Korean C# file specification: [docs/CS_FILE_SPEC.md](docs/CS_FILE_SPEC.md)
- English C# file specification: [docs/CS_FILE_SPEC.en.md](docs/CS_FILE_SPEC.en.md)

## Development Note

The broad refactoring pass, code specifications, and README were drafted with Codex and uploaded after developer review.

`Undo And Restart`는 Slay the Spire 2 전투 중 되돌리기, 다시 실행, 층 다시 시작 기능을 추가하는 C# 모드입니다.

## 기능

- 되돌리기: 기본값은 왼쪽 방향키입니다.
- 다시 실행: 기본값은 오른쪽 방향키입니다.
- 층 다시 시작: 기본값은 `F5`입니다.
- 게임의 입력 설정 화면에서 세 기능의 단축키를 직접 변경할 수 있습니다.
- 전투 중 사용한 카드와 포션을 격자형 사용 기록 탭으로 볼 수 있습니다.
- 사용 기록 탭에서 특정 항목을 클릭하면 해당 스냅샷으로 이동합니다.
- 모드 설정에서 최대 스냅샷 수와 사용 기록 탭 표시 여부를 조절할 수 있습니다.

## 멀티플레이어 정책

이 모드는 `affects_gameplay=false`로 배포됩니다. 멀티플레이어 방에는 접속할 수 있어야 하지만, 전투 상태를 직접 되돌리는 기능은 싱글플레이 중심으로 설계되어 있습니다. 코드에서는 일반 멀티플레이어 런에서 스냅샷 캡처와 F5 재시작을 차단합니다.

## 빌드 요구 사항

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

## 프로젝트 구조

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

## 공개 저장소 주의 사항

- `bin/`, `obj/`는 빌드 산출물이므로 커밋하지 않습니다.
- STS2 런타임 DLL은 게임 설치본의 파일이므로 저장소에 포함하지 않습니다.
- Steam Workshop 업로드 패키지는 별도 폴더에서 관리합니다.
