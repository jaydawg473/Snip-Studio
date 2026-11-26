import Foundation

enum MacUpdateChecker {
    enum Result: Equatable, Sendable {
        case noMacRelease
        case upToDate
        case available(version: String, releasePage: URL)
    }

    private static let repository = "jaydawg473/Snip-Studio"
    private static let pageSize = 100
    private static let maximumPages = 5

    static func check(currentVersion: String? = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String) async throws -> Result {
        guard let currentVersion, let installed = Version(currentVersion) else { throw Failure.invalidAppVersion }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 12
        configuration.timeoutIntervalForResource = 25
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        let session = URLSession(configuration: configuration)
        defer { session.finishTasksAndInvalidate() }

        var releases: [Release] = []
        for page in 1...maximumPages {
            guard let url = URL(string: "https://api.github.com/repos/\(repository)/releases?per_page=\(pageSize)&page=\(page)")
            else { throw Failure.unexpectedResponse }
            var request = URLRequest(url: url)
            request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
            request.setValue("2026-03-10", forHTTPHeaderField: "X-GitHub-Api-Version")
            request.setValue("SnipStudioMac/\(currentVersion)", forHTTPHeaderField: "User-Agent")
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse, http.url == url else { throw Failure.unexpectedResponse }
            guard http.statusCode == 200 else { throw Failure.serviceUnavailable(http.statusCode) }
            let pageReleases = try decodeReleases(data)
            releases.append(contentsOf: pageReleases)
            let hasNext = http.value(forHTTPHeaderField: "Link")?.contains("rel=\"next\"") == true
            if !hasNext { return selectUpdate(in: releases, installed: installed) }
        }
        throw Failure.incompleteCatalog
    }

    static func resolve(releaseData: Data, currentVersion: String) throws -> Result {
        guard let installed = Version(currentVersion) else { throw Failure.invalidAppVersion }
        return selectUpdate(in: try decodeReleases(releaseData), installed: installed)
    }

    private static func decodeReleases(_ data: Data) throws -> [Release] {
        guard data.count <= 8 * 1024 * 1024 else { throw Failure.unexpectedResponse }
        do {
            return try JSONDecoder().decode([Release].self, from: data)
        } catch {
            throw Failure.unexpectedResponse
        }
    }

    private static func selectUpdate(in releases: [Release], installed: Version) -> Result {
        var hasMacRelease = false
        var stable: Candidate?
        var preview: Candidate?
        for release in releases where !release.draft {
            guard isSafeTag(release.tag_name),
                  let page = officialURL(release.html_url, path: "/\(repository)/releases/tag/\(release.tag_name)") else { continue }
            for asset in release.assets where asset.state == "uploaded" && asset.size > 0 {
                guard let version = macVersion(from: asset.name),
                      officialURL(asset.browser_download_url,
                                  path: "/\(repository)/releases/download/\(release.tag_name)/\(asset.name)") != nil else { continue }
                hasMacRelease = true
                let isPreview = release.prerelease || !version.prerelease.isEmpty || asset.name.lowercased().hasSuffix("-trial.dmg")
                // A bare version on a preview has no ordering evidence against an installed build of the same release.
                if isPreview && version.prerelease.isEmpty && version.core == installed.core { continue }
                guard version.compare(to: installed) > 0 else { continue }
                let candidate = Candidate(version: version, page: page)
                if isPreview {
                    if preview.map({ version.compare(to: $0.version) > 0 }) ?? true { preview = candidate }
                } else {
                    if stable.map({ version.compare(to: $0.version) > 0 }) ?? true { stable = candidate }
                }
            }
        }
        guard let candidate = stable ?? preview else { return hasMacRelease ? .upToDate : .noMacRelease }
        return .available(version: candidate.version.label, releasePage: candidate.page)
    }

    private static func macVersion(from name: String) -> Version? {
        let prefix = "SnipStudio-"
        let normalized = name.lowercased()
        guard normalized.hasPrefix(prefix.lowercased()),
              let suffix = ["-macos-arm64-trial.dmg", "-macos-arm64.dmg"].first(where: { normalized.hasSuffix($0) }),
              name.count > prefix.count + suffix.count else { return nil }
        return Version(String(name.dropFirst(prefix.count).dropLast(suffix.count)))
    }

    private static func isSafeTag(_ tag: String) -> Bool {
        guard !tag.isEmpty, tag.utf8.count <= 200,
              tag.utf8.allSatisfy({ Version.isASCIIAlphanumeric($0) || [UInt8(43), 45, 46, 47, 95].contains($0) }) else { return false }
        return tag.split(separator: "/", omittingEmptySubsequences: false).allSatisfy { !$0.isEmpty && $0 != "." && $0 != ".." }
    }

    private static func officialURL(_ raw: String, path: String) -> URL? {
        guard let components = URLComponents(string: raw),
              components.scheme == "https", components.host?.lowercased() == "github.com",
              components.user == nil, components.password == nil, components.port == nil,
              components.query == nil, components.fragment == nil, components.path == path else { return nil }
        return components.url
    }

    private struct Release: Decodable {
        let tag_name: String
        let html_url: String
        let draft: Bool
        let prerelease: Bool
        let assets: [Asset]
    }

    private struct Asset: Decodable {
        let name: String
        let state: String
        let size: Int64
        let browser_download_url: String
    }

    private struct Candidate {
        let version: Version
        let page: URL
    }

    private struct Version {
        let label: String
        let core: [Int]
        let prerelease: [Substring]

        init?(_ raw: String) {
            let label = raw.lowercased().hasPrefix("v") ? String(raw.dropFirst()) : raw
            guard label.utf8.count <= 100 else { return nil }
            let parts = label.split(separator: "+", maxSplits: 1, omittingEmptySubsequences: false)
            guard let first = parts.first else { return nil }
            if parts.count == 2, Self.identifiers(parts[1], prerelease: false) == nil { return nil }
            let version = first.split(separator: "-", maxSplits: 1, omittingEmptySubsequences: false)
            guard let versionNumber = version.first else { return nil }
            let numbers = versionNumber.split(separator: ".", omittingEmptySubsequences: false)
            let core = numbers.compactMap { number -> Int? in
                guard Self.isNumeric(number), number.count == 1 || number.first != "0" else { return nil }
                return Int(number)
            }
            guard numbers.count == 3, core.count == numbers.count else { return nil }
            let prerelease: [Substring]
            if version.count == 2 {
                guard let identifiers = Self.identifiers(version[1], prerelease: true) else { return nil }
                prerelease = identifiers
            } else {
                prerelease = []
            }
            self.label = label
            self.core = core
            self.prerelease = prerelease
        }

        func compare(to other: Self) -> Int {
            if core != other.core { return core.lexicographicallyPrecedes(other.core) ? -1 : 1 }
            if prerelease == other.prerelease { return 0 }
            if prerelease.isEmpty { return 1 }
            if other.prerelease.isEmpty { return -1 }
            for (ours, theirs) in zip(prerelease, other.prerelease) where ours != theirs {
                switch (Self.isNumeric(ours), Self.isNumeric(theirs)) {
                case (true, true):
                    if ours.count != theirs.count { return ours.count < theirs.count ? -1 : 1 }
                case (true, false): return -1
                case (false, true): return 1
                case (false, false): break
                }
                return ours < theirs ? -1 : 1
            }
            return prerelease.count < other.prerelease.count ? -1 : 1
        }

        fileprivate static func isASCIIAlphanumeric(_ byte: UInt8) -> Bool {
            (48...57).contains(byte) || (65...90).contains(byte) || (97...122).contains(byte)
        }

        private static func isNumeric(_ value: Substring) -> Bool {
            !value.isEmpty && value.utf8.allSatisfy { (48...57).contains($0) }
        }

        private static func identifiers(_ raw: Substring, prerelease: Bool) -> [Substring]? {
            let parts = raw.split(separator: ".", omittingEmptySubsequences: false)
            guard !parts.isEmpty, parts.allSatisfy({ part in
                !part.isEmpty && part.utf8.allSatisfy { isASCIIAlphanumeric($0) || $0 == 45 } &&
                    (!prerelease || !isNumeric(part) || part.count == 1 || part.first != "0")
            }) else { return nil }
            return parts
        }
    }

    private enum Failure: LocalizedError {
        case invalidAppVersion, unexpectedResponse, serviceUnavailable(Int), incompleteCatalog

        var errorDescription: String? {
            switch self {
            case .invalidAppVersion: "Snip Studio could not read the installed app version."
            case .unexpectedResponse: "GitHub returned a release list Snip Studio could not verify."
            case .serviceUnavailable(let status): "GitHub could not provide the release list (HTTP \(status)). Try again later."
            case .incompleteCatalog: "Snip Studio could not finish checking the release list. Try again later."
            }
        }
    }
}
