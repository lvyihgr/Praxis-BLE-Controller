import Foundation

enum BluetoothConnectionState: Equatable {
    case stopped
    case starting
    case bluetoothUnavailable
    case advertising
    case connected(maximumUpdateValueLength: Int)
    case backpressured(maximumUpdateValueLength: Int)
    case error(String)

    var isConnected: Bool {
        switch self {
        case .connected, .backpressured: return true
        default: return false
        }
    }

    var title: String {
        switch self {
        case .stopped: return "已停止"
        case .starting: return "正在启动"
        case .bluetoothUnavailable: return "蓝牙不可用"
        case .advertising: return "等待 Praxis Bluetooth"
        case .connected: return "已连接"
        case .backpressured: return "正在发送"
        case .error: return "连接异常"
        }
    }
}

enum BluetoothPeripheralServiceError: Error, LocalizedError {
    case notConnected
    case invalidMessage

    var errorDescription: String? {
        switch self {
        case .notConnected: return "Praxis Bluetooth 尚未订阅蓝牙命令通道。"
        case .invalidMessage: return "无法发送空消息或超过 65,536 字节的消息。"
        }
    }
}
