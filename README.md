# MemoryBooster 开发文档

> 版本: 2.0 | 最后更新: 2026-04-29

---

## 1. 项目概述

MemoryBooster（编译产物名 **内存清理加速器.exe**）是一个 Windows 内存清理工具，UI 风格参考 360 安全卫士加速球。平时悬浮球隐藏在屏幕右侧，鼠标靠近自动滑出，左键点击一键清理内存，右键打开管理面板。

**核心特性：**
- 悬浮加速球（屏幕右侧自动隐藏/滑出）
- 一键内存清理（释放所有进程 WorkingSet）
- 进程列表查看与管理（排序/筛选/搜索/批量清理或结束）
- 右键菜单（结束进程 / 打开文件位置 / 复制路径）
- 实时网速监控（可开关，节省资源）
- 自动定时清理 + 自身瘦身
- 开机自启动
- 加速球透明度/皮肤设置
- **单实例运行**（防止多开）
- **懒加载异常日志**（仅出错时才生成 error.log）

---

## 2. 技术架构

```
┌─────────────────────────────────────┐
│   MemoryBooster (C# WPF)           │
│   .NET Framework 4.5 (向下兼容 4.x) │
│                                     │
│   MainWindow ── 悬浮加速球          │
│   BoosterPanel ── 管理面板          │
│                                     │
│   Services/                         │
│     ├─ NativeInterop (P/Invoke)     │
│     ├─ MemoryService                │
│     ├─ NetworkMonitor               │
│     └─ StartupManager               │
├─────────────────────────────────────┤
│   MemoryCore.dll (C++ DLL)          │
│   Win32 API: psapi / iphlpapi       │
└─────────────────────────────────────┘
```

| 层 | 技术 | 说明 |
|---|---|---|
| UI | C# WPF (.NET Framework 4.5) | 悬浮窗 + 管理面板 |
| 后端 | C++ DLL (MemoryCore.dll) | 内存/进程/网络操作 |
| 互操作 | P/Invoke (CallingConvention.Cdecl) | C# 调用 C++ |
| 序列化 | XmlSerializer | 设置持久化 |
| 构建 | dotnet build (C#) + CMake (C++) | 分别构建 |

**选择 .NET Framework 4.5 的原因：** 4.5 是支持 `async/await` + `Task.Run` + `Dispatcher.InvokeAsync` 的最低 4.x 版本，Win7 SP1 及以上系统通过 Windows Update 都装有 4.5+ 运行时；将目标框架设为 4.5 意味着 4.5/4.6/4.7/4.8 任意版本都能跑，兼容面最大。

> **注**：因 net45 原生未内置 `System.ValueTuple`（tuple 返回值），项目引入 `System.ValueTuple 4.5.0` NuGet 包进行 backport。

---

## 3. 目录结构

```
02/
├── MemoryCore/                   # C++ DLL 项目
│   ├── CMakeLists.txt            # CMake 构建文件
│   ├── MemoryCore.h              # 导出函数声明 + 结构体定义
│   ├── MemoryCore.cpp            # 实现（Win32 API 调用）
│   └── build/                    # CMake 构建输出
│       └── bin/Release/
│           └── MemoryCore.dll
│
├── MemoryBooster/                # C# WPF 项目
│   ├── MemoryBooster.csproj      # 项目文件 (net45 + AssemblyName=内存清理加速器)
│   ├── App.xaml / App.xaml.cs    # 入口 + 单实例 Mutex + 全局异常处理
│   ├── MainWindow.xaml / .cs     # 悬浮加速球窗口
│   ├── Assets/
│   │   └── icon.ico              # 应用图标
│   ├── Models/
│   │   ├── ProcessInfo.cs        # 进程数据模型
│   │   └── AppSettings.cs        # 设置模型 + XML 持久化
│   ├── Services/
│   │   ├── NativeInterop.cs      # P/Invoke 声明 + 结构体映射
│   │   ├── MemoryService.cs      # 内存/进程业务逻辑
│   │   ├── NetworkMonitor.cs     # 网速计算
│   │   └── StartupManager.cs     # 开机自启（注册表）
│   ├── Views/
│   │   ├── BoosterPanel.xaml/.cs # 管理面板（进程/网速/设置）
│   │   └── ToolTipPopup.xaml/.cs # 清理结果提示气泡
│   ├── Converters/
│   │   └── ValueConverters.cs    # XAML 值转换器
│   └── MemoryCore.dll            # DLL 副本（构建时复制到输出）
│
├── favicon.ico                   # 原始图标文件
└── DEV_DOC.md                    # 本文档
```

---

## 4. 模块详解

### 4.1 MemoryCore.dll (C++)

纯 C 接口导出，`extern "C"` + `__declspec(dllexport)`，无名称修饰。

| 函数 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `CleanAllWorkingSets(void)` | 无 | 成功清理的进程数 | 遍历所有进程调用 EmptyWorkingSet |
| `CleanProcessWorkingSet(pid)` | 进程 PID | 1=成功, 0=失败 | 清理单个进程 |
| `GetProcessList(buffer, count)` | 输出缓冲区 + 容量 | 实际进程数 | 返回 ProcessInfoNative 数组 |
| `KillProcess(pid)` | 进程 PID | 1=成功, 0=失败 | TerminateProcess |
| `GetSystemMemoryInfo(info)` | 输出指针 | 1=成功 | GlobalMemoryStatusEx |
| `GetNetworkSnapshot(snapshot)` | 输出指针 | 1=成功 | GetIfTable 累计网络字节 |

**关键结构体（pack=1）：**

```c
ProcessInfoNative { pid, name[260], filePath[520], workingSetSize, privateBytes, cpuPercent }
SystemMemoryInfo  { totalPhysical, availablePhysical, memoryLoadPercent }
NetworkSnapshot   { bytesSent, bytesRecv }
```

**依赖库：** `psapi.lib` (EmptyWorkingSet)、`iphlpapi.lib` (GetIfTable)

**构建：**
```bash
cd MemoryCore
mkdir build && cd build
cmake .. -G "Visual Studio 17 2022" -A x64
cmake --build . --config Release
```

### 4.2 NativeInterop.cs — P/Invoke 层

C# 侧的结构体用 `[StructLayout(LayoutKind.Sequential, Pack = 1)]` 与 C++ 一一对应。字符串字段用 `[MarshalAs(UnmanagedType.ByValTStr, SizeConst = N)]`。

所有 DLL 导入使用 `CallingConvention.Cdecl`。

### 4.3 MemoryService.cs — 业务层

- `CleanMemory()` → 调用 DLL 清理，返回 `(清理进程数, 释放字节数)`
- `GetProcessList()` → 获取进程列表并按内存从大到小排序
- `GetMemoryInfo()` / `GetMemoryLoadPercent()` → 内存占用百分比
- `CleanProcess(pid)` / `KillProcess(pid)` → 单进程操作

### 4.4 NetworkMonitor.cs — 网速监控

通过 DLL 的 `GetNetworkSnapshot` 获取累计收发字节，两次快照差值除以时间间隔得到实时网速。`MainWindow` 每 1 秒调用一次 `Update()`。

### 4.5 AppSettings.cs — 设置持久化

- 存储路径：`%APPDATA%\MemoryBooster\settings.xml`
- 使用 `XmlSerializer` 序列化/反序列化
- `Save()` / `Load()` 均有 try-catch，出错时静默降级为默认值

**可配置项：**

| 属性 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| AutoStart | bool | false | 开机自启 |
| AutoClean | bool | false | 自动清理 |
| AutoCleanIntervalMinutes | int | 10 | 自动清理间隔(分钟) |
| BallOpacity | double | 0.9 | 加速球透明度 |
| BallSkin | int | 0 | 皮肤 (0=绿, 1=蓝, 2=紫) |
| BallPositionY | int | -1 | 记忆纵坐标 (-1=屏幕中间) |

### 4.6 StartupManager.cs — 开机自启

通过写入/删除注册表 `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` 实现。

### 4.7 MainWindow — 悬浮加速球

**窗口属性：** `WindowStyle=None, AllowsTransparency=True, Topmost=True, ShowInTaskbar=False`

**交互逻辑：**
- **鼠标靠近** → 从屏幕右侧滑出（动画 200ms）
- **鼠标离开** → 800ms 后滑回隐藏（仅露出 14px）
- **左键点击** → 一键清理内存（带旋转动画），如面板已开则关闭面板
- **左键拖拽** → 上下拖动调整位置（自动保存 Y 坐标）
- **右键点击** → 打开/关闭管理面板

**定时器：**
| 定时器 | 间隔 | 用途 |
|---|---|---|
| _updateTimer | 2s | 刷新内存百分比 + 球体颜色 |
| _netTimer | 1s | 刷新网速显示（**按设置开关启停**，关闭时不跑） |
| _autoCleanTimer | 用户设定 | 自动清理内存 |
| _selfTrimTimer | 60s | **自身瘦身**：对本进程调 `EmptyWorkingSet`，保持 MemoryBooster 自身占用最小 |
| _hideTimer | 800ms | 延迟滑回屏幕边缘 |

**球体颜色逻辑：** <50% 绿 → 50-80% 黄橙 → >80% 红

`ApplyNetMonitorSetting()`：设置面板切换网络监视开关时，按需启动或停止 `_netTimer` 并联动隐藏 `NetSpeedPanel`。

### 4.8 BoosterPanel — 管理面板

底部三个 Tab 页：

| Tab | 功能 |
|---|---|
| ⚡ 加速 | 内存占用百分比 + 一键加速按钮 + 进程列表（名称、内存、CPU、复选框）+ 搜索 + 排序下拉 + 显示路径/显示前台窗口进程复选框 + 清理/结束进程按钮 |
| ⇅ 网速 | 上传/下载实时速度 + 累计流量（可通过启用开关关闭整个网络监控节省资源） |
| ⚙ 设置 | 开机自启、自动清理、清理间隔、网络监视开关、透明度、皮肤切换、关于信息、退出按钮 |

**UI 风格：** 蓝绿渐变头部 + 白色内容区 + 渐变底部 Tab 栏

**进程列表关键行为：**
- **搜索**：输入关键词时自动包含前台进程（忽略“显示前台窗口进程”复选框），避免刚切换焦点的应用搜不到。
- **分组**：前台进程置顶并用 `IsSectionBreak` + DataTrigger 画分割线（**取代 `GroupStyle` 以规避 WPF DataGrid 虚拟化 + 分组的已知崩溃**）。
- **滚动锚定**：刷新时按 PID 记住当前视口首行 / 当前选中项，新快照就位后用 `ScrollIntoView` 回滚并恢复 `SelectedItem`，避免新增前台进程把用户视线挤走。
- **红色选中状态**：`DataGridCell` `IsSelected` Trigger 改为 `#FFECEC` 背景 + `#C62828` 红字 + `Bold`，视觉明显；点击列表外区域才清除选中（`PreviewMouseLeftButtonDown` 视觉树判断）。
- **右键菜单目标锁定**：菜单弹出前通过 `ContextMenuOpening` 把 `SelectedItem` 拨回当前红色选中项；`OnProcessSelected` 忽略 `Mouse.RightButton == Pressed` 期间的选中切换；`GetMenuTarget` 统一取目标。这样即便右键点在其他进程上，菜单依然针对当前选中项操作。
- **路径显示**：为避免虚拟化回收容器时 `RelativeSource AncestorType=Window` 导致 `PropertyMetadata.get_DefaultValue` NRE，改为给 `ProcessInfo` 新增 `PathVisibility` 属性（实现 `INotifyPropertyChanged`），DataTemplate 直接绑 DataContext；checkbox 变化时 fan-out 到所有行。

### 4.9 单实例运行（防多开）

`App.OnStartup` 第一步创建命名 Mutex `Local\MemoryBooster.SingleInstance.{固定GUID}`：
- 若 `createdNew == true` → 本实例为首发，继续启动流程；`OnExit` 时 `ReleaseMutex` + `Dispose`。
- 若 `createdNew == false` → 已有实例在跑，弹 `MessageBox` 提示 “内存清理加速器已在运行。请从托盘图标打开主界面。”，随后 `Shutdown()` 立即退出。

`Local\` 前缀限定当前用户会话，不跨用户冲突；名字包含固定 GUID 防止与同名产品混淆。

---

## 5. 异常处理策略

1. **全局异常捕获** (`App.xaml.cs`)：
   - `DispatcherUnhandledException` → 记录日志，`e.Handled = true` 防止崩溃
   - `AppDomain.UnhandledException` → 记录日志
   - `TaskScheduler.UnobservedTaskException` → 记录日志
   - 日志**懒加载**：`EnsureLogPath()` 仅在真正出错时才探测并创建 `error.log`；正常启动 / 运行不会留下空文件。
   - 路径优先 exe 同目录；写入失败回退到 `%LocalAppData%\MemoryBooster\error.log`。

2. **定时器回调**：所有 `Tick` 回调均用 `try { } catch { }` 包裹

3. **DLL 调用**：MemoryService 中的调用可能抛出 `DllNotFoundException` / `EntryPointNotFoundException`，调用方已保护

4. **XAML 初始化**：BoosterPanel 使用 `_loaded` 标志位，防止 `InitializeComponent` 期间事件处理器访问未初始化的控件

---

## 6. 构建与部署

### 6.1 构建步骤

```bash
# 1. 构建 C++ DLL
cd MemoryCore/build
cmake --build . --config Release

# 2. 构建 C# WPF
cd MemoryBooster
dotnet build --configuration Release

# 3. 复制 DLL 到输出目录
copy MemoryCore\build\bin\Release\MemoryCore.dll MemoryBooster\bin\Release\net45\
```

### 6.2 部署文件

将以下文件打包即可在任何已安装 .NET Framework 4.5+ 的 Windows 上运行：

```
内存清理加速器.exe     (原 MemoryBooster.exe，经 AssemblyName 改名)
MemoryCore.dll
```

> ⚠ 两个文件必须在同一目录下。MemoryCore.dll 是 x64 编译，需在 64 位系统运行。

### 6.3 运行时生成文件

| 文件 | 路径 | 说明 |
|---|---|---|
| settings.xml | %APPDATA%\MemoryBooster\ | 用户设置 |
| error.log | exe 同目录（可写时）或 %LocalAppData%\MemoryBooster\ | 异常日志（**仅首次异常时才创建**） |

---

## 7. 已知注意事项

1. **DLL 导出名称**：`MemoryCore.h` 中所有函数必须在 `extern "C"` 块内，且不能有中文注释干扰编译器导出。使用 `dumpbin /exports MemoryCore.dll` 验证。

2. **结构体对齐**：C++ 和 C# 两侧必须都用 `pack=1`，否则字段偏移不一致导致数据错乱。

3. **.NET Framework 4.5 兼容性**：
   - net45 原生无 `ValueTuple` → 须引用 `System.ValueTuple 4.5.0` NuGet 包
   - 不能使用 `nullable reference types` (`string?`)
   - 不能使用 `is not` 模式匹配（部分）
   - 不能使用 `Environment.ProcessPath`（用 `Assembly.GetExecutingAssembly().Location`）
   - `LangVersion=latest` 时 file-scoped namespace / record 等语法糖仍可用（纯 C# 语言层）

4. **WPF DataGrid 虚拟化坑**：
   - `GroupStyle` + 虚拟化 + 快速滚动 会 NRE 崩溃 → 项目用 `IsSectionBreak` 代替分组
   - `CellTemplate` 内使用 `Binding Tag, RelativeSource={RelativeSource AncestorType=Window}` 在容器回收瞬间 parent 为 null，`PropertyMetadata.get_DefaultValue()` 抛 NRE → 改为绑定 DataContext 自身属性（`ProcessInfo.PathVisibility`）

5. **权限**：清理其他进程内存需要 `SeDebugPrivilege`，程序启动时 DLL 会自动提权。普通用户权限下只能清理自己的进程。

6. **单实例**：多开会被 Mutex 拦截弹窗退出；若进程异常残留 Mutex，重启操作系统或手动 `taskkill /f /im 内存清理加速器.exe` 即可恢复。

---

## 8. 扩展方向

- 增加系统托盘图标（NotifyIcon）
- 进程黑/白名单（不清理/优先清理）
- 进程级网速监控（ESTATS，需管理员权限）
- 内存清理历史统计
- 多语言支持
- 自动更新

---

## 9. 版本记录

### v2.0（2026-04-29）
- **构建**：目标框架 net48 → net45；产物改名 `内存清理加速器.exe`；引入 `System.ValueTuple` backport。
- **稳定性**：单实例 Mutex 防多开；error.log 懒加载，正常运行不再产生空文件。
- **DataGrid 虚拟化 NRE 修复**：`ProcessInfo.PathVisibility` 替代 `RelativeSource AncestorType=Window`。
- **滚动/选中持久化**：PID 锚定滚动位置 + 红色选中跨刷新保留 + 点击列表外清除。
- **右键菜单**：新增“结束进程 / 打开文件位置 / 复制路径”，菜单目标始终锁定当前选中项。
- **搜索增强**：搜索时自动包含前台进程；搜索行 4 列布局 + 排序下拉缩窄。
- **资源优化**：网络监控可开关；新增 60s 自身瘦身定时器。

### v1.0
- 悬浮加速球 + 管理面板基础功能
- C++ DLL + C# P/Invoke 架构
