namespace DRGX.His.SqlServer;

/// <summary>
/// HealthOne年龄明细解析:格式 "000Y00M00D00H00N00W"(字母不一定齐全,数字位数不定)。
/// 日龄 = 月×30 + 日 + 周×7;年/时/分不计入。
///
/// <para><b>返回值语义(关键):</b> 返回 <c>null</c> 表示「没有日级证据」—— 明细为空、
/// 只有年/时/分单位,或压根不含 M/D/W。</para>
///
/// <para>为什么必须区分「无证据」与「0 天」:旧实现在无证据时返回 0,而成年患者的明细形如
/// <c>45Y</c> 同样解析出 0。于是当 age 列为空(字段缺失、脏数据,或源 SQL 用
/// <c>ELSE 0</c> 表示"非新生儿")时,引擎会把一个成年患者当成 **0 天新生儿**,
/// 命中 <c>ageDayLt 29</c> 一类新生儿入组条件,静默落到新生儿 ADRG。
/// 返回 null 后,引擎的「日龄可用」判定自然不成立,该类条件不会命中。</para>
///
/// <para>真实新生儿(如 <c>0Y0M0D</c> / <c>0Y0M5D</c>)必然含 M 或 D,因此仍能正常解析出 0 / 5。</para>
/// </summary>
public static class AgeDetailParser
{
    /// <summary>解析日龄(天);无日级证据时返回 null。</summary>
    public static int? Parse(string? ageDetail)
    {
        if (string.IsNullOrWhiteSpace(ageDetail)) return null;

        var day = 0;
        var hasDayLevelEvidence = false;
        var digits = new List<char>();

        for (var i = 0; i <= ageDetail.Length; i++)
        {
            var ch = i < ageDetail.Length ? ageDetail[i] : '\0';
            if (char.IsAsciiDigit(ch))
            {
                digits.Add(ch);
            }
            else if (char.IsLetter(ch))
            {
                // 位数不限,溢出按"脏数据"处理:该单位按 0 计,不让整单炸掉(与 HIS 连接器的宽容口径一致)
                var value = digits.Count > 0 && int.TryParse(new string(digits.ToArray()), out var parsed) ? parsed : 0;
                switch (ch)
                {
                    case 'M' or 'm':
                        hasDayLevelEvidence = true;
                        day += value * 30;
                        break;
                    case 'D' or 'd':
                        hasDayLevelEvidence = true;
                        day += value;
                        break;
                    case 'W' or 'w':
                        hasDayLevelEvidence = true;
                        day += value * 7;
                        break;
                    default:
                        break; // Y/H/N 等不计入日龄,也不构成日级证据
                }
                digits.Clear();
            }
            else if (ch == '\0' && digits.Count > 0)
            {
                digits.Clear(); // 尾部悬挂数字(无单位)丢弃
            }
        }

        return hasDayLevelEvidence ? day : null;
    }
}
