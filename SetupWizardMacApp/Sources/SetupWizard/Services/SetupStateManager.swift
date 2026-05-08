import Foundation

/// Manages persistent setup state across wizard sessions
actor SetupStateManager {
    private var state: SetupStateFile
    private let stateFilePath: String
    
    init() {
        let home = FileManager.default.homeDirectoryForCurrentUser
        let wineDir = home.appendingPathComponent(".wine")
        let stateFilePath = wineDir.appendingPathComponent("setup-state.json").path
        
        // Create wine directory if needed
        try? FileManager.default.createDirectory(at: wineDir, withIntermediateDirectories: true)
        
        self.stateFilePath = stateFilePath
        
        // Load existing state or create new
        if let loaded = SetupStateFile.load(from: stateFilePath) {
            self.state = loaded
            Logger.shared.debug("Loaded existing setup state")
        } else {
            self.state = SetupStateFile()
            Logger.shared.debug("Created new setup state")
        }
    }
    
    func getCurrentStep() -> SetupStateFile.Step {
        return state.currentStep
    }
    
    func setCurrentStep(_ step: SetupStateFile.Step) async {
        state.currentStep = step
        try? state.save(to: stateFilePath)
    }
    
    func setSelectedFolder(_ folder: String) async {
        state.selectedFolder = folder
        try? state.save(to: stateFilePath)
    }
    
    func getSelectedFolder() -> String? {
        return state.selectedFolder
    }
    
    func setPatchedExePath(_ path: String) async {
        state.patchedExePath = path
        try? state.save(to: stateFilePath)
    }
    
    func getPatchedExePath() -> String? {
        return state.patchedExePath
    }
    
    func setSelectedWineType(_ type: String) async {
        state.selectedWineType = type
        try? state.save(to: stateFilePath)
    }
    
    func getSelectedWineType() -> String? {
        return state.selectedWineType
    }
    
    func setDotNetStatus(_ status: SetupStateFile.OperationStatus, error: String? = nil) async {
        state.dotNetStatus = status
        state.dotNetError = error
        try? state.save(to: stateFilePath)
    }
    
    func getDotNetStatus() -> (status: SetupStateFile.OperationStatus, error: String?) {
        return (state.dotNetStatus, state.dotNetError)
    }
    
    func setCurrentOperationPID(_ pid: Int32) async {
        state.currentOperationPID = pid
        state.operationStartTime = Date()
        try? state.save(to: stateFilePath)
    }
    
    func clearCurrentOperationPID() async {
        state.currentOperationPID = nil
        state.operationStartTime = nil
        try? state.save(to: stateFilePath)
    }
    
    func recordCancellation() async {
        state.lastCancellationTime = Date()
        try? state.save(to: stateFilePath)
    }
    
    func getLastCancellationTime() -> Date? {
        return state.lastCancellationTime
    }
    
    func markComplete() async {
        state.wizardStatus = .completed
        state.completionTime = Date()
        try? state.save(to: stateFilePath)
    }
    
    func markFailed(error: String) async {
        state.wizardStatus = .failed
        state.lastError = error
        try? state.save(to: stateFilePath)
    }
    
    func getWizardStatus() -> (status: SetupStateFile.OperationStatus, error: String?) {
        return (state.wizardStatus, state.lastError)
    }
    
    func reset() async {
        state = SetupStateFile()
        try? FileManager.default.removeItem(atPath: stateFilePath)
        Logger.shared.debug("Reset setup state")
    }
    
    func getFullState() -> SetupStateFile {
        return state
    }
    
    func cleanupStaleProcesses() async {
        if let pid = state.currentOperationPID, state.dotNetStatus == .inProgress {
            // Check if process still exists
            let killResult = kill(pid, 0) // Signal 0 just checks if process exists
            if killResult == -1 {
                Logger.shared.log("Process \(pid) no longer exists, cleaning up")
                state.currentOperationPID = nil
                state.dotNetStatus = .failed
                state.dotNetError = "Process was terminated unexpectedly"
                try? state.save(to: stateFilePath)
            }
        }
    }
}
