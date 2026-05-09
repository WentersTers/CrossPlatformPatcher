import SwiftUI

struct ErrorView: View {
    let message: String
    let onDismiss: () -> Void
    
    var body: some View {
        VStack(spacing: 24) {
            Image(systemName: "exclamationmark.circle.fill")
                .font(.system(size: 64))
                .foregroundColor(.red)
            
            VStack(spacing: 8) {
                Text("Setup Failed")
                    .font(.title)
                    .fontWeight(.bold)
                
                Text(message)
                    .font(.callout)
                    .foregroundColor(.secondary)
                    .multilineTextAlignment(.center)
            }
            
            VStack(alignment: .leading, spacing: 8) {
                Text("Troubleshooting")
                    .fontWeight(.semibold)
                
                BulletPoint(text: "Check that PAIcom.exe exists in the selected folder")
                BulletPoint(text: "Ensure you have internet connectivity for downloads")
                BulletPoint(text: "Check ~/Library/Application Support/CrossPlatformPatcher/setup-wizard.log for details")
            }
            .padding()
            .background(Color(.controlBackgroundColor))
            .cornerRadius(8)
            
            Spacer()
            
            HStack {
                Button("Show Logs") {
                    let logPath = Logger.shared.getLogPath()
                    NSWorkspace.shared.selectFile(logPath, inFileViewerRootedAtPath: "")
                }
                .buttonStyle(.bordered)
                
                Spacer()
                
                Button("Restart", action: onDismiss)
                    .buttonStyle(.borderedProminent)
            }
        }
        .padding(32)
    }
}
