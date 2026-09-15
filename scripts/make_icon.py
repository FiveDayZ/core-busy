#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
生成 CORE-BUSY 应用图标（任务栏 / 窗口 / EXE 共用）。

设计语言：**CPU 芯片**。
  · 引脚    —— 四边各 3 根（≤24px 降为 2 根），冷灰金属色，圆头引线
  · 芯片本体 —— 圆角方 + 纵向渐变 #4E4E5E → #212127，1px 冷灰描边
  · 管芯    —— 本体正中一块暖色渐变方块（琥珀 → 红），直读「核心 / 负载」
  · 外辉光  —— ≥64px 才画，暖色微光溢出，暗示发热

「芯片 + 炽热管芯」同时承载 CPU 语义与「核心繁忙」主题，取代旧版的
四根柱状条——后者在任务栏尺寸下会被读成柱状图而非处理器。

小而准：每个尺寸在 8x/4x 超采样下**独立重绘**（而非从 256 缩放），
≤24px 时加粗引脚、放大管芯、去掉辉光，保证 16px 下仍是一颗芯片。

输出：
  src/CoreBusy.App/Assets/CoreBusy.ico   （16/20/24/32/40/48/64/128/256）
  docs/icon-preview.png                  （设计预览，供人工核对）
"""

import os
import struct
import sys

from PIL import Image, ImageDraw, ImageFilter

# ============================ 路径 ============================

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ICO_OUT = os.path.join(REPO, "src", "CoreBusy.App", "Assets", "CoreBusy.ico")
PREVIEW_OUT = os.path.join(REPO, "docs", "icon-preview.png")

# ============================ 设计令牌 ============================

TILE_TOP = (0x4E, 0x4E, 0x5E)
TILE_BOTTOM = (0x21, 0x21, 0x27)
# 小尺寸（≤24px）用更亮、更平的瓦片：深色任务栏下轮廓才立得住，渐变太强反而糊。
TILE_TOP_SMALL = (0x56, 0x56, 0x68)
TILE_BOTTOM_SMALL = (0x2E, 0x2E, 0x38)

RIM = (0x9E, 0x9E, 0xB4, 0xDC)
RIM_SMALL = (0xB6, 0xB6, 0xC8, 0xFF)

PIN = (0xA6, 0xA6, 0xBA, 0xFF)
PIN_SMALL = (0xC2, 0xC2, 0xD2, 0xFF)

# 管芯：热力图色带的中高段（琥珀 → 红），是整枚图标唯一的暖色焦点
DIE_TOP = (0xF2, 0xA5, 0x2C)
DIE_MID = (0xE5, 0x6A, 0x30)
DIE_BOTTOM = (0xDE, 0x3A, 0x42)
GLOW = (0xE8, 0x7A, 0x2A)

ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def _rounded_mask(size, box, radius):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box, radius=radius, fill=255)
    return mask


def _vertical_gradient(width, height, top, bottom, mid=None):
    """纵向渐变：先画 1px 宽的色柱，再横向复制（NEAREST 不引入插值误差）。"""
    strip = Image.new("RGB", (1, height))
    px = strip.load()
    span = max(1, height - 1)
    for y in range(height):
        t = y / span
        if mid is not None and t < 0.5:
            a, b, u = top, mid, t * 2
        elif mid is not None:
            a, b, u = mid, bottom, (t - 0.5) * 2
        else:
            a, b, u = top, bottom, t
        px[0, y] = (
            round(a[0] + (b[0] - a[0]) * u),
            round(a[1] + (b[1] - a[1]) * u),
            round(a[2] + (b[2] - a[2]) * u),
        )
    return strip.resize((width, height), Image.NEAREST)


def _glow_layer(canvas_size, box, radius, color, blur, peak_alpha):
    """以 box 为光源的柔光层：高斯模糊后上色。

    早先用「同心圆角矩形逐层叠加」实现，但 alpha_composite 是累积式的，
    靠近光源处会迅速饱和成一条硬亮带，远端又骤降为 0——不是柔光。
    模糊实现的衰减天然连续，且只需一次滤波。
    """
    mask = Image.new("L", (canvas_size, canvas_size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box, radius=radius, fill=255)
    mask = mask.filter(ImageFilter.GaussianBlur(blur))

    layer = Image.new("RGBA", (canvas_size, canvas_size), color[:3] + (0,))
    layer.putalpha(mask.point(lambda v: round(v * peak_alpha / 255.0)))
    return layer


def render(size, supersample=None):
    """按目标像素尺寸独立绘制（内部超采样后 LANCZOS 收敛）。"""
    if supersample is None:
        supersample = 8 if size <= 64 else 4

    s = size * supersample
    small = size <= 24
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # 线宽/尺寸统一按「目标像素」定义再乘超采样倍数 —— 否则 16px 下 0.75px 的
    # 折算会让轮廓直接消失（这是小尺寸可辨性的关键）。
    def px(v):
        return max(1, round(v * supersample))

    # ---- 几何：以整幅画布的比例定义 ----
    # 大尺寸引脚短而密（像 LGA 焊盘）；小尺寸引脚画粗画短，否则 16px 只剩几个
    # 1px 孤点，既不成形也糊成一团。
    if small:
        body_lo, body_hi = 0.215, 0.785
        pin_w = 0.095 * s
        pin_out = 0.058 * s
        pin_offsets = [-0.150, 0.150]
    else:
        body_lo, body_hi = 0.205, 0.795
        pin_w = 0.060 * s
        pin_out = 0.112 * s
        pin_offsets = [-0.185, 0.0, 0.185]

    body_r = (body_hi - body_lo) * s         # 本体边长
    radius = body_r * 0.130
    pin_reach = body_lo * s + px(2)          # 内端插进本体，接缝由本体覆盖

    pin_color = PIN_SMALL if small else PIN
    rim_color = RIM_SMALL if small else RIM

    # 本体与管芯都落在整像素网格上，后续渐变/遮罩按各自边长创建（而非整幅画布），
    # 否则 paste 的 mask 尺寸不匹配。
    b_lo = int(round(body_lo * s))
    b_hi = int(round(body_hi * s))
    body_h = b_hi - b_lo
    box = (b_lo, b_lo, b_hi, b_hi)

    # 管芯：小尺寸下相对本体放大，否则 16px 只剩 4px 暖点，色块读不出来
    die_pad = int(round((0.200 if small else 0.262) * body_r))
    d_lo, d_hi = b_lo + die_pad, b_hi - die_pad
    die_h = d_hi - d_lo
    die_box = (d_lo, d_lo, d_hi, d_hi)

    # ---- 1. 引脚（先画，内端由本体盖住，接缝干净） ----
    center = s / 2.0
    d = ImageDraw.Draw(img)
    for off in pin_offsets:
        p = center + off * s - pin_w / 2.0
        for rect in (
            (pin_out, p, pin_reach, p + pin_w),                       # 左
            (s - pin_reach, p, s - pin_out, p + pin_w),               # 右
            (p, pin_out, p + pin_w, pin_reach),                       # 上
            (p, s - pin_reach, p + pin_w, s - pin_out),               # 下
        ):
            d.rounded_rectangle(rect, radius=pin_w / 2.0, fill=pin_color)

    # ---- 2. 芯片本体：圆角方 + 纵向渐变 ----
    tile_top, tile_bottom = (
        (TILE_TOP_SMALL, TILE_BOTTOM_SMALL) if small else (TILE_TOP, TILE_BOTTOM)
    )
    grad = _vertical_gradient(body_h, body_h, tile_top, tile_bottom).convert("RGBA")
    img.paste(
        grad,
        (b_lo, b_lo),
        _rounded_mask(body_h, (0, 0, body_h - 1, body_h - 1), radius),
    )

    # ---- 3. 外辉光（≥64px）：暖光从管芯溢出，暗示发热 ----
    # 必须在本体之后合成：本体是 paste（遮罩内整体替换），会连同辉光一起抹掉。
    # 强度刻意压低——辉光是氛围，不是主体；过强会让本体退化成一团橙色，
    # 冷灰描边的轮廓感随之消失。
    if size >= 64:
        glow = _glow_layer(s, die_box, die_h * 0.20, GLOW, s * 0.024, 168)
        # 本体遮罩，避免辉光溢到芯片轮廓之外
        glow.putalpha(
            Image.composite(
                glow.getchannel("A"),
                Image.new("L", (s, s), 0),
                _rounded_mask(s, box, radius),
            )
        )
        img.alpha_composite(glow)

    ImageDraw.Draw(img).rounded_rectangle(
        box,
        radius=radius,
        outline=rim_color,
        width=px(0.9 if small else 1.0),
    )

    # ---- 4. 管芯：暖色渐变，图标唯一的高饱和焦点 ----
    die_grad = _vertical_gradient(
        die_h, die_h, DIE_TOP, DIE_BOTTOM, mid=DIE_MID
    ).convert("RGBA")
    img.paste(
        die_grad,
        (d_lo, d_lo),
        _rounded_mask(die_h, (0, 0, die_h - 1, die_h - 1), die_h * (0.20 if small else 0.16)),
    )

    # ≥64px：管芯上刻一道极淡的十字分线，读作「多核」
    if size >= 64:
        lw = px(0.9)
        mid_x = (d_lo + d_hi) / 2.0
        mid_y = (d_lo + d_hi) / 2.0
        line = (0x00, 0x00, 0x00, 0x3A)
        d = ImageDraw.Draw(img)
        d.rectangle((mid_x - lw / 2, d_lo, mid_x + lw / 2, d_hi), fill=line)
        d.rectangle((d_lo, mid_y - lw / 2, d_hi, mid_y + lw / 2), fill=line)

    return img.resize((size, size), Image.LANCZOS)


# ============================ ICO 打包 ============================
# 自建容器以获得「每尺寸独立绘制」的控制权（Pillow 的 ICO 保存会把单张源图缩放）。
# ≤64px 用传统 BMP/DIB 条目（32bpp + AND 掩码），128/256 用 PNG 条目（Vista+ 标准做法）。


def _dib_bytes(img):
    w, h = img.size
    px = img.convert("RGBA").load()

    header = struct.pack(
        "<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, w * h * 4, 0, 0, 0, 0
    )

    body = bytearray()
    for y in range(h - 1, -1, -1):  # DIB 自底向上
        for x in range(w):
            r, g, b, a = px[x, y]
            # 预乘无关；ICO 的 32bpp 用直通 BGRA + AND 掩码
            body += bytes((b, g, r, a))

    mask_row = ((w + 31) // 32) * 4
    body += bytes(mask_row * h)  # AND 掩码全 0，透明度由 alpha 通道决定
    return header + bytes(body)


def _png_bytes(img):
    import io

    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(path, frames):
    entries, payloads = [], b""
    offset = 6 + 16 * len(frames)

    for img in frames:
        w, h = img.size
        data = _png_bytes(img) if w >= 128 else _dib_bytes(img)
        entries.append(
            struct.pack(
                "<BBBBHHII",
                0 if w >= 256 else w,
                0 if h >= 256 else h,
                0, 0, 1, 32, len(data), offset,
            )
        )
        payloads += data
        offset += len(data)

    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(frames)) + b"".join(entries) + payloads)


# ============================ 预览图 ============================


def build_preview(frames):
    pad, label_h = 24, 22
    strip_sizes = [16, 20, 24, 32, 48, 64]
    hero = 256

    cell_w = max(hero, len(strip_sizes) * 72)
    width = cell_w + pad * 2
    height = pad + hero + label_h + 20 + max(strip_sizes) + label_h + pad

    canvas = Image.new("RGBA", (width, height), (0x1A, 0x1A, 0x1A, 0xFF))
    canvas.alpha_composite(frames[-1], (pad, pad))

    y_strip = pad + hero + 20
    x = pad
    for size in strip_sizes:
        canvas.alpha_composite(render(size), (x, y_strip))
        x += 72

    # 实际尺寸行：左侧深色任务栏底、右侧浅色底，检验两种背景下的可辨性
    y_real = y_strip + max(strip_sizes) + label_h
    half = width // 2
    d = ImageDraw.Draw(canvas)
    d.rectangle([(0, y_real - 8), (width, height)], fill=(0x20, 0x20, 0x20, 0xFF))
    d.rectangle([(half, y_real - 8), (width, height)], fill=(0xF3, 0xF3, 0xF3, 0xFF))
    for i, size in enumerate([16, 20, 24, 32, 48]):
        for base in (0, half):
            canvas.alpha_composite(render(size), (base + 30 + i * 46, y_real + 6))

    return canvas.convert("RGB")


# ============================ 主流程 ============================


def main():
    print("渲染尺寸：%s" % ", ".join(str(s) for s in ICON_SIZES))

    frames = [render(s) for s in ICON_SIZES]
    write_ico(ICO_OUT, frames)
    print("已写入 %s（%d 字节）" % (ICO_OUT, os.path.getsize(ICO_OUT)))

    os.makedirs(os.path.dirname(PREVIEW_OUT), exist_ok=True)
    build_preview(frames).save(PREVIEW_OUT)
    print("已写入 %s" % PREVIEW_OUT)
    return 0


if __name__ == "__main__":
    sys.exit(main())
