using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;

namespace BigBlueFish.Windows;

public partial class MainWindow
{
    internal const int HttpApiPort = 19265;
    private static readonly HttpClient CompanionWebhookClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private HttpListener? httpApi;
    private CancellationTokenSource? httpApiCancel;
    private const string HttpApiRoutes = "GET /health、GET/POST /webhook、GET/POST /webhook/action、POST /say、POST /say/stream、POST /chat、POST /chat/stream";

    private void StartHttpApi()
    {
        if (httpApi != null) return;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{HttpApiPort}/");
        try
        {
            listener.Start();
        }
        catch (Exception e)
        {
            try { File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "companion-errors.log"),
                DateTime.Now + " HTTP API 未能启动：" + e + Environment.NewLine); } catch { }
            return;
        }
        httpApi = listener;
        httpApiCancel = new CancellationTokenSource();
        _ = Task.Run(() => AcceptHttpApi(listener, httpApiCancel.Token));
    }

    private void StopHttpApi()
    {
        httpApiCancel?.Cancel();
        httpApiCancel?.Dispose();
        httpApiCancel = null;
        try { httpApi?.Stop(); } catch { }
        try { httpApi?.Close(); } catch { }
        httpApi = null;
    }

    private async Task AcceptHttpApi(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(token); }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleHttpApi(context));
        }
    }

    private async Task HandleHttpApi(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        try
        {
            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            var path = (request.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
            if (path.Length == 0) path = "/";

            if (request.HttpMethod == "GET" && path is "/" or "/health")
            {
                await WriteHttpJson(response, 200, await Dispatcher.InvokeAsync(() => new
                {
                    ok = true,
                    port = HttpApiPort,
                    modelAvailable = brain.ModelAvailable,
                    modelEnabled = preferences.ModelEnabled,
                    busy = brain.Busy || speechRequestPending,
                    status = brain.Status,
                    webhookUrl = preferences.SpeechWebhookUrl,
                    actionWebhookUrl = preferences.ActionWebhookUrl
                }));
                return;
            }

            if (request.HttpMethod == "GET" && path == "/webhook")
            {
                await WriteHttpJson(response, 200, await Dispatcher.InvokeAsync(() => new
                {
                    ok = true,
                    url = preferences.SpeechWebhookUrl
                }));
                return;
            }

            if (request.HttpMethod == "GET" && path == "/webhook/action")
            {
                await WriteHttpJson(response, 200, await Dispatcher.InvokeAsync(() => new
                {
                    ok = true,
                    url = preferences.ActionWebhookUrl
                }));
                return;
            }

            if (request.HttpMethod != "POST")
            {
                await WriteHttpJson(response, 405, new { ok = false, error = "仅支持 " + HttpApiRoutes + "。" });
                return;
            }

            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            using var document = string.IsNullOrWhiteSpace(body)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(body);
            var root = document.RootElement;

            if (path == "/webhook")
            {
                var url = ReadWebhookUrl(root);
                await Dispatcher.InvokeAsync(() =>
                {
                    preferences.SpeechWebhookUrl = url;
                    SaveCompanion();
                });
                await WriteHttpJson(response, 200, new { ok = true, url });
                return;
            }

            if (path == "/webhook/action")
            {
                var url = ReadWebhookUrl(root);
                await Dispatcher.InvokeAsync(() =>
                {
                    preferences.ActionWebhookUrl = url;
                    SaveCompanion();
                });
                await WriteHttpJson(response, 200, new { ok = true, url });
                return;
            }

            if (path == "/say")
            {
                var text = ReadHttpText(root, "text", 4000);
                // HTTP 调用方已从响应拿到全文，不再二次 webhook，避免与 Chat/Say 重复。
                await Dispatcher.InvokeAsync(() => ShowHttpSpeech(text, publishWebhook: false));
                await WriteHttpJson(response, 200, new { ok = true, displayed = true, text });
                return;
            }

            if (path == "/say/stream")
            {
                var text = ReadHttpText(root, "text", 4000);
                await WriteSayStream(response, text);
                return;
            }

            if (path == "/chat")
            {
                var text = ReadHttpText(root, "text", 1000);
                var user = root.TryGetProperty("user", out var userNode) && userNode.ValueKind == JsonValueKind.String
                    ? userNode.GetString()
                    : null;
                if (user != null && user.Length > 64) user = user[..64];
                var reply = await Dispatcher.InvokeAsync(() => GenerateHttpChatAsync(text, user)).Task.Unwrap();
                if (reply.Length == 0)
                {
                    await WriteHttpJson(response, 503, new { ok = false, error = "本地模型不可用、忙碌、已暂停，或未能生成回复。" });
                    return;
                }
                await WriteHttpJson(response, 200, new { ok = true, text, user, reply });
                return;
            }

            if (path == "/chat/stream")
            {
                var text = ReadHttpText(root, "text", 1000);
                var user = root.TryGetProperty("user", out var userNode) && userNode.ValueKind == JsonValueKind.String
                    ? userNode.GetString()
                    : null;
                if (user != null && user.Length > 64) user = user[..64];
                await WriteChatStream(response, text, user);
                return;
            }

            await WriteHttpJson(response, 404, new { ok = false, error = "未知路径。可用：" + HttpApiRoutes + "。" });
        }
        catch (ArgumentException e)
        {
            await WriteHttpJson(response, 400, new { ok = false, error = e.Message });
        }
        catch (JsonException)
        {
            await WriteHttpJson(response, 400, new { ok = false, error = "请求体必须是 JSON 对象。" });
        }
        catch (Exception e)
        {
            await WriteHttpJson(response, 500, new { ok = false, error = e.Message });
        }
    }

    private async Task WriteChatStream(HttpListenerResponse response, string text, string? user)
    {
        BeginSse(response);
        var channel = Channel.CreateUnbounded<(string Event, object Payload)>();
        var produce = Task.Run(async () =>
        {
            try
            {
                await channel.Writer.WriteAsync(("start", new { ok = true, text, user, source = "chat" }));
                var reply = await Dispatcher.InvokeAsync(() => GenerateHttpChatAsync(text, user, partial =>
                {
                    channel.Writer.TryWrite(("partial", new { text = partial, source = "chat" }));
                })).Task.Unwrap();
                if (reply.Length == 0)
                    await channel.Writer.WriteAsync(("error", new { ok = false, error = "本地模型不可用、忙碌、已暂停，或未能生成回复。" }));
                else
                    await channel.Writer.WriteAsync(("done", new { ok = true, text, user, reply, source = "chat" }));
            }
            catch (Exception e)
            {
                await channel.Writer.WriteAsync(("error", new { ok = false, error = e.Message }));
            }
            finally { channel.Writer.TryComplete(); }
        });

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync())
                await WriteSseEvent(response, item.Event, item.Payload);
            await produce;
        }
        catch
        {
            try { response.Abort(); } catch { }
        }
        finally
        {
            try { response.OutputStream.Close(); } catch { }
            try { response.Close(); } catch { }
        }
    }

    private async Task WriteSayStream(HttpListenerResponse response, string text)
    {
        BeginSse(response);
        try
        {
            await WriteSseEvent(response, "start", new { ok = true, text, source = "say" });
            await Dispatcher.InvokeAsync(() => ShowHttpSpeech(text, publishWebhook: false));
            var boundaries = StringInfo.ParseCombiningCharacters(text);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int shown = 0;
            while (shown < boundaries.Length)
            {
                int count = Math.Min(boundaries.Length, 1 + (int)(watch.Elapsed.TotalSeconds * CompanionSpeech.CharactersPerSecond));
                if (count > shown)
                {
                    shown = count;
                    var partial = count >= boundaries.Length ? text : text[..boundaries[count]];
                    await WriteSseEvent(response, "partial", new { text = partial, source = "say" });
                }
                if (shown >= boundaries.Length) break;
                await Task.Delay(25);
            }
            await WriteSseEvent(response, "done", new { ok = true, text, reply = text, source = "say" });
        }
        catch (Exception e)
        {
            try { await WriteSseEvent(response, "error", new { ok = false, error = e.Message }); } catch { }
        }
        finally
        {
            try { response.OutputStream.Close(); } catch { }
            try { response.Close(); } catch { }
        }
    }

    private static void BeginSse(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.Add("Cache-Control", "no-cache");
        response.Headers.Add("Connection", "keep-alive");
        response.SendChunked = true;
    }

    private static async Task WriteSseEvent(HttpListenerResponse response, string eventName, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {json}\n\n");
        await response.OutputStream.WriteAsync(bytes);
        await response.OutputStream.FlushAsync();
    }

    private void ShowHttpSpeech(string text, bool publishWebhook = true)
    {
        if (companionClosed || exitAnimation) throw new InvalidOperationException("桌宠正在退出。");
        InvalidateSpeechRequests();
        EndGreetingNotice();
        externalBubble = true;
        bubbleIsModelReply = false;
        bubbleUntil = DateTime.MaxValue;
        speech.Show(text, true);
        if (publishWebhook) PublishSpeechWebhook(text, "say");
    }

    private async Task<string> GenerateHttpChatAsync(string text, string? user, Action<string>? onStream = null)
    {
        if (companionClosed || exitAnimation) return "";
        if (!preferences.ModelEnabled || !brain.ModelAvailable || !brain.CanRunOnCurrentPower) return "";

        InvalidateSpeechRequests();
        EndGreetingNotice();
        speechRequestPending = true;
        long version = speechRequestVersion;
        BeginModelSpeech();
        bool streamed = false;
        string reply = "";
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(100);
            while ((brain.Busy || weatherRequestPending) && DateTime.UtcNow < deadline
                && version == speechRequestVersion && !companionClosed && !exitAnimation
                && preferences.ModelEnabled && brain.CanRunOnCurrentPower)
                await Task.Delay(100);
            if (version != speechRequestVersion || brain.Busy || weatherRequestPending || companionClosed || exitAnimation
                || !preferences.ModelEnabled || !brain.CanRunOnCurrentPower)
                return "";
            BeginModelSpeech();
            reply = await brain.GenerateChat(text, user, partial =>
            {
                streamed = true;
                ShowCompanionPartial(partial, version, true);
                onStream?.Invoke(partial);
            });
        }
        finally
        {
            if (version == speechRequestVersion) { speechRequestPending = false; EndModelSpeech(); }
        }

        if (version == speechRequestVersion && !companionClosed && reply.Length > 0)
        {
            // 响应 / SSE 已交付全文，跳过 webhook，避免调用方收两遍。
            if (streamed) FinishCompanionPartial(reply, true, "chat", notifyWebhook: false);
            else ShowCompanionBubble(reply, true, "chat", notifyWebhook: false);
            return reply;
        }
        if (streamed && version == speechRequestVersion) ClearCompanionModelReply();
        return "";
    }

    private void PublishSpeechWebhook(string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        PostCompanionWebhook(preferences.SpeechWebhookUrl, "Speech webhook", new
        {
            text,
            source,
            at = DateTimeOffset.Now
        });
    }

    private void PublishActionWebhook(string action, string pool, string phase, bool mirror)
    {
        if (string.IsNullOrWhiteSpace(action)) return;
        PostCompanionWebhook(preferences.ActionWebhookUrl, "Action webhook", new
        {
            action,
            pool,
            phase,
            mirror,
            at = DateTimeOffset.Now
        });
    }

    private static void PostCompanionWebhook(string? configuredUrl, string label, object payloadObject)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl)) return;
        var url = configuredUrl.Trim();
        try { ValidateWebhookUrl(url); }
        catch { return; }

        var payload = JsonSerializer.Serialize(payloadObject);
        _ = Task.Run(async () =>
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await CompanionWebhookClient.PostAsync(url, content);
            }
            catch (Exception e)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "companion-errors.log"),
                        DateTime.Now + " " + label + " 失败：" + e.Message + Environment.NewLine);
                }
                catch { }
            }
        });
    }

    private static string ReadWebhookUrl(JsonElement root)
    {
        string url = "";
        if (root.TryGetProperty("url", out var urlNode))
        {
            if (urlNode.ValueKind == JsonValueKind.Null) url = "";
            else if (urlNode.ValueKind == JsonValueKind.String) url = urlNode.GetString() ?? "";
            else throw new ArgumentException("url 必须是字符串或 null。");
        }
        url = url.Trim();
        if (url.Length > 0) ValidateWebhookUrl(url);
        return url;
    }

    private static void ValidateWebhookUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
            || uri.Host is not ("127.0.0.1" or "localhost"))
            throw new ArgumentException("webhook url 仅允许本机 http(s)://127.0.0.1 或 http(s)://localhost。");
        if (url.Length > 500) throw new ArgumentException("webhook url 过长。");
    }

    private static string ReadHttpText(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maximum)
            throw new ArgumentException($"{name} 必须是 1–{maximum} 字符的文字。");
        return value.GetString()!;
    }

    private static async Task WriteHttpJson(HttpListenerResponse response, int status, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;
        try
        {
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }
        catch
        {
            try { response.Abort(); } catch { }
        }
    }
}
