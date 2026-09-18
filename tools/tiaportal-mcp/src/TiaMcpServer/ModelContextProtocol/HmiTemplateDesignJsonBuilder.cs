#region

using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

#endregion

namespace TiaMcpServer.ModelContextProtocol;

public static class HmiTemplateDesignJsonBuilder
{
  public static string BuildApplyDesignJson(string templateFile, int fallbackWidth, int fallbackHeight) =>
    HmiTemplateDesignJsonBuilder.BuildApplyDesign(templateFile, fallbackWidth, fallbackHeight).ToJsonString(
      new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, });

  public static JsonObject BuildApplyDesign(string templateFile, int fallbackWidth, int fallbackHeight)
  {
    var root = JsonNode.Parse(File.ReadAllText(templateFile, Encoding.UTF8)) as JsonObject ??
      throw new InvalidOperationException($"HMI template JSON root must be an object: {templateFile}");
    return HmiTemplateDesignJsonBuilder.BuildApplyDesign(root, fallbackWidth, fallbackHeight);
  }

  // 不能收窄成 private：离线单测工程直接链接本文件，这个重载（直接吃已解析的 JsonObject、
  // 不碰文件系统）正是往里面喂真输入用的入口。
  public static JsonObject BuildApplyDesign(JsonObject root, int fallbackWidth, int fallbackHeight)
  {
    var screen = root["Screen"] as JsonObject ?? root["screen"] as JsonObject ?? new JsonObject();
    var screenOut = new JsonObject();
    if (screen["Properties"] is JsonObject screenProps)
    {
      foreach (var prop in screenProps)
      {
        screenOut[prop.Key] = prop.Value?.DeepClone();
      }
    }

    var itemsOut = new JsonArray();
    var items = root["Items"] as JsonArray ?? root["items"] as JsonArray ?? [];
    foreach (var node in items.OfType<JsonObject>())
    {
      var item = new JsonObject
      {
        ["name"] = node["Name"]?.ToString() ?? node["name"]?.ToString() ?? "",
        ["type"] =
          HmiTemplateDesignJsonBuilder.NormalizeUnifiedHmiItemType(node["Type"]?.ToString() ??
            node["type"]?.ToString() ?? "Rectangle"),
        ["left"] = HmiTemplateDesignJsonBuilder.CloneJsonValueOrDefault(node["Left"] ?? node["left"], 0),
        ["top"] = HmiTemplateDesignJsonBuilder.CloneJsonValueOrDefault(node["Top"] ?? node["top"], 0),
        ["width"] = HmiTemplateDesignJsonBuilder.CloneJsonValueOrDefault(node["Width"] ?? node["width"], 120),
        ["height"] = HmiTemplateDesignJsonBuilder.CloneJsonValueOrDefault(node["Height"] ?? node["height"], 40),
      };

      var text = HmiTemplateDesignJsonBuilder.ExtractTemplateText(node["Text"] ?? node["text"]);
      if (!string.IsNullOrWhiteSpace(text))
      {
        item["text"] = text;
        item["culture"] = "zh-CN";
      }

      if (node["Properties"] is JsonObject || node["properties"] is JsonObject)
      {
        var source = node["Properties"] as JsonObject ?? node["properties"] as JsonObject ?? new JsonObject();
        var itemType = item["type"]?.ToString() ?? "";
        var executionProps = new JsonObject();
        foreach (var prop in source)
        {
          if (prop.Key.Equals("FontSize", StringComparison.OrdinalIgnoreCase))
          {
            continue;
          }

          if (itemType.Equals("Text", StringComparison.OrdinalIgnoreCase) &&
            prop.Key.Equals("BackColor", StringComparison.OrdinalIgnoreCase))
          {
            continue;
          }

          executionProps[prop.Key] = prop.Value?.DeepClone();
        }

        if (executionProps.Count > 0)
        {
          item["properties"] = executionProps;
        }

        // 中文说明：执行 JSON 只写当前 Openness 验证过的样式路径，完整设计意图仍保留在模板文件中。
        if (source["FontSize"] is { } fontSize)
        {
          item["font"] = new JsonObject { ["Size"] = fontSize.DeepClone(), };
        }
      }

      itemsOut.Add(item);
    }

    return new JsonObject
    {
      ["screen"] = screenOut,
      ["items"] = itemsOut,
      ["width"] = screen["Width"]?.DeepClone() ?? JsonValue.Create(fallbackWidth),
      ["height"] = screen["Height"]?.DeepClone() ?? JsonValue.Create(fallbackHeight),
    };
  }

  private static JsonNode CloneJsonValueOrDefault(JsonNode? node, int fallback) =>
    node?.DeepClone() ?? JsonValue.Create(fallback);

  private static string NormalizeUnifiedHmiItemType(string type)
  {
    if (string.IsNullOrWhiteSpace(type))
    {
      return "Rectangle";
    }

    return type.StartsWith("Hmi", StringComparison.OrdinalIgnoreCase)
      ? type[3..]
      : type;
  }

  private static string ExtractTemplateText(JsonNode? node)
  {
    switch (node)
    {
      case null:
        return "";

      case JsonValue value:
        return HmiTemplateDesignJsonBuilder.StripHtmlText(value.ToString());

      case JsonObject obj:
      {
        var zh = obj["zh-CN"]?.ToString() ?? obj["zh"]?.ToString() ?? obj.FirstOrDefault().Value?.ToString() ?? "";
        return HmiTemplateDesignJsonBuilder.StripHtmlText(zh);
      }

      default:
        return HmiTemplateDesignJsonBuilder.StripHtmlText(node.ToString());
    }
  }

  private static string StripHtmlText(string text)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return "";
    }

    var value = Regex.Replace(text, "<[^>]+>", "");
    return SecurityElement.Escape(value)?.Replace("\\n", Environment.NewLine) ?? "";
  }
}
