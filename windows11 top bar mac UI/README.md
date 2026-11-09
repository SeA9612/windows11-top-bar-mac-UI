# Windows 11 TopBar — 실행·소스 안내

[소개](../README.md) · [변경 이력](../Release%20Notes/README.md) · [v0.1.1 GitHub Release](https://github.com/SeA9612/windows11-top-bar-mac-UI/releases/tag/v0.1.1)

## 다운로드와 실행

1. [실행용 ZIP](https://github.com/SeA9612/windows11-top-bar-mac-UI/releases/download/v0.1.1/GlassDock-TopBar-v0.1.1-win-x64.zip)을 내려받습니다. 저장소 전체 ZIP을 받았다면 이 폴더의 `portable/GlassDock-TopBar-v0.1.1-win-x64.zip`을 사용합니다.
2. 전체 압축을 풉니다.
3. 기존 상단 바가 켜져 있으면 종료하고 `GlassDock.TopBar.exe`를 실행합니다.

Windows 11 x64와 일반 사용자 권한으로 실행합니다. .NET 8 Desktop Runtime이 포함되어 있어 .NET이나 SDK를 설치할 필요가 없습니다. EXE와 DLL·런타임 파일은 같은 폴더에 두세요. 자동 시작은 등록하지 않습니다. 실행 중인 상단 바를 중복 실행하면 안내 창이 나타납니다.

## 포함 파일

| 위치 | 내용 |
|---|---|
| `portable/GlassDock-TopBar-v0.1.1-win-x64.zip` | 런타임 포함 실행본, 사용 안내와 릴리즈 노트, 파일별 체크섬 |
| `source/` | C# WPF 소스, 검증 프로젝트, 빌드 스크립트 |
| `SHA256SUMS.txt` | 실행 ZIP의 SHA-256 |

GitHub Release에 첨부한 ZIP과 저장소의 ZIP은 같은 파일입니다. 개인 설정·로그·대화 원문·사용자 화면·빌드 캐시와 독 실행파일은 포함하지 않습니다.

## 사용과 종료

일반 창에서는 투명하게 표시하고, 선택 모니터의 최대화·전체 화면 창이 있으면 숨깁니다. 복원·최소화하면 다시 나타납니다. 최대화 중에는 화면 위쪽 가장자리에 마우스를 약 0.4초 올려 잠시 표시할 수 있습니다. 메뉴나 트레이를 사용한 뒤 마우스를 떼면 다시 숨습니다.

상단 바 우클릭 메뉴에서 모니터 변경과 종료를 할 수 있습니다. Windows 트레이의 `GlassDock 상단 바` 메뉴에도 표시·종료 항목이 있습니다. 설정은 `%LOCALAPPDATA%\GlassDock\topbar.json`, 오류 기록은 같은 폴더의 `topbar-errors.log`에 생성됩니다.

트레이의 유리 아이콘 표시에는 미해결 오류가 있고, 알림 수신·배치는 최신 재검사에서 확인하지 못했습니다. 배포 범위는 [릴리즈 노트](../Release%20Notes/Release%20Note0.1.1%20ver.md)를 먼저 확인하세요. 카카오톡 자체 알림은 각 PC에서 `설정 → 알림 → 표시 위치 → 화면 우상단`으로 선택해야 합니다.

## 소스 수정과 빌드

Windows와 .NET 8 SDK 이상이 필요합니다. PowerShell에서 `source` 폴더로 이동합니다. 최초 빌드는 대상 팩과 패키지 복원을 위해 인터넷 연결이 필요할 수 있습니다.

```powershell
dotnet run --project src/TopBar/TopBar.csproj
dotnet run --project validation/TopBar/TopBarTests.csproj -c Release
powershell -ExecutionPolicy Bypass -File .\Build-TopBar.ps1
```

기본 빌드 결과는 `source/artifacts/topbar-portable/`이며 런타임이 포함됩니다. `-FrameworkDependent`를 지정하면 별도 .NET Desktop Runtime이 필요한 작은 실행본을 만듭니다. 공개 ZIP은 기본 런타임 포함 방식입니다.

실제 화면을 사용하는 자동 숨김·트레이 검사는 실행 중인 상단 바를 종료한 뒤 별도로 진행하세요. 이전 검사 기록과 최신 제한은 `source/validation/검증결과_2026-11-09.md`에서 구분했습니다.
