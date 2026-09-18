#region

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

#endregion

namespace TiaMcpServer.ModelContextProtocol;

// The escape hatch that lets the lite roster be the default without losing anything.
//
// Shipping all 212 tools costs ~40k tokens of JSON schema in every single turn and
// exceeds what Copilot (128) and Windsurf (100) will even load. Shipping only the ~48
// lite tools fixes that but used to be a dead end: a model in lite could not reach
// ExportPlcWatchTable at all, and had no way to find out it existed.
//
// FindTools + CallTool close that gap: two tools (~700 tokens) buy on-demand access to
// the entire roster. The model searches when it needs something the roster lacks, reads
// just that one signature, and calls it. This is the progressive-disclosure / tool-search
// pattern that Anthropic, VS Code and the agent gateways all converged on during 2025-26.
public static partial class McpServer
{
  // name -> the static method carrying [McpServerTool]. Built once; ~212 entries.
  private static Dictionary<string, MethodInfo>? _allToolMethods;

  private static readonly JsonSerializerOptions BridgeJson = new()
  {
    PropertyNameCaseInsensitive = true,
    // Chinese project/block names must survive the round trip unescaped.
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
  };

  private static Dictionary<string, MethodInfo> AllToolMethods()
  {
    if (McpServer._allToolMethods != null)
    {
      return McpServer._allToolMethods;
    }

    var map = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
    foreach (var m in typeof(McpServer).GetMethods(BindingFlags.Public | BindingFlags.Static))
    {
      var attr = m.GetCustomAttribute<McpServerToolAttribute>();
      if (attr == null)
      {
        continue;
      }

      map[attr.Name ?? m.Name] = m;
    }

    McpServer._allToolMethods = map;
    return map;
  }

  private static string ToolDescription(MethodInfo m)
  {
    var d = m.GetCustomAttribute<DescriptionAttribute>();
    return d == null
      ? ""
      : d.Description;
  }

  /// <summary>Renders one tool's signature the way the model needs to call it through CallTool.</summary>
  private static string RenderSignature(string name, MethodInfo m)
  {
    var parts = new List<string>();
    foreach (var p in m.GetParameters())
    {
      var t = McpServer.FriendlyTypeName(p.ParameterType);
      // Optional params are what a model most often gets wrong, so show the actual
      // default rather than a bare "?".
      if (!p.HasDefaultValue)
      {
        parts.Add($"{p.Name}: {t}");
        continue;
      }

      var def = p.DefaultValue switch
      {
        null => "null",
        bool value => value
          ? "true"
          : "false",
        string => $"\"{p.DefaultValue}\"",
        _      => Convert.ToString(p.DefaultValue, CultureInfo.InvariantCulture) ?? "null",
      };

      parts.Add($"{p.Name}?: {t} = {def}");
    }

    return $"{name}({string.Join(", ", parts)})";
  }

  private static string FriendlyTypeName(Type t)
  {
    var u = Nullable.GetUnderlyingType(t) ?? t;
    if (u == typeof(string))
    {
      return "string";
    }

    if (u == typeof(bool))
    {
      return "boolean";
    }

    if (u == typeof(int) || u == typeof(long))
    {
      return "integer";
    }

    if (u == typeof(double) || u == typeof(float) || u == typeof(decimal))
    {
      return "number";
    }

    return u.IsArray
      ? $"{McpServer.FriendlyTypeName(u.GetElementType()!)}[]"
      : u.Name;
  }

  [McpServerTool(Name = "FindTools")]
  [Description("[L0][Meta] Search the FULL tool roster (all ~200 tools), including ones not listed in this session. " +
    "The server ships a ~48-tool 'lite' roster by default so the tool list stays small and every host can load it; " +
    "everything else is reached through this tool plus CallTool. " +
    "USE THIS whenever the visible tools do not cover what you need, before concluding the server cannot do something. " +
    "Search by capability words, not exact names: 'watch table', 'HMI screen', 'download', 'cross reference', 'GSD'. " +
    "Returns each match's exact name, parameter signature with defaults, and full description; then invoke it with CallTool.")]
  public static ResponseStringList FindTools(
    [Description(
      "query: space-separated words matched against tool names and descriptions, e.g. 'export watch table'. Empty lists the whole roster.")]
    string? query = "",
    [Description("limit: max tools to return (default 12). Raise it for a broad survey.")] int limit = 12)
  {
    try
    {
      var all = McpServer.AllToolMethods();
      if (limit <= 0)
      {
        limit = 12;
      }

      var terms = (query ?? "").Split([' ', ',', ';', '\t', '\r', '\n',], StringSplitOptions.RemoveEmptyEntries)
        .Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).ToArray();

      var scored = new List<KeyValuePair<int, string>>();
      foreach (var kv in all)
      {
        var lname = kv.Key.ToLowerInvariant();
        var desc = McpServer.ToolDescription(kv.Value).ToLowerInvariant();
        var score = 0;
        if (terms.Length == 0)
        {
          score = 1;
        }

        foreach (var t in terms)
        {
          // Name hits outrank description hits: a model searching "watch table"
          // wants ExportPlcWatchTable ahead of every tool that merely mentions it.
          if (lname == t)
          {
            score += 100;
          }
          else if (lname.Contains(t))
          {
            score += 20;
          }

          if (desc.Contains(t))
          {
            score += 3;
          }
        }

        if (score > 0)
        {
          scored.Add(new KeyValuePair<int, string>(score, kv.Key));
        }
      }

      if (scored.Count == 0)
      {
        return new ResponseStringList
        {
          Message =
            $"No tool matches '{query}'. Try fewer or more general words (e.g. 'watch table' instead of 'ExportPlcWatchTableToCsv'), or call FindTools with an empty query to list everything.",
          Meta = McpServer.BridgeMeta(true),
        };
      }

      var hits = scored.OrderByDescending(x => x.Key).ThenBy(x => x.Value, StringComparer.Ordinal).Take(limit).ToList();

      var lite = McpServer.IsLiteProfile();
      var lines = new List<string>();
      foreach (var h in hits)
      {
        var m = all[h.Value];
        var listed = !lite || McpServer.LiteToolNames.Contains(h.Value);
        lines.Add(McpServer.RenderSignature(h.Value, m) + (listed
          ? "  [already listed - call it directly]"
          : "  [call via CallTool]"));
        lines.Add($"    {McpServer.ToolDescription(m)}");
      }

      return new ResponseStringList
      {
        Message =
          $"{hits.Count} of {scored.Count} matching tools (roster: {all.Count} total). Tools marked [call via CallTool] are not in this session's tool list - invoke them with CallTool(name, argumentsJson).",
        Items = lines,
        Meta = McpServer.BridgeMeta(true),
      };
    }
    catch (Exception ex)
    {
      return new ResponseStringList
      {
        Message = $"FindTools failed: {ex.Message}", Meta = McpServer.BridgeMeta(false),
      };
    }
  }

  [McpServerTool(Name = "CallTool")]
  [Description("[L0][Meta] Invoke ANY tool in the full roster by name, including ones not listed in this session. " +
    "Use FindTools first to get the exact name and parameter signature. " +
    "Behaves exactly like calling the tool directly: same work, same result, same safety checks. " +
    "Example: name='ExportPlcWatchTable', argumentsJson='{\"softwarePath\":\"PLC_1\",\"watchTableName\":\"WT1\"}'.")]
  public static ResponseMessage CallTool(
    [Description("name: exact tool name from FindTools, e.g. 'ExportPlcWatchTable'.")] string? name,
    [Description(
      "argumentsJson: JSON object of the tool's arguments, e.g. '{\"softwarePath\":\"PLC_1\"}'. Omit or '{}' for a no-argument tool.")]
    string argumentsJson = "")
  {
    var target = (name ?? "").Trim();
    try
    {
      if (target.Length == 0)
      {
        return new ResponseMessage
        {
          Message = "CallTool: 'name' is required. Call FindTools to look up a tool name.",
          Meta = McpServer.BridgeMeta(false),
        };
      }

      // Self-recursion would be a loop with no purpose; refuse it explicitly.
      if (string.Equals(target, "CallTool", StringComparison.OrdinalIgnoreCase))
      {
        return new ResponseMessage
        {
          Message = "CallTool cannot invoke itself. Pass the target tool's own name.",
          Meta = McpServer.BridgeMeta(false),
        };
      }

      var all = McpServer.AllToolMethods();
      if (!all.TryGetValue(target, out var method))
      {
        // A wrong name is the likeliest failure, so spend the message on the fix
        // rather than on restating the problem.
        var near = all.Keys
          .Where(k => k.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0 ||
            target.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(k => k, StringComparer.Ordinal).Take(8)
          .ToList();
        // Containment misses the commonest case of all - a typo in the middle of an
        // otherwise correct name ("ExportPlcWatchTabel"). Fall back to shared prefix.
        if (near.Count == 0)
        {
          near =
          [
            .. all.Keys.Select(k => new KeyValuePair<int, string>(McpServer.CommonPrefixLength(k, target), k))
              .Where(x => x.Key >= 6).OrderByDescending(x => x.Key).ThenBy(x => x.Value, StringComparer.Ordinal).Take(5)
              .Select(x => x.Value),
          ];
        }

        return new ResponseMessage
        {
          Message = $"No tool named '{target}'.{(near.Count > 0
            ? $" Did you mean: {string.Join(", ", near)}?"
            : " Call FindTools with a capability keyword to find the right name.")}",
          Meta = McpServer.BridgeMeta(false),
        };
      }

      JsonObject args;
      if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson.Trim() == "{}")
      {
        args = new JsonObject();
      }
      else
      {
        JsonNode? parsed;
        try
        {
          parsed = JsonNode.Parse(argumentsJson);
        }
        catch (JsonException jx)
        {
          return new ResponseMessage
          {
            Message =
              $"argumentsJson is not valid JSON ({jx.Message}). It must be a JSON OBJECT of the tool's parameters, e.g. {{\"softwarePath\":\"PLC_1\"}} - not a bare value, not the tool name.",
            Meta = McpServer.BridgeMeta(false),
          };
        }

        if (parsed is not JsonObject obj)
        {
          return new ResponseMessage
          {
            Message =
              $"argumentsJson must be a JSON object, e.g. {{\"softwarePath\":\"PLC_1\"}}. Expected signature: {McpServer.RenderSignature(target, method!)}",
            Meta = McpServer.BridgeMeta(false),
          };
        }

        args = obj;
      }

      var ps = method.GetParameters();
      var call = new object?[ps.Length];
      var missing = new List<string>();
      for (var i = 0; i < ps.Length; i++)
      {
        var p = ps[i];
        // Match case-insensitively: models routinely send PascalCase for a camelCase param.
        JsonNode? value = null;
        var found = false;
        foreach (var kv in args)
        {
          if (!string.Equals(kv.Key, p.Name, StringComparison.OrdinalIgnoreCase))
          {
            continue;
          }

          value = kv.Value;
          found = kv.Value != null;
          break;
        }

        if (!found)
        {
          if (p.HasDefaultValue)
          {
            call[i] = p.DefaultValue;
            continue;
          }

          missing.Add(p.Name!);
          call[i] = p.ParameterType.IsValueType
            ? Activator.CreateInstance(p.ParameterType)
            : null;
          continue;
        }

        try
        {
          call[i] = value!.Deserialize(p.ParameterType, McpServer.BridgeJson);
        }
        catch (Exception cx)
        {
          return new ResponseMessage
          {
            Message =
              $"Argument '{p.Name}' of {target} could not be read as {McpServer.FriendlyTypeName(p.ParameterType)}: {cx.Message}. Expected signature: {McpServer.RenderSignature(target, method)}",
            Meta = McpServer.BridgeMeta(false),
          };
        }
      }

      if (missing.Count > 0)
      {
        return new ResponseMessage
        {
          Message =
            $"{target} is missing required argument(s): {string.Join(", ", missing)}. Expected signature: {McpServer.RenderSignature(target, method)}",
          Meta = McpServer.BridgeMeta(false),
        };
      }

      var result = method.Invoke(null, call);
      // Tools return their own strongly-typed response objects; hand that JSON through
      // unchanged so the model sees exactly what a direct call would have produced.
      var payload = result == null
        ? "null"
        : JsonSerializer.Serialize(result, result.GetType(), McpServer.BridgeJson);

      return new ResponseMessage { Message = payload, Meta = McpServer.BridgeMeta(true), };
    }
    catch (TargetInvocationException tie)
    {
      var inner = tie.InnerException ?? tie;
      return new ResponseMessage
      {
        Message = $"{target} failed: {inner.Message}", Meta = McpServer.BridgeMeta(false),
      };
    }
    catch (Exception ex)
    {
      return new ResponseMessage
      {
        Message = $"CallTool('{target}') failed: {ex.Message}", Meta = McpServer.BridgeMeta(false),
      };
    }
  }

  private static int CommonPrefixLength(string a, string b)
  {
    int n = Math.Min(a.Length, b.Length), i = 0;
    while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
    {
      i++;
    }

    return i;
  }

  private static JsonObject BridgeMeta(bool success) => new() { ["timestamp"] = DateTime.Now, ["success"] = success, };
}
