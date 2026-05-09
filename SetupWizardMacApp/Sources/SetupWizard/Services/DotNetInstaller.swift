import Foundation

/// Orchestrates .NET installation in Wine via bundled winetricks with fallbacks
actor DotNetInstaller {
    enum InstallError: LocalizedError {
        case winetricksNotFound
        case wineNotConfigured
        case installFailed(String)
        case cancelled
        
        var errorDescription: String? {
            switch self {
            case .winetricksNotFound:
                return "Winetricks not available (bundled or system)"
            case .wineNotConfigured:
                return "Wine prefix not configured"
            case .installFailed(let msg):
                return "Installation failed: \(msg)"
            case .cancelled:
                return "Installation cancelled by user"
            }
        }
    }
    
    private let stateManager: SetupStateManager
    private var currentRunner: ProcessRunner?
    
    init(stateManager: SetupStateManager) {
        self.stateManager = stateManager
    }
    
    /// Attempt to install .NET 4.8 using available strategies
    func installDotNet(winePrefix: String, onOutput: @escaping (String) -> Void) async throws {
        await stateManager.setDotNetStatus(.inProgress)
        Logger.shared.log("Starting .NET installation in prefix: \(winePrefix)")
        
        // Strategy 1: Try bundled winetricks
        do {
            try await installViaBundle(winePrefix: winePrefix, onOutput: onOutput)
            await stateManager.setDotNetStatus(.completed)
            Logger.shared.log(".NET installation completed successfully")
            return
        } catch {
            Logger.shared.error("Bundled winetricks failed: \(error)")
        }
        
        // Strategy 2: Try system winetricks
        do {
            try await installViaSystemWinetricks(winePrefix: winePrefix, onOutput: onOutput)
            await stateManager.setDotNetStatus(.completed)
            Logger.shared.log(".NET installation completed successfully")
            return
        } catch {
            Logger.shared.error("System winetricks failed: \(error)")
        }
        
        // Strategy 3: Try native registry approach
        do {
            try await installViaRegistry(winePrefix: winePrefix, onOutput: onOutput)
            await stateManager.setDotNetStatus(.completed)
            Logger.shared.log(".NET installation completed successfully (via registry)")
            return
        } catch {
            Logger.shared.error("Registry approach failed: \(error)")
        }
        
        // All strategies failed
        let errorMsg = "All .NET installation methods failed"
        Logger.shared.error(errorMsg)
        await stateManager.setDotNetStatus(.failed, error: errorMsg)
        throw InstallError.installFailed(errorMsg)
    }
    
    /// Strategy 1: Use bundled winetricks from app bundle
    private func installViaBundle(winePrefix: String, onOutput: @escaping (String) -> Void) async throws {
        // Try to find bundled winetricks
        let bundledPath = "/Applications/CrossPlatformPatcher/SetupWizardApp.app/Contents/Resources/winetricks"
        guard FileManager.default.fileExists(atPath: bundledPath) else {
            throw InstallError.winetricksNotFound
        }
        
        Logger.shared.debug("Using bundled winetricks: \(bundledPath)")
        
        let runner = ProcessRunner(executable: "/bin/bash", arguments: [bundledPath, "dotnet48"])
        self.currentRunner = runner
        
        var environment = [String: String]()
        environment["WINEPREFIX"] = winePrefix
        environment["WINEARCH"] = "win64"
        
        // Set output handler
        await runner.setOutputHandler(onOutput)
        
        do {
            _ = try await runner.run(environment: environment)
            Logger.shared.debug("Bundled winetricks completed successfully")
        } catch ProcessRunner.RunnerError.cancelled {
            throw InstallError.cancelled
        } catch {
            throw InstallError.installFailed(error.localizedDescription)
        }
    }
    
    /// Strategy 2: Use system winetricks
    private func installViaSystemWinetricks(winePrefix: String, onOutput: @escaping (String) -> Void) async throws {
        let paths = [
            "/usr/local/bin/winetricks",
            "/usr/bin/winetricks",
            "/opt/homebrew/bin/winetricks"
        ]
        
        guard let winetricksPath = paths.first(where: { FileManager.default.fileExists(atPath: $0) }) else {
            throw InstallError.winetricksNotFound
        }
        
        Logger.shared.debug("Using system winetricks: \(winetricksPath)")
        
        let runner = ProcessRunner(executable: winetricksPath, arguments: ["dotnet48"])
        self.currentRunner = runner
        
        var environment = [String: String]()
        environment["WINEPREFIX"] = winePrefix
        environment["WINEARCH"] = "win64"
        
        // Set output handler
        await runner.setOutputHandler(onOutput)
        
        do {
            _ = try await runner.run(environment: environment)
            Logger.shared.debug("System winetricks completed successfully")
        } catch ProcessRunner.RunnerError.cancelled {
            throw InstallError.cancelled
        } catch {
            throw InstallError.installFailed(error.localizedDescription)
        }
    }
    
    /// Strategy 3: Fallback to native wine regedit
    private func installViaRegistry(winePrefix: String, onOutput: @escaping (String) -> Void) async throws {
        Logger.shared.log("Falling back to native registry approach")
        
        let runner = ProcessRunner(executable: "/bin/bash", arguments: [
            "-c",
            """
            # Check if .NET is already installed
            WINEPREFIX="\(winePrefix)" WINEARCH=win64 wine regedit /e /d "HKEY_LOCAL_MACHINE\\\\Software\\\\Microsoft\\\\.NETFramework" 2>/dev/null | grep -q "4.8" && exit 0
            # If not, try to set registry key
            echo "Setting .NET registry keys..." 
            WINEPREFIX="\(winePrefix)" WINEARCH=win64 wine regedit <<'EOF'
            REGEDIT4
            [HKEY_LOCAL_MACHINE\\Software\\Microsoft\\.NETFramework]
            "InstallRoot"="C:\\\\Windows\\\\Microsoft.NET\\\\Framework64\\\\"
            [HKEY_LOCAL_MACHINE\\Software\\Microsoft\\.NETFramework\\v4.0.30319]
            "Install"=dword:00000001
            EOF
            exit 0
            """
        ])
        
        self.currentRunner = runner
        
        // Set output handler
        await runner.setOutputHandler(onOutput)
        
        do {
            _ = try await runner.run()
            Logger.shared.debug("Registry approach completed")
        } catch ProcessRunner.RunnerError.cancelled {
            throw InstallError.cancelled
        } catch {
            throw InstallError.installFailed(error.localizedDescription)
        }
    }
    
    /// Cancel the current installation
    func cancel() async {
        Logger.shared.log("Cancelling .NET installation")
        if let runner = currentRunner {
            await runner.cancel()
        }
        await stateManager.recordCancellation()
    }
}
