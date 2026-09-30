using System.Collections.Frozen;

namespace DRGX.Engine;

/// <summary>
/// 数据包的**可达性派生**:哪些落位码真的产得出来、声明了却产不出的又是哪些,
/// 以及"非分组操作"表在本轨上该怎么算。
///
/// <para><b>为什么值得单独成类。</b>这三件事原先都写在两条装载轨各自的 <c>Load</c> 里,
/// 而两条轨的判据并不一样 —— 可达码一处按大小写敏感去重、一处不敏感;一处把 QY 白名单
/// 动态产出的 <c>{MDC}QY</c> 排除在外,一处没排。同一件事两个答案,查起来只能靠逐行比对代码。
/// 更麻烦的是测试侧还独立推导了第三遍(原 <c>OfficialPack.EmittedCodes</c>)——
/// 三份推导里任何一份走偏,都不会有任何信号。</para>
///
/// <para><b>与 <c>tests/.../cases/unreachable.txt</c> 不是一回事</b>(名字撞了,刻意在此点明):
/// 那个文件列的是"生成器造不出 witness 病案"的组(24 个 MDC 占位组),属于**测试工具**的中间产物;
/// 本类的 <see cref="Unreachable"/> 回答的是"按规则根本产不出来",属于**数据包自身的性质**。
/// 一个组可以可达却造不出 witness(缺样本),也可以不可达却进了清单(规则自相矛盾)。</para>
/// </summary>
public static class PackCoverage
{
    /// <summary>
    /// 可达落位码 = 每条 split 的码 ∪ QY 白名单动态产出的 <c>{MDC}QY</c>。
    ///
    /// <para><b>QY 必须算进来。</b>它不是任何 split 的码,而是运行时按 <c>{MDC}QY</c> 拼出来的
    /// 歧义组(官方 21 个 MDC 有 QY 规则)。漏掉它,这 21 个码会被误报成"不可达"。</para>
    ///
    /// <para>比较器一律 <see cref="StringComparer.OrdinalIgnoreCase"/>:组码是编码,
    /// <c>ab1</c> 与 <c>AB1</c> 是同一个组。</para>
    /// </summary>
    public static FrozenSet<string> Emitted(IEnumerable<CompiledMdc> mdcChain, IEnumerable<string> qyMdcCodes)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mdc in mdcChain)
            foreach (var adrg in mdc.Adrgs)
                foreach (var split in adrg.Splits)
                    set.Add(split.Code);
        foreach (var mdc in qyMdcCodes) set.Add(mdc + "QY");
        return set.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 声明了却没有任何落位路径能产出的组码(警告级,不阻断装载)。
    /// 返回顺序即传入顺序(组表的行序),便于日志对照。
    /// </summary>
    public static List<string> Unreachable(IEnumerable<string> declaredGroupCodes, FrozenSet<string> emitted) =>
        declaredGroupCodes.Where(code => !emitted.Contains(code)).ToList();

    /// <summary>
    /// 本轨的"非分组操作"表:从声明清单里去掉机器人辅助手术码。
    ///
    /// <para><b>为什么机器人码要去掉。</b>官方 3.0 把机器人直赋写成 DRG 分档规则的一部分
    /// (<c>{ZYSS,QTSS} in OP_ARB</c>),这些码必须参与常规手术匹配 —— 若仍留在非分组表里,
    /// 它们会在匹配前先被剔除,规则永远命中不了(症状是机器人档全落兜底组)。
    /// <c>blocked.csv</c> 里的机器人条目是旧包脚注18 直赋机制的手工残留。</para>
    ///
    /// <para><b>不能按 "17.4" 前缀一刀切。</b>17.4901(康复机器人)同属 17.4 家族但不在官方
    /// OP_ARB 集合内,官方不给它直赋资格,必须保留剔除 —— 所以这里比对的是<b>码表成员资格</b>,
    /// 不是前缀。</para>
    ///
    /// <para><b>旧 JSON 轨不走这里。</b>那条轨用 <c>robotAssist</c> 原语表达直赋,机器人码本就
    /// 该从常规匹配里剔除(只用来置 <c>RobotAssist</c> 标志),故原样保留声明清单。
    /// 这正是"同名不同义"的所在:同一个 <c>NonGroupingProcedures</c>,机器人码在两条轨上身份相反,
    /// 判据是**本轨规则的写法**而非配置。</para>
    /// </summary>
    public static FrozenSet<string> NonGroupingOf(IEnumerable<string> declared, FrozenSet<string> robotCodes) =>
        declared.Where(code => !robotCodes.Contains(code))
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
