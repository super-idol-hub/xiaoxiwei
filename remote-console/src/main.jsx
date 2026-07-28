import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { createClient } from "@supabase/supabase-js";
import "./styles.css";

const HISTORY_KEY = "xiaoxiwei.remote.history.v1";
const LEGACY_REMOTE_KEY = "xiaoxiwei.remote.v1";
const MAX_MESSAGE_LENGTH = 300;
const runtime = window.XIAOXIWEI_CONFIG || {};
const accountName = runtime.accountName || "xiaoxiwei";

localStorage.removeItem(LEGACY_REMOTE_KEY);

const supabase = createClient(runtime.supabaseUrl, runtime.supabaseKey, {
  auth: {
    persistSession: true,
    autoRefreshToken: true,
    detectSessionInUrl: false,
  },
});

function safeJsonParse(value, fallback) {
  try {
    return JSON.parse(value);
  } catch {
    return fallback;
  }
}

function loadHistory() {
  const value = safeJsonParse(localStorage.getItem(HISTORY_KEY), []);
  return Array.isArray(value) ? value.slice(0, 8) : [];
}

async function rpc(functionName, body = {}) {
  const { data, error } = await supabase.rpc(functionName, body);
  if (error) {
    if (/jwt|session|login|authenticated/i.test(error.message)) {
      throw new Error("登录已过期，请重新登录。");
    }
    if (/access denied|not bound/i.test(error.message)) {
      throw new Error("这个账号没有绑定小曦薇。");
    }
    throw new Error(error.message || "请求失败，请稍后重试。");
  }
  return data;
}

function Icon({ name, size = 20 }) {
  const paths = {
    send: (
      <>
        <path d="m22 2-7 20-4-9-9-4Z" />
        <path d="M22 2 11 13" />
      </>
    ),
    user: (
      <>
        <circle cx="12" cy="8" r="4" />
        <path d="M4.5 21a7.5 7.5 0 0 1 15 0" />
      </>
    ),
    lock: (
      <>
        <rect x="4" y="10" width="16" height="11" rx="3" />
        <path d="M8 10V7a4 4 0 0 1 8 0v3" />
      </>
    ),
    logout: (
      <>
        <path d="M10 17l5-5-5-5M15 12H3" />
        <path d="M14 3h5a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-5" />
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

function LoginScreen() {
  const [account, setAccount] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const signIn = async (event) => {
    event.preventDefault();
    setBusy(true);
    setError("");
    try {
      if (account.trim().toLowerCase() !== accountName.toLowerCase()) {
        throw new Error("账号或密码不正确。");
      }
      const { error: signInError } = await supabase.auth.signInWithPassword({
        email: runtime.loginEmail,
        password,
      });
      if (signInError) throw new Error("账号或密码不正确。");
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "登录失败，请稍后重试。");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="login-shell">
      <section className="login-card" aria-labelledby="login-title">
        <div className="login-character" aria-hidden="true">
          <div className="login-orbit" />
          <img src="./xiwei-idle.png" alt="" />
        </div>
        <div className="login-content">
          <div className="login-mark"><Icon name="lock" size={24} /></div>
          <p className="login-brand">小曦薇 · 遥控台</p>
          <h1 id="login-title">欢迎回来</h1>
          <p className="login-copy">登录后才能给小曦薇发送消息。</p>
          <form onSubmit={signIn}>
            <label>
              <span>账号</span>
              <input
                value={account}
                onChange={(event) => setAccount(event.target.value)}
                autoComplete="username"
                placeholder="请输入账号"
                required
              />
            </label>
            <label>
              <span>密码</span>
              <input
                type="password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                autoComplete="current-password"
                placeholder="请输入密码"
                required
              />
            </label>
            {error && <p className="form-error" role="alert">{error}</p>}
            <button className="primary-button login-button" disabled={busy} type="submit">
              <Icon name="lock" size={18} />
              {busy ? "正在登录…" : "登录遥控台"}
            </button>
          </form>
          <p className="login-note">账号登录不绑定当前电脑，手机或新设备也可以使用。</p>
        </div>
      </section>
    </div>
  );
}

function AccountDialog({ onClose, onSignOut }) {
  return (
    <div className="dialog-backdrop" role="presentation">
      <section className="dialog account-dialog" role="dialog" aria-modal="true" aria-labelledby="account-title">
        <button className="icon-button dialog-close" onClick={onClose} aria-label="关闭账户设置">
          <Icon name="close" />
        </button>
        <div className="dialog-heading">
          <div className="setup-mark"><Icon name="user" size={25} /></div>
          <div>
            <h2 id="account-title">账户设置</h2>
            <p>当前账号已安全连接到专属小曦薇。</p>
          </div>
        </div>
        <div className="account-row">
          <span>当前账号</span>
          <strong>{accountName}</strong>
        </div>
        <button className="secondary-button signout-button" onClick={onSignOut}>
          <Icon name="logout" size={18} />
          退出登录
        </button>
      </section>
    </div>
  );
}

function ConsoleApp() {
  const [accountOpen, setAccountOpen] = useState(false);
  const [deviceName, setDeviceName] = useState("我的小曦薇");
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
    try {
      const result = await rpc("get_my_pet_status");
      const status = Array.isArray(result) ? result[0] : result;
      setDeviceName(status?.device_name || "我的小曦薇");
      setConnection({
        state: status?.is_online ? "online" : "offline",
        lastSeen: status?.last_seen_at || null,
      });
    } catch {
      setConnection((current) => ({ ...current, state: "error" }));
    }
  }, []);

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
    if (!value || sending) return;
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
      const response = await rpc("send_my_pet_message", {
        p_content: value,
        p_client_message_id: clientMessageId,
      });
      const messageId = typeof response === "number" ? response : response?.message_id ?? response;
      const sent = { ...optimistic, serverId: messageId, status: "sent" };
      persistHistory([sent, ...history.filter((item) => item.id !== clientMessageId)]);
      setMessage("");
      setNotice("消息已送出");
      textareaRef.current?.focus();
      window.setTimeout(async () => {
        try {
          const statusResult = await rpc("get_my_pet_message_status", {
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
          // Delivery status is helpful but does not change a successful send.
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

  const signOut = async () => {
    setAccountOpen(false);
    persistHistory([]);
    await supabase.auth.signOut();
  };

  return (
    <div className="app-shell">
      <header className="topbar">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">曦</span>
          <span>小曦薇 <i>·</i> 遥控台</span>
        </div>
        <button className="settings-button" onClick={() => setAccountOpen(true)}>
          <Icon name="user" size={18} />
          账户
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
            <h1>{deviceName}</h1>
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
                disabled={!message.trim() || sending}
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
        <button className="footer-settings" onClick={() => setAccountOpen(true)}>
          <Icon name="user" size={16} />
          账户
        </button>
      </footer>

      {notice && <div className="toast" role="status">{notice}</div>}
      {accountOpen && <AccountDialog onClose={() => setAccountOpen(false)} onSignOut={signOut} />}
    </div>
  );
}

function Root() {
  const [session, setSession] = useState(undefined);

  useEffect(() => {
    supabase.auth.getSession().then(({ data }) => setSession(data.session));
    const { data: listener } = supabase.auth.onAuthStateChange((_event, nextSession) => {
      setSession(nextSession);
    });
    return () => listener.subscription.unsubscribe();
  }, []);

  if (session === undefined) {
    return (
      <div className="auth-loading" role="status">
        <span className="brand-mark" aria-hidden="true">曦</span>
        正在安全连接…
      </div>
    );
  }

  return session ? <ConsoleApp /> : <LoginScreen />;
}

createRoot(document.getElementById("root")).render(
  <React.StrictMode>
    <Root />
  </React.StrictMode>,
);
