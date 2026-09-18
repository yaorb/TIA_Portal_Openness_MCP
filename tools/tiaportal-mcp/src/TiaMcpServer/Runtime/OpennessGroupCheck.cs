#region

using System;

#endregion

namespace TiaMcpServer.Runtime;

/// <summary>
///   The verdict on "may this user use Openness?" — decided in one place, in both languages.
///
///   Why it is not two lines inside the CLI: the answer was wrong. The check asked whether the
///   current *logon token* carries 'Siemens TIA Openness', got "no", and reported
///   "current user NOT in 'Siemens TIA Openness' group" — while the local group's member list
///   had that very user in it. Measured on the machine this was written on: the group's SID
///   (S-1-5-21-…-1012) is absent from `whoami /groups` but present in the group's member list,
///   i.e. the group was added *after* this logon session started. Windows only puts a group
///   into the token at logon, so the two states are genuinely different problems:
///     * not a member      → add the user, then sign out/in;
///     * member, stale token → just sign out/in; adding again changes nothing.
///   Reporting both as "not in the group" sent people to add themselves over and over (and to
///   chase an admin for rights they already had).
///
///   The mapping lives here, dependency-free, so the offline suite can pin it — in particular
///   the invariant that **no non-member state may ever come out Ok**, which is the property a
///   wrong "OK" would silently destroy (the exact failure this repo keeps guards for).
/// </summary>
public static class OpennessGroupCheck
{
  /// <summary>The Windows local group TIA Portal's Openness component requires.</summary>
  public const string GroupName = "Siemens TIA Openness";

  public static OpennessGroupVerdict Classify(OpennessGroupObservation probe)
  {
    if (probe.TokenCarriesGroup == true)
    {
      return new OpennessGroupVerdict
      {
        State = OpennessGroupState.InGroup,
        Ok = true,
        DetailEn = $"current user is in '{OpennessGroupCheck.GroupName}' (this logon session's token carries it)",
        DetailZh = $"当前用户已在 '{OpennessGroupCheck.GroupName}' 组（本次登录会话的令牌里带着它）",
      };
    }

    // The group itself missing is decided first: "you are not a member of a group that does not
    // exist" is technically true and completely useless as a fix instruction.
    if (probe.GroupExists == false)
    {
      return new OpennessGroupVerdict
      {
        State = OpennessGroupState.GroupMissing,
        Ok = false,
        DetailEn = $"the local group '{OpennessGroupCheck.GroupName}' does not exist on this machine",
        DetailZh = $"本机不存在本地组 '{OpennessGroupCheck.GroupName}'",
        FixEn = "That group is created by TIA Portal's Openness component. Re-run the TIA Portal setup " +
          "and add the 'Openness' component, then sign out/in. On a domain-managed machine the group " +
          "may come from policy instead — ask your administrator.",
        FixZh = "这个组由 TIA Portal 的 Openness 组件创建。重新运行 TIA Portal 安装程序补装『Openness』组件，" +
          "然后注销重登。若本机由域统一管理，这个组也可能由策略下发——请找管理员确认。",
      };
    }

    if (probe.TokenCarriesGroup == false && probe.MemberOnDisk == true)
    {
      return new OpennessGroupVerdict
      {
        State = OpennessGroupState.MemberWithStaleToken,
        Ok = false,
        DetailEn = $"you ARE a member of '{OpennessGroupCheck.GroupName}' (it is in the local group's " +
          "member list), but this Windows logon session started before that change, so the group is " +
          "NOT in the session token yet — Openness authorization cannot see it.",
        DetailZh = $"你确实在 '{OpennessGroupCheck.GroupName}' 组里（本机组的成员列表里有你），但本次 Windows " +
          "登录会话早于这次改动，令牌里还没有这个组——Openness 授权现在看不到它。",
        FixEn = "Sign out and back in (or reboot) — that is what puts the group into the token — then re-run " +
          "`tia doctor`. Adding yourself again does NOT help: you are already a member, and nothing on this " +
          "machine is missing.",
        FixZh = "注销后重新登录（或重启）——组要那时才会进令牌——再跑一次 `tia doctor`。重复添加自己没有用：" +
          "你已经是成员，本机不缺任何东西。",
      };
    }

    if (probe.TokenCarriesGroup == false && probe.MemberOnDisk == false)
    {
      return new OpennessGroupVerdict
      {
        State = OpennessGroupState.NotAMember,
        Ok = false,
        DetailEn = $"current user is not in '{OpennessGroupCheck.GroupName}' (neither in this logon " +
          "session's token nor in the local group's member list)",
        DetailZh = $"当前用户不在 '{OpennessGroupCheck.GroupName}' 组（登录令牌与本机组的成员列表里都没有）",
        FixEn = "Run Doctor with fix=true / `tia doctor --fix` (may prompt UAC to add you), or add your " +
          "Windows user to the local group by hand (lusrmgr.msc), then sign out/in. Admin rights required.",
        FixZh = "运行 Doctor（fix=true）或 `tia doctor --fix`（可能弹 UAC 把你加进去），或用 lusrmgr.msc 手动把" +
          "当前 Windows 用户加入该本地组，然后注销重登。需要管理员权限。",
      };
    }

    // Nothing usable came back. Say so — "not verified" and "not a member" are different
    // conclusions, and this check exists precisely so people do not have to guess which one it is.
    var why = probe.ProbeError ?? "the membership probes returned no result";
    return new OpennessGroupVerdict
    {
      State = OpennessGroupState.Unknown,
      Ok = false,
      DetailEn = $"not in this logon session's token, and the local group's member list could not be " +
        $"read ({why}) — treat as NOT verified",
      DetailZh = $"本次登录会话的令牌里没有这个组，且读不到本机组的成员列表（{why}）—— 按「未验证」处理",
      FixEn = "Re-run `tia doctor`. If it keeps failing, check by hand: `net localgroup \"" +
        OpennessGroupCheck.GroupName + "\"`. If you were added recently, sign out and back in.",
      FixZh = "重跑 `tia doctor`。若一直失败，手工核对：`net localgroup \"" + OpennessGroupCheck.GroupName +
        "\"`。如果你是最近才被加进去的，注销重登即可。",
    };
  }
}

/// <summary>
///   What the two independent probes saw. Both are nullable on purpose: "probe failed" must stay
///   distinguishable from "probe said no", or the verdict collapses back into a guess.
/// </summary>
public sealed class OpennessGroupObservation
{
  /// <summary>Does the current process token carry the group? null = probe failed.</summary>
  public bool? TokenCarriesGroup;

  /// <summary>Is the current user in the local group's member list (nested groups included)? null = unknown.</summary>
  public bool? MemberOnDisk;

  /// <summary>Does the local group exist at all? null = unknown.</summary>
  public bool? GroupExists;

  /// <summary>The first probe failure, verbatim, so the report can name it instead of hiding it.</summary>
  public string? ProbeError;
}

/// <summary>How a session stands with respect to the Openness group.</summary>
public enum OpennessGroupState
{
  /// <summary>The token carries the group: Openness authorization can see it.</summary>
  InGroup,

  /// <summary>Member on disk, but this logon session predates it — needs a sign-out/in, not a re-add.</summary>
  MemberWithStaleToken,

  /// <summary>Not a member anywhere: the user has to be added (then sign out/in).</summary>
  NotAMember,

  /// <summary>The local group does not exist — usually TIA was installed without the Openness component.</summary>
  GroupMissing,

  /// <summary>A probe failed; the verdict is "not verified", not "no".</summary>
  Unknown,
}

/// <summary>The answer the doctors print: one state, and text for each language.</summary>
public sealed class OpennessGroupVerdict
{
  public string DetailEn = "", DetailZh = "";
  public string? FixEn, FixZh;
  public bool Ok;
  public OpennessGroupState State;

  public string Detail(bool zh) =>
    zh
      ? this.DetailZh
      : this.DetailEn;

  public string? Fix(bool zh) =>
    zh
      ? this.FixZh
      : this.FixEn;
}
