using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace SnipStudio
{
    public interface IOfflineTextTranslationService : ITextTranslationService
    {
        bool RequiresExplicitSourceLanguage { get; }
    }

    // Bundled Node hosts the open-source Bergamot WASM engine. The worker has
    // no server, account, model-download, or browser dependency. Text travels
    // only over this owned child's anonymous pipes and is never a command-line
    // argument, temporary file, diagnostic message, or saved preference.
    public sealed class OfflineTranslationService : IOfflineTextTranslationService, ISegmentedTextTranslationService
    {
        public const int MaximumTextLength = 16000;
        public const int MaximumSegments = 256;
        public const int MaximumFrameBytes = 1024 * 1024;
        private const int MaximumManifestBytes = 64 * 1024;
        private const int MaximumErrorBytes = 1024 * 1024;
        private static readonly SemaphoreSlim EngineSlots = new SemaphoreSlim(1, 1);
        private static readonly Dictionary<string, string> LanguageNames = CreateLanguageNames();
        private readonly string engineRoot;
        private readonly TimeSpan requestTimeout;
        private readonly object stateGate = new object();
        private readonly CancellationTokenSource lifetimeCancellation = new CancellationTokenSource();
        private bool disposed;
        public bool RequiresExplicitSourceLanguage { get { return true; } }

        private sealed class LocalBundle
        {
            internal string Runtime;
            internal string Worker;
            internal readonly HashSet<string> Pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public OfflineTranslationService(string engineRoot) : this(engineRoot, TimeSpan.FromSeconds(60)) { }

        internal OfflineTranslationService(string engineRoot, TimeSpan timeout)
        {
            if (String.IsNullOrWhiteSpace(engineRoot)) throw new ArgumentException("An offline translation folder is required.", "engineRoot");
            this.engineRoot = Path.GetFullPath(engineRoot);
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException("timeout");
            requestTimeout = timeout;
        }

        public async Task<IList<TranslationLanguage>> GetLanguagesAsync(CancellationToken cancellationToken)
        {
            using (CancellationTokenSource operation = LinkLifetime(cancellationToken))
            {
                CancellationToken token = operation.Token;
                return await Task.Run<IList<TranslationLanguage>>(delegate
                {
                    token.ThrowIfCancellationRequested();
                    LocalBundle bundle = LoadBundle();
                    List<TranslationLanguage> languages = new List<TranslationLanguage>();
                    foreach (KeyValuePair<string, string> source in LanguageNames)
                    {
                        List<string> targets = new List<string>();
                        foreach (KeyValuePair<string, string> target in LanguageNames)
                            if (!String.Equals(source.Key, target.Key, StringComparison.OrdinalIgnoreCase) && CanTranslate(bundle, source.Key, target.Key)) targets.Add(target.Key);
                        if (targets.Count > 0) languages.Add(new TranslationLanguage(source.Key, source.Value, false, targets));
                    }
                    languages.Sort(delegate(TranslationLanguage left, TranslationLanguage right)
                    { return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name); });
                    token.ThrowIfCancellationRequested();
                    return languages.AsReadOnly();
                }, token).ConfigureAwait(false);
            }
        }

        public async Task<TranslationResult> TranslateAsync(string text, string sourceCodeOrEmpty, string targetCode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (String.IsNullOrWhiteSpace(text)) throw InvalidRequest("There is no recognized text to translate.");
            IList<string> translated = await TranslateSegmentsAsync(new string[] { text }, sourceCodeOrEmpty, targetCode, cancellationToken).ConfigureAwait(false);
            return new TranslationResult(translated[0], NormalizeLanguage(sourceCodeOrEmpty), NormalizeLanguage(targetCode), null, "Offline translation (Bergamot)");
        }

        public async Task<IList<string>> TranslateSegmentsAsync(IList<string> segments, string sourceCodeOrEmpty, string targetCode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<string> input = SnapshotSegments(segments);
            string source = NormalizeLanguage(sourceCodeOrEmpty), target = NormalizeLanguage(targetCode);
            if (source == "" || source == "auto") throw InvalidRequest("Choose the language of the original text. Offline translation does not detect it automatically.");
            if (!LanguageNames.ContainsKey(source) || !LanguageNames.ContainsKey(target)) throw InvalidRequest("Choose one of the installed source and target languages.");
            if (source == target) throw InvalidRequest("Choose a target language different from the original language.");
            using (CancellationTokenSource operation = LinkLifetime(cancellationToken))
            {
                operation.CancelAfter(requestTimeout);
                CancellationToken token = operation.Token;
                bool entered = false;
                bool started = false;
                Process process = null;
                try
                {
                    bool hasText = false;
                    foreach (string segment in input) if (!String.IsNullOrWhiteSpace(segment)) { hasText = true; break; }
                    if (!hasText) { token.ThrowIfCancellationRequested(); return input.AsReadOnly(); }
                    LocalBundle bundle = await Task.Run(delegate { token.ThrowIfCancellationRequested(); return LoadBundle(); }, token).ConfigureAwait(false);
                    if (!CanTranslate(bundle, source, target)) throw InvalidRequest("This language pair is not installed. Reinstall Snip Studio to restore the bundled translation languages.");
                    await EngineSlots.WaitAsync(token).ConfigureAwait(false);
                    entered = true;
                    token.ThrowIfCancellationRequested();
                    process = new Process();
                    ProcessStartInfo start = new ProcessStartInfo
                    {
                        FileName = bundle.Runtime,
                        Arguments = "\"" + bundle.Worker + "\"",
                        WorkingDirectory = engineRoot,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    // Parent-shell Node configuration must not inject modules or
                    // change the packaged worker's module-resolution boundary.
                    start.EnvironmentVariables.Remove("NODE_OPTIONS");
                    start.EnvironmentVariables.Remove("NODE_PATH");
                    process.StartInfo = start;
                    // Do not rely on a previously awaited file task being
                    // incomplete: Process.Start itself must never run on the
                    // window thread. Do not cancel this Task before Start has
                    // returned; cleanup must know whether it created a child.
                    bool didStart = await Task.Run(delegate { return process.Start(); }).ConfigureAwait(false);
                    if (!didStart) throw EngineUnavailable();
                    started = true;
                    token.ThrowIfCancellationRequested();
                    // Keep the actual created process handle; cleanup never
                    // searches by executable name, PID, or a process-tree match.
                    if (process.Handle == IntPtr.Zero) throw EngineUnavailable();
                    using (token.Register(delegate { StopOwnedProcess(process); }))
                    {
                        token.ThrowIfCancellationRequested();
                        Task errors = DrainErrorAsync(process.StandardError.BaseStream, token);
                        ObserveFault(errors);
                        Task canceled = Task.Delay(Timeout.Infinite, token);
                        List<string> output = new List<string>(input.Count);
                        int requestId = 0;
                        long responseBytes = 0;
                        foreach (string segment in input)
                        {
                            token.ThrowIfCancellationRequested();
                            // Retain line positions, including blank lines; the
                            // worker rejects whitespace-only requests.
                            if (String.IsNullOrWhiteSpace(segment)) { output.Add(segment); continue; }
                            requestId++;
                            Dictionary<string, object> data = new Dictionary<string, object>();
                            data.Add("src", source);
                            data.Add("trg", target);
                            data.Add("text", segment);
                            data.Add("html", false);
                            Dictionary<string, object> request = new Dictionary<string, object>();
                            request.Add("id", requestId);
                            request.Add("command", "Translate");
                            request.Add("data", data);
                            byte[] payload = Encoding.UTF8.GetBytes(Serializer().Serialize(request));
                            if (payload.Length > MaximumFrameBytes) throw InvalidRequest("Select a smaller portion of text to translate.");
                            // Keep stdin open between lines. Send one request,
                            // await its same-ID response, then send the next so
                            // the worker reuses its models without queue growth.
                            Task write = WriteFrameAsync(process.StandardInput.BaseStream, payload, token);
                            ObserveFault(write);
                            await AwaitWorkerStepAsync(write, errors, canceled, token).ConfigureAwait(false);
                            Task<byte[]> frame = ReadFrameAsync(process.StandardOutput.BaseStream, token);
                            ObserveFault(frame);
                            await AwaitWorkerStepAsync(frame, errors, canceled, token).ConfigureAwait(false);
                            byte[] response = await frame.ConfigureAwait(false);
                            responseBytes += response.Length;
                            if (responseBytes > MaximumFrameBytes) throw new TranslationException(TranslationErrorKind.ResponseTooLarge,
                                "The offline translation engine returned too much data. Try a smaller selection.");
                            if (errors.IsFaulted) await errors.ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            output.Add(ParseResponse(response, requestId, source, target).Text);
                        }
                        token.ThrowIfCancellationRequested();
                        return output.AsReadOnly();
                    }
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsDisposed()) throw new OperationCanceledException("Offline translation was closed.");
                    throw new TranslationException(TranslationErrorKind.TimedOut, "Offline translation took too long. Try a smaller text selection.");
                }
                catch (IOException)
                {
                    ThrowIfCanceled(cancellationToken, operation);
                    throw EngineUnavailable();
                }
                catch (Win32Exception)
                {
                    ThrowIfCanceled(cancellationToken, operation);
                    throw EngineUnavailable();
                }
                catch (ObjectDisposedException)
                {
                    ThrowIfCanceled(cancellationToken, operation);
                    throw EngineUnavailable();
                }
                catch (InvalidOperationException)
                {
                    ThrowIfCanceled(cancellationToken, operation);
                    throw EngineUnavailable();
                }
                finally
                {
                    bool exited = !started || FinishOwnedProcess(process);
                    // Keep the global slot until this exact child has exited.
                    // The rare failed-stop case is observed through its owned
                    // handle rather than spawning another overlapping engine.
                    bool deferred = !exited && entered && DeferSlotUntilExit(process);
                    if (process != null && !deferred) process.Dispose();
                    if (entered && exited) EngineSlots.Release();
                    if (!exited) throw new TranslationException(TranslationErrorKind.ServiceUnavailable,
                        "Offline translation could not stop cleanly. Wait for it to finish before trying again. Restart Snip Studio if translation remains unavailable.");
                }
            }
        }

        private LocalBundle LoadBundle()
        {
            try
            {
                if (!Directory.Exists(engineRoot) || HasUnsupportedReparsePoint(engineRoot)) throw MissingBundle();
                string runtimeDirectory = ContainedPath(engineRoot, "runtime");
                string runtime = ContainedPath(runtimeDirectory, "node.exe");
                string worker = ContainedPath(engineRoot, "worker.js");
                string manifest = ContainedPath(engineRoot, "bundle.json");
                if (!Directory.Exists(runtimeDirectory) || HasUnsupportedReparsePoint(runtimeDirectory) ||
                    !File.Exists(runtime) || !File.Exists(worker) || !File.Exists(manifest) ||
                    HasUnsupportedReparsePoint(runtime) || HasUnsupportedReparsePoint(worker) || HasUnsupportedReparsePoint(manifest)) throw MissingBundle();
                byte[] bytes = ReadManifest(manifest);
                Dictionary<string, object> contents = Serializer().DeserializeObject(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF')) as Dictionary<string, object>;
                object version, modelValues;
                if (contents == null || !contents.TryGetValue("version", out version) || !(version is int) || (int)version != 1 ||
                    !contents.TryGetValue("models", out modelValues)) throw MissingBundle();
                object[] models = modelValues as object[];
                if (models == null || models.Length != 6) throw MissingBundle();
                LocalBundle bundle = new LocalBundle { Runtime = runtime, Worker = worker };
                foreach (object value in models)
                {
                    Dictionary<string, object> model = value as Dictionary<string, object>;
                    string source = Field(model, "source"), target = Field(model, "target"), directory = Field(model, "directory");
                    string pair = source + "-" + target;
                    if (!LanguageNames.ContainsKey(source ?? "") || !LanguageNames.ContainsKey(target ?? "") ||
                        source != NormalizeLanguage(source) || target != NormalizeLanguage(target) ||
                        source == target || (source != "en" && target != "en") || directory != pair + "-tiny" || !bundle.Pairs.Add(pair)) throw MissingBundle();
                    string modelPath = ContainedPath(engineRoot, directory);
                    if (!Directory.Exists(modelPath) || HasUnsupportedReparsePoint(modelPath)) throw MissingBundle();
                    string modelInfo = ContainedPath(modelPath, "model_info.json");
                    if (!File.Exists(modelInfo) || HasUnsupportedReparsePoint(modelInfo)) throw MissingBundle();
                    object fileValues;
                    if (!model.TryGetValue("files", out fileValues)) throw MissingBundle();
                    Dictionary<string, object> files = fileValues as Dictionary<string, object>;
                    if (files == null) throw MissingBundle();
                    ValidateArtifactFile(modelPath, Field(files, "model"));
                    ValidateArtifactFile(modelPath, Field(files, "shortlist"));
                    object vocabValues;
                    if (!files.TryGetValue("vocabs", out vocabValues)) throw MissingBundle();
                    object[] vocabs = vocabValues as object[];
                    if (vocabs == null || vocabs.Length != 2) throw MissingBundle();
                    foreach (object vocab in vocabs) ValidateArtifactFile(modelPath, vocab as string);
                }
                return bundle;
            }
            catch (IOException) { throw MissingBundle(); }
            catch (UnauthorizedAccessException) { throw MissingBundle(); }
            catch (ArgumentException) { throw MissingBundle(); }
            catch (InvalidOperationException) { throw MissingBundle(); }
        }

        private static bool CanTranslate(LocalBundle bundle, string source, string target)
        {
            return bundle.Pairs.Contains(source + "-" + target) ||
                (source != "en" && target != "en" && bundle.Pairs.Contains(source + "-en") && bundle.Pairs.Contains("en-" + target));
        }

        private static string ContainedPath(string parent, string relative)
        {
            if (String.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) throw MissingBundle();
            string fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(fullParent, relative));
            if (!path.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase)) throw MissingBundle();
            return path;
        }

        private static bool HasUnsupportedReparsePoint(string path)
        {
            // A hydrated OneDrive file is also a reparse point. Inspect the tag
            // without following it; allow only ordinary files and Microsoft's
            // documented Cloud Files tags, not symlinks, junctions, app aliases,
            // or other unreviewed redirect mechanisms.
            const uint readAttributes = 0x80, shareReadWriteDelete = 7, openExisting = 3;
            const uint openReparsePointAndDirectory = 0x00200000 | 0x02000000;
            using (SafeFileHandle handle = OpenAttributeHandle(path, readAttributes, shareReadWriteDelete, IntPtr.Zero,
                openExisting, openReparsePointAndDirectory, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw MissingBundle();
                FileAttributeTagInfo information;
                if (!GetAttributeTagInformation(handle, 9, out information, 8)) throw MissingBundle();
                if ((information.Attributes & 0x400) == 0) return false;
                return !IsAllowedCloudReparseTag(information.Tag);
            }
        }

        // MS-FSCC 2.1.2.1 lists CLOUD (0x9000001A) through CLOUD_F
        // (0x9000F01A). Mask only their four-bit Cloud Files variant field.
        internal static bool IsAllowedCloudReparseTag(uint tag)
        { return (tag & 0xFFFF0FFFU) == 0x9000001AU; }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInfo { internal uint Attributes; internal uint Tag; }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle OpenAttributeHandle(string path, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetAttributeTagInformation(SafeFileHandle handle, int informationClass,
            out FileAttributeTagInfo information, uint size);

        private static void ValidateArtifactFile(string modelPath, string filename)
        {
            // Artifacts are flat files in the selected model directory. An
            // allowlist of filename characters excludes traversal, rooted paths,
            // alternate streams, separators, and shell metacharacters.
            if (String.IsNullOrEmpty(filename) || filename.Length > 128 ||
                !IsAsciiLetterOrDigit(filename[0])) throw MissingBundle();
            foreach (char character in filename)
                if (!IsAsciiLetterOrDigit(character) && character != '.' && character != '-' && character != '_') throw MissingBundle();
            if (filename.EndsWith(".", StringComparison.Ordinal)) throw MissingBundle();
            string stem = filename.Split('.')[0].ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '0' && stem[3] <= '9')) throw MissingBundle();
            string path = ContainedPath(modelPath, filename);
            if (!File.Exists(path) || HasUnsupportedReparsePoint(path) || new FileInfo(path).Length <= 0) throw MissingBundle();
        }

        private static bool IsAsciiLetterOrDigit(char value)
        { return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') || (value >= '0' && value <= '9'); }

        private static byte[] ReadManifest(string path)
        {
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (MemoryStream output = new MemoryStream())
            {
                if (input.Length <= 0 || input.Length > MaximumManifestBytes) throw MissingBundle();
                byte[] chunk = new byte[4096];
                while (true)
                {
                    int count = input.Read(chunk, 0, chunk.Length);
                    if (count == 0) break;
                    if (output.Length + count > MaximumManifestBytes) throw MissingBundle();
                    output.Write(chunk, 0, count);
                }
                return output.ToArray();
            }
        }

        private CancellationTokenSource LinkLifetime(CancellationToken cancellationToken)
        {
            lock (stateGate)
            {
                if (disposed) throw new ObjectDisposedException("OfflineTranslationService");
                return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeCancellation.Token);
            }
        }

        private bool IsDisposed() { lock (stateGate) return disposed; }

        private void ThrowIfCanceled(CancellationToken cancellationToken, CancellationTokenSource operation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisposed()) throw new OperationCanceledException("Offline translation was closed.");
            if (operation.IsCancellationRequested) throw new TranslationException(TranslationErrorKind.TimedOut, "Offline translation took too long. Try a smaller text selection.");
        }

        private static async Task WriteFrameAsync(Stream stream, byte[] bytes, CancellationToken token)
        {
            byte[] header = new byte[] { (byte)bytes.Length, (byte)(bytes.Length >> 8), (byte)(bytes.Length >> 16), (byte)(bytes.Length >> 24) };
            await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        private static async Task AwaitWorkerStepAsync(Task step, Task errors, Task canceled, CancellationToken token)
        {
            // Anonymous-pipe streams on older Framework builds can ignore the
            // ReadAsync/WriteAsync token once their synchronous I/O has begun.
            // Race the owned operation explicitly, so even a failed child kill
            // cannot leave this request waiting indefinitely for a pipe read.
            Task first = await Task.WhenAny(step, errors, canceled).ConfigureAwait(false);
            if (Object.ReferenceEquals(first, errors))
            {
                await errors.ConfigureAwait(false);
                await Task.WhenAny(step, canceled).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            await step.ConfigureAwait(false);
            if (errors.IsFaulted) await errors.ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
        {
            byte[] header = new byte[4];
            await ReadExactlyAsync(stream, header, token).ConfigureAwait(false);
            uint length = (uint)header[0] | ((uint)header[1] << 8) | ((uint)header[2] << 16) | ((uint)header[3] << 24);
            if (length == 0) throw InvalidResponse();
            if (length > MaximumFrameBytes) throw new TranslationException(TranslationErrorKind.ResponseTooLarge, "The offline translation engine returned too much data. Try a smaller selection.");
            byte[] payload = new byte[(int)length];
            await ReadExactlyAsync(stream, payload, token).ConfigureAwait(false);
            return payload;
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] bytes, CancellationToken token)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                if (count == 0) { token.ThrowIfCancellationRequested(); throw InvalidResponse(); }
                offset += count;
            }
            token.ThrowIfCancellationRequested();
        }

        private static async Task DrainErrorAsync(Stream stream, CancellationToken token)
        {
            byte[] chunk = new byte[8192];
            int total = 0;
            while (true)
            {
                int count = await stream.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false);
                if (count == 0) return;
                total += count;
                if (total > MaximumErrorBytes) throw new TranslationException(TranslationErrorKind.ResponseTooLarge, "The offline translation engine could not finish this request. Try a smaller selection.");
            }
        }

        private static TranslationResult ParseResponse(byte[] payload, int requestId, string source, string target)
        {
            try
            {
                Dictionary<string, object> response = Serializer().DeserializeObject(new UTF8Encoding(false, true).GetString(payload)) as Dictionary<string, object>;
                object id, success, value;
                if (response == null || !response.TryGetValue("id", out id) || !(id is int) || (int)id != requestId ||
                    !response.TryGetValue("success", out success) || !(success is bool)) throw InvalidResponse();
                if (!(bool)success) throw EngineUnavailable();
                if (!response.TryGetValue("data", out value)) throw InvalidResponse();
                Dictionary<string, object> data = value as Dictionary<string, object>;
                if (data == null || !data.TryGetValue("target", out value)) throw InvalidResponse();
                Dictionary<string, object> translated = value as Dictionary<string, object>;
                string text = Field(translated, "text");
                if (text == null) throw InvalidResponse();
                return new TranslationResult(text, source, target, null, "Offline translation (Bergamot)");
            }
            catch (ArgumentException) { throw InvalidResponse(); }
            catch (InvalidOperationException) { throw InvalidResponse(); }
        }

        private static void StopOwnedProcess(Process process)
        {
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }

        private static bool FinishOwnedProcess(Process process)
        {
            if (process == null) return true;
            // Do not close/flush stdin while a canceled anonymous-pipe write
            // might still be blocked. Stop this owned worker first; stream
            // disposal happens after confirmed exit (or its later exit event).
            StopOwnedProcess(process);
            try { return process.HasExited || process.WaitForExit(2000); }
            catch (InvalidOperationException) { return true; }
            catch (Win32Exception) { return false; }
        }

        private sealed class DeferredWorkerCleanup
        {
            private readonly Process process;
            private int state;
            internal DeferredWorkerCleanup(Process process) { this.process = process; }
            internal void Attach()
            {
                process.Exited += OnExited;
                process.EnableRaisingEvents = true;
                if (process.HasExited) OnExited(process, EventArgs.Empty);
            }
            internal bool Abandon()
            {
                if (Interlocked.CompareExchange(ref state, -1, 0) != 0) return true;
                process.Exited -= OnExited;
                return false;
            }
            private void OnExited(object sender, EventArgs e)
            {
                if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
                try { process.Exited -= OnExited; }
                finally { process.Dispose(); EngineSlots.Release(); }
            }
        }

        private static bool DeferSlotUntilExit(Process process)
        {
            DeferredWorkerCleanup cleanup = new DeferredWorkerCleanup(process);
            try { cleanup.Attach(); return true; }
            catch (InvalidOperationException) { return cleanup.Abandon(); }
            catch (Win32Exception) { return cleanup.Abandon(); }
        }

        private static void ObserveFault(Task task)
        {
            task.ContinueWith(delegate(Task completed) { completed.Exception.Handle(delegate(Exception ignored) { return true; }); }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static JavaScriptSerializer Serializer()
        { return new JavaScriptSerializer { MaxJsonLength = MaximumFrameBytes, RecursionLimit = 12 }; }

        private static string NormalizeLanguage(string code) { return (code ?? "").Trim().ToLowerInvariant(); }
        private static List<string> SnapshotSegments(IList<string> segments)
        {
            if (segments == null) throw InvalidRequest("There is no recognized text to translate.");
            int count = segments.Count;
            if (count <= 0) throw InvalidRequest("There is no recognized text to translate.");
            if (count > MaximumSegments) throw InvalidRequest("Select at most 256 lines to translate at once. Your text has not been changed.");
            List<string> snapshot = new List<string>(count);
            long totalCharacters = 0;
            foreach (string segment in segments)
            {
                if (segment == null) throw InvalidRequest("Some selected text is missing. Select the text again before translating.");
                totalCharacters += segment.Length;
                if (totalCharacters > MaximumTextLength) throw InvalidRequest("Select at most 16,000 characters to translate at once. Your text has not been changed.");
                if (!HasCompleteUnicodeCharacters(segment)) throw InvalidRequest("The selected text contains an incomplete Unicode character. Select the text again before translating.");
                snapshot.Add(segment);
                if (snapshot.Count > MaximumSegments) throw InvalidRequest("Select at most 256 lines to translate at once. Your text has not been changed.");
            }
            if (snapshot.Count == 0) throw InvalidRequest("There is no recognized text to translate.");
            return snapshot;
        }

        private static bool HasCompleteUnicodeCharacters(string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                if (!Char.IsSurrogate(text[index])) continue;
                if (!Char.IsHighSurrogate(text[index]) || index + 1 >= text.Length || !Char.IsLowSurrogate(text[index + 1])) return false;
                index++;
            }
            return true;
        }
        private static string Field(Dictionary<string, object> value, string key)
        { object field; return value != null && value.TryGetValue(key, out field) ? field as string : null; }

        private static Dictionary<string, string> CreateLanguageNames()
        {
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            names.Add("en", "English"); names.Add("de", "German"); names.Add("es", "Spanish"); names.Add("fr", "French");
            return names;
        }

        private static TranslationException InvalidRequest(string message)
        { return new TranslationException(TranslationErrorKind.InvalidRequest, message); }
        private static TranslationException MissingBundle()
        { return new TranslationException(TranslationErrorKind.ServiceUnavailable, "Offline translation files are missing or incomplete. Reinstall Snip Studio to restore its translation engine and language packs."); }
        private static TranslationException EngineUnavailable()
        { return new TranslationException(TranslationErrorKind.ServiceUnavailable, "The offline translation engine could not finish. Your source text has been kept. Try a smaller selection, or reinstall Snip Studio if the problem continues."); }
        private static TranslationException InvalidResponse()
        { return new TranslationException(TranslationErrorKind.InvalidResponse, "The offline translation engine returned an unexpected response. Your source text has been kept."); }

        public void Dispose()
        {
            lock (stateGate)
            {
                if (disposed) return;
                disposed = true;
                lifetimeCancellation.Cancel();
                lifetimeCancellation.Dispose();
            }
        }
    }
}
