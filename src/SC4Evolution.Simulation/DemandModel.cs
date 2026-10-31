using System;
using System.Collections.Generic;

namespace SC4Evolution.Simulation
{
    public enum DemandLinkKind : byte { Satisfies = 1, Drives = 2, Contributes = 3, Caps = 4, Sums = 5 }

    public sealed class DemandLink
    {
        public uint TargetKey { get; private set; }
        public DemandLinkKind Kind { get; private set; }
        public float Factor { get; private set; }

        public DemandLink(uint targetKey, DemandLinkKind kind, float factor)
        {
            if (kind < DemandLinkKind.Satisfies || kind > DemandLinkKind.Sums) throw new ArgumentOutOfRangeException("kind");
            DemandNumbers.RequireFinite(factor, "factor");
            TargetKey = targetKey; Kind = kind; Factor = factor;
        }
    }

    public sealed class DemandNodeDefinition
    {
        private readonly DemandLink[] _links;
        public uint Key { get; private set; }
        public string Name { get; private set; }
        public float Minimum { get; private set; }
        public float Maximum { get; private set; }
        public float InitialSupply { get; private set; }
        public float InitialDemand { get; private set; }
        public byte RegionUse { get; private set; }
        public DemandLink[] Links { get { return (DemandLink[])_links.Clone(); } }

        public DemandNodeDefinition(uint key, string name, float minimum, float maximum,
            float initialSupply, float initialDemand, byte regionUse, DemandLink[] links)
        {
            DemandNumbers.RequireFinite(minimum, "minimum"); DemandNumbers.RequireFinite(maximum, "maximum");
            DemandNumbers.RequireFinite(initialSupply, "initialSupply"); DemandNumbers.RequireFinite(initialDemand, "initialDemand");
            if (minimum > maximum) throw new ArgumentException("Minimum exceeds maximum.");
            if (regionUse > 2) throw new ArgumentOutOfRangeException("regionUse");
            if (name == null || name.Length > 2048) throw new ArgumentException("Invalid node name.", "name");
            if (links == null || links.Length > DemandProfile.MaximumLinks) throw new ArgumentException("Invalid links.", "links");
            foreach (DemandLink link in links) if (link == null) throw new ArgumentException("Null link.", "links");
            Key = key; Name = name; Minimum = minimum; Maximum = maximum;
            InitialSupply = initialSupply; InitialDemand = initialDemand; RegionUse = regionUse;
            _links = (DemandLink[])links.Clone();
        }
    }

    public sealed class DemandProfile
    {
        public const int MaximumNodes = 4096;
        public const int MaximumLinks = 32768;
        private readonly DemandNodeDefinition[] _nodes;
        public string SourceIdentity { get; private set; }
        public DemandNodeDefinition[] Nodes { get { return (DemandNodeDefinition[])_nodes.Clone(); } }

        public DemandProfile(string sourceIdentity, DemandNodeDefinition[] nodes)
        {
            if (sourceIdentity == null || sourceIdentity.Length > 4096) throw new ArgumentException("Invalid source identity.", "sourceIdentity");
            if (nodes == null || nodes.Length == 0 || nodes.Length > MaximumNodes) throw new ArgumentException("Invalid node count.", "nodes");
            Dictionary<uint, bool> keys = new Dictionary<uint, bool>();
            int totalLinks = 0;
            foreach (DemandNodeDefinition node in nodes)
            {
                if (node == null) throw new ArgumentException("Null node.", "nodes");
                if (keys.ContainsKey(node.Key)) throw new ArgumentException("Duplicate node key.", "nodes");
                keys.Add(node.Key, true);
                totalLinks += node.Links.Length;
                if (totalLinks > MaximumLinks) throw new ArgumentException("Too many links.", "nodes");
            }
            foreach (DemandNodeDefinition node in nodes)
                foreach (DemandLink link in node.Links)
                    if (!keys.ContainsKey(link.TargetKey)) throw new ArgumentException("A link targets a missing node.", "nodes");
            _nodes = (DemandNodeDefinition[])nodes.Clone();
            Array.Sort(_nodes, delegate(DemandNodeDefinition a, DemandNodeDefinition b) { return a.Key.CompareTo(b.Key); });
            SourceIdentity = sourceIdentity;
        }
    }

    public struct DemandNodeState
    {
        public uint Key;
        public float Supply, Demand, PendingSupply, PendingDemand, ActiveDemand, Minimum, Maximum;
        public float EconomyModifier, TaxModifier, DemandCap;
        public int NormalizedDemand;
    }

    public struct DemandPropagationResult
    {
        public bool Converged;
        public int Passes;
        public DemandPropagationResult(bool converged, int passes) { Converged = converged; Passes = passes; }
    }

    internal static class DemandNumbers
    {
        internal static void RequireFinite(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException(name, "Value must be finite.");
        }
    }
}
