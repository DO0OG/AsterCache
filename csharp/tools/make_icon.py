#!/usr/bin/env python3
"""AsterCache 트레이 아이콘 생성기.

인자 없이 실행하면 csharp/src/AsterCache.Tray/Assets 에 aster.ico,
aster.png(256x256), aster-tray.png(32x32) 를 저장하고 크기와 바이트를 출력합니다.
각 크기는 4배 크기로 그린 뒤 LANCZOS 로 축소합니다.
"""
import math
import os

from PIL import Image, ImageDraw

BG = (31, 42, 68, 255)       # #1F2A44
TEAL = (62, 220, 203, 255)   # #3EDCCB
WHITE = (255, 255, 255, 255)
SUPERSAMPLE = 4
ICO_SIZES = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.normpath(os.path.join(HERE, "..", "src", "AsterCache.Tray", "Assets"))

# 플래터 바깥 오른쪽 아래의 번개 모양 (정규화 좌표, 0~1)
BOLT = [(0.84, 0.60), (0.70, 0.78), (0.79, 0.78), (0.73, 0.92), (0.90, 0.72), (0.80, 0.72), (0.86, 0.60)]


def star_points(cx, cy, r_out, r_in, n=5):
    pts = []
    for i in range(2 * n):
        r = r_out if i % 2 == 0 else r_in
        a = -math.pi / 2 + i * math.pi / n
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return pts


def render(size):
    s = size * SUPERSAMPLE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=0.2 * s, fill=BG)
    cx = cy = 0.44 * s
    r_platter = 0.31 * s
    d.ellipse([cx - r_platter, cy - r_platter, cx + r_platter, cy + r_platter], fill=WHITE)
    d.polygon(star_points(cx, cy, 0.25 * s, 0.105 * s), fill=TEAL)
    r_hole = 0.04 * s
    d.ellipse([cx - r_hole, cy - r_hole, cx + r_hole, cy + r_hole], fill=BG)
    d.polygon([(x * s, y * s) for x, y in BOLT], fill=TEAL)
    return img.resize((size, size), Image.LANCZOS)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    big = render(256)
    ico_path = os.path.join(OUT_DIR, "aster.ico")
    png_path = os.path.join(OUT_DIR, "aster.png")
    tray_path = os.path.join(OUT_DIR, "aster-tray.png")
    big.save(ico_path, format="ICO", sizes=ICO_SIZES)
    big.save(png_path, format="PNG")
    render(32).save(tray_path, format="PNG")
    for path in (ico_path, png_path, tray_path):
        print(f"{path} {os.path.getsize(path)} bytes")


if __name__ == "__main__":
    main()
