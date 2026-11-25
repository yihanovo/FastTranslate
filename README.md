# SnapTranslate 划屏即译

轻量级 Windows 截图翻译工具：按下快捷键 → 框选屏幕上的英文 → 松手即得中文翻译，结果以可钉屏浮窗展示。交互体验对标 Snipaste。

## 快速开始

```powershell
# 方式一：直接运行发布版（推荐，免装任何运行时）
.\publish\SnapTranslate\SnapTranslate.exe

# 方式二：源码运行（需要 .NET 10 SDK）
dotnet run --project src\SnapTranslate
```

程序启动后**静默驻留系统托盘**（无主窗口）。

## 使用

| 操作 | 方式 |
|---|---|
| 截图翻译 | 快捷键（默认 `F1`，可自定义）或 托盘左键 |
| 框选 | 按住左键拖出矩形，**松手即出结果** |
| 取消 | 框选过程中 `Esc` / 右键 |
| 浮窗操作 | ↺再截取 / 复制原文译文 / 重试翻译 / ✕关闭；标题栏拖动位置 |
| 设置 | 托盘右键 → 设置 |

## 功能

- **截图 → OCR → 翻译** 全链路 ≤ 1.5s（OCR 离线毫秒级）
- **双翻译引擎**：
  - **MyMemory（默认）**：免注册免费引擎，开箱即用——发 exe 给别人即可直接翻译，不消耗任何人额度
  - **百度翻译（可选）**：质量更稳，设置界面填入 AppID/密钥即可启用（密钥 DPAPI 加密存储，不明文落盘）
  - "自动"模式：已配置百度用百度，否则走 MyMemory
- **松手即译**：框选完成直接出结果，无二次确认
- **钉屏浮窗**：不抢焦点、可拖动、可保留多个（或按设置自动关闭旧窗）
- **自定义快捷键**：支持 `Ctrl/Alt/Shift/Win + 字母/数字/F键` 组合，保存时真实注册验证冲突
- **禁用快捷键**：一键让出全局热键给其他应用（游戏/IDE 场景）
- **开机自启**：可选，HKCU 注册表实现，免管理员权限

## 技术栈

- .NET 10 / WPF（`net10.0-windows10.0.19041.0`）
- OCR：`Windows.Media.Ocr`（系统自带，离线免费，需英文语言包）
- 翻译：百度翻译开放平台 · 通用翻译 API（标准版 1 QPS，客户端已做排队节流 + 指数退避重试）
- 托盘：Hardcodet.NotifyIcon.Wpf

## 配置

配置文件：`%APPDATA%\SnapTranslate\config.json`

```jsonc
{
  "version": 1,
  "baidu": { "appid": "…", "secretEnc": "DPAPI加密串" },
  "hotkey": { "key": "F1", "modifiers": [] },
  "behavior": {
    "autoStartWithWindows": false,
    "closePreviousPin": true,     // 新截图自动关旧浮窗
    "hotkeyEnabled": true         // 热键总开关
  }
}
```

写入百度凭据（密钥以 DPAPI 加密落盘，仅本机本用户可解）：

```powershell
dotnet run --project tools\SeedConfig -- <appid> <secret>
```

日志：`%APPDATA%\SnapTranslate\app.log`（完整异常堆栈，排障用）

## 发布

```powershell
# 自包含单文件（目标机器免装运行时，双击即用）
dotnet publish src\SnapTranslate\SnapTranslate.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none `
  -o publish\SnapTranslate
```

## 常见问题

| 问题 | 处理 |
|---|---|
| 提示"OCR 引擎不可用" | 设置 → 时间和语言 → 语言和区域 → 添加语言（英语），装完重启程序 |
| 热键注册失败 | 与其他软件（如 Snipaste）冲突，换一个组合 |
| 翻译报 54001 签名错误 | 检查 AppID/密钥是否匹配（`tools\SeedConfig` 重新写入） |
| 翻译报 54003 频率受限 | 标准版 1 QPS，程序已自动排队，连续快速截图时稍等即可 |
| 修改代码后运行无反应 | 旧实例驻留托盘（单实例锁），先退出旧实例再运行 |

## 项目结构

```
├── DESIGN.md                     # 详细设计文档
├── SnapTranslate.slnx
├── src\SnapTranslate\            # 主程序
│   ├── Interop\                  # Win32 P/Invoke
│   ├── Models\                   # 数据模型
│   ├── Services\                 # 热键/抓屏/OCR/翻译/配置/日志
│   ├── Pipelines\                # 流程编排 + 浮窗管理
│   └── Views\                    # 遮罩/结果浮窗/设置
└── tools\SeedConfig\             # 凭据种子工具（DPAPI 加密写入）
```

## 路线图

- [x] M1 截图链路（热键/抓屏/框选）
- [x] M2 OCR（WinRT 离线识别）
- [x] M3 百度翻译（签名/限流/重试 + DPAPI）
- [x] M5 设置界面（热键录制/禁用开关/自启/浮窗策略）
- [x] M6 单文件发布
- [ ] M4 单词词典卡片（框单个单词时显示音标/词性/释义）
- [ ] 智能选区（OCR 先行，鼠标悬停自动吸附文字块）
- [ ] 常驻内存优化（当前 ~250MB，目标 ≤ 60MB）
