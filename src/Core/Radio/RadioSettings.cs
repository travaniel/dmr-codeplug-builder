using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeplugBuilder.Core.Radio
{
    public enum SettingKind
    {
        /// <summary>A number picked from a list of labelled values (enumerations and small ranges).</summary>
        Choice,
        /// <summary>On/off: a whole byte (non-zero = on) or a single bit.</summary>
        Flag,
        /// <summary>ASCII text, 00-padded.</summary>
        Text,
        /// <summary>Frequency, uint32 little-endian in 10 Hz.</summary>
        FrequencyLe,
        /// <summary>Frequency, 8-digit big-endian BCD in 10 Hz.</summary>
        FrequencyBcd,
    }

    /// <summary>
    /// One radio setting: where it lives and how its stored number reads. Taken from the 6X2 PRO codeplug
    /// description (firmware 1.21b, dmr-tools.github.io); <see cref="Verified"/> marks the ones checked
    /// against the CPS on the user's radio.
    /// </summary>
    public sealed class SettingDef
    {
        public string Key, Group, Label, Help;
        public SettingKind Kind;
        public uint Address;
        /// <summary>For bit fields: lowest bit and width. Width 8 at bit 0 = the whole byte.</summary>
        public int Lsb, Bits = 8;
        /// <summary>Bytes, for text and frequencies.</summary>
        public int Length = 1;
        /// <summary>Choice: allowed stored values with their labels, in display order.</summary>
        public List<KeyValuePair<int, string>> Options = new List<KeyValuePair<int, string>>();
        /// <summary>Column in the CPS's OptionalSetting.CSV holding the same number (null if none or unknown).</summary>
        public string CsvColumn;
        /// <summary>Stored value â†’ the number the CPS writes in <see cref="CsvColumn"/>.</summary>
        public Func<int, int> ToCsv = v => v;
        public bool Verified;


        public string OptionLabel(int raw)
        {
            foreach (var o in Options) if (o.Key == raw) return o.Value;
            return "(" + raw.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }

    public sealed class SettingValue
    {
        public SettingDef Def;
        /// <summary>Stored number (Choice, Flag 0/1, frequencies in 10 Hz); 0 for text.</summary>
        public long Raw;
        public string Text = "";

        /// <summary>The value as the user sees it.</summary>
        public string Display
        {
            get
            {
                switch (Def.Kind)
                {
                    case SettingKind.Choice: return Def.OptionLabel((int)Raw);
                    case SettingKind.Flag: return Raw != 0 ? "On" : "Off";
                    case SettingKind.Text: return Text;
                    default: return (Raw / 100000m).ToString("0.00000", CultureInfo.InvariantCulture) + " MHz";
                }
            }
        }
    }

    /// <summary>
    /// The radio's optional settings (CPS: Optional Setting) read from and written to a <see cref="MemoryImage"/>.
    /// Only fields with a documented meaning are listed; unknown bytes are never touched.
    /// </summary>
    public static class RadioSettings
    {
        public const uint General = Dmr6x2Pro.GeneralSettings;   // 0x2500000
        public const uint Boot = Dmr6x2Pro.BootSettings;         // 0x2500600
        public const uint Extended = Dmr6x2Pro.ExtendedSettings; // 0x2501400
        public const uint Aprs = Dmr6x2Pro.AprsSettings;         // 0x2501000

        public static readonly string[] KeyFunctions =
        {
            "Off", "Voltage", "Power", "Repeater", "Reverse", "DMR Encryption", "Call", "VOX", "VFO/Channel", "Sub PTT",
            "Scan", "FM Radio", "Alarm", "Record Switch", "Record", "SMS", "Dial", "GPS Info", "Monitor", "Main Channel Switch",
            "Hot Key 1", "Hot Key 2", "Hot Key 3", "Hot Key 4", "Hot Key 5", "Hot Key 6", "Work Alone", "Nuisance Delete",
            "DMR Monitor", "Sub Channel Switch", "Priority Zone", "Programming Scan", "MIC Sound Quality", "Last Call Reply",
            "Channel Type Switch", "Simplex Repeater", "Ranging", "Channel Ranging", "Maximum Volume", "Slot Switch",
            "FM Squelch", "Roaming", "Zone Select", "Roaming Settings", "Fixed Time Mute", "CTCSS/DCS Settings", "APRS Type",
            "APRS Settings", "TBST Send", "Bluetooth Toggle", "GPS Toggle", "Channel Name", "APRS Send", "FM APRS Info",
            "GPS Roaming", "CTCSS/DCS Scan", "DIM Shut", "Satellite Predict",
        };

        static readonly string[] Colors = { "Orange", "Red", "Yellow", "Green", "Turquoise", "Blue", "White", "Black" };
        static readonly string[] OnOffChoices = { "Off", "On" };

        static List<SettingDef> all;

        /// <summary>Every known setting, grouped in display order.</summary>
        public static IReadOnlyList<SettingDef> All => all ?? (all = Build());

        public static SettingDef Find(string key) => All.FirstOrDefault(d => d.Key == key);

        // ---- reading and writing -------------------------------------------------------------

        public static List<SettingValue> Read(MemoryImage img)
        {
            return All.Where(d => img.Has(d.Address, d.Length)).Select(d => Read(img, d)).ToList();
        }

        public static SettingValue Read(MemoryImage img, SettingDef d)
        {
            var v = new SettingValue { Def = d };
            byte[] b = img.Get(d.Address, d.Length);
            switch (d.Kind)
            {
                case SettingKind.Text:
                    v.Text = RadioCodec.AsciiZ(b, 0, d.Length);
                    break;
                case SettingKind.FrequencyLe:
                    v.Raw = (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);
                    break;
                case SettingKind.FrequencyBcd:
                    v.Raw = RadioCodec.IsBcd(b, 0) ? RadioCodec.BcdBigEndian(b, 0) : 0;
                    break;
                default:
                    v.Raw = b[0] >> d.Lsb & ((1 << d.Bits) - 1);
                    if (d.Kind == SettingKind.Flag) v.Raw = v.Raw != 0 ? 1 : 0;
                    break;
            }
            return v;
        }

        /// <summary>Stores a number (Choice, Flag, frequency in 10 Hz). Bits outside the field are kept.</summary>
        public static void Write(MemoryImage img, SettingDef d, long raw)
        {
            if (!img.Has(d.Address, d.Length)) throw new InvalidOperationException(d.Label + " wasn't read from the radio.");
            switch (d.Kind)
            {
                case SettingKind.Choice:
                    if (d.Options.All(o => o.Key != raw)) throw new ArgumentOutOfRangeException(nameof(raw), raw + " isn't a valid value for " + d.Label + ".");
                    break;
                case SettingKind.Flag:
                    raw = raw != 0 ? 1 : 0;
                    break;
                case SettingKind.FrequencyLe:
                    img.Set(d.Address, BitConverter.GetBytes((uint)raw));
                    return;
                case SettingKind.FrequencyBcd:
                    var bcd = new byte[4];
                    uint v = (uint)raw;
                    for (int i = 3; i >= 0; i--) { uint two = v % 100; v /= 100; bcd[i] = (byte)(two / 10 << 4 | two % 10); }
                    img.Set(d.Address, bcd);
                    return;
                case SettingKind.Text:
                    throw new ArgumentException(d.Label + " is text; use WriteText.");
            }
            int mask = ((1 << d.Bits) - 1) << d.Lsb;
            int old = img.U8(d.Address);
            img.Set(d.Address, new[] { (byte)(old & ~mask | ((int)raw << d.Lsb & mask)) });
        }

        public static void WriteText(MemoryImage img, SettingDef d, string text)
        {
            if (d.Kind != SettingKind.Text) throw new ArgumentException(d.Label + " isn't text.");
            if (!img.Has(d.Address, d.Length)) throw new InvalidOperationException(d.Label + " wasn't read from the radio.");
            var b = new byte[d.Length];
            string s = text ?? "";
            for (int i = 0; i < s.Length && i < d.Length; i++)
            {
                char c = s[i];
                if (c < 0x20 || c > 0x7E) throw new ArgumentException(d.Label + " can only hold plain ASCII characters.");
                b[i] = (byte)c;
            }
            img.Set(d.Address, b);
        }

        /// <summary>
        /// Checks the settings that have an OptionalSetting.CSV column against a CPS export of the same radio.
        /// Returns one line per difference. (CPS 1.22e writes 0 for most of the general block, so only the
        /// columns listed in the table are compared.)
        /// </summary>
        public static List<string> CompareWithCps(MemoryImage img, string optionalSettingCsv, out int compared)
        {
            var t = CsvTable.Load(optionalSettingCsv);
            var row = t.Rows.FirstOrDefault() ?? new List<string>();
            var diffs = new List<string>();
            compared = 0;
            foreach (var d in All.Where(x => x.CsvColumn != null && img.Has(x.Address, x.Length)))
            {
                if (t.IndexOf(d.CsvColumn) < 0) { diffs.Add(d.CsvColumn + ": column not in the CPS file"); continue; }
                compared++;
                int want = d.ToCsv((int)Read(img, d).Raw);
                string got = t.Get(row, d.CsvColumn);
                if (got != want.ToString(CultureInfo.InvariantCulture))
                    diffs.Add(d.Label + " (" + d.CsvColumn + "): CPS \"" + got + "\", radio " + want);
            }
            return diffs;
        }

        /// <summary>Plain-text listing of every setting, grouped, for logs and checking against the CPS screen.</summary>
        public static string Report(MemoryImage img)
        {
            var sb = new StringBuilder();
            foreach (var g in Read(img).GroupBy(v => v.Def.Group))
            {
                sb.Append(g.Key).Append("\r\n");
                foreach (var v in g)
                    sb.Append("  ").Append(v.Def.Label.PadRight(38)).Append(' ').Append(v.Display.PadRight(22))
                      .Append(v.Def.Verified ? "" : "  (unchecked)").Append("\r\n");
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        // ---- the table ----------------------------------------------------------------------

        static List<SettingDef> Build()
        {
            var b = new Builder();

            b.Group("Power-on");
            b.Choice("BootDisplay", "Power-on display", General + 0x06, "Default", "Custom text", "Custom image").Ok(); // user confirmed: custom image = 2
            b.Text("BootLine1", "Power-on text line 1", Boot + 0x00, 16);
            b.Text("BootLine2", "Power-on text line 2", Boot + 0x10, 16);
            b.Flag("BootPasswordOn", "Power-on password", General + 0x07);
            b.Text("BootPassword", "Power-on password digits", Boot + 0x20, 8);
            b.Flag("BootSound", "Power-on sound", General + 0x39);
            b.Flag("DefaultChannelOn", "Start on a fixed channel", General + 0xCE).Csv("StartChUse").Ok();
            b.Range("DefaultZoneA", "Start zone A", General + 0xCF, 0, 249, v => "Zone " + (v + 1)).Csv("StartZone1");
            b.Range("DefaultZoneB", "Start zone B", General + 0xD0, 0, 249, v => "Zone " + (v + 1)).Csv("StartZone2");
            b.Range("DefaultChannelA", "Start channel A (in zone)", General + 0xD1, 0, 249, v => "Channel " + (v + 1)).Special(0xFF, "VFO").Csv("StartCurChan1", v => (v + 1) & 0xFF).Ok();
            b.Range("DefaultChannelB", "Start channel B (in zone)", General + 0xD2, 0, 249, v => "Channel " + (v + 1)).Special(0xFF, "VFO").Csv("StartCurChan2", v => (v + 1) & 0xFF).Ok();

            b.Group("Display");
            b.Choice("DisplayMode", "Display mode", General + 0x01, "Channel", "Frequency");
            b.Range("Brightness", "Brightness", General + 0x26, 0, 4, v => (v + 1).ToString(CultureInfo.InvariantCulture));
            b.Choice("Backlight", "Backlight time", General + 0x27, "Always", "5 s", "10 s", "15 s", "20 s", "25 s", "30 s", "1 min", "2 min", "3 min", "4 min", "5 min");
            b.Range("RxBacklight", "Backlight on receive", General + 0xD4, 0, 30, v => v + " s").Special(0, "Always").Csv("RxDimWait");
            b.Range("MenuTime", "Menu exit time", General + 0x37, 0, 11, v => (v + 1) * 5 + " s");
            b.Flag("ShowClock", "Show clock", General + 0x51);
            b.Flag("VolumeNote", "Show volume change", General + 0x47);
            b.Choice("LastCaller", "Last caller display", General + 0x4D, "Off", "DMR ID", "Callsign", "Both");
            b.Flag("KeepLastCaller", "Keep last caller on channel change", General + 0xD3).Csv("LastHeardChanSet");
            b.Flag("LastCallOnLaunch", "Show last call on power-on", General + 0xB8);
            b.Choice("CallsignMode", "Contact display", General + 0xAF, "Name", "Callsign");
            b.Choice("CallsignColor", "Contact color", General + 0xB0, Colors);
            b.Flag("ShowTxContact", "Show TX contact", General + 0xB4);
            b.Choice("ChannelBackground", "Channel background", General + 0xD5, "Black", "Blue");
            b.Choice("TextColor", "Text color", Extended + 0x03, "White", "Black", "Orange", "Red", "Yellow", "Green", "Turquoise", "Blue").Csv("WorkCharDisColour");
            b.Flag("CustomBackground", "Custom channel background", Extended + 0x04).Csv("WorkBackPic").Ok();
            b.Choice("ZoneAColor", "Zone A name color", Extended + 0x12, Colors).Csv("ZoneNameColourA").Ok();
            b.Choice("ZoneBColor", "Zone B name color", Extended + 0x13, Colors).Csv("ZoneNameColourB").Ok();
            b.Choice("ChannelAColor", "Channel A name color", Extended + 0x14, Colors).Csv("ChanNameColourA").Ok();
            b.Choice("ChannelBColor", "Channel B name color", Extended + 0x15, Colors).Csv("ChanNameColourB").Ok();
            b.Choice("ChannelIndex", "Channel number shown", Extended + 0x24, "Overall number", "Number within zone").Csv("ChanNumDisKind");

            b.Group("Audio and tones");
            b.Flag("KeyTone", "Key tone", General + 0x00);
            b.Bits("KeyToneVolume", "Key tone volume", General + 0xB5, 0, 4, 0, 15, v => v.ToString(CultureInfo.InvariantCulture)).Special(0, "Adjustable");
            b.Range("MaxVolume", "Maximum speaker volume", General + 0x3B, 0, 8, v => v.ToString(CultureInfo.InvariantCulture)).Special(0, "Indoor");
            b.Range("MaxHeadphoneVolume", "Maximum earphone volume", General + 0x52, 0, 8, v => v.ToString(CultureInfo.InvariantCulture)).Special(0, "Indoor");
            b.Range("MicGain", "Mic gain (digital)", General + 0x0F, 0, 4, v => (v + 1).ToString(CultureInfo.InvariantCulture));
            b.Range("FmMicGain", "Mic gain (FM)", Extended + 0x20, 0, 4, v => (v + 1).ToString(CultureInfo.InvariantCulture)).Csv("AnaMic").Ok();
            b.Flag("EnhancedSound", "Enhanced sound quality", General + 0x57);
            b.Flag("CallAlertTone", "Call alert tone", General + 0x2F);
            b.Flag("SmsAlertTone", "SMS alert tone", General + 0x29);
            b.Bits("FmTalkPermit", "Talk permit tone (FM)", General + 0x31, 1, 1, 0, 1, v => OnOffChoices[v]);
            b.Bits("DmrTalkPermit", "Talk permit tone (digital)", General + 0x31, 0, 1, 0, 1, v => OnOffChoices[v]);
            b.Flag("CallResetTone", "Call reset tone (digital)", General + 0x32);
            b.Flag("FmIdleTone", "Channel idle tone (FM)", Extended + 0x1F).Csv("AnaSqOnVoice");
            b.Choice("DmrIdleTone", "Channel idle tone (digital)", General + 0x36, "Off", "Type 1", "Type 2", "Type 3");
            b.Choice("Tbst", "Tone burst (TBST)", General + 0x2E, "1000 Hz", "1450 Hz", "1750 Hz", "2100 Hz");
            b.Choice("DtmfDuration", "DTMF tone length", General + 0x23, "50 ms", "100 ms", "200 ms", "300 ms", "500 ms");
            b.Flag("Record", "Recording", General + 0x22);
            b.Range("RecordDelay", "Recording delay", General + 0xAE, 0, 25, v => (v * 0.2m).ToString("0.0", CultureInfo.InvariantCulture) + " s");

            b.Group("Power and timers");
            b.Range("Tot", "Transmit time-out", General + 0x04, 0, 16, v => v * 30 + " s").Special(0, "Off");
            b.Flag("TotWarning", "Time-out warning", Extended + 0x21).Csv("TotPreEn").Ok();
            b.Flag("TxAgc", "TX power control (ATPC)", Extended + 0x22).Csv("TxAgcCon").Ok();
            b.Choice("PowerSave", "Power save", General + 0x0B, "Off", "1:1", "1:2");
            b.Choice("AutoPowerOff", "Auto power off", General + 0x03, "Off", "10 min", "30 min", "60 min", "120 min");
            b.Range("MuteTimer", "Fixed-time mute", Extended + 0x10, 0, 255, v => v + 1 + " min").Csv("FixTimeMute");

            b.Group("Keys");
            b.Choice("KeyLock", "Key lock", General + 0x02, "Auto", "Manual");
            b.Bits("LockProfessional", "Professional key lock", General + 0xB6, 3, 1, 0, 1, v => OnOffChoices[v]);
            b.Bits("LockSideKeys", "Lock side keys", General + 0xB6, 2, 1, 0, 1, v => OnOffChoices[v]);
            b.Bits("LockKeyboard", "Lock keypad", General + 0xB6, 1, 1, 0, 1, v => OnOffChoices[v]);
            b.Bits("LockKnob", "Lock knob", General + 0xB6, 0, 1, 0, 1, v => OnOffChoices[v]);
            b.Range("LongPress", "Long press time", General + 0x46, 0, 4, v => v + 1 + " s");
            string[] keys = { "PF1", "PF2", "PF3", "P1", "P2" };
            for (int i = 0; i < keys.Length; i++)
                b.Choice("Key" + keys[i] + "Short", keys[i] + " short press", General + 0x10 + (uint)i, KeyFunctions);
            for (int i = 0; i < keys.Length; i++)
                b.Choice("Key" + keys[i] + "Long", keys[i] + " long press", General + 0x41 + (uint)i, KeyFunctions);

            b.Group("Squelch and VOX");
            b.Range("SquelchA", "Squelch level A", General + 0x09, 0, 5, v => v.ToString(CultureInfo.InvariantCulture)).Special(0, "Open");
            b.Range("SquelchB", "Squelch level B", General + 0x0A, 0, 5, v => v.ToString(CultureInfo.InvariantCulture)).Special(0, "Open");
            b.Choice("Ste", "Squelch tail elimination", General + 0x17, "Off", "Silent", "120Â°", "180Â°", "240Â°");
            b.Choice("SteFrequency", "Tail elimination frequency", General + 0x18, "Off", "55.2 Hz", "259.2 Hz");
            b.Range("VoxLevel", "VOX level", General + 0x0C, 0, 3, v => v.ToString(CultureInfo.InvariantCulture)).Special(0, "Off");
            b.Range("VoxDelay", "VOX delay", General + 0x0D, 0, 25, v => ((v + 5) * 0.1m).ToString("0.0", CultureInfo.InvariantCulture) + " s");
            b.Choice("VoxSource", "VOX source", General + 0x33, "Built-in mic", "External mic", "Both");

            b.Group("Digital");
            b.Range("GroupHang", "Group call hold time", General + 0x19, 0, 31, v => v + " s").Special(31, "Unlimited");
            b.Range("PrivateHang", "Private call hold time", General + 0x1A, 0, 31, v => v + " s").Special(31, "Unlimited");
            b.Range("DialGroupHang", "Manual dial group hold time", General + 0xD6, 0, 31, v => v + " s").Special(31, "Unlimited").Csv("DialGroupHold").Ok();
            b.Range("DialPrivateHang", "Manual dial private hold time", General + 0xD7, 0, 31, v => v + " s").Special(31, "Unlimited").Csv("DialPrivateHold").Ok();
            b.Range("VoiceHeader", "Voice header repeats", General + 0x1B, 0, 6, v => (v + 2).ToString(CultureInfo.InvariantCulture));
            b.Range("Preamble", "TX preamble", General + 0x1C, 0, 40, v => v * 60 + " ms");
            b.Flag("FilterOwnId", "Hide own ID in missed calls", General + 0x38);
            b.Flag("SelectTxContact", "Select TX contact", General + 0x40);
            b.Flag("RemoteMonitor", "Allow remote monitor", General + 0x3E);
            b.Flag("GetPosition", "Answer position requests", General + 0x3F);
            b.Choice("DmrMonitor", "Promiscuous (DMR monitor)", General + 0x49, "Off", "Single slot", "Dual slot");
            b.Choice("MonitorCc", "Monitor color code", General + 0x4A, "Any", "Same");
            b.Choice("MonitorId", "Monitor ID", General + 0x4B, "Any", "Same");
            b.Flag("MonitorHoldSlot", "Monitor holds slot", General + 0x4C);
            b.Flag("KeepCallChannel", "Keep call channel", General + 0x6E);
            b.Flag("SmsConfirm", "SMS confirmation", General + 0x71);
            b.Choice("SmsFormat", "SMS format", General + 0xB9, "Motorola", "Hytera", "DMR");
            b.Flag("AddressBookOwnId", "Send own ID with address book", General + 0xCD).Csv("BookOwnId").Ok();
            b.Choice("SimplexRepeater", "Simplex repeater", General + 0xB1, "Off", "On");
            b.Choice("SimplexRepeaterMonitor", "Monitor simplex repeater", General + 0xB3, "Off", "On");
            b.Choice("SimplexRepeaterSlot", "Simplex repeater slot", General + 0xB7, "Slot 1", "Slot 2", "Channel slot");
            b.Flag("TalkerAliasSend", "Send talker alias", Extended + 0x00).Csv("SctTxTalkAliasEn");
            b.Choice("TalkerAliasSource", "Talker alias display", Extended + 0x01, "Off", "Contacts first", "Over the air first").Csv("SctTxTalkAliasKind");
            b.Choice("TalkerAliasFormat", "Talker alias encoding", Extended + 0x02, "ISO 7-bit", "ISO 8-bit", "Unicode");
            b.Choice("EncryptionType", "Encryption type", Extended + 0x11, "DMR", "AES + ARC4");
            b.Range("FmHang", "FM call hold time", General + 0x50, 0, 30, v => v + " s");

            b.Group("Scan and VFO");
            b.Choice("Step", "VFO step", General + 0x08, "2.5 kHz", "5 kHz", "6.25 kHz", "10 kHz", "12.5 kHz", "20 kHz", "25 kHz", "30 kHz", "50 kHz");
            b.Choice("ScanMode", "Scan resume", General + 0x0E, "Time", "Carrier", "Stop");
            b.Choice("WorkModeA", "Side A mode", General + 0x15, "Channel", "VFO");
            b.Choice("WorkModeB", "Side B mode", General + 0x16, "Channel", "VFO");
            b.Choice("MainSide", "Main side", General + 0x2C, "A", "B");
            b.Flag("SubChannel", "Show sub channel", General + 0x2D);
            b.Range("PriorityZoneA", "Priority zone A", General + 0x6F, 0, 249, v => "Zone " + (v + 1)).Special(0xFF, "None");
            b.Range("PriorityZoneB", "Priority zone B", General + 0x70, 0, 249, v => "Zone " + (v + 1)).Special(0xFF, "None");
            b.Freq("VfoScanUhfLow", "VFO scan UHF from", General + 0x58);
            b.Freq("VfoScanUhfHigh", "VFO scan UHF to", General + 0x5C);
            b.Freq("VfoScanVhfLow", "VFO scan VHF from", General + 0x60);
            b.Freq("VfoScanVhfHigh", "VFO scan VHF to", General + 0x64);

            b.Group("Auto repeater");
            b.Choice("AutoRepeaterA", "Auto repeater A", General + 0x48, "Off", "Positive", "Negative");
            b.Choice("AutoRepeaterB", "Auto repeater B", General + 0xCC, "Off", "Positive", "Negative");
            b.Range("AutoRepeaterUhfOffset", "UHF offset (list entry)", General + 0x68, 0, 249, v => "Offset " + (v + 1)).Special(0xFF, "Off");
            b.Range("AutoRepeaterVhfOffset", "VHF offset (list entry)", General + 0x69, 0, 249, v => "Offset " + (v + 1)).Special(0xFF, "Off");
            b.Freq("AutoRepeaterVhfLow", "VHF range from", General + 0xBC);
            b.Freq("AutoRepeaterVhfHigh", "VHF range to", General + 0xC0);
            b.Freq("AutoRepeaterUhfLow", "UHF range from", General + 0xC4);
            b.Freq("AutoRepeaterUhfHigh", "UHF range to", General + 0xC8);

            b.Group("Roaming");
            b.Range("RoamingZone", "Roaming zone", Extended + 0x05, 0, 63, v => "Roaming zone " + (v + 1)).Csv("CurRoamZone");
            b.Flag("AutoRoaming", "Auto roaming", Extended + 0x06).Csv("TimeRoamOn");
            b.Flag("RepeaterCheck", "Repeater check", Extended + 0x07).Csv("BsModeCheck");
            b.Choice("OutOfRangeAlert", "Out-of-range alert", Extended + 0x08, "Off", "Tone", "Voice").Csv("OutRepNote").Ok();
            b.Range("OutOfRangeRepeats", "Out-of-range alert repeats", Extended + 0x09, 0, 9, v => (v + 1).ToString(CultureInfo.InvariantCulture)).Csv("OutNoteTimes").Ok();
            b.Range("RepeaterCheckInterval", "Repeater check interval", Extended + 0x0A, 0, 9, v => (v + 1) * 5 + " s").Csv("TimeBsCheck").Ok();
            b.Range("RepeaterReconnects", "Reconnection attempts", Extended + 0x0B, 0, 4, v => (v + 1).ToString(CultureInfo.InvariantCulture)).Csv("BsCheckTimes").Ok();
            b.Choice("RoamingStart", "Start roaming on", Extended + 0x0C, "Time", "Out of range").Csv("FixRomanStartOp");
            b.Range("RoamingInterval", "Auto roaming interval", Extended + 0x0D, 0, 255, v => v + 1 + " min").Csv("RoamPerod");
            b.Range("RoamingDelay", "Roaming delay", Extended + 0x0E, 0, 255, v => v + " s").Special(0, "Off").Csv("RoamEffectWait");
            b.Choice("RoamingReturn", "Return to home on", Extended + 0x0F, "Time", "Out of range").Csv("RoamEffectChanDis").Ok();

            b.Group("Bluetooth");
            b.Flag("Bluetooth", "Bluetooth", Extended + 0x16).Csv("BlueToothOn");
            b.Flag("BluetoothMicToo", "Keep built-in mic", Extended + 0x17).Csv("MicInBlueTooth");
            b.Flag("BluetoothSpeakerToo", "Keep built-in speaker", Extended + 0x18).Csv("SpkInBlueTooth");
            b.Range("BluetoothMicGain", "Bluetooth mic gain", Extended + 0x19, 0, 4, v => (v + 1).ToString(CultureInfo.InvariantCulture)).Csv("BhtMicGain").Ok();
            b.Range("BluetoothSpeakerGain", "Bluetooth speaker gain", Extended + 0x1A, 0, 5, v => (v + 1).ToString(CultureInfo.InvariantCulture)).Csv("BhtSpkGain").Ok();
            b.Range("BluetoothHold", "PTT hold time", Extended + 0x1B, 0, 33, v => v <= 30 ? v + " s" : v == 31 ? "60 s" : v == 32 ? "120 s" : "Unlimited").Special(0, "Off").Csv("BhtHoldTime").Ok();
            b.Range("BluetoothHoldDelay", "PTT hold delay", Extended + 0x1C, 0, 10, v => (v * 0.5m).ToString("0.0", CultureInfo.InvariantCulture) + " s").Special(0, "30 ms").Csv("BhtHoldDelay");
            b.Flag("BluetoothPttLatch", "PTT latch", Extended + 0x1D).Csv("BhtPttHold");
            b.Range("BluetoothSleep", "Bluetooth PTT sleep", Extended + 0x1E, 0, 4, v => v + " min").Special(0, "Never").Csv("PttSleepTime");

            b.Group("GPS and APRS");
            b.Flag("Gps", "GPS", General + 0x28);
            b.Choice("GpsSystems", "Positioning systems", Extended + 0x23, "GPS", "BeiDou", "GPS + BeiDou").Csv("GpsMode");
            // Half-hour steps from GMT-12 (stored 14 = GMT-5 on the user's radio per the CPS; dmr-tools lists whole hours, which is wrong here).
            b.Range("TimeZone", "Time zone", General + 0x30, 0, 50, TimeZoneLabel);
            b.Choice("RangingUnits", "Distance units", General + 0xBA, "Metric", "Imperial");
            b.Range("RangingInterval", "Ranging interval", General + 0xB2, 5, 255, v => v + " s");
            b.Flag("WeatherAlarm", "NOAA weather alarm", Extended + 0x26).Csv("WxAlarm");
            b.Range("FixedLocation", "Position source", Extended + 0x27, 0, 8, v => "Fixed location " + v).Special(0, "GPS");
            b.Text("AprsSource", "APRS callsign", Aprs + 0x1D, 6);
            b.Range("AprsSsid", "APRS SSID", Aprs + 0x23, 0, 15, v => v.ToString(CultureInfo.InvariantCulture));
            b.Text("AprsDestination", "APRS destination", Aprs + 0x16, 6);
            b.Range("AprsDestinationSsid", "APRS destination SSID", Aprs + 0x1C, 0, 15, v => v.ToString(CultureInfo.InvariantCulture));
            b.Text("AprsPath", "APRS path", Aprs + 0x24, 21);
            b.Text("AprsSymbolTable", "APRS symbol table", Aprs + 0x39, 1);
            b.Text("AprsSymbol", "APRS symbol", Aprs + 0x3A, 1);
            b.FreqBcd("AprsFrequency", "FM APRS frequency", Aprs + 0x01);
            b.Choice("AprsPower", "FM APRS power", Aprs + 0x3B, "Low", "Mid", "High", "Turbo");
            b.Range("AprsAutoInterval", "APRS beacon interval", Aprs + 0x0B, 0, 255, v => v * 30 + " s").Special(0, "Off");
            b.Range("AprsManualInterval", "APRS manual interval", Aprs + 0x0A, 0, 255, v => v + " s").Special(0, "Off");
            return b.Defs;
        }

        static string TimeZoneLabel(int v)
        {
            int halfHours = v - 24;
            if (halfHours == 0) return "GMT";
            int h = Math.Abs(halfHours) / 2;
            return "GMT" + (halfHours < 0 ? "-" : "+") + h + (Math.Abs(halfHours) % 2 == 1 ? ":30" : "");
        }

        sealed class Builder
        {
            public readonly List<SettingDef> Defs = new List<SettingDef>();
            string group = "";

            public void Group(string g) => group = g;

            Def Add(SettingDef d)
            {
                d.Group = group;
                if (Defs.Any(x => x.Key == d.Key)) throw new InvalidOperationException("Duplicate setting " + d.Key);
                Defs.Add(d);
                return new Def(d);
            }

            public Def Choice(string key, string label, uint addr, params string[] labels)
            {
                var d = new SettingDef { Key = key, Label = label, Kind = SettingKind.Choice, Address = addr };
                for (int i = 0; i < labels.Length; i++) d.Options.Add(new KeyValuePair<int, string>(i, labels[i]));
                return Add(d);
            }

            public Def Range(string key, string label, uint addr, int min, int max, Func<int, string> fmt) =>
                Bits(key, label, addr, 0, 8, min, max, fmt);

            public Def Bits(string key, string label, uint addr, int lsb, int bits, int min, int max, Func<int, string> fmt)
            {
                var d = new SettingDef { Key = key, Label = label, Kind = SettingKind.Choice, Address = addr, Lsb = lsb, Bits = bits };
                for (int v = min; v <= max; v++) d.Options.Add(new KeyValuePair<int, string>(v, fmt(v)));
                return Add(d);
            }

            public Def Flag(string key, string label, uint addr) =>
                Add(new SettingDef { Key = key, Label = label, Kind = SettingKind.Flag, Address = addr });

            public Def Text(string key, string label, uint addr, int length) =>
                Add(new SettingDef { Key = key, Label = label, Kind = SettingKind.Text, Address = addr, Length = length });

            public Def Freq(string key, string label, uint addr) =>
                Add(new SettingDef { Key = key, Label = label, Kind = SettingKind.FrequencyLe, Address = addr, Length = 4 });

            public Def FreqBcd(string key, string label, uint addr) =>
                Add(new SettingDef { Key = key, Label = label, Kind = SettingKind.FrequencyBcd, Address = addr, Length = 4 });
        }

        /// <summary>Fluent extras on a just-added setting.</summary>
        sealed class Def
        {
            readonly SettingDef d;
            public Def(SettingDef d) { this.d = d; }

            /// <summary>Gives stored value <paramref name="raw"/> its own label (adding it if it's outside the range).</summary>
            public Def Special(int raw, string label)
            {
                int i = d.Options.FindIndex(o => o.Key == raw);
                if (i >= 0) d.Options[i] = new KeyValuePair<int, string>(raw, label);
                else d.Options.Insert(0, new KeyValuePair<int, string>(raw, label));
                return this;
            }

            public Def Csv(string column, Func<int, int> toCsv = null)
            {
                d.CsvColumn = column;
                if (toCsv != null) d.ToCsv = toCsv;
                return this;
            }

            /// <summary>Checked against the CPS on the user's radio.</summary>
            public Def Ok()
            {
                d.Verified = true;
                return this;
            }
        }
    }
}
