#!/usr/bin/env python
"""CORE-BUSY UI 静态检查（无需 .NET SDK 即可运行）。

背景：本项目历史上反复出现两类"静默失效"缺陷，编译期与运行期都不报错：
  1) XAML 结构破损          —— 标签未配平、命名空间缺失，编译才暴露。
  2) 绑定路径拼错/属性改名  —— WPF 只写 trace，界面静默空白，是"信息显示不全"的典型成因。

本脚本把这两类检查前移，可在没有 SDK 的机器上快速兜底。

用法:
    python scripts/lint_ui.py
退出码: 0 = 全部通过; 1 = 存在问题。
"""
from __future__ import annotations

import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP = os.path.join(REPO, "src", "CoreBusy.App")

# 绑定校验覆盖的 XAML：这些文件的 DataContext 是 ViewModel。
BOUND_XAML = [
    r"Views\MainWindow.xaml",
    r"Views\SettingsWindow.xaml",
    r"Controls\CpuOverviewPanel.xaml",
    r"Controls\StatusBarControl.xaml",
    r"Controls\RankingPanel.xaml",
    r"Controls\ProcessListPanel.xaml",
    r"Controls\CpuCoreTile.xaml",
    r"Controls\CpuSummaryCard.xaml",
    r"Controls\SectionHeader.xaml",
    r"Controls\PowerCalendarPanel.xaml",
]

# 相对源绑定（如 RelativeSource AncestorType=Button 的 Foreground）不查成员表。
RELATIVE_SOURCE_OK = {"Foreground", "Background", "Text", "Tag", "Name", "IsChecked"}

PROP_RE = re.compile(
    r"public\s+(?:static\s+|sealed\s+|override\s+|virtual\s+|readonly\s+|partial\s+)*"
    r"[A-Za-z_][\w\.<>\[\],\?\s]*?\s+([A-Za-z_]\w*)\s*(?:\{|=>)"
)

failures: list[str] = []


def iter_binding_paths(content: str):
    """提取需要按成员表校验的绑定路径。

    形如 `{Binding X, RelativeSource=...}` 的绑定，数据源是模板父级或祖先元素上的
    **框架属性**（ActualWidth / IsDropDownOpen / SelectionBoxItem 等），与 DataContext
    无关，必须结构化地跳过 —— 早前靠 RELATIVE_SOURCE_OK 白名单按属性名硬扛，
    一旦自绘控件模板引入新的框架属性名，白名单就不成立了
    （v1.12.0 的深色 ComboBox 模板一次带进 4 个，全是误报）。

    用花括号配平扫描而不是按行匹配：绑定表达式允许跨行书写。
    """
    marker = "{Binding"
    index = 0
    while True:
        start = content.find(marker, index)
        if start < 0:
            return

        pos = start + len(marker)
        depth = 1
        while pos < len(content) and depth > 0:
            char = content[pos]
            if char == "{":
                depth += 1
            elif char == "}":
                depth -= 1
            pos += 1

        expression = content[start + 1:pos - 1] if pos > start else content[start + 1:]
        index = max(pos, start + len(marker))

        if "RelativeSource" in expression:
            continue

        match = re.match(r"Binding\s+(?:(?:Path\s*=\s*)?([A-Za-z_]\w*))", expression)
        if match:
            yield match.group(1)


def iter_sources(pattern: str):
    for path in glob.glob(os.path.join(APP, "**", pattern), recursive=True):
        if os.sep + "obj" + os.sep in path:
            continue
        yield path


def check_xaml_structure() -> int:
    total = 0
    for path in iter_sources("*.xaml"):
        total += 1
        try:
            ET.parse(path)
        except Exception as exc:  # noqa: BLE001
            failures.append("XAML 结构破损: %s -> %r" % (os.path.relpath(path, REPO), exc))
    print("[1/2] XAML 结构   : 检查 %d 个文件" % total)
    return total


def collect_members() -> set[str]:
    members: set[str] = set()
    for path in iter_sources("*.cs"):
        with open(path, encoding="utf-8-sig") as fh:
            members.update(PROP_RE.findall(fh.read()))
    return members


def check_bindings() -> int:
    members = collect_members()
    checked = 0
    for rel in BOUND_XAML:
        path = os.path.join(APP, rel)
        if not os.path.exists(path):
            continue
        with open(path, encoding="utf-8-sig") as fh:
            content = fh.read()
        for name in sorted(set(iter_binding_paths(content))):
            if name in RELATIVE_SOURCE_OK:
                continue
            checked += 1
            if name not in members:
                failures.append("绑定无对应成员: %s -> {Binding %s}" % (rel, name))
    print("[2/2] XAML 绑定   : 校验 %d 条（成员表 %d 项）" % (checked, len(members)))
    return checked


def main() -> int:
    check_xaml_structure()
    check_bindings()
    print("-" * 52)
    if failures:
        print("FAILED (%d):" % len(failures))
        for item in failures:
            print("  " + item)
        return 1
    print("PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
