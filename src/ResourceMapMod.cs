using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ResourceMap
{
    /// <summary>A chunk column's resources as the client keeps them, plus things worked out for drawing.</summary>
    public class ClientChunk
    {
        public int X;
        public int Z;
        public byte[] Cells;
        public byte[] Types;
        /// <summary>Whether the chunk has anything for each layer (clay, soil, ores).</summary>
        public bool[] HasKind = new bool[ResourceKind.Count];
        /// <summary>Drawn textures for the clay and soil layers, rebuilt when the data or colours change.</summary>
        public LoadedTexture[] Textures = new LoadedTexture[ResourceKind.Count];
        public int[] TextureVersion = new int[ResourceKind.Count];
        /// <summary>One marker per ore type in this chunk: the middle of its nuggets, and how many cells have one.</summary>
        public List<OreCluster> Ores = new List<OreCluster>();

        public void DisposeTextures()
        {
            for (int i = 0; i < Textures.Length; i++)
            {
                if (Textures[i] != null) Textures[i].Dispose();
                Textures[i] = null;
            }
        }
    }

    public class OreCluster
    {
        public int Type;
        public double X;
        public double Z;
        public int Count;
    }

    /// <summary>Server side: what the server knows about one chunk column.</summary>
    internal class ServerChunk
    {
        public int X;
        public int Z;
        public byte[] Cells = new byte[0];
        public byte[] Types = new byte[0];
        /// <summary>Change number, for sending only what's new to each player.</summary>
        public int Rev;
    }

    public class ResourceMapMod : ModSystem
    {
        public const string ChannelName = "resourcemap";
        public const string ServerConfigFile = "resourcemap-server.json";
        public const string ClientConfigFile = "resourcemap-client.json";
        private const string SaveKey = "resourcemap-data";
        public const int CellsPerSide = 16;   // 32 blocks / 2-block cells
        public const int CellSize = 2;

        public static long ChunkKey(int x, int z)
        {
            return ((long)z << 32) | (uint)x;
        }

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return true;
        }

        public override void Start(ICoreAPI api)
        {
            api.Network
                .RegisterChannel(ChannelName)
                .RegisterMessageType<ResourceUpdatePacket>();

            WorldMapManager mapManager = api.ModLoader.GetModSystem<WorldMapManager>();
            if (mapManager != null)
            {
                // Above the terrain, below waypoints and player markers.
                mapManager.RegisterMapLayer<SoilMapLayer>("resourcemap-soil", 0.50);
                mapManager.RegisterMapLayer<ClayMapLayer>("resourcemap-clay", 0.51);
                mapManager.RegisterMapLayer<OreMapLayer>("resourcemap-ores", 0.52);
            }
        }

        // =====================================================================
        // Server
        // =====================================================================

        private ICoreServerAPI sapi;
        private IServerNetworkChannel serverChannel;
        private ServerConfig serverConfig;

        private readonly List<string> typeKeys = new List<string>();
        private readonly List<string> typeNames = new List<string>();
        private readonly Dictionary<string, int> typeIndex = new Dictionary<string, int>();
        /// <summary>Block id to resource type index + 1 (0 = not a resource). Missing = not looked at yet.</summary>
        private readonly Dictionary<int, int> blockTypes = new Dictionary<int, int>();

        private readonly Dictionary<long, ServerChunk> serverChunks = new Dictionary<long, ServerChunk>();
        /// <summary>Chunk columns waiting to be scanned, and the earliest time (ms) to do it.</summary>
        private readonly Dictionary<long, long> pending = new Dictionary<long, long>();
        private readonly Dictionary<string, int> lastSentRev = new Dictionary<string, int>();
        private int revision = 1;
        private long scanListenerId, sendListenerId, rescanListenerId;
        private readonly BlockPos scanPos = new BlockPos(0, 0, 0);
        private bool loggedScanError;
        private int scannedColumns;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            serverConfig = LoadServerConfig();
            serverChannel = api.Network.GetChannel(ChannelName);

            api.Event.SaveGameLoaded += LoadSavedData;
            api.Event.GameWorldSave += SaveData;
            api.Event.ChunkColumnLoaded += (coord, chunks) => QueueScan(coord.X, coord.Y, 3000);
            api.Event.PlayerJoin += (player) =>
            {
                if (player != null) lastSentRev.Remove(player.PlayerUID);
                QueueAroundPlayer(player, 0, true);
            };
            api.Event.PlayerDisconnect += (player) =>
            {
                if (player != null) lastSentRev.Remove(player.PlayerUID);
            };

            api.ChatCommands
                .Create("resourcemap")
                .WithDescription(Lang.Get("resourcemap:cmd-server-desc"))
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("rescan")
                    .WithDescription(Lang.Get("resourcemap:cmd-rescan-desc"))
                    .HandleWith(OnRescanCommand)
                .EndSubCommand()
                .BeginSubCommand("reload")
                    .WithDescription(Lang.Get("resourcemap:cmd-reload-desc"))
                    .HandleWith(OnReloadCommand)
                .EndSubCommand()
                .BeginSubCommand("stats")
                    .WithDescription(Lang.Get("resourcemap:cmd-stats-desc"))
                    .HandleWith(OnStatsCommand)
                .EndSubCommand()
                .BeginSubCommand("settings")
                    .WithDescription(Lang.Get("resourcemap:cmd-settings-desc"))
                    .HandleWith(OnSettingsCommand)
                .EndSubCommand()
                .BeginSubCommand("set")
                    .WithDescription(Lang.Get("resourcemap:cmd-set-desc"))
                    .WithArgs(api.ChatCommands.Parsers.All("setting and value"))
                    .HandleWith(OnSetCommand)
                .EndSubCommand()
                .BeginSubCommand("speed")
                    .WithDescription(Lang.Get("resourcemap:cmd-speed-desc"))
                    .WithArgs(api.ChatCommands.Parsers.All("slow, normal, fast or max"))
                    .HandleWith(OnSpeedCommand)
                .EndSubCommand();

            StartTimers();
            api.Logger.Notification("[resourcemap] Server side started: clay {0}, peat {1}, soils [{2}], surface ores {3}, scan depth {4}",
                serverConfig.ShowClay, serverConfig.ShowPeat, string.Join(", ", serverConfig.SoilFertilities), serverConfig.ShowSurfaceOres, serverConfig.ScanDepth);
        }

        private ServerConfig LoadServerConfig()
        {
            ServerConfig config = null;
            try
            {
                config = sapi.LoadModConfig<ServerConfig>(ServerConfigFile);
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[resourcemap] Could not read {0}, using defaults: {1}", ServerConfigFile, e.Message);
            }
            if (config == null) config = new ServerConfig();
            config.Clamp();
            sapi.StoreModConfig(config, ServerConfigFile);
            return config;
        }

        private void StartTimers()
        {
            if (scanListenerId != 0) sapi.Event.UnregisterGameTickListener(scanListenerId);
            if (sendListenerId != 0) sapi.Event.UnregisterGameTickListener(sendListenerId);
            if (rescanListenerId != 0) sapi.Event.UnregisterGameTickListener(rescanListenerId);
            scanListenerId = sapi.Event.RegisterGameTickListener(OnScanTick, serverConfig.ScanIntervalMs);
            sendListenerId = sapi.Event.RegisterGameTickListener(OnSendTick, serverConfig.SendIntervalMs);
            rescanListenerId = sapi.Event.RegisterGameTickListener(OnRescanTick, serverConfig.RescanAroundPlayersSeconds * 1000);
        }

        private bool MayView(IServerPlayer player)
        {
            return !serverConfig.AdminOnly || player.HasPrivilege(Privilege.controlserver);
        }

        // ---------------------------------------------------------------------
        // Saving and loading
        // ---------------------------------------------------------------------

        private void LoadSavedData()
        {
            try
            {
                byte[] data = sapi.WorldManager.SaveGame.GetData(SaveKey);
                if (data == null || data.Length == 0) return;
                ResourceSaveData saved = SerializerUtil.Deserialize<ResourceSaveData>(data);
                if (saved == null || saved.TypeKeys == null) return;

                typeKeys.Clear(); typeNames.Clear(); typeIndex.Clear();
                for (int i = 0; i < saved.TypeKeys.Length; i++)
                {
                    typeKeys.Add(saved.TypeKeys[i]);
                    string savedName = saved.TypeNames != null && i < saved.TypeNames.Length ? saved.TypeNames[i] : saved.TypeKeys[i];
                    int inSoil = savedName.IndexOf(" in soil", StringComparison.OrdinalIgnoreCase);
                    if (inSoil > 0) savedName = savedName.Substring(0, inSoil);
                    if (saved.TypeKeys[i].StartsWith("ore-"))
                    {
                        string langKey = "game:" + saved.TypeKeys[i];
                        string translated = Lang.Get(langKey);
                        if (!string.IsNullOrEmpty(translated) && translated != langKey) savedName = translated;
                    }
                    typeNames.Add(savedName);
                    typeIndex[saved.TypeKeys[i]] = i;
                }

                serverChunks.Clear();
                if (saved.Chunks != null)
                {
                    foreach (ChunkResources c in saved.Chunks)
                    {
                        if (c == null || c.Cells == null || c.Types == null || c.Cells.Length == 0) continue;
                        serverChunks[ChunkKey(c.X, c.Z)] = new ServerChunk { X = c.X, Z = c.Z, Cells = c.Cells, Types = c.Types, Rev = revision };
                    }
                }
                sapi.Logger.Notification("[resourcemap] Loaded {0} chunk columns with resources ({1} resource types) from the save", serverChunks.Count, typeKeys.Count);
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[resourcemap] Could not load saved resource data, starting fresh: {0}", e.Message);
            }
        }

        private void SaveData()
        {
            try
            {
                List<ChunkResources> list = new List<ChunkResources>();
                foreach (ServerChunk c in serverChunks.Values)
                {
                    if (c.Cells.Length == 0) continue;
                    list.Add(new ChunkResources { X = c.X, Z = c.Z, Cells = c.Cells, Types = c.Types });
                }
                ResourceSaveData data = new ResourceSaveData
                {
                    TypeKeys = typeKeys.ToArray(),
                    TypeNames = typeNames.ToArray(),
                    Chunks = list.ToArray()
                };
                sapi.WorldManager.SaveGame.StoreData(SaveKey, SerializerUtil.Serialize(data));
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[resourcemap] Could not save resource data: {0}", e.Message);
            }
        }

        // ---------------------------------------------------------------------
        // Scanning
        // ---------------------------------------------------------------------

        private void QueueScan(int chunkX, int chunkZ, long delayMs)
        {
            long key = ChunkKey(chunkX, chunkZ);
            long due = sapi.World.ElapsedMilliseconds + delayMs;
            long existing;
            if (pending.TryGetValue(key, out existing) && existing <= due) return;
            pending[key] = due;
        }

        /// <summary>Queues the chunk columns around a player. onlyUnknown: skip ones already scanned.</summary>
        private void QueueAroundPlayer(IPlayer player, int radius, bool onlyUnknown)
        {
            if (player == null || player.Entity == null) return;
            int size = GlobalConstants.ChunkSize;
            int cx = (int)Math.Floor(player.Entity.Pos.X / size);
            int cz = (int)Math.Floor(player.Entity.Pos.Z / size);
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (onlyUnknown && serverChunks.ContainsKey(ChunkKey(cx + dx, cz + dz))) continue;
                    QueueScan(cx + dx, cz + dz, 1000);
                }
            }
        }

        private void OnRescanTick(float dt)
        {
            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                QueueAroundPlayer(player, serverConfig.RescanRadiusChunks, false);
            }
        }

        private void OnScanTick(float dt)
        {
            if (pending.Count == 0) return;
            long now = sapi.World.ElapsedMilliseconds;

            List<long> due = new List<long>();
            foreach (KeyValuePair<long, long> entry in pending)
            {
                if (entry.Value <= now) due.Add(entry.Key);
                if (due.Count >= serverConfig.ColumnsPerScan) break;
            }

            foreach (long key in due)
            {
                pending.Remove(key);
                int x = (int)(uint)(key & 0xFFFFFFFFL);
                int z = (int)(key >> 32);
                try
                {
                    ScanColumn(x, z);
                }
                catch (Exception e)
                {
                    if (!loggedScanError)
                    {
                        loggedScanError = true;
                        sapi.Logger.Warning("[resourcemap] Scanning chunk column {0},{1} failed (further errors not logged): {2}", x, z, e);
                    }
                }
            }
        }

        /// <summary>Looks at the ground of one chunk column and records which resources are where.</summary>
        private void ScanColumn(int chunkX, int chunkZ)
        {
            IMapChunk mapChunk = sapi.WorldManager.GetMapChunk(chunkX, chunkZ);
            if (mapChunk == null) return;      // not loaded (any more)
            ushort[] heights = mapChunk.RainHeightMap;
            if (heights == null) return;

            int size = GlobalConstants.ChunkSize;
            int cellCount = CellsPerSide * CellsPerSide;
            int[] area = new int[cellCount];   // clay / soil type index + 1
            int[] ore = new int[cellCount];    // ore type index + 1
            IBlockAccessor ba = sapi.World.BlockAccessor;
            int maxY = ba.MapSizeY - 1;

            for (int lz = 0; lz < size; lz++)
            {
                for (int lx = 0; lx < size; lx++)
                {
                    int cell = (lz / CellSize) * CellsPerSide + (lx / CellSize);
                    int wx = chunkX * size + lx;
                    int wz = chunkZ * size + lz;
                    int top = Math.Min(maxY, (int)heights[lz * size + lx]);
                    if (top <= 0) continue;

                    // Walk down from the highest block past trees, plants, snow and water to the ground.
                    // Ore nuggets lying on the way are noted; then the ground and a few blocks below it are checked.
                    for (int y = top; y > 0 && y > top - 48; y--)
                    {
                        scanPos.Set(wx, y, wz);
                        Block block = ba.GetBlock(scanPos);
                        if (block == null || block.Id == 0) continue;

                        int t = ClassifyBlock(block);
                        if (t > 0 && ResourceKind.Of(typeKeys[t - 1]) == ResourceKind.Ore)
                        {
                            ore[cell] = t;
                            if (block.Code.Path.StartsWith("looseores")) continue;   // nugget lying on the ground
                            ScanCliffFace(ba, wx, wz, y - 1, LowestNeighbour(wx, wz, chunkX, chunkZ, heights), cell, ore, area);
                            break;                                                     // ore block is the ground here
                        }
                        if (!IsGround(block)) continue;

                        ScanCliffFace(ba, wx, wz, y - 1, LowestNeighbour(wx, wz, chunkX, chunkZ, heights), cell, ore, area);

                        for (int d = 0; d < serverConfig.ScanDepth && y - d > 0; d++)
                        {
                            Block below = d == 0 ? block : GetBlockAt(ba, wx, y - d, wz);
                            int bt = below == null ? 0 : ClassifyBlock(below);
                            if (bt > 0 && ResourceKind.Of(typeKeys[bt - 1]) != ResourceKind.Ore)
                            {
                                // Clay wins over soil if a cell has both.
                                if (area[cell] == 0 || ResourceKind.Of(typeKeys[bt - 1]) == ResourceKind.Clay) area[cell] = bt;
                                break;
                            }
                        }
                        break;
                    }
                }
            }

            List<byte> cells = new List<byte>();
            List<byte> types = new List<byte>();
            for (int i = 0; i < cellCount; i++)
            {
                if (area[i] > 0) { cells.Add((byte)i); types.Add((byte)(area[i] - 1)); }
                if (ore[i] > 0) { cells.Add((byte)i); types.Add((byte)(ore[i] - 1)); }
            }

            scannedColumns++;
            long key = ChunkKey(chunkX, chunkZ);
            ServerChunk existing;
            bool had = serverChunks.TryGetValue(key, out existing);
            byte[] newCells = cells.ToArray();
            byte[] newTypes = types.ToArray();

            if (had && SameBytes(existing.Cells, newCells) && SameBytes(existing.Types, newTypes)) return;
            if (!had && newCells.Length == 0)
            {
                // Remember that it was scanned (so it isn't queued again for players), without sending anything.
                serverChunks[key] = new ServerChunk { X = chunkX, Z = chunkZ, Rev = 0 };
                return;
            }

            revision++;
            serverChunks[key] = new ServerChunk { X = chunkX, Z = chunkZ, Cells = newCells, Types = newTypes, Rev = revision };
        }

        /// <summary>
        /// Where a column stands higher than a neighbour, its side is open to the air from its ground down to the
        /// neighbour's height: a cliff face. Ore blocks there can be seen, so they're shown.
        /// </summary>
        private void ScanCliffFace(IBlockAccessor ba, int wx, int wz, int fromY, int lowestNeighbour, int cell, int[] ore, int[] area)
        {
            if (lowestNeighbour <= 0) return;
            int bottom = Math.Max(1, Math.Max(lowestNeighbour + 1, fromY - 64));
            bool foundOre = !serverConfig.ShowExposedOres || ore[cell] > 0;
            bool foundArea = area[cell] > 0 && ResourceKind.Of(typeKeys[area[cell] - 1]) == ResourceKind.Clay;
            for (int y = fromY; y >= bottom && !(foundOre && foundArea); y--)
            {
                Block block = GetBlockAt(ba, wx, y, wz);
                if (block == null || block.Id == 0) continue;
                int t = ClassifyBlock(block);
                if (t <= 0) continue;
                int kind = ResourceKind.Of(typeKeys[t - 1]);
                if (kind == ResourceKind.Ore)
                {
                    if (!foundOre && block.Code != null && !block.Code.Path.StartsWith("looseores")) { ore[cell] = t; foundOre = true; }
                }
                else if (!foundArea)
                {
                    // Clay, peat or soil showing in the side of a slope, bank or cliff. Clay wins over soil.
                    if (area[cell] == 0 || kind == ResourceKind.Clay) area[cell] = t;
                    if (kind == ResourceKind.Clay) foundArea = true;
                }
            }
        }

        /// <summary>The lowest ground height of the four columns next to this one (neighbouring chunks included).</summary>
        private int LowestNeighbour(int wx, int wz, int chunkX, int chunkZ, ushort[] ownHeights)
        {
            int lowest = int.MaxValue;
            lowest = Math.Min(lowest, HeightAt(wx + 1, wz, chunkX, chunkZ, ownHeights));
            lowest = Math.Min(lowest, HeightAt(wx - 1, wz, chunkX, chunkZ, ownHeights));
            lowest = Math.Min(lowest, HeightAt(wx, wz + 1, chunkX, chunkZ, ownHeights));
            lowest = Math.Min(lowest, HeightAt(wx, wz - 1, chunkX, chunkZ, ownHeights));
            return lowest == int.MaxValue ? 0 : lowest;
        }

        private int HeightAt(int wx, int wz, int chunkX, int chunkZ, ushort[] ownHeights)
        {
            int size = GlobalConstants.ChunkSize;
            int cx = (int)Math.Floor(wx / (double)size), cz = (int)Math.Floor(wz / (double)size);
            ushort[] heights = ownHeights;
            if (cx != chunkX || cz != chunkZ)
            {
                IMapChunk neighbour = sapi.WorldManager.GetMapChunk(cx, cz);
                if (neighbour == null || neighbour.RainHeightMap == null) return int.MaxValue;
                heights = neighbour.RainHeightMap;
            }
            int lx = wx - cx * size, lz = wz - cz * size;
            return heights[lz * size + lx];
        }

        private Block GetBlockAt(IBlockAccessor ba, int x, int y, int z)
        {
            scanPos.Set(x, y, z);
            return ba.GetBlock(scanPos);
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>Natural ground: soil, sand, gravel, rock and the like. Air, plants, leaves, logs, snow, water and ice are passed through.</summary>
        private static readonly string[] OreGrades = { "poor", "medium", "rich", "bountiful", "low", "high" };

        private static readonly string[] PassThroughMaterials =
        {
            "Air", "Plant", "Leaves", "Wood", "Snow", "Liquid", "Ice", "Other"
        };

        /// <summary>Natural ground: soil, sand, gravel, rock and the like. Air, plants, leaves, logs, snow, water and ice are passed through.</summary>
        private static bool IsGround(Block block)
        {
            // Compared by name, so no enum member has to exist in this game version for the mod to compile.
            string material = block.BlockMaterial.ToString();
            foreach (string pass in PassThroughMaterials) if (pass == material) return false;
            return true;
        }

        /// <summary>Resource type index + 1 for a block, or 0 if it isn't one. Cached per block id.</summary>
        private int ClassifyBlock(Block block)
        {
            int cached;
            if (blockTypes.TryGetValue(block.Id, out cached)) return cached;

            int result = 0;
            string key = TypeKeyFor(block);
            if (key != null) result = RegisterType(key, block) + 1;
            blockTypes[block.Id] = result;
            return result;
        }

        private string TypeKeyFor(Block block)
        {
            if (block.Code == null) return null;
            string full = block.Code.ToString();
            foreach (string ignore in serverConfig.IgnoreCodes)
            {
                if (!string.IsNullOrEmpty(ignore) && full.StartsWith(ignore, StringComparison.OrdinalIgnoreCase)) return null;
            }

            string path = block.Code.Path;
            string[] parts = path.Split('-');

            if (serverConfig.ShowSurfaceOres && path.StartsWith("looseores-") && parts.Length > 1) return "ore-" + parts[1];
            if (serverConfig.ShowExposedOres && parts[0] == "ore" && parts.Length >= 3)
            {
                // ore-{type}-{rock}, ore-{grade}-{type}-{rock} or ore-{potential}-{gem}-{rock}
                bool graded = parts.Length >= 4 && Array.IndexOf(OreGrades, parts[1]) >= 0;
                return "ore-" + (graded ? parts[2] : parts[1]);
            }

            if (serverConfig.ShowClay)
            {
                if (path.StartsWith("rawclay-") && parts.Length > 1) return "clay-" + parts[1];
                foreach (string extra in serverConfig.ExtraClayCodes)
                {
                    if (string.IsNullOrEmpty(extra) || !full.StartsWith(extra, StringComparison.OrdinalIgnoreCase)) continue;
                    string rest = full.Substring(extra.Length).Split('-')[0];
                    return "clay-" + (string.IsNullOrEmpty(rest) ? block.Code.Domain : rest);
                }
            }

            if (serverConfig.ShowPeat && parts[0] == "peat") return "peat";

            if (parts[0] == "soil" && parts.Length > 1 && block.Code.Domain == "game")
            {
                foreach (string fertility in serverConfig.SoilFertilities)
                {
                    if (string.Equals(parts[1], fertility, StringComparison.OrdinalIgnoreCase)) return "soil-" + parts[1];
                }
            }
            return null;
        }

        private int RegisterType(string key, Block example)
        {
            int index;
            if (typeIndex.TryGetValue(key, out index)) return index;
            if (typeKeys.Count >= 255) return 0;   // a byte holds the type; 255 kinds is plenty

            index = typeKeys.Count;
            typeKeys.Add(key);
            typeNames.Add(NameFor(key, example));
            typeIndex[key] = index;
            revision++;
            return index;
        }

        /// <summary>The block's own name, without the "(Grassy)" style part, e.g. "Blue clay".</summary>
        private string NameFor(string key, Block block)
        {
            string name = null;
            if (key.StartsWith("ore-"))
            {
                string langKey = "game:" + key;
                string translated = Lang.Get(langKey);
                if (!string.IsNullOrEmpty(translated) && translated != langKey && translated != key) return translated;
            }
            try
            {
                // Made by reflection: some ItemStack constructors are awkward for the mod compiler.
                object stack = Activator.CreateInstance(typeof(ItemStack), new object[] { block, 1 });
                System.Reflection.MethodInfo getName = typeof(ItemStack).GetMethod("GetName", Type.EmptyTypes);
                name = getName != null ? getName.Invoke(stack, null) as string : null;
            }
            catch (Exception) { }

            if (!string.IsNullOrEmpty(name))
            {
                int paren = name.IndexOf(" (");
                if (paren > 0) name = name.Substring(0, paren);
                int inSoil = name.IndexOf(" in soil", StringComparison.OrdinalIgnoreCase);
                if (inSoil > 0) name = name.Substring(0, inSoil);
                name = name.Trim();
            }
            if (string.IsNullOrEmpty(name)) name = key;
            return name;
        }

        // ---------------------------------------------------------------------
        // Sending
        // ---------------------------------------------------------------------

        private void OnSendTick(float dt)
        {
            foreach (IPlayer p in sapi.World.AllOnlinePlayers)
            {
                IServerPlayer player = p as IServerPlayer;
                if (player == null || player.Entity == null) continue;
                SendUpdates(player);
            }
        }

        private void SendUpdates(IServerPlayer player)
        {
            int last;
            bool first = !lastSentRev.TryGetValue(player.PlayerUID, out last);
            if (first) last = 0;

            if (!MayView(player))
            {
                if (first)
                {
                    serverChannel.SendPacket(new ResourceUpdatePacket { Full = true, Allowed = false }, player);
                    lastSentRev[player.PlayerUID] = int.MaxValue;
                }
                return;
            }

            List<ServerChunk> changed = new List<ServerChunk>();
            foreach (ServerChunk c in serverChunks.Values)
            {
                if (c.Rev > last && (c.Rev > 0)) changed.Add(c);
            }
            if (!first && changed.Count == 0) return;

            changed.Sort((a, b) => a.Rev.CompareTo(b.Rev));
            // Joining players get everything found so far at once; later top-ups are kept to MaxChunksPerPacket.
            int take = first ? changed.Count : Math.Min(changed.Count, serverConfig.MaxChunksPerPacket);
            ChunkResources[] chunks = new ChunkResources[take];
            for (int i = 0; i < take; i++)
            {
                ServerChunk c = changed[i];
                chunks[i] = new ChunkResources { X = c.X, Z = c.Z, Cells = c.Cells, Types = c.Types };
            }

            serverChannel.SendPacket(new ResourceUpdatePacket
            {
                Full = first,
                Allowed = true,
                TypeKeys = typeKeys.ToArray(),
                TypeNames = typeNames.ToArray(),
                Chunks = chunks
            }, player);

            lastSentRev[player.PlayerUID] = take > 0 ? changed[take - 1].Rev : Math.Max(last, 0);
        }

        // ---------------------------------------------------------------------
        // Commands
        // ---------------------------------------------------------------------

        private TextCommandResult OnRescanCommand(TextCommandCallingArgs args)
        {
            int queued = 0;
            List<long> keys = new List<long>(serverChunks.Keys);
            foreach (long key in keys)
            {
                int x = (int)(uint)(key & 0xFFFFFFFFL);
                int z = (int)(key >> 32);
                if (sapi.WorldManager.GetMapChunk(x, z) == null) continue;
                QueueScan(x, z, 0);
                queued++;
            }
            foreach (IPlayer player in sapi.World.AllOnlinePlayers) QueueAroundPlayer(player, 8, false);
            return TextCommandResult.Success(Lang.Get("resourcemap:rescan-started", pending.Count));
        }

        private TextCommandResult OnReloadCommand(TextCommandCallingArgs args)
        {
            serverConfig = LoadServerConfig();
            ApplyServerConfigChange();
            return TextCommandResult.Success(Lang.Get("resourcemap:reloaded"));
        }

        /// <summary>The settings a /resourcemap set command can change: every public field of ServerConfig.</summary>
        private static System.Reflection.FieldInfo[] SettingFields()
        {
            return typeof(ServerConfig).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        }

        private static string ValueText(object value)
        {
            if (value == null) return "";
            string[] list = value as string[];
            if (list != null) return list.Length == 0 ? "none" : string.Join(", ", list);
            if (value is bool) return (bool)value ? "true" : "false";
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private TextCommandResult OnSettingsCommand(TextCommandCallingArgs args)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Lang.Get("resourcemap:settings-title"));
            foreach (System.Reflection.FieldInfo field in SettingFields())
            {
                sb.AppendLine("<strong>" + field.Name + "</strong> = " + ValueText(field.GetValue(serverConfig)) + "  – " + Lang.Get("resourcemap:setting-" + field.Name));
            }
            sb.Append(Lang.Get("resourcemap:settings-help"));
            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult OnSetCommand(TextCommandCallingArgs args)
        {
            string text = (args[0] as string ?? "").Trim();
            int space = text.IndexOf(' ');
            if (space <= 0) return TextCommandResult.Error(Lang.Get("resourcemap:set-usage"));
            string name = text.Substring(0, space).Trim();
            string value = text.Substring(space + 1).Trim();

            System.Reflection.FieldInfo field = null;
            foreach (System.Reflection.FieldInfo f in SettingFields())
            {
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) { field = f; break; }
            }
            if (field == null) return TextCommandResult.Error(Lang.Get("resourcemap:unknown-setting", name));

            object parsed;
            if (field.FieldType == typeof(int))
            {
                int number;
                if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number))
                    return TextCommandResult.Error(Lang.Get("resourcemap:need-number", field.Name));
                parsed = number;
            }
            else if (field.FieldType == typeof(bool))
            {
                string v = value.ToLowerInvariant();
                if (v == "true" || v == "on" || v == "yes" || v == "1") parsed = true;
                else if (v == "false" || v == "off" || v == "no" || v == "0") parsed = false;
                else return TextCommandResult.Error(Lang.Get("resourcemap:need-bool", field.Name));
            }
            else if (field.FieldType == typeof(string[]))
            {
                List<string> items = new List<string>();
                if (!string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string part in value.Split(','))
                    {
                        string item = part.Trim();
                        if (item.Length > 0) items.Add(item);
                    }
                }
                parsed = items.ToArray();
            }
            else
            {
                return TextCommandResult.Error(Lang.Get("resourcemap:unknown-setting", name));
            }

            field.SetValue(serverConfig, parsed);
            ApplyServerConfigChange();
            string result = Lang.Get("resourcemap:setting-changed", field.Name, ValueText(field.GetValue(serverConfig)));
            if (AffectsWhatIsFound(field.Name)) result += " " + Lang.Get("resourcemap:rescan-hint");
            return TextCommandResult.Success(result);
        }

        private static bool AffectsWhatIsFound(string name)
        {
            return name.StartsWith("Show") || name == "ScanDepth" || name == "SoilFertilities" || name == "ExtraClayCodes" || name == "IgnoreCodes";
        }

        private TextCommandResult OnSpeedCommand(TextCommandCallingArgs args)
        {
            string preset = (args[0] as string ?? "").Trim().ToLowerInvariant();
            switch (preset)
            {
                case "slow": serverConfig.ScanIntervalMs = 500; serverConfig.ColumnsPerScan = 1; break;
                case "normal": serverConfig.ScanIntervalMs = 250; serverConfig.ColumnsPerScan = 2; break;
                case "fast": serverConfig.ScanIntervalMs = 100; serverConfig.ColumnsPerScan = 8; break;
                case "max": serverConfig.ScanIntervalMs = 50; serverConfig.ColumnsPerScan = 16; break;
                default: return TextCommandResult.Error(Lang.Get("resourcemap:speed-usage"));
            }
            ApplyServerConfigChange();
            int perSecond = (int)Math.Round(serverConfig.ColumnsPerScan * 1000.0 / serverConfig.ScanIntervalMs);
            return TextCommandResult.Success(Lang.Get("resourcemap:speed-set", preset, serverConfig.ScanIntervalMs, serverConfig.ColumnsPerScan, perSecond));
        }

        /// <summary>Checks, saves and applies new server settings, and tells players if who may see the map changed.</summary>
        private void ApplyServerConfigChange()
        {
            serverConfig.Clamp();
            sapi.StoreModConfig(serverConfig, ServerConfigFile);
            blockTypes.Clear();
            lastSentRev.Clear();     // resend to everyone (also applies AdminOnly changes)
            StartTimers();
        }

        private TextCommandResult OnStatsCommand(TextCommandCallingArgs args)
        {
            int withResources = 0;
            int[] perType = new int[typeKeys.Count];
            foreach (ServerChunk c in serverChunks.Values)
            {
                if (c.Cells.Length > 0) withResources++;
                foreach (byte t in c.Types) if (t < perType.Length) perType[t]++;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Lang.Get("resourcemap:stats", serverChunks.Count, withResources, pending.Count, scannedColumns));
            for (int i = 0; i < typeKeys.Count; i++)
            {
                sb.AppendLine("  " + typeNames[i] + " (" + typeKeys[i] + "): " + perType[i]);
            }
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        // =====================================================================
        // Client
        // =====================================================================

        private ICoreClientAPI capi;
        private IClientNetworkChannel clientChannel;

        public ClientConfig ClientConfig { get; private set; }
        public readonly Dictionary<long, ClientChunk> Chunks = new Dictionary<long, ClientChunk>();
        public string[] TypeKeys = new string[0];
        public string[] TypeNames = new string[0];
        public bool Allowed = true;
        /// <summary>Goes up when colours, opacity or hidden types change, so textures are rebuilt.</summary>
        public int VisualVersion = 1;

        /// <summary>Search text from .resourcemap find (empty = show everything).</summary>
        public string Filter = "";
        public bool FilterActive => !string.IsNullOrEmpty(Filter);

        /// <summary>Whether a resource type matches the search (always true with no search).</summary>
        public bool MatchesFilter(int type)
        {
            if (!FilterActive) return true;
            return TypeKeyOf(type).IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0
                || TypeNameOf(type).IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>World coordinates as the game shows them (relative to the world spawn).</summary>
        public string ShownCoords(double x, double z)
        {
            double sx = 0, sz = 0;
            if (capi != null && capi.World.DefaultSpawnPosition != null)
            {
                sx = capi.World.DefaultSpawnPosition.X;
                sz = capi.World.DefaultSpawnPosition.Z;
            }
            return (int)Math.Floor(x - sx) + ", " + (int)Math.Floor(z - sz);
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            ClientConfig = LoadClientConfig();
            clientChannel = api.Network.GetChannel(ChannelName);
            clientChannel.SetMessageHandler<ResourceUpdatePacket>(OnUpdateReceived);

            api.ChatCommands
                .Create("resourcemap")
                .WithDescription(Lang.Get("resourcemap:cmd-client-desc"))
                .HandleWith(OnLegendCommand)
                .BeginSubCommand("opacity")
                    .WithDescription(Lang.Get("resourcemap:cmd-opacity-desc"))
                    .WithArgs(api.ChatCommands.Parsers.Int("percent"))
                    .HandleWith(OnOpacityCommand)
                .EndSubCommand()
                .BeginSubCommand("markersize")
                    .WithDescription(Lang.Get("resourcemap:cmd-markersize-desc"))
                    .WithArgs(api.ChatCommands.Parsers.Int("pixels"))
                    .HandleWith(OnMarkerSizeCommand)
                .EndSubCommand()
                .BeginSubCommand("hide")
                    .WithDescription(Lang.Get("resourcemap:cmd-hide-desc"))
                    .WithArgs(api.ChatCommands.Parsers.All("resource"))
                    .HandleWith(args => OnHideCommand(args, true))
                .EndSubCommand()
                .BeginSubCommand("show")
                    .WithDescription(Lang.Get("resourcemap:cmd-show-desc"))
                    .WithArgs(api.ChatCommands.Parsers.All("resource"))
                    .HandleWith(args => OnHideCommand(args, false))
                .EndSubCommand()
                .BeginSubCommand("find")
                    .WithDescription(Lang.Get("resourcemap:cmd-find-desc"))
                    .WithArgs(api.ChatCommands.Parsers.All("resource"))
                    .HandleWith(OnFindCommand)
                .EndSubCommand()
                .BeginSubCommand("clear")
                    .WithDescription(Lang.Get("resourcemap:cmd-clear-desc"))
                    .HandleWith(args => ClearFind())
                .EndSubCommand()
                .BeginSubCommand("settings")
                    .WithDescription(Lang.Get("resourcemap:cmd-client-settings-desc"))
                    .HandleWith(OnClientSettingsCommand)
                .EndSubCommand()
                .BeginSubCommand("reset")
                    .WithDescription(Lang.Get("resourcemap:cmd-reset-desc"))
                    .HandleWith(OnResetCommand)
                .EndSubCommand()
                .BeginSubCommand("color")
                    .WithDescription(Lang.Get("resourcemap:cmd-color-desc"))
                    .WithArgs(api.ChatCommands.Parsers.All("resource and colour"))
                    .HandleWith(OnColorCommand)
                .EndSubCommand();
        }

        private ClientConfig LoadClientConfig()
        {
            ClientConfig config = null;
            try
            {
                config = capi.LoadModConfig<ClientConfig>(ClientConfigFile);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[resourcemap] Could not read {0}, using defaults: {1}", ClientConfigFile, e.Message);
            }
            if (config == null) config = new ClientConfig();
            config.Clamp();
            capi.StoreModConfig(config, ClientConfigFile);
            return config;
        }

        private void SaveClientConfig()
        {
            ClientConfig.Clamp();
            capi.StoreModConfig(ClientConfig, ClientConfigFile);
            VisualVersion++;
        }

        private void OnUpdateReceived(ResourceUpdatePacket packet)
        {
            if (packet == null) return;
            if (packet.Full)
            {
                foreach (ClientChunk c in Chunks.Values) c.DisposeTextures();
                Chunks.Clear();
            }
            Allowed = packet.Allowed;
            if (packet.TypeKeys != null)
            {
                if (TypeKeys.Length != packet.TypeKeys.Length) VisualVersion++;
                TypeKeys = packet.TypeKeys;
                DefaultColors.AssignSpares(TypeKeys);
                TypeNames = packet.TypeNames ?? packet.TypeKeys;
            }
            if (packet.Chunks == null) return;

            foreach (ChunkResources data in packet.Chunks)
            {
                if (data == null) continue;
                long key = ChunkKey(data.X, data.Z);
                ClientChunk existing;
                if (Chunks.TryGetValue(key, out existing))
                {
                    existing.DisposeTextures();
                    Chunks.Remove(key);
                }
                if (data.Cells == null || data.Types == null || data.Cells.Length == 0) continue;
                Chunks[key] = BuildClientChunk(data);
            }
        }

        private ClientChunk BuildClientChunk(ChunkResources data)
        {
            ClientChunk chunk = new ClientChunk { X = data.X, Z = data.Z, Cells = data.Cells, Types = data.Types };
            int size = GlobalConstants.ChunkSize;
            Dictionary<int, OreCluster> clusters = new Dictionary<int, OreCluster>();

            int n = Math.Min(data.Cells.Length, data.Types.Length);
            for (int i = 0; i < n; i++)
            {
                int type = data.Types[i];
                int kind = ResourceKind.Of(TypeKeyOf(type));
                chunk.HasKind[kind] = true;
                if (kind != ResourceKind.Ore) continue;

                int cell = data.Cells[i];
                double wx = data.X * size + (cell % CellsPerSide) * CellSize + CellSize / 2.0;
                double wz = data.Z * size + (cell / CellsPerSide) * CellSize + CellSize / 2.0;
                OreCluster cluster;
                if (!clusters.TryGetValue(type, out cluster))
                {
                    cluster = new OreCluster { Type = type };
                    clusters[type] = cluster;
                }
                cluster.X += wx;
                cluster.Z += wz;
                cluster.Count++;
            }
            foreach (OreCluster cluster in clusters.Values)
            {
                cluster.X /= cluster.Count;
                cluster.Z /= cluster.Count;
                chunk.Ores.Add(cluster);
            }
            return chunk;
        }

        public string TypeKeyOf(int type)
        {
            return type >= 0 && type < TypeKeys.Length ? TypeKeys[type] : "";
        }

        public string TypeNameOf(int type)
        {
            if (type >= 0 && type < TypeNames.Length && !string.IsNullOrEmpty(TypeNames[type])) return TypeNames[type];
            return TypeKeyOf(type);
        }

        /// <summary>Finds a resource type by key ("clay-blue") or by its shown name ("Blue clay").</summary>
        private string FindTypeKey(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            text = text.Trim();
            for (int i = 0; i < TypeKeys.Length; i++)
            {
                if (string.Equals(TypeKeys[i], text, StringComparison.OrdinalIgnoreCase)) return TypeKeys[i];
            }
            for (int i = 0; i < TypeKeys.Length; i++)
            {
                if (string.Equals(TypeNameOf(i), text, StringComparison.OrdinalIgnoreCase)) return TypeKeys[i];
            }
            return null;
        }

        private TextCommandResult OnLegendCommand(TextCommandCallingArgs args)
        {
            if (!Allowed) return TextCommandResult.Error(Lang.Get("resourcemap:not-allowed"));

            int[] perType = new int[TypeKeys.Length];
            foreach (ClientChunk c in Chunks.Values)
            {
                foreach (byte t in c.Types) if (t < perType.Length) perType[t]++;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Lang.Get("resourcemap:legend-title", Chunks.Count));
            for (int kind = 0; kind < ResourceKind.Count; kind++)
            {
                for (int i = 0; i < TypeKeys.Length; i++)
                {
                    if (ResourceKind.Of(TypeKeys[i]) != kind) continue;
                    string hidden = ClientConfig.IsHidden(TypeKeys[i]) ? " " + Lang.Get("resourcemap:hidden") : "";
                    sb.AppendLine("<font color=\"" + ClientConfig.ColorFor(TypeKeys[i]) + "\">■</font> " + TypeNameOf(i) + " (" + TypeKeys[i] + ") – " + Lang.Get("resourcemap:cells", perType[i]) + hidden);
                }
            }
            sb.Append(Lang.Get("resourcemap:legend-help"));
            return TextCommandResult.Success(sb.ToString());
        }

        private class FoundSpot
        {
            public int Type;
            public double X;
            public double Z;
            public double Distance;
        }

        private TextCommandResult ClearFind()
        {
            Filter = "";
            VisualVersion++;
            return TextCommandResult.Success(Lang.Get("resourcemap:find-cleared"));
        }

        /// <summary>
        /// .resourcemap find malachite [waypoint]: shows only matching resources on the map, lists the nearest
        /// spots in chat, and optionally puts a waypoint on the nearest one.
        /// </summary>
        private TextCommandResult OnFindCommand(TextCommandCallingArgs args)
        {
            if (!Allowed) return TextCommandResult.Error(Lang.Get("resourcemap:not-allowed"));
            string text = (args[0] as string ?? "").Trim();
            string lower = text.ToLowerInvariant();
            if (text.Length == 0 || lower == "clear" || lower == "off" || lower == "all" || lower == "none") return ClearFind();

            bool waypoint = false;
            if (lower.EndsWith(" waypoint") || lower.EndsWith(" wp"))
            {
                waypoint = true;
                text = text.Substring(0, text.LastIndexOf(' ')).Trim();
            }

            // Which known types match?
            List<string> matchedNames = new List<string>();
            string oldFilter = Filter;
            Filter = text;
            for (int i = 0; i < TypeKeys.Length; i++)
            {
                if (MatchesFilter(i) && !matchedNames.Contains(TypeNameOf(i))) matchedNames.Add(TypeNameOf(i));
            }
            if (matchedNames.Count == 0)
            {
                Filter = oldFilter;
                return TextCommandResult.Error(Lang.Get("resourcemap:find-none-known", text));
            }
            VisualVersion++;

            // Every matching spot, nearest first.
            double px = 0, pz = 0;
            if (capi.World.Player != null && capi.World.Player.Entity != null)
            {
                px = capi.World.Player.Entity.Pos.X;
                pz = capi.World.Player.Entity.Pos.Z;
            }
            int size = GlobalConstants.ChunkSize;
            List<FoundSpot> spots = new List<FoundSpot>();
            foreach (ClientChunk chunk in Chunks.Values)
            {
                int n = Math.Min(chunk.Cells.Length, chunk.Types.Length);
                for (int i = 0; i < n; i++)
                {
                    int type = chunk.Types[i];
                    if (!MatchesFilter(type) || ClientConfig.IsHidden(TypeKeyOf(type))) continue;
                    int cell = chunk.Cells[i];
                    double x = chunk.X * size + (cell % CellsPerSide) * CellSize + CellSize / 2.0;
                    double z = chunk.Z * size + (cell / CellsPerSide) * CellSize + CellSize / 2.0;
                    spots.Add(new FoundSpot { Type = type, X = x, Z = z, Distance = Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz)) });
                }
            }
            spots.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            // Keep the nearest few, skipping spots that are part of a deposit already listed.
            List<FoundSpot> shown = new List<FoundSpot>();
            foreach (FoundSpot spot in spots)
            {
                bool near = false;
                foreach (FoundSpot kept in shown)
                {
                    if (kept.Type == spot.Type && Math.Abs(kept.X - spot.X) < 24 && Math.Abs(kept.Z - spot.Z) < 24) { near = true; break; }
                }
                if (near) continue;
                shown.Add(spot);
                if (shown.Count >= 5) break;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Lang.Get("resourcemap:find-showing", string.Join(", ", matchedNames)));
            if (shown.Count == 0)
            {
                sb.Append(Lang.Get("resourcemap:find-none-found"));
                return TextCommandResult.Success(sb.ToString());
            }
            foreach (FoundSpot spot in shown)
            {
                sb.AppendLine(Lang.Get("resourcemap:find-line", TypeNameOf(spot.Type), (int)Math.Round(spot.Distance),
                    Lang.Get("resourcemap:dir-" + Direction(spot.X - px, spot.Z - pz)), ShownCoords(spot.X, spot.Z)));
            }

            if (waypoint)
            {
                FoundSpot nearest = shown[0];
                double y = capi.World.Player != null && capi.World.Player.Entity != null ? capi.World.Player.Entity.Pos.Y : 110;
                string colour = ClientConfig.ColorFor(TypeKeyOf(nearest.Type));
                string cmd = string.Format(System.Globalization.CultureInfo.InvariantCulture, "/waypoint addati circle ={0} ={1} ={2} false {3} {4}",
                    (int)Math.Floor(nearest.X), (int)Math.Floor(y), (int)Math.Floor(nearest.Z), colour, TypeNameOf(nearest.Type));
                capi.SendChatMessage(cmd);
                sb.AppendLine(Lang.Get("resourcemap:find-waypoint", TypeNameOf(nearest.Type)));
            }
            sb.Append(Lang.Get("resourcemap:find-help"));
            return TextCommandResult.Success(sb.ToString());
        }

        /// <summary>Compass direction from a world offset (north is -Z in Vintage Story).</summary>
        private static string Direction(double dx, double dz)
        {
            if (Math.Abs(dx) < 1 && Math.Abs(dz) < 1) return "here";
            double angle = Math.Atan2(dx, -dz) * 180 / Math.PI;   // 0 = north, 90 = east
            if (angle < 0) angle += 360;
            string[] names = { "n", "ne", "e", "se", "s", "sw", "w", "nw" };
            return names[(int)Math.Round(angle / 45) % 8];
        }

        private TextCommandResult OnClientSettingsCommand(TextCommandCallingArgs args)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Lang.Get("resourcemap:client-settings-title"));
            sb.AppendLine("<strong>opacity</strong> = " + (int)Math.Round(ClientConfig.Opacity * 100) + "%");
            sb.AppendLine("<strong>markersize</strong> = " + ClientConfig.OreMarkerSize);
            sb.AppendLine("<strong>hidden</strong> = " + (ClientConfig.Hidden.Count == 0 ? "none" : string.Join(", ", ClientConfig.Hidden)));
            sb.AppendLine("<strong>search</strong> = " + (FilterActive ? Filter : "none"));
            List<string> colours = new List<string>();
            foreach (KeyValuePair<string, string> entry in ClientConfig.Colors) colours.Add(entry.Key + " " + entry.Value);
            sb.AppendLine("<strong>colours changed</strong> = " + (colours.Count == 0 ? "none" : string.Join(", ", colours)));
            sb.Append(Lang.Get("resourcemap:client-settings-help"));
            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult OnResetCommand(TextCommandCallingArgs args)
        {
            ClientConfig = new ClientConfig();
            SaveClientConfig();
            return TextCommandResult.Success(Lang.Get("resourcemap:reset-done"));
        }

        private TextCommandResult OnOpacityCommand(TextCommandCallingArgs args)
        {
            int percent = (int)args[0];
            ClientConfig.Opacity = percent / 100f;
            SaveClientConfig();
            return TextCommandResult.Success(Lang.Get("resourcemap:opacity-set", (int)Math.Round(ClientConfig.Opacity * 100)));
        }

        private TextCommandResult OnMarkerSizeCommand(TextCommandCallingArgs args)
        {
            ClientConfig.OreMarkerSize = (int)args[0];
            SaveClientConfig();
            return TextCommandResult.Success(Lang.Get("resourcemap:markersize-set", ClientConfig.OreMarkerSize));
        }

        private TextCommandResult OnHideCommand(TextCommandCallingArgs args, bool hide)
        {
            string key = FindTypeKey(args[0] as string);
            if (key == null) return TextCommandResult.Error(Lang.Get("resourcemap:unknown-resource", args[0] as string));
            ClientConfig.Hidden.Remove(key);
            if (hide) ClientConfig.Hidden.Add(key);
            SaveClientConfig();
            return TextCommandResult.Success(Lang.Get(hide ? "resourcemap:now-hidden" : "resourcemap:now-shown", key));
        }

        private TextCommandResult OnColorCommand(TextCommandCallingArgs args)
        {
            string text = (args[0] as string ?? "").Trim();
            int space = text.LastIndexOf(' ');
            if (space <= 0) return TextCommandResult.Error(Lang.Get("resourcemap:cmd-color-desc"));
            string name = text.Substring(0, space).Trim();
            string hex = text.Substring(space + 1).Trim();
            string key = FindTypeKey(name);
            if (key == null) return TextCommandResult.Error(Lang.Get("resourcemap:unknown-resource", name));
            if (string.Equals(hex, "default", StringComparison.OrdinalIgnoreCase))
            {
                ClientConfig.Colors.Remove(key);
            }
            else
            {
                if (hex != null && !hex.StartsWith("#")) hex = "#" + hex;
                if (!ColorHex.IsValid(hex)) return TextCommandResult.Error(Lang.Get("resourcemap:bad-color", hex));
                ClientConfig.Colors[key] = hex.ToLowerInvariant();
            }
            SaveClientConfig();
            return TextCommandResult.Success(Lang.Get("resourcemap:color-set", key, ClientConfig.ColorFor(key)));
        }

        public override void Dispose()
        {
            foreach (ClientChunk c in Chunks.Values) c.DisposeTextures();
            Chunks.Clear();
            base.Dispose();
        }
    }
}
