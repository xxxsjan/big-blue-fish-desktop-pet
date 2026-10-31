/**
 * 蓝色大肥鱼 HTTP API 联调脚本
 *
 * 用法：
 *   1. 先启动桌宠（源码：cd code; ./run.ps1 -InputMode Ocr）
 *   2. node test-http-api.mjs
 *
 * 可选环境变量：
 *   BIG_BLUE_API=http://127.0.0.1:19265
 *   WEBHOOK_PORT=19266
 *   SKIP_CHAT=1          # 跳过 /chat（不占本地显卡）
 */

import http from 'node:http';

const BIG_BLUE_API = (process.env.BIG_BLUE_API || 'http://127.0.0.1:19265').replace(/\/$/, '');
const WEBHOOK_PORT = Number(process.env.WEBHOOK_PORT || 19266);
const WEBHOOK_PATH = '/big-blue-speech';
const WEBHOOK_URL = `http://127.0.0.1:${WEBHOOK_PORT}${WEBHOOK_PATH}`;
const SKIP_CHAT = process.env.SKIP_CHAT === '1';

const received = [];

function log(step, detail) {
  const time = new Date().toLocaleTimeString();
  console.log(`[${time}] ${step}${detail ? ' — ' + detail : ''}`);
}

async function bigBlue(path, { method = 'GET', body } = {}) {
  const res = await fetch(`${BIG_BLUE_API}${path}`, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let json;
  try { json = JSON.parse(text); } catch { json = { raw: text }; }
  return { status: res.status, json };
}

function startWebhookServer() {
  return new Promise((resolve, reject) => {
    const server = http.createServer((req, res) => {
      if (req.method === 'POST' && req.url === WEBHOOK_PATH) {
        let raw = '';
        req.on('data', chunk => { raw += chunk; });
        req.on('end', () => {
          let payload;
          try { payload = JSON.parse(raw); } catch { payload = { raw }; }
          received.push(payload);
          log('webhook ←', `${payload.source ?? '?'} | ${payload.text ?? raw}`);
          res.writeHead(200, { 'Content-Type': 'application/json' });
          res.end(JSON.stringify({ ok: true }));
        });
        return;
      }
      res.writeHead(404);
      res.end();
    });
    server.on('error', reject);
    server.listen(WEBHOOK_PORT, '127.0.0.1', () => {
      log('webhook 监听', WEBHOOK_URL);
      resolve(server);
    });
  });
}

function sleep(ms) {
  return new Promise(r => setTimeout(r, ms));
}

async function waitForWebhook(predicate, timeoutMs = 15000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    const hit = received.find(predicate);
    if (hit) return hit;
    await sleep(200);
  }
  return null;
}

async function main() {
  console.log('=== 蓝色大肥鱼 HTTP API 测试 ===');
  console.log(`API: ${BIG_BLUE_API}`);
  console.log('');

  const server = await startWebhookServer();

  try {
    log('1/5 health');
    const health = await bigBlue('/health');
    console.log(JSON.stringify(health.json, null, 2));
    if (health.status !== 200 || !health.json.ok) {
      throw new Error('桌宠 /health 不可用，请先启动蓝色大肥鱼');
    }

    log('2/5 注册 webhook');
    const hook = await bigBlue('/webhook', { method: 'POST', body: { url: WEBHOOK_URL } });
    console.log(JSON.stringify(hook.json, null, 2));
    if (hook.status !== 200 || !hook.json.ok) {
      throw new Error('注册 webhook 失败：' + (hook.json.error || hook.status));
    }

    log('3/5 POST /say');
    const sayText = `联调说一句 ${new Date().toLocaleTimeString()}`;
    const say = await bigBlue('/say', { method: 'POST', body: { text: sayText } });
    console.log(JSON.stringify(say.json, null, 2));
    if (say.status !== 200 || !say.json.ok) {
      throw new Error('/say 失败：' + (say.json.error || say.status));
    }
    const sayHook = await waitForWebhook(p => p.source === 'say' && p.text === sayText);
    if (!sayHook) throw new Error('超时：未收到 /say 对应的 webhook');
    log('收到 say webhook', 'OK');

    if (SKIP_CHAT) {
      log('4/5 /chat', '已跳过（SKIP_CHAT=1）');
    } else {
      log('4/5 POST /chat', '会走本地模型，可能要几十秒');
      const chat = await bigBlue('/chat', {
        method: 'POST',
        body: { text: '鱼鱼你好，这是联调测试', user: '联调脚本' },
      });
      console.log(JSON.stringify(chat.json, null, 2));
      if (chat.status === 503) {
        log('警告', '/chat 返回 503（模型未就绪或忙碌），跳过 webhook 校验');
      } else if (chat.status !== 200 || !chat.json.ok) {
        throw new Error('/chat 失败：' + (chat.json.error || chat.status));
      } else {
        const chatHook = await waitForWebhook(
          p => p.source === 'chat' && p.text === chat.json.reply,
          120000
        );
        if (!chatHook) throw new Error('超时：未收到 /chat 对应的 webhook');
        log('收到 chat webhook', 'OK');
      }
    }

    log('5/5 查询 webhook 配置');
    const current = await bigBlue('/webhook');
    console.log(JSON.stringify(current.json, null, 2));

    console.log('');
    console.log('=== 全部通过 ===');
    console.log(`本轮共收到 webhook ${received.length} 条`);
    console.log('提示：可再在桌宠里点「天气」或「吐字测试」，观察本窗口是否继续打印 webhook。');
    console.log('按 Ctrl+C 结束监听。');

    await new Promise(() => {});
  } finally {
    server.close();
  }
}

main().catch(err => {
  console.error('\n测试失败：', err.message || err);
  process.exitCode = 1;
  setTimeout(() => process.exit(process.exitCode), 100);
});
