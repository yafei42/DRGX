namespace DRGX.Engine.Dsl;

/// <summary>DSL 抽象语法树节点种类。</summary>
public enum DslNodeKind
{
    /// <summary>恒真（空规则、字面量 1/true）。</summary>
    True,
    And,
    Or,
    Not,
    /// <summary>集合成员判断：字段集合 in / not in 集合引用。</summary>
    Membership,
    /// <summary>标量比较：变量 算子 数值。</summary>
    Compare,
    /// <summary>交集基数：length(集合 ∩ 字段集合) 算子 数值。</summary>
    IntersectCount,
}

/// <summary>DSL 语法树节点。</summary>
public abstract record DslNode
{
    public abstract DslNodeKind Kind { get; }
}

/// <summary>恒真节点：空规则或字面量真值。空规则在官方表中即"兜底落位"。</summary>
public sealed record DslTrue : DslNode
{
    public override DslNodeKind Kind => DslNodeKind.True;
}

/// <summary>逻辑与（官方 and）。</summary>
public sealed record DslAnd(IReadOnlyList<DslNode> Items) : DslNode
{
    public override DslNodeKind Kind => DslNodeKind.And;
}

/// <summary>逻辑或（官方 or / OR）。</summary>
public sealed record DslOr(IReadOnlyList<DslNode> Items) : DslNode
{
    public override DslNodeKind Kind => DslNodeKind.Or;
}

/// <summary>逻辑非（官方 not in 或 not 前缀）。</summary>
public sealed record DslNot(DslNode Inner) : DslNode
{
    public override DslNodeKind Kind => DslNodeKind.Not;
}

/// <summary>
/// 成员判断。Fields 为左侧字段集合（语义：字段并集，任一命中）；
/// Sets 为右侧集合引用（语义：集合并集）。Negated 对应 not in。
/// </summary>
public sealed record DslMembership(IReadOnlyList<string> Fields, bool Negated, IReadOnlyList<string> Sets) : DslNode
{
    public override DslNodeKind Kind => DslNodeKind.Membership;
}

/// <summary>标量比较。Op 取值 &gt;= &gt; &lt;= &lt; =</summary>
public sealed record DslCompare(string Variable, string Op, double Value) : DslNode
{
    public override DslNodeKind Kind => DslNodeKind.Compare;
}

/// <summary>
/// 交集基数：length(集合 ∩ 字段集合) 算子 数值。
/// 语义：给定集合中有多少个不同编码出现在字段集合（如主手术∪其他手术）里。
/// 官方实例（ADRG IC2）：length(OP_IC2 ∩ {ZYSS, QTSS})&gt;=2。
/// </summary>
public sealed record DslIntersectCount(string Set, IReadOnlyList<string> Fields, string Op, double Value) : DslNode
{
    public override DslNodeKind Kind => DslNodeKind.IntersectCount;
}
