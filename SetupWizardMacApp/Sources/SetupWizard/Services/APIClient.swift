import Foundation

/// Handles GitHub API interactions for fetching latest patcher release
actor APIClient {
    enum APIError: LocalizedError {
        case invalidURL
        case networkError(Error)
        case decodingError(Error)
        case assetNotFound(String)
        case noRelease
        case httpError(statusCode: Int, message: String?)
        
        var errorDescription: String? {
            switch self {
            case .invalidURL:
                return "Invalid GitHub API URL"
            case .networkError(let error):
                return "Network error: \(error.localizedDescription)"
            case .decodingError(let error):
                return "Failed to parse GitHub response: \(error.localizedDescription)"
            case .assetNotFound(let name):
                return "Asset not found: \(name)"
            case .noRelease:
                return "No releases found"
            case .httpError(let statusCode, let message):
                let statusMsg = statusCodeDescription(statusCode)
                return "GitHub API error \(statusCode) \(statusMsg): \(message ?? "Unknown error")"
            }
        }
        
        private func statusCodeDescription(_ code: Int) -> String {
            switch code {
            case 401: return "(Unauthorized - check GitHub authentication)"
            case 403: return "(Forbidden - rate limited or insufficient permissions)"
            case 404: return "(Not Found - repository does not exist, is private, or has no releases published)"
            case 422: return "(Unprocessable Entity)"
            default: return ""
            }
        }
    }
    
    struct GitHubAsset: Decodable {
        let name: String
        let browser_download_url: String
        let size: Int
    }
    
    struct GitHubRelease: Decodable {
        let tag_name: String
        let assets: [GitHubAsset]
    }
    
    private let repository: String
    private let urlSession: URLSession
    
    init(repository: String = "WentersTers/CrossPlatformPatcher") {
        self.repository = repository
        self.urlSession = URLSession.shared
    }
    
    /// Fetch latest release metadata
    func fetchLatestRelease() async throws -> GitHubRelease {
        let latestUrl = "https://api.github.com/repos/\(repository)/releases/latest"
        guard let url = URL(string: latestUrl) else {
            Logger.shared.error("Invalid GitHub API URL: \(latestUrl)")
            throw APIError.invalidURL
        }
        
        Logger.shared.debug("Fetching latest release from: \(latestUrl)")
        
        do {
            let (data, response) = try await urlSession.data(from: url)
            
            guard let httpResponse = response as? HTTPURLResponse else {
                Logger.shared.error("No HTTP response received")
                throw APIError.networkError(NSError(domain: "NoHTTPResponse", code: -1))
            }
            
            // If /releases/latest returns 404, fall back to fetching all releases
            if httpResponse.statusCode == 404 {
                Logger.shared.debug("No latest release found, falling back to list all releases endpoint")
                return try await fetchLatestReleaseFromList()
            }
            
            // Handle other error status codes
            guard httpResponse.statusCode == 200 else {
                var errorMessage: String?
                if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                   let message = json["message"] as? String {
                    errorMessage = message
                }
                Logger.shared.error("GitHub API returned status \(httpResponse.statusCode): \(errorMessage ?? "Unknown error")")
                throw APIError.httpError(statusCode: httpResponse.statusCode, message: errorMessage)
            }
            
            let release = try JSONDecoder().decode(GitHubRelease.self, from: data)
            Logger.shared.debug("Fetched release: \(release.tag_name) with \(release.assets.count) assets")
            return release
        } catch let error as APIError {
            throw error  // Re-throw API errors as-is
        } catch let error as DecodingError {
            Logger.shared.error("Failed to decode release: \(error)")
            throw APIError.decodingError(error)
        } catch {
            Logger.shared.error("Network error fetching release: \(error)")
            throw APIError.networkError(error)
        }
    }
    
    /// Fallback: fetch all releases and return the newest one
    private func fetchLatestReleaseFromList() async throws -> GitHubRelease {
        let allReleasesUrl = "https://api.github.com/repos/\(repository)/releases"
        guard let url = URL(string: allReleasesUrl) else {
            Logger.shared.error("Invalid GitHub API URL: \(allReleasesUrl)")
            throw APIError.invalidURL
        }
        
        Logger.shared.debug("Fetching all releases from: \(allReleasesUrl)")
        
        do {
            let (data, response) = try await urlSession.data(from: url)
            
            guard let httpResponse = response as? HTTPURLResponse else {
                Logger.shared.error("No HTTP response received")
                throw APIError.networkError(NSError(domain: "NoHTTPResponse", code: -1))
            }
            
            guard httpResponse.statusCode == 200 else {
                var errorMessage: String?
                if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                   let message = json["message"] as? String {
                    errorMessage = message
                }
                Logger.shared.error("GitHub API returned status \(httpResponse.statusCode): \(errorMessage ?? "Unknown error")")
                throw APIError.httpError(statusCode: httpResponse.statusCode, message: errorMessage)
            }
            
            let releases = try JSONDecoder().decode([GitHubRelease].self, from: data)
            guard let latestRelease = releases.first else {
                Logger.shared.error("No releases found in repository")
                throw APIError.noRelease
            }
            
            Logger.shared.debug("Found \(releases.count) releases, using newest: \(latestRelease.tag_name)")
            return latestRelease
        } catch let error as APIError {
            throw error
        } catch let error as DecodingError {
            Logger.shared.error("Failed to decode releases list: \(error)")
            throw APIError.decodingError(error)
        } catch {
            Logger.shared.error("Network error fetching releases list: \(error)")
            throw APIError.networkError(error)
        }
    }
    
    /// Get patcher asset URL for current architecture
    func getPatcherAssetURL() async throws -> (URL: URL, name: String, size: Int) {
        let release = try await fetchLatestRelease()
        guard !release.assets.isEmpty else {
            Logger.shared.error("Release has no assets")
            throw APIError.noRelease
        }
        
        let arch = getArchitecture()
        let assetName: String
        
        switch arch {
        case "arm64":
            assetName = "CrossPlatformPatcher-M-Arm"
        case "x86_64":
            assetName = "CrossPlatformPatcher-M-x64"
        default:
            Logger.shared.error("Unsupported architecture: \(arch)")
            throw APIError.assetNotFound(arch)
        }
        
        Logger.shared.debug("Looking for asset: \(assetName)")
        
        guard let asset = release.assets.first(where: { $0.name == assetName }) else {
            Logger.shared.error("Asset not found: \(assetName)")
            throw APIError.assetNotFound(assetName)
        }
        
        guard let url = URL(string: asset.browser_download_url) else {
            Logger.shared.error("Invalid asset URL: \(asset.browser_download_url)")
            throw APIError.invalidURL
        }
        
        Logger.shared.debug("Found asset: \(asset.name) (\(asset.size) bytes)")
        return (url, asset.name, asset.size)
    }
    
    /// Download file with progress tracking
    func download(from url: URL, to destinationPath: String, onProgress: @escaping (Double) -> Void) async throws {
        Logger.shared.debug("Downloading from: \(url) to: \(destinationPath)")
        
        let request = URLRequest(url: url)
        
        do {
            let (data, response) = try await urlSession.data(for: request)
            
            guard let httpResponse = response as? HTTPURLResponse, httpResponse.statusCode == 200 else {
                Logger.shared.error("Invalid HTTP response: \(response)")
                throw APIError.networkError(NSError(domain: "HTTPError", code: -1))
            }
            
            try data.write(to: URL(fileURLWithPath: destinationPath), options: .atomic)
            Logger.shared.debug("Download completed: \(destinationPath)")
            onProgress(1.0)
        } catch {
            Logger.shared.error("Download failed: \(error)")
            throw APIError.networkError(error)
        }
    }
    
    private func getArchitecture() -> String {
        var systemInfo = utsname()
        uname(&systemInfo)
        let machine = withUnsafeBytes(of: systemInfo.machine) { ptr in
            String(cString: ptr.baseAddress!.assumingMemoryBound(to: CChar.self))
        }
        return machine
    }
}
