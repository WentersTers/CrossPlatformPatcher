import SwiftUI

struct DotNetInstallView: View {
    @ObservedObject var viewModel: SetupWizardViewModel
    @State private var isInstalling = false
    @State private var showCancelConfirm = false
    @State private var installError: String?
    @State private var selectedFolder: String?
    
    var body: some View {
        VStack(spacing: 24) {
            VStack(alignment: .leading, spacing: 8) {
                Text(".NET Framework Installation")
                    .font(.title2)
                    .fontWeight(.bold)
                
                Text("Installing .NET 4.8 in your Wine prefix")
                    .font(.callout)
                    .foregroundColor(.secondary)
            }
            
            if isInstalling {
                VStack(spacing: 12) {
                    HStack {
                        ProgressView()
                            .frame(maxWidth: 20)
                        
                        VStack(alignment: .leading) {
                            Text("Installing .NET Framework 4.8...")
                                .fontWeight(.semibold)
                            Text("This may take 10-20 minutes")
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }
                        
                        Spacer()
                    }
                    
                    // Log view
                    LogViewComponent(lines: viewModel.logOutput, isLive: true)
                        .frame(maxHeight: 200)
                    
                    HStack {
                        Text("\(viewModel.logOutput.count) operations")
                            .font(.caption)
                            .foregroundColor(.secondary)
                        
                        Spacer()
                    }
                }
            } else if let error = installError {
                VStack(spacing: 12) {
                    HStack {
                        Image(systemName: "exclamationmark.circle.fill")
                            .font(.title2)
                            .foregroundColor(.red)
                        
                        VStack(alignment: .leading) {
                            Text("Installation Failed")
                                .fontWeight(.semibold)
                            Text(error)
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }
                        
                        Spacer()
                    }
                    .padding()
                    .background(Color(.controlBackgroundColor))
                    .cornerRadius(8)
                    
                    HStack {
                        Button("Retry", action: {
                            startInstallation()
                        })
                        .buttonStyle(.bordered)
                        
                        Button("Skip .NET Install", role: .destructive) {
                            viewModel.goToComplete()
                        }
                        .buttonStyle(.bordered)
                        
                        Spacer()
                    }
                }
            } else {
                VStack(spacing: 12) {
                    HStack {
                        Image(systemName: "checkmark.circle.fill")
                            .font(.title2)
                            .foregroundColor(.green)
                        
                        VStack(alignment: .leading) {
                            Text(".NET Installation Complete")
                                .fontWeight(.semibold)
                            Text("Your system is ready for PAIcom")
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }
                        
                        Spacer()
                    }
                    .padding()
                    .background(Color(.controlBackgroundColor))
                    .cornerRadius(8)
                }
            }
            
            Spacer()
            
            HStack {
                if isInstalling {
                    Button("Cancel", role: .destructive, action: {
                        showCancelConfirm = true
                    })
                    .alert("Cancel Installation?", isPresented: $showCancelConfirm) {
                        Button("Cancel Installation", role: .destructive) {
                            cancelInstallation()
                        }
                        Button("Keep Installing", role: .cancel) {}
                    } message: {
                        Text("This will stop the current installation. You can retry later.")
                    }
                } else {
                    Button("Back") {
                        viewModel.currentScreen = .dependencyCheck
                    }
                }
                
                Spacer()
                
                if !isInstalling && installError == nil {
                    Button("Continue", action: {
                        viewModel.goToComplete()
                    })
                    .buttonStyle(.borderedProminent)
                }
            }
        }
        .padding(32)
        .onAppear {
            startInstallation()
        }
    }
    
    private func startInstallation() {
        isInstalling = true
        installError = nil
        viewModel.logOutput = []
        
        Task {
            do {
                guard let folder = viewModel.selectedFolder else {
                    throw NSError(domain: "Setup", code: -1, userInfo: [NSLocalizedDescriptionKey: "No folder selected"])
                }
                
                let _ = viewModel.stateManager  // Stored for potential future use
                try await viewModel.dotNetInstaller.installDotNet(winePrefix: folder) { line in
                    DispatchQueue.main.async {
                        viewModel.logOutput.append(line)
                    }
                }
                
                await MainActor.run {
                    isInstalling = false
                }
            } catch {
                await MainActor.run {
                    isInstalling = false
                    installError = error.localizedDescription
                }
            }
        }
    }
    
    private func cancelInstallation() {
        Task {
            await viewModel.dotNetInstaller.cancel()
            await MainActor.run {
                isInstalling = false
                installError = "Installation cancelled by user"
            }
        }
    }
}
