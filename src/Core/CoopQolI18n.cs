using System;
using MegaCrit.Sts2.Core.Localization;

namespace CoopQolSuite.Core;

public static class CoopQolI18n
{
    public static string CurrentLang
    {
        get
        {
            try
            {
                var lang = LocManager.Instance?.Language;
                return string.IsNullOrWhiteSpace(lang) ? "zhs" : lang;
            }
            catch
            {
                return "zhs";
            }
        }
    }

    public static bool IsZh => CurrentLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static string T(string zh, string en) => IsZh ? zh : en;
}
