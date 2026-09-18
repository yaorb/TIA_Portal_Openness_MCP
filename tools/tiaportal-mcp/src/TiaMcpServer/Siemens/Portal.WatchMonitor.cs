#region

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Runtime;

#endregion

namespace TiaMcpServer.Siemens;

// Watch-table live monitoring: take an existing TIA watch table, read its entry
// addresses (read-only, via Openness), and live-read those addresses through the
// S7 runtime channel. Read-only end to end — no watch-table edits, no writes, no
// force. Symbolic entries and optimized-DB members that have no absolute address
// are reported as unresolved (read them through OPC UA instead).
public partial class Portal
{
  public ResponseJsonReport MonitorWatchTableLiveS7(string softwarePath, string watchTableName, string ip, int rack = 0,
    int slot = 1, string expectModuleContains = "")
  {
    var data = new JsonObject
    {
      ["softwarePath"] = softwarePath,
      ["watchTableName"] = watchTableName,
      ["ip"] = ip,
      ["channel"] = "watch-table (Openness, read-only) + S7 live read",
      ["safety"] = new JsonObject
      {
        ["readOnly"] = true, ["modifiesWatchTables"] = false, ["writesValues"] = false, ["usesForce"] = false,
      },
    };

    if (this.IsProjectNull())
    {
      return new ResponseJsonReport { Ok = false, Message = "No project open. Attach first.", Data = data, };
    }

    var plc = this.GetPlcSoftware(softwarePath);
    if (plc == null)
    {
      return new ResponseJsonReport
      {
        Ok = false, Message = $"PLC software not found at '{softwarePath}'.", Data = data,
      };
    }

    var group = Portal.ResolvePlcWatchAndForceTableGroup(plc);
    if (group == null)
    {
      return new ResponseJsonReport { Ok = false, Message = "WatchAndForceTableGroup not found.", Data = data, };
    }

    var tables = Portal.EnumeratePlcWatchTables(group);
    data["availableWatchTables"] = new JsonArray([.. tables.Select(x => JsonValue.Create(x.Name)),]);
    var table = tables.FirstOrDefault(x =>
      string.Equals(x.Path, watchTableName, StringComparison.OrdinalIgnoreCase) ||
      string.Equals(x.Name, watchTableName, StringComparison.OrdinalIgnoreCase)).Table;
    if (table == null)
    {
      return new ResponseJsonReport
      {
        Ok = false, Message = $"Watch table '{watchTableName}' not found.", Data = data,
      };
    }

    // Extract entry rows (address + name + display format).
    var rows = new List<(string name, string address, string fmt)>();
    var entries = Portal.TryGetPropertyValue(table, "Entries", "WatchTableEntries", "Rows", "Items");
    if (entries is IEnumerable en && !(entries is string))
    {
      foreach (var e in en)
      {
        if (e == null)
        {
          continue;
        }

        var addr = Portal.ReadEntryAttr(e, "Address");
        var name = Portal.ReadEntryAttr(e, "Name").Trim().Trim('"');
        var fmt = Portal.ReadEntryAttr(e, "DisplayFormat", "MonitorDisplayFormat", "Format");
        if (string.IsNullOrWhiteSpace(addr) && string.IsNullOrWhiteSpace(name))
        {
          continue;
        }

        rows.Add((name, addr, fmt));
      }
    }

    data["entryCount"] = rows.Count;

    if (rows.Count == 0)
    {
      data["note"] =
        "This watch table exposed no entries through Openness. It may be empty, or Openness cannot read rows authored in the TIA UI (known limitation). Read the values directly with ReadPlcLiveValuesS7 using explicit addresses.";
      return new ResponseJsonReport
      {
        Ok = true, Message = "Watch table has 0 readable entries via Openness.", Data = data,
      };
    }

    // Build S7 specs for resolvable absolute addresses.
    var specs = new List<string>();
    var specToRow = new Dictionary<string, (string name, string address)>();
    var unresolved = new JsonArray();
    foreach (var r in rows)
    {
      var spec = S7LiveReader.TiaAddressToSpec(r.address, r.fmt);
      if (spec == null)
      {
        unresolved.Add(new JsonObject
        {
          ["name"] = r.name,
          ["address"] = r.address,
          ["reason"] = "symbolic / optimized / no absolute address — use OPC UA",
        });
        continue;
      }

      if (!specToRow.ContainsKey(spec))
      {
        specs.Add(spec);
        specToRow[spec] = (r.name, r.address);
      }
    }

    var outRows = new JsonArray();
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
      data["identity"] = new JsonObject
      {
        ["moduleTypeName"] = read.Identity.ModuleTypeName, ["szlError"] = read.Identity.SzlError,
      };
      if (read.Error != null)
      {
        data["readError"] = read.Error;
      }

      foreach (var it in read.Items)
      {
        string rowName = "", rowAddr = it.Spec;
        if (specToRow.TryGetValue(it.Spec, out var ri))
        {
          rowName = ri.name;
          rowAddr = ri.address;
        }

        var o = new JsonObject
        {
          ["name"] = rowName, ["address"] = rowAddr, ["spec"] = it.Spec, ["type"] = it.Type,
        };
        if (it.Error != null)
        {
          o["error"] = it.Error;
        }
        else
        {
          o["value"] = JsonValue.Create(it.Value);
        }

        outRows.Add(o);
      }
    }

    data["values"] = outRows;
    data["unresolved"] = unresolved;
    data["elapsedMs"] = elapsed;

    var ok = specs.Count > 0 && outRows.OfType<JsonObject>().All(o => o["error"] == null);
    return new ResponseJsonReport
    {
      Ok = ok,
      Message = ok
        ? $"Monitored {outRows.Count} live value(s) from watch table '{watchTableName}' in {elapsed} ms ({unresolved.Count} unresolved)."
        : $"Watch table '{watchTableName}': {rows.Count} entries, {specs.Count} absolute, {unresolved.Count} unresolved. See values/unresolved.",
      Data = data,
      Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = ok, },
    };
  }

  private static string ReadEntryAttr(object entry, params string[] names)
  {
    // Try a property first, then GetAttribute(name).
    var prop = Portal.TryGetPropertyValue(entry, names);
    if (prop != null)
    {
      return prop.ToString() ?? "";
    }

    var getAttr = entry.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m =>
      m.Name == "GetAttribute" && m.GetParameters().Length == 1 &&
      m.GetParameters()[0].ParameterType == typeof(string));
    if (getAttr != null)
    {
      foreach (var n in names)
      {
        try
        {
          var v = getAttr.Invoke(entry, [n,]);
          if (v != null)
          {
            return v.ToString() ?? "";
          }
        }
        catch
        {
          // ignored
        }
      }
    }

    return "";
  }
}
