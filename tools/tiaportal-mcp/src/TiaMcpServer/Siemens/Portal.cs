#region

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;

#endregion

namespace TiaMcpServer.Siemens;

public partial class Portal(ILogger<Portal>? logger = null)
{
  // closing parantheses for regex characters omitted, because they are not relevant for regex detection
  private readonly char[] _regexChars = ['.', '^', '$', '*', '+', '?', '(', '[', '{', '\\', '|',];

  // Resolving a softwarePath walks the device tree via Openness (~40 COM calls per call). Cache it per
  // open project; ReferenceEquals(_project) auto-invalidates on any project open/close/create/attach
  // without a COM call. Device adds only introduce new paths (cache misses); there is no delete-device tool.
  private readonly Dictionary<string, SoftwareContainer>
    _softwareContainerCache = new(StringComparer.OrdinalIgnoreCase);

  private TiaPortal? _portal;

  // Did THIS session open the current project, or did we attach to one the user already had
  // open? ConnectPortal deliberately prefers a running Portal that already HAS a project --
  // right for AttachToOpenProject, catastrophic for CreateProject/OpenProject, whose first act
  // is to Close() whatever is open. Without this flag those two silently close the engineer's
  // work, unsaved edits included. Set true only where we ourselves opened/created it.
  private bool _projectOpenedByUs;
  private LocalSession? _session;
  private ProjectBase? _softwareCacheProject;

  /// <summary>
  ///   The currently open project/session, or null. Exposed because some Openness features are
  ///   only reachable as a service off the project root (e.g. VersionControlInterface) and the
  ///   generic reflection helpers navigate properties, not services.
  /// </summary>
  public ProjectBase? CurrentProject { get; private set; }

  /// <summary>True when the open project is one the user already had open (we merely attached).</summary>
  private bool HasForeignProject => this.CurrentProject != null && !this._projectOpenedByUs;

  public string? LastConnectError { get; private set; }

  #region helper for mcp server

  public bool ProjectIsValid
  {
    get
    {
      if (this.CurrentProject == null)
      {
        return false;
      }

      // Check if the project is a valid Project instance
      if (this._session == null && this.CurrentProject is Project)
      {
        return true;
      }

      // If it's a MultiuserProject, we can also check its validity
      return this._session != null && this.CurrentProject is MultiuserProject;
    }
  }

  public bool IsLocalSession => this._session != null;

  public bool IsLocalProject => this._session == null;

  #endregion

  #region helper for unit tests

  public static bool IsLocalSessionFile(string sessionPath)
  {
    // Check if the path ends with '.als\d+' using regex
    var regex = new Regex(@"\.als\d+$", RegexOptions.IgnoreCase);
    return regex.IsMatch(sessionPath);
  }

  public static bool IsLocalProjectFile(string projectPath)
  {
    // Check if the path ends with '.ap\d+' using regex
    var regex = new Regex(@"\.ap\d+$", RegexOptions.IgnoreCase);
    return regex.IsMatch(projectPath);
  }

  public void Dispose()
  {
    try
    {
      (this.CurrentProject as Project)?.Close();
    }
    catch (Exception ex)
    {
      logger?.LogWarning(ex, "Error closing the project on Dispose");
    }

    try
    {
      this._portal?.Dispose();
    }
    catch (Exception ex)
    {
      logger?.LogWarning(ex, "Error disposing TIA Portal on Dispose");
    }
  }

  #endregion

  #region portal

  // Attach to a portal process but never block longer than timeoutMs. An orphaned/dying
  // Siemens.Automation.Portal (e.g. its controlling process was killed) can otherwise hang the
  // COM Attach() call ~200s before throwing EngineeringSecurityException, stalling the whole
  // connect. On timeout we return null so the caller skips this process and tries the next /
  // launches a fresh instance. The worker thread is background and dies with the dead process.
  private TiaPortal? AttachWithTimeout(TiaPortalProcess proc, int timeoutMs)
  {
    TiaPortal? result = null;
    Exception? error = null;
    var worker = new Thread(() =>
    {
      try
      {
        result = proc.Attach();
      }
      catch (Exception ex)
      {
        error = ex;
      }
    }) { IsBackground = true, };
    worker.Start();
    if (worker.Join(timeoutMs))
    {
      return error != null
        ? throw error
        : result;
    }

    logger?.LogWarning(
      $"Attach to TIA Portal PID={proc.Id} exceeded {timeoutMs}ms; skipping (likely orphaned/dying instance).");
    return null;

  }

  // 判断一个 attach 失败是不是"白名单/授权被拒"（Openness 用户组、授权白名单）。
  // 这类拒绝跟具体是哪个 Portal 进程无关——换下一个候选、乃至自己新起一个实例，
  // 结果都一样被拒。用类型名字符串匹配而不是 catch 具体类型：
  // EngineeringSecurityException 来自运行时解析的 Openness 程序集（V20/V21 两套 csproj），
  // 不引入编译期依赖更稳。沿 InnerException 链走，因为它常被
  // EngineeringTargetInvocationException 之类包一层。
  private static bool IsSecurityRefusal(Exception? ex)
  {
    var e = ex;
    while (e != null)
    {
      if (e.GetType().Name == "EngineeringSecurityException")
      {
        return true;
      }

      e = e.InnerException;
    }

    return false;
  }

  public bool ConnectPortal()
  {
    logger?.LogInformation("Connecting to TIA Portal...");

    try
    {
      this.LastConnectError = null;
      this.CurrentProject = null;
      this._projectOpenedByUs = false;
      this._session = null;
      this._portal = null;

      // connect to running TIA Portal
      var processes = TiaPortal.GetProcesses();
      logger?.LogInformation($"TIA Portal process count: {processes.Count()}");
      if (processes.Any())
      {
        // IMPORTANT: multiple Siemens.Automation.Portal.exe can run at once.
        // Attaching to processes.First() is unstable and often attaches to an instance
        // without the user's open project, causing "project already opened by user" errors.
        //
        // Strategy:
        // - Try attach each process
        // - Prefer the first instance that exposes LocalSessions/Projects (i.e. has an open project)
        // - Otherwise fall back to the first attachable instance
        TiaPortal? firstAttachable = null;
        string? firstAttachableInfo = null;

        foreach (var proc in processes)
        {
          TiaPortal? candidate = null;
          try
          {
            logger?.LogInformation($"Trying attach to TIA Portal process PID={proc.Id}");
            candidate = this.AttachWithTimeout(proc, 30000);
            logger?.LogInformation(candidate == null
              ? $"Attach returned null/timed out for PID={proc.Id} — skipping"
              : $"Attach succeeded for PID={proc.Id}");
            if (candidate == null)
            {
              continue;
            }

            // record first attachable in case none has projects
            if (firstAttachable == null)
            {
              firstAttachable = candidate;
              firstAttachableInfo = $"PID={proc.Id}";
            }

            // Prefer instance with an open project/session
            var hasSession = false;
            var hasProject = false;
            try
            {
              hasSession = candidate.LocalSessions.Any();
            }
            catch (Exception ex)
            {
              // 探测失败会让 hasSession 停在 false，而它决定「挑哪个实例 attach」——
              // 只记录结果（下面那行 hasSession=…）不足以排障，原因必须留下。
              logger?.LogWarning(ex, $"Portal PID={proc.Id}: probing LocalSessions failed; treated as no-session");
            }

            try
            {
              hasProject = candidate.Projects.Any();
            }
            catch (Exception ex)
            {
              logger?.LogWarning(ex, $"Portal PID={proc.Id}: probing Projects failed; treated as no-project");
            }

            logger?.LogInformation($"Portal PID={proc.Id}: hasSession={hasSession}, hasProject={hasProject}");

            if (hasSession || hasProject)
            {
              this._portal = candidate;
              logger?.LogInformation($"Selected attached TIA Portal PID={proc.Id}");

              if (hasSession)
              {
                try
                {
                  this._session = this._portal.LocalSessions.First();
                  this.CurrentProject = this._session.Project;
                  this._projectOpenedByUs = false;
                }
                catch (Exception ex)
                {
                  logger?.LogWarning(ex, "ConnectPortal: attaching the local session threw; falling back to Projects.First()");
                }
              }

              if (this.CurrentProject != null || !hasProject)
              {
                return true;
              }

              try
              {
                this.CurrentProject = this._portal.Projects.First();
              }
              catch (Exception ex)
              {
                logger?.LogWarning(ex, "ConnectPortal: attaching the first project threw; returning connected WITHOUT a bound project");
              }

              this._projectOpenedByUs = false;

              return true;
            }
          }
          catch (Exception ex)
          {
            logger?.LogWarning(ex, $"Attach failed for TIA Portal PID={proc.Id}");
            this.LastConnectError = ex.ToString();

            // "这个候选连不上"（忙 / attach 超时 / 没有工程）可以换下一个；
            // 但白名单/授权被拒换谁都一样，继续扫毫无意义且有害：扫空所有候选后
            // 会落到下面"新起一个无头 TIA 实例"，那次同样被拒，却在用户机器上
            // 留下一个空转的孤儿 Siemens.Automation.Portal 进程。所以当场原样重抛，
            // 让调用方拿到真因而不是"没有可 attach 的实例"。
            if (Portal.IsSecurityRefusal(ex))
            {
              // 先释放此前记下的可 attach 候选——已经不会有人用它了。
              // 置 null 后，当前候选（若正是它）也能被 finally 正常释放，不漏 COM 引用。
              if (firstAttachable == null)
              {
                throw;
              }

              if (firstAttachable != candidate)
              {
                try
                {
                  firstAttachable.Dispose();
                }
                catch
                {
                  // teardown：释放备用实例失败无处可报
                }
              }

              firstAttachable = null;

              throw;
            }
          }
          finally
          {
            // If this candidate wasn't selected and isn't firstAttachable, dispose it.
            if (candidate != null && candidate != this._portal && candidate != firstAttachable)
            {
              try
              {
                candidate.Dispose();
              }
              catch
              {
                // teardown：替换实例前释放旧的，失败不影响后续
              }
            }
          }
        }

        // fallback to first attachable instance
        if (firstAttachable != null)
        {
          this._portal = firstAttachable;
          logger?.LogInformation($"Falling back to first attachable TIA Portal ({firstAttachableInfo})");
          this.LastConnectError =
            $"Attached to first available portal ({firstAttachableInfo}), but it has no visible projects/sessions.";
          return true;
        }

        this.LastConnectError = "No attachable TIA Portal process found; starting a new TIA Portal instance.";
        logger?.LogInformation(this.LastConnectError);
      }

      // start new TIA Portal. Headless (WithoutUserInterface) is the default because it
      // starts far faster than booting the full GUI; --with-ui flips it for visual inspection.
      var launchMode = Engineering.LaunchWithUserInterface
        ? TiaPortalMode.WithUserInterface
        : TiaPortalMode.WithoutUserInterface;
      logger?.LogInformation($"Starting a new TIA Portal instance ({launchMode}).");
      this._portal = new TiaPortal(launchMode);

      return true;
    }
    catch (Exception ex)
    {
      // 统一错误处理：硬失败抛结构化异常，替代 return false + LastConnectError 侧信道
      throw new PortalException(PortalErrorCode.OpennessError,
        $"ConnectPortal failed: {Portal.FormatExceptionDetail(ex)}",
        inner: ex);
    }
  }

  public List<string> ListPortalProcessProjects()
  {
    var lines = new List<string>();
    IReadOnlyList<TiaPortalProcess> processes;
    try
    {
      processes = [.. TiaPortal.GetProcesses(),];
    }
    catch (Exception ex)
    {
      lines.Add("GetProcesses error: " + Portal.FormatExceptionDetail(ex));
      return lines;
    }

    lines.Add("TIA Portal process count: " + processes.Count);
    foreach (var proc in processes)
    {
      TiaPortal? candidate = null;
      try
      {
        lines.Add("PID=" + proc.Id + " attach: trying");
        candidate = proc.Attach();
        if (candidate == null)
        {
          lines.Add("PID=" + proc.Id + " attach: <null>");
          continue;
        }

        lines.Add("PID=" + proc.Id + " attach: OK");
        try
        {
          var any = false;
          foreach (var s in candidate.LocalSessions)
          {
            any = true;
            lines.Add("PID=" + proc.Id + " sessionProject=" + (s.Project?.Name ?? "<null>"));
          }

          if (!any)
          {
            lines.Add("PID=" + proc.Id + " sessions=<empty>");
          }
        }
        catch (Exception ex)
        {
          lines.Add("PID=" + proc.Id + " sessions error: " + Portal.FormatExceptionDetail(ex));
        }

        try
        {
          var any = false;
          foreach (var p in candidate.Projects)
          {
            any = true;
            lines.Add("PID=" + proc.Id + " project=" + (p?.Name ?? "<null>"));
          }

          if (!any)
          {
            lines.Add("PID=" + proc.Id + " projects=<empty>");
          }
        }
        catch (Exception ex)
        {
          lines.Add("PID=" + proc.Id + " projects error: " + Portal.FormatExceptionDetail(ex));
        }
      }
      catch (Exception ex)
      {
        lines.Add("PID=" + proc.Id + " attach error: " + Portal.FormatExceptionDetail(ex));
      }
      finally
      {
        if (candidate != null && candidate != this._portal)
        {
          try
          {
            candidate.Dispose();
          }
          catch
          {
            // teardown：释放候选实例失败无处可报
          }
        }
      }
    }

    return lines;
  }

  /// <summary>
  ///   起一个**全新的无头 TIA Portal 实例**，不去 attach 任何已经在跑的实例。
  ///   为什么需要它：ConnectPortal 会优先接管**已经开着工程**的那个实例，
  ///   而 OpenProject 又（正确地）拒绝动别人的工程 —— 于是用户只要博途里开着
  ///   任何工程，MCP 就**完全用不了**。那不是安全，那是把人挡在门外。
  ///   有了这条路，用户可以一边在界面里干活，一边让 MCP 在自己的实例里跑自己的工程。
  ///   必须是这个 MCP 进程里的第一个连接动作：已经绑了别的连接再起隔离实例，
  ///   只会留下一个没人管的 Siemens.Automation.Portal 进程，之后别的 attach 会把它
  ///   抢走，表现为间歇性的「no open project」。
  /// </summary>
  public bool ConnectIsolatedPortal()
  {
    if (this._portal != null || this.CurrentProject != null || this._session != null)
    {
      throw new PortalException(PortalErrorCode.InvalidState,
        "ConnectIsolated: this MCP session already owns a TIA connection. " +
        "Start a fresh MCP process before calling ConnectIsolated.");
    }

    this.LastConnectError = null;
    this._portal = new TiaPortal();
    logger?.LogInformation("Started isolated headless TIA Portal instance.");
    return true;
  }

  public bool IsConnected() => this._portal != null;

  public bool DisconnectPortal()
  {
    logger?.LogInformation("Disconnecting from TIA Portal...");
    return Operation.Run(logger,
      nameof(Portal.DisconnectPortal),
      () =>
      {
        this.CurrentProject = null;
        this._projectOpenedByUs = false;
        this._session = null;
        this._portal?.Dispose();
        this._portal = null;
      });
  }

  #endregion

  #region status

  public State GetState()
  {
    logger?.LogInformation("Getting TIA Portal state...");
    if (this._portal == null)
    {
      return new State
      {
        IsConnected = this.IsConnected(),
        Project = this.CurrentProject != null
          ? this.CurrentProject.Name
          : "-",
        Session = this._session != null
          ? this._session.Project.Name
          : "-",
      };
    }

    // check for existing local sessions
    if (this._portal.LocalSessions.Any())
    {
      // pick first session whose Project is accessible
      foreach (var s in this._portal.LocalSessions)
      {
        try
        {
          var p = s.Project;
          var _ = p?.Name; // touch to validate not disposed
          this._session = s;
          this.CurrentProject = p;
          break;
        }
        catch
        {
          // skip disposed/inaccessible session projects
        }
      }
    }
    // checks for existing projects
    else if (this._portal.Projects.Any())
    {
      // pick first accessible project (avoid disposed placeholder)
      foreach (var p in this._portal.Projects)
      {
        try
        {
          var _ = p?.Name;
          this.CurrentProject = p;
          break;
        }
        catch
        {
          // skip disposed
        }
      }
    }

    return new State
    {
      IsConnected = this.IsConnected(),
      Project = this.CurrentProject != null
        ? this.CurrentProject.Name
        : "-",
      Session = this._session != null
        ? this._session.Project.Name
        : "-",
    };
  }

  public bool AttachToOpenProject(string projectName)
  {
    logger?.LogInformation($"Attaching to open project: {projectName}");

    if (string.IsNullOrWhiteSpace(projectName))
    {
      return false;
    }

    projectName = projectName.Trim();

    // Connect 之后 TIA 的 LocalSessions / Projects 是异步填充的，
    // 这里轮询最多 15s，避免 Connect+Attach 并行或刚启动时刷出 false。
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (true)
    {
      try
      {
        if (this._portal != null && this.TryAttachProjectInPortal(this._portal, projectName))
        {
          return true;
        }

        foreach (var proc in TiaPortal.GetProcesses())
        {
          try
          {
            var candidate = proc.Attach();
            if (candidate == null)
            {
              continue;
            }

            if (this.TryAttachProjectInPortal(candidate, projectName))
            {
              if (this._portal != null && !object.ReferenceEquals(this._portal, candidate))
              {
                try
                {
                  this._portal.Dispose();
                }
                catch
                {
                  // teardown：替换实例前释放旧的，失败不影响后续
                }
              }

              this._portal = candidate;
              return true;
            }

            if (object.ReferenceEquals(this._portal, candidate))
            {
              continue;
            }

            try
            {
              candidate.Dispose();
            }
            catch
            {
              // teardown：候选不是目标实例，释放它；失败不影响后续
            }
          }
          catch (Exception ex)
          {
            // 逐个实例尝试 attach 是预期内的（候选可能被别人占用或已 dispose），所以不抛；
            // 但全吞掉会让调用方只看到 false、拿不到任何原因 —— 记进 LastConnectError
            // 侧信道（Bootstrap / GetState 会读它），与 ConnectPortal 的既有约定一致。
            this.LastConnectError =
              $"AttachToOpenProject: attach attempt failed: {Portal.FormatExceptionDetail(ex)}";
            logger?.LogDebug(ex, "AttachToOpenProject: attach attempt failed; trying the next candidate");
          }
        }
      }
      catch (Exception ex)
      {
        this.LastConnectError = $"AttachToOpenProject: {Portal.FormatExceptionDetail(ex)}";
        logger?.LogDebug(ex, "AttachToOpenProject: pass failed; retrying until the deadline");
      }

      if (DateTime.UtcNow >= deadline)
      {
        break;
      }

      Thread.Sleep(500);
    }

    return false;
  }

  private bool TryAttachProjectInPortal(TiaPortal portal, string projectName)
  {
    try
    {
      foreach (var s in portal.LocalSessions)
      {
        try
        {
          var p = s.Project;
          if (p == null || !string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase))
          {
            continue;
          }

          this._session = s;
          this.CurrentProject = p;
          return true;
        }
        catch
        {
          // 这个 session 不是目标工程的：继续试下一个，全部试完返回 false
        }
      }

      foreach (var p in portal.Projects)
      {
        try
        {
          if (p == null || !string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase))
          {
            continue;
          }

          this._session = null;
          this.CurrentProject = p;
          return true;
        }
        catch
        {
          // 这个 project 打不开：继续试下一个，全部试完返回 false
        }
      }
    }
    catch
    {
      // 同上：单个 project 读取失败即跳过，最终返回 false
    }

    return false;
  }

  #endregion

  #region project

  public List<ProjectBase> GetProjects()
  {
    logger?.LogInformation("Getting open projects...");

    if (this._portal == null)
    {
      logger?.LogWarning("No TIA Portal instance available.");

      return [];
    }

    var projects = new List<ProjectBase>();

    if (this._portal.Projects == null)
    {
      return projects;
    }

    projects.AddRange(this._portal.Projects.Cast<ProjectBase>());

    return projects;
  }

  /// <summary>The refusal text. Written for the model: what happened, why, and the two ways out.</summary>
  private static string ForeignProjectRefusal(string projectName, string verb) =>
    verb + " refused: TIA Portal already has the project '" + projectName + "' open, and this " +
    "session did not open it - it is the user's. " + verb + " closes the current project first, " +
    "which would discard any unsaved edits. " +
    "If you meant to work on that project, call AttachToOpenProject(projectName=\"" + projectName + "\"). " +
    "If you really do want it closed, pass closeForeignProject=true (ask the user first).";

  /// <summary>Name of the user's own open project when closing it would be collateral damage, else null.</summary>
  public string? ForeignOpenProjectName()
  {
    if (!this.HasForeignProject)
    {
      return null;
    }

    try
    {
      return this.CurrentProject?.Name ?? "(unnamed)";
    }
    catch
    {
      return "(unnamed)";
    }
  }

  public bool OpenProject(string projectPath, bool closeForeignProject = false)
  {
    logger?.LogInformation($"Opening project: {projectPath}");

    var foreign = this.ForeignOpenProjectName();
    if (foreign != null && !closeForeignProject)
    {
      this.LastConnectError = Portal.ForeignProjectRefusal(foreign, "OpenProject");
      return false;
    }

    if (this.IsPortalNull())
    {
      // ConnectPortal 现以 PortalException 报硬失败；此处保留 OpenProject 原有 bool 契约
      try
      {
        this.ConnectPortal();
      }
      catch (PortalException ex)
      {
        this.LastConnectError = $"Portal is null and reconnect failed: {ex.Message}";
        return false;
      }
    }

    if (this.CurrentProject != null)
    {
      (this.CurrentProject as Project)?.Close();
      this.CurrentProject = null;
      this._projectOpenedByUs = false;
    }

    if (this._session != null)
    {
      this._session.Close();
      this._session = null;
    }

    try
    {
      this.LastConnectError = null;

      if (string.IsNullOrWhiteSpace(projectPath))
      {
        this.LastConnectError = "projectPath is empty";
        return false;
      }

      if (!File.Exists(projectPath))
      {
        this.LastConnectError = $"Project file not found: {projectPath}";
        return false;
      }

      var projects = this.GetProjects();
      var projectName = Path.GetFileNameWithoutExtension(projectPath);

      if (!string.IsNullOrEmpty(projectName) && projects.Any(p => p.Name.Equals(projectName)))
      {
        // Project is already open
        return this.AttachToOpenProject(projectName);
      }

      // see [5.3.1 Projekt öffnen, S.113]
      var fi = new FileInfo(projectPath);

      try
      {
        this.CurrentProject = this._portal?.Projects.OpenWithUpgrade(fi);
        this._projectOpenedByUs = true;
      }
      catch (Exception ex)
      {
        this.LastConnectError = $"OpenWithUpgrade failed: {ex}";
        this.CurrentProject = null;
        this._projectOpenedByUs = false;
      }

      if (this.CurrentProject != null)
      {
        return true;
      }

      // Fallback: some environments expose Projects.Open(FileInfo) instead.
      try
      {
        var projectsComp = this._portal?.Projects;
        if (projectsComp != null)
        {
          var mOpen = projectsComp.GetType().GetMethod("Open", [typeof(FileInfo),]);
          if (mOpen != null)
          {
            var opened = mOpen.Invoke(projectsComp, [fi,]);
            if (opened is ProjectBase pb)
            {
              this.CurrentProject = pb;
              this.LastConnectError = null;
              return true;
            }
          }
          else
          {
            this.LastConnectError ??= "Projects.Open(FileInfo) method not found";
          }
        }
      }
      catch (TargetInvocationException tie) when (tie.InnerException != null)
      {
        this.LastConnectError =
          $"Projects.Open failed: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
      }
      catch (Exception ex)
      {
        this.LastConnectError = $"Projects.Open failed: {ex}";
      }

      this.LastConnectError ??= "OpenProject returned null (no exception)";
      return false;
    }
    catch (Exception ex)
    {
      this.LastConnectError = ex.ToString();
      return false;
    }
  }

  public bool CreateProject(string directoryPath, string projectName, bool closeForeignProject = false)
  {
    var foreign = this.ForeignOpenProjectName();
    if (foreign != null && !closeForeignProject)
    {
      this.LastConnectError = Portal.ForeignProjectRefusal(foreign, "CreateProject");
      return false;
    }

    logger?.LogInformation($"Creating project: dir={directoryPath}, name={projectName}");

    if (this.IsPortalNull())
    {
      return false;
    }

    try
    {
      if (this.CurrentProject != null)
      {
        (this.CurrentProject as Project)?.Close();
        this.CurrentProject = null;
        this._projectOpenedByUs = false;
      }

      if (this._session != null)
      {
        this._session.Close();
        this._session = null;
      }

      Directory.CreateDirectory(directoryPath);
      var di = new DirectoryInfo(directoryPath);

      var created = this._portal!.Projects.Create(di, projectName);
      this.CurrentProject = created;
      this._projectOpenedByUs = true;
      return this.CurrentProject != null;
    }
    catch (Exception ex)
    {
      logger?.LogError(ex, "CreateProject failed: dir={Dir}, name={Name}", directoryPath, projectName);
      return false;
    }
  }

  public object? GetProjectInfo()
  {
    logger?.LogInformation("Getting project info...");

    if (this.IsPortalNull())
    {
      return null;
    }

    if (this.IsProjectNull())
    {
      return null;
    }

    var project = this.CurrentProject!;

    var info = new
    {
      project.Name,
      project.Path,
      Type = project.GetType().Name,
      IsMultiuserProject = project is MultiuserProject,
      IsLocalSession = this._session != null,
      IsLocalProject = this._session == null,
    };

    return info;
  }

  public bool SaveProject()
  {
    logger?.LogInformation("Saving project...");

    if (this.IsProjectNull())
    {
      return false;
    }

    (this.CurrentProject as Project)?.Save();

    return true;
  }

  public bool SaveAsProject(string path)
  {
    logger?.LogInformation($"Saving project as: {path}");

    if (this.IsProjectNull())
    {
      return false;
    }

    var di = new DirectoryInfo(path);

    (this.CurrentProject as Project)?.SaveAs(di);

    return true;
  }

  public bool CloseProject()
  {
    logger?.LogInformation("Closing project...");

    if (this.IsProjectNull())
    {
      return false;
    }

    (this.CurrentProject as Project)?.Close();
    this.CurrentProject = null;
    this._projectOpenedByUs = false;

    return true;
  }

  #endregion

  #region session

  public List<ProjectBase> GetSessions()
  {
    logger?.LogInformation("Getting open local sessions...");

    if (this.IsPortalNull())
    {
      return [];
    }

    var sessions = new List<ProjectBase>();

    if (this._portal?.LocalSessions == null)
    {
      return sessions;
    }

    sessions.AddRange(this._portal.LocalSessions.Select(session => session.Project));

    return sessions;
  }

  public bool OpenSession(string localSessionPath)
  {
    logger?.LogInformation($"Opening session: {localSessionPath}");

    if (this.IsPortalNull())
    {
      return false;
    }

    if (this._session != null)
    {
      this.CurrentProject = null;
      this._projectOpenedByUs = false;
      this._session?.Close();
      this._session = null;
    }

    try
    {
      var sessions = this.GetSessions();
      var projectName = Path.GetFileNameWithoutExtension(localSessionPath);
      var sessionName = Regex.Replace(projectName, @"_(LS|ES)_\d$", string.Empty, RegexOptions.IgnoreCase);

      if (!string.IsNullOrEmpty(sessionName) && sessions.Any(s => s.Name.Equals(sessionName)))
      {
        // Session is already open
        this._session = this._portal?.LocalSessions.FirstOrDefault(s => s.Project.Name == sessionName);
        // Correctly cast MultiuserProject to Project
      }
      else
      {
        this._session = this._portal?.LocalSessions.Open(new FileInfo(localSessionPath));
        // Correctly cast MultiuserProject to Project
      }

      if (this._session != null)
      {
        // Correctly cast MultiuserProject to Project
        this.CurrentProject = this._session.Project;
        return this.CurrentProject != null;
      }
    }
    catch (Exception ex)
    {
      logger?.LogError(ex, "OpenSession failed: {Path}", localSessionPath);
      return false;
    }

    return false;
  }

  public bool SaveSession()
  {
    logger?.LogInformation("Saving session...");

    if (this.IsSessionNull())
    {
      return false;
    }

    // Save session
    this._session?.Save();

    return true;
  }

  public bool CloseSession()
  {
    logger?.LogInformation("Closing session...");

    if (this.IsSessionNull())
    {
      return false;
    }

    this.CurrentProject = null;
    this._projectOpenedByUs = false;
    this._session?.Close();
    this._session = null;

    return true;
  }

  #endregion

  // #region devices — moved to Portal.Devices.cs

  // #region software — moved to Portal.Software.cs

  // #region alarms — moved to Portal.Alarms.cs

  // #region opcua — moved to Portal.OpcUa.cs

  // #region download — moved to Portal.Download.cs

  // #region online — moved to Portal.Online.cs

  // #region blocks/types — moved to Portal.Blocks.cs

  // #region private helper — moved to Portal.Helpers.cs
}
