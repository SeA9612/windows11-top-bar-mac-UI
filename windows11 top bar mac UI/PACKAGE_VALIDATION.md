# 실행 ZIP 배포 검증 — v0.1.1

검증일: 2026-11-09, 한국 시간. Windows 11 x64 개발 PC에서 확인했습니다.

## 이번 업로드에서 확인한 항목

- 빌드 캐시를 제외하고 복사한 소스에서 상태·배치·Win32 ABI 검사 **42개 통과**.
- 해당 소스로 Release / win-x64 / self-contained 게시. .NET SDK나 런타임 설치가 필요 없는 배포 방식.
- ZIP CRC 검사 통과 및 압축 해제본의 **467개 파일 SHA-256 일치**. 별도로 체크섬 목록 파일도 포함.
- `coreclr.dll`, `hostfxr.dll`, `hostpolicy.dll`, `PresentationFramework.dll`과 실행 EXE 존재 확인.
- 포함 런타임: Microsoft.NETCore.App **8.0.33**, Microsoft.WindowsDesktop.App **8.0.33**.
- 압축 해제한 EXE의 자체 복구 감시 진입 경로를 별도 프로세스로 실행해 **종료 코드 0** 확인. 이 검사는 두 번째 상단 바를 띄우지 않는 시작 검사이며, 다른 PC의 실제 UI 검증을 의미하지 않음.
- 공개 소스에 빌드 캐시, 개인 설정, 실행 로그, 대화 원문과 사용자 화면 이미지를 포함하지 않음.
- Markdown 내부 링크와 소개 SVG의 XML 구문 검사.

## 파일

- ZIP: `portable/GlassDock-TopBar-v0.1.1-win-x64.zip`
- 크기: **76,296,829 바이트**
- SHA-256: `6fe770f982e528035fc75a717cd8814da28ab9c069f9c025d925099f59791ed9`
- 저장소와 GitHub Release에 동일한 ZIP을 제공.

## 실제 UI 검사와 제한

직전 위치 수정 검증에서 최대화·복원 6회, 전체 화면 해제, 최소화와 위치 자동 복구가 통과했습니다. 그때 발견된 **트레이 아이콘 렌더링 오류는 미해결**입니다. 실제 Windows 알림 배너는 재검사에서 표시되지 않아 수신·배치 확인을 완료하지 못했습니다. 이번 업로드를 위해 이 실패한 검사를 성공으로 바꾸거나 Windows 알림 설정을 변경하지 않았습니다.

다른 PC, 보조 모니터와 다른 DPI 장치의 실제 UI 검사는 수행하지 않았습니다. 전체 내용은 [0.1.1 릴리즈 노트](../Release%20Notes/Release%20Note0.1.1%20ver.md)와 [위치 수정 검증](source/validation/검증결과_2026-11-09.md)을 확인하세요.
