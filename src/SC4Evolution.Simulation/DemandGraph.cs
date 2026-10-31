using System;
using System.Collections.Generic;

namespace SC4Evolution.Simulation
{
    /// <summary>
    /// A deterministic, instance-local demand graph. This is a clean-room port of
    /// the documented node/link behavior; it does not depend on the game runtime.
    /// </summary>
    public sealed class DemandGraph
    {
        private const float TransferThreshold = 0.01f;
        private const float NormalizationScale = 1000.0f;
        private readonly List<Node> _orderedNodes = new List<Node>();
        private readonly Dictionary<uint, Node> _nodesByKey = new Dictionary<uint, Node>();

        public DemandGraph(DemandProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException("profile");

            DemandNodeDefinition[] definitions = profile.Nodes;
            int index;
            for (index = 0; index < definitions.Length; index++)
            {
                Node node = new Node(definitions[index]);
                _orderedNodes.Add(node);
                _nodesByKey.Add(node.Key, node);
            }
            _orderedNodes.Sort(delegate(Node left, Node right) { return left.Key.CompareTo(right.Key); });
        }

        public void QueueSupplyChange(uint key, float delta)
        {
            DemandNumbers.RequireFinite(delta, "delta");
            Node node = GetNode(key);
            node.PendingSupply = Add32(node.PendingSupply, delta, "queued supply");
        }

        public void QueueDemandChange(uint key, float delta)
        {
            DemandNumbers.RequireFinite(delta, "delta");
            Node node = GetNode(key);
            node.PendingDemand = Add32(node.PendingDemand, delta, "queued demand");
        }

        public void SetModifiers(uint key, float economy, float tax)
        {
            DemandNumbers.RequireFinite(economy, "economy");
            DemandNumbers.RequireFinite(tax, "tax");
            Node node = GetNode(key);
            node.EconomyModifier = economy;
            node.TaxModifier = tax;
        }

        public void SetDemandCap(uint key, float cap)
        {
            DemandNumbers.RequireFinite(cap, "cap");
            GetNode(key).DemandCap = cap;
        }

        public DemandNodeState GetState(uint key)
        {
            return GetNode(key).ToState();
        }

        public DemandNodeState[] GetAllStates()
        {
            DemandNodeState[] states = new DemandNodeState[_orderedNodes.Count];
            int index;
            for (index = 0; index < _orderedNodes.Count; index++)
                states[index] = _orderedNodes[index].ToState();
            return states;
        }

        public DemandPropagationResult Propagate(int maximumPasses)
        {
            if (maximumPasses < 1 || maximumPasses > 1000)
                throw new ArgumentOutOfRangeException("maximumPasses", "Pass limit must be between 1 and 1000.");

            NodeBackup[] backup = CaptureState();
            try
            {
                int pass;
                for (pass = 1; pass <= maximumPasses; pass++)
                {
                    bool stable = true;
                    int nodeIndex;
                    for (nodeIndex = 0; nodeIndex < _orderedNodes.Count; nodeIndex++)
                    {
                        Node node = _orderedNodes[nodeIndex];
                        DemandLink[] links = node.Links;
                        float sumSupply = 0.0f;
                        float sumDemand = 0.0f;
                        bool hasSums = false;

                        int linkIndex;
                        for (linkIndex = 0; linkIndex < links.Length; linkIndex++)
                        {
                            DemandLink link = links[linkIndex];
                            Node target = _nodesByKey[link.TargetKey];
                            switch (link.Kind)
                            {
                                case DemandLinkKind.Drives:
                                    if (Transfer(node.PendingSupply, link.Factor, target, false))
                                        stable = false;
                                    break;
                                case DemandLinkKind.Satisfies:
                                    if (Transfer(node.PendingSupply, link.Factor, target, true))
                                        stable = false;
                                    break;
                                case DemandLinkKind.Contributes:
                                    if (Transfer(node.PendingDemand, link.Factor, target, false))
                                        stable = false;
                                    break;
                                case DemandLinkKind.Caps:
                                    ApplyCap(node, target);
                                    break;
                                case DemandLinkKind.Sums:
                                    sumSupply = Add32(sumSupply, target.Supply, "supply sum");
                                    sumDemand = Add32(sumDemand, target.Demand, "demand sum");
                                    hasSums = true;
                                    break;
                                default:
                                    throw new InvalidOperationException("The demand profile contains an unsupported link kind.");
                            }
                        }

                        EndOfCycle(node);

                        if (hasSums)
                        {
                            float supplyCorrection = Subtract32(sumSupply, node.Supply, "supply sum correction");
                            if (Abs32(supplyCorrection) > TransferThreshold)
                            {
                                node.PendingSupply = Add32(node.PendingSupply, supplyCorrection, "pending supply sum correction");
                                stable = false;
                            }

                            float demandCorrection = Subtract32(sumDemand, node.Demand, "demand sum correction");
                            if (Abs32(demandCorrection) > TransferThreshold)
                            {
                                node.PendingDemand = Add32(node.PendingDemand, demandCorrection, "pending demand sum correction");
                                stable = false;
                            }
                        }
                    }

                    if (stable)
                        return new DemandPropagationResult(true, pass);
                }

                return new DemandPropagationResult(false, maximumPasses);
            }
            catch
            {
                // Arithmetic failures must not leave a partially committed graph.
                RestoreState(backup);
                throw;
            }
        }

        private static bool Transfer(float pending, float factor, Node target, bool toSupply)
        {
            float amount = Multiply32(pending, factor, "link transfer");
            if (Abs32(amount) <= TransferThreshold)
                return false;

            if (toSupply)
                target.PendingSupply = Add32(target.PendingSupply, amount, "transferred pending supply");
            else
                target.PendingDemand = Add32(target.PendingDemand, amount, "transferred pending demand");
            return true;
        }

        private static void ApplyCap(Node source, Node target)
        {
            float supply = source.Supply < TransferThreshold ? TransferThreshold : source.Supply;
            float demand = source.Demand < 0.0f ? 0.0f : source.Demand;
            float cap = demand < supply ? Divide32(demand, supply, "demand cap") : 1.0f;
            target.DemandCap = cap;
        }

        private static void EndOfCycle(Node node)
        {
            float committedSupply = Add32(node.Supply, node.PendingSupply, "committed supply");
            float committedDemand = Add32(node.Demand, node.PendingDemand, "committed demand");
            node.Supply = committedSupply;
            node.Demand = committedDemand;
            node.PendingSupply = 0.0f;
            node.PendingDemand = 0.0f;
            RecalculateActiveDemand(node);
        }

        private static void RecalculateActiveDemand(Node node)
        {
            float baseDemand = Clamp(Subtract32(node.Demand, node.Supply, "raw demand"), node.Minimum, node.Maximum);
            float modifier = Add32(node.EconomyModifier, node.TaxModifier, "combined modifier");
            float maximumBonus = Multiply32(node.Maximum, modifier, "maximum modifier bonus");
            float demandBonus = Multiply32(node.Demand, modifier, "demand modifier bonus");
            float bonus = maximumBonus < demandBonus ? maximumBonus : demandBonus;
            float target = Clamp(Add32(baseDemand, bonus, "modified active demand"), node.Minimum, node.Maximum);
            float cappedMaximum = Subtract32(node.Maximum, Multiply32(node.Maximum, node.DemandCap, "demand cap product"), "capped maximum");
            target = Max32(node.Minimum, Min32(target, cappedMaximum));
            node.ActiveDemand = target;
            node.NormalizedDemand = NormalizeDemand(target, node.Minimum, node.Maximum);
        }

        private static int NormalizeDemand(float activeDemand, float minimum, float maximum)
        {
            float normalized;
            if (activeDemand >= 0.0f)
            {
                if (maximum == 0.0f)
                    return 0;
                normalized = Multiply32(Divide32(activeDemand, maximum, "positive demand normalization"),
                    NormalizationScale, "positive demand normalization scale");
            }
            else
            {
                if (minimum == 0.0f)
                    return 0;
                normalized = -Multiply32(Divide32(activeDemand, minimum, "negative demand normalization"),
                    NormalizationScale, "negative demand normalization scale");
            }

            if (normalized > NormalizationScale)
                normalized = NormalizationScale;
            else if (normalized < -NormalizationScale)
                normalized = -NormalizationScale;

            return (int)Math.Round((double)normalized, MidpointRounding.ToEven);
        }

        private Node GetNode(uint key)
        {
            Node node;
            if (!_nodesByKey.TryGetValue(key, out node))
                throw new KeyNotFoundException("Unknown demand node key: " + key.ToString() + ".");
            return node;
        }

        private NodeBackup[] CaptureState()
        {
            NodeBackup[] states = new NodeBackup[_orderedNodes.Count];
            int index;
            for (index = 0; index < _orderedNodes.Count; index++)
                states[index] = new NodeBackup(_orderedNodes[index]);
            return states;
        }

        private void RestoreState(NodeBackup[] states)
        {
            int index;
            for (index = 0; index < _orderedNodes.Count; index++)
                states[index].Restore(_orderedNodes[index]);
        }

        private static float Add32(float left, float right, string operation)
        {
            return RequireFinite((float)((double)left + (double)right), operation);
        }

        private static float Subtract32(float left, float right, string operation)
        {
            return RequireFinite((float)((double)left - (double)right), operation);
        }

        private static float Multiply32(float left, float right, string operation)
        {
            return RequireFinite((float)((double)left * (double)right), operation);
        }

        private static float Divide32(float left, float right, string operation)
        {
            return RequireFinite((float)((double)left / (double)right), operation);
        }

        private static float RequireFinite(float value, string operation)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArithmeticException("Non-finite result in demand " + operation + ".");
            return value;
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            if (value > maximum)
                return maximum;
            if (value < minimum)
                return minimum;
            return value;
        }

        private static float Min32(float left, float right) { return left < right ? left : right; }
        private static float Max32(float left, float right) { return left > right ? left : right; }
        private static float Abs32(float value) { return value < 0.0f ? -value : value; }

        private sealed class Node
        {
            internal readonly uint Key;
            internal readonly float Minimum;
            internal readonly float Maximum;
            internal readonly DemandLink[] Links;
            internal float Supply;
            internal float Demand;
            internal float PendingSupply;
            internal float PendingDemand;
            internal float ActiveDemand;
            internal float EconomyModifier;
            internal float TaxModifier;
            internal float DemandCap;
            internal int NormalizedDemand;

            internal Node(DemandNodeDefinition definition)
            {
                Key = definition.Key;
                Minimum = definition.Minimum;
                Maximum = definition.Maximum;
                Supply = definition.InitialSupply;
                Demand = definition.InitialDemand;
                Links = definition.Links;
                PendingSupply = 0.0f;
                PendingDemand = 0.0f;
                ActiveDemand = 0.0f;
                EconomyModifier = 0.0f;
                TaxModifier = 0.0f;
                DemandCap = 0.0f;
                NormalizedDemand = 0;
            }

            internal DemandNodeState ToState()
            {
                return new DemandNodeState
                {
                    Key = Key,
                    Supply = Supply,
                    Demand = Demand,
                    PendingSupply = PendingSupply,
                    PendingDemand = PendingDemand,
                    ActiveDemand = ActiveDemand,
                    Minimum = Minimum,
                    Maximum = Maximum,
                    EconomyModifier = EconomyModifier,
                    TaxModifier = TaxModifier,
                    DemandCap = DemandCap,
                    NormalizedDemand = NormalizedDemand
                };
            }
        }

        private struct NodeBackup
        {
            private readonly float _supply, _demand, _pendingSupply, _pendingDemand;
            private readonly float _activeDemand, _economyModifier, _taxModifier, _demandCap;
            private readonly int _normalizedDemand;

            internal NodeBackup(Node node)
            {
                _supply = node.Supply;
                _demand = node.Demand;
                _pendingSupply = node.PendingSupply;
                _pendingDemand = node.PendingDemand;
                _activeDemand = node.ActiveDemand;
                _economyModifier = node.EconomyModifier;
                _taxModifier = node.TaxModifier;
                _demandCap = node.DemandCap;
                _normalizedDemand = node.NormalizedDemand;
            }

            internal void Restore(Node node)
            {
                node.Supply = _supply;
                node.Demand = _demand;
                node.PendingSupply = _pendingSupply;
                node.PendingDemand = _pendingDemand;
                node.ActiveDemand = _activeDemand;
                node.EconomyModifier = _economyModifier;
                node.TaxModifier = _taxModifier;
                node.DemandCap = _demandCap;
                node.NormalizedDemand = _normalizedDemand;
            }
        }
    }
}
