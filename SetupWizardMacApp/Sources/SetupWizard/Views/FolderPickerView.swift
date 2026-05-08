import SwiftUI
import Combine

struct FolderPickerView: View {
    @ObservedObject var viewModel: SetupWizardViewModel
    @State private var isSelectingFolder = false
    
    var body: some View {
        VStack(spacing: 24) {
            VStack(alignment: .leading, spacing: 8) {
                Text("Select PAIcom Folder")
                    .font(.title2)
                    .fontWeight(.bold)
                
                Text("Choose the folder where you have PAIcom.exe")
                    .font(.callout)
                    .foregroundColor(.secondary)
            }
            
            VStack(spacing: 12) {
                if let folder = viewModel.selectedFolder {
                    HStack {
                        Image(systemName: "folder.fill")
                            .foregroundColor(.blue)
                        
                        VStack(alignment: .leading, spacing: 2) {
                            Text("Selected Folder")
                                .font(.caption)
                                .foregroundColor(.secondary)
                            Text(folder)
                                .font(.callout)
                                .lineLimit(2)
                        }
                        
                        Spacer()
                        
                        Image(systemName: "checkmark.circle.fill")
                            .foregroundColor(.green)
                            .font(.title3)
                    }
                    .padding()
                    .background(Color(.controlBackgroundColor))
                    .cornerRadius(8)
                    
                    HStack {
                        Button("Change") {
                            isSelectingFolder = true
                        }
                        
                        Spacer()
                        
                        Button("Continue") {
                            viewModel.goToModelSelection()
                        }
                        .buttonStyle(.borderedProminent)
                    }
                } else {
                    VStack(spacing: 12) {
                        Image(systemName: "folder.badge.questionmark")
                            .font(.system(size: 48))
                            .foregroundColor(.secondary)
                        
                        Text("No folder selected")
                            .font(.callout)
                            .foregroundColor(.secondary)
                        
                        Button("Browse for PAIcom Folder", action: {
                            isSelectingFolder = true
                        })
                        .buttonStyle(.bordered)
                    }
                    .padding()
                    .frame(maxHeight: .infinity)
                }
            }
            
            Spacer()
            
            HStack {
                Button("Back") {
                    viewModel.currentScreen = .welcome
                }
                
                Spacer()
            }
        }
        .padding(32)
        .onReceive(Just(isSelectingFolder).eraseToAnyPublisher()) { newValue in
            if newValue {
                Task {
                    if let selected = await viewModel.selectFolderDialog() {
                        await MainActor.run {
                            viewModel.selectedFolder = selected
                            isSelectingFolder = false
                        }
                    } else {
                        await MainActor.run {
                            isSelectingFolder = false
                        }
                    }
                }
            }
        }
    }
}
