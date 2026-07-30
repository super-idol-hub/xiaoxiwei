import { createClient } from "npm:@supabase/supabase-js@2";

type ChatRole = "user" | "assistant";

type ChatMessage = {
  role: ChatRole;
  content: string;
};

type RequestBody = {
  action?: "ai" | "transfer" | "human-message" | "poll-human" | "close-human";
  deviceId?: string;
  deviceSecret?: string;
  message?: string;
  history?: ChatMessage[];
  sessionId?: string;
};

const corsHeaders = {
  "access-control-allow-origin": "*",
  "access-control-allow-headers": "authorization, apikey, content-type",
  "access-control-allow-methods": "POST, OPTIONS",
};

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: {
      ...corsHeaders,
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store",
    },
  });

const trimText = (value: unknown, max: number) =>
  typeof value === "string" ? value.trim().slice(0, max) : "";

const sanitizeHistory = (value: unknown): ChatMessage[] => {
  if (!Array.isArray(value)) return [];
  return value
    .filter((item) => item && (item.role === "user" || item.role === "assistant"))
    .map((item) => ({
      role: item.role as ChatRole,
      content: trimText(item.content, 800),
    }))
    .filter((item) => item.content)
    .slice(-12);
};

const getSecretKey = () => {
  const modern = Deno.env.get("SUPABASE_SECRET_KEYS");
  if (modern) {
    const keys = JSON.parse(modern);
    if (keys.default) return keys.default as string;
  }
  return Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") || "";
};

const admin = createClient(
  Deno.env.get("SUPABASE_URL") || "",
  getSecretKey(),
  { auth: { persistSession: false, autoRefreshToken: false } },
);

const verifyDevice = async (deviceId: string, deviceSecret: string) => {
  const { data, error } = await admin.rpc("pet_device_authorized", {
    p_device_id: deviceId,
    p_secret: deviceSecret,
  });
  if (error) throw error;
  return data === true;
};

const getOpenSession = async (deviceId: string, requestedId = "") => {
  let query = admin
    .from("pet_support_sessions")
    .select("id,status,created_at,updated_at")
    .eq("device_id", deviceId)
    .eq("status", "open");
  if (requestedId) query = query.eq("id", requestedId);
  const { data, error } = await query
    .order("updated_at", { ascending: false })
    .limit(1)
    .maybeSingle();
  if (error) throw error;
  return data;
};

const touchSession = async (sessionId: string) => {
  const { error } = await admin
    .from("pet_support_sessions")
    .update({ updated_at: new Date().toISOString() })
    .eq("id", sessionId);
  if (error) throw error;
};

const insertSupportMessages = async (
  sessionId: string,
  messages: Array<{ sender: string; content: string }>,
) => {
  if (!messages.length) return;
  const { error } = await admin.from("pet_support_messages").insert(
    messages.map((message) => ({
      session_id: sessionId,
      sender: message.sender,
      content: message.content,
    })),
  );
  if (error) throw error;
  await touchSession(sessionId);
};

const callQwen = async (message: string, history: ChatMessage[]) => {
  const apiKey = Deno.env.get("QWEN_API_KEY") || "";
  if (!apiKey) throw new Error("QWEN_API_KEY is not configured");

  const baseUrl = (
    Deno.env.get("QWEN_BASE_URL") ||
    "https://dashscope.aliyuncs.com/compatible-mode/v1"
  ).replace(/\/+$/, "");
  const model = Deno.env.get("QWEN_MODEL") || "qwen-plus";
  const endpoint = baseUrl.endsWith("/chat/completions")
    ? baseUrl
    : `${baseUrl}/chat/completions`;

  const response = await fetch(endpoint, {
    method: "POST",
    headers: {
      authorization: `Bearer ${apiKey}`,
      "content-type": "application/json",
    },
    body: JSON.stringify({
      model,
      messages: [
        {
          role: "system",
          content:
            "你是桌面宠物“小曦薇”的 AI 聊天伙伴。你温柔、自然、活泼，回答简洁，通常不超过120个汉字。你不是田曦薇本人，不得声称自己是真人、艺人本人或其官方团队。不要编造私生活、行程、联系方式或未公开事实。遇到危险、自伤、医疗、法律或财务问题时，清楚说明能力边界并建议寻求专业帮助。用户要求联系真人时，提示她点击“转人工”。",
        },
        ...history,
        { role: "user", content: message },
      ],
      temperature: 0.82,
      max_tokens: 400,
      enable_thinking: false,
    }),
  });

  const payload = await response.json().catch(() => ({}));
  if (!response.ok) {
    const detail =
      payload?.error?.message || payload?.message || `Qwen HTTP ${response.status}`;
    throw new Error(detail);
  }

  const reply = trimText(payload?.choices?.[0]?.message?.content, 1200);
  if (!reply) throw new Error("Qwen returned an empty response");
  return reply;
};

Deno.serve(async (request) => {
  if (request.method === "OPTIONS") {
    return new Response("ok", { headers: corsHeaders });
  }
  if (request.method !== "POST") return json({ error: "Method not allowed" }, 405);

  try {
    const body = (await request.json()) as RequestBody;
    const action = body.action || "ai";
    const deviceId = trimText(body.deviceId, 80);
    const deviceSecret = trimText(body.deviceSecret, 200);
    const message = trimText(body.message, 1200);
    const history = sanitizeHistory(body.history);
    const requestedSessionId = trimText(body.sessionId, 80);

    if (!deviceId || !deviceSecret) {
      return json({ error: "Device credentials are required" }, 401);
    }
    if (!(await verifyDevice(deviceId, deviceSecret))) {
      return json({ error: "Device authentication failed" }, 401);
    }

    if (action === "ai") {
      if (!message) return json({ error: "Message is required" }, 400);
      const { data: allowed, error: quotaError } = await admin.rpc(
        "consume_pet_ai_quota",
        {
          p_device_id: deviceId,
          p_secret: deviceSecret,
          p_daily_limit: 200,
        },
      );
      if (quotaError) throw quotaError;
      if (allowed !== true) {
        return json(
          { error: "今天聊得有点多啦，明天再来找小曦薇吧。" },
          429,
        );
      }
      const reply = await callQwen(message, history);
      return json({ mode: "ai", reply });
    }

    if (action === "transfer") {
      let session = await getOpenSession(deviceId, requestedSessionId);
      let created = false;
      if (!session) {
        const { data, error } = await admin
          .from("pet_support_sessions")
          .insert({ device_id: deviceId, status: "open" })
          .select("id,status,created_at,updated_at")
          .single();
        if (error) throw error;
        session = data;
        created = true;
      }

      const transcript = created
        ? history.map((item) => ({
            sender: item.role === "user" ? "user" : "assistant",
            content: item.content,
          }))
        : [];
      transcript.push({
        sender: "system",
        content: "用户已请求转接人工。",
      });
      const lastTranscriptItem = transcript[transcript.length - 1];
      if (
        message &&
        !(
          lastTranscriptItem?.sender === "user" &&
          lastTranscriptItem?.content === message
        )
      ) {
        transcript.push({ sender: "user", content: message });
      }
      await insertSupportMessages(session.id, transcript);

      return json({
        mode: "human",
        sessionId: session.id,
        status: "waiting",
      });
    }

    if (action === "human-message") {
      if (!message) return json({ error: "Message is required" }, 400);
      const session = await getOpenSession(deviceId, requestedSessionId);
      if (!session) return json({ error: "人工会话已经结束" }, 404);
      await insertSupportMessages(session.id, [
        { sender: "user", content: message },
      ]);
      return json({ mode: "human", sessionId: session.id, status: "sent" });
    }

    if (action === "poll-human") {
      const session = await getOpenSession(deviceId, requestedSessionId);
      if (!session) {
        return json({ mode: "ai", status: "closed", messages: [] });
      }
      const { data, error } = await admin
        .from("pet_support_messages")
        .select("id,content,created_at")
        .eq("session_id", session.id)
        .eq("sender", "operator")
        .is("delivered_at", null)
        .order("created_at")
        .limit(20);
      if (error) throw error;

      const ids = (data || []).map((item) => item.id);
      if (ids.length) {
        const { error: deliveryError } = await admin
          .from("pet_support_messages")
          .update({ delivered_at: new Date().toISOString() })
          .in("id", ids);
        if (deliveryError) throw deliveryError;
      }
      return json({
        mode: "human",
        sessionId: session.id,
        status: "open",
        messages: data || [],
      });
    }

    if (action === "close-human") {
      const session = await getOpenSession(deviceId, requestedSessionId);
      if (session) {
        const { error } = await admin
          .from("pet_support_sessions")
          .update({ status: "closed", updated_at: new Date().toISOString() })
          .eq("id", session.id);
        if (error) throw error;
      }
      return json({ mode: "ai", status: "closed" });
    }

    return json({ error: "Unknown action" }, 400);
  } catch (error) {
    console.error("xiaoxiwei-chat", error);
    return json(
      {
        error:
          error instanceof Error
            ? error.message
            : "聊天服务暂时不可用，请稍后再试。",
      },
      500,
    );
  }
});
