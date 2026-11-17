# QJAR 工具

把原来的「注册工具」（C + Win32）和「示教器命令调试工具」（C# WinForms）合并成一个桌面程序：
左侧竖排导航切换「注册」「命令调试」「编译」三页，控制器地址可存两套，共用一次管理员登录。

界面按 [Astryx](https://github.com/meta/astryx) 的设计 token 重做，但在原生 WinForms 里自绘实现，不引入 WebView。

## 功能

- **注册页**：查询设备状态、注册设备、取消等待、单独重启示教器；进度条 + 状态徽标 + 操作日志。
- **命令调试页**：发送任意命令（UTF-8 / GBK 可选，超时 1–600000 ms），收发记录可导出。
- **编译页**：读取控制器工程 `src/Makefile`，显示并改写版本号、切换实体轴 `ETHERCAT_X86` / 虚拟轴 `VIRTUAL_SERVO`，调用 WSL 里的通用入口编译，成功后打包 `control_update.tar.gz`。
- **控制器地址**：平时只读，点「修改」才可编辑；可存两套（文档里叫地址 1 / 地址 2），点上方切换，保存后写入 `%LOCALAPPDATA%\QJAR\settings.ini`。
- **管理员登录**：同一口令解锁三页的写操作；登录状态会被记住，直到点「退出管理」。

## 结构

```
src/core/   C 业务核心 → QJCore.dll（controller_run / pendant_restart / qj_exchange）
src/ui/     C# 外壳（窗口、主题、自绘控件、三个页面、P/Invoke 封装）
Tests/      模拟控制器、通信冒烟、界面回归、Makefile 读写检查、编译页集成检查、截图工具
Docs/       UI 规格、发布说明、使用说明、Astryx token 原始导出
build.ps1   编译 → 测试 → 打包
```

线协议（28 字节头 + 校验和 + 命令/数据字段）只有一份实现，位于 `src/core/Controller.c`；命令调试页也走同一个 `qj_exchange`。

## 构建

需要 Windows 10/11 x64、Visual Studio Build Tools（MSVC x64 + Roslyn csc）。

```powershell
.\build.ps1                 # 编译 QJCore.dll + QJAR工具.exe
.\build.ps1 -Tests          # 额外编译并运行通信冒烟与界面回归
.\build.ps1 -Tests -Package # 再打一个 dist\QJAR工具-v1.1.11.zip
```

编译页依赖本机的 WSL 2 与 `/opt/qjar-build` 环境。环境包可从[公开 Release](https://github.com/zizhuoruzi/qj-wsl-env/releases/tag/wsl-env-v1)下载，解压密码由发布者授权提供。`QJAR工具.exe --guide` 打开环境准备指引；提示词单一来源为 [wsl-agent-prompt.txt](<Assets/wsl-agent-prompt.txt>)，构建时同步生成 [agent-wsl-setup.md](<Docs/agent-wsl-setup.md>)。选择工程目录会记住上次位置，并在下三级内查找唯一工程，多个候选不会自动选中。

运行时不需要安装 .NET（使用系统自带的 .NET Framework 4.x）；示教器自动重启使用系统 OpenSSH 客户端。

## 测试

- `Smoke.exe`：对进程内模拟控制器验证查询、注册（含轮询等待）、裸命令收发。
- `UiCheck.exe`：驱动真实窗口走一遍口令校验、登录状态持久化、查询、注册、命令发送、手动重启，以及编译页的 Makefile 读写。
- `BuildCheck.exe`：只测 `ProjectMakefile` 的解析、校验与原子写回（版本号、DRIVER、行尾、编码）。
- `BuildRun.exe <控制器工程目录>`：手动集成检查，用真实窗口走一次 WSL 编译 + 打包（需要本机 WSL 环境，不在自动回归里）。

## 说明

- 该项目面向内部设备维护，口令与设备地址按现场环境硬编码，公开仓库里的值仅作示例。
- 标题字体为源流明体裁剪版（`Assets/QJToolDisplay.otf`），许可见 `Assets/Fonts/OFL.txt`。
- 图标来自用户提供的猴子造型素材。