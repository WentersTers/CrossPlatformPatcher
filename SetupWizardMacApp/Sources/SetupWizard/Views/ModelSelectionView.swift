import SwiftUI

struct ModelSelectionView: View {
    @ObservedObject var viewModel: SetupWizardViewModel
    @State private var selectedModel: VoskModelDownloader.VoskModel = .enUs022Lgraph
    @State private var useCustomModel = false
    @State private var customModelPath = ""
    @State private var customModelName = ""
    
    var body: some View {
        VStack(spacing: 20) {
            // Header
            VStack(alignment: .leading, spacing: 8) {
                Text("Select or Download a Vosk Model")
                    .font(.title2)
                    .fontWeight(.bold)
                
                Text("Choose a pre-built model or provide your own")
                    .font(.subheadline)
                    .foregroundColor(.secondary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            
            Divider()
            
            // Model Selection
            if !useCustomModel {
                ScrollView {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("Available Models")
                            .font(.headline)
                        
                        VStack(alignment: .leading, spacing: 8) {
                            ForEach(VoskModelDownloader.VoskModel.allCases) { model in
                                HStack(spacing: 12) {
                                    Image(systemName: selectedModel == model ? "checkmark.circle.fill" : "circle")
                                        .foregroundColor(selectedModel == model ? .blue : .gray)
                                    
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(model.displayName)
                                            .font(.body)
                                        Text(modelDescription(for: model))
                                            .font(.caption)
                                            .foregroundColor(.secondary)
                                    }
                                    
                                    Spacer()
                                }
                                .contentShape(Rectangle())
                                .onTapGesture {
                                    selectedModel = model
                                }
                                .padding(12)
                                .background(selectedModel == model ? Color.blue.opacity(0.1) : Color.gray.opacity(0.05))
                                .cornerRadius(8)
                            }
                        }
                    }
                }
            } else {
                VStack(alignment: .leading, spacing: 12) {
                    Text("Custom Model")
                        .font(.headline)
                    
                    VStack(alignment: .leading, spacing: 8) {
                        HStack {
                            TextField("Model URL or file path", text: $customModelPath)
                                .textFieldStyle(.roundedBorder)
                            
                            Button(action: selectCustomModel) {
                                Image(systemName: "folder")
                            }
                            .help("Browse for model file")
                        }
                        
                        TextField("Model name (optional)", text: $customModelName)
                            .textFieldStyle(.roundedBorder)
                        
                        Text("Provide a URL to download or local path to use")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                }
                .padding()
                .background(Color.gray.opacity(0.05))
                .cornerRadius(8)
            }
            
            // Toggle custom model
            HStack {
                Image(systemName: useCustomModel ? "checkmark.square" : "square")
                    .foregroundColor(useCustomModel ? .blue : .gray)
                
                Text("Use your own model")
                    .font(.body)
                
                Spacer()
            }
            .contentShape(Rectangle())
            .onTapGesture {
                useCustomModel.toggle()
            }
            .padding(12)
            .background(useCustomModel ? Color.blue.opacity(0.1) : Color.gray.opacity(0.05))
            .cornerRadius(8)
            
            Divider()
            
            // Progress
            if viewModel.isProcessing {
                VStack(spacing: 8) {
                    ProgressView(value: viewModel.progress)
                        .tint(.blue)
                    
                    Text("Downloading model: \(Int(viewModel.progress * 100))%")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }
            
            // Logs
            if !viewModel.logOutput.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Activity Log")
                        .font(.caption)
                        .fontWeight(.semibold)
                    
                    ScrollView {
                        VStack(alignment: .leading, spacing: 4) {
                            ForEach(viewModel.logOutput, id: \.self) { log in
                                Text(log)
                                    .font(.caption)
                                    .foregroundColor(.secondary)
                                    .textSelection(.enabled)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .frame(height: 100)
                    .padding(8)
                    .background(Color.black.opacity(0.05))
                    .cornerRadius(6)
                }
            }
            
            Spacer()
            
            // Buttons
            HStack(spacing: 12) {
                Button(action: {
                    viewModel.goToFolderPicker()
                }) {
                    Text("Back")
                        .frame(maxWidth: .infinity)
                }
                .keyboardShortcut(.cancelAction)
                
                Button(action: downloadAndContinue) {
                    if viewModel.isProcessing {
                        ProgressView()
                            .scaleEffect(0.8, anchor: .center)
                    } else {
                        Text("Download & Continue")
                    }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(viewModel.isProcessing || (useCustomModel && customModelPath.isEmpty))
                .frame(maxWidth: .infinity)
            }
        }
        .padding(24)
    }
    
    private func modelDescription(for model: VoskModelDownloader.VoskModel) -> String {
        switch model {
        case .smallEnUs:
            return "~50 MB - Best for limited storage"
        case .enUs022:
            return "~330 MB - Good accuracy"
        case .enUs022Lgraph:
            return "~850 MB - Better accuracy with larger graph"
        case .enUs042Gigaspeech:
            return "~1.4 GB - Highest accuracy, requires most storage"
        }
    }
    
    private func selectCustomModel() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.allowedContentTypes = [.zip]
        panel.prompt = "Select Vosk Model ZIP"
        
        if panel.runModal() == .OK, let url = panel.url {
            customModelPath = url.path
            if customModelName.isEmpty {
                customModelName = url.deletingPathExtension().lastPathComponent
            }
        }
    }
    
    private func downloadAndContinue() {
        Task {
            if useCustomModel {
                await viewModel.downloadCustomModel(path: customModelPath, name: customModelName)
            } else {
                await viewModel.downloadModel(selectedModel)
            }
            
            // Navigate to patcher download step after successful model setup
            await MainActor.run {
                viewModel.goToDownload()
            }
        }
    }
}
