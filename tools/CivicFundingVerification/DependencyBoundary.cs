using System;
namespace SC4Evolution.Mod.Economy
{
    // Inert boundaries only. These checks never initialize/query/patch a provider.
    internal sealed class ResidentialCapacityProviderActivation { }
    internal static class GrowthGateDependency { internal static bool Available() { throw new InvalidOperationException("Provider initialization is forbidden in this isolated harness."); } }
    internal static class ResidentialCapacityCompatibility
    {
        internal static bool TryActivateOfficialProviderCopies(out ResidentialCapacityProviderActivation activation, out string reason)
        { activation = null; reason = "Provider initialization is forbidden in this isolated harness."; throw new InvalidOperationException(reason); }
    }
}
