using System;

namespace CarboCircle.data
{

    [Serializable]
    public class carboCirclePair
    {

        /// <summary>What the new design needs.</summary>
        public carboCircleElement required_element { get; set; }

        /// <summary>
        /// The piece of stock as it was OFFERED, before the cut. netLength is its usable length
        /// and netVolume its usable volume. For an offcut this is the remnant, and its Revit id
        /// still points at the member it came out of.
        /// </summary>
        public carboCircleElement mined_Element { get; set; }

        /// <summary>
        /// How much of the substitute's bending capacity the requirement actually uses, as a
        /// percentage. Exactly 100 for an exact section, lower the more over-provisioned the
        /// substitute is, 0 when unknown.
        ///
        /// Bounded and monotone, unlike the old score. That was an unbounded 0-500 figure
        /// multiplied by a length factor, which meant a well-fitting substitute could outscore
        /// the exact section - the length factor destroyed the very priority it was meant to
        /// express. Priority is now carried by matchClass, structurally, where it cannot be
        /// outvoted by a number.
        /// </summary>
        public double match_Score { get; set; }

        /// <summary>Which of the five classes this pair falls in. See carboCircleMatchRules.</summary>
        public int matchClass { get; set; }

        /// <summary>Length actually cut out of stock for this requirement, in metres.</summary>
        public double used_netLength { get; set; }

        /// <summary>
        /// Volume actually cut out of stock for this requirement, in cubic metres.
        ///
        /// This, not the whole piece's netVolume, is the quantity of material the requirement
        /// consumes, and therefore the quantity the carbon calculation wants. Taking the whole
        /// piece counted the entire 9 m beam against a 6 m requirement and then counted its
        /// offcut again on the next match.
        /// </summary>
        public double used_netVolume { get; set; }

        /// <summary>
        /// Length of the remnant this cut produced, in metres. Zero when the cut left nothing
        /// worth keeping.
        /// </summary>
        public double offcut_netLength { get; set; }

        /// <summary>One sentence naming the numbers that decided this pair.</summary>
        public string description { get; set; }

        public carboCirclePair()
        {
            required_element = new carboCircleElement();
            mined_Element = new carboCircleElement();
            match_Score = 0;
            matchClass = carboCircleMatchRules.ClassNoMatch;
            used_netLength = 0;
            used_netVolume = 0;
            offcut_netLength = 0;
            description = string.Empty;
        }

        public carboCirclePair(carboCircleElement requiredElement, carboCircleElement minedElement, double matchScore = 0, string description = "")
        {
            this.required_element = requiredElement.Copy();
            this.mined_Element = minedElement.Copy();
            this.match_Score = matchScore;
            this.description = description;
        }

        /// <summary>
        /// Returns the remaining part as an offcut
        /// </summary>
        /// <returns></returns>
        /// <summary>
        /// The remnant this cut leaves, as a fresh piece of stock, or null if there is nothing
        /// worth handing back.
        ///
        /// This is the only place offcut geometry is derived, and the arithmetic was wrong in
        /// three separate ways before.
        ///
        /// LENGTH. The remnant is what is left after the requirement is taken out, less one
        /// cutting allowance - the saw makes one new end, and that end has to be trimmed and
        /// prepared before the remnant can be used. The old code charged no allowance at all,
        /// so a chain of cuts manufactured length out of nothing.
        ///
        /// VOLUME. Volume is taken pro rata from the piece being cut, at the cross section it
        /// actually has: volume per metre times the remaining length. The old code scaled the
        /// remnant.s volume by required/mined, which is the fraction CONSUMED, so the offcut
        /// was given the volume of the part that had just been used and the arithmetic ran
        /// backwards.
        ///
        /// GROSS AND NET. Both are set to the same figure. A remnant has no further deduction
        /// waiting to be applied: its usable length IS its length, and pretending otherwise
        /// double-charges the cutting allowance on the next generation.
        ///
        /// IDENTITY. The Revit id is inherited unchanged, because the remnant is physically
        /// part of that member and both the model colouring and the select-pair button resolve
        /// it. sourceGUID carries the ORIGINAL member through every generation, so all the
        /// pieces cut from one beam stay traceable to it.
        /// </summary>
        /// <param name="cutNumber">Which cut of this source member this is, for a stable id.</param>
        /// <param name="cutAllowanceMetres">Length lost preparing the new end, in metres.</param>
        internal carboCircleElement getOffcut(int cutNumber, double cutAllowanceMetres)
        {
            if (mined_Element == null || required_element == null)
                return null;

            if (mined_Element.netLength <= 0)
                return null;

            double remainder = mined_Element.netLength - required_element.length - cutAllowanceMetres;

            if (remainder <= 0)
                return null;

            //Volume per metre of the piece being cut, so the remnant is costed at its own
            //cross section rather than at an average.
            double volumePerMetre = mined_Element.netVolume / mined_Element.netLength;

            carboCircleElement offcut = mined_Element.Copy();

            offcut.isOffcut = true;
            offcut.sourceGUID = string.IsNullOrEmpty(mined_Element.sourceGUID)
                ? mined_Element.GUID
                : mined_Element.sourceGUID;

            offcut.GUID = offcut.sourceGUID + "_OC" + cutNumber;
            offcut.humanId = baseName(mined_Element.humanId) + "_OC" + cutNumber;
            offcut.name = baseName(mined_Element.name) + "_Offcut";

            offcut.netLength = remainder;
            offcut.length = remainder;
            offcut.netVolume = volumePerMetre * remainder;
            offcut.volume = offcut.netVolume;

            //Fresh stock: nothing has claimed it yet.
            offcut.matchGUID = "";

            return offcut;
        }

        /// <summary>
        /// Strips a previously appended offcut suffix, so a remnant of a remnant reads
        /// "B7_Offcut" rather than "B7_Offcut_Offcut".
        /// </summary>
        private static string baseName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "";

            int cut = name.IndexOf("_Offcut", StringComparison.Ordinal);

            if (cut > 0)
                return name.Substring(0, cut);

            cut = name.IndexOf("_OC", StringComparison.Ordinal);

            return cut > 0 ? name.Substring(0, cut) : name;
        }
        public carboCirclePair Copy()
        {
            carboCirclePair clone = new carboCirclePair();
            clone = new carboCirclePair
            {
                required_element = required_element.Copy(),
                mined_Element = this.mined_Element.Copy(),
                match_Score = this.match_Score,
                matchClass = this.matchClass,
                used_netLength = this.used_netLength,
                used_netVolume = this.used_netVolume,
                offcut_netLength = this.offcut_netLength,
                description = this.description
            };
            return clone;
        }


    }
}