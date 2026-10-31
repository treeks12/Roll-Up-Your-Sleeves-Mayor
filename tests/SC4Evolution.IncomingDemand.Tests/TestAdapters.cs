// Explicit isolated adapters: execute the actual context and extension sources,
// without loading CS1, installing Harmony or claiming native callback registration.
namespace ICities
{
    public class DemandExtensionBase
    {
        public virtual int OnCalculateResidentialDemand(int value) { return value; }
        public virtual int OnCalculateCommercialDemand(int value) { return value; }
        public virtual int OnCalculateWorkplaceDemand(int value) { return value; }
        public virtual int OnUpdateDemand(int last, int next, int target) { return next; }
    }
}
namespace SC4Evolution.Mod.Economy
{
    internal sealed class SegmentedDemandSnapshot
    {
        internal bool Valid = true;
        internal bool Missing = false;
        internal bool TryGetDemand(uint key, out int value) { value = 3; return !Missing; }
    }
    internal sealed class EconomicBridge
    {
        internal ResidentialCapacityBridge Capacity;
        internal IncomingDemandBinding DemandContextBinding;
        internal int DemandContextSession;
        internal SegmentedDemandSnapshot Published = new SegmentedDemandSnapshot();
        internal System.Action DuringCanUse = null;
        internal bool CanUseDemand(SegmentedDemandSnapshot snapshot) { if (DuringCanUse != null) DuringCanUse(); return snapshot != null && snapshot.Valid; }
        internal EconomicBridge(int session)
        { DemandContextSession = session; DemandContextBinding = new IncomingDemandBinding(this, session); }
    }
    internal sealed class ResidentialCapacityBridge
    {
        internal bool ModeRequested;
    }
}
