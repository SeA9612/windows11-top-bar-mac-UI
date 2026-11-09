# 실행 ZIP 배포 검증 — v0.1.2

검증일: 2026-11-09, 한국 시간. Windows 11 x64 개발 PC에서 확인했습니다.

## 이번 업로드의 패키지 검사

- 수정 소스에서 상태·좌표·Win32 ABI 검사 42개 통과.
- Release / win-x64 / self-contained 게시. .NET SDK나 런타임 설치 없이 실행하는 배포 방식.
- ZIP CRC 검사 통과 및 압축 해제본의 467개 파일 SHA-256 일치. 체크섬 목록 파일 자체는 별도 포함.
- coreclr.dll, hostfxr.dll, hostpolicy.dll, PresentationFramework.dll과 실행 EXE 존재 확인.
- Microsoft.NETCore.App 8.0.33, Microsoft.WindowsDesktop.App 8.0.33 런타임 포함.
- 압축 해제 EXE ProductVersion 0.1.2 확인. 창 없는 자체 복구 감시 시작 경로의 종료 코드 0 확인. 두 번째 상단 바를 띄우는 검사는 아님.
- 개인 설정·대화·로그·사용자 화면·인수인계 자료·빌드 캐시는 공개 소스에 포함하지 않음.

## ZIP

- 파일: `portable/GlassDock-TopBar-v0.1.2-win-x64.zip`
- 크기: **76,299,259바이트**
- SHA-256: `5b6f0d303f12cecdbab15ef3c1a69f09caa81ee86e3f7c4abd079edf6574b999`
- 저장소 ZIP과 GitHub Release 첨부는 동일한 파일.

## 직전 수정 단계에서 확인한 실제 UI 검사

트레이 아이콘 7개 표시와 각 영역의 실제 픽셀, Explorer 입력 대상, 테스트 NotifyIcon의 그림 변경·앱 콜백, 닫기·재열기·취소·스타일/자식 상태 복구를 확인했습니다. 최대화·복원 6회와 전체 화면·최소화 후 위치 유지·트레이 재열기, 실제 상단 바 화살표의 표시·닫기·재열기도 확인했습니다.

이번 업로드 단계에서 실행 중인 사용자 상단 바를 중단하거나 이 실제 UI 검사를 다시 반복하지 않았습니다. 소스 검사·게시·압축 검증을 새로 수행한 결과와 직전 UI 검사 결과를 구분합니다.

다른 PC·보조 모니터·DPI 전체 조합과 실제 카카오톡/Windows 알림 수신을 새로 검증하지 않았습니다. [0.1.2 릴리즈 노트](../Release%20Notes/Release%20Note0.1.2%20ver.md), [트레이 수정 검증](source/validation/트레이수정_0.1.2_2026-11-09.md)을 확인하세요.
