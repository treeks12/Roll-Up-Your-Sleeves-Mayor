using System;
using System.IO;
using SC4Evolution.Mod.Civic;
using SC4Evolution.Simulation;

namespace SC4Evolution.Mod.Runtime
{
    public static partial class ModRuntime
    {
        private static volatile CivicFundingBridge _civic;
        private static byte[] _pendingCivicState;
        private static string _pendingCivicLoadFailure;

        internal static CivicFundingBridge CurrentCivicFundingBridge { get { return _loaded ? _civic : null; } }
        public static string CivicFundingStatus
        {
            get { CivicFundingBridge civic = _civic; return civic == null ? "Financiamento local aguardando cidade." : civic.Status; }
        }

        internal static void SetLoadedCivicState(byte[] payload)
        {
            if (payload != null && payload.Length > CivicFundingLedgerCodec.MaximumPayloadBytes)
                throw new InvalidDataException("Civic funding payload exceeds the supported size.");
            lock (StateLock)
            {
                _pendingCivicState = payload == null ? null : (byte[])payload.Clone();
                // A read failure remains sticky until BeginEconomicLoad.
            }
        }

        internal static void SetCivicLoadFailure(string reason)
        {
            lock (StateLock)
                _pendingCivicLoadFailure = string.IsNullOrEmpty(reason) ? "Civic funding save could not be read." : reason;
        }

        internal static byte[] GetCivicSaveState()
        {
            lock (StateLock)
            {
                if (_loaded)
                {
                    if (_civic == null) throw new InvalidOperationException("Civic funding owner is unavailable.");
                    return _civic.CaptureSave();
                }
                if (_pendingCivicLoadFailure != null && _pendingCivicState == null)
                    throw new InvalidDataException("Civic funding save is unavailable after a failed read.");
                return _pendingCivicState == null ? null : (byte[])_pendingCivicState.Clone();
            }
        }
    }
}
