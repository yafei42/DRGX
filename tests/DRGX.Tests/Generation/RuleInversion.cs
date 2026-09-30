using System.Collections.Frozen;
using DRGX.Engine;

namespace DRGX.Tests.Generation;

/// <summary>反推构造中的病案草稿。字段与 <see cref="MedicalRecord"/> 一一对应(除 Index)。</summary>
internal sealed class Draft
{
    public string? MainDx;
    public string? MainProc;
    public readonly List<string> OtherDx = [];
    public readonly List<string> Procs = [];
    public int? Age;
    public int? AgeDay;
    public int? Weight;
    public string? Gender;

    public Draft Clone()
    {
        var n = new Draft
        {
            MainDx = MainDx, MainProc = MainProc,
            Age = Age, AgeDay = AgeDay, Weight = Weight, Gender = Gender,
        };
        n.OtherDx.AddRange(OtherDx);
        n.Procs.AddRange(Procs);
        return n;
    }

    public void CopyFrom(Draft other)
    {
        MainDx = other.MainDx; MainProc = other.MainProc;
        Age = other.Age; AgeDay = other.AgeDay; Weight = other.Weight; Gender = other.Gender;
        OtherDx.Clear(); OtherDx.AddRange(other.OtherDx);
        Procs.Clear(); Procs.AddRange(other.Procs);
    }

    public MedicalRecord ToRecord(string index)
    {
        var diagnoses = new List<string>();
        if (MainDx is not null) diagnoses.Add(MainDx);
        diagnoses.AddRange(OtherDx);
        var procedures = new List<string>(Procs);
        if (MainProc is not null && !procedures.Contains(MainProc, StringComparer.OrdinalIgnoreCase))
            procedures.Insert(0, MainProc);
        return new MedicalRecord
        {
            Index = index,
            Gender = Gender ?? "1",
            Age = Age,
            AgeDay = AgeDay,
            Weight = Weight,
            Diagnoses = diagnoses,
            Procedures = procedures,
        };
    }
}

/// <summary>
/// 条件树反推:给一棵"期望为真"的条件树,把满足它所需的事实**追加/写入**草稿。
///
/// <para>这套逻辑是"造 witness"的全部智力所在 —— 引擎只认病案,不认条件,所以必须有人
/// 把条件翻译成事实。三条纪律:</para>
/// <list type="number">
/// <item><b>只写不猜</b>:能给出一组必然满足的事实(如 <c>ageGte(70)</c> → age=70)就写死;
/// 给不出(码表为空、部位映射缺失)就返回 false,交给调用方处理,绝不放宽条件假装满足。</item>
/// <item><b>Any 分支必须隔离</b>:失败分支的部分副作用不得污染后续分支 —— 否则前一个分支
/// 抢设的主诊断会让后一分支的 <c>mainDiagnosisIn</c> 无法回退,整组被误判为"不可达"。</item>
/// <item><b>主操作不可被覆盖</b>:引擎以操作列表首项为主操作,<c>anyProcedureIn</c> 只能追加
/// 其他操作,不能改写主操作(同一条 entry 里有 <c>mainProcedureIn</c> 时会自相矛盾)。</item>
/// </list>
/// </summary>
internal static class RuleInversion
{
    /// <summary>
    /// 从码表里挑一个码。<b>优先挑"没有被先判规则引用过"的码</b> —— 同一个码若出现在更靠前的
    /// 核心组/更靠前的档里,构造出来的病案必然被那些规则先截获,挑它等于白挑。
    ///
    /// <para>实测教训:WB29(烧伤伴手术)的入口是 <c>Any(其他诊断∈DI2_WB2 | 主诊断∈DI_WB2 且
    /// 其他诊断∈DI1_WB2)</c>,而 <c>DI2_WB2</c> 的头两个码(T31.300/T31.400)同时出现在更靠前的
    /// WB1 的 <c>DI2_WB1</c> 里 —— 只要"取集合首码"就永远被 WB1 吃走,WB29 会被误判成不可达。
    /// 集合里明明还有 T32.300/T32.400 可用。</para>
    /// </summary>
    private static string? PickCode(IEnumerable<string> codes, Func<string, bool> accept, GeneratorContext g)
    {
        string? fallback = null;
        foreach (var code in codes)
        {
            if (!accept(code)) continue;
            if (!g.Contested.Contains(code)) return code;
            fallback ??= code;
        }
        return fallback;
    }

    /// <summary>码表优先取"可作主诊断"的码(在诊断字典内、不属不参与分组清单、且尽量不被先判规则引用)。</summary>
    internal static string? PickMainDiagnosisCode(Condition c, DataPack pack, GeneratorContext g) =>
        PickCode(c.Codes, x => pack.DiagnosisNames.ContainsKey(x) && !pack.NonPrincipalDiagnoses.Contains(x), g)
        ?? c.Codes.FirstOrDefault();

    /// <summary>码表优先取"字典里认得"的码(其他诊断不参与主诊断校验,但仍应给得出中文名)。</summary>
    internal static string? PickDiagnosisCode(Condition c, DataPack pack, GeneratorContext g) =>
        PickCode(c.Codes, x => pack.DiagnosisNames.ContainsKey(x), g) ?? c.Codes.FirstOrDefault();

    /// <summary>操作码表:只要求这是本码表里的码,同样把"被先判规则引用过"的码排后。</summary>
    private static string? PickProcedureCode(IEnumerable<string> codes, GeneratorContext g) =>
        PickCode(codes, _ => true, g);

    public static bool Satisfy(Condition w, Draft d, GeneratorContext g)
    {
        var pack = g.Pack;
        switch (w.Kind)
        {
            case ConditionKind.True:
                return true;

            case ConditionKind.All:
                return w.Children.All(ch => Satisfy(ch, d, g));

            case ConditionKind.Any:
            {
                // 分支选择偏好"不引入被先判规则引用过的码"的那一支。
                //
                // 实测教训:WB29 的入口是 Any(其他诊断∈DI2_WB2 | 主诊断∈DI_WB2 且 其他诊断∈DI1_WB2)。
                // 两个分支都"能满足",但 DI2_WB2 的四个码全部同时出现在更靠前的 WB1 的 DI2_WB1 里 ——
                // 只按书写顺序取第一支,构造出来的病案永远被 WB1 截获,WB29 就被误判成不可达;
                // 而第二支用的 DI1_WB2 两个码没人抢,是真能落到 WB29 的。
                Draft? dirty = null;
                foreach (var ch in w.Children)
                {
                    var cand = d.Clone();
                    if (!Satisfy(ch, cand, g)) continue;
                    if (!IntroducesContestedCode(d, cand, g)) { d.CopyFrom(cand); return true; }
                    dirty ??= cand;
                }
                if (dirty is null) return false;
                d.CopyFrom(dirty);
                return true;
            }

            case ConditionKind.Not:
                // 纯判定:不添加任何事实,只看当前草稿是否已使子条件为真。
                return !IsSatisfiedNow(w.Children[0], d, g);

            case ConditionKind.MainDiagnosisIn:
                if (d.MainDx is not null && w.CodeSet.Contains(d.MainDx)) return true;
                d.MainDx = PickMainDiagnosisCode(w, pack, g);
                return d.MainDx is not null && w.CodeSet.Contains(d.MainDx);

            case ConditionKind.AnyDiagnosisIn:
                if (d.MainDx is not null && w.CodeSet.Contains(d.MainDx)) return true;
                if (d.OtherDx.Any(w.CodeSet.Contains)) return true;
                var adx = PickDiagnosisCode(w, pack, g);
                if (adx is null) return false;
                d.OtherDx.Add(adx);
                return true;

            case ConditionKind.OtherDiagnosisIn:
                var odx = PickDiagnosisCode(w, pack, g);
                if (odx is null) return false;
                d.OtherDx.Add(odx);
                return true;

            case ConditionKind.AnyDiagnosisPrefix:
            {
                var pd = pack.DiagnosisNames.Keys.FirstOrDefault(k =>
                    w.Prefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
                if (pd is null) return false;
                if (d.MainDx is null) d.MainDx = pd;
                else if (!w.Prefixes.Any(p => d.MainDx!.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    d.OtherDx.Add(pd);
                return true;
            }

            case ConditionKind.MainProcedureIn:
                if (d.MainProc is not null && w.CodeSet.Contains(d.MainProc)) return true;
                d.MainProc = PickProcedureCode(w.CodeSet, g);
                if (d.MainProc is null) return false;
                if (!d.Procs.Contains(d.MainProc)) d.Procs.Insert(0, d.MainProc);
                return true;

            case ConditionKind.AnyProcedureIn:
                if (d.MainProc is not null && w.CodeSet.Contains(d.MainProc)) return true;
                if (d.MainProc is not null)
                {
                    // 主操作已存在但不属本集合:追加一个"其他操作",不改写主操作。
                    var extra = PickProcedureCode(w.CodeSet.Where(x => !string.Equals(x, d.MainProc, StringComparison.OrdinalIgnoreCase)), g)
                                ?? PickProcedureCode(w.CodeSet, g);
                    if (extra is null) return false;
                    if (!d.Procs.Contains(extra)) d.Procs.Add(extra);
                    return true;
                }
                var p = PickProcedureCode(w.CodeSet, g);
                if (p is null) return false;
                d.MainProc = p;
                if (!d.Procs.Contains(p)) d.Procs.Insert(0, p);
                return true;

            case ConditionKind.AnyOtherProcedureIn:
            {
                var op = PickProcedureCode(w.CodeSet, g);
                if (op is null) return false;
                if (!d.Procs.Contains(op)) d.Procs.Add(op);
                return true;
            }

            case ConditionKind.HasMcc:
                return AddComplication(d, g, g.MccCodes, pack.Mcc);

            case ConditionKind.HasCc:
                return AddComplication(d, g, g.CcCodes, pack.Cc);

            case ConditionKind.HasCcOrMcc:
                return AddComplication(d, g, g.CcCodes, pack.Cc) || AddComplication(d, g, g.MccCodes, pack.Mcc);

            case ConditionKind.HasValidProcedure:
            case ConditionKind.HasProcedure:
            {
                var vp = PickProcedureCode(g.ValidProcedures, g);
                if (vp is null) return false;
                if (d.MainProc is null) { d.MainProc = vp; d.Procs.Insert(0, vp); }
                else if (!d.Procs.Contains(vp)) d.Procs.Add(vp);
                return true;
            }

            case ConditionKind.NoValidMainProcedure:
                d.MainProc = null;
                d.Procs.Clear();
                return true;

            case ConditionKind.ProcedureCountGte:
            {
                var need = (int)w.Value;
                for (int i = 0; d.Procs.Count < need && i < g.ValidProcedures.Count; i++)
                    if (!d.Procs.Contains(g.ValidProcedures[i])) d.Procs.Add(g.ValidProcedures[i]);
                return d.Procs.Count >= need;
            }

            case ConditionKind.AgeGte:
                d.Age = (int)Math.Max(w.Value, 1);
                return true;
            case ConditionKind.AgeGt:
                d.Age = (int)(w.Value + 1);
                return true;
            case ConditionKind.AgeLte:
                d.Age = (int)Math.Min(Math.Max(w.Value, 0), 120);
                return true;
            case ConditionKind.AgeLt:
                d.Age = (int)Math.Max(w.Value - 1, 0);
                return true;
            case ConditionKind.AgeEq:
                d.Age = (int)w.Value;
                return true;
            case ConditionKind.AgeNot:
                // 年龄 ≠ Value:取 Value+1,保证是与阈值相邻的确定值(取 0 会与"年龄缺失"混淆)。
                d.Age = (int)w.Value + 1;
                return true;

            case ConditionKind.AgeDayGte:
                d.AgeDay = (int)Math.Max(w.Value, 0);
                return true;
            case ConditionKind.AgeDayGt:
                d.AgeDay = (int)(w.Value + 1);
                return true;
            case ConditionKind.AgeDayLte:
                d.AgeDay = (int)Math.Max(w.Value, 0);
                return true;
            case ConditionKind.AgeDayLt:
                d.AgeDay = (int)Math.Max(w.Value - 1, 0);
                return true;

            case ConditionKind.WeightGte:
                d.Weight = (int)Math.Max(w.Value, 1000);
                return true;
            case ConditionKind.WeightGt:
                d.Weight = (int)w.Value + 1;
                return true;
            case ConditionKind.WeightLte:
                d.Weight = (int)Math.Max(w.Value, 1);
                return true;
            case ConditionKind.WeightLt:
                d.Weight = (int)Math.Max(w.Value - 1, 1);
                return true;

            case ConditionKind.GenderIs:
                d.Gender = w.ExpectedValue;
                return true;

            case ConditionKind.RobotAssist:
            {
                var robot = g.RobotProcedures.FirstOrDefault();
                if (robot is null) return false;
                d.MainProc = robot;
                if (!d.Procs.Contains(robot)) d.Procs.Insert(0, robot);
                return true;
            }

            case ConditionKind.HasOtherDiagnosis:
            {
                if (d.OtherDx.Count > 0) return true;
                var other = PickCode(pack.DiagnosisNames.Keys, k =>
                    !string.Equals(k, d.MainDx, StringComparison.OrdinalIgnoreCase), g);
                if (other is null) return false;
                d.OtherDx.Add(other);
                return true;
            }

            case ConditionKind.ProcedureIntersectCountGte:
            {
                var need = (int)w.Value;
                var pick = w.CodeSet.Take(need).ToList();
                if (pick.Count < need) return false;
                foreach (var code in pick)
                {
                    if (d.MainProc is null) { d.MainProc = code; d.Procs.Insert(0, code); }
                    else if (!d.Procs.Contains(code)) d.Procs.Add(code);
                }
                return true;
            }

            case ConditionKind.DiagnosisIntersectCountGte:
            {
                var need = (int)w.Value;
                var pick = w.CodeSet.Take(need).ToList();
                if (pick.Count < need) return false;
                foreach (var code in pick)
                {
                    if (d.MainDx is null) d.MainDx = code;
                    else if (!d.OtherDx.Contains(code)) d.OtherDx.Add(code);
                }
                return true;
            }

            case ConditionKind.SiteCountGte:
            {
                var sites = g.Sites;
                if (sites is null) return false;
                var need = (int)w.Value;
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (d.MainDx is not null)
                    foreach (var (site, codes) in sites)
                        if (codes.Contains(d.MainDx)) { used.Add(site); break; }
                foreach (var (site, codes) in sites)
                {
                    if (used.Count >= need) break;
                    var code = codes.FirstOrDefault(x => !string.Equals(x, d.MainDx, StringComparison.OrdinalIgnoreCase))
                               ?? codes.FirstOrDefault();
                    if (code is null) continue;
                    if (d.MainDx is null) d.MainDx = code;
                    else if (!d.OtherDx.Contains(code)) d.OtherDx.Add(code);
                    used.Add(site);
                }
                return used.Count >= need;
            }

            default:
                // 未做机械反推的原语 → 交由调用方记为"不可达",不假装满足。
                return false;
        }
    }

    /// <summary>候选草稿相对原草稿**新引入**的编码里,有没有"已被先判规则引用过"的。
    /// 有,就意味着这条分支构造出来的病案大概率会被那些规则先吃掉。</summary>
    private static bool IntroducesContestedCode(Draft before, Draft after, GeneratorContext g)
    {
        if (g.Contested.Count == 0) return false;
        foreach (var dx in after.OtherDx)
            if (!before.OtherDx.Contains(dx) && g.Contested.Contains(dx)) return true;
        foreach (var p in after.Procs)
            if (!before.Procs.Contains(p) && g.Contested.Contains(p)) return true;
        return after.MainDx is not null && !string.Equals(after.MainDx, before.MainDx, StringComparison.OrdinalIgnoreCase)
               && g.Contested.Contains(after.MainDx);
    }

    /// <summary>纯判定:当前草稿在事实上是否已满足该条件(无副作用)。</summary>
    public static bool IsSatisfiedNow(Condition w, Draft d, GeneratorContext g) => w.Kind switch
    {
        ConditionKind.True => true,
        ConditionKind.All => w.Children.All(ch => IsSatisfiedNow(ch, d, g)),
        ConditionKind.Any => w.Children.Any(ch => IsSatisfiedNow(ch, d, g)),
        ConditionKind.Not => !IsSatisfiedNow(w.Children[0], d, g),
        ConditionKind.MainDiagnosisIn => d.MainDx is not null && w.CodeSet.Contains(d.MainDx),
        ConditionKind.AnyDiagnosisIn => (d.MainDx is not null && w.CodeSet.Contains(d.MainDx))
                                        || d.OtherDx.Any(w.CodeSet.Contains),
        ConditionKind.OtherDiagnosisIn => d.OtherDx.Any(w.CodeSet.Contains),
        ConditionKind.AnyDiagnosisPrefix =>
            (d.MainDx is not null && w.Prefixes.Any(p => d.MainDx!.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            || d.OtherDx.Any(x => w.Prefixes.Any(p => x.StartsWith(p, StringComparison.OrdinalIgnoreCase))),
        ConditionKind.MainProcedureIn => d.MainProc is not null && w.CodeSet.Contains(d.MainProc),
        ConditionKind.AnyProcedureIn => (d.MainProc is not null && w.CodeSet.Contains(d.MainProc))
                                        || d.Procs.Skip(1).Any(w.CodeSet.Contains),
        ConditionKind.AnyOtherProcedureIn => d.Procs.Skip(1).Any(w.CodeSet.Contains),
        ConditionKind.HasProcedure => d.Procs.Count > 0,
        ConditionKind.HasValidProcedure => d.Procs.Any(g.Pack.ValidProcedures.Contains),
        ConditionKind.NoValidMainProcedure => d.MainProc is null || !g.Pack.ValidProcedures.Contains(d.MainProc),
        ConditionKind.HasOtherDiagnosis => d.OtherDx.Count > 0,
        ConditionKind.HasMcc => d.OtherDx.Any(x => g.Pack.Mcc.ContainsKey(x) && !g.Excluded(x, d.MainDx)),
        ConditionKind.HasCc => d.OtherDx.Any(x => g.Pack.Cc.ContainsKey(x) && !g.Excluded(x, d.MainDx)),
        ConditionKind.HasCcOrMcc => d.OtherDx.Any(x => g.Pack.Mcc.ContainsKey(x) && !g.Excluded(x, d.MainDx))
                                    || d.OtherDx.Any(x => g.Pack.Cc.ContainsKey(x) && !g.Excluded(x, d.MainDx)),
        ConditionKind.AgeGte => d.Age >= w.Value,
        ConditionKind.AgeGt => d.Age > w.Value,
        ConditionKind.AgeLte => d.Age <= w.Value,
        ConditionKind.AgeLt => d.Age < w.Value,
        ConditionKind.AgeEq => d.Age == w.Value,
        ConditionKind.AgeNot => d.Age != w.Value,
        ConditionKind.AgeDayGte => DayAgeUsable(d) && d.AgeDay >= w.Value,
        ConditionKind.AgeDayGt => DayAgeUsable(d) && d.AgeDay > w.Value,
        ConditionKind.AgeDayLte => DayAgeUsable(d) && d.AgeDay <= w.Value,
        ConditionKind.AgeDayLt => DayAgeUsable(d) && d.AgeDay < w.Value,
        ConditionKind.WeightGte => d.Weight >= w.Value,
        ConditionKind.WeightGt => d.Weight > w.Value,
        ConditionKind.WeightLte => d.Weight <= w.Value,
        ConditionKind.WeightLt => d.Weight < w.Value,
        ConditionKind.GenderIs => string.Equals(d.Gender, w.ExpectedValue, StringComparison.OrdinalIgnoreCase),
        ConditionKind.RobotAssist => g.Pack.RobotProcedures.Any(x => d.Procs.Contains(x)),
        ConditionKind.ProcedureIntersectCountGte => CountIntersect(
            (d.MainProc is null ? [] : new[] { d.MainProc }).Concat(d.Procs.Skip(1)), w.CodeSet) >= w.Value,
        ConditionKind.DiagnosisIntersectCountGte => CountIntersect(
            (d.MainDx is null ? [] : new[] { d.MainDx }).Concat(d.OtherDx), w.CodeSet) >= w.Value,
        ConditionKind.SiteCountGte => g.Sites is not null && CountSites(d, g.Sites) >= w.Value,
        _ => false,
    };

    /// <summary>与 <see cref="ConditionEvaluator"/> 同一口径的两道护栏:年龄已给出(且非 0)时
    /// 日龄条件一律为假;日龄本身缺失时也不得当成 0 天。</summary>
    private static bool DayAgeUsable(Draft d) => (d.Age is not int age || age == 0) && d.AgeDay is not null;

    private static int CountIntersect(IEnumerable<string> codes, IReadOnlySet<string> set)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in codes) if (!string.IsNullOrEmpty(c) && set.Contains(c)) seen.Add(c);
        return seen.Count;
    }

    private static int CountSites(Draft d, IReadOnlyDictionary<string, FrozenSet<string>> sites)
    {
        var hit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (site, codes) in sites)
        {
            if (d.MainDx is not null && codes.Contains(d.MainDx)) { hit.Add(site); continue; }
            if (d.OtherDx.Any(codes.Contains)) hit.Add(site);
        }
        return hit.Count;
    }

    /// <summary>往草稿里补一个并发症码:必须与主诊断**不属同一排除组**(否则引擎会把它当作
    /// "主诊断已排除的并发症"剔掉,条件仍然不成立),并优先选没被先判规则引用过的码。</summary>
    private static bool AddComplication(Draft d, GeneratorContext g, IReadOnlyList<string> codes,
        FrozenDictionary<string, string> table)
    {
        var usable = codes.Where(code =>
            table.ContainsKey(code)
            && !string.Equals(code, d.MainDx, StringComparison.OrdinalIgnoreCase)
            && !d.OtherDx.Contains(code)
            && !g.Excluded(code, d.MainDx)).ToList();

        var pick = usable.FirstOrDefault(code => !g.Contested.Contains(code)) ?? usable.FirstOrDefault();
        if (pick is null) return false;
        d.OtherDx.Add(pick);
        return true;
    }
}

/// <summary>反推所需的上下文:数据包 + 预物化的候选码表。</summary>
internal sealed class GeneratorContext
{
    public required DataPack Pack { get; init; }
    public IReadOnlyList<string> MccCodes { get; init; } = [];
    public IReadOnlyList<string> CcCodes { get; init; } = [];
    public IReadOnlyList<string> ValidProcedures { get; init; } = [];
    public IReadOnlyList<string> RobotProcedures { get; init; } = [];
    public IReadOnlyDictionary<string, FrozenSet<string>>? Sites { get; init; }
    /// <summary>默认主诊断:任何可作主诊断的码(兜底,用于门控不窄化主诊断的 MDC)。</summary>
    public string FallbackMainDiagnosis { get; init; } = "";

    /// <summary>
    /// "已经被先判规则引用过"的编码集合:同一 MDC 中排在本核心组之前的所有核心组(入口 + 全部档),
    /// 以及本核心组更靠前的档,所引用的全部编码。
    ///
    /// <para>它是**排序提示而不是约束**:反推时优先避开这些码(避开就能落到目标组),
    /// 但若某个条件的所有码都在其中,仍然照常取用 —— 那时候"谁先判"才是决定性因素,
    /// 硬避开只会把候选病案改成不满足条件的样子。</para>
    /// </summary>
    public IReadOnlySet<string> Contested { get; set; } = System.Collections.Frozen.FrozenSet<string>.Empty;

    /// <summary>本次目标已消耗的引擎探针次数(反搜是穷举,必须有界)。</summary>
    public int Probes { get; set; }

    /// <summary>换 MDC 只换部位映射视图(与引擎 <see cref="EvaluationContext"/> 同一做法)。</summary>
    public GeneratorContext WithSites(IReadOnlyDictionary<string, FrozenSet<string>>? sites) => new()
    {
        Pack = Pack,
        MccCodes = MccCodes,
        CcCodes = CcCodes,
        ValidProcedures = ValidProcedures,
        RobotProcedures = RobotProcedures,
        Sites = sites,
        FallbackMainDiagnosis = FallbackMainDiagnosis,
        Contested = Contested,
    };

    /// <summary>该并发症码是否被主诊断的排除表排除。</summary>
    public bool Excluded(string complication, string? mainDiagnosis) =>
        mainDiagnosis is not null
        && Pack.Exclusions.TryGetValue(mainDiagnosis, out var group)
        && ((Pack.Mcc.TryGetValue(complication, out var mg) && mg == group)
            || (Pack.Cc.TryGetValue(complication, out var cg) && cg == group));
}
