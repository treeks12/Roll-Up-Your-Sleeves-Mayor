using SC4Evolution.Simulation;
namespace SC4Evolution.Mod.Economy
{
    // Compile-only API adapter. No full Observe success is claimed by this
    // harness, which exercises local native struct arrays through InspectChain.
    internal static class ResidentialClassBridge
    {
        internal static bool TryIdentity(ushort id, out ResidentialLotIdentity identity)
        { identity = null; return false; }
    }
}
