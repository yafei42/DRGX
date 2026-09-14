using System.Globalization;
using System.Text;

namespace DRGX.Engine.Dsl;

/// <summary>官方 DSL 词法单元种类。</summary>
public enum DslTokenKind
{
    Ident,
    Number,
    /// <summary>and（官方为小写，词法层大小写不敏感）。</summary>
    And,
    /// <summary>or（官方表中同时出现小写与大写 OR）。</summary>
    Or,
    Not,
    In,
    /// <summary>比较算子：&gt;= &lt;= &lt; &gt; =</summary>
    Compare,
    LParen,
    RParen,
    LBrace,
    RBrace,
    Comma,
    /// <summary>集合交集符号 ∩（U+2229），仅出现在 length(S ∩ {字段}) 中。</summary>
    Intersect,
    End,
}

/// <summary>词法单元：含原文与起始位置，便于解析失败时定位。</summary>
public sealed record DslToken(DslTokenKind Kind, string Text, int Position);

/// <summary>DSL 解析失败（语法错误、未知变量/集合）。fail-fast，消息含位置与原文片段。</summary>
public sealed class DslException(string message) : Exception(message);

/// <summary>
/// 官方分组规则 DSL 的词法分析器。
/// 语法要素：变量（ZYZD/QTZD/ZYSS/QTSS/NL/XB/XSRTL/XSRTZ）、集合引用（DI_x/OP_x/MCC/CC）、
/// 关键字（and/or/not/in，大小写不敏感）、比较算子、圆括号与花括号。
/// </summary>
public static class DslLexer
{
    public static List<DslToken> Tokenize(string? input)
    {
        var tokens = new List<DslToken>();
        if (string.IsNullOrWhiteSpace(input))
        {
            tokens.Add(new DslToken(DslTokenKind.End, "", 0));
            return tokens;
        }

        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;
                var sb = new StringBuilder();
                while (i < input.Length && (char.IsAsciiLetterOrDigit(input[i]) || input[i] == '_'))
                    sb.Append(input[i++]);
                var word = sb.ToString();
                var kind = word.ToUpperInvariant() switch
                {
                    "AND" => DslTokenKind.And,
                    "OR" => DslTokenKind.Or,
                    "NOT" => DslTokenKind.Not,
                    "IN" => DslTokenKind.In,
                    _ => DslTokenKind.Ident,
                };
                tokens.Add(new DslToken(kind, kind == DslTokenKind.Ident ? word : word.ToUpperInvariant(), start));
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                int start = i;
                while (i < input.Length && (char.IsAsciiDigit(input[i]) || input[i] == '.'))
                    i++;
                tokens.Add(new DslToken(DslTokenKind.Number, input[start..i], start));
                continue;
            }

            switch (c)
            {
                case '(':
                    tokens.Add(new DslToken(DslTokenKind.LParen, "(", i++));
                    continue;
                case ')':
                    tokens.Add(new DslToken(DslTokenKind.RParen, ")", i++));
                    continue;
                case '{':
                    tokens.Add(new DslToken(DslTokenKind.LBrace, "{", i++));
                    continue;
                case '}':
                    tokens.Add(new DslToken(DslTokenKind.RBrace, "}", i++));
                    continue;
                case ',':
                    tokens.Add(new DslToken(DslTokenKind.Comma, ",", i++));
                    continue;
                case '∩':
                    tokens.Add(new DslToken(DslTokenKind.Intersect, "∩", i++));
                    continue;
                case '>':
                case '<':
                case '=':
                {
                    int start = i;
                    // 双字符算子 >= / <= 优先
                    if (i + 1 < input.Length && input[i + 1] == '=' && c != '=')
                    {
                        i += 2;
                        tokens.Add(new DslToken(DslTokenKind.Compare, input[start..i], start));
                    }
                    else
                    {
                        i++;
                        tokens.Add(new DslToken(DslTokenKind.Compare, input[start..i], start));
                    }
                    continue;
                }
                default:
                    throw new DslException($"位置 {i}: 无法识别的字符 '{c}'（原文：{Snippet(input, i)}）");
            }
        }

        tokens.Add(new DslToken(DslTokenKind.End, "", input.Length));
        return tokens;
    }

    internal static string Snippet(string input, int position)
    {
        var span = input.Length > 60 ? input[..60] + "…" : input;
        return $"{span} (位置 {position})";
    }

    /// <summary>数值字面量解析（不变文化）。</summary>
    internal static double ParseNumber(string text) =>
        double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
}
