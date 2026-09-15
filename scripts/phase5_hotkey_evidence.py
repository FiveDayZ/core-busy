# -*- coding: utf-8 -*-
"""通过 Ctrl+Alt+B 热键恢复 CORE-BUSY 主窗口后取证（PrintWindow + UIA 文本）。
用法: python phase5_hotkey_evidence.py <out_dir>
"""
import sys, time, ctypes, ctypes.wintypes as wt

user32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

VK_CONTROL, VK_MENU, VK_B = 0x11, 0x12, 0x42

def find_main_window():
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

def send_ctrl_alt_b():
    user32.keybd_event(VK_CONTROL, 0, 0, 0)
    user32.keybd_event(VK_MENU, 0, 0, 0)
    user32.keybd_event(VK_B, 0, 0, 0)
    time.sleep(0.05)
    user32.keybd_event(VK_B, 0, 2, 0)
    user32.keybd_event(VK_MENU, 0, 2, 0)
    user32.keybd_event(VK_CONTROL, 0, 2, 0)

def print_window(hwnd, path):
    import ctypes as ct
    gdi32 = ct.windll.gdi32
    from PIL import Image
    rect = wt.RECT()
    if not user32.GetWindowRect(hwnd, ct.byref(rect)):
        return False
    w, h = rect.right - rect.left, rect.bottom - rect.top
    hdc = user32.GetWindowDC(hwnd)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    ok = user32.PrintWindow(hwnd, mem, 2)
    class BIH(ct.Structure):
        _fields_ = [("biSize", ct.c_uint32), ("biWidth", ct.c_int32),
                    ("biHeight", ct.c_int32), ("biPlanes", ct.c_uint16),
                    ("biBitCount", ct.c_uint16), ("biCompression", ct.c_uint32),
                    ("biSizeImage", ct.c_uint32), ("biXPelsPerMeter", ct.c_int32),
                    ("biYPelsPerMeter", ct.c_int32), ("biClrUsed", ct.c_uint32),
                    ("biClrImportant", ct.c_uint32)]
    bh = BIH()
    bh.biSize = ct.sizeof(BIH); bh.biWidth = w; bh.biHeight = -h
    bh.biPlanes = 1; bh.biBitCount = 32; bh.biCompression = 0
    buf = ct.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ct.byref(bh), 0)
    img = Image.frombuffer("RGB", (w, h), buf.raw, "raw", "BGRX", 0, 1)
    img.save(path)
    gdi32.DeleteObject(bmp); gdi32.DeleteDC(mem); user32.ReleaseDC(hwnd, hdc)
    print(f"screenshot ok={ok} size={w}x{h} rect=({rect.left},{rect.top}) -> {path}")

def dump_uia(hwnd):
    from comtypes.client import GetModule, CreateObject
    GetModule("UIAutomationCore.dll")
    import comtypes.gen.UIAutomationClient as uac
    cua = CreateObject(uac.CUIAutomation, interface=uac.IUIAutomation)
    element = cua.ElementFromHandle(hwnd)
    texts = []

    def walk(el, depth):
        if depth > 10:
            return
        try:
            name = el.CurrentName
        except Exception:
            name = None
        if name:
            texts.append("  " * depth + str(name))
        try:
            walker = cua.ControlViewWalker()
            child = walker.GetFirstChildElement(el)
            while child:
                walk(child, depth + 1)
                child = walker.GetNextSiblingElement(child)
        except Exception:
            pass

    walk(element, 0)
    return texts

def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "."
    hwnd = find_main_window()
    print("hwnd before:", hwnd)

    # 若窗口离屏（左边界 < -20000）或不可见，先热键恢复（按两次确保状态切换正确）
    rect = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    # 无条件热键切换（Opacity=0 时应用判定为隐藏态，会走 RestoreFromTray）
    send_ctrl_alt_b()
    time.sleep(1.5)
    hwnd = find_main_window()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    print("after hotkey rect:", rect.left, rect.top)

    # 移到可见区域
    user32.SetWindowPos(hwnd, 0, 100, 100, 0, 0, 0x0001 | 0x0040)
    time.sleep(2)
    print_window(hwnd, out + "/phase5-final.png")

    print("--- UIA texts ---")
    try:
        for t in dump_uia(hwnd):
            if t.strip():
                print("UIA|", t)
    except Exception as e:
        print("UIA failed:", e)

if __name__ == "__main__":
    main()
