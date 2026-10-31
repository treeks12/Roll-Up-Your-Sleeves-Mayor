using System;
using ColossalFramework;
using ColossalFramework.Globalization;

namespace SC4Evolution.Mod.UI
{
    internal static class GameLanguage
    {
        internal static bool English
        {
            get
            {
                if (!SingletonLite<LocaleManager>.exists) return true;
                string language = SingletonLite<LocaleManager>.instance.language;
                return string.IsNullOrEmpty(language) || !language.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
            }
        }
        internal static string Text(string portuguese, string english)
        {
            return English ? english : portuguese;
        }
    }
}
