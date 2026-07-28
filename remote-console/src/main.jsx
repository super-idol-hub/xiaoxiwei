import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import "./styles.css";

const STORAGE_KEY = "xiaoxiwei.remote.v1";
const HISTORY_KEY = "xiaoxiwei.remote.history.v1";
const MAX_MESSAGE_LENGTH = 300;

function safeJsonParse(value, fallback) {
  try {
    return JSON.parse(value);
  } catch {
    return fallback;
  }
}

function loadRemote() {
  return safeJsonParse(localStorage.getItem(STORAGE_KEY), null);
}

function loadHistory() {
  const value = safeJsonParse(localStorage.getItem(HISTORY_KEY), []);
  return Array.isArray(value) ? value.slice(0, 8) : [];
}

function bytesToBase64Url(bytes) {
  let binary = "";
  bytes.forEach((value) => {
    binary += String.fromCharCode(value);
  });
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}

function makeSecret() {
  const bytes = new Uint8Array(32);
  crypto.getRandomValues(bytes);
  return bytesToBase64Url(bytes);
}

function normalizeProjectUrl(value) {
  return value.trim().replace(/\/+$/, "");
}

async function rpc(remote, functionName, body) {
  const response = await fetch(
    `${normalizeProjectUrl(remote.supabaseUrl)}/rest/v1/rpc/${functionName}`,
    {
      method: "POST",
      headers: {
        apikey: remote.supabaseKey,
        Authorization: `Bearer ${remote.supabaseKey}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify(body),
    },
  );

  const text = await response.text();
  if (!response.ok) {
    let detail = text;
    try {
      const parsed = JSON.parse(text);
      detail = parsed.message || parsed.hint || text;
    } catch {
      // Keep the response text.
    }
    throw new Error(detail || `请求失败（${response.status}）`);
  }
  if (!text) return null;
  return JSON.parse(text);
}

function Icon({ name, size = 20 }) {
  const paths = {
    send: (
      <>
        <path d="m22 2-7 20-4-9-9-4Z" />
        <path d="M22 2 11 13" />
      </>
    ),
    settings: (
      <>
        <path d="M12 15.5a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7Z" />
        <path d="M19.4 15a1.7 1.7 0 0 0 .34 1.88l.06.06-2.83 2.83-.06-.06a1.7 1.7 0 0 0-1.88-.34 1.7 1.7 0 0 0-1.03 1.55V21h-4v-.08a1.7 1.7 0 0 0-1.03-1.55 1.7 1.7 0 0 0-1.88.34l-.06.06-2.83-2.83.06-.06A1.7 1.7 0 0 0 4.6 15a1.7 1.7 0 0 0-1.55-1.03H3v-4h.05A1.7 1.7 0 0 0 4.6 8.94a1.7 1.7 0 0 0-.34-1.88L4.2 7l2.83-2.83.06.06a1.7 1.7 0 0 0 1.88.34A1.7 1.7 0 0 0 10 3.02V3h4v.02a1.7 1.7 0 0 0 1.03 1.55 1.7 1.7 0 0 0 1.88-.34l.06-.06L19.8 7l-.06.06a1.7 1.7 0 0 0-.34 1.88A1.7 1.7 0 0 0 20.95 10H21v4h-.05A1.7 1.7 0 0 0 19.4 15Z" />
      </>
    ),
    check: <path d="m5 12 4 4L19 6" />,
    message: (
      <>
        <path d="M21 15a4 4 0 0 1-4 4H8l-5 3V7a4 4 0 0 1 4-4h10a4 4 0 0 1 4 4Z" />
        <path d="M8 10h.01M12 10h.01M16 10h.01" />
      </>
    ),
    close: <path d="m6 6 12 12M18 6 6 18" />,
    download: (
      <>
        <path d="M12 3v12" />
        <path d="m7 10 5 5 5-5" />
        <path d="M5 21h14" />
      </>
    ),
  };

  return (
    <svg
      aria-hidden="true"
      className="icon"
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      {paths[name]}
    </svg>
  );
}

function configPayload(remote) {
  return {
    schemaVersion: 1,
    supabaseUrl: remote.supabaseUrl,
    supabaseKey: remote.supabaseKey,
    deviceId: remote.deviceId,
    deviceSecret: remote.deviceSecret,
    deviceName: remote.deviceName,
  };
}

function saveFile(remote) {
  const payload = configPayload(remote);
  const blob = new Blob([`${JSON.stringify(payload, null, 2)}\n`], {
    type: "application/json;charset=utf-8",
  });
  const href = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = href;
  link.download = "xiaoxiwei-remote.json";
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(href);
}

function SetupDialog({ initial, onClose, onSaved }) {
  const runtime = window.XIAOXIWEI_CONFIG || {};
  const [supabaseUrl, setSupabaseUrl] = useState(
    initial?.supabaseUrl || runtime.supabaseUrl || "",
  );
  const [supabaseKey, setSupabaseKey] = useState(
    initial?.supabaseKey || runtime.supabaseKey || "",
  );
  const [deviceName, setDeviceName] = useState(initial?.deviceName || "我的小曦薇");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const configure = async (event) => {
    event.preventDefault();
    setBusy(true);
    setError("");
    try {
      if (!supabaseUrl.trim() || !supabaseKey.trim()) {
        throw new Error("请填写 Supabase Project URL 和公开密钥。");
      }
      const remote = initial?.deviceId
        ? {
            ...initial,
            supabaseUrl: normalizeProjectUrl(supabaseUrl),
            supabaseKey: supabaseKey.trim(),
            deviceName: deviceName.trim() || "我的小曦薇",
          }
        : {
            supabaseUrl: normalizeProjectUrl(supabaseUrl),
            supabaseKey: supabaseKey.trim(),
            deviceId: crypto.randomUUID(),
            deviceSecret: makeSecret(),
            deviceName: deviceName.trim() || "我的小曦薇",
          };

      await rpc(remote, "register_pet_device", {
        p_device_id: remote.deviceId,
        p_name: remote.deviceName,
        p_secret: remote.deviceSecret,
      });
      localStorage.setItem(STORAGE_KEY, JSON.stringify(remote));
      saveFile(remote);
      onSaved(remote);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "配置失败，请稍后再试。");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="dialog-backdrop" role="presentation">
      <section className="dialog" role="dialog" aria-modal="true" aria-labelledby="setup-title">
        <button className="icon-button dialog-close" onClick={onClose} aria-label="关闭设置">
          <Icon name="close" />
        </button>
        <div className="dialog-heading">
          <div className="setup-mark"><Icon name="message" size={25} /></div>
          <div>
            <h2 id="setup-title">{initial ? "连接设置" : "连接你的小曦薇"}</h2>
            <p>首次配置会生成专属设备密钥，并下载给桌宠使用的配置文件。</p>
          </div>
        </div>
        <form onSubmit={configure}>
          <label>
            <span>Supabase Project URL</span>
            <input
              type="url"
              value={supabaseUrl}
              onChange={(event) => setSupabaseUrl(event.target.value)}
              placeholder="https://xxxx.supabase.co"
              autoComplete="url"
              required
            />
          </label>
          <label>
            <span>Supabase 公开密钥</span>
            <input
              type="password"
              value={supabaseKey}
              onChange={(event) => setSupabaseKey(event.target.value)}
              placeholder="sb_publishable_… 或 anon key"
              autoComplete="off"
              required
            />
          </label>
          <label>
            <span>设备名称</span>
            <input
              value={deviceName}
              onChange={(event) => setDeviceName(event.target.value)}
              maxLength={40}
              placeholder="我的小曦薇"
            />
          </label>
          {error && <p className="form-error">{error}</p>}
          <button className="primary-button setup-button" disabled={busy} type="submit">
            <Icon name="download" />
            {busy ? "正在连接…" : initial ? "保存并重新下载配置" : "连接并下载配置"}
          </button>
          {initial && (
            <label>
              <span>配置 JSON（请勿分享）</span>
              <textarea
                aria-label="配置 JSON"
                readOnly
                rows={8}
                value={`${JSON.stringify(
                  configPayload({
                    ...initial,
                    supabaseUrl: normalizeProjectUrl(supabaseUrl),
                    supabaseKey: supabaseKey.trim(),
                    deviceName: deviceName.trim() || initial.deviceName,
                  }),
                  null,
                  2,
                )}\n`}
              />
            </label>
          )}
          <p className="privacy-note">
            请把下载的 <code>xiaoxiwei-remote.json</code> 放到新 EXE 同一目录。设备密钥不会上传到 GitHub。
          </p>
        </form>
      </section>
    </div>
  );
}

function App() {
  const [remote, setRemote] = useState(loadRemote);
  const [settingsOpen, setSettingsOpen] = useState(!loadRemote());
  const [message, setMessage] = useState("");
  const [history, setHistory] = useState(loadHistory);
  const [connection, setConnection] = useState({ state: "checking", lastSeen: null });
  const [sending, setSending] = useState(false);
  const [notice, setNotice] = useState("");
  const textareaRef = useRef(null);

  const persistHistory = useCallback((next) => {
    const trimmed = next.slice(0, 8);
    setHistory(trimmed);
    localStorage.setItem(HISTORY_KEY, JSON.stringify(trimmed));
  }, []);

  const checkStatus = useCallback(async () => {
    if (!remote) return;
    try {
      const result = await rpc(remote, "get_pet_status", {
        p_device_id: remote.deviceId,
        p_secret: remote.deviceSecret,
      });
      const status = Array.isArray(result) ? result[0] : result;
      setConnection({
        state: status?.is_online ? "online" : "offline",
        lastSeen: status?.last_seen_at || null,
      });
    } catch {
      setConnection((current) => ({ ...current, state: "error" }));
    }
  }, [remote]);

  useEffect(() => {
    checkStatus();
    const timer = window.setInterval(checkStatus, 8000);
    return () => window.clearInterval(timer);
  }, [checkStatus]);

  useEffect(() => {
    if (!notice) return undefined;
    const timer = window.setTimeout(() => setNotice(""), 3600);
    return () => window.clearTimeout(timer);
  }, [notice]);

  const statusCopy = useMemo(() => {
    if (connection.state === "online") return "设备在线";
    if (connection.state === "offline") return "设备离线";
    if (connection.state === "error") return "连接异常";
    return "正在检查";
  }, [connection.state]);

  const send = async (content = message) => {
    const value = content.trim();
    if (!remote || !value || sending) return;
    setSending(true);
    setNotice("");
    const clientMessageId = crypto.randomUUID();
    const optimistic = {
      id: clientMessageId,
      content: value,
      sentAt: new Date().toISOString(),
      status: "sending",
    };
    persistHistory([optimistic, ...history.filter((item) => item.id !== clientMessageId)]);

    try {
      const response = await rpc(remote, "send_pet_message", {
        p_device_id: remote.deviceId,
        p_secret: remote.deviceSecret,
        p_content: value,
        p_client_message_id: clientMessageId,
      });
      const messageId = typeof response === "number" ? response : response?.message_id ?? response;
      const delivered = {
        ...optimistic,
        serverId: messageId,
        status: "sent",
      };
      persistHistory([delivered, ...history.filter((item) => item.id !== clientMessageId)]);
      setMessage("");
      setNotice("消息已送出");
      textareaRef.current?.focus();
      window.setTimeout(async () => {
        try {
          const statusResult = await rpc(remote, "get_pet_message_status", {
            p_device_id: remote.deviceId,
            p_secret: remote.deviceSecret,
            p_message_id: Number(messageId),
          });
          if (statusResult === "delivered") {
            const latest = loadHistory().map((item) =>
              item.id === clientMessageId ? { ...item, status: "delivered" } : item,
            );
            persistHistory(latest);
            setNotice("小曦薇已收到");
          }
        } catch {
          // A delivery receipt is helpful but does not change a successful send.
        }
      }, 2800);
    } catch (reason) {
      const failed = { ...optimistic, status: "failed" };
      persistHistory([failed, ...history.filter((item) => item.id !== clientMessageId)]);
      setNotice(reason instanceof Error ? reason.message : "发送失败");
    } finally {
      setSending(false);
    }
  };

  const onSaved = (next) => {
    setRemote(next);
    setSettingsOpen(false);
    setConnection({ state: "checking", lastSeen: null });
    setNotice("连接配置已保存");
  };

  return (
    <div className="app-shell">
      <header className="topbar">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">曦</span>
          <span>小曦薇 <i>·</i> 遥控台</span>
        </div>
        <button className="settings-button" onClick={() => setSettingsOpen(true)}>
          <Icon name="settings" size={18} />
          设置
        </button>
      </header>

      <main>
        <section className="control-layout" aria-label="消息控制">
          <aside className="pet-preview" aria-hidden="true">
            <div className="pet-orbit" />
            <img className="pet-model" src="./xiwei-idle.png" alt="" />
            <div className="preview-bubble">今天也要开心呀</div>
          </aside>

          <div className="composer-panel">
            <div className={`presence ${connection.state}`}>
              <span className="presence-dot" />
              <span>{statusCopy}</span>
            </div>
            <h1>{remote?.deviceName || "我的小曦薇"}</h1>
            <div className="composer">
              <textarea
                ref={textareaRef}
                value={message}
                onChange={(event) => setMessage(event.target.value.slice(0, MAX_MESSAGE_LENGTH))}
                onKeyDown={(event) => {
                  if ((event.ctrlKey || event.metaKey) && event.key === "Enter") send();
                }}
                maxLength={MAX_MESSAGE_LENGTH}
                placeholder="想让小曦薇说什么？"
                aria-label="消息内容"
              />
              <span className="counter">{message.length} / {MAX_MESSAGE_LENGTH}</span>
            </div>
            <div className="composer-actions">
              <button
                className="primary-button"
                onClick={() => send()}
                disabled={!message.trim() || sending || !remote}
              >
                <Icon name="send" />
                {sending ? "正在发送…" : "发送给小曦薇"}
              </button>
              <button className="secondary-button" onClick={() => setMessage("")} disabled={!message}>
                清空
              </button>
            </div>
            <p className="shortcut">Ctrl + Enter 快速发送</p>
          </div>
        </section>

        <section className="history-section" aria-labelledby="history-heading">
          <div className="section-heading">
            <h2 id="history-heading">最近发送</h2>
            {history.length > 0 && (
              <button className="text-button" onClick={() => persistHistory([])}>清除记录</button>
            )}
          </div>
          {history.length === 0 ? (
            <div className="empty-state">
              <Icon name="message" size={24} />
              <span>发送过的消息会出现在这里</span>
            </div>
          ) : (
            <div className="history-list">
              {history.map((item) => (
                <div className="history-row" key={item.id}>
                  <span className="history-icon"><Icon name="message" size={16} /></span>
                  <span className="history-content">{item.content}</span>
                  <span className={`delivery ${item.status}`}>
                    {item.status === "delivered" && <Icon name="check" size={15} />}
                    {item.status === "failed" ? "发送失败" :
                      item.status === "sending" ? "发送中" :
                      item.status === "delivered" ? "已送达" : "已发送"}
                  </span>
                  <button
                    className="resend-button"
                    onClick={() => send(item.content)}
                    aria-label={`再次发送：${item.content}`}
                    title="再次发送"
                  >
                    <Icon name="send" size={18} />
                  </button>
                </div>
              ))}
            </div>
          )}
        </section>
      </main>

      <footer>
        <button className="footer-settings" onClick={() => setSettingsOpen(true)}>
          <Icon name="settings" size={16} />
          设置
        </button>
      </footer>

      {notice && <div className="toast" role="status">{notice}</div>}
      {settingsOpen && (
        <SetupDialog
          initial={remote}
          onClose={() => setSettingsOpen(false)}
          onSaved={onSaved}
        />
      )}
    </div>
  );
}

createRoot(document.getElementById("root")).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
