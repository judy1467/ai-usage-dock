# -*- coding: utf-8 -*-
"""Generate design-doc mockups (PNG) for the AI Usage Dock project.

Deterministic: no network, no external assets. Run with:
    python tools/make_mocks.py
"""
from __future__ import annotations

import math
import os
from PIL import Image, ImageDraw, ImageFilter, ImageFont

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "assets")
os.makedirs(OUT, exist_ok=True)

S = 2  # supersample factor
KR = "C:/Windows/Fonts/malgun.ttf"
KRB = "C:/Windows/Fonts/malgunbd.ttf"
EN = "C:/Windows/Fonts/segoeui.ttf"
ENB = "C:/Windows/Fonts/segoeuib.ttf"

C_BG = (11, 15, 26)
C_TASKBAR = (31, 31, 31)
C_PANEL = (27, 27, 31)
C_PANEL2 = (33, 33, 38)
C_BORDER = (56, 56, 63)
C_TXT = (230, 237, 243)
C_TXT2 = (139, 148, 158)
C_TXT3 = (110, 118, 129)
C_GREEN = (63, 185, 80)
C_AMBER = (210, 153, 34)
C_RED = (248, 81, 73)
C_BLUE = (79, 140, 255)
C_DS = (77, 107, 254)
C_AG = (167, 139, 250)
C_TRACK = (48, 54, 61)


def font(path: str, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(path, int(size * S))


def rr(d: ImageDraw.ImageDraw, box, r, fill=None, outline=None, width=1):
    d.rounded_rectangle([v * S for v in box], radius=r * S, fill=fill, outline=outline, width=width * S)


def text(d, xy, s, f, fill, anchor="la"):
    d.text((xy[0] * S, xy[1] * S), s, font=f, fill=fill, anchor=anchor)


def tw(d, s, f) -> float:
    return d.textlength(s, font=f) / S


def bar(d, x, y, w, h, frac, color, track=C_TRACK):
    rr(d, (x, y, x + w, y + h), h / 2, fill=track)
    if frac > 0:
        rr(d, (x, y, x + max(h, w * frac), y + h), h / 2, fill=color)


def status_color(frac: float):
    if frac <= 0.15:
        return C_RED
    if frac <= 0.40:
        return C_AMBER
    return C_GREEN


def wallpaper(w, h) -> Image.Image:
    img = Image.new("RGB", (w * S, h * S), C_BG)
    d = ImageDraw.Draw(img)
    for y in range(h * S):
        t = y / (h * S)
        c = (int(9 + 12 * t), int(12 + 16 * t), int(24 + 26 * t))
        d.line([(0, y), (w * S, y)], fill=c)
    # soft glow blobs
    glow = Image.new("RGB", (w * S, h * S), (0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse([(-120 * S, 40 * S), (420 * S, 430 * S)], fill=(26, 34, 64))
    gd.ellipse([(980 * S, -60 * S), (1500 * S, 300 * S)], fill=(38, 24, 56))
    glow = glow.filter(ImageFilter.GaussianBlur(90 * S))
    img = Image.blend(img, Image.blend(img, glow, 0.55), 0.9)
    d = ImageDraw.Draw(img)
    # subtle dot grid
    for x in range(0, w, 32):
        for y in range(0, h, 32):
            d.point((x * S, y * S), fill=(38, 44, 60))
    return img


def taskbar(img, w, h, strip_rect=None):
    d = ImageDraw.Draw(img, "RGBA")
    ty = h - 48
    d.rectangle([0, ty * S, w * S, h * S], fill=C_TASKBAR + (242,))
    d.line([(0, ty * S), (w * S, ty * S)], fill=(58, 58, 58), width=1 * S)

    # centered app icons
    cx = w // 2 - 190
    icons = [
        ("start", C_BLUE),
        ("search", (200, 205, 214)),
        ("widgets", (200, 205, 214)),
        ("explorer", (255, 196, 87)),
        ("edge", (90, 190, 240)),
        ("code", (120, 170, 255)),
        ("term", (200, 205, 214)),
    ]
    for i, (kind, col) in enumerate(icons):
        x = cx + i * 46
        if kind == "start":
            for dx in (0, 8):
                for dy in (0, 8):
                    rr(d, (x + dx, ty + 12 + dy, x + dx + 6, ty + 12 + dy + 6), 1.2, fill=col)
        elif kind == "search":
            d.ellipse([(x + 2) * S, (ty + 13) * S, (x + 12) * S, (ty + 23) * S], outline=col, width=int(1.6 * S))
            d.line([(x + 13) * S, (ty + 24) * S, (x + 18) * S, (ty + 29) * S], fill=col, width=int(1.8 * S))
        elif kind == "widgets":
            rr(d, (x, ty + 13, x + 7, ty + 20), 1, fill=col)
            rr(d, (x + 9, ty + 13, x + 17, ty + 20), 1, fill=col)
            rr(d, (x, ty + 23, x + 7, ty + 30), 1, fill=col)
            rr(d, (x + 9, ty + 23, x + 17, ty + 30), 1, fill=col)
        elif kind == "explorer":
            rr(d, (x, ty + 15, x + 18, ty + 30), 1.6, fill=col)
            rr(d, (x, ty + 12, x + 8, ty + 16), 1.2, fill=col)
        elif kind == "edge":
            d.arc([(x + 1) * S, (ty + 12) * S, (x + 18) * S, (ty + 31) * S], 200, 20, fill=col, width=int(2.2 * S))
            d.arc([(x + 1) * S, (ty + 12) * S, (x + 18) * S, (ty + 31) * S], 20, 120, fill=col, width=int(2.2 * S))
        elif kind == "code":
            d.line([(x + 2) * S, (ty + 15) * S, (x + 17) * S, (ty + 30) * S], fill=col, width=int(2.2 * S))
            d.line([(x + 17) * S, (ty + 15) * S, (x + 2) * S, (ty + 30) * S], fill=col, width=int(2.2 * S))
        else:
            rr(d, (x, ty + 13, x + 18, ty + 31), 2, outline=col, width=int(1.6 * S))
            text(d, (x + 4.5, ty + 18.5), ">_", font(EN, 5.5), col)

    # tray
    tx = w - 250
    for i in range(3):
        rr(d, (tx + i * 22, ty + 20, tx + i * 22 + 9, ty + 26), 1.4, fill=(200, 205, 214))
    text(d, (w - 150, ty + 12), "오전 1:12", font(KR, 8), C_TXT)
    text(d, (w - 150, ty + 25), "2026-11-09", font(KR, 8), C_TXT)
    text(d, (w - 44, ty + 18), "▲", font(EN, 7), (200, 205, 214))

    if strip_rect:
        dock_strip(img, *strip_rect)


def dock_strip(img, x, y, w, h, state="ok", dim_ag=False):
    d = ImageDraw.Draw(img, "RGBA")
    # shadow
    sh = Image.new("RGBA", img.size, (0, 0, 0, 0))
    sd = ImageDraw.Draw(sh)
    sd.rounded_rectangle([(x) * S, (y + 2) * S, (x + w) * S, (y + h + 3) * S], radius=9 * S, fill=(0, 0, 0, 120))
    sh = sh.filter(ImageFilter.GaussianBlur(6 * S))
    img.alpha_composite(sh) if img.mode == "RGBA" else img.paste(Image.alpha_composite(img.convert("RGBA"), sh).convert("RGB"), (0, 0))

    rr(d, (x, y, x + w, y + h), 9, fill=(30, 30, 34, 246), outline=(190, 70, 60) if state == "error" else C_BORDER, width=1)

    tiles = [
        ("DeepSeek", "$12.34", C_DS, None, None),
        ("Gemini 4", "87%", C_AG, 0.8723, C_GREEN),
        ("Flash 3.8", "99%", C_AG, 0.9954, C_GREEN),
        ("Claude 5.5", "100%", C_AG, 1.0, C_GREEN),
    ]
    pad = 10
    widths = [92, 98, 98, 98]
    cx = x + pad
    stale = state == "stale"
    for i, (label, value, accent, frac, col) in enumerate(tiles):
        tw_ = widths[i]
        if i:
            d.line([(cx - 6) * S, (y + 9) * S, (cx - 6) * S, (y + h - 9) * S], fill=(58, 58, 63), width=1 * S)
        dim = (dim_ag or stale) and i > 0
        hide_value = dim_ag and i > 0
        tile_value = "—" if hide_value else value
        label_col = (95, 102, 112) if stale and i > 0 else ((95, 100, 110) if dim else C_TXT2)
        value_col = (148, 155, 165) if stale and i > 0 else ((120, 125, 135) if dim else C_TXT)

        # accent dot
        d.ellipse([(cx) * S, (y + 7) * S, (cx + 4) * S, (y + 11) * S], fill=accent if not dim else (90, 80, 110))
        text(d, (cx + 7, y + 6), label, font(KR, 6.5), label_col)
        text(d, (cx + 7, y + 15), tile_value, font(ENB, 10), value_col)
        if frac is not None:
            bar_frac = 0 if hide_value else frac
            bar_col = col or status_color(frac)
            if dim:
                bar_col = (46, 112, 58) if stale else (80, 80, 80)
            bar(d, cx + 7, y + h - 9, tw_ - 12, 2.4, bar_frac, bar_col)
        cx += tw_

    # status dot at right edge
    dot = C_GREEN
    if state == "down":
        dot = (120, 120, 130)
    elif state == "stale":
        dot = C_AMBER
    elif state == "error":
        dot = C_RED
    d.ellipse([(x + w - 9) * S, (y + 6) * S, (x + w - 5) * S, (y + 10) * S], fill=dot)
    if state == "error":
        text(d, (x + w - 16, y + 7), "!", font(ENB, 8.5), C_RED)


def flyout(img, x, y, w, h):
    d = ImageDraw.Draw(img, "RGBA")
    sh = Image.new("RGBA", img.size, (0, 0, 0, 0))
    sd = ImageDraw.Draw(sh)
    sd.rounded_rectangle([x * S, (y + 6) * S, (x + w) * S, (y + h + 8) * S], radius=14 * S, fill=(0, 0, 0, 160))
    sh = sh.filter(ImageFilter.GaussianBlur(14 * S))
    img.paste(Image.alpha_composite(img.convert("RGBA"), sh).convert("RGB"), (0, 0))

    rr(d, (x, y, x + w, y + h), 13, fill=(24, 24, 28, 252), outline=C_BORDER, width=1)

    # header
    text(d, (x + 20, y + 18), "AI 사용량", font(KR, 11), C_TXT)
    text(d, (x + w - 66, y + 20), "새로고침", font(KR, 7.2), C_TXT3)
    for i in range(3):
        d.ellipse([(x + w - 30 + i * 5) * S, (y + 22) * S, (x + w - 27.4 + i * 5) * S, (y + 24.6) * S], fill=C_TXT3)
    d.line([(x + 14) * S, (y + 44) * S, (x + w - 14) * S, (y + 44) * S], fill=(48, 48, 54), width=1 * S)

    # DeepSeek block
    cy = y + 56
    text(d, (x + 20, cy), "DEEPSEEK API", font(ENB, 6.5), C_TXT3)
    text(d, (x + 20, cy + 12), "$12.34", font(ENB, 24), C_TXT)
    rr(d, (x + 132, cy + 18, x + 182, cy + 30), 6, fill=(30, 60, 40, 200))
    text(d, (x + 138, cy + 20.5), "사용 가능", font(KR, 6.2), C_GREEN)
    text(d, (x + 20, cy + 44), "충전 $10.00   ·   보너스 $2.34   ·   USD", font(KR, 6.8), C_TXT2)
    text(d, (x + 20, cy + 57), "오늘 사용 -$0.42   (00:00 이후 잔액 차감)", font(KR, 6.8), C_AMBER)

    # 7-day balance sparkline
    pts = [12.98, 12.91, 12.86, 12.74, 12.68, 12.52, 12.34]
    sx0, sy0, sw, shh = x + 250, cy + 6, 150, 52
    lo, hi = min(pts) - 0.06, max(pts) + 0.06
    xy = [(sx0 + i * sw / (len(pts) - 1), sy0 + shh - (v - lo) / (hi - lo) * shh) for i, v in enumerate(pts)]
    poly = xy + [(sx0 + sw, sy0 + shh), (sx0, sy0 + shh)]
    d.polygon([(p[0] * S, p[1] * S) for p in poly], fill=(77, 107, 254, 46))
    d.line([(p[0] * S, p[1] * S) for p in xy], fill=(110, 140, 255), width=int(1.6 * S), joint="curve")
    d.ellipse([(xy[-1][0] - 2.2) * S, (xy[-1][1] - 2.2) * S, (xy[-1][0] + 2.2) * S, (xy[-1][1] + 2.2) * S], fill=(140, 165, 255))
    text(d, (sx0, sy0 + shh + 8), "최근 7일 잔액", font(KR, 5.8), C_TXT3)

    # Antigravity block
    ay = y + 150
    d.line([(x + 14) * S, (ay - 8) * S, (x + w - 14) * S, (ay - 8) * S], fill=(48, 48, 54), width=1 * S)
    text(d, (x + 20, ay), "ANTIGRAVITY", font(ENB, 6.5), C_TXT3)
    rr(d, (x + 96, ay - 2, x + 122, ay + 8), 5, fill=(48, 36, 20, 220))
    text(d, (x + 102, ay - 0.2), "Pro", font(ENB, 6), (230, 180, 90))
    d.ellipse([(x + 132) * S, (ay + 1.5) * S, (x + 138) * S, (ay + 7.5) * S], fill=C_GREEN)
    text(d, (x + 142, ay - 0.5), "앱 실행 중 · 30초 주기", font(KR, 6.4), C_TXT2)

    rows = [
        ("Gemini 4 Argon", "87%", 0.8723, "11/15 22:54 리셋", C_GREEN),
        ("Gemini 3.8 Flash", "99%", 0.9954, "11/09 03:55 리셋", C_GREEN),
        ("Claude Opus 5.5", "100%", 1.0, "11/09 06:01 리셋", C_GREEN),
        ("Claude Sonnet 5.5", "100%", 1.0, "11/09 06:01 리셋", C_GREEN),
        ("GPT-OSS 120B", "100%", 1.0, "11/09 06:01 리셋", C_GREEN),
    ]
    ry = ay + 26
    for label, pct, frac, reset, col in rows:
        text(d, (x + 20, ry), label, font(KR, 8.2), C_TXT)
        text(d, (x + w - 20, ry), pct, font(ENB, 8.6), C_TXT, anchor="ra")
        bar(d, x + 20, ry + 15, w - 40, 3.2, frac, col)
        text(d, (x + 20, ry + 22), reset, font(KR, 6.2), C_TXT3)
        ry += 47

    text(d, (x + 20, ry + 4), "모델 13개 · 쿼터 그룹 3개 · 5시간/주간 리셋", font(KR, 6.2), C_TXT3)
    text(d, (x + 20, ry + 18), "행을 클릭하면 작업표시줄에 고정 · 설정에서 주기 변경", font(KR, 6.2), C_TXT3)

    text(d, (x + 20, y + h - 22), "마지막 갱신 01:12:30 · 자동 30초/5분 · v1.0", font(KR, 6.2), C_TXT3)


def build_overview():
    w, h = 1600, 820
    img = wallpaper(w, h).convert("RGB")
    # window hint behind flyout (an editor-ish backdrop, abstract)
    d = ImageDraw.Draw(img, "RGBA")
    rr(d, (120, 90, 900, 700), 12, fill=(18, 20, 28, 180), outline=(40, 44, 56), width=1)
    for i in range(16):
        text(d, (150, 120 + i * 22), ["def main():", "    args = parse()", '    print("hello")', "", "class Dock:", "    def __init__(self):", "        self.timer = ..."][i % 7], font(EN, 7), (70, 80, 100))
    flyout(img, 1000, 250, 430, 470)
    taskbar(img, w, h, strip_rect=(920, h - 42, 410, 34))
    img = img.resize((w, h), Image.LANCZOS)
    img.save(os.path.join(OUT, "mock-overview.png"))


def build_states():
    w, h = 1240, 360
    img = Image.new("RGB", (w * S, h * S), (24, 24, 28))
    d = ImageDraw.Draw(img, "RGBA")
    d.rectangle([0, 0, w * S, h * S], fill=(24, 24, 28))
    states = [
        ("정상", "ok", "두 소스 모두 신선", C_GREEN),
        ("일부 지연", "stale", "마지막 성공 12분 전 (60% 흐림)", C_AMBER),
        ("Antigravity 종료", "down", "안티그래비티 타일 — · 딥시크 잔액 정상 표시", (150, 150, 160)),
        ("오류", "error", "키 만료 / 네트워크 실패 (! 표시)", C_RED),
    ]
    x0, y0 = 70, 46
    for i, (title, st, desc, col) in enumerate(states):
        yy = y0 + i * 74
        text(d, (x0, yy + 4), title, font(KR, 9), C_TXT)
        d.ellipse([(x0 - 14) * S, (yy + 7) * S, (x0 - 8) * S, (yy + 13) * S], fill=col)
        dock_strip(img, x0 + 120, yy, 410, 34, state=st, dim_ag=(st == "down"))
        text(d, (x0 + 560, yy + 8), desc, font(KR, 6.6), C_TXT2)
    img = img.resize((w, h), Image.LANCZOS)
    img.save(os.path.join(OUT, "mock-states.png"))


def arrow(d, x1, y1, x2, y2, col=(90, 96, 110), width=1.6, head=6):
    d.line([(x1 * S, y1 * S), (x2 * S, y2 * S)], fill=col, width=int(width * S))
    ang = math.atan2(y2 - y1, x2 - x1)
    for s in (0.5, -0.5):
        d.line(
            [
                (x2 * S, y2 * S),
                ((x2 - head * math.cos(ang - s)) * S, (y2 - head * math.sin(ang - s)) * S),
            ],
            fill=col,
            width=int(width * S),
        )


def build_arch():
    w, h = 1400, 600
    img = Image.new("RGB", (w * S, h * S), (17, 20, 28))
    d = ImageDraw.Draw(img, "RGBA")
    for i in range(h * S):
        t = i / (h * S)
        d.line([(0, i), (w * S, i)], fill=(int(15 + 6 * t), int(18 + 7 * t), int(26 + 9 * t)))

    def box(x, y, bw, bh, title, lines, accent=(90, 110, 150), fill=(26, 30, 40, 235)):
        rr(d, (x, y, x + bw, y + bh), 9, fill=fill, outline=accent + (110,) if len(accent) == 3 else accent, width=1)
        d.rounded_rectangle([x * S, y * S, (x + 4) * S, (y + bh) * S], radius=2 * S, fill=accent + (220,))
        text(d, (x + 14, y + 10), title, font(KRB, 8.5), (226, 232, 240))
        for i, ln in enumerate(lines):
            text(d, (x + 14, y + 28 + i * 16), ln, font(KR, 6.8), (150, 160, 175))

    text(d, (60, 34), "AI Usage Dock — 컴포넌트 구조", font(KRB, 13), (236, 241, 248))
    text(d, (60, 58), "단일 프로세스(.NET 10 / WPF) · 로컬 전용 · 외부 전송 없음", font(KR, 7), (130, 140, 155))

    # sources
    box(60, 110, 340, 92, "딥시크 공식 REST", ["GET api.deepseek.com/user/balance", "Bearer key · 5분 주기"], (77, 107, 254))
    box(60, 226, 340, 110, "안티그래비티 로컬 RPC", [
        "language_server.exe 탐색 → --csrf_token",
        "PID TCP 테이블 + main.log → 동적 포트 (30초 주기)",
        "GetUserStatus (비공식)",
    ], (167, 139, 250))

    # adapters
    box(470, 110, 330, 92, "DeepSeekAdapter", ["파싱 · decimal 변환", "잔액 델타 → 일일 사용액"], (77, 107, 254))
    box(470, 226, 330, 110, "AntigravityAdapter", [
        "프로세스/포트 자동 발견 (PID 기반)",
        "쿼터 그룹핑 (동일 reset)",
        "상태: OK·STALE·DOWN·AUTH",
    ], (167, 139, 250))

    # core
    box(870, 150, 250, 130, "UsageCore", ["Scheduler (주기·백오프)", "UsageStore (스냅샷)", "Health 집계"], (79, 140, 255))
    box(1150, 150, 195, 130, "Storage", ["settings.json · secret.dat", "cache.json · history.jsonl", "로테이션 로그 (7일)"], (110, 118, 129))

    # ui
    box(870, 340, 475, 120, "Shell (UI)", ["TaskbarDockWindow · FlyoutWindow", "TrayIcon · SettingsWindow", "DPI/모니터/전체화면·Z-Order 대응"], (63, 185, 80))

    # security strip
    box(470, 372, 330, 96, "Security (cross-cutting)", ["DPAPI(CurrentUser) 키 보관", "CSRF 토큰은 메모리만", "PII(이름·메일) 미저장"], (210, 153, 34))

    # arrows
    arrow(d, 400, 156, 470, 156)
    arrow(d, 400, 281, 470, 281)
    arrow(d, 800, 156, 870, 190)
    arrow(d, 800, 281, 870, 240)
    arrow(d, 1120, 205, 1150, 205)
    arrow(d, 1150, 225, 1120, 225)
    arrow(d, 800, 415, 870, 260)
    arrow(d, 995, 280, 995, 340)

    text(d, (60, 386), "데이터 흐름", font(KRB, 8), (200, 208, 220))
    text(d, (60, 404), "① 30초/5분 타이머 → ② 어댑터 fetch → ③ 정규화 스냅샷", font(KR, 6.6), (150, 160, 175))
    text(d, (60, 420), "④ Store 갱신 이벤트 → ⑤ WPF UI 스레드에서 렌더", font(KR, 6.6), (150, 160, 175))
    text(d, (60, 436), "⑥ 실패 시 지수 백오프 + 마지막 값 유지(흐림 표시)", font(KR, 6.6), (150, 160, 175))

    text(d, (60, 488), "스레드 모델", font(KRB, 8), (200, 208, 220))
    text(d, (60, 506), "UI 스레드: 창/아이콘 렌더만 · I/O는 전부 비동기 Task", font(KR, 6.6), (150, 160, 175))
    text(d, (60, 522), "동시 fetch 2개 제한 · 3초 타임아웃 · 취소 토큰", font(KR, 6.6), (150, 160, 175))

    img = img.resize((w, h), Image.LANCZOS)
    img.save(os.path.join(OUT, "architecture.png"))


def build_flyout_only():
    w, h = 470, 510
    img = Image.new("RGB", (w * S, h * S), (16, 18, 26))
    flyout(img, 20, 20, 430, 470)
    img = img.resize((w, h), Image.LANCZOS)
    img.save(os.path.join(OUT, "mock-flyout.png"))


if __name__ == "__main__":
    build_overview()
    build_states()
    build_flyout_only()
    build_arch()
    print("wrote:", os.listdir(OUT))
