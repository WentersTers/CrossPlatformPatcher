import SwiftUI

struct CompleteView: View {
    @ObservedObject var viewModel: SetupWizardViewModel
    @State private var launchingApp = false
    
    var body: some View {
        VStack(spacing: 24) {
            Image(systemName: "checkmark.circle.fill")
                .font(.system(size: 64))
                .foregroundColor(.green)
            
            VStack(spacing: 8) {
                Text("Setup Complete!")
                    .font(.title)
                    .fontWeight(.bold)
                
                Text("PAIcom is ready to launch on macOS")
                    .font(.callout)
                    .foregroundColor(.secondary)
            }
            
            VStack(alignment: .leading, spacing: 12) {
                CompletionItem(icon: "checkmark", title: "PAIcom.exe patched", subtitle: "With Vosk speech recognition")
                CompletionItem(icon: "checkmark", title: "Wine configured", subtitle: "Ready to run Windows apps")
                CompletionItem(icon: "checkmark", title: ".NET Framework installed", subtitle: "All dependencies satisfied")
                CompletionItem(icon: "checkmark", title: "Launch scripts created", subtitle: "run.sh and launch.command")
            }
            .padding()
            .background(Color(.controlBackgroundColor))
            .cornerRadius(8)
            
            VStack(alignment: .leading, spacing: 8) {
                Text("Next Steps")
                    .fontWeight(.semibold)
                
                Text("You can now launch PAIcom by:")
                    .font(.callout)
                    .foregroundColor(.secondary)
                
                VStack(alignment: .leading, spacing: 6) {
                    BulletPoint(text: "Double-clicking launch.command in the PAIcom folder")
                    BulletPoint(text: "Running sh run.sh from Terminal")
                    BulletPoint(text: "Creating a Whisky bottle shortcut")
                }
                .padding()
                .background(Color(.controlBackgroundColor))
                .cornerRadius(8)
            }
            
            Spacer()
            
            HStack {
                Button("Show Log File") {
                    let logPath = Logger.shared.getLogPath()
                    NSWorkspace.shared.selectFile(logPath, inFileViewerRootedAtPath: "")
                }
                .buttonStyle(.bordered)
                
                Spacer()
                
                Button("Done", action: {
                    NSApplication.shared.terminate(nil)
                })
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(32)
    }
}

struct CompletionItem: View {
    let icon: String
    let title: String
    let subtitle: String
    
    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: icon)
                .foregroundColor(.green)
                .font(.title3)
            
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.callout)
                    .fontWeight(.semibold)
                
                Text(subtitle)
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            
            Spacer()
        }
    }
}

struct BulletPoint: View {
    let text: String
    
    var body: some View {
        HStack(spacing: 8) {
            Text("•")
                .font(.title3)
            
            Text(text)
                .font(.callout)
        }
    }
}
