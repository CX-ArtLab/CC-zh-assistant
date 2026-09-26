// swift-tools-version: 5.7
import PackageDescription

let package = Package(
    name: "CCZhAssistantMac",
    platforms: [.macOS(.v12)],
    products: [
        .executable(name: "CCZhAssistantMac", targets: ["CCZhAssistantMac"])
    ],
    targets: [
        .executableTarget(name: "CCZhAssistantMac")
    ]
)
