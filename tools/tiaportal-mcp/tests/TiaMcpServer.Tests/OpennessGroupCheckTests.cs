using System;
using TiaMcpServer.Runtime;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// Openness 用户组判定。这里盯的是一条**已经错过一次**的结论：
    /// 原实现只问「登录令牌里有没有这个组」，答「没有」，然后写成「当前用户不在组里」——
    /// 而本机组成员列表里明明有这个人（实测：组的 SID 不在 whoami /groups 里，但组成员里有）。
    /// 两者修法完全不同：一个是「把自己加进去」，一个是「注销重登」；再去 --fix 只是白弹 UAC。
    ///
    /// 所以用例分两半：一半钉住五种状态各自的文案与修法，一半（更要紧）钉住不变量——
    /// **只有 InGroup 才允许 Ok**。这条一破，doctor 就会在没有权限的状态下说环境正常，
    /// 正是这个仓一直在防的那种假绿灯。
    /// </summary>
    internal static class OpennessGroupCheckTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var inGroup = Classify(true, true, true);
            check(inGroup.State == OpennessGroupState.InGroup && inGroup.Ok, "令牌里有组 → InGroup 且绿灯");
            check(inGroup.FixEn == null && inGroup.FixZh == null, "已经在组里就不该再给「修法」");

            // 实测到的那种：人是成员，令牌过期。原实现把它报成 NotAMember，修法指向「加人」。
            var stale = Classify(false, true, true);
            check(stale.State == OpennessGroupState.MemberWithStaleToken, "成员 + 令牌过期 → 独立状态（不许报成「不在组里」）");
            check(!stale.Ok, "令牌里没有组 → 不许给绿灯（Openness 授权看不到它）");
            check(stale.DetailZh.Contains("确实在") && stale.DetailEn.Contains("ARE a member"),
                "文案必须说清「你在组里」，否则读的人会去加一个已经存在的成员");
            check(stale.FixZh!.Contains("注销") && stale.FixEn!.Contains("Sign out"), "修法必须是注销重登");
            check(!stale.FixZh!.Contains("--fix") && !stale.FixEn!.Contains("--fix"),
                "这一种修法不能让人去跑 --fix：重复添加成员无效，还会白弹一次 UAC");

            var notMember = Classify(false, false, true);
            check(notMember.State == OpennessGroupState.NotAMember && !notMember.Ok, "确实不是成员 → NotAMember");
            check(notMember.DetailZh.Contains("不在") , "不是成员时文案要说「不在组里」");
            check(notMember.FixZh!.Contains("--fix") && notMember.FixEn!.Contains("--fix"), "不是成员 → 修法要包含加入（--fix / lusrmgr.msc）");
            check(notMember.FixZh!.Contains("注销"), "加完还得重登：组要下次登录才进令牌");

            var missing = Classify(false, false, false);
            check(missing.State == OpennessGroupState.GroupMissing && !missing.Ok, "组根本不存在 → 单独一种状态");
            check(missing.FixZh!.Contains("Openness 组件"), "组不存在多半是没装 Openness 组件，修法要指向它");

            // 探测失败要说「没验证成」，不能伪装成任何确定结论。
            var unknown = OpennessGroupCheck.Classify(new OpennessGroupObservation
            {
                TokenCarriesGroup = null,
                MemberOnDisk = null,
                GroupExists = null,
                ProbeError = "sam probe exploded",
            });
            check(unknown.State == OpennessGroupState.Unknown && !unknown.Ok, "探测失败 → Unknown，且不给绿灯");
            check(unknown.DetailZh.Contains("sam probe exploded") && unknown.DetailEn.Contains("sam probe exploded"),
                "探测失败的原因必须出现在报告里（「查不到」和「不在」不是一个结论）");

            // 不变量：全枚举 3×3×3 种探测组合，Ok 只能来自 InGroup。
            var combinations = 0;
            var greens = 0;
            var tokenHasIt = 0;
            foreach (bool? token in new bool?[] { true, false, null })
            {
                foreach (bool? member in new bool?[] { true, false, null })
                {
                    foreach (bool? exists in new bool?[] { true, false, null })
                    {
                        combinations++;
                        if (token == true)
                        {
                            tokenHasIt++;
                        }

                        var verdict = OpennessGroupCheck.Classify(new OpennessGroupObservation
                        {
                            TokenCarriesGroup = token,
                            MemberOnDisk = member,
                            GroupExists = exists,
                        });
                        if (verdict.Ok)
                        {
                            greens++;
                        }

                        check(verdict.Ok == (verdict.State == OpennessGroupState.InGroup),
                            $"Ok 只能来自 InGroup（token={token} member={member} exists={exists} → {verdict.State}/{verdict.Ok}）");
                        check(!string.IsNullOrWhiteSpace(verdict.DetailEn) && !string.IsNullOrWhiteSpace(verdict.DetailZh),
                            $"每种状态都要有中英文说明（token={token} member={member} exists={exists}）");
                        check(verdict.Ok || verdict.FixEn != null || verdict.FixZh != null || verdict.State == OpennessGroupState.Unknown,
                            $"非绿灯状态必须给出下一步（token={token} member={member} exists={exists}）");
                    }
                }
            }

            // 哨兵：绿灯数必须恰好等于「令牌里有组」的组合数。多了 = 在没权限时说环境正常；
            // 少了 = 判定只会摇头，那它就没在判定，上面那堆断言全部失去意义。
            check(combinations == 27, "组合数应为 27（实际 " + combinations + "）");
            check(greens == tokenHasIt,
                $"绿灯数必须等于「令牌里有组」的组合数（绿灯 {greens}，令牌有组 {tokenHasIt}）");
        }

        private static OpennessGroupVerdict Classify(bool? token, bool? member, bool? exists) =>
            OpennessGroupCheck.Classify(new OpennessGroupObservation
            {
                TokenCarriesGroup = token,
                MemberOnDisk = member,
                GroupExists = exists,
            });
    }
}
