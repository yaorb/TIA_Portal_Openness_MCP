#region

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32;

#endregion

namespace TiaMcpServer.Siemens;

// Manual Siemens.Engineering.dll resolve
public static class Engineering
{
  public static int TiaMajorVersion { get; set; }

  // Optional explicit override (CLI --tia-portal-location), e.g. D:\app\TIA20\Portal V20.
  // Takes precedence over TiaPortalLocation env var and registry lookup.
  public static string? TiaPortalLocationOverride { get; set; }

  // When true, launch TIA Portal with its full GUI (slower cold start, allows visual inspection).
  // Default false = headless (WithoutUserInterface), which starts much faster. Set via --with-ui.
  // Lives here (not on Portal) because Program.Main must set it without forcing the CLR to load the
  // Portal type — Portal's Siemens.Engineering field types would be needed before Resolver is wired up.
  public static bool LaunchWithUserInterface { get; set; } = false;

  public static Assembly? Resolver(object sender, ResolveEventArgs args)
  {
    var assemblyName = new AssemblyName(args.Name);
    if (!assemblyName.Name.StartsWith("Siemens.Engineering"))
    {
      return null;
    }

    var tiaInstallPath = Engineering.GetTiaPortalInstallPath();
    if (string.IsNullOrEmpty(tiaInstallPath))
    {
      throw new InvalidOperationException(
        $"Could not find TIA Portal installation path for version {Engineering.TiaMajorVersion} in the registry.");
    }

    var tiaMajorVersionString = Engineering.TiaMajorVersion.ToString();
    var searchDirectories = new[]
    {
      Path.Combine(tiaInstallPath, "PublicAPI", $"V{tiaMajorVersionString}"),
      Path.Combine(tiaInstallPath, "Bin", "PublicAPI"),
    };

    // IEnumerable without given majorVersionString
    var excludedTiaMajorVersions = new[] { "V13", "V14", "V15", "V16", "V17", "V18", "V19", "V20", }
      .Where(v => v != $"V{tiaMajorVersionString}").ToList();

    foreach (var dir in searchDirectories)
    {
      var assemblyPath = Engineering.FindAssemblyRecursive(dir, assemblyName.Name + ".dll", excludedTiaMajorVersions);
      if (assemblyPath != null)
      {
        return Assembly.LoadFrom(assemblyPath);
      }
    }

    throw new FileNotFoundException(
      $"Could not find DLL '{assemblyName.Name}' for TIA Portal version {Engineering.TiaMajorVersion} in the installation directories.");
  }

  /// <summary>
  ///   Detects the highest installed TIA Portal major version without requiring a CLI flag.
  ///   Detection order:
  ///   1. TiaPortalLocation env var — extract version from path (e.g. "Portal V21" → 21)
  ///   2. Registry: HKLM\SOFTWARE\Siemens\Automation\_InstalledSW\TIAP*\TIA_Opns
  ///   3. Registry: HKLM\SOFTWARE\Siemens\Automation\Openness\* — the Openness registration,
  ///      which also covers installs outside %ProgramFiles% (see OpennessRegistrations)
  ///   4. Filesystem: C:\Program Files\Siemens\Automation\Portal V*
  ///   Returns the highest found version, or null if nothing detected.
  /// </summary>
  public static int? DetectTiaMajorVersion()
  {
    var candidates = new List<int>();

    // 0. Explicit override (CLI --tia-portal-location)
    if (!string.IsNullOrWhiteSpace(Engineering.TiaPortalLocationOverride))
    {
      var m = Regex.Match(Engineering.TiaPortalLocationOverride, @"[Vv](\d{2})", RegexOptions.RightToLeft);
      if (m.Success && int.TryParse(m.Groups[1].Value, out var ovVer))
      {
        candidates.Add(ovVer);
      }
    }

    // 1. TiaPortalLocation env var
    var env = Environment.GetEnvironmentVariable("TiaPortalLocation");
    if (!string.IsNullOrWhiteSpace(env))
    {
      var match = Regex.Match(env, @"[Vv](\d{2})", RegexOptions.RightToLeft);
      if (match.Success && int.TryParse(match.Groups[1].Value, out var envVer))
      {
        candidates.Add(envVer);
      }
    }

    // 2. Registry scan
    try
    {
      using var regBase = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
      using var installedSw = regBase.OpenSubKey(@"SOFTWARE\Siemens\Automation\_InstalledSW");
      if (installedSw != null)
      {
        foreach (var subName in installedSw.GetSubKeyNames())
        {
          // Key names follow pattern TIAP21, TIAP20, etc.
          var numMatch = Regex.Match(subName, @"TIAP(\d+)", RegexOptions.IgnoreCase);
          if (!numMatch.Success || !int.TryParse(numMatch.Groups[1].Value, out var regVer))
          {
            continue;
          }

          using var opnsKey = installedSw.OpenSubKey(subName + @"\TIA_Opns");
          if (opnsKey?.GetValue("Path") is string path && Directory.Exists(path))
          {
            candidates.Add(regVer);
          }
        }
      }
    }
    catch
    {
      // 注册表里没有这一项（或读不到）就换下一个候选，属正常探测
    }

    // 2b. Openness registration — the only source that still sees an install on ANOTHER DRIVE.
    //     Measured on a machine with V18 under C:\Program Files and V21 under E:\:
    //     _InstalledSW\TIAP21\TIA_Opns carried no Path value at all, and the filesystem scan
    //     below only looks under %ProgramFiles%, so detection answered "V18" — and the exe built
    //     for V21 then died loading Siemens.Engineering.Base. The registration key named the right
    //     folder the whole time. See OpennessRegistrations().
    foreach (var reg in Engineering.OpennessRegistrations())
    {
      // The API major is the version this folder can actually serve; the key major is the TIA
      // that registered it. Both are evidence that a TIA of that major is installed here.
      candidates.Add(reg.ApiMajor);
      candidates.Add(reg.KeyMajor);
    }

    // 3. Filesystem scan
    try
    {
      var siemensRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Siemens",
        "Automation");
      if (Directory.Exists(siemensRoot))
      {
        foreach (var dir in Directory.GetDirectories(siemensRoot, "Portal V*"))
        {
          var dirMatch = Regex.Match(dir, @"Portal V(\d+)", RegexOptions.IgnoreCase);
          if (dirMatch.Success && int.TryParse(dirMatch.Groups[1].Value, out var fsVer))
          {
            candidates.Add(fsVer);
          }
        }
      }
    }
    catch
    {
      // 目录名解析不出 Portal V版本 就跳过该项，候选列表仍可用
    }

    return candidates.Count > 0
      ? candidates.Max()
      : null;
  }

  /// <summary>
  ///   Answer "will Resolver() actually find Siemens.Engineering at load time?" by walking the
  ///   exact same install path and search directories it does. A check that only asks the
  ///   registry whether TIA is installed says OK on a machine where Openness was never
  ///   installed — and the engine then dies with FileLoadException / "Could not find
  ///   installation path" on the first real call. Reuses the resolver's own private helpers so
  ///   the two cannot drift apart.
  /// </summary>
  public static (bool Ok, string? InstallPath, string? ResolvedDll, string? Problem) ProbeOpennessAssemblies()
  {
    string? installPath;
    try
    {
      installPath = Engineering.GetTiaPortalInstallPath();
    }
    catch (Exception ex)
    {
      return (false, null, null, "install path lookup threw: " + ex.Message);
    }

    if (string.IsNullOrEmpty(installPath))
    {
      return (false, null, null,
        $"no TIA Portal V{Engineering.TiaMajorVersion} install path (registry TIAP{Engineering.TiaMajorVersion}\\TIA_Opns, " +
        "TiaPortalLocation env var and the default install folder were all checked)");
    }

    var versionString = Engineering.TiaMajorVersion.ToString();
    var searchDirectories = new[]
    {
      Path.Combine(installPath, "PublicAPI", $"V{versionString}"), Path.Combine(installPath, "Bin", "PublicAPI"),
    };
    var excluded = new[] { "V13", "V14", "V15", "V16", "V17", "V18", "V19", "V20", }
      .Where(v => v != $"V{versionString}").ToList();

    // V20 ships the monolithic Siemens.Engineering.dll; V21 splits it into
    // Siemens.Engineering.Base/Step7/... — either one proves Openness is present.
    foreach (var dll in new[] { "Siemens.Engineering.dll", "Siemens.Engineering.Base.dll", })
    {
      foreach (var dir in searchDirectories)
      {
        string? found;
        try
        {
          found = Engineering.FindAssemblyRecursive(dir, dll, excluded);
        }
        catch
        {
          continue;
        }

        if (found != null)
        {
          return (true, installPath, found, null);
        }
      }
    }

    return (false, installPath, null,
      $"found the TIA folder but no Siemens.Engineering(.Base).dll under {string.Join(" or ", searchDirectories)}");
  }

  private static string? GetTiaPortalInstallPath() =>
    Engineering.ResolveTiaPortalInstallPath(Engineering.TiaMajorVersion).Path;

  /// <summary>Where an install path came from. The doctor prints it: the source decides what to do when an AI client cannot start the engine (an env var only this shell has is a different problem from a registry entry every process sees).</summary>
  public enum InstallPathSource
  {
    NotFound = 0,
    CliOverride,
    EnvironmentVariable,
    RegistryTiaOpns,
    RegistryOpenness,
    DefaultFolder,
  }

  /// <summary>The install folder for <paramref name="majorVersion" /> plus the source it was found in — for the doctor, so it can name it instead of guessing.</summary>
  public static (string? Path, InstallPathSource Source) DescribeTiaPortalInstallPath(int majorVersion) =>
    Engineering.ResolveTiaPortalInstallPath(majorVersion);

  private static (string? Path, InstallPathSource Source) ResolveTiaPortalInstallPath(int majorVersion)
  {
    // 1. Explicit CLI override (--tia-portal-location). Highest priority — needed when TIA
    //    is installed at a non-default location (e.g. D:\app\TIA20\Portal V20) and the
    //    registry/env var path is wrong or absent.
    if (!string.IsNullOrWhiteSpace(Engineering.TiaPortalLocationOverride) &&
      Directory.Exists(Engineering.TiaPortalLocationOverride))
    {
      return (Engineering.TiaPortalLocationOverride, InstallPathSource.CliOverride);
    }

    // 2. env var (Cursor MCP env or user env) — but it is version-agnostic and on
    //    multi-version machines it typically points at ONE install (e.g. V21), which
    //    used to hijack V20 assembly resolution ("Could not find DLL ... for version 20").
    //    Only trust it when its path names the version we need (or names no version).
    var env = Environment.GetEnvironmentVariable("TiaPortalLocation");
    var envUsable = !string.IsNullOrWhiteSpace(env) && Directory.Exists(env);
    if (envUsable && Engineering.PathMatchesVersion(env!, majorVersion))
    {
      return (env, InstallPathSource.EnvironmentVariable);
    }

    // 3. Version-specific registry entry — authoritative on multi-version machines.
    try
    {
      var subKeyName = $@"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP{majorVersion}\TIA_Opns";
      using var regBaseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
      using var tiaOpnsKey = regBaseKey.OpenSubKey(subKeyName);
      var regPath = tiaOpnsKey?.GetValue("Path")?.ToString();
      if (!string.IsNullOrWhiteSpace(regPath) && Directory.Exists(regPath))
      {
        return (regPath, InstallPathSource.RegistryTiaOpns);
      }
    }
    catch
    {
      // 读不到这个键就换下一个来源（Openness 注册项 / 默认目录 / 环境变量）——
      // 单一来源失效不该让整条解析链断掉，doctor 会把最终用的是哪个来源打出来。
    }

    // 4. Openness registration: the cross-drive source. On the machine this was written on,
    //    _InstalledSW\TIAP21\TIA_Opns had no Path value while the registration named
    //    E:\...\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.Base.dll all along.
    foreach (var reg in Engineering.OpennessRegistrations())
    {
      if (reg.ApiMajor != majorVersion && reg.KeyMajor != majorVersion)
      {
        continue;
      }

      var root = Engineering.InstallRootFromApiDll(reg.DllPath);
      if (root != null && Engineering.PathMatchesVersion(root, majorVersion))
      {
        return (root, InstallPathSource.RegistryOpenness);
      }
    }

    // 5. The default folder. The old code never looked here even though the failure message
    //    claimed it had ("... and the default install folder were all checked") — that claim
    //    is now true.
    var defaultFolder = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
      "Siemens",
      "Automation",
      $"Portal V{majorVersion}");
    if (Directory.Exists(defaultFolder))
    {
      return (defaultFolder, InstallPathSource.DefaultFolder);
    }

    // 6. Last resort: the env var even when its version looks different — better than nothing.
    return envUsable
      ? (env, InstallPathSource.EnvironmentVariable)
      : (null, InstallPathSource.NotFound);
  }

  /// <summary>
  ///   What Siemens itself registered under
  ///   HKLM\SOFTWARE\Siemens\Automation\Openness\&lt;major&gt;.&lt;minor&gt;\PublicAPI\&lt;api&gt;[\net48]:
  ///   one value per API DLL, **with the full path** (Siemens.Engineering,
  ///   Siemens.Engineering.Base, …Hmi). Two things make this worth reading:
  ///   * it is the only registration that still names the install when TIA lives on another
  ///     drive (the other sources are keyed to %ProgramFiles% or are simply absent);
  ///   * it is written by the TIA setup itself, so a path that no longer exists means a stale
  ///     registration, not an install — hence the File.Exists filter.
  ///   The API major is the version whose DLLs sit in that folder; the key major is the TIA
  ///   that registered it (a V18 install also registers the older API versions it ships, e.g.
  ///   15.1 … 18, so only the highest one is meaningful — which is exactly what Max() picks).
  /// </summary>
  private static List<(int KeyMajor, int ApiMajor, string DllPath)> OpennessRegistrations()
  {
    var registrations = new List<(int KeyMajor, int ApiMajor, string DllPath)>();
    try
    {
      using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
      using var openness = baseKey.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness");
      if (openness == null)
      {
        return registrations;
      }

      foreach (var keyName in openness.GetSubKeyNames())
      {
        var keyMajorText = keyName.Split('.')[0];
        if (!int.TryParse(keyMajorText, out var keyMajor))
        {
          continue;
        }

        using var publicApi = openness.OpenSubKey(keyName + @"\PublicAPI");
        if (publicApi == null)
        {
          continue;
        }

        foreach (var apiName in publicApi.GetSubKeyNames())
        {
          var apiMajorText = apiName.Split('.')[0];
          if (!int.TryParse(apiMajorText, out var apiMajor))
          {
            continue;
          }

          using var apiKey = publicApi.OpenSubKey(apiName);
          if (apiKey == null)
          {
            continue;
          }

          foreach (var dllPath in Engineering.RegisteredApiDlls(apiKey))
          {
            registrations.Add((keyMajor, apiMajor, dllPath));
          }
        }
      }
    }
    catch
    {
      // 注册表没有 Openness 这一支（或读不到）就当作「这个来源没有」：其余来源继续走，
      // doctor 会把最终采用哪个来源打印出来，不会因此给出错误的安装结论。
    }

    return registrations;
  }

  /// <summary>The DLL paths registered for one API version — directly under it and one level deeper (the "net48" sub-key V21 uses).</summary>
  private static IEnumerable<string> RegisteredApiDlls(RegistryKey apiKey)
  {
    var names = new[] { "Siemens.Engineering", "Siemens.Engineering.Base", };
    foreach (var name in names)
    {
      if (apiKey.GetValue(name) is string direct && File.Exists(direct))
      {
        yield return direct;
      }
    }

    foreach (var childName in apiKey.GetSubKeyNames())
    {
      using var child = apiKey.OpenSubKey(childName);
      if (child == null)
      {
        continue;
      }

      foreach (var name in names)
      {
        if (child.GetValue(name) is string nested && File.Exists(nested))
        {
          yield return nested;
        }
      }
    }
  }

  /// <summary>Turns "...\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.Base.dll" back into "...\Portal V21".</summary>
  private static string? InstallRootFromApiDll(string dllPath)
  {
    var marker = @"\PublicAPI\";
    var index = dllPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    if (index <= 0)
    {
      return null;
    }

    var root = dllPath.Substring(0, index);
    return Directory.Exists(root)
      ? root
      : null;
  }

  /// <summary>True when the path names no version at all, or names exactly V{version}.</summary>
  private static bool PathMatchesVersion(string path, int version)
  {
    var m = Regex.Match(path, @"[Vv](\d{2})", RegexOptions.RightToLeft);
    if (!m.Success)
    {
      return true;
    }

    return int.TryParse(m.Groups[1].Value, out var pv) && pv == version;
  }

  private static string? FindAssemblyRecursive(string directory, string fileName,
    IEnumerable<string> excludedTiaMajorVersions)
  {
    if (!Directory.Exists(directory))
    {
      return null;
    }

    var filePath = Path.Combine(directory, fileName);
    if (File.Exists(filePath))
    {
      return filePath;
    }

    var tmpExcludedTiaMajorVersions = excludedTiaMajorVersions.ToList();
    foreach (var subDir in Directory.GetDirectories(directory))
    {
      var subDirName = new DirectoryInfo(subDir).Name;
      if (tmpExcludedTiaMajorVersions.Contains(subDirName))
      {
        continue;
      }

      var result = Engineering.FindAssemblyRecursive(subDir, fileName, tmpExcludedTiaMajorVersions);
      if (result != null)
      {
        return result;
      }
    }

    return null;
  }
}
