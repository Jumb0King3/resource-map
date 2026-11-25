using System;
using System.Collections.Generic;
using System.Globalization;

namespace ResourceMap
{
    /// <summary>The three map layers.</summary>
    public static class ResourceKind
    {
        public const int Clay = 0;
        public const int Soil = 1;
        public const int Ore = 2;
        public const int Count = 3;
        public static readonly string[] Keys = { "clay", "soil", "ores" };

        public static int Of(string typeKey)
        {
            if (typeKey == null) return Soil;
            if (typeKey.StartsWith("clay-")) return Clay;
            if (typeKey.StartsWith("ore-")) return Ore;
            return Soil;
        }
    }

    /// <summary>Server settings: ModConfig/resourcemap-server.json</summary>
    public class ServerConfig
    {
        /// <summary>How often (ms) the server scans a few chunk columns.</summary>
        public int ScanIntervalMs = 250;
        /// <summary>Chunk columns (32 x 32 blocks) scanned each time. Lower it if the server lags.</summary>
        public int ColumnsPerScan = 2;
        /// <summary>How often (ms) changes are sent to players.</summary>
        public int SendIntervalMs = 2000;
        /// <summary>Chunk columns per update packet after a player has joined (joining players get everything at once).</summary>
        public int MaxChunksPerPacket = 150;
        /// <summary>How many blocks into the ground to look for clay, peat and soil (1 = only the top block).</summary>
        public int ScanDepth = 3;
        /// <summary>Every this many seconds, the ground around each player is checked again (picks up digging).</summary>
        public int RescanAroundPlayersSeconds = 60;
        /// <summary>Radius in chunk columns around players for that recheck.</summary>
        public int RescanRadiusChunks = 2;

        public bool ShowClay = true;
        public bool ShowPeat = true;
        public bool ShowSurfaceOres = true;
        /// <summary>Ore blocks you can see: on the ground surface or in a cliff face (not buried in rock).</summary>
        public bool ShowExposedOres = true;
        /// <summary>Soil fertilities to show (game codes: verylow, low, medium, compost, high).</summary>
        public string[] SoilFertilities = new string[] { "high", "compost" };
        /// <summary>Extra blocks to treat as clay, by code start, e.g. "mymod:clay-". Shown on the clay layer.</summary>
        public string[] ExtraClayCodes = new string[0];
        /// <summary>Blocks to never show, by code start, e.g. "game:looseores-quartz".</summary>
        public string[] IgnoreCodes = new string[0];
        /// <summary>Only players with server control rights see resources.</summary>
        public bool AdminOnly = false;

        public void Clamp()
        {
            ScanIntervalMs = Math.Max(50, Math.Min(10000, ScanIntervalMs));
            ColumnsPerScan = Math.Max(1, Math.Min(32, ColumnsPerScan));
            SendIntervalMs = Math.Max(500, Math.Min(60000, SendIntervalMs));
            MaxChunksPerPacket = Math.Max(10, Math.Min(2000, MaxChunksPerPacket));
            ScanDepth = Math.Max(1, Math.Min(8, ScanDepth));
            RescanAroundPlayersSeconds = Math.Max(10, Math.Min(3600, RescanAroundPlayersSeconds));
            RescanRadiusChunks = Math.Max(0, Math.Min(8, RescanRadiusChunks));
            if (SoilFertilities == null) SoilFertilities = new string[0];
            if (ExtraClayCodes == null) ExtraClayCodes = new string[0];
            if (IgnoreCodes == null) IgnoreCodes = new string[0];
        }
    }

    /// <summary>Each player's own settings: ModConfig/resourcemap-client.json</summary>
    public class ClientConfig
    {
        /// <summary>How solid the clay and soil areas are drawn, 0.1 to 1.</summary>
        public float Opacity = 0.85f;
        /// <summary>Size of the surface ore markers, in pixels.</summary>
        public int OreMarkerSize = 12;
        /// <summary>Colour overrides by type key, e.g. "clay-blue": "#3a7bd5".</summary>
        public Dictionary<string, string> Colors = new Dictionary<string, string>();
        /// <summary>Type keys not to draw, e.g. "soil-compost".</summary>
        public List<string> Hidden = new List<string>();

        public void Clamp()
        {
            Opacity = Math.Max(0.1f, Math.Min(1f, Opacity));
            OreMarkerSize = Math.Max(4, Math.Min(40, OreMarkerSize));
            if (Colors == null) Colors = new Dictionary<string, string>();
            if (Hidden == null) Hidden = new List<string>();
        }

        public string ColorFor(string typeKey)
        {
            string hex;
            if (typeKey != null && Colors.TryGetValue(typeKey, out hex) && ColorHex.IsValid(hex)) return hex;
            return DefaultColors.For(typeKey);
        }

        public bool IsHidden(string typeKey)
        {
            return typeKey != null && Hidden.Contains(typeKey);
        }
    }

    public static class DefaultColors
    {
        private static readonly Dictionary<string, string> Known = new Dictionary<string, string>
        {
            { "clay-blue", "#1e6bff" },
            { "clay-red", "#ff2d2d" },
            { "clay-fire", "#ff9f0a" },
            { "peat", "#a033ff" },
            { "soil-high", "#ffe600" },
            { "soil-compost", "#ff5ec8" },
            { "soil-medium", "#fff3a0" },
            { "ore-nativecopper", "#e8762c" },
            { "ore-quartz_nativegold", "#ffd23f" },
            { "ore-quartz_nativesilver", "#bcc6d1" },
            { "ore-galena_nativesilver", "#7c8fc9" },
            { "ore-anthracite", "#111111" },
            { "ore-bituminouscoal", "#3c3c3c" },
            { "ore-lignite", "#5c3d1e" },
            { "ore-quartz", "#ffffff" },
            { "ore-malachite", "#16a085" },
            { "ore-cassiterite", "#6b6f78" },
            { "ore-sulfur", "#c8e600" },
            { "ore-cinnabar", "#b0002a" },
            { "ore-hematite", "#7a1a1a" },
            { "ore-olivine", "#6b8e23" },
            { "ore-olivine_peridot", "#9acd32" },
            { "ore-emerald", "#00c26b" },
            { "ore-diamond", "#a8f0ff" },
            { "ore-lapislazuli", "#1f3a93" },
            { "ore-uranium", "#39ff14" },
            { "ore-magnetite", "#2b2b45" },
            { "ore-halite", "#f4a6c8" },
            { "ore-limonite", "#b5651d" },
            { "ore-flint", "#997d54" },
            { "ore-galena", "#730056" },
            { "ore-chromite", "#c499ff" },
            { "ore-ilmenite", "#ff00ea" },
            { "ore-sphalerite", "#8521a6" },
            { "ore-bismuthinite", "#99ffcc" },
            { "ore-pentlandite", "#ffbb99" },
            { "ore-wolframite", "#0095ff" },
            { "ore-rhodochrosite", "#ff668c" },
            { "ore-alum", "#0000a6" },
            { "ore-corundum", "#0000ff" },
            { "ore-borax", "#006ea6" },
            { "ore-sylvite", "#a66369" },
            { "ore-fluorite", "#66ff73" },
            { "ore-graphite", "#587345" },
            { "ore-kernite", "#a663a0" },
            { "ore-phosphorite", "#a68a00" },
        };

        /// <summary>Spare colours, kept well apart from the ones above, for resources added by other mods.</summary>
        public static readonly string[] Spare = { "#0ea600", "#00ffff", "#d966ff", "#00bfff", "#c3ff99", "#2190a6", "#007326", "#260073", "#736317", "#ff0080", "#0040ff", "#ff7366", "#ff0055", "#a60053", "#ffbf66", "#00ffbf" };

        public static bool IsKnown(string typeKey)
        {
            return typeKey != null && Known.ContainsKey(typeKey);
        }

        /// <summary>Spare colours handed out to the other mods' resources this world has, one each.</summary>
        private static readonly Dictionary<string, string> Assigned = new Dictionary<string, string>();

        /// <summary>Gives every resource type without a colour of its own a different spare colour (alphabetical, so it stays the same).</summary>
        public static void AssignSpares(string[] typeKeys)
        {
            Assigned.Clear();
            if (typeKeys == null) return;
            List<string> unknown = new List<string>();
            foreach (string key in typeKeys)
            {
                if (key != null && !Known.ContainsKey(key) && !unknown.Contains(key)) unknown.Add(key);
            }
            unknown.Sort(StringComparer.Ordinal);
            for (int i = 0; i < unknown.Count; i++)
            {
                Assigned[unknown[i]] = i < Spare.Length ? Spare[i] : FromHash(unknown[i]);
            }
        }

        public static string For(string typeKey)
        {
            string hex;
            if (typeKey != null && Known.TryGetValue(typeKey, out hex)) return hex;
            if (typeKey != null && Assigned.TryGetValue(typeKey, out hex)) return hex;
            return FromHash(typeKey ?? "");
        }

        /// <summary>A steady, fairly bright colour made from the name, for resources added by other mods.</summary>
        private static string FromHash(string key)
        {
            int h = 17;
            foreach (char ch in key) h = unchecked(h * 31 + ch);
            double hue = (Math.Abs(h % 360)) / 360.0;
            double r, g, b;
            HsvToRgb(hue, 0.65, 0.85, out r, out g, out b);
            return ColorHex.Format(r, g, b);
        }

        private static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
        {
            int i = (int)Math.Floor(h * 6);
            double f = h * 6 - i;
            double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            switch (((i % 6) + 6) % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
        }
    }

    public static class ColorHex
    {
        public static bool IsValid(string hex)
        {
            double r, g, b;
            return TryParse(hex, out r, out g, out b);
        }

        public static bool TryParse(string hex, out double r, out double g, out double b)
        {
            r = g = b = 1;
            if (string.IsNullOrEmpty(hex)) return false;
            string s = hex.Trim().TrimStart('#');
            if (s.Length != 6) return false;
            int v;
            if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;
            r = ((v >> 16) & 255) / 255.0;
            g = ((v >> 8) & 255) / 255.0;
            b = (v & 255) / 255.0;
            return true;
        }

        public static string Format(double r, double g, double b)
        {
            int ri = (int)Math.Round(Math.Max(0, Math.Min(1, r)) * 255);
            int gi = (int)Math.Round(Math.Max(0, Math.Min(1, g)) * 255);
            int bi = (int)Math.Round(Math.Max(0, Math.Min(1, b)) * 255);
            return "#" + ri.ToString("x2") + gi.ToString("x2") + bi.ToString("x2");
        }
    }
}
