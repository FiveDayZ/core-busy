# -*- coding: utf-8 -*-
"""恢复 CORE-BUSY 主窗口到屏幕并取证：
1. SetWindowPos 移回屏幕；2. SetLayeredWindowAttributes 恢复 alpha；
3. PrintWindow 截图；4. UIA 遍历关键文本（横幅/状态栏/排行/温度）。
用法: python phase5_restore_evidence.py <out_dir>
"""
import sys, time, ctypes, ctypes.wintypes as wt

user32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

LWA_ALPHA = 2

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

def restore(hwnd):
    user32.SetWindowPos(hwnd, 0, 100, 100, 0, 0, 0x0001 | 0x0040)  # NOSIZE|SHOWWINDOW
    # 尝试恢复分层透明度（WPF Opacity=0 的 layered 窗口）
    if not user32.SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA):
        print("not layered or restore failed:", ctypes.get_last_error())

def dump_uia(hwnd):
    import comtypes
    import comtypes.client
    from comtypes import GUID
    uia = comtypes.client.CreateObject(
        "{ff48dba4-60ef-4201-aa87-54103eef594e}",
        interface=ctypes.c_void_p)
    # 使用动态生成：用 comtypes.client.GetModule 获取 UIAutomationClient 类型库
    from comtypes.client import GetModule, CreateObject
    GetModule("UIAutomationCore.dll")
    import comtypes.gen.UIAutomationClient as uac
    cua = CreateObject(uac.CUIAutomation().IPersist_GetClassID() if False else uac.CUIAutomation,
                       interface=uac.IUIAutomation)
    element = cua.ElementFromHandle(hwnd)
    texts = []

    def walk(el, depth):
        if depth > 8:
            return
        try:
            name = el.CurrentName or ""
        except Exception:
            name = ""
        try:
            val = el.GetCurrentPropertyValue(uac.UIA_ValueValuePropertyId) if el.CurrentControlType else ""
        except Exception:
            val = ""
        if name:
            texts.append("  " * depth + name)
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
    print(f"screenshot ok={ok} size={w}x{h} -> {path}")

def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "."
    hwnd = find_main_window()
    print("hwnd:", hwnd)
    if not hwnd:
        sys.exit(1)
    restore(hwnd)
    time.sleep(2)
    print_window(hwnd, out + "/phase5-restored.png")
    try:
        for t in dump_uia(hwnd):
            if t.strip():
                print("UIA|", t)
    except Exception as e:
        print("UIA failed:", e)

if __name__ == "__main__":
    main()
