using System;
using System.Threading;
using ColossalFramework;
using ColossalFramework.Math;
using SC4Evolution.Simulation;

namespace SC4Evolution.Mod.Economy
{
    // Captured entirely inside the existing single simulation action, before
    // fixture offsets/allocations. Never reconstruct these values on the UI thread.
    internal sealed class QaIncomingDemandReceipt
    {
        internal bool Verified, QueryAttempted, GetterReturned, RngUnchanged;
        internal string Error;
        internal IncomingDemandSnapshot Before, After;
        internal int GetterResult, ProbeThreadId;
        internal uint FrameBefore, FrameAfter, CensusFrame;
        internal ulong RngSeedBefore, RngSeedAfter;
        internal long CashBefore, CashAfter, LastCashBefore, LastCashAfter, DateTicksBefore, DateTicksAfter;
        internal bool PublicationConverged, PublicationGameplayEnabled;
        internal int PublicationPasses;
        internal DemandNodeState[] ResidentialNodes = new DemandNodeState[0];
    }

    public sealed partial class EconomicBridge
    {
        internal QaIncomingDemandReceipt ProbeQaIncomingResidentDemand(QaCityLease lease)
        {
            QaIncomingDemandReceipt receipt = new QaIncomingDemandReceipt();
            lock (_sync)
            {
                try
                {
                    RequireQaLease(lease, false);
                    receipt.ProbeThreadId = Thread.CurrentThread.ManagedThreadId;
                    if (lease.Offsets[0] != 0 || lease.Offsets[1] != 0 || lease.Offsets[2] != 0 || lease.Constructions.Count != 0)
                        throw new InvalidOperationException("Incoming demand proof must precede fixture offsets and construction.");
                    IncomingDemandBinding binding = _demandContextBinding;
                    receipt.Before = IncomingDemandDiagnostics;
                    RequireQaIncomingBinding(receipt.Before, lease);
                    if (!_aggregate || !_incomingPatchesInstalled || receipt.Before.CurrentDepth != 0)
                        throw new InvalidOperationException("Incoming demand proof requires the active provider patchset and an empty native demand scope.");
                    SegmentedDemandSnapshot publication = _published;
                    if (!CanUseDemand(publication) || publication.Census == null)
                        throw new InvalidOperationException("Incoming demand proof has no current usable publication.");
                    receipt.CensusFrame = publication.Census.Frame;
                    receipt.PublicationConverged = publication.Converged;
                    receipt.PublicationGameplayEnabled = publication.GameplayEnabled;
                    receipt.PublicationPasses = publication.PropagationPasses;
                    DemandNodeState[] nodes = publication.Nodes;
                    receipt.ResidentialNodes = new DemandNodeState[3];
                    for (int i = 0; i < 3; i++)
                    {
                        uint key = (uint)(0x21010 + i * 0x10);
                        bool found = false;
                        foreach (DemandNodeState node in nodes) if (node.Key == key)
                        {
                            if (found || float.IsNaN(node.ActiveDemand) || float.IsInfinity(node.ActiveDemand))
                                throw new InvalidOperationException("Incoming demand publication has invalid residential nodes.");
                            receipt.ResidentialNodes[i] = node; found = true;
                        }
                        if (!found) throw new InvalidOperationException("Incoming demand publication is missing a residential class.");
                    }
                    SimulationManager simulation = Singleton<SimulationManager>.instance;
                    EconomyManager economy = Singleton<EconomyManager>.instance;
                    RequireQaLease(lease, false);
                    Randomizer rngBefore = simulation.m_randomizer;
                    receipt.FrameBefore = simulation.m_currentFrameIndex;
                    receipt.RngSeedBefore = rngBefore.seed;
                    receipt.CashBefore = economy.InternalCashAmount;
                    receipt.LastCashBefore = economy.LastCashAmount;
                    receipt.DateTicksBefore = simulation.m_metaData.m_currentDateTime.Ticks;
                    receipt.QueryAttempted = true;
                    try
                    {
                        // Real public native entry point. Its calculator prefix and
                        // finalizer must establish/restore the scope themselves.
                        receipt.GetterResult = Singleton<ZoneManager>.instance.GetIncomingResidentDemand();
                        receipt.GetterReturned = true;
                    }
                    finally
                    {
                        receipt.After = IncomingDemandDiagnostics;
                        receipt.FrameAfter = simulation.m_currentFrameIndex;
                        Randomizer rngAfter = simulation.m_randomizer;
                        receipt.RngSeedAfter = rngAfter.seed;
                        receipt.RngUnchanged = rngBefore.Equals(rngAfter);
                        receipt.CashAfter = economy.InternalCashAmount;
                        receipt.LastCashAfter = economy.LastCashAmount;
                        receipt.DateTicksAfter = simulation.m_metaData == null ? -1 : simulation.m_metaData.m_currentDateTime.Ticks;
                    }
                    RequireQaLease(lease, false);
                    RequireQaIncomingBinding(receipt.After, lease);
                    if (!ReferenceEquals(binding, _demandContextBinding) || !ReferenceEquals(publication, _published) || !CanUseDemand(publication))
                        throw new InvalidOperationException("Incoming demand binding/publication changed during the native query.");
                    IncomingDemandSnapshot before = receipt.Before, after = receipt.After;
                    if (after.IncomingEntries <= before.IncomingEntries || after.PreservedCallbacks <= before.PreservedCallbacks ||
                        after.Finalizers <= before.Finalizers || after.Mismatches != before.Mismatches ||
                        after.CurrentDepth != before.CurrentDepth || after.LastKind != ResidentialDemandKind.Incoming ||
                        after.LastCallbackThreadId != receipt.ProbeThreadId || after.LastFinalizerThreadId != receipt.ProbeThreadId ||
                        after.LastAggregated || after.LastInput != after.LastReturn)
                        throw new InvalidOperationException("Native incoming demand query did not verify a preserved callback and restored finalizer scope.");
                    if (receipt.FrameBefore != receipt.FrameAfter || !receipt.RngUnchanged || receipt.CashBefore != receipt.CashAfter ||
                        receipt.LastCashBefore != receipt.LastCashAfter || receipt.DateTicksBefore != receipt.DateTicksAfter)
                        throw new InvalidOperationException("Native incoming demand query changed frame, RNG, cash or serialized date; no repair was attempted.");
                    receipt.Verified = true;
                }
                catch (Exception error) { receipt.Error = error.ToString(); }
            }
            return receipt;
        }

        private void RequireQaIncomingBinding(IncomingDemandSnapshot snapshot, QaCityLease lease)
        {
            if (snapshot == null || !snapshot.BindingValid || !ReferenceEquals(snapshot.Owner, this) || snapshot.Session != lease.Generation)
                throw new InvalidOperationException("Incoming demand diagnostics are absent, invalid or bound to another owner/session.");
        }
    }
}
