# 蓝色大肥鱼 · 本机 HTTP API

给弹幕机、桌面端等本地程序调用。桌宠启动后自动在本机开启，**不依赖「连接 Agent」**，也不需要额外开关。

## 基本信息

| 项 | 值 |
| --- | --- |
| Base URL | `http://127.0.0.1:19265` |
| 绑定地址 | 仅本机环回，外网不可访问 |
| 编码 | UTF-8 |
| 请求体 | `Content-Type: application/json` |
| CORS | 已允许跨域（含 `OPTIONS` 预检） |

桌宠未运行、端口被占用或监听失败时，接口不可用。可先请求健康检查确认。失败原因有时会写入运行目录下的 `companion-errors.log`。

---

## GET `/health`

检查桌宠与本地模型是否就绪。

### 请求

```http
GET http://127.0.0.1:19265/health
```

`GET /` 与 `/health` 行为相同。

### 成功响应 `200`

```json
{
  "ok": true,
  "port": 19265,
  "modelAvailable": true,
  "modelEnabled": true,
  "busy": false,
  "status": "本地模型就绪",
  "webhookUrl": "http://127.0.0.1:3000/big-blue-speech",
  "actionWebhookUrl": "http://127.0.0.1:3000/big-blue-action"
}
```

| 字段 | 说明 |
| --- | --- |
| `modelAvailable` | 是否已找到可用的本地 GGUF |
| `modelEnabled` | 设置里是否启用本地模型 |
| `busy` | 当前是否正在推理或吐字占用中 |
| `status` | 模型侧状态文案 |
| `webhookUrl` | 当前配置的吐字回调地址；未配置时为空字符串 |
| `actionWebhookUrl` | 当前配置的动作回调地址；未配置时为空字符串 |

---

## Speech Webhook（桌宠 → 你的程序）

鱼鱼**主动开口**时（日常吐字、天气、吐字测试、请安、Agent `say` 等），会向你配置的本机 URL 发一次 `POST`。流式中间片段不推，只推最终完整句子。

**不会**对 HTTP 的 `/say`、`/say/stream`、`/chat`、`/chat/stream` 再推 webhook——这些接口的响应 / SSE 里已经有全文，避免同一句收两遍。

状态提示（如「正在查看天气」「请先启用模型」）**不会**推送。

### 配置回调

```http
POST http://127.0.0.1:19265/webhook
Content-Type: application/json

{
  "url": "http://127.0.0.1:3000/big-blue-speech"
}
```

| 字段 | 说明 |
| --- | --- |
| `url` | 本机回调地址；仅允许 `http(s)://127.0.0.1` 或 `localhost`。传 `""` 或 `null` 关闭 |

查询当前配置：

```http
GET http://127.0.0.1:19265/webhook
```

配置会写入桌宠的 `preferences.json`，下次启动仍有效。

### 桌宠推给你的正文

```http
POST http://127.0.0.1:3000/big-blue-speech
Content-Type: application/json

{
  "text": "唔，今天有点凉，主人记得多穿一点哦～",
  "source": "weather",
  "at": "2026-10-31T21:05:00.123+08:00"
}
```

| 字段 | 说明 |
| --- | --- |
| `text` | 气泡里要说的完整文字 |
| `source` | 来源，见下表 |
| `at` | 推送时间（带时区） |

| `source` | 含义 |
| --- | --- |
| `auto` | 日常自动吐字 |
| `weather` | 天气播报 |
| `test` | 右键「吐字测试」 |
| `say` | Agent / MCP 的 `say`（HTTP `/say` 不走 webhook） |
| `local` | Agent 触发的本地吐字 |
| `greeting` | 启动请安等问候 |
| `speech` | 其他气泡显示 |

Webhook 为异步、最多约 5 秒超时；你的服务失败不会打断鱼鱼说话。失败可能记入 `companion-errors.log`。

### Node.js 接收示例

```js
import http from 'http';

http.createServer((req, res) => {
  if (req.method === 'POST' && req.url === '/big-blue-speech') {
    let body = '';
    req.on('data', c => body += c);
    req.on('end', () => {
      const { text, source, at } = JSON.parse(body);
      console.log(`[${source}]`, text, at);
      // 在这里转给直播间 / TTS / 弹幕机 UI …
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end('{"ok":true}');
    });
    return;
  }
  res.writeHead(404);
  res.end();
}).listen(3000, '127.0.0.1');

// 桌宠启动后注册一次：
await fetch('http://127.0.0.1:19265/webhook', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ url: 'http://127.0.0.1:3000/big-blue-speech' }),
});
```

---

## Action Webhook（桌宠 → 你的程序）

鱼鱼**开始播放任一动作**时（待机轮换、点击回应、定时事件、拖动、启动/退出、MCP `play_action` 等），会向你配置的本机 URL 发一次 `POST`。与吐字 webhook 分开配置，互不影响。

### 配置回调

```http
POST http://127.0.0.1:19265/webhook/action
Content-Type: application/json

{
  "url": "http://127.0.0.1:3000/big-blue-action"
}
```

| 字段 | 说明 |
| --- | --- |
| `url` | 本机回调地址；仅允许 `http(s)://127.0.0.1` 或 `localhost`。传 `""` 或 `null` 关闭 |

查询当前配置：

```http
GET http://127.0.0.1:19265/webhook/action
```

配置会写入桌宠的 `preferences.json`，下次启动仍有效。

### 桌宠推给你的正文

```http
POST http://127.0.0.1:3000/big-blue-action
Content-Type: application/json

{
  "action": "待机呼吸休闲",
  "pool": "Idle",
  "phase": "Idle",
  "mirror": false,
  "at": "2026-10-31T21:05:00.123+08:00"
}
```

| 字段 | 说明 |
| --- | --- |
| `action` | 动作精确名称（与 MCP `list_actions` 一致） |
| `pool` | 动作池：`Idle` / `Event` / `Click` / `Greeting` / `Daily` / `Drag` / `Disabled` 等 |
| `phase` | 本次播放阶段，见下表 |
| `mirror` | 是否水平镜像（移动去程/返程时常见） |
| `at` | 推送时间（带时区） |

| `phase` | 含义 |
| --- | --- |
| `Startup` | 启动行礼 |
| `Idle` / `PuppetIdle` | 待机 / 提线木偶固定待机 |
| `Event` | 普通定时事件 |
| `Click` | 点击回应 |
| `Drag` | 拖动悬空 |
| `Daily` | 每日固定事件（如刷牙） |
| `Outbound` / `BetweenMoves` / `Return` | 移动去程、中途待机、返程 |
| `External` | MCP / 外部排队动作 |
| `Exit` | 退出告别 |
| `Preview` | 预览播放 |

待机片段约每十几秒就会换一次，事件频率取决于设置里的动作间隔。Webhook 同样异步、约 5 秒超时；失败不打断动画，可能记入 `companion-errors.log`。

### Node.js 接收示例

```js
import http from 'http';

http.createServer((req, res) => {
  if (req.method === 'POST' && req.url === '/big-blue-action') {
    let body = '';
    req.on('data', c => body += c);
    req.on('end', () => {
      const { action, pool, phase, mirror, at } = JSON.parse(body);
      console.log(`[${phase}/${pool}]`, action, mirror, at);
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end('{"ok":true}');
    });
    return;
  }
  res.writeHead(404);
  res.end();
}).listen(3000, '127.0.0.1');

await fetch('http://127.0.0.1:19265/webhook/action', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ url: 'http://127.0.0.1:3000/big-blue-action' }),
});
```

---

## POST `/say`

让鱼鱼**原样念出**传入文字。不调用本地模型，不占显卡。

适合：直接播报弹幕原文、固定话术、自己后端已经生成好的回复。

### 请求

```http
POST http://127.0.0.1:19265/say
Content-Type: application/json

{
  "text": "大家好呀～"
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `text` | string | 是 | 1–4000 个字符，不能全空白 |

### 成功响应 `200`

```json
{
  "ok": true,
  "displayed": true,
  "text": "大家好呀～"
}
```

`displayed: true` 表示已进入气泡显示流程，不代表主人已经看完整句。

### 错误

| 状态码 | 情况 |
| --- | --- |
| `400` | `text` 缺失、空白或超长；或 JSON 无效 |
| `500` | 桌宠正在退出等内部错误 |

---

## POST `/say/stream`

与 `/say` 相同，但响应为 **SSE 流式吐字**（约 28 字/秒，与气泡动画同步）。`Content-Type: text/event-stream`。

### 事件

| event | 含义 |
| --- | --- |
| `start` | 开始，`text` 为完整原文 |
| `partial` | 当前已显示到的前缀（累计文本） |
| `done` | 吐字结束，`text` / `reply` 为完整原文 |
| `error` | 失败 |

```http
POST http://127.0.0.1:19265/say/stream
Content-Type: application/json

{"text":"大家好呀～"}
```

示例片段：

```
event: start
data: {"ok":true,"text":"大家好呀～","source":"say"}

event: partial
data: {"text":"大","source":"say"}

event: partial
data: {"text":"大家","source":"say"}

event: done
data: {"ok":true,"text":"大家好呀～","reply":"大家好呀～","source":"say"}
```

---

## POST `/chat`

把弹幕交给**本地 GGUF**，由鱼鱼生成一句回复并显示在气泡里。算力走本机（含 GPU，按桌宠供电/GPU 设置）。

适合：观众弹幕 → 鱼鱼本地回话。

### 请求

```http
POST http://127.0.0.1:19265/chat
Content-Type: application/json

{
  "text": "鱼鱼你好",
  "user": "观众昵称"
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `text` | string | 是 | 弹幕内容，1–1000 个字符，不能全空白 |
| `user` | string | 否 | 发送者昵称；超过 64 字符会截断 |

### 成功响应 `200`

```json
{
  "ok": true,
  "text": "鱼鱼你好",
  "user": "观众昵称",
  "reply": "唔，你好呀～鱼鱼在的！"
}
```

| 字段 | 说明 |
| --- | --- |
| `text` | 你传入的弹幕原文 |
| `user` | 你传入的昵称（未传则为 `null`） |
| `reply` | 本地模型生成并已开始显示的回复 |

本接口会等待生成为止（最长约 100 秒量级），请在桌面端设置足够超时。

### 错误

| 状态码 | 情况 |
| --- | --- |
| `400` | `text` 无效或 JSON 无效 |
| `503` | 本地模型未启用/未安装、供电暂停、忙碌冲突，或生成结果为空 |
| `500` | 其他内部错误 |

`503` 示例：

```json
{
  "ok": false,
  "error": "本地模型不可用、忙碌、已暂停，或未能生成回复。"
}
```

---

## POST `/chat/stream`

与 `/chat` 相同（本地模型生成），但响应为 **SSE**：模型边生成边推 `partial`（累计文本），最后 `done` 给整句。气泡同步流式显示。

### 事件

| event | 含义 |
| --- | --- |
| `start` | 已接受请求 |
| `partial` | 当前已生成的累计回复 |
| `done` | 完成，含完整 `reply` |
| `error` | 失败（模型不可用等） |

```http
POST http://127.0.0.1:19265/chat/stream
Content-Type: application/json

{"text":"鱼鱼你好","user":"观众昵称"}
```

### Node.js 读取示例

```js
async function chatStream(text, user) {
  const res = await fetch('http://127.0.0.1:19265/chat/stream', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text, user }),
  });
  const reader = res.body.getReader();
  const dec = new TextDecoder();
  let buf = '';
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    const parts = buf.split('\n\n');
    buf = parts.pop() ?? '';
    for (const part of parts) {
      const ev = part.match(/^event: (.+)$/m)?.[1];
      const data = part.match(/^data: (.+)$/m)?.[1];
      if (!ev || !data) continue;
      const json = JSON.parse(data);
      if (ev === 'partial') process.stdout.write('\r' + json.text);
      if (ev === 'done') console.log('\n完成:', json.reply);
      if (ev === 'error') throw new Error(json.error);
    }
  }
}
```

说明：流式请用 SSE 接口。HTTP `/chat`、`/say` 及其 stream **不**再触发 webhook；主动吐字仍走 webhook。

---

## 弹幕机对接建议

1. 启动桌宠后，先 `GET /health`；`ok === true` 再发业务请求。
2. 若要同步鱼鱼自己说的话，先在本机起好接收服务，再 `POST /webhook` 注册回调。
3. 若要同步动作切换，另起接收服务后 `POST /webhook/action` 注册回调（与吐字分开）。
4. **只播弹幕原文** → `POST /say`；要边吐字边收 → `POST /say/stream`。
5. **本地模型回一句** → `POST /chat`；要边生成边收 → `POST /chat/stream`。
6. 弹幕很密时建议限流或排队；新的 `/say`、`/chat` 及其 stream 会取消当前本地生成。
7. 仅本机可访问，弹幕客户端与桌宠需在同一台电脑。

### Node.js 示例

```js
const BASE = 'http://127.0.0.1:19265';

async function health() {
  const res = await fetch(`${BASE}/health`);
  return res.json();
}

async function setWebhook(url) {
  const res = await fetch(`${BASE}/webhook`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ url }),
  });
  return res.json();
}

async function setActionWebhook(url) {
  const res = await fetch(`${BASE}/webhook/action`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ url }),
  });
  return res.json();
}

async function say(text) {
  const res = await fetch(`${BASE}/say`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text }),
  });
  return res.json();
}

async function chat(text, user) {
  const res = await fetch(`${BASE}/chat`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text, user }),
  });
  return res.json();
}

await setWebhook('http://127.0.0.1:3000/big-blue-speech');
await setActionWebhook('http://127.0.0.1:3000/big-blue-action');
await chat(danmakuText, nick);
```

### curl 示例

```bash
curl http://127.0.0.1:19265/health

curl -X POST http://127.0.0.1:19265/webhook ^
  -H "Content-Type: application/json" ^
  -d "{\"url\":\"http://127.0.0.1:3000/big-blue-speech\"}"

curl -X POST http://127.0.0.1:19265/webhook/action ^
  -H "Content-Type: application/json" ^
  -d "{\"url\":\"http://127.0.0.1:3000/big-blue-action\"}"

curl -X POST http://127.0.0.1:19265/say ^
  -H "Content-Type: application/json" ^
  -d "{\"text\":\"大家好呀\"}"

curl -X POST http://127.0.0.1:19265/chat ^
  -H "Content-Type: application/json" ^
  -d "{\"text\":\"鱼鱼你好\",\"user\":\"小明\"}"
```

---

## 与「连接 Agent」的关系

本 API 与设置里的 CLI Agent / DeepSeek Harness **无关**。关闭 Agent 也可使用。`/chat` 只走桌宠内置本地模型，不会把弹幕发给云端 Agent。
