using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SC4Evolution.Simulation;

internal static class Program
{
    private static int _assertions;

    private static int Main(string[] args)
    {
        try
        {
            string profilePath;
            string reportPath;
            ParseArguments(args, out profilePath, out reportPath);
            TestInitialValuesAndInstanceIsolation();
            TestEachLinkKindAndCommittedSums();
            TestAscendingKeyPassOrder();
            TestStrictTransferThreshold();
            TestActiveDemandModifiersCapsAndRounding();
            TestNegativeSumCorrections();
            TestInvalidInputsAndTransactionalArithmeticFailure();
            TestPassLimitAndArgumentBounds();
            TestProfileCodecRoundTripAndStreamOwnership();
            TestProfileCodecRejectsInvalidRecords();
            Console.WriteLine("PASS: " + _assertions + " assertions across 10 demand-graph scenarios.");
            if (profilePath != null)
                RunInstalledProfileProbe(profilePath, reportPath);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private static void ParseArguments(string[] args, out string profilePath, out string reportPath)
    {
        profilePath = null;
        reportPath = null;
        int index;
        for (index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--profile", StringComparison.Ordinal))
            {
                if (profilePath != null || index + 1 >= args.Length)
                    throw new ArgumentException("Usage: --profile <path> [--report <path>]");
                profilePath = args[++index];
            }
            else if (string.Equals(args[index], "--report", StringComparison.Ordinal))
            {
                if (reportPath != null || index + 1 >= args.Length)
                    throw new ArgumentException("Usage: --profile <path> [--report <path>]");
                reportPath = args[++index];
            }
            else
            {
                throw new ArgumentException("Unknown argument. Usage: --profile <path> [--report <path>]");
            }
        }

        if (reportPath != null && profilePath == null)
            throw new ArgumentException("--report requires an explicit --profile path.");
    }

    private static void RunInstalledProfileProbe(string profilePath, string reportPath)
    {
        DemandProfile profile;
        using (FileStream input = File.OpenRead(profilePath))
            profile = DemandProfileCodec.Read(input);

        DemandNodeDefinition[] definitions = profile.Nodes;
        int linkCount = 0;
        Dictionary<uint, DemandNodeDefinition> definitionsByKey = new Dictionary<uint, DemandNodeDefinition>();
        int index;
        for (index = 0; index < definitions.Length; index++)
        {
            definitionsByKey.Add(definitions[index].Key, definitions[index]);
            linkCount += definitions[index].Links.Length;
        }

        DemandGraph baselineGraph = new DemandGraph(profile);
        DemandPropagationResult baselineConvergence = baselineGraph.Propagate(128);
        RequireConverged(baselineConvergence, "installed profile baseline");
        DemandGraph unpropagatedGraph = new DemandGraph(profile);
        for (index = 0; index < definitions.Length; index++)
        {
            DemandNodeDefinition definition = definitions[index];
            DemandNodeState initial = unpropagatedGraph.GetState(definition.Key);
            Equal(definition.InitialSupply, initial.Supply, "profile initial supply is committed before propagation");
            Equal(definition.InitialDemand, initial.Demand, "profile initial demand is committed before propagation");
            Equal(0.0f, initial.PendingSupply, "profile starts without pending initial supply");
            Equal(0.0f, initial.PendingDemand, "profile starts without pending initial demand");
        }

        DemandNodeState[] baseline = baselineGraph.GetAllStates();
        Dictionary<uint, DemandNodeState> baselineByKey = IndexStates(baseline);
        string baselineVector = NormalizedRciVector(baselineGraph);
        float initialSettleDelta = MaximumCommittedDelta(IndexStates(unpropagatedGraph.GetAllStates()), baseline);
        StringBuilder report = new StringBuilder();
        report.AppendLine("SC4Evolution actual demand-profile probe");
        report.AppendLine("Source: " + profile.SourceIdentity);
        report.AppendLine("Profile: nodes=" + definitions.Length.ToString(CultureInfo.InvariantCulture) +
            " links=" + linkCount.ToString(CultureInfo.InvariantCulture));
        report.AppendLine("Baseline: converged=true passes=" + baselineConvergence.Passes.ToString(CultureInfo.InvariantCulture) +
            " initial-settle-max-committed-delta=" + Format(initialSettleDelta) +
            " initial-settle-differences=[" + CommittedDifferences(IndexStates(unpropagatedGraph.GetAllStates()), baseline, 0.01f) + "]" +
            " RCI-normalized=" + baselineVector);

        DemandNodeDefinition commercialServiceJobs = RequireNode(definitionsByKey, 0x3111u);
        EnsureProbeNodes(definitionsByKey, new uint[] { 0x21010u, 0x2111u, 0x2114u, 0x24200u, 0x24400u,
            0x3111u, 0x21010u, 0x21020u, 0x21030u, 0x23110u, 0x23120u, 0x23130u, 0x23320u, 0x23330u,
            0x24100u, 0x24200u, 0x24300u, 0x24400u });

        DemandGraph commercialGraph = CreateSettledGraph(profile, "CS$ census baseline");
        DemandNodeState[] commercialBaseline = commercialGraph.GetAllStates();
        Dictionary<uint, DemandNodeState> commercialBaselineByKey = IndexStates(commercialBaseline);
        commercialGraph.QueueSupplyChange(0x3111u, 100.0f);
        DemandPropagationResult commercialResult = commercialGraph.Propagate(128);
        RequireConverged(commercialResult, "CS$ census +100");
        DemandNodeState commercialSourceAfter = commercialGraph.GetState(0x3111u);
        DemandNodeState residentialDollarAfter = commercialGraph.GetState(0x21010u);
        float residentialDemandDelta = SubtractForProbe(residentialDollarAfter.Demand, commercialBaselineByKey[0x21010u].Demand);
        DemandNodeState aggregate3000AfterPlus = commercialGraph.GetState(0x3000u);

        List<string> directChanges = new List<string>();
        DemandLink[] directLinks = commercialServiceJobs.Links;
        for (index = 0; index < directLinks.Length; index++)
        {
            DemandNodeState before = commercialBaselineByKey[directLinks[index].TargetKey];
            DemandNodeState after = commercialGraph.GetState(directLinks[index].TargetKey);
            float supplyDelta = SubtractForProbe(after.Supply, before.Supply);
            float demandDelta = SubtractForProbe(after.Demand, before.Demand);
            float capDelta = SubtractForProbe(after.DemandCap, before.DemandCap);
            if (supplyDelta != 0.0f || demandDelta != 0.0f || capDelta != 0.0f)
                directChanges.Add("0x" + directLinks[index].TargetKey.ToString("X", CultureInfo.InvariantCulture) +
                    ":" + directLinks[index].Kind.ToString() + "(s=" + Format(supplyDelta) +
                    ",d=" + Format(demandDelta) + ",cap=" + Format(capDelta) + ")");
        }
        True(directChanges.Count > 0, "CS$ census changes at least one direct installed link dependency");
        report.AppendLine("CS$ jobs +100: converged=true passes=" + commercialResult.Passes.ToString(CultureInfo.InvariantCulture) +
            " sourceSupply=" + Format(commercialSourceAfter.Supply) +
            " R-total-demand-delta=" + Format(residentialDemandDelta) +
            " R-total-demand-increased=" + (residentialDemandDelta > 0.0f).ToString(CultureInfo.InvariantCulture) +
            " direct-dependencies=[" + string.Join("; ", directChanges.ToArray()) + "]" +
            " RCI-normalized=" + NormalizedRciVector(commercialGraph));

        commercialGraph.QueueSupplyChange(0x3111u, -100.0f);
        DemandPropagationResult commercialReturn = commercialGraph.Propagate(128);
        RequireConverged(commercialReturn, "CS$ census -100");
        float commercialReturnDelta = MaximumCommittedDelta(baselineByKey, commercialGraph.GetAllStates());
        report.AppendLine("CS$ jobs -100: converged=true passes=" + commercialReturn.Passes.ToString(CultureInfo.InvariantCulture) +
            " max-committed-return-delta=" + Format(commercialReturnDelta) +
            " within-0.01=" + (commercialReturnDelta <= 0.01f).ToString(CultureInfo.InvariantCulture) +
            " largest-difference=" + LargestCommittedDeltaDescription(baselineByKey, commercialGraph.GetAllStates()) +
            " differences=[" + CommittedDifferences(baselineByKey, commercialGraph.GetAllStates(), 0.01f) + "]" +
            " node0x3000-initial=" + StatePair(unpropagatedGraph.GetState(0x3000u)) +
            " baseline=" + StatePair(baselineByKey[0x3000u]) +
            " after+100=" + StatePair(aggregate3000AfterPlus) +
            " after-100=" + StatePair(commercialGraph.GetState(0x3000u)) +
            " links=[" + LinksSummary(RequireNode(definitionsByKey, 0x3000u)) + "]" +
            " RCI-normalized=" + NormalizedRciVector(commercialGraph));

        ProbeEducationWorkforce(profile, baselineByKey, 0x2111u, "lower-education workforce", report);
        ProbeEducationWorkforce(profile, baselineByKey, 0x2114u, "higher-education workforce", report);
        ProbeEducationWorkforce(profile, baselineByKey, 0x2134u, "wealth3 high-education workforce", report);

        string text = report.ToString();
        Console.WriteLine(text.TrimEnd());
        if (reportPath != null)
        {
            string fullReportPath = Path.GetFullPath(reportPath);
            string directory = Path.GetDirectoryName(fullReportPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(fullReportPath, text, new UTF8Encoding(false));
            Console.WriteLine("Profile probe report: " + fullReportPath);
        }
    }

    private static void ProbeEducationWorkforce(DemandProfile profile,
        Dictionary<uint, DemandNodeState> sharedBaseline, uint workforceKey, string label, StringBuilder report)
    {
        DemandGraph graph = CreateSettledGraph(profile, label + " baseline");
        Dictionary<uint, DemandNodeState> baseline = IndexStates(graph.GetAllStates());
        graph.QueueSupplyChange(workforceKey, 100.0f);
        DemandPropagationResult changed = graph.Propagate(128);
        RequireConverged(changed, label + " +100");
        float dirtyIndustryDelta = SubtractForProbe(graph.GetState(0x24200u).Demand, baseline[0x24200u].Demand);
        float highTechIndustryDelta = SubtractForProbe(graph.GetState(0x24400u).Demand, baseline[0x24400u].Demand);
        report.AppendLine("" + label + " +100 (0x" + workforceKey.ToString("X", CultureInfo.InvariantCulture) +
            "): converged=true passes=" + changed.Passes.ToString(CultureInfo.InvariantCulture) +
            " I-D-demand-delta=" + Format(dirtyIndustryDelta) +
            " I-HT-demand-delta=" + Format(highTechIndustryDelta) +
            " RCI-normalized=" + NormalizedRciVector(graph));

        graph.QueueSupplyChange(workforceKey, -100.0f);
        DemandPropagationResult returned = graph.Propagate(128);
        RequireConverged(returned, label + " -100");
        float returnDelta = MaximumCommittedDelta(sharedBaseline, graph.GetAllStates());
        report.AppendLine("" + label + " -100: converged=true passes=" + returned.Passes.ToString(CultureInfo.InvariantCulture) +
            " max-committed-return-delta=" + Format(returnDelta) +
            " within-0.01=" + (returnDelta <= 0.01f).ToString(CultureInfo.InvariantCulture) +
            " largest-difference=" + LargestCommittedDeltaDescription(sharedBaseline, graph.GetAllStates()));
    }

    private static DemandGraph CreateSettledGraph(DemandProfile profile, string scenario)
    {
        DemandGraph graph = new DemandGraph(profile);
        DemandPropagationResult result = graph.Propagate(128);
        RequireConverged(result, scenario);
        return graph;
    }

    private static void RequireConverged(DemandPropagationResult result, string scenario)
    {
        True(result.Converged, scenario + " converges within 128 passes");
    }

    private static void EnsureProbeNodes(Dictionary<uint, DemandNodeDefinition> definitions, uint[] keys)
    {
        int index;
        for (index = 0; index < keys.Length; index++)
            RequireNode(definitions, keys[index]);
    }

    private static DemandNodeDefinition RequireNode(Dictionary<uint, DemandNodeDefinition> definitions, uint key)
    {
        DemandNodeDefinition definition;
        if (!definitions.TryGetValue(key, out definition))
            throw new InvalidDataException("The profile is missing required probe node 0x" + key.ToString("X", CultureInfo.InvariantCulture) + ".");
        return definition;
    }

    private static Dictionary<uint, DemandNodeState> IndexStates(DemandNodeState[] states)
    {
        Dictionary<uint, DemandNodeState> indexed = new Dictionary<uint, DemandNodeState>();
        int index;
        for (index = 0; index < states.Length; index++)
            indexed.Add(states[index].Key, states[index]);
        return indexed;
    }

    private static string NormalizedRciVector(DemandGraph graph)
    {
        uint[] keys = new uint[] { 0x21010u, 0x21020u, 0x21030u, 0x23110u, 0x23120u, 0x23130u,
            0x23320u, 0x23330u, 0x24100u, 0x24200u, 0x24300u, 0x24400u };
        StringBuilder vector = new StringBuilder();
        int index;
        for (index = 0; index < keys.Length; index++)
        {
            if (index > 0)
                vector.Append(",");
            vector.Append("0x").Append(keys[index].ToString("X", CultureInfo.InvariantCulture)).Append("=")
                .Append(graph.GetState(keys[index]).NormalizedDemand.ToString(CultureInfo.InvariantCulture));
        }
        return vector.ToString();
    }

    private static float MaximumCommittedDelta(Dictionary<uint, DemandNodeState> baseline, DemandNodeState[] current)
    {
        double maximum = 0.0;
        int index;
        for (index = 0; index < current.Length; index++)
        {
            DemandNodeState before = baseline[current[index].Key];
            double supplyDelta = Math.Abs((double)current[index].Supply - before.Supply);
            double demandDelta = Math.Abs((double)current[index].Demand - before.Demand);
            if (supplyDelta > maximum) maximum = supplyDelta;
            if (demandDelta > maximum) maximum = demandDelta;
        }
        return (float)maximum;
    }

    private static string LargestCommittedDeltaDescription(Dictionary<uint, DemandNodeState> baseline, DemandNodeState[] current)
    {
        double maximum = 0.0;
        uint key = 0;
        string field = "none";
        float beforeValue = 0.0f;
        float afterValue = 0.0f;
        int index;
        for (index = 0; index < current.Length; index++)
        {
            DemandNodeState before = baseline[current[index].Key];
            double supplyDelta = Math.Abs((double)current[index].Supply - before.Supply);
            if (supplyDelta > maximum)
            {
                maximum = supplyDelta; key = current[index].Key; field = "supply";
                beforeValue = before.Supply; afterValue = current[index].Supply;
            }
            double demandDelta = Math.Abs((double)current[index].Demand - before.Demand);
            if (demandDelta > maximum)
            {
                maximum = demandDelta; key = current[index].Key; field = "demand";
                beforeValue = before.Demand; afterValue = current[index].Demand;
            }
        }
        return field + "@0x" + key.ToString("X", CultureInfo.InvariantCulture) +
            "(" + Format(beforeValue) + "->" + Format(afterValue) + ")";
    }

    private static string StatePair(DemandNodeState state)
    {
        return "s=" + Format(state.Supply) + ",d=" + Format(state.Demand) +
            ",ps=" + Format(state.PendingSupply) + ",pd=" + Format(state.PendingDemand);
    }

    private static string LinksSummary(DemandNodeDefinition definition)
    {
        DemandLink[] links = definition.Links;
        List<string> summaries = new List<string>();
        int index;
        for (index = 0; index < links.Length; index++)
            summaries.Add(links[index].Kind.ToString() + ":0x" +
                links[index].TargetKey.ToString("X", CultureInfo.InvariantCulture) + "×" + Format(links[index].Factor));
        return string.Join("; ", summaries.ToArray());
    }

    private static string CommittedDifferences(Dictionary<uint, DemandNodeState> baseline,
        DemandNodeState[] current, float minimumDifference)
    {
        List<string> differences = new List<string>();
        int index;
        for (index = 0; index < current.Length; index++)
        {
            DemandNodeState before = baseline[current[index].Key];
            if (Math.Abs((double)current[index].Supply - before.Supply) > minimumDifference ||
                Math.Abs((double)current[index].Demand - before.Demand) > minimumDifference)
            {
                differences.Add("0x" + current[index].Key.ToString("X", CultureInfo.InvariantCulture) +
                    "[" + Format(before.Supply) + "," + Format(before.Demand) + "->" +
                    Format(current[index].Supply) + "," + Format(current[index].Demand) + "]");
            }
        }
        return string.Join("; ", differences.ToArray());
    }

    private static float SubtractForProbe(float left, float right)
    {
        return (float)((double)left - right);
    }

    private static string Format(float value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static void TestInitialValuesAndInstanceIsolation()
    {
        DemandProfile profile = Profile(
            Node(2, -100.0f, 100.0f, 0.0f, 0.0f),
            Node(1, -100.0f, 100.0f, 10.0f, 20.0f,
                Link(2, DemandLinkKind.Drives, 0.5f)));
        DemandGraph first = new DemandGraph(profile);
        DemandGraph second = new DemandGraph(profile);

        Equal(1u, first.GetAllStates()[0].Key, "state copies are sorted by key");
        Equal(10.0f, first.GetState(1).Supply, "initial supply is committed at construction");
        Equal(20.0f, first.GetState(1).Demand, "initial demand is committed at construction");
        Equal(0.0f, first.GetState(1).PendingSupply, "initial supply is not a pending census delta");
        Equal(0.0f, first.GetState(1).PendingDemand, "initial demand is not a pending census delta");
        DemandPropagationResult initialResult = first.Propagate(3);
        True(initialResult.Converged, "an untouched initial graph converges");
        Equal(1, initialResult.Passes, "an untouched graph needs only its stable pass");
        Equal(0.0f, first.GetState(2).Demand, "initial committed values do not travel through Drives");

        first.QueueSupplyChange(1, 4.0f);
        DemandPropagationResult changed = first.Propagate(3);
        True(changed.Converged, "a one-way forward link converges");
        Equal(14.0f, first.GetState(1).Supply, "queued supply is added to the initial committed value at EndOfCycle");
        Equal(2.0f, first.GetState(2).Demand, "later-key target receives only this cycle's supply delta");
        Equal(10.0f, second.GetState(1).Supply, "separate graph instances keep their own initial committed state");
        Equal(0.0f, second.GetState(2).Demand, "separate graph instances do not share propagated state");

        DemandNodeState[] copy = first.GetAllStates();
        copy[0].Supply = -999.0f;
        Equal(14.0f, first.GetState(1).Supply, "mutating a returned state array cannot alter graph state");
        Throws<KeyNotFoundException>(delegate { first.GetState(999); }, "unknown state keys are rejected");
    }

    private static void TestEachLinkKindAndCommittedSums()
    {
        DemandProfile profile = Profile(
            Node(6, -1000.0f, 1000.0f, 0.0f, 0.0f,
                Link(2, DemandLinkKind.Sums, 1.0f),
                Link(3, DemandLinkKind.Sums, 1.0f)),
            Node(5, -6000.0f, 6000.0f, 0.0f, 5000.0f),
            Node(4, -100.0f, 100.0f, 4.0f, 5.0f),
            Node(3, -100.0f, 100.0f, 0.0f, 1.0f),
            Node(2, -100.0f, 100.0f, 3.0f, 2.0f),
            Node(1, -100.0f, 100.0f, 100.0f, 50.0f,
                Link(2, DemandLinkKind.Satisfies, 0.5f),
                Link(3, DemandLinkKind.Drives, 0.25f),
                Link(4, DemandLinkKind.Contributes, 0.2f),
                Link(5, DemandLinkKind.Caps, 1.0f)));
        DemandGraph graph = new DemandGraph(profile);
        graph.QueueSupplyChange(1, 10.0f);
        graph.QueueDemandChange(1, 20.0f);

        DemandPropagationResult result = graph.Propagate(8);
        True(result.Converged, "all five link kinds converge in a finite graph");
        Equal(110.0f, graph.GetState(1).Supply, "source commits its supply delta");
        Equal(70.0f, graph.GetState(1).Demand, "source commits its demand delta");
        Equal(8.0f, graph.GetState(2).Supply, "Satisfies transfers pending supply times factor");
        Equal(3.5f, graph.GetState(3).Demand, "Drives transfers source pending supply to demand");
        Equal(9.0f, graph.GetState(4).Demand, "Contributes transfers source pending demand to demand");
        Near(70.0f / 110.0f, graph.GetState(5).DemandCap, 0.000001f, "Caps recalculates from the source's latest committed usage and capacity");
        Near(2181.8184f, graph.GetState(5).ActiveDemand, 0.01f, "the committed cap limits the target after its own modifier and clamp");
        Equal(8.0f, graph.GetState(6).Supply, "Sums copies the committed target supply total");
        Equal(5.5f, graph.GetState(6).Demand, "Sums copies the committed target demand total");
        Equal(0.0f, graph.GetState(6).PendingSupply, "a converged sum has no pending correction");
        Equal(0.0f, graph.GetState(6).PendingDemand, "a converged demand sum has no pending correction");
    }

    private static void TestAscendingKeyPassOrder()
    {
        DemandGraph forward = new DemandGraph(Profile(
            Node(10, -100.0f, 100.0f, 0.0f, 0.0f),
            Node(20, -100.0f, 100.0f, 0.0f, 0.0f,
                Link(10, DemandLinkKind.Satisfies, 0.5f))));
        forward.QueueSupplyChange(20, 10.0f);
        DemandPropagationResult onePass = forward.Propagate(1);
        True(!onePass.Converged, "a transfer to an already-processed lower key requires another pass");
        Equal(10.0f, forward.GetState(20).Supply, "higher-key source commits on its first pass");
        Equal(5.0f, forward.GetState(10).PendingSupply, "lower-key target keeps the late transfer pending");
        Equal(0.0f, forward.GetState(10).Supply, "lower-key target cannot commit after its turn passed");
        True(forward.Propagate(4).Converged, "the next pass commits the late lower-key transfer");
        Equal(5.0f, forward.GetState(10).Supply, "lower-key target eventually commits transferred supply");

        DemandGraph reverse = new DemandGraph(Profile(
            Node(10, -100.0f, 100.0f, 0.0f, 0.0f,
                Link(20, DemandLinkKind.Satisfies, 0.5f)),
            Node(20, -100.0f, 100.0f, 0.0f, 0.0f)));
        reverse.QueueSupplyChange(10, 10.0f);
        DemandPropagationResult reversePass = reverse.Propagate(1);
        True(!reversePass.Converged, "a forward transfer still requires a final stability pass");
        Equal(5.0f, reverse.GetState(20).Supply, "later-key target commits a same-pass transfer");
        Equal(0.0f, reverse.GetState(20).PendingSupply, "same-pass target has already folded its delta");
    }

    private static void TestStrictTransferThreshold()
    {
        DemandGraph dropped = new DemandGraph(Profile(
            Node(1, -10.0f, 10.0f, 0.0f, 0.0f,
                Link(2, DemandLinkKind.Satisfies, 0.01f)),
            Node(2, -10.0f, 10.0f, 0.0f, 0.0f)));
        dropped.QueueSupplyChange(1, 1.0f);
        DemandPropagationResult droppedResult = dropped.Propagate(2);
        True(droppedResult.Converged, "a transfer exactly equal to the threshold is dropped");
        Equal(1, droppedResult.Passes, "a dropped transfer does not cause an extra pass");
        Equal(0.0f, dropped.GetState(2).Supply, "an exact 0.01 transfer is not deferred");

        DemandGraph transferred = new DemandGraph(Profile(
            Node(1, -10.0f, 10.0f, 0.0f, 0.0f,
                Link(2, DemandLinkKind.Satisfies, 0.0101f)),
            Node(2, -10.0f, 10.0f, 0.0f, 0.0f)));
        transferred.QueueSupplyChange(1, 1.0f);
        DemandPropagationResult transferredResult = transferred.Propagate(1);
        True(!transferredResult.Converged, "a transfer just above the threshold marks propagation unstable");
        Near(0.0101f, transferred.GetState(2).Supply, 0.0000001f, "a transfer above 0.01 is applied");

        DemandGraph zeroCapacity = new DemandGraph(Profile(
            Node(1, -10.0f, 10.0f, 0.0f, 0.0f,
                Link(2, DemandLinkKind.Caps, 1.0f)),
            Node(2, -10.0f, 10.0f, 0.0f, 0.0f)));
        zeroCapacity.Propagate(1);
        Equal(0.0f, zeroCapacity.GetState(2).DemandCap, "zero source capacity uses the 0.01 floor without division by zero");
    }

    private static void TestActiveDemandModifiersCapsAndRounding()
    {
        DemandGraph graph = new DemandGraph(Profile(
            Node(1, -6000.0f, 6000.0f, 400.0f, 1000.0f),
            Node(2, -6000.0f, 6000.0f, 0.0f, 1000.0f),
            Node(3, -2000.0f, 2000.0f, 0.0f, 1.0f),
            Node(4, -2000.0f, 2000.0f, 0.0f, 3.0f),
            Node(5, -2000.0f, 2000.0f, 0.0f, -1.0f),
            Node(6, -2000.0f, 2000.0f, 0.0f, -3.0f),
            Node(7, 0.0f, 0.0f, 0.0f, 0.0f)));
        graph.Propagate(1);
        Equal(600.0f, graph.GetState(1).ActiveDemand, "base active demand clamps demand minus supply");
        Equal(100, graph.GetState(1).NormalizedDemand, "positive demand normalizes into the signed range");

        graph.SetModifiers(1, 0.03f, 0.0f);
        graph.Propagate(1);
        Equal(630.0f, graph.GetState(1).ActiveDemand, "positive economy modifier applies the bounded demand bonus");
        Equal(105, graph.GetState(1).NormalizedDemand, "positive modifier changes normalized demand");

        graph.SetDemandCap(1, 0.95f);
        graph.Propagate(1);
        Equal(300.0f, graph.GetState(1).ActiveDemand, "cap is applied after the modifier and limits the maximum");
        Equal(50, graph.GetState(1).NormalizedDemand, "capped active demand is normalized after clamping");

        graph.SetModifiers(2, 0.02f, -0.12f);
        graph.Propagate(1);
        Near(400.0f, graph.GetState(2).ActiveDemand, 0.001f, "negative combined modifiers select the larger bounded penalty with float32 rounding");
        Equal(67, graph.GetState(2).NormalizedDemand, "negative-modifier demand still normalizes with midpoint-even rounding");

        graph.Propagate(1);
        Equal(0, graph.GetState(3).NormalizedDemand, "normalization midpoint 0.5 rounds to even zero");
        Equal(2, graph.GetState(4).NormalizedDemand, "normalization midpoint 1.5 rounds to even two");
        Equal(0, graph.GetState(5).NormalizedDemand, "negative normalization midpoint -0.5 rounds to even zero");
        Equal(-2, graph.GetState(6).NormalizedDemand, "negative normalization midpoint -1.5 rounds to even minus two");
        Equal(0, graph.GetState(7).NormalizedDemand, "a zero active range normalizes safely to zero");
    }

    private static void TestNegativeSumCorrections()
    {
        DemandGraph graph = new DemandGraph(Profile(
            Node(1, -100.0f, 100.0f, 10.0f, 5.0f),
            Node(2, -100.0f, 100.0f, 20.0f, -3.0f),
            Node(3, -100.0f, 100.0f, 0.0f, 0.0f,
                Link(1, DemandLinkKind.Sums, 1.0f),
                Link(2, DemandLinkKind.Sums, 1.0f))));
        DemandPropagationResult initial = graph.Propagate(5);
        True(initial.Converged, "committed-value sums converge");
        Equal(30.0f, graph.GetState(3).Supply, "initial committed supply totals are summed");
        Equal(2.0f, graph.GetState(3).Demand, "initial committed demand totals are summed");

        graph.QueueSupplyChange(2, -25.0f);
        graph.QueueDemandChange(2, -6.0f);
        DemandPropagationResult corrected = graph.Propagate(5);
        True(corrected.Converged, "negative sum corrections converge");
        Equal(5.0f, graph.GetState(3).Supply, "negative census correction subtracts from the prior sum");
        Equal(-4.0f, graph.GetState(3).Demand, "negative demand correction preserves signed totals");
        Equal(0.0f, graph.GetState(3).PendingSupply, "negative correction is committed without residual pending supply");
        Equal(0.0f, graph.GetState(3).PendingDemand, "negative correction is committed without residual pending demand");
    }

    private static void TestInvalidInputsAndTransactionalArithmeticFailure()
    {
        DemandGraph graph = new DemandGraph(Profile(
            Node(0, -100.0f, 100.0f, 0.0f, 0.0f),
            Node(1, -100.0f, 100.0f, 0.0f, 0.0f,
                Link(2, DemandLinkKind.Drives, float.MaxValue)),
            Node(2, -100.0f, 100.0f, 0.0f, 0.0f)));
        Throws<ArgumentOutOfRangeException>(delegate { graph.QueueSupplyChange(0, float.NaN); }, "nonfinite queued deltas are rejected");
        Throws<ArgumentOutOfRangeException>(delegate { graph.SetModifiers(0, float.PositiveInfinity, 0.0f); }, "nonfinite modifiers are rejected");
        Throws<ArgumentOutOfRangeException>(delegate { graph.SetDemandCap(0, float.NegativeInfinity); }, "nonfinite caps are rejected");
        Throws<KeyNotFoundException>(delegate { graph.QueueDemandChange(999, 1.0f); }, "unknown keys are rejected for queued changes");

        graph.QueueSupplyChange(0, 3.0f);
        graph.QueueSupplyChange(1, 2.0f);
        DemandNodeState beforeZero = graph.GetState(0);
        DemandNodeState beforeOne = graph.GetState(1);
        DemandNodeState beforeTwo = graph.GetState(2);
        Throws<ArithmeticException>(delegate { graph.Propagate(5); }, "overflowing link arithmetic is rejected");
        StateEqual(beforeZero, graph.GetState(0), "arithmetic failure rolls back nodes already processed in the pass");
        StateEqual(beforeOne, graph.GetState(1), "arithmetic failure preserves the overflowing source and its pending delta");
        StateEqual(beforeTwo, graph.GetState(2), "arithmetic failure preserves target state");

        DemandGraph sumOverflow = new DemandGraph(Profile(
            Node(1, -float.MaxValue, float.MaxValue, float.MaxValue, 0.0f),
            Node(2, -float.MaxValue, float.MaxValue, float.MaxValue, 0.0f),
            Node(3, -float.MaxValue, float.MaxValue, 0.0f, 0.0f,
                Link(1, DemandLinkKind.Sums, 1.0f),
                Link(2, DemandLinkKind.Sums, 1.0f))));
        DemandNodeState beforeSum = sumOverflow.GetState(1);
        Throws<ArithmeticException>(delegate { sumOverflow.Propagate(5); }, "overflowing committed-value sums are rejected");
        StateEqual(beforeSum, sumOverflow.GetState(1), "sum overflow rolls back the graph atomically");

        DemandGraph capOverflow = new DemandGraph(Profile(
            Node(1, -float.MaxValue, float.MaxValue, 0.0f, 0.0f)));
        capOverflow.SetDemandCap(1, 2.0f);
        Throws<ArithmeticException>(delegate { capOverflow.Propagate(2); }, "overflowing maximum-times-cap arithmetic is rejected");
        Equal(2.0f, capOverflow.GetState(1).DemandCap, "failed active-demand arithmetic preserves the finite input cap");
    }

    private static void TestPassLimitAndArgumentBounds()
    {
        DemandGraph runaway = new DemandGraph(Profile(
            Node(1, -1000000.0f, 1000000.0f, 0.0f, 0.0f,
                Link(2, DemandLinkKind.Satisfies, 1.0f)),
            Node(2, -1000000.0f, 1000000.0f, 0.0f, 0.0f,
                Link(1, DemandLinkKind.Satisfies, 1.0f))));
        runaway.QueueSupplyChange(1, 1.0f);
        DemandPropagationResult bounded = runaway.Propagate(5);
        True(!bounded.Converged, "a two-node cycle reports non-convergence at the configured pass bound");
        Equal(5, bounded.Passes, "non-convergence reports exactly the attempted pass count");
        True(runaway.GetState(1).Supply > 1.0f, "bounded propagation exposes its finite partial progress");
        Equal(1.0f, runaway.GetState(1).PendingSupply, "in-flight lower-key transfer remains pending at the pass bound");
        Throws<ArgumentOutOfRangeException>(delegate { runaway.Propagate(0); }, "zero pass limits are rejected");
        Throws<ArgumentOutOfRangeException>(delegate { runaway.Propagate(1001); }, "pass limits above 1000 are rejected");
    }

    private static void TestProfileCodecRoundTripAndStreamOwnership()
    {
        DemandProfile profile = Profile(
            Node(2, -60.0f, 80.0f, 1.0f, -2.0f, regionUse: 2),
            Node(1, -50.0f, 70.0f, 3.0f, 4.0f, regionUse: 1,
                Link(2, DemandLinkKind.Drives, 0.5f),
                Link(2, DemandLinkKind.Caps, 1.0f),
                Link(2, DemandLinkKind.Sums, 1.0f)));
        profile = new DemandProfile("pinned:perfil-ação", profile.Nodes);
        byte[] bytes;
        using (MemoryStream output = new MemoryStream())
        {
            DemandProfileCodec.Write(output, profile);
            True(output.CanWrite, "codec writer leaves caller stream open");
            bytes = output.ToArray();
        }

        DemandProfile loaded;
        using (MemoryStream input = new MemoryStream(bytes, false))
        {
            loaded = DemandProfileCodec.Read(input);
            True(input.CanRead, "codec reader leaves caller stream open");
            Equal(input.Length, input.Position, "successful codec read consumes the complete record");
        }
        Equal("pinned:perfil-ação", loaded.SourceIdentity, "codec preserves source attribution and UTF-8 text");
        Equal(2, loaded.Nodes.Length, "codec preserves node count");
        Equal(1u, loaded.Nodes[0].Key, "codec returns nodes in canonical key order");
        Equal((byte)1, loaded.Nodes[0].RegionUse, "codec preserves region-use data");
        Equal(3, loaded.Nodes[0].Links.Length, "codec preserves link count and profile link order");
        Equal(DemandLinkKind.Caps, loaded.Nodes[0].Links[1].Kind, "codec preserves link order and type");
        Near(0.5f, loaded.Nodes[0].Links[0].Factor, 0.000001f, "codec preserves link factors");

        byte[] roundTrip;
        using (MemoryStream output = new MemoryStream())
        {
            DemandProfileCodec.Write(output, loaded);
            roundTrip = output.ToArray();
        }
        BytesEqual(bytes, roundTrip, "codec roundtrip is byte-stable");
    }

    private static void TestProfileCodecRejectsInvalidRecords()
    {
        DemandProfile profile = Profile(Node(1, -10.0f, 10.0f, 0.0f, 0.0f));
        byte[] valid;
        using (MemoryStream output = new MemoryStream())
        {
            DemandProfileCodec.Write(output, profile);
            valid = output.ToArray();
        }

        byte[] corrupt = (byte[])valid.Clone();
        corrupt[corrupt.Length - 1] ^= 0x20;
        Throws<InvalidDataException>(delegate { ReadProfile(corrupt); }, "invalid checksum is rejected");

        byte[] truncated = new byte[valid.Length - 1];
        Array.Copy(valid, truncated, truncated.Length);
        Throws<InvalidDataException>(delegate { ReadProfile(truncated); }, "truncated profile is rejected");

        byte[] unknownVersion = (byte[])valid.Clone();
        unknownVersion[4] = 2;
        Throws<InvalidDataException>(delegate { ReadProfile(unknownVersion); }, "unknown profile format version is rejected");

        byte[] trailing = new byte[valid.Length + 1];
        Array.Copy(valid, trailing, valid.Length);
        Throws<InvalidDataException>(delegate { ReadProfile(trailing); }, "trailing bytes are rejected");

        byte[] duplicateKeys = Envelope(BodyWithDuplicateKeys());
        Throws<InvalidDataException>(delegate { ReadProfile(duplicateKeys); }, "duplicate node keys are rejected");

        byte[] danglingLink = Envelope(BodyWithDanglingLink());
        Throws<InvalidDataException>(delegate { ReadProfile(danglingLink); }, "links to missing nodes are rejected");
    }

    private static DemandProfile ReadProfile(byte[] bytes)
    {
        return DemandProfileCodec.Read(new MemoryStream(bytes, false));
    }

    private static byte[] BodyWithDuplicateKeys()
    {
        using (MemoryStream body = new MemoryStream())
        {
            using (BinaryWriter writer = new BinaryWriter(body, Encoding.UTF8, true))
            {
                WriteText(writer, "test");
                writer.Write(2);
                WriteNodeRecord(writer, 1, "first", 0);
                WriteNodeRecord(writer, 1, "duplicate", 0);
            }
            return body.ToArray();
        }
    }

    private static byte[] BodyWithDanglingLink()
    {
        using (MemoryStream body = new MemoryStream())
        {
            using (BinaryWriter writer = new BinaryWriter(body, Encoding.UTF8, true))
            {
                WriteText(writer, "test");
                writer.Write(1);
                writer.Write((uint)1);
                WriteText(writer, "source");
                writer.Write(-10.0f); writer.Write(10.0f); writer.Write(0.0f); writer.Write(0.0f); writer.Write((byte)0);
                writer.Write(1);
                writer.Write((uint)2); writer.Write((byte)DemandLinkKind.Satisfies); writer.Write(1.0f);
            }
            return body.ToArray();
        }
    }

    private static void WriteNodeRecord(BinaryWriter writer, uint key, string name, int linkCount)
    {
        writer.Write(key); WriteText(writer, name);
        writer.Write(-10.0f); writer.Write(10.0f); writer.Write(0.0f); writer.Write(0.0f); writer.Write((byte)0);
        writer.Write(linkCount);
    }

    private static void WriteText(BinaryWriter writer, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static byte[] Envelope(byte[] body)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(0x44453453u);
                writer.Write(1);
                writer.Write(body.Length);
                writer.Write(Crc32(body));
                writer.Write(body);
            }
            return stream.ToArray();
        }
    }

    private static uint Crc32(byte[] bytes)
    {
        uint crc = uint.MaxValue;
        int index;
        for (index = 0; index < bytes.Length; index++)
        {
            crc ^= bytes[index];
            int bit;
            for (bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1u) == 0u ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }

    private static DemandProfile Profile(params DemandNodeDefinition[] nodes)
    {
        return new DemandProfile("test:pinned-reference", nodes);
    }

    private static DemandNodeDefinition Node(uint key, float minimum, float maximum, float supply, float demand,
        params DemandLink[] links)
    {
        return Node(key, minimum, maximum, supply, demand, 0, links);
    }

    private static DemandNodeDefinition Node(uint key, float minimum, float maximum, float supply, float demand,
        byte regionUse, params DemandLink[] links)
    {
        return new DemandNodeDefinition(key, "node-" + key.ToString(), minimum, maximum, supply, demand, regionUse, links);
    }

    private static DemandLink Link(uint targetKey, DemandLinkKind kind, float factor)
    {
        return new DemandLink(targetKey, kind, factor);
    }

    private static void StateEqual(DemandNodeState expected, DemandNodeState actual, string message)
    {
        Equal(expected.Key, actual.Key, message + " key");
        Equal(expected.Supply, actual.Supply, message + " supply");
        Equal(expected.Demand, actual.Demand, message + " demand");
        Equal(expected.PendingSupply, actual.PendingSupply, message + " pending supply");
        Equal(expected.PendingDemand, actual.PendingDemand, message + " pending demand");
        Equal(expected.ActiveDemand, actual.ActiveDemand, message + " active demand");
        Equal(expected.EconomyModifier, actual.EconomyModifier, message + " economy modifier");
        Equal(expected.TaxModifier, actual.TaxModifier, message + " tax modifier");
        Equal(expected.DemandCap, actual.DemandCap, message + " demand cap");
        Equal(expected.NormalizedDemand, actual.NormalizedDemand, message + " normalized demand");
    }

    private static void BytesEqual(byte[] expected, byte[] actual, string message)
    {
        Equal(expected.Length, actual.Length, message + " length");
        int index;
        for (index = 0; index < expected.Length; index++)
        {
            if (expected[index] != actual[index])
                throw new Exception(message + ": byte difference at offset " + index.ToString() + ".");
            _assertions++;
        }
    }

    private static void Near(float expected, float actual, float tolerance, string message)
    {
        _assertions++;
        float difference = Math.Abs(expected - actual);
        if (float.IsNaN(actual) || difference > tolerance)
            throw new Exception(message + ": expected " + expected.ToString("R") + ", got " + actual.ToString("R") + ".");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        _assertions++;
        if (!object.Equals(expected, actual))
            throw new Exception(message + ": expected " + expected + ", got " + actual + ".");
    }

    private static void True(bool condition, string message)
    {
        _assertions++;
        if (!condition)
            throw new Exception(message + ": condition was false.");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        _assertions++;
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        catch (Exception error)
        {
            throw new Exception(message + ": expected " + typeof(T).Name + ", got " + error.GetType().Name + ".", error);
        }
        throw new Exception(message + ": expected " + typeof(T).Name + " but no exception was thrown.");
    }
}
