using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ColossalFramework;
using SC4Evolution.Mod.Economy;
using SC4Evolution.Mod.Runtime;
using SC4Evolution.Simulation;
using UnityEngine;

namespace SC4Evolution.Mod.Diagnostics
{
    public sealed partial class AutonomousTestDriver
    {
        private sealed partial class NativeOrganicResidentialProbe
        {
            private bool _handoffRequested, _handoffFrozen, _handoffReloadArmed, _handoffReloadSeen, _handoffAdopted, _roundtripUnloadAcknowledged;
            private NativeSaveRoundtrip _reloadPermitOwner;
            private string _reloadPermitPath, _reloadPermitHash;
            private EconomicBridge _checkpointOwner;
            private ResidentialCapacityBridge _checkpointCapacity;
            private EconomicBridge _restoredOwner;
            private ResidentialCapacityBridge _restoredCapacity;
            private int _checkpointEpoch, _restoredEpoch, _initialOwnerEpoch;
            private string _fixtureFingerprint, _fixtureFingerprintHash, _initialClassHash;
            private int _auditedHomes, _auditedHomeUnits, _auditedPeople;
            private uint _freshAuditFrame, _publishedAuditFrame;

            public bool CheckpointReady { get { lock (_gate) return _handoffRequested && _verified && !_pending && !_interrupted && !_done; } }
            public bool OwnedReloadWasObserved { get { return _handoffReloadSeen; } }
            public bool ConsumeRoundtripUnloadAcknowledgment()
            { if (!_handoffReloadSeen || _roundtripUnloadAcknowledged) return false; _roundtripUnloadAcknowledged = true; return true; }

            // Called only after the roundtrip acquired its forced pause, in the
            // SAME simulation action. No native step separates these leases.
            public void FreezeForSave()
            {
                lock (_gate)
                {
                    Guard(); RequireSimPause();
                    if (!_handoffRequested || !_verified || !_capacityAdmissionVerified || _pending || _handoffFrozen)
                        throw new InvalidOperationException("No unique verified organic checkpoint is ready for handoff.");
                    if (!_speedHeld || _simulation.SelectedSimulationSpeed != 3 || _simulation.SimulationPaused)
                        throw new InvalidOperationException("Organic speed/pause lease changed before paused handoff.");
                    _checkpointOwner = _bridge; _checkpointCapacity = _bridge.Capacity;
                    _checkpointEpoch = _bridge.DemandContextSession;
                    ReleaseSpeedLease(); _handoffFrozen = true;
                    Detach(); // Roundtrip subscribed on MAIN before queuing this action.
                    SetPhase("OrganicFixtureFrozenForNativeSave");
                }
            }

            private void RequireSimPause()
            {
                if (!OwnsCity() || !ReferenceEquals(Thread.CurrentThread, _simulation.m_simulationThread) || !_simulation.ForcedSimulationPaused)
                    throw new InvalidOperationException("Fixture audit requires the owned paused simulation thread.");
            }

            public void AuditPausedFixture(byte[] envelope, bool restored)
            {
                lock (_gate)
                {
                    RequireSimPause();
                    if (!_handoffFrozen || _interrupted || _cancelled) throw new InvalidOperationException("Organic handoff is inactive or cancelled.");
                    EconomicBridge bridge = ModRuntime.CurrentEconomicBridge;
                    if (bridge == null || bridge.Capacity == null || !bridge.InitialSampleComplete)
                        throw new InvalidOperationException("Restored economic baseline is not complete.");
                    ResidentialCapacityBridge auditCapacity = bridge.Capacity; int auditEpoch = bridge.DemandContextSession;
                    if (!restored)
                    {
                        Guard();
                        if (!ReferenceEquals(bridge, _checkpointOwner) || !ReferenceEquals(bridge.Capacity, _checkpointCapacity)
                            || bridge.DemandContextSession != _checkpointEpoch) throw new InvalidOperationException("Original checkpoint owner changed.");
                    }
                    else if (!_handoffReloadSeen || ReferenceEquals(bridge, _checkpointOwner) || ReferenceEquals(bridge.Capacity, _checkpointCapacity))
                        throw new InvalidOperationException("Owned reload did not establish a new economic owner.");
                    if (restored && _restoredOwner != null && (!ReferenceEquals(bridge, _restoredOwner) || !ReferenceEquals(bridge.Capacity, _restoredCapacity)
                        || bridge.DemandContextSession != _restoredEpoch))
                        throw new InvalidOperationException("Restored checkpoint owner changed before explicit fixture adoption.");
                    // This is an additional observation, not a graph sample. The
                    // production publisher has a1024-frame cadence and may have
                    // an older frame with the exact same applied inputs.
                    CityCensus fresh = CityCensus.Capture(bridge.Classes);
                    if (!SameFixtureBytes(envelope, ModRuntime.GetEconomicSaveEnvelope()))
                        throw new InvalidDataException("Observational census changed authoritative economic bytes.");
                    ResidentialCapacityStatus status = bridge.Capacity.Published;
                    ResidentialClassStatus classStatus = ModRuntime.CurrentResidentialClasses;
                    if (!bridge.Capacity.ModeRequested || !bridge.Capacity.Ready || status == null || !status.Ready || !status.HooksReady || !status.CompatibilityKnown
                        || status.UnknownBuildings != 0 || status.PendingReservations != 0 || status.UnresolvedCreations != 0
                        || status.PendingMutations != 0 || status.UnresolvedMutations != 0 || bridge.Capacity.SessionEpoch != bridge.DemandContextSession
                        || classStatus == null || !classStatus.Enabled || classStatus.UnboundResidents != 0)
                        throw new InvalidDataException("RAW baseline/publication/hooks or physical-work barriers are not certified at the fixture checkpoint.");
                    byte[] classes, capacity;
                    EconomicSaveRoundtripOracle.DecodeRequired(envelope, out classes, out capacity);
                    if (capacity == null) throw new InvalidDataException("Populated roundtrip requires retained capacity.");
                    ResidentialClassLedger ledger; ResidentialCapacityLedgerState state;
                    using (MemoryStream stream = new MemoryStream(classes, false)) ledger = ResidentialClassLedgerCodec.Read(stream);
                    using (MemoryStream stream = new MemoryStream(capacity, false)) state = ResidentialCapacityLedgerCodec.ReadBound(stream, classes);
                    if (state.Records.Length == 0 || state.StockRevision != status.StockRevision || state.ExternalRevision != status.ExternalRevision
                        || state.PublishedStockRevision != state.StockRevision || state.PublishedExternalRevision != state.ExternalRevision)
                        throw new InvalidDataException("Populated capacity checkpoint has no exact converged publication.");
                    long[] nominal = new long[3], publishedQ = status.StockQ;
                    foreach (ResidentialCapacityRecord record in state.Records) if (record.IsActive) nominal[(int)record.Class - 1] = checked(nominal[(int)record.Class - 1] + record.CapacityQ);
                    if (publishedQ.Length != 3 || nominal[0] != publishedQ[0] || nominal[1] != publishedQ[1] || nominal[2] != publishedQ[2])
                        throw new InvalidDataException("Published nominal stock differs from the complete saved record set.");
                    string fingerprint = AuditAllNativeSlots(ledger, state, fresh);
                    if (!ReferenceEquals(bridge, ModRuntime.CurrentEconomicBridge) || !ReferenceEquals(auditCapacity, bridge.Capacity)
                        || auditEpoch != bridge.DemandContextSession) throw new InvalidOperationException("Fixture audit owner changed during its paused observation.");
                    if (_fixtureFingerprint == null)
                    {
                        if (restored) throw new InvalidOperationException("Missing original organic fixture manifest.");
                        _fixtureFingerprint = fingerprint; _fixtureFingerprintHash = FingerprintHash(fingerprint); _initialClassHash = FingerprintHash(classes);
                        byte[] manifest = Encoding.UTF8.GetBytes(fingerprint);
                        using (FileStream file = new FileStream(Path.Combine(_owner._directory, "organic-fixture.manifest.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            file.Write(manifest, 0, manifest.Length);
                    }
                    else if (!string.Equals(_fixtureFingerprint, fingerprint, StringComparison.Ordinal) || _initialClassHash != FingerprintHash(classes))
                        throw new InvalidDataException("Paused organic fixture/class bytes changed; no resource adoption or repair is authorized.");
                    if (restored) { _restoredOwner = bridge; _restoredCapacity = bridge.Capacity; _restoredEpoch = bridge.DemandContextSession; }
                    Add("{\"phase\":\"PausedOrganicFixtureAudit\",\"restored\":" + Bool(restored) + ",\"frame\":" + _simulation.m_currentFrameIndex
                        + ",\"ownerEpoch\":" + bridge.DemandContextSession + ",\"newOwnerReference\":" + Bool(!ReferenceEquals(bridge, _checkpointOwner))
                        + ",\"freshObservedFrame\":" + _freshAuditFrame + ",\"publishedInputFrame\":" + _publishedAuditFrame + ",\"freshCaptureIsAdditionalObservation\":true,\"appliedInputsExactlyEqual\":true"
                        + ",\"allResidentialSlots\":" + _auditedHomes + ",\"homeUnits\":" + _auditedHomeUnits + ",\"people\":" + _auditedPeople
                        + ",\"fixtureSha256\":" + Json(_fixtureFingerprintHash) + ",\"classSha256\":" + Json(_initialClassHash) + ",\"allCompleted\":true}");
                }
            }

            private string AuditAllNativeSlots(ResidentialClassLedger ledger, ResidentialCapacityLedgerState state, CityCensus fresh)
            {
                Dictionary<ushort, ResidentialCapacityRecord> records = new Dictionary<ushort, ResidentialCapacityRecord>();
                foreach (ResidentialCapacityRecord record in state.Records)
                {
                    if (record.Phase != ResidentialCapacityPhase.Completed || records.ContainsKey(record.Identity.BuildingId))
                        throw new InvalidDataException("The narrow roundtrip requires distinct, Completed capacity records only.");
                    records.Add(record.Identity.BuildingId, record);
                }
                BuildingManager bm = Singleton<BuildingManager>.instance; NetManager net = Singleton<NetManager>.instance;
                CitizenManager citizens = Singleton<CitizenManager>.instance;
                StringBuilder value = new StringBuilder("OrganicFixture.v1;"); int homes = 0, units = 0, people = 0; bool qualified = false;
                HashSet<uint> allMembers = new HashSet<uint>(), allUnits = new HashSet<uint>();
                for (int i = 1; i < bm.m_buildings.m_buffer.Length; ++i)
                {
                    ref Building building = ref bm.m_buildings.m_buffer[i]; BuildingInfo info = building.Info;
                    if ((building.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created
                        || info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.Residential) continue;
                    if (++homes > 128 || !_buildings.ContainsKey((ushort)i)) throw new InvalidDataException("Unexpected native residential slot outside the bounded owned manifest.");
                    ResidentialCapacityObservation observation = ResidentialCapacityObserver.Observe((ushort)i, ref building);
                    ResidentialCapacityRecord record; ResidentialLotRecord selected;
                    if (observation.Status != ResidentialCapacityObservationStatus.Known || observation.Phase != ResidentialCapacityObservationPhase.Completed
                        || !records.TryGetValue((ushort)i, out record) || !ledger.TryGet(observation.Identity, out selected)
                        || !SameLotIdentity(record.Identity, observation.Identity) || record.Generation != selected.Generation || record.Class != selected.OccupantClass
                        || record.Origin != selected.Origin || record.Category != observation.Category || record.NativeLevel != observation.NativeLevel
                        || record.HomeCount != observation.ObservedHomeUnits || record.CapacityQ != observation.CapacityQ)
                        throw new InvalidDataException("Native slot/class/capacity identity or nominal HOME observation differs: " + i);
                    bool power, water, sewage; byte pollution;
                    Singleton<ElectricityManager>.instance.CheckElectricity(building.m_position, out power);
                    Singleton<WaterManager>.instance.CheckWater(building.m_position, out water, out sewage, out pollution);
                    if (!power || !water || !sewage || building.m_accessSegment == 0 || !_zoningRoads.Contains(building.m_accessSegment))
                        throw new InvalidDataException("A populated checkpoint home lacks genuine utilities/fixture road access: " + i);
                    NetSegment access = net.m_segments.m_buffer[building.m_accessSegment];
                    if ((access.m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) != NetSegment.Flags.Created
                        || FindOutside(access.m_startNode) == 0 || FindOutside(access.m_endNode) == 0)
                        throw new InvalidDataException("Residential access lost its actual outside connection.");
                    value.Append("H:").Append(i).Append(':').Append(record.Generation).Append(':').Append((int)record.Class).Append(':')
                        .Append((int)record.Origin).Append(':').Append(record.HomeCount).Append(':').Append(record.CapacityQ).Append(':').Append(building.m_accessSegment).Append(';');
                    uint unit = building.m_citizenUnits; HashSet<uint> seen = new HashSet<uint>(); bool familyFound = false;
                    List<string> families = new List<string>();
                    while (unit != 0)
                    {
                        if (seen.Count >= 4096 || unit >= citizens.m_units.m_buffer.Length || !seen.Add(unit) || !allUnits.Add(unit)) throw new InvalidDataException("HOME chain is invalid at paused fixture audit.");
                        CitizenUnit home = citizens.m_units.m_buffer[unit];
                        if ((home.m_flags & (CitizenUnit.Flags.Created | CitizenUnit.Flags.Home)) != (CitizenUnit.Flags.Created | CitizenUnit.Flags.Home) || home.m_building != i)
                            throw new InvalidDataException("HOME unit is not bound to this native lot.");
                        ++units; int settled = 0, family = -1; bool coherent = true; List<string> settledIds = new List<string>();
                        value.Append("U:").Append(unit).Append(':').Append(home.m_nextUnit).Append(':').Append((uint)home.m_flags).Append(';');
                        for (int slot = 0; slot < 5; ++slot)
                        {
                            uint id = home.GetCitizen(slot); value.Append("M:").Append(slot).Append(':').Append(id).Append(';'); if (id == 0) continue;
                            if (id >= citizens.m_citizens.m_buffer.Length || !allMembers.Add(id)) throw new InvalidDataException("Duplicate or invalid native HOME citizen.");
                            Citizen citizen = citizens.m_citizens.m_buffer[id];
                            if ((citizen.m_flags & Citizen.Flags.Created) == 0 || citizen.m_homeBuilding != i || (int)citizen.WealthLevel + 1 != (int)record.Class)
                                throw new InvalidDataException("Native HOME member/class mirror differs.");
                            ++people; value.Append("C:").Append(citizen.m_family).Append(':').Append((uint)(citizen.m_flags & (Citizen.Flags.Created | Citizen.Flags.Dead | Citizen.Flags.MovingIn)))
                                .Append(':').Append((int)citizen.WealthLevel).Append(';');
                            if ((citizen.m_flags & (Citizen.Flags.Dead | Citizen.Flags.MovingIn)) == 0)
                            { ++settled; settledIds.Add(id.ToString(CultureInfo.InvariantCulture)); if (family < 0) family = citizen.m_family; else if (family != citizen.m_family) coherent = false; }
                        }
                        if (settled >= 2 && coherent)
                        {
                            familyFound = true;
                            families.Add("{\"homeUnit\":" + unit + ",\"nativeFamily\":" + family + ",\"uniqueSettledCitizenIds\":[" + string.Join(",", settledIds.ToArray()) + "]}");
                        }
                        unit = home.m_nextUnit;
                    }
                    if (i == _qualifiedBuilding && building.m_buildIndex == _qualifiedBuildIndex && (int)record.Class == _qualifiedClass
                        && record.Origin == ClassOrigin.Selected && familyFound)
                    {
                        string familySignature = i + ":" + building.m_buildIndex + ":" + selected.Generation + ":" + selected.OccupantClass + ":" + string.Join(",", families.ToArray());
                        if (familySignature != _qualifiedSignature) throw new InvalidDataException("The original organically qualified native family changed before/during the roundtrip.");
                        qualified = true;
                    }
                }
                if (homes != records.Count || ledger.Count != records.Count || !qualified)
                    throw new InvalidDataException("Native residential slot coverage, saved records or original qualified family is incomplete.");
                // Forward chains alone cannot prove coverage. Reject orphan HOME
                // units and Created citizens that claim a fixture home but have no
                // corresponding unique chain/member slot.
                RequireInverseHomeCoverage(new HashSet<ushort>(records.Keys), allUnits, allMembers, citizens.m_units.m_buffer, citizens.m_citizens.m_buffer);
                var demand = ModRuntime.CurrentDemand;
                Add("{\"phase\":\"PausedCensusInputComparison\",\"freshFrame\":" + fresh.Frame + ",\"publishedFrame\":" + (demand == null || demand.Census == null ? "null" : demand.Census.Frame.ToString(CultureInfo.InvariantCulture))
                    + ",\"additionalObservationOnly\":true,\"fresh\":" + AppliedInputsJson(fresh) + ",\"published\":" + AppliedInputsJson(demand == null ? null : demand.Census) + "}");
                if (demand == null || !demand.Converged || !demand.GameplayEnabled || demand.Census == null || fresh == null || fresh.Frame != _simulation.m_currentFrameIndex
                    || demand.Census.Frame > fresh.Frame || fresh.UnmappedJobCapacity != 0 || fresh.UnmappedFilledJobs != 0
                    || demand.Census.UnmappedJobCapacity != 0 || demand.Census.UnmappedFilledJobs != 0
                    || !AllZero(fresh.UnboundResidentsByNativeWealth) || !AllZero(fresh.UnboundWorkforceByNativeWealthEducation)
                    || !AllZero(demand.Census.UnboundResidentsByNativeWealth) || !AllZero(demand.Census.UnboundWorkforceByNativeWealthEducation)
                    || !SameInputs(fresh.ResidentsByLotClass, demand.Census.ResidentsByLotClass)
                    || !SameInputs(fresh.WorkforceByLotClassEducation, demand.Census.WorkforceByLotClassEducation)
                    || !SameInputs(fresh.JobCapacityBySegment, demand.Census.JobCapacityBySegment) || !SameInputs(fresh.FilledJobsBySegment, demand.Census.FilledJobsBySegment))
                    throw new InvalidDataException("Fresh paused census differs from the exact converged RAW publication inputs; no resampling or graph repair is applied.");
                ResidentialStockSnapshot stock = fresh.ResidentialStock, publishedStock = demand.Census.ResidentialStock;
                if (stock == null || stock.Frame != _simulation.m_currentFrameIndex || stock.Totals.Lots != homes || stock.Totals.HomeUnits != units
                    || stock.Totals.InvalidMemberReferences != 0 || stock.Totals.PeopleWithoutHomeUnitReference != 0
                    || publishedStock == null || publishedStock.Frame != demand.Census.Frame || publishedStock.Totals.InvalidMemberReferences != 0 || publishedStock.Totals.PeopleWithoutHomeUnitReference != 0
                    || stock.Totals.Lots != publishedStock.Totals.Lots || stock.Totals.HomeUnits != publishedStock.Totals.HomeUnits
                    || stock.Totals.EmptyHomeUnits != publishedStock.Totals.EmptyHomeUnits || stock.Totals.LiveHomeUnits != publishedStock.Totals.LiveHomeUnits
                    || stock.Totals.OtherNonemptyHomeUnits != publishedStock.Totals.OtherNonemptyHomeUnits || stock.Totals.SettledPeople != publishedStock.Totals.SettledPeople
                    || stock.Totals.IncomingPeople != publishedStock.Totals.IncomingPeople)
                    throw new InvalidDataException("Published native residential stock does not cover the exact HOME fixture without orphan references.");
                _freshAuditFrame = fresh.Frame; _publishedAuditFrame = demand.Census.Frame;
                AppendCensus(value, fresh.ResidentsByLotClass); AppendCensus(value, fresh.WorkforceByLotClassEducation);
                AppendCensus(value, fresh.JobCapacityBySegment); AppendCensus(value, fresh.FilledJobsBySegment);
                AppendResourceManifest(value, bm, net);
                if (value.Length > 2 * 1024 * 1024) throw new InvalidDataException("Bounded organic fixture manifest exceeds2MiB.");
                _auditedHomes = homes; _auditedHomeUnits = units; _auditedPeople = people; return value.ToString();
            }
            private static void RequireInverseHomeCoverage(HashSet<ushort> homes, HashSet<uint> reachedUnits, HashSet<uint> reachedMembers, CitizenUnit[] units, Citizen[] citizens)
            {
                for (uint id = 1; id < units.Length; ++id)
                {
                    CitizenUnit unit = units[id];
                    if ((unit.m_flags & (CitizenUnit.Flags.Created | CitizenUnit.Flags.Home)) == (CitizenUnit.Flags.Created | CitizenUnit.Flags.Home)
                        && homes.Contains(unit.m_building) && !reachedUnits.Contains(id))
                        throw new InvalidDataException("Orphan native HOME unit points into the fixture: " + id);
                }
                for (uint id = 1; id < citizens.Length; ++id)
                {
                    Citizen citizen = citizens[id];
                    if ((citizen.m_flags & Citizen.Flags.Created) != 0 && homes.Contains(citizen.m_homeBuilding) && !reachedMembers.Contains(id))
                        throw new InvalidDataException("Native fixture resident has no unique HOME member slot: " + id);
                }
            }

            private void AppendResourceManifest(StringBuilder value, BuildingManager bm, NetManager net)
            {
                List<ushort> ids = new List<ushort>(_buildings.Keys); ids.Sort();
                foreach (ushort id in ids)
                {
                    Building b = bm.m_buildings.m_buffer[id]; BuildingStamp stamp = _buildings[id];
                    if ((b.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created || b.Info == null
                        || b.m_buildIndex != stamp.BuildIndex || b.m_position != stamp.Position || (!_handoffReloadSeen && b.Info != stamp.Info))
                        throw new InvalidDataException("Owned building stamp changed: " + id);
                    value.Append("B:").Append(id).Append(':').Append(b.m_buildIndex).Append(':').Append(Json(b.Info.name)).Append(':'); AppendPosition(value, b.m_position);
                    AppendBuildingPrefabContract(value, b.Info); value.Append(b.Width).Append(':').Append(b.Length).Append(';');
                }
                ids = new List<ushort>(_segments.Keys); ids.Sort();
                foreach (ushort id in ids)
                {
                    NetSegment s = net.m_segments.m_buffer[id]; NetStamp stamp = _segments[id];
                    if ((s.m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) != NetSegment.Flags.Created || s.Info == null
                        || s.m_buildIndex != stamp.BuildIndex || s.m_startNode != stamp.Start || s.m_endNode != stamp.End || (!_handoffReloadSeen && s.Info != stamp.Info))
                        throw new InvalidDataException("Owned segment stamp changed: " + id);
                    value.Append("S:").Append(id).Append(':').Append(s.m_buildIndex).Append(':').Append(Json(s.Info.name)).Append(':').Append(s.m_startNode).Append(':').Append(s.m_endNode).Append(';');
                    AppendNetPrefabContract(value, s.Info);
                }
                ids = new List<ushort>(_nodes.Keys); ids.Sort();
                foreach (ushort id in ids)
                {
                    NetNode n = net.m_nodes.m_buffer[id]; NetStamp stamp = _nodes[id];
                    if ((n.m_flags & (NetNode.Flags.Created | NetNode.Flags.Deleted)) != NetNode.Flags.Created || n.Info == null
                        || n.m_buildIndex != stamp.BuildIndex || n.m_position != stamp.Position || (!_handoffReloadSeen && n.Info != stamp.Info))
                        throw new InvalidDataException("Owned node stamp changed: " + id);
                    value.Append("N:").Append(id).Append(':').Append(n.m_buildIndex).Append(':').Append(Json(n.Info.name)).Append(':'); AppendPosition(value, n.m_position);
                    AppendNetPrefabContract(value, n.Info);
                    List<ushort> attached = new List<ushort>();
                    for (int edge = 0; edge < 8; ++edge) if (n.GetSegment(edge) != 0) attached.Add(n.GetSegment(edge));
                    // Native deserialization rebuilds node slots; topology is the
                    // exact set of attached segment IDs, independent of slot order.
                    attached.Sort(); foreach (ushort segment in attached) value.Append(segment).Append(','); value.Append(';');
                }
            }
            private static void AppendBuildingPrefabContract(StringBuilder value, BuildingInfo info)
            {
                if (info.m_class == null || info.m_buildingAI == null || !ReferenceEquals(PrefabCollection<BuildingInfo>.FindLoaded(info.name), info)
                    || !ReferenceEquals(info.m_buildingAI.m_info, info)) throw new InvalidDataException("Owned building prefab is not its coherent native catalog instance.");
                value.Append("BP:").Append(Json(info.GetType().AssemblyQualifiedName)).Append(':').Append(Json(info.m_buildingAI.GetType().AssemblyQualifiedName)).Append(':');
                AppendItemClass(value, info.m_class);
                value.Append(info.m_cellWidth).Append(':').Append(info.m_cellLength).Append(':').Append(Bool(info.m_circular)).Append(':')
                    .Append((int)info.m_placementMode).Append(':').Append((int)info.m_placementStyle).Append(':').Append((int)info.m_zoningMode).Append(':').Append(FloatBits(info.m_placementOffset)).Append(';');
            }
            private static void AppendNetPrefabContract(StringBuilder value, NetInfo info)
            {
                if (info.m_class == null || info.m_netAI == null || !ReferenceEquals(PrefabCollection<NetInfo>.FindLoaded(info.name), info)
                    || !ReferenceEquals(info.m_netAI.m_info, info)) throw new InvalidDataException("Owned network prefab is not its coherent native catalog instance.");
                value.Append("NP:").Append(Json(info.GetType().AssemblyQualifiedName)).Append(':').Append(Json(info.m_netAI.GetType().AssemblyQualifiedName)).Append(':');
                AppendItemClass(value, info.m_class);
                value.Append(FloatBits(info.m_halfWidth)).Append(':').Append(FloatBits(info.m_pavementWidth)).Append(':').Append(FloatBits(info.m_segmentLength)).Append(';');
            }
            private static void AppendItemClass(StringBuilder value, ItemClass item)
            { value.Append((int)item.m_service).Append(':').Append((int)item.m_subService).Append(':').Append((int)item.m_level).Append(';'); }

            public void ArmOwnedReload(NativeSaveRoundtrip roundtrip, string path, string hash)
            {
                lock (_gate)
                {
                    if (!_handoffFrozen || _handoffReloadSeen || _handoffReloadArmed || _fixtureFingerprint == null || _interrupted || _cancelled
                        || !ReferenceEquals(roundtrip, _owner._roundtrip) || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(hash))
                        throw new InvalidOperationException("No unique frozen fixture can authorize this owned reload.");
                    _reloadPermitOwner = roundtrip; _reloadPermitPath = path; _reloadPermitHash = hash; _handoffReloadArmed = true;
                }
            }
            public void RevokeUnusedReloadPermit() { lock (_gate) _handoffReloadArmed = false; }
            private bool ConsumeOwnedReloadPermit()
            {
                lock (_gate)
                {
                    if (!_handoffReloadArmed || _cancelled || _interrupted || !OwnsCity() || _reloadPermitOwner == null
                        || !ReferenceEquals(_owner._roundtrip, _reloadPermitOwner) || !ReferenceEquals(ModRuntime.CurrentEconomicBridge, _checkpointOwner)
                        || !ReferenceEquals(_checkpointOwner.Capacity, _checkpointCapacity) || _checkpointOwner.DemandContextSession != _checkpointEpoch
                        || !_reloadPermitOwner.IsSubmittingOwnedReload(_reloadPermitPath, _reloadPermitHash)) return false;
                    _reloadPermitOwner.ConsumeOwnedUnload(); _handoffReloadArmed = false; _handoffReloadSeen = true;
                    SetPhase("OwnedReloadPreservingFrozenOrganicFixture"); return true;
                }
            }

            public void AdoptRestoredFixture(byte[] expectedLive)
            {
                byte[] actual = ModRuntime.GetEconomicSaveEnvelope();
                if (!SameFixtureBytes(expectedLive, actual)) throw new InvalidDataException("Economic state changed before explicit fixture adoption.");
                AuditPausedFixture(actual, true);
                lock (_gate)
                {
                    if (_handoffAdopted || _interrupted || _cancelled) throw new InvalidOperationException("Fixture adoption is cancelled or already consumed.");
                    _bridge = ModRuntime.CurrentEconomicBridge;
                    foreach (ushort id in _buildings.Keys) _buildings[id].Info = Singleton<BuildingManager>.instance.m_buildings.m_buffer[id].Info;
                    foreach (ushort id in _segments.Keys) _segments[id].Info = Singleton<NetManager>.instance.m_segments.m_buffer[id].Info;
                    foreach (ushort id in _nodes.Keys) _nodes[id].Info = Singleton<NetManager>.instance.m_nodes.m_buffer[id].Info;
                    _handoffAdopted = true; SetPhase("RestoredOrganicFixtureExplicitlyAdopted");
                }
            }
            public void InterruptRoundtrip() { lock (_gate) { _interrupted = true; _handoffReloadArmed = false; lock (_errors) _optionalToolbarContext = null; } }
            public void RequireSuccessfulFixtureRelease()
            {
                lock (_gate)
                    if (!_done || !_cleaned || _failure != null || !_handoffAdopted)
                        throw new InvalidOperationException("Restored organic fixture was not successfully adopted and released: " + _failure);
            }
            private void RequireCleanupOwner()
            {
                EconomicBridge expected = _handoffAdopted ? _restoredOwner : _bridge;
                int epoch = _handoffAdopted ? _restoredEpoch : _handoffFrozen ? _checkpointEpoch : _initialOwnerEpoch;
                if (expected == null || !ReferenceEquals(expected, ModRuntime.CurrentEconomicBridge) || expected.DemandContextSession != epoch)
                    throw new InvalidOperationException("Organic cleanup refused a stopped or divergent economic owner; same native session ID grants no destruction authority.");
            }
            private void ReleaseSpeedLease()
            {
                if (!_speedHeld) return;
                bool unchanged = _simulation.SelectedSimulationSpeed == 3 && !_simulation.SimulationPaused;
                if (unchanged) { _simulation.SelectedSimulationSpeed = _oldSpeed; _simulation.SimulationPaused = _oldPaused; }
                _speedHeld = false;
                Add("{\"phase\":\"SpeedLeaseReleased\",\"restored\":" + Bool(unchanged) + ",\"previousPaused\":" + Bool(_oldPaused) + ",\"previousSpeed\":" + _oldSpeed + "}");
            }
            public string RoundtripHandoffJson()
            {
                lock (_gate) return "{\"requested\":" + Bool(_handoffRequested) + ",\"frozen\":" + Bool(_handoffFrozen) + ",\"ownedUnloadObserved\":" + Bool(_handoffReloadSeen)
                    + ",\"adoptedNewOwner\":" + Bool(_handoffAdopted) + ",\"originalEpoch\":" + _checkpointEpoch + ",\"restoredEpoch\":" + _restoredEpoch
                    + ",\"fixtureSha256\":" + Json(_fixtureFingerprintHash) + ",\"exactClassSha256\":" + Json(_initialClassHash)
                    + ",\"auditedResidentialSlots\":" + _auditedHomes + ",\"auditedHomeUnits\":" + _auditedHomeUnits + ",\"auditedPeople\":" + _auditedPeople + "}";
            }
            private static bool SameLotIdentity(ResidentialLotIdentity a, ResidentialLotIdentity b)
            { return a != null && b != null && a.BuildingId == b.BuildingId && a.NativeBuildIndex == b.NativeBuildIndex && a.OriginXBits == b.OriginXBits && a.OriginZBits == b.OriginZBits
                && a.PrefabName == b.PrefabName && a.Service == b.Service && a.SubService == b.SubService && a.Width == b.Width && a.Length == b.Length; }
            private static void AppendPosition(StringBuilder value, Vector3 p) { value.Append(FloatBits(p.x)).Append(':').Append(FloatBits(p.y)).Append(':').Append(FloatBits(p.z)).Append(';'); }
            private static void AppendCensus(StringBuilder value, int[] inputs) { value.Append("Census:").Append(inputs.Length).Append(':'); foreach (int input in inputs) value.Append(input).Append(','); value.Append(';'); }
            private static bool SameInputs(int[] first, int[] second) { if (first.Length != second.Length) return false; for (int i = 0; i < first.Length; ++i) if (first[i] != second[i]) return false; return true; }
            private static bool AllZero(int[] values) { foreach (int value in values) if (value != 0) return false; return true; }
            private static string AppliedInputsJson(CityCensus census)
            {
                if (census == null) return "null";
                return "{\"residentsByLotClass\":" + IntArrayJson(census.ResidentsByLotClass) + ",\"workforceByLotClassEducation\":" + IntArrayJson(census.WorkforceByLotClassEducation)
                    + ",\"jobCapacity\":" + IntArrayJson(census.JobCapacityBySegment) + ",\"filledJobs\":" + IntArrayJson(census.FilledJobsBySegment)
                    + ",\"unboundResidents\":" + IntArrayJson(census.UnboundResidentsByNativeWealth) + ",\"unboundWorkers\":" + IntArrayJson(census.UnboundWorkforceByNativeWealthEducation)
                    + ",\"unmappedJobCapacity\":" + census.UnmappedJobCapacity + ",\"unmappedFilledJobs\":" + census.UnmappedFilledJobs + "}";
            }
            private static string IntArrayJson(int[] values) { List<string> parts = new List<string>(); foreach (int value in values) parts.Add(value.ToString(CultureInfo.InvariantCulture)); return "[" + string.Join(",", parts.ToArray()) + "]"; }
            private static string FingerprintHash(string value) { return FingerprintHash(Encoding.UTF8.GetBytes(value)); }
            private static string FingerprintHash(byte[] value) { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", string.Empty); }
            private static bool SameFixtureBytes(byte[] a, byte[] b)
            { if (a == null || b == null) return a == b; if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; ++i) if (a[i] != b[i]) return false; return true; }
        }
    }
}
