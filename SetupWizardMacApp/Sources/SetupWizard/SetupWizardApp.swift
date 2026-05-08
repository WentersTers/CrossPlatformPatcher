import SwiftUI

@main
struct SetupWizardApp: App {
    @State private var viewModel: SetupWizardViewModel?
    
    var body: some Scene {
        WindowGroup {
            if let viewModel = viewModel {
                SetupWizardView(viewModel: viewModel)
                    .frame(minWidth: 600, minHeight: 500)
                    .onAppear {
                        NSWindow.allowsAutomaticWindowTabbing = false
                    }
            } else {
                ProgressView()
                    .onAppear {
                        Task {
                            let vm = await SetupWizardViewModel()
                            await MainActor.run {
                                viewModel = vm
                            }
                        }
                    }
            }
        }
        .windowStyle(.hiddenTitleBar)
    }
}

struct SetupWizardView: View {
    @ObservedObject var viewModel: SetupWizardViewModel
    @State private var showingError = false
    
    var body: some View {
        ZStack {
            switch viewModel.currentScreen {
            case .welcome:
                WelcomeView(viewModel: viewModel)
            case .folderPicker:
                FolderPickerView(viewModel: viewModel)
            case .modelSelection:
                ModelSelectionView(viewModel: viewModel)
            case .download:
                DownloadPatchView(viewModel: viewModel)
            case .dependencyCheck:
                DependencyCheckView(viewModel: viewModel)
            case .dotNetInstall:
                DotNetInstallView(viewModel: viewModel)
            case .complete:
                CompleteView(viewModel: viewModel)
            case .error(let message):
                ErrorView(message: message, onDismiss: {
                    viewModel.currentScreen = .welcome
                })
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(.controlBackgroundColor))
    }
}

// Previews not available for executable target
