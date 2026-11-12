#!/usr/bin/env python3
"""AsterCache 트레이 아이콘 생성기.

인자 없이 실행하면 csharp/src/AsterCache.Tray/Assets 에 aster.ico,
aster.png(256x256), aster-tray.png(32x32) 를 저장하고 크기와 바이트를 출력합니다.
디자인: 남색 스쿼클 위의 청록 별과 그 뒤를 지나는 기울어진 궤도 링입니다.
각 크기는 4배(작은 크기는 8배) 크기로 그린 뒤 LANCZOS 로 축소하며,
작은 크기는 링을 굵게(32 px) 하거나 빼고(16 px) 별을 키워 판독성을 높입니다.
"""
import math
import os

from PIL import Image, ImageChops, ImageDraw, ImageFilter

NAVY_TOP = (44, 62, 107)     # #2C3E6B
NAVY_BOTTOM = (17, 26, 51)   # #111A33
TEAL_LIGHT = (123, 243, 226) # #7BF3E2
TEAL_DEEP = (37, 191, 196)   # #25BFC4
RING_LIGHT = (154, 247, 234)  # #9AF7EA
RING_DEEP = (47, 182, 200)    # #2FB6C8
ICO_SIZES = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.normpath(os.path.join(HERE, "..", "src", "AsterCache.Tray", "Assets"))


def gradient(s, c1, c2):
    """왼쪽 위(c1)에서 오른쪽 아래(c2)로 가는 대각선 그라데이션."""
    v = Image.linear_gradient("L").resize((s, s))
    t = ImageChops.add(v, v.rotate(90), scale=2)
    return Image.composite(Image.new("RGB", (s, s), c2), Image.new("RGB", (s, s), c1), t)


def squircle_mask(s, margin=0.02, n=5.0):
    """모서리가 부드러운 스쿼클(초타원) 마스크."""
    c, r = s / 2, s / 2 * (1 - 2 * margin)
    pts = []
    for i in range(720):
        a = 2 * math.pi * i / 720
        ca, sa = math.cos(a), math.sin(a)
        pts.append((c + r * math.copysign(abs(ca) ** (2 / n), ca), c + r * math.copysign(abs(sa) ** (2 / n), sa)))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).polygon(pts, fill=255)
    return mask


def star_mask(s, cx, cy, r_out, round_px):
    """꼭짓점이 살짝 둥근 오각 별 마스크 (블러 후 임계 처리로 둥글림)."""
    pts = []
    for i in range(10):
        r = r_out if i % 2 == 0 else r_out * 0.45
        a = -math.pi / 2 + i * math.pi / 5
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).polygon(pts, fill=255)
    return mask.filter(ImageFilter.GaussianBlur(round_px)).point(lambda v: 255 if v > 127 else 0)


def ring_mask(s, c, rx, ry, width, angle):
    """기울어진 타원 궤도 링 마스크."""
    mask = Image.new("L", (s, s), 0)
    d = ImageDraw.Draw(mask)
    d.ellipse([c - rx, c - ry, c + rx, c + ry], fill=255)
    d.ellipse([c - rx + width, c - ry + width, c + rx - width, c + ry - width], fill=0)
    return mask.rotate(angle, center=(c, c), resample=Image.BICUBIC)


def render(size, tray=False):
    """tray=True 이면 오른쪽 아래 상태 점에 가리지 않도록 그림을 왼쪽 위로 조금 옮깁니다.
    48 px 이상은 가는 링, 32 px 는 굵고 단순한 링, 16 px 는 별만 그립니다."""
    s = size * (4 if size >= 128 else 8)
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    img.paste(gradient(s, NAVY_TOP, NAVY_BOTTOM), (0, 0), squircle_mask(s))
    c = (0.45 if tray else 0.5) * s
    r_star, round_px = {16: (0.40, 0.03), 32: (0.31, 0.019)}.get(size, (0.27, 0.012))
    star = star_mask(s, c, c + 0.02 * s, r_star * s, round_px * s)  # 별의 무게중심 보정으로 2% 아래
    if size > 16:
        ring = ring_mask(s, c, 0.43 * s, 0.17 * s, (0.085 if size <= 32 else 0.045) * s, 28)
        # 별 둘레를 비워 링이 별 뒤로 지나가는 것처럼 보이게 함
        gap = star.filter(ImageFilter.GaussianBlur(0.03 * s)).point(lambda v: 255 if v > 30 else 0)
        ring = ImageChops.subtract(ring, gap).point(lambda v: int(v * 0.9))
        img.paste(gradient(s, RING_LIGHT, RING_DEEP), (0, 0), ring)
    img.paste(gradient(s, TEAL_LIGHT, TEAL_DEEP), (0, 0), star)
    return img.resize((size, size), Image.LANCZOS)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    frames = [render(w) for w, _ in ICO_SIZES]  # 크기별로 따로 그려 작은 크기의 판독성 확보
    big = frames[0]
    ico_path = os.path.join(OUT_DIR, "aster.ico")
    png_path = os.path.join(OUT_DIR, "aster.png")
    tray_path = os.path.join(OUT_DIR, "aster-tray.png")
    big.save(ico_path, format="ICO", sizes=ICO_SIZES, append_images=frames[1:])
    big.save(png_path, format="PNG")
    render(32, tray=True).save(tray_path, format="PNG")
    for path in (ico_path, png_path, tray_path):
        print(f"{path} {os.path.getsize(path)} bytes")


if __name__ == "__main__":
    main()
