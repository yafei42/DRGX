namespace DRGX.Fee;

/// <summary>地区费用包清单(manifest.json,schemaVersion 2:全国基准权重已内嵌,无独立权重库)。</summary>
public sealed record RegionPackManifest(
    string Kind,
    /// <summary>地区标识(如 zhoukou)。</summary>
    string Id,
    /// <summary>地区显示名(如 "周口(河南)")。</summary>
    string RegionName,
    /// <summary>算法标识(FeeAlgorithmRegistry 按此解析实现)。</summary>
    string Algorithm,
    /// <summary>权重数据溯源(源自哪个全国方案;仅记录,不参与加载)。</summary>
    string? BaseWeightsId,
    string SchemeVersion,
    string DataVersion,
    /// <summary>
    /// 数据级别:official=该地区公布的值(默认);reference=非该地区公布的值(外部参考 / 演示用),不可作为结算依据。
    /// 用于阻断「把非本地公布的数据当作地方执行值」这类数据源错用。
    /// </summary>
    string DataLevel,
    /// <summary>
    /// 是否为演示/样例数据。true 表示本包仅用于验证功能链路,<b>不具备任何结算效力</b>。
    ///
    /// <para>与 <see cref="DataLevel"/> 正交:后者回答「这批数字是不是该地区公布的」,
    /// 前者回答「这批数字是不是拿来出数的」。两者都可能为真 ——
    /// 一个包完全可以是"某地区公布过的旧版参数",既 official 又 demo。</para>
    ///
    /// <para>标记只影响展示与告警,不参与任何计算,也不影响默认位选择
    /// (默认位规则按真实部署该有的语义实现,不因演示数据而变)。</para>
    /// </summary>
    bool Demo,
    string? EffectiveFrom,
    string? EffectiveUntil,
    int SchemaVersion,
    /// <summary>weights.csv 数据行数(清单登记;>0 时与实际行数强校验)。</summary>
    int WeightsRows,
    /// <summary>weights.csv 已公布行数(清单登记;>0 时与实际强校验)。</summary>
    int WeightsPublished,
    IReadOnlyDictionary<string, string> Files,
    string Notes);

/// <summary>DRG 地区属性:基础/非稳定病组标记 + 分等级系数(列名 → 系数;空为 null,走 defaultFactor)。</summary>
public sealed record DrgAttributes(
    string DrgCode,
    bool Basic,
    bool Unstable,
    IReadOnlyDictionary<string, decimal?> Factors);

/// <summary>稀疏权重覆盖行:仅登记与本包 weights.csv 基准不同的 DRG。</summary>
public sealed record WeightOverride(string DrgCode, decimal Rw);
