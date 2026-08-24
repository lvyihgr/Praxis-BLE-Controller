# Praxis Bluetooth Windows

该目录是 Praxis Bluetooth 1.2.0 的完整 Windows 产品源码。

它同时包含动作执行、配置、状态、PowerPoint 自动化、消息路由、通知区宿主和 BLE Central；除内部传输组件 `Praxis.Link.Bluetooth` 外，不依赖网络版项目。

进程不启动 HTTP 或 WebSocket 服务，也不提供局域网回退。发布后运行 `Praxis.Bluetooth.exe`，通过通知区图标查看连接状态或退出服务。

无窗口运行日志保存在发布目录的 `data/logs/runtime.log`。文件达到 5 MB 后会轮换为 `runtime.previous.log`，日志只记录状态、消息类型与错误，不记录配对码或消息正文。

首次连接时，通知区菜单会显示六位临时配对码。配对完成后，设备密钥由 Windows 用户级数据保护保存；后续消息使用会话密钥、序列号和 HMAC 验证。更新配对码或忘记设备会主动重连，避免 PC 与 iPad 保留不一致的安全状态。

第三方应用图标不随源码分发。Core 会从用户本机已安装应用提取并缩放图标，并按来源文件时间、大小或应用包版本把结果缓存到 `data/icon-cache`。iPad 在安全会话建立后先上报本地配置与图标哈希；Core 只传输缺失或已变化的图标，普通重连不会重复提取或发送全部资源。

安全会话建立与面板可操作是两个独立阶段。完整缓存命中时，Core 会优先发送 `bootstrap.completed` 开放操作，再补发普通状态快照；初始化期间的 `control.invoke` 会被明确拒绝，不会排队到稍后执行。首次缺失图标使用按分帧数量自适应节流的快速 GATT 写入，每个完整图标仍须经 SHA-256 校验并由 iPad 回执；未收到回执时自动退回可靠写入重试。BLE UUID、角色和 Praxis Link 帧格式保持不变。

```powershell
.\scripts\run.ps1
.\scripts\test.ps1
.\scripts\publish.ps1
```

蓝牙 UUID、方向和帧约定见 `docs/bluetooth-central-validation.md`。
