using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeplugBuilder.Core.Radio
{
    /// <summary>A radio setting worth changing when a codeplug is written, with the reason.</summary>
    public sealed class Recommendation
    {
        public SettingDef Def;
        public long Now, Value;
        public string Why;

        /// <summary>"Send talker alias: now Off, recommended On. Other radios..."</summary>
        public string Describe()
        {
            return Def.Label + ": now " + Show(Now) + ", recommended " + Show(Value) + ". " + Why;
        }

        string Show(long raw) { return new SettingValue { Def = Def, Raw = raw }.Display; }
    }

    /// <summary>
    /// Settings Radio > Write codeplug to radio offers to change in the same write (a codeplug write otherwise leaves the
    /// radio's settings as they are). Only settings whose address the dmr-tools description and the CPS agree on. The user
    /// says yes, or no once for good (<see cref="Decline"/>, kept by the app in its settings).
    /// </summary>
    public static class RecommendedSettings
    {
        /// <summary>The app setting that holds declined keys.</summary>
        public const string DeclinedKey = "DeclinedRadioSettings";

        /// <summary>What to offer for this read of the radio: settings not yet at the recommended value and not declined before.</summary>
        public static List<Recommendation> Check(MemoryImage img, string radioIdName, string declined)
        {
            var no = Declined(declined);
            var list = new List<Recommendation>();
            void Offer(string key, long value, string why)
            {
                var d = RadioSettings.Find(key);
                if (d == null || no.Contains(key) || !img.Has(d.Address, d.Length)) return;
                long now = RadioSettings.Read(img, d).Raw;
                if (now != value) list.Add(new Recommendation { Def = d, Now = now, Value = value, Why = why });
            }
            // Extended settings byte 0 (CPS: Talker Alias Settings > Send Talker Alias). Bytes 1 and 2 are left alone: dmr-tools
            // calls them display and encoding, but the CPS's OptionalSetting.CSV column order suggests alias type and display.
            string name = string.IsNullOrWhiteSpace(radioIdName) ? "" : " (\"" + radioIdName.Trim() + "\")";
            Offer("TalkerAliasSend", 1, "Other DMR radios and network last-heard lists can then show your name as you talk, even without you " +
                                        "in their contact list. The radio sends its alias: normally your Radio ID name" + name + ".");
            return list;
        }

        public static void Apply(MemoryImage img, IEnumerable<Recommendation> list)
        {
            foreach (var r in list) RadioSettings.Write(img, r.Def, r.Value);
        }

        /// <summary>The declined keys in an app setting value ("TalkerAliasSend,Other").</summary>
        public static HashSet<string> Declined(string value)
        {
            return new HashSet<string>((value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim()), StringComparer.Ordinal);
        }

        /// <summary>The app setting value after declining <paramref name="list"/>.</summary>
        public static string Decline(string value, IEnumerable<Recommendation> list)
        {
            var keys = Declined(value);
            foreach (var r in list) keys.Add(r.Def.Key);
            return string.Join(",", keys.OrderBy(k => k, StringComparer.Ordinal));
        }
    }
}
