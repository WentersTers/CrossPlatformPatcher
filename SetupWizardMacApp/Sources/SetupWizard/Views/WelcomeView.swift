import SwiftUI

struct WelcomeView: View {
    @ObservedObject var viewModel: SetupWizardViewModel
    
    var body: some View {
        VStack(spacing: 24) {
            VStack(spacing: 12) {
                Text("PAIcom Setup Wizard")
                    .font(.largeTitle)
                    .fontWeight(.bold)
                
                Text("For macOS")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            
            VStack(alignment: .leading, spacing: 16) {
                HStack(spacing: 12) {
                    Image(systemName: "info.circle.fill")
                        .font(.title2)
                        .foregroundColor(.blue)
                    
                    VStack(alignment: .leading, spacing: 4) {
                        Text("About PAIcom on macOS")
                            .fontWeight(.semibold)
                        Text("PAIcom is a Windows application. This wizard will help you set up Wine to run it on macOS.")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                }
                .padding()
                .background(Color(.controlBackgroundColor))
                .cornerRadius(8)
            }
            
            VStack(alignment: .leading, spacing: 12) {
                Text("This wizard will:")
                    .fontWeight(.semibold)
                
                VStack(alignment: .leading, spacing: 8) {
                    StepView(number: 1, title: "Select your PAIcom folder")
                    StepView(number: 2, title: "Download the latest patcher")
                    StepView(number: 3, title: "Patch your PAIcom.exe")
                    StepView(number: 4, title: "Check for Wine/Whisky")
                    StepView(number: 5, title: "Install .NET Framework")
                    StepView(number: 6, title: "Create launch scripts")
                }
                .padding()
                .background(Color(.controlBackgroundColor))
                .cornerRadius(8)
            }
            
            Spacer()
            
            HStack {
                Button("Quit") {
                    NSApplication.shared.terminate(nil)
                }
                .keyboardShortcut(.cancelAction)
                
                Spacer()
                
                Button("Start", action: {
                    viewModel.goToFolderPicker()
                })
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
            }
        }
        .padding(32)
    }
}

struct StepView: View {
    let number: Int
    let title: String
    
    var body: some View {
        HStack(spacing: 12) {
            Circle()
                .fill(Color.blue)
                .frame(width: 28, height: 28)
                .overlay(
                    Text("\(number)")
                        .foregroundColor(.white)
                        .font(.caption.weight(.bold))
                )
            
            Text(title)
                .font(.callout)
            
            Spacer()
        }
    }
}
