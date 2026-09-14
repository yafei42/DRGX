using DRGX.Engine;

namespace DRGX.Host;

// ============================================================================
// 字典检索端点:/api/search 前缀/包含分级排序检索、/api/lookup 精确查码(含编码映射)、
// /api/names 批量取名称。检索打分是纯函数,收敛为本类私有静态方法。
//
// 检索与查码按 `version` 分流:国临版只搜国临目录、医保版只搜医保目录 ——
// 用并集字典搜会让"选得到但分不了组"的码混进候选(国临独有 4,418 / 医保独有 1,855)。
// /api/names 刻意**不分版本**:结果区回显的码经转换后恒为医保版,且要覆盖 MCC/CC
// 与有效操作清单里的码,用分组侧并集字典最稳。
//
// 条目上的分组属性(灰码/有效/MCC-CC/机器人)由 PackRuntime 建索引时按医保版目标码算好,
// 本类不再二次推导 —— 历史上有 WithMappedTarget 与 Lookup* 两套并行实现,口径已经开始漂移。
// ============================================================================

internal static class SearchEndpoints
{
    public static void Map(WebApplication app, WebApp svc)
    {
        // ---- 字典检索:编码前缀/包含 + 名称前缀/包含,按匹配强度排序 ----
        app.MapGet("/api/search", (string type, string? q, int? limit, string? version, bool? useCodeMap) =>
        {
            var query = (q ?? "").Trim();
            var system = CodeSystemParam.Parse(version, useCodeMap);
            if (query.Length == 0)
                return Results.Ok(new { items = Array.Empty<DictEntry>(), version = VersionId(system) });
            var index = svc.Index(system); // 经容器取字典索引(单一入口)
            var source = type == "procedure" ? index.Procedures : index.Diagnoses;
            var byCode = type == "procedure" ? index.ProcByCode : index.DiagByCode;
            var size = Math.Clamp(limit ?? 20, 1, 50);

            IReadOnlyList<DictEntry> searched = Search(source, query, size);

            // 精确录入扩展码/映射码时必须置顶;否则通用词(如"填塞")可能把它挤出前 8 条
            byCode.TryGetValue(query, out var exact);
            // 精确命中已在检索结果里时去重;否则置顶,保证完整编码直接回车也可见
            if (exact is not null)
                searched = searched.Where(e => !string.Equals(e.Code, exact.Code, StringComparison.OrdinalIgnoreCase)).ToArray();
            var items = exact is null
                ? searched
                : new[] { exact }.Concat(searched.Take(size - 1)).ToArray();
            return Results.Ok(new { items, version = VersionId(system) });
        });

        // ---- 精确查码:编码员直接键入编码时回显名称;国临版命中映射时携带医保目标码 ----
        app.MapGet("/api/lookup", (string type, string code, string? version, bool? useCodeMap) =>
        {
            var raw = code.Trim();
            if (raw.Length == 0) return Results.Ok<object?>(null);
            var system = CodeSystemParam.Parse(version, useCodeMap);
            var index = svc.Index(system);
            var byCode = type == "procedure" ? index.ProcByCode : index.DiagByCode;
            byCode.TryGetValue(raw, out var entry);
            return Results.Ok<object?>(entry);
        });

        // ---- 批量取名称:结果详情里 MCC/CC/有效操作的编码回显名称 ----
        app.MapPost("/api/names", (NamesRequest req) =>
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in req.Codes ?? [])
            {
                var code = raw.Trim();
                if (code.Length == 0 || names.ContainsKey(code)) continue;
                if (svc.Pack.DiagnosisNames.TryGetValue(code, out var n1)) names[code] = n1;
                else if (svc.Pack.ProcedureNames.TryGetValue(code, out var n2)) names[code] = n2;
            }
            return Results.Ok(new { names });
        });
    }

    /// <summary>版本标识符(/api/info 与检索响应共用的稳定字符串)。</summary>
    internal static string VersionId(CodeSystem system) => system == CodeSystem.Guolin ? "guolin" : "yibao";

    /// <summary>检索打分:前缀 &lt; 名称前缀 &lt; 编码包含 &lt; 名称包含,同级按编码序稳定排序。</summary>
    private static IReadOnlyList<DictEntry> Search(IReadOnlyList<DictEntry> index, string query, int limit)
    {
        List<(DictEntry E, int Score)> hits = new();
        foreach (var e in index)
        {
            int score = Score(e, query);
            if (score < 4) hits.Add((e, score));
        }
        if (hits.Count == 0) return [];
        hits.Sort((a, b) => a.Score != b.Score
            ? a.Score - b.Score
            : string.CompareOrdinal(a.E.Code, b.E.Code));
        return hits.Take(limit).Select(h => h.E).ToArray();

        static int Score(DictEntry e, string q)
        {
            if (e.Code.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 0;
            if (e.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 1;
            if (e.Code.Contains(q, StringComparison.OrdinalIgnoreCase)) return 2;
            if (e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 3;
            return 4;
        }
    }
}
