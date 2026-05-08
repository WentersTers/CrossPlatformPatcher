import Foundation

/// Handles downloading and extracting Vosk models
actor VoskModelDownloader {
    enum VoskModel: String, CaseIterable, Identifiable {
        case smallEnUs = "vosk-model-small-en-us-0.15"
        case enUs022 = "vosk-model-en-us-0.22"
        case enUs022Lgraph = "vosk-model-en-us-0.22-lgraph"
        case enUs042Gigaspeech = "vosk-model-en-us-0.42-gigaspeech"
        
        var id: String { self.rawValue }
        var displayName: String {
            switch self {
            case .smallEnUs:
                return "vosk-model-small-en-us-0.15 (fast, lightweight)"
            case .enUs022:
                return "vosk-model-en-us-0.22 (balanced)"
            case .enUs022Lgraph:
                return "vosk-model-en-us-0.22-lgraph (large graph)"
            case .enUs042Gigaspeech:
                return "vosk-model-en-us-0.42-gigaspeech (large, accurate)"
            }
        }
        
        var downloadURL: URL? {
            // Models are hosted on Alphacephei CDN at /models/ path
            switch self {
            case .smallEnUs:
                return URL(string: "https://alphacephei.com/vosk/models/\(self.rawValue).zip")
            case .enUs022:
                return URL(string: "https://alphacephei.com/vosk/models/\(self.rawValue).zip")
            case .enUs022Lgraph:
                return URL(string: "https://alphacephei.com/vosk/models/\(self.rawValue).zip")
            case .enUs042Gigaspeech:
                return URL(string: "https://alphacephei.com/vosk/models/\(self.rawValue).zip")
            }
        }
    }
    
    enum DownloadError: LocalizedError {
        case invalidURL
        case networkError(Error)
        case writeFailed(Error)
        case extractionFailed(Error)
        case invalidTarget
        
        var errorDescription: String? {
            switch self {
            case .invalidURL:
                return "Invalid download URL"
            case .networkError(let error):
                return "Network error: \(error.localizedDescription)"
            case .writeFailed(let error):
                return "Failed to save file: \(error.localizedDescription)"
            case .extractionFailed(let error):
                return "Failed to extract model: \(error.localizedDescription)"
            case .invalidTarget:
                return "Invalid target directory"
            }
        }
    }
    
    private let urlSession: URLSession
    
    init() {
        let config = URLSessionConfiguration.default
        config.waitsForConnectivity = true
        config.timeoutIntervalForRequest = 300
        config.timeoutIntervalForResource = 3600 // 1 hour for large downloads
        self.urlSession = URLSession(configuration: config)
    }
    
    /// Download and extract a model to the target directory
    func downloadModel(
        _ model: VoskModel,
        to targetDir: String,
        progressHandler: ((Double) -> Void)? = nil
    ) async throws {
        guard let downloadURL = model.downloadURL else {
            throw DownloadError.invalidURL
        }
        
        let tempDir = NSTemporaryDirectory()
        let zipName = "\(model.rawValue).zip"
        let tempZipPath = (tempDir as NSString).appendingPathComponent(zipName)
        
        print("[VoskModelDownloader] Starting download: \(model.rawValue)")
        print("[VoskModelDownloader] URL: \(downloadURL)")
        print("[VoskModelDownloader] Temp path: \(tempZipPath)")
        print("[VoskModelDownloader] Target dir: \(targetDir)")
        
        // Download the model zip
        let (downloadedPath, _) = try await download(
            from: downloadURL,
            to: tempZipPath,
            progressHandler: progressHandler
        )
        
        print("[VoskModelDownloader] Download completed: \(downloadedPath)")
        
        defer {
            try? FileManager.default.removeItem(atPath: downloadedPath)
        }
        
        // Ensure target directory exists
        try FileManager.default.createDirectory(
            atPath: targetDir,
            withIntermediateDirectories: true,
            attributes: nil
        )
        
        print("[VoskModelDownloader] Target directory created: \(targetDir)")
        
        // Extract the zip
        try extractZip(downloadedPath, to: targetDir, modelName: model.rawValue)
        
        print("[VoskModelDownloader] Extraction completed successfully")
    }
    
    /// Download a custom model from a URL
    func downloadCustomModel(
        from urlString: String,
        modelName: String,
        to targetDir: String,
        progressHandler: ((Double) -> Void)? = nil
    ) async throws {
        guard let downloadURL = URL(string: urlString) else {
            throw DownloadError.invalidURL
        }
        
        let tempDir = NSTemporaryDirectory()
        let zipName = "\(modelName).zip"
        let tempZipPath = (tempDir as NSString).appendingPathComponent(zipName)
        
        // Download the model zip
        let (downloadedPath, _) = try await download(
            from: downloadURL,
            to: tempZipPath,
            progressHandler: progressHandler
        )
        
        defer {
            try? FileManager.default.removeItem(atPath: downloadedPath)
        }
        
        // Ensure target directory exists
        try FileManager.default.createDirectory(
            atPath: targetDir,
            withIntermediateDirectories: true,
            attributes: nil
        )
        
        // Extract the zip
        try extractZip(downloadedPath, to: targetDir, modelName: modelName)
    }
    
    /// Download a file from URL and save to local path
    private func download(
        from url: URL,
        to localPath: String,
        progressHandler: ((Double) -> Void)? = nil
    ) async throws -> (path: String, totalBytes: Int) {
        var request = URLRequest(url: url)
        request.setValue("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36", forHTTPHeaderField: "User-Agent")
        // Large files like 0.22 model (1.9GB) need much longer timeouts
        request.timeoutInterval = 3600 // 1 hour timeout for large downloads
        
        print("[VoskModelDownloader] Starting download from: \(url.absoluteString)")
        print("[VoskModelDownloader] User-Agent: Mozilla/5.0")
        print("[VoskModelDownloader] Request timeout: 3600 seconds (1 hour) for large files")
        
        let delegate = DownloadProgressDelegate(progressHandler: progressHandler)
        
        do {
            let (tempURL, response) = try await urlSession.download(for: request, delegate: delegate)
            
            guard let httpResponse = response as? HTTPURLResponse else {
                print("[VoskModelDownloader] ERROR: Response is not HTTPURLResponse: \(type(of: response))")
                throw DownloadError.networkError(
                    NSError(domain: "HTTP", code: -1, userInfo: [NSLocalizedDescriptionKey: "Response is not HTTPURLResponse"])
                )
            }
            
            let statusCode = httpResponse.statusCode
            print("[VoskModelDownloader] HTTP Status: \(statusCode)")
            print("[VoskModelDownloader] Content-Type: \(httpResponse.value(forHTTPHeaderField: "Content-Type") ?? "unknown")")
            print("[VoskModelDownloader] Content-Length: \(httpResponse.value(forHTTPHeaderField: "Content-Length") ?? "unknown")")
            
            guard (200...299).contains(statusCode) else {
                let errorBody = try? String(contentsOfFile: tempURL.path, encoding: .utf8)
                print("[VoskModelDownloader] ERROR: HTTP \(statusCode)")
                if let body = errorBody {
                    print("[VoskModelDownloader] Response body: \(body.prefix(500))")
                }
                throw DownloadError.networkError(
                    NSError(domain: "HTTP", code: statusCode, userInfo: [NSLocalizedDescriptionKey: "HTTP \(statusCode)"])
                )
            }
            
            let totalBytes = Int(httpResponse.expectedContentLength) > 0 ? Int(httpResponse.expectedContentLength) : 0
            
            print("[VoskModelDownloader] Download successful. Moving \(tempURL.path) to \(localPath)")
            
            try FileManager.default.moveItem(atPath: tempURL.path, toPath: localPath)
            
            print("[VoskModelDownloader] File moved successfully. Size: \(totalBytes) bytes")
            
            return (localPath, totalBytes)
        } catch {
            print("[VoskModelDownloader] Download error: \(error)")
            print("[VoskModelDownloader] Error type: \(type(of: error))")
            throw DownloadError.networkError(error)
        }
    }
    
    /// Extract zip file to target directory
    private func extractZip(
        _ zipPath: String,
        to targetDir: String,
        modelName: String
    ) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        
        let extractDir = (targetDir as NSString).appendingPathComponent(modelName)
        
        print("[VoskModelDownloader] Creating extract directory: \(extractDir)")
        
        try FileManager.default.createDirectory(
            atPath: extractDir,
            withIntermediateDirectories: true,
            attributes: nil
        )
        
        print("[VoskModelDownloader] Unzipping: \(zipPath) to \(extractDir)")
        print("[VoskModelDownloader] Unzip command: /usr/bin/unzip -q \"\(zipPath)\" -d \"\(extractDir)\"")
        
        process.arguments = ["-q", zipPath, "-d", extractDir]
        
        let pipe = Pipe()
        process.standardError = pipe
        
        do {
            try process.run()
            process.waitUntilExit()
            
            print("[VoskModelDownloader] Unzip process exited with status: \(process.terminationStatus)")
            
            if process.terminationStatus != 0 {
                let errorData = pipe.fileHandleForReading.readDataToEndOfFile()
                let errorMessage = String(data: errorData, encoding: .utf8) ?? "Unknown error"
                print("[VoskModelDownloader] Unzip error: \(errorMessage)")
                throw DownloadError.extractionFailed(
                    NSError(domain: "unzip", code: Int(process.terminationStatus), userInfo: [NSLocalizedDescriptionKey: errorMessage])
                )
            }
            
            // Verify extraction worked by checking for model marker files
            print("[VoskModelDownloader] Verifying extracted files...")
            let extractedFiles = try FileManager.default.contentsOfDirectory(atPath: extractDir)
            print("[VoskModelDownloader] Files in extract directory: \(extractedFiles)")
            
        } catch {
            print("[VoskModelDownloader] Exception during unzip: \(error)")
            throw DownloadError.extractionFailed(error)
        }
    }
}

// MARK: - URLSessionDownloadDelegate

private class DownloadProgressDelegate: NSObject, URLSessionDownloadDelegate {
    let progressHandler: ((Double) -> Void)?
    private var lastProgressUpdate = Date()
    
    init(progressHandler: ((Double) -> Void)? = nil) {
        self.progressHandler = progressHandler
    }
    
    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didFinishDownloadingTo location: URL
    ) {
        // Handled by async/await wrapper
    }
    
    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64,
        totalBytesWritten: Int64,
        totalBytesExpectedToWrite: Int64
    ) {
        let now = Date()
        // Log first update and then periodically
        let progress = Double(totalBytesWritten) / Double(totalBytesExpectedToWrite)
        let progressPercent = Int(progress * 100)
        let megabytesWritten = Double(totalBytesWritten) / (1024 * 1024)
        let megabytesTotal = Double(totalBytesExpectedToWrite) / (1024 * 1024)
        
        // Throttle progress updates to avoid excessive UI updates
        if now.timeIntervalSince(lastProgressUpdate) > 0.5 {
            print("[VoskModelDownloader] Download progress: \(progressPercent)% (\(String(format: "%.1f", megabytesWritten)) / \(String(format: "%.1f", megabytesTotal)) MB)")
            DispatchQueue.main.async {
                self.progressHandler?(progress)
            }
            lastProgressUpdate = now
        }
    }
}
