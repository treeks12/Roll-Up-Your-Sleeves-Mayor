using System.IO;
using SC4Evolution.Mod.Runtime;
using SC4Evolution.Simulation;

namespace SC4Evolution.Mod.Diagnostics
{
    // Pure oracle only. It never captures native buffers, writes keys, or relaxes
    // session/frame/date/cash/pause ownership checks in the native driver.
    internal static class EconomicSaveRoundtripOracle
    {
        internal static void DecodeRequired(byte[] envelope, out byte[] classes, out byte[] capacity)
        {
            if (envelope == null) throw new InvalidDataException("Authoritative EconomicPair.v1 envelope is absent.");
            EconomicSaveEnvelope.Decode(envelope, out classes, out capacity);
        }

        internal static void RequireSaved(byte[] expectedLive, byte[] actualStored, byte[] legacyBefore, byte[] legacyAfter)
        {
            RequireSamePair(expectedLive, actualStored);
            RequireLegacyUnchanged(legacyBefore, legacyAfter);
        }

        internal static void RequireRestored(byte[] saved, byte[] restoredStored, byte[] restoredLive,
            byte[] legacySaved, byte[] legacyRestored)
        {
            RequireSamePair(saved, restoredStored);
            RequireSamePair(saved, restoredLive);
            RequireLegacyUnchanged(legacySaved, legacyRestored);
        }

        // Call and freeze this result BEFORE native reload. No observed live data,
        // native clock, census, mutable configuration or runtime epoch is an input.
        internal static byte[] PrepareStableCapacityRestore(byte[] saved, bool rawMode)
        {
            byte[] classes;
            ResidentialCapacityLedgerState state = ReadStableCapacity(saved, out classes);
            ResidentialCapacityLedger ledger = ResidentialCapacityLedger.Restore(state.ClassLedgerId, state, 1);
            if (rawMode)
            {
                long external = checked(state.ExternalRevision + 1L);
                if (!ledger.AdvanceExternalRevision(external) || !ledger.AcknowledgePublication(state.StockRevision, external))
                    throw new InvalidDataException("Stable RAW restore revision/publication transition failed.");
            }
            ResidentialCapacityLedgerState expected;
            if (!ledger.TryCapture(out expected))
                throw new InvalidDataException("Stable restore unexpectedly contains unresolved accounting work.");
            byte[] capacity;
            using (MemoryStream stream = new MemoryStream())
            {
                ResidentialCapacityLedgerCodec.Write(stream, expected, classes);
                capacity = stream.ToArray();
            }
            return EconomicSaveEnvelope.Encode(classes, capacity);
        }

        internal static void RequireRestoredWithExpectedLive(byte[] saved, byte[] restoredStored,
            byte[] expectedLive, byte[] restoredLive, byte[] legacySaved, byte[] legacyRestored)
        {
            // The caller owns expectedLive's pre-load provenance and declared mode.
            // This method never manufactures expectation from restoredLive.
            RequireSamePair(saved, restoredStored);
            byte[] savedClasses, expectedClasses;
            ReadStableCapacity(saved, out savedClasses);
            ReadStableCapacity(expectedLive, out expectedClasses);
            if (!Same(savedClasses, expectedClasses))
                throw new InvalidDataException("Stable restore expectation changed the exact saved class payload.");
            RequireSamePair(expectedLive, restoredLive);
            RequireLegacyUnchanged(legacySaved, legacyRestored);
        }

        private static ResidentialCapacityLedgerState ReadStableCapacity(byte[] envelope, out byte[] classes)
        {
            byte[] capacity;
            DecodeRequired(envelope, out classes, out capacity);
            if (capacity == null) throw new InvalidDataException("Stable populated restore requires a capacity payload.");
            ResidentialCapacityLedgerState state;
            using (MemoryStream stream = new MemoryStream(capacity, false))
                state = ResidentialCapacityLedgerCodec.ReadBound(stream, classes);
            ResidentialCapacityRecord[] records = state.Records;
            if (records.Length == 0) throw new InvalidDataException("Stable populated restore requires nonempty capacity records.");
            foreach (ResidentialCapacityRecord record in records)
                if (record.Phase != ResidentialCapacityPhase.Completed)
                    throw new InvalidDataException("Stable populated restore requires every capacity record Completed.");
            return state;
        }

        private static void RequireSamePair(byte[] expected, byte[] actual)
        {
            byte[] expectedClasses, expectedCapacity, actualClasses, actualCapacity;
            DecodeRequired(expected, out expectedClasses, out expectedCapacity);
            DecodeRequired(actual, out actualClasses, out actualCapacity);
            if (!Same(expectedClasses, actualClasses)) throw new InvalidDataException("Authoritative exact class payload changed during native save/reload.");
            if (!Same(expectedCapacity, actualCapacity)) throw new InvalidDataException("Authoritative exact capacity payload/absence changed during native save/reload.");
            if (!Same(expected, actual)) throw new InvalidDataException("Authoritative envelope bytes changed during native save/reload.");
        }

        private static void RequireLegacyUnchanged(byte[] before, byte[] after)
        {
            if (!Same(before, after)) throw new InvalidDataException("Optional legacy class key was created, removed or modified by native save/reload.");
        }
        private static bool Same(byte[] first, byte[] second)
        {
            if (first == null || second == null) return first == second;
            if (first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false;
            return true;
        }
    }
}
