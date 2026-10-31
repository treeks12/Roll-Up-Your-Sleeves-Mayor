using System;
using SC4Evolution.Mod.Economy;
using SC4Evolution.Simulation.Employment;

internal static class Program
{
    private static int _checks;

    private static int Main()
    {
        try
        {
            SegmentKeys();
            QualificationTable();
            InputValidation();
            ZeroCity();
            ConservationAndMismatch();
            NoJobs();
            NoQualifiedWorkers();
            OfficeDoesNotFixOtherEducation();
            VacanciesWithoutResidents();
            ResidentsWithoutVacancies();
            Monotonicity();
            Determinism();
            AdapterOptIn();
            Console.WriteLine("PASS: " + _checks + " employment assertions; pure model + real adapter source, no native execution.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }

    private static void SegmentKeys()
    {
        Equal(3, EmploymentSegments.WealthLevels, "wealth levels");
        Equal(4, EmploymentSegments.EducationLevels, "education levels");
        Equal(12, EmploymentSegments.WorkforceCells, "workforce cells");
        Equal(9, EmploymentSegments.Segments, "job segments");
        Equal("CS$", EmploymentSegments.SegmentName(0), "segment 0 name");
        Equal("CS$$", EmploymentSegments.SegmentName(1), "segment 1 name");
        Equal("CS$$$", EmploymentSegments.SegmentName(2), "segment 2 name");
        Equal("CO$$", EmploymentSegments.SegmentName(3), "segment 3 name");
        Equal("CO$$$", EmploymentSegments.SegmentName(4), "segment 4 name");
        Equal("IR", EmploymentSegments.SegmentName(5), "segment 5 name");
        Equal("ID", EmploymentSegments.SegmentName(6), "segment 6 name");
        Equal("IM", EmploymentSegments.SegmentName(7), "segment 7 name");
        Equal("IHT", EmploymentSegments.SegmentName(8), "segment 8 name");
        Equal(0, EmploymentSegments.TierOfSegment(0), "CS$ tier");
        Equal(0, EmploymentSegments.TierOfSegment(5), "IR tier");
        Equal(0, EmploymentSegments.TierOfSegment(6), "ID tier");
        Equal(1, EmploymentSegments.TierOfSegment(1), "CS$$ tier");
        Equal(1, EmploymentSegments.TierOfSegment(3), "CO$$ tier");
        Equal(1, EmploymentSegments.TierOfSegment(7), "IM tier");
        Equal(2, EmploymentSegments.TierOfSegment(2), "CS$$$ tier");
        Equal(2, EmploymentSegments.TierOfSegment(4), "CO$$$ tier");
        Equal(2, EmploymentSegments.TierOfSegment(8), "IHT tier");
        Equal(0, EmploymentSegments.CellIndex(0, 0), "cell W$ EQ1");
        Equal(5, EmploymentSegments.CellIndex(1, 1), "cell W$$ EQ2");
        Equal(11, EmploymentSegments.CellIndex(2, 3), "cell W$$$ EQ4");
        Equal(2, EmploymentSegments.WealthOfCell(11), "wealth of last cell");
        Equal(3, EmploymentSegments.EducationOfCell(11), "education of last cell");
        Equal("W$$$ EQ4", EmploymentSegments.CellName(11), "cell name");
        Throws<ArgumentOutOfRangeException>(delegate { EmploymentSegments.CellIndex(3, 0); }, "wealth above range rejected");
        Throws<ArgumentOutOfRangeException>(delegate { EmploymentSegments.CellIndex(0, 4); }, "education above range rejected");
        Throws<ArgumentOutOfRangeException>(delegate { EmploymentSegments.SegmentName(9); }, "segment above range rejected");
        Throws<ArgumentNullException>(delegate { EmploymentSegments.ValidateCellVector(null, "x"); }, "null cell vector rejected");
        Throws<ArgumentException>(delegate { EmploymentSegments.ValidateCellVector(new int[11], "x"); }, "short cell vector rejected");
        Throws<ArgumentException>(delegate { EmploymentSegments.ValidateSegmentVector(new int[10], "x"); }, "long segment vector rejected");
    }

    private static void QualificationTable()
    {
        EmploymentQualification q = EmploymentQualification.Default;
        int[] expected = new[] { 0, 1, 2, 1, 2, 0, 0, 1, 3 };
        for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
            Equal(expected[segment], q.MinimumEducation(segment), "default floor " + EmploymentSegments.SegmentName(segment));
        // Monotone in education: every cell eligible at education e is eligible at e+1 for each segment.
        for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
            for (int education = 0; education < 3; education++)
                True(!q.IsEligible(segment, EmploymentSegments.CellIndex(0, education)) ||
                     q.IsEligible(segment, EmploymentSegments.CellIndex(0, education + 1)),
                    "eligibility monotone in education for " + EmploymentSegments.SegmentName(segment));
        // Wealth never gates eligibility.
        for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
            for (int education = 0; education < 4; education++)
                True(q.IsEligible(segment, EmploymentSegments.CellIndex(0, education)) ==
                     q.IsEligible(segment, EmploymentSegments.CellIndex(2, education)),
                    "wealth does not gate eligibility " + EmploymentSegments.SegmentName(segment));
        // Education floor really blocks: education 0 is never eligible where floor > 0.
        for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
            Equal(expected[segment] == 0, q.IsEligible(segment, EmploymentSegments.CellIndex(0, 0)),
                "education floor blocks " + EmploymentSegments.SegmentName(segment));
        int[] custom = new int[9];
        custom[4] = 1;
        EmploymentQualification injected = new EmploymentQualification(custom);
        Equal(1, injected.MinimumEducation(4), "injected floor CO$$$");
        True(injected.IsEligible(4, EmploymentSegments.CellIndex(0, 1)), "injected eligibility low");
        True(!injected.IsEligible(4, EmploymentSegments.CellIndex(0, 0)), "injected eligibility floor");
        custom[4] = 3; // mutating the source array must not affect the instance
        Equal(1, injected.MinimumEducation(4), "qualification clones its table");
        Throws<ArgumentNullException>(delegate { new EmploymentQualification(null); }, "null table rejected");
        Throws<ArgumentException>(delegate { new EmploymentQualification(new int[8]); }, "short table rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new EmploymentQualification(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 4 }); }, "floor above range rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new EmploymentQualification(new[] { -1, 0, 0, 0, 0, 0, 0, 0, 0 }); }, "negative floor rejected");
        True(q.ToString().Contains("IHT>=EQ4"), "table text lists IHT floor");
    }

    private static void InputValidation()
    {
        int[] workforce = new int[12], capacity = new int[9], filled = new int[9];
        workforce[0] = 5; capacity[0] = 4; filled[0] = 2;
        EmploymentMarketInput input = new EmploymentMarketInput(7, workforce, capacity, filled, 1, 0, 3, true);
        workforce[0] = 99; capacity[0] = 99; filled[0] = 99;
        Equal(5, input.WorkforceByCell[0], "input clones workforce");
        Equal(4, input.JobCapacityBySegment[0], "input clones capacity");
        Equal(2, input.JobFilledBySegment[0], "input clones filled");
        Equal(7u, input.Frame, "input frame");
        Equal(3, input.AvailableHomeUnits, "input homes");
        True(input.EntryPathOpen, "input entry path");
        Throws<ArgumentNullException>(delegate { new EmploymentMarketInput(0, null, capacity, filled, 0, 0, 0, true); }, "null workforce rejected");
        Throws<ArgumentException>(delegate { new EmploymentMarketInput(0, new int[12], new int[8], filled, 0, 0, 0, true); }, "short capacity rejected");
        Throws<ArgumentException>(delegate { new EmploymentMarketInput(0, new int[12], capacity, new int[10], 0, 0, 0, true); }, "long filled rejected");
        int[] negative = new int[12]; negative[3] = -1;
        Throws<ArgumentException>(delegate { new EmploymentMarketInput(0, negative, capacity, filled, 0, 0, 0, true); }, "negative workforce rejected");
        int[] negativeJobs = new int[9]; negativeJobs[2] = -1;
        Throws<ArgumentException>(delegate { new EmploymentMarketInput(0, new int[12], negativeJobs, filled, 0, 0, 0, true); }, "negative capacity rejected");
        int[] negativeFilled = new int[9]; negativeFilled[2] = -1;
        Throws<ArgumentException>(delegate { new EmploymentMarketInput(0, new int[12], capacity, negativeFilled, 0, 0, 0, true); }, "negative filled rejected");
        Throws<ArgumentException>(delegate { new EmploymentMarketInput(0, new int[12], capacity, filled, -1, 0, 0, true); }, "negative unmapped capacity rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new EmploymentMarketInput(0, new int[12], capacity, filled, 0, 0, -1, true); }, "negative homes rejected");
        Throws<ArgumentNullException>(delegate { new EmploymentMarket(null); }, "null market qualification rejected");
        EmploymentMarket market = new EmploymentMarket(EmploymentQualification.Default);
        Throws<ArgumentNullException>(delegate { market.Apply(null); }, "null input rejected");
    }

    private static void ZeroCity()
    {
        EmploymentMarket market = new EmploymentMarket(EmploymentQualification.Default);
        EmploymentMarketReport report = market.Apply(new EmploymentMarketInput(1, new int[12], new int[9], new int[9], 0, 0, 0, false));
        Equal(0, report.TotalEmployed(), "zero city employed");
        Equal(0, report.TotalUnemployed(), "zero city unemployed");
        Equal(0, report.TotalImmigrationGranted(), "zero city immigration");
        Equal(0, report.FilledAboveCapacitySegments, "zero city anomalies");
        for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++)
            Equal(ImmigrationBlockReason.None, report.ImmigrationReasonByCell[cell], "zero city reason " + EmploymentSegments.CellName(cell));
    }

    private static void ConservationAndMismatch()
    {
        int[] workforce = new int[12], capacity = new int[9], filled = new int[9];
        workforce[EmploymentSegments.CellIndex(0, 0)] = 30; // W$ EQ1
        workforce[EmploymentSegments.CellIndex(1, 2)] = 20; // W$$ EQ3
        workforce[EmploymentSegments.CellIndex(2, 3)] = 10; // W$$$ EQ4
        capacity[6] = 25; filled[6] = 25; // ID hires EQ1 exactly
        capacity[4] = 15; filled[4] = 12; // CO$$$ needs EQ3; EQ3/EQ4 exist
        capacity[8] = 40; filled[8] = 35; // IHT needs EQ4; only 10 EQ4 workers exist
        EmploymentMarket market = new EmploymentMarket(EmploymentQualification.Default);
        EmploymentMarketReport report = market.Apply(new EmploymentMarketInput(2, workforce, capacity, filled, 0, 0, 4, true));
        Equal(25, report.ModelFilledBySegment[6], "ID fully allocated");
        Equal(0, report.UnfilledObservedFilledBySegment[6], "ID no mismatch");
        Equal(12, report.ModelFilledBySegment[4], "CO$$$ fully allocated");
        Equal(10, report.ModelFilledBySegment[8], "IHT limited by EQ4 workforce");
        Equal(25, report.UnfilledObservedFilledBySegment[8], "IHT mismatch explicit");
        Equal(35, report.ModelFilledBySegment[8] + report.UnfilledObservedFilledBySegment[8], "IHT conservation target split");
        int workforceTotal = 0;
        for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++) workforceTotal += workforce[cell];
        Equal(workforceTotal, report.TotalEmployed() + report.TotalUnemployed(), "workforce conservation");
        for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
            Equal(filled[segment], report.ModelFilledBySegment[segment] + report.UnfilledObservedFilledBySegment[segment],
                "segment conservation " + EmploymentSegments.SegmentName(segment));
        // Workforce drop reconciles: EQ1 workers halve; ID then rehires from the still-free
        // eligible EQ3 workforce before reporting the remaining mismatch.
        workforce[EmploymentSegments.CellIndex(0, 0)] = 12;
        EmploymentMarketReport after = market.Apply(new EmploymentMarketInput(3, workforce, capacity, filled, 0, 0, 4, true));
        Equal(20, after.ModelFilledBySegment[6], "ID rehires eligible workforce after drop");
        Equal(5, after.UnfilledObservedFilledBySegment[6], "ID mismatch after drop");
        Equal(12, after.EmployedByCell[EmploymentSegments.CellIndex(0, 0)], "EQ1 fully employed after drop");
        Equal(0, after.UnemployedByCell[EmploymentSegments.CellIndex(0, 0)], "EQ1 unemployment after drop");
        Equal(0, after.UnemployedByCell[EmploymentSegments.CellIndex(1, 2)], "EQ3 absorbed by ID after drop");
        // Filled above capacity is reported, never silently clamped.
        int[] overflowFilled = new int[9]; overflowFilled[0] = 5;
        EmploymentMarketReport overflow = market.Apply(new EmploymentMarketInput(4, new int[12], new int[9], overflowFilled, 0, 0, 0, true));
        Equal(1, overflow.FilledAboveCapacitySegments, "filled above capacity counted");
        Equal(0, overflow.VacantBySegment[0], "vacancy floored at zero");
    }

    private static void NoJobs()
    {
        int[] workforce = new int[12];
        workforce[EmploymentSegments.CellIndex(0, 0)] = 100; // 100 uneducated workers
        EmploymentMarket market = new EmploymentMarket(EmploymentQualification.Default);
        EmploymentMarketReport report = market.Apply(new EmploymentMarketInput(5, workforce, new int[9], new int[9], 0, 0, 0, false));
        Equal(100, report.UnemployedByCell[EmploymentSegments.CellIndex(0, 0)], "no jobs: all EQ1 unemployed");
        Equal(100, report.SectorGrowthPressureBySegment[0], "no jobs: CS$ labor pressure");
        Equal(100, report.SectorGrowthPressureBySegment[5], "no jobs: IR labor pressure");
        Equal(100, report.SectorGrowthPressureBySegment[6], "no jobs: ID labor pressure");
        Equal(0, report.SectorGrowthPressureBySegment[4], "no jobs: CO$$$ no EQ1 pressure");
        Equal(0, report.TotalImmigrationGranted(), "no jobs: no immigration without vacancies");
    }

    private static void NoQualifiedWorkers()
    {
        int[] workforce = new int[12], capacity = new int[9], filled = new int[9];
        workforce[EmploymentSegments.CellIndex(0, 0)] = 50; // only EQ1 (education 0) residents
        capacity[4] = 50; filled[4] = 20; // CO$$$ floor EQ3
        EmploymentMarket market = new EmploymentMarket(EmploymentQualification.Default);
        EmploymentMarketReport report = market.Apply(new EmploymentMarketInput(6, workforce, capacity, filled, 0, 0, 0, false));
        Equal(50, report.UnemployedByCell[EmploymentSegments.CellIndex(0, 0)], "CO$$$ cannot employ EQ1");
        Equal(0, report.ModelFilledBySegment[4], "CO$$$ model filled zero");
        Equal(20, report.UnfilledObservedFilledBySegment[4], "CO$$$ observed filled mismatch explicit");
        Equal(30, report.VacantBySegment[4], "CO$$$ vacancies observed");
        Equal(30, report.UnfillableVacanciesBySegment[4], "CO$$$ vacancies unfillable without EQ3");
        int demandCell = EmploymentSegments.CellIndex(2, 2); // CO$$$ tier 2, floor education 2
        Equal(30, report.ImmigrationDemandByCell[demandCell], "CO$$$ asks for W$$$ EQ3 immigrants");
        Equal(30, report.ResidentialPressureByCell[demandCell], "CO$$$ housing pressure W$$$ EQ3");
        // Adding MORE office capacity changes nothing for the uneducated unemployed.
        capacity[4] = 150; filled[4] = 20;
        EmploymentMarketReport more = market.Apply(new EmploymentMarketInput(7, workforce, capacity, filled, 0, 0, 0, false));
        Equal(50, more.UnemployedByCell[EmploymentSegments.CellIndex(0, 0)], "more CO$$$ never fixes EQ1 unemployment");
        Equal(130, more.UnfillableVacanciesBySegment[4], "more CO$$$ raises unfillable count only");
    }

    private static void OfficeDoesNotFixOtherEducation()
    {
        int[] workforce = new int[12], capacity = new int[9], filled = new int[9];
        workforce[EmploymentSegments.CellIndex(0, 1)] = 40; // EQ2 (education 1) workers
        capacity[0] = 20; filled[0] = 20; // CS$ absorbs 20
        EmploymentMarket market = new EmploymentMarket(EmploymentQualification.Default);
        EmploymentMarketReport before = market.Apply(new EmploymentMarketInput(8, workforce, capacity, filled, 0, 0, 0, false));
        Equal(20, before.UnemployedByCell[EmploymentSegments.CellIndex(0, 1)], "EQ2 partial unemployment before office");
        // New CO$$ office: EQ1-floor jobs (education 1) — EQ2 IS eligible, so it may absorb;
        // the handoff case is education BELOW the floor, checked with IHT (floor 3) next.
        capacity[8] = 500; filled[8] = 500; // IHT floor EQ4
        EmploymentMarketReport after = market.Apply(new EmploymentMarketInput(9, workforce, capacity, filled, 0, 0, 0, false));
        Equal(20, after.UnemployedByCell[EmploymentSegments.CellIndex(0, 1)], "IHT boom never employs EQ2");
        Equal(0, after.ModelFilledBySegment[8], "IHT boom allocates nothing to EQ2");
        Equal(500, after.UnfilledObservedFilledBySegment[8], "IHT boom observed filled mismatch explicit");
        // Same with CO$$$ (floor EQ3) for an education-0 city.
        int[] eq1Only = new int[12]; eq1Only[EmploymentSegments.CellIndex(1, 0)] = 60;
        EmploymentMarket officeCity = new EmploymentMarket(EmploymentQualification.Default);
        int[] officeCapacity = new int[9], officeFilled = new int[9];
        officeCapacity[3] = 300; officeFilled[3] = 0; // CO$$ floor EQ2
        EmploymentMarketReport officeReport = officeCity.Apply(new EmploymentMarketInput(10, eq1Only, officeCapacity, officeFilled, 0, 0, 0, false));
        Equal(60, officeReport.UnemployedByCell[EmploymentSegments.CellIndex(1, 0)], "CO$$ vacancies never fix education-0 unemployment");
        Equal(300, officeReport.UnfillableVacanciesBySegment[3], "CO$$ fully unfillable for education 0");
    }

    private static void VacanciesWithoutResidents()
    {
        int[] capacity = new int[9], filled = new int[9];
        capacity[0] = 40; filled[0] = 10; // CS$ 30 vacancies, no residents at all
        EmploymentMarket market = new EmploymentMarket(EmploymentQualification.Default);
        int cell = EmploymentSegments.CellIndex(0, 0); // CS$ tier 0, floor 0
        // Entry path open, housing limits the grant.
        EmploymentMarketReport partial = market.Apply(new EmploymentMarketInput(11, new int[12], capacity, filled, 0, 0, 5, true));
        Equal(30, partial.ImmigrationDemandByCell[cell], "CS$ immigration demand");
        Equal(5, partial.ImmigrationGrantByCell[cell], "housing limits grant");
        Equal(ImmigrationBlockReason.PartialResidentialCapacity, partial.ImmigrationReasonByCell[cell], "partial reason");
        Equal(5, partial.HomeUnitsUsedByImmigration, "homes used equal grant");
        // No housing at all.
        EmploymentMarketReport noHomes = market.Apply(new EmploymentMarketInput(12, new int[12], capacity, filled, 0, 0, 0, true));
        Equal(0, noHomes.ImmigrationGrantByCell[cell], "no homes: zero grant");
        Equal(ImmigrationBlockReason.BlockedNoResidentialCapacity, noHomes.ImmigrationReasonByCell[cell], "no homes reason");
        // Entry path closed even with housing.
        EmploymentMarketReport noPath = market.Apply(new EmploymentMarketInput(13, new int[12], capacity, filled, 0, 0, 99, false));
        Equal(0, noPath.ImmigrationGrantByCell[cell], "closed entry: zero grant");
        Equal(ImmigrationBlockReason.BlockedNoEntryPath, noPath.ImmigrationReasonByCell[cell], "closed entry reason");
        Equal(0, noPath.HomeUnitsUsedByImmigration, "closed entry keeps homes unused");
        // Grants never exceed available homes across several demanding segments.
        int[] multiCapacity = new int[9], multiFilled = new int[9];
        multiCapacity[0] = 30; multiCapacity[4] = 25; multiCapacity[8] = 15; // demand 30 + 25 + 15
        EmploymentMarketReport multi = market.Apply(new EmploymentMarketInput(14, new int[12], multiCapacity, multiFilled, 0, 0, 40, true));
        Equal(40, multi.HomeUnitsUsedByImmigration, "total grants bounded by homes");
        Equal(40, multi.TotalImmigrationGranted(), "granted equals homes when demand exceeds");
        Equal(30, multi.ImmigrationGrantByCell[EmploymentSegments.CellIndex(0, 0)], "CS$ demand granted in cell order");
        Equal(10, multi.ImmigrationGrantByCell[EmploymentSegments.CellIndex(2, 2)], "CO$$$ partial after homes run low");
        Equal(0, multi.ImmigrationGrantByCell[EmploymentSegments.CellIndex(2, 3)], "IHT blocked once homes exhausted");
        True(multi.ImmigrationReasonByCell[EmploymentSegments.CellIndex(0, 0)] != ImmigrationBlockReason.None ||
             multi.ImmigrationGrantByCell[EmploymentSegments.CellIndex(0, 0)] > 0, "CS$ cell has an outcome");
        // Vacancies alone are not success: unemployment of residents stays zero, nothing applied.
        Equal(0, multi.TotalUnemployed(), "no residents means no unemployment");
    }

    private static void ResidentsWithoutVacancies()
    {
        int[] workforce = new int[12];
        workforce[EmploymentSegments.CellIndex(1, 1)] = 30; // W$$ EQ2
        EmploymentMarket market = new EmploymentMarket(EmploymentQualification.Default);
        EmploymentMarketReport report = market.Apply(new EmploymentMarketInput(15, workforce, new int[9], new int[9], 0, 0, 10, true));
        Equal(30, report.TotalUnemployed(), "residents without vacancies stay unemployed");
        Equal(0, report.TotalImmigrationGranted(), "no vacancies: no immigration");
        for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
            Equal(0, report.VacantBySegment[segment], "no capacity anywhere");
        // Housing exists but there is nothing to immigrate for.
        for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++)
            Equal(ImmigrationBlockReason.None, report.ImmigrationReasonByCell[cell], "no demand reason " + EmploymentSegments.CellName(cell));
    }

    private static void Monotonicity()
    {
        for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
        {
            int[] workforce = new int[12], capacity = new int[9], filled = new int[9];
            workforce[EmploymentSegments.CellIndex(0, 0)] = 10;
            workforce[EmploymentSegments.CellIndex(0, 1)] = 10;
            workforce[EmploymentSegments.CellIndex(1, 2)] = 10;
            workforce[EmploymentSegments.CellIndex(2, 3)] = 10;
            capacity[segment] = 30; filled[segment] = 5;
            EmploymentMarket market = new EmploymentMarket(EmploymentQualification.Default);
            EmploymentMarketReport before = market.Apply(new EmploymentMarketInput(16, workforce, capacity, filled, 0, 0, 0, false));
            filled[segment] = 25;
            EmploymentMarketReport after = market.Apply(new EmploymentMarketInput(17, workforce, capacity, filled, 0, 0, 0, false));
            for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++)
                True(after.UnemployedByCell[cell] <= before.UnemployedByCell[cell],
                    "filling " + EmploymentSegments.SegmentName(segment) + " never raises unemployment of " + EmploymentSegments.CellName(cell));
            Equal(before.TotalUnemployed() - after.TotalUnemployed(),
                after.TotalEmployed() - before.TotalEmployed(), "unemployment drop equals employment rise " + EmploymentSegments.SegmentName(segment));
            // Capacity-only growth changes no employment at all.
            int[] bigCapacity = (int[])capacity.Clone(); bigCapacity[segment] = 300;
            EmploymentMarketReport built = market.Apply(new EmploymentMarketInput(18, workforce, bigCapacity, filled, 0, 0, 0, false));
            Equal(after.TotalEmployed(), built.TotalEmployed(), "capacity alone hires nobody " + EmploymentSegments.SegmentName(segment));
            True(built.VacantBySegment[segment] >= after.VacantBySegment[segment], "capacity grows vacancies " + EmploymentSegments.SegmentName(segment));
        }
    }

    private static void Determinism()
    {
        int[] workforce = new int[12], capacity = new int[9], filled = new int[9];
        workforce[EmploymentSegments.CellIndex(0, 0)] = 7;
        workforce[EmploymentSegments.CellIndex(2, 1)] = 3;
        capacity[0] = 6; capacity[4] = 5; filled[0] = 2; filled[4] = 2;
        EmploymentMarket first = new EmploymentMarket(EmploymentQualification.Default);
        EmploymentMarket second = new EmploymentMarket(EmploymentQualification.Default);
        for (uint frame = 1; frame <= 5; frame++)
        {
            EmploymentMarketReport a = first.Apply(new EmploymentMarketInput(frame, workforce, capacity, filled, 1, 1, 4, true));
            EmploymentMarketReport b = second.Apply(new EmploymentMarketInput(frame, workforce, capacity, filled, 1, 1, 4, true));
            Equal(a.Frame, b.Frame, "frames align");
            for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++)
            {
                Equal(a.UnemployedByCell[cell], b.UnemployedByCell[cell], "deterministic unemployment " + EmploymentSegments.CellName(cell));
                Equal(a.ImmigrationGrantByCell[cell], b.ImmigrationGrantByCell[cell], "deterministic grant " + EmploymentSegments.CellName(cell));
                Equal((int)a.ImmigrationReasonByCell[cell], (int)b.ImmigrationReasonByCell[cell], "deterministic reason " + EmploymentSegments.CellName(cell));
            }
            for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
                Equal(a.ModelFilledBySegment[segment], b.ModelFilledBySegment[segment], "deterministic filled " + EmploymentSegments.SegmentName(segment));
            // Evolve the same synthetic history on both markets.
            filled[0] += 1; filled[4] = Math.Max(0, filled[4] - 1);
        }
    }

    private static void AdapterOptIn()
    {
        True(EmploymentSegmentSession.TryCreate(false, EmploymentQualification.Default) == null, "disabled session is null");
        EmploymentSegmentSession session = EmploymentSegmentSession.TryCreate(true, EmploymentQualification.Default);
        True(session != null, "enabled session exists");
        Equal(EmploymentQualification.Default.MinimumEducation(8),
            session.Qualification.MinimumEducation(8), "session exposes qualification");
        int[] workforce = new int[12], capacity = new int[9], filled = new int[9];
        workforce[EmploymentSegments.CellIndex(0, 0)] = 10;
        capacity[6] = 8; filled[6] = 8;
        EmploymentMarketReport report = session.Apply(99, workforce, capacity, filled, 2, 1, 6, true);
        Equal(8, report.TotalEmployed(), "adapter runs the real model");
        Equal(2, report.UnemployedByCell[EmploymentSegments.CellIndex(0, 0)], "adapter unemployment");
        Equal(2, report.UnmappedJobCapacity, "adapter passes unmapped capacity");
        Equal(1, report.UnmappedFilledJobs, "adapter passes unmapped filled");
        Equal(99u, report.Frame, "adapter frame");
        Throws<ArgumentNullException>(delegate { EmploymentSegmentSession.TryCreate(true, null); }, "adapter rejects null qualification");
        Throws<ArgumentException>(delegate { session.Apply(1, new int[11], capacity, filled, 0, 0, 0, true); }, "adapter validates workforce length");
    }

    private static void Equal(int expected, int actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(uint expected, uint actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(string expected, string actual, string label)
    {
        _checks++;
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException(label + ": expected '" + expected + "', got '" + actual + "'.");
    }

    private static void Equal(ImmigrationBlockReason expected, ImmigrationBlockReason actual, string label)
    {
        _checks++;
        if (expected != actual)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(bool expected, bool actual, string label)
    {
        _checks++;
        if (expected != actual)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void True(bool condition, string label)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(label + ": condition false.");
    }

    private static void Throws<TException>(Action action, string label) where TException : Exception
    {
        _checks++;
        try { action(); }
        catch (TException) { return; }
        catch (Exception error)
        {
            throw new InvalidOperationException(label + ": wrong exception type " + error.GetType().Name + ".");
        }
        throw new InvalidOperationException(label + ": no exception thrown.");
    }
}
