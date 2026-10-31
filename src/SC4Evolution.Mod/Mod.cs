using ICities;
using SC4Evolution.Mod.Diagnostics;
using SC4Evolution.Mod.Runtime;

namespace SC4Evolution.Mod
{
    public sealed class Mod : IUserMod
    {
        public string Name { get { return "SC4Evolution — Gabinete municipal"; } }
        public string Description
        {
            get { return "Fundação da conversão: gabinete, assessores e informações reais da cidade. A conversão econômica completa está em desenvolvimento."; }
        }

        public void OnEnabled()
        {
            // Ordinary enabled sessions have no explicit test request and take no
            // action here. Development requests are consumed once inside the project.
            try
            {
                ModRuntime.Log("SC4Evolution enabled; project root=" + ModRuntime.ProjectRoot);
                AutonomousTestDriver.TryStart(ModRuntime.ProjectRoot);
            }
            catch (System.Exception error) { ModRuntime.Log("Unattended test startup rejected: " + error); }
        }

        public void OnDisabled()
        {
            try { AutonomousTestDriver.CancelPendingTest(); }
            catch (System.Exception error) { ModRuntime.Log("Unattended test cancellation failed: " + error); }
        }

    }
}
