import Foundation
import SwiftUI

/// Main app state machine and observable state
class SetupWizardViewModel: NSObject, ObservableObject {
    enum Screen {
        case welcome
        case folderPicker
        case modelSelection
        case download
        case dependencyCheck
        case dotNetInstall
        case complete
        case error(String)
    }
    
    // MARK: - Observable Properties
    @Published var currentScreen: Screen = .welcome
    @Published var selectedFolder: String?
    @Published var logOutput: [String] = []
    @Published var isProcessing: Bool = false
    @Published var progress: Double = 0.0
    @Published var errorMessage: String?
    @Published var modelPath: String?
    
    // MARK: - Services
    let stateManager: SetupStateManager
    let apiClient: APIClient
    let dependencyChecker: DependencyChecker
    let dotNetInstaller: DotNetInstaller
    let modelDownloader: VoskModelDownloader
    
    private var processRunner: ProcessRunner?
    
    init(
        stateManager: SetupStateManager = SetupStateManager(),
        apiClient: APIClient = APIClient(),
        dependencyChecker: DependencyChecker = DependencyChecker(),
        dotNetInstaller: DotNetInstaller? = nil
    ) async {
        self.stateManager = stateManager
        self.apiClient = apiClient
        self.dependencyChecker = dependencyChecker
        self.dotNetInstaller = dotNetInstaller ?? DotNetInstaller(stateManager: stateManager)
        self.modelDownloader = VoskModelDownloader()
        
        // Clean up any stale processes
        await stateManager.cleanupStaleProcesses()
    }
    
    // MARK: - Navigation
    func goToFolderPicker() {
        currentScreen = .folderPicker
        Task {
            await stateManager.setCurrentStep(.folderPicker)
        }
    }
    
    func goToModelSelection() {
        currentScreen = .modelSelection
        Task {
            await stateManager.setCurrentStep(.modelSelection)
        }
    }
    
    func goToDownload() {
        currentScreen = .download
        Task {
            await stateManager.setCurrentStep(.download)
        }
    }
    
    func goToDependencyCheck() {
        currentScreen = .dependencyCheck
        Task {
            await stateManager.setCurrentStep(.dependencyCheck)
        }
    }
    
    func goToDotNetInstall() {
        currentScreen = .dotNetInstall
        Task {
            await stateManager.setCurrentStep(.dotNetInstall)
        }
    }
    
    func goToComplete() {
        currentScreen = .complete
        Task {
            await stateManager.markComplete()
        }
    }
    
    func showError(_ message: String) {
        errorMessage = message
        currentScreen = .error(message)
        Task {
            await stateManager.markFailed(error: message)
        }
    }
    
    // MARK: - Folder Selection
    func selectFolder(_ path: String) async {
        selectedFolder = path
        await stateManager.setSelectedFolder(path)
    }
    
    // MARK: - File Operations
    func selectFolderDialog() async -> String? {
        return await MainActor.run {
            let panel = NSOpenPanel()
            panel.canChooseDirectories = true
            panel.canChooseFiles = false
            panel.allowsMultipleSelection = false
            panel.prompt = "Select PAIcom Folder"
            
            let result = panel.runModal()
            if result == .OK, let url = panel.url {
                return url.path
            }
            return nil
        }
    }
    
    // MARK: - Download & Patch
    func downloadAndPatch() async {
        isProcessing = true
        logOutput = []
        progress = 0.0
        
        defer { isProcessing = false }
        
        do {
            guard let selectedFolder = selectedFolder else {
                showError("No folder selected")
                return
            }
            
            let paicomExe = (selectedFolder as NSString).appendingPathComponent("PAIcom.exe")
            guard FileManager.default.fileExists(atPath: paicomExe) else {
                showError("PAIcom.exe not found in selected folder")
                return
            }
            
            addLog("Fetching latest patcher release...")
            progress = 0.1
            
            let (downloadURL, assetName, totalSize) = try await apiClient.getPatcherAssetURL()
            addLog("Found release asset: \(assetName) (\(totalSize / 1024 / 1024) MB)")
            
            let tempDir = NSTemporaryDirectory()
            let tempPatcher = (tempDir as NSString).appendingPathComponent(assetName)
            
            addLog("Downloading patcher...")
            try await apiClient.download(from: downloadURL, to: tempPatcher) { [weak self] prog in
                DispatchQueue.main.async {
                    self?.progress = 0.1 + (prog * 0.3)
                }
            }
            
            progress = 0.4
            addLog("Patcher downloaded, making executable...")
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: tempPatcher)
            
            addLog("Running patcher on PAIcom.exe...")
            progress = 0.5
            
            let runner = ProcessRunner(executable: tempPatcher, arguments: [
                paicomExe,
                "--out", paicomExe,
                "--migration-mode", "full"
            ])
            
            await runner.setOutputHandler { [weak self] line in
                self?.addLog(line)
            }
            
            _ = try await runner.run()
            
            progress = 1.0
            addLog("Patching completed successfully!")
            
            await stateManager.setPatchedExePath(paicomExe)
            
            // Cleanup temp file
            try? FileManager.default.removeItem(atPath: tempPatcher)
            
        } catch {
            showError("Download/patch failed: \(error.localizedDescription)")
        }
    }
    
    // MARK: - Dependency Check
    func checkDependencies() async -> (hasWine: Bool, wineType: String?) {
        let deps = await dependencyChecker.checkAll()
        let _ = await dependencyChecker.recommendedWineSetup()
        
        addLog("Checking dependencies...")
        
        switch deps.whisky {
        case .installed(let path):
            addLog("✓ Found Whisky at: \(path)")
            return (true, "whisky")
        default:
            addLog("✗ Whisky not found")
        }
        
        switch deps.wine {
        case .installed(let path):
            addLog("✓ Found Wine at: \(path)")
            return (true, "system_wine")
        default:
            addLog("✗ Wine not found")
        }
        
        addLog("No Wine/Whisky installation detected")
        return (false, nil)
    }
    
    // MARK: - Vosk Model Download
    func downloadModel(_ model: VoskModelDownloader.VoskModel) async {
        isProcessing = true
        logOutput = []
        progress = 0.0
        
        defer { isProcessing = false }
        
        do {
            guard let selectedFolder = selectedFolder else {
                showError("No folder selected")
                return
            }
            
            let modelDir = (selectedFolder as NSString).appendingPathComponent("models")
            addLog("=== Starting Model Download ===")
            addLog("Model: \(model.rawValue)")
            addLog("Display Name: \(model.displayName)")
            addLog("Destination: \(modelDir)")
            
            try await modelDownloader.downloadModel(model, to: modelDir) { [weak self] progress in
                DispatchQueue.main.async {
                    self?.progress = progress
                    if progress > 0 && progress < 1.0 {
                        self?.addLog("Download progress: \(Int(progress * 100))%")
                    }
                }
            }
            
            addLog("✓ Model downloaded successfully!")
            modelPath = (modelDir as NSString).appendingPathComponent(model.rawValue)
            addLog("Model path: \(modelPath ?? "unknown")")
            
            // Verify the model folder exists
            if let modelPath = modelPath, FileManager.default.fileExists(atPath: modelPath) {
                do {
                    let contents = try FileManager.default.contentsOfDirectory(atPath: modelPath)
                    addLog("✓ Model folder verified with \(contents.count) files/folders")
                    for item in contents {
                        addLog("  - \(item)")
                    }
                } catch {
                    addLog("⚠ Warning: Could not list model contents: \(error)")
                }
            } else {
                addLog("✗ ERROR: Model path does not exist at: \(modelPath ?? "nil")")
            }
            
        } catch {
            addLog("✗ ERROR: Model download failed!")
            addLog("Error: \(error.localizedDescription)")
            showError("Model download failed: \(error.localizedDescription)")
        }
    }
    
    func downloadCustomModel(path: String, name: String) async {
        isProcessing = true
        logOutput = []
        progress = 0.0
        
        defer { isProcessing = false }
        
        do {
            guard let selectedFolder = selectedFolder else {
                showError("No folder selected")
                return
            }
            
            let modelDir = (selectedFolder as NSString).appendingPathComponent("models")
            let modelName = name.isEmpty ? URL(fileURLWithPath: path).deletingPathExtension().lastPathComponent : name
            
            addLog("=== Starting Custom Model Setup ===")
            addLog("Model Name: \(modelName)")
            addLog("Source: \(path)")
            addLog("Destination: \(modelDir)")
            
            // Check if it's a URL or local path
            if path.hasPrefix("http://") || path.hasPrefix("https://") {
                addLog("Detected URL source, downloading...")
                try await modelDownloader.downloadCustomModel(
                    from: path,
                    modelName: modelName,
                    to: modelDir
                ) { [weak self] progress in
                    DispatchQueue.main.async {
                        self?.progress = progress
                        if progress > 0 && progress < 1.0 {
                            self?.addLog("Download progress: \(Int(progress * 100))%")
                        }
                    }
                }
            } else if FileManager.default.fileExists(atPath: path) {
                // Local file - extract directly
                addLog("Detected local file, extracting...")
                progress = 0.5
                
                let process = Process()
                process.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
                
                let extractDir = (modelDir as NSString).appendingPathComponent(modelName)
                try FileManager.default.createDirectory(
                    atPath: extractDir,
                    withIntermediateDirectories: true,
                    attributes: nil
                )
                
                addLog("Extract directory created: \(extractDir)")
                addLog("Running unzip: /usr/bin/unzip -q \"\(path)\" -d \"\(extractDir)\"")
                
                process.arguments = ["-q", path, "-d", extractDir]
                
                let errorPipe = Pipe()
                process.standardError = errorPipe
                
                try process.run()
                process.waitUntilExit()
                
                addLog("Unzip completed with status: \(process.terminationStatus)")
                
                if process.terminationStatus != 0 {
                    let errorData = errorPipe.fileHandleForReading.readDataToEndOfFile()
                    let errorMessage = String(data: errorData, encoding: .utf8) ?? "Unknown error"
                    addLog("✗ Unzip error: \(errorMessage)")
                    throw NSError(domain: "unzip", code: Int(process.terminationStatus), userInfo: [NSLocalizedDescriptionKey: errorMessage])
                }
                
                progress = 1.0
            } else {
                throw NSError(domain: "FileNotFound", code: -1, userInfo: [NSLocalizedDescriptionKey: "File not found: \(path)"])
            }
            
            addLog("✓ Custom model setup completed!")
            modelPath = (modelDir as NSString).appendingPathComponent(modelName)
            addLog("Model path: \(modelPath ?? "unknown")")
            
            // Verify the model folder exists
            if let modelPath = modelPath, FileManager.default.fileExists(atPath: modelPath) {
                do {
                    let contents = try FileManager.default.contentsOfDirectory(atPath: modelPath)
                    addLog("✓ Model folder verified with \(contents.count) files/folders")
                } catch {
                    addLog("⚠ Warning: Could not list model contents: \(error)")
                }
            } else {
                addLog("✗ ERROR: Model path does not exist at: \(modelPath ?? "nil")")
            }
            
        } catch {
            addLog("✗ ERROR: Custom model setup failed!")
            addLog("Error: \(error.localizedDescription)")
            showError("Custom model setup failed: \(error.localizedDescription)")
        }
    }
    
    // MARK: - Logging
    private func addLog(_ message: String) {
        DispatchQueue.main.async {
            self.logOutput.append(message)
            Logger.shared.debug(message)
        }
    }
    
    // MARK: - Cancellation
    func cancel() async {
        if let runner = processRunner {
            await runner.cancel()
        }
    }
}
