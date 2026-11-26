import CFNetwork
import Foundation

enum StudioTranslationServer {
    static let preferenceKey = "libreTranslateServer"

    private static let maximumTextLength = 16_000
    private static let maximumResponseBytes = 1_048_576
    private static let timeout: TimeInterval = 30

    static func parse(_ base: String) throws -> URL {
        let address = base.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !address.isEmpty, address.utf8.count <= 2_048, !address.contains("\\"),
              !address.unicodeScalars.contains(where: { $0.properties.isWhitespace || $0.properties.generalCategory == .control || $0.properties.generalCategory == .format }),
              var parts = URLComponents(string: address), let scheme = parts.scheme?.lowercased(),
              let host = parts.percentEncodedHost?.lowercased(), !host.isEmpty,
              parts.user == nil, parts.password == nil, parts.query == nil, parts.fragment == nil,
              parts.port.map({ (1...65535).contains($0) }) ?? true else {
            throw Failure.invalidEndpoint
        }
        if scheme == "http" {
            switch host {
            case "localhost":
                // Use a literal loopback address so local DNS cannot redirect an HTTP request.
                parts.host = "127.0.0.1"
            case "127.0.0.1", "[::1]", "::1":
                break
            default:
                throw Failure.invalidEndpoint
            }
        } else if scheme != "https" {
            throw Failure.invalidEndpoint
        }
        parts.scheme = scheme
        var path = parts.percentEncodedPath
        while path.hasSuffix("/") { path.removeLast() }
        parts.percentEncodedPath = path + "/"
        guard let url = parts.url else { throw Failure.invalidEndpoint }
        return url
    }

    static func translate(rows: [String], source: String, target: String, server: URL) async throws -> [String] {
        try Task.checkCancellation()
        let base = try parse(server.absoluteString)
        let destination = base.appendingPathComponent("translate", isDirectory: false)
        let input = try makeInput(rows, source: source, target: target)
        guard !input.indices.isEmpty else { return rows }

        var request = URLRequest(url: destination, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        request.httpMethod = "POST"
        request.httpShouldHandleCookies = false
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        do { request.httpBody = try JSONEncoder().encode(input.payload) } catch { throw Failure.invalidRequest }

        let configuration = URLSessionConfiguration.ephemeral
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.urlCredentialStorage = nil
        configuration.timeoutIntervalForRequest = timeout
        configuration.timeoutIntervalForResource = timeout
        if base.scheme == "http" {
            configuration.connectionProxyDictionary = [
                kCFNetworkProxiesHTTPEnable as String: 0,
                kCFNetworkProxiesHTTPSEnable as String: 0,
                kCFNetworkProxiesSOCKSEnable as String: 0,
                kCFNetworkProxiesProxyAutoConfigEnable as String: 0,
                kCFNetworkProxiesProxyAutoDiscoveryEnable as String: 0
            ]
        }

        let session = URLSession(configuration: configuration, delegate: NoRedirects(), delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        do {
            let (bytes, metadata) = try await session.bytes(for: request)
            guard let response = metadata as? HTTPURLResponse, response.url == destination else {
                throw Failure.invalidResponse
            }
            try checkStatus(response.statusCode)
            guard metadata.expectedContentLength <= Int64(maximumResponseBytes) else { throw Failure.responseTooLarge }
            var data = Data()
            for try await byte in bytes {
                guard data.count < maximumResponseBytes else { throw Failure.responseTooLarge }
                data.append(byte)
            }
            try Task.checkCancellation()
            return try merge(data, into: rows, replacing: input.indices)
        } catch let failure as Failure {
            throw failure
        } catch is CancellationError {
            throw CancellationError()
        } catch let error as URLError {
            if Task.isCancelled || error.code == .cancelled { throw CancellationError() }
            if error.code == .timedOut { throw Failure.timedOut }
            if error.code == .appTransportSecurityRequiresSecureConnection { throw Failure.secureConnectionRequired }
            throw Failure.unavailable
        } catch {
            throw Failure.unavailable
        }
    }

    private static func makeInput(_ rows: [String], source: String, target: String) throws -> (indices: [Int], payload: Payload) {
        guard !rows.isEmpty, rows.count <= maximumTextLength else { throw Failure.invalidRequest }
        var remaining = maximumTextLength - (rows.count - 1)
        for row in rows {
            let length = row.utf16.count
            guard length <= remaining else { throw Failure.invalidRequest }
            remaining -= length
        }
        let suppliedSource = source.trimmingCharacters(in: .whitespacesAndNewlines)
        let sourceCode = suppliedSource.isEmpty || suppliedSource.lowercased() == "auto" ? "auto" : suppliedSource
        let targetCode = target.trimmingCharacters(in: .whitespacesAndNewlines)
        guard validLanguage(sourceCode), validLanguage(targetCode), targetCode.lowercased() != "auto" else {
            throw Failure.invalidLanguage
        }
        let indices = rows.indices.filter { !rows[$0].allSatisfy(\.isWhitespace) }
        return (indices, Payload(rows: indices.map { rows[$0] }, source: sourceCode, target: targetCode))
    }

    private static func validLanguage(_ code: String) -> Bool {
        code.range(of: #"\A[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8}){0,3}\z"#, options: .regularExpression) != nil
    }

    private static func merge(_ data: Data, into rows: [String], replacing indices: [Int]) throws -> [String] {
        guard data.count <= maximumResponseBytes else { throw Failure.responseTooLarge }
        guard let output = try? JSONDecoder().decode(Reply.self, from: data), output.rows.count == indices.count else {
            throw Failure.invalidResponse
        }
        var result = rows
        for (index, text) in zip(indices, output.rows) { result[index] = text }
        return result
    }

    private static func checkStatus(_ status: Int) throws {
        switch status {
        case 200: return
        case 300...399: throw Failure.redirected
        case 400, 413, 422: throw Failure.rejected
        case 401, 403: throw Failure.authorizationRequired
        case 429: throw Failure.rateLimited
        default: throw Failure.unavailable
        }
    }

    private struct Payload: Encodable {
        let rows: [String]
        let source: String
        let target: String

        private enum CodingKeys: String, CodingKey { case q, source, target, format }

        func encode(to encoder: Encoder) throws {
            var fields = encoder.container(keyedBy: CodingKeys.self)
            if rows.count == 1 { try fields.encode(rows[0], forKey: .q) }
            else { try fields.encode(rows, forKey: .q) }
            try fields.encode(source, forKey: .source)
            try fields.encode(target, forKey: .target)
            try fields.encode("text", forKey: .format)
        }
    }

    private struct Reply: Decodable {
        let rows: [String]

        private enum CodingKeys: String, CodingKey { case translatedText }

        init(from decoder: Decoder) throws {
            let fields = try decoder.container(keyedBy: CodingKeys.self)
            if let batch = try? fields.decode([String].self, forKey: .translatedText) { rows = batch }
            else { rows = [try fields.decode(String.self, forKey: .translatedText)] }
        }
    }

    private final class NoRedirects: NSObject, URLSessionTaskDelegate {
        func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                        newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
            completionHandler(nil)
        }
    }

    private enum Failure: LocalizedError {
        case invalidEndpoint, invalidRequest, invalidLanguage, invalidResponse, responseTooLarge, redirected,
             rejected, authorizationRequired, rateLimited, unavailable, timedOut, secureConnectionRequired

        var errorDescription: String? {
            switch self {
            case .invalidEndpoint: "Enter an HTTPS LibreTranslate base address or an HTTP address on this Mac."
            case .invalidRequest: "Select up to 16,000 characters to translate."
            case .invalidLanguage: "Choose valid source and target languages."
            case .invalidResponse: "The translation server returned an invalid result or a different number of rows."
            case .responseTooLarge: "The translation server returned more than 1 MB of data."
            case .redirected: "The translation server redirected the request. Use its final server address."
            case .rejected: "The translation server rejected the request. Check its language support and limits."
            case .authorizationRequired: "The translation server requires authorization or denied access."
            case .rateLimited: "The translation server is limiting requests. Try again later."
            case .unavailable: "The translation server is unavailable. Check the address or try again later."
            case .timedOut: "The translation server did not respond in time."
            case .secureConnectionRequired: "macOS blocked the HTTP connection. Use an HTTPS server address."
            }
        }
    }

    static func selfCheck() throws {
        enum CheckFailure: Error { case endpoint, request, response }

        let https = try parse(" https://example.invalid/service/// ")
        let local = try parse("http://localhost:5000/")
        guard https.absoluteString == "https://example.invalid/service/",
              local.absoluteString == "http://127.0.0.1:5000/" else { throw CheckFailure.endpoint }
        for value in ["http://example.invalid", "http://localhost.example.invalid", "https://user@example.invalid",
                      "https://example.invalid/?key=sample", "https://example.invalid/#part", "file:///tmp/data"] {
            guard (try? parse(value)) == nil else { throw CheckFailure.endpoint }
        }
        let rows = ["first", "  ", "first", "last"]
        let input = try makeInput(rows, source: "", target: "es")
        guard input.indices == [0, 2, 3], input.payload.rows == ["first", "first", "last"], input.payload.source == "auto",
              (try? makeInput([String(repeating: "x", count: 16_001)], source: "en", target: "es")) == nil else {
            throw CheckFailure.request
        }
        let data = Data(#"{"translatedText":["uno","uno","último"]}"#.utf8)
        guard try merge(data, into: rows, replacing: input.indices) == ["uno", "  ", "uno", "último"],
              (try? merge(Data(#"{"translatedText":"wrong count"}"#.utf8), into: rows, replacing: input.indices)) == nil else {
            throw CheckFailure.response
        }
    }
}
