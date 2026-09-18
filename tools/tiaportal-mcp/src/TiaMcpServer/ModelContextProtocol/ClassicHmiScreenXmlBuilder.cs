#region

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

#endregion

namespace TiaMcpServer.ModelContextProtocol;

/// <summary>
///   Classic/Basic HMI 画面 XML 离线构建器。
///   这里只生成可审查、可解析的基础画面 XML；导入真实 TIA 项目前仍必须在临时项目里读回验证。
/// </summary>
public static class ClassicHmiScreenXmlBuilder
{
  public static JsonObject BuildFromJson(string designJson)
  {
    var design = JsonNode.Parse(designJson) as JsonObject ??
      throw new ArgumentException("Classic HMI design JSON root must be an object.", nameof(designJson));
    var document = ClassicHmiScreenXmlBuilder.BuildDocument(design);
    var xml = ClassicHmiScreenXmlBuilder.ToXml(document);
    var analysis = ClassicHmiScreenXmlBuilder.AnalyzeXml(xml);
    return new JsonObject
    {
      ["format"] = "tia-classic-hmi-screen-xml-offline-v1",
      ["timestamp"] = DateTime.Now.ToString("O"),
      ["offlineOnly"] = true,
      ["safetyPolicy"] = new JsonObject
      {
        ["tia"] = "Offline XML generation only; TIA Portal is not connected.",
        ["write"] = "No project, reference project, or delivery package content is modified.",
        ["apply"] =
          "Import this XML only in a temporary Classic/Basic HMI project first, then read back the screen and compile/diagnose before using a real project.",
      },
      ["ok"] = analysis["ok"]?.GetValue<bool>() == true,
      ["analysis"] = analysis,
      ["xml"] = xml,
    };
  }

  public static XDocument BuildDocument(JsonObject design)
  {
    var screen = design["Screen"] as JsonObject ?? design["screen"] as JsonObject ?? design;
    var screenName = ClassicHmiScreenXmlBuilder.GetString(screen, "Name", "name", "Classic_Generated_Screen");
    var width = ClassicHmiScreenXmlBuilder.GetInt(screen, "Width", "width", 640);
    var height = ClassicHmiScreenXmlBuilder.GetInt(screen, "Height", "height", 480);
    if (width < 320 || height < 240)
    {
      throw new ArgumentException("Classic HMI screen size must be at least 320x240.");
    }

    var items = (design["Items"] as JsonArray ?? design["items"] as JsonArray ?? []).OfType<JsonObject>()
      .ToArray();
    ClassicHmiScreenXmlBuilder.ValidateItems(items, width, height);

    var id = new ClassicIdAllocator();
    return new XDocument(new XDeclaration("1.0", "utf-8", null),
      new XElement("Document",
        new XElement("Engineering", new XAttribute("version", "V18")),
        new XElement("DocumentInfo",
          new XElement("Created", "2000-01-01T00:00:00.0000000Z"),
          new XElement("ExportSetting", "WithDefaults"),
          new XElement("InstalledProducts")),
        new XElement("Hmi.Screen.Screen",
          new XAttribute("ID", id.Next()),
          new XElement("AttributeList",
            new XElement("ActiveLayer", "0"),
            new XElement("BackColor",
              ClassicHmiScreenXmlBuilder.NormalizeColor(
                ClassicHmiScreenXmlBuilder.GetString(screen, "BackColor", "backColor", "242, 246, 250"))),
            new XElement("GridColor", "0, 0, 0"),
            new XElement("Height", height),
            new XElement("Name", screenName),
            new XElement("Number", ClassicHmiScreenXmlBuilder.GetInt(screen, "Number", "number", 1)),
            new XElement("Visible", "true"),
            new XElement("Width", width)),
          new XElement("ObjectList",
            new XElement("MultilingualText",
              new XAttribute("ID", id.Next()),
              new XAttribute("CompositionName", "HelpText"),
              ClassicHmiScreenXmlBuilder.BuildMultilingualTextItems(id, "")),
            new XElement("Hmi.Screen.ScreenLayer",
              new XAttribute("ID", id.Next()),
              new XAttribute("CompositionName", "Layers"),
              new XElement("AttributeList",
                new XElement("Index", "0"),
                new XElement("Name", ""),
                new XElement("VisibleES", "true")),
              new XElement("ObjectList", items.Select(item => ClassicHmiScreenXmlBuilder.BuildItem(id, item))))))));
  }

  public static JsonObject AnalyzeXml(string xml)
  {
    var errors = new JsonArray();
    var warnings = new JsonArray();
    var root = new JsonObject
    {
      ["ok"] = false,
      ["screenName"] = "",
      ["width"] = 0,
      ["height"] = 0,
      ["itemCount"] = 0,
      ["dynamicBindingCount"] = 0,
      ["eventActionCount"] = 0,
      ["errors"] = errors,
      ["warnings"] = warnings,
    };

    try
    {
      var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
      var screen = doc.Descendants("Hmi.Screen.Screen").FirstOrDefault();
      if (screen == null)
      {
        errors.Add("missing-screen: Hmi.Screen.Screen element is required.");
        return root;
      }

      var attr = screen.Element("AttributeList");
      root["screenName"] = attr?.Element("Name")?.Value ?? "";
      root["width"] = ClassicHmiScreenXmlBuilder.ParseInt(attr?.Element("Width")?.Value, 0);
      root["height"] = ClassicHmiScreenXmlBuilder.ParseInt(attr?.Element("Height")?.Value, 0);
      var items = doc.Descendants().Where(x => (string?)x.Attribute("CompositionName") == "ScreenItems").ToArray();
      root["itemCount"] = items.Length;
      root["dynamicBindingCount"] = doc.Descendants("Hmi.Dynamic.TagConnectionDynamic").Count();
      root["eventActionCount"] = doc.Descendants("Hmi.Event.FunctionListEntry").Count();
      if (items.Length == 0)
      {
        warnings.Add("empty-screen: no Classic HMI screen items found.");
      }

      var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var item in items)
      {
        var name = item.Element("AttributeList")?.Element("ObjectName")?.Value ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
          warnings.Add($"unnamed-item: {item.Name.LocalName}");
        }
        else if (!names.Add(name))
        {
          errors.Add($"duplicate-item-name: {name}");
        }
      }

      root["ok"] = errors.Count == 0;
      return root;
    }
    catch (Exception ex)
    {
      errors.Add($"xml-parse-error: {ex.Message}");
      return root;
    }
  }

  public static JsonObject AnalyzeFile(string path)
  {
    if (!File.Exists(path))
    {
      return new JsonObject { ["ok"] = false, ["path"] = path, ["errors"] = new JsonArray($"file-not-found: {path}"), };
    }

    var result = ClassicHmiScreenXmlBuilder.AnalyzeXml(File.ReadAllText(path, Encoding.UTF8));
    result["path"] = path;
    return result;
  }

  private static XElement BuildItem(ClassicIdAllocator id, JsonObject item)
  {
    var type = ClassicHmiScreenXmlBuilder.NormalizeItemType(
      ClassicHmiScreenXmlBuilder.GetString(item, "Type", "type", "Text"));
    var elementName = type switch
    {
      "Button"    => "Hmi.Screen.Button",
      "IOField"   => "Hmi.Screen.IOField",
      "Lamp"      => "Hmi.Screen.Rectangle",
      "Rectangle" => "Hmi.Screen.Rectangle",
      _           => "Hmi.Screen.TextField",
    };

    var name = ClassicHmiScreenXmlBuilder.GetString(item,
      "Name",
      "name",
      $"{elementName.Split('.').Last()}_{id.Peek()}");
    var text = ClassicHmiScreenXmlBuilder.ExtractText(item["Text"] ?? item["text"]);
    // V21 Classic HMI 各控件接受的属性子集严格不同。这里采用最小公共子集 + 类型扩展原则：
    //   通用：BackColor / BorderColor / BorderWidth / Width / Height / Left / Top / ObjectName
    //   Button / IOField：再加 Enabled / ForeColor / TabIndex / Visible
    //   TextField：仅加 ForeColor（不带 Enabled / TabIndex / Visible — 标签是被动控件）
    //   Rectangle：纯几何，啥都不加
    //   IOField：再加 Mode
    var attributes = new List<XElement>
    {
      new("BackColor",
        ClassicHmiScreenXmlBuilder.NormalizeColor(ClassicHmiScreenXmlBuilder.GetString(item,
          "BackColor",
          "backColor",
          ClassicHmiScreenXmlBuilder.GetPropertyString(item, "BackColor", "255, 255, 255")))),
      new("BorderColor",
        ClassicHmiScreenXmlBuilder.NormalizeColor(
          ClassicHmiScreenXmlBuilder.GetPropertyString(item, "BorderColor", "148, 163, 184"))),
      new("BorderWidth", ClassicHmiScreenXmlBuilder.GetPropertyInt(item, "BorderWidth", 1)),
      new("Height", ClassicHmiScreenXmlBuilder.GetInt(item, "Height", "height", 40)),
      new("Left", ClassicHmiScreenXmlBuilder.GetInt(item, "Left", "left", 0)),
      new("ObjectName", name),
      new("Top", ClassicHmiScreenXmlBuilder.GetInt(item, "Top", "top", 0)),
      new("Width", ClassicHmiScreenXmlBuilder.GetInt(item, "Width", "width", 120)),
    };

    if (elementName == "Hmi.Screen.Button" || elementName == "Hmi.Screen.IOField")
    {
      attributes.Add(new XElement("Enabled", "true"));
      attributes.Add(new XElement("ForeColor",
        ClassicHmiScreenXmlBuilder.NormalizeColor(
          ClassicHmiScreenXmlBuilder.GetPropertyString(item, "ForeColor", "30, 41, 59"))));
      attributes.Add(new XElement("TabIndex", ClassicHmiScreenXmlBuilder.GetPropertyInt(item, "TabIndex", 0)));
      attributes.Add(new XElement("Visible", "true"));
    }
    else if (elementName == "Hmi.Screen.TextField")
    {
      attributes.Add(new XElement("ForeColor",
        ClassicHmiScreenXmlBuilder.NormalizeColor(
          ClassicHmiScreenXmlBuilder.GetPropertyString(item, "ForeColor", "30, 41, 59"))));
    }
    // Rectangle: 不加任何额外属性

    if (elementName == "Hmi.Screen.IOField")
    {
      attributes.Add(new XElement("Mode", ClassicHmiScreenXmlBuilder.GetPropertyString(item, "Mode", "InOutput")));
    }

    // V21 Classic HMI 各控件接受的 MultilingualText composition 不同：
    //   TextField → "Text"（单个）
    //   Button    → "TextOff" + "TextOn"（两态文字，瞬时按钮两边一致即可）
    //   IOField / Rectangle / Lamp → 无 Text composition（IOField 走 Tag binding，Rectangle 是图形）
    var objList = new XElement("ObjectList");
    // Font composition 只对 Button / TextField / IOField 有效；Rectangle 是几何图形不带字体。
    if (elementName != "Hmi.Screen.Rectangle")
    {
      objList.Add(ClassicHmiScreenXmlBuilder.BuildFont(id,
        ClassicHmiScreenXmlBuilder.GetPropertyInt(item, "FontSize", 14)));
    }

    if (elementName == "Hmi.Screen.Button")
    {
      objList.Add(ClassicHmiScreenXmlBuilder.BuildMultilingualText(id, "TextOff", text));
      objList.Add(ClassicHmiScreenXmlBuilder.BuildMultilingualText(id, "TextOn", text));
    }
    else if (elementName == "Hmi.Screen.TextField")
    {
      objList.Add(ClassicHmiScreenXmlBuilder.BuildMultilingualText(id, "Text", text));
    }

    // IOField / Rectangle 不加 Text composition；交给 ProcessValue 动态绑定 / 静态颜色
    var pv = ClassicHmiScreenXmlBuilder.BuildProcessValueDynamic(id, item);
    if (pv != null)
    {
      objList.Add(pv);
    }

    var ea = ClassicHmiScreenXmlBuilder.BuildEventActions(id, item);
    objList.Add(ea);

    return new XElement(elementName,
      new XAttribute("ID", id.Next()),
      new XAttribute("CompositionName", "ScreenItems"),
      new XElement("AttributeList", attributes),
      objList);
  }

  private static XElement? BuildProcessValueDynamic(ClassicIdAllocator id, JsonObject item)
  {
    var tag = ClassicHmiScreenXmlBuilder.GetItemTag(item);
    if (string.IsNullOrWhiteSpace(tag))
    {
      return null;
    }

    return new XElement("Hmi.Screen.Property",
      new XAttribute("ID", id.Next()),
      new XAttribute("CompositionName", "Properties"),
      new XElement("AttributeList", new XElement("Name", "ProcessValue")),
      new XElement("ObjectList",
        new XElement("Hmi.Dynamic.TagConnectionDynamic",
          new XAttribute("ID", id.Next()),
          new XAttribute("CompositionName", "Dynamic"),
          new XElement("AttributeList", new XElement("Indirect", "false")),
          new XElement("LinkList",
            new XElement("Tag",
              new XAttribute("TargetID", "@OpenLink"),
              new XElement("Name", ClassicHmiScreenXmlBuilder.QuoteClassicTag(tag)))))));
  }

  private static IEnumerable<XElement> BuildEventActions(ClassicIdAllocator id, JsonObject item)
  {
    foreach (var action in item["Actions"] as JsonArray ?? item["actions"] as JsonArray ?? [])
    {
      if (action is not JsonObject obj)
      {
        continue;
      }

      var eventName =
        ClassicHmiScreenXmlBuilder.NormalizeClassicEvent(
          ClassicHmiScreenXmlBuilder.GetString(obj, "Event", "event", ""));
      var functionName =
        ClassicHmiScreenXmlBuilder.NormalizeClassicFunction(
          ClassicHmiScreenXmlBuilder.GetString(obj, "ActionKind", "actionKind", ""));
      var tag = ClassicHmiScreenXmlBuilder.GetString(obj,
        "TargetTag",
        "targetTag",
        ClassicHmiScreenXmlBuilder.GetString(obj, "Tag", "tag", ""));
      if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(functionName) ||
        string.IsNullOrWhiteSpace(tag))
      {
        continue;
      }

      yield return new XElement("Hmi.Event.Event",
        new XAttribute("ID", id.Next()),
        new XAttribute("CompositionName", "Events"),
        new XElement("AttributeList", new XElement("Name", eventName)),
        new XElement("ObjectList",
          new XElement("Hmi.Event.FunctionListEventHandler",
            new XAttribute("ID", id.Next()),
            new XAttribute("CompositionName", "EventHandler"),
            new XElement("ObjectList",
              new XElement("Hmi.Event.FunctionListEntry",
                new XAttribute("ID", id.Next()),
                new XAttribute("CompositionName", "FunctionListEntries"),
                new XElement("AttributeList",
                  new XElement("Name", functionName),
                  new XElement("Type", "SystemFunction")),
                new XElement("ObjectList",
                  new XElement("Hmi.Event.FunctionListEntryParameter",
                    new XAttribute("ID", id.Next()),
                    new XAttribute("CompositionName", "Parameters"),
                    new XElement("AttributeList", new XElement("Name", "Tag")),
                    new XElement("LinkList",
                      new XElement("Value",
                        new XAttribute("TargetID", "@OpenLink"),
                        new XElement("Name", ClassicHmiScreenXmlBuilder.QuoteClassicTag(tag)))))))))));
    }
  }

  private static XElement BuildFont(ClassicIdAllocator id, int fontSize) =>
    new("Hmi.Globalization.MultiLingualFont",
      new XAttribute("ID", id.Next()),
      new XAttribute("CompositionName", "Font"),
      new XElement("ObjectList",
        new XElement("Hmi.Globalization.FontItem",
          new XAttribute("ID", id.Next()),
          new XAttribute("CompositionName", "Items"),
          new XElement("AttributeList",
            new XElement("Culture", "zh-CN"),
            new XElement("FontFamily", "宋体"),
            new XElement("FontSize", Math.Max(8, fontSize)),
            new XElement("FontStyle", "Regular")))));

  private static XElement BuildMultilingualText(ClassicIdAllocator id, string compositionName, string text) =>
    new("MultilingualText",
      new XAttribute("ID", id.Next()),
      new XAttribute("CompositionName", compositionName),
      ClassicHmiScreenXmlBuilder.BuildMultilingualTextItems(id, text));

  // 兼容老调用签名（默认 Text composition）
  private static XElement BuildMultilingualText(ClassicIdAllocator id, string text) =>
    ClassicHmiScreenXmlBuilder.BuildMultilingualText(id, "Text", text);

  private static XElement BuildMultilingualTextItems(ClassicIdAllocator id, string? text)
  {
    // V21 Classic HMI 期望非空文字内容用 <body><p>...</p></body> HTML 包装；空字符串则保持空 Text。
    var safe = text ?? "";
    var textElement = string.IsNullOrEmpty(safe)
      ? new XElement("Text")
      : new XElement("Text", new XElement("body", new XElement("p", safe)));

    return new XElement("ObjectList",
      new XElement("MultilingualTextItem",
        new XAttribute("ID", id.Next()),
        new XAttribute("CompositionName", "Items"),
        new XElement("AttributeList", new XElement("Culture", "zh-CN"), textElement)));
  }

  private static void ValidateItems(JsonObject[] items, int screenWidth, int screenHeight)
  {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in items)
    {
      var name = ClassicHmiScreenXmlBuilder.GetString(item, "Name", "name", "");
      if (string.IsNullOrWhiteSpace(name))
      {
        throw new ArgumentException("Classic HMI item Name is required.");
      }

      if (!names.Add(name))
      {
        throw new ArgumentException($"Duplicate Classic HMI item name: {name}");
      }

      var width = ClassicHmiScreenXmlBuilder.GetInt(item, "Width", "width", 120);
      var height = ClassicHmiScreenXmlBuilder.GetInt(item, "Height", "height", 40);
      var left = ClassicHmiScreenXmlBuilder.GetInt(item, "Left", "left", 0);
      var top = ClassicHmiScreenXmlBuilder.GetInt(item, "Top", "top", 0);
      if (width <= 0 || height <= 0)
      {
        throw new ArgumentException($"Classic HMI item size must be positive: {name}");
      }

      if (left < 0 || top < 0 || left + width > screenWidth || top + height > screenHeight)
      {
        throw new ArgumentException($"Classic HMI item is outside the screen: {name}");
      }
    }
  }

  private static string NormalizeItemType(string type)
  {
    if (string.IsNullOrWhiteSpace(type))
    {
      return "Text";
    }

    if (type.StartsWith("Hmi", StringComparison.OrdinalIgnoreCase))
    {
      type = type.Substring(3);
    }

    return type.Equals("TextField", StringComparison.OrdinalIgnoreCase)
      ? "Text"
      : type;
  }

  private static string NormalizeColor(string color)
  {
    if (string.IsNullOrWhiteSpace(color))
    {
      return "255, 255, 255";
    }

    color = color.Trim();
    if (!color.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
      return color;
    }

    var hex = color.Substring(color.Length == 10
      ? 4
      : 2);
    if (hex.Length != 6)
    {
      return color;
    }

    var r = Convert.ToInt32(hex.Substring(0, 2), 16);
    var g = Convert.ToInt32(hex.Substring(2, 2), 16);
    var b = Convert.ToInt32(hex.Substring(4, 2), 16);
    return $"{r}, {g}, {b}";
  }

  private static string ExtractText(JsonNode? node)
  {
    if (node == null)
    {
      return "";
    }

    if (node is JsonValue)
    {
      return node.ToString();
    }

    if (node is JsonObject obj)
    {
      return obj["zh-CN"]?.ToString() ?? obj["zh"]?.ToString() ?? obj.FirstOrDefault().Value?.ToString() ?? "";
    }

    return node.ToString();
  }

  private static string GetItemTag(JsonObject item)
  {
    var props = item["Properties"] as JsonObject ?? item["properties"] as JsonObject;
    return item["ProcessValueTag"]?.ToString() ?? item["processValueTag"]?.ToString() ?? item["HmiTag"]?.ToString() ??
      item["hmiTag"]?.ToString() ?? item["Tag"]?.ToString() ?? item["tag"]?.ToString() ??
      props?["ProcessValueTag"]?.ToString() ?? props?["processValueTag"]?.ToString() ?? props?["HmiTag"]?.ToString() ??
      props?["hmiTag"]?.ToString() ?? props?["Tag"]?.ToString() ?? props?["tag"]?.ToString() ?? "";
  }

  private static string NormalizeClassicEvent(string? eventName)
  {
    return (eventName ?? "").Trim().ToLowerInvariant() switch
    {
      "pressed"  => "Press",
      "press"    => "Press",
      "released" => "Release",
      "release"  => "Release",
      "tapped"   => "Click",
      "clicked"  => "Click",
      "click"    => "Click",
      _          => eventName ?? "",
    };
  }

  private static string NormalizeClassicFunction(string? actionKind)
  {
    var kind = (actionKind ?? "").Trim().ToLowerInvariant();
    return kind switch
    {
      "set-bit"       => "SetBit",
      "setbit"        => "SetBit",
      "setbitintag"   => "SetBit",
      "reset-bit"     => "ResetBit",
      "resetbit"      => "ResetBit",
      "resetbitintag" => "ResetBit",
      _               => "",
    };
  }

  private static string QuoteClassicTag(string? tag)
  {
    tag = (tag ?? "").Trim();
    if (tag.StartsWith("\"", StringComparison.Ordinal) && tag.EndsWith("\"", StringComparison.Ordinal))
    {
      return tag;
    }

    return $"\"{tag.Replace("\"", "")}\"";
  }

  private static string GetString(JsonObject obj, string pascal, string camel, string fallback) =>
    obj[pascal]?.ToString() ?? obj[camel]?.ToString() ?? fallback;

  private static int GetInt(JsonObject obj, string pascal, string camel, int fallback) =>
    ClassicHmiScreenXmlBuilder.ParseInt(obj[pascal]?.ToString() ?? obj[camel]?.ToString(), fallback);

  private static string GetPropertyString(JsonObject item, string name, string fallback)
  {
    var props = item["Properties"] as JsonObject ?? item["properties"] as JsonObject;
    return props?[name]?.ToString() ??
      props?[char.ToLowerInvariant(name[0]) + name.Substring(1)]?.ToString() ?? fallback;
  }

  private static int GetPropertyInt(JsonObject item, string name, int fallback) =>
    ClassicHmiScreenXmlBuilder.ParseInt(ClassicHmiScreenXmlBuilder.GetPropertyString(item, name, fallback.ToString()),
      fallback);

  private static int ParseInt(string? value, int fallback)
  {
    if (int.TryParse(value, out var result))
    {
      return result;
    }

    if (double.TryParse(value, out var dbl))
    {
      return (int)Math.Round(dbl);
    }

    return fallback;
  }

  private static string ToXml(XDocument document)
  {
    using var writer = new Utf8StringWriter();
    document.Save(writer, SaveOptions.None);
    return writer.ToString();
  }

  private sealed class ClassicIdAllocator
  {
    private int _next;
    public string Next() => (this._next++).ToString("X");
    public string Peek() => this._next.ToString("X");
  }

  private sealed class Utf8StringWriter : StringWriter
  {
    public override Encoding Encoding => Encoding.UTF8;
  }
}
