# AI Usage Dock

Windows 11 작업표시줄에 붙어서 **딥시크 API 잔액**과 **안티그래비티 모델별 잔여 쿼터**를
클릭 없이 보여주는 로컬 전용 위젯. (.NET 10 / WPF)

![실행 모습](assets/verified-running.png)

## 빠른 실행

```powershell
dotnet run --project src\AiUsageDock.App -c Release
```

- 독(작업표시줄 바로 위)을 클릭하면 상세 패널이 열립니다.
- 설정: 독 우클릭 → 설정, 또는 트레이 아이콘 우클릭 → 설정
- API 키는 `%LOCALAPPDATA%\AiUsageDock\secret.dat`에 **DPAPI**로 암호화되어 저장됩니다.

## 파일

| 경로 | 내용 |
|---|---|
| [`DESIGN.md`](DESIGN.md) | 설계도 — 요구사항, 데이터 소스 실측, 아키텍처, UI, Win32, 보안, 테스트 |
| [`REVIEW.md`](REVIEW.md) | **구현 검증 리포트** — 발견/수정한 결함, 설계 대비 남은 격차 |
| `src/AiUsageDock.Core` | 모델, 딥시크/안티그래비티 어댑터, 저장소, 스케줄러 |
| `src/AiUsageDock.App` | WPF 독/플라이아웃/설정, 트레이, Win32 interop |
| `tests/AiUsageDock.Core.Tests` | 파서·그룹핑 단위 테스트 (6개) |
| `assets/` | 목업 및 실행 스크린샷 |
| `tools/probe.ps1` | 두 데이터 소스를 직접 호출해 검증하는 스크립트 |
| `tools/make_mocks.py` | 목업 PNG 재생성 |

## 핵심 사실 (2026-11-09, 이 PC 실측)

- **딥시크**: `GET https://api.deepseek.com/user/balance` → 200. 금액은 문자열.
- **안티그래비티**: 실행 중인 앱의 로컬 RPC
  `https://127.0.0.1:<동적포트>/exa.language_server_pb.LanguageServerService/GetUserStatus`
  → 200, 13개 모델. `x-codeium-csrf-token` 필수(프로세스 인자에서 추출).
- **작업표시줄**: 데스크밴드는 Win11에서 불가. 최상위 `WS_POPUP` 오버레이로 구현.

## 검증

```powershell
dotnet build AiUsageDock.slnx -c Release   # 경고 0
dotnet test  tests\AiUsageDock.Core.Tests  # 6/6 통과
```

> 빌드 전에 실행 중인 앱을 종료하세요(파일 잠금).
