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
    "[L0][Diagnostics] One-call environment doctor for non-experts. Checks TIA install, Openness group membership, and connection/project state, and returns a plain-language diagnosis with the exact fix per problem — including the difference between 'not in the group' and 'in the group, but this Windows logon session predates it' (the second one only needs a sign-out/in). When fix=true (default) it ENSURES Openness group membership: it adds the current user only if they are not a member yet (may prompt a Windows UAC dialog). Read-only apart from that one fix. Call this first when setup is failing or you are unsure the environment is ready.")]
  public static async Task<ResponseDoctor> Doctor(
    [Description(
      "fix: when true (default), ensure Openness group membership (adds the user if missing, may prompt UAC). false = read-only diagnosis, no prompt.")]
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
      //    Same shared verdict as `tia doctor` (OpennessGroupCheck): the token and the local
      //    group store are probed separately, because "not a member" and "member, but this
      //    logon session predates the change" need different fixes and used to be reported
      //    as the same thing.
      var groupProbe = WindowsGroupMembership.Probe(OpennessGroupCheck.GroupName);

      // Repair only what a repair can fix: re-adding an existing member changes nothing and can
      // still pop a UAC prompt, and the stale-token case is fixed by signing out/in.
      if (fix && groupProbe.MemberOnDisk != true && groupProbe.GroupExists != false)
      {
        try
        {
          if (!await Openness.IsUserInGroup())
          {
            groupProbe.ProbeError = McpServer.JoinProbeError(groupProbe.ProbeError,
              "the add attempt reported failure (admin rights?)");
          }

          groupProbe = WindowsGroupMembership.Probe(OpennessGroupCheck.GroupName);
        }
        catch (Exception ex)
        {
          // 修复失败必须进报告：否则下面那句「不在组里」会被读成「加过了还是没加进去」。
          groupProbe.ProbeError = McpServer.JoinProbeError(groupProbe.ProbeError, "add to group: " + ex.Message);
        }
      }

      var groupVerdict = OpennessGroupCheck.Classify(groupProbe);
      var groupOk = groupVerdict.Ok;
      checks.Add(new DoctorCheck
      {
        Name = "Openness user group",
        Ok = groupVerdict.Ok,
        Detail = groupVerdict.DetailEn,
        Fix = groupVerdict.FixEn,
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
        // Same distinction as Bootstrap: only a non-member can be helped by adding one.
        next = groupVerdict.State == OpennessGroupState.MemberWithStaleToken
          ? "(sign out and back in — the group is not in this logon session's token)"
          : "EnsureOpennessUserGroup";
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

  /// <summary>Appends a probe/fix failure to the detail that will be shown, keeping every reason.</summary>
  private static string JoinProbeError(string? existing, string addition) =>
    string.IsNullOrWhiteSpace(existing)
      ? addition
      : existing + "; " + addition;
}
