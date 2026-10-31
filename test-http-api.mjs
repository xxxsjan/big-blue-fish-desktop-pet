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

async function bigBlueStream(path, body, onEvent) {
  const res = await fetch(`${BIG_BLUE_API}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok && !res.headers.get('content-type')?.includes('text/event-stream')) {
    const text = await res.text();
    throw new Error(`${path} HTTP ${res.status}: ${text}`);
  }
  const reader = res.body.getReader();
  const dec = new TextDecoder();
  let buf = '';
  const events = [];
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
      events.push({ event: ev, data: json });
      onEvent?.(ev, json);
    }
  }
  return events;
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
    log('1/6 health');
    const health = await bigBlue('/health');
    console.log(JSON.stringify(health.json, null, 2));
    if (health.status !== 200 || !health.json.ok) {
      throw new Error('桌宠 /health 不可用，请先启动蓝色大肥鱼');
    }

    log('2/6 注册 webhook');
    const hook = await bigBlue('/webhook', { method: 'POST', body: { url: WEBHOOK_URL } });
    console.log(JSON.stringify(hook.json, null, 2));
    if (hook.status !== 200 || !hook.json.ok) {
      throw new Error('注册 webhook 失败：' + (hook.json.error || hook.status));
    }

    log('3/6 POST /say/stream');
    const sayText = `联调说一句 ${new Date().toLocaleTimeString()}`;
    let lastPartial = '';
    const sayEvents = await bigBlueStream('/say/stream', { text: sayText }, (ev, json) => {
      if (ev === 'partial') {
        lastPartial = json.text;
        process.stdout.write(`\r  吐字: ${json.text}`);
      }
      if (ev === 'done') process.stdout.write('\n');
      if (ev === 'error') throw new Error(json.error || 'say/stream error');
    });
    if (!sayEvents.some(e => e.event === 'done')) throw new Error('/say/stream 未收到 done');
    if (lastPartial !== sayText) throw new Error('/say/stream 最终 partial 与原文不一致');
    log('say/stream', 'OK');
    await sleep(800);
    if (received.some(p => p.source === 'say' && p.text === sayText)) {
      throw new Error('不应收到 /say/stream 的 webhook（HTTP 已返回全文）');
    }
    log('确认无 say webhook', 'OK');

    if (SKIP_CHAT) {
      log('4/6 /chat/stream', '已跳过（SKIP_CHAT=1）');
    } else {
      log('4/6 POST /chat/stream', '会走本地模型，可能要几十秒');
      const before = received.length;
      let reply = '';
      const chatEvents = await bigBlueStream(
        '/chat/stream',
        { text: '鱼鱼你好，这是联调测试', user: '联调脚本' },
        (ev, json) => {
          if (ev === 'partial') {
            process.stdout.write(`\r  生成: ${json.text}`);
          }
          if (ev === 'done') {
            process.stdout.write('\n');
            reply = json.reply || '';
          }
          if (ev === 'error') {
            process.stdout.write('\n');
            log('警告', '/chat/stream error: ' + (json.error || ''));
          }
        }
      );
      if (chatEvents.some(e => e.event === 'error') && !reply) {
        log('警告', '/chat/stream 失败（模型未就绪？）');
      } else if (!reply) {
        throw new Error('/chat/stream 未收到 done/reply');
      } else {
        log('chat/stream reply', reply);
        await sleep(800);
        if (received.slice(before).some(p => p.source === 'chat' && p.text === reply)) {
          throw new Error('不应收到 /chat/stream 的 webhook（HTTP 已返回全文）');
        }
        log('确认无 chat webhook', 'OK');
      }
    }

    log('5/6 POST /chat（完整一次返回，对照）');
    if (SKIP_CHAT) {
      log('跳过', 'SKIP_CHAT=1');
    } else {
      const chat = await bigBlue('/chat', {
        method: 'POST',
        body: { text: '再回一句短的', user: '联调脚本' },
      });
      console.log(JSON.stringify(chat.json, null, 2));
    }

    log('6/6 查询 webhook 配置');
    const current = await bigBlue('/webhook');
    console.log(JSON.stringify(current.json, null, 2));

    console.log('');
    console.log('=== 全部通过 ===');
    console.log(`本轮 webhook 条数（HTTP say/chat 应为 0）：${received.length}`);
    console.log('提示：可再在桌宠里点「天气」或「吐字测试」，那些才会打到 webhook。');
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
