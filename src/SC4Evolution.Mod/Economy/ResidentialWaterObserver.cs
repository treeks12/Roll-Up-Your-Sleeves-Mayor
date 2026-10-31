using System;
using System.IO;
using ColossalFramework;
using SC4Evolution.Simulation;
using UnityEngine;

namespace SC4Evolution.Mod.Economy
{
    internal sealed class ResidentialWaterObservation
    {
        internal ResidentialWaterEvidence Evidence;
        internal bool Sewage;
        internal byte Pollution;
        internal string Reason;
        internal ResidentialWaterObservation(ResidentialWaterEvidence evidence, bool sewage, byte pollution, string reason)
        { Evidence = evidence; Sewage = sewage; Pollution = pollution; Reason = reason; }
    }

    internal sealed class ResidentialWaterObserver
    {
        internal ResidentialWaterProfile Profile { get; private set; }
        internal string FailureReason { get; private set; }
        internal bool IsValid { get { return Profile != null; } }
        private ResidentialWaterObserver() { }
        internal static ResidentialWaterObserver Load(string root)
        {
            ResidentialWaterObserver result = new ResidentialWaterObserver();
            try
            {
                string path = Path.Combine(root, "analysis/generated/sc4-residential-profile/water-prerequisites.s4ew");
                if (!File.Exists(path) || new FileInfo(path).Length != ResidentialWaterProfileCodec.EncodedSize)
                    throw new InvalidDataException("Pinned 100-byte residential water profile is missing or has the wrong size.");
                ResidentialWaterProfile profile;
                using (FileStream stream = File.OpenRead(path)) profile = ResidentialWaterProfileCodec.Read(stream);
                ResidentialSelectionPolicy.ValidateWaterProfile(profile);
                result.Profile = profile;
            }
            catch (Exception error) { result.FailureReason = error.Message; }
            return result;
        }

        // Called only at a real residential candidate on the simulation thread.
        // Public CheckWater reads service state; never fetches or reserves water.
        internal ResidentialWaterObservation Observe(Vector3 position)
        {
            if (!IsValid) return Unknown("ProfileUnavailable");
            if (!Finite(position.x) || !Finite(position.y) || !Finite(position.z)) return Unknown("NonFinitePosition");
            if (!ResidentialWaterPrerequisite.IsSupportedNativeQueryPosition(position.x, position.y, position.z)) return Unknown("UnsupportedQueryDomain");
            if (!Singleton<WaterManager>.exists || !Singleton<NetManager>.exists || !Singleton<LoadingManager>.exists) return Unknown("ManagerUnavailable");
            LoadingManager loading = Singleton<LoadingManager>.instance;
            if (loading.m_currentlyLoading || !loading.m_loadingComplete || !loading.m_simulationDataLoaded) return Unknown("ManagerNotReady");
            try
            {
                bool water, sewage; byte pollution;
                Singleton<WaterManager>.instance.CheckWater(position, out water, out sewage, out pollution);
                return new ResidentialWaterObservation(water ? ResidentialWaterEvidence.Served : ResidentialWaterEvidence.Unserved, sewage, pollution,
                    water ? "NativePipedWaterServed" : "NativePipedWaterUnserved");
            }
            catch (Exception) { return Unknown("NativeQueryException"); }
        }
        private static ResidentialWaterObservation Unknown(string reason)
        { return new ResidentialWaterObservation(ResidentialWaterEvidence.Unknown, false, 0, reason); }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }

    public sealed class ResidentialLocalDecision
    {
        private readonly int[] _normalized;
        private readonly float[] _active;
        public uint Frame { get; private set; }
        public float X { get; private set; }
        public float Y { get; private set; }
        public float Z { get; private set; }
        public string Policy { get; private set; }
        public string ProfileSource { get; private set; }
        public string ProfileSha256 { get; private set; }
        public ResidentialWaterEvidence WaterEvidence { get; private set; }
        public bool Sewage { get; private set; }
        public byte WaterPollution { get; private set; }
        public ResidentialClassMask Mask { get; private set; }
        public ResidentialChoiceKind Kind { get; private set; }
        public ResidentialClass Class { get; private set; }
        public string Reason { get; private set; }
        public int[] NormalizedDemand { get { return (int[])_normalized.Clone(); } }
        public float[] ActiveDemand { get { return (float[])_active.Clone(); } }
        internal ResidentialLocalDecision(uint frame, Vector3 position, string policy, DemandNodeState[] nodes,
            ResidentialWaterObservation observation, ResidentialClassChoice choice)
        {
            Frame = frame; X = position.x; Y = position.y; Z = position.z; Policy = policy;
            ProfileSource = policy == "SC4WaterPrerequisiteV1" ? ResidentialSelectionPolicy.SourceArchiveSha256 : string.Empty;
            ProfileSha256 = policy == "SC4WaterPrerequisiteV1" ? ResidentialSelectionPolicy.WaterProfileSha256 : string.Empty;
            WaterEvidence = observation.Evidence; Sewage = observation.Sewage; WaterPollution = observation.Pollution;
            Mask = choice.Mask; Kind = choice.Kind; Class = choice.Class; Reason = observation.Reason;
            _normalized = new int[3]; _active = new float[3];
            foreach (DemandNodeState state in nodes)
                if (state.Key == 0x21010 || state.Key == 0x21020 || state.Key == 0x21030)
                { int index = (int)((state.Key - 0x21010) / 0x10); _normalized[index] = state.NormalizedDemand; _active[index] = state.ActiveDemand; }
        }
    }

    public sealed class ResidentialLocalDecisionStatus
    {
        public ulong Served { get; private set; }
        public ulong Unserved { get; private set; }
        public ulong Unknown { get; private set; }
        public ulong UnknownDomain { get; private set; }
        public ulong HighVetoes { get; private set; }
        public ulong LowChoices { get; private set; }
        public ulong MediumChoices { get; private set; }
        public ulong HighChoices { get; private set; }
        public ulong ControlledRejections { get; private set; }
        public ulong NativeFallbacks { get; private set; }
        public ResidentialLocalDecision LastDecision { get; private set; }
        internal ResidentialLocalDecisionStatus(ulong[] counts, ResidentialLocalDecision last)
        {
            Served = counts[0]; Unserved = counts[1]; Unknown = counts[2]; UnknownDomain = counts[3]; HighVetoes = counts[4];
            LowChoices = counts[5]; MediumChoices = counts[6]; HighChoices = counts[7]; ControlledRejections = counts[8]; NativeFallbacks = counts[9];
            LastDecision = last;
        }
    }
}
