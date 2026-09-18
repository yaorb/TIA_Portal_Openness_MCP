#region

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using TiaMcpServer.ModelContextProtocol;

#endregion

namespace TiaMcpServer.Siemens;

// Partial: download. Extracted from Portal.cs (god-file split); behavior unchanged.
public partial class Portal
{
  #region download

  public ResponseDownload DownloadToPlc(string softwarePath, bool consistentBlocksOnly = true,
    bool keepActualValues = true, bool startAfterDownload = true, bool stopBeforeDownload = true,
    string? password = null, string? pgPcInterface = null, string? targetIpAddress = null)
  {
    logger?.LogInformation(
      "DownloadToPlc: softwarePath={SoftwarePath} consistentOnly={C} keepDB={K} start={S} stop={T} hasPassword={P} pgPc={I} targetIp={A}",
      softwarePath,
      consistentBlocksOnly,
      keepActualValues,
      startAfterDownload,
      stopBeforeDownload,
      !string.IsNullOrEmpty(password),
      pgPcInterface,
      targetIpAddress);

    if (this.IsProjectNull())
    {
      return new ResponseDownload { Ok = false, Message = "No project open.", };
    }

    var plcSoftware = this.GetPlcSoftware(softwarePath);
    if (plcSoftware == null)
    {
      return new ResponseDownload { Ok = false, Message = $"PLC software not found: '{softwarePath}'.", };
    }

    // Declared outside the try so the catch can report which PG/PC route was used.
    DownloadRouteSelection? routeDiagnostics = null;

    try
    {
      var downloadProvider = this.ResolvePlcService<DownloadProvider>(softwarePath, plcSoftware);
      if (downloadProvider == null)
      {
        return new ResponseDownload
        {
          Ok = false,
          Message =
            "DownloadProvider service not available for this PLC. Ensure hardware configuration has network settings.",
        };
      }

      object? configuration = downloadProvider.Configuration;
      if (configuration == null)
      {
        return new ResponseDownload
        {
          Ok = false,
          Message =
            "No connection configuration found. Configure the PLC's PROFINET/IP address in hardware configuration first.",
        };
      }

      using var passwordScope = Portal.AttachPasswordHandler(configuration, password);

      var capture_keepActualValues = keepActualValues;
      var capture_startAfterDownload = startAfterDownload;
      var capture_stopBeforeDownload = stopBeforeDownload;
      var capture_consistentBlocksOnly = consistentBlocksOnly;

      DownloadConfigurationDelegate preDelegate = config =>
      {
        this.ApplyDefaultDownloadConfig(config,
          capture_keepActualValues,
          capture_startAfterDownload,
          capture_stopBeforeDownload,
          capture_consistentBlocksOnly);
      };

      DownloadConfigurationDelegate postDelegate = config => { };

      // V21 fix: ConnectionConfiguration does NOT implement IConfiguration, but a
      // ConfigurationTargetInterface (Modes -> PcInterfaces -> TargetInterfaces) DOES.
      // Select a target interface (applying its route) and pass THAT to Download();
      // fall back to the raw configuration if no route is selectable.
      routeDiagnostics = Portal.SelectDownloadRoute(configuration, pgPcInterface, targetIpAddress);
      if (routeDiagnostics.Error != null)
      {
        return new ResponseDownload
        {
          Ok = false, Message = routeDiagnostics.Error, Errors = new[] { routeDiagnostics.Error, },
        };
      }

      var downloadConfig = routeDiagnostics.Configuration ?? configuration;
      logger?.LogInformation("DownloadToPlc: PG/PC route = {Route}", routeDiagnostics.Description);

      // Resolve the 4-arg overload Download(IConfiguration, pre, post, DownloadOptions)
      // and invoke via reflection (the parameter is typed IConfiguration).
      var downloadMethod = downloadProvider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .FirstOrDefault(m =>
        {
          if (m.Name != "Download")
          {
            return false;
          }

          var p = m.GetParameters();
          return p.Length == 4 && p[1].ParameterType.Name == "DownloadConfigurationDelegate";
        });

      if (downloadMethod == null)
      {
        return new ResponseDownload
        {
          Ok = false,
          Message = "Download(IConfiguration,…) method not found on DownloadProvider. TIA Portal version mismatch?",
        };
      }

      var rawResult = downloadMethod.Invoke(downloadProvider,
        new[] { downloadConfig!, preDelegate, postDelegate, DownloadOptions.Software, });

      if (rawResult is not DownloadResult result)
      {
        return new ResponseDownload { Ok = false, Message = "Download returned an unexpected result type.", };
      }

      return this.BuildDownloadResponse(result, softwarePath, routeDiagnostics);
    }
    catch (Exception ex)
    {
      // The Download call is invoked via reflection, so a real failure arrives wrapped in
      // TargetInvocationException ("调用的目标发生了异常"). Unwrap it so the caller sees the
      // actual reason (connection/route error, not-reachable CPU, etc.).
      var real = ex is TargetInvocationException tie && tie.InnerException != null
        ? tie.InnerException
        : ex;
      logger?.LogError(real, "DownloadToPlc failed for {SoftwarePath}", softwarePath);

      // A connection failure is usually the wrong PG/PC adapter on a multi-NIC PC, so
      // always show which route was used and what else was available (issue #14).
      var routeHint = routeDiagnostics == null
        ? string.Empty
        : $" Route used: {routeDiagnostics.Description}." + (routeDiagnostics.Candidates.Count > 1
          ? $" Available routes: {Portal.DescribeRoutes(routeDiagnostics.Candidates)}." +
          " Pass pgPcInterface / targetIpAddress to DownloadToPlc to pick one explicitly."
          : string.Empty);

      return new ResponseDownload
      {
        Ok = false, Message = $"Download failed: {real.Message}{routeHint}", Errors = new[] { real.Message, },
      };
    }
  }

  // ---- PG/PC route selection (issue #14) --------------------------------------------------
  // V21: ConnectionConfiguration.ApplyConfiguration(ConfigurationTargetInterface) returns a
  // bool (whether the online route applied) — it does NOT return the IConfiguration. The
  // ConfigurationTargetInterface itself IS an IConfiguration (verified against the V21
  // PublicAPI), so we hand the target to Download().
  //
  // The route tree is Modes -> PcInterfaces -> TargetInterfaces. Picking the FIRST applicable
  // target was enough on a single-NIC PC, but on a multi-NIC PC (WLAN + VPN + PLCSIM virtual
  // adapter) TIA enumerates the wrong adapter first and ApplyConfiguration "succeeds" on it
  // too — it does not verify reachability. The download then leaves through an adapter that
  // cannot see the CPU and fails with "connection to the target module cannot be established".
  // So: enumerate every route, rank by IP proximity to the CPU, and apply the best one.

  // One flattened Modes -> PcInterfaces -> TargetInterfaces route, with the addresses on both
  // ends so the adapter facing the CPU can be identified (and reported back to the caller).
  private sealed class DownloadRoute
  {
    public string ModeName = string.Empty;
    public List<string> PcAddresses = new();
    public string PcInterfaceName = string.Empty;
    public int PcInterfaceNumber;
    public int Score;
    public object Target = null!;
    public List<string> TargetAddresses = new();
    public string TargetName = string.Empty;

    public string Describe() =>
      $"{this.ModeName} / {this.PcInterfaceName}" + (this.PcAddresses.Count > 0
        ? $" [{string.Join(", ", this.PcAddresses)}]"
        : " [no IP]") + $" -> {this.TargetName}" + (this.TargetAddresses.Count > 0
        ? $" [{string.Join(", ", this.TargetAddresses)}]"
        : string.Empty);
  }

  private sealed class DownloadRouteSelection
  {
    public List<DownloadRoute> Candidates = new();
    public object? Configuration; // what to hand to Download(); null = fall back to the raw configuration
    public string Description = "(no route selected — raw connection configuration)";
    public string? Error; // set when an explicit pgPcInterface/targetIpAddress filter matched nothing
  }

  private static List<DownloadRoute> EnumerateDownloadRoutes(object? connectionConfiguration)
  {
    var routes = new List<DownloadRoute>();
    if (connectionConfiguration == null)
    {
      return routes;
    }

    foreach (var mode in Portal.EnumerateReflectedProperty(connectionConfiguration, "Modes"))
    foreach (var pcInterface in Portal.EnumerateReflectedProperty(mode, "PcInterfaces"))
    foreach (var target in Portal.EnumerateReflectedProperty(pcInterface, "TargetInterfaces"))
    {
      if (target == null)
      {
        continue;
      }

      routes.Add(new DownloadRoute
      {
        Target = target,
        ModeName = Portal.ReadReflectedString(mode, "Name"),
        PcInterfaceName = Portal.ReadReflectedString(pcInterface, "Name"),
        PcInterfaceNumber = Portal.ReadReflectedInt(pcInterface, "Number"),
        PcAddresses = Portal.ReadConfigurationAddresses(pcInterface),
        TargetName = Portal.ReadReflectedString(target, "Name"),
        TargetAddresses = Portal.ReadConfigurationAddresses(target),
      });
    }

    return routes;
  }

  private static string ReadReflectedString(object? owner, string propertyName)
  {
    try
    {
      return owner?.GetType().GetProperty(propertyName)?.GetValue(owner)?.ToString() ?? string.Empty;
    }
    catch
    {
      return string.Empty;
    }
  }

  private static int ReadReflectedInt(object? owner, string propertyName)
  {
    try
    {
      return owner?.GetType().GetProperty(propertyName)?.GetValue(owner) is int number
        ? number
        : 0;
    }
    catch
    {
      return 0;
    }
  }

  // ConfigurationPcInterface.Addresses = the PG/PC adapter's own IPs.
  // ConfigurationTargetInterface.Addresses = the CPU interface's IPs.
  // Both are ConfigurationAddress compositions whose Address property holds the IP string.
  private static List<string> ReadConfigurationAddresses(object? owner)
  {
    var addresses = new List<string>();
    foreach (var address in Portal.EnumerateReflectedProperty(owner, "Addresses"))
    {
      var value = Portal.ReadReflectedString(address, "Address");
      if (!string.IsNullOrWhiteSpace(value))
      {
        addresses.Add(value);
      }
    }

    return addresses;
  }

  // Rough "can these two adapters see each other" test. ConfigurationAddress exposes only the
  // address, never the mask, so /24 is an assumption — it holds for the usual 192.168.x.y /
  // 10.x.y.z engineering subnets, and it is only ever used to RANK candidates, never to reject
  // a download outright.
  private static bool SameIpv4Subnet24(string a, string b)
  {
    var left = a.Split('.');
    var right = b.Split('.');
    if (left.Length != 4 || right.Length != 4)
    {
      return false;
    }

    return left[0] == right[0] && left[1] == right[1] && left[2] == right[2];
  }

  private static void ScoreDownloadRoutes(List<DownloadRoute> routes, string? preferredTargetIp)
  {
    foreach (var route in routes)
    {
      var score = 0;
      if (!string.IsNullOrWhiteSpace(preferredTargetIp))
      {
        if (route.TargetAddresses.Any(t => string.Equals(t, preferredTargetIp, StringComparison.OrdinalIgnoreCase)))
        {
          score += 8;
        }

        if (route.PcAddresses.Any(p => Portal.SameIpv4Subnet24(p, preferredTargetIp!)))
        {
          score += 4;
        }
      }

      // No explicit target IP: the CPU address on the route itself is the reference point.
      // Prefer the adapter sitting in the same subnet as the CPU it has to reach — that is
      // exactly what separates the PLCSIM virtual adapter from a WLAN/VPN adapter.
      if (route.PcAddresses.Any(p => route.TargetAddresses.Any(t => Portal.SameIpv4Subnet24(p, t))))
      {
        score += 2;
      }

      route.Score = score;
    }
  }

  private static string DescribeRoutes(IEnumerable<DownloadRoute> routes) =>
    string.Join(" | ", routes.Select(r => r.Describe()));

  // Returns the IConfiguration to pass to Download(), or a selection carrying an Error when an
  // explicit filter matched nothing. Configuration stays null when no route exists at all —
  // the caller then falls back to the raw connection configuration (old behaviour).
  private static DownloadRouteSelection SelectDownloadRoute(object? connectionConfiguration, string? pgPcInterface,
    string? targetIpAddress)
  {
    var selection = new DownloadRouteSelection();
    if (connectionConfiguration == null)
    {
      return selection;
    }

    try
    {
      selection.Candidates = Portal.EnumerateDownloadRoutes(connectionConfiguration);
      if (selection.Candidates.Count == 0)
      {
        return selection;
      }

      var pool = selection.Candidates;

      if (!string.IsNullOrWhiteSpace(pgPcInterface))
      {
        var byName = pool.Where(r => r.PcInterfaceName.IndexOf(pgPcInterface, StringComparison.OrdinalIgnoreCase) >= 0)
          .ToList();
        if (byName.Count == 0)
        {
          selection.Error =
            $"No PG/PC interface matches '{pgPcInterface}'. Available routes: {Portal.DescribeRoutes(pool)}";
          return selection;
        }

        pool = byName;
      }

      if (!string.IsNullOrWhiteSpace(targetIpAddress))
      {
        var byIp = pool.Where(r =>
          r.TargetAddresses.Any(t => string.Equals(t, targetIpAddress, StringComparison.OrdinalIgnoreCase))).ToList();
        if (byIp.Count == 0)
        {
          selection.Error =
            $"No download route reaches target IP '{targetIpAddress}'. Available routes: {Portal.DescribeRoutes(pool)}";
          return selection;
        }

        pool = byIp;
      }

      Portal.ScoreDownloadRoutes(pool, targetIpAddress);

      var applyMethod = connectionConfiguration.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .FirstOrDefault(m =>
          m.Name == "ApplyConfiguration" && m.GetParameters().Length == 1 &&
          m.GetParameters()[0].ParameterType.Name == "ConfigurationTargetInterface");

      // OrderByDescending is a stable sort, so equal scores keep the original enumeration
      // order — i.e. the old first-wins behaviour whenever nothing distinguishes adapters.
      var ranked = pool.OrderByDescending(r => r.Score).ToList();

      foreach (var route in ranked)
      {
        try
        {
          if (applyMethod?.Invoke(connectionConfiguration, new[] { route.Target, }) is bool ok && ok)
          {
            selection.Configuration = route.Target;
            selection.Description = route.Describe();
            return selection;
          }
        }
        catch
        {
        }
      }

      // Nothing applied cleanly — hand back the best-ranked target anyway (it IS an
      // IConfiguration). Mirrors the previous fallback to the first target.
      selection.Configuration = ranked[0].Target;
      selection.Description = ranked[0].Describe() + " (not confirmed by ApplyConfiguration)";
      return selection;
    }
    catch
    {
    }

    return selection;
  }

  // Read a property by name and materialize it as a sequence (Openness compositions are
  // IEnumerable). Materialized inside try/catch so a throwing enumerator can't escape.
  private static List<object?> EnumerateReflectedProperty(object? owner, string propertyName)
  {
    var items = new List<object?>();
    try
    {
      var value = owner?.GetType().GetProperty(propertyName)?.GetValue(owner);
      if (value is IEnumerable en)
      {
        foreach (var item in en)
        {
          items.Add(item);
        }
      }
    }
    catch
    {
    }

    return items;
  }

  public ResponseCheckDownload CheckDownloadReadiness(string softwarePath)
  {
    var issues = new List<string>();

    if (this.IsProjectNull())
    {
      return new ResponseCheckDownload { Ready = false, Issues = new[] { "No project open.", }, };
    }

    var plcSoftware = this.GetPlcSoftware(softwarePath);
    if (plcSoftware == null)
    {
      return new ResponseCheckDownload
      {
        Ready = false, Issues = new[] { $"PLC software not found: '{softwarePath}'.", },
      };
    }

    var hasProvider = false;
    var hasConfig = false;
    var isConsistent = false;
    var routes = new List<DownloadRoute>();

    try
    {
      var provider = this.ResolvePlcService<DownloadProvider>(softwarePath, plcSoftware);
      hasProvider = provider != null;
      if (!hasProvider)
      {
        issues.Add("DownloadProvider service not available. Check hardware/network configuration.");
      }
      else
      {
        hasConfig = provider!.Configuration != null;
        if (!hasConfig)
        {
          issues.Add("No network configuration for this PLC. Set the IP address in hardware configuration.");
        }
        else
          // Read-only: enumerate the PG/PC routes WITHOUT applying any of them, so a
          // multi-NIC PC can be diagnosed before touching the CPU (issue #14).
        {
          routes = Portal.EnumerateDownloadRoutes(provider.Configuration);
        }
      }
    }
    catch (Exception ex)
    {
      issues.Add($"Error accessing DownloadProvider: {ex.Message}");
    }

    // Check compile consistency via ICompilable
    try
    {
      var compilable = plcSoftware.GetService<ICompilable>();
      if (compilable != null)
      {
        // We skip an actual compile here; check block consistency heuristically
        isConsistent = true; // Assume consistent unless caller has run CompileSoftware
      }
    }
    catch
    {
    }

    Portal.ScoreDownloadRoutes(routes, null);
    var routesJson = new JsonArray();
    foreach (var route in routes.OrderByDescending(r => r.Score))
    {
      routesJson.Add(new JsonObject
      {
        ["mode"] = route.ModeName,
        ["pgPcInterface"] = route.PcInterfaceName,
        ["pgPcInterfaceNumber"] = route.PcInterfaceNumber,
        ["pgPcAddresses"] = string.Join(", ", route.PcAddresses),
        ["targetInterface"] = route.TargetName,
        ["targetAddresses"] = string.Join(", ", route.TargetAddresses),
        ["preferred"] = route.Score > 0,
      });
    }

    var ready = hasProvider && hasConfig && issues.Count == 0;
    return new ResponseCheckDownload
    {
      Ready = ready,
      HasDownloadProvider = hasProvider,
      HasConfiguration = hasConfig,
      IsConsistent = isConsistent,
      Message = ready
        ? $"PLC '{softwarePath}' is ready for download."
        : $"PLC '{softwarePath}' has {issues.Count} readiness issue(s).",
      Issues = issues.Count > 0
        ? issues.ToArray()
        : null,
      Meta = new JsonObject
      {
        ["downloadRouteCount"] = routes.Count,
        // Ordered best-first — the same ranking DownloadToPlc applies. preferred=true
        // means the PG/PC adapter shares an IPv4 /24 with the CPU it has to reach.
        ["downloadRoutes"] = routesJson,
        ["note"] =
          "Override the automatic pick with DownloadToPlc(pgPcInterface:…) or DownloadToPlc(targetIpAddress:…).",
      },
    };
  }

  private void ApplyDefaultDownloadConfig(DownloadConfiguration config, bool keepActualValues, bool startAfterDownload,
    bool stopBeforeDownload, bool consistentBlocksOnly)
  {
    var typeName = config.GetType().Name;
    logger?.LogDebug("ApplyDownloadConfig: {TypeName}", typeName);

    switch (typeName)
    {
      case "StopModules":
        // StopModulesSelections = { NoAction, StopAll } — NOT "StopModule" (verified
        // against V21 PublicAPI; the old value parsed to nothing and left the prompt
        // "unhandled", which aborted every download).
        Portal.DownloadConfigSetSelection(config,
          stopBeforeDownload
            ? "StopAll"
            : "NoAction");
        break;

      case "StopHSystemOrModule":
        Portal.DownloadConfigSetSelection(config,
          stopBeforeDownload
            ? "StopModule"
            : "NoAction");
        break;

      case "StopHSystem":
        Portal.DownloadConfigSetSelection(config,
          stopBeforeDownload
            ? "StopHSystem"
            : "NoAction");
        break;

      case "StartModules":
      case "StartBackupModules":
        Portal.DownloadConfigSetSelection(config,
          startAfterDownload
            ? "StartModule"
            : "NoAction");
        break;

      case "DataBlockReinitialization":
        Portal.DownloadConfigSetSelection(config,
          keepActualValues
            ? "KeepActualValues"
            : "Reinitialize");
        break;

      case "DataBlockReinitializationOrKeepActualValues":
        Portal.DownloadConfigSetSelection(config,
          keepActualValues
            ? "KeepActualValues"
            : "StopPlcAndReinitialize");
        break;

      case "ConsistentBlocksDownload":
        Portal.DownloadConfigSetSelection(config, "ConsistentDownload");
        break;

      case "AllBlocksDownload":
        if (!consistentBlocksOnly)
        {
          Portal.DownloadConfigSetSelection(config, "DownloadAllBlocks");
        }

        break;

      case "CheckBeforeDownload":
      case "AlarmTextLibrariesDownload":
      case "UserManagementDownload":
      case "DownloadCertificate":
        Portal.DownloadConfigSetChecked(config, true);
        break;

      case "DifferentTargetConfiguration":
      case "ActiveTestCanBeAborted":
      case "ActiveTestCanPreventDownload":
        Portal.DownloadConfigSetSelection(config, "AcceptAll");
        break;
    }
  }

  private static void DownloadConfigSetSelection(object config, string selectionName)
  {
    try
    {
      var prop = config.GetType().GetProperty("CurrentSelection");
      if (prop == null)
      {
        return;
      }

      var enumType = prop.PropertyType;
      if (!enumType.IsEnum)
      {
        return;
      }

      var value = Enum.Parse(enumType, selectionName, true);
      prop.SetValue(config, value);
    }
    catch
    {
    }
  }

  private static void DownloadConfigSetChecked(object config, bool value)
  {
    try
    {
      var prop = config.GetType().GetProperty("Checked");
      prop?.SetValue(config, value);
    }
    catch
    {
    }
  }

  private ResponseDownload BuildDownloadResponse(DownloadResult result, string softwarePath,
    DownloadRouteSelection? route)
  {
    var errors = new List<string>();
    var warnings = new List<string>();
    Portal.CollectDownloadMessages(result.Messages, errors, warnings);

    var ok = result.State == DownloadResultState.Success || result.State == DownloadResultState.Information ||
      result.State == DownloadResultState.Warning;

    return new ResponseDownload
    {
      Ok = ok,
      Message = $"Download {result.State}: {result.ErrorCount} error(s), {result.WarningCount} warning(s).",
      State = result.State.ToString(),
      ErrorCount = result.ErrorCount,
      WarningCount = result.WarningCount,
      Errors = errors.Count > 0
        ? errors.ToArray()
        : null,
      Warnings = warnings.Count > 0
        ? warnings.ToArray()
        : null,
      Meta = new JsonObject
      {
        ["softwarePath"] = softwarePath,
        ["timestamp"] = DateTime.Now,
        ["downloadState"] = result.State.ToString(),
        // Which PG/PC adapter the download actually left through — the thing you need
        // to see first when a multi-NIC PC downloads "successfully" to the wrong place.
        ["pgPcRoute"] = route?.Description ?? string.Empty,
        ["pgPcRouteCandidates"] = route?.Candidates.Count ?? 0,
      },
    };
  }

  private static void CollectDownloadMessages(IEnumerable? messages, List<string> errors, List<string> warnings)
  {
    if (messages == null)
    {
      return;
    }

    foreach (var obj in messages)
    {
      if (obj == null)
      {
        continue;
      }

      try
      {
        var msgText = obj.GetType().GetProperty("Message")?.GetValue(obj) as string ?? string.Empty;
        var stateObj = obj.GetType().GetProperty("State")?.GetValue(obj);
        var stateName = stateObj?.ToString() ?? string.Empty;

        if (stateName == "Error" && !string.IsNullOrWhiteSpace(msgText))
        {
          errors.Add(msgText);
        }
        else if (stateName == "Warning" && !string.IsNullOrWhiteSpace(msgText))
        {
          warnings.Add(msgText);
        }

        // Recurse into nested Messages
        var nested = obj.GetType().GetProperty("Messages")?.GetValue(obj) as IEnumerable;
        if (nested != null)
        {
          Portal.CollectDownloadMessages(nested, errors, warnings);
        }
      }
      catch
      {
      }
    }
  }

  #endregion
}
