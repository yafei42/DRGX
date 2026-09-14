namespace DRGX.Engine.Dsl;

/// <summary>
/// 官方 DSL 递归下降解析器。优先级由低到高：or &lt; and &lt; not &lt; 原子。
/// 产出 <see cref="DslNode"/> 语法树，由 <see cref="DslCompiler"/> 编译为 <see cref="Condition"/>。
/// </summary>
public static class DslParser
{
    /// <summary>解析一条规则；空/空白输入返回恒真。</summary>
    public static DslNode Parse(string? expression)
    {
        var text = expression ?? "";
        var tokens = DslLexer.Tokenize(text);
        // 空规则（官方语义：兜底落位）
        if (tokens.Count == 1)
            return new DslTrue();
        var cursor = new Cursor(tokens, text);
        var node = cursor.ParseOr();
        cursor.ExpectEnd();
        return node;
    }

    private sealed class Cursor(List<DslToken> tokens, string source)
    {
        private int _index;

        public DslToken Current => tokens[_index];

        public DslToken Peek(int offset = 1) =>
            tokens[Math.Min(_index + offset, tokens.Count - 1)];

        /// <summary>取当前记号并前进；末尾 End 记号不前进，避免越界。</summary>
        public DslToken Take()
        {
            var token = tokens[_index];
            if (token.Kind != DslTokenKind.End)
                _index++;
            return token;
        }

        public void ExpectEnd()
        {
            if (Current.Kind != DslTokenKind.End)
                throw new DslException($"规则末尾存在多余记号 '{Current.Text}'（原文：{DslLexer.Snippet(source, Current.Position)}）");
        }

        public DslNode ParseOr()
        {
            var items = new List<DslNode> { ParseAnd() };
            while (Current.Kind == DslTokenKind.Or)
            {
                Take();
                items.Add(ParseAnd());
            }
            return items.Count == 1 ? items[0] : new DslOr(items);
        }

        public DslNode ParseAnd()
        {
            var items = new List<DslNode> { ParseNot() };
            while (Current.Kind == DslTokenKind.And)
            {
                Take();
                items.Add(ParseNot());
            }
            return items.Count == 1 ? items[0] : new DslAnd(items);
        }

        public DslNode ParseNot()
        {
            // "not in" 交给原子处理（成员判断的否定形式）
            if (Current.Kind == DslTokenKind.Not && Peek().Kind == DslTokenKind.In)
                return ParseAtom();
            if (Current.Kind == DslTokenKind.Not)
            {
                Take();
                return new DslNot(ParseNot());
            }
            return ParseAtom();
        }

        private DslNode ParseAtom()
        {
            switch (Current.Kind)
            {
                case DslTokenKind.LParen:
                {
                    Take();
                    var inner = ParseOr();
                    if (Current.Kind != DslTokenKind.RParen)
                        throw new DslException($"缺少右括号 ')'（原文：{DslLexer.Snippet(source, Current.Position)}）");
                    Take();
                    return inner;
                }
                case DslTokenKind.Number:
                {
                    // 字面量规则：官方 ZZ1 的规则就是数值 1，语义恒真
                    var token = Take();
                    var value = DslLexer.ParseNumber(token.Text);
                    return value != 0 ? new DslTrue() : new DslNot(new DslTrue());
                }
                case DslTokenKind.LBrace:
                    return ParseMembership(ParseFieldList());
                case DslTokenKind.Ident:
                {
                    // 函数式：length(集合 ∩ 字段集合) 算子 数值
                    if (Current.Text.Equals("length", StringComparison.OrdinalIgnoreCase)
                        && Peek().Kind == DslTokenKind.LParen)
                        return ParseIntersectCount();
                    // 标量变量走比较，否则视为字段集合
                    if (DslVariables.IsScalar(Current.Text))
                        return ParseCompare();
                    // 字段名统一大写：与花括号字段列表（ParseFieldList）同一口径，
                    // 否则 "zyzd in DI_x" 与 "{zyzd} in DI_x" 会以不同大小写进入编译器，
                    // 全靠下游用 OrdinalIgnoreCase 兜住 —— 靠调用方约定成立是脆的。
                    return ParseMembership([Take().Text.ToUpperInvariant()]);
                }
                default:
                    throw new DslException($"无法解析的记号 '{Current.Text}'（原文：{DslLexer.Snippet(source, Current.Position)}）");
            }
        }

        private DslNode ParseMembership(IReadOnlyList<string> fields)
        {
            bool negated = false;
            if (Current.Kind == DslTokenKind.Not)
            {
                negated = true;
                Take();
            }
            if (Current.Kind != DslTokenKind.In)
                throw new DslException($"字段 {string.Join(",", fields)} 后应为 in（原文：{DslLexer.Snippet(source, Current.Position)}）");
            Take();

            IReadOnlyList<string> sets;
            if (Current.Kind == DslTokenKind.LBrace)
            {
                sets = ParseBraceList();
            }
            else
            {
                if (Current.Kind != DslTokenKind.Ident)
                    throw new DslException($"in 之后缺少集合引用（原文：{DslLexer.Snippet(source, Current.Position)}）");
                sets = [Take().Text];
            }
            if (sets.Count == 0)
                throw new DslException($"in 之后缺少集合引用（原文：{DslLexer.Snippet(source, Current.Position)}）");
            return new DslMembership(fields, negated, sets);
        }

        /// <summary>length ( 集合 ∩ 字段集合 ) 算子 数值</summary>
        private DslNode ParseIntersectCount()
        {
            Take(); // length
            Take(); // (
            if (Current.Kind != DslTokenKind.Ident)
                throw new DslException($"length() 内缺少集合引用（原文：{DslLexer.Snippet(source, Current.Position)}）");
            var set = Take().Text;
            if (Current.Kind != DslTokenKind.Intersect)
                throw new DslException($"length() 内缺少交集符号 '∩'（原文：{DslLexer.Snippet(source, Current.Position)}）");
            Take();
            var fields = Current.Kind == DslTokenKind.LBrace ? ParseFieldList() : [Take().Text.ToUpperInvariant()];
            if (Current.Kind != DslTokenKind.RParen)
                throw new DslException($"length() 缺少右括号 ')'（原文：{DslLexer.Snippet(source, Current.Position)}）");
            Take();
            if (Current.Kind != DslTokenKind.Compare)
                throw new DslException($"length() 后应为比较算子（原文：{DslLexer.Snippet(source, Current.Position)}）");
            var op = Take().Text;
            if (Current.Kind != DslTokenKind.Number)
                throw new DslException($"length() 的比较算子后应为数值（原文：{DslLexer.Snippet(source, Current.Position)}）");
            var value = DslLexer.ParseNumber(Take().Text);
            return new DslIntersectCount(set, fields, op, value);
        }

        private DslNode ParseCompare()
        {
            var variable = Take().Text.ToUpperInvariant();
            if (Current.Kind != DslTokenKind.Compare)
                throw new DslException($"变量 {variable} 后应为比较算子（原文：{DslLexer.Snippet(source, Current.Position)}）");
            var op = Take().Text;
            if (Current.Kind != DslTokenKind.Number)
                throw new DslException($"比较算子 {op} 后应为数值（原文：{DslLexer.Snippet(source, Current.Position)}）");
            var value = DslLexer.ParseNumber(Take().Text);
            return new DslCompare(variable, op, value);
        }

        private IReadOnlyList<string> ParseFieldList()
        {
            Take(); // {
            var fields = new List<string>();
            while (Current.Kind is not (DslTokenKind.RBrace or DslTokenKind.End))
            {
                if (Current.Kind != DslTokenKind.Ident)
                    throw new DslException($"字段列表含非法记号 '{Current.Text}'（原文：{DslLexer.Snippet(source, Current.Position)}）");
                fields.Add(Take().Text.ToUpperInvariant());
                if (Current.Kind == DslTokenKind.Comma)
                    Take();
            }
            if (Current.Kind != DslTokenKind.RBrace)
                throw new DslException($"字段列表缺少右花括号 '}}'（原文：{DslLexer.Snippet(source, Current.Position)}）");
            Take();
            if (fields.Count == 0)
                throw new DslException("字段列表为空");
            return fields;
        }

        private IReadOnlyList<string> ParseBraceList()
        {
            Take(); // {
            var items = new List<string>();
            while (Current.Kind is not (DslTokenKind.RBrace or DslTokenKind.End))
            {
                if (Current.Kind != DslTokenKind.Ident)
                    throw new DslException($"集合列表含非法记号 '{Current.Text}'（原文：{DslLexer.Snippet(source, Current.Position)}）");
                items.Add(Take().Text);
                if (Current.Kind == DslTokenKind.Comma)
                    Take();
            }
            if (Current.Kind != DslTokenKind.RBrace)
                throw new DslException($"集合列表缺少右花括号 '}}'（原文：{DslLexer.Snippet(source, Current.Position)}）");
            Take();
            return items;
        }
    }
}
