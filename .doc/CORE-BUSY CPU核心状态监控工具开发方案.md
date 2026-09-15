# CORE-BUSY
## CPU Core Activity Monitor

> 看看你的 CPU，谁在干活，谁在摸鱼。

版本：V1.0 开发规划  
目标平台：Windows 10 / Windows 11  
项目类型：轻量级 CPU 核心状态监控工具


---

# 1. 项目定位

## 1.1 产品目标

CORE-BUSY 是一款专注于 **CPU 每核心运行状态可视化** 的 Windows 桌面工具。

区别于传统硬件监控软件：

传统工具关注：

- CPU 总占用率
- CPU 温度
- CPU 频率
- CPU 功耗

CORE-BUSY 关注：

- 哪个核心正在工作
- 哪个核心长期满载
- P-Core / E-Core 调度情况
- 游戏是否存在单核心瓶颈
- CPU 是否存在异常降频


核心理念：

> 不只是告诉用户 CPU 有多忙，而是告诉用户哪颗核心最忙。


---

# 2. 产品名称

## 正式名称

## CORE-BUSY


中文名称：

## 核忙


产品宣传语：

> 看看你的 CPU，谁在干活，谁在摸鱼。


Logo 设计方向：

- CPU 芯片图案
- 多核心方格
- 部分核心高亮
- 部分核心暗淡
- 科技感蓝绿色配色


---

# 3. 第一阶段功能范围（MVP）

目标：

开发一个稳定、低占用、可长期运行的 CPU 核心监控工具。


## 3.1 CPU信息显示

显示：

- CPU名称
- CPU厂商
- 核心数量
- 线程数量
- P-Core数量
- E-Core数量


示例：

```
Intel Core i5-14600KF

P-Core:
6

E-Core:
8

Threads:
20
```


---

# 3.2 每核心实时状态


核心展示：

```
P-Core

P0
92%
5.2GHz

P1
12%
5.1GHz

P2
85%
5.2GHz


E-Core

E0
8%

E1
4%
```


每个核心显示：

- 当前占用率
- 当前频率
- 状态


状态分类：

|负载|状态|
|-|-|
|0-5%|摸鱼|
|5-20%|空闲|
|20-50%|工作|
|50-80%|忙碌|
|80-95%|高负载|
|95-100%|爆肝|


---

# 3.3 CPU核心热力图


显示最近60秒核心负载变化。


示例：

```
        -60s                 Now


P0  ░░▒▒▓▓███████▓▒░

P1  ░░░░▒▒▓▓▒▒░░░░

P2  ██████████████

P3  ▒▒▓▓█████▓▓▒▒


E0  ░░░░░░░░░░░░

E1  ░▒▒░░░░░░░░
```


用途：

快速观察：

- 游戏线程分布
- 单核心瓶颈
- 后台程序影响


---

# 3.4 CPU温度

显示：

- CPU Package温度
- 核心最高温度


示例：

```
Temperature

Package:
62℃


Max Core:
68℃
```


---

# 3.5 CPU频率

显示：

- 当前频率
- 最大频率


示例：

```
Clock

Average:
4.8GHz

Max:
5.2GHz
```


---

# 3.6 CPU功耗


显示：

- Package Power


示例：

```
Power

CPU Package:
95W
```


---

# 4. 第二阶段功能规划


## 4.1 游戏监控模式


自动识别游戏进程。


记录：

```
Game:

Cyberpunk2077.exe


运行时间:
02:15:20


CPU平均:
54%


最高核心:
P-Core 3


最高温度:
78℃
```


---

## 4.2 核心劳模排行榜


增加趣味功能。


示例：

```
今日劳模


P-Core 3

平均负载:
72%

最高:
100%


今日摸鱼王


E-Core 6

平均:
1%
```


---

## 4.3 进程核心分析


点击核心：

显示：

```
P-Core 3

当前负载:

Game.exe
72%

Chrome.exe
12%

System
8%
```


---

# 5. 技术方案


# 5.1 技术栈


开发语言：

C#


框架：

.NET 8


UI：

WPF


架构：

MVVM


数据库：

SQLite


日志：

Serilog


图表：

LiveCharts2


安装：

MSIX / Inno Setup



---

# 6. 软件架构


项目结构：


```
CORE-BUSY

│

├── CoreBusy.App

│   ├── MainWindow

│   ├── Tray

│   └── Settings


├── CoreBusy.Core

│   ├── CpuInfo

│   ├── CpuTopology

│   ├── CpuUsage

│   └── CoreState


├── CoreBusy.Sensor

│   ├── Temperature

│   ├── Power

│   └── Frequency


├── CoreBusy.Windows

│   ├── PerformanceCounter

│   ├── NativeAPI

│   └── ProcessorGroup


├── CoreBusy.History

│   ├── SQLite

│   └── Sampling


└── CoreBusy.UI

    ├── CoreTile

    ├── HeatMap

    └── Charts

```


---

# 7. 核心技术模块


## 7.1 CPU拓扑识别


必须支持：

Intel:

- P-Core
- E-Core
- Hyper Threading


AMD:

- CCD
- SMT


目标：

建立逻辑关系：


```
CPU

├── Physical Core

│
├── Thread 0

└── Thread 1

```


---

# 7.2 CPU占用率采集


方式：

Windows Performance Counter


采样周期：

默认：

1000ms


可调整：

500ms

1000ms

2000ms


---

# 7.3 温度/功耗


采用：

LibreHardwareMonitorLib


支持：

- Intel
- AMD


---

# 8. UI设计


## 主界面


结构：


```
------------------------------------------------

CORE-BUSY


CPU:
Intel Core i5-14600KF


Temperature:
62℃

Power:
95W


------------------------------------------------


P-Core


[P0]
92%
5.2GHz
爆肝


[P1]
15%
5.1GHz
摸鱼



E-Core


[E0]
5%

[E1]
3%


------------------------------------------------


60秒负载曲线


------------------------------------------------

```


---

# 9. 性能要求


软件自身：


CPU占用：

<1%


内存：

<100MB


启动：

<3秒


后台运行：

支持系统托盘。


---

# 10. 开发阶段


# Phase 1

基础版本


完成：

- CPU识别
- 核心数量
- 每核心占用率
- 基础UI


目标：

可以运行。


---

# Phase 2

硬件监控


增加：

- 温度
- 功耗
- 频率
- P/E Core


目标：

成为可用工具。


---

# Phase 3

特色功能


增加：

- 热力图
- 摸鱼模式
- 劳模排行
- 游戏模式


目标：

形成产品特色。


---

# Phase 4

高级分析


增加：

- 进程关联
- 单核心瓶颈分析
- CPU调度分析


目标：

成为CPU诊断工具。


---

# 11. Codex开发要求


## 开发原则


1. 模块化设计

禁止所有代码写在MainWindow。


2. 使用MVVM。


3. 所有硬件读取必须异步。


4. UI刷新不能阻塞。


5. 支持未来扩展。


6. 第一版本优先稳定。


---

# 12. 第一轮开发任务


Codex执行顺序：


## Task 1

创建WPF项目：

```
CORE-BUSY
```


要求：

.NET 8

MVVM结构


---

## Task 2

实现CPU信息模块：

输出：

- CPU名称
- 核心数量
- 线程数量


---

## Task 3

实现核心监控：

显示：

- 每线程占用率


---

## Task 4

实现核心Tile UI：

每个核心显示：

- 名称
- 百分比
- 状态


---

## Task 5

加入托盘运行。


---

# 13. 后续版本方向


未来可以增加：


- CPU压力测试
- 游戏性能报告
- CPU排行榜
- 核心调度分析
- 超频辅助
- 温度预测


最终目标：

打造一个：

> 面向玩家和硬件爱好者的 CPU 核心行为观察工具。


---

# END

项目：

CORE-BUSY

版本：

V1.0