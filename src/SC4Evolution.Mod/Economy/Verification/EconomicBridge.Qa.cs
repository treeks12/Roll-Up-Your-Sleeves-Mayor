using System;
using System.Collections.Generic;
using System.Threading;
using ColossalFramework;
using ColossalFramework.Math;
using SC4Evolution.Simulation;
using UnityEngine;

namespace SC4Evolution.Mod.Economy
{
    internal sealed class QaCityLease
    {
        internal EconomicBridge Bridge;
        internal int Generation, ThreadId;
        internal string NativeSession;
        internal Func<bool> Execute, Cleanup;
        internal readonly float[] Offsets = new float[3];
        internal readonly List<QaConstructionReceipt> Constructions = new List<QaConstructionReceipt>();
    }

    internal sealed class QaDemandReceipt
    {
        internal float[] Before, Desired, Delta;
        internal SegmentedDemandSnapshot Snapshot;
        internal int SampleAttempts;
    }

    internal sealed class QaConstructionReceipt
    {
        internal ushort Building;
        internal bool NativeSuccess;
        internal BuildingInfo Prefab;
        internal Vector3 Position;
        internal uint BuildIndex;
        internal ResidentialLotIdentity Identity;
        internal SegmentedDemandSnapshot Snapshot;
    }

    public sealed partial class EconomicBridge
    {
        private QaCityLease _qaLease;

        internal QaCityLease BeginQaResidentialSession(string nativeSession, Func<bool> execute, Func<bool> cleanup)
        {
            RequireQaSimulationThread();
            lock (_sync)
            {
                if (execute == null || cleanup == null || !execute() || !cleanup())
                    throw new InvalidOperationException("Residential QA authorization rejected.");
                if (_qaLease != null && _qaLease.Generation == _session)
                    throw new InvalidOperationException("Residential QA already claimed this bridge generation.");
                RequireQaPolicy();
                if (!OwnsQaNativeSession(nativeSession)) throw new InvalidOperationException("Residential QA native session mismatch.");
                _qaLease = new QaCityLease { Bridge = this, Generation = _session,
                    ThreadId = Thread.CurrentThread.ManagedThreadId, NativeSession = nativeSession,
                    Execute = execute, Cleanup = cleanup };
                return _qaLease;
            }
        }

        internal QaDemandReceipt SetQaResidentialDemandOffsets(QaCityLease lease, float low, float medium, float high)
        {
            lock (_sync)
            {
                RequireQaLease(lease, false);
                if (!((low == 0 && medium == 0 && high == 0) || (low == 1000 && medium == 0 && high == 0) ||
                    (low == 0 && medium == 0 && high == 1000)))
                    throw new ArgumentOutOfRangeException("low", "Only the approved three residential QA vectors are supported.");
                return ApplyQaOffsets(lease, new float[] { low, medium, high }, false);
            }
        }

        internal QaDemandReceipt ClearQaResidentialDemandOffsets(QaCityLease lease)
        {
            lock (_sync)
            {
                RequireQaLease(lease, true);
                return ApplyQaOffsets(lease, new float[3], true);
            }
        }

        private QaDemandReceipt ApplyQaOffsets(QaCityLease lease, float[] desired, bool cleanup)
        {
            if (_graph == null) throw new InvalidOperationException("Residential QA graph unavailable; offset removal cannot be verified.");
            DemandGraph operationGraph = _graph;
            SegmentedDemandSnapshot previousPublication = _published;
            float[] before = (float[])lease.Offsets.Clone(), delta = new float[3];
            for (int i = 0; i < 3; i++) delta[i] = desired[i] - before[i];
            try
            {
                // No prior controller may remain visible while this operation has
                // queued changes which have not yet been propagated/published.
                _published = null;
                // Fixed profile keys and bounded fixture vectors cannot be supplied by a caller.
                for (int i = 0; i < 3; i++)
                {
                    RequireQaLease(lease, cleanup);
                    if (delta[i] != 0) _graph.QueueDemandChange((uint)(0x21010 + i * 0x10), delta[i]);
                    // Retain what was actually queued even if a later operation fails.
                    lease.Offsets[i] = desired[i];
                }
                // Each shared sample may spend its entire 4096-item maintenance
                // budget without publishing. Complete that bounded work inside this
                // same native action, without changing cursors or advancing the world.
                CitizenManager citizens = Singleton<CitizenManager>.instance;
                int maximumSamples = checked(8 + (int)(((long)citizens.m_citizens.m_buffer.Length +
                    5L * citizens.m_units.m_buffer.Length + 4095L) / 4096L));
                int attempts = 0;
                while (_published == null && ReferenceEquals(_graph, operationGraph) && attempts < maximumSamples)
                {
                    RequireQaLease(lease, cleanup);
                    attempts++;
                    SampleLocked();
                }
                SegmentedDemandSnapshot snapshot = _published;
                SimulationManager simulation = Singleton<SimulationManager>.instance;
                if (!ReferenceEquals(_graph, operationGraph) || snapshot == null || ReferenceEquals(snapshot, previousPublication) ||
                    snapshot.Census == null || (previousPublication != null && ReferenceEquals(snapshot.Census, previousPublication.Census)) ||
                    snapshot.Census.Frame != simulation.m_currentFrameIndex ||
                    !snapshot.Converged || snapshot.PropagationPasses < 1 || (!cleanup && !CanUseDemand(snapshot)))
                    throw new InvalidOperationException("Residential QA queued offsets but did not verify a fresh census/graph publication; propagation is unconfirmed.");
                if (cleanup && !lease.Execute())
                {
                    // An interrupted fixture may withdraw its stimulus, never republish a gameplay permission.
                    snapshot = new SegmentedDemandSnapshot(snapshot.Census, snapshot.Nodes, true, false, snapshot.PropagationPasses);
                    _published = snapshot;
                }
                return new QaDemandReceipt { Before = before, Desired = (float[])desired.Clone(), Delta = delta,
                    Snapshot = snapshot, SampleAttempts = attempts };
            }
            catch
            {
                // Queued offsets are not proof of propagation. Never leave the old valid
                // snapshot controlling construction after a deferred/failed QA sample.
                // Recorded offsets remain available for an owned cleanup withdrawal.
                _published = null;
                throw;
            }
        }

        internal QaConstructionReceipt ConstructQaResidentialCandidate(QaCityLease lease, BuildingInfo prefab,
            Vector3 position, float angle, int length, ref Randomizer randomizer)
        {
            lock (_sync)
            {
                RequireQaLease(lease, false);
                RequireQaPolicy();
                if (prefab == null || prefab.m_class == null || prefab.m_class.m_service != ItemClass.Service.Residential ||
                    prefab.m_class.m_subService != ItemClass.SubService.ResidentialLow || prefab.m_class.m_level != ItemClass.Level.Level1 ||
                    prefab.m_isCustomContent || !(prefab.m_buildingAI is ResidentialBuildingAI) ||
                    !Finite(position.x) || !Finite(position.y) || !Finite(position.z) || !Finite(angle) || length != prefab.m_cellLength)
                    throw new InvalidOperationException("Residential QA candidate rejected.");
                if (!CanUseDemand(_published)) throw new InvalidOperationException("Residential QA construction has no current gameplay snapshot.");
                QaConstructionReceipt receipt = new QaConstructionReceipt { Prefab = prefab, Position = position,
                    BuildIndex = Singleton<SimulationManager>.instance.m_currentBuildIndex, Snapshot = _published };
                ushort building = 0;
                try
                {
                    RequireQaLease(lease, false);
                    receipt.NativeSuccess = NewLotGrowthGate.CreateEligibleBuilding(Singleton<BuildingManager>.instance,
                        out building, ref randomizer, prefab, position, angle, length, receipt.BuildIndex);
                    if (receipt.NativeSuccess) Singleton<SimulationManager>.instance.m_currentBuildIndex++;
                    return receipt;
                }
                finally
                {
                    // Also expose an allocation when native/another patch throws after assigning its ID.
                    receipt.Building = building;
                    lease.Constructions.Add(receipt);
                    ResidentialLotIdentity identity;
                    if (building != 0 && ResidentialClassBridge.TryIdentity(building, out identity)) receipt.Identity = identity;
                }
            }
        }

        private void RequireQaLease(QaCityLease lease, bool cleanup)
        {
            RequireQaSimulationThread();
            if (lease == null || !ReferenceEquals(lease, _qaLease) || !ReferenceEquals(lease.Bridge, this) ||
                lease.Generation != _session || lease.ThreadId != Thread.CurrentThread.ManagedThreadId || !_running ||
                !OwnsQaNativeSession(lease.NativeSession) || !(cleanup ? lease.Cleanup() : lease.Execute()))
                throw new InvalidOperationException("Residential QA lease expired or ownership changed.");
            if (!cleanup) RequireQaPolicy();
        }

        private void RequireQaPolicy()
        {
            ResidentialClassStatus status = PublishedClassStatus;
            if (!_running || !_initialComplete || !_lotClass || _graph == null || _classes == null || !_classes.EffectsEnabled ||
                status == null || !status.Enabled || status.EffectivePolicy != "NativeSafetyOnly")
                throw new InvalidOperationException("Residential QA requires active NativeSafetyOnly lot-class effects.");
        }

        private static bool OwnsQaNativeSession(string expected)
        {
            return !string.IsNullOrEmpty(expected) && Singleton<SimulationManager>.exists &&
                Singleton<SimulationManager>.instance.m_metaData != null &&
                Singleton<SimulationManager>.instance.m_metaData.m_gameInstanceIdentifier == expected;
        }
        private static void RequireQaSimulationThread()
        {
            if (!Singleton<SimulationManager>.exists || !ReferenceEquals(Thread.CurrentThread, Singleton<SimulationManager>.instance.m_simulationThread))
                throw new InvalidOperationException("Residential QA must execute on the native simulation thread.");
        }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
