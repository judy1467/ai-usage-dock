# 구현 검증 리포트

> 다른 에이전트가 만든 구현을 2026-11-09에 직접 빌드·실행·검증한 기록.
> **결론: 동작한다.** 다만 보안·완성도 결함 6건을 발견해 수정했고, 설계 대비 남은 격차가 있다.

![실행 중인 모습](assets/verified-running.png)

---

## 1. 검증 방법 (실제 수행)

```powershell
dotnet build AiUsageDock.slnx -c Release          # 경고 0, 오류 0
dotnet test  tests\AiUsageDock.Core.Tests          # 6/6 통과
# 실행 후 화면 캡처 + Win32 창/DPI/좌표 실측 + UI 자동화
```

- 프로세스 실행 → 6초 대기 → 실제 창 rect/DPI/스타일 열거
- 독 클릭 → 플라이아웃 캡처 → 바깥 클릭으로 닫기
- 두 번째 인스턴스 실행 → 단일 인스턴스 확인
- `%LOCALAPPDATA%\AiUsageDock\` 파일 상태 확인

## 2. 동작 확인 결과

| 항목 | 결과 | 근거 |
|---|---|---|
| 빌드 | 성공, 경고 0 | `dotnet build` |
| 단위 테스트 | 6/6 통과 | `dotnet test` |
| 딥시크 잔액 | 실제 값 표시 | `$13.69`, 오늘 사용 ≈ `$0.21` |
| 안티그래비티 쿼터 | 13개 모델 표시 | Argon 35%, Flash/Claude/GPT 100%, 리셋 시각 |
| 독 렌더링 | 정상 | 4타일 + 상태 점, 물리 642×69px (150% DPI) |
| 플라이아웃 | 정상 | 상세 목록, 핀 표시, Esc/바깥클릭 닫기 |
| 단일 인스턴스 | 정상 | 2번째 실행 시 안내 후 종료 |
| 트레이 아이콘 | 생성됨 | `WindowsForms10.Window` 숨은 창 확인 |

## 3. 발견 → 수정한 결함

| # | 심각도 | 문제 | 수정 |
|---|---|---|---|
| 1 | **Critical** | API 키를 **하드코딩 XOR**로 저장 (`secret.dat`이 사실상 평문) | DPAPI(`CurrentUser`)로 교체 + 구버전 XOR 파일 자동 마이그레이션 (`Stores.cs`) |
| 2 | Major | 독 높이 36 DIP < 콘텐츠 높이 → **하단 진행바 잘림** | 높이 46 DIP, 패딩 조정 (`MainWindow.xaml`) |
| 3 | Major | **트레이 아이콘 없음** → 창을 닫으면 앱 종료, 백그라운드 불가 | `TrayIconHost` 추가(런타임 드로잉 아이콘, 열기/새로고침/설정/종료) |
| 4 | Major | **단일 인스턴스 없음** → 독이 여러 개 겹침 | `Local\AiUsageDock.SingleInstance` 뮤텍스 |
| 5 | Major | **자동 시작 미구현** (설정값만 존재) | `StartupRegistrar` + 설정 창 체크박스 |
| 6 | Major | 플라이아웃 `Deactivated`에서 `Close()` → **InvalidOperationException** | `SafeClose()` + Dispatcher 지연 호출 (`FlyoutWindow.xaml.cs`) |
| 7 | Minor | 500ms 타이머마다 `settings.json` 재읽기 | 메모리 캐시(`_settings`)로 교체 |
| 8 | Minor | 의미 불명의 `icacls /setintegritylevel` 빌드 타깃 | 제거 |
| 9 | Minor | WinForms 분석기 경고(WFO0003) | `NoWarn` 처리(매니페스트 DPI 설정 유지) |

> 6번은 실제로 `startup.log`에 스택 트레이스가 쌓여 있었고, 크래시는 아니지만 플라이아웃이 닫히지 않을 수 있는 경로였다.

## 4. 설계(DESIGN.md) 대비 남은 격차

| 항목 | 상태 | 비고 |
|---|---|---|
| 캐시 영속화 (`cache.json`) | 없음 | 재시작 후 첫 fetch 전까지 `$--.--`. 설계 5.5 |
| 전체화면 감지·숨김 | 없음 | 전체화면 앱 위에 독이 뜰 수 있음. 설계 8.5 |
| 작업표시줄 자동 숨김 대응 | 없음 | `ABM_GETSTATE` 미사용(죽은 코드 존재) |
| 독 위치 | 작업표시줄 **바로 위** | 설계는 "작업표시줄 위에 겹침". 동작상 문제는 없음 |
| 7일 스파크라인 | 없음 | `history.jsonl`은 쌓이고 있음 |
| 저잔액/저쿼터 알림 | 없음 | 설정 모델만 존재 |
| 보조 모니터 작업표시줄 | 미지원 | `SystemParameters.WorkArea` = 주 모니터 기준 |
| 통합 테스트 | 없음 | 파서 단위 테스트 6개뿐 |

## 5. 실행

```powershell
cd C:\Users\judy1\Desktop\deepseekharness\ai-usage-dock
dotnet run --project src\AiUsageDock.App -c Release
```

- 설정: 독 우클릭 → 설정, 또는 트레이 우클릭 → 설정
- API 키: `%LOCALAPPDATA%\AiUsageDock\secret.dat` (DPAPI 암호문)
- **빌드 전에 실행 중인 앱을 종료**해야 한다(파일 잠금).

## 6. 참고

- 화면이 150% 배율이면 PowerShell이 비인식 프로세스라 좌표가 1.5배 축소되어 보인다.
  검증 시 `SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)`로 물리 좌표를 사용했다.
- 트레이 아이콘은 작업표시줄이 전체화면 브라우저에 가려져 있으면 육안 확인이 어렵다.
  (숨은 WinForms 창 존재로 생성은 확인)
