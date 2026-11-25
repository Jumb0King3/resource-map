using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ResourceMap
{
    /// <summary>Shared parts of the three resource layers: finding the mod, map positions and textures.</summary>
    public abstract class ResourceLayerBase : MapLayer
    {
        protected readonly ICoreClientAPI capi;
        private ResourceMapMod modSystem;
        protected Vec2f viewPos = new Vec2f();

        protected ResourceLayerBase(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
        {
            capi = api as ICoreClientAPI;
        }

        protected abstract int Kind { get; }

        public override string Title => Lang.Get("resourcemap:layer-" + ResourceKind.Keys[Kind]);

        public override string LayerGroupCode => "resourcemap-" + ResourceKind.Keys[Kind];

        public override EnumMapAppSide DataSide => EnumMapAppSide.Client;

        protected ResourceMapMod Mod
        {
            get
            {
                if (modSystem == null && capi != null) modSystem = capi.ModLoader.GetModSystem<ResourceMapMod>();
                return modSystem;
            }
        }

        protected bool Ready(out ResourceMapMod mod)
        {
            mod = Mod;
            return Active && capi != null && mod != null && mod.ClientConfig != null && mod.Allowed;
        }

        /// <summary>Screen position of a world x/z on this map element.</summary>
        protected void ToScreen(GuiElementMap mapElem, double worldX, double worldZ, out double x, out double y)
        {
            mapElem.TranslateWorldPosToViewPos(new Vec3d(worldX, 0, worldZ), ref viewPos);
            x = mapElem.Bounds.renderX + viewPos.X;
            y = mapElem.Bounds.renderY + viewPos.Y;
        }

        /// <summary>The world x/z under a screen position (the map is a straight scale and shift of the world).</summary>
        protected void ToWorld(GuiElementMap mapElem, double screenX, double screenY, out double worldX, out double worldZ)
        {
            double baseX = 512000, baseZ = 512000;
            if (capi.World.Player != null && capi.World.Player.Entity != null)
            {
                baseX = Math.Floor(capi.World.Player.Entity.Pos.X);
                baseZ = Math.Floor(capi.World.Player.Entity.Pos.Z);
            }
            double x0, y0, x1, y1;
            ToScreen(mapElem, baseX, baseZ, out x0, out y0);
            ToScreen(mapElem, baseX + 256, baseZ + 256, out x1, out y1);
            double sx = (x1 - x0) / 256.0, sy = (y1 - y0) / 256.0;
            worldX = sx != 0 ? baseX + (screenX - x0) / sx : baseX;
            worldZ = sy != 0 ? baseZ + (screenY - y0) / sy : baseZ;
        }

        protected static bool Overlaps(GuiElementMap mapElem, double x0, double y0, double x1, double y1)
        {
            double left = mapElem.Bounds.renderX, top = mapElem.Bounds.renderY;
            double right = left + mapElem.Bounds.InnerWidth, bottom = top + mapElem.Bounds.InnerHeight;
            return x1 >= left && x0 <= right && y1 >= top && y0 <= bottom;
        }

        protected static bool Inside(GuiElementMap mapElem, double x, double y)
        {
            double left = mapElem.Bounds.renderX, top = mapElem.Bounds.renderY;
            return x >= left && x <= left + mapElem.Bounds.InnerWidth && y >= top && y <= top + mapElem.Bounds.InnerHeight;
        }

        protected static void ParseColor(string hex, out double r, out double g, out double b)
        {
            if (!ColorHex.TryParse(hex, out r, out g, out b)) { r = 1; g = 0; b = 1; }
        }
    }

    /// <summary>
    /// Clay or soil: each chunk column becomes a small 16 x 16 picture (one pixel per 2 x 2 blocks) in the resource
    /// colours, drawn over the map at the chunk's position.
    /// </summary>
    public abstract class AreaLayerBase : ResourceLayerBase
    {
        protected AreaLayerBase(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }

        public override void Render(GuiElementMap mapElem, float dt)
        {
            ResourceMapMod mod;
            if (!Ready(out mod)) return;

            int size = GlobalConstants.ChunkSize;
            float alpha = mod.ClientConfig.Opacity;
            Vec4f tint = new Vec4f(alpha, alpha, alpha, alpha);

            foreach (ClientChunk chunk in mod.Chunks.Values)
            {
                if (!chunk.HasKind[Kind]) continue;

                double x0, y0, x1, y1;
                ToScreen(mapElem, chunk.X * size, chunk.Z * size, out x0, out y0);
                ToScreen(mapElem, (chunk.X + 1) * size, (chunk.Z + 1) * size, out x1, out y1);
                if (!Overlaps(mapElem, x0, y0, x1, y1)) continue;

                LoadedTexture tex = TextureFor(mod, chunk);
                if (tex == null) continue;
                capi.Render.Render2DTexturePremultipliedAlpha(tex.TextureId, (float)x0, (float)y0, (float)(x1 - x0), (float)(y1 - y0), 50, tint);
            }
        }

        private LoadedTexture TextureFor(ResourceMapMod mod, ClientChunk chunk)
        {
            if (chunk.Textures[Kind] != null && chunk.TextureVersion[Kind] == mod.VisualVersion) return chunk.Textures[Kind];
            if (chunk.Textures[Kind] != null) { chunk.Textures[Kind].Dispose(); chunk.Textures[Kind] = null; }

            int cells = ResourceMapMod.CellsPerSide;
            // Which visible type is in each cell (-1 = nothing), for this layer.
            int[] grid = new int[cells * cells];
            for (int i = 0; i < grid.Length; i++) grid[i] = -1;
            int n = Math.Min(chunk.Cells.Length, chunk.Types.Length);
            for (int i = 0; i < n; i++)
            {
                string key = mod.TypeKeyOf(chunk.Types[i]);
                if (ResourceKind.Of(key) != Kind || mod.ClientConfig.IsHidden(key) || !mod.MatchesFilter(chunk.Types[i])) continue;
                grid[chunk.Cells[i]] = chunk.Types[i];
            }

            const int scale = 2;    // texture pixels per cell (one per block)
            int px = cells * scale;
            bool any = false;
            LoadedTexture tex;
            using (Cairo.ImageSurface surface = new Cairo.ImageSurface(Cairo.Format.Argb32, px, px))
            using (Cairo.Context ctx = new Cairo.Context(surface))
            {
                for (int cz = 0; cz < cells; cz++)
                {
                    for (int cx = 0; cx < cells; cx++)
                    {
                        int type = grid[cz * cells + cx];
                        if (type < 0) continue;
                        any = true;
                        double r, g, b;
                        ParseColor(mod.ClientConfig.ColorFor(mod.TypeKeyOf(type)), out r, out g, out b);
                        ctx.Rectangle(cx * scale, cz * scale, scale, scale);
                        ctx.SetSourceRGBA(r, g, b, 1);
                        ctx.Fill();

                        // Darker edge where the deposit ends (neighbour inside this chunk is different)
                        ctx.SetSourceRGBA(r * 0.35, g * 0.35, b * 0.35, 1);
                        if (cx > 0 && grid[cz * cells + cx - 1] != type) { ctx.Rectangle(cx * scale, cz * scale, 1, scale); ctx.Fill(); }
                        if (cx < cells - 1 && grid[cz * cells + cx + 1] != type) { ctx.Rectangle(cx * scale + scale - 1, cz * scale, 1, scale); ctx.Fill(); }
                        if (cz > 0 && grid[(cz - 1) * cells + cx] != type) { ctx.Rectangle(cx * scale, cz * scale, scale, 1); ctx.Fill(); }
                        if (cz < cells - 1 && grid[(cz + 1) * cells + cx] != type) { ctx.Rectangle(cx * scale, cz * scale + scale - 1, scale, 1); ctx.Fill(); }
                    }
                }
                int textureId = capi.Gui.LoadCairoTexture(surface, false);
                tex = new LoadedTexture(capi, textureId, px, px);
            }

            chunk.Textures[Kind] = tex;
            chunk.TextureVersion[Kind] = mod.VisualVersion;
            return any ? tex : null;
        }

        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
        {
            ResourceMapMod mod;
            if (!Ready(out mod) || !Inside(mapElem, args.X, args.Y)) return;

            double wx, wz;
            ToWorld(mapElem, args.X, args.Y, out wx, out wz);
            int size = GlobalConstants.ChunkSize;
            int cx = (int)Math.Floor(wx / size), cz = (int)Math.Floor(wz / size);

            ClientChunk chunk;
            if (!mod.Chunks.TryGetValue(ResourceMapMod.ChunkKey(cx, cz), out chunk) || !chunk.HasKind[Kind]) return;

            int lx = (int)Math.Floor(wx) - cx * size, lz = (int)Math.Floor(wz) - cz * size;
            int cell = (lz / ResourceMapMod.CellSize) * ResourceMapMod.CellsPerSide + (lx / ResourceMapMod.CellSize);

            int n = Math.Min(chunk.Cells.Length, chunk.Types.Length);
            for (int i = 0; i < n; i++)
            {
                if (chunk.Cells[i] != cell) continue;
                string key = mod.TypeKeyOf(chunk.Types[i]);
                if (ResourceKind.Of(key) != Kind || mod.ClientConfig.IsHidden(key) || !mod.MatchesFilter(chunk.Types[i])) continue;
                hoverText.AppendLine(Lang.Get("resourcemap:hover-area", mod.TypeNameOf(chunk.Types[i]), mod.ShownCoords(wx, wz)));
                return;
            }
        }
    }

    public class ClayMapLayer : AreaLayerBase
    {
        public ClayMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }
        protected override int Kind => ResourceKind.Clay;
    }

    public class SoilMapLayer : AreaLayerBase
    {
        public SoilMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }
        protected override int Kind => ResourceKind.Soil;
    }

    /// <summary>
    /// Surface ores: nuggets lying on the ground point at a deposit underneath. One hexagon marker per ore type
    /// per chunk column, at the middle of its nuggets.
    /// </summary>
    public class OreMapLayer : ResourceLayerBase
    {
        private const int MaxHoverLines = 8;
        private readonly Dictionary<string, LoadedTexture> textureCache = new Dictionary<string, LoadedTexture>();
        private int cacheVersion = -1;

        public OreMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }

        protected override int Kind => ResourceKind.Ore;

        private float MarkerSize(ResourceMapMod mod)
        {
            return (float)GuiElement.scaled(mod.ClientConfig.OreMarkerSize);
        }

        public override void Render(GuiElementMap mapElem, float dt)
        {
            ResourceMapMod mod;
            if (!Ready(out mod)) return;
            if (cacheVersion != mod.VisualVersion) ClearTextures(mod.VisualVersion);

            float size = MarkerSize(mod);
            bool filtering = mod.FilterActive;
            float bigSize = size * 1.5f;
            Vec4f faded = new Vec4f(0.25f, 0.25f, 0.25f, 0.25f);

            // Pass 0: everything (or, while searching, the faded non-matches). Pass 1: search matches, bigger, on top.
            for (int pass = 0; pass < (filtering ? 2 : 1); pass++)
            {
                foreach (ClientChunk chunk in mod.Chunks.Values)
                {
                    if (!chunk.HasKind[ResourceKind.Ore]) continue;
                    foreach (OreCluster ore in chunk.Ores)
                    {
                        string key = mod.TypeKeyOf(ore.Type);
                        if (mod.ClientConfig.IsHidden(key)) continue;
                        bool match = mod.MatchesFilter(ore.Type);
                        if (filtering && (pass == 0) == match) continue;

                        double x, y;
                        ToScreen(mapElem, ore.X, ore.Z, out x, out y);
                        if (!Inside(mapElem, x, y)) continue;
                        float s = filtering && match ? bigSize : size;
                        LoadedTexture tex = HexTexture(mod.ClientConfig.ColorFor(key), s);
                        if (tex == null) continue;
                        capi.Render.Render2DTexturePremultipliedAlpha(tex.TextureId, (float)(x - s / 2), (float)(y - s / 2), s, s,
                            50, filtering && !match ? faded : null);
                    }
                }
            }
        }

        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
        {
            ResourceMapMod mod;
            if (!Ready(out mod) || !Inside(mapElem, args.X, args.Y)) return;

            double reach = Math.Max(5, MarkerSize(mod) * 0.6);
            Vec3d myPos = capi.World.Player != null && capi.World.Player.Entity != null ? capi.World.Player.Entity.Pos.XYZ : null;
            int lines = 0, extra = 0;

            foreach (ClientChunk chunk in mod.Chunks.Values)
            {
                if (!chunk.HasKind[ResourceKind.Ore]) continue;
                foreach (OreCluster ore in chunk.Ores)
                {
                    if (mod.ClientConfig.IsHidden(mod.TypeKeyOf(ore.Type))) continue;
                    double x, y;
                    ToScreen(mapElem, ore.X, ore.Z, out x, out y);
                    if (Math.Abs(x - args.X) > reach || Math.Abs(y - args.Y) > reach) continue;

                    if (lines >= MaxHoverLines) { extra++; continue; }
                    lines++;
                    string distance = "";
                    if (myPos != null)
                    {
                        double dx = ore.X - myPos.X, dz = ore.Z - myPos.Z;
                        distance = ", " + Lang.Get("resourcemap:blocks-away", (int)Math.Round(Math.Sqrt(dx * dx + dz * dz)));
                    }
                    hoverText.AppendLine(Lang.Get("resourcemap:hover-ore", mod.TypeNameOf(ore.Type), ore.Count) + distance);
                }
            }
            if (extra > 0) hoverText.AppendLine(Lang.Get("resourcemap:and-more", extra));
        }

        /// <summary>A hexagon in the ore's colour with a dark outline, so it shows on any terrain.</summary>
        private LoadedTexture HexTexture(string hex, float size)
        {
            int px = Math.Max(6, (int)Math.Ceiling(size));
            string key = hex + "|" + px;
            LoadedTexture existing;
            if (textureCache.TryGetValue(key, out existing)) return existing;

            double r, g, b;
            ParseColor(hex, out r, out g, out b);
            double c = px / 2.0;
            double line = Math.Max(1.2, px / 8.0);
            double rad = c - line;

            LoadedTexture tex;
            using (Cairo.ImageSurface surface = new Cairo.ImageSurface(Cairo.Format.Argb32, px, px))
            using (Cairo.Context ctx = new Cairo.Context(surface))
            {
                // Hexagon (flat top), a shape neither Creature Map Markers nor Far Player Map Markers uses.
                for (int k = 0; k < 6; k++)
                {
                    double a = Math.PI / 3 * k;
                    double hx = c + rad * Math.Cos(a), hy = c + rad * 0.9 * Math.Sin(a);
                    if (k == 0) ctx.MoveTo(hx, hy); else ctx.LineTo(hx, hy);
                }
                ctx.ClosePath();
                ctx.SetSourceRGBA(r, g, b, 1);
                ctx.FillPreserve();
                ctx.LineWidth = line;
                ctx.SetSourceRGBA(0.08, 0.06, 0.05, 1);
                ctx.Stroke();
                int textureId = capi.Gui.LoadCairoTexture(surface, true);
                tex = new LoadedTexture(capi, textureId, px, px);
            }
            textureCache[key] = tex;
            return tex;
        }

        private void ClearTextures(int version)
        {
            foreach (LoadedTexture tex in textureCache.Values) if (tex != null) tex.Dispose();
            textureCache.Clear();
            cacheVersion = version;
        }

        public override void Dispose()
        {
            ClearTextures(-1);
            base.Dispose();
        }
    }
}
