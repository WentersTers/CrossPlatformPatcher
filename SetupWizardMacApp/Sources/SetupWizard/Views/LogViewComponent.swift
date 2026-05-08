import SwiftUI
import Combine

struct LogViewComponent: View {
    let lines: [String]
    let isLive: Bool
    @State private var canScroll = true
    
    var body: some View {
        ScrollViewReader { scrollProxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(Array(lines.enumerated()), id: \.offset) { index, line in
                        HStack(spacing: 8) {
                            Text("\(String(format: "%03d", index + 1))")
                                .font(.system(.caption2, design: .monospaced))
                                .foregroundColor(.secondary)
                                .frame(width: 30, alignment: .trailing)
                            
                            Text(line)
                                .font(.system(.caption, design: .monospaced))
                                .foregroundColor(.secondary)
                                .lineLimit(1)
                            
                            Spacer()
                        }
                        .id(index)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(8)
                .onReceive([self.lines.count].publisher.merge(with: Just(self.lines.count))) { _ in
                    if isLive && !lines.isEmpty {
                        scrollProxy.scrollTo(lines.count - 1, anchor: .bottom)
                    }
                }
            }
        }
        .background(Color(.controlBackgroundColor))
        .cornerRadius(6)
    }
}
