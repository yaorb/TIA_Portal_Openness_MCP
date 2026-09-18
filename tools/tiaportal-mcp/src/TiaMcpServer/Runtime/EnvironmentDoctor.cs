#region

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TiaMcpServer.Siemens;

#endregion

namespace TiaMcpServer.Runtime;

/// <summary>
///   The environment checks behind both `tia doctor` (CLI) and the Doctor MCP tool.
///   It lives in one place because the two used to be written separately and had drifted: each
///   only asked the registry whether TIA existed, checked the Openness group, and stopped. The
///   three failures that actually strand a first-time user on a fresh machine were checked by
///   neither:
///   * Openness was never installed, so Siemens.Engineering cannot be resolved (the registry
///   still says TIA is there, so both doctors reported OK and the engine died on first call);
///   * the delivery was unzipped straight from a download, so Windows marked every DLL with a
///   zone identifier and .NET refuses to load them;
///   * .NET Framework 4.8 is missing, which is prerequisite #1 in the README.
///   Each check carries both languages: the CLI is invoked from Chinese .bat files by Chinese
///   engineers, while the MCP tool's output is consumed by a model alongside English tool text.
/// </summary>
public static class EnvironmentDoctor
{
  /// <summary>The supported TIA majors. Messages must not promise more than the product delivers.</summary>
  private const string SupportedVersions = "V20 / V21";

  public static bool PreferChinese =>
    CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

  public static List<Check> Run(int compiledTiaMajorVersion, int? detectedTiaMajorVersion)
  {
    // The version the engine will actually load assemblies for — set in Program.Main before any
    // verb runs. Falls back to the detected one so the checks stay meaningful when called early.
    var inUse = Engineering.TiaMajorVersion != 0
      ? Engineering.TiaMajorVersion
      : detectedTiaMajorVersion;

    var checks = new List<Check>
    {
      EnvironmentDoctor.TiaInstall(detectedTiaMajorVersion),
      EnvironmentDoctor.TiaInstallLocation(inUse),
      EnvironmentDoctor.OpennessAssemblies(),
      // Keeps the machine's *detected* version: the message says "machine has V{detected}", and
      // that must stay a statement about the machine, not about a --tia-major-version override.
      EnvironmentDoctor.EngineVersionMatch(compiledTiaMajorVersion, detectedTiaMajorVersion),
      EnvironmentDoctor.DotNetFramework48(),
      EnvironmentDoctor.FilesNotBlocked(),
    };
    return checks;
  }

  /// <summary>
  ///   Which folder the engine will load the Openness API from, and how it found it.
  ///   This is informational — <see cref="OpennessAssemblies" /> is the check that gates — but it
  ///   answers the question the other checks cannot: *where* is TIA, and does the AI client see
  ///   the same place this shell does. That distinction bites exactly on non-default installs:
  ///   TIA on another drive is invisible to the default-folder scan, and an env var set in one
  ///   shell is not inherited by an MCP host, so "tia doctor is green but the client cannot
  ///   start the engine" was possible with nothing in either report explaining it.
  /// </summary>
  private static Check TiaInstallLocation(int? inUse)
  {
    if (inUse == null || inUse.Value == 0)
    {
      return new Check
      {
        Id = "tia-location",
        Ok = false,
        Gating = false,
        NameEn = "TIA Portal install path",
        NameZh = "TIA Portal 安装目录",
        DetailEn = "unknown — no TIA Portal version was detected",
        DetailZh = "未知——没有检测到任何 TIA Portal 版本",
        FixEn = "Install TIA Portal V20 / V21, or set the TiaPortalLocation environment variable to the install folder.",
        FixZh = "安装 TIA Portal V20 / V21，或把环境变量 TiaPortalLocation 指向安装目录。",
      };
    }

    var major = inUse.Value;
    var (path, source) = Engineering.DescribeTiaPortalInstallPath(major);
    var found = !string.IsNullOrWhiteSpace(path);
    var (sourceEn, sourceZh) = EnvironmentDoctor.SourceName(source);
    var isDefaultFolder = source == Engineering.InstallPathSource.DefaultFolder;

    var noteEn = "";
    var noteZh = "";
    if (found && source == Engineering.InstallPathSource.EnvironmentVariable)
    {
      noteEn = " — note: this comes from the TiaPortalLocation environment variable, and an AI client does " +
        "not inherit your shell's environment. Put the same variable in the client's MCP config (env block), " +
        "or pass --tia-portal-location.";
      noteZh = " —— 注意：这个路径来自环境变量 TiaPortalLocation，而 AI 客户端不会继承你 shell 的环境变量。" +
        "请在客户端的 MCP 配置里也写上它（env 段），或者加参数 --tia-portal-location。";
    }
    else if (found && !isDefaultFolder)
    {
      noteEn = " — not the default %ProgramFiles% folder; the engine resolves it through the registry, so no " +
        "environment variable is needed.";
      noteZh = " —— 非默认安装目录（不在 %ProgramFiles% 下）；引擎能从注册表找到它，不需要设环境变量。";
    }

    return new Check
    {
      Id = "tia-location",
      Ok = found,
      Gating = false,
      NameEn = "TIA Portal install path",
      NameZh = "TIA Portal 安装目录",
      DetailEn = found
        ? $"V{major} at {path} (found via {sourceEn}){noteEn}"
        : $"could not resolve a V{major} install folder — checked the --tia-portal-location argument, the " +
        "TiaPortalLocation environment variable, both registry sources and the default folder",
      DetailZh = found
        ? $"V{major} 位于 {path}（来源：{sourceZh}）{noteZh}"
        : $"没能定位 V{major} 的安装目录——命令行参数 --tia-portal-location、环境变量 TiaPortalLocation、" +
        "两支注册表来源和默认安装目录都查过了",
      FixEn = found
        ? null
        : $"Set the TiaPortalLocation environment variable (or pass --tia-portal-location) to the TIA Portal " +
        $"V{major} folder, e.g. D:\\Siemens\\Portal V{major}.",
      FixZh = found
        ? null
        : $"把环境变量 TiaPortalLocation（或参数 --tia-portal-location）指向 TIA Portal V{major} 的安装目录，" +
        $"例如 D:\\Siemens\\Portal V{major}。",
    };
  }

  /// <summary>Names an install-path source for people (Bootstrap reports the same wording in English, so the two cannot drift).</summary>
  public static (string En, string Zh) SourceName(Engineering.InstallPathSource source)
  {
    switch (source)
    {
      case Engineering.InstallPathSource.CliOverride:
        return ("the --tia-portal-location argument", "命令行参数 --tia-portal-location");

      case Engineering.InstallPathSource.EnvironmentVariable:
        return ("the TiaPortalLocation environment variable", "环境变量 TiaPortalLocation");

      case Engineering.InstallPathSource.RegistryTiaOpns:
        return (@"the registry (TIAP<ver>\TIA_Opns)", @"注册表 TIAP<版本>\TIA_Opns");

      case Engineering.InstallPathSource.RegistryOpenness:
        return ("the registry (Openness registration)", "注册表里的 Openness 注册项");

      case Engineering.InstallPathSource.DefaultFolder:
        return ("the default install folder", "默认安装目录");

      default:
        return ("no source", "没有来源");
    }
  }

  private static Check TiaInstall(int? detected)
  {
    var ok = detected != null;
    return new Check
    {
      Id = "tia-install",
      Ok = ok,
      NameEn = "TIA Portal installation",
      NameZh = "TIA Portal 安装",
      DetailEn = ok
        ? $"detected V{detected}"
        : "no TIA Portal detected (registry / TiaPortalLocation / default folder)",
      DetailZh = ok
        ? $"检测到 V{detected}"
        : "未检测到 TIA Portal（注册表 / TiaPortalLocation 环境变量 / 默认安装目录都查过了）",
      FixEn = ok
        ? null
        : $"Install TIA Portal {EnvironmentDoctor.SupportedVersions} including the Openness option, or set the TiaPortalLocation environment variable to the install folder (e.g. D:\\TIA21\\Portal V21).",
      FixZh = ok
        ? null
        : $"安装 TIA Portal {EnvironmentDoctor.SupportedVersions}（安装时要勾选 Openness 组件），或把用户环境变量 TiaPortalLocation 指向安装根目录（例如 D:\\TIA21\\Portal V21）。",
    };
  }

  private static Check OpennessAssemblies()
  {
    var probe = Engineering.ProbeOpennessAssemblies();
    return new Check
    {
      Id = "openness-dll",
      Ok = probe.Ok,
      NameEn = "Openness API assemblies",
      NameZh = "Openness 编程接口 DLL",
      DetailEn = probe.Ok
        ? "resolvable: " + probe.ResolvedDll
        : probe.Problem ?? "not resolvable",
      DetailZh = probe.Ok
        ? "可解析：" + probe.ResolvedDll
        : "无法解析——" + (probe.Problem ?? "原因未知"),
      FixEn = probe.Ok
        ? null
        : "TIA can be installed without Openness. Re-run the TIA Portal setup and add the 'Openness' component, then confirm Siemens.Engineering.dll (V20) or Siemens.Engineering.Base.dll (V21) exists under <install>\\PublicAPI\\V<version>\\.",
      FixZh = probe.Ok
        ? null
        : "装了 TIA 不等于装了 Openness。重新运行 TIA Portal 安装程序补装『Openness』组件，然后确认 <安装目录>\\PublicAPI\\V<版本>\\ 下存在 Siemens.Engineering.dll（V20）或 Siemens.Engineering.Base.dll（V21）。",
    };
  }

  private static Check EngineVersionMatch(int compiled, int? detected)
  {
    var ok = detected == null || detected.Value == compiled || EngineRouter.FindSiblingExe(detected.Value) != null;
    return new Check
    {
      Id = "engine-version",
      Ok = ok,
      NameEn = "Engine exe / TIA version",
      NameZh = "引擎 exe 与 TIA 版本匹配",
      DetailEn = $"exe built for V{compiled}" + (detected != null
        ? $", machine has V{detected}"
        : ", machine version unknown"),
      DetailZh = $"该 exe 为 V{compiled} 构建" + (detected != null
        ? $"，本机装的是 V{detected}"
        : "，本机版本未知"),
      FixEn = ok || detected == null
        ? null
        : $"Use runtime\\v{detected}\\TiaMcpServer.exe from the delivery (both versions ship), or keep this one and pass --tia-major-version {compiled}.",
      FixZh = ok || detected == null
        ? null
        : $"改用交付包里的 runtime\\v{detected}\\TiaMcpServer.exe（两个版本都随包提供），或继续用当前这个并加参数 --tia-major-version {compiled}。",
    };
  }

  private static Check DotNetFramework48()
  {
    var release = 0;
    string? probeError = null;
    try
    {
      using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
        .OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
      release = (int)(key?.GetValue("Release") ?? 0);
    }
    catch (Exception ex)
    {
      // 读不到注册表 ≠ 没装 4.8。原来两者都落进 release=0，于是报「未检测到」——
      // 一个错误的诊断结论。把原因带出来，让人能分辨「没装」和「查不到」。
      probeError = ex.Message;
    }

    // 528040 = .NET Framework 4.8 RTM; anything at or above it satisfies net48.
    var ok = release >= 528040;
    return new Check
    {
      Id = "dotnet48",
      Ok = ok,
      NameEn = ".NET Framework 4.8",
      NameZh = ".NET Framework 4.8",
      DetailEn = probeError != null
        ? $"could not read the registry ({probeError}) — treat as NOT verified"
        : ok
          ? $"present (release {release})"
          : release > 0
            ? $"too old (release {release}, need >= 528040)"
            : "not detected",
      DetailZh = probeError != null
        ? $"读不到注册表（{probeError}）—— 按「未验证」处理"
        : ok
          ? $"已安装（release {release}）"
          : release > 0
            ? $"版本过低（release {release}，需要 >= 528040）"
            : "未检测到",
      FixEn = ok
        ? null
        : "Install the .NET Framework 4.8 runtime (Windows 10 1903+ and Windows 11 ship it built in).",
      FixZh = ok
        ? null
        : "安装 .NET Framework 4.8 运行时（Windows 10 1903 及以上、Windows 11 自带）。",
    };
  }

  /// <summary>
  ///   Windows tags every file extracted from a downloaded .zip with a Zone.Identifier stream;
  ///   .NET then refuses to load the assemblies and the engine fails in ways that look nothing
  ///   like "your download is blocked".
  /// </summary>
  private static Check FilesNotBlocked()
  {
    var blocked = new List<string>();
    var dir = "";
    string? probeError = null;
    try
    {
      dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
      if (!string.IsNullOrEmpty(dir))
      {
        foreach (var f in Directory.GetFiles(dir, "*.dll").Concat(Directory.GetFiles(dir, "*.exe")))
        {
          if (EnvironmentDoctor.HasZoneIdentifier(f))
          {
            blocked.Add(Path.GetFileName(f));
          }

          if (blocked.Count >= 5)
          {
            break;
          }
        }
      }
    }
    catch (Exception ex)
    {
      // 探测失败原来落进「blocked 为空」→ 直接报 OK，是个**假通过**。
      // 这里不擅自把 Ok 翻成 false（本机无法探测时不该直接判红），
      // 而是把「没验证成」写进 Detail —— 读报告的人看得到真相。
      probeError = ex.Message;
    }

    var ok = blocked.Count == 0;
    var list = string.Join(", ", blocked);
    return new Check
    {
      Id = "motw",
      Ok = ok,
      NameEn = "Files not blocked by Windows (MOTW)",
      NameZh = "文件未被 Windows 标记为网络来源 (MOTW)",
      DetailEn = probeError != null
        ? $"could not scan the engine folder ({probeError}) — NOT verified"
        : ok
          ? "no zone identifier on the engine files"
          : $"blocked files present: {list}{(blocked.Count >= 5 ? ", ..." : "")}",
      DetailZh = probeError != null
        ? $"扫描引擎目录失败（{probeError}）—— 未验证"
        : ok
          ? "引擎目录下的文件没有网络来源标记"
          : $"存在被阻止的文件：{list}{(blocked.Count >= 5 ? " …" : "")}",
      FixEn = ok
        ? null
        : $"Unblock the delivery folder in PowerShell:  Get-ChildItem -Recurse '{dir}' | Unblock-File",
      FixZh = ok
        ? null
        : $"用 PowerShell 解除阻止：  Get-ChildItem -Recurse '{dir}' | Unblock-File",
    };
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
    IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool CloseHandle(IntPtr hObject);

  /// <summary>
  ///   Alternate data streams have to be opened through the Win32 API: File.Exists() on an
  ///   "file.dll:Zone.Identifier" path returns false even when the stream is right there, so a
  ///   check written with it silently never fires.
  /// </summary>
  private static bool HasZoneIdentifier(string path)
  {
    const uint GENERIC_READ = 0x80000000;
    const uint FILE_SHARE_READWRITE = 0x00000003;
    const uint OPEN_EXISTING = 3;
    var invalid = new IntPtr(-1);
    var h = invalid;
    try
    {
      h = EnvironmentDoctor.CreateFileW(path + ":Zone.Identifier",
        GENERIC_READ,
        FILE_SHARE_READWRITE,
        IntPtr.Zero,
        OPEN_EXISTING,
        0,
        IntPtr.Zero);
      return h != invalid;
    }
    catch
    {
      return false;
    }
    finally
    {
      if (h != invalid && h != IntPtr.Zero)
      {
        EnvironmentDoctor.CloseHandle(h);
      }
    }
  }

  public sealed class Check
  {
    public string DetailEn = "", DetailZh = "";
    public string? FixEn, FixZh;

    /// <summary>Informational checks never gate readiness.</summary>
    public bool Gating = true;

    public string Id = "";
    public string NameEn = "", NameZh = "";
    public bool Ok;

    public string Name(bool zh) =>
      zh
        ? this.NameZh
        : this.NameEn;

    public string Detail(bool zh) =>
      zh
        ? this.DetailZh
        : this.DetailEn;

    public string? Fix(bool zh) =>
      zh
        ? this.FixZh
        : this.FixEn;
  }
}
