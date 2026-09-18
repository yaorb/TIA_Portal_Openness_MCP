"""面向反射发现的可见性闸：带 MCP 特性的方法/类型必须是 public。

为什么要有这道闸：引擎不是靠人工登记，而是靠**反射扫描**把这些成员捞出来的 ——
Program.cs 用 `mcp.WithPromptsFromAssembly()`；工具侧同理（`McpServer.GetMcpToolNames()`
用 `BindingFlags.Public | BindingFlags.Static` 取自己的方法）。
于是把成员收窄成 private/internal 之后，那个 prompt / tool 会从 MCP 的列表里
**静默消失**：编译照过，离线回归套件也不覆盖这些文件，除了这道闸没有别的东西看得见。

真踩过：一次「能收窄就收窄」的自动清理把
`McpPrompts.ExportBlocks / ExportTypes / ExportBlocksAsDocuments` 改成了 private ——
3 个 prompt 就此从列表里消失。同一批清理还把离线单测工程要用的成员收成 private，
那次编不过（由 offline-checks.yml 的离线套件兜住了），但这 3 个 prompt 没有任何东西兜。

规则（两条，都是硬性的 —— 挂上特性本身就是「外部要用」的声明，不是内部实现细节）：
  1. 带 [McpServerTool] / [McpServerPrompt] / [McpServerResource] 的方法必须 `public static`；
  2. 带 [McpServerToolType] / [McpServerPromptType] / [McpServerResourceType] 的类型必须 `public`。

为什么不能靠编译器：C# 里 private 方法挂特性完全合法，只是没人会去扫它。

用法：
    python scripts/Check-McpAttributeVisibility.py            # 0=干净 1=有违规 2=闸门自己坏了
    python scripts/Check-McpAttributeVisibility.py --selftest # 哨兵：注入一个 private 特性方法，必须被抓到
"""
import io
import os
import re
import sys

ROOT = 'tools/tiaportal-mcp/src/TiaMcpServer'

# 特性名 → 它标注的是「成员」还是「类型」
MEMBER_ATTRS = ('McpServerTool', 'McpServerPrompt', 'McpServerResource')
TYPE_ATTRS = ('McpServerToolType', 'McpServerPromptType', 'McpServerResourceType')


def mask(text):
    """把注释与字符串字面量换成空格，长度与行号保持不变。

    必须做：特性参数里全是字符串（`Name = "ExportBlocks"`、多行 `[Description("…")]`），
    不掩码就无法可靠地找到「特性段结束、声明开始」的位置。
    """
    out = list(text)
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '/' and i + 1 < n and text[i + 1] == '/':
            while i < n and text[i] != '\n':
                out[i] = ' '
                i += 1
        elif c == '/' and i + 1 < n and text[i + 1] == '*':
            out[i] = out[i + 1] = ' '
            i += 2
            while i < n and not (text[i] == '*' and i + 1 < n and text[i + 1] == '/'):
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
            if i < n:
                out[i] = ' '
                if i + 1 < n:
                    out[i + 1] = ' '
                i += 2
        elif c == '@' and i + 1 < n and text[i + 1] == '"':          # 逐字字符串 @"…"
            out[i] = out[i + 1] = ' '
            i += 2
            while i < n:
                if text[i] == '"':
                    if i + 1 < n and text[i + 1] == '"':
                        out[i] = out[i + 1] = ' '
                        i += 2
                        continue
                    out[i] = ' '
                    i += 1
                    break
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
        elif c == '"' and i + 2 < n and text[i + 1] == '"' and text[i + 2] == '"':   # 原始字符串 """…"""
            out[i] = out[i + 1] = out[i + 2] = ' '
            i += 3
            while i < n:
                if text[i] == '"' and i + 2 < n and text[i + 1] == '"' and text[i + 2] == '"':
                    out[i] = out[i + 1] = out[i + 2] = ' '
                    i += 3
                    break
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
        elif c == '$' and i + 1 < n and text[i + 1] == '@' and i + 2 < n and text[i + 2] == '"':
            out[i] = out[i + 1] = out[i + 2] = ' '
            i += 3
            while i < n:
                if text[i] == '"':
                    if i + 1 < n and text[i + 1] == '"':
                        out[i] = out[i + 1] = ' '
                        i += 2
                        continue
                    out[i] = ' '
                    i += 1
                    break
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
        elif c == '$' and i + 1 < n and text[i + 1] == '"':          # 插值字符串：连 {} 一起掩掉
            out[i] = out[i + 1] = ' '
            i += 2
            depth = 0
            while i < n:
                d = text[i]
                if d == '\\':
                    out[i] = ' '
                    if i + 1 < n:
                        out[i + 1] = ' '
                    i += 2
                    continue
                if d == '{':
                    depth += 1
                elif d == '}':
                    depth = max(0, depth - 1)
                elif d == '"' and depth == 0:
                    out[i] = ' '
                    i += 1
                    break
                if d != '\n':
                    out[i] = ' '
                i += 1
        elif c == '"':                                               # 普通字符串
            out[i] = ' '
            i += 1
            while i < n:
                if text[i] == '\\':
                    out[i] = ' '
                    if i + 1 < n:
                        out[i + 1] = ' '
                    i += 2
                    continue
                if text[i] == '"':
                    out[i] = ' '
                    i += 1
                    break
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
        elif c == "'":                                               # 字符字面量
            out[i] = ' '
            i += 1
            while i < n:
                if text[i] == '\\':
                    out[i] = ' '
                    if i + 1 < n:
                        out[i + 1] = ' '
                    i += 2
                    continue
                if text[i] == "'":
                    out[i] = ' '
                    i += 1
                    break
                out[i] = ' '
                i += 1
        else:
            i += 1
    return ''.join(out)


def skip_attributes(s, i):
    """从 s[i]（一个 '['）起，跳过连续的特性段与空白，返回声明的起始下标。"""
    j, n = i, len(s)
    while j < n:
        if s[j].isspace():
            j += 1
            continue
        if s[j] == '[':
            depth = 0
            while j < n:
                if s[j] == '[':
                    depth += 1
                elif s[j] == ']':
                    depth -= 1
                    if depth == 0:
                        j += 1
                        break
                j += 1
            continue
        break
    return j


# 从声明起点读到第一个 ( 或 { 为止。**不能加 ^ 锚点**：本式用 re.search(s, pos) 调用，
# 而 ^ 锚定的是整个字符串的开头（非 pos），加了就永远匹配不到 —— 哨兵正是这么抓出来的。
# [^;{=] 排除语句/块/赋值边界，保证不会越过声明去吃下一段代码。
DECL = re.compile(r'[^;{=]{0,300}?[({]', re.S)


def scan_file(masked, display):
    """返回 (带 MCP 特性的声明总数, [(行号, 原因, 种类, 名字, 声明片段)])。"""
    found = []
    total = 0
    for m in re.finditer(r'\[McpServer(Tool|Prompt|Resource)(Type)?\b', masked):
        kind = 'type' if m.group(2) else 'member'
        j = skip_attributes(masked, m.start())
        dm = DECL.search(masked, j, j + 400)
        if not dm:
            continue
        total += 1
        decl = dm.group(0)
        line = masked[:m.start()].count('\n') + 1
        name = re.search(r'(\w+)\s*[({]', decl)
        name = name.group(1) if name else '?'
        if 'public' not in decl:
            found.append((line, 'missing-public', kind, name, decl.strip().replace('\n', ' ')[:90]))
        elif kind == 'member' and 'static' not in decl:
            found.append((line, 'missing-static', kind, name, decl.strip().replace('\n', ' ')[:90]))
    return total, found


def load(root):
    out = []
    for dp, _, fs in os.walk(root):
        for f in sorted(fs):
            if f.endswith('.cs'):
                p = os.path.join(dp, f)
                out.append((p, io.open(p, encoding='utf-8-sig', errors='replace').read()))
    return out


def run(files, extra=None):
    """extra: (display, text) 供哨兵注入。返回 (声明总数, [(文件, 行, 原因, 种类, 名字, 声明)])。"""
    bad = []
    total = 0
    items = list(files)
    if extra:
        items.append(extra)
    for p, text in items:
        masked = mask(text)
        t, found = scan_file(masked, p)
        total += t
        for line, why, kind, name, decl in found:
            bad.append((os.path.basename(p), line, why, kind, name, decl))
    return total, bad


SENTINEL = '''
public static class SentinelPrompts
{
  [McpServerPrompt(Name = "SentinelPrivate")]
  [Description("多行描述在这里，里面还有括号 ( 和 ] 来干扰解析。")]
  private static string SentinelPrivate(string a) => a;

  [McpServerPrompt(Name = "SentinelGood")]
  [Description("合规的对照样本。")]
  public static string SentinelGood(string a) => a;
}
'''


def main():
    files = load(ROOT)
    if not files:
        print('找不到源码目录 %s —— 请在仓库根目录运行。' % ROOT)
        return 2

    # 哨兵：注入一个 private 的特性方法，闸门必须抓到它，且不能误报旁边那个 public 的。
    _, bad = run(files, extra=('<sentinel>', SENTINEL))
    caught = [b for b in bad if b[0] == '<sentinel>']
    names = set(b[4] for b in caught)
    if names != {'SentinelPrivate'}:
        print('[FAIL] 哨兵结果不对：期望只抓到 SentinelPrivate，实际 %s —— 这道闸自己坏了，'
              '它的 PASS 不可信。' % (sorted(names) or '什么都没抓到'))
        return 2

    total, bad = run(files)
    print('扫描 %d 个 .cs 文件；带 MCP 特性的声明 %d 处（哨兵已验证闸门有效）。' % (len(files), total))
    if not bad:
        print('[PASS] 带 MCP 特性的成员/类型全部对外可见 —— 反射扫描不会漏掉它们。')
        return 0
    print('[FAIL] 下列成员/类型挂了 MCP 特性，却不是 public（会被反射扫描漏掉，'
          '在 MCP 列表里静默消失）：')
    for f, line, why, kind, name, decl in bad:
        reason = '缺少 public' if why == 'missing-public' else '缺少 static（工具/prompt 方法必须是静态的）'
        print('  %s:%d  %s %s  —— %s' % (f, line, kind, name, reason))
        print('      %s' % decl)
    print('修法：把访问修饰符改回 public（按需 public static）。'
          '挂上特性就等于声明「这是要暴露给 MCP 客户端的」，不该收窄成内部实现细节。')
    return 1


if __name__ == '__main__':
    if '--selftest' in sys.argv:
        files = load(ROOT)
        _, bad = run(files, extra=('<sentinel>', SENTINEL))
        caught = [b for b in bad if b[0] == '<sentinel>']
        ok = set(b[4] for b in caught) == {'SentinelPrivate'}
        print('哨兵自检：' + ('PASS（private 的特性方法被抓到，public 的没被误报）'
                              if ok else 'FAIL（闸门行为不对）'))
        sys.exit(0 if ok else 1)
    sys.exit(main())
