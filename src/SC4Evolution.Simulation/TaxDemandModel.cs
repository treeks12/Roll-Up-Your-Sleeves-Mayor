using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SC4Evolution.Simulation
{
    /// <summary>
    /// Immutable piecewise-linear response curve for the two tax tuning curves.
    /// The constructor accepts flattened x/y pairs; an odd trailing float is ignored
    /// as specified by the SC4 response-curve loader.
    /// </summary>
    public sealed class TaxResponseCurve
    {
        public const int MaximumValues = 8192;
        private const float FlatDeltaThreshold = 1.0e-10f;
        private readonly float[] _values;
        private readonly Segment[] _segments;

        public int PointCount { get { return _values.Length / 2; } }
        public int SegmentCount { get { return _segments.Length; } }

        public TaxResponseCurve(float[] flattenedValues)
        {
            if (flattenedValues == null) throw new ArgumentNullException("flattenedValues");
            if (flattenedValues.Length > MaximumValues) throw new ArgumentOutOfRangeException("flattenedValues", "Curve contains too many values.");
            int pointCount = flattenedValues.Length / 2;
            if (pointCount == 0) throw new ArgumentException("A tax response curve must contain at least one x/y pair.", "flattenedValues");

            _values = new float[pointCount * 2];
            Array.Copy(flattenedValues, _values, _values.Length);
            int index;
            for (index = 0; index < _values.Length; index++) RequireFinite(_values[index], "flattenedValues");

            List<Segment> segments = new List<Segment>(pointCount);
            float previousX = -float.MaxValue;
            float previousY = _values[1];
            for (index = 0; index < pointCount; index++)
            {
                float x = _values[index * 2];
                float y = _values[index * 2 + 1];
                if (index != 0 && x < previousX)
                    throw new ArgumentException("Tax response curve x values must be non-decreasing.", "flattenedValues");

                if (index == 0)
                {
                    // The first point creates a constant left-hand segment.
                    segments.Add(new Segment(x, y, x, y, 0.0f));
                }
                else
                {
                    // The game uses the previous input point, even if it was dropped.
                    float dy = y - previousY;
                    float dx = x - previousX;
                    if (Math.Abs(dy) < FlatDeltaThreshold)
                    {
                        segments.Add(new Segment(x, y, previousX, previousY, 0.0f));
                    }
                    else if (dy * FlatDeltaThreshold < dx)
                    {
                        float slope = dy / dx;
                        // A duplicate-X downward transition is the documented
                        // vertical drop. Its infinite slope is never selected
                        // for interpolation because the next segment has the
                        // same X boundary. Every non-vertical segment must have
                        // a finite, usable slope.
                        if (dx != 0.0f && (float.IsNaN(slope) || float.IsInfinity(slope)))
                            throw new ArgumentException("Tax response curve has a non-finite segment slope.", "flattenedValues");
                        segments.Add(new Segment(x, y, previousX, previousY, slope));
                    }
                    // Otherwise the input point is dropped, matching the documented loader.
                }

                previousX = x;
                previousY = y;
            }

            _segments = segments.ToArray();
            if (_segments.Length == 0) throw new ArgumentException("Tax response curve has no usable segments.", "flattenedValues");
        }

        public float Evaluate(float input)
        {
            RequireFinite(input, "input");
            int last = _segments.Length - 1;
            int selected = last;
            int index;
            for (index = 0; index <= last; index++)
            {
                if (input < _segments[index].X)
                {
                    selected = index;
                    break;
                }
            }

            if (selected == 0) return _segments[0].Y;
            if (selected < last || input < _segments[last].X)
            {
                Segment segment = _segments[selected];
                float delta = input - segment.PreviousX;
                float product = delta * segment.Slope;
                float result = segment.PreviousY + product;
                if (float.IsNaN(result) || float.IsInfinity(result))
                    throw new ArithmeticException("Tax response curve produced a non-finite interpolation result.");
                return result;
            }

            return _segments[last].Y;
        }

        internal float[] CopyValues()
        {
            float[] copy = new float[_values.Length];
            Array.Copy(_values, copy, _values.Length);
            return copy;
        }

        internal static void RequireFinite(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException(name, "Value must be finite.");
        }

        private struct Segment
        {
            internal readonly float X;
            internal readonly float Y;
            internal readonly float PreviousX;
            internal readonly float PreviousY;
            internal readonly float Slope;

            internal Segment(float x, float y, float previousX, float previousY, float slope)
            {
                X = x;
                Y = y;
                PreviousX = previousX;
                PreviousY = previousY;
                Slope = slope;
            }
        }
    }

    /// <summary>
    /// Loaded tax tuning. Both curves are required; this type intentionally has no
    /// embedded defaults so missing or malformed installed tuning cannot be hidden.
    /// </summary>
    public sealed class TaxDemandTuning
    {
        public const int MaximumSourceIdentityCharacters = 1024;

        public TaxResponseCurve NeutralTaxRateByPopulation { get; private set; }
        public TaxResponseCurve TaxModifierByRateVariance { get; private set; }
        public uint TaxPenaltyDays { get; private set; }
        public string SourceIdentity { get; private set; }

        public TaxDemandTuning(float[] neutralTaxRateCurveValues, float[] taxModifierCurveValues,
            uint taxPenaltyDays, string sourceIdentity)
        {
            if (string.IsNullOrEmpty(sourceIdentity) || sourceIdentity.Length > MaximumSourceIdentityCharacters)
                throw new ArgumentException("A bounded tuning source identity is required.", "sourceIdentity");

            NeutralTaxRateByPopulation = new TaxResponseCurve(neutralTaxRateCurveValues);
            TaxModifierByRateVariance = new TaxResponseCurve(taxModifierCurveValues);
            TaxPenaltyDays = taxPenaltyDays;
            SourceIdentity = sourceIdentity;
        }
    }

    /// <summary>
    /// Pure calculations for the documented tax response. Rate arguments and
    /// results use ordinary percentage points: 9.0 means 9 percent (SC4's stored
    /// tax-history value for the same rate is 90 tenths of a percent).
    /// </summary>
    public static class TaxDemandModel
    {
        public static float EvaluateNeutralTaxRatePercent(TaxDemandTuning tuning, float totalPopulation)
        {
            if (tuning == null) throw new ArgumentNullException("tuning");
            TaxResponseCurve.RequireFinite(totalPopulation, "totalPopulation");
            return tuning.NeutralTaxRateByPopulation.Evaluate(totalPopulation);
        }

        public static float CalculateRateVarianceRatio(float neutralRatePercent, float maximumActualRatePercent)
        {
            TaxResponseCurve.RequireFinite(neutralRatePercent, "neutralRatePercent");
            TaxResponseCurve.RequireFinite(maximumActualRatePercent, "maximumActualRatePercent");

            float neutral = neutralRatePercent < 0.1f ? 0.1f : neutralRatePercent;
            float reciprocal = 1.0f / neutral;
            float ratio = maximumActualRatePercent * reciprocal;
            TaxResponseCurve.RequireFinite(ratio, "rate variance ratio");
            return ratio;
        }

        public static float CalculateTaxModifier(TaxDemandTuning tuning, float neutralRatePercent,
            float maximumActualRatePercent)
        {
            if (tuning == null) throw new ArgumentNullException("tuning");
            float ratio = CalculateRateVarianceRatio(neutralRatePercent, maximumActualRatePercent);
            return tuning.TaxModifierByRateVariance.Evaluate(ratio);
        }

        /// <summary>
        /// Calculates the tax modifier from the maximum recorded rate in the
        /// inclusive rolling window. With no samples, the documented choice is a
        /// rate equal to the floored neutral rate, which gives ratio 1.
        /// </summary>
        public static float CalculateTaxModifier(TaxDemandTuning tuning, float neutralRatePercent,
            TaxRateHistory history, long today)
        {
            if (tuning == null) throw new ArgumentNullException("tuning");
            if (history == null) throw new ArgumentNullException("history");

            float maximumRatePercent;
            if (!history.TryGetMaximumRatePercent(today, tuning.TaxPenaltyDays, out maximumRatePercent))
                maximumRatePercent = neutralRatePercent < 0.1f ? 0.1f : neutralRatePercent;

            return CalculateTaxModifier(tuning, neutralRatePercent, maximumRatePercent);
        }
    }

    /// <summary>
    /// In-memory rate history, expressed in percentage points. Samples must be
    /// recorded in non-decreasing day order; multiple changes on one day are valid.
    /// </summary>
    public sealed class TaxRateHistory
    {
        public const int MaximumSamples = 1000000;
        private readonly List<TaxRateSample> _samples = new List<TaxRateSample>();

        public int Count { get { return _samples.Count; } }

        public void Record(long day, float ratePercent)
        {
            if (day < 0) throw new ArgumentOutOfRangeException("day", "Day index must be non-negative.");
            TaxResponseCurve.RequireFinite(ratePercent, "ratePercent");
            if (_samples.Count >= MaximumSamples) throw new InvalidOperationException("Tax rate history sample limit exceeded.");
            if (_samples.Count != 0 && day < _samples[_samples.Count - 1].Day)
                throw new ArgumentException("Tax rate samples must be recorded in non-decreasing day order.", "day");

            _samples.Add(new TaxRateSample(day, ratePercent));
        }

        public bool TryGetMaximumRatePercent(long today, uint windowDays, out float maximumRatePercent)
        {
            if (today < 0) throw new ArgumentOutOfRangeException("today", "Day index must be non-negative.");

            long firstDay = today - (long)windowDays;
            if (firstDay < 0) firstDay = 0;
            bool found = false;
            float maximum = 0.0f;
            int index;
            for (index = 0; index < _samples.Count; index++)
            {
                TaxRateSample sample = _samples[index];
                if (sample.Day < firstDay) continue;
                if (sample.Day > today) break;
                if (!found || sample.RatePercent > maximum)
                {
                    maximum = sample.RatePercent;
                    found = true;
                }
            }

            maximumRatePercent = found ? maximum : 0.0f;
            return found;
        }

        private struct TaxRateSample
        {
            internal readonly long Day;
            internal readonly float RatePercent;

            internal TaxRateSample(long day, float ratePercent)
            {
                Day = day;
                RatePercent = ratePercent;
            }
        }
    }

    /// <summary>
    /// Bounded portable tuning file. Its payload is data extracted at runtime from
    /// an installed archive; no shipped coefficient table is embedded in code.
    /// </summary>
    public static class TaxDemandTuningCodec
    {
        private const uint Magic = 0x44543453; // S4TD, little endian.
        private const int Version = 1;
        private const int MaximumBodyBytes = 256 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static void Write(Stream output, TaxDemandTuning tuning)
        {
            if (output == null || !output.CanWrite) throw new ArgumentException("Writable stream required.", "output");
            if (tuning == null) throw new ArgumentNullException("tuning");

            byte[] body;
            using (MemoryStream buffer = new MemoryStream())
            {
                BinaryWriter writer = new BinaryWriter(buffer, Utf8);
                WriteText(writer, tuning.SourceIdentity);
                writer.Write(tuning.TaxPenaltyDays);
                WriteCurve(writer, tuning.NeutralTaxRateByPopulation.CopyValues());
                WriteCurve(writer, tuning.TaxModifierByRateVariance.CopyValues());
                writer.Flush();
                if (buffer.Length > MaximumBodyBytes) throw new InvalidDataException("Tax tuning profile exceeds the size limit.");
                body = buffer.ToArray();
            }

            BinaryWriter header = new BinaryWriter(output, Utf8);
            header.Write(Magic);
            header.Write(Version);
            header.Write(body.Length);
            header.Write(Crc32(body));
            header.Write(body);
            header.Flush();
        }

        public static TaxDemandTuning Read(Stream input)
        {
            if (input == null || !input.CanRead) throw new ArgumentException("Readable stream required.", "input");
            BinaryReader header = new BinaryReader(input, Utf8);
            try
            {
                if (header.ReadUInt32() != Magic) throw new InvalidDataException("Invalid tax tuning signature.");
                if (header.ReadInt32() != Version) throw new InvalidDataException("Unsupported tax tuning version.");
                int length = header.ReadInt32();
                if (length < 0 || length > MaximumBodyBytes) throw new InvalidDataException("Invalid tax tuning length.");
                uint expectedCrc = header.ReadUInt32();
                byte[] body = header.ReadBytes(length);
                if (body.Length != length) throw new InvalidDataException("Truncated tax tuning profile.");
                if (input.ReadByte() != -1) throw new InvalidDataException("Trailing data after tax tuning profile.");
                if (Crc32(body) != expectedCrc) throw new InvalidDataException("Tax tuning checksum mismatch.");

                using (MemoryStream buffer = new MemoryStream(body, false))
                {
                    BinaryReader reader = new BinaryReader(buffer, Utf8);
                    string source = ReadText(reader);
                    uint penaltyDays = reader.ReadUInt32();
                    float[] neutralCurve = ReadCurve(reader);
                    float[] modifierCurve = ReadCurve(reader);
                    if (buffer.Position != buffer.Length) throw new InvalidDataException("Trailing data inside tax tuning profile.");
                    return new TaxDemandTuning(neutralCurve, modifierCurve, penaltyDays, source);
                }
            }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated tax tuning profile.", error); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid tax tuning source identity.", error); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid tax tuning values.", error); }
        }

        private static void WriteCurve(BinaryWriter writer, float[] values)
        {
            writer.Write(values.Length);
            int index;
            for (index = 0; index < values.Length; index++) writer.Write(values[index]);
        }

        private static float[] ReadCurve(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 2 || count > TaxResponseCurve.MaximumValues || (count & 1) != 0)
                throw new InvalidDataException("Invalid tax response curve value count.");
            float[] values = new float[count];
            int index;
            for (index = 0; index < values.Length; index++) values[index] = reader.ReadSingle();
            return values;
        }

        private static void WriteText(BinaryWriter writer, string text)
        {
            byte[] bytes = Utf8.GetBytes(text);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadText(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 1 || length > TaxDemandTuning.MaximumSourceIdentityCharacters * 4)
                throw new InvalidDataException("Invalid tax tuning source identity length.");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidDataException("Truncated tax tuning source identity.");
            string text = Utf8.GetString(bytes);
            if (text.Length == 0 || text.Length > TaxDemandTuning.MaximumSourceIdentityCharacters)
                throw new InvalidDataException("Invalid tax tuning source identity.");
            return text;
        }

        private static uint Crc32(byte[] bytes)
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
    }
}
