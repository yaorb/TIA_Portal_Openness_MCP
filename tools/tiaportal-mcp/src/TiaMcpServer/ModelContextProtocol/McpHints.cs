#region

using System;
using System.Text;

#endregion

namespace TiaMcpServer.ModelContextProtocol;

/// <summary>
///   Central recovery-hint translator. Turns a raw Openness/runtime exception into a short,
///   actionable suffix appended to the generic "Unexpected error …: {ex.Message}" tool errors,
///   so a less-capable AI driver is told WHAT TO DO instead of just seeing a raw stack message.
///   Returns "" when nothing useful can be inferred (keeps clean errors clean).
///   One helper, injected once into the 175 uniform catch sites — no per-tool edits.
/// </summary>
public static class McpHints
{
  public static string Recovery(Exception? ex)
  {
    var m = McpHints.Flatten(ex);
    if (m.Length == 0)
    {
      return "";
    }

    // not connected
    if (McpHints.Has(m, "not connected") || McpHints.Has(m, "connect first") || McpHints.Has(m, "_portal") ||
      McpHints.Has(m, "no tia portal"))
    {
      return McpHints.Tip("call Connect first (the server also auto-connects when a TIA Portal is already running).");
    }

    // no project bound
    if (McpHints.Has(m, "no project is open") || McpHints.Has(m, "project is null"))
    {
      return McpHints.Tip(
        "open a project first: AttachToOpenProject(projectName) if it is already open in the TIA UI, else OpenProject(path) or CreateProject.");
    }

    // project already open by another session/UI
    if (McpHints.Has(m, "already open") || McpHints.Has(m, "opened by") || McpHints.Has(m, "in use by another"))
    {
      return McpHints.Tip(
        "the project is already open elsewhere — use AttachToOpenProject(projectName) instead of OpenProject.");
    }

    // stale handle after project switch / TIA UI opened the project (very common)
    if (McpHints.Has(m, "disposed"))
    {
      return McpHints.Tip(
        "the software/project handle went stale (you switched project, or the TIA UI opened it). Re-bind with AttachToOpenProject(projectName) or GetProjectTree, then retry — do not reuse the old handle.");
    }

    // online-mode lock (export/import/compile require offline)
    if (McpHints.Has(m, "online mode") || McpHints.Has(m, "not permitted in online") ||
      McpHints.Has(m, "supported in online"))
    {
      return McpHints.Tip(
        "this operation needs the target offline. Call GoOfflineAll (releases the UI's online session and all others) or GoOffline(softwarePath), then retry.");
    }

    // name / path not found  -> covers the wrong-softwarePath / wrong-block-name case
    if (McpHints.Has(m, "not found") || McpHints.Has(m, "does not exist") || McpHints.Has(m, "could not be found") ||
      McpHints.Has(m, "no such") || McpHints.Has(m, "unable to locate") || McpHints.Has(m, "cannot find"))
    {
      return McpHints.Tip(
        "the name/path may be wrong — call GetProjectTree / GetSoftwareTree / GetBlocks to read the REAL names (plc software path defaults to 'PLC_1', HMI to 'HMI_RT_1') instead of guessing.");
    }

    // version mismatch (V20 exe vs V21 XML etc.)
    if (McpHints.Has(m, "engineering version") || (McpHints.Has(m, "version") && McpHints.Has(m, "not supported")))
    {
      return McpHints.Tip(
        "TIA version mismatch — run the exe that matches the installed TIA (V20 vs V21), and ensure imported XML's <Engineering version> matches.");
    }

    // openness group / permissions
    if (McpHints.Has(m, "openness") &&
      (McpHints.Has(m, "group") || McpHints.Has(m, "permission") || McpHints.Has(m, "denied")))
    {
      return McpHints.Tip(
        "add the Windows user to the 'Siemens TIA Openness' group (call EnsureOpennessUserGroup), then retry.");
    }

    // know-how protected blocks
    if (McpHints.Has(m, "know-how") || McpHints.Has(m, "knowhow") || McpHints.Has(m, "protected"))
    {
      return McpHints.Tip(
        "the block is know-how protected and cannot be read/exported via Openness; unprotect it in the TIA UI.");
    }

    // download blocked because an interface/static-var change needs the CPU stopped
    if (McpHints.Has(m, "stopmodules") || (McpHints.Has(m, "download") && McpHints.Has(m, "unhandled")))
    {
      return McpHints.Tip(
        "the change alters a block interface (e.g. a new static VAR -> instance DB rebuild), so a RUN download is refused. Call DownloadToPlc(stopBeforeDownload=true) for a brief stop-download, or do 'download software (changes only)' in the TIA UI to stay in RUN.");
    }

    // export refused because the software/block is inconsistent
    if (McpHints.Has(m, "not consistent") || McpHints.Has(m, "inconsistent") || McpHints.Has(m, "isconsistent"))
    {
      return McpHints.Tip(
        "the block/software is inconsistent and cannot be exported — call CompileSoftware first to make it consistent, then retry the export.");
    }

    return "";
  }

  private static string Tip(string s) => "  ▶ RECOVERY: " + s;

  private static bool Has(string haystack, string needle) =>
    haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

  // Concatenate this exception's message with its inner chain so signatures buried in
  // inner Openness exceptions are still matched.
  private static string Flatten(Exception? ex)
  {
    if (ex == null)
    {
      return "";
    }

    var sb = new StringBuilder();
    for (var e = ex; e != null; e = e.InnerException)
    {
      if (sb.Length > 0)
      {
        sb.Append(" | ");
      }

      sb.Append(e.Message);
    }

    return sb.ToString();
  }
}
