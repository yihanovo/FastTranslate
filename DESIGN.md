# SnapTranslate — 划屏即译 轻量级截图翻译工具 详细设计文档

> 版本：v1.0 草案
> 日期：2026-11-25
> 目标平台：Windows 10 / 11（x64）
> 技术栈：C# / .NET 8 / WPF

---

## 1. 项目概述

### 1.1 一句话定位

一个常驻后台的轻量级 Windows 工具：按下热键 → 框选屏幕上的英文 → OCR 识别 → 调用翻译 → 结果以可钉屏浮窗展示。交互体验对标 Snipaste 的截图流程。

### 1.2 目标（Goals）

| # | 目标 | 验收标准 |
|---|------|----------|
| G1 | 快速截图翻译 | 热键到出结果全链路 ≤ 1.5s（网络正常时） |
| G2 | 轻量常驻 | 安装包 ≤ 10MB，内存占用 ≤ 40MB，冷启动 ≤ 300ms |
| G3 | 结果钉屏 | 翻译浮窗可钉在屏幕任意位置、可拖动、不抢焦点 |
| G4 | 单词/句子自动区分 | 选区为单个英文单词时展示词典式卡片 |
| G5 | 静默后台 | 托盘常驻，无主窗口，开机自启可选 |

### 1.3 非目标（Non-Goals，v1 不做）

- 跨平台（macOS / Linux）
- 离线翻译（离线模型体积过大，违背轻量定位）
- 截图编辑（画笔、马赛克等，Snipaste 已做得足够好）
- 语音输入 / 整屏滚动翻译
- PDF / 浏览器插件形态

---

## 2. 技术选型与理由

| 层 | 选型 | 理由 | 备选 |
|----|------|------|------|
| UI 框架 | .NET 8 + WPF (C#) | 原生无边框/透明窗口、托盘、热键成熟；体积小 | Tauri（跨平台但原生能力要自写） |
| OCR | `Windows.Media.Ocr` (WinRT) | 系统自带、离线、免费、毫秒级，英文准确率高 | PaddleOCR（更准但 100MB+） |
| 翻译 | 百度翻译开放平台 · 通用翻译 API（标准版） | 免费额度（不限量但限 1 QPS）、延迟低、中文质量好 | DeepL API Free |
| 单词词典（增强） | Free Dictionary API `dictionaryapi.dev` | 免费、无 Key、返回音标/词性/释义/例句 | 本地 ECDICT 词典（离线，v2 备选） |
| 托盘图标 | `Hardcodet.NotifyIcon.Wpf` | 成熟小巧的 WPF 托盘封装 | 手写 Shell_NotifyIcon P/Invoke |
| 热键 | `RegisterHotKey` P/Invoke | Windows 原生全局热键，零依赖 | 无 |
| 配置 | `System.Text.Json` + %APPDATA% | 无额外依赖 | — |
| HTTP | `HttpClient` 单例 | 内置 | — |

---

## 3. 系统架构

### 3.1 总体流程

```
┌─────────────────────────── 常驻进程（托盘，单实例） ───────────────────────────┐
│                                                                                │
│  [全局热键 F1]                                                                 │
│       │                                                                        │
│       ▼                                                                        │
│  ① ScreenCaptureService ── 先抓全屏 Bitmap（冻结屏幕）                          │
│       │                                                                        │
│       ▼                                                                        │
│  ② OverlayWindow 全屏遮罩 ── 显示冻结帧 + 框选交互（8 手柄微调）                 │
│       │  Enter / 双击确认，Esc 取消                                             │
│       ▼                                                                        │
│  ③ 裁剪选区 → OcrService (WinRT OCR) → OcrResult(文本 + 行/词边界)              │
│       │                                                                        │
│       ├── 单个英文单词 ──→ DictionaryService（Free Dictionary API，可选）        │
│       ▼                                                                        │
│  ④ TranslationService（百度 API，1 QPS 客户端排队）                              │
│       │                                                                        │
│       ▼                                                                        │
│  ⑤ PinResultWindow 钉屏浮窗：原文 + 译文（单词则显示词典卡片）                   │
│     可拖动 / 钉住 / 复制 / 重新查询 / Esc 关闭                                   │
│                                                                                │
└────────────────────────────────────────────────────────────────────────────────┘
```

### 3.2 架构原则

1. **进程模型**：单进程单实例（`Mutex` 防多开），无主窗口，`App.xaml` 关闭 ShutdownMode，由托盘菜单退出。
2. **分层**：UI（Views/ViewModels）与逻辑（Services）分离；Services 面向接口编程，便于单测与替换翻译供应商。
3. **异步管道**：`热键 → 截图（同步，<50ms）→ OCR（异步）→ 翻译（异步）`，浮窗先弹出显示"识别中…/翻译中…"占位，结果到达后原地刷新，用户感知延迟更低。
4. **失败降级**：OCR 失败 → 浮窗显示错误提示；翻译失败（网络/限流）→ 自动重试 2 次（指数退避），仍失败则提示并可一键重试。

---

## 4. 模块详细设计

### 4.1 HotkeyService（全局热键）

- P/Invoke `RegisterHotKey(hwnd, id, modifiers, vk)` / `UnregisterHotKey`。
- 默认绑定 **F1**（与 Snipaste 截图一致），可在设置中修改；注册冲突时托盘气泡提示。
- 通过 `HwndSource.AddHook` 接收 `WM_HOTKEY`，用 `Task.Run` 触发截图流程，避免阻塞消息循环。

### 4.2 ScreenCaptureService（抓屏）

- **冻结屏幕技巧**（关键顺序）：
  1. 先用 `System.Drawing.Graphics.CopyFromScreen` 抓取虚拟屏完整位图；
  2. **然后**才显示全屏遮罩窗口并把该位图设为背景。
  > 若顺序颠倒，抓到的会是遮罩自身。这是此类工具的经典坑。
- 多显示器：使用 `SystemParameters.VirtualScreenLeft/Top/ScreenWidth/ScreenHeight` 覆盖整个虚拟屏。
- DPI：进程清单声明 `PerMonitorV2`，物理像素与 WPF 逻辑像素之间按各显示器 DPI 换算，截图与选区坐标统一使用**物理像素**，仅在渲染层换算。

### 4.3 OverlayWindow（截图遮罩 + 框选）

- 全屏无边框顶级窗口：`WindowStyle=None`、`AllowsTransparency=True`、`Topmost=True`、`ShowInTaskbar=False`、`ShowActivated=True`。
- 背景渲染：冻结帧原图 + 半透明黑色遮罩，选区内部"挖洞"显示原图（用 `GeometryGroup`/`CombinedGeometry` EvenOdd 实现蒙版）。
- 交互细节（对标 Snipaste）：
  - 按住左键拖出矩形；松开后显示 8 个手柄可微调；
  - 鼠标悬停时按磁性吸附自动套住文字块（v1.1，基于 OCR 词边界做"智能选区"，v1 先手动框选）；
  - **Enter / 双击** 确认；**Esc** 取消；**Ctrl+C** 确认并复制原图到剪贴板；
  - 选区下方浮动工具条：翻译（默认）、复制图片、取消。
- 确认后裁剪 `Bitmap` 交给 OCR，遮罩立即关闭（不等待 OCR/翻译完成）。

### 4.4 OcrService（OCR）

- 封装 WinRT `Windows.Media.Ocr`：
  - 启动时检查 `OcrEngine.AvailableRecognizerLanguages` 是否含 `en`，缺失则托盘气泡引导用户到"设置 → 时间和语言 → 添加语言（英语）"安装 OCR 组件，并给出设置页深链 `ms-settings:regionlanguage`。
  - `OcrEngine.TryCreateFromLanguage(new Language("en"))` 创建引擎。
- 输入裁剪后的 `SoftwareBitmap`，输出归一化的 `OcrOutcome`：

```csharp
public record OcrOutcome
{
    public string FullText { get; init; }        // 按行拼接、已清洗
    public IReadOnlyList<OcrLine> Lines { get; init; }
    public bool IsSingleWord { get; init; }      // 清洗后仅一个英文单词
}

public record OcrLine(string Text, Rect BoundingBox);
```

- 文本清洗规则：去零宽字符；按行去重合并；`Regex.IsMatch(text, @"^[A-Za-z][A-Za-z\-']*$")` 判定单词模式。

### 4.5 TranslationService（百度翻译）

- 实现 `ITranslationProvider`（见 §5.2），内部维护：
  - **限流器**：`SemaphoreSlim(1,1)` + 最近请求时间戳，保证客户端节流 ≥ 1.05s/次（标准版 1 QPS），排队而非丢弃；
  - **重试**：网络异常 / `error_code ∈ {52001, 52002}` 时指数退避重试最多 2 次；
  - **签名**：`MD5(appid + q + salt + secret)`，UTF-8 字节序（见 §5.1）。
- 超时：连接 3s / 读取 5s。

### 4.6 DictionaryService（单词词典，可选增强）

- 触发条件：`OcrOutcome.IsSingleWord == true`。
- 调用 Free Dictionary API 拿音标、词性、释义、例句；**失败静默降级**为普通句子翻译结果，绝不阻塞主流程（词典请求与翻译请求并行发出）。
- 展示：浮窗切换为词典卡片样式（见 §4.7）。

### 4.7 PinResultWindow（钉屏结果浮窗）

- 无边框 `Topmost` 窗口，初始位置在选区右侧（超出屏幕则左侧/下方），默认**非激活**显示（`WS_EX_NOACTIVATE`），不抢用户焦点。
- 结构：

```
┌───────────────────────────────┐
│ 🔒(钉住)  📋复制  ↻重试  ✕   │  ← 工具栏，悬停整窗时出现
│ ── 原文（可选中复制）───────── │
│ The quick brown fox           │
│ ── 译文 ───────────────────── │
│ 敏捷的棕色狐狸                 │
└───────────────────────────────┘
  单词模式：
┌───────────────────────────────┐
│ fox   /fɒks/  🔊              │
│ n. 狐狸；v. 欺骗               │
│ ─ 例句 ─                       │
│ A fox ran across the road.    │
└───────────────────────────────┘
```

- 状态机：`Recognizing → Translating → Done / Failed`；Failed 态提供"重试"。
- 拖动：标题区 `MouseLeftButtonDown` + `DragMove()`；钉住后固定 Topmost，未钉住时"点击外部自动淡出"（可关）。
- 同一时间允许多个结果浮窗共存（各自独立）。

### 4.8 ConfigService（配置）

- 路径：`%APPDATA%\SnapTranslate\config.json`，见 §5.4 schema。
- 密钥保护：`appid` 明文；`secret` 使用 Windows DPAPI（`System.Security.Cryptography.ProtectedData`，CurrentUser 范围）加密后以 Base64 存储，避免明文落盘。
- 配置变更热更新：热键变更时先 `UnregisterHotKey` 再注册。

### 4.9 TrayIcon / App 生命周期

- `Hardcodet.NotifyIcon.Wpf`：右键菜单 = 截图翻译(F1) / 设置 / 开机自启(勾选) / 检查更新(v1 占位) / 退出。
- 单实例：`Mutex("SnapTranslate.SingleInstance")`，二次启动时激活托盘气泡提示。
- 开机自启：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`（注册表，免管理员权限，比计划任务/启动文件夹简单可靠）。

---

## 5. 接口协议

### 5.1 百度翻译 · 通用翻译 API（外部）

- **Endpoint**：`POST https://fanyi-api.baidu.com/api/trans/vip/translate`（Content-Type: `application/x-www-form-urlencoded`，UTF-8）
- **请求参数**：

| 参数 | 值/说明 |
|------|---------|
| `q` | 待翻译文本（UTF-8，单次 ≤ 6000 字节；选区文本远小于此） |
| `from` | 固定 `en` |
| `to` | 固定 `zh` |
| `appid` | 控制台申请的 AppID |
| `salt` | 随机数（UUID 去横线或随机整数的字符串） |
| `sign` | **MD5(appid + q + salt + 密钥)**，对拼接字符串按 UTF-8 取字节计算，输出 32 位小写十六进制 |

- **成功响应**：

```json
{
  "from": "en", "to": "zh",
  "trans_result": [ { "src": "hello", "dst": "你好" } ]
}
```

- **错误响应**：`{ "error_code": "54001", "error_msg": "Invalid Sign" }`

| error_code | 含义 | 客户端处理 |
|-----------|------|-----------|
| 52001 | 超时 | 重试（退避） |
| 52002 | 系统错误 | 重试 1 次 |
| 54000 | 请求参数非法 | 提示（本地 bug） |
| 54001 | 签名错误 | 提示检查密钥，不重试 |
| 54003 | QPS 超限 | 退避 1.2s 重试 |
| 54004 / 54005 | 账户余额/请求量受限 | 提示升级额度，不重试 |

- **JSON 反序列化模型**：

```csharp
public record BaiduResponse(
    string? From, string? To,
    IReadOnlyList<BaiduTransItem>? TransResult,
    string? ErrorCode, string? ErrorMsg);

public record BaiduTransItem(string Src, string Dst);
```

- 注意：错误响应中 HTTP 状态码仍为 200，必须以 `error_code` 字段判定成败。

### 5.2 Free Dictionary API（外部，单词增强）

- **Endpoint**：`GET https://api.dictionaryapi.dev/api/v2/entries/en/{word}`
- 成功返回数组；取 `[0]`：
  - `phonetics[]` → 取含 `text` 的项作为音标
  - `meanings[]` → `partOfSpeech`（词性）+ `definitions[].definition / example`
- 404 = 单词不存在 → 静默降级为普通翻译。超时 3s，超时同样降级。

### 5.3 内部接口（C#）

```csharp
public interface IOcrService
{
    /// 初始化；返回 false 表示系统缺少英文 OCR 语言包
    Task<bool> EnsureEngineAsync(CancellationToken ct = default);

    Task<OcrOutcome?> RecognizeAsync(Bitmap region, CancellationToken ct = default);
}

public interface ITranslationProvider
{
    string Name { get; }                       // "Baidu"
    Task<TranslationResult> TranslateAsync(
        string text, string from = "en", string to = "zh",
        CancellationToken ct = default);
}

public record TranslationResult(
    bool Success, string? Translated, string? ErrorCode, string? ErrorMessage);

public interface IDictionaryProvider
{
    Task<WordEntry?> LookupAsync(string word, CancellationToken ct = default);
}

public record WordEntry(
    string Word, string? Phonetic,
    IReadOnlyList<Meaning> Meanings);

public record Meaning(
    string PartOfSpeech,
    IReadOnlyList<string> Definitions,
    IReadOnlyList<string> Examples);
```

- 翻译供应商可替换：`ITranslationProvider` 注册表式管理（v2 预留 DeepL 实现）。

### 5.4 配置文件 Schema（config.json）

```json
{
  "version": 1,
  "baidu": { "appid": "20261125...", "secretEnc": "DPAPI:base64..." },
  "hotkey": { "key": "F1", "modifiers": ["None"] },
  "pin": {
    "fontSize": 14,
    "opacity": 0.96,
    "autoFadeOut": false,
    "maxWindows": 8
  },
  "behavior": {
    "copyTextAfterTranslate": false,
    "autoStartWithWindows": false
  }
}
```

---

## 6. 项目结构

```
SnapTranslate/
├── SnapTranslate.sln
├── src/
│   └── SnapTranslate/                  # 单项目（轻量优先，文件夹即分层）
│       ├── SnapTranslate.csproj        # net8.0-windows, UseWPF, WinRT Ocr 引用
│       ├── App.xaml(.cs)               # ShutdownMode=OnExplicitShutdown
│       ├── app.manifest                # PerMonitorV2 DPI
│       ├── Interop/
│       │   └── NativeMethods.cs        # RegisterHotKey / WS_EX_NOACTIVATE 等
│       ├── Models/
│       │   ├── OcrOutcome.cs
│       │   ├── TranslationResult.cs
│       │   ├── WordEntry.cs
│       │   └── AppConfig.cs
│       ├── Services/
│       │   ├── HotkeyService.cs
│       │   ├── ScreenCaptureService.cs
│       │   ├── OcrService.cs
│       │   ├── BaiduTranslationProvider.cs   (+ 限流/重试/签名)
│       │   ├── FreeDictionaryProvider.cs
│       │   ├── ConfigService.cs        (+ DPAPI)
│       │   └── SingleInstanceGuard.cs
│       ├── Pipelines/
│       │   └── TranslatePipeline.cs    # 编排：截图→OCR→翻译→浮窗（调用序）
│       ├── ViewModels/
│       │   ├── OverlayViewModel.cs
│       │   ├── PinResultViewModel.cs
│       │   └── SettingsViewModel.cs
│       ├── Views/
│       │   ├── OverlayWindow.xaml(.cs)
│       │   ├── PinResultWindow.xaml(.cs)
│       │   └── SettingsWindow.xaml(.cs)
│       ├── Controls/
│       │   └── SelectionAdorner.cs     # 8 手柄微调
│       └── Assets/
│           └── tray.ico
├── tests/
│   └── SnapTranslate.Tests/            # xUnit：签名/清洗/限流/管道编排(Mock)
└── tools/
    └── sign-calc/                      # 小控制台：本地核对百度签名算法
```

- 依赖 NuGet：`Hardcodet.NotifyIcon.Wpf`、`System.Drawing.Common`（仅 Windows）、`xunit`（测试）。
- 发布：`dotnet publish -c Release -r win-x64 --self-contained false`（依赖运行时，体积最小）；提供 `--self-contained true` 单文件版作为可选下载。

---

## 7. 关键技术点与风险

| # | 风险/难点 | 对策 |
|---|-----------|------|
| 1 | 抓屏顺序错误导致抓到遮罩自身 | 固定顺序：先 CopyFromScreen，后 Show 遮罩（单测无法覆盖，靠 code review + 集成自测） |
| 2 | 多显示器混合 DPI 下选区偏移 | 全程物理像素坐标系；每显示器 DPI 换算函数集中一处；实机测试 100%/125%/150% |
| 3 | 用户未装英文 OCR 语言包 | 启动检测 + 气泡引导 + `ms-settings:regionlanguage` 深链；设置页显示 OCR 状态 |
| 4 | 百度 1 QPS 限流，连续快速截图时 429/54003 | 客户端排队节流 + 退避重试；浮窗排队期间显示"排队中…" |
| 5 | 密钥明文泄露 | DPAPI CurrentUser 加密存储；日志中永不打印 sign/secret |
| 6 | WinRT OCR API 在 .NET 8 的互操作 | 目标框架 `net8.0-windows10.0.19041.0`（TpMinVersion 直接引入 WinRT API，无需额外包） |
| 7 | 浮窗抢焦点打断用户输入 | `WS_EX_NOACTIVATE` + `ShowActivated=false`；工具栏按钮用鼠标点击无需键盘焦点 |

---

## 8. 开发里程碑

| 里程碑 | 内容 | 预估 |
|--------|------|------|
| M1 | 骨架 + 热键 + 抓屏 + 遮罩框选（含手柄微调）+ Esc/Enter | 2~3 天 |
| M2 | OCR 集成（含语言包检测引导），浮窗显示识别文本 | 1~2 天 |
| M3 | 百度翻译接入（签名/限流/重试）+ 结果浮窗钉屏 | 2 天 |
| M4 | 单词模式：Free Dictionary API + 词典卡片 UI | 1~2 天 |
| M5 | 设置界面（热键/密钥/浮窗行为）+ 开机自启 + 单实例 | 1~2 天 |
| M6 | 打包、发布（含自包含单文件版）、README | 0.5 天 |

每个里程碑结束产出可运行版本，优先保证 M1~M3 打通主链路。

---

## 9. v2 展望（不承诺）

- 智能选区：OCR 先行全屏识别，鼠标悬停自动吸附文字块（Snipaste 的核心体验）
- ECDICT 本地离线词典（单词模式完全离线）
- DeepL 供应商切换、中→英反向翻译
- 截图历史与翻译历史
- macOS 版本评估（届时考虑 Tauri 复用 Services 层协议）
