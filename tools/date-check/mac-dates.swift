// Prints the creation date AVFoundation reports for each file given on the command line, as an ISO 8601 instant, with the metadata key it came from.
//   swiftc -O tools/date-check/mac-dates.swift -o mac-dates && ./mac-dates file...
import AVFoundation
import Foundation

let iso = ISO8601DateFormatter()
iso.formatOptions = [.withInternetDateTime]
let sem = DispatchSemaphore(value: 0)

Task {
    for path in CommandLine.arguments.dropFirst() {
        let asset = AVURLAsset(url: URL(fileURLWithPath: path))
        var line = URL(fileURLWithPath: path).lastPathComponent + "\t"
        do {
            if let item = try await asset.load(.creationDate) {
                let d = try await item.load(.dateValue)
                let key = item.key.map { "\($0)" } ?? "?"
                line += "creationDate=" + (d.map { iso.string(from: $0) } ?? "nil") + "\tkey=" + key + "\tspace=" + (item.keySpace?.rawValue ?? "?")
            } else {
                line += "creationDate=none"
            }
        } catch {
            line += "error=\(error)"
        }
        print(line)
    }
    sem.signal()
}
sem.wait()
