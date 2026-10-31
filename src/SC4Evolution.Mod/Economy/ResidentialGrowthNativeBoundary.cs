using System;
using System.Reflection;

namespace SC4Evolution.Mod.Economy
{
    // Literal original-body pins. These are additional admission boundaries, not
    // a substitute for the effective-patch and full capacity compatibility guard.
    internal static class ResidentialGrowthNativeBoundary
    {
        internal static void RequireZoneBlock(MethodInfo target)
        {
            Require(target, "ZoneBlock", "SimulationStep", "public instance System.Void ZoneBlock::SimulationStep(System.UInt16)",
                "1cbce958ca74423d28820298c8c36aa5f900158800c55aa1df981871ea353b97",
                "01e04c0d1ab5d75122fe9a2ab3e5a90a914e44c5661334c25b106c7b409d469a", 4188);
        }
        internal static void RequireRedevelopment(MethodInfo target)
        {
            Require(target, "PrivateBuildingAI", "SimulationStep", "public instance System.Void PrivateBuildingAI::SimulationStep(System.UInt16,Building&,Building+Frame&)",
                "5c0705f1f350f8baf92dc9646e37e64ebd8ea687dfde743a44aea199c48f65a9",
                "21927f06aa3b46299ecc894511714a1613258e4cd76cd19fe33f49b55bb88eaf", 1839);
        }
        internal static void RequireResidentialPhase(MethodInfo target)
        {
            Require(target, "ResidentialBuildingAI", "SimulationStep", "public instance System.Void ResidentialBuildingAI::SimulationStep(System.UInt16,Building&,Building+Frame&)",
                "250074cf0c7ab5ae2b412102b03809d7476642073a799bf21f341101617316cf",
                "f786a377a34ec65fa08ab2a0a3061a9a1f448952c5fef2c7335301347b5c98a1", 807);
        }
        internal static void RequireRelease(MethodInfo target)
        {
            Require(target, "BuildingManager", "ReleaseBuilding", "public instance System.Void BuildingManager::ReleaseBuilding(System.UInt16)",
                "9f85239cef7329c14ddd54f06c698b7acd2631b03c5dcebab5350ada512f1cde",
                "6b56c07c5fc8c3c2a406729197584cf7414b95b05543563c1e0cc9d92b211e91", 55);
        }
        internal static void RequireStartUpgrading(MethodInfo target)
        {
            Require(target, "PrivateBuildingAI", "StartUpgrading", "public instance System.Void PrivateBuildingAI::StartUpgrading(System.UInt16,Building&)",
                "0ed985bd4e986c7c44579c36c976614f18831757142293f3e038b431690e0eaa",
                "d372c29a65ef963d05b9935c6986142507ebd2534aee1886d175c60ce9843b36", 397);
        }
        private static void Require(MethodInfo target, string type, string name, string signature, string il, string body, int length)
        {
            if (target == null || BuildConfig.applicationVersion != "1.21.1-f9")
                throw new InvalidOperationException("Residential admission native boundary is unavailable.");
            ResidentialCapacityCompatibility.MethodPin pin = new ResidentialCapacityCompatibility.MethodPin("Assembly-CSharp", type, name, signature, il, body, length);
            if (!ResidentialCapacityCompatibility.ObserveMethod(target, pin).Known)
                throw new InvalidOperationException("Residential admission native signature/IL/locals/EH differs: " + signature);
        }
    }
}
