#region

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

#endregion

namespace TiaMcpServer;

/// <summary>
///   HTTP host for the MCP server. Implements a pragmatic subset of the
///   MCP Streamable HTTP transport spec aimed at local / internal use:
///   <list type="bullet">
///     <item>
///       <description>POST /mcp — JSON-RPC, returns JSON or SSE based on Accept header.</description>
///     </item>
///     <item>
///       <description>GET /mcp/health — liveness/info probe.</description>
///     </item>
///     <item>
///       <description>DELETE /mcp — terminate session (best-effort).</description>
///     </item>
///     <item>
///       <description>GET / — server identity (kept for backward compat).</description>
///     </item>
///   </list>
///   Auth: a single shared secret can be supplied via either
///   <c>Authorization: Bearer &lt;secret&gt;</c> or <c>X-API-Key: &lt;secret&gt;</c>.
///   Mcp-Session-Id is generated on first request and correlated on subsequent requests;
///   state isolation between sessions is intentionally not implemented because the
///   underlying TIA Portal handle is process-wide.
/// </summary>
internal static class HttpMcpServer
{
  private const string ServerName = "TIA Portal MCP";
  private const string SessionHeader = "Mcp-Session-Id";
  private const string ProtocolHeader = "MCP-Protocol-Version";

  // Upper bound on how long a POST waits for the MCP host to produce a matching
  // response before returning 504, so a stalled pipe can't hang the request forever.
  private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

  private static readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);

  public static async Task Run(CliOptions? options, McpBlockingStream httpToMcp, McpBlockingStream mcpToHttp,
    Action<string> log)
  {
    var prefix = options?.HttpPrefix ?? "http://127.0.0.1:8765/";
    if (!prefix.EndsWith("/"))
    {
      prefix += "/";
    }

    var secret = options?.HttpApiKey;

    var listener = new HttpListener();
    listener.Prefixes.Add(prefix);
    listener.Start();

    Console.Error.WriteLine($"TIA Portal MCP Server (HTTP) listening at {prefix}");
    log($"HTTP transport started at {prefix}");
    if (secret == null)
    {
      Console.Error.WriteLine("WARNING: --http-api-key not set; endpoint is unauthenticated.");
    }

    // The MCP SDK is single-threaded over the underlying stream pair, so all
    // forwarded JSON-RPC must be serialized.
    var requestLock = new SemaphoreSlim(1, 1);
    var mcpWriter = new StreamWriter(httpToMcp, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true, };
    var mcpReader = new StreamReader(mcpToHttp, new UTF8Encoding(false));

    while (listener.IsListening)
    {
      HttpListenerContext ctx;
      try
      {
        ctx = await listener.GetContextAsync().ConfigureAwait(false);
      }
      catch (HttpListenerException)
      {
        break;
      }
      catch (ObjectDisposedException)
      {
        break;
      }

      _ = Task.Run(async () =>
      {
        try
        {
          await HttpMcpServer.Dispatch(ctx, secret, requestLock, mcpWriter, mcpReader, log).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
          log("HTTP handler error: " + ex.Message);
          try
          {
            ctx.Response.Abort();
          }
          catch
          {
            // teardown：中止响应失败无处可报（连接已经在错误路径上）
          }
        }
      });
    }

    listener.Stop();
  }

  private static async Task Dispatch(HttpListenerContext ctx, string? secret, SemaphoreSlim requestLock,
    StreamWriter mcpWriter, StreamReader mcpReader, Action<string> log)
  {
    var req = ctx.Request;
    var res = ctx.Response;
    var method = req.HttpMethod ?? "";
    var rawUrl = req.RawUrl ?? "";

    // Health probe — unauthenticated by design so monitoring can hit it cheaply.
    if (HttpMcpServer.HttpMethod("GET", method) && rawUrl.StartsWith("/mcp/health", StringComparison.OrdinalIgnoreCase))
    {
      await HttpMcpServer.WriteJson(res, 200, HttpMcpServer.BuildHealthJson()).ConfigureAwait(false);
      return;
    }

    // Server identity (kept for backward compatibility with existing tooling).
    if (HttpMcpServer.HttpMethod("GET", method) && rawUrl == "/")
    {
      await HttpMcpServer.WriteJson(res, 200, $"{{\"server\":\"{HttpMcpServer.ServerName}\",\"transport\":\"http\"}}")
        .ConfigureAwait(false);
      return;
    }

    // All /mcp paths require auth (when a secret is configured).
    if (!rawUrl.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase))
    {
      res.StatusCode = 404;
      res.Close();
      return;
    }

    if (secret != null && !HttpMcpServer.AuthOk(req, secret))
    {
      res.StatusCode = 401;
      res.Headers["WWW-Authenticate"] = "Bearer";
      res.Close();
      return;
    }

    // DELETE /mcp — session termination (best-effort; idempotent).
    if (HttpMcpServer.HttpMethod("DELETE", method))
    {
      var sid = req.Headers[HttpMcpServer.SessionHeader];
      if (!string.IsNullOrEmpty(sid))
      {
        HttpMcpServer._sessions.TryRemove(sid!, out _);
      }

      res.StatusCode = 204;
      res.Close();
      return;
    }

    if (!HttpMcpServer.HttpMethod("POST", method))
    {
      res.StatusCode = 405;
      res.Close();
      return;
    }

    // Body length guard: real MCP requests are tens of KB; cap defensively.
    const long maxBodyBytes = 10L * 1024 * 1024;
    if (req.ContentLength64 > maxBodyBytes)
    {
      res.StatusCode = 413;
      res.Close();
      return;
    }

    // Read the raw body, bounded by Content-Length. Async reads against the
    // HttpListener input stream can hang, so read synchronously (this handler
    // already runs on a dedicated task thread) and stop at the declared length.
    string body;
    {
      var declared = req.ContentLength64;
      var ms = new MemoryStream();
      var buf = new byte[8192];
      var input = req.InputStream;
      int read;
      while ((read = await input.ReadAsync(buf, 0, buf.Length)) > 0)
      {
        ms.Write(buf, 0, read);
        if (ms.Length > maxBodyBytes)
        {
          res.StatusCode = 413;
          res.Close();
          return;
        }

        if (declared >= 0 && ms.Length >= declared)
        {
          break;
        }
      }

      body = (req.ContentEncoding ?? Encoding.UTF8).GetString(ms.ToArray());
    }

    if (string.IsNullOrWhiteSpace(body))
    {
      res.StatusCode = 400;
      res.Close();
      return;
    }

    JsonNode? requestId;
    string? rpcMethod;
    try
    {
      var parsed = JsonNode.Parse(body);
      requestId = parsed?["id"];
      rpcMethod = parsed?["method"]?.GetValue<string>();
    }
    catch
    {
      res.StatusCode = 400;
      res.Close();
      return;
    }

    // Session bookkeeping. We assign on initialize and accept any subsequent header.
    var session = HttpMcpServer.TouchSession(req, rpcMethod);
    res.Headers[HttpMcpServer.SessionHeader] = session.Id;
    var protoVersion = req.Headers[HttpMcpServer.ProtocolHeader];
    if (!string.IsNullOrEmpty(protoVersion))
    {
      session.ProtocolVersion = protoVersion;
    }

    var isNotification = requestId == null;
    var wantsSse = HttpMcpServer.WantsEventStream(req);

    if (isNotification)
    {
      await requestLock.WaitAsync().ConfigureAwait(false);
      try
      {
        await mcpWriter.WriteLineAsync(body);
      }
      finally
      {
        requestLock.Release();
      }

      res.StatusCode = 202;
      res.Close();
      return;
    }

    // Forward to MCP and wait for the matching response.
    await requestLock.WaitAsync().ConfigureAwait(false);
    string? responseLine = null;
    var timedOut = false;
    try
    {
      await mcpWriter.WriteLineAsync(body);
      var expectedId = requestId!.ToJsonString();

      // ReadLineAsync on a StreamReader wrapping a blocking stream can block the
      // calling thread synchronously, so race a dedicated read worker against a
      // wall-clock delay to guarantee a 504 rather than an indefinite hang.
      var readWork = Task.Run(() =>
      {
        while (mcpReader.ReadLine() is { } line)
        {
          if (string.IsNullOrWhiteSpace(line))
          {
            continue;
          }

          try
          {
            var ln = JsonNode.Parse(line);
            var lnId = ln?["id"]?.ToJsonString();
            var hasMethod = ln?["method"] != null;

            if (lnId == expectedId)
            {
              return line;
            }

            // Skip server-initiated notifications (have method, no id).
            if (hasMethod && lnId == null)
            {
              continue;
            }
          }
          catch
          {
            /* malformed line — skip */
          }
        }

        return null;
      });

      var done = await Task.WhenAny(readWork, Task.Delay(HttpMcpServer.ResponseTimeout)).ConfigureAwait(false);
      if (done == readWork)
      {
        responseLine = await readWork.ConfigureAwait(false);
      }
      else
      {
        timedOut = true;
      }
    }
    finally
    {
      requestLock.Release();
    }

    if (timedOut)
    {
      res.StatusCode = 504;
      res.Close();
      return;
    }

    if (responseLine == null)
    {
      res.StatusCode = 500;
      res.Close();
      return;
    }

    if (wantsSse)
    {
      await HttpMcpServer.WriteSse(res, responseLine).ConfigureAwait(false);
    }
    else
    {
      await HttpMcpServer.WriteJson(res, 200, responseLine).ConfigureAwait(false);
    }
  }

  // ---------- helpers ----------

  private static bool HttpMethod(string expected, string actual) =>
    string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

  private static bool AuthOk(HttpListenerRequest req, string secret)
  {
    var apiKey = req.Headers["X-API-Key"];
    if (apiKey == secret)
    {
      return true;
    }

    var authz = req.Headers["Authorization"];
    if (!string.IsNullOrEmpty(authz) && authz!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
      authz[7..].Trim() == secret)
    {
      return true;
    }

    return false;
  }

  private static bool WantsEventStream(HttpListenerRequest req)
  {
    var accept = req.Headers["Accept"];
    return !string.IsNullOrEmpty(accept) &&
      accept!.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
  }

  private static Session TouchSession(HttpListenerRequest req, string? rpcMethod)
  {
    var sid = req.Headers[HttpMcpServer.SessionHeader];
    if (!string.IsNullOrEmpty(sid) && HttpMcpServer._sessions.TryGetValue(sid!, out var existing))
    {
      existing.LastSeenUtc = DateTime.UtcNow;
      return existing;
    }

    // Allocate a new session on initialize, or whenever a client omits the header.
    var s = new Session { Id = Guid.NewGuid().ToString("N"), LastSeenUtc = DateTime.UtcNow, };
    HttpMcpServer._sessions[s.Id] = s;
    return s;
  }

  private static string BuildHealthJson()
  {
    var sb = new StringBuilder();
    sb.Append("{\"server\":\"").Append(HttpMcpServer.ServerName).Append("\"");
    sb.Append(",\"transport\":\"http\"");
    sb.Append(",\"sessions\":").Append(HttpMcpServer._sessions.Count);
    sb.Append(",\"build\":\"").Append(typeof(HttpMcpServer).Assembly.GetName().Version).Append("\"");
    sb.Append("}");
    return sb.ToString();
  }

  private static async Task WriteJson(HttpListenerResponse res, int status, string json)
  {
    var bytes = Encoding.UTF8.GetBytes(json);
    res.StatusCode = status;
    res.ContentType = "application/json";
    res.ContentLength64 = bytes.Length;
    await res.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    res.Close();
  }

  private static async Task WriteSse(HttpListenerResponse res, string jsonRpcLine)
  {
    // Single-shot SSE: one "message" event carrying the JSON-RPC response body,
    // then end of stream. This keeps spec-compliant clients happy without
    // adding a real long-lived stream that local use does not need.
    var sb = new StringBuilder();
    sb.Append("event: message\n");
    // Split payload on newlines per SSE framing rules.
    foreach (var line in jsonRpcLine.Split('\n'))
    {
      sb.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');
    }

    sb.Append('\n');

    var bytes = Encoding.UTF8.GetBytes(sb.ToString());
    res.StatusCode = 200;
    res.ContentType = "text/event-stream";
    res.Headers["Cache-Control"] = "no-cache";
    res.SendChunked = true;
    await res.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    res.OutputStream.Close();
    res.Close();
  }

  private sealed class Session
  {
    public string Id = "";
    public DateTime LastSeenUtc;
    public string? ProtocolVersion;
  }
}
