import Foundation

/// Runs shell commands with real-time output streaming, cancellation support, and state tracking
actor ProcessRunner {
    enum RunnerError: LocalizedError {
        case processTerminated(exitCode: Int32)
        case cancelled
        case failedToStart
        case invalidExecutable
        
        var errorDescription: String? {
            switch self {
            case .processTerminated(let code):
                return "Process exited with code \(code)"
            case .cancelled:
                return "Process was cancelled by user"
            case .failedToStart:
                return "Failed to start process"
            case .invalidExecutable:
                return "Invalid or missing executable"
            }
        }
    }
    
    typealias OutputHandler = (String) -> Void
    
    private let executable: String
    private let arguments: [String]
    private var process: Process?
    private var outputLines: [String] = []
    private var isCancelled: Bool = false
    private var isRunning: Bool = false
    
    private var _onOutput: OutputHandler?
    
    func setOutputHandler(_ handler: @escaping OutputHandler) {
        _onOutput = handler
    }
    
    init(executable: String, arguments: [String] = []) {
        self.executable = executable
        self.arguments = arguments
    }
    
    /// Run the process with real-time output streaming
    func run(environment: [String: String] = [:]) async throws -> Int32 {
        let process = Process()
        self.process = process
        self.isCancelled = false
        self.isRunning = true
        self.outputLines = []
        
        // Verify executable exists
        guard FileManager.default.fileExists(atPath: executable) else {
            Logger.shared.error("Executable not found: \(executable)")
            self.isRunning = false
            throw RunnerError.invalidExecutable
        }
        
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        
        // Set up environment
        var env = ProcessInfo.processInfo.environment
        env.merge(environment) { _, new in new }
        process.environment = env
        
        // Set up pipes for output capture
        let stdoutPipe = Pipe()
        let stderrPipe = Pipe()
        process.standardOutput = stdoutPipe
        process.standardError = stderrPipe
        
        // Start streaming output (detached from actor)
        let outputQueue = DispatchQueue(label: "com.setupwizard.process-output")
        outputQueue.async { [weak self] in
            guard let self = self else { return }
            Task.detached { [weak self] in
                await self?.captureOutputAsync(from: stdoutPipe)
            }
        }
        outputQueue.async { [weak self] in
            guard let self = self else { return }
            Task.detached { [weak self] in
                await self?.captureOutputAsync(from: stderrPipe)
            }
        }
        
        // Start process
        do {
            try process.run()
            Logger.shared.debug("Started process: \(executable) with args: \(arguments)")
        } catch {
            Logger.shared.error("Failed to start process: \(error)")
            self.isRunning = false
            throw RunnerError.failedToStart
        }
        
        // Wait for completion
        process.waitUntilExit()
        
        // Give output capture threads a moment to process final data
        try? await Task.sleep(nanoseconds: 100_000_000) // 100ms
        
        if isCancelled {
            Logger.shared.log("Process cancelled")
            self.isRunning = false
            throw RunnerError.cancelled
        }
        
        let exitCode = process.terminationStatus
        self.isRunning = false
        
        if exitCode != 0 {
            Logger.shared.error("Process exited with code: \(exitCode)")
            throw RunnerError.processTerminated(exitCode: exitCode)
        }
        
        return exitCode
    }
    
    /// Cancel the running process gracefully
    func cancel() async {
        isCancelled = true
        
        guard let process = self.process, process.isRunning else {
            Logger.shared.log("No process to cancel")
            return
        }
        
        Logger.shared.log("Cancelling process (PID: \(process.processIdentifier))")
        
        // Send SIGTERM first (graceful)
        kill(process.processIdentifier, SIGTERM)
        
        // Wait up to 10 seconds for graceful termination
        let deadline = Date().addingTimeInterval(10)
        while process.isRunning && Date() < deadline {
            usleep(100_000) // 100ms
        }
        
        // If still running, send SIGKILL (forceful)
        if process.isRunning {
            Logger.shared.log("Force-killing process (PID: \(process.processIdentifier))")
            kill(process.processIdentifier, SIGKILL)
        }
    }
    
    private nonisolated func captureOutput(from pipe: Pipe) {
        let fileHandle = pipe.fileHandleForReading
        
        // Use readDataToEndOfFile() to block until EOF, not availableData which returns immediately
        let data = fileHandle.readDataToEndOfFile()
        
        guard !data.isEmpty, let string = String(data: data, encoding: .utf8) else {
            return
        }
        
        let lines = string.split(separator: "\n", omittingEmptySubsequences: false)
        for line in lines where !line.isEmpty {
            Task { await self.addOutputLine(String(line)) }
        }
    }
    
    private func captureOutputAsync(from pipe: Pipe) async {
        captureOutput(from: pipe)
    }
    
    private func addOutputLine(_ line: String) {
        outputLines.append(line)
        _onOutput?(line)
    }
    
    func getOutputLines() -> [String] {
        return outputLines
    }
    
    func isStillRunning() -> Bool {
        return isRunning && process?.isRunning ?? false
    }
}
