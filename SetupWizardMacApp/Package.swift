// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "SetupWizard",
    platforms: [
        .macOS(.v12)
    ],
    products: [
        .executable(name: "SetupWizard", targets: ["SetupWizard"])
    ],
    dependencies: [],
    targets: [
        .executableTarget(
            name: "SetupWizard",
            dependencies: []
        ),
        .testTarget(
            name: "SetupWizardTests",
            dependencies: ["SetupWizard"]
        )
    ]
)
