# AGENTS-AI.md — 面向 AI 助手的协作约定

> 本文件面向参与本项目的 AI 助手（agent）。人类开发者请先读根目录 `AGENTS.md`（项目工程手册），本文件是其补充，不替代。

## 项目是什么

笔吧验机：Windows 桌面验机工具（WPF），二手/新机到手后一键采集硬件信息、逐项人工核对、标记通过。主程序是插件外壳，每个验机功能模块是独立插件（class library）。**全部硬件采集基于开源组件与 Windows 自带接口，禁止集成闭源商业软件。**

当前版本：v0.0.4。目标用户：二手交易买家、新机验机用户。

## 两种语言的开发分工

本项目涉及两种语言，分工边界必须分清：

| 语言 | 职责 | 在本项目中的位置 | 谁来写 |
|---|---|---|---|
| **C#** | 全部产品代码：宿主外壳、6 个插件、SDK 契约、烤机工具重实现 | `验机软件/`、`Plugins/*/`、`PluginSdk/`、`FurMark/` | AI 助手可写，人类质检 |
| **Java** | **不属于本产品**。仅用于人类开发者的独立学习沙盒 | 不在本仓库，在另一个学习工程 | 人类自己写，AI 只讲解 |

**关键约定**：本仓库内**没有任何 Java 代码需要编写**。如果任务涉及 Java，那是另一个独立学习工程的事，与笔吧验机无关。AI 助手在本项目中的代码产出**全部是 C#**。

## 全套技术栈

### 平台与框架

- **.NET 10**（目标框架 `net10.0-windows10.0.19041.0`），SDK 10.0.400
- **WPF**（Windows Presentation Foundation）桌面 UI，无 MVVM 框架，UserControl + code-behind
- **x64 平台**：`LibreHardwareMonitorLib` 的 CsWin32 源生成器要求固定 x64，不可改回 AnyCPU

### 硬件采集（核心）

| 组件 | 用途 | 协议 |
|---|---|---|
| LibreHardwareMonitorLib（源码引用） | CPU/GPU/内存/硬盘/传感器采集 | MPL-2.0 |
| System.Management (WMI) | 网卡、电池、部分硬件信息 | — |
| 注册表 EDID + Win32 显示 API | 屏幕信息、色域估算 | — |
| PawnIO 内核驱动（官方签名安装器） | CPU/主板传感器 Ring0 访问 | GPL-2.0（带例外） |

### 功能组件

| 组件 | 用途 | 协议 |
|---|---|---|
| NAudio 2.2.1 | 麦克风/扬声器（WASAPI） | Ms-PL |
| CrystalDiskMark 9.0.3（源码编译为 CdmHost） | 硬盘跑分引擎 | MIT |
| diskspd 2.0.20a（微软，CDM 定制 fork） | 硬盘测速子进程 | MIT |
| prime95（GIMPS 源码编译） | CPU 烤机 | GIMPS freeware |
| FurMark（C#/OpenTK 重实现） | GPU 烤机 | MIT（原型） |
| OpenTK 4.x | FurMark 的 OpenGL 绑定 | MIT |
| StbImageSharp | FurMark 贴图解码 | 公有领域 |

### 打包

- **Inno Setup 6**：安装向导（`installer/setup.iss`），产物约 116MB
- **dotnet publish**：self-contained win-x64，目标机无需装 .NET

## 开发现状

### 已可用（已发布 v0.0.4）

6 个插件全部功能完整：配置核对（含电池）、键盘测试、屏幕坏点、影音会议、散热测试（烤机+监控）、硬盘跑分。插件化架构完整（zip 导入/删除、免重启加载、致谢随插件走）。

### 开发环境现状（当前 fork 工作区）

- **工作目录**：`F:/BibaYanji-dev`（clone 自 `F:/BibaYanji`，origin=beifeng08 fork，upstream=Bibapcs 原版）
- **分支**：`dev` 上开发，`main` 保持与 `origin/main` 一致
- **缺失 1**：本机无 .NET SDK 10，**当前无法构建/编译**，只能写代码与静态审查。安装 SDK 是开工前提
- **缺失 2**：上游 `AGENTS.md` 引用的 `项目交接文档.md` **不存在于本仓库**（全历史未提交）。涉及采集逻辑（LHM/EDID/SMART/色域）的改动**必须先读源码自行验证**，不能引用该文档中的结论
- **已发布成品**：`F:/BibaInspection/` 有 v0.0.4 自包含发布版（464MB），可直接运行查看产品现状，但无 SDK 组件不能用于编译

### 网络现状

GitHub fetch/push 当前返回 502（代理问题）。本地开发不受影响；push/pull 需网络恢复。

## 开发全流程与安排

### 单次改动流程（AI 助手必须遵守）

1. **说明背景**：改哪个文件、为什么、涉及什么机制、有什么风险
2. **人类确认方案** → 未确认不动手
3. **实现**：最小改动，风格对齐周边文件（注释密度、命名、UserControl+code-behind）
4. **自检**：`git status` 看改动清单，`git diff` 看具体差异
5. **提交**：`git add <明确路径>`（不用 `git add .`），commit message 格式 `<类型>：<简述>`
6. **push**：**需人类单独授权**，不擅自执行

### 硬性约束（违反即返工）

- 禁闭源商业软件（HWiNFO 等）；采集只用 LHM 源码 + Windows 自带接口
- 插件引用 `YanJi.PluginSdk` 必须 `Private="false"`
- `LibreHardwareMonitorLib` 引用必须带 `SetPlatform="Platform=x64"`
- 宿主 csproj 里 `System.Management` 的 PackageReference **勿删**（插件运行时靠宿主解析真身）
- 试音曲署名（AvConference 页面）是用户给定原文，**不得改动**
- 新增内置插件需同步 3 处：slnx + 宿主 BuildPlugins/CopyPlugins 两个 target + `installer/setup.iss` 的 [Components]/[Files]
- 临时探针工程（`.*-probe/`）用完即删；LHM 上游源码不批量转码/改动

### UI 约定

- 只用语义画刷令牌（`WindowBgBrush`/`AccentBrush` 等），全部 `DynamicResource`，**不写字面颜色**；深/浅两个字典键集必须一致
- 「测试通过」语义色用 `ForestGreen`
- 资源还原用 `SetResourceReference` 恢复动态绑定（直接赋画刷会断主题切换）
- 日志区：只读 TextBox（Consolas 11）外套 `CornerRadius=6` 的 Border

### 证据标注（AI 助手必须遵守）

每个结论必须标注证据等级：
- **已实跑**：真的执行过，有输出
- **静态审阅**：读过代码但没运行
- **未验证**：推断或猜测

**未编译的改动必须显式标注「未编译验证」**。构建成功与实际运行是两项不同证据，不能互相替代。

## 项目开发方向与目标

### 产品定位

面向二手交易与新机验机场景的**免费开源**硬件核对工具。差异化：**不依赖任何闭源商业软件**（对照：图吧工具箱集成 HWiNFO 等），全部基于开源组件 + Windows 自带接口实现同等采集能力。

### 已确认的方向（来自上游 README/AGENTS）

- 插件化架构持续完善：第三方插件生态、更灵活的模块组合
- 采集能力深化：更多硬件细节（当前已有 CPU/显卡/内存/硬盘/网卡/屏幕/电池）
- 安装体验优化：模块化安装、PawnIO 驱动静默集成

### 本 fork 的工作目标（人类开发者设定，待补充）

> 此处由人类开发者填写本 fork 的具体目标。AI 助手开工前应确认当前迭代要解决什么问题。

**当前待确认**：
- [ ] 安装 .NET SDK 10（构建前提）
- [ ] 确认 `项目交接文档.md` 是否能从上游获取（采集类改动的前提）
- [ ] 明确本 fork 第一个要改的功能点

---

_本文件随项目演进更新。机制变化（目录、契约、构建链路、协作约定）时同步修改。_
