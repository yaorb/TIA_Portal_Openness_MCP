#region

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcpServer.ModelContextProtocol;

#endregion

namespace TiaMcpServer.Siemens;

// Partial: private helper. Extracted from Portal.cs (god-file split); behavior unchanged.
public partial class Portal
{
  #region private helper

  private bool IsPortalNull()
  {
    if (this._portal == null)
    {
      logger?.LogWarning("No TIA portal available.");

      return true;
    }

    return false;
  }

  private bool IsProjectNull()
  {
    if (this.CurrentProject == null)
    {
      // Self-heal for less-capable AI drivers that call a tool before Connect/Open.
      // Deliberately conservative (this is a 99-call-site predicate):
      //   - connected but unbound  -> rebind a project already open in the TIA UI;
      //   - not connected, but a TIA process is already running -> attach & bind it;
      //   - not connected and no TIA running -> do NOT launch (would be a slow failure);
      //     fall through to the actionable "no project" message so the AI calls Connect.
      try
      {
        if (this._portal != null)
        {
          this.GetState();
        }
        else
        {
          var tiaRunning = false;
          try
          {
            tiaRunning = TiaPortal.GetProcesses().Any();
          }
          catch
          {
            // ignored
          }

          if (tiaRunning)
          {
            this.ConnectPortal();
          }
        }
      }
      catch (Exception ex)
      {
        logger?.LogWarning(ex, "IsProjectNull self-heal (auto connect/bind) failed");
      }
    }

    if (this.CurrentProject == null)
    {
      logger?.LogWarning("No TIA project available.");

      return true;
    }

    return false;
  }

  private bool IsSessionNull()
  {
    if (this._session == null)
    {
      logger?.LogWarning("No TIA session available.");

      return true;
    }

    return false;
  }

  #region GetTree ...

  private string GetTreePrefix(List<bool> ancestorStates, bool isLast)
  {
    var prefix = new StringBuilder();

    // Build prefix based on ancestor states
    for (var i = 0; i < ancestorStates.Count; i++)
    {
      prefix.Append(ancestorStates[i]
        ? "    "
        : "│   ");
    }

    // Add current level connector
    prefix.Append(isLast
      ? "└── "
      : "├── ");
    return prefix.ToString();
  }

  private void GetProjectTreeDevices(StringBuilder sb, DeviceComposition devices, List<bool> ancestorStates)
  {
    if (devices.Count == 0)
    {
      return;
    }

    // Check if this is the last main section
    var hasOtherSections = this.CurrentProject?.DeviceGroups is { Count: > 0, } ||
      this.CurrentProject?.UngroupedDevicesGroup != null;
    var isLastMainSection = !hasOtherSections;

    sb.AppendLine($"{this.GetTreePrefix(ancestorStates, isLastMainSection)}Devices [Collection]");

    var deviceList = devices.ToList();
    var newAncestorStates = new List<bool>(ancestorStates) { isLastMainSection, };

    for (var i = 0; i < deviceList.Count; i++)
    {
      var device = deviceList[i];
      var isLastDevice = i == deviceList.Count - 1;

      sb.AppendLine(
        $"{this.GetTreePrefix(newAncestorStates, isLastDevice)}{device.Name} [Device: {device.TypeIdentifier}]");

      if (device.DeviceItems is { Count: > 0, })
      {
        this.GetProjectTreeDeviceItemsRecursive(sb,
          device.DeviceItems,
          [.. newAncestorStates, isLastDevice,]);
      }
    }
  }

  private void GetProjectTreeGroups(StringBuilder sb, DeviceUserGroupComposition groups, List<bool> ancestorStates)
  {
    if (groups.Count == 0)
    {
      return;
    }

    var isLastMainSection = this.CurrentProject?.UngroupedDevicesGroup == null;

    sb.AppendLine($"{this.GetTreePrefix(ancestorStates, isLastMainSection)}Groups [Collection]");

    var groupList = groups.ToList();
    var newAncestorStates = new List<bool>(ancestorStates) { isLastMainSection, };

    for (var i = 0; i < groupList.Count; i++)
    {
      var group = groupList[i];
      var isLastGroup = i == groupList.Count - 1;

      sb.AppendLine($"{this.GetTreePrefix(newAncestorStates, isLastGroup)}{group.Name} [Group]");

      var groupAncestorStates = new List<bool>(newAncestorStates) { isLastGroup, };

      if (group.Devices is { Count: > 0, })
      {
        this.GetProjectTreeGroupDevices(sb,
          group.Devices,
          groupAncestorStates,
          group.Groups is { Count: > 0, });
      }

      if (group.Groups is { Count: > 0, })
      {
        this.GetProjectTreeSubGroups(sb, group.Groups, groupAncestorStates);
      }
    }
  }

  private void GetProjectTreeGroupDevices(StringBuilder sb, DeviceComposition devices, List<bool> ancestorStates,
    bool hasSubGroups)
  {
    var deviceList = devices.ToList();

    for (var i = 0; i < deviceList.Count; i++)
    {
      var device = deviceList[i];
      var isLastDevice = i == deviceList.Count - 1 && !hasSubGroups;

      sb.AppendLine($"{this.GetTreePrefix(ancestorStates, isLastDevice)}{device.Name} [Device]");

      if (device.DeviceItems is { Count: > 0, })
      {
        this.GetProjectTreeDeviceItemsRecursive(sb,
          device.DeviceItems,
          [.. ancestorStates, isLastDevice,]);
      }
    }
  }

  private void GetProjectTreeSubGroups(StringBuilder sb, DeviceUserGroupComposition groups, List<bool> ancestorStates)
  {
    var groupList = groups.ToList();

    for (var i = 0; i < groupList.Count; i++)
    {
      var group = groupList[i];
      var isLastGroup = i == groupList.Count - 1;

      sb.AppendLine($"{this.GetTreePrefix(ancestorStates, isLastGroup)}{group.Name} [Subgroup]");

      var groupAncestorStates = new List<bool>(ancestorStates) { isLastGroup, };

      if (group.Devices is { Count: > 0, })
      {
        this.GetProjectTreeGroupDevices(sb,
          group.Devices,
          groupAncestorStates,
          group.Groups is { Count: > 0, });
      }

      if (group.Groups is { Count: > 0, })
      {
        this.GetProjectTreeSubGroups(sb, group.Groups, groupAncestorStates);
      }
    }
  }

  private void GetProjectTreeDeviceItemsRecursive(StringBuilder sb, DeviceItemComposition deviceItems,
    List<bool> ancestorStates)
  {
    var deviceItemsList = deviceItems.ToList();

    for (var i = 0; i < deviceItemsList.Count; i++)
    {
      var deviceItem = deviceItemsList[i];
      var isLastDeviceItem = i == deviceItemsList.Count - 1;

      sb.AppendLine($"{this.GetTreePrefix(ancestorStates, isLastDeviceItem)}{deviceItem.Name} [DeviceItem]");

      var itemAncestorStates = new List<bool>(ancestorStates) { isLastDeviceItem, };

      // Get software first
      this.GetProjectTreeDeviceItemSoftware(sb, deviceItem, itemAncestorStates);

      // Then get items
      if (deviceItem.Items is { Count: > 0, })
      {
        this.GetProjectTreeItems(sb,
          deviceItem.Items,
          itemAncestorStates,
          deviceItem.DeviceItems is { Count: > 0, });
      }

      // Finally get sub-device items
      if (deviceItem.DeviceItems is { Count: > 0, })
      {
        this.GetProjectTreeDeviceItemsRecursive(sb, deviceItem.DeviceItems, itemAncestorStates);
      }
    }
  }

  private void GetProjectTreeItems(StringBuilder sb, DeviceItemAssociation items, List<bool> ancestorStates,
    bool hasSubDeviceItems)
  {
    var itemsList = items.ToList();

    for (var i = 0; i < itemsList.Count; i++)
    {
      var subItem = itemsList[i];
      var isLastItem = i == itemsList.Count - 1 && !hasSubDeviceItems;

      sb.AppendLine($"{this.GetTreePrefix(ancestorStates, isLastItem)}{subItem.Name} [Hardware Component]");
    }
  }


  private void GetProjectTreeDeviceItemSoftware(StringBuilder sb, DeviceItem deviceItem, List<bool> ancestorStates)
  {
    var softwareContainer = deviceItem.GetService<SoftwareContainer>();
    var hasSoftware = false;

    //PLC software
    if (softwareContainer?.Software is PlcSoftware plcSoftware)
    {
      var hasOtherItems = deviceItem.Items is { Count: > 0, } ||
        deviceItem.DeviceItems is { Count: > 0, };
      sb.AppendLine(
        $"{this.GetTreePrefix(ancestorStates, !hasOtherItems)}PlcSoftware: {plcSoftware.Name} [PLC Program]");
      hasSoftware = true;
    }

    //WinCC HMI software
    if (softwareContainer?.Software is HmiTarget hmiTarget)
    {
      var hasOtherItems = deviceItem.Items is { Count: > 0, } ||
        deviceItem.DeviceItems is { Count: > 0, };
      sb.AppendLine(
        $"{this.GetTreePrefix(ancestorStates, !hasOtherItems && !hasSoftware)}HmiTarget: {hmiTarget.Name} [HMI Program]");
    }

    //Unified HMI software: dlls will only exist on TIA Portal V19 and newer.
    if (Engineering.TiaMajorVersion >= 19)
    {
      this.TryGetUnifiedSoftware(sb, deviceItem, ancestorStates, softwareContainer, hasSoftware);
    }
  }

  private bool TryGetUnifiedSoftware(StringBuilder sb, DeviceItem deviceItem, List<bool> ancestorStates,
    SoftwareContainer? softwareContainer, bool hasSoftware)
  {
    var software = softwareContainer?.Software;
    if (software != null && Portal.IsUnifiedHmiSoftware(software))
    {
      var hasOtherItems = deviceItem.Items is { Count: > 0, } ||
        deviceItem.DeviceItems is { Count: > 0, };
      sb.AppendLine(
        $"{this.GetTreePrefix(ancestorStates, !hasOtherItems && !hasSoftware)}HmiSoftware: {software.Name} [HMI Program]");
      hasSoftware = true;
    }

    return hasSoftware;
  }

  /// <summary>
  ///   是不是 WinCC Unified 的 HmiSoftware。该类型定义在 Siemens.Engineering.HmiUnified 程序集里，
  ///   而这个程序集只在安装了 WinCC Unified Openness 的机器上存在 —— 没装的机器上连编译期引用都没有，
  ///   所以按 FullName 判定（等价于 <c>software is HmiSoftware</c>，只是不依赖那个程序集）。
  ///   本命名空间前缀的判断与 Portal.Software.cs 里其它 Unified 类型（控件 / 动态化）的判定一致。
  /// </summary>
  internal static bool IsUnifiedHmiSoftware(object software) =>
    software.GetType().FullName?.StartsWith("Siemens.Engineering.HmiUnified.", StringComparison.Ordinal) == true;

  private void GetProjectTreeUngroupedDeviceGroup(StringBuilder sb, DeviceSystemGroup ungroupedDevicesGroup,
    List<bool> ancestorStates)
  {
    sb.AppendLine(
      $"{this.GetTreePrefix(ancestorStates, true)}UngroupedDevicesGroup: {ungroupedDevicesGroup.Name} [System Group]");

    if (ungroupedDevicesGroup.Devices is { Count: > 0, })
    {
      var deviceList = ungroupedDevicesGroup.Devices.ToList();
      var newAncestorStates = new List<bool>(ancestorStates) { true, };

      for (var i = 0; i < deviceList.Count; i++)
      {
        var device = deviceList[i];
        var isLastDevice = i == deviceList.Count - 1;

        sb.AppendLine($"{this.GetTreePrefix(newAncestorStates, isLastDevice)}{device.Name} [{device.TypeIdentifier}]");
      }
    }
  }

  #endregion

  #region GetSoftwareTree ...

  public string GetSoftwareTree(string softwarePath)
  {
    logger?.LogInformation("Getting software tree for path: {SoftwarePath}", softwarePath);

    if (this.IsProjectNull())
    {
      // 原来返回空串，工具层的 else 分支于是报「Failed retrieving software tree from 'X'」
      // + InternalError —— 最常见的一种错（忘了 Connect）拿到的是最没用的一句话：
      // 既没说该去 Connect，又把用户的操作顺序问题说成服务器内部错误。
      // 同轮的 GetOnlineState / GetTechnologyObjects 对同一情形已经这么改了，这里补齐。
      throw new PortalException(PortalErrorCode.InvalidState,
        "GetSoftwareTree: no project is open. Call Connect + OpenProject " + "(or AttachToOpenProject) first.");
    }

    try
    {
      var softwareContainer = this.GetSoftwareContainer(softwarePath);
      if (softwareContainer?.Software is PlcSoftware plcSoftware)
      {
        StringBuilder sb = new();
        sb.AppendLine($"{plcSoftware.Name} [PLC Software]");

        var ancestorStates = new List<bool>();
        var sections = new List<Action>();

        var hasBlocks = plcSoftware.BlockGroup != null;
        var hasTypes = plcSoftware.TypeGroup != null;

        // Add blocks section
        if (hasBlocks)
        {
          var blockGroup = plcSoftware.BlockGroup;
          if (blockGroup != null)
          {
            sections.Add(() =>
              this.GetSoftwareTreeBlockGroup(sb, blockGroup, ancestorStates, "Program blocks", !hasTypes));
          }
        }

        // Add types section
        if (hasTypes)
        {
          var typeGroup = plcSoftware.TypeGroup;
          if (typeGroup != null)
          {
            sections.Add(() => this.GetSoftwareTreeTypeGroup(sb, typeGroup, ancestorStates, "PLC data types", true));
          }
        }


        // Execute sections
        for (var i = 0; i < sections.Count; i++)
        {
          sections[i]();
        }

        return sb.ToString();
      }

      // 原来这里把一句「找不到」当作**树的内容**返回。工具层只判了
      // string.IsNullOrEmpty(tree)，非空即算成功 —— 于是路径写错会得到
      // outcome=Success + message「Software tree retrieved from 'XXX'」，
      // 而树体里写着找不到。两个信号互相矛盾，客户端只读 message 就被骗了。
      throw new PortalException(PortalErrorCode.NotFound,
        $"GetSoftwareTree: PLC software not found at '{softwarePath}'." + this.AvailablePlcPathsSuffix());
    }
    catch (PortalException)
    {
      throw;
    }
    catch (Exception ex)
    {
      logger?.LogError(ex, "Error getting software tree for {SoftwarePath}", softwarePath);
      // 同理：异常文本原来也被当成树返回，于是「遍历炸在半路」也是一次成功。
      // 残缺的树比没有树更危险 —— 它看起来完全正常。
      throw new PortalException(PortalErrorCode.OpennessError,
        $"GetSoftwareTree failed halfway through '{softwarePath}': {ex.Message}. " +
        "The tree would have been INCOMPLETE, so it is not returned.",
        null,
        ex);
    }
  }

  private void GetSoftwareTreeBlockGroup(StringBuilder sb, PlcBlockGroup blockGroup, List<bool> ancestorStates,
    string groupLabel, bool isLastSection)
  {
    sb.AppendLine($"{this.GetTreePrefix(ancestorStates, isLastSection)}{groupLabel}"); // [Collection]
    var newAncestorStates = new List<bool>(ancestorStates) { isLastSection, };

    // Get blocks in this group
    var blocks = blockGroup.Blocks.ToList();
    var subGroups = blockGroup.Groups.ToList();

    // First, add all blocks
    for (var i = 0; i < blocks.Count; i++)
    {
      var block = blocks[i];
      // Block is last only if it's the last block AND there are no subgroups following
      var isLastBlock = i == blocks.Count - 1 && subGroups.Count == 0;

      var blockTypeName = new[] { "ArrayDB", "GlobalDB", "InstanceDB", }.Contains(block.GetType().Name)
        ? "DB"
        : block.GetType().Name;

      sb.AppendLine(
        $"{this.GetTreePrefix(newAncestorStates, isLastBlock)}{block.Name} [{blockTypeName}{block.Number}, {block.ProgrammingLanguage}]");
    }

    // Then, add all subgroups recursively
    for (var i = 0; i < subGroups.Count; i++)
    {
      var subGroup = subGroups[i];
      var isLastGroup = i == subGroups.Count - 1;

      sb.AppendLine($"{this.GetTreePrefix(newAncestorStates, isLastGroup)}{subGroup.Name}"); // [Block Group]

      var groupAncestorStates = new List<bool>(newAncestorStates) { isLastGroup, };
      this.GetSoftwareTreeBlockGroupRecursive(sb, subGroup, groupAncestorStates);
    }
  }

  private void GetSoftwareTreeBlockGroupRecursive(StringBuilder sb, PlcBlockGroup blockGroup, List<bool> ancestorStates)
  {
    // Get blocks in this group
    var blocks = blockGroup.Blocks.ToList();
    var subGroups = blockGroup.Groups.ToList();

    // First, add all blocks
    for (var i = 0; i < blocks.Count; i++)
    {
      var block = blocks[i];
      // Block is last only if it's the last block AND there are no subgroups following
      var isLastBlock = i == blocks.Count - 1 && subGroups.Count == 0;

      var blockTypeName = new[] { "ArrayDB", "GlobalDB", "InstanceDB", }.Contains(block.GetType().Name)
        ? "DB"
        : block.GetType().Name;

      sb.AppendLine(
        $"{this.GetTreePrefix(ancestorStates, isLastBlock)}{block.Name} [{blockTypeName}{block.Number}, {block.ProgrammingLanguage}]");
    }

    // Then, add all subgroups recursively
    for (var i = 0; i < subGroups.Count; i++)
    {
      var subGroup = subGroups[i];
      var isLastGroup = i == subGroups.Count - 1;

      sb.AppendLine($"{this.GetTreePrefix(ancestorStates, isLastGroup)}{subGroup.Name}"); // [Block Group]

      var groupAncestorStates = new List<bool>(ancestorStates) { isLastGroup, };
      this.GetSoftwareTreeBlockGroupRecursive(sb, subGroup, groupAncestorStates);
    }
  }

  private void GetSoftwareTreeTypeGroup(StringBuilder sb, PlcTypeGroup typeGroup, List<bool> ancestorStates,
    string groupLabel, bool isLastSection)
  {
    sb.AppendLine($"{this.GetTreePrefix(ancestorStates, isLastSection)}{groupLabel}"); // [Collection]
    var newAncestorStates = new List<bool>(ancestorStates) { isLastSection, };

    // Get types in this group
    var types = typeGroup.Types.ToList();
    var subGroups = typeGroup.Groups.ToList();

    // First, add all types
    for (var i = 0; i < types.Count; i++)
    {
      var type = types[i];
      // Type is last only if it's the last type AND there are no subgroups following
      var isLastType = i == types.Count - 1 && subGroups.Count == 0;

      var typeTypeName = type.GetType().Name;
      typeTypeName = typeTypeName == "PlcStruct"
        ? "UDT"
        : typeTypeName;

      sb.AppendLine($"{this.GetTreePrefix(newAncestorStates, isLastType)}{type.Name} [{typeTypeName}]");
    }

    // Then, add all subgroups recursively
    for (var i = 0; i < subGroups.Count; i++)
    {
      var subGroup = subGroups[i];
      var isLastGroup = i == subGroups.Count - 1;

      sb.AppendLine($"{this.GetTreePrefix(newAncestorStates, isLastGroup)}{subGroup.Name}"); // [Type Group]

      var groupAncestorStates = new List<bool>(newAncestorStates) { isLastGroup, };
      this.GetSoftwareTreeTypeGroupRecursive(sb, subGroup, groupAncestorStates);
    }
  }

  private void GetSoftwareTreeTypeGroupRecursive(StringBuilder sb, PlcTypeGroup typeGroup, List<bool> ancestorStates)
  {
    // Get types in this group
    var types = typeGroup.Types.ToList();
    var subGroups = typeGroup.Groups.ToList();

    // First, add all types
    for (var i = 0; i < types.Count; i++)
    {
      var type = types[i];
      // Type is last only if it's the last type AND there are no subgroups following
      var isLastType = i == types.Count - 1 && subGroups.Count == 0;

      var typeTypeName = type.GetType().Name;
      typeTypeName = typeTypeName == "PlcStruct"
        ? "UDT"
        : typeTypeName;

      sb.AppendLine($"{this.GetTreePrefix(ancestorStates, isLastType)}{type.Name} [{typeTypeName}]");
    }

    // Then, add all subgroups recursively
    for (var i = 0; i < subGroups.Count; i++)
    {
      var subGroup = subGroups[i];
      var isLastGroup = i == subGroups.Count - 1;

      sb.AppendLine($"{this.GetTreePrefix(ancestorStates, isLastGroup)}{subGroup.Name}"); // [Type Group]

      var groupAncestorStates = new List<bool>(ancestorStates) { isLastGroup, };
      this.GetSoftwareTreeTypeGroupRecursive(sb, subGroup, groupAncestorStates);
    }
  }

  #endregion

  #region GetSoftwareContainer ...

  /// <summary>
  ///   Resolve a TIA Openness service (e.g. OnlineProvider, DownloadProvider) for a given
  ///   PLC software path. Tries the PlcSoftware first, then walks up the SoftwareContainer's
  ///   DeviceItem chain. Required because some hardware variants (notably 1200-series CPUs in
  ///   nested device groups) only expose Online/Download providers on the CPU DeviceItem,
  ///   not on the PlcSoftware itself.
  /// </summary>
  private T? ResolvePlcService<T>(string softwarePath, PlcSoftware plcSoftware) where T : class, IEngineeringService
  {
    var direct = plcSoftware.GetService<T>();
    if (direct != null)
    {
      return direct;
    }

    var sc = this.GetSoftwareContainer(softwarePath);
    var di = sc?.Parent as DeviceItem;
    while (di != null)
    {
      var s = di.GetService<T>();
      if (s != null)
      {
        return s;
      }

      di = di.Parent as DeviceItem;
    }

    return null;
  }

  /// <summary>
  ///   Subscribes a password handler to the OnlineLegitimation event so a protected CPU
  ///   can be authenticated during GoOnline / Download. Returns an IDisposable that
  ///   unsubscribes when disposed; callers MUST dispose to avoid handler leaks.
  ///   Returns null when no password is provided or the configuration object is not a
  ///   ConnectionConfiguration (no event to hook).
  /// </summary>
  private static IDisposable? AttachPasswordHandler(object? configuration, string? password)
  {
    if (string.IsNullOrEmpty(password) || configuration is not ConnectionConfiguration conn)
    {
      return null;
    }

    // Build the SecureString once. The same instance can be reused across multiple
    // legitimation prompts within the same call (e.g., read-then-write access).
    var secure = new SecureString();
    foreach (var c in password!)
    {
      secure.AppendChar(c);
    }

    secure.MakeReadOnly();

    OnlineConfigurationDelegate handler = cfg =>
    {
      if (cfg is OnlinePasswordConfiguration pwdCfg)
      {
        pwdCfg.SetPassword(secure);
      }
    };
    conn.OnlineLegitimation += handler;
    return new HandlerScope(() => conn.OnlineLegitimation -= handler);
  }

  private sealed class HandlerScope(Action detach) : IDisposable
  {
    private Action? _detach = detach;

    public void Dispose()
    {
      this._detach?.Invoke();
      this._detach = null;
    }
  }

  private SoftwareContainer? GetSoftwareContainer(string softwarePath)
  {
    // 清空点必须在这里、不能放到 ResolveSoftwareContainerUncached 里：下面有缓存，
    // 命中缓存时根本不进 Uncached，在那里清会让上一次的错误跨调用残留，
    // 把一次早已成功的解析说成"遍历出错"。放在入口＝每次对外解析都从干净状态开始。
    Portal._deviceScanFirstError = null;

    if (this.CurrentProject == null)
    {
      if (this._softwareCacheProject != null)
      {
        this._softwareContainerCache.Clear();
        this._softwareCacheProject = null;
      }

      return null;
    }

    // Invalidate cached resolutions whenever the open project instance changes
    // (open/close/create/attach all reassign _project). Free check, no COM call.
    if (!object.ReferenceEquals(this.CurrentProject, this._softwareCacheProject))
    {
      this._softwareContainerCache.Clear();
      this._softwareCacheProject = this.CurrentProject;
    }

    if (this._softwareContainerCache.TryGetValue(softwarePath, out var cached))
    {
      return cached;
    }

    var resolved = this.ResolveSoftwareContainerUncached(softwarePath);
    if (resolved != null)
    {
      this._softwareContainerCache[softwarePath] = resolved;
    }

    return resolved;
  }

  private SoftwareContainer? ResolveSoftwareContainerUncached(string softwarePath)
  {
    if (this.CurrentProject == null)
    {
      return null;
    }

    var pathSegments = softwarePath.Split('/');
    var index = 0;

    if (index >= pathSegments.Length)
    {
      return null;
    }

    var segment = pathSegments[index];
    SoftwareContainer? softwareContainer = null;

    // in Devices
    if (this.CurrentProject.Devices != null)
    {
      softwareContainer = this.GetSoftwareContainerInDevices(this.CurrentProject.Devices, pathSegments, index);
      if (softwareContainer != null)
      {
        return softwareContainer;
      }
    }

    // in Groups
    if (this.CurrentProject.DeviceGroups != null)
    {
      softwareContainer = this.GetSoftwareContainerInGroups(this.CurrentProject.DeviceGroups, pathSegments, index);
      if (softwareContainer != null)
      {
        return softwareContainer;
      }
    }

    return null;
  }

  private SoftwareContainer? GetSoftwareContainerInDevices(DeviceComposition devices, string[] pathSegments, int index)
  {
    if (index >= pathSegments.Length)
    {
      return null;
    }

    var segment = pathSegments[index];
    var nextSegment = index + 1 < pathSegments.Length
      ? pathSegments[index + 1]
      : string.Empty;

    if (devices != null)
    {
      SoftwareContainer? softwareContainer = null;
      Device? device = null;
      DeviceItem? deviceItem = null;

      // a pc based plc has a Device.Name = 'PC-System_1' or something like that, which is visible in the TIA-Portal IDE
      // use segment to find device
      device = devices.FirstOrDefault(d => d.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
      if (device != null)
      {
        // If path is only the device name (no next segment), or next segment doesn't match,
        // search within the device's device items for one that actually hosts a SoftwareContainer.
        var scFromDeviceItems = Portal.FindFirstSoftwareContainer(device.DeviceItems,
          string.IsNullOrWhiteSpace(nextSegment)
            ? null
            : nextSegment);
        if (scFromDeviceItems != null)
        {
          return scFromDeviceItems;
        }

        // Otherwise fall back to the old behavior (exact next-segment match, non-recursive)
        if (!string.IsNullOrWhiteSpace(nextSegment))
        {
          deviceItem =
            device.DeviceItems.FirstOrDefault(di => di.Name.Equals(nextSegment, StringComparison.OrdinalIgnoreCase));
          softwareContainer = Portal.GetSoftwareContainerInDeviceItem(deviceItem, pathSegments, index + 1);
          if (softwareContainer != null)
          {
            return softwareContainer;
          }
        }
      }

      // a hardware plc has a Device.Name = 'S7-1500/ET200MP-Station_1' or something like that, which is not visible in
      // the TIA-Portal IDE
      // ignored segment for Device.Name and use it for DeviceItem.Name
      // IMPORTANT: multiple DeviceItems can share the same name (Unified HMI often does).
      // Prefer the one that actually has a SoftwareContainer service.
      var flatItems = devices.SelectMany(d => d.DeviceItems).ToList();
      var scFromFlat = Portal.FindFirstSoftwareContainer(flatItems, segment);
      if (scFromFlat != null)
      {
        return scFromFlat;
      }

      deviceItem = flatItems.FirstOrDefault(di => di.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
      if (deviceItem != null)
      {
        return Portal.GetSoftwareContainerInDeviceItem(deviceItem, pathSegments, index);
      }
    }

    return null;
  }

  /// <summary>
  ///   遍历设备树时被吞掉的首个异常（"位置: 异常类型: message"），没有则为 null。
  ///   为什么要存这么一份：FindFirstSoftwareContainer 是静态方法、返回值又只有
  ///   SoftwareContainer?，改签名会波及大量调用点；把真因暂存在这里，才能在不动签名的
  ///   前提下让上层的"找不到 PLC"消息说出真话，而不是让用户去改一个本来就对的路径。
  ///   [ThreadStatic]：Openness 调用按线程走，避免并发会话互相串消息。
  /// </summary>
  [ThreadStatic]
  private static string? _deviceScanFirstError;

  /// <summary>
  ///   只记首个：后续异常多半是同一个代理故障的连锁反应，首个最接近真因。
  /// </summary>
  private static void RecordDeviceScanError(string where, Exception ex)
  {
    Portal._deviceScanFirstError ??= $"{where}: {ex.GetType().Name}: {ex.Message}";
  }

  /// <summary>
  ///   供"找不到 PLC"一类消息拼接的后缀；本次解析没有吞过异常时返回空串
  ///   （保证遍历正常时消息与以前逐字节相同）。
  /// </summary>
  public string DeviceScanErrorSuffix()
  {
    var err = Portal._deviceScanFirstError;
    return string.IsNullOrEmpty(err)
      ? string.Empty
      : " Device tree scan error: " + err;
  }

  private static SoftwareContainer? FindFirstSoftwareContainer(IEnumerable<DeviceItem> roots, string? preferName)
  {
    try
    {
      var stack = new Stack<DeviceItem>(roots?.Where(x => x != null) ?? []);
      while (stack.Count > 0)
      {
        var it = stack.Pop();
        if (it == null)
        {
          continue;
        }

        var nameOk = string.IsNullOrWhiteSpace(preferName) ||
          it.Name.Equals(preferName, StringComparison.OrdinalIgnoreCase);
        if (nameOk)
        {
          try
          {
            var sc = it.GetService<SoftwareContainer>();
            if (sc != null)
            {
              return sc;
            }
          }
          catch
          {
          }
        }

        try
        {
          if (it.DeviceItems != null)
          {
            foreach (var ch in it.DeviceItems)
            {
              if (ch != null)
              {
                stack.Push(ch);
              }
            }
          }
        }
        catch (Exception ex)
        {
          // 这里抛异常＝整棵子树被静默剪掉，PLC 可能就在被剪掉的那半边。
          // 继续遍历其余分支（保持原行为），但把真因留下。
          Portal.RecordDeviceScanError("DeviceItems(" + (Portal.TryGetDeviceItemName(it) ?? "?") + ")", ex);
        }
      }
    }
    catch (Exception ex)
    {
      // 这里抛异常＝整次遍历被静默中止，结果一定是"找不到"，必须留下真因。
      Portal.RecordDeviceScanError("DeviceTreeScan", ex);
    }

    return null;
  }

  /// <summary>
  ///   取 DeviceItem 名字用于错误定位；读 Name 本身也可能抛（代理已失效），所以再包一层。
  /// </summary>
  private static string? TryGetDeviceItemName(DeviceItem item)
  {
    try
    {
      return item.Name;
    }
    catch
    {
      return null;
    }
  }

  private SoftwareContainer? GetSoftwareContainerInGroups(DeviceUserGroupComposition? groups, string[] pathSegments, int index)
  {
    while (true)
    {
      if (index >= pathSegments.Length)
      {
        return null;
      }

      var segment = pathSegments[index];

      var group = groups?.FirstOrDefault(g => g.Name.Equals(segment));
      if (group == null)
      {
        return null;
      }

      // when segment matched
      var softwareContainer = this.GetSoftwareContainerInDevices(group.Devices, pathSegments, index + 1);
      if (softwareContainer != null)
      {
        return softwareContainer;
      }

      groups = group.Groups;
      index = index + 1;
    }
  }

  private static SoftwareContainer? GetSoftwareContainerInDeviceItem(DeviceItem? deviceItem, string[] pathSegments, int index)
  {
    if (deviceItem != null)
    {
      // when segment matched
      if (index == pathSegments.Length - 1)
      {
        // get from DeviceItem
        var softwareContainer = deviceItem.GetService<SoftwareContainer>();
        if (softwareContainer != null)
        {
          return softwareContainer;
        }
      }
    }

    return null;
  }

  #endregion

  #region Get...ByPath

  private Device? GetDeviceByPath(string devicePath)
  {
    if (this.CurrentProject?.Devices == null || string.IsNullOrWhiteSpace(devicePath))
    {
      return null;
    }

    devicePath = devicePath.Trim();

    // Siemens hardware device names legitimately contain '/', e.g. "S7-1500/ET200MP station_1".
    // An exact full-name match must therefore win BEFORE we treat '/' as a path separator,
    // otherwise such devices are impossible to address (split -> bogus group lookup -> null).
    // Also accept the IDE-visible CPU name (a DeviceItem name, e.g. "安全PLC"/"5T车"), which differs
    // from the parent Device name (e.g. "S7-1200 station_3"); resolve it back to its owning Device.
    var allDevices = this.EnumerateAllDevices().ToList();
    var exact = allDevices.FirstOrDefault(d => d.Name.Equals(devicePath, StringComparison.OrdinalIgnoreCase));
    if (exact != null)
    {
      return exact;
    }

    var byItem = allDevices.FirstOrDefault(d => Portal.DeviceHasItemNamed(d.DeviceItems, devicePath));
    if (byItem != null)
    {
      return byItem;
    }

    var pathSegments = devicePath.Split(['/',], StringSplitOptions.RemoveEmptyEntries);
    if (pathSegments.Length == 0)
    {
      return null;
    }

    // Try top-level device first
    if (pathSegments.Length == 1)
    {
      return this.CurrentProject.Devices.FirstOrDefault(d =>
        d.Name.Equals(pathSegments[0], StringComparison.OrdinalIgnoreCase));
    }

    // Traverse device groups
    var groups = this.CurrentProject.DeviceGroups;
    var group = groups?.FirstOrDefault(g => g.Name.Equals(pathSegments[0], StringComparison.OrdinalIgnoreCase));

    if (group == null)
    {
      return null;
    }

    for (var i = 1; i < pathSegments.Length; i++)
    {
      // Try to find device in current group
      var device =
        group.Devices.FirstOrDefault(d => d.Name.Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase));
      if (device != null)
      {
        return device;
      }

      // Try to find subgroup
      group = group.Groups.FirstOrDefault(g => g.Name.Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase));
      if (group == null)
      {
        break;
      }
    }

    return null;
  }

  // Enumerate every Device in the project, including those nested inside device groups.
  private IEnumerable<Device> EnumerateAllDevices()
  {
    if (this.CurrentProject?.Devices != null)
    {
      foreach (var d in this.CurrentProject.Devices)
      {
        yield return d;
      }
    }

    foreach (var d in Portal.EnumerateGroupDevices(this.CurrentProject?.DeviceGroups))
    {
      yield return d;
    }
  }

  private static IEnumerable<Device> EnumerateGroupDevices(DeviceUserGroupComposition? groups)
  {
    if (groups == null)
    {
      yield break;
    }

    foreach (var g in groups)
    {
      foreach (var d in g.Devices)
      {
        yield return d;
      }

      foreach (var d in Portal.EnumerateGroupDevices(g.Groups))
      {
        yield return d;
      }
    }
  }

  // True if the device contains a DeviceItem (CPU/module, at any nesting depth) with the given name.
  private static bool DeviceHasItemNamed(DeviceItemComposition? items, string name)
  {
    if (items == null)
    {
      return false;
    }

    foreach (var it in items)
    {
      if (it.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }

      if (Portal.DeviceHasItemNamed(it.DeviceItems, name))
      {
        return true;
      }
    }

    return false;
  }

  private DeviceItem? GetDeviceItemByPath(string deviceItemPath)
  {
    if (this.CurrentProject?.Devices == null)
    {
      return null;
    }

    // Split the device path by '/' to get each device name
    var pathSegments = deviceItemPath.Split(['/',], StringSplitOptions.RemoveEmptyEntries);

    DeviceItem? deviceItem = null;

    // initial devices and groups
    var devices = this.CurrentProject.Devices;
    var groups = this.CurrentProject.DeviceGroups;

    for (var index = 0; index < pathSegments.Length; index++)
    {
      deviceItem = Portal.GetDeviceItemFromDevice(pathSegments, devices, index, out var anchorMatched);

      // 这个循环是有意宽松的：它允许调用方省掉前缀（"PLC_1/AI 2_1" 而不是
      // "S7-1200 station_1/PLC_1/AI 2_1"），做法是匹配不上就把窗口往后滑一格再试。
      // 但窗口滑动必须只用于「这一段根本不是这层的东西」，**不能用于
      // 「这一段对上了、后面某一段对不上」** —— 那种情况下往后滑，最终会被
      // SelectMany 扫到路径中间的某一段并原样返回，于是「路径打错一段」
      // 静默变成「拿到它的父级设备项」：读地址读到父级的（空）清单、
      // 改地址改到父级头上。调用方看不出任何异常。
      if (anchorMatched)
      {
        return deviceItem;
      }

      if (deviceItem == null)
      {
        // search in groups
        var group = groups?.FirstOrDefault(g => g.Name.Equals(pathSegments[index], StringComparison.OrdinalIgnoreCase));
        if (group != null)
        {
          devices = group.Devices;
          if (devices != null)
          {
            // 这里丢弃 anchorMatched 是有意的：设备组可以嵌套
            // （"GroupA/GroupB/Device/Item"），下一段对不上组内设备属于正常，
            // 要继续往 group.Groups 里找，此处必须允许窗口滑动。
            deviceItem = Portal.GetDeviceItemFromDevice(pathSegments, devices, index + 1, out _);
          }

          if (deviceItem != null)
          {
            return deviceItem;
          }

          // not found, but on the path
          groups = group.Groups;
          devices = group.Devices;
        }
      }
      else
      {
        return deviceItem;
      }
    }

    return deviceItem;
  }

  /// <param name="anchorMatched">
  ///   本层是否**认领**了 pathSegments[index]（匹配到同名 Device 或 DeviceItem）。
  ///   认领了却返回 null，意思是「锚点对上了，但后面某一段不存在」——
  ///   调用方必须就此判定失败，不能再把窗口往后滑（滑动会让错误路径解析成祖先节点）。
  /// </param>
  /// <param name="index"></param>
  /// <param name="consumed"></param>
  /// <param name="children"></param>
  /// <param name="segments"></param>
  /// <summary>
  ///   在一组子设备项里按名字找一个，**允许名字本身含 '/'**。
  ///   为什么要这样：deviceItemPath 用 '/' 分段，而西门子的模块名常常自带 '/'——
  ///   `DQ 16x24VDC/0.5A ST_1`、`AI 4xI 2-/4-wire ST_1`、`AQ 4xU/I ST_1`、`DI 6/DQ 4_1`。
  ///   逐段比对的话，这些模块**从原理上就寻不到址**：不是找不到，是根本表达不出来
  ///   （用户在 issue #33 里点名了这一条）。
  ///   做法是贪心：把 segments[index..] 里的前 k 段拼回 "a/b/c" 再和子项名比，
  ///   **k 从大到小试**，先匹配更长（更具体）的那个，命中就把 consumed 设成 k。
  ///   子项名是已知的有限集合，所以这不是猜——是拿候选名去对，不会误吃别人的段。
  /// </summary>
  private static DeviceItem? MatchChildByName(IEnumerable<DeviceItem>? children, string[] segments, int index,
    out int consumed)
  {
    consumed = 0;
    if (children == null || index >= segments.Length)
    {
      return null;
    }

    var list = children as IList<DeviceItem> ?? [.. children,];
    var maxSpan = segments.Length - index;
    for (var span = maxSpan; span >= 1; span--)
    {
      var candidate = string.Join("/", segments, index, span);
      foreach (var child in list)
      {
        if (child != null && child.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
        {
          consumed = span;
          return child;
        }
      }
    }

    return null;
  }

  private static DeviceItem? GetDeviceItemFromDevice(string[] pathSegments, DeviceComposition? devices, int index,
    out bool anchorMatched)
  {
    var segment = pathSegments[index];
    var nextSegment = index + 1 < pathSegments.Length
      ? pathSegments[index + 1]
      : string.Empty;

    anchorMatched = false;
    DeviceItem? deviceItem = null;

    // a pc based plc has a Device.Name = 'PC-System_1' or something like that, which is visible in the TIA-Portal IDE
    // use segment to find device
    if (devices != null)
    {
      var device = devices.FirstOrDefault(d => d.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
      if (device != null)
      {
        anchorMatched = true;
        if (string.IsNullOrWhiteSpace(nextSegment))
        {
          deviceItem =
            device.DeviceItems.FirstOrDefault(di => di.Name.Equals(segment, StringComparison.OrdinalIgnoreCase)) ??
            device.DeviceItems.FirstOrDefault();
        }
        else
        {
          // 名字里可能含 '/'，所以每一层都走贪心匹配，吃掉几段由匹配结果决定。
          deviceItem = Portal.MatchChildByName(device.DeviceItems, pathSegments, index + 1, out var used);
          var nextIndex = index + 1 + used;
          while (deviceItem != null && nextIndex < pathSegments.Length)
          {
            deviceItem = Portal.MatchChildByName(deviceItem.DeviceItems, pathSegments, nextIndex, out used);
            if (used == 0)
            {
              break;
            }

            nextIndex += used;
          }
        }
      }

      // a hardware plc has a Device.Name = 'S7-1500/ET200MP-Station_1' or something like that, which is not visible in
      // the TIA-Portal IDE
      if (device == null)
      {
        deviceItem = devices.SelectMany(d => d.DeviceItems)
          .FirstOrDefault(di => di.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));

        // 这一段被认领了就得把**剩下的段也走完**。原来这里找到就直接返回，
        // 后面的段整段被忽略：'安全PLC/不存在的模块' 会返回 '安全PLC' 本身。
        if (deviceItem != null)
        {
          anchorMatched = true;
          var next = index + 1;
          while (deviceItem != null && next < pathSegments.Length)
          {
            deviceItem = Portal.MatchChildByName(deviceItem.DeviceItems, pathSegments, next, out var used);
            if (used == 0)
            {
              break;
            }

            next += used;
          }
        }
      }
    }

    return deviceItem;
  }

  private PlcBlockGroup? GetPlcBlockGroupByPath(string softwarePath, string groupPath)
  {
    if (this.CurrentProject == null)
    {
      return null;
    }

    var softwareContainer = this.GetSoftwareContainer(softwarePath);
    if (softwareContainer?.Software is PlcSoftware plcSoftware)
    {
      if (plcSoftware?.BlockGroup == null)
      {
        return null;
      }


      // Split the path by '/' to get each group name
      var groupNames = groupPath.Split(['/',], StringSplitOptions.RemoveEmptyEntries);

      PlcBlockGroup? currentGroup = plcSoftware.BlockGroup;

      // GetSoftwareTree labels the root user block group "Program blocks" (localized "程序块").
      // Callers naturally copy that full path, so skip a leading segment that denotes the root itself.
      var startIndex = 0;
      if (groupNames.Length > 0 && (groupNames[0].Equals("Program blocks", StringComparison.OrdinalIgnoreCase) ||
        groupNames[0].Equals("程序块", StringComparison.OrdinalIgnoreCase)))
      {
        startIndex = 1;
      }

      for (var i = startIndex; i < groupNames.Length; i++)
      {
        currentGroup =
          currentGroup.Groups.FirstOrDefault(g => g.Name.Equals(groupNames[i], StringComparison.OrdinalIgnoreCase));

        if (currentGroup == null)
        {
          return null;
        }
      }

      return currentGroup;
    }

    return null;
  }

  // ======================================================================
  // Block group operations: create (sub)groups + move a block into a group.
  // NOTE: TIA Openness has NO API to reparent an existing PlcBlock
  // (PlcBlock.Parent is read-only). So:
  //   - EnsurePlcBlockGroup: native, via PlcBlockUserGroupComposition.Create.
  //   - MoveBlockToGroup: export -> Delete -> import-into-group round-trip,
  //     so generated blocks can be organized into layer folders afterwards.
  // ======================================================================

  // Navigate the block-group tree by path, CREATING any missing user groups.
  // Returns the leaf group (or null if software not found). `created` lists the
  // group names that were newly created this call (for reporting / idempotency).
  public PlcBlockGroup? EnsurePlcBlockGroup(string softwarePath, string groupPath, out List<string> created)
  {
    created = [];
    if (this.IsProjectNull())
    {
      return null;
    }

    var softwareContainer = this.GetSoftwareContainer(softwarePath);
    if (softwareContainer?.Software is not PlcSoftware plcSoftware || plcSoftware.BlockGroup == null)
    {
      return null;
    }

    var groupNames = groupPath.Split(['/',], StringSplitOptions.RemoveEmptyEntries);
    PlcBlockGroup currentGroup = plcSoftware.BlockGroup;
    foreach (var groupName in groupNames)
    {
      var next = currentGroup.Groups.FirstOrDefault(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
      if (next == null)
      {
        next = currentGroup.Groups.Create(groupName);
        created.Add(groupName);
        logger?.LogInformation($"Created PLC block group '{groupName}'");
      }

      currentGroup = next;
    }

    return currentGroup;
  }

  // Move an existing block (found anywhere by exact name) into targetGroupPath.
  // Implemented as export -> Delete -> import-into-group because Openness cannot
  // reparent a block. Prefers SIMATIC SD documents (.s7dcl, keeps comments);
  // falls back to SimaticML XML for mixed-language/STL blocks. Returns a summary.
  public string MoveBlockToGroup(string softwarePath, string blockName, string targetGroupPath,
    bool autoCreateGroup = true)
  {
    if (this.IsProjectNull())
    {
      throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
    }

    var softwareContainer = this.GetSoftwareContainer(softwarePath);
    if (softwareContainer?.Software is not PlcSoftware plcSoftware || plcSoftware.BlockGroup == null)
    {
      throw new PortalException(PortalErrorCode.NotFound, $"PlcSoftware not found at '{softwarePath}'");
    }

    // 1) find the block anywhere by exact name
    var all = new List<PlcBlock>();
    this.GetBlocksRecursive(plcSoftware.BlockGroup, all);
    var block = all.FirstOrDefault(b => b.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase));
    if (block == null)
    {
      throw new PortalException(PortalErrorCode.NotFound, $"Block '{blockName}' not found in '{softwarePath}'");
    }

    // 2) ensure the target group exists
    var targetGroup = autoCreateGroup
      ? this.EnsurePlcBlockGroup(softwarePath, targetGroupPath, out _)
      : this.GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
    if (targetGroup == null)
    {
      throw new PortalException(PortalErrorCode.NotFound,
        $"Target block group '{targetGroupPath}' not found (set autoCreateGroup=true to create it)");
    }

    // already in the target group?
    if (object.ReferenceEquals(block.Parent, targetGroup))
    {
      return $"Block '{blockName}' already in group '{targetGroupPath}' (no move needed)";
    }

    // 3) export -> delete -> import into target group (no native reparent)
    var tempDir = Path.Combine(Path.GetTempPath(), "tia_mcp_move", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    string method;
    try
    {
      bool usedDocs;
      try
      {
        var exp = block.ExportAsDocuments(new DirectoryInfo(tempDir), blockName);
        usedDocs = exp is { State: DocumentResultState.Success, };
      }
      catch (EngineeringNotSupportedException)
      {
        usedDocs = false; // mixed-language / STL -> fall back to XML
      }

      if (usedDocs)
      {
        block.Delete();
        var res = targetGroup.Blocks.ImportFromDocuments(new DirectoryInfo(tempDir),
          blockName,
          ImportDocumentOptions.Override);
        if (res is not { State: DocumentResultState.Success, })
        {
          throw new PortalException(PortalErrorCode.ImportFailed,
            $"Re-import of '{blockName}' into '{targetGroupPath}' failed (documents)");
        }

        method = "documents(.s7dcl)";
      }
      else
      {
        var xml = Path.Combine(tempDir, blockName + ".xml");
        block.Export(new FileInfo(xml), ExportOptions.None);
        block.Delete();
        var imp = targetGroup.Blocks.Import(new FileInfo(xml), ImportOptions.Override);
        if (imp == null || imp.Count == 0)
        {
          throw new PortalException(PortalErrorCode.ImportFailed,
            $"Re-import of '{blockName}' into '{targetGroupPath}' failed (xml)");
        }

        method = "xml(SimaticML)";
      }
    }
    finally
    {
      try
      {
        if (Directory.Exists(tempDir))
        {
          Directory.Delete(tempDir, true);
        }
      }
      catch
      {
        /* best-effort cleanup */
      }
    }

    // 4) verify the block is now under the target group
    var verifyGroup = this.GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
    var present =
      verifyGroup?.Blocks.FirstOrDefault(b => b.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase)) != null;
    if (!present)
    {
      throw new PortalException(PortalErrorCode.ImportFailed,
        $"Move of '{blockName}' to '{targetGroupPath}' could not be verified");
    }

    return $"Moved '{blockName}' to '{targetGroupPath}' via {method}";
  }

  private PlcTypeGroup? GetPlcTypeGroupByPath(string softwarePath, string groupPath)
  {
    if (this.CurrentProject == null)
    {
      return null;
    }

    var softwareContainer = this.GetSoftwareContainer(softwarePath);
    if (softwareContainer?.Software is PlcSoftware plcSoftware)
    {
      if (plcSoftware?.TypeGroup == null)
      {
        return null;
      }

      var groupNames = groupPath.Split(['/',], StringSplitOptions.RemoveEmptyEntries);

      PlcTypeGroup? currentGroup = plcSoftware.TypeGroup;

      foreach (var groupName in groupNames)
      {
        currentGroup =
          currentGroup.Groups.FirstOrDefault(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));

        if (currentGroup == null)
        {
          return null;
        }
      }

      return currentGroup;
    }

    return null;
  }

  private string GetPlcBlockGroupPath(PlcBlockGroup? group)
  {
    if (group == null)
    {
      return string.Empty;
    }

    var nullableGroup = group;
    var path = group.Name;

    while (nullableGroup is { Parent: not null, })
    {
      try
      {
        //group = (PlcBlockGroup) group.Parent;
        if (group is PlcBlockSystemGroup systemGroup)
        {
          // do not get parent for system group
          break;
        }

        nullableGroup = nullableGroup.Parent as PlcBlockGroup;
      }
      catch (Exception)
      {
        // Handle any exceptions that may occur while accessing the parent
        break;
      }

      if (nullableGroup != null)
      {
        path = $"{nullableGroup.Name}/{path}";
      }
    }

    return path;
  }

  private string GetPlcTypeGroupPath(PlcTypeGroup? group)
  {
    if (group == null)
    {
      return string.Empty;
    }

    var nullableGroup = group;
    var path = group.Name;

    while (nullableGroup is { Parent: not null, })
    {
      try
      {
        //group = (PlcTypeGroup) group.Parent;
        if (group is PlcTypeSystemGroup systemGroup)
        {
          // do not get parent for system group
          break;
        }

        nullableGroup = nullableGroup.Parent as PlcTypeGroup;
      }
      catch (Exception)
      {
        // Handle any exceptions that may occur while accessing the parent
        break;
      }

      if (nullableGroup != null)
      {
        path = $"{nullableGroup.Name}/{path}";
      }
    }

    return path;
  }

  #endregion

  #region GetRecursive ...

  private bool GetDevicesRecursive(DeviceUserGroup group, List<Device> list, string regexName = "")
  {
    var anySuccess = false;

    foreach (var composition in group.Devices)
    {
      if (composition is not { } device)
      {
        continue;
      }

      try
      {
        if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(device.Name, regexName, RegexOptions.IgnoreCase))
        {
          continue; // Skip this device if it doesn't match the pattern
        }
      }
      catch (Exception)
      {
        // Invalid regex pattern, skip this device
        continue;
      }

      list.Add(device);

      anySuccess = true;
    }

    foreach (var subgroup in group.Groups)
    {
      anySuccess = this.GetDevicesRecursive(subgroup, list, regexName);
    }

    return anySuccess;
  }

  // 返回值曾经是 bool anySuccess，但它**算了没人看**：两个调用点都丢弃返回值，
  // 而且子组那一行写的是 `anySuccess = 递归(...)` —— 覆盖而非累积，最终只反映最后一个子组。
  // 一个既没人读、读了也是错的值，留着只会让人以为这里有个「有没有找到」的信号。
  private void GetBlocksRecursive(PlcBlockGroup group, List<PlcBlock> list, string regexName = "")
  {
    // 正则一次编译好。原来是逐块 try/catch：模式写错时每个块都被 catch 掉、
    // 最后返回空列表，对外表现成「这个 PLC 里没有匹配的块」—— 把「你的模式非法」
    // 说成了一个具体的、错的事实。
    var filter = Portal.CompileNameFilterOrThrow(regexName);

    foreach (var composition in group.Blocks)
    {
      if (composition is not { } block)
      {
        continue;
      }

      if (filter != null && !filter.IsMatch(block.Name))
      {
        continue; // Skip this block if it doesn't match the pattern
      }

      list.Add(block);
    }

    foreach (var subgroup in group.Groups)
    {
      this.GetBlocksRecursive(subgroup, list, regexName);
    }
  }

  /// <summary>
  ///   把 regexName 编成 Regex；空串 = 不过滤（返回 null）。模式非法就当场抛，
  ///   绝不退化成「一个都没匹配上」—— 那会让调用方以为工程里真的没有这些对象。
  /// </summary>
  private static Regex? CompileNameFilterOrThrow(string regexName)
  {
    if (string.IsNullOrEmpty(regexName))
    {
      return null;
    }

    try
    {
      return new Regex(regexName, RegexOptions.IgnoreCase);
    }
    catch (ArgumentException ex)
    {
      throw new PortalException(PortalErrorCode.InvalidParams,
        $"regexName '{regexName}' is not a valid regular expression: {ex.Message}. " +
        "Pass an empty string to list everything, or escape the special characters.",
        null,
        ex);
    }
  }

  // 与 GetBlocksRecursive 同因：返回值没人读、且子组那行是覆盖不是累积，已去掉。
  private void GetTypesRecursive(PlcTypeGroup group, List<PlcType> list, string regexName = "")
  {
    var filter = Portal.CompileNameFilterOrThrow(regexName);

    foreach (var composition in group.Types)
    {
      if (composition is not { } type)
      {
        continue;
      }

      if (filter != null && !filter.IsMatch(type.Name))
      {
        continue; // Skip this type if it doesn't match the pattern
      }

      list.Add(type);
    }

    foreach (PlcTypeGroup subgroup in group.Groups)
    {
      this.GetTypesRecursive(subgroup, list, regexName);
    }
  }

  #region meta (reflection helpers)

  private object? ResolveObject(string? objectKind, string objectPath, string softwarePath)
  {
    if (this.IsProjectNull())
    {
      return null;
    }

    switch ((objectKind ?? string.Empty).Trim().ToLowerInvariant())
    {
      case "project":
        return this.CurrentProject;

      case "portal":
        return this._portal;

      case "device":
        return this.GetDevice(objectPath);

      case "deviceitem":
      case "device_item":
      case "device-item":
        return this.GetDeviceItem(objectPath);

      case "software":
      case "plcsoftware":
      case "plc_software":
      case "plc-software":
      {
        // PLC 优先
        var plcsw = this.GetPlcSoftware(objectPath);
        if (plcsw != null)
        {
          return plcsw;
        }

        // HMI 兜底（Unified / Classic），让 DescribeObject/InvokeObject 也能操作 HMI
        try
        {
          var sc = this.GetSoftwareContainer(objectPath);
          if (sc?.Software != null)
          {
            return sc.Software;
          }
        }
        catch
        {
          // ignored
        }

        return null;
      }

      case "hmi":
      case "hmisoftware":
      case "hmi_software":
      case "hmi-software":
      {
        try
        {
          var sc = this.GetSoftwareContainer(objectPath);
          if (sc?.Software != null)
          {
            return sc.Software;
          }
        }
        catch
        {
          // ignored
        }

        return null;
      }

      case "block":
        if (string.IsNullOrWhiteSpace(softwarePath))
        {
          return null;
        }

        return this.GetBlock(softwarePath, objectPath);

      case "type":
        if (string.IsNullOrWhiteSpace(softwarePath))
        {
          return null;
        }

        return this.GetType(softwarePath, objectPath);

      case "hmiscreen":
      case "hmi_screen":
      case "hmi-screen":
      {
        // objectPath: "HMI_RT_1:Main"
        var parts = (objectPath ?? "").Split([':',], 2);
        if (parts.Length != 2)
        {
          return null;
        }

        var swPath = parts[0];
        var screenName = parts[1];
        var sc = this.GetSoftwareContainer(swPath);
        if (sc?.Software == null)
        {
          return null;
        }

        return Portal.TryFindScreenByName(sc.Software, screenName);
      }

      case "hmitagtable":
      case "hmi_tagtable":
      case "hmi-tagtable":
      {
        // objectPath: "HMI_RT_1:默认变量表"
        var parts = (objectPath ?? "").Split([':',], 2);
        if (parts.Length != 2)
        {
          return null;
        }

        var swPath = parts[0];
        var tableName = parts[1];
        var sc = this.GetSoftwareContainer(swPath);
        if (sc?.Software == null)
        {
          return null;
        }

        return Portal.TryFindByNameInCollection(sc.Software, ["TagTables",], tableName);
      }

      case "hmitag":
      case "hmi_tag":
      case "hmi-tag":
      {
        // objectPath: "HMI_RT_1:默认变量表:StartPB"
        var parts = (objectPath ?? "").Split([':',], 3);
        if (parts.Length != 3)
        {
          return null;
        }

        var swPath = parts[0];
        var tableName = parts[1];
        var tagName = parts[2];
        var sc = this.GetSoftwareContainer(swPath);
        if (sc?.Software == null)
        {
          return null;
        }

        var table = Portal.TryFindByNameInCollection(sc.Software, ["TagTables",], tableName);
        if (table == null)
        {
          return null;
        }

        var tagsComp = table.GetType().GetProperty("Tags")?.GetValue(table);
        if (tagsComp == null)
        {
          return null;
        }

        try
        {
          if (tagsComp is IEnumerable en)
          {
            foreach (var it in en)
            {
              var n = Portal.TryGetName(it);
              if (!string.IsNullOrWhiteSpace(n) &&
                string.Equals(n!.Trim(), tagName, StringComparison.OrdinalIgnoreCase))
              {
                return it;
              }
            }
          }
        }
        catch
        {
          // ignored
        }

        // 原来这里还有一层 TryFindByNameInCollection(tagsComp, Array.Empty<string>(), ...) 的"兜底"，
        // 但该方法只遍历 propertyHints，空数组＝循环体一次不进＝恒返回 null，是死代码。
        return null;
      }

      case "hmiconnection":
      case "hmi_connection":
      case "hmi-connection":
      {
        // objectPath: "HMI_RT_1:HMI_Connection_1"
        var parts = (objectPath ?? "").Split([':',], 2);
        if (parts.Length != 2)
        {
          return null;
        }

        var swPath = parts[0];
        var connectionName = parts[1];
        var sc = this.GetSoftwareContainer(swPath);
        if (sc?.Software == null)
        {
          return null;
        }

        var conns = Portal.TryGetPropertyValue(sc.Software, "Connections");
        if (conns == null)
        {
          return null;
        }

        // 去掉 ?? TryFindByNameInCollection(conns, Array.Empty<string>(), ...)：空 hints 恒返回 null。
        return Portal.FindExistingByName(conns, connectionName);
      }

      case "hmiscreenitem":
      case "hmi_screenitem":
      case "hmi-screenitem":
      case "hmiscreen_item":
      case "hmi_screen_item":
      case "hmi-screen-item":
      {
        // objectPath: "HMI_RT_1:Main:BTN_Start"
        var parts = (objectPath ?? "").Split([':',], 3);
        if (parts.Length != 3)
        {
          return null;
        }

        var swPath = parts[0];
        var screenName = parts[1];
        var itemName = parts[2];
        var sc = this.GetSoftwareContainer(swPath);
        if (sc?.Software == null)
        {
          return null;
        }

        var screen = Portal.TryFindScreenByName(sc.Software, screenName);
        if (screen == null)
        {
          return null;
        }

        var itemsComp = screen.GetType().GetProperty("ScreenItems")?.GetValue(screen);
        if (itemsComp == null)
        {
          return null;
        }

        try
        {
          if (itemsComp is IEnumerable en)
          {
            foreach (var it in en)
            {
              var n = Portal.TryGetName(it);
              if (!string.IsNullOrWhiteSpace(n) &&
                string.Equals(n!.Trim(), itemName, StringComparison.OrdinalIgnoreCase))
              {
                return it;
              }
            }
          }
        }
        catch
        {
          // ignored
        }

        return null;
      }

      default:
        return null;
    }
  }

  private static string? TryGetName(object? o)
  {
    if (o == null)
    {
      return null;
    }

    try
    {
      var t = o.GetType();
      var p = t.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
      var v = p?.GetValue(o);
      return v?.ToString();
    }
    catch
    {
      return null;
    }
  }

  private static IEnumerable<ObjectMember> DescribeMembers(object o, int maxMembers)
  {
    var t = o.GetType();
    var list = new List<ObjectMember>();

    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
      if (p.GetIndexParameters().Length != 0)
      {
        continue;
      }

      list.Add(new ObjectMember
      {
        Kind = "Property", Name = p.Name, Type = p.PropertyType.FullName ?? p.PropertyType.Name,
      });
      if (list.Count >= maxMembers)
      {
        return list;
      }
    }

    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
    {
      if (m.IsSpecialName)
      {
        continue;
      }

      var ps = m.GetParameters();
      var sig =
        $"{m.Name}({string.Join(", ", ps.Select(x => $"{x.ParameterType.Name} {x.Name}"))}) -> {m.ReturnType.Name}";
      list.Add(new ObjectMember
      {
        Kind = "Method", Name = m.Name, Type = m.ReturnType.FullName ?? m.ReturnType.Name, Signature = sig,
      });
      if (list.Count >= maxMembers)
      {
        return list;
      }
    }

    return list;
  }

  private static object? GetPropertyPathValue(object root, string propertyPath)
  {
    var current = root;
    foreach (var part in (propertyPath ?? string.Empty).Split(['.',], StringSplitOptions.RemoveEmptyEntries))
    {
      if (current == null)
      {
        return null;
      }

      var t = current.GetType();
      var p = t.GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
      if (p == null)
      {
        return null;
      }

      if (p.GetIndexParameters().Length != 0)
      {
        return null;
      }

      current = p.GetValue(current);
    }

    return current;
  }

  public ResponseObjectDescribe DescribeObject(string objectKind, string objectPath, string softwarePath = "",
    int maxMembers = 200)
  {
    var o = this.ResolveObject(objectKind, objectPath, softwarePath);
    if (o == null)
    {
      // 「找不到」必须是失败。原来这里返回一条 Message="Object not found" 的**正常**响应：
      // 客户端看到的是 isError=false + 一张空成员表，模型据此断定「这个对象没有任何成员/属性」，
      // 而真相是路径写错了。反射桥恰恰是**用来猜路径**的工具，猜错时它必须响，
      // 否则每一次猜错都被记成一条「已确认为空」的事实，越猜越偏。
      throw new PortalException(PortalErrorCode.NotFound,
        $"{objectKind} '{objectPath}' not found. Resolve the exact path first " +
        "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetBlocksWithHierarchy); " +
        "for objectKind=Block/Type also pass softwarePath.");
    }

    return new ResponseObjectDescribe
    {
      Message = "OK",
      ObjectKind = objectKind,
      ObjectPath = objectPath,
      TypeName = o.GetType().FullName ?? o.GetType().Name,
      Members = [.. Portal.DescribeMembers(o, Math.Max(10, Math.Min(2000, maxMembers))),],
    };
  }

  public ResponseObjectDescribe DescribeObjectProperty(string objectKind, string objectPath, string propertyPath,
    string softwarePath = "", int maxMembers = 200)
  {
    var o = this.ResolveObject(objectKind, objectPath, softwarePath);
    if (o == null)
    {
      // 「找不到」必须是失败。原来这里返回一条 Message="Object not found" 的**正常**响应：
      // 客户端看到的是 isError=false + 一张空成员表，模型据此断定「这个对象没有任何成员/属性」，
      // 而真相是路径写错了。反射桥恰恰是**用来猜路径**的工具，猜错时它必须响，
      // 否则每一次猜错都被记成一条「已确认为空」的事实，越猜越偏。
      throw new PortalException(PortalErrorCode.NotFound,
        $"{objectKind} '{objectPath}' not found. Resolve the exact path first " +
        "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetBlocksWithHierarchy); " +
        "for objectKind=Block/Type also pass softwarePath.");
    }

    var v = Portal.GetPropertyPathValue(o, propertyPath);
    if (v == null)
    {
      return new ResponseObjectDescribe
      {
        Message = "Property not found",
        ObjectKind = objectKind,
        ObjectPath = $"{objectPath}.{propertyPath}",
        TypeName = null,
        Members = [],
      };
    }

    return new ResponseObjectDescribe
    {
      Message = "OK",
      ObjectKind = objectKind,
      ObjectPath = $"{objectPath}.{propertyPath}",
      TypeName = v.GetType().FullName ?? v.GetType().Name,
      Members = [.. Portal.DescribeMembers(v, Math.Max(10, Math.Min(2000, maxMembers))),],
    };
  }

  public ResponseObjectValue GetObjectProperty(string objectKind, string objectPath, string propertyPath,
    string softwarePath = "")
  {
    var o = this.ResolveObject(objectKind, objectPath, softwarePath);
    if (o == null)
    {
      // 「找不到」必须是失败。原来这里返回一条 Message="Object not found" 的**正常**响应：
      // 客户端看到的是 isError=false + 一张空成员表，模型据此断定「这个对象没有任何成员/属性」，
      // 而真相是路径写错了。反射桥恰恰是**用来猜路径**的工具，猜错时它必须响，
      // 否则每一次猜错都被记成一条「已确认为空」的事实，越猜越偏。
      throw new PortalException(PortalErrorCode.NotFound,
        $"{objectKind} '{objectPath}' not found. Resolve the exact path first " +
        "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetBlocksWithHierarchy); " +
        "for objectKind=Block/Type also pass softwarePath.");
    }

    var v = Portal.GetPropertyPathValue(o, propertyPath);
    var vt = v?.GetType();

    var outValue = v;
    if (v is IEnumerable enumerable and not string)
    {
      var items = new List<string>();
      foreach (var it in enumerable)
      {
        if (it == null)
        {
          continue;
        }

        items.Add(Portal.TryGetName(it) ?? it.ToString() ?? "");
        if (items.Count >= 200)
        {
          break;
        }
      }

      outValue = items;
    }

    return new ResponseObjectValue
    {
      Message = "OK",
      ObjectKind = objectKind,
      ObjectPath = objectPath,
      ValueType = vt?.FullName ?? v?.GetType().Name,
      Value = outValue,
    };
  }

  public ResponseObjectChildren ListObjectChildren(string objectKind, string objectPath, string collectionProperty,
    string softwarePath = "", int limit = 200)
  {
    var o = this.ResolveObject(objectKind, objectPath, softwarePath);
    if (o == null)
    {
      // 「找不到」必须是失败。原来这里返回一条 Message="Object not found" 的**正常**响应：
      // 客户端看到的是 isError=false + 一张空成员表，模型据此断定「这个对象没有任何成员/属性」，
      // 而真相是路径写错了。反射桥恰恰是**用来猜路径**的工具，猜错时它必须响，
      // 否则每一次猜错都被记成一条「已确认为空」的事实，越猜越偏。
      throw new PortalException(PortalErrorCode.NotFound,
        $"{objectKind} '{objectPath}' not found. Resolve the exact path first " +
        "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetBlocksWithHierarchy); " +
        "for objectKind=Block/Type also pass softwarePath.");
    }

    var v = Portal.GetPropertyPathValue(o, collectionProperty);
    if (v is not IEnumerable enumerable || v is string)
    {
      return new ResponseObjectChildren
      {
        Message = "Collection not found or not enumerable",
        ObjectKind = objectKind,
        ObjectPath = objectPath,
        Collection = collectionProperty,
        Items = [],
      };
    }

    var items = new List<string>();
    foreach (var it in enumerable)
    {
      if (it == null)
      {
        continue;
      }

      items.Add(Portal.TryGetName(it) ?? it.ToString() ?? "");
      if (items.Count >= Math.Max(1, Math.Min(2000, limit)))
      {
        break;
      }
    }

    return new ResponseObjectChildren
    {
      Message = "OK",
      ObjectKind = objectKind,
      ObjectPath = objectPath,
      Collection = collectionProperty,
      Items = items,
    };
  }

  private static ResponseObjectValue InvokeOnInstance(object instance, string resultKind, string resultPath,
    string methodName, JsonArray? args, bool allowWrite)
  {
    var hardDenyReason = Portal.GetHardDeniedReflectionReason(instance, resultKind, resultPath, methodName);
    if (!string.IsNullOrWhiteSpace(hardDenyReason))
    {
      return new ResponseObjectValue { Message = hardDenyReason, ObjectKind = resultKind, ObjectPath = resultPath, };
    }

    var safe = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      "ToString", "GetAttribute", "GetAttributeInfos",
    };

    if (!allowWrite && !safe.Contains(methodName))
    {
      return new ResponseObjectValue
      {
        Message = "Method not allowed (read-only mode)", ObjectKind = resultKind, ObjectPath = resultPath,
      };
    }

    var argValues = new List<object?>();
    if (args != null)
    {
      foreach (var a in args)
      {
        if (a == null)
        {
          argValues.Add(null);
          continue;
        }

        if (a is JsonValue jv)
        {
          if (jv.TryGetValue<string>(out var s))
          {
            argValues.Add(s);
            continue;
          }

          if (jv.TryGetValue<int>(out var i))
          {
            argValues.Add(i);
            continue;
          }

          if (jv.TryGetValue<long>(out var l))
          {
            argValues.Add(l);
            continue;
          }

          if (jv.TryGetValue<double>(out var d))
          {
            argValues.Add(d);
            continue;
          }

          if (jv.TryGetValue<bool>(out var b))
          {
            argValues.Add(b);
            continue;
          }

          argValues.Add(jv.ToString());
          continue;
        }

        argValues.Add(a.ToString());
      }
    }

    try
    {
      var t = instance.GetType();
      var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m =>
        !m.IsSpecialName && m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)).ToList();

      var mi = methods.FirstOrDefault(m => m.GetParameters().Length == argValues.Count);
      if (mi == null)
      {
        return new ResponseObjectValue
        {
          Message = "Method not found (signature mismatch)", ObjectKind = resultKind, ObjectPath = resultPath,
        };
      }

      var ps = mi.GetParameters();
      var converted = new object?[ps.Length];
      for (var i = 0; i < ps.Length; i++)
      {
        var av = argValues[i];
        if (av == null)
        {
          converted[i] = null;
          continue;
        }

        var pt = ps[i].ParameterType;
        if (pt == typeof(string))
        {
          converted[i] = av.ToString();
          continue;
        }

        if (pt == typeof(int))
        {
          converted[i] = Convert.ToInt32(av);
          continue;
        }

        if (pt == typeof(long))
        {
          converted[i] = Convert.ToInt64(av);
          continue;
        }

        if (pt == typeof(double))
        {
          converted[i] = Convert.ToDouble(av);
          continue;
        }

        if (pt == typeof(bool))
        {
          converted[i] = Convert.ToBoolean(av);
          continue;
        }

        if (pt == typeof(object) && methodName.Equals("SetAttribute", StringComparison.OrdinalIgnoreCase) && i == 1 &&
          argValues is [string attrName, _, ..,])
        {
          var oldValue = instance.GetType().GetMethod("GetAttribute", [typeof(string),])
            ?.Invoke(instance, [attrName,]);
          converted[i] = oldValue == null
            ? av
            : Portal.CoerceReflectionValue(av, oldValue.GetType());
          continue;
        }

        converted[i] = av;
      }

      var result = mi.Invoke(instance, converted);

      var outValue = result;
      if (result is IEnumerable enumerable and not string)
      {
        var items = new List<string>();
        foreach (var it in enumerable)
        {
          if (it == null)
          {
            continue;
          }

          items.Add(Portal.TryGetName(it) ?? it.ToString() ?? "");
          if (items.Count >= 200)
          {
            break;
          }
        }

        outValue = items;
      }

      return new ResponseObjectValue
      {
        Message = "OK",
        ObjectKind = resultKind,
        ObjectPath = resultPath,
        ValueType = result?.GetType().FullName ?? result?.GetType().Name,
        Value = outValue,
      };
    }
    catch (TargetInvocationException tie)
    {
      return new ResponseObjectValue
      {
        Message = tie.InnerException?.Message ?? tie.Message, ObjectKind = resultKind, ObjectPath = resultPath,
      };
    }
    catch (Exception ex)
    {
      return new ResponseObjectValue { Message = ex.Message, ObjectKind = resultKind, ObjectPath = resultPath, };
    }
  }

  private static string? GetHardDeniedReflectionReason(object instance, string resultKind, string resultPath,
    string methodName)
  {
    var instanceType = instance.GetType().FullName ?? instance.GetType().Name;
    var haystack = string.Join(" ", instanceType, resultKind ?? "", resultPath ?? "", methodName ?? "");

    if (haystack.IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
    {
      return
        "Denied by safety policy: force-table and force-related operations are not exposed through this MCP server.";
    }

    var isOnlineMonitorSurface = haystack.IndexOf("Online", StringComparison.OrdinalIgnoreCase) >= 0 ||
      haystack.IndexOf("Monitor", StringComparison.OrdinalIgnoreCase) >= 0 ||
      haystack.IndexOf("Watch", StringComparison.OrdinalIgnoreCase) >= 0;

    if (!isOnlineMonitorSurface)
    {
      return null;
    }

    var mutatingPrefixes = new[]
    {
      "Set", "Write", "Create", "Delete", "Remove", "Import", "Add", "Insert", "Update", "Modify", "GoOnline",
      "GoOffline", "Download", "Activate", "Start", "Stop",
    };

    if (mutatingPrefixes.Any(p => (methodName ?? "").StartsWith(p, StringComparison.OrdinalIgnoreCase)))
    {
      return
        "Denied by safety policy: online/watch/monitor surfaces are read-only. The MCP server may read current status only and must not modify watch-table objects or PLC values.";
    }

    return null;
  }

  public ResponseObjectValue InvokeObject(string objectKind, string objectPath, string methodName,
    JsonArray? args = null, string softwarePath = "", bool allowWrite = false)
  {
    var o = this.ResolveObject(objectKind, objectPath, softwarePath);
    if (o == null)
    {
      // 「找不到」必须是失败。原来这里返回一条 Message="Object not found" 的**正常**响应：
      // 客户端看到的是 isError=false + 一张空成员表，模型据此断定「这个对象没有任何成员/属性」，
      // 而真相是路径写错了。反射桥恰恰是**用来猜路径**的工具，猜错时它必须响，
      // 否则每一次猜错都被记成一条「已确认为空」的事实，越猜越偏。
      throw new PortalException(PortalErrorCode.NotFound,
        $"{objectKind} '{objectPath}' not found. Resolve the exact path first " +
        "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetBlocksWithHierarchy); " +
        "for objectKind=Block/Type also pass softwarePath.");
    }

    return Portal.InvokeOnInstance(o, objectKind, objectPath, methodName, args, allowWrite);
  }

  private static Type? FindTypeBySuffix(string typeSuffix)
  {
    if (string.IsNullOrWhiteSpace(typeSuffix))
    {
      return null;
    }

    var suf = typeSuffix.Trim();

    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
    {
      Type[] types;
      try
      {
        types = asm.GetTypes();
      }
      catch
      {
        continue;
      }

      foreach (var t in types)
      {
        var n = t.FullName ?? t.Name;
        if (n.EndsWith(suf, StringComparison.OrdinalIgnoreCase) ||
          t.Name.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
        {
          return t;
        }
      }
    }

    return null;
  }

  private static object? TryGetService(object target, Type serviceType)
  {
    try
    {
      var t = target.GetType();
      var mi = t.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m =>
        m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
      if (mi == null)
      {
        return null;
      }

      var g = mi.MakeGenericMethod(serviceType);
      return g.Invoke(target, null);
    }
    catch
    {
      return null;
    }
  }

  public ResponseObjectDescribe DescribeService(string objectKind, string objectPath, string serviceTypeSuffix,
    string softwarePath = "", int maxMembers = 200)
  {
    if ((serviceTypeSuffix ?? string.Empty).IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
    {
      return new ResponseObjectDescribe
      {
        Message = "Denied by safety policy: force-related services are not exposed through this MCP server.",
        ObjectKind = objectKind,
        ObjectPath = objectPath,
        TypeName = null,
        Members = [],
      };
    }

    var o = this.ResolveObject(objectKind, objectPath, softwarePath);
    if (o == null)
    {
      // 「找不到」必须是失败。原来这里返回一条 Message="Object not found" 的**正常**响应：
      // 客户端看到的是 isError=false + 一张空成员表，模型据此断定「这个对象没有任何成员/属性」，
      // 而真相是路径写错了。反射桥恰恰是**用来猜路径**的工具，猜错时它必须响，
      // 否则每一次猜错都被记成一条「已确认为空」的事实，越猜越偏。
      throw new PortalException(PortalErrorCode.NotFound,
        $"{objectKind} '{objectPath}' not found. Resolve the exact path first " +
        "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetBlocksWithHierarchy); " +
        "for objectKind=Block/Type also pass softwarePath.");
    }

    var st = Portal.FindTypeBySuffix(serviceTypeSuffix!);
    if (st == null)
    {
      return new ResponseObjectDescribe
      {
        Message = "Service type not found",
        ObjectKind = objectKind,
        ObjectPath = objectPath,
        TypeName = null,
        Members = [],
      };
    }

    var svc = Portal.TryGetService(o, st);
    if (svc == null)
    {
      return new ResponseObjectDescribe
      {
        Message = "GetService failed (service not available for this object)",
        ObjectKind = objectKind,
        ObjectPath = objectPath,
        TypeName = st.FullName ?? st.Name,
        Members = [],
      };
    }

    return new ResponseObjectDescribe
    {
      Message = "OK",
      ObjectKind = "Service",
      ObjectPath = $"{objectKind}:{objectPath}::{serviceTypeSuffix}",
      TypeName = svc.GetType().FullName ?? svc.GetType().Name,
      Members = [.. Portal.DescribeMembers(svc, Math.Max(10, Math.Min(2000, maxMembers))),],
    };
  }

  public ResponseObjectValue InvokeService(string objectKind, string objectPath, string serviceTypeSuffix,
    string methodName, JsonArray? args = null, string softwarePath = "", bool allowWrite = false)
  {
    if ((serviceTypeSuffix ?? string.Empty).IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
    {
      return new ResponseObjectValue
      {
        Message = "Denied by safety policy: force-related services are not exposed through this MCP server.",
        ObjectKind = objectKind,
        ObjectPath = objectPath,
      };
    }

    var o = this.ResolveObject(objectKind, objectPath, softwarePath);
    if (o == null)
    {
      // 「找不到」必须是失败。原来这里返回一条 Message="Object not found" 的**正常**响应：
      // 客户端看到的是 isError=false + 一张空成员表，模型据此断定「这个对象没有任何成员/属性」，
      // 而真相是路径写错了。反射桥恰恰是**用来猜路径**的工具，猜错时它必须响，
      // 否则每一次猜错都被记成一条「已确认为空」的事实，越猜越偏。
      throw new PortalException(PortalErrorCode.NotFound,
        $"{objectKind} '{objectPath}' not found. Resolve the exact path first " +
        "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetBlocksWithHierarchy); " +
        "for objectKind=Block/Type also pass softwarePath.");
    }

    var st = Portal.FindTypeBySuffix(serviceTypeSuffix!);
    if (st == null)
    {
      return new ResponseObjectValue
      {
        Message = "Service type not found", ObjectKind = objectKind, ObjectPath = objectPath,
      };
    }

    var svc = Portal.TryGetService(o, st);
    if (svc == null)
    {
      return new ResponseObjectValue
      {
        Message = "GetService failed (service not available for this object)",
        ObjectKind = objectKind,
        ObjectPath = objectPath,
      };
    }

    var svcPath = $"{objectKind}:{objectPath}::{serviceTypeSuffix}";
    return Portal.InvokeOnInstance(svc, "Service", svcPath, methodName, args, allowWrite);
  }

  #endregion

  #endregion

  #endregion
}
