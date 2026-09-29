import AppKit
import SwiftUI
import Foundation
import Darwin

private let appName = "Claude Code 中文助手"
private let appVersion = (Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String) ?? "1.4.0"
private let manifestURL = URL(string: "https://raw.githubusercontent.com/CX-ArtLab/CC-zh-assistant/main/translation/manifest.json")!

private func assistantIconImage() -> NSImage {
    if let bundled = Bundle.main.url(forResource: "assistant-icon", withExtension: "png"),
       let image = NSImage(contentsOf: bundled) { return image }
    let cwd = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
    let repositoryRoot = cwd.lastPathComponent == "macOS" ? cwd.deletingLastPathComponent() : cwd
    let candidates = [
        cwd.appendingPathComponent("Resources/assistant-icon.png"),
        repositoryRoot.appendingPathComponent("macOS/Resources/assistant-icon.png"),
        repositoryRoot.appendingPathComponent("src/Assets/assistant-icon.png")
    ]
    for url in candidates {
        if let image = NSImage(contentsOf: url) { return image }
    }
    return NSImage(size: NSSize(width: 64, height: 64))
}

struct ClaudeEnvironment {
    var isInstalled: Bool = false
    var isRunning: Bool = false
    var appURL: URL?
    var resourcesURL: URL?
    var version: String = "未安装"
}

@MainActor
private final class AppModel: ObservableObject {
    @Published var status = "立即应用汉化"
    @Published var detail = ""
    @Published var isLocalized = false
    @Published var isBusy = false
    @Published var autoUpdate: Bool
    @Published var launchAtLogin: Bool
    @Published var claudeEnv = ClaudeEnvironment()
    @Published var totalEntries = 32606

    private var translationPack: [String: Any] = [:]
    private var monitorTask: Task<Void, Never>?

    init() {
        autoUpdate = UserDefaults.standard.object(forKey: "AutoUpdate") as? Bool ?? true
        launchAtLogin = LaunchAtLogin.isEnabled
        translationPack = Self.loadTranslationPack()
        refreshEnvironment()

        monitorTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 3_000_000_000)
                guard let self else { continue }
                self.refreshEnvironment()
                if self.autoUpdate && !self.isBusy {
                    await self.checkAndUpdatePack(silent: true)
                }
            }
        }
    }

    deinit { monitorTask?.cancel() }

    func refreshEnvironment() {
        let env = Self.detectClaudeEnvironment()
        claudeEnv = env
        if !env.isInstalled {
            isLocalized = false
            status = "未检测到 Claude"
            detail = "未在 /Applications 或 ~/Applications 中找到 Claude.app"
            return
        }

        let localized = Self.checkIfLocalized(env: env)
        isLocalized = localized
        if !isBusy {
            status = localized ? "汉化已生效" : "立即应用汉化"
        }
    }

    func toggleLocalization() async {
        guard !isBusy else { return }
        guard claudeEnv.isInstalled else {
            detail = "未检测到 Claude Desktop 安装。"
            return
        }

        isBusy = true
        if isLocalized {
            status = "正在恢复..."
            detail = "正在恢复官方原版文件与配置..."
            Self.performRestoreOfficial(env: claudeEnv)
            refreshEnvironment()
            detail = "已成功恢复为官方英文原版！"
            promptRestartIfNeeded()
        } else {
            status = "正在部署..."
            detail = "正在写入中文语言包并优化配置..."
            if autoUpdate {
                _ = try? await checkAndUpdatePack(silent: true)
            }
            do {
                try Self.performApplyLocalization(env: claudeEnv, pack: translationPack)
                refreshEnvironment()
                detail = "中文汉化包已成功应用！"
                promptRestartIfNeeded()
            } catch {
                detail = "应用汉化失败: \(error.localizedDescription)"
            }
        }
        isBusy = false
    }

    func restartClaude() {
        Self.doRestartClaude(env: claudeEnv)
        detail = "已请求重启 Claude Desktop。"
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
            self?.refreshEnvironment()
        }
    }

    func openDataDirectory() {
        let dir = Self.dataDirectory()
        NSWorkspace.shared.open(dir)
    }

    func setAutoUpdate(_ value: Bool) {
        autoUpdate = value
        UserDefaults.standard.set(value, forKey: "AutoUpdate")
    }

    func setLaunchAtLogin(_ value: Bool) {
        launchAtLogin = value
        UserDefaults.standard.set(value, forKey: "LaunchAtLogin")
        LaunchAtLogin.setEnabled(value)
    }

    private func promptRestartIfNeeded() {
        guard claudeEnv.isRunning else { return }
        let alert = NSAlert()
        alert.messageText = isLocalized ? "汉化包应用成功" : "已恢复官方原版"
        alert.informativeText = "检测到 Claude 正在运行。需要重启 Claude 才能使界面完全生效。\n\n是否立即重启 Claude？"
        alert.alertStyle = .informational
        alert.addButton(withTitle: "立即重启")
        alert.addButton(withTitle: "稍后手动重启")
        if alert.runModal() == .alertFirstButtonReturn {
            restartClaude()
        }
    }

    private func checkAndUpdatePack(silent: Bool) async -> Bool {
        do {
            let (manifestData, _) = try await URLSession.shared.data(from: manifestURL)
            guard let manifest = try JSONSerialization.jsonObject(with: manifestData) as? [String: Any],
                  let version = manifest["version"] as? String,
                  let downloadUrlString = (manifest["downloadUrl"] as? String) ?? (manifest["packUrl"] as? String),
                  let url = URL(string: downloadUrlString) else { return false }

            let bundledVersion = Self.bundledTranslationPackVersion()
            guard Self.comparePackVersions(version, bundledVersion) == .orderedDescending else { return false }
            let cachedVersion = UserDefaults.standard.string(forKey: "TranslationPackVersion") ?? ""
            guard Self.comparePackVersions(version, cachedVersion) == .orderedDescending else { return false }

            let (packData, _) = try await URLSession.shared.data(from: url)
            guard let pack = try JSONSerialization.jsonObject(with: packData) as? [String: Any], !pack.isEmpty else { return false }

            translationPack = pack
            try packData.write(to: Self.dataDirectory().appendingPathComponent("translation-pack.json"), options: .atomic)
            UserDefaults.standard.set(version, forKey: "TranslationPackVersion")

            if let stats = manifest["stats"] as? [String: Int], let total = manifest["totalEntries"] as? Int {
                totalEntries = total
                _ = stats
            }

            if isLocalized && claudeEnv.isInstalled {
                try Self.performApplyLocalization(env: claudeEnv, pack: pack)
            }
            if !silent { detail = "已自动同步最新云端词典 (v\(version))。" }
            return true
        } catch {
            return false
        }
    }

    // MARK: - Core Implementation Logic

    static func detectClaudeEnvironment() -> ClaudeEnvironment {
        var env = ClaudeEnvironment()

        // 1. Check running processes
        let running = NSWorkspace.shared.runningApplications.filter { app in
            app.bundleIdentifier == "com.anthropic.claudefordesktop" ||
            app.executableURL?.lastPathComponent == "Claude"
        }
        if let firstRunning = running.first {
            env.isRunning = true
            if let bundleURL = firstRunning.bundleURL {
                env.appURL = bundleURL
            }
        }

        // 2. Candidate paths
        let home = FileManager.default.homeDirectoryForCurrentUser
        let candidates = [
            URL(fileURLWithPath: "/Applications/Claude.app"),
            home.appendingPathComponent("Applications/Claude.app")
        ]

        if env.appURL == nil {
            for candidate in candidates {
                if FileManager.default.fileExists(atPath: candidate.path) {
                    env.appURL = candidate
                    break
                }
            }
        }

        guard let appURL = env.appURL, FileManager.default.fileExists(atPath: appURL.path) else {
            return env
        }

        let resources = appURL.appendingPathComponent("Contents/Resources")
        let ionDist = resources.appendingPathComponent("ion-dist")
        if FileManager.default.fileExists(atPath: resources.path) || FileManager.default.fileExists(atPath: ionDist.path) {
            env.isInstalled = true
            env.resourcesURL = resources

            let infoPlist = appURL.appendingPathComponent("Contents/Info.plist")
            if let data = try? Data(contentsOf: infoPlist),
               let plist = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any] {
                if let ver = plist["CFBundleShortVersionString"] as? String, !ver.isEmpty {
                    env.version = ver
                } else if let ver = plist["CFBundleVersion"] as? String, !ver.isEmpty {
                    env.version = ver
                } else {
                    env.version = "已安装"
                }
            } else {
                env.version = "已安装"
            }
        }

        return env
    }

    static func checkIfLocalized(env: ClaudeEnvironment) -> Bool {
        guard env.isInstalled, let res = env.resourcesURL else { return false }
        let zhCN = res.appendingPathComponent("ion-dist/i18n/zh-CN.json")
        return FileManager.default.fileExists(atPath: zhCN.path)
    }

    static func performApplyLocalization(env: ClaudeEnvironment, pack: [String: Any]) throws {
        guard let res = env.resourcesURL, let appURL = env.appURL else {
            throw NSError(domain: "CCZhAssistant", code: 1, userInfo: [NSLocalizedDescriptionKey: "未检测到 Claude 安装目录"])
        }
        let fm = FileManager.default
        let ionDist = res.appendingPathComponent("ion-dist")
        let i18nDir = ionDist.appendingPathComponent("i18n")
        let dynamicDir = i18nDir.appendingPathComponent("dynamic")
        let statsigDir = i18nDir.appendingPathComponent("statsig")
        let assetsDir = ionDist.appendingPathComponent("assets/v1")
        let resourcesSubdir = res.appendingPathComponent("resources")
        let backupDir = backupDirectory()

        try fm.createDirectory(at: i18nDir, withIntermediateDirectories: true)
        try fm.createDirectory(at: dynamicDir, withIntermediateDirectories: true)
        try fm.createDirectory(at: statsigDir, withIntermediateDirectories: true)
        try fm.createDirectory(at: backupDir, withIntermediateDirectories: true)

        // 1. Write frontend zh-CN.json
        if let frontendObj = pack["frontend"] {
            let data = try JSONSerialization.data(withJSONObject: frontendObj, options: [])
            try data.write(to: i18nDir.appendingPathComponent("zh-CN.json"), options: .atomic)
        }

        // 2. Write dynamic zh-CN.json
        if let dynamicObj = pack["dynamic"] {
            let data = try JSONSerialization.data(withJSONObject: dynamicObj, options: [])
            try data.write(to: dynamicDir.appendingPathComponent("zh-CN.json"), options: .atomic)
        }

        // 3. Write statsig zh-CN.json
        if let statsigObj = pack["statsig"] {
            let data = try JSONSerialization.data(withJSONObject: statsigObj, options: [])
            try data.write(to: statsigDir.appendingPathComponent("zh-CN.json"), options: .atomic)
        }

        // 4. Write desktop zh-CN.json & patch resources/en-US.json
        if let desktopObj = pack["desktop"] as? [String: Any] {
            let data = try JSONSerialization.data(withJSONObject: desktopObj, options: [])
            try? fm.createDirectory(at: resourcesSubdir, withIntermediateDirectories: true)
            try data.write(to: res.appendingPathComponent("zh-CN.json"), options: .atomic)
            try? data.write(to: resourcesSubdir.appendingPathComponent("zh-CN.json"), options: .atomic)

            let enDesktopCandidates = [
                res.appendingPathComponent("en-US.json"),
                resourcesSubdir.appendingPathComponent("en-US.json")
            ]
            for enDesktopPath in enDesktopCandidates {
                if fm.fileExists(atPath: enDesktopPath.path) {
                    let backupFile = backupDir.appendingPathComponent("en-US.json.orig")
                    if !fm.fileExists(atPath: backupFile.path) {
                        try? fm.copyItem(at: enDesktopPath, to: backupFile)
                    }
                    if let enData = try? Data(contentsOf: enDesktopPath),
                       var enDict = (try? JSONSerialization.jsonObject(with: enData)) as? [String: Any] {
                        for (k, v) in desktopObj {
                            enDict[k] = v
                        }
                        if let mergedData = try? JSONSerialization.data(withJSONObject: enDict, options: []) {
                            try? mergedData.write(to: enDesktopPath, options: .atomic)
                        }
                    }
                }
            }
        }

        // 5. Deploy translator.js and inject into index.html / frame-shell.html
        if let translatorURL = resourceURL("translator", ext: "js"),
           let translatorContent = try? String(contentsOf: translatorURL, encoding: .utf8) {
            let translatorData = Data(translatorContent.utf8)
            try? translatorData.write(to: ionDist.appendingPathComponent("translator.js"), options: .atomic)
            if fm.fileExists(atPath: assetsDir.path) {
                try? translatorData.write(to: assetsDir.appendingPathComponent("translator.js"), options: .atomic)
            }
            injectScriptTag(into: ionDist.appendingPathComponent("index.html"), backupDir: backupDir)
            injectScriptTag(into: ionDist.appendingPathComponent("frame-shell.html"), backupDir: backupDir)
        }

        // 6. Patch language whitelist in ion-dist/assets/v1/shared-*.js
        if fm.fileExists(atPath: assetsDir.path) {
            if let files = try? fm.contentsOfDirectory(at: assetsDir, includingPropertiesForKeys: nil) {
                for file in files where file.lastPathComponent.hasPrefix("shared-") && file.pathExtension == "js" {
                    if let content = try? String(contentsOf: file, encoding: .utf8) {
                        let target = "[\"en-US\",\"de-DE\""
                        let replacement = "[\"zh-CN\",\"en-US\",\"de-DE\""
                        if content.contains(target) && !content.contains(replacement) {
                            let backup = backupDir.appendingPathComponent(file.lastPathComponent + ".orig")
                            if !fm.fileExists(atPath: backup.path) {
                                try? fm.copyItem(at: file, to: backup)
                            }
                            let patched = content.replacingOccurrences(of: target, with: replacement)
                            try? patched.write(to: file, atomically: true, encoding: .utf8)
                            break
                        }
                    }
                }
            }
        }

        // 7. Update user configuration files
        updateAllClaudeConfigFiles(locale: "zh-CN")

        // 8. Gatekeeper & signature fix
        clearQuarantineAndResign(appURL: appURL)
    }

    static func performRestoreOfficial(env: ClaudeEnvironment) {
        guard let res = env.resourcesURL, let appURL = env.appURL else { return }
        let fm = FileManager.default
        let ionDist = res.appendingPathComponent("ion-dist")
        let i18nDir = ionDist.appendingPathComponent("i18n")
        let dynamicDir = i18nDir.appendingPathComponent("dynamic")
        let statsigDir = i18nDir.appendingPathComponent("statsig")
        let assetsDir = ionDist.appendingPathComponent("assets/v1")
        let resourcesSubdir = res.appendingPathComponent("resources")
        let backupDir = backupDirectory()

        // 1. Delete installed zh-CN.json and translator files
        try? fm.removeItem(at: i18nDir.appendingPathComponent("zh-CN.json"))
        try? fm.removeItem(at: dynamicDir.appendingPathComponent("zh-CN.json"))
        try? fm.removeItem(at: statsigDir.appendingPathComponent("zh-CN.json"))
        try? fm.removeItem(at: res.appendingPathComponent("zh-CN.json"))
        try? fm.removeItem(at: resourcesSubdir.appendingPathComponent("zh-CN.json"))
        try? fm.removeItem(at: ionDist.appendingPathComponent("translator.js"))
        try? fm.removeItem(at: assetsDir.appendingPathComponent("translator.js"))

        // 2. Restore shared-*.js from backup
        if let backupItems = try? fm.contentsOfDirectory(at: backupDir, includingPropertiesForKeys: nil) {
            for bFile in backupItems where bFile.lastPathComponent.hasPrefix("shared-") && bFile.lastPathComponent.hasSuffix(".orig") {
                let origName = String(bFile.lastPathComponent.dropLast(5))
                let targetPath = assetsDir.appendingPathComponent(origName)
                if fm.fileExists(atPath: targetPath.path) {
                    try? fm.removeItem(at: targetPath)
                    try? fm.copyItem(at: bFile, to: targetPath)
                }
            }
        }

        // 3. Restore en-US.json, index.html, frame-shell.html
        restoreOriginalFile(target: res.appendingPathComponent("en-US.json"), backupDir: backupDir)
        restoreOriginalFile(target: resourcesSubdir.appendingPathComponent("en-US.json"), backupDir: backupDir)
        restoreOriginalFile(target: ionDist.appendingPathComponent("index.html"), backupDir: backupDir)
        restoreOriginalFile(target: ionDist.appendingPathComponent("frame-shell.html"), backupDir: backupDir)

        // 4. Reset user configuration to en-US
        updateAllClaudeConfigFiles(locale: "en-US")

        // 5. Gatekeeper & signature fix
        clearQuarantineAndResign(appURL: appURL)
    }

    private static func injectScriptTag(into url: URL, backupDir: URL) {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path),
              var content = try? String(contentsOf: url, encoding: .utf8) else { return }

        let backup = backupDir.appendingPathComponent(url.lastPathComponent + ".orig")
        if !fm.fileExists(atPath: backup.path) {
            try? fm.copyItem(at: url, to: backup)
        }

        if content.contains("./translator.js") {
            content = content.replacingOccurrences(of: "./translator.js", with: "/translator.js")
            try? content.write(to: url, atomically: true, encoding: .utf8)
            return
        }

        if content.contains("translator.js") { return }

        if let range = content.range(of: "</head>", options: .caseInsensitive) {
            content.insert(contentsOf: "<script src=\"/translator.js\"></script>", at: range.lowerBound)
            try? content.write(to: url, atomically: true, encoding: .utf8)
        }
    }

    private static func restoreOriginalFile(target: URL, backupDir: URL) {
        let fm = FileManager.default
        let backup = backupDir.appendingPathComponent(target.lastPathComponent + ".orig")
        if fm.fileExists(atPath: backup.path) {
            try? fm.removeItem(at: target)
            try? fm.copyItem(at: backup, to: target)
        }
    }

    private static func updateAllClaudeConfigFiles(locale: String) {
        let home = FileManager.default.homeDirectoryForCurrentUser
        let paths = [
            home.appendingPathComponent("Library/Application Support/Claude/config.json"),
            home.appendingPathComponent("Library/Application Support/Claude-3p/config.json")
        ]
        for path in paths {
            updateUserConfigFile(at: path, locale: locale)
        }
    }

    private static func updateUserConfigFile(at path: URL, locale: String) {
        let fm = FileManager.default
        if !fm.fileExists(atPath: path.path) {
            try? fm.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true)
            let initial = "{\"locale\":\"\(locale)\"}\n"
            try? initial.write(to: path, atomically: true, encoding: .utf8)
            return
        }

        guard let data = try? Data(contentsOf: path),
              var dict = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return }

        dict["locale"] = locale
        if let newData = try? JSONSerialization.data(withJSONObject: dict, options: [.prettyPrinted]) {
            try? newData.write(to: path, options: .atomic)
        }
    }

    private static func clearQuarantineAndResign(appURL: URL) {
        let xattr = Process()
        xattr.executableURL = URL(fileURLWithPath: "/usr/bin/xattr")
        xattr.arguments = ["-cr", appURL.path]
        try? xattr.run()
        xattr.waitUntilExit()

        let codesign = Process()
        codesign.executableURL = URL(fileURLWithPath: "/usr/bin/codesign")
        codesign.arguments = ["--force", "--deep", "--sign", "-", appURL.path]
        try? codesign.run()
        codesign.waitUntilExit()
    }

    static func doRestartClaude(env: ClaudeEnvironment) {
        let running = NSWorkspace.shared.runningApplications.filter { app in
            app.bundleIdentifier == "com.anthropic.claudefordesktop" ||
            app.executableURL?.lastPathComponent == "Claude"
        }
        for app in running {
            app.terminate()
        }

        let pkill = Process()
        pkill.executableURL = URL(fileURLWithPath: "/usr/bin/pkill")
        pkill.arguments = ["-x", "Claude"]
        try? pkill.run()
        pkill.waitUntilExit()

        usleep(800_000)

        let openProc = Process()
        openProc.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        if let appURL = env.appURL, FileManager.default.fileExists(atPath: appURL.path) {
            openProc.arguments = ["-a", appURL.path]
        } else {
            openProc.arguments = ["-a", "Claude"]
        }
        try? openProc.run()
    }

    // MARK: - Resources & Utilities

    static func dataDirectory() -> URL {
        let url = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/Claude Code 中文助手")
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    static func backupDirectory() -> URL {
        let url = dataDirectory().appendingPathComponent("backups")
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    static func resourceURL(_ name: String, ext: String) -> URL? {
        if let url = Bundle.main.url(forResource: name, withExtension: ext) { return url }
        let cwd = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        let repositoryRoot = cwd.lastPathComponent == "macOS" ? cwd.deletingLastPathComponent() : cwd
        let candidates = [
            cwd.appendingPathComponent("Resources/\(name).\(ext)"),
            cwd.appendingPathComponent("macOS/Resources/\(name).\(ext)"),
            cwd.appendingPathComponent("src/Assets/\(name).\(ext)"),
            cwd.appendingPathComponent("translation/\(name).\(ext)"),
            repositoryRoot.appendingPathComponent("macOS/Resources/\(name).\(ext)"),
            repositoryRoot.appendingPathComponent("src/Assets/\(name).\(ext)"),
            repositoryRoot.appendingPathComponent("translation/\(name).\(ext)")
        ]
        return candidates.first { FileManager.default.fileExists(atPath: $0.path) }
    }

    static func loadTranslationPack() -> [String: Any] {
        let bundled: [String: Any] = loadJSON(named: "translation-pack", ext: "json") ?? [:]
        let bundledVersion = bundledTranslationPackVersion()
        let cachedVersion = UserDefaults.standard.string(forKey: "TranslationPackVersion") ?? ""
        let cachedURL = dataDirectory().appendingPathComponent("translation-pack.json")
        if comparePackVersions(cachedVersion, bundledVersion) == .orderedDescending,
           let data = try? Data(contentsOf: cachedURL),
           let cached = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
           !cached.isEmpty {
            return cached
        }
        return bundled
    }

    static func bundledTranslationPackVersion() -> String {
        let manifest: [String: Any] = loadJSON(named: "translation-manifest", ext: "json") ?? [:]
        return manifest["version"] as? String ?? ""
    }

    static func comparePackVersions(_ left: String, _ right: String) -> ComparisonResult {
        left.compare(right, options: [.numeric, .caseInsensitive])
    }

    static func loadJSON<T>(named name: String, ext: String) -> T? {
        guard let url = resourceURL(name, ext: ext), let data = try? Data(contentsOf: url) else { return nil }
        return (try? JSONSerialization.jsonObject(with: data)) as? T
    }
}

private enum LaunchAtLogin {
    static let label = "com.cxartlab.cc-zh-assistant"

    static var isEnabled: Bool {
        FileManager.default.fileExists(atPath: agentURL.path)
    }

    private static var agentURL: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }

    static func setEnabled(_ enabled: Bool) {
        let url = agentURL
        if enabled {
            guard let executable = Bundle.main.executablePath else { return }
            let plist: [String: Any] = [
                "Label": label,
                "ProgramArguments": [executable],
                "RunAtLoad": true,
                "ProcessType": "Interactive"
            ]
            try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            (plist as NSDictionary).write(to: url, atomically: true)
            runLaunchctl(["bootstrap", "gui/\(getuid())", url.path])
        } else {
            runLaunchctl(["bootout", "gui/\(getuid())/\(label)"])
            try? FileManager.default.removeItem(at: url)
        }
    }

    private static func runLaunchctl(_ arguments: [String]) {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        task.arguments = arguments
        try? task.run()
        task.waitUntilExit()
    }
}

private struct WindowConfigurator: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async { configure(view.window) }
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {
        DispatchQueue.main.async { configure(view.window) }
    }

    private func configure(_ window: NSWindow?) {
        guard let window else { return }
        let size = NSSize(width: 500, height: 550)
        guard window.contentView?.frame.size != size || window.styleMask.contains(.resizable) else { return }
        window.setContentSize(size)
        window.minSize = size
        window.maxSize = size
        window.styleMask.remove(.resizable)
        window.isMovableByWindowBackground = true
        window.center()
    }
}

private struct ContentView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        ZStack {
            Color(red: 0.93, green: 0.94, blue: 0.96).ignoresSafeArea()
            VStack(spacing: 0) {
                Spacer().frame(height: 52)

                // Soft pastel ambient glow with brand logo
                ZStack {
                    Circle()
                        .fill(RadialGradient(colors: [Color(red: 0.85, green: 0.47, blue: 0.34).opacity(0.26), .clear], center: .center, startRadius: 0, endRadius: 74))
                        .frame(width: 148, height: 158)
                        .offset(x: -28, y: -6)
                    Circle()
                        .fill(RadialGradient(colors: [Color(red: 1.0, green: 0.77, blue: 0.47).opacity(0.21), .clear], center: .center, startRadius: 0, endRadius: 74))
                        .frame(width: 148, height: 148)
                        .offset(x: 28, y: -20)
                    Circle()
                        .fill(RadialGradient(colors: [Color(red: 0.92, green: 0.60, blue: 0.45).opacity(0.19), .clear], center: .center, startRadius: 0, endRadius: 68))
                        .frame(width: 134, height: 144)
                        .offset(x: 48, y: -2)
                    Circle()
                        .fill(RadialGradient(colors: [Color(red: 0.55, green: 0.59, blue: 0.86).opacity(0.19), .clear], center: .center, startRadius: 0, endRadius: 75))
                        .frame(width: 151, height: 150)
                        .offset(x: 10, y: 35)

                    Image(nsImage: assistantIconImage())
                        .resizable()
                        .interpolation(.high)
                        .frame(width: 64, height: 64)
                        .clipShape(RoundedRectangle(cornerRadius: 14))
                        .shadow(color: Color.black.opacity(0.12), radius: 6, x: 0, y: 3)
                }
                .frame(width: 64, height: 64)

                Text("Welcome to Claude")
                    .font(.system(size: 26, weight: .regular, design: .rounded))
                    .foregroundColor(Color(red: 0.30, green: 0.31, blue: 0.42))
                    .padding(.top, 24)

                // Onboarding Card
                VStack(spacing: 0) {
                    Text("Claude Code 汉化助手 v\(appVersion)")
                        .font(.system(size: 15, weight: .bold))
                        .foregroundColor(Color(red: 0.30, green: 0.31, blue: 0.42))
                        .padding(.top, 18)

                    Button {
                        Task { await model.toggleLocalization() }
                    } label: {
                        HStack(spacing: 8) {
                            if model.isBusy {
                                ProgressView()
                                    .scaleEffect(0.8)
                                    .colorInvert()
                            }
                            Text(model.isBusy ? "正在处理..." : (model.isLocalized ? "✓  汉化已生效" : model.status))
                                .font(.system(size: 15, weight: .bold))
                        }
                        .foregroundColor(.white)
                        .frame(maxWidth: .infinity, minHeight: 42)
                        .background(model.isLocalized ? Color(red: 0.18, green: 0.63, blue: 0.26) : Color(red: 0.85, green: 0.47, blue: 0.34))
                        .clipShape(RoundedRectangle(cornerRadius: 10))
                        .shadow(color: Color.black.opacity(0.08), radius: 4, x: 0, y: 2)
                    }
                    .buttonStyle(.plain)
                    .disabled(model.isBusy || !model.claudeEnv.isInstalled)
                    .padding(.horizontal, 28)
                    .padding(.top, 18)

                    HStack(spacing: 28) {
                        Toggle("自动更新", isOn: Binding(get: { model.autoUpdate }, set: model.setAutoUpdate))
                        Toggle("开机启动", isOn: Binding(get: { model.launchAtLogin }, set: model.setLaunchAtLogin))
                    }
                    .toggleStyle(.checkbox)
                    .font(.system(size: 13))
                    .foregroundColor(Color(red: 0.30, green: 0.31, blue: 0.42))
                    .padding(.top, 18)
                }
                .frame(width: 344, height: 172)
                .background(Color(red: 0.925, green: 0.937, blue: 0.949))
                .clipShape(RoundedRectangle(cornerRadius: 12))
                .overlay(RoundedRectangle(cornerRadius: 12).stroke(Color.gray.opacity(0.24)))
                .shadow(color: Color.black.opacity(0.04), radius: 8, x: 0, y: 3)
                .padding(.top, 36)

                // Info line
                let claudeVer = model.claudeEnv.version.replacingOccurrences(of: "v", with: "")
                Text("Claude v\(claudeVer)  ·  已汉化 \(model.totalEntries) 条  ·  \(model.claudeEnv.isRunning ? "运行中" : "未启动")")
                    .font(.system(size: 13))
                    .foregroundColor(Color(red: 0.40, green: 0.42, blue: 0.52))
                    .padding(.top, 18)

                // Action buttons below
                HStack(spacing: 16) {
                    Button {
                        model.restartClaude()
                    } label: {
                        Text(model.claudeEnv.isRunning ? "重启 Claude" : "启动 Claude")
                            .font(.system(size: 13, weight: .medium))
                            .foregroundColor(Color(red: 0.30, green: 0.31, blue: 0.42))
                            .frame(width: 120, height: 32)
                            .background(Color.white.opacity(0.85))
                            .clipShape(RoundedRectangle(cornerRadius: 6))
                            .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.gray.opacity(0.25), lineWidth: 1))
                    }
                    .buttonStyle(.plain)
                    .disabled(!model.claudeEnv.isInstalled)

                    Button {
                        model.openDataDirectory()
                    } label: {
                        Text("配置目录")
                            .font(.system(size: 13, weight: .medium))
                            .foregroundColor(Color(red: 0.30, green: 0.31, blue: 0.42))
                            .frame(width: 120, height: 32)
                            .background(Color.white.opacity(0.85))
                            .clipShape(RoundedRectangle(cornerRadius: 6))
                            .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.gray.opacity(0.25), lineWidth: 1))
                    }
                    .buttonStyle(.plain)
                }
                .padding(.top, 14)

                if !model.detail.isEmpty {
                    Text(model.detail)
                        .font(.system(size: 11))
                        .foregroundColor(Color(red: 0.40, green: 0.42, blue: 0.52))
                        .padding(.top, 10)
                        .padding(.horizontal, 40)
                        .multilineTextAlignment(.center)
                }

                Spacer()
            }
        }
        .frame(width: 500, height: 550)
        .background(WindowConfigurator())
    }
}

@main
struct CCZhAssistantMacApp: App {
    @StateObject private var model = AppModel()

    var body: some Scene {
        WindowGroup {
            ContentView(model: model)
        }
        .windowStyle(.hiddenTitleBar)
    }
}
