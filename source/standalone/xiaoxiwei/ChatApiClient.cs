using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace XiaoXiWei.Standalone
{
    internal sealed class ChatConversationMessage
    {
        public string role { get; set; }
        public string content { get; set; }
    }

    internal sealed class HumanChatMessage
    {
        public long id { get; set; }
        public string content { get; set; }
        public string created_at { get; set; }
    }

    internal sealed class ChatServiceResponse
    {
        public string mode { get; set; }
        public string reply { get; set; }
        public string sessionId { get; set; }
        public string status { get; set; }
        public string error { get; set; }
        public List<HumanChatMessage> messages { get; set; }
    }

    internal sealed class ChatRequestPayload
    {
        public string action { get; set; }
        public string deviceId { get; set; }
        public string deviceSecret { get; set; }
        public string message { get; set; }
        public List<ChatConversationMessage> history { get; set; }
        public string sessionId { get; set; }
    }

    internal sealed class ChatApiClient
    {
        private const int RequestTimeoutMilliseconds = 45000;
        private readonly RemoteConfiguration _configuration;
        private readonly JavaScriptSerializer _serializer;

        public ChatApiClient()
        {
            string error;
            _configuration = RemoteConfiguration.TryLoad(out error);
            _serializer = new JavaScriptSerializer();
            _serializer.MaxJsonLength = 256 * 1024;
        }

        public bool IsConfigured
        {
            get { return _configuration != null; }
        }

        public ChatServiceResponse SendAi(
            string message,
            List<ChatConversationMessage> history)
        {
            return Send("ai", message, history, null);
        }

        public ChatServiceResponse TransferToHuman(
            string message,
            List<ChatConversationMessage> history,
            string sessionId)
        {
            return Send("transfer", message, history, sessionId);
        }

        public ChatServiceResponse SendHumanMessage(
            string message,
            string sessionId)
        {
            return Send(
                "human-message",
                message,
                new List<ChatConversationMessage>(),
                sessionId);
        }

        public ChatServiceResponse PollHuman(string sessionId)
        {
            return Send(
                "poll-human",
                string.Empty,
                new List<ChatConversationMessage>(),
                sessionId);
        }

        public ChatServiceResponse CloseHuman(string sessionId)
        {
            return Send(
                "close-human",
                string.Empty,
                new List<ChatConversationMessage>(),
                sessionId);
        }

        private ChatServiceResponse Send(
            string action,
            string message,
            List<ChatConversationMessage> history,
            string sessionId)
        {
            if (_configuration == null)
            {
                throw new InvalidOperationException("聊天服务尚未配置。");
            }

            string endpoint = _configuration.supabaseUrl.TrimEnd('/')
                + "/functions/v1/xiaoxiwei-chat";
            ChatRequestPayload payload = new ChatRequestPayload
            {
                action = action,
                deviceId = _configuration.deviceId,
                deviceSecret = _configuration.deviceSecret,
                message = message ?? string.Empty,
                history = history ?? new List<ChatConversationMessage>(),
                sessionId = sessionId ?? string.Empty
            };
            byte[] body = Encoding.UTF8.GetBytes(_serializer.Serialize(payload));

            ServicePointManager.SecurityProtocol =
                ServicePointManager.SecurityProtocol
                | SecurityProtocolType.Tls12;

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.Accept = "application/json";
            request.Timeout = RequestTimeoutMilliseconds;
            request.ReadWriteTimeout = RequestTimeoutMilliseconds;
            request.UserAgent = "XiaoXiWeiPet/3.0.6 Chat";
            request.Headers["apikey"] = _configuration.supabaseKey;
            request.Headers["Authorization"] =
                "Bearer " + _configuration.supabaseKey;
            request.ContentLength = body.Length;

            using (Stream requestStream = request.GetRequestStream())
            {
                requestStream.Write(body, 0, body.Length);
            }

            try
            {
                using (HttpWebResponse response =
                    (HttpWebResponse)request.GetResponse())
                {
                    return ReadResponse(response);
                }
            }
            catch (WebException exception)
            {
                HttpWebResponse errorResponse =
                    exception.Response as HttpWebResponse;
                if (errorResponse == null)
                {
                    throw new InvalidOperationException(
                        "无法连接聊天服务，请检查网络。",
                        exception);
                }

                using (errorResponse)
                {
                    ChatServiceResponse result = ReadResponse(errorResponse);
                    string messageText =
                        result == null || string.IsNullOrWhiteSpace(result.error)
                            ? "聊天服务暂时不可用。"
                            : result.error;
                    throw new InvalidOperationException(messageText, exception);
                }
            }
        }

        private ChatServiceResponse ReadResponse(HttpWebResponse response)
        {
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(
                stream,
                Encoding.UTF8,
                true))
            {
                string json = reader.ReadToEnd();
                ChatServiceResponse result =
                    _serializer.Deserialize<ChatServiceResponse>(json)
                    ?? new ChatServiceResponse();
                if ((int)response.StatusCode >= 400)
                {
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(result.error)
                            ? "聊天服务暂时不可用。"
                            : result.error);
                }
                if (result.messages == null)
                {
                    result.messages = new List<HumanChatMessage>();
                }
                return result;
            }
        }
    }
}
