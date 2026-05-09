import SwiftUI

struct DownloadPatchView: View {
    @ObservedObject var viewModel: SetupWizardViewModel
    
    var body: some View {
        VStack(spacing: 24) {
            VStack(alignment: .leading, spacing: 8) {
                Text("Download & Patch")
                    .font(.title2)
                    .fontWeight(.bold)
                
                Text("Fetching latest patcher and applying it to your PAIcom.exe")
                    .font(.callout)
                    .foregroundColor(.secondary)
            }
            
            VStack(spacing: 16) {
                // Progress bar
                ProgressView(value: viewModel.progress)
                    .tint(.blue)
                
                // Status text
                Text("\(Int(viewModel.progress * 100))%")
                    .font(.caption)
                    .foregroundColor(.secondary)
                
                // Log output
                ScrollView {
                    VStack(alignment: .leading, spacing: 4) {
                        ForEach(viewModel.logOutput, id: \.self) { line in
                            Text(line)
                                .font(.system(.caption, design: .monospaced))
                                .foregroundColor(.secondary)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 150)
                .padding()
                .background(Color(.controlBackgroundColor))
                .cornerRadius(8)
            }
            
            Spacer()
            
            HStack {
                Button("Cancel", role: .destructive) {
                    Task {
                        await viewModel.cancel()
                    }
                    viewModel.currentScreen = .welcome
                }
                
                Spacer()
            }
        }
        .padding(32)
        .onAppear {
            Task {
                await viewModel.downloadAndPatch()
                if viewModel.progress >= 1.0 {
                    try? await Task.sleep(nanoseconds: 1_000_000_000) // 1 second
                    viewModel.goToDependencyCheck()
                }
            }
        }
    }
}
