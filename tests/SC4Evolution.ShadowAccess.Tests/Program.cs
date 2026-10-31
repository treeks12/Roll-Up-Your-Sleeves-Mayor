using System;
using SC4Evolution.Mod.Economy;

internal static class Program
{
    private static int _checks;

    private static int Main()
    {
        try
        {
            DefaultsAndValidation();
            AccessPressure();
            FreightPressure();
            ClampingAndOverflow();
            Determinism();
            Console.WriteLine("PASS: " + _checks + " shadow access/freight assertions; aggregate heuristic only.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private static void DefaultsAndValidation()
    {
        ShadowAccessFreight model = new ShadowAccessFreight();
        Equal(ShadowAccessFreight.DefaultDistanceScaleUnits, model.DistanceScaleUnits, "default distance scale");
        Throws<ArgumentOutOfRangeException>(delegate { new ShadowAccessFreight(0); }, "zero distance scale rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ShadowAccessFreight(-1); }, "negative distance scale rejected");
        Equal(50, ShadowAccessFreight.HeuristicConfidencePercent, "aggregate confidence is explicit");
        Equal(50, ShadowAccessFreight.ConstrainedThresholdPercent, "constraint threshold is explicit");
    }

    private static void AccessPressure()
    {
        ShadowAccessFreight model = new ShadowAccessFreight(100);
        ShadowAccessPressure(model, 0, 0, true, 100, 100, 0, "free access is neutral");
        ShadowAccessPressure(model, 80, 100, true, 100, 0, 66, "traffic and offer shortage add pressure");
        ShadowAccessPressure(model, 0, 0, false, 20, 20, 100, "missing connection is hard access pressure");

        ShadowPressure inactive = model.EvaluateAccess(new ShadowAccessObservation
        {
            TrafficLoadPercent = 100,
            DistanceUnits = 100,
            HasConnection = false,
            DemandUnits = 0,
            OfferUnits = 0
        });
        Equal(0, inactive.PressurePercent, "no access demand has no effect");
        Equal(0, inactive.ConfidencePercent, "no access demand has no confidence");
    }

    private static void FreightPressure()
    {
        ShadowAccessFreight model = new ShadowAccessFreight(100);
        ShadowPressure free = model.EvaluateFreight(new ShadowFreightObservation
        {
            TrafficLoadPercent = 0,
            DistanceUnits = 0,
            HasConnection = true,
            OfferUnits = 20,
            StockUnits = 10,
            StockCapacityUnits = 100
        });
        Equal(2, free.PressurePercent, "small stock with offers is low pressure");
        Equal(50, free.ConfidencePercent, "active freight uses heuristic confidence");
        True((free.Signals & ShadowPressureSignal.Stock) != 0, "freight exposes stock signal");

        ShadowPressure blocked = model.EvaluateFreight(new ShadowFreightObservation
        {
            TrafficLoadPercent = 0,
            DistanceUnits = 0,
            HasConnection = false,
            OfferUnits = 0,
            StockUnits = 10,
            StockCapacityUnits = 10
        });
        Equal(100, blocked.PressurePercent, "missing freight connection is hard pressure");
        True(blocked.IsConstrained, "hard freight pressure is constrained");

        ShadowPressure empty = model.EvaluateFreight(new ShadowFreightObservation
        {
            TrafficLoadPercent = 100,
            DistanceUnits = 100,
            HasConnection = false,
            OfferUnits = 0,
            StockUnits = 0,
            StockCapacityUnits = 0
        });
        Equal(0, empty.PressurePercent, "empty freight stock has no effect");
        Equal(0, empty.ConfidencePercent, "empty freight stock has no confidence");
    }

    private static void ClampingAndOverflow()
    {
        ShadowAccessFreight model = new ShadowAccessFreight(100);
        ShadowPressure access = model.EvaluateAccess(new ShadowAccessObservation
        {
            TrafficLoadPercent = int.MaxValue,
            DistanceUnits = int.MaxValue,
            HasConnection = true,
            DemandUnits = int.MaxValue,
            OfferUnits = 0
        });
        True(access.PressurePercent <= 100, "large access values remain bounded");
        True(access.PressurePercent > 0, "large access values retain pressure");

        ShadowPressure freight = model.EvaluateFreight(new ShadowFreightObservation
        {
            TrafficLoadPercent = -10,
            DistanceUnits = int.MaxValue,
            HasConnection = true,
            OfferUnits = 0,
            StockUnits = int.MaxValue,
            StockCapacityUnits = 1
        });
        True(freight.PressurePercent <= 100, "large freight values remain bounded");
        True(freight.PressurePercent > 0, "large freight values retain pressure");
        Equal(50, freight.ConfidencePercent, "bounded freight remains active");
    }

    private static void Determinism()
    {
        ShadowAccessFreight firstModel = new ShadowAccessFreight(777);
        ShadowAccessFreight secondModel = new ShadowAccessFreight(777);
        ShadowAccessObservation access = new ShadowAccessObservation
        {
            TrafficLoadPercent = 31,
            DistanceUnits = 222,
            HasConnection = true,
            DemandUnits = 40,
            OfferUnits = 13
        };
        ShadowPressure first = firstModel.EvaluateAccess(access);
        ShadowPressure second = secondModel.EvaluateAccess(access);
        Equal(first.PressurePercent, second.PressurePercent, "access pressure deterministic");
        Equal(first.ConfidencePercent, second.ConfidencePercent, "access confidence deterministic");
        Equal(first.Signals, second.Signals, "access signal mask deterministic");
        True(!first.IsConstrained, "moderate sample stays below hard constraint");
    }

    private static void ShadowAccessPressure(ShadowAccessFreight model, int traffic, int distance,
        bool connected, int demand, int offers, int expected, string label)
    {
        ShadowPressure result = model.EvaluateAccess(new ShadowAccessObservation
        {
            TrafficLoadPercent = traffic,
            DistanceUnits = distance,
            HasConnection = connected,
            DemandUnits = demand,
            OfferUnits = offers
        });
        Equal(expected, result.PressurePercent, label);
        Equal(50, result.ConfidencePercent, label + " confidence");
        True((result.Signals & ShadowPressureSignal.Traffic) != 0, label + " traffic signal");
    }

    private static void True(bool value, string label)
    {
        _checks++;
        if (!value) throw new InvalidOperationException("Assertion failed: " + label);
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        _checks++;
        if (!object.Equals(expected, actual))
            throw new InvalidOperationException("Assertion failed: " + label + "; expected " + expected + ", got " + actual);
    }

    private static void Throws<T>(Action action, string label) where T : Exception
    {
        _checks++;
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException("Assertion failed: " + label + "; no " + typeof(T).Name + " was thrown.");
    }
}
