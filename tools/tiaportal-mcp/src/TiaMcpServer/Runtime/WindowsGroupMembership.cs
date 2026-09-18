#region

using System;
using System.Collections.Generic;
using System.DirectoryServices.AccountManagement;
using System.Security.Principal;

#endregion

namespace TiaMcpServer.Runtime;

/// <summary>
///   Reads the two facts the Openness group verdict needs, and keeps them apart.
///
///   1. <b>Token</b>: does this process token carry the group? Windows adds local groups to a
///      token only at logon, so a user added five minutes ago is *not* in it — that is the
///      normal, expected state right after an install, and it is also the state people get
///      stuck in when nothing tells them to sign out.
///   2. <b>Group store</b>: is that user in the local group's member list? This is read from
///      SAM, so it is true immediately after the add — which is what makes the two states
///      tellable apart. Membership is compared by <b>SID</b> (not by name) and nested groups
///      are included, so domain accounts and "user is in the group via another group" both
///      answer correctly.
///
///   Both probes report failure as null with the reason attached: a thrown probe is not a "no".
/// </summary>
public static class WindowsGroupMembership
{
  public static OpennessGroupObservation Probe(string groupName)
  {
    var probe = new OpennessGroupObservation();
    var errors = new List<string>();
    SecurityIdentifier? userSid = null;

    try
    {
      using var identity = WindowsIdentity.GetCurrent();
      userSid = identity.User;
      probe.TokenCarriesGroup = new WindowsPrincipal(identity).IsInRole(groupName);
    }
    catch (Exception ex)
    {
      // 探测失败不等于「不在组里」。原实现把两者都落进 false，于是输出了一句
      // 「当前用户不在组里」—— 一个查不到的结论被写成了确定的事实。
      errors.Add("logon token: " + ex.Message);
    }

    try
    {
      using var context = new PrincipalContext(ContextType.Machine);
      using var group = GroupPrincipal.FindByIdentity(context, IdentityType.Name, groupName);
      if (group == null)
      {
        probe.GroupExists = false;
        probe.MemberOnDisk = false;
      }
      else
      {
        probe.GroupExists = true;
        probe.MemberOnDisk = WindowsGroupMembership.ContainsUser(group, userSid, errors);
      }
    }
    catch (Exception ex)
    {
      errors.Add("local group store: " + ex.Message);
    }

    probe.ProbeError = errors.Count > 0
      ? string.Join("; ", errors)
      : null;
    return probe;
  }

  private static bool? ContainsUser(GroupPrincipal group, SecurityIdentifier? userSid, List<string> errors)
  {
    if (userSid == null)
    {
      errors.Add("current user SID unavailable");
      return null;
    }

    // 递归展开会在成员 SID 解析不出来时抛（例如卸载残留的账户），这时退一步只比直接成员：
    // 「通过嵌套组成为成员」的人，令牌里本来就会带上这个组 —— 走到这里的基本都是直接成员。
    // 只有两条路都失败才算「查不到」，且两条的原因都要带出去。
    var recursiveError = "";
    try
    {
      return WindowsGroupMembership.EnumerateContains(group, true, userSid);
    }
    catch (Exception ex)
    {
      recursiveError = ex.Message;
    }

    try
    {
      return WindowsGroupMembership.EnumerateContains(group, false, userSid);
    }
    catch (Exception ex)
    {
      errors.Add("read members (recursive: " + recursiveError + "; direct: " + ex.Message + ")");
      return null;
    }
  }

  private static bool EnumerateContains(GroupPrincipal group, bool recursive, SecurityIdentifier userSid)
  {
    using var members = group.GetMembers(recursive);
    foreach (var member in members)
    {
      if (member.Sid != null && member.Sid.Equals(userSid))
      {
        return true;
      }
    }

    return false;
  }
}
