// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "SnipStudioMac",
    platforms: [.macOS(.v14)],
    products: [.executable(name: "SnipStudioMac", targets: ["SnipStudioMac"])],
    targets: [.executableTarget(name: "SnipStudioMac")]
)
