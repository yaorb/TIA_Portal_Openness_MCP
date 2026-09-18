#region

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TiaMcpServer.Runtime;
using TiaMcpServer.Siemens;

#endregion

namespace TiaMcpServer.ModelContextProtocol;

// Doctor: one-call environment diagnosis for non-experts / fresh machines.
// Ported from the pre-split repo where it shipped alongside the lite profile;
// SKILL.md documents it, so the tool must exist in every released build.
public static partial class McpServer
{
  [McpServerTool(Name = "Doctor")]
  [Description(
    "[L0][Diagnostics] One-call environment doctor for non-experts. Checks TIA install, Openness group membership, and connection/project state, and returns a plain-language diagnosis with the exact fix per problem. When fix=true (default) it ENSURES Openness group membership (adds the current user; may prompt a Windows UAC dialog). Read-only apart from that one fix. Call this first when setup is failing or you are unsure the environment is ready.")]
  public static async Task<ResponseDoctor> Doctor(
    [Description(
      "fix: when true (default), ensure Openness group membership (adds user, may prompt UAC). false = read-only diagnosis, no prompt.")]
    bool fix = true)
  {
    try
    {
      var checks = new List<DoctorCheck>();

      // 1) Environment prerequisites, shared with `tia doctor` so the two cannot drift.
      //    Covers TIA install, Openness assembly resolution (TIA can be installed WITHOUT
      //    Openness — the old check said OK and the engine then died on first call),
      //    engine/TIA version match, .NET Framework 4.8, and Windows MOTW blocking.
      var inUse = Engineering.TiaMajorVersion == 0
        ? (int?)null
        : Engineering.TiaMajorVersion;
      var detected = Engineering.DetectTiaMajorVersion();
      foreach (var c in EnvironmentDoctor.Run(EngineRouter.CompiledTiaMajorVersion, inUse ?? detected))
      {
        checks.Add(new DoctorCheck
        {
          Name = c.NameEn, Ok = c.Ok, Detail = c.DetailEn, Fix = c.FixEn,
        });
      }

      var envOk = checks.All(c => c.Ok);
      var firstEnvProblem = checks.FirstOrDefault(c => !c.Ok)?.Name;

      // 2) Openness group membership (+ optional auto-fix)
      bool groupOk;
      if (fix)
      {
        try
        {
          groupOk = await Openness.IsUserInGroup();
        }
        catch
        {
          groupOk = false;
        }
      }
      else
      {
        try
        {
          groupOk = Openness.IsUserInGroupNoFix();
        }
        catch
        {
          groupOk = false;
        }
      }

      checks.Add(new DoctorCheck
      {
        Name = "Openness user group",
        Ok = groupOk,
        Detail = groupOk
          ? "current user is in 'Siemens TIA Openness' group"
          : "current user NOT in 'Siemens TIA Openness' group",
        Fix = groupOk
          ? null
          : "Run Doctor with fix=true (prompts UAC to add you), or manually add your Windows user to the 'Siemens TIA Openness' local group and sign out/in. Admin rights required.",
      });

      // 3) Connection + project state
      var connected = false;
      string? projectName = null;
      string? stateProbeError = null;
      try
      {
        var st = McpServer.Portal.GetState();
        connected = st?.IsConnected ?? false;
        projectName = st?.Project;
      }
      catch (Exception ex)
      {
        // 探测抛异常时**不能表现成「没连上」**——那是另一个结论，Doctor 会因此给出错误诊断，
        // 而这个工具存在的意义就是别让人猜。原因进 Detail，让人能分辨「没连」和「查不到」。
        stateProbeError = ex.Message;
      }

      var hasProject = !string.IsNullOrWhiteSpace(projectName) && projectName != "-";
      checks.Add(new DoctorCheck
      {
        Name = "TIA connection / project",
        Ok = connected,
        Detail = stateProbeError != null
          ? $"could not read the connection state ({stateProbeError}) — treat as NOT verified"
          : connected
            ? hasProject
              ? $"connected, project '{projectName}' open"
              : "connected, no project bound"
            : "not connected",
        Fix = stateProbeError != null
          ? "Re-run Doctor; if it keeps failing, call RunCapabilitySelfTest and read the error detail."
          : connected
            ? hasProject
              ? null
              : "Call AttachToOpenProject (if a project is open in TIA UI) or OpenProject/CreateProject."
            : "Call Connect (first call may pop an Openness authorization dialog in TIA — click Yes).",
      });

      string next;
      if (!envOk)
      {
        next = $"(fix first: {firstEnvProblem})";
      }
      else if (!groupOk)
      {
        next = "EnsureOpennessUserGroup";
      }
      else if (!connected)
      {
        next = "Connect";
      }
      else if (!hasProject)
      {
        next = "AttachToOpenProject";
      }
      else
      {
        next = "GetProjectTree";
      }

      var ready = envOk && groupOk;
      var failed = checks.Where(c => !c.Ok).Select(c => c.Name).ToList();
      var summary = ready && connected && hasProject
        ? "Environment healthy — project open, ready to work."
        : ready
          ? "Environment OK — connect/open a project next."
          : $"Not ready. Fix: {string.Join("; ", failed)}.";

      return new ResponseDoctor
      {
        Ready = ready,
        Checks = checks,
        RecommendedNextTool = next,
        Summary = summary,
        Message = summary,
        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, },
      };
    }
    catch (Exception ex) when (ex is not McpException)
    {
      throw new McpProtocolException($"Doctor unexpected error: {ex.Message}", ex, McpErrorCode.InternalError);
    }
  }
}
