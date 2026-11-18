using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using CodexQuotaTray.Compatibility;

namespace CodexQuotaTray
{
    internal sealed class LanguageServerCandidate
    {
        public uint Pid { get; set; }
        public string CsrfToken { get; set; }
        public bool EnableLsp { get; set; }
        public List<ushort> Ports { get; set; } = new List<ushort>();
    }

    internal sealed class SessionQuotaReader
    {
        internal const string NotRunningMessage = "Antigravity 未运行";
        internal const string UnreadableProcessMessage = "无法读取 Antigravity 服务参数（请保持相同运行权限）";
        private const string ServicePath = "/exa.language_server_pb.LanguageServerService/";
        private const string UserStatusRequestBody = "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}";
        private const int MaxPortsPerProcess = 6;
        private const int MaximumCommandLineChars = 32768;
        private const int TailBytes = 768 * 1024;

        private static readonly string[] LanguageServerExeNames =
        {
            "language_server_windows_x64.exe",
            "language_server_windows_arm.exe",
            "language_server_windows_arm64.exe",
            "language_server_windows.exe"
        };

        private static readonly HttpClient LoopbackHttpClient = CreateLoopbackHttpClient();

        private readonly Func<bool> _processDetector;
        private readonly string _liveQuotaHistoryPath;
        private List<QuotaUsageSample> _liveQuotaHistory;

        public SessionQuotaReader()
            : this(null)
        {
        }

        internal SessionQuotaReader(Func<bool> processDetector)
        {
            _processDetector = processDetector;
            _liveQuotaHistoryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AntigravityMeter", "quota-history-v1.tsv");
        }

        public QuotaReadResult ReadLatest()
        {
            var result = new QuotaReadResult();

            try
            {
                bool running;
                bool keepNormalPace;
                string failure;
                var latest = ReadLatestFromLocalLanguageServer(out running, out keepNormalPace, out failure);
                result.IsCodexRunning = _processDetector != null ? _processDetector() : running;
                result.Snapshot = latest;
                result.KeepNormalPollInterval = keepNormalPace;
                if (latest != null) RecordLiveQuotaSnapshot(latest);
                result.QuotaHistory = GetCombinedQuotaHistory();
                if (latest == null)
                {
                    result.Error = failure ?? "读取额度时出现未知错误";
                }
            }
            catch (Exception)
            {
                result.Error = "读取额度时出现未知错误";
            }

            return result;
        }

        private static HttpClient CreateLoopbackHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
            // Every URI passed to LoopbackHttpClient is built from 127.0.0.1 in BuildLoopbackRpcUri;
            // the local Antigravity language server uses a self-signed localhost certificate.
            handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true;
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        }

        private static Uri BuildLoopbackRpcUri(bool useHttps, ushort port, string rpcMethod)
        {
            return new Uri((useHttps ? "https://127.0.0.1:" : "http://127.0.0.1:") +
                port.ToString(CultureInfo.InvariantCulture) + ServicePath + rpcMethod);
        }

        private static QuotaSnapshot ReadLatestFromLocalLanguageServer(
            out bool isRunning,
            out bool keepNormalPace,
            out string failure)
        {
            isRunning = false;
            keepNormalPace = false;
            failure = null;

            bool unreadableFound;
            var candidates = DiscoverLanguageServers(out unreadableFound);
            if (candidates.Count == 0)
            {
                if (unreadableFound)
                {
                    failure = UnreadableProcessMessage;
                    return null;
                }
                keepNormalPace = true;
                failure = NotRunningMessage;
                return null;
            }

            isRunning = true;
            SortLanguageServerCandidates(candidates);

            bool anyPortFound = false;
            bool unsupportedSummary = false;
            string lastError = null;

            foreach (var candidate in candidates)
            {
                var portCount = Math.Min(candidate.Ports.Count, MaxPortsPerProcess);
                for (int i = 0; i < portCount; i++)
                {
                    anyPortFound = true;
                    var port = candidate.Ports[i];
                    QuotaSnapshot snapshot;
                    bool methodMissing;
                    string portError;
                    if (TryQueryCandidatePort(port, candidate.CsrfToken, out snapshot, out methodMissing, out portError))
                    {
                        return snapshot;
                    }
                    if (methodMissing) unsupportedSummary = true;
                    if (!string.IsNullOrWhiteSpace(portError)) lastError = portError;
                }
            }

            if (!anyPortFound)
            {
                keepNormalPace = true;
                failure = "Antigravity 正在启动，暂未监听本地端口";
                return null;
            }

            failure = unsupportedSummary
                ? "当前 Antigravity 版本不支持本地额度汇总接口"
                : (lastError ?? "无法从本地 Antigravity 服务读取额度");
            return null;
        }

        private static bool TryQueryCandidatePort(
            ushort port,
            string csrfToken,
            out QuotaSnapshot snapshot,
            out bool methodMissing,
            out string error)
        {
            snapshot = null;
            methodMissing = false;
            error = null;

            foreach (var useHttps in new[] { true, false })
            {
                int status;
                string body;
                if (!TryPostLoopbackRpc(useHttps, port, csrfToken, "RetrieveUserQuotaSummary", "{}", out status, out body))
                {
                    continue;
                }

                if (status >= 200 && status < 300)
                {
                    if (TryParseAntigravityQuotaSummary(body, out snapshot)) return true;
                    error = "Antigravity 返回的额度数据无法识别";
                    return false;
                }

                if (status == 404 || status == 501)
                {
                    int fallbackStatus;
                    string fallbackBody;
                    if (TryPostLoopbackRpc(useHttps, port, csrfToken, "GetUserStatus", UserStatusRequestBody, out fallbackStatus, out fallbackBody) &&
                        fallbackStatus >= 200 && fallbackStatus < 300 &&
                        TryParseAntigravityUserStatusFallback(fallbackBody, out snapshot))
                    {
                        return true;
                    }
                    methodMissing = true;
                    return false;
                }

                error = status == 401 || status == 403
                    ? "Antigravity 本地服务未登录或令牌未就绪"
                    : "Antigravity 本地服务返回错误 (" + status.ToString(CultureInfo.InvariantCulture) + ")";
                return false;
            }

            return false;
        }

        private static bool TryPostLoopbackRpc(
            bool useHttps,
            ushort port,
            string csrfToken,
            string rpcMethod,
            string jsonBody,
            out int statusCode,
            out string responseBody)
        {
            statusCode = 0;
            responseBody = null;
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, BuildLoopbackRpcUri(useHttps, port, rpcMethod)))
                {
                    request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
                    request.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrfToken);
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    using (var response = LoopbackHttpClient.SendAsync(request).GetAwaiter().GetResult())
                    {
                        statusCode = (int)response.StatusCode;
                        responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryParseAntigravityQuotaSummary(string json, out QuotaSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) return false;

                var response = AsDictionary(Get(root, "response")) ?? root;
                var groups = Get(response, "groups") as System.Collections.IEnumerable;
                if (groups == null || groups is string) return false;

                var parsed = new QuotaSnapshot
                {
                    CapturedAt = DateTimeOffset.Now,
                    LimitId = "antigravity"
                };

                foreach (var groupItem in groups)
                {
                    var group = AsDictionary(groupItem);
                    if (group == null) continue;
                    var groupDisplayName = AsString(Get(group, "displayName", "name")) ?? string.Empty;
                    var buckets = Get(group, "buckets") as System.Collections.IEnumerable;
                    if (buckets == null || buckets is string) continue;

                    foreach (var bucketItem in buckets)
                    {
                        var bucket = AsDictionary(bucketItem);
                        if (bucket == null) continue;

                        bool disabled;
                        if (TryBool(Get(bucket, "disabled"), out disabled) && disabled) continue;

                        double remainingFraction;
                        if (!TryDouble(Get(bucket, "remainingFraction", "remaining_fraction"), out remainingFraction)) continue;
                        if (double.IsNaN(remainingFraction) || double.IsInfinity(remainingFraction) ||
                            remainingFraction < 0d || remainingFraction > 1d)
                        {
                            continue;
                        }

                        var bucketId = AsString(Get(bucket, "bucketId", "id")) ?? string.Empty;
                        var windowName = AsString(Get(bucket, "window")) ?? string.Empty;
                        var pool = ClassifyPool(bucketId, groupDisplayName);
                        var minutes = ParseAntigravityWindowMinutes(windowName, bucketId);
                        var kind = ClassifyWindow(minutes);
                        if (pool == QuotaPoolKind.Default || kind == QuotaWindowKind.Other) continue;
                        if (parsed.FindPoolWindow(pool, kind) != null) continue;

                        var untouchedFull = remainingFraction >= 1.0d;
                        DateTimeOffset? resetTime = null;
                        // At remainingFraction == 1, resetTime is a sliding placeholder rather than a real deadline.
                        if (!untouchedFull)
                        {
                            var parsedReset = ParseTimestamp(Get(bucket, "resetTime", "reset_time"));
                            if (parsedReset != DateTimeOffset.MinValue) resetTime = parsedReset;
                        }

                        var poolPrefix = pool == QuotaPoolKind.Gemini ? "gemini" : "thirdparty";
                        parsed.Windows.Add(new QuotaWindow
                        {
                            Pool = pool,
                            Name = (pool == QuotaPoolKind.Gemini ? "Gemini" : "第三方") + " · " + (kind == QuotaWindowKind.FiveHour ? "5h" : "Weekly"),
                            Key = poolPrefix + ":" + minutes.ToString(CultureInfo.InvariantCulture),
                            Kind = kind,
                            WindowMinutes = minutes,
                            UsedPercent = Math.Max(0d, Math.Min(100d, (1.0d - remainingFraction) * 100.0d)),
                            ResetsAt = resetTime,
                            IsUntouchedFull = untouchedFull
                        });
                    }
                }

                if (parsed.Windows.Count == 0) return false;
                snapshot = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryParseAntigravityUserStatusFallback(string json, out QuotaSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                var userStatus = AsDictionary(Get(root, "userStatus"));
                var cascade = AsDictionary(Get(userStatus, "cascadeModelConfigData"));
                var configs = Get(cascade, "clientModelConfigs") as System.Collections.IEnumerable;
                if (configs == null || configs is string) return false;

                QuotaWindow gemini = null;
                QuotaWindow thirdParty = null;

                foreach (var item in configs)
                {
                    var config = AsDictionary(item);
                    var quotaInfo = AsDictionary(Get(config, "quotaInfo"));
                    double remainingFraction;
                    if (quotaInfo == null || !TryDouble(Get(quotaInfo, "remainingFraction"), out remainingFraction)) continue;
                    if (double.IsNaN(remainingFraction) || double.IsInfinity(remainingFraction) ||
                        remainingFraction < 0d || remainingFraction > 1d)
                    {
                        continue;
                    }

                    var label = AsString(Get(config, "label")) ?? string.Empty;
                    var modelAlias = AsDictionary(Get(config, "modelOrAlias"));
                    var modelId = AsString(Get(modelAlias, "model")) ?? string.Empty;
                    var combinedName = label + " " + modelId;

                    QuotaPoolKind pool;
                    if (combinedName.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) >= 0)
                        pool = QuotaPoolKind.Gemini;
                    else if (combinedName.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             combinedName.IndexOf("gpt", StringComparison.OrdinalIgnoreCase) >= 0)
                        pool = QuotaPoolKind.ThirdParty;
                    else
                        continue;

                    var untouchedFull = remainingFraction >= 1.0d;
                    DateTimeOffset? reset = null;
                    if (!untouchedFull)
                    {
                        var parsedReset = ParseTimestamp(Get(quotaInfo, "resetTime"));
                        if (parsedReset != DateTimeOffset.MinValue) reset = parsedReset;
                    }

                    var candidate = new QuotaWindow
                    {
                        Pool = pool,
                        Name = (pool == QuotaPoolKind.Gemini ? "Gemini" : "第三方") + " · 5h",
                        Key = (pool == QuotaPoolKind.Gemini ? "gemini:300" : "thirdparty:300"),
                        Kind = QuotaWindowKind.FiveHour,
                        WindowMinutes = 300,
                        UsedPercent = Math.Max(0d, Math.Min(100d, (1.0d - remainingFraction) * 100.0d)),
                        ResetsAt = reset,
                        IsUntouchedFull = untouchedFull
                    };

                    if (pool == QuotaPoolKind.Gemini)
                    {
                        if (gemini == null || candidate.UsedPercent > gemini.UsedPercent) gemini = candidate;
                    }
                    else
                    {
                        if (thirdParty == null || candidate.UsedPercent > thirdParty.UsedPercent) thirdParty = candidate;
                    }
                }

                if (gemini == null && thirdParty == null) return false;
                var parsed = new QuotaSnapshot { CapturedAt = DateTimeOffset.Now, LimitId = "antigravity" };
                if (gemini != null) parsed.Windows.Add(gemini);
                if (thirdParty != null) parsed.Windows.Add(thirdParty);
                snapshot = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static QuotaPoolKind ClassifyPool(string bucketId, string groupDisplayName)
        {
            if (!string.IsNullOrWhiteSpace(bucketId))
            {
                if (bucketId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)) return QuotaPoolKind.Gemini;
                if (bucketId.StartsWith("3p", StringComparison.OrdinalIgnoreCase) ||
                    bucketId.StartsWith("third", StringComparison.OrdinalIgnoreCase))
                {
                    return QuotaPoolKind.ThirdParty;
                }
            }

            if (!string.IsNullOrWhiteSpace(groupDisplayName))
            {
                if (groupDisplayName.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) >= 0)
                    return QuotaPoolKind.Gemini;
                if (groupDisplayName.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    groupDisplayName.IndexOf("gpt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    groupDisplayName.IndexOf("third", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    groupDisplayName.IndexOf("other", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return QuotaPoolKind.ThirdParty;
                }
            }

            return QuotaPoolKind.Default;
        }

        private static int ParseAntigravityWindowMinutes(string windowName, string bucketId)
        {
            if (string.Equals(windowName, "5h", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(bucketId) && bucketId.EndsWith("-5h", StringComparison.OrdinalIgnoreCase)))
            {
                return 300;
            }
            if (string.Equals(windowName, "weekly", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(bucketId) && bucketId.EndsWith("-weekly", StringComparison.OrdinalIgnoreCase)))
            {
                return 10080;
            }
            return 0;
        }

        internal static bool IsAntigravityLanguageServerExe(string exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName)) return false;
            var fileName = Path.GetFileName(exeName.Trim());
            foreach (var known in LanguageServerExeNames)
            {
                if (string.Equals(fileName, known, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        internal static void SortLanguageServerCandidates(List<LanguageServerCandidate> candidates)
        {
            if (candidates == null) return;
            // Prefer the global non-LSP language server over workspace-scoped LSP children, then lower PID.
            candidates.Sort((a, b) =>
            {
                var lspCompare = a.EnableLsp.CompareTo(b.EnableLsp);
                return lspCompare != 0 ? lspCompare : a.Pid.CompareTo(b.Pid);
            });
        }

        internal static bool TryParseLanguageServerCommandLine(
            string commandLine,
            out string csrfToken,
            out bool enableLsp)
        {
            csrfToken = null;
            enableLsp = false;
            if (string.IsNullOrWhiteSpace(commandLine)) return false;

            var tokens = TokenizeCommandLine(commandLine);
            if (tokens.Count == 0) return false;

            string appDataDir = FindFlagValue(tokens, "--app_data_dir");
            if (appDataDir != null)
            {
                var normalized = appDataDir.Trim().TrimEnd('\\', '/');
                var leaf = Path.GetFileName(normalized);
                if (string.IsNullOrEmpty(leaf)) leaf = normalized;
                if (!string.Equals(leaf, "antigravity", StringComparison.OrdinalIgnoreCase) &&
                    !leaf.StartsWith("antigravity-", StringComparison.OrdinalIgnoreCase) &&
                    !leaf.StartsWith("antigravity_", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else
            {
                if (tokens[0].IndexOf("antigravity", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            var csrf = FindFlagValue(tokens, "--csrf_token");
            if (string.IsNullOrWhiteSpace(csrf) || csrf.Length < 16 || csrf.Length > 128) return false;
            foreach (var ch in csrf)
            {
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z') ||
                      (ch >= 'A' && ch <= 'Z') || ch == '-' || ch == '_'))
                {
                    return false;
                }
            }

            foreach (var token in tokens)
            {
                if (string.Equals(token, "--enable_lsp", StringComparison.Ordinal))
                {
                    enableLsp = true;
                    break;
                }
                if (token.StartsWith("--enable_lsp=", StringComparison.Ordinal))
                {
                    var val = token.Substring("--enable_lsp=".Length);
                    enableLsp = !string.Equals(val, "false", StringComparison.OrdinalIgnoreCase) && val != "0";
                    break;
                }
            }

            csrfToken = csrf;
            return true;
        }

        private static string FindFlagValue(List<string> tokens, string flag)
        {
            var prefix = flag + "=";
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (string.Equals(token, flag, StringComparison.Ordinal))
                {
                    return i + 1 < tokens.Count ? tokens[i + 1] : null;
                }
                if (token.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return token.Substring(prefix.Length);
                }
            }
            return null;
        }

        private static List<string> TokenizeCommandLine(string commandLine)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;
            bool hasToken = false;

            for (int i = 0; i < commandLine.Length; i++)
            {
                char ch = commandLine[i];
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    hasToken = true;
                    continue;
                }
                if (!inQuotes && char.IsWhiteSpace(ch))
                {
                    if (hasToken)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                        hasToken = false;
                    }
                    continue;
                }
                current.Append(ch);
                hasToken = true;
            }

            if (hasToken) result.Add(current.ToString());
            return result;
        }

        private static List<LanguageServerCandidate> DiscoverLanguageServers(out bool unreadableFound)
        {
            unreadableFound = false;
            var candidates = new List<LanguageServerCandidate>();
            if (!OperatingSystem.IsWindows()) return candidates;

            var pids = EnumerateLanguageServerPids();
            if (pids.Count == 0) return candidates;

            var portsByPid = GetLoopbackListeningPortsByPid();
            foreach (var pid in pids)
            {
                bool accessDenied;
                var commandLine = TryReadProcessCommandLine(pid, out accessDenied);
                if (string.IsNullOrWhiteSpace(commandLine))
                {
                    if (accessDenied) unreadableFound = true;
                    continue;
                }

                string csrf;
                bool enableLsp;
                if (!TryParseLanguageServerCommandLine(commandLine, out csrf, out enableLsp)) continue;

                List<ushort> ports;
                if (!portsByPid.TryGetValue(pid, out ports)) ports = new List<ushort>();
                candidates.Add(new LanguageServerCandidate
                {
                    Pid = pid,
                    CsrfToken = csrf,
                    EnableLsp = enableLsp,
                    Ports = ports
                });
            }

            return candidates;
        }

        private static List<uint> EnumerateLanguageServerPids()
        {
            var pids = new List<uint>();
            IntPtr snapshot = IntPtr.Zero;
            try
            {
                snapshot = CreateToolhelp32Snapshot(0x00000002u /* TH32CS_SNAPPROCESS */, 0);
                if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return pids;

                var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (Process32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        if (IsAntigravityLanguageServerExe(entry.szExeFile))
                        {
                            pids.Add(entry.th32ProcessID);
                        }
                    }
                    while (Process32NextW(snapshot, ref entry));
                }
            }
            catch
            {
            }
            finally
            {
                if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1)) CloseHandle(snapshot);
            }
            return pids;
        }

        private static string TryReadProcessCommandLine(uint pid, out bool accessDenied)
        {
            accessDenied = false;
            IntPtr process = IntPtr.Zero;
            try
            {
                process = OpenProcess(0x1000u /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
                if (process == IntPtr.Zero)
                {
                    accessDenied = Marshal.GetLastWin32Error() == 5 /* ERROR_ACCESS_DENIED */;
                    return null;
                }

                var direct = TryReadCommandLineViaNtInfo(process);
                if (!string.IsNullOrEmpty(direct)) return direct;
            }
            catch
            {
            }
            finally
            {
                if (process != IntPtr.Zero) CloseHandle(process);
            }

            if (IntPtr.Size != 8) return null;

            IntPtr vmProcess = IntPtr.Zero;
            try
            {
                vmProcess = OpenProcess(0x0400u | 0x0010u /* PROCESS_QUERY_INFORMATION | PROCESS_VM_READ */, false, pid);
                if (vmProcess == IntPtr.Zero)
                {
                    if (Marshal.GetLastWin32Error() == 5) accessDenied = true;
                    return null;
                }
                return TryReadCommandLineViaPeb64(vmProcess);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (vmProcess != IntPtr.Zero) CloseHandle(vmProcess);
            }
        }

        private static string TryReadCommandLineViaNtInfo(IntPtr process)
        {
            const int ProcessCommandLineInformation = 60;
            uint needed = 0;
            NtQueryInformationProcess(process, ProcessCommandLineInformation, IntPtr.Zero, 0, out needed);
            if (needed == 0 || needed > MaximumCommandLineChars * 2 + 64) return null;

            uint alloc = needed + 64;
            IntPtr buffer = Marshal.AllocHGlobal((int)alloc);
            try
            {
                uint written;
                int status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, alloc, out written);
                if (status != 0) return null;

                int byteLength = (ushort)Marshal.ReadInt16(buffer, 0);
                if (byteLength <= 0 || byteLength > MaximumCommandLineChars * 2) return null;
                IntPtr stringPtr = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                if (stringPtr == IntPtr.Zero) return null;
                return Marshal.PtrToStringUni(stringPtr, byteLength / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string TryReadCommandLineViaPeb64(IntPtr process)
        {
            IntPtr pbiBuffer = Marshal.AllocHGlobal(48);
            try
            {
                uint written;
                if (NtQueryInformationProcess(process, 0 /* ProcessBasicInformation */, pbiBuffer, 48, out written) != 0)
                    return null;

                IntPtr pebAddress = Marshal.ReadIntPtr(pbiBuffer, 8);
                if (pebAddress == IntPtr.Zero) return null;

                IntPtr processParameters;
                if (!TryReadRemoteIntPtr(process, IntPtr.Add(pebAddress, 0x20), out processParameters) ||
                    processParameters == IntPtr.Zero)
                {
                    return null;
                }

                byte[] unicodeHeader = new byte[16];
                IntPtr bytesRead;
                if (!ReadProcessMemory(process, IntPtr.Add(processParameters, 0x70), unicodeHeader, (IntPtr)16, out bytesRead) ||
                    bytesRead.ToInt64() < 16)
                {
                    return null;
                }

                int byteLength = BitConverter.ToUInt16(unicodeHeader, 0);
                if (byteLength <= 0 || byteLength > MaximumCommandLineChars * 2) return null;
                IntPtr cmdBuffer = new IntPtr(BitConverter.ToInt64(unicodeHeader, 8));
                if (cmdBuffer == IntPtr.Zero) return null;

                byte[] rawChars = new byte[byteLength];
                if (!ReadProcessMemory(process, cmdBuffer, rawChars, (IntPtr)byteLength, out bytesRead) ||
                    bytesRead.ToInt64() < byteLength)
                {
                    return null;
                }

                return Encoding.Unicode.GetString(rawChars);
            }
            finally
            {
                Marshal.FreeHGlobal(pbiBuffer);
            }
        }

        private static bool TryReadRemoteIntPtr(IntPtr process, IntPtr address, out IntPtr value)
        {
            value = IntPtr.Zero;
            byte[] bytes = new byte[8];
            IntPtr read;
            if (!ReadProcessMemory(process, address, bytes, (IntPtr)8, out read) || read.ToInt64() < 8) return false;
            value = new IntPtr(BitConverter.ToInt64(bytes, 0));
            return true;
        }

        private static Dictionary<uint, List<ushort>> GetLoopbackListeningPortsByPid()
        {
            var result = new Dictionary<uint, List<ushort>>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2 /* AF_INET */, 3 /* TCP_TABLE_OWNER_PID_LISTENER */, 0);
            if (size <= 0 || size > 16 * 1024 * 1024) return result;

            int alloc = size + 4096;
            IntPtr buffer = Marshal.AllocHGlobal(alloc);
            try
            {
                if (GetExtendedTcpTable(buffer, ref alloc, false, 2, 3, 0) != 0) return result;
                int entries = Marshal.ReadInt32(buffer, 0);
                int maxEntries = Math.Max(0, (alloc - 4) / 24);
                int count = Math.Min(entries, maxEntries);

                for (int i = 0; i < count; i++)
                {
                    int row = 4 + i * 24;
                    uint localAddr = unchecked((uint)Marshal.ReadInt32(buffer, row + 4));
                    // Only loopback (127.0.0.1 = 0x0100007F in little-endian) or wildcard (0.0.0.0).
                    if (localAddr != 0x0100007Fu && localAddr != 0u) continue;

                    uint rawPort = unchecked((uint)Marshal.ReadInt32(buffer, row + 8));
                    uint owningPid = unchecked((uint)Marshal.ReadInt32(buffer, row + 20));
                    ushort port = (ushort)(((rawPort & 0xFFu) << 8) | ((rawPort >> 8) & 0xFFu));
                    if (port == 0) continue;

                    List<ushort> list;
                    if (!result.TryGetValue(owningPid, out list))
                    {
                        list = new List<ushort>();
                        result[owningPid] = list;
                    }
                    if (!list.Contains(port)) list.Add(port);
                }

                foreach (var pair in result)
                {
                    pair.Value.Sort();
                }
            }
            catch
            {
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return result;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public UIntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            [Out] byte[] lpBuffer,
            IntPtr nSize,
            out IntPtr lpNumberOfBytesRead);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr ProcessHandle,
            int ProcessInformationClass,
            IntPtr ProcessInformation,
            uint ProcessInformationLength,
            out uint ReturnLength);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable,
            ref int pdwSize,
            bool bOrder,
            int ulAf,
            int TableClass,
            uint Reserved);

        private List<QuotaUsageSample> GetCombinedQuotaHistory()
        {
            EnsureLiveQuotaHistoryLoaded();
            var cutoff = DateTimeOffset.Now.AddDays(-31);
            return _liveQuotaHistory
                .Where(item => item.CapturedAt >= cutoff)
                .GroupBy(item => new { item.CapturedAt, item.WindowKey, item.WindowMinutes })
                .Select(group => group.Last())
                .OrderBy(item => item.CapturedAt)
                .ToList();
        }

        private void RecordLiveQuotaSnapshot(QuotaSnapshot snapshot)
        {
            EnsureLiveQuotaHistoryLoaded();
            foreach (var window in snapshot.Windows)
            {
                var sample = new QuotaUsageSample
                {
                    CapturedAt = snapshot.CapturedAt,
                    WindowKey = window.Key,
                    WindowKind = window.Kind,
                    WindowMinutes = window.WindowMinutes,
                    RemainingPercent = window.RemainingPercent,
                    ResetsAt = window.ResetsAt
                };
                var previous = _liveQuotaHistory.LastOrDefault(item =>
                    string.Equals(item.WindowKey, sample.WindowKey, StringComparison.OrdinalIgnoreCase) &&
                    item.WindowMinutes == sample.WindowMinutes);
                if (!ShouldRecordQuotaSample(previous, sample)) continue;

                _liveQuotaHistory.Add(sample);
                try
                {
                    var directory = Path.GetDirectoryName(_liveQuotaHistoryPath);
                    if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                    File.AppendAllText(_liveQuotaHistoryPath, SerializeQuotaHistoryLine(sample) + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                }
            }
        }

        private void EnsureLiveQuotaHistoryLoaded()
        {
            if (_liveQuotaHistory != null) return;
            _liveQuotaHistory = new List<QuotaUsageSample>();
            try
            {
                if (!File.Exists(_liveQuotaHistoryPath)) return;
                var cutoff = DateTimeOffset.Now.AddDays(-31);
                foreach (var line in File.ReadLines(_liveQuotaHistoryPath, Encoding.UTF8))
                {
                    QuotaUsageSample sample;
                    if (TryParseQuotaHistoryLine(line, out sample) && sample.CapturedAt >= cutoff)
                        _liveQuotaHistory.Add(sample);
                }
                _liveQuotaHistory = _liveQuotaHistory.OrderBy(item => item.CapturedAt).ToList();
            }
            catch
            {
                _liveQuotaHistory.Clear();
            }
        }

        internal static bool ShouldRecordQuotaSample(QuotaUsageSample previous, QuotaUsageSample current)
        {
            if (current == null) return false;
            if (previous == null) return true;
            if (previous.RemainingPercent != current.RemainingPercent) return true;
            if (previous.ResetsAt != current.ResetsAt) return true;
            return current.CapturedAt - previous.CapturedAt >= TimeSpan.FromMinutes(5);
        }

        internal static string SerializeQuotaHistoryLine(QuotaUsageSample sample)
        {
            var reset = sample.ResetsAt.HasValue ? sample.ResetsAt.Value.ToUnixTimeSeconds() : 0;
            return string.Join("\t", new[]
            {
                sample.CapturedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                sample.WindowKey ?? "",
                ((int)sample.WindowKind).ToString(CultureInfo.InvariantCulture),
                sample.WindowMinutes.ToString(CultureInfo.InvariantCulture),
                sample.RemainingPercent.ToString(CultureInfo.InvariantCulture),
                reset.ToString(CultureInfo.InvariantCulture)
            });
        }

        internal static bool TryParseQuotaHistoryLine(string line, out QuotaUsageSample sample)
        {
            sample = null;
            if (string.IsNullOrWhiteSpace(line)) return false;
            var parts = line.Split('\t');
            long capturedAt;
            int kind;
            int minutes;
            int remaining;
            long resetsAt;
            if (parts.Length != 6 ||
                !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out capturedAt) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out kind) ||
                !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) ||
                !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out remaining) ||
                !long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out resetsAt) ||
                capturedAt <= 0 || minutes <= 0 || remaining < 0 || remaining > 100 ||
                !Enum.IsDefined(typeof(QuotaWindowKind), kind)) return false;

            sample = new QuotaUsageSample
            {
                CapturedAt = DateTimeOffset.FromUnixTimeSeconds(capturedAt),
                WindowKind = (QuotaWindowKind)kind,
                WindowKey = parts[1],
                WindowMinutes = minutes,
                RemainingPercent = remaining,
                ResetsAt = resetsAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(resetsAt) : (DateTimeOffset?)null
            };
            return true;
        }

        internal static string TryReadPlanTypeFromIdToken(string idToken)
        {
            if (string.IsNullOrWhiteSpace(idToken)) return null;

            try
            {
                var parts = idToken.Split('.');
                if (parts.Length < 2) return null;

                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                var remainder = payload.Length % 4;
                if (remainder == 1) return null;
                if (remainder > 0) payload = payload.PadRight(payload.Length + 4 - remainder, '=');

                var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var claims = serializer.DeserializeObject(json) as Dictionary<string, object>;
                var authClaims = AsDictionary(Get(claims, "https://api.openai.com/auth"));
                return AsString(Get(authClaims, "chatgpt_plan_type", "chatgptPlanType"));
            }
            catch
            {
                return null;
            }
        }

        internal static bool TryParseTokenCountLine(string line, out QuotaSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(line) || line.IndexOf("token_count", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.DeserializeObject(line) as Dictionary<string, object>;
                if (root == null || !StringEquals(Get(root, "type"), "event_msg"))
                {
                    return false;
                }

                var payload = AsDictionary(Get(root, "payload"));
                if (payload == null || !StringEquals(Get(payload, "type"), "token_count"))
                {
                    return false;
                }

                var rateLimits = AsDictionary(Get(payload, "rate_limits", "rateLimits"));
                if (rateLimits == null)
                {
                    return false;
                }

                var parsed = new QuotaSnapshot
                {
                    CapturedAt = ParseTimestamp(Get(root, "timestamp")),
                    LimitId = AsString(Get(rateLimits, "limit_id", "limitId")) ?? "codex",
                    PlanType = AsString(Get(rateLimits, "plan_type", "planType"))
                };

                var buckets = AsDictionary(Get(rateLimits, "rate_limits_by_limit_id", "rateLimitsByLimitId"));
                var rootIsReserve = IsReserveBucket(parsed.LimitId, rateLimits);
                if (!rootIsReserve && (AsDictionary(Get(rateLimits, "primary")) != null || AsDictionary(Get(rateLimits, "secondary")) != null))
                {
                    AddBucket(parsed, rateLimits, parsed.LimitId);
                }
                else if (buckets != null && buckets.Count > 0)
                {
                    object ordinary;
                    if (!buckets.TryGetValue(parsed.LimitId, out ordinary)) buckets.TryGetValue("codex", out ordinary);
                    AddBucket(parsed, AsDictionary(ordinary), parsed.LimitId);
                }
                else if (!rootIsReserve)
                {
                    AddBucket(parsed, rateLimits, parsed.LimitId);
                }

                AddReserveFromBuckets(parsed, buckets);
                if (rootIsReserve) SetReserveBucket(parsed, rateLimits, parsed.LimitId);

                if (parsed.Windows.Count == 0)
                {
                    return false;
                }

                snapshot = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryParseAppServerRateLimitsResponse(string line, out QuotaSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(line)) return false;

            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.DeserializeObject(line) as Dictionary<string, object>;
                var result = AsDictionary(Get(root, "result"));
                var buckets = AsDictionary(Get(result, "rateLimitsByLimitId", "rate_limits_by_limit_id"));
                var rateLimits = AsDictionary(Get(result, "rateLimits", "rate_limits"));
                if (rateLimits == null)
                {
                    object ordinary;
                    if (buckets == null || !buckets.TryGetValue("codex", out ordinary)) return false;
                    rateLimits = AsDictionary(ordinary);
                }
                if (rateLimits == null) return false;

                var parsed = new QuotaSnapshot
                {
                    CapturedAt = DateTimeOffset.Now,
                    LimitId = AsString(Get(rateLimits, "limitId", "limit_id")) ?? "codex",
                    PlanType = AsString(Get(rateLimits, "planType", "plan_type"))
                };
                bool ordinaryUsageAllowed;
                if (TryBool(Get(result, "ordinaryUsageAllowed", "ordinary_usage_allowed"), out ordinaryUsageAllowed))
                    parsed.OrdinaryUsageAllowed = ordinaryUsageAllowed;
                AddBucket(parsed, rateLimits, parsed.LimitId);
                AddReserveFromBuckets(parsed, buckets);
                if (parsed.Windows.Count == 0) return false;

                snapshot = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryParseTokenUsageLine(string line, out TokenActivitySample usage)
        {
            usage = null;
            if (string.IsNullOrWhiteSpace(line) || line.IndexOf("token_count", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.DeserializeObject(line) as Dictionary<string, object>;
                if (root == null || !StringEquals(Get(root, "type"), "event_msg"))
                {
                    return false;
                }

                var payload = AsDictionary(Get(root, "payload"));
                if (payload == null || !StringEquals(Get(payload, "type"), "token_count"))
                {
                    return false;
                }

                var info = AsDictionary(Get(payload, "info"));
                var totalUsage = AsDictionary(Get(info, "total_token_usage", "totalTokenUsage"));
                long totalTokens;
                if (!TryLong(Get(totalUsage, "total_tokens", "totalTokens"), out totalTokens) || totalTokens < 0)
                {
                    return false;
                }

                var capturedAt = ParseTimestamp(Get(root, "timestamp"));
                if (capturedAt == DateTimeOffset.MinValue)
                {
                    return false;
                }

                usage = new TokenActivitySample
                {
                    CapturedAt = capturedAt,
                    Tokens = totalTokens
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void AddBucket(QuotaSnapshot snapshot, Dictionary<string, object> bucket, string fallbackName)
        {
            if (bucket == null) return;
            var bucketName = AsString(Get(bucket, "limit_name", "limitName"));
            if (string.IsNullOrWhiteSpace(bucketName)) bucketName = fallbackName;
            AddWindow(snapshot, AsDictionary(Get(bucket, "primary")), bucketName, false);
            AddWindow(snapshot, AsDictionary(Get(bucket, "secondary")), bucketName, true);
        }

        private static bool IsReserveBucket(string key, Dictionary<string, object> bucket)
        {
            var name = AsString(Get(bucket, "limit_name", "limitName"));
            return string.Equals(key, "base_model_inference", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "gpt-reserve", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddReserveFromBuckets(QuotaSnapshot snapshot, Dictionary<string, object> buckets)
        {
            if (buckets == null) return;
            object reserve;
            if (buckets.TryGetValue("base_model_inference", out reserve))
            {
                SetReserveBucket(snapshot, AsDictionary(reserve), "gpt-reserve");
                return;
            }

            foreach (var pair in buckets)
            {
                var bucket = AsDictionary(pair.Value);
                if (!IsReserveBucket(pair.Key, bucket)) continue;
                SetReserveBucket(snapshot, bucket, "gpt-reserve");
                return;
            }
        }

        private static void SetReserveBucket(QuotaSnapshot snapshot, Dictionary<string, object> bucket, string fallbackName)
        {
            if (bucket == null) return;
            var holder = new QuotaSnapshot();
            AddBucket(holder, bucket, fallbackName);
            var reserve = holder.Windows.OrderByDescending(window => window.WindowMinutes).FirstOrDefault();
            if (reserve == null) return;
            reserve.Name = "gpt-reserve · 备用额度";
            reserve.Key = "gpt-reserve:" + reserve.WindowMinutes.ToString(CultureInfo.InvariantCulture);
            snapshot.ReserveWindow = reserve;
        }

        private static void AddWindow(
            QuotaSnapshot snapshot,
            Dictionary<string, object> source,
            string bucketName,
            bool secondary)
        {
            if (source == null) return;
            double usedPercent;
            if (!TryDouble(Get(source, "used_percent", "usedPercent"), out usedPercent)) return;

            int windowMinutes;
            TryInt(Get(source, "window_minutes", "windowDurationMins"), out windowMinutes);

            long resetsAt;
            DateTimeOffset? resetTime = null;
            if (TryLong(Get(source, "resets_at", "resetsAt"), out resetsAt) && resetsAt > 0)
            {
                resetTime = DateTimeOffset.FromUnixTimeSeconds(resetsAt);
            }

            snapshot.Windows.Add(new QuotaWindow
            {
                Name = BuildWindowName(bucketName, windowMinutes, secondary),
                Key = (string.IsNullOrWhiteSpace(bucketName) ? "codex" : bucketName.Trim().ToLowerInvariant()) + ":" + windowMinutes,
                Kind = ClassifyWindow(windowMinutes),
                UsedPercent = usedPercent,
                WindowMinutes = windowMinutes,
                ResetsAt = resetTime
            });
        }

        private static string BuildWindowName(string bucketName, int minutes, bool secondary)
        {
            string windowName;
            if (minutes == 300) windowName = "5小时额度";
            else if (minutes >= 10080) windowName = "每周额度";
            else if (minutes >= 1440) windowName = "长期额度";
            else if (minutes > 0) windowName = "短期额度";
            else windowName = secondary ? "次要额度" : "主要额度";

            if (!string.IsNullOrWhiteSpace(bucketName) &&
                !string.Equals(bucketName, "codex", StringComparison.OrdinalIgnoreCase))
            {
                return bucketName + " · " + windowName;
            }

            return windowName;
        }

        internal static QuotaWindowKind ClassifyWindow(int minutes)
        {
            if (minutes == 300) return QuotaWindowKind.FiveHour;
            if (minutes == 10080) return QuotaWindowKind.Weekly;
            return QuotaWindowKind.Other;
        }

        private static object Get(Dictionary<string, object> source, params string[] keys)
        {
            if (source == null) return null;
            foreach (var key in keys)
            {
                object value;
                if (source.TryGetValue(key, out value)) return value;
            }
            return null;
        }

        private static Dictionary<string, object> AsDictionary(object value)
        {
            return value as Dictionary<string, object>;
        }

        private static string AsString(object value)
        {
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool StringEquals(object value, string expected)
        {
            return string.Equals(AsString(value), expected, StringComparison.Ordinal);
        }

        private static DateTimeOffset ParseTimestamp(object value)
        {
            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(AsString(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
            {
                return parsed;
            }
            return DateTimeOffset.MinValue;
        }

        private static bool TryDouble(object value, out double parsed)
        {
            return double.TryParse(AsString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
        }

        private static bool TryInt(object value, out int parsed)
        {
            return int.TryParse(AsString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
        }

        private static bool TryLong(object value, out long parsed)
        {
            return long.TryParse(AsString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
        }

        private static bool TryBool(object value, out bool parsed)
        {
            return bool.TryParse(AsString(value), out parsed);
        }
    }
}
