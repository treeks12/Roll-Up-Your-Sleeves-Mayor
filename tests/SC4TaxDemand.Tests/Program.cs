using System;
using System.IO;
using SC4Evolution.Simulation;

internal static class Program
{
    private static int _checks;

    private static int Main()
    {
        try
        {
            Run("response curve clamps and interpolates", TestCurveInterpolationAndClamping);
            Run("one-point and odd trailing values follow the curve loader", TestSinglePointAndOddTrailingValue);
            Run("duplicate x values produce the documented vertical step", TestVerticalStep);
            Run("non-finite slopes and interpolation results reject while downward vertical drops remain valid", TestFiniteCurveSafety);
            Run("curve rejects absent, non-finite, oversized, and descending input", TestInvalidCurves);
            Run("percent-facing tax ratio drives a hand-computed synthetic curve", TestTaxFormulaAndSyntheticCurve);
            Run("neutral floor and negative curve output are retained", TestNeutralFloorAndNegativeOutput);
            Run("rate history is a maximum over an inclusive rolling window", TestRollingMaximumAndExpiration);
            Run("history excludes future samples and zero-day windows are supported", TestHistoryWindowEdges);
            Run("history rejects invalid and out-of-order samples", TestInvalidHistory);
            Run("history-based calculation uses tuning window and documented empty-history fallback", TestHistoryCalculationAndFallback);
            Run("tuning requires both valid curves and bounded provenance", TestTuningValidation);
            Run("tuning codec round-trips and leaves caller streams open", TestCodecRoundTrip);
            Run("tuning codec rejects corruption, truncation, trailing data, and versions", TestCodecRejectsInvalidData);
            Console.WriteLine("PASS: " + _checks + " tax-demand assertions across 14 scenarios.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private static void Run(string name, Action test)
    {
        test();
        Console.WriteLine("PASS " + name);
    }

    private static void TestCurveInterpolationAndClamping()
    {
        TaxResponseCurve curve = new TaxResponseCurve(new float[] { 0.0f, 9.0f, 100.0f, 7.0f });
        Near(9.0f, curve.Evaluate(-1.0f), 0.000001f, "left clamp");
        Near(9.0f, curve.Evaluate(0.0f), 0.000001f, "first point");
        Near(8.0f, curve.Evaluate(50.0f), 0.000001f, "midpoint interpolation");
        Near(7.0f, curve.Evaluate(100.0f), 0.000001f, "last point");
        Near(7.0f, curve.Evaluate(101.0f), 0.000001f, "right clamp");
    }

    private static void TestSinglePointAndOddTrailingValue()
    {
        TaxResponseCurve curve = new TaxResponseCurve(new float[] { 2.0f, 3.25f, float.NaN });
        Equal(1, curve.PointCount, "odd trailing value does not form a point");
        Near(3.25f, curve.Evaluate(-1000.0f), 0.000001f, "single point is constant at the left");
        Near(3.25f, curve.Evaluate(1000.0f), 0.000001f, "single point is constant at the right");
    }

    private static void TestVerticalStep()
    {
        TaxResponseCurve curve = new TaxResponseCurve(new float[]
        {
            0.0f, 0.0f, 5.0f, 10.0f, 5.0f, 20.0f, 10.0f, 30.0f
        });
        Near(9.998f, curve.Evaluate(4.999f), 0.00001f, "value just before duplicate x");
        Near(20.0f, curve.Evaluate(5.0f), 0.000001f, "exact duplicate x selects the later value");
        Near(25.0f, curve.Evaluate(7.5f), 0.000001f, "interpolation after vertical step");
    }

    private static void TestFiniteCurveSafety()
    {
        Throws<ArgumentException>(delegate
        {
            new TaxResponseCurve(new float[] { 0.0f, 0.0f, 1.0e-40f, -1.0f });
        }, "non-vertical segment with an infinite slope");

        TaxResponseCurve downwardVerticalDrop = new TaxResponseCurve(new float[]
        {
            0.0f, 0.0f, 5.0f, 10.0f, 5.0f, 0.0f, 10.0f, 10.0f
        });
        Near(9.998f, downwardVerticalDrop.Evaluate(4.999f), 0.00001f, "value before duplicate-X downward drop");
        Near(0.0f, downwardVerticalDrop.Evaluate(5.0f), 0.000001f, "duplicate-X downward drop remains reachable at its boundary");
        Near(5.0f, downwardVerticalDrop.Evaluate(7.5f), 0.000001f, "interpolation after duplicate-X downward drop");

        TaxResponseCurve finiteSegments = new TaxResponseCurve(new float[]
        {
            0.0f, 0.0f, 2.0e28f, 3.0e38f, 6.0e28f, -4.0e37f
        });
        Throws<ArithmeticException>(delegate { finiteSegments.Evaluate(0.0f); }, "overflowing interpolation result");
    }

    private static void TestInvalidCurves()
    {
        Throws<ArgumentException>(delegate { new TaxResponseCurve(new float[0]); }, "empty curve");
        Throws<ArgumentOutOfRangeException>(delegate { new TaxResponseCurve(new float[] { 0.0f, float.PositiveInfinity }); }, "non-finite curve value");
        Throws<ArgumentException>(delegate { new TaxResponseCurve(new float[] { 0.0f, 1.0f, 5.0f, 2.0f, 4.0f, 3.0f }); }, "descending x");
        Throws<ArgumentOutOfRangeException>(delegate { new TaxResponseCurve(new float[] { 0.0f, 1.0f }).Evaluate(float.NaN); }, "non-finite curve input");
        Throws<ArgumentOutOfRangeException>(delegate { new TaxResponseCurve(new float[TaxResponseCurve.MaximumValues + 1]); }, "maximum value count");
    }

    private static void TestTaxFormulaAndSyntheticCurve()
    {
        TaxDemandTuning tuning = CreateTuning(12.5f, 90u);
        Near(12.5f, TaxDemandModel.EvaluateNeutralTaxRatePercent(tuning, 0.0f), 0.000001f, "neutral curve population response");
        Near(1.0f, TaxDemandModel.CalculateRateVarianceRatio(9.0f, 9.0f), 0.000001f, "plain 9 percent gives ratio 1, not internal 90");

        float ratio = TaxDemandModel.CalculateRateVarianceRatio(9.0f, 7.0f);
        Near(0.7777778f, ratio, 0.000001f, "public 7 percent is compared with a 9 percent neutral rate");
        float taxMod = TaxDemandModel.CalculateTaxModifier(tuning, 9.0f, 7.0f);
        Near(0.4444444f, taxMod, 0.000001f, "synthetic curve y=2-2x at the computed ratio");
        Throws<ArgumentOutOfRangeException>(delegate { TaxDemandModel.CalculateRateVarianceRatio(9.0f, float.NaN); }, "non-finite tax rate");
    }

    private static void TestNeutralFloorAndNegativeOutput()
    {
        TaxDemandTuning tuning = new TaxDemandTuning(
            new float[] { 0.0f, 0.05f },
            new float[] { 0.0f, 2.0f, 2.0f, -2.0f },
            90u,
            "synthetic test tuning");
        Near(2.0f, TaxDemandModel.CalculateRateVarianceRatio(0.05f, 0.2f), 0.000001f, "neutral rate floor at 0.1 percent");
        Near(-2.0f, TaxDemandModel.CalculateTaxModifier(tuning, 0.05f, 0.2f), 0.000001f, "negative modifier is preserved");
        Near(2.0f, tuning.TaxModifierByRateVariance.Evaluate(-1.0f), 0.000001f, "negative ratio uses curve's left clamp");
    }

    private static void TestRollingMaximumAndExpiration()
    {
        TaxRateHistory history = new TaxRateHistory();
        history.Record(10, 8.0f);
        history.Record(11, 7.0f);
        history.Record(100, 6.0f);

        float maximum;
        True(history.TryGetMaximumRatePercent(100, 90u, out maximum), "window has samples");
        Near(8.0f, maximum, 0.000001f, "high rate remains in the inclusive 90-day window");
        True(history.TryGetMaximumRatePercent(101, 90u, out maximum), "window still has later samples");
        Near(7.0f, maximum, 0.000001f, "high rate expires after its first day leaves the window");
        True(history.TryGetMaximumRatePercent(100, 0u, out maximum), "zero-day window includes today");
        Near(6.0f, maximum, 0.000001f, "a zero-day window excludes prior days");
    }

    private static void TestHistoryWindowEdges()
    {
        TaxRateHistory history = new TaxRateHistory();
        history.Record(5, 9.0f);
        history.Record(5, 10.0f);
        history.Record(12, 8.0f);

        float maximum;
        True(history.TryGetMaximumRatePercent(5, 0u, out maximum), "same-day samples included");
        Near(10.0f, maximum, 0.000001f, "multiple rates on one day use maximum");
        True(!history.TryGetMaximumRatePercent(11, 0u, out maximum), "future sample excluded from query");
        True(history.TryGetMaximumRatePercent(12, 0u, out maximum), "today's sample is included");
        Near(8.0f, maximum, 0.000001f, "today's rate returned");
        True(!history.TryGetMaximumRatePercent(0, uint.MaxValue, out maximum), "empty range is explicit");
    }

    private static void TestInvalidHistory()
    {
        TaxRateHistory history = new TaxRateHistory();
        Throws<ArgumentOutOfRangeException>(delegate { history.Record(-1, 5.0f); }, "negative day");
        Throws<ArgumentOutOfRangeException>(delegate { history.Record(0, float.NaN); }, "non-finite sample rate");
        history.Record(3, 5.0f);
        Throws<ArgumentException>(delegate { history.Record(2, 5.0f); }, "out-of-order sample");
        Throws<ArgumentOutOfRangeException>(delegate { history.TryGetMaximumRatePercent(-1, 90u, out _); }, "negative query day");
    }

    private static void TestHistoryCalculationAndFallback()
    {
        TaxDemandTuning tuning = CreateTuning(12.5f, 10u);
        TaxRateHistory history = new TaxRateHistory();
        history.Record(10, 7.0f);
        history.Record(11, 5.0f);

        Near(0.4444444f, TaxDemandModel.CalculateTaxModifier(tuning, 9.0f, history, 20), 0.000001f,
            "10-day tuning includes the historic 7 percent rate at day 20");
        Near(0.8888889f, TaxDemandModel.CalculateTaxModifier(tuning, 9.0f, history, 21), 0.000001f,
            "older 7 percent sample expires while the 5 percent sample remains");
        Near(0.0f, TaxDemandModel.CalculateTaxModifier(tuning, 9.0f, history, 22), 0.000001f,
            "empty window falls back to neutral rate and ratio 1");
        Near(0.0f, TaxDemandModel.CalculateTaxModifier(tuning, 0.05f, new TaxRateHistory(), 0), 0.000001f,
            "empty history fallback uses the floored neutral rate");
        Throws<ArgumentNullException>(delegate { TaxDemandModel.CalculateTaxModifier(tuning, 9.0f, (TaxRateHistory)null, 0); }, "null history");
    }

    private static void TestTuningValidation()
    {
        Throws<ArgumentException>(delegate { new TaxDemandTuning(null, new float[] { 0.0f, 0.0f }, 90u, "x"); }, "missing neutral curve");
        Throws<ArgumentException>(delegate { new TaxDemandTuning(new float[] { 0.0f, 9.0f }, new float[0], 90u, "x"); }, "missing modifier curve");
        Throws<ArgumentException>(delegate { new TaxDemandTuning(new float[] { 0.0f, 9.0f }, new float[] { 0.0f, 0.0f }, 90u, ""); }, "missing source identity");
        TaxDemandTuning maximumDays = new TaxDemandTuning(new float[] { 0.0f, 9.0f }, new float[] { 0.0f, 0.0f }, uint.MaxValue, "bounded source");
        Equal(uint.MaxValue, maximumDays.TaxPenaltyDays, "penalty-day field retains the uint32 domain");
    }

    private static void TestCodecRoundTrip()
    {
        TaxDemandTuning tuning = CreateTuning(9.0f, 90u);
        MemoryStream stream = new MemoryStream();
        TaxDemandTuningCodec.Write(stream, tuning);
        True(stream.CanWrite, "writer leaves caller stream open");
        stream.Position = 0;
        TaxDemandTuning decoded = TaxDemandTuningCodec.Read(stream);
        True(stream.CanRead, "reader leaves caller stream open");
        Equal(tuning.SourceIdentity, decoded.SourceIdentity, "source identity round trip");
        Equal(tuning.TaxPenaltyDays, decoded.TaxPenaltyDays, "penalty days round trip");
        Equal(tuning.NeutralTaxRateByPopulation.PointCount, decoded.NeutralTaxRateByPopulation.PointCount, "neutral points round trip");
        Equal(tuning.TaxModifierByRateVariance.PointCount, decoded.TaxModifierByRateVariance.PointCount, "modifier points round trip");
        Near(tuning.TaxModifierByRateVariance.Evaluate(2.0f), decoded.TaxModifierByRateVariance.Evaluate(2.0f), 0.000001f, "curve output round trip");
        stream.Dispose();
    }

    private static void TestCodecRejectsInvalidData()
    {
        byte[] valid = Encode(CreateTuning(9.0f, 90u));

        byte[] truncated = new byte[valid.Length - 1];
        Array.Copy(valid, truncated, truncated.Length);
        Throws<InvalidDataException>(delegate { Decode(truncated); }, "truncated body");

        byte[] trailing = new byte[valid.Length + 1];
        Array.Copy(valid, trailing, valid.Length);
        Throws<InvalidDataException>(delegate { Decode(trailing); }, "trailing byte");

        byte[] corrupt = (byte[])valid.Clone();
        corrupt[corrupt.Length - 1] ^= 0x40;
        Throws<InvalidDataException>(delegate { Decode(corrupt); }, "CRC mismatch");

        byte[] version = (byte[])valid.Clone();
        version[4] = 2;
        Throws<InvalidDataException>(delegate { Decode(version); }, "unknown version");

        byte[] signature = (byte[])valid.Clone();
        signature[0] ^= 0x01;
        Throws<InvalidDataException>(delegate { Decode(signature); }, "bad signature");

        byte[] invalidCurve = EncodeRawProfile("invalid finite-slope regression", 90u,
            new float[] { 0.0f, 0.0f, 1.0e-40f, -1.0f }, new float[] { 0.0f, 0.0f });
        Throws<InvalidDataException>(delegate { Decode(invalidCurve); }, "codec rejects curve with an infinite reachable slope");
    }

    private static TaxDemandTuning CreateTuning(float neutral, uint days)
    {
        return new TaxDemandTuning(
            new float[] { 0.0f, neutral },
            new float[] { 0.0f, 2.0f, 2.0f, -2.0f },
            days,
            "synthetic test tuning");
    }

    private static byte[] Encode(TaxDemandTuning tuning)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            TaxDemandTuningCodec.Write(stream, tuning);
            return stream.ToArray();
        }
    }

    private static byte[] EncodeRawProfile(string sourceIdentity, uint penaltyDays, float[] neutralCurve, float[] modifierCurve)
    {
        byte[] body;
        using (MemoryStream bodyStream = new MemoryStream())
        {
            BinaryWriter bodyWriter = new BinaryWriter(bodyStream, System.Text.Encoding.UTF8);
            byte[] sourceBytes = System.Text.Encoding.UTF8.GetBytes(sourceIdentity);
            bodyWriter.Write(sourceBytes.Length);
            bodyWriter.Write(sourceBytes);
            bodyWriter.Write(penaltyDays);
            WriteRawCurve(bodyWriter, neutralCurve);
            WriteRawCurve(bodyWriter, modifierCurve);
            bodyWriter.Flush();
            body = bodyStream.ToArray();
        }

        using (MemoryStream stream = new MemoryStream())
        {
            BinaryWriter writer = new BinaryWriter(stream, System.Text.Encoding.UTF8);
            writer.Write(0x44543453u);
            writer.Write(1);
            writer.Write(body.Length);
            writer.Write(Crc32ForTest(body));
            writer.Write(body);
            writer.Flush();
            return stream.ToArray();
        }
    }

    private static void WriteRawCurve(BinaryWriter writer, float[] values)
    {
        writer.Write(values.Length);
        int index;
        for (index = 0; index < values.Length; index++) writer.Write(values[index]);
    }

    private static uint Crc32ForTest(byte[] bytes)
    {
        uint crc = uint.MaxValue;
        int index;
        for (index = 0; index < bytes.Length; index++)
        {
            crc ^= bytes[index];
            int bit;
            for (bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1u) == 0 ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }

    private static TaxDemandTuning Decode(byte[] data)
    {
        using (MemoryStream stream = new MemoryStream(data, false))
            return TaxDemandTuningCodec.Read(stream);
    }

    private static void Near(float expected, float actual, float tolerance, string message)
    {
        _checks++;
        if (float.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            throw new Exception(message + ": expected " + expected + ", got " + actual);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        _checks++;
        if (!object.Equals(expected, actual)) throw new Exception(message + ": expected " + expected + ", got " + actual);
    }

    private static void True(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new Exception(message);
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        _checks++;
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new Exception(message + ": expected " + typeof(TException).Name);
    }
}
