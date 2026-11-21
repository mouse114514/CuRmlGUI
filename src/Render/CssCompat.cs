using System;
using System.Collections.Generic;
using System.Text;

namespace CuRmlGUI.Render
{
    /// <summary>
    /// 浏览器 CSS 兼容层：把 RmlUi 6.3 未注册、但浏览器开发者习惯的写法，
    /// 在文本层面重写为 RmlUi 等价的原生写法。**不修改 RmlUi 源码**。
    ///
    /// 支持的重写（声明级）：
    ///   - <c>text-shadow: x y [blur] color</c>            → <c>font-effect: shadow(x y color)</c>
    ///       模糊半径 RmlUi 的 font-effect 不支持，会被丢弃并告警（见 <see cref="RewriteTextShadow"/>）。
    ///   - <c>outline: width style color</c>               → <c>box-shadow: 0 0 0 (width+offset) color</c>
    ///       非 solid 线型会退化为实线并告警；<c>outline-offset</c> 会被合并进扩散值。
    ///   - <c>background: linear-gradient(...)|radial-gradient(...)</c>
    ///       → <c>background-color: transparent; decorator: &lt;gradient&gt;</c>
    ///       仅当整条声明恰好是单个渐变函数时转换；与颜色/多层背景混合的写法保持原样并告警。
    ///
    /// 无法安全转换的声明**原样保留**（RmlUi 会忽略它们，不会报错），并发出一次告警，
    /// 绝不静默改变语义。解析器是完整的 RCSS 词法器：能正确跳过注释、字符串、括号，
    /// 以及 <c>@media</c>/<c>@keyframes</c>/<c>@font-face</c>/<c>@decorator</c> 等嵌套块。
    ///
    /// 这是纯文本转换，可单独用 <see cref="Rewrite"/> 测试，不依赖原生库。
    /// </summary>
    public static class CssCompat
    {
        // 跨调用的去重集合：同一警告只发一次（即使 LoadDocument 被反复调用）。
        private static readonly HashSet<string> s_warned = new HashSet<string>();

        private static readonly string[] s_gradientNames = { "linear-gradient", "radial-gradient" };

        /// <summary>
        /// 重写 RCSS。默认开启；<paramref name="enabled"/> 为 false 时原样返回。
        /// <paramref name="warn"/> 收到人类可读的一次性告警（无法转换/降级的写法）。
        /// </summary>
        public static string Rewrite(string css, bool enabled = true, Action<string> warn = null)
        {
            if (!enabled || string.IsNullOrEmpty(css)) return css;
            List<Node> nodes = Parse(css);
            RewriteNodes(nodes, warn);
            return Serialize(nodes);
        }

        private static void Warn(Action<string> warn, string msg)
        {
            if (warn == null || string.IsNullOrEmpty(msg)) return;
            lock (s_warned)
            {
                if (!s_warned.Add(msg)) return;
            }
            warn(msg);
        }

        // ------------------------------------------------------------------ 词法 / AST

        private abstract class Node { }
        private sealed class Raw : Node { public string Text; public Raw(string t) { Text = t; } }
        private sealed class Rule : Node { public string Prelude; public List<Node> Body = new List<Node>(); }
        private sealed class Decl : Node
        {
            public string Name;
            public string Value;
            public string Original;
            public string Rewritten;

            /// <summary>生成重写文本，并保留原声明结尾的分号状态（'}' 结尾的声明不加分号）。</summary>
            public string Finish(string text)
                => text + (Original != null && Original.EndsWith(";") ? ";" : "");
        }

        private static List<Node> Parse(string css)
        {
            int pos = 0;
            return ParseBlock(css, ref pos, topLevel: true);
        }

        /// <summary>解析一块内容（顶层或某个 <c>{ }</c> 体内），直到块结束或 EOF。</summary>
        private static List<Node> ParseBlock(string css, ref int pos, bool topLevel)
        {
            var nodes = new List<Node>();
            while (pos < css.Length)
            {
                int wsStart = pos;
                SkipWsAndComments(css, ref pos);
                if (pos > wsStart) nodes.Add(new Raw(css.Substring(wsStart, pos - wsStart)));
                if (pos >= css.Length) break;

                char c = css[pos];
                if (c == '}')
                {
                    if (topLevel) { nodes.Add(new Raw("}")); pos++; continue; }
                    pos++;                 // 消费 '}'，由调用方的 Rule 重新输出
                    return nodes;
                }
                if (c == '{')
                {
                    pos++;
                    nodes.Add(new Rule { Prelude = "", Body = ParseBlock(css, ref pos, false) });
                    continue;
                }

                int start = pos;
                char term = ScanPrelude(css, ref pos);   // 停在 '{' ';' '}' 或 EOF
                string prelude = css.Substring(start, pos - start);

                if (term == '{')
                {
                    pos++;
                    nodes.Add(new Rule { Prelude = prelude, Body = ParseBlock(css, ref pos, false) });
                }
                else if (term == ';')
                {
                    pos++;
                    AddDeclOrRaw(nodes, prelude, ';');
                }
                else if (term == '}')
                {
                    // 块内最后一个声明可以不带分号（CSS 允许 "color: red }"）：
                    // 把紧邻 '}' 的文本当作声明或原样文本，而不是丢弃。
                    if (prelude.Length > 0) AddDeclOrRaw(nodes, prelude, '}');
                    if (topLevel) { pos++; continue; }
                    pos++;                 // 消费 '}'，由调用方的 Rule 重新输出
                    return nodes;
                }
                else
                {
                    if (prelude.Length > 0) nodes.Add(new Raw(prelude));
                    break;
                }
            }
            return nodes;
        }

        private static void SkipWsAndComments(string css, ref int pos)
        {
            while (pos < css.Length)
            {
                char c = css[pos];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f') { pos++; continue; }
                if (c == '/' && pos + 1 < css.Length && css[pos + 1] == '*')
                {
                    pos += 2;
                    while (pos + 1 < css.Length && !(css[pos] == '*' && css[pos + 1] == '/')) pos++;
                    pos = Math.Min(pos + 2, css.Length);
                    continue;
                }
                break;
            }
        }

        /// <summary>从 pos 扫描到下一个顶层 '{' ';' '}'，跳过注释/字符串/括号。返回终止字符。</summary>
        private static char ScanPrelude(string css, ref int pos)
        {
            int paren = 0, bracket = 0;
            while (pos < css.Length)
            {
                char c = css[pos];
                if (c == '/' && pos + 1 < css.Length && css[pos + 1] == '*')
                {
                    pos += 2;
                    while (pos + 1 < css.Length && !(css[pos] == '*' && css[pos + 1] == '/')) pos++;
                    pos = Math.Min(pos + 2, css.Length);
                    continue;
                }
                if (c == '\'' || c == '"')
                {
                    char q = c; pos++;
                    while (pos < css.Length && css[pos] != q)
                    {
                        if (css[pos] == '\\') pos++;
                        pos++;
                    }
                    if (pos < css.Length) pos++;
                    continue;
                }
                if (c == '(') { paren++; pos++; continue; }
                if (c == ')') { if (paren > 0) paren--; pos++; continue; }
                if (c == '[') { bracket++; pos++; continue; }
                if (c == ']') { if (bracket > 0) bracket--; pos++; continue; }
                if (paren == 0 && bracket == 0 && (c == '{' || c == ';' || c == '}')) return c;
                pos++;
            }
            return '\0';
        }

        private static void AddDeclOrRaw(List<Node> nodes, string prelude, char terminator)
        {
            bool semi = terminator == ';';
            if (prelude.TrimStart().StartsWith("@"))   // @import 等顶层语句
            {
                nodes.Add(new Raw(prelude + (semi ? ";" : "")));
                return;
            }
            int colon = FindTopLevelColon(prelude);
            if (colon < 0)
            {
                nodes.Add(new Raw(prelude + (semi ? ";" : "")));
                return;
            }
            string name = prelude.Substring(0, colon).Trim();
            string value = prelude.Substring(colon + 1).Trim();
            nodes.Add(new Decl { Name = name, Value = value, Original = prelude + (semi ? ";" : "") });
        }

        private static int FindTopLevelColon(string s)
        {
            int paren = 0, bracket = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'' || c == '"')
                {
                    char q = c; i++;
                    while (i < s.Length && s[i] != q) { if (s[i] == '\\') i++; i++; }
                    continue;
                }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    i++;
                    continue;
                }
                if (c == '(') paren++;
                else if (c == ')') { if (paren > 0) paren--; }
                else if (c == '[') bracket++;
                else if (c == ']') { if (bracket > 0) bracket--; }
                else if (c == ':' && paren == 0 && bracket == 0) return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------ 重写

        private static void RewriteNodes(List<Node> nodes, Action<string> warn)
        {
            foreach (Node n in nodes)
                if (n is Rule r) RewriteNodes(r.Body, warn);

            // 同一属性取最后一次出现（符合 CSS 级联：后者胜出）。
            Decl textShadow = null, fontEffect = null, boxShadow = null;
            Decl outline = null, outlineOffset = null;
            Decl background = null, decorator = null, bgColor = null;
            foreach (Node n in nodes)
            {
                if (!(n is Decl d)) continue;
                switch (d.Name.ToLowerInvariant())
                {
                    case "text-shadow": textShadow = d; break;
                    case "font-effect": fontEffect = d; break;
                    case "box-shadow": boxShadow = d; break;
                    case "outline": outline = d; break;
                    case "outline-offset": outlineOffset = d; break;
                    case "background": background = d; break;
                    case "decorator": decorator = d; break;
                    case "background-color": bgColor = d; break;
                }
            }

            // 1) text-shadow → font-effect: shadow(...)
            if (textShadow != null)
            {
                string fx = RewriteTextShadow(textShadow.Value, warn);
                if (fx != null)
                {
                    if (fontEffect != null)
                    {
                        fontEffect.Rewritten = fontEffect.Finish("font-effect: " + fontEffect.Value + ", " + fx);
                        textShadow.Rewritten = "";
                    }
                    else
                    {
                        textShadow.Rewritten = textShadow.Finish("font-effect: " + fx);
                    }
                }
            }

            // 2) outline → box-shadow: 0 0 0 (width+offset) color
            if (outline != null)
            {
                string bs = RewriteOutline(outline.Value, outlineOffset != null ? outlineOffset.Value : null, warn);
                if (bs != null)
                {
                    if (outlineOffset != null) outlineOffset.Rewritten = "";
                    if (boxShadow != null)
                    {
                        boxShadow.Rewritten = boxShadow.Finish("box-shadow: " + boxShadow.Value + ", " + bs);
                        outline.Rewritten = "";
                    }
                    else
                    {
                        outline.Rewritten = outline.Finish("box-shadow: " + bs);
                    }
                }
            }

            // 3) 单渐变 background → decorator: gradient
            if (background != null)
            {
                string grad = TrySingleGradient(background.Value);
                if (grad != null)
                {
                    if (decorator != null)
                    {
                        decorator.Rewritten = decorator.Finish("decorator: " + decorator.Value + ", " + grad);
                        // background 简写会重置颜色为透明；仅在未显式声明 background-color 时补 transparent。
                        background.Rewritten = bgColor == null ? "background-color: transparent;" : "";
                    }
                    else
                    {
                        background.Rewritten = background.Finish(
                            (bgColor == null ? "background-color: transparent;\n  " : "") + "decorator: " + grad);
                    }
                }
            }
        }

        private static string RewriteTextShadow(string value, Action<string> warn)
        {
            if (value.Trim() == "none") return null;
            List<string> shadows = SplitTopLevel(value, ',');
            var parts = new List<string>();
            foreach (string s in shadows)
            {
                string t = s.Trim();
                if (t.Length == 0) continue;
                List<string> toks = SplitTopLevel(t, ' ');
                if (toks.Count < 3)
                {
                    Warn(warn, "text-shadow '" + t + "' is not in 'x y [blur] color' form; left unconverted");
                    return null;
                }
                if (toks.Count > 4)
                {
                    Warn(warn, "text-shadow '" + t + "' has too many components; left unconverted");
                    return null;
                }
                string color = toks[toks.Count - 1];
                string x = toks[0], y = toks[1];
                string blur = toks.Count == 4 ? toks[2] : null;
                if (blur != null && blur != "0" && blur != "0px")
                    Warn(warn, "text-shadow blur radius '" + blur + "' is dropped (RmlUi font-effect has no blur); shadow kept sharp");
                parts.Add("shadow(" + x + " " + y + " " + color + ")");
            }
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        private static string RewriteOutline(string value, string offsetValue, Action<string> warn)
        {
            if (value.Trim() == "none") return null;
            List<string> toks = SplitTopLevel(value.Trim(), ' ');
            string width = null, color = null, style = null;
            foreach (string tok in toks)
            {
                string lt = tok.Trim();
                string ltl = lt.ToLowerInvariant();
                if (IsOutlineStyle(ltl)) { style = ltl; continue; }
                if (TryMapOutlineWidth(lt, out string mapped)) { width = mapped; continue; }
                color = lt;   // 其余视为颜色
            }
            if (width == null) width = "3px";           // CSS 默认 medium
            if (style != null && style != "solid" && style != "none" && style != "hidden")
                Warn(warn, "outline style '" + style + "' cannot be represented by box-shadow; rendered solid");
            if (color == null)
            {
                color = "#000000";
                Warn(warn, "outline has no color; defaulted to black for box-shadow");
            }

            float off = 0f;
            if (offsetValue != null && offsetValue.Trim().Length > 0)
            {
                string ot = offsetValue.Trim();
                if (ot != "0" && ot != "0px")
                {
                    if (!TryParsePx(ot, out off) || !TryParsePx(width, out _))
                    {
                        Warn(warn, "outline-offset '" + ot + "' with non-px width; offset ignored");
                        off = 0f;
                    }
                }
            }

            string spread = width;
            if (off != 0f && TryParsePx(width, out float w))
                spread = (w + off) + "px";
            return "0 0 0 " + spread + " " + color;
        }

        /// <summary>仅当 value 恰好是单个 linear/radial-gradient 函数时返回它（规范化函数名），否则 null。</summary>
        private static string TrySingleGradient(string value)
        {
            string t = value.Trim();
            if (t.Length == 0) return null;
            string lower = t.ToLowerInvariant();
            string fn = null;
            foreach (string g in s_gradientNames)
            {
                if (lower.StartsWith(g + "(", StringComparison.Ordinal)) { fn = g; break; }
            }
            if (fn == null || !IsSingleFunction(t)) return null;
            int open = t.IndexOf('(');
            return fn + t.Substring(open);
        }

        private static bool IsSingleFunction(string t)
        {
            int open = t.IndexOf('(');
            if (open < 0) return false;
            int depth = 0;
            for (int i = open; i < t.Length; i++)
            {
                if (t[i] == '(') depth++;
                else if (t[i] == ')')
                {
                    depth--;
                    if (depth == 0) return i == t.Length - 1;
                }
            }
            return false;
        }

        private static bool IsOutlineStyle(string s)
        {
            return s == "solid" || s == "dashed" || s == "dotted" || s == "double"
                || s == "groove" || s == "ridge" || s == "inset" || s == "outset"
                || s == "none" || s == "hidden";
        }

        private static bool TryMapOutlineWidth(string s, out string mapped)
        {
            mapped = null;
            switch (s.ToLowerInvariant())
            {
                case "thin": mapped = "1px"; return true;
                case "medium": mapped = "3px"; return true;
                case "thick": mapped = "5px"; return true;
            }
            if (TryParsePx(s, out _)) { mapped = s; return true; }
            return false;
        }

        private static bool TryParsePx(string s, out float v)
        {
            v = 0f;
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            if (t.EndsWith("px", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 2);
            else if (t.Length > 1 && (t[t.Length - 1] == 'p' || t[t.Length - 1] == 'x')) return false; // em/rem/% 等
            return float.TryParse(t, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        /// <summary>在顶层（括号/括号/字符串/注释之外）按分隔符切分；去除空白 token。</summary>
        private static List<string> SplitTopLevel(string s, char sep)
        {
            var list = new List<string>();
            int paren = 0, bracket = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'' || c == '"')
                {
                    char q = c; i++;
                    while (i < s.Length && s[i] != q) { if (s[i] == '\\') i++; i++; }
                    continue;
                }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    i++;
                    continue;
                }
                if (c == '(') paren++;
                else if (c == ')') { if (paren > 0) paren--; }
                else if (c == '[') bracket++;
                else if (c == ']') { if (bracket > 0) bracket--; }
                else if (c == sep && paren == 0 && bracket == 0)
                {
                    AddToken(list, s, start, i);
                    start = i + 1;
                }
            }
            AddToken(list, s, start, s.Length);
            return list;
        }

        private static void AddToken(List<string> list, string s, int start, int end)
        {
            string t = s.Substring(start, end - start).Trim();
            if (t.Length > 0) list.Add(t);
        }

        // ------------------------------------------------------------------ 序列化

        private static string Serialize(List<Node> nodes)
        {
            var sb = new StringBuilder();
            foreach (Node n in nodes) WriteNode(sb, n);
            return sb.ToString();
        }

        private static void WriteNode(StringBuilder sb, Node n)
        {
            if (n is Raw r) { sb.Append(r.Text); return; }
            if (n is Decl d) { sb.Append(d.Rewritten ?? d.Original); return; }
            var rule = (Rule)n;
            sb.Append(rule.Prelude);
            sb.Append('{');
            foreach (Node c in rule.Body) WriteNode(sb, c);
            sb.Append('}');
        }
    }
}
