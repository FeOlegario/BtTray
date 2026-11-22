using System;
using System.IO;
using System.Text;

namespace BtTray
{
    enum Language { PtBR, English }

    /// <summary>
    /// Interface language. Portuguese (Brazil) is the default; the choice is saved in
    /// %LOCALAPPDATA%\BtTray\configuracoes.txt as "idioma=pt-BR" or "idioma=en".
    /// Texts are written inline at each call site: L.T("texto em português", "English text").
    /// </summary>
    static class L
    {
        public static Language Current = Language.PtBR;

        /// <summary>Raised after the language changes, so open UI can redraw its texts.</summary>
        public static event Action Changed;

        public static string T(string ptBR, string en) { return Current == Language.English ? en : ptBR; }

        public static string Code(Language lang) { return lang == Language.English ? "en" : "pt-BR"; }

        /// <summary>Name of each language written in that language, as shown in the settings.</summary>
        public static string NativeName(Language lang) { return lang == Language.English ? "English" : "Português (Brasil)"; }

        /// <summary>Reads a saved language code; anything unknown falls back to Portuguese.</summary>
        public static Language Parse(string code)
        {
            code = (code ?? "").Trim();
            return code.Equals("en", StringComparison.OrdinalIgnoreCase) || code.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
                ? Language.English : Language.PtBR;
        }

        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BtTray", "configuracoes.txt"); }
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && line.Substring(0, eq).Trim().Equals("idioma", StringComparison.OrdinalIgnoreCase))
                        Current = Parse(line.Substring(eq + 1));
                }
            }
            catch { }
        }

        public static void Set(Language lang)
        {
            if (lang == Current) return;
            Current = lang;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[] { "idioma=" + Code(lang) }, Encoding.UTF8);
            }
            catch { }
            if (Changed != null) Changed();
        }
    }
}
