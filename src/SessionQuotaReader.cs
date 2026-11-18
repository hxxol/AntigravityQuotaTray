using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CodexQuotaTray.Compatibility;

namespace CodexQuotaTray
{
    internal sealed class PortEndpoint
    {
        public ushort Port { get; set; }
        public bool PreferIpv6 { get; set; }
    }

    internal sealed class LanguageServerCandidate
    {
        public uint Pid { get; set; }
        public string ExeName { get; set; }
        public string CsrfToken { get; set; }
        public string ExtensionCsrfToken { get; set; }
        public ushort? HttpsServerPort { get; set; }
        public ushort? ExtensionServerPort { get; set; }
        public bool EnableLsp { get; set; }
        public bool IsCliProcess { get; set; }
        public List<PortEndpoint> Endpoints { get; set; } = new List<PortEndpoint>();
        public List<ushort> Ports
        {
            get { return Endpoints.Select(e => e.Port).ToList(); }
        }
    }

    internal sealed class SessionQuotaReader
    {
        internal const string NotRunningMessage = "Antigravity 未运行";
        internal const string UnreadableProcessMessage = "无法读取 Antigravity 服务参数（请保持相同运行权限）";
        private const string ServicePath = "/exa.language_server_pb.LanguageServerService/";
        private const string UserStatusRequestBody = "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"ideVersion\":\"unknown\",\"locale\":\"en\"}}";
        private const int MaxPortsPerProcess = 16;
        private const int MaximumCommandLineChars = 32768;

        private static readonly string[] SessionCadenceAliases =
        {
            "5h",
            "session",
            "5-hour",
            "five-hour",
            "five hour"
        };

        private static readonly string[] WeeklyCadenceAliases =
        {
            "weekly",
            "7d",
            "7-day",
            "seven-day",
            "seven day"
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
                string diagnostics;
                var latest = ReadLatestFromLocalLanguageServer(out running, out keepNormalPace, out failure, out diagnostics);
                result.IsCodexRunning = _processDetector != null ? _processDetector() : running;
                result.Snapshot = latest;
                result.KeepNormalPollInterval = keepNormalPace;
                if (latest != null) RecordLiveQuotaSnapshot(latest);
                result.QuotaHistory = GetCombinedQuotaHistory();
                if (latest == null)
                {
                    result.Error = failure ?? "读取额度时出现未知错误";
                    result.Diagnostics = diagnostics;
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
                ConnectTimeout = TimeSpan.FromMilliseconds(900),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
            // Every URI passed to LoopbackHttpClient is built from 127.0.0.1 or [::1] in BuildLoopbackRpcUri;
            // the local Antigravity language server uses a self-signed localhost certificate.
            handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true;
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        }

        private static Uri BuildLoopbackRpcUri(bool useHttps, bool useIpv6, ushort port, string rpcMethod)
        {
            var scheme = useHttps ? "https://" : "http://";
            var host = useIpv6 ? "[::1]:" : "127.0.0.1:";
            return new Uri(scheme + host + port.ToString(CultureInfo.InvariantCulture) + ServicePath + rpcMethod);
        }

        private static QuotaSnapshot ReadLatestFromLocalLanguageServer(
            out bool isRunning,
            out bool keepNormalPace,
            out string failure,
            out string diagnostics)
        {
            isRunning = false;
            keepNormalPace = false;
            failure = null;
            diagnostics = null;

            int rawProcessCount;
            bool unreadableFound;
            var candidates = DiscoverLanguageServers(out rawProcessCount, out unreadableFound);
            if (candidates.Count == 0)
            {
                diagnostics = "ls_pids=" + rawProcessCount.ToString(CultureInfo.InvariantCulture) +
                              " unreadable=" + (unreadableFound ? "1" : "0");
                if (unreadableFound)
                {
                    failure = UnreadableProcessMessage;
                    return null;
                }
                if (rawProcessCount > 0)
                {
                    keepNormalPace = true;
                    failure = "已检测到后台服务进程，但未能提取连接参数";
                    return null;
                }
                keepNormalPace = true;
                failure = NotRunningMessage;
                return null;
            }

            isRunning = true;
            SortLanguageServerCandidates(candidates);

            int totalEndpoints = 0;
            bool unsupportedSummary = false;
            int lastHttpStatus = 0;
            string lastError = null;

            foreach (var candidate in candidates)
            {
                var endpoints = BuildOrderedEndpoints(candidate);
                var limit = Math.Min(endpoints.Count, MaxPortsPerProcess);
                for (int i = 0; i < limit; i++)
                {
                    totalEndpoints++;
                    var endpoint = endpoints[i];
                    QuotaSnapshot snapshot;
                    bool methodMissing;
                    int status;
                    string portError;
                    if (TryQueryCandidateEndpoint(endpoint, candidate, out snapshot, out methodMissing, out status, out portError))
                    {
                        return snapshot;
                    }
                    if (methodMissing) unsupportedSummary = true;
                    if (status > 0) lastHttpStatus = status;
                    if (!string.IsNullOrWhiteSpace(portError)) lastError = portError;
                }
            }

            diagnostics = "ls_pids=" + rawProcessCount.ToString(CultureInfo.InvariantCulture) +
                          " candidates=" + candidates.Count.ToString(CultureInfo.InvariantCulture) +
                          " endpoints=" + totalEndpoints.ToString(CultureInfo.InvariantCulture) +
                          " last_status=" + lastHttpStatus.ToString(CultureInfo.InvariantCulture);

            if (totalEndpoints == 0)
            {
                keepNormalPace = true;
                failure = "Antigravity 正在启动，暂未监听本地端口";
                return null;
            }

            failure = unsupportedSummary
                ? "当前 Antigravity 版本不支持本地额度汇总接口"
                : (lastError ?? "无法连接本地 Antigravity 额度接口");
            return null;
        }

        private static List<PortEndpoint> BuildOrderedEndpoints(LanguageServerCandidate candidate)
        {
            var ordered = new List<PortEndpoint>();
            var seen = new HashSet<ushort>();

            if (candidate.Endpoints != null)
            {
                foreach (var ep in candidate.Endpoints)
                {
                    if (ep.Port > 0 && seen.Add(ep.Port)) ordered.Add(ep);
                }
            }

            if (candidate.HttpsServerPort.HasValue && candidate.HttpsServerPort.Value > 0 && seen.Add(candidate.HttpsServerPort.Value))
            {
                ordered.Add(new PortEndpoint { Port = candidate.HttpsServerPort.Value, PreferIpv6 = false });
            }

            if (candidate.ExtensionServerPort.HasValue && candidate.ExtensionServerPort.Value > 0)
            {
                for (ushort offset = 0; offset < 12; offset++)
                {
                    int port = candidate.ExtensionServerPort.Value + offset;
                    if (port <= ushort.MaxValue && seen.Add((ushort)port))
                    {
                        ordered.Add(new PortEndpoint { Port = (ushort)port, PreferIpv6 = false });
                    }
                }
            }

            return ordered;
        }

        private static List<string> BuildOrderedCsrfTokens(LanguageServerCandidate candidate)
        {
            var tokens = new List<string>();
            if (!string.IsNullOrWhiteSpace(candidate.CsrfToken))
            {
                tokens.Add(candidate.CsrfToken);
            }
            if (!string.IsNullOrWhiteSpace(candidate.ExtensionCsrfToken) &&
                !string.Equals(candidate.ExtensionCsrfToken, candidate.CsrfToken, StringComparison.Ordinal))
            {
                tokens.Add(candidate.ExtensionCsrfToken);
            }
            if (tokens.Count == 0 || candidate.IsCliProcess)
            {
                tokens.Add(null);
            }
            return tokens;
        }

        private static bool TryQueryCandidateEndpoint(
            PortEndpoint endpoint,
            LanguageServerCandidate candidate,
            out QuotaSnapshot snapshot,
            out bool methodMissing,
            out int lastStatus,
            out string error)
        {
            snapshot = null;
            methodMissing = false;
            lastStatus = 0;
            error = null;

            var ipv6Order = endpoint.PreferIpv6 ? new[] { true, false } : new[] { false, true };
            var csrfTokens = BuildOrderedCsrfTokens(candidate);

            foreach (var useIpv6 in ipv6Order)
            {
                bool hostAnswered = false;
                foreach (var useHttps in new[] { true, false })
                {
                    int status = 0;
                    string body = null;
                    bool transportOk = false;

                    for (int t = 0; t < csrfTokens.Count; t++)
                    {
                        if (!TryPostLoopbackRpc(useHttps, useIpv6, endpoint.Port, csrfTokens[t],
                            "RetrieveUserQuotaSummary", "{}", 2500, out status, out body))
                        {
                            break;
                        }

                        transportOk = true;
                        hostAnswered = true;
                        lastStatus = status;

                        if (status >= 200 && status < 300)
                        {
                            if (TryParseAntigravityQuotaSummary(body, out snapshot)) return true;

                            // Retry once with forceRefresh if the initial empty-body cache was unpopulated.
                            int refreshStatus;
                            string refreshBody;
                            if (TryPostLoopbackRpc(useHttps, useIpv6, endpoint.Port, csrfTokens[t],
                                "RetrieveUserQuotaSummary", "{\"forceRefresh\":true}", 3500, out refreshStatus, out refreshBody) &&
                                refreshStatus >= 200 && refreshStatus < 300 &&
                                TryParseAntigravityQuotaSummary(refreshBody, out snapshot))
                            {
                                return true;
                            }

                            if (TryFallbackUserStatus(useHttps, useIpv6, endpoint.Port, csrfTokens[t], out snapshot))
                            {
                                return true;
                            }

                            error = "Antigravity 返回的额度数据为空或无法识别";
                            return false;
                        }

                        if (status == 404 || status == 501)
                        {
                            if (TryFallbackUserStatus(useHttps, useIpv6, endpoint.Port, csrfTokens[t], out snapshot))
                            {
                                return true;
                            }
                            methodMissing = true;
                            return false;
                        }

                        // On 401/403, try the alternate CSRF token (e.g. extension_server_csrf_token) if present.
                        if (status == 401 || status == 403)
                        {
                            continue;
                        }

                        break;
                    }

                    if (transportOk)
                    {
                        error = status == 401 || status == 403
                            ? "Antigravity 本地服务鉴权未通过或尚未登录"
                            : "Antigravity 本地服务返回错误 (" + status.ToString(CultureInfo.InvariantCulture) + ")";
                        return false;
                    }
                }

                if (hostAnswered) break;
            }

            return false;
        }

        private static bool TryFallbackUserStatus(
            bool useHttps,
            bool useIpv6,
            ushort port,
            string csrfToken,
            out QuotaSnapshot snapshot)
        {
            snapshot = null;
            int fallbackStatus;
            string fallbackBody;
            return TryPostLoopbackRpc(useHttps, useIpv6, port, csrfToken, "GetUserStatus", UserStatusRequestBody, 3500, out fallbackStatus, out fallbackBody) &&
                   fallbackStatus >= 200 && fallbackStatus < 300 &&
                   TryParseAntigravityUserStatusFallback(fallbackBody, out snapshot);
        }

        private static bool TryPostLoopbackRpc(
            bool useHttps,
            bool useIpv6,
            ushort port,
            string csrfToken,
            string rpcMethod,
            string jsonBody,
            int timeoutMs,
            out int statusCode,
            out string responseBody)
        {
            statusCode = 0;
            responseBody = null;
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var request = new HttpRequestMessage(HttpMethod.Post, BuildLoopbackRpcUri(useHttps, useIpv6, port, rpcMethod)))
                {
                    request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
                    if (!string.IsNullOrWhiteSpace(csrfToken))
                    {
                        request.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrfToken);
                    }
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    using (var response = LoopbackHttpClient.SendAsync(request, cts.Token).GetAwaiter().GetResult())
                    {
                        statusCode = (int)response.StatusCode;
                        responseBody = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
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

                var payload = AsDictionary(Get(root, "response"))
                              ?? AsDictionary(Get(root, "summary"))
                              ?? AsDictionary(Get(AsDictionary(Get(root, "command")), "data"))
                              ?? root;
                var groups = Get(payload, "groups") as System.Collections.IEnumerable;
                if (groups == null || groups is string) return false;

                var parsed = new QuotaSnapshot
                {
                    CapturedAt = DateTimeOffset.Now,
                    LimitId = "antigravity"
                };

                var candidates = new List<(QuotaWindow Window, bool IsOrdinaryPrimary)>();

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
                        if (!TryReadRemainingFraction(bucket, out remainingFraction)) continue;
                        if (double.IsNaN(remainingFraction) || double.IsInfinity(remainingFraction) ||
                            remainingFraction < 0d || remainingFraction > 1d)
                        {
                            continue;
                        }

                        var bucketId = AsString(Get(bucket, "bucketId", "bucket_id", "id")) ?? string.Empty;
                        var bucketDisplayName = AsString(Get(bucket, "displayName", "display_name", "name")) ?? string.Empty;
                        var windowName = AsString(Get(bucket, "window")) ?? string.Empty;
                        var pool = ClassifyPool(bucketId, groupDisplayName);
                        var minutes = ParseAntigravityWindowMinutes(windowName, bucketId, bucketDisplayName);
                        var kind = ClassifyWindow(minutes);
                        if (pool == QuotaPoolKind.Default || kind == QuotaWindowKind.Other) continue;

                        var untouchedFull = remainingFraction >= 1.0d;
                        DateTimeOffset? resetTime = null;
                        // At remainingFraction == 1, resetTime is a sliding placeholder rather than a real deadline.
                        if (!untouchedFull)
                        {
                            var parsedReset = ParseResetTimestamp(Get(bucket, "resetTime", "reset_time"));
                            if (parsedReset != DateTimeOffset.MinValue) resetTime = parsedReset;
                        }

                        var poolPrefix = pool == QuotaPoolKind.Gemini ? "gemini" : "thirdparty";
                        var window = new QuotaWindow
                        {
                            Pool = pool,
                            Name = (pool == QuotaPoolKind.Gemini ? "Gemini" : "第三方") + " · " + (kind == QuotaWindowKind.FiveHour ? "5h" : "Weekly"),
                            Key = poolPrefix + ":" + minutes.ToString(CultureInfo.InvariantCulture),
                            Kind = kind,
                            WindowMinutes = minutes,
                            UsedPercent = Math.Max(0d, Math.Min(100d, (1.0d - remainingFraction) * 100.0d)),
                            ResetsAt = resetTime,
                            IsUntouchedFull = untouchedFull
                        };

                        candidates.Add((window, IsOrdinaryGeminiBucket(bucketId)));
                    }
                }

                foreach (var pool in new[] { QuotaPoolKind.Gemini, QuotaPoolKind.ThirdParty })
                {
                    foreach (var kind in new[] { QuotaWindowKind.FiveHour, QuotaWindowKind.Weekly })
                    {
                        var chosen = SelectRepresentativeBucket(candidates, pool, kind);
                        if (chosen != null) parsed.Windows.Add(chosen);
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

        private static QuotaWindow SelectRepresentativeBucket(
            List<(QuotaWindow Window, bool IsOrdinaryPrimary)> candidates,
            QuotaPoolKind pool,
            QuotaWindowKind kind)
        {
            if (pool == QuotaPoolKind.Gemini)
            {
                QuotaWindow ordinaryBest = null;
                foreach (var item in candidates)
                {
                    if (item.Window.Pool != pool || item.Window.Kind != kind || !item.IsOrdinaryPrimary) continue;
                    if (ordinaryBest == null || item.Window.UsedPercent > ordinaryBest.UsedPercent)
                        ordinaryBest = item.Window;
                }
                if (ordinaryBest != null) return ordinaryBest;
            }

            QuotaWindow mostConstrained = null;
            foreach (var item in candidates)
            {
                if (item.Window.Pool != pool || item.Window.Kind != kind) continue;
                if (mostConstrained == null || item.Window.UsedPercent > mostConstrained.UsedPercent)
                    mostConstrained = item.Window;
            }
            return mostConstrained;
        }

        private static bool IsOrdinaryGeminiBucket(string bucketId)
        {
            if (string.IsNullOrWhiteSpace(bucketId)) return false;
            var normalized = NormalizeCadenceToken(bucketId);
            if (normalized == "gemini-weekly") return true;
            foreach (var alias in SessionCadenceAliases)
            {
                if (normalized == "gemini-" + alias) return true;
            }
            return false;
        }

        private static bool TryReadRemainingFraction(Dictionary<string, object> bucket, out double fraction)
        {
            if (TryDouble(Get(bucket, "remainingFraction", "remaining_fraction"), out fraction))
            {
                return true;
            }

            // Connect-ES / protobuf oneof form: {"remaining": {"case": "remainingFraction", "value": 0.8}}
            // or {"remaining": {"remainingFraction": 0.8}}.
            var remainingObj = AsDictionary(Get(bucket, "remaining"));
            if (remainingObj != null)
            {
                if (TryDouble(Get(remainingObj, "remainingFraction", "remaining_fraction"), out fraction))
                {
                    return true;
                }
                var oneofCase = AsString(Get(remainingObj, "case"));
                if (string.Equals(oneofCase, "remainingFraction", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(oneofCase, "remaining_fraction", StringComparison.OrdinalIgnoreCase))
                {
                    return TryDouble(Get(remainingObj, "value"), out fraction);
                }
            }

            fraction = 0d;
            return false;
        }

        internal static bool TryParseAntigravityUserStatusFallback(string json, out QuotaSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                var userStatus = AsDictionary(Get(root, "userStatus")) ?? root;
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
                    if (quotaInfo == null || !TryReadRemainingFraction(quotaInfo, out remainingFraction)) continue;
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
                        var parsedReset = ParseResetTimestamp(Get(quotaInfo, "resetTime", "reset_time"));
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
            if (!string.IsNullOrWhiteSpace(groupDisplayName))
            {
                if (groupDisplayName.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) >= 0)
                    return QuotaPoolKind.Gemini;
                if (groupDisplayName.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    groupDisplayName.IndexOf("gpt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    groupDisplayName.IndexOf("third", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    groupDisplayName.IndexOf("other", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    groupDisplayName.IndexOf("partner", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    groupDisplayName.IndexOf("external", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return QuotaPoolKind.ThirdParty;
                }
            }

            if (!string.IsNullOrWhiteSpace(bucketId))
            {
                if (bucketId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)) return QuotaPoolKind.Gemini;
                if (bucketId.StartsWith("3p", StringComparison.OrdinalIgnoreCase) ||
                    bucketId.StartsWith("third", StringComparison.OrdinalIgnoreCase))
                {
                    return QuotaPoolKind.ThirdParty;
                }
            }

            // Any non-Gemini named model group in Antigravity's summary belongs to the non-Gemini pool.
            if (!string.IsNullOrWhiteSpace(groupDisplayName))
            {
                return QuotaPoolKind.ThirdParty;
            }

            return QuotaPoolKind.Default;
        }

        private static int ParseAntigravityWindowMinutes(string windowName, string bucketId, string displayName)
        {
            if (!string.IsNullOrWhiteSpace(windowName))
            {
                return MatchCadenceMinutes(windowName);
            }

            var fromId = MatchCadenceMinutes(bucketId);
            if (fromId > 0) return fromId;
            return MatchCadenceMinutes(displayName);
        }

        private static int MatchCadenceMinutes(string raw)
        {
            var normalized = NormalizeCadenceToken(raw);
            if (string.IsNullOrEmpty(normalized)) return 0;

            foreach (var alias in SessionCadenceAliases)
            {
                if (normalized == alias || normalized.EndsWith("-" + alias, StringComparison.Ordinal))
                    return 300;
            }
            foreach (var alias in WeeklyCadenceAliases)
            {
                if (normalized == alias || normalized.EndsWith("-" + alias, StringComparison.Ordinal))
                    return 10080;
            }
            return 0;
        }

        private static string NormalizeCadenceToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var text = raw.Trim().ToLowerInvariant().Replace('_', '-');
            if (text.EndsWith(" limit remaining", StringComparison.Ordinal))
                text = text.Substring(0, text.Length - " limit remaining".Length).TrimEnd();
            else if (text.EndsWith(" limit", StringComparison.Ordinal))
                text = text.Substring(0, text.Length - " limit".Length).TrimEnd();
            return text;
        }

        private static DateTimeOffset ParseResetTimestamp(object value)
        {
            var parsed = ParseTimestamp(value);
            if (parsed != DateTimeOffset.MinValue) return parsed;

            double epochSeconds;
            if (TryDouble(value, out epochSeconds) && !double.IsNaN(epochSeconds) && !double.IsInfinity(epochSeconds) &&
                epochSeconds > 1_000_000_000d && epochSeconds < 10_000_000_000d)
            {
                return DateTimeOffset.FromUnixTimeSeconds((long)Math.Truncate(epochSeconds));
            }
            return DateTimeOffset.MinValue;
        }

        internal static bool IsAntigravityLanguageServerExe(string exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName)) return false;
            var fileName = Path.GetFileName(exeName.Trim());
            if (string.IsNullOrEmpty(fileName)) return false;

            // Antigravity 2.0 desktop bundles `language_server.exe`; older/IDE builds use
            // `language_server_windows_x64.exe` / `language_server_windows_arm*.exe`; CLI uses `agy.exe`.
            if (string.Equals(fileName, "language_server.exe", StringComparison.OrdinalIgnoreCase)) return true;
            if (fileName.StartsWith("language_server_", StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(fileName, "agy.exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "antigravity-cli.exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "antigravity_cli.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        private static bool IsCliExecutableName(string exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName)) return false;
            var fileName = Path.GetFileName(exeName.Trim());
            return string.Equals(fileName, "agy.exe", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fileName, "agy", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fileName, "antigravity-cli.exe", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fileName, "antigravity_cli.exe", StringComparison.OrdinalIgnoreCase);
        }

        internal static void SortLanguageServerCandidates(List<LanguageServerCandidate> candidates)
        {
            if (candidates == null) return;
            // Desktop language servers (with CSRF) first, then global non-LSP before workspace LSP, then lower PID.
            candidates.Sort((a, b) =>
            {
                var cliCompare = a.IsCliProcess.CompareTo(b.IsCliProcess);
                if (cliCompare != 0) return cliCompare;
                var lspCompare = a.EnableLsp.CompareTo(b.EnableLsp);
                return lspCompare != 0 ? lspCompare : a.Pid.CompareTo(b.Pid);
            });
        }

        internal static bool TryParseLanguageServerCommandLine(
            string commandLine,
            out string csrfToken,
            out bool enableLsp)
        {
            LanguageServerCandidate candidate;
            if (TryParseCandidateCommandLine("language_server.exe", commandLine, out candidate))
            {
                csrfToken = candidate.CsrfToken ?? candidate.ExtensionCsrfToken;
                enableLsp = candidate.EnableLsp;
                return !string.IsNullOrEmpty(csrfToken);
            }
            csrfToken = null;
            enableLsp = false;
            return false;
        }

        internal static bool TryParseCandidateCommandLine(
            string exeName,
            string commandLine,
            out LanguageServerCandidate candidate)
        {
            candidate = null;
            if (string.IsNullOrWhiteSpace(commandLine)) return false;

            var tokens = TokenizeCommandLine(commandLine);
            if (tokens.Count == 0) return false;

            // Reject sibling non-Antigravity Codeium/Windsurf/Cursor servers if explicitly identified.
            foreach (var identityFlag in new[] { "--app_data_dir", "--ide_name", "--override_ide_name" })
            {
                var flagVal = FindFlagValue(tokens, identityFlag);
                if (!string.IsNullOrWhiteSpace(flagVal) && IsForeignIdeIdentifier(flagVal))
                {
                    return false;
                }
            }

            var csrf = SanitizeToken(FindFlagValue(tokens, "--csrf_token"));
            var extCsrf = SanitizeToken(FindFlagValue(tokens, "--extension_server_csrf_token"));
            bool isCli = IsCliExecutableName(exeName) || IsCliExecutableName(tokens[0]);

            if (csrf == null && extCsrf == null && !isCli)
            {
                return false;
            }

            bool enableLsp = false;
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

            ushort httpsPort;
            ushort extPort;
            bool hasHttpsPort = ushort.TryParse(FindFlagValue(tokens, "--https_server_port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out httpsPort) && httpsPort > 0;
            bool hasExtPort = ushort.TryParse(FindFlagValue(tokens, "--extension_server_port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out extPort) && extPort > 0;

            candidate = new LanguageServerCandidate
            {
                ExeName = exeName,
                CsrfToken = csrf,
                ExtensionCsrfToken = extCsrf,
                HttpsServerPort = hasHttpsPort ? httpsPort : (ushort?)null,
                ExtensionServerPort = hasExtPort ? extPort : (ushort?)null,
                EnableLsp = enableLsp,
                IsCliProcess = isCli && csrf == null && extCsrf == null
            };
            return true;
        }

        private static bool IsForeignIdeIdentifier(string value)
        {
            if (value.IndexOf("antigravity", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("jetski", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf(".gemini", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            var leaf = Path.GetFileName(value.Trim().TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(leaf)) leaf = value.Trim();

            return leaf.IndexOf("windsurf", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   leaf.IndexOf("codeium", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   leaf.IndexOf("cursor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SanitizeToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var trimmed = raw.Trim();
            if (trimmed.Length < 8 || trimmed.Length > 256) return null;
            foreach (var ch in trimmed)
            {
                if (ch <= 0x20 || ch >= 0x7F) return null;
            }
            return trimmed;
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

        internal static List<(uint Pid, string ExeName, bool IsAppDescendant)> SelectTargetProcesses(
            List<(uint Pid, uint ParentPid, string ExeName)> allProcesses,
            HashSet<uint> listeningPids)
        {
            var selected = new List<(uint Pid, string ExeName, bool IsAppDescendant)>();
            if (allProcesses == null || allProcesses.Count == 0) return selected;

            var seen = new HashSet<uint>();
            var antigravityRootPids = new HashSet<uint>();

            foreach (var proc in allProcesses)
            {
                if (IsAntigravityLanguageServerExe(proc.ExeName) && seen.Add(proc.Pid))
                {
                    selected.Add((proc.Pid, proc.ExeName, false));
                }
                if (IsAntigravityHostAppExe(proc.ExeName))
                {
                    antigravityRootPids.Add(proc.Pid);
                }
            }

            if (antigravityRootPids.Count == 0) return selected;

            // Walk the process tree from Antigravity.exe to include any renamed sidecar or utility
            // child process that owns a listening TCP port.
            var descendants = new HashSet<uint>(antigravityRootPids);
            bool added;
            do
            {
                added = false;
                foreach (var proc in allProcesses)
                {
                    if (proc.Pid != 0 && descendants.Contains(proc.ParentPid) && descendants.Add(proc.Pid))
                    {
                        added = true;
                    }
                }
            }
            while (added);

            foreach (var proc in allProcesses)
            {
                if (!descendants.Contains(proc.Pid) || seen.Contains(proc.Pid)) continue;
                if (listeningPids != null && listeningPids.Contains(proc.Pid) && seen.Add(proc.Pid))
                {
                    selected.Add((proc.Pid, proc.ExeName, true));
                }
            }

            return selected;
        }

        private static bool IsAntigravityHostAppExe(string exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName)) return false;
            var fileName = Path.GetFileName(exeName.Trim());
            return string.Equals(fileName, "Antigravity.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static List<LanguageServerCandidate> DiscoverLanguageServers(out int rawProcessCount, out bool unreadableFound)
        {
            rawProcessCount = 0;
            unreadableFound = false;
            var candidates = new List<LanguageServerCandidate>();
            if (!OperatingSystem.IsWindows()) return candidates;

            var allProcesses = EnumerateAllProcesses();
            if (allProcesses.Count == 0) return candidates;

            var endpointsByPid = GetLoopbackListeningPortsByPid();
            var listeningPids = new HashSet<uint>(endpointsByPid.Keys);
            var matchedProcesses = SelectTargetProcesses(allProcesses, listeningPids);
            rawProcessCount = matchedProcesses.Count;
            if (matchedProcesses.Count == 0) return candidates;

            Dictionary<uint, string> cimFallbackMap = null;

            foreach (var proc in matchedProcesses)
            {
                bool accessDenied;
                var commandLine = TryReadProcessCommandLine(proc.Pid, out accessDenied);
                if (string.IsNullOrWhiteSpace(commandLine))
                {
                    if (cimFallbackMap == null)
                    {
                        cimFallbackMap = TryReadCommandLinesViaCimFallback(matchedProcesses.Select(p => p.Pid).ToList());
                    }
                    cimFallbackMap.TryGetValue(proc.Pid, out commandLine);
                }

                LanguageServerCandidate candidate = null;
                if (!string.IsNullOrWhiteSpace(commandLine))
                {
                    TryParseCandidateCommandLine(proc.ExeName, commandLine, out candidate);
                }

                // If an Antigravity.exe descendant is actively listening on a local port without a
                // command-line CSRF flag (like tokenless embedded/CLI hosts), allow probing it as last resort.
                if (candidate == null && proc.IsAppDescendant)
                {
                    candidate = new LanguageServerCandidate
                    {
                        ExeName = proc.ExeName,
                        IsCliProcess = true,
                        EnableLsp = true
                    };
                }

                if (candidate == null)
                {
                    if (accessDenied) unreadableFound = true;
                    continue;
                }

                candidate.Pid = proc.Pid;
                List<PortEndpoint> endpoints;
                if (!endpointsByPid.TryGetValue(proc.Pid, out endpoints)) endpoints = new List<PortEndpoint>();
                candidate.Endpoints = endpoints;
                candidates.Add(candidate);
            }

            return candidates;
        }

        private static List<(uint Pid, uint ParentPid, string ExeName)> EnumerateAllProcesses()
        {
            var list = new List<(uint, uint, string)>();
            IntPtr snapshot = IntPtr.Zero;
            try
            {
                snapshot = CreateToolhelp32Snapshot(0x00000002u /* TH32CS_SNAPPROCESS */, 0);
                if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return list;

                var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (Process32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        if (!string.IsNullOrEmpty(entry.szExeFile) &&
                            !string.Equals(entry.szExeFile, "AntigravityQuotaTray.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            list.Add((entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile));
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
            return list;
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
            // Pass a real initial buffer (16 KB) instead of NULL/0 so all Windows builds populate or report ReturnLength reliably.
            uint alloc = 16384;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal((int)alloc);
                try
                {
                    uint needed = 0;
                    int status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, alloc, out needed);
                    if (status == 0)
                    {
                        int byteLength = (ushort)Marshal.ReadInt16(buffer, 0);
                        if (byteLength <= 0 || byteLength > MaximumCommandLineChars * 2) return null;
                        IntPtr stringPtr = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                        if (stringPtr == IntPtr.Zero) return null;
                        return Marshal.PtrToStringUni(stringPtr, byteLength / 2);
                    }

                    // STATUS_INFO_LENGTH_MISMATCH (0xC0000004) or STATUS_BUFFER_OVERFLOW / BUFFER_TOO_SMALL
                    if (needed > alloc && needed <= MaximumCommandLineChars * 2 + 64)
                    {
                        alloc = needed + 64;
                        continue;
                    }
                    return null;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
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

        private static Dictionary<uint, string> TryReadCommandLinesViaCimFallback(List<uint> pids)
        {
            var map = new Dictionary<uint, string>();
            if (pids == null || pids.Count == 0) return map;

            try
            {
                var filter = string.Join(" OR ", pids.Select(id => "ProcessId=" + id.ToString(CultureInfo.InvariantCulture)));
                var script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " +
                             "Get-CimInstance Win32_Process -Filter '" + filter + "' | ForEach-Object { \"$($_.ProcessId)`t$($_.CommandLine)\" }";

                using (var proc = new Process())
                {
                    proc.StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script + "\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };
                    if (!proc.Start()) return map;
                    var outputTask = proc.StandardOutput.ReadToEndAsync();
                    proc.StandardError.ReadToEndAsync();
                    if (!outputTask.Wait(4000))
                    {
                        try { proc.Kill(); } catch { }
                        return map;
                    }
                    foreach (var line in (outputTask.Result ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int tab = line.IndexOf('\t');
                        uint parsedPid;
                        if (tab > 0 && uint.TryParse(line.Substring(0, tab).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedPid))
                        {
                            var cmd = line.Substring(tab + 1).Trim();
                            if (!string.IsNullOrEmpty(cmd)) map[parsedPid] = cmd;
                        }
                    }
                }
            }
            catch
            {
            }

            return map;
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

        private static Dictionary<uint, List<PortEndpoint>> GetLoopbackListeningPortsByPid()
        {
            var result = new Dictionary<uint, List<PortEndpoint>>();
            CollectIpv4Listeners(result);
            CollectIpv6Listeners(result);

            foreach (var pair in result)
            {
                pair.Value.Sort((a, b) => a.Port.CompareTo(b.Port));
            }
            return result;
        }

        private static void CollectIpv4Listeners(Dictionary<uint, List<PortEndpoint>> result)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2 /* AF_INET */, 3 /* TCP_TABLE_OWNER_PID_LISTENER */, 0);
            if (size <= 0 || size > 16 * 1024 * 1024) return;

            int alloc = size + 4096;
            IntPtr buffer = Marshal.AllocHGlobal(alloc);
            try
            {
                if (GetExtendedTcpTable(buffer, ref alloc, false, 2, 3, 0) != 0) return;
                int entries = Marshal.ReadInt32(buffer, 0);
                int maxEntries = Math.Max(0, (alloc - 4) / 24);
                int count = Math.Min(entries, maxEntries);

                for (int i = 0; i < count; i++)
                {
                    int row = 4 + i * 24;
                    uint rawPort = unchecked((uint)Marshal.ReadInt32(buffer, row + 8));
                    uint owningPid = unchecked((uint)Marshal.ReadInt32(buffer, row + 20));
                    ushort port = (ushort)(((rawPort & 0xFFu) << 8) | ((rawPort >> 8) & 0xFFu));
                    if (port == 0) continue;

                    AddEndpoint(result, owningPid, port, false);
                }
            }
            catch
            {
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void CollectIpv6Listeners(Dictionary<uint, List<PortEndpoint>> result)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 23 /* AF_INET6 */, 3 /* TCP_TABLE_OWNER_PID_LISTENER */, 0);
            if (size <= 0 || size > 16 * 1024 * 1024) return;

            int alloc = size + 4096;
            IntPtr buffer = Marshal.AllocHGlobal(alloc);
            try
            {
                if (GetExtendedTcpTable(buffer, ref alloc, false, 23, 3, 0) != 0) return;
                int entries = Marshal.ReadInt32(buffer, 0);
                // MIB_TCP6ROW_OWNER_PID is 56 bytes: LocalAddr(16) + LocalScopeId(4) + LocalPort(4) + RemoteAddr(16) + RemoteScopeId(4) + RemotePort(4) + State(4) + OwningPid(4)
                int maxEntries = Math.Max(0, (alloc - 4) / 56);
                int count = Math.Min(entries, maxEntries);

                for (int i = 0; i < count; i++)
                {
                    int row = 4 + i * 56;
                    uint rawPort = unchecked((uint)Marshal.ReadInt32(buffer, row + 20));
                    uint owningPid = unchecked((uint)Marshal.ReadInt32(buffer, row + 52));
                    ushort port = (ushort)(((rawPort & 0xFFu) << 8) | ((rawPort >> 8) & 0xFFu));
                    if (port == 0) continue;

                    AddEndpoint(result, owningPid, port, true);
                }
            }
            catch
            {
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void AddEndpoint(Dictionary<uint, List<PortEndpoint>> map, uint pid, ushort port, bool ipv6)
        {
            List<PortEndpoint> list;
            if (!map.TryGetValue(pid, out list))
            {
                list = new List<PortEndpoint>();
                map[pid] = list;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Port == port)
                {
                    // If a port is in both IPv4 and IPv6 tables, prefer IPv4 127.0.0.1 first.
                    if (!ipv6) list[i].PreferIpv6 = false;
                    return;
                }
            }
            list.Add(new PortEndpoint { Port = port, PreferIpv6 = ipv6 });
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
