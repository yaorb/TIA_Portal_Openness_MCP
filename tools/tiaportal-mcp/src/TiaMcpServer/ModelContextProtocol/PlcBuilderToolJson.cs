#region

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;

#endregion

namespace TiaMcpServer.ModelContextProtocol;

/// <summary>
///   PLC Builder MCP 工具的 JSON 适配层。
///   这里集中做字段校验和错误定位，避免工具层继续堆散乱解析逻辑。
/// </summary>
internal static class PlcBuilderToolJson
{
  public static JsonObject BuildUdt(string json)
  {
    var root = PlcBuilderToolJson.ParseObject(json, "$");
    var udtName = PlcBuilderToolJson.ReadString(root, "$.name", "name", "udtName");
    var members = PlcBuilderToolJson.ReadArray(root, "members", "$.members").Select((x, i) =>
      new PlcUdtMemberDefinition(
        PlcBuilderToolJson.ReadString(PlcBuilderToolJson.AsObject(x, "$.members[" + i + "]"),
          "$.members[" + i + "].name",
          "name"),
        PlcBuilderToolJson.ReadString(PlcBuilderToolJson.AsObject(x, "$.members[" + i + "]"),
          "$.members[" + i + "].datatype",
          "datatype",
          "dataType"),
        PlcBuilderToolJson.ReadBool(PlcBuilderToolJson.AsObject(x, "$.members[" + i + "]"), "externalWritable", false),
        PlcBuilderToolJson.ReadOptionalString(PlcBuilderToolJson.AsObject(x, "$.members[" + i + "]"),
          "commentZhCn",
          "comment",
          "commentZh"))).ToArray();

    var xml = PlcUdtXmlBuilder.BuildXml(udtName, members);
    return PlcBuilderToolJson.BuildResult("plc-build-udt-xml",
      xml,
      new JsonObject { ["udtName"] = udtName, ["memberCount"] = members.Length, });
  }

  public static JsonObject BuildTagTable(string json)
  {
    var root = PlcBuilderToolJson.ParseObject(json, "$");
    var tableName = PlcBuilderToolJson.ReadString(root, "$.tableName", "tableName", "name");
    var tags = PlcBuilderToolJson.ReadArray(root, "tags", "$.tags").Select((x, i) =>
    {
      var tag = PlcBuilderToolJson.AsObject(x, "$.tags[" + i + "]");
      return new PlcTagDefinition(PlcBuilderToolJson.ReadString(tag, "$.tags[" + i + "].name", "name"),
        PlcBuilderToolJson.ReadString(tag, "$.tags[" + i + "].dataTypeName", "dataTypeName", "datatype", "dataType"),
        PlcBuilderToolJson.ReadString(tag, "$.tags[" + i + "].logicalAddress", "logicalAddress", "address"));
    }).ToArray();

    var xml = PlcTagTableXmlBuilder.BuildXml(tableName, tags);
    return PlcBuilderToolJson.BuildResult("plc-build-tag-table-xml",
      xml,
      new JsonObject { ["tableName"] = tableName, ["tagCount"] = tags.Length, });
  }

  public static JsonObject BuildGlobalDb(string json)
  {
    var root = PlcBuilderToolJson.ParseObject(json, "$");
    var dbName = PlcBuilderToolJson.ReadString(root, "$.dbName", "dbName", "name");
    var dbNumber = PlcBuilderToolJson.ReadInt(root, "$.dbNumber", "dbNumber", "number");
    var membersNode = root["staticMembers"] as JsonArray ?? root["members"] as JsonArray;
    if (membersNode == null)
    {
      throw new ArgumentException("Missing required JSON array: $.staticMembers (or $.members)");
    }

    var members = membersNode.Select((x, i) =>
    {
      var member = PlcBuilderToolJson.AsObject(x, "$.staticMembers[" + i + "]");
      return new PlcDbMemberDefinition(PlcBuilderToolJson.ReadString(member, "$.staticMembers[" + i + "].name", "name"),
        PlcBuilderToolJson.ReadString(member, "$.staticMembers[" + i + "].datatype", "datatype", "dataType"),
        PlcBuilderToolJson.ReadNullableBool(member, "externalWritable"),
        PlcBuilderToolJson.ReadOptionalString(member, "commentZhCn", "comment", "commentZh"),
        PlcBuilderToolJson.ReadOptionalString(member, "startValue"));
    }).ToArray();

    var xml = PlcGlobalDbXmlBuilder.BuildXml(dbName, dbNumber, members);
    return PlcBuilderToolJson.BuildResult("plc-build-global-db-xml",
      xml,
      new JsonObject { ["dbName"] = dbName, ["dbNumber"] = dbNumber, ["memberCount"] = members.Length, });
  }

  public static JsonObject BuildStructuredText(string json, bool innerOnly = false)
  {
    var root = PlcBuilderToolJson.ParseObject(json, "$");
    var builder = PlcBuilderToolJson.BuildStructuredTextBuilder(root);
    var xml = innerOnly
      ? builder.BuildInnerXml()
      : builder.BuildStructuredTextXml();
    return PlcBuilderToolJson.BuildResult("plc-build-structured-text-xml",
      xml,
      new JsonObject
      {
        ["innerOnly"] = innerOnly, ["operationCount"] = (root["operations"] as JsonArray)?.Count ?? 0,
      });
  }

  public static JsonObject BuildFlgNetCall(string json)
  {
    var root = PlcBuilderToolJson.ParseObject(json, "$");
    var callName = PlcBuilderToolJson.ReadString(root, "$.callName", "callName", "name");
    var parameters = PlcBuilderToolJson.ReadArray(root, "parameters", "$.parameters").Select((x, i) =>
      PlcBuilderToolJson.ReadFlgNetParameter(PlcBuilderToolJson.AsObject(x, "$.parameters[" + i + "]"), i)).ToArray();

    var xml = FlgNetCallXmlBuilder.BuildXml(callName, parameters);
    return PlcBuilderToolJson.BuildResult("plc-build-flgnet-call-xml",
      xml,
      new JsonObject { ["callName"] = callName, ["parameterCount"] = parameters.Length, });
  }

  public static JsonObject ComposeFcBlock(string json)
  {
    var root = PlcBuilderToolJson.ParseObject(json, "$");
    var blockName = PlcBuilderToolJson.ReadString(root, "$.blockName", "blockName", "name");
    var blockNumber = PlcBuilderToolJson.ReadInt(root, "$.blockNumber", "blockNumber", "number");
    var inputs = PlcBuilderToolJson.ReadMembers(root, "inputs", "$.inputs");
    var outputs = PlcBuilderToolJson.ReadMembers(root, "outputs", "$.outputs");
    var structuredTextInnerXml = PlcBuilderToolJson.ReadStructuredTextInnerXml(root);
    var blockComment = PlcBuilderToolJson.ReadOptionalString(root, "commentZhCn", "blockCommentZhCn", "comment");
    var blockTitle = PlcBuilderToolJson.ReadOptionalString(root, "titleZhCn", "blockTitleZhCn", "title");
    var netComment = PlcBuilderToolJson.ReadOptionalString(root, "networkCommentZhCn", "networkComment");
    var netTitle = PlcBuilderToolJson.ReadOptionalString(root, "networkTitleZhCn", "networkTitle");

    var xml = PlcFcBlockXmlComposer.ComposeXml(blockName,
      blockNumber,
      inputs,
      outputs,
      structuredTextInnerXml,
      blockComment,
      blockTitle,
      netComment,
      netTitle);
    return PlcBuilderToolJson.BuildResult("plc-compose-fc-block-xml",
      xml,
      new JsonObject
      {
        ["blockName"] = blockName,
        ["blockNumber"] = blockNumber,
        ["inputCount"] = inputs.Length,
        ["outputCount"] = outputs.Length,
      });
  }

  public static JsonObject ComposeLadFcBlock(string json)
  {
    var root = PlcBuilderToolJson.ParseObject(json, "$");
    var blockName = PlcBuilderToolJson.ReadString(root, "$.blockName", "blockName", "name");
    var blockNumber = PlcBuilderToolJson.ReadInt(root, "$.blockNumber", "blockNumber", "number");
    var inputs = PlcBuilderToolJson.ReadOptionalMembers(root, "inputs", "$.inputs");
    var outputs = PlcBuilderToolJson.ReadOptionalMembers(root, "outputs", "$.outputs");
    var blockComment = PlcBuilderToolJson.ReadOptionalString(root, "commentZhCn", "blockCommentZhCn", "comment");
    var blockTitle = PlcBuilderToolJson.ReadOptionalString(root, "titleZhCn", "blockTitleZhCn", "title");

    var nets = PlcBuilderToolJson.ReadArray(root, "networks", "$.networks");
    if (nets.Count == 0)
    {
      throw new ArgumentException("LAD FC 至少需要 1 个 networks[] 元素");
    }

    var built = new List<PlcLadFcBlockXmlComposer.LadNetwork>();
    for (var i = 0; i < nets.Count; i++)
    {
      var n = PlcBuilderToolJson.AsObject(nets[i], "$.networks[" + i + "]");
      var netTitle = PlcBuilderToolJson.ReadOptionalString(n, "titleZhCn", "title");
      var netComment = PlcBuilderToolJson.ReadOptionalString(n, "commentZhCn", "comment");
      // 每个 network 是一个 FC 调用：用 FlgNetCallXmlBuilder
      var callJson = n["callJson"] as JsonObject ?? n["call"] as JsonObject;
      if (callJson == null)
      {
        throw new ArgumentException("$.networks[" + i + "] 缺少 callJson 对象");
      }

      var callName =
        PlcBuilderToolJson.ReadString(callJson, "$.networks[" + i + "].callJson.callName", "callName", "name");
      var parametersArray = callJson["parameters"] as JsonArray;
      var parameters = parametersArray == null
        ? Array.Empty<FlgNetCallParameter>()
        : parametersArray.Select((x, j) =>
          PlcBuilderToolJson.ReadFlgNetParameter(
            PlcBuilderToolJson.AsObject(x, "$.networks[" + i + "].callJson.parameters[" + j + "]"),
            j)).ToArray();
      var flgNet = FlgNetCallXmlBuilder.BuildFlgNet(callName, parameters);
      built.Add(new PlcLadFcBlockXmlComposer.LadNetwork(flgNet, netTitle, netComment));
    }

    var xml = PlcLadFcBlockXmlComposer.ComposeXml(blockName,
      blockNumber,
      inputs,
      outputs,
      built,
      blockComment,
      blockTitle);
    return PlcBuilderToolJson.BuildResult("plc-compose-lad-fc-block-xml",
      xml,
      new JsonObject
      {
        ["blockName"] = blockName,
        ["blockNumber"] = blockNumber,
        ["networkCount"] = built.Count,
        ["inputCount"] = inputs.Length,
        ["outputCount"] = outputs.Length,
      });
  }

  public static JsonObject ComposeFbBlock(string json)
  {
    var root = PlcBuilderToolJson.ParseObject(json, "$");
    var blockName = PlcBuilderToolJson.ReadString(root, "$.blockName", "blockName", "name");
    var blockNumber = PlcBuilderToolJson.ReadInt(root, "$.blockNumber", "blockNumber", "number");
    var inputs = PlcBuilderToolJson.ReadOptionalMembers(root, "inputs", "$.inputs");
    var outputs = PlcBuilderToolJson.ReadOptionalMembers(root, "outputs", "$.outputs");
    var inouts = PlcBuilderToolJson.ReadOptionalMembers(root, "inouts", "$.inouts", "inOuts", "inOut");
    var statics = PlcBuilderToolJson.ReadOptionalMembers(root, "statics", "$.statics", "staticMembers", "static");
    var temps = PlcBuilderToolJson.ReadOptionalMembers(root, "temps", "$.temps", "tempMembers", "temp");
    var structuredTextInnerXml = PlcBuilderToolJson.ReadStructuredTextInnerXml(root);
    var blockComment = PlcBuilderToolJson.ReadOptionalString(root, "commentZhCn", "blockCommentZhCn", "comment");
    var blockTitle = PlcBuilderToolJson.ReadOptionalString(root, "titleZhCn", "blockTitleZhCn", "title");
    var netComment = PlcBuilderToolJson.ReadOptionalString(root, "networkCommentZhCn", "networkComment");
    var netTitle = PlcBuilderToolJson.ReadOptionalString(root, "networkTitleZhCn", "networkTitle");

    var xml = PlcFbBlockXmlComposer.ComposeXml(blockName,
      blockNumber,
      inputs,
      outputs,
      inouts,
      statics,
      temps,
      structuredTextInnerXml,
      blockComment,
      blockTitle,
      netComment,
      netTitle);
    return PlcBuilderToolJson.BuildResult("plc-compose-fb-block-xml",
      xml,
      new JsonObject
      {
        ["blockName"] = blockName,
        ["blockNumber"] = blockNumber,
        ["inputCount"] = inputs.Length,
        ["outputCount"] = outputs.Length,
        ["inOutCount"] = inouts.Length,
        ["staticCount"] = statics.Length,
        ["tempCount"] = temps.Length,
      });
  }

  private static StructuredTextXmlBuilder BuildStructuredTextBuilder(JsonObject root)
  {
    var firstUid = PlcBuilderToolJson.ReadOptionalInt(root, "firstUid") ?? 21;
    var builder = new StructuredTextXmlBuilder(firstUid);
    var operations = PlcBuilderToolJson.ReadArray(root, "operations", "$.operations");
    for (var i = 0; i < operations.Count; i++)
    {
      var op = PlcBuilderToolJson.AsObject(operations[i], "$.operations[" + i + "]");
      var kind = PlcBuilderToolJson.ReadString(op, "$.operations[" + i + "].op", "op", "kind", "type").Trim()
        .ToLowerInvariant();
      var indent = PlcBuilderToolJson.ReadOptionalInt(op, "indent") ?? 0;
      switch (kind)
      {
        case "if":
        case "ifheader":
          builder.IfHeader(PlcBuilderToolJson.ReadString(op,
              "$.operations[" + i + "].condition",
              "condition",
              "conditionVariable",
              "variable"),
            indent);
          break;

        case "elsif":
        case "elseif":
        case "elsifheader":
          builder.ElsIfHeader(PlcBuilderToolJson.ReadString(op,
              "$.operations[" + i + "].condition",
              "condition",
              "conditionVariable",
              "variable"),
            indent);
          break;

        case "else":
          builder.ElseLine(indent);
          break;

        case "endif":
        case "end_if":
          builder.EndIf(indent);
          break;

        case "assign":
        case "assignment":
        {
          var target = PlcBuilderToolJson.ReadString(op, "$.operations[" + i + "].target", "target");
          var src = PlcBuilderToolJson.ReadOptionalString(op, "source", "fromSymbol");
          if (!string.IsNullOrWhiteSpace(src))
          {
            builder.AssignFromSymbol(target, src, indent);
          }
          else
          {
            builder.Assignment(target,
              PlcBuilderToolJson.ReadString(op, "$.operations[" + i + "].literalValue", "literalValue", "value"),
              indent);
          }
        }
          break;

        case "token":
          if (indent > 0)
          {
            builder.Blank(indent);
          }

          builder.Token(PlcBuilderToolJson.ReadString(op, "$.operations[" + i + "].text", "text"));
          break;

        case "blank":
          builder.Blank(PlcBuilderToolJson.ReadOptionalInt(op, "count") ?? 1);
          break;

        case "newline":
        case "new_line":
          builder.NewLine();
          break;

        case "global":
          if (indent > 0)
          {
            builder.Blank(indent);
          }

          builder.GlobalVariable(PlcBuilderToolJson.ReadString(op, "$.operations[" + i + "].name", "name").Split('.'));
          break;

        case "local":
          if (indent > 0)
          {
            builder.Blank(indent);
          }

          builder.LocalVariable(PlcBuilderToolJson.ReadString(op, "$.operations[" + i + "].name", "name"));
          break;

        case "symbol":
          // 智能识别 scope："x" 全局 / x 局部
          if (indent > 0)
          {
            builder.Blank(indent);
          }

          builder.Symbol(PlcBuilderToolJson.ReadString(op, "$.operations[" + i + "].name", "name"));
          break;

        case "literal":
          if (indent > 0)
          {
            builder.Blank(indent);
          }

          builder.LiteralConstant(PlcBuilderToolJson.ReadString(op,
            "$.operations[" + i + "].value",
            "value",
            "literalValue"));
          break;

        case "line":
          // 自由表达式行：items[] 数组，每项可为：
          //   {sym:"name"}     局部 / 引号则全局，由 Symbol() 智能识别
          //   {token:"AND"}    关键字 / 运算符 token（前后自动加 Blank，除 ( ) , ; ）
          //   {lit:"FALSE"}    字面常量
          //   {raw:"("}        裸 token（不自动加 Blank）
          // 末尾自动追加 ; + newline（除非最后一项已是 ; 或 raw=";"）
          if (indent > 0)
          {
            builder.Blank(indent);
          }

          PlcBuilderToolJson.EmitLine(builder, op, i);
          break;

        default:
          throw new ArgumentException("Unsupported StructuredText operation at $.operations[" + i + "].op: " + kind);
      }
    }

    return builder;
  }

  // SCL 自由表达式行：items[] 解析 + 自动 Blank（紧贴标点除外）
  private static void EmitLine(StructuredTextXmlBuilder builder, JsonObject lineOp, int opIndex)
  {
    var items = lineOp["items"] as JsonArray ?? throw new ArgumentException("$.operations[" + opIndex + "].items 缺失");
    string? lastTokenText = null;
    for (var k = 0; k < items.Count; k++)
    {
      var item = items[k] as JsonObject ??
        throw new ArgumentException("$.operations[" + opIndex + "].items[" + k + "] 必须是对象");
      var tightBefore = false; // 当前项前是否需要紧贴（如 ) ; , ）

      if (item["sym"] is JsonNode symN)
      {
        if (k > 0 && !tightBefore)
        {
          builder.Blank();
        }

        builder.Symbol(symN.ToString());
        lastTokenText = null;
      }
      else if (item["token"] is JsonNode tokN)
      {
        var t = tokN.ToString();
        tightBefore = t == ")" || t == "," || t == ";";
        if (k > 0 && !tightBefore)
        {
          builder.Blank();
        }

        builder.Token(t);
        lastTokenText = t;
      }
      else if (item["lit"] is JsonNode litN)
      {
        if (k > 0)
        {
          builder.Blank();
        }

        builder.LiteralConstant(litN.ToString());
        lastTokenText = null;
      }
      else if (item["raw"] is JsonNode rawN)
      {
        builder.Token(rawN.ToString()); // 不自动加 Blank
        lastTokenText = rawN.ToString();
      }
      else
      {
        throw new ArgumentException("$.operations[" + opIndex + "].items[" + k + "] 需要 sym / token / lit / raw 之一");
      }
    }

    // 末尾若不是 ; 则补 ;
    if (lastTokenText != ";")
    {
      builder.Token(";");
    }

    builder.NewLine();
  }

  private static FlgNetCallParameter ReadFlgNetParameter(JsonObject parameter, int index)
  {
    var name = PlcBuilderToolJson.ReadString(parameter, "$.parameters[" + index + "].name", "parameterName", "name");
    var section = PlcBuilderToolJson.ReadString(parameter, "$.parameters[" + index + "].section", "section");
    var dataType = PlcBuilderToolJson.ReadString(parameter,
      "$.parameters[" + index + "].dataType",
      "dataType",
      "datatype",
      "type");
    var sourceKind = PlcBuilderToolJson.ReadOptionalString(parameter, "sourceKind", "source", "kind").Trim()
      .ToLowerInvariant();
    if (sourceKind == "constant" || sourceKind == "literal" || sourceKind == "literalconstant")
    {
      return FlgNetCallParameter.Constant(name,
        section,
        dataType,
        PlcBuilderToolJson.ReadString(parameter, "$.parameters[" + index + "].value", "value", "constantValue"));
    }

    var path = parameter["symbolPath"] as JsonArray;
    if (path != null)
    {
      return FlgNetCallParameter.Global(name,
        section,
        dataType,
        path.Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToArray());
    }

    var symbol =
      PlcBuilderToolJson.ReadString(parameter, "$.parameters[" + index + "].symbol", "symbol", "path", "plcTag");
    return FlgNetCallParameter.Global(name,
      section,
      dataType,
      symbol.Split(new[] { '.', }, StringSplitOptions.RemoveEmptyEntries));
  }

  private static PlcBlockMemberDefinition[] ReadMembers(JsonObject root, string arrayName, string path)
  {
    return PlcBuilderToolJson.ReadArray(root, arrayName, path).Select((x, i) =>
    {
      var member = PlcBuilderToolJson.AsObject(x, path + "[" + i + "]");
      return new PlcBlockMemberDefinition(PlcBuilderToolJson.ReadString(member, path + "[" + i + "].name", "name"),
        PlcBuilderToolJson.ReadString(member, path + "[" + i + "].datatype", "datatype", "dataType"),
        PlcBuilderToolJson.ReadOptionalString(member, "commentZhCn", "comment", "commentZh"));
    }).ToArray();
  }

  private static PlcBlockMemberDefinition[] ReadOptionalMembers(JsonObject root, string defaultArrayName,
    string defaultPath, params string[] aliases)
  {
    var names = new[] { defaultArrayName, }.Concat(aliases ?? Array.Empty<string>()).ToArray();
    var foundName = names.FirstOrDefault(name => root[name] is JsonArray);
    if (foundName == null)
    {
      return Array.Empty<PlcBlockMemberDefinition>();
    }

    return PlcBuilderToolJson.ReadMembers(root, foundName, "$." + foundName);
  }

  private static string ReadStructuredTextInnerXml(JsonObject root)
  {
    var direct =
      PlcBuilderToolJson.ReadOptionalString(root, "structuredTextInnerXml", "structuredTextXml", "sclInnerXml");
    if (!string.IsNullOrWhiteSpace(direct))
    {
      return direct;
    }

    if (root["structuredText"] is JsonObject structuredText)
    {
      return PlcBuilderToolJson.BuildStructuredTextBuilder(structuredText).BuildInnerXml();
    }

    throw new ArgumentException(
      "Missing required StructuredText content: $.structuredTextInnerXml or $.structuredText.operations");
  }

  private static JsonObject BuildResult(string mode, string xml, JsonObject summary)
  {
    var parseOk = true;
    var error = "";
    try
    {
      XDocument.Parse(xml);
    }
    catch (Exception ex)
    {
      try
      {
        // StructuredText inner XML 是供 Block Composer 嵌入的片段，允许多个同级节点。
        XDocument.Parse("<Fragment>" + xml + "</Fragment>");
      }
      catch
      {
        parseOk = false;
        error = ex.Message;
      }
    }

    return new JsonObject
    {
      ["ok"] = parseOk,
      ["mode"] = mode,
      ["offlineOnly"] = true,
      ["xmlParseOk"] = parseOk,
      ["error"] = string.IsNullOrWhiteSpace(error)
        ? null
        : JsonValue.Create(error),
      ["xml"] = xml,
      ["summary"] = summary,
      ["safetyPolicy"] = new JsonObject
      {
        ["tia"] = "离线 XML 生成：不连接 TIA Portal，不打开项目，不导入 PLC 对象。", ["write"] = "本工具只返回 XML 字符串，不写工程、不写交付包。",
      },
    };
  }

  private static JsonObject ParseObject(string json, string path)
  {
    if (string.IsNullOrWhiteSpace(json))
    {
      throw new ArgumentException("Missing required JSON object: " + path);
    }

    var node = JsonNode.Parse(json);
    return PlcBuilderToolJson.AsObject(node, path);
  }

  private static JsonObject AsObject(JsonNode? node, string path) =>
    node as JsonObject ?? throw new ArgumentException("Expected JSON object at " + path);

  private static JsonArray ReadArray(JsonObject root, string name, string path) =>
    root[name] as JsonArray ?? throw new ArgumentException("Missing required JSON array: " + path);

  private static string ReadString(JsonObject root, string path, params string[] names)
  {
    var value = PlcBuilderToolJson.ReadOptionalString(root, names);
    if (string.IsNullOrWhiteSpace(value))
    {
      throw new ArgumentException("Missing required JSON string: " + path);
    }

    return value;
  }

  private static string ReadOptionalString(JsonObject root, params string[] names)
  {
    foreach (var name in names)
    {
      if (root.TryGetPropertyValue(name, out var node) && node != null)
      {
        return node.ToString();
      }
    }

    return "";
  }

  private static int ReadInt(JsonObject root, string path, params string[] names)
  {
    var value = PlcBuilderToolJson.ReadOptionalInt(root, names);
    if (!value.HasValue)
    {
      throw new ArgumentException("Missing required JSON integer: " + path);
    }

    return value.Value;
  }

  private static int? ReadOptionalInt(JsonObject root, params string[] names)
  {
    foreach (var name in names)
    {
      if (!root.TryGetPropertyValue(name, out var node) || node == null)
      {
        continue;
      }

      if (node is JsonValue value && value.TryGetValue<int>(out var intValue))
      {
        return intValue;
      }

      if (int.TryParse(node.ToString(), out var parsed))
      {
        return parsed;
      }
    }

    return null;
  }

  private static bool ReadBool(JsonObject root, string name, bool defaultValue) =>
    PlcBuilderToolJson.ReadNullableBool(root, name) ?? defaultValue;

  private static bool? ReadNullableBool(JsonObject root, string name)
  {
    if (!root.TryGetPropertyValue(name, out var node) || node == null)
    {
      return null;
    }

    if (node is JsonValue value && value.TryGetValue<bool>(out var boolValue))
    {
      return boolValue;
    }

    if (bool.TryParse(node.ToString(), out var parsed))
    {
      return parsed;
    }

    throw new ArgumentException("Expected JSON boolean at property '" + name + "'.");
  }
}
