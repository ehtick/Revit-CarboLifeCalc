using System;
using System.Globalization;

namespace CarboCircle.data
{
    /// <summary>
    /// Whether one member may stand in for another, and why.
    ///
    /// Every rule the brief asks for is one readable line in here. That is the whole point of
    /// the file: this is the part a reviewer has to be able to check by eye, so it holds no
    /// state, touches no Revit type, allocates nothing, and is the only place in the project
    /// that reads Iy, Iz, Wy or Wz.
    ///
    /// THE TWO HALVES OF THE BRIEF, AND WHY THEY PULL IN OPPOSITE DIRECTIONS
    ///
    /// "Structurally equal or better" is four LOWER bounds: the substitute must carry at least
    /// the bending capacity and at least the stiffness of the member it replaces, on both axes.
    ///
    /// "Not larger than the user specified" is three UPPER bounds: it must not be deeper or
    /// wider than the requirement plus the user's allowance, and it must not be wastefully
    /// stronger than needed.
    ///
    /// Note the asymmetry, because it is deliberate and it is the thing most likely to be
    /// "tidied" into a bug later. There is no lower bound on depth or width, and no upper bound
    /// on stiffness. A substitute that is shallower, narrower and stiffer than the requirement
    /// is the best outcome available and must never be rejected for being small. The old engine
    /// required the substitute to be strictly DEEPER than the requirement, which threw away
    /// exactly the substitutions an engineer wants most.
    ///
    /// UNITS
    ///
    /// Steel section properties come from the catalogue in cm4 and cm3. Timber properties are
    /// computed from the Revit type parameters b and d in mm, giving mm4 and mm3. The two are a
    /// factor of 10^4 and 10^3 apart and nothing here converts between them - because nothing
    /// here ever compares across them. Material class equality is the first gate of
    /// <see cref="isAdmissible"/>, before a single number is read, and only "Steel" and "Wood"
    /// ever reach this file. Every later comparison is therefore like against like.
    /// </summary>
    internal static class carboCircleMatchRules
    {
        //--------------------------------------------------------------------------------
        // Match classes
        //
        // Plain int constants rather than a C# enum, so XML serialisation and WPF binding stay
        // boring. The numeric value is also the display order.
        //--------------------------------------------------------------------------------

        /// <summary>The same section, whole. Nothing to check and nothing to think about.</summary>
        public const int ClassExactSection = 1;

        /// <summary>A different size in the same family, proven adequate by its numbers.</summary>
        public const int ClassAdequateSameFamily = 2;

        /// <summary>Adequate, but a different shape family. Needs a human decision.</summary>
        public const int ClassAdequateCrossFamily = 3;

        /// <summary>Cut from the remnant of a member already used for something else.</summary>
        public const int ClassFromOffcut = 4;

        /// <summary>Nothing in stock could serve this requirement.</summary>
        public const int ClassNoMatch = 5;

        /// <summary>What the user reads, per class. The ListView groups and colours on this.</summary>
        public static string classLabel(int matchClass)
        {
            switch (matchClass)
            {
                case ClassExactSection: return "100% match";
                case ClassAdequateSameFamily: return "Structurally good enough";
                case ClassAdequateCrossFamily: return "Different shape - check";
                case ClassFromOffcut: return "From an offcut";
                default: return "No match";
            }
        }

        //--------------------------------------------------------------------------------
        // Section confidence, mirroring carboCircleElement.sectionConfidence
        //--------------------------------------------------------------------------------

        public const int ConfidenceExact = 0;
        public const int ConfidenceAssumed = 1;
        public const int ConfidenceUnmapped = 2;

        //--------------------------------------------------------------------------------
        // Why a candidate was rejected. Ordered by how useful it is to be told about, so the
        // nearest-miss tracker can prefer the most actionable reason.
        //--------------------------------------------------------------------------------

        //Ordered by what the user can DO about it, lowest first, because the nearest-miss
        //tracker prefers the lowest. Being told a section is 3% too weak is a dead end - no
        //setting changes it. Being told one is 4% too strong for the tolerance, or 12 mm too
        //deep, names something the user can actually change, so those rank above it.
        public const int MissNone = 0;
        public const int MissOverProvision = 1; // too strong, above the user's cap
        public const int MissTooDeep = 2;
        public const int MissTooWide = 3;
        public const int MissFamily = 4;
        public const int MissCapacity = 5;      // not strong or stiff enough - nothing to be done
        public const int MissTooShort = 6;
        public const int MissUnusable = 7;

        //--------------------------------------------------------------------------------
        // Comparison helpers
        //
        // Both allow equality, and both carry a relative epsilon. Equality matters: a section
        // of exactly the required depth, or of exactly the required capacity, is admissible,
        // and the catalogue quotes three significant figures so exact equality between two
        // tabulated values is common. Comparing the doubles bare would reject a member for
        // being 0.0000001 mm too deep.
        //--------------------------------------------------------------------------------

        private const double epsilonFactor = 1e-9;

        /// <summary>True when a is at least b, allowing equality.</summary>
        public static bool atLeast(double a, double b)
        {
            return a >= b - epsilonFactor * (1 + Math.Abs(b));
        }

        /// <summary>True when a is no more than b, allowing equality.</summary>
        public static bool notMoreThan(double a, double b)
        {
            return a <= b + epsilonFactor * (1 + Math.Abs(b));
        }

        //--------------------------------------------------------------------------------
        // Section identity
        //--------------------------------------------------------------------------------

        /// <summary>
        /// Whether this element carries enough section information to be matched at all.
        ///
        /// A name, a depth and a major-axis modulus are the minimum: without them there is
        /// nothing to compare and nothing to tell the user. Elements failing this are reported
        /// as unusable rather than quietly skipped, which is what happened before.
        /// </summary>
        public static bool hasUsableSection(carboCircleElement el)
        {
            if (el == null)
                return false;

            return !string.IsNullOrWhiteSpace(el.standardName)
                && el.standardDepth > 0
                && el.Wy > 0;
        }

        /// <summary>
        /// The section family: the catalogue's own shape letter.
        ///
        /// No name parsing is needed, and that is worth understanding before anyone adds any.
        /// standardCategory already holds the CategoryType letter from the section table, and
        /// the table has already done the hard part: family "I" contains both the British UKB
        /// rows and the European IPE rows, family "H" contains UC and UKC, family "L" contains
        /// both EA and UKA. The orthographic variants that a name parser would exist to
        /// reconcile are already reconciled in the data.
        ///
        /// Timber has no catalogue and therefore no letter, so it is given one of its own.
        /// </summary>
        public static string sectionFamily(carboCircleElement el)
        {
            if (el == null)
                return "";

            if (el.materialClass == "Wood")
                return "T";

            return el.standardCategory == null ? "" : el.standardCategory.Trim();
        }

        /// <summary>
        /// Whether these two are the same section.
        ///
        /// Both sides must have been mapped outright, not guessed at. A nearest-name guess that
        /// happens to land on the same wrong section on both sides is not a 100% match, and
        /// presenting it as one would be the most misleading thing this tool could do. Such a
        /// pair can still be offered as an adequate substitution, where the numbers carry the
        /// argument instead of the name.
        ///
        /// Two blank names are never equal here. That was a real false positive: every timber
        /// member without b and d parameters had standardName "", so they all "matched" each
        /// other exactly.
        /// </summary>
        public static bool isExactSection(carboCircleElement required, carboCircleElement stock)
        {
            if (!hasUsableSection(required) || !hasUsableSection(stock))
                return false;

            if (required.sectionConfidence != ConfidenceExact || stock.sectionConfidence != ConfidenceExact)
                return false;

            return string.Equals(required.standardName.Trim(), stock.standardName.Trim(), StringComparison.Ordinal);
        }

        /// <summary>
        /// How much stronger than necessary the substitute is, as a fraction of what is needed.
        /// 0 means a perfect fit; 0.15 means 15% more bending capacity than the job requires.
        /// </summary>
        public static double overProvision(carboCircleElement required, carboCircleElement stock)
        {
            if (required == null || stock == null || required.Wy <= 0)
                return 0;

            return (stock.Wy - required.Wy) / required.Wy;
        }

        /// <summary>
        /// Length of stock left over if this piece were cut for this requirement, in metres.
        /// </summary>
        public static double waste(carboCircleElement required, carboCircleElement stock)
        {
            if (required == null || stock == null)
                return 0;

            return stock.netLength - required.length;
        }

        //--------------------------------------------------------------------------------
        // Family eligibility
        //--------------------------------------------------------------------------------

        /// <summary>
        /// Families whose tabulated properties do not describe the axes they would be
        /// substituted about, so they may only ever match their own exact section.
        ///
        /// L, angles: the catalogue tabulates about the leg axes, not the principal axes an
        /// angle actually bends about, and the plastic modulus is blank on all 41 rows.
        /// C, channels: loaded in the plane of the web a channel twists, and the tabulated
        /// properties say nothing about that.
        ///
        /// Substituting within these families on the numbers in this file would be arithmetic
        /// dressed up as engineering.
        /// </summary>
        public static bool familyOnlyMatchesExactly(string family)
        {
            return family == "L" || family == "C";
        }

        /// <summary>
        /// Whether stock of one family may be offered for a requirement of another.
        ///
        /// Exactly one move is allowed, and only when the user has asked for it: a column
        /// section serving a beam. At equal bending capacity a UC is shallower than a UB and far
        /// stiffer about its minor axis, so it makes a sound beam.
        ///
        /// The reverse is refused outright. A UB used as a column has a fraction of the
        /// minor-axis stiffness a column needs, and this tool knows nothing about effective
        /// lengths, so it is in no position to offer one. Hollow sections are refused in both
        /// directions: the connection, the fire protection and the fit-out all change with the
        /// shape, and none of that is visible here.
        /// </summary>
        public static bool crossFamilyAllowed(string requiredFamily, string stockFamily, carboCircleSettings settings)
        {
            if (settings == null || !settings.allowCrossFamilySubstitution)
                return false;

            return requiredFamily == "I" && stockFamily == "H";
        }

        //--------------------------------------------------------------------------------
        // The decision
        //--------------------------------------------------------------------------------

        /// <summary>
        /// Whether this piece of stock may serve this requirement.
        ///
        /// The gates run in a deliberate order - cheapest and most fundamental first, so the
        /// reason handed back names the most basic thing that was wrong rather than an
        /// incidental one.
        /// </summary>
        /// <param name="sectionRank">0 exact section, 1 same family, 2 cross family.</param>
        /// <param name="reason">Why it was refused, in words fit to show a user.</param>
        /// <param name="missKind">Which <c>Miss*</c> constant applies, for the nearest-miss report.</param>
        /// <param name="missAmount">How far off it was, in the unit of that miss kind.</param>
        public static bool isAdmissible(carboCircleElement required, carboCircleElement stock,
                                        carboCircleSettings settings,
                                        out int sectionRank, out string reason,
                                        out int missKind, out double missAmount)
        {
            sectionRank = 0;
            reason = "";
            missKind = MissNone;
            missAmount = 0;

            if (required == null || stock == null || settings == null)
            {
                reason = "missing data";
                missKind = MissUnusable;
                return false;
            }

            // GATE 1 - same material class.
            // This is what makes every number below comparable: steel carries cm4/cm3 and
            // timber mm4/mm3, and this gate is the only thing standing between them.
            if (!string.Equals(required.materialClass, stock.materialClass, StringComparison.Ordinal))
            {
                reason = "different material";
                missKind = MissUnusable;
                return false;
            }

            // GATE 2 - both sides have a section worth comparing.
            if (!hasUsableSection(required) || !hasUsableSection(stock))
            {
                reason = "no usable section";
                missKind = MissUnusable;
                return false;
            }

            if (required.sectionConfidence == ConfidenceUnmapped || stock.sectionConfidence == ConfidenceUnmapped)
            {
                reason = "section not recognised";
                missKind = MissUnusable;
                return false;
            }

            // GATE 3 - long enough. Checked before the section tests because length is the one
            // thing no substitution can argue its way around.
            if (!atLeast(stock.netLength, required.length))
            {
                missKind = MissTooShort;
                missAmount = required.length - stock.netLength;
                reason = "too short by " + m(missAmount);
                return false;
            }

            // GATE 4 - the same section. Nothing further to prove: it IS the required member,
            // so no depth, width or strength test applies and none should.
            if (isExactSection(required, stock))
            {
                sectionRank = 0;
                return true;
            }

            // GATE 5 - is a substitution of this shape allowed at all?
            string requiredFamily = sectionFamily(required);
            string stockFamily = sectionFamily(stock);

            if (requiredFamily.Length == 0 || stockFamily.Length == 0)
            {
                reason = "section family unknown";
                missKind = MissFamily;
                return false;
            }

            if (requiredFamily == stockFamily)
            {
                if (familyOnlyMatchesExactly(requiredFamily))
                {
                    reason = requiredFamily == "L"
                        ? "angles are only offered as an exact section - the published properties are about the legs, not the axis an angle bends about"
                        : "channels are only offered as an exact section - a channel loaded through its web twists, and the published properties do not describe that";
                    missKind = MissFamily;
                    return false;
                }

                sectionRank = 1;
            }
            else if (crossFamilyAllowed(requiredFamily, stockFamily, settings))
            {
                sectionRank = 2;
            }
            else
            {
                reason = "different shape family (" + stockFamily + " for " + requiredFamily + ")";
                missKind = MissFamily;
                return false;
            }

            // GATE 6 - structurally equal or better. Four lower bounds, all of which must hold.
            if (!atLeast(stock.Wy, required.Wy))
            {
                missKind = MissCapacity;
                missAmount = (required.Wy - stock.Wy) / required.Wy;
                reason = "weaker in bending about the major axis, by " + pc(missAmount);
                return false;
            }

            if (required.Wz > 0 && !atLeast(stock.Wz, required.Wz))
            {
                missKind = MissCapacity;
                missAmount = (required.Wz - stock.Wz) / required.Wz;
                reason = "weaker in bending about the minor axis, by " + pc(missAmount);
                return false;
            }

            if (required.Iy > 0 && !atLeast(stock.Iy, required.Iy))
            {
                missKind = MissCapacity;
                missAmount = (required.Iy - stock.Iy) / required.Iy;
                reason = "less stiff about the major axis, by " + pc(missAmount);
                return false;
            }

            if (required.Iz > 0 && !atLeast(stock.Iz, required.Iz))
            {
                missKind = MissCapacity;
                missAmount = (required.Iz - stock.Iz) / required.Iz;
                reason = "less stiff about the minor axis, by " + pc(missAmount);
                return false;
            }

            // GATE 7 - not larger than the user allowed. Three upper bounds.
            // Upper bounds only. A shallower, narrower substitute is better, not worse.
            if (required.standardDepth > 0 &&
                !notMoreThan(stock.standardDepth, required.standardDepth + settings.depthRange))
            {
                missKind = MissTooDeep;
                missAmount = stock.standardDepth - (required.standardDepth + settings.depthRange);
                reason = "deeper than allowed, by " + mm(missAmount);
                return false;
            }

            if (required.standardWidth > 0 &&
                !notMoreThan(stock.standardWidth, required.standardWidth + settings.widthRange))
            {
                missKind = MissTooWide;
                missAmount = stock.standardWidth - (required.standardWidth + settings.widthRange);
                reason = "wider than allowed, by " + mm(missAmount);
                return false;
            }

            double over = overProvision(required, stock);

            if (!notMoreThan(over, settings.strengthRange / 100.0))
            {
                missKind = MissOverProvision;
                missAmount = over;
                reason = "stronger than needed by " + pc(over) + ", above the " +
                         pc(settings.strengthRange / 100.0) + " allowed";
                return false;
            }

            return true;
        }

        /// <summary>
        /// The class a formed pair belongs to.
        ///
        /// Coming from an offcut takes precedence over how the section was arrived at, because
        /// that is the distinction the user reacts to first: this is material that would
        /// otherwise have been scrap. Which section it was is then said in the reason line.
        /// </summary>
        public static int classify(carboCircleElement stock, int sectionRank)
        {
            //A changed shape family outranks everything, including coming from an offcut.
            //Classifying a cross-family remnant as "from an offcut" would file it under good
            //news and drop the warning that every connection on it has to be re-detailed - the
            //one thing about that row a user must not miss.
            if (sectionRank == 2)
                return ClassAdequateCrossFamily;

            if (stock != null && stock.isOffcut)
                return ClassFromOffcut;

            if (sectionRank == 0)
                return ClassExactSection;

            return ClassAdequateSameFamily;
        }

        /// <summary>
        /// What this tool does NOT check, said once, in the same words every time.
        ///
        /// Every gate in this file is about bending and stiffness, because that is what the
        /// catalogue supports. A substitution can pass all of them and still be wrong for
        /// reasons nothing here can see, and the tool has no business implying otherwise.
        /// </summary>
        public static string substitutionCaveat()
        {
            return "Not checked: steel grade, shear, axial capacity, section classification, " +
                   "or the connections at either end.";
        }

        /// <summary>
        /// The one thing an identical section still does not guarantee.
        ///
        /// Same profile does not mean same steel. carboCircleElement.grade holds the Revit
        /// material class - "Metal", "Steel" - never S275 or S355, so a reclaimed member of a
        /// lower grade is indistinguishable here from the one it is replacing.
        /// </summary>
        public static string exactSectionCaveat()
        {
            return "Same section, but the steel grade is not recorded in the model and has not been checked.";
        }

        //--------------------------------------------------------------------------------
        // Formatting, kept here so every sentence the engine produces reads the same way
        //--------------------------------------------------------------------------------

        /// <summary>A length in metres.</summary>
        public static string m(double metres)
        {
            return metres.ToString("N2", CultureInfo.InvariantCulture) + " m";
        }

        /// <summary>A length in millimetres.</summary>
        public static string mm(double millimetres)
        {
            return millimetres.ToString("N0", CultureInfo.InvariantCulture) + " mm";
        }

        /// <summary>A fraction as a percentage.</summary>
        public static string pc(double fraction)
        {
            return (fraction * 100).ToString("N1", CultureInfo.InvariantCulture) + "%";
        }
    }
}
