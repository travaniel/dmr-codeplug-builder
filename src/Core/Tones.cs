using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeplugBuilder.Core
{
    /// <summary>CTCSS / DCS values in the CPS spelling: "Off", "94.8", "100.0", "D023N", "D023I".</summary>
    public static class Tones
    {
        public static readonly string[] Ctcss =
        {
            "67.0", "69.3", "71.9", "74.4", "77.0", "79.7", "82.5", "85.4", "88.5", "91.5",
            "94.8", "97.4", "100.0", "103.5", "107.2", "110.9", "114.8", "118.8", "123.0", "127.3",
            "131.8", "136.5", "141.3", "146.2", "151.4", "156.7", "159.8", "162.2", "165.5", "167.9",
            "171.3", "173.8", "177.3", "179.9", "183.5", "186.2", "189.9", "192.8", "196.6", "199.5",
            "203.5", "206.5", "210.7", "218.1", "225.7", "229.1", "233.6", "241.8", "250.3", "254.1",
        };

        static readonly Regex DcsPattern = new Regex(@"^D?([0-7]{3})([NI])?$", RegexOptions.IgnoreCase);

        public static string Normalize(string value)
        {
            string s = (value ?? "").Trim();
            if (s.Length == 0) return "Off";
            string lower = s.ToLowerInvariant();
            if (lower == "off" || lower == "none" || lower == "no" || lower == "0") return "Off";

            if (decimal.TryParse(s.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal hz)
                && hz >= 60 && hz <= 260 && !s.StartsWith("0"))
                return hz.ToString("0.0", CultureInfo.InvariantCulture);

            var m = DcsPattern.Match(s);
            if (m.Success && (char.ToUpperInvariant(s[0]) == 'D' || m.Groups[2].Success))
                return "D" + m.Groups[1].Value + (m.Groups[2].Success ? m.Groups[2].Value.ToUpperInvariant() : "N");

            return s;
        }

        public static bool IsValid(string value)
        {
            string s = Normalize(value);
            if (s == "Off") return true;
            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal hz))
                return hz >= 60 && hz <= 260;
            return DcsPattern.IsMatch(s) && s.StartsWith("D");
        }
    }
}
