import SwiftUI

struct DependencyCheckView: View {
    @ObservedObject var viewModel: SetupWizardViewModel
    @State private var isChecking = true
    @State private var hasWine = false
    @State private var wineType: String?
    @State private var needsInstallation = false
    
    var body: some View {
        VStack(spacing: 24) {
            VStack(alignment: .leading, spacing: 8) {
                Text("Dependency Check")
                    .font(.title2)
                    .fontWeight(.bold)
                
                Text("Checking for Wine, Whisky, and Homebrew")
                    .font(.callout)
                    .foregroundColor(.secondary)
            }
            
            if isChecking {
                VStack(spacing: 16) {
                    ProgressView()
                    Text("Scanning your system...")
                        .font(.callout)
                        .foregroundColor(.secondary)
                }
                .frame(maxHeight: .infinity)
            } else if hasWine {
                VStack(spacing: 12) {
                    HStack {
                        Image(systemName: "checkmark.circle.fill")
                            .font(.title2)
                            .foregroundColor(.green)
                        
                        VStack(alignment: .leading) {
                            Text("Wine/Whisky Found")
                                .fontWeight(.semibold)
                            Text("Using: \(wineType ?? "unknown")")
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }
                        
                        Spacer()
                    }
                    .padding()
                    .background(Color(.controlBackgroundColor))
                    .cornerRadius(8)
                    
                    Text("Your system is ready for the next step.")
                        .font(.callout)
                        .foregroundColor(.secondary)
                }
            } else {
                VStack(spacing: 12) {
                    HStack {
                        Image(systemName: "exclamationmark.circle.fill")
                            .font(.title2)
                            .foregroundColor(.orange)
                        
                        VStack(alignment: .leading) {
                            Text("Wine Not Installed")
                                .fontWeight(.semibold)
                            Text("You'll need to install Whisky or Wine")
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }
                        
                        Spacer()
                    }
                    .padding()
                    .background(Color(.controlBackgroundColor))
                    .cornerRadius(8)
                    
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Recommended options:")
                            .font(.callout)
                            .fontWeight(.semibold)
                        
                        Link(destination: URL(string: "https://github.com/Whisky-App/Whisky/releases")!) {
                            Label("Install Whisky (Recommended)", systemImage: "arrow.up.right")
                        }
                        
                        Link(destination: URL(string: "https://www.codeweavers.com/crossover")!) {
                            Label("Install CrossOver", systemImage: "arrow.up.right")
                        }
                    }
                    .padding()
                    .background(Color(.controlBackgroundColor))
                    .cornerRadius(8)
                    
                    Button("Retry Check", action: {
                        isChecking = true
                        checkDependencies()
                    })
                    .buttonStyle(.bordered)
                }
            }
            
            Spacer()
            
            HStack {
                Button("Back") {
                    viewModel.currentScreen = .download
                }
                
                Spacer()
                
                if hasWine {
                    Button("Continue", action: {
                        viewModel.goToDotNetInstall()
                    })
                    .buttonStyle(.borderedProminent)
                }
            }
        }
        .padding(32)
        .onAppear {
            checkDependencies()
        }
    }
    
    private func checkDependencies() {
        Task {
            let result = await viewModel.checkDependencies()
            await MainActor.run {
                hasWine = result.hasWine
                wineType = result.wineType
                isChecking = false
                
                if !hasWine {
                    needsInstallation = true
                }
            }
        }
    }
}
