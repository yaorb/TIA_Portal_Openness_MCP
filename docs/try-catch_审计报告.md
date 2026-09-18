# try/catch 审计报告

> 审计对象：`tools/tiaportal-mcp/src/TiaMcpServer`（重点：`Siemens/` 层）
> 审计时间：2026-09-18 · 基线提交：`9da0f44`
> 数据：全仓 **904** 个 catch 块，逐块抽取函数体后分类；统计脚本见文末「复现方法」

---

## 0. 结论摘要

**问题是真的，但性质比"乱用"更具体：不是 catch 写得多，而是大量 catch 不留任何诊断痕迹，且没有任何机制阻止它继续增长。**

| 结论 | 数据 |
|---|---|
| Siemens 层 45% 的 catch 不留诊断痕迹 | 362 个 catch 中：**109 个空体** + **55 个有体但零诊断** |
| 其中"有正当理由"的极少 | 109 个空 catch 里：**54 个**是 IDE 机械补的 `// ignored`、**46 个**连注释都没有、**只有 9 个**写了真正的人类理由 |
| 少量但真实的"沉默改变了行为" | 我逐处读过代码，确认 **6 处**（见 §4）会造成调用方误判，而非仅仅少一句日志 |
| 既有规范形同虚设 | 专为消除 try/catch 样板而写的 `Operation.Run`，全仓**只有 1 个调用点**（`Portal.cs:495`） |
| 也没有机械守卫 | 无 lint 规则 / CI 检查；最近一轮 IDE 清理一次性**新增了 82 条 `// ignored`**，并两次把 `Concat` 的修复改回去 |

**但也要说句公道话**（避免一刀切误伤）：

- `Siemens` 层有 44 处包装后重抛、64 处记日志，质量不差；
- 宽 catch 有一部分是**硬约束下的合理选择**——Openness 的异常类型（`EngineeringSecurityException` 等）来自运行时解析的程序集（V20/V21 两套 csproj），不引入编译期依赖更稳，代码里有明确注释；
- 我复核过的 `Blocks.cs:617`、`Deletion.cs:92` 等"高危候选"，读代码后判定**是合理的最佳努力降级**，不该动。

所以本报告的建议是**分优先级精准修**，不是全仓重写。

---

## 1. 统计方法（含三次口径修正）

审计过程中我自己的判据出过三次误判，记录下来，因为结论的可信度取决于口径：

| 迭代 | 错在哪 | 修正 |
|---|---|---|
| v1 | 直接对源码文本正则找 `catch` | 注释里的中文 "catch"（如 `// 别 catch 吞成 null`）被当成代码，多算 16 个 → 改为**先把注释与字符串字面量掩码成空格**再做括号匹配 |
| v2 | 用 `.Append(` / `.Add(` / `errors` 判断"错误有没有交给调用方" | `return new JsonObject { ["ok"]=false, ["message"]="... failed: {ex.Message}" }` 这类**结构化的错误返回被漏判成静默** → 改为看函数体里有没有用到异常信息（`.Message` / `.ToString` / `FormatExceptionDetail` / `Data[` / 存入 out 参数） |
| v3 | 用**方法名**推断严重性 | `ImportBlocksFromDirectory` 里那个空 catch 守的其实是**导入前的存在性查询**，真导入在 try 之外 → 严重性必须看 **try 体内**发生了什么，不能看方法名 |

**最终分类轴**（互不重叠的口径）：

- **① 空体**：catch 体内除注释外没有任何代码 —— 异常彻底消失。
- **② 有体零诊断**：有代码，但既没用到异常信息、也没记日志、也没重抛。
- **③ 有诊断**：记了日志 / 包装重抛 / 把 `ex.Message` 或 `Data` 或 out 参数交给调用方。

---

## 2. 数据总览

### 2.1 按目录

| 目录 | catch 总数 | ① 空体 | ② 有体零诊断 | ③ 有诊断 | **无诊断占比** |
|---|---:|---:|---:|---:|---:|
| **Siemens** | **362** | **109** | **55** | 198 | **45%** |
| ModelContextProtocol | 424 | 22 | 49 | 353 | 17% |
| (root: Program*.cs) | 89 | 6 | 14 | 69 | 22% |
| Runtime | 20 | 9 | 3 | 8 | 60% |
| Cli | 9 | 3 | 2 | 4 | 56% |
| **合计** | **904** | **149** | **123** | **632** | 30% |

> `Runtime/` 60% 看似最差，但它是 S7 / OPC UA 实时读值通道，本身就是"读不到就报读不到"的最佳努力语义，且总量只有 20 个。**规模 × 占比**看，`Siemens/` 是唯一值得系统性治理的层。

### 2.2 Siemens 层的 catch 子句类型

| 子句 | 数量 | 说明 |
|---|---:|---|
| `catch (Exception)` | 163 | catch-all |
| `catch`（无类型） | 149 | catch-all，连异常对象都拿不到 |
| `catch (PortalException)` | 21 | 项目自有异常 ✓ |
| `catch (TargetInvocationException) when (...)` | 15 | 带过滤条件 ✓ |
| `EngineeringNotSupportedException` | 5 | 具体类型 ✓ |
| `ReflectionTypeLoadException` | 3 | 具体类型 ✓ |
| `EngineeringTargetInvocationException` / `LicenseNotFoundException` | 2 / 2 | 具体类型 ✓ |
| `ArgumentException` / `TargetInvocationException` | 1 / 1 | 具体类型 ✓ |

**catch-all 占 312/362 = 86%**。其中大部分有正当理由（反射调用、运行时解析的 Openness 类型），但代价是**类型信息丢失**：调用方无法区分"参数写错了"还是"TIA 挂了"。

### 2.3 Siemens 层的处理动作

| 动作 | 数量 | 占比 |
|---|---:|---:|
| 记日志 | 64 | 18% |
| 包装后重抛 | 44 | 12% |
| （两者都做） | 10 | 3% |
| **两者都没有** | **264** | **73%** |

> 362 个 catch 里，**264 个既没记日志、也没重抛** —— 失败的唯一去向是返回值或虚空。

### 2.4 空 catch 的注释质量（关键发现）

| | 全仓 149 | Siemens 109 |
|---|---:|---:|
| IDE 机械补的 `// ignored` | 82 | **54** |
| 完全无注释 | 46 | **46** |
| **人类写了真正理由** | 21 | **9** |

Siemens 里那 9 条人类理由（全部列出，其中 3 条只有 "best-effort" 三个字）：

```
EngineRouter.cs:72          // best-effort only; caller falls back to a warning
Portal.Blocks.cs:617        // best effort only; if Find fails, we still import with Override semantics below
Portal.cs:542               // skip disposed/inaccessible session projects
Portal.cs:560               // skip disposed
Portal.Deletion.cs:92       // 某些块类型不给 Number/AutoNumber。读不到就不警告，但绝不因此让删除失败。
Portal.Devices.cs:1104      // best-effort
Portal.Modules.cs:538       // 读不到兄弟就不做去重，交给 TIA 自己报重名。
Portal.Software.cs:7675     // best-effort only
Portal.Software.cs:8498     // best-effort
```

**这意味着：`// ignored` 这种注释提供了"已审慎处理"的假象，实际上 100 处里有 100 处没有记录理由。**

### 2.5 其它形态

- **try 范围过宽**：Siemens 有 **12 处** try 体超过 60 行，最大 **134 行**（`Portal.Software.cs:6752 SeedProjectFromReference`）。
  ```
  Portal.Software.cs:6752  134 行   SeedProjectFromReference
  Portal.Software.cs:1313  111 行   ProbePlcMonitorOnlineCapabilities
  Portal.Helpers.cs:2485   110 行   InvokeOnInstance
  Portal.Software.cs:3514  106 行   ApplyUnifiedHmiScreenDesignJson
  Portal.Helpers.cs:2478   103 行   InvokeOnInstance
  Portal.Download.cs:144    91 行   （下载主流程）
  ```
  一个 catch 兜住 130 行时，它到底在兜哪一步的失败已经无法回答。

- **catch 密集方法**：`McpServer.Patch.cs` 单文件 19 个 catch；`ScaffoldProject` 12 个；`Portal.Blocks.cs::ImportType` 11 个；`ExportTypeToTemp` 10 个；`ConnectPortal` 8 个。

- **既有规范使用率**：
  | 设施 | 设计意图 | 调用点 |
  |---|---|---:|
  | `Operation.Run` | "One call replaces ~8 lines of identical exception-handling code"，按 `PortalException`/其它分级记日志 | **1** |
  | `Guard.Require*` | 用断言替代散落的 null 检查 | 5 |
  | `PortalFailureClassifier` | 区分"这次调用失败"和"TIA 进程没了" | 5 |

---

## 3. 反模式清单（按危害排序）

| # | 反模式 | 规模 | 危害 |
|---|---|---:|---|
| A | **空 catch 改变了行为**（写操作/选实例路径上的静默失败） | 6 处已确认 | ★★★★★ 调用方误判，可能按错误配置继续执行 |
| B | **有体零诊断**：捕获后只 `return null` / `continue` / 设默认值 | 55 | ★★★★ 无法区分"确实没有"和"炸了" |
| C | **无注释空 catch** | 46 | ★★★ 无人知道是有意还是漏写 |
| D | **机械 `// ignored` 冒充理由** | 54 | ★★★ 制造"已审慎处理"的假象，还挡住了后续审计 |
| E | **catch-all 泛化** | 312 (86%) | ★★ 类型信息丢失，降级路径无法分级处理 |
| F | **try 范围过宽** | 12 处 >60 行 | ★★ 报错无法定位到具体步骤 |
| G | **循环内 `continue` 静默跳过** | 见 B | ★★★ 返回不完整结果却看起来完整（本仓自己在 `Portal.Blocks.cs:202` 明确反对这种） |

---

## 4. 重点复核清单（逐处读过代码）

### 4.1 确认该修（6 处）

| 位置 | 代码 | 为什么是问题 |
|---|---|---|
| **`Portal.Download.cs:656`** `DownloadConfigSetSelection`<br>**`Portal.Download.cs:668`** `DownloadConfigSetChecked` | `prop.SetValue(config, value); catch { }` | 反射设置下载配置项，**失败即静默**，函数返回 void、无注释、调用方无法察觉。后果是**下载可能按默认选项执行**（比如某个步骤该勾没勾）。这是本次审计里最该先修的一处：它是写操作，且失败完全没有出口。 |
| **`Portal.Software.cs:8650`** `ImportPlcExternalSource` | 反射收集导入目标 `ExternalSources` 失败 → `catch { }` | 目标容器少了，导入会落到非预期位置。无注释、无回读校验。 |
| **`Portal.Software.cs:112`** `GetAllPlcSoftware` | 逐设备项 try/catch → `catch { }` | 静默返回**不完整的 PLC 列表**。本仓在 `Portal.Blocks.cs:202` 的注释里自己写过：<br>*"遍历炸在半路时原来只记日志、把残缺的列表当完整结果返回。「少了几个块」比「一个都没有」更难发现，因为它看起来完全正常。"* —— 这里是同一类问题。 |
| **`Portal.cs:644` / `Portal.cs:650`** `AttachToOpenProject` | 逐候选 attach 失败被吞 | 最终只返回 `false`，**失败原因全部丢失**。该类已有 `LastConnectError` 机制（`Bootstrap` 会读它）却没在这里使用，属实现不一致。 |
| **`Portal.cs:235` / `Portal.cs:244`** `ConnectPortal` | `LocalSessions.Any()` / `Projects.Any()` 探测失败被吞 | 探测失败会让 `hasSession`/`hasProject` 变成 `false`，而这两个值**决定选哪个 TIA 实例去 attach**。第 249 行只记录了结果（`hasSession=False`），没记录原因，排障时无法分辨"确实没有工程"还是"探测炸了"。 |
| **`Portal.Software.cs:1004`** `SetEnumPropertyByName` | 反射设枚举，`catch { // ignored }` | 写操作 + 机械注释，与上一条同类。 |

**修法（统一建议）**：这些位置不需要改成抛异常（会打断既有的降级链路），只需要**给失败一个出口**：

```csharp
catch (Exception ex)
{
  // 失败要留痕：调用方/日志至少能知道"这步没成"
  logger?.LogWarning(ex, "DownloadConfigSetSelection('{Name}') failed; the step keeps its default", selectionName);
  lastFailure = ex;   // 或写进返回对象的 Meta["warnings"]
}
```

### 4.2 复核后判定**合理，不要动**（4 处）

这些原本被自动化判据标成"高危"，读代码后推翻：

| 位置 | 实际守的是什么 |
|---|---|
| `Portal.Blocks.cs:617` | 导入**前**的"块是否已存在"查询；真正的 `group.Blocks.Import(...)` 在 try 之外（第 623 行）。注释明确：查不到就按 Override 语义继续导入。 |
| `Portal.Deletion.cs:92` | 读 `block.Number`/`AutoNumber`（部分块类型不暴露）。注释：*"读不到就不警告，但绝不因此让删除失败"*。 |
| `Portal.Blocks.cs:459` | `return path; // best effort; on any failure import the original file` —— 有明确兜底路径。 |
| `Portal.cs:334/620/639` | `Dispose()` 清理路径。清理失败无处可报，吞掉是标准做法。 |

> 这 4 处说明一件事：**"空 catch" 不等于"错误处理写得差"**，判据必须看它守的是什么。本报告 §4.1 的 6 处是逐处读代码确认的，不是启发式筛出来的。

---

## 5. 值得保留的正面做法

审计里同样找到了应当作为范本的写法，建议在评审中明确为"本仓推荐姿势"：

1. **参数类异常原样上抛，不许被包成"遍历失败"**
   `Portal.Blocks.cs:194`、`:271`
   ```csharp
   catch (PortalException) { throw; }   // 包装会给出不准确的原因，调用方照着查就跑偏了
   ```
2. **宁可整体失败，也不返回残缺结果** —— `Portal.Blocks.cs:275`
   ```csharp
   throw new PortalException(PortalErrorCode.OpennessError,
     $"GetTypes: type enumeration failed after {list.Count} type(s) in '{softwarePath}'; " +
     "the returned list would have been INCOMPLETE, so it is not returned at all. " +
     $"Root cause: {ex.Message}", null, ex);
   ```
3. **包装时补上下文再记日志** —— `Portal.Blocks.cs:332`、`:395`（`pex.Data["softwarePath"] = ...` + `logger?.LogError`）
4. **错误进返回值而不是进虚空** —— `Portal.cs:383/418/437/442` 用 `lines.Add("... error: " + Portal.FormatExceptionDetail(ex))`；`Portal.cs:137` 用 `error = ex` out 参数
5. **`Operation.Run` 的设计本身是对的**（`PortalException` → Warning，其它 → Error，永不重抛），只是没人用

---

## 6. 建议（分优先级）

### P0 —— 修 6 处"沉默改变行为"（§4.1）
预计改动量：每处 1–3 行。**这是唯一会真实影响用户结果的类别。**
验收：这 6 条路径失败时，`Meta["warnings"]` 或日志里能看到原因。

### P1 —— 46 处无注释空 catch 做一次三角分诊
逐处判定为「①写操作 → 按 P0 处理」「②只读探测 → 补一句理由注释」「③清理路径 → 标 `// teardown: 失败无处可报`」。
建议以 `Siemens/` 为范围，一次提交改完，便于评审。

### P2 —— 把 54 条机械 `// ignored` 替换成真实理由或日志
这一步的价值不在代码本身，而在于**恢复注释的可信度**：现在看到 `// ignored` 无法判断作者是否想过。

### P3 —— 结构治理
- 12 处 >60 行的 try：至少把"能独立失败的步骤"拆成独立小 try，让 catch 的语义与步骤对应；
- `ConnectPortal`（8 个 catch）、`ImportType`（11 个）这类密集方法，改用 `Operation.Run` 收敛样板（它本来就是为此写的）；
- 对 `GetAllPlcSoftware` 这类"遍历 + 收集"的方法，采纳 `Portal.Blocks.cs:275` 的做法：**要么完整，要么明确告知不完整**。

### P4 —— 加机械守卫（防止本轮成果被下一轮清理抹掉）
本仓已经亲历两次：IDE 清理把 `Concat` 修复改回去、并批量新增 82 条 `// ignored`。建议加一条可执行规则，放进 `scripts/` 并挂到 `.github/workflows/validate.yml`：

1. **空 catch 必须带非 `ignored` 的注释**；
2. **无类型 `catch {` 与 `catch (Exception)` 必须满足其一**：记日志 / `throw` / 把 `ex.Message`（或 `Data`、out 参数）交给调用方；
3. 例外可显式豁免（`// teardown:` 前缀），让"我知道我在吞"变成一个需要写下来的动作。

规则 2 能挡住 86% 的 catch-all 里那些真正无声的那些，且实现成本很低（本报告的分析器已经具备全部判定逻辑，见附录）。

---

## 附录 A：复现方法

统计由 `%TEMP%/mcp_probe` 下的临时分析器产出（未入库）。核心逻辑：

1. **掩码**：逐字符扫描源码，把 `//`、`/* */`、`"..."`、`@"..."`、`$"..."`、`"""..."""`、`'c'` 的内容替换成空格（**保持长度与行号不变**）→ 得到只含真实代码的等长副本；
2. **定位**：在掩码副本上匹配 `\bcatch\b`，从 `catch` 后第一个 `{` 起做括号计数找到配对的 `}`；
3. **取样**：用同一对下标从**原始文本**取函数体（保留注释，用于判定"有没有写理由"）；
4. **分类**：按 §1 的三轴，外加 `try` 体文本（用于判断守的是读还是写）。

数据文件（本次审计留档）：`%TEMP%/catch_analysis.tsv`（904 行，含 folder/file/line/method/clause/分类位/注释/try 行数）。

**建议**：把上述 1–3 步固化成 `scripts/Audit-TryCatch.ps1` 或一个 Roslyn analyzer，这样 P4 的守卫才有落点。需要的话我可以直接实现。

## 附录 B：本次审计未覆盖的范围

- **不判断业务正确性**：例如某处吞掉异常后"降级成默认值"是否业务上可接受，需要领域判断，报告只标注了"失败无出口"这一事实；
- **未审计 `Runtime/`、`Cli/` 的细节**：这两层总量小（20/9 个 catch），且语义上就是最佳努力通道，仅给出占比；
- **未做运行时验证**：全部结论来自静态阅读与统计，未在真实 TIA 工程上触发这些失败路径。
