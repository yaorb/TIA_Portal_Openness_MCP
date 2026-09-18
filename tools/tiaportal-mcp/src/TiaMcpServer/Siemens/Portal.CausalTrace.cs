#region

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Runtime;

#endregion

namespace TiaMcpServer.Siemens;

// Static causal tracer orchestration: answers "why is tag X this value / what
// sets it" by analysing the OFFLINE project logic. Fully read-only on the
// project; never touches the live CPU. The returned gating operands can then be
// live-read with ReadPlcLiveValuesS7 to see which condition is currently driving
// the value. Cross-reference service is unavailable via Openness here, so we
// export each code block to SimaticML and parse it with CausalTraceParser.
public partial class Portal
{
  public ResponseJsonReport TraceTagCause(string softwarePath, string tag, string blockScope = "")
  {
    var data = new JsonObject
    {
      ["softwarePath"] = softwarePath, ["tag"] = tag, ["timestamp"] = DateTime.Now.ToString("O"), ["readOnly"] = true,
    };
    var warnings = new JsonArray();

    if (this.IsProjectNull())
    {
      return new ResponseJsonReport { Ok = false, Message = "No project open. Attach first.", Data = data, };
    }

    if (string.IsNullOrWhiteSpace(tag))
    {
      return new ResponseJsonReport { Ok = false, Message = "tag is required.", Data = data, };
    }

    var normTag = CausalTraceParser.NormalizeOperand(tag);

    List<PlcBlock>? blocks;
    try
    {
      blocks = this.GetBlocks(softwarePath, blockScope ?? "");
    }
    catch (Exception ex)
    {
      return new ResponseJsonReport { Ok = false, Message = $"GetBlocks failed: {ex.Message}", Data = data, };
    }

    // null = 没连接/没打开项目。以前 GetBlocks 这时返回空列表，于是一路扫出
    // 「0 个块、没找到任何写点」，报成一份看起来正常的空结果。
    if (blocks == null)
    {
      return new ResponseJsonReport
      {
        Ok = false,
        Message = "No TIA project is open. Call Connect / OpenProject (or AttachToOpenProject) first.",
        Data = data,
      };
    }

    var codeBlocks = blocks.Where(b => b is not DataBlock).ToList();
    data["scannedBlockCount"] = codeBlocks.Count;

    var tmpDir = Path.Combine(Path.GetTempPath(), "tia_trace_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tmpDir);

    var writeSites = new JsonArray();
    var readSites = new JsonArray();
    var allConditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var analyzedOk = 0;

    try
    {
      foreach (var block in codeBlocks)
      {
        var blockName = block.Name;
        var blockPath = this.GetBlockPath(block);
        if (!block.IsConsistent)
        {
          warnings.Add($"Skipped inconsistent block '{blockName}' (compile first).");
          continue;
        }

        var xmlPath = Path.Combine(tmpDir, blockName + ".xml");
        try
        {
          block.Export(new FileInfo(xmlPath), ExportOptions.None);
        }
        catch (Exception ex)
        {
          warnings.Add($"Export failed for '{blockName}': {ex.Message}");
          continue;
        }

        XDocument doc;
        try
        {
          doc = XDocument.Load(xmlPath);
        }
        catch (Exception ex)
        {
          warnings.Add($"Parse failed for '{blockName}': {ex.Message}");
          continue;
        }

        CausalTraceParser.AnalyzeBlock(doc, blockName, blockPath, normTag, tag, writeSites, readSites, allConditions);
        analyzedOk++;
      }
    }
    finally
    {
      try
      {
        Directory.Delete(tmpDir, true);
      }
      catch
      {
        // teardown：临时目录删不掉无处可报，也不影响已分析出的结果
      }
    }

    data["writeSites"] = writeSites;
    data["readSites"] = readSites;
    data["gatingConditions"] = new JsonArray([.. allConditions.OrderBy(x => x).Select(x => JsonValue.Create(x)),]);
    data["analyzedBlockCount"] = analyzedOk;
    if (warnings.Count > 0)
    {
      data["warnings"] = warnings;
    }

    string summary;
    if (codeBlocks.Count > 0 && analyzedOk == 0)
    {
      var onlineMode = warnings.Any(w => w!.ToString().IndexOf("online mode", StringComparison.OrdinalIgnoreCase) >= 0);
      summary = onlineMode
        ? "INCONCLUSIVE: no block could be exported because TIA is connected ONLINE to the PLC (Openness cannot export blocks in online mode). Go offline in TIA (Online ▸ Go offline) — the project stays open and the S7 live-read is a separate direct connection — then retry."
        : $"INCONCLUSIVE: none of {codeBlocks.Count} code block(s) could be exported/parsed (see warnings); no trace was performed.";
    }
    else if (writeSites.Count == 0)
    {
      summary =
        $"No block writes '{tag}'. It may be set by HMI, an instruction's output, an indirect/optimized access this parser does not resolve, or the name differs from the project symbol.";
    }
    else
    {
      summary =
        $"'{tag}' is written at {writeSites.Count} site(s). {allConditions.Count} distinct gating condition operand(s) found. " +
        "Live-read those with ReadPlcLiveValuesS7 to see which is currently driving the value.";
    }

    return new ResponseJsonReport
    {
      Ok = true,
      Message = summary,
      Data = data,
      Warnings = warnings.Count > 0
        ? [.. warnings.Select(w => w!.ToString()),]
        : null,
      Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, },
    };
  }

  // Live variant: run the offline trace, then resolve each gating-condition operand
  // to an absolute address via the PLC tag table and live-read the resolvable ones
  // over S7. DB members / optimized / symbolic operands have no absolute PLC-tag
  // address and are returned unresolved (read those via OPC UA symbolic). Read-only.
  public ResponseJsonReport TraceTagCauseLive(string softwarePath, string tag, string ip, int rack = 0, int slot = 1,
    string blockScope = "", string expectModuleContains = "")
  {
    if (string.IsNullOrWhiteSpace(ip))
    {
      return new ResponseJsonReport
      {
        Ok = false, Message = "ip is required for live tracing. For the offline-only trace, use TraceTagCause.",
      };
    }

    var trace = this.TraceTagCause(softwarePath, tag, blockScope);
    if (trace.Ok != true || trace.Data == null)
    {
      return trace;
    }

    var conditions = trace.Data["gatingConditions"] as JsonArray;
    var live = new JsonArray();
    var specToOperand = new Dictionary<string, string>();
    var specs = new List<string>();

    var plc = this.GetPlcSoftware(softwarePath);
    var map = plc != null
      ? this.BuildPlcTagAddressMap(plc)
      : new Dictionary<string, (string name, string address, string dataType)>();

    if (conditions != null)
    {
      foreach (var c in conditions)
      {
        var operand = c?.ToString() ?? "";
        if (operand.Length == 0)
        {
          continue;
        }

        var key = CausalTraceParser.NormalizeOperand(operand);
        if (map.TryGetValue(key, out var hit) && hit.address.StartsWith("%"))
        {
          var spec = S7LiveReader.TiaTagToSpec(hit.address, hit.dataType);
          if (spec != null)
          {
            if (!specToOperand.ContainsKey(spec))
            {
              specs.Add(spec);
              specToOperand[spec] = operand;
            }

            continue;
          }
        }

        live.Add(new JsonObject
        {
          ["operand"] = operand,
          ["resolved"] = false,
          ["hint"] =
            "no absolute PLC-tag address (DB member / optimized / symbolic) — read via OPC UA symbolic or a watch table",
        });
      }
    }

    long elapsed = 0;
    if (specs.Count > 0)
    {
      var read = S7LiveReader.ReadItems(ip,
        rack,
        slot,
        specs,
        string.IsNullOrWhiteSpace(expectModuleContains)
          ? null
          : expectModuleContains);
      elapsed = read.ElapsedMs;
      if (read.Error != null)
      {
        trace.Data["liveReadError"] = read.Error;
      }

      trace.Data["liveIdentity"] = new JsonObject
      {
        ["moduleTypeName"] = read.Identity.ModuleTypeName, ["szlError"] = read.Identity.SzlError,
      };
      foreach (var it in read.Items)
      {
        var op = specToOperand.TryGetValue(it.Spec, out var v)
          ? v
          : it.Spec;
        var o = new JsonObject
        {
          ["operand"] = op, ["resolved"] = true, ["address"] = it.Spec, ["type"] = it.Type,
        };
        if (it.Error != null)
        {
          o["error"] = it.Error;
        }
        else
        {
          o["value"] = JsonValue.Create(it.Value);
        }

        live.Add(o);
      }
    }

    trace.Data["liveChannel"] = "S7 / ISO-on-TCP (read-only)";
    trace.Data["liveGatingConditions"] = live;
    trace.Data["liveElapsedMs"] = elapsed;
    trace.Data["safety"] = new JsonObject
    {
      ["readOnly"] = true, ["writesValues"] = false, ["usesForce"] = false, ["changesCpuMode"] = false,
    };

    var total = conditions?.Count ?? 0;
    trace.Message =
      $"{trace.Message} Live-read {specs.Count}/{total} gating condition(s) over S7 ({total - specs.Count} unresolved).";
    return trace;
  }

  // name (normalized) -> (raw name, absolute LogicalAddress, TIA DataTypeName) for
  // every PLC tag in the software's tag tables (recursing user group folders).
  // Read-only reflection; tolerant of V20/V21 shape differences.
  private Dictionary<string, (string name, string address, string dataType)> BuildPlcTagAddressMap(object plc)
  {
    var map = new Dictionary<string, (string name, string address, string dataType)>(StringComparer.Ordinal);
    var tables = Portal.TryGetPropertyValue(plc, "TagTables") ??
      Portal.TryGetPropertyValue(Portal.TryGetPropertyValue(plc, "TagTableGroup", "TagTableFolder") ?? plc,
        "TagTables");
    this.CollectTagsFromTables(tables, map);
    this.CollectTagGroups(Portal.TryGetPropertyValue(plc, "TagTableGroup", "TagTableFolder"),
      map,
      []);
    return map;
  }

  private void CollectTagGroups(object? group, Dictionary<string, (string name, string address, string dataType)> map,
    HashSet<object> visited)
  {
    if (group == null || !visited.Add(group))
    {
      return;
    }

    this.CollectTagsFromTables(Portal.TryGetPropertyValue(group, "TagTables"), map);
    var subs = Portal.TryGetPropertyValue(group, "Groups");
    if (subs is not (IEnumerable en and not string))
    {
      return;
    }

    foreach (var g in en)
    {
      if (g != null)
      {
        this.CollectTagGroups(g, map, visited);
      }
    }
  }

  private void CollectTagsFromTables(object? tables,
    Dictionary<string, (string name, string address, string dataType)> map)
  {
    if (tables is not IEnumerable ten || tables is string)
    {
      return;
    }

    foreach (var table in ten)
    {
      if (table == null)
      {
        continue;
      }

      var tags = Portal.TryGetPropertyValue(table, "Tags");
      if (tags is not IEnumerable gen || tags is string)
      {
        continue;
      }

      foreach (var tag in gen)
      {
        if (tag == null)
        {
          continue;
        }

        var name = Portal.TryGetPropertyValue(tag, "Name")?.ToString() ?? "";
        if (name.Length == 0)
        {
          continue;
        }

        var addr = Portal.TryGetPropertyValue(tag, "LogicalAddress")?.ToString() ?? "";
        var dt = Portal.TryGetPropertyValue(tag, "DataTypeName")?.ToString() ?? "";
        var keyNorm = CausalTraceParser.NormalizeOperand(name);
        if (keyNorm.Length > 0 && !map.ContainsKey(keyNorm))
        {
          map[keyNorm] = (name, addr, dt);
        }
      }
    }
  }
}
