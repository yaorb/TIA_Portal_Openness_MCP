#region

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

#endregion

namespace TiaMcpServer.ModelContextProtocol;

/// <summary>
///   PLC FC 块 XML 组合器。
///   第一版只组合 SCL FC：接口 + 单个 StructuredText CompileUnit。
/// </summary>
public static class PlcFcBlockXmlComposer
{
  private static readonly XNamespace InterfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

  private static readonly XNamespace StructuredTextNs =
    "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4";

  public static XDocument Compose(string blockName, int blockNumber, IEnumerable<PlcBlockMemberDefinition>? inputMembers,
    IEnumerable<PlcBlockMemberDefinition>? outputMembers, string structuredTextInnerXml, string blockCommentZhCn = "",
    string blockTitleZhCn = "", string networkCommentZhCn = "", string networkTitleZhCn = "")
  {
    if (string.IsNullOrWhiteSpace(blockName))
    {
      throw new ArgumentException("FC 块名称不能为空。", nameof(blockName));
    }

    if (blockNumber <= 0)
    {
      throw new ArgumentException("FC 块编号必须大于 0。", nameof(blockNumber));
    }

    if (string.IsNullOrWhiteSpace(structuredTextInnerXml))
    {
      throw new ArgumentException(
        "StructuredText 内容不能为空：JSON 里的 $.structuredText.operations 是空数组，" + "$.structuredTextInnerXml 也没给。至少给一条操作，例如 " +
        "{\"op\":\"line\",\"text\":\"MyTemp := TRUE;\"}。" + "（报错里的参数名是构建器内部的形参，不是你要填的 JSON 字段名。）",
        nameof(structuredTextInnerXml));
    }

    var inputs = inputMembers?.ToArray() ?? throw new ArgumentNullException(nameof(inputMembers));
    var outputs = outputMembers?.ToArray() ?? throw new ArgumentNullException(nameof(outputMembers));
    PlcFcBlockXmlComposer.ValidateMembers([.. inputs, .. outputs,]);

    var st = XElement.Parse(
      $"<StructuredText xmlns=\"{PlcFcBlockXmlComposer.StructuredTextNs}\">{structuredTextInnerXml}</StructuredText>");

    // CompileUnit ObjectList: 网络级 Comment + Title
    var compileUnitObjList = new XElement("ObjectList",
      PlcBlockXmlHelpers.BuildMultilingualText("4", "5", "Comment", networkCommentZhCn),
      PlcBlockXmlHelpers.BuildMultilingualText("6", "7", "Title", networkTitleZhCn));
    var compileUnit = new XElement("SW.Blocks.CompileUnit",
      new XAttribute("ID", "3"),
      new XAttribute("CompositionName", "CompileUnits"),
      new XElement("AttributeList", new XElement("NetworkSource", st), new XElement("ProgrammingLanguage", "SCL")),
      compileUnitObjList);
    // 块级 ObjectList: Comment → CompileUnit → Title（与 TIA 真实导出 schema 顺序一致）
    var blockObjList = new XElement("ObjectList",
      PlcBlockXmlHelpers.BuildMultilingualText("1", "2", "Comment", blockCommentZhCn),
      compileUnit,
      PlcBlockXmlHelpers.BuildMultilingualText("8", "9", "Title", blockTitleZhCn));

    return new XDocument(new XDeclaration("1.0", "utf-8", null),
      new XElement("Document",
        new XElement("Engineering", new XAttribute("version", "V21")),
        new XElement("DocumentInfo",
          new XElement("Created", "2000-01-01T00:00:00.0000000Z"),
          new XElement("ExportSetting", "None"),
          new XElement("InstalledProducts")),
        new XElement("SW.Blocks.FC",
          new XAttribute("ID", "0"),
          new XElement("AttributeList",
            new XElement("Interface",
              new XElement(PlcFcBlockXmlComposer.InterfaceNs + "Sections",
                PlcFcBlockXmlComposer.BuildSection("Input", inputs),
                PlcFcBlockXmlComposer.BuildSection("Output", outputs),
                PlcFcBlockXmlComposer.BuildSection("InOut", []),
                PlcFcBlockXmlComposer.BuildSection("Temp", []),
                PlcFcBlockXmlComposer.BuildSection("Constant", []),
                PlcFcBlockXmlComposer.BuildSection("Return", [new PlcBlockMemberDefinition("Ret_Val", "Void"),]))),
            new XElement("MemoryLayout", "Optimized"),
            new XElement("Name", blockName),
            new XElement("Namespace"),
            new XElement("Number", blockNumber),
            new XElement("ProgrammingLanguage", "SCL"),
            new XElement("SetENOAutomatically", "false")),
          blockObjList)));
  }

  public static string ComposeXml(string blockName, int blockNumber, IEnumerable<PlcBlockMemberDefinition> inputMembers,
    IEnumerable<PlcBlockMemberDefinition> outputMembers, string structuredTextInnerXml, string blockCommentZhCn = "",
    string blockTitleZhCn = "", string networkCommentZhCn = "", string networkTitleZhCn = "")
  {
    using var writer = new Utf8StringWriter();
    PlcFcBlockXmlComposer
      .Compose(blockName,
        blockNumber,
        inputMembers,
        outputMembers,
        structuredTextInnerXml,
        blockCommentZhCn,
        blockTitleZhCn,
        networkCommentZhCn,
        networkTitleZhCn).Save(writer, SaveOptions.None);
    return writer.ToString();
  }

  public static JsonObject RunProbe(string fixtureDirectory, string reportDirectory)
  {
    Directory.CreateDirectory(reportDirectory);
    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    var goldenPath = Path.Combine(fixtureDirectory, "FC_StartStop.xml");
    var generatedPath = Path.Combine(reportDirectory, $"FC_StartStop.composed_{stamp}.xml");
    var jsonPath = Path.Combine(reportDirectory, $"plc_fc_block_composer_probe_{stamp}.json");
    var mdPath = Path.Combine(reportDirectory, $"plc_fc_block_composer_probe_{stamp}.md");

    var st = new StructuredTextXmlBuilder().IfHeader("EStop").Assignment("Run", "FALSE", 2).ElseLine()
      .IfHeader("Stop", 2).Assignment("Run", "FALSE", 4).EndIf(2).IfHeader("Start", 2).Assignment("Run", "TRUE", 4)
      .EndIf(2).Token("END_IF").Token(";").BuildInnerXml();

    var xml = PlcFcBlockXmlComposer.ComposeXml("FC_StartStop",
      1,
      [
        new PlcBlockMemberDefinition("Start", "Bool"), new PlcBlockMemberDefinition("Stop", "Bool"),
        new PlcBlockMemberDefinition("EStop", "Bool"),
      ],
      [new PlcBlockMemberDefinition("Run", "Bool"),],
      st);
    File.WriteAllText(generatedPath, xml, Encoding.UTF8);

    var golden = PlcFcBlockXmlComposer.AnalyzeFcBlock(goldenPath);
    var generated = PlcFcBlockXmlComposer.AnalyzeFcBlock(generatedPath);
    var semanticEqual = PlcFcBlockXmlComposer.CompareSemantics(golden, generated);

    var root = new JsonObject
    {
      ["timestamp"] = DateTime.Now.ToString("O"),
      ["mode"] = "offline-plc-fc-block-composer-probe",
      ["goldenPath"] = goldenPath,
      ["generatedPath"] = generatedPath,
      ["safetyPolicy"] =
        new JsonObject
        {
          ["tia"] = "离线生成和解析 FC XML，不连接 TIA Portal，不导入 PLC 块。",
          ["write"] = "只写 reports 目录下的生成样本和探针报告，不修改 TMP_EXPORT 或交付包。",
        },
      ["golden"] = golden,
      ["generated"] = generated,
      ["semanticEqual"] = semanticEqual,
      ["ok"] = golden["ok"]?.GetValue<bool>() == true && generated["ok"]?.GetValue<bool>() == true && semanticEqual,
    };

    File.WriteAllText(jsonPath,
      root.ToJsonString(new JsonSerializerOptions
      {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
      }),
      Encoding.UTF8);
    File.WriteAllText(mdPath, PlcFcBlockXmlComposer.BuildProbeMarkdown(root, jsonPath), Encoding.UTF8);
    root["jsonPath"] = jsonPath;
    root["markdownPath"] = mdPath;
    return root;
  }

  public static JsonObject AnalyzeFcBlock(string path)
  {
    var root = new JsonObject { ["path"] = path, ["exists"] = File.Exists(path), };
    if (!File.Exists(path))
    {
      root["ok"] = false;
      root["error"] = "文件不存在。";
      return root;
    }

    try
    {
      var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
      var fc = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "SW.Blocks.FC");
      var attrs = fc?.Element("AttributeList");
      var members = doc.Descendants().Where(x => x.Name.LocalName == "Section").SelectMany(section =>
        section.Elements().Where(x => x.Name.LocalName == "Member").Select(member => new JsonObject
        {
          ["section"] = section.Attribute("Name")?.Value ?? "",
          ["name"] = member.Attribute("Name")?.Value ?? "",
          ["datatype"] = member.Attribute("Datatype")?.Value ?? "",
        })).ToArray();
      var st = StructuredTextXmlBuilder.AnalyzeStructuredText(path);

      root["ok"] = fc != null && attrs != null && st["ok"]?.GetValue<bool>() == true;
      root["blockName"] = attrs?.Element("Name")?.Value ?? "";
      root["number"] = attrs?.Element("Number")?.Value ?? "";
      root["programmingLanguage"] = attrs?.Element("ProgrammingLanguage")?.Value ?? "";
      root["memoryLayout"] = attrs?.Element("MemoryLayout")?.Value ?? "";
      root["compileUnitCount"] = doc.Descendants().Count(x => x.Name.LocalName == "SW.Blocks.CompileUnit");
      root["members"] = new JsonArray(members);
      root["structuredText"] = st;
      return root;
    }
    catch (Exception ex)
    {
      root["ok"] = false;
      root["error"] = ex.Message;
      return root;
    }
  }

  private static XElement BuildSection(string name, IReadOnlyCollection<PlcBlockMemberDefinition> members)
  {
    if (members.Count == 0)
    {
      return new XElement(PlcFcBlockXmlComposer.InterfaceNs + "Section", new XAttribute("Name", name));
    }

    var section = new XElement(PlcFcBlockXmlComposer.InterfaceNs + "Section", new XAttribute("Name", name));
    foreach (var m in members)
    {
      var memEl = new XElement(PlcFcBlockXmlComposer.InterfaceNs + "Member",
        new XAttribute("Name", m.Name),
        new XAttribute("Datatype", m.Datatype));
      PlcBlockXmlHelpers.AppendMemberCommentIfAny(memEl, m.CommentZhCn);
      section.Add(memEl);
    }

    return section;
  }

  private static bool CompareSemantics(JsonObject golden, JsonObject generated) =>
    golden["blockName"]?.ToString() == generated["blockName"]?.ToString() &&
    golden["number"]?.ToString() == generated["number"]?.ToString() &&
    golden["programmingLanguage"]?.ToString() == generated["programmingLanguage"]?.ToString() &&
    golden["memoryLayout"]?.ToString() == generated["memoryLayout"]?.ToString() &&
    golden["compileUnitCount"]?.ToString() == generated["compileUnitCount"]?.ToString() &&
    PlcFcBlockXmlComposer.NormalizeMembers(golden)
      .SequenceEqual(PlcFcBlockXmlComposer.NormalizeMembers(generated), StringComparer.Ordinal) &&
    PlcFcBlockXmlComposer.CompareStructuredText(golden, generated);

  private static bool CompareStructuredText(JsonObject golden, JsonObject generated)
  {
    var a = golden["structuredText"] as JsonObject ?? new JsonObject();
    var b = generated["structuredText"] as JsonObject ?? new JsonObject();
    return PlcFcBlockXmlComposer.NormalizeArray(a, "tokens")
        .SequenceEqual(PlcFcBlockXmlComposer.NormalizeArray(b, "tokens"), StringComparer.Ordinal) &&
      PlcFcBlockXmlComposer.NormalizeArray(a, "variables")
        .SequenceEqual(PlcFcBlockXmlComposer.NormalizeArray(b, "variables"), StringComparer.Ordinal) &&
      PlcFcBlockXmlComposer.NormalizeArray(a, "constants")
        .SequenceEqual(PlcFcBlockXmlComposer.NormalizeArray(b, "constants"), StringComparer.Ordinal);
  }

  private static string[] NormalizeMembers(JsonObject root)
  {
    return
    [
      .. (root["members"] as JsonArray ?? []).OfType<JsonObject>()
      .Select(x => $"{x["section"]}|{x["name"]}|{x["datatype"]}"),
    ];
  }

  private static string[] NormalizeArray(JsonObject root, string name)
  {
    return
    [
      .. (root[name] as JsonArray ?? []).Select(x => x?.ToString() ?? "")
      .Where(x => !string.IsNullOrWhiteSpace(x)),
    ];
  }

  private static string BuildProbeMarkdown(JsonObject root, string jsonPath)
  {
    var md = new StringBuilder();
    md.AppendLine("# PLC FC Block Composer Probe");
    md.AppendLine();
    md.AppendLine($"Generated: {root["timestamp"]}");
    md.AppendLine($"JSON: {jsonPath}");
    md.AppendLine();
    md.AppendLine("## Safety");
    md.AppendLine("- 离线生成和解析 FC XML，不连接 TIA Portal，不导入 PLC 块。");
    md.AppendLine("- 只写 reports 目录下的生成样本和探针报告，不修改 TMP_EXPORT 或交付包。");
    md.AppendLine();
    md.AppendLine("## Summary");
    md.AppendLine($"- OK: {root["ok"]}");
    md.AppendLine($"- Semantic equal to golden: {root["semanticEqual"]}");
    md.AppendLine($"- Golden: {root["goldenPath"]}");
    md.AppendLine($"- Generated: {root["generatedPath"]}");
    md.AppendLine();
    if (root["generated"] is JsonObject generated)
    {
      md.AppendLine("## Generated Block");
      md.AppendLine($"- Name: {generated["blockName"]}");
      md.AppendLine($"- Number: {generated["number"]}");
      md.AppendLine($"- Language: {generated["programmingLanguage"]}");
      md.AppendLine($"- Compile units: {generated["compileUnitCount"]}");
    }

    return md.ToString();
  }

  private static void ValidateMembers(IReadOnlyCollection<PlcBlockMemberDefinition> members)
  {
    var duplicates = members.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1)
      .Select(x => x.Key).ToArray();
    if (duplicates.Length > 0)
    {
      throw new ArgumentException($"FC 接口成员名重复: {string.Join(", ", duplicates)}");
    }

    foreach (var member in members)
    {
      if (string.IsNullOrWhiteSpace(member.Name))
      {
        throw new ArgumentException("FC 接口成员名不能为空。");
      }

      if (string.IsNullOrWhiteSpace(member.Datatype))
      {
        throw new ArgumentException($"FC 接口成员数据类型不能为空: {member.Name}");
      }
    }
  }

  private sealed class Utf8StringWriter : StringWriter
  {
    public override Encoding Encoding => Encoding.UTF8;
  }
}

public sealed class PlcBlockMemberDefinition
{
  public PlcBlockMemberDefinition(string name, string datatype, string? commentZhCn = "")
  {
    this.Name = name;
    this.Datatype = datatype;
    this.CommentZhCn = commentZhCn ?? "";
  }

  public string Name { get; }
  public string Datatype { get; }
  public string CommentZhCn { get; }
}

/// <summary>
///   共享的 PLC XML 帮助：成员注释、块级 MultilingualText（块/网络级中文标题与注释）。
/// </summary>
internal static class PlcBlockXmlHelpers
{
  public static readonly XNamespace InterfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

  // 给 Member 元素追加 zh-CN 注释（如果非空）
  public static void AppendMemberCommentIfAny(XElement member, string commentZhCn)
  {
    if (string.IsNullOrWhiteSpace(commentZhCn))
    {
      return;
    }

    member.Add(new XElement(PlcBlockXmlHelpers.InterfaceNs + "Comment",
      new XElement(PlcBlockXmlHelpers.InterfaceNs + "MultiLanguageText",
        new XAttribute("Lang", "zh-CN"),
        commentZhCn)));
  }

  // 构造 ObjectList 里的块/网络级 MultilingualText 节点（CompositionName=Comment / Title）
  public static XElement BuildMultilingualText(string id, string itemId, string compositionName, string? textZhCn) =>
    new("MultilingualText",
      new XAttribute("ID", id),
      new XAttribute("CompositionName", compositionName),
      new XElement("ObjectList",
        new XElement("MultilingualTextItem",
          new XAttribute("ID", itemId),
          new XAttribute("CompositionName", "Items"),
          new XElement("AttributeList", new XElement("Culture", "zh-CN"), new XElement("Text", textZhCn ?? "")))));
}
