namespace ADLMRateGen.ViewModel.BlockWork
{
    /// <summary>
    /// The mortar arithmetic the wall and render rates share, in one place.
    ///
    /// Calibrated Sep 2026 against the build-up formulas in ~690 real Nigerian QS
    /// workbooks (one vote per author: template lineage, firm or unit-rate sheet).
    /// Before this, a 225 mm wall carried 0.0535 bags of cement per m2 against a
    /// practice median of 0.20 - about a quarter of what a QS orders - and the gap
    /// came from two places at once:
    ///
    ///   1. The mortar mixes divide a batch by its DRY volume (1:6 by 7, 1:4 by 5),
    ///      so a batch of 1 part cement and 6 parts sand was treated as yielding
    ///      7 m3 of mortar. Dry material shrinks when mixed; practice allows about
    ///      1.35, so the same batch yields nearer 5.2 m3. See <see cref="WetYield"/>.
    ///   2. The mortar volume per m2 of wall was 0.013 m3, against 0.0253 (the Lagos
    ///      build-up rate, which measures the bed and perpends) and 0.038 (the School
    ///      unit-rate estimate). See <see cref="PerSqM225"/> and friends.
    ///
    /// Both levers now sit inside the range the sources use, and the product - what
    /// a bill actually buys - lands on the practice median.
    /// </summary>
    public static class MortarMix
    {
        /// <summary>Bags of cement in 1 m3 of cement, the conversion every source uses.</summary>
        public const double BagsPerCubicMetreOfCement = 28.82;

        /// <summary>
        /// Dry material to wet mortar. A batch measured dry yields less mixed mortar;
        /// the corpus runs 1.3 to 1.4 (QUIV, Heron and the website use 1.54 for the
        /// coarser concrete mixes).
        /// </summary>
        public const double DryToWetFactor = 1.35;

        /// <summary>Mortar a batch of this dry volume actually yields, in m3.</summary>
        public static double WetYield(double dryVolumeM3) =>
            dryVolumeM3 <= 0 ? 0 : dryVolumeM3 / DryToWetFactor;

        /// <summary>Bags of cement in 1 m3 of mortar, for a 1:n mix measured dry.</summary>
        public static double CementBagsPerM3(double sandPartsPerCementPart)
        {
            double dryVolume = 1 + sandPartsPerCementPart;
            double wet = WetYield(dryVolume);
            return wet <= 0 ? 0 : BagsPerCubicMetreOfCement / wet;
        }

        // Mortar per m2 of wall face. The 225 figure sits between the two sources that
        // measure mortar geometrically (Lagos 0.0253, School 0.038); the thinner walls
        // keep Lagos's measured ratio to the 225 wall (0.65 and 0.47), because the
        // corpus's own 150 and 100 figures come mostly from templates that use one
        // number for every wall thickness.
        public const double PerSqM225 = 0.035;
        public const double PerSqM150 = 0.0228;
        public const double PerSqM100 = 0.0166;

        /// <summary>Render and plaster: 12 mm finished, with the usual allowance for dubbing out.</summary>
        public const double RenderPerSqM = 0.015;

        /// <summary>Cement a wall buys per m2, for checking against practice.</summary>
        public static double WallCementBagsPerSqM(double mortarPerSqM, double sandPartsPerCementPart) =>
            mortarPerSqM * CementBagsPerM3(sandPartsPerCementPart);
    }
}
