using System;
using ICities;
using SC4Evolution.Mod.Diagnostics;
using SC4Evolution.Mod.UI;

namespace SC4Evolution.Mod.Runtime
{
    public sealed class Loading : LoadingExtensionBase
    {
        public override void OnLevelLoaded(LoadMode mode)
        {
            base.OnLevelLoaded(mode);
            if (mode != LoadMode.NewGame && mode != LoadMode.LoadGame && mode != LoadMode.NewGameFromScenario)
                return;

            try
            {
                ModRuntime.Start(mode == LoadMode.NewGame || mode == LoadMode.NewGameFromScenario);
                CabinetUI.Install();
                ModRuntime.Log("Playable level ready; cabinet installed. Mode=" + mode + "; CS1=" + BuildConfig.applicationVersion);
            }
            catch (ModRuntime.StartSupersededException error)
            {
                // The callback no longer owns the stopped/replaced session. Its
                // cleanup must never uninstall the UI or stop the new owner.
                ModRuntime.Log("Obsolete loading callback rejected without session cleanup: " + error);
            }
            catch (Exception error)
            {
                ModRuntime.Log("Cabinet initialization failed: " + error);
                CabinetUI.Uninstall();
                ModRuntime.Stop();
            }
        }

        public override void OnLevelUnloading()
        {
            AutonomousTestDriver.OnLevelUnloading();
            try { CabinetUI.Uninstall(); }
            catch (Exception error) { ModRuntime.Log("Cabinet removal failed: " + error); }
            finally { ModRuntime.Stop(); }
            base.OnLevelUnloading();
        }
    }

    public sealed class Threading : ThreadingExtensionBase
    {
        public override void OnAfterSimulationFrame()
        {
            ModRuntime.SampleIfDue();
        }
    }

    public sealed class SavedData : SerializableDataExtensionBase
    {
        private const string DataKey = "SC4Evolution.Cabinet.v1";
        private const int MaximumStateBytes = 262144;
        private const string ResidentialClassKey = "SC4Evolution.ResidentialClasses.v1";
        private const int MaximumResidentialClassBytes = 16 * 1024 * 1024;
        internal const string EconomicPairKey = "SC4Evolution.EconomicPair.v1";
        internal const string CivicFundingKey = "SC4Evolution.CivicFunding.v1";

        public override void OnLoadData()
        {
            ModRuntime.BeginEconomicLoad();
            try
            {
                ModRuntime.SetLoadedCivicState(serializableDataManager.LoadData(CivicFundingKey));
            }
            catch (Exception error)
            {
                ModRuntime.SetCivicLoadFailure(error.Message);
                ModRuntime.Log("Civic funding data unavailable; retained storage must not be reset: " + error.Message);
            }
            try
            {
                byte[] state = serializableDataManager.LoadData(DataKey);
                if (state != null && state.Length > MaximumStateBytes)
                    throw new InvalidOperationException("Cabinet save data exceeds the supported size.");
                ModRuntime.SetLoadedState(state);
            }
            catch (Exception error)
            {
                ModRuntime.SetLoadedState(null);
                ModRuntime.Log("Could not read the cabinet's optional save data: " + error.Message);
            }

            // A present envelope is authoritative, including when invalid. Never
            // mix a stale legacy class payload with a newer capacity payload.
            try
            {
                byte[] envelope = serializableDataManager.LoadData(EconomicPairKey);
                if (envelope != null)
                {
                    byte[] classes, capacity;
                    EconomicSaveEnvelope.Decode(envelope, out classes, out capacity);
                    ModRuntime.SetLoadedEconomicPair(classes, capacity);
                    return;
                }
            }
            catch (Exception error)
            {
                ModRuntime.SetEconomicLoadFailure(error.Message);
                ModRuntime.Log("Bound economic save rejected; state retained and effects suspended: " + error.Message);
                return;
            }

            try
            {
                byte[] state = serializableDataManager.LoadData(ResidentialClassKey);
                if (state != null && state.Length > MaximumResidentialClassBytes)
                    throw new InvalidOperationException("Residential class save data exceeds the supported size.");
                ModRuntime.SetLoadedResidentialClassState(state);
            }
            catch (Exception error)
            {
                // A failed read is different from an absent key. Never silently import
                // a new class ledger over unreadable data from an existing city.
                ModRuntime.SetResidentialClassLoadFailure(error.Message);
                ModRuntime.Log("Residential class effects disabled because save data could not be read: " + error.Message);
            }
        }

        public override void OnSaveData()
        {
            try
            {
                // Capture/validate everything before touching any storage key. One
                // pair entry is atomic at the native API's per-key boundary only.
                byte[] economic = ModRuntime.GetEconomicSaveEnvelope();
                byte[] cabinet = ModRuntime.GetSaveState();
                byte[] civic = ModRuntime.GetCivicSaveState();
                if (economic != null && economic.Length > EconomicSaveEnvelope.MaximumBytes)
                    throw new InvalidOperationException("Economic pair save state is unexpectedly large.");
                if (cabinet != null && cabinet.Length > MaximumStateBytes)
                    throw new InvalidOperationException("Cabinet save state is unexpectedly large.");
                if (civic != null && civic.Length > SC4Evolution.Simulation.CivicFundingLedgerCodec.MaximumPayloadBytes)
                    throw new InvalidOperationException("Civic funding save state is unexpectedly large.");
                if (economic != null) serializableDataManager.SaveData(EconomicPairKey, economic);
                if (cabinet != null) serializableDataManager.SaveData(DataKey, cabinet);
                if (civic != null) serializableDataManager.SaveData(CivicFundingKey, civic);
                // The original legacy class entry is retained verbatim. Future loads
                // prefer the complete envelope, including legitimate null capacity.
                ModRuntime.ReportPersistenceSuccess();
            }
            catch (Exception error)
            {
                ModRuntime.ReportPersistenceFailure(error.Message);
                // CS1 forwards callback failures visibly but may continue its global
                // save. Never claim rollback of another key or the native savefile.
                throw new InvalidOperationException("SC4Evolution economic save failed; retained state must not be reset.", error);
            }
        }
    }
}
