import Foundation

/// Detects Wine, Whisky, and Homebrew installations
actor DependencyChecker {
    enum DependencyStatus {
        case installed(path: String)
        case notInstalled
        case partial  // e.g., Homebrew present but Whisky not installed
    }
    
    /// Check if Whisky is installed
    func checkWhisky() -> DependencyStatus {
        let whiskiPath = "/Applications/Whisky.app"
        if FileManager.default.fileExists(atPath: whiskiPath) {
            Logger.shared.debug("Found Whisky at: \(whiskiPath)")
            return .installed(path: whiskiPath)
        }
        return .notInstalled
    }
    
    /// Check if system Wine is installed (wine64 or wine)
    func checkSystemWine() -> DependencyStatus {
        let paths = ["/usr/local/bin/wine64", "/usr/bin/wine64", "/opt/homebrew/bin/wine64",
                     "/usr/local/bin/wine", "/usr/bin/wine", "/opt/homebrew/bin/wine"]
        
        for path in paths {
            if FileManager.default.fileExists(atPath: path) {
                Logger.shared.debug("Found system Wine at: \(path)")
                return .installed(path: path)
            }
        }
        return .notInstalled
    }
    
    /// Check if Homebrew is installed
    func checkHomebrew() -> DependencyStatus {
        let paths = ["/usr/local/bin/brew", "/opt/homebrew/bin/brew"]
        
        for path in paths {
            if FileManager.default.fileExists(atPath: path) {
                Logger.shared.debug("Found Homebrew at: \(path)")
                return .installed(path: path)
            }
        }
        return .notInstalled
    }
    
    /// Get current system architecture
    func getArchitecture() -> String {
        var systemInfo = utsname()
        uname(&systemInfo)
        let machine = withUnsafeBytes(of: systemInfo.machine) { ptr in
            String(cString: ptr.baseAddress!.assumingMemoryBound(to: CChar.self))
        }
        return machine
    }
    
    /// Check if running on Apple Silicon
    func isAppleSilicon() -> Bool {
        return getArchitecture() == "arm64"
    }
    
    /// Comprehensive dependency check
    func checkAll() -> (whisky: DependencyStatus, wine: DependencyStatus, homebrew: DependencyStatus) {
        return (
            whisky: checkWhisky(),
            wine: checkSystemWine(),
            homebrew: checkHomebrew()
        )
    }
    
    /// Determine recommended Wine setup
    func recommendedWineSetup() -> String {
        let (whisky, wine, _) = checkAll()
        
        switch (whisky, wine) {
        case (.installed, _):
            return "whisky"
        case (_, .installed):
            return "system_wine"
        default:
            return "homebrew_required"
        }
    }
}
