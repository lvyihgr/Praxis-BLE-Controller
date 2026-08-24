// swift-tools-version:5.7
import PackageDescription

let package = Package(
    name: "PraxisFrameCodec",
    platforms: [.macOS(.v10_15)],
    products: [
        .library(name: "PraxisFrameCodec", targets: ["PraxisFrameCodec"])
    ],
    targets: [
        .target(
            name: "PraxisFrameCodec",
            path: "PraxisPanelIOS/Protocol",
            exclude: [
                "JSONValue.swift",
                "PraxisProtocolMessage.swift",
                "PraxisProtocolSession.swift"
            ],
            sources: [
                "PraxisFrameError.swift",
                "PraxisFrameEncoder.swift",
                "PraxisFrameReassembler.swift"
            ]
        ),
        .testTarget(
            name: "PraxisFrameCodecTests",
            dependencies: ["PraxisFrameCodec"],
            path: "PraxisPanelIOSTests",
            sources: ["PraxisFrameCodecTests.swift"]
        )
    ]
)
