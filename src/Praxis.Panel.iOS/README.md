# Praxis Panel iOS

Praxis Panel iOS 是面向 iPad mini 2（iOS 12.5.8、横屏 1024 × 768 pt）的原生 UIKit 控制面板。它只使用 Core Bluetooth，不包含 WebView、HTTP、WebSocket、Bonjour、局域网发现或网络回退。

## 当前实现

- UIKit + Auto Layout，iPad-only，Deployment Target 为 iOS 12.0。
- `CBPeripheralManager` Peripheral / GATT Server，广播名称为 `Praxis Panel`。
- 固定的 Praxis Service、Command to Core、Event from Core 和 Peer Info UUID。
- Command to Core 使用 Notify；Event from Core 支持 Write 与 Write Without Response；Peer Info 返回持久设备 ID、版本和设备名称的 JSON。
- 12 字节、小端序 Praxis Link 帧头，支持编码、分片、单消息重组、65,536 字节上限及异常后的恢复。
- 根据 `CBCentral.maximumUpdateValueLength` 发送通知；`updateValue` 返回 `false` 时保留帧，并从 `peripheralManagerIsReady(toUpdateSubscribers:)` 继续。
- 处理断订阅、蓝牙关闭/恢复、服务重建和自动重新广播。
- 首次连接使用六位配对码；配对密钥存入 iOS Keychain，后续自动恢复经过认证的会话。
- 六位码的 50,000 轮 PBKDF2-SHA256 由 iOS CommonCrypto 执行，兼顾安全强度与 iPad mini 2 的配对响应速度；完整配对尝试有 15 秒超时。
- 配对后的消息使用会话 ID、单调序列和 HMAC，拒绝篡改与重放。
- 支持 `core.hello`、配置/状态快照、状态变化、动作生命周期、初始化协商、动态图标、`pong` 和 `protocol.error`。
- 顶部连接测试按钮发送 `ping`；初始化阶段不自动产生额外测试消息。
- 配对完成后先发送 `bootstrap.prepare`，上报持久化配置哈希和图标哈希。Core 只返回变更内容，iPad 对动态图标执行 SHA-256 校验并发送 `app.icon.received`。
- 所有控制与演示按钮发送真实 `control.invoke`，Action ID 来自 Core 配置；收到 `bootstrap.completed` 前按钮保持禁用，误触不会发送或延后执行。
- 控制页、演示页、固定顶部状态区、紧凑双入口导航、通知栈和系统摘要浮层均为原生 UIKit。
- `presentation.state` 驱动最近文件、备注、文稿状态、页码和已用时间，不使用定时假状态。
- `bluetooth-peripheral` Background Mode 已配置；锁屏与后台兼容性仍需作为独立真机阶段验证。

## 工程结构

```text
src/Praxis.Panel.iOS/
├── PraxisPanelIOS.xcodeproj
├── PraxisPanelIOS/
│   ├── Application/
│   ├── Bluetooth/
│   ├── Protocol/
│   ├── State/
│   ├── UI/
│   └── Resources/
├── PraxisPanelIOSTests/
└── Package.swift
```

UI 不直接访问 `CBPeripheralManager`。数据流为：

```text
Core Bluetooth Peripheral → PraxisProtocolSession → Configuration/State Store → UIKit
UIKit action → PraxisProtocolSession → Frame Encoder → Core Bluetooth Notify → Windows Core
```

## Xcode 与 iOS 12

Xcode 15.4 的 iPhoneOS 17.5 SDK 能编译 `arm64-apple-ios12.0`，工程不会因此提高最低系统版本。当前开发机已完成 Xcode 首次启动组件和 iOS 17.5 Simulator 安装。Xcode 15 不提供 iOS 12 模拟器运行时，所以 iOS 12 行为仍必须在目标 iPad mini 2 上验证。

安装组件后可执行无签名构建检查：

```bash
xcodebuild \
  -project src/Praxis.Panel.iOS/PraxisPanelIOS.xcodeproj \
  -scheme PraxisPanelIOS \
  -destination 'generic/platform=iOS' \
  -derivedDataPath /tmp/PraxisPanelDerivedData \
  CODE_SIGNING_ALLOWED=NO \
  build
```

有可用 iOS 模拟器运行时后，可编译并运行 XCTest：

```bash
xcodebuild \
  -project src/Praxis.Panel.iOS/PraxisPanelIOS.xcodeproj \
  -scheme PraxisPanelIOS \
  -destination 'platform=iOS Simulator,name=iPad (9th generation)' \
  -derivedDataPath /tmp/PraxisPanelDerivedData \
  test
```

纯帧协议也提供 Swift Package XCTest：

```bash
CLANG_MODULE_CACHE_PATH=/tmp/PraxisClangCache \
swift test --disable-sandbox \
  --package-path src/Praxis.Panel.iOS \
  --scratch-path /tmp/PraxisFrameTests
```

## 签名与 iPad mini 2 真机运行

1. 用 Xcode 打开 `PraxisPanelIOS.xcodeproj`。
2. 选择 `PraxisPanelIOS` Target → Signing & Capabilities。
3. 选择你的 Apple Developer Team，并按需要把 Bundle Identifier 改成该 Team 唯一的值。
4. 保留 Deployment Target `12.0`，不要改成 13 或更高。
5. 确认 Background Modes 中只有 **Uses Bluetooth LE accessories / Acts as a Bluetooth LE accessory** 对应的 `bluetooth-peripheral`。
6. 连接并信任 iPad mini 2，选择真机运行。首次安装后按系统提示允许蓝牙。
7. 横屏检查控制页、演示页以及所有至少 44 × 44 pt 的触控区域。

不要提交签名证书、Provisioning Profile、`.xcarchive`、`xcuserdata` 或 DerivedData；仓库根目录 `.gitignore` 已覆盖这些内容。

## 与 Windows Praxis Bluetooth 联调

Windows PC 保持 Central / GATT Client，iPad 保持 Peripheral / GATT Server。不要同时运行会占用同一工作流的其他虚拟 Peripheral。

1. 在 Windows 的当前 `main` 基线上，以正常桌面进程运行：

   ```powershell
   .\.tools\dotnet\dotnet.exe run --project .\src\Praxis.Core.Bluetooth
   ```

2. 在 iPad 前台启动 Praxis Panel，保持屏幕点亮。
3. Core 应发现 `Praxis Panel`、连接、发现 GATT 服务并订阅 `f001`。
4. 首次连接时，在 iPad 输入 Windows 通知区显示的六位配对码；之后应自动恢复安全会话。
5. 安全会话建立后，iPad 应先保持按钮禁用并发送 `bootstrap.prepare`；随后收到 `core.hello`、配置确认或快照、必要状态、缺失图标以及 `bootstrap.completed`。
6. 第二次连接应命中配置和图标缓存，不再传输全部 8 个图标。顶部连接测试应收到 `pong`，右上角显示“连接测试通过”。
7. 点击一个明确且安全的真实控制（例如“计算器”），确认发送 `control.invoke`，依次收到 `action.accepted` 和 `action.completed`/`action.failed`。
8. 切换到“演示”，确认 `presentation.state` 自动更新最近文件或当前页备注。
9. 分别验证 Core 重启、取消订阅、iPad 蓝牙关闭再打开、忘记设备以及前台重新连接。
10. 前台稳定后，再独立验证锁屏和后台行为，并记录 Windows build、蓝牙适配器/驱动、iPad 型号和 iOS 版本。

正式消息始终经过 Praxis Link 分帧。LightBlue 中的裸 JSON 只适用于仓库文档所述的兼容性探针，不适用于本 App 的正式会话。

## 图标

仓库不分发 ChatGPT、ZCode、TRAE、Edge、飞书、VS Code 或 PowerPoint 等第三方应用图标。Windows Core 会从用户自己的已安装应用提取 96 × 96 PNG，经已认证的蓝牙会话按需发送；iPad 在 Application Support 中保存图标及哈希并排除云备份。正常重连只交换缓存摘要，只有缺失或变化的图标才重新传输。图标尚未到达时显示中性的字母占位，初始化完成前所有动作按钮保持禁用。

iOS 12 没有 `UIImage(systemName:)`。系统、快捷指令与演示功能图标使用随 App 打包的 Lucide 矢量 PDF，并以模板图片方式着色；它们不依赖蓝牙连接，也不包含从 Apple 字体导出的图片。Lucide/Feather 的许可证保存在仓库 `licenses/LUCIDE-LICENSE.txt`，Praxis AppIcon 和顶部标志仍使用项目自有的曜石蓝图标。

## 配置乱码交接

历史版本的 `praxis.json` 曾出现中文乱码。当前 iPad UI 对已知 Control ID 使用明确的本地中文名称映射，但 Action ID、状态绑定和能力仍完全来自 Core 的真实配置。未知 Control 不会猜测名称或动作；应在 Core 配置源修复后再展示。

## 当前验证状态

已在本机完成：

- 指定 PC 协议、蓝牙、状态、演示、配置和 Web 交互文件完整阅读。
- iPhoneOS SDK 下 `arm64-apple-ios12.0` 全部 Swift 源码类型检查（1.2.0 安全配对改造需在 Mac CI 再确认）。
- 帧编码、分片、乱序、超长与异常恢复的可执行自检通过。
- Swift Package XCTest：10 项测试全部通过。
- Xcode 工程在 iPad mini Simulator 上的 XCTest：历史基线 10 项测试全部通过。
- `xcodebuild` iPhoneOS 无签名构建历史基线成功；1.2.0 由 GitHub Actions 的 macOS 构建再次验证。
- Xcode 工程、Info.plist、Storyboard 和共享 Scheme 语法检查通过。

已在目标环境完成：

- Apple Development 签名与 iPad mini 2 真机安装。
- Windows PC ↔ iPad 的真实 BLE 连接、`ping`/`pong` 和 `control.invoke` 动作结果联调。
- 控制页、PowerPoint 打开/放映/翻页/结束放映和状态回传的多轮真机调试。
- iPad mini 2 横屏布局、应用图标和主要交互检查。

仍需单独完成：

- 锁屏、长时间后台、系统回收后的重新广播与恢复验收。
- Windows 安装包和非开发机环境验证。
