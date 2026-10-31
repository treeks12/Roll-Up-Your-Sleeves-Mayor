using System;

namespace SC4Evolution.Simulation
{
    /// <summary>Raw, source-attributed water-stage caps from the three residential exemplars.</summary>
    public sealed class ResidentialWaterProfile
    {
        private readonly byte[] _sourceSha256;

        public byte[] SourceSha256 { get { return (byte[])_sourceSha256.Clone(); } }
        public int LowMaximumStage { get; private set; }
        public int MediumMaximumStage { get; private set; }
        public int HighMaximumStage { get; private set; }

        public ResidentialWaterProfile(byte[] sourceSha256, int lowMaximumStage,
            int mediumMaximumStage, int highMaximumStage)
        {
            ValidateSourceHash(sourceSha256);
            ValidateCap(lowMaximumStage, "lowMaximumStage");
            ValidateCap(mediumMaximumStage, "mediumMaximumStage");
            ValidateCap(highMaximumStage, "highMaximumStage");

            _sourceSha256 = (byte[])sourceSha256.Clone();
            LowMaximumStage = lowMaximumStage;
            MediumMaximumStage = mediumMaximumStage;
            HighMaximumStage = highMaximumStage;
        }

        internal static void ValidateSourceHash(byte[] sourceSha256)
        {
            if (sourceSha256 == null) throw new ArgumentNullException("sourceSha256");
            if (sourceSha256.Length != 32) throw new ArgumentException("Source SHA-256 must contain exactly 32 bytes.", "sourceSha256");
            int index;
            for (index = 0; index < sourceSha256.Length; index++)
                if (sourceSha256[index] != 0) return;
            throw new ArgumentException("Source SHA-256 must not be all zero.", "sourceSha256");
        }

        internal static void ValidateCap(int value, string parameterName)
        {
            if (value < 0 || value > byte.MaxValue)
                throw new ArgumentOutOfRangeException(parameterName, "Raw water cap must be between 0 and 255.");
        }
    }
}
