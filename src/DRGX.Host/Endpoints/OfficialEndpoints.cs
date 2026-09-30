using DRGX.Engine;

namespace DRGX.Host;

// ============================================================================
// 官方配置表只读浏览端点("数据一览"页):直接回放数据包内**官方配置信息工作簿**(official/*.xlsx)。
//
// 设计约束(与旧 /api/pack/* 的根本区别):
//   * 单一数据源 —— 只读官方工作簿装载出的原始表(见 DRGX.Engine 的 OfficialWorkbook),
//     不再从编译后的 DataPack 反推、不再拼接区域费用包、不再重排/改写任何一列;
//   * 全列回放 —— 返回行包含该表的全部列(列定义见 OfficialTable.Definitions,
//     前端按同样顺序渲染),列数始终等于工作簿该 sheet 的列数;
//   * 外键即下钻 —— 可用于钻取的表 id/列成对下发,前端渲染成链接,
//     组合 f(列)+ v(值)即可切到目标表的对应行。
// 只读,绝不写包。
//
// 五个 handler 一律走 OfficialPackView:打开视图、取表、表源说明、集合编号、两棵树、
// 筛选/排序/分页,都由它回答 —— 这些原先在每个 handler 里各写一遍,文案与默认值已经开始分叉。
// ============================================================================

internal static class OfficialEndpoints
{
    public static void Map(WebApplication app, WebApp svc)
    {
        // ---- 表清单:列定义 + 行计数 + 集合编号(dsl 列下钻判定用) ----
        app.MapGet("/api/official/tables", IResult () =>
        {
            var view = OfficialPackView.Open(svc.PackPath);
            if (view is null)
                return Results.Ok(new { dir = NoWorkbookDir(svc), exists = false, tables = Array.Empty<object>(), setIds = Array.Empty<string>() });

            try
            {
                // 分组主干(MDC/ADRG/DRG)与编码集合都是层级数据:先算出层级与计数(表已随工作簿装在内存,
                // 成本为一次内存遍历),再按"主干在前、集合垫底"的顺序把它们插进清单。
                var tree = view.Tree();
                var sets = view.SetTree();
                var book = view.FileName;

                var list = new List<object>
                {
                    new
                    {
                        id = OfficialTree.Id,
                        label = OfficialTree.Label,
                        desc = OfficialTree.Desc,
                        file = $"{book} · {OfficialTree.SourceSheets}",   // 表源说明:不是单个文件,而是工作簿的三张 sheet
                        kind = "tree",                                     // 前端据此切树视图(不分页/不排序)
                        total = tree.Levels.Sum(l => l.Count),             // 入树节点数(不含 00 类)
                        levels = tree.Levels,
                        zeroTotal = tree.ZeroTotal,                        // 被剔除的 00 类档数(表源说明如实标注)
                        cols = Array.Empty<object>(),
                    },
                };

                foreach (var def in OfficialTable.Definitions)
                {
                    if (OfficialTable.TreeMemberIds.Contains(def.Id)) continue;   // 已并入分组主干树
                    // 集合两表在集合树建成时并入它;树建不起来(索引列缺失)则退回平表,
                    // 免得一处退化把成员明细也一起挡在门外。
                    if (OfficialSetTree.MemberIds.Contains(def.Id)) continue;
                    var table = view.Table(def.Id);
                    list.Add(new
                    {
                        id = def.Id,
                        label = def.Label,
                        desc = def.Desc,
                        file = view.SourceOf(def.Id),
                        total = table.Rows.Length,
                        cols = def.Columns.Select(c => new
                        {
                            key = c.Key,
                            label = c.Label,
                            format = c.Format,
                            mono = c.Mono,
                            dsl = c.Dsl,
                            hint = c.Hint,
                            link = c.LinkTable is null ? null : new { table = c.LinkTable, col = c.LinkColumn },
                        }),
                    });
                }

                // 编码集合树垫底(它是规则引用的词表,不是分组骨架;主干树在前、明细表居中)
                list.Add(new
                {
                    id = OfficialSetTree.Id,
                    label = OfficialSetTree.Label,
                    desc = OfficialSetTree.Desc,
                    file = $"{book} · {OfficialSetTree.SourceSheets}",
                    kind = "tree",
                    total = sets.Levels.Sum(l => l.Count),
                    levels = sets.Levels,
                    zeroTotal = 0,                          // 无 00 类口径:字段留着是为了两棵树同形
                    memberTotal = sets.MemberTotal,          // 全部集合的成员合计(右栏按需加载,不随树下发)
                    cols = Array.Empty<object>(),
                });

                return Results.Ok(new { dir = view.Dir, exists = true, tables = list, setIds = view.SetIds() });
            }
            catch (PackException ex) { return LoadFailed(ex); }
        });

        // ---- 层级树:?t=tree(分组主干 MDC → ADRG → DRG) / ?t=sets(编码集合 类型 → 集合) ----
        // 主干整树一次下发(约 90KB):前端本地展开/检索/导出,不必为每个节点往返。
        // 集合树只含前两级(634 + 2 个节点),成员体积太大,由 /api/official/setmembers 按需分页给。
        // 00 类落位档(MDC 0000 / ADRG X00 / DRG X000)在服务端就已剔除,不下发、不打标 ——
        // 前端不需要"含兜底组"开关,树上的计数与屏幕所见始终同一口径。
        app.MapGet("/api/official/tree", IResult (string? t) =>
        {
            var view = OfficialPackView.Open(svc.PackPath);
            if (view is null) return NoWorkbook(svc);
            var id = string.IsNullOrWhiteSpace(t) ? OfficialTree.Id : t!;
            try
            {
                if (string.Equals(id, OfficialSetTree.Id, StringComparison.OrdinalIgnoreCase))
                {
                    var sets = view.SetTree();
                    return Results.Ok(new
                    {
                        levels = sets.Levels,
                        zeroTotal = 0,
                        memberTotal = sets.MemberTotal,
                        nodes = sets.Roots,
                    });
                }
                if (!string.Equals(id, OfficialTree.Id, StringComparison.OrdinalIgnoreCase))
                    return ApiResults.BadRequest($"未知层级树: {id}");

                var tree = view.Tree();
                return Results.Ok(new
                {
                    levels = tree.Levels,
                    zeroTotal = tree.ZeroTotal,
                    memberTotal = 0,
                    nodes = tree.Roots,
                });
            }
            catch (PackException ex) { return LoadFailed(ex); }
        });

        // ---- 集合成员:按集合号取成员清单(编码集合树的右栏懒加载;q 为集合内筛选) ----
        // 成员表 106,837 行不进树、也不整包下发:这里按 offset/limit 分页给,前端"加载更多"逐段追加。
        app.MapGet("/api/official/setmembers", IResult (string set, string? q, int? offset, int? limit) =>
        {
            if (string.IsNullOrWhiteSpace(set)) return ApiResults.BadRequest("缺少集合号");
            var view = OfficialPackView.Open(svc.PackPath);
            if (view is null) return NoWorkbook(svc);

            OfficialTableData idx, members;
            try { (idx, members) = view.CodeSets(); }
            catch (PackException ex) { return LoadFailed(ex); }

            var setCol = idx.IndexOf("set_id");
            var known = setCol >= 0 && idx.Rows.Any(r => string.Equals(r[setCol], set, StringComparison.OrdinalIgnoreCase));
            if (!known) return ApiResults.NotFound($"集合号 {set} 不在官方集合索引中");

            var si = members.IndexOf("set_id");
            if (si < 0) return ApiResults.NotFound("集合成员表缺列: set_id");
            var rows = members.Rows.Where(r => string.Equals(r[si], set, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(q))
                rows = rows.Where(r => OfficialPackView.RowContains(r, q!));

            var page = OfficialPackView.Page(members, rows.ToArray(), offset, limit, PageSpec.Members);
            return Results.Ok(new { set, page.Total, page.Offset, page.Limit, page.Rows });
        });

        // ---- 集合检索:一次覆盖集合号与成员编码/名称(编码集合树的检索框) ----
        // 集合本身没有官方中文名,所以"按名字找集合"实际是"按成员名找集合" —— 两张表一起搜,
        // 命中集合与命中成员一并返回,右栏据此给出可点选的落点。
        app.MapGet("/api/official/setsearch", IResult (string? q, int? limit) =>
        {
            var view = OfficialPackView.Open(svc.PackPath);
            if (view is null) return NoWorkbook(svc);
            var key = (q ?? "").Trim();
            OfficialTableData idx, members;
            try { (idx, members) = view.CodeSets(); }
            catch (PackException ex) { return LoadFailed(ex); }

            var take = PageSpec.Members.Clamp(limit);
            if (key.Length == 0)
                return Results.Ok(new { q = "", sets = Array.Empty<object>(), members = Array.Empty<object>(), memberTotal = 0 });

            // 两侧各搜各自的列,不跨列兜底:
            //   * 集合 —— set_id / type(不搜 member_count:否则"84"会命中一堆成员数含 84 的集合);
            //   * 成员 —— icd_code / icd_name(不搜 set_id:否则搜一个集合号会把它的几千条成员全倒出来,
            //     而"看这个集合的成员"是左树选中/点集合命中那一行的动线,不该由检索代劳)。
            var sSet = idx.IndexOf("set_id");
            var sType = idx.IndexOf("type");
            var hitSets = idx.Rows
                .Where(r => OfficialPackView.CellContains(r, sSet, key) || OfficialPackView.CellContains(r, sType, key))
                .Take(take)
                .Select(idx.ToObject)
                .ToArray();

            var mCode = members.IndexOf("icd_code");
            var mName = members.IndexOf("icd_name");
            var memHit = members.Rows.Where(r =>
                OfficialPackView.CellContains(r, mCode, key) || OfficialPackView.CellContains(r, mName, key));
            var hitMembers = memHit.Take(take).Select(members.ToObject).ToArray();
            return Results.Ok(new
            {
                q = key,
                sets = hitSets,
                members = hitMembers,
                memberTotal = memHit.Count(),
            });
        });

        // ---- 表数据:q=全文检索 / f+v=按列精确下钻 / sort+sortDir=按列排序 / offset+limit=分页 ----
        app.MapGet("/api/official/rows", IResult (string t, string? q, string? f, string? v, string? sort, string? sortDir, int? offset, int? limit) =>
        {
            var def = OfficialTable.ById(t ?? "");
            if (def is null) return ApiResults.BadRequest($"未知数据表: {t}");
            var view = OfficialPackView.Open(svc.PackPath);
            if (view is null) return NoWorkbook(svc);

            OfficialTableData table;
            try { table = view.Table(def.Id); }
            catch (PackException ex) { return LoadFailed(ex); }

            var rows = table.Rows.AsEnumerable();
            // 下钻:该列视为指向目标表某列的外键,值按 IgnoreCase 精确匹配(编码大小写同义)
            if (!string.IsNullOrWhiteSpace(f) && !string.IsNullOrEmpty(v))
            {
                var ci = table.IndexOf(f!);
                if (ci < 0) return ApiResults.BadRequest($"数据表 {def.Id} 不含列 {f}");
                rows = rows.Where(r => string.Equals(r[ci], v, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(q))
                rows = rows.Where(r => OfficialPackView.RowContains(r, q!));

            // 排序:sort=列 Key,sortDir=desc 为降序。数值列(前端 format=num)按数值比较,
            // 而非字典序(否则 10 会排在 9 前面);空值/非数值恒沉底 —— 口径见 OfficialPackView.CompareCell。
            if (!string.IsNullOrWhiteSpace(sort))
            {
                var si = table.IndexOf(sort!);
                if (si < 0) return ApiResults.BadRequest($"数据表 {def.Id} 不含列 {sort}");
                var numeric = string.Equals(
                    def.Columns.FirstOrDefault(c => string.Equals(c.Key, sort, StringComparison.OrdinalIgnoreCase))?.Format,
                    "num", StringComparison.OrdinalIgnoreCase);
                var desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
                var cmp = Comparer<string[]>.Create((a, b) => OfficialPackView.CompareCell(a[si], b[si], numeric, desc));
                rows = rows.OrderBy(r => r, cmp);
            }

            var all = rows as IReadOnlyList<string[]> ?? rows.ToArray();
            var page = OfficialPackView.Page(table, all, offset, limit, PageSpec.Rows);
            return Results.Ok(new { id = def.Id, page.Total, page.Offset, page.Limit, page.Rows });
        });
    }

    /// <summary>官方工作簿所在的目录(供"不走官方轨"的响应如实报出找过的位置)。</summary>
    private static string NoWorkbookDir(WebApp svc) => OfficialTable.DirOf(svc.PackPath);

    /// <summary>该数据包不走官方轨(目录内没有官方工作簿)。</summary>
    private static IResult NoWorkbook(WebApp svc) =>
        ApiResults.NotFound($"未找到官方配置信息工作簿: {NoWorkbookDir(svc)}");

    /// <summary>工作簿读取失败(表缺失 / 格式损坏)。消息来自引擎,原样回给前端便于定位。</summary>
    private static IResult LoadFailed(PackException ex) => ApiResults.NotFound(ex.Message);
}
