using ProtoBuf;

namespace ResourceMap
{
    /// <summary>
    /// The resources found in one chunk column (32 x 32 blocks), on a grid of 2 x 2 block cells (16 x 16 cells).
    /// Cells[i] is the cell index (row * 16 + column, row = z) and Types[i] the resource type index found there.
    /// A cell can appear more than once (for example clay with an ore nugget on top). Empty arrays = nothing here.
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class ChunkResources
    {
        public int X;
        public int Z;
        public byte[] Cells;
        public byte[] Types;
    }

    /// <summary>Server to client: resource type names plus chunks that changed since the last packet.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class ResourceUpdatePacket
    {
        /// <summary>Forget everything known before applying this packet.</summary>
        public bool Full;
        /// <summary>False if this player isn't allowed to see resources (admin-only servers).</summary>
        public bool Allowed;
        /// <summary>Type keys such as "clay-blue", "peat", "soil-high", "ore-nativecopper". Index = type index.</summary>
        public string[] TypeKeys;
        /// <summary>Display names, same order as TypeKeys.</summary>
        public string[] TypeNames;
        public ChunkResources[] Chunks;
    }

    /// <summary>What the server saves in the world file, so areas seen before stay on the map after a restart.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class ResourceSaveData
    {
        public string[] TypeKeys;
        public string[] TypeNames;
        public ChunkResources[] Chunks;
    }
}
