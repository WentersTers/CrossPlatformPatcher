import Foundation

/// Centralized logging utility for setup wizard
class Logger {
    static let shared = Logger()
    
    private let fileHandle: FileHandle?
    private let queue = DispatchQueue(label: "com.setupwizard.logging", attributes: .concurrent)
    
    private init() {
        let logDir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("CrossPlatformPatcher")
        try? FileManager.default.createDirectory(at: logDir, withIntermediateDirectories: true)
        
        let logPath = logDir.appendingPathComponent("setup-wizard.log").path
        if !FileManager.default.fileExists(atPath: logPath) {
            FileManager.default.createFile(atPath: logPath, contents: nil)
        }
        self.fileHandle = FileHandle(forWritingAtPath: logPath)
        self.fileHandle?.seekToEndOfFile()
    }
    
    func log(_ message: String, level: String = "INFO") {
        let timestamp = ISO8601DateFormatter().string(from: Date())
        let formatted = "[\(timestamp)] [\(level)] \(message)\n"
        
        queue.async(flags: .barrier) { [weak self] in
            if let data = formatted.data(using: .utf8) {
                self?.fileHandle?.write(data)
            }
            print(formatted, terminator: "")
        }
    }
    
    func debug(_ message: String) {
        log(message, level: "DEBUG")
    }
    
    func error(_ message: String) {
        log(message, level: "ERROR")
    }
    
    func getLogPath() -> String {
        let logDir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("CrossPlatformPatcher")
        return logDir.appendingPathComponent("setup-wizard.log").path
    }
}
