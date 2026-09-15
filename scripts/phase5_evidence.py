# -*- coding: utf-8 -*-
"""Phase 5 取证脚本：
1. DPI 感知初始化；
2. 打印窗口当前状态（PrintWindow 截图）；
3. 创建无边框全屏窗口（WS_POPUP，铺满主屏）模拟游戏 8 秒，退出后 CORE-BUSY 应
   显示"上次游戏"横幅并写游戏报告 JSON；
4. 第二次 PrintWindow 截图取证横幅。
用法: python phase5_evidence.py <out_dir>
"""
import sys, time, ctypes, ctypes.wintypes as wt

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

# 1. DPI 感知
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

PW_RENDERFULLCONTENT = 2
GW_HWNDPREV = 3

class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32),
                ("biHeight", ctypes.c_int32), ("biPlanes", ctypes.c_uint16),
                ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_int32),
                ("biYPelsPerMeter", ctypes.c_int32), ("biClrUsed", ctypes.c_uint32),
                ("biClrImportant", ctypes.c_uint32)]

def find_main_window():
    """按标题前缀找 CORE-BUSY 主窗口。"""
    result = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(hwnd, lparam):
        buf = ctypes.create_unicode_buffer(256)
        user32.GetWindowTextW(hwnd, buf, 256)
        if buf.value.startswith("CORE-BUSY") and user32.IsWindowVisible(hwnd):
            result.append(hwnd)
        return True
    user32.EnumWindows(cb, 0)
    return result[0] if result else None

def print_window(hwnd, path):
    user32.GetWindowRect.restype = ctypes.c_bool
    rect = wt.RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
        print("GetWindowRect failed")
        return False
    w, h = rect.right - rect.left, rect.bottom - rect.top
    if w <= 0 or h <= 0:
        print("window offscreen/zero size")
        return False
    hdc = user32.GetWindowDC(hwnd)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    ok = user32.PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT)
    from PIL import Image
    bmpheader = BITMAPINFOHEADER()
    bmpheader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmpheader.biWidth = w
    bmpheader.biHeight = -h
    bmpheader.biPlanes = 1
    bmpheader.biBitCount = 32
    bmpheader.biCompression = 0
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bmpheader), 0)
    img = Image.frombuffer("RGB", (w, h), buf.raw, "raw", "BGRX", 0, 1)
    img.save(path)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(hwnd, hdc)
    print(f"screenshot ok={ok} size={w}x{h} -> {path}")
    return True

def run_borderless_fullscreen(seconds):
    """WS_POPUP 无边框窗口铺满主屏，模拟无边框窗口化游戏。"""
    style = 0x80000000  # WS_POPUP
    ex = 0x00000080     # WS_EX_TOOLWINDOW off; plain
    ex = 0
    sw, sh = user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)
    WC = "STATIC"
    hwnd = user32.CreateWindowExW(
        0, WC, "BorderlessGameSim", style,
        0, 0, sw, sh, None, None, None, None)
    if not hwnd:
        print("CreateWindow failed", ctypes.get_last_error())
        return
    # 背景刷成深色便于识别（可选）
    user32.ShowWindow(hwnd, 5)  # SW_SHOW
    user32.SetForegroundWindow(hwnd)
    print(f"borderless fullscreen shown {sw}x{sh}, holding {seconds}s")
    end = time.time() + seconds
    msg = wt.MSG()
    while time.time() < end:
        while user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, 1):
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))
        time.sleep(0.2)
    user32.DestroyWindow(hwnd)
    print("borderless window destroyed")

def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "."
    hwnd = find_main_window()
    print("main window hwnd:", hwnd)
    if hwnd:
        print_window(hwnd, out + "/phase5-before-game.png")

    run_borderless_fullscreen(8)
    time.sleep(3)  # 等待 CORE-BUSY 检测到游戏退出并写报告

    hwnd2 = find_main_window()
    if hwnd2:
        print_window(hwnd2, out + "/phase5-after-game.png")

if __name__ == "__main__":
    main()
