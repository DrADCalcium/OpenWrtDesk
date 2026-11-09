# OpenWrt Desk

一个 WinUI 3 原生的 OpenWrt / OpenClash 桌面管理工具。使用 C# / .NET 9，直接访问路由器接口。

当前版本 0.1.2：节点与分流规则交给 Mihomo 面板管理；本程序的运行日志只显示 OpenClash 程序日志。保留 0.1.1 的即时主题切换修复，详见 CHANGELOG.md。

项目源码采用 [MIT 许可证](LICENSE)。便携包所含第三方运行时与组件适用各自的许可证，相关声明随包保留。

## 功能

- 多设备连接档案：场景分组、排序、复制、修改、删除、连接测试和断开。
- 自动识别：结合本机网络、网关 MAC、已登记的路由器身份及可获得的邻居 MAC 选择设备；候选不唯一时交由用户选择。
- Windows DPAPI 凭据与备份保护；普通导出排除凭据，可使用口令加密导出并在其他电脑导入。
- 硬件监控：CPU、内存、温度、系统负载、运行时间、固件、网卡及存储空间。
- 流量：区分 WAN 接口与 Mihomo 的流量、连接数及内核报告的内存。
- OpenClash：启动、停止、重启、规则/全局/直连模式、日志等级、已有配置切换和订阅更新。
- 订阅新增/编辑/启用/删除；DNS 模式、接管方式、IPv6 和域名嗅探设置。
- 本机配置备份恢复；修改前自动备份，保存后回读核对。
- OpenClash 程序日志自动读取、暂停、手动刷新、关键词筛选与脱敏导出；不连接 Mihomo 内核日志流。
- 托盘、后台降频、离线重试退避、网络变化检测、资源阈值与离线通知。
- 多设备摘要、最近 720 个采样点的本地历史、主题、刷新频率、窗口大小与启动方式设置。

## 运行

解压便携包后运行 `OpenWrtDesk.exe`，请保留同目录中的全部文件。已包含 .NET 与 WinUI 运行文件，无需另装这些运行时。
目标：Windows 10 2004 或更新版本、Windows 11，x64。已在开发用 Windows 电脑上验证，其他系统版本仍需实际验证。

首次使用：

1. 打开“设备档案”，新增设备，填写名称、管理地址、用户名和密码。
2. 点击“保存并连接”，登记设备身份及当前网络。
3. 下次启动时，唯一匹配当前网络的已登记设备会自动连接。
4. 若设备有独立的 Mihomo 控制地址或密钥，可在设备档案的高级选项中填写。

同一地址更换路由器硬件后，在编辑档案时选择“重新登记设备身份”。
托盘右键可打开窗口、切换设备或退出。关闭窗口默认收起到托盘，可在应用设置中调整。
重复打开程序会恢复已有窗口，避免启动多个副本。
应用数据保存在程序目录的 `data/` 中；需要移动到可写目录后运行。

## 修改与恢复

- 模式和日志等级可以选择仅本次运行，或保存到路由器。
- 持久化修改前备份 OpenClash 配置，保存后回读核对。
- 订阅更新前额外备份对应配置正文；当前实现请求“更新但不立即重启”，下载完成后检查日志并按需重启。
- 高级设置保存后会重启 OpenClash，并等待服务及控制接口恢复。
- 备份按设备保存，最多 20 份。恢复后会回读核对文件，按需重启服务以加载恢复的内容。
- 部分固件未开放文件读写或相应 RPC 权限时，应用会报告缺项；不会自动扩大路由器权限。

## 连接与隐私

所有管理请求只发往设备档案中配置的地址。无第三方遥测，不内置代理内核。
系统 HTTP 代理不参与路由器连接，访问 HTTPS 时正常验证证书；自签名证书可填写核验过的 SHA-256 指纹。
密码、会话和控制密钥不写入普通日志；订阅 URL 在导出日志时脱敏。
DPAPI 文件仅对当前 Windows 用户可解密。跨电脑迁移请使用“加密导出”；普通导出不含密码和密钥。
本地数据、备份及运行日志不应加入源码或公开分发包。

## 验证范围

已在一台运行 OpenWrt / OpenClash 的 x86 设备上完成实机联调。公开源码不包含实机地址、硬件标识、设备档案或原始日志。

0.1.2 的 25 项自动检查覆盖凭据与导入导出、身份校验、运行模式、持久化写入、订阅、服务控制，以及 OpenClash 程序日志分离、日志轮转、增量读取、旧版文件回退、脱敏、缓存上限和取消刷新。节点与分流规则已从应用及后台加载中移除。
此前实机测试覆盖硬件与网络读取、流量、订阅增删改、模式设置回读、日志等级实际改变和恢复、配置保存备份、文件恢复及自动识别。本次更新对实机仅做日志读取验证。
实机测试结束后核对了原始配置文件与原始日志等级，保持一致。

实际服务停止/重启、DNS/TUN 切换的业务影响由模拟测试覆盖；未为联调主动中断这台路由器的联网。
跨场景、不同固件及其他 Windows 版本尚需对应设备的实际验证。Mihomo 内核若报告内存为 0，界面显示“内核未报告”。

## 开发

目录：

- `src/OpenWrtDesk.Core`：协议、监控、配置写入、身份校验、数据存储。
- `src/OpenWrtDesk`：WinUI 3 原生界面与 Win32 托盘。
- `tests/OpenWrtDesk.Checks`：无额外测试框架的可执行检查，模拟 HTTP 服务不需要管理员权限。
- `outputs`：便携版、源码包及脱敏验证报告。

需要 .NET 9 SDK、Windows SDK 10.0.26100、WinUI 开发环境。依赖固定于项目文件。

```powershell
dotnet build src/OpenWrtDesk/OpenWrtDesk.csproj -c Release
dotnet run --project tests/OpenWrtDesk.Checks -c Release -- --checks work/checks-data
dotnet publish src/OpenWrtDesk/OpenWrtDesk.csproj -c Release -r win-x64 --self-contained true -o outputs/OpenWrtDesk-Windows-x64
```

可通过环境变量 `OPENWRTDESK_DATA_DIR` 指定开发或测试用的数据目录。

默认自动检查仅连接本机模拟服务。`--seed` 需要显式指定路由器地址；`--diagnose` 和 `--program-logs` 使用指定数据目录中的设备档案。`--live` 会实际修改路由器设置并尝试恢复，仅应在自行备份的测试设备上手动运行。

公开仓库仅包含源码和通用文档，排除设备档案（包括加密凭据）、日志、订阅、备份、验证报告和构建产物。详细说明见 [PRIVACY.md](PRIVACY.md)。

接口参考：[OpenWrt LuCI](https://github.com/openwrt/luci)、[rpcd](https://github.com/openwrt/rpcd)、[OpenClash](https://github.com/vernesong/OpenClash)、[Mihomo API](https://wiki.metacubex.one/api/)。交互参考：[ClashRemote](https://github.com/DonVitsch/ClashRemote)、[WinUI Gallery](https://github.com/microsoft/WinUI-Gallery)。
