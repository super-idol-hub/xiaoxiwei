using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace XiaoXiWei.Standalone
{
    internal sealed class RemoteMessage
    {
        public long id { get; set; }
        public string content { get; set; }
        public string created_at { get; set; }
    }

    internal sealed class RemoteConfiguration
    {
        private const string EmbeddedResourceName =
            "XiaoXiWei.Standalone.RemoteConfig.json";

        public int schemaVersion { get; set; }
        public string supabaseUrl { get; set; }
        public string supabaseKey { get; set; }
        public string deviceId { get; set; }
        public string deviceSecret { get; set; }
        public string deviceName { get; set; }

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(
                    Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath)
                        ?? string.Empty,
                    "xiaoxiwei-remote.json");
            }
        }

        public static RemoteConfiguration TryLoad(out string error)
        {
            string source;
            return TryLoad(out error, out source);
        }

        public static RemoteConfiguration TryLoad(
            out string error,
            out string source)
        {
            error = null;
            source = null;
            try
            {
                string json;
                if (File.Exists(DefaultPath))
                {
                    json = File.ReadAllText(DefaultPath, Encoding.UTF8);
                    source = "external";
                }
                else
                {
                    using (Stream stream = Assembly.GetExecutingAssembly()
                        .GetManifestResourceStream(EmbeddedResourceName))
                    {
                        if (stream == null)
                        {
                            error = "未配置";
                            return null;
                        }

                        using (StreamReader reader = new StreamReader(
                            stream,
                            Encoding.UTF8,
                            true))
                        {
                            json = reader.ReadToEnd();
                        }
                    }
                    source = "embedded";
                }

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 32 * 1024;
                RemoteConfiguration configuration =
                    serializer.Deserialize<RemoteConfiguration>(
                        json);
                if (configuration == null
                    || string.IsNullOrWhiteSpace(configuration.supabaseUrl)
                    || string.IsNullOrWhiteSpace(configuration.supabaseKey)
                    || string.IsNullOrWhiteSpace(configuration.deviceId)
                    || string.IsNullOrWhiteSpace(configuration.deviceSecret))
                {
                    error = "配置无效";
                    return null;
                }

                Uri projectUri;
                if (!Uri.TryCreate(
                        configuration.supabaseUrl.TrimEnd('/'),
                        UriKind.Absolute,
                        out projectUri)
                    || !string.Equals(
                        projectUri.Scheme,
                        Uri.UriSchemeHttps,
                        StringComparison.OrdinalIgnoreCase)
                    || !projectUri.Host.EndsWith(
                        ".supabase.co",
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "Project URL 无效";
                    return null;
                }

                Guid deviceId;
                if (!Guid.TryParse(configuration.deviceId, out deviceId)
                    || configuration.deviceSecret.Length < 32)
                {
                    error = "设备密钥无效";
                    return null;
                }

                configuration.supabaseUrl = projectUri.GetLeftPart(UriPartial.Authority);
                configuration.supabaseKey = configuration.supabaseKey.Trim();
                configuration.deviceId = deviceId.ToString();
                return configuration;
            }
            catch
            {
                error = "无法读取配置";
                source = null;
                return null;
            }
        }

        public static int RunSelfTest(string reportPath)
        {
            string error;
            string source;
            RemoteConfiguration configuration = TryLoad(out error, out source);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> report =
                new Dictionary<string, object>();
            report["ok"] = configuration != null;
            report["source"] = source ?? string.Empty;
            report["error"] = error ?? string.Empty;
            report["deviceId"] =
                configuration == null ? string.Empty : configuration.deviceId;
            report["deviceName"] =
                configuration == null ? string.Empty : configuration.deviceName;
            report["secretLength"] =
                configuration == null
                    || configuration.deviceSecret == null
                    ? 0
                    : configuration.deviceSecret.Length;

            string directory = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(
                reportPath,
                serializer.Serialize(report),
                new UTF8Encoding(false));
            return configuration == null ? 2 : 0;
        }

        public static int RunEmbeddedContentSelfTest(string reportPath)
        {
            Dictionary<string, object> report =
                new Dictionary<string, object>();
            Dictionary<string, object> skins =
                new Dictionary<string, object>();
            List<string> errors = new List<string>();

            string configurationError;
            string configurationSource;
            RemoteConfiguration configuration = TryLoad(
                out configurationError,
                out configurationSource);
            report["remoteConfigEmbedded"] =
                configuration != null
                && string.Equals(
                    configurationSource,
                    "embedded",
                    StringComparison.Ordinal);

            SkinCatalog catalog = SkinCatalog.Discover();
            string[] requiredSkinIds =
                new string[] { "built-in", "linan-princess", "huang-chengzi" };
            for (int index = 0; index < requiredSkinIds.Length; index++)
            {
                string requiredId = requiredSkinIds[index];
                SkinPack match = null;
                foreach (SkinPack candidate in catalog.Packs)
                {
                    if (string.Equals(
                        candidate.Id,
                        requiredId,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        match = candidate;
                        break;
                    }
                }

                byte[] archiveBytes = null;
                bool loaded = match != null
                    && match.TryLoadArchive(out archiveBytes);
                string validationError = string.Empty;
                bool valid = loaded;
                if (loaded && !match.IsBuiltIn)
                {
                    valid = FrameResource.ValidateExternalArchive(
                        archiveBytes,
                        out validationError);
                }
                if (!valid)
                {
                    errors.Add(
                        requiredId
                        + ": "
                        + (string.IsNullOrEmpty(validationError)
                            ? "missing or unreadable"
                            : validationError));
                }

                Dictionary<string, object> skin =
                    new Dictionary<string, object>();
                skin["loaded"] = loaded;
                skin["valid"] = valid;
                skin["archiveBytes"] =
                    loaded && archiveBytes != null
                        ? archiveBytes.Length
                        : 0;
                skins[requiredId] = skin;
            }

            report["skins"] = skins;
            report["errors"] = errors;
            report["ok"] = errors.Count == 0
                && configuration != null
                && string.Equals(
                    configurationSource,
                    "embedded",
                    StringComparison.Ordinal);

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 64 * 1024;
            string directory = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(
                reportPath,
                serializer.Serialize(report),
                new UTF8Encoding(false));
            return errors.Count == 0
                && configuration != null
                && string.Equals(
                    configurationSource,
                    "embedded",
                    StringComparison.Ordinal)
                ? 0
                : 2;
        }
    }

    internal sealed class RemoteMessageClient : IDisposable
    {
        private const int PollIntervalMilliseconds = 2600;
        private const int RetryIntervalMilliseconds = 7000;
        private const int RequestTimeoutMilliseconds = 9000;

        private readonly object _sync = new object();
        private readonly RemoteConfiguration _configuration;
        private readonly Func<RemoteMessage, bool> _onMessage;
        private readonly Action<string> _onStatus;
        private readonly JavaScriptSerializer _serializer;
        private readonly System.Threading.Timer _timer;
        private bool _disposed;
        private bool _polling;
        private DateTime _lastHeartbeatAt;

        private RemoteMessageClient(
            RemoteConfiguration configuration,
            Func<RemoteMessage, bool> onMessage,
            Action<string> onStatus)
        {
            _configuration = configuration;
            _onMessage = onMessage;
            _onStatus = onStatus;
            _serializer = new JavaScriptSerializer();
            _serializer.MaxJsonLength = 128 * 1024;
            _timer = new System.Threading.Timer(
                OnTimer,
                null,
                Timeout.Infinite,
                Timeout.Infinite);
        }

        public bool IsConfigured
        {
            get { return _configuration != null; }
        }

        public static RemoteMessageClient Create(
            Func<RemoteMessage, bool> onMessage,
            Action<string> onStatus)
        {
            string error;
            RemoteConfiguration configuration = RemoteConfiguration.TryLoad(out error);
            RemoteMessageClient client =
                new RemoteMessageClient(configuration, onMessage, onStatus);
            client.ReportStatus(configuration == null ? error : "正在连接");
            return client;
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_disposed || _configuration == null)
                {
                    return;
                }
                _timer.Change(400, Timeout.Infinite);
            }
        }

        private void OnTimer(object state)
        {
            lock (_sync)
            {
                if (_disposed || _configuration == null || _polling)
                {
                    return;
                }
                _polling = true;
            }

            bool succeeded = false;
            try
            {
                if ((DateTime.UtcNow - _lastHeartbeatAt).TotalSeconds >= 14.0)
                {
                    CallRpc(
                        "pet_heartbeat",
                        new Dictionary<string, object>
                        {
                            { "p_device_id", _configuration.deviceId },
                            { "p_secret", _configuration.deviceSecret }
                        });
                    _lastHeartbeatAt = DateTime.UtcNow;
                }

                string json = CallRpc(
                    "pull_pet_messages",
                    new Dictionary<string, object>
                    {
                        { "p_device_id", _configuration.deviceId },
                        { "p_secret", _configuration.deviceSecret },
                        { "p_limit", 5 }
                    });
                List<RemoteMessage> messages =
                    _serializer.Deserialize<List<RemoteMessage>>(json)
                    ?? new List<RemoteMessage>();
                foreach (RemoteMessage message in messages)
                {
                    if (message == null
                        || message.id <= 0
                        || string.IsNullOrWhiteSpace(message.content)
                        || _onMessage == null
                        || !_onMessage(message))
                    {
                        continue;
                    }
                    Acknowledge(message.id);
                }
                succeeded = true;
                ReportStatus("已连接");
            }
            catch
            {
                ReportStatus("连接中断，自动重试");
            }
            finally
            {
                lock (_sync)
                {
                    _polling = false;
                    if (!_disposed)
                    {
                        _timer.Change(
                            succeeded
                                ? PollIntervalMilliseconds
                                : RetryIntervalMilliseconds,
                            Timeout.Infinite);
                    }
                }
            }
        }

        private void Acknowledge(long messageId)
        {
            try
            {
                CallRpc(
                    "ack_pet_message",
                    new Dictionary<string, object>
                    {
                        { "p_device_id", _configuration.deviceId },
                        { "p_secret", _configuration.deviceSecret },
                        { "p_message_id", messageId }
                    });
            }
            catch
            {
                // The 30-second lease makes an unacknowledged message available again.
            }
        }

        private string CallRpc(
            string functionName,
            Dictionary<string, object> body)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            }
            catch
            {
                // TLS 1.2 is unavailable only on unsupported Windows versions.
            }

            string endpoint = _configuration.supabaseUrl
                + "/rest/v1/rpc/"
                + Uri.EscapeDataString(functionName);
            byte[] payload = Encoding.UTF8.GetBytes(_serializer.Serialize(body));
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json";
            request.Timeout = RequestTimeoutMilliseconds;
            request.ReadWriteTimeout = RequestTimeoutMilliseconds;
            request.UserAgent = "XiaoXiWeiPet/3.0.6 Remote";
            request.Headers["apikey"] = _configuration.supabaseKey;
            request.Headers["Authorization"] =
                "Bearer " + _configuration.supabaseKey;
            request.ContentLength = payload.Length;

            using (Stream output = request.GetRequestStream())
            {
                output.Write(payload, 0, payload.Length);
            }
            using (HttpWebResponse response =
                (HttpWebResponse)request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(input, Encoding.UTF8))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new InvalidDataException(
                        "Unexpected Supabase status: "
                        + ((int)response.StatusCode).ToString(
                            CultureInfo.InvariantCulture));
                }
                return reader.ReadToEnd();
            }
        }

        private void ReportStatus(string status)
        {
            if (_onStatus == null)
            {
                return;
            }
            try
            {
                _onStatus(status);
            }
            catch
            {
                // Status reporting must never destabilize the polling thread.
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            _timer.Dispose();
        }
    }
}
