#region

using System;
using System.Collections;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.OpcUa;
using TiaMcpServer.ModelContextProtocol;

#endregion

namespace TiaMcpServer.Siemens;

// Partial: opcua. Extracted from Portal.cs (god-file split); behavior unchanged.
public partial class Portal
{
  #region opcua

  /// <summary>Returns the ServerInterfaceGroup node via reflection chain.</summary>
  private static object? GetOpcUaServerInterfaceGroup(PlcSoftware plc)
  {
    var provider = plc.GetService<OpcUaProvider>();
    if (provider == null)
    {
      return null;
    }

    var commGroup = Portal.TryGetPropertyValue(provider, "CommunicationGroup");
    if (commGroup == null)
    {
      return null;
    }

    return Portal.TryGetPropertyValue(commGroup, "ServerInterfaceGroup");
  }

  public ResponseJsonReport GetOpcUaConfig(string softwarePath)
  {
    var data = new JsonObject { ["softwarePath"] = softwarePath, ["timestamp"] = DateTime.Now.ToString("O"), };

    if (this.IsProjectNull())
    {
      return new ResponseJsonReport { Ok = false, Message = "No project open.", Data = data, };
    }

    var plc = this.GetPlcSoftware(softwarePath);
    if (plc == null)
    {
      return new ResponseJsonReport
      {
        Ok = false, Message = $"PLC software not found: '{softwarePath}'.", Data = data,
      };
    }

    try
    {
      var provider = plc.GetService<OpcUaProvider>();
      if (provider == null)
      {
        return new ResponseJsonReport
        {
          Ok = false, Message = "OpcUaProvider not available for this PLC.", Data = data,
        };
      }

      var sig = Portal.GetOpcUaServerInterfaceGroup(plc);
      if (sig == null)
      {
        return new ResponseJsonReport { Ok = false, Message = "ServerInterfaceGroup not accessible.", Data = data, };
      }

      data["serverInterfaces"] = Portal.CollectOpcUaItems(Portal.TryGetPropertyValue(sig, "ServerInterfaces"));
      data["simaticInterfaces"] = Portal.CollectOpcUaItems(Portal.TryGetPropertyValue(sig, "SimaticInterfaces"));
      data["referenceNamespaces"] = Portal.CollectOpcUaItems(Portal.TryGetPropertyValue(sig, "ReferenceNamespaces"));

      return new ResponseJsonReport { Ok = true, Message = $"OPC UA config read for '{softwarePath}'.", Data = data, };
    }
    catch (Exception ex)
    {
      logger?.LogError(ex, "GetOpcUaConfig failed for {SoftwarePath}", softwarePath);
      return new ResponseJsonReport { Ok = false, Message = $"Error: {ex.Message}", Data = data, };
    }
  }

  private static JsonArray CollectOpcUaItems(object? collection)
  {
    var arr = new JsonArray();
    if (collection is not IEnumerable items || collection is string)
    {
      return arr;
    }

    foreach (var item in items)
    {
      if (item == null)
      {
        continue;
      }

      var obj = new JsonObject();
      foreach (var prop in new[]
        {
          "Name", "Comment", "Author", "Enabled", "UseStringNodeIds", "GenerateNodes", "GeneratedInterfaceName",
        })
      {
        var val = Portal.TryGetPropertyValue(item, prop);
        if (val != null)
        {
          obj[prop] = JsonValue.Create(val.ToString());
        }
      }

      arr.Add(obj);
    }

    return arr;
  }

  public ResponseMessage SetOpcUaInterfaceEnabled(string softwarePath, string interfaceName, bool enabled,
    string interfaceType = "ServerInterface")
  {
    if (this.IsProjectNull())
    {
      return new ResponseMessage { Message = "No project open.", };
    }

    var plc = this.GetPlcSoftware(softwarePath);
    if (plc == null)
    {
      return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'.", };
    }

    try
    {
      var sig = Portal.GetOpcUaServerInterfaceGroup(plc);
      if (sig == null)
      {
        return new ResponseMessage { Message = "ServerInterfaceGroup not accessible.", };
      }

      var collectionProp = interfaceType switch
      {
        "SimaticInterface"   => "SimaticInterfaces",
        "ReferenceNamespace" => "ReferenceNamespaces",
        _                    => "ServerInterfaces",
      };

      var collection = Portal.TryGetPropertyValue(sig, collectionProp);
      var item = Portal.FindByName(collection, interfaceName);
      if (item == null)
      {
        return new ResponseMessage { Message = $"{interfaceType} '{interfaceName}' not found in '{softwarePath}'.", };
      }

      Portal.TrySetProperty(item, "Enabled", enabled);
      return new ResponseMessage
      {
        Message =
          $"{interfaceType} '{interfaceName}' {(enabled ? "enabled" : "disabled")}. Download to PLC to apply.",
        Meta = new JsonObject
        {
          ["softwarePath"] = softwarePath, ["interfaceName"] = interfaceName, ["enabled"] = enabled,
        },
      };
    }
    catch (Exception ex)
    {
      logger?.LogError(ex, "SetOpcUaInterfaceEnabled failed");
      return new ResponseMessage { Message = $"Error: {ex.Message}", };
    }
  }

  public ResponseMessage ExportOpcUaInterface(string softwarePath, string interfaceName, string exportPath,
    string interfaceType = "ServerInterface")
  {
    if (this.IsProjectNull())
    {
      return new ResponseMessage { Message = "No project open.", };
    }

    var plc = this.GetPlcSoftware(softwarePath);
    if (plc == null)
    {
      return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'.", };
    }

    try
    {
      var sig = Portal.GetOpcUaServerInterfaceGroup(plc);
      if (sig == null)
      {
        return new ResponseMessage { Message = "ServerInterfaceGroup not accessible.", };
      }

      var collectionProp = interfaceType switch
      {
        "SimaticInterface"   => "SimaticInterfaces",
        "ReferenceNamespace" => "ReferenceNamespaces",
        _                    => "ServerInterfaces",
      };

      var collection = Portal.TryGetPropertyValue(sig, collectionProp);
      var item = Portal.FindByName(collection, interfaceName);
      if (item == null)
      {
        return new ResponseMessage { Message = $"{interfaceType} '{interfaceName}' not found.", };
      }

      Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
      Portal.TryInvokeMethodByName(item, "Export", new FileInfo(exportPath));
      return new ResponseMessage
      {
        Message = $"{interfaceType} '{interfaceName}' exported to '{exportPath}'.",
        Meta = new JsonObject { ["exportPath"] = exportPath, },
      };
    }
    catch (Exception ex)
    {
      logger?.LogError(ex, "ExportOpcUaInterface failed");
      return new ResponseMessage { Message = $"Export failed: {ex.Message}", };
    }
  }

  public ResponseMessage ImportOpcUaInterface(string softwarePath, string importPath,
    string interfaceType = "ServerInterface")
  {
    if (this.IsProjectNull())
    {
      return new ResponseMessage { Message = "No project open.", };
    }

    var plc = this.GetPlcSoftware(softwarePath);
    if (plc == null)
    {
      return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'.", };
    }

    try
    {
      var sig = Portal.GetOpcUaServerInterfaceGroup(plc);
      if (sig == null)
      {
        return new ResponseMessage { Message = "ServerInterfaceGroup not accessible.", };
      }

      var collectionProp = interfaceType switch
      {
        "ReferenceNamespace" => "ReferenceNamespaces",
        _                    => "ServerInterfaces",
      };

      var collection = Portal.TryGetPropertyValue(sig, collectionProp);
      if (collection == null)
      {
        return new ResponseMessage { Message = $"{collectionProp} collection not accessible.", };
      }

      // ServerInterfaceComposition.Create(name) then Import(file)
      // OR find existing and call Import
      var fi = new FileInfo(importPath);
      var interfaceName = Path.GetFileNameWithoutExtension(importPath);
      var existing = Portal.FindByName(collection, interfaceName);

      if (existing != null)
      {
        Portal.TryInvokeMethodByName(existing, "Import", fi);
        return new ResponseMessage
        {
          Message = $"Existing {interfaceType} '{interfaceName}' updated from '{importPath}'.",
        };
      }

      // Try Create then Import
      var created = Portal.TryInvokeMethodByName(collection, "Create", interfaceName);
      if (created != null)
      {
        Portal.TryInvokeMethodByName(created, "Import", fi);
      }

      return new ResponseMessage
      {
        Message = created != null
          ? $"{interfaceType} '{interfaceName}' created and imported from '{importPath}'."
          : $"Could not create {interfaceType} '{interfaceName}'. Try importing via ExportOpcUaInterface first.",
      };
    }
    catch (Exception ex)
    {
      logger?.LogError(ex, "ImportOpcUaInterface failed");
      return new ResponseMessage { Message = $"Import failed: {ex.Message}", };
    }
  }

  private static object? FindByName(object? collection, string name)
  {
    if (collection is not IEnumerable items || collection is string)
    {
      return null;
    }

    foreach (var item in items)
    {
      if (item == null)
      {
        continue;
      }

      if (string.Equals(Portal.TryGetPropertyValue(item, "Name")?.ToString(), name, StringComparison.OrdinalIgnoreCase))
      {
        return item;
      }
    }

    return null;
  }

  #endregion
}
