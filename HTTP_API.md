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
  "webhookUrl": "http://127.0.0.1:3000/big-blue-speech"
}
```

| 字段 | 说明 |
| --- | --- |
| `modelAvailable` | 是否已找到可用的本地 GGUF |
| `modelEnabled` | 设置里是否启用本地模型 |
| `busy` | 当前是否正在推理或吐字占用中 |
| `status` | 模型侧状态文案 |
| `webhookUrl` | 当前配置的吐字回调地址；未配置时为空字符串 |

---

## Speech Webhook（桌宠 → 你的程序）

鱼鱼**真正开口**时（日常吐字、天气、吐字测试、`/say`、`/chat`、请安等），会向你配置的本机 URL 发一次 `POST`。流式生成过程中的中间片段不会推送，只推最终完整句子。

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
| `say` | `POST /say` 或 Agent `say` |
| `chat` | `POST /chat` 本地模型回复 |
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

## 弹幕机对接建议

1. 启动桌宠后，先 `GET /health`；`ok === true` 再发业务请求。
2. 若要同步鱼鱼自己说的话，先在本机起好接收服务，再 `POST /webhook` 注册回调。
3. **只播弹幕原文** → `POST /say`。
4. **要鱼鱼用本地模型回一句** → `POST /chat`（需 `modelAvailable` 且 `modelEnabled` 为 true）。
5. 弹幕很密时建议限流或排队，避免连续 `/chat` 互相打断；新的 `/say` 或 `/chat` 会取消当前正在进行的本地生成。
6. 仅本机可访问，弹幕客户端与桌宠需在同一台电脑。

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
await chat(danmakuText, nick);
```

### curl 示例

```bash
curl http://127.0.0.1:19265/health

curl -X POST http://127.0.0.1:19265/webhook ^
  -H "Content-Type: application/json" ^
  -d "{\"url\":\"http://127.0.0.1:3000/big-blue-speech\"}"

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
