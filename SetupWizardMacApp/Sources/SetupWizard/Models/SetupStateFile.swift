import Foundation

/// Persistent state file format: ~/.wine/setup-state.json
/// Tracks progress across wizard sessions for resume capability
struct SetupStateFile: Codable {
    enum Step: String, Codable {
        case welcome
        case folderPicker
        case modelSelection
        case download
        case dependencyCheck
        case dotNetInstall
        case complete
    }
    
    enum OperationStatus: String, Codable {
        case notStarted = "not_started"
        case inProgress = "in_progress"
        case completed
        case failed
        case cancelled
    }
    
    /// Current step in wizard
    var currentStep: Step = .welcome
    
    /// Path to selected PAIcom folder
    var selectedFolder: String?
    
    /// Path to patched PAIcom executable
    var patchedExePath: String?
    
    /// Wine type chosen: "whisky", "system_wine", "homebrew"
    var selectedWineType: String?
    
    /// .NET installation status
    var dotNetStatus: OperationStatus = .notStarted
    
    /// Last .NET install error (if any)
    var dotNetError: String?
    
    /// Process ID of currently running operation (for cleanup)
    var currentOperationPID: Int32?
    
    /// When current operation started
    var operationStartTime: Date?
    
    /// When last cancellation occurred
    var lastCancellationTime: Date?
    
    /// Overall wizard completion status
    var wizardStatus: OperationStatus = .notStarted
    
    /// Completion timestamp
    var completionTime: Date?
    
    /// Any error that caused failure
    var lastError: String?
    
    static func load(from path: String) -> SetupStateFile? {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: path)) else {
            return nil
        }
        return try? JSONDecoder().decode(SetupStateFile.self, from: data)
    }
    
    func save(to path: String) throws {
        let data = try JSONEncoder().encode(self)
        try data.write(to: URL(fileURLWithPath: path), options: .atomic)
    }
    
    func stateDirectory() -> String {
        // Use ~/.wine directory or Whisky bottle
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        return (home as NSString).appendingPathComponent(".wine")
    }
    
    func stateFilePath() -> String {
        return (stateDirectory() as NSString).appendingPathComponent("setup-state.json")
    }
}
