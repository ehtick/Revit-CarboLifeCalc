using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using Autodesk.Revit.UI;
using CarboLifeAPI.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Animation;

namespace CarboCircle.data
{
    internal class carboCircleMatchCore
    {
        /// <summary>
        /// What the demolished concrete and masonry can come back as.
        ///
        /// Aggregate, and only aggregate. These materials cannot return as the thing they were:
        /// you cannot re-hang a cast wall or re-lay a bonded skin as structure. Crushing them
        /// for aggregate is the honest answer, and it is the only one this tool offers.
        ///
        /// The old version claimed otherwise in two ways. Masonry came back as "Reused masonry",
        /// implying whole reclaimed bricks, which no part of the import can establish. And every
        /// concrete volume was multiplied by TWO, which had the tool reporting more aggregate
        /// than there was concrete to crush - a headline figure twice the truth.
        ///
        /// ONE LOSS, APPLIED ONCE. The recoverable figure is netVolume, which
        /// carboCircleProject.correctMinedValues has already reduced by the user's loss
        /// percentage. Applying a factor again here would charge the same loss twice.
        /// </summary>
        internal static List<carboCircleElement> findVolumeOpportunities(carboCircleProject carboCircleProject)
        {
            List<carboCircleElement> result = new List<carboCircleElement>();

            if (carboCircleProject == null)
                return result;

            foreach (carboCircleElement mined in carboCircleProject.minedVolumes)
            {
                if (mined == null)
                    continue;

                try
                {
                    carboCircleElement aggregate = mined.Copy();

                    //Only concrete and masonry are crushed for aggregate. Everything else that
                    //ends up in the volume lists - a timber floor, a material the importer could
                    //not class - keeps its own name, because calling it aggregate would be a
                    //claim about it that nobody has made.
                    if (mined.materialClass == "Concrete" || mined.materialClass == "Masonry")
                    {
                        //netVolume already carries the deconstruction loss. Gross is carried too,
                        //so the report can show both what was there and what survives.
                        aggregate.name = "Aggregate from " + describeOrigin(mined);
                        aggregate.materialName = "Aggregate from " + lowerFirst(materialLabel(mined));
                        aggregate.materialClass = "Aggregate";
                    }
                    else
                    {
                        aggregate.name = "Recovered " + lowerFirst(materialLabel(mined)) +
                                         " from " + describeOrigin(mined);
                    }

                    result.Add(aggregate);
                }
                catch (Exception)
                {
                    //One unreadable record must not cost the rest of the schedule.
                }
            }

            return result;
        }

        /// <summary>What this volume came out of, for the aggregate row's name.</summary>
        private static string describeOrigin(carboCircleElement mined)
        {
            if (!string.IsNullOrWhiteSpace(mined.name))
                return mined.name;

            if (!string.IsNullOrWhiteSpace(mined.materialName))
                return mined.materialName;

            return "demolished material";
        }

        /// <summary>The material in words, whatever the class string happens to be.</summary>
        private static string materialLabel(carboCircleElement mined)
        {
            if (mined.materialClass == "Concrete")
                return "Concrete";

            if (mined.materialClass == "Masonry")
                return "Masonry";

            return string.IsNullOrWhiteSpace(mined.materialClass) ? "Other material" : mined.materialClass;
        }

        private static string lowerFirst(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            return char.ToLowerInvariant(text[0]) + text.Substring(1);
        }

        /// <summary>
        /// Pairs each required member with the best piece of existing material that can serve
        /// it, and says why for every one - including the ones that found nothing.
        ///
        /// HOW IT WORKS, AND WHY THIS SHAPE
        ///
        /// There is ONE pool of stock, and an offcut goes straight back into it the moment it is
        /// created. That deletes a whole class of bug rather than fixing it: the old engine
        /// appended remnants to one list and then searched a different, permanently empty one,
        /// so offcut reuse was dead code that looked alive.
        ///
        /// Requirements are served longest-and-strongest first. That ordering is the mitigation
        /// for the obvious greedy failure - a short requirement eating a long member while a
        /// long requirement then finds nothing - because a long strong member can only be served
        /// by long strong stock, whereas short light requirements can be served by the remnants
        /// the big ones leave behind.
        ///
        /// Each requirement then takes the BEST admissible piece, not the first: exact sections
        /// before substitutes, remnants before whole members, tightest fit before loosest. The
        /// selection key is lexicographic, so a perfect match can never be outvoted by a
        /// better-fitting substitute. Priority is structural, not a number.
        ///
        /// It is greedy, not globally optimal, and that is a deliberate choice. The exact
        /// problem is assignment and cutting-stock combined; an exact solver would be a few
        /// hundred lines that no reviewer of this codebase could check by reading, in a project
        /// with no test suite. This is the standard heuristic for saw-and-reuse and it is
        /// auditable line by line.
        ///
        /// Same input, same output, always: every ordering in here is total, ending in an
        /// ordinal GUID comparison.
        /// </summary>
        internal static List<carboCirclePair> findOpportunities(carboCircleProject carboCircleProject, out List<carboCircleElement> leftOvers)
        {
            //Assigned before anything can return, so no exit path leaves the caller with null.
            leftOvers = new List<carboCircleElement>();

            List<carboCirclePair> matchedpairs = new List<carboCirclePair>();

            if (carboCircleProject == null)
                return matchedpairs;

            carboCircleSettings settings = carboCircleProject.settings ?? new carboCircleSettings();

            //Clear last run's claims. Every list, because a member can move between them
            //between runs.
            resetMatchMarks(carboCircleProject.minedData);
            resetMatchMarks(carboCircleProject.requiredData);
            resetMatchMarks(carboCircleProject.minedVolumes);
            resetMatchMarks(carboCircleProject.requiredVolumes);

            //Lookup only, never iterated: used at the end to mark the project's own mined
            //records so the mined grid can show what was taken.
            Dictionary<string, carboCircleElement> minedByGuid = new Dictionary<string, carboCircleElement>(StringComparer.Ordinal);

            foreach (carboCircleElement el in carboCircleProject.minedData)
            {
                if (el != null && !string.IsNullOrEmpty(el.GUID) && !minedByGuid.ContainsKey(el.GUID))
                    minedByGuid.Add(el.GUID, el);
            }

            //---------------------------------------------------------------------------
            // The stock pool
            //---------------------------------------------------------------------------
            List<carboCircleElement> pool = new List<carboCircleElement>();

            foreach (carboCircleElement mined in carboCircleProject.minedData)
            {
                if (mined == null)
                    continue;

                if (mined.materialClass != "Steel" && mined.materialClass != "Wood")
                    continue;

                //Copies, because cutting mutates lengths and the project's own lists are
                //recalculated from gross figures on every run.
                carboCircleElement piece = mined.Copy();

                if (piece.netLength <= 0)
                {
                    leftOvers.Add(withNote(piece, "no usable length once the cutting allowance is taken off"));
                    continue;
                }

                if (!carboCircleMatchRules.hasUsableSection(piece) ||
                    piece.sectionConfidence == carboCircleMatchRules.ConfidenceUnmapped)
                {
                    leftOvers.Add(withNote(piece, "section not recognised" + rawName(piece)));
                    continue;
                }

                pool.Add(piece);
            }

            //Stable, content-based order so the whole run is reproducible.
            pool = pool.OrderBy(p => p.GUID, StringComparer.Ordinal).ToList();

            //---------------------------------------------------------------------------
            // The requirements
            //
            // References, not copies: matchGUID is written back onto the project's own objects
            // and the grids and the csv export read it from there.
            //---------------------------------------------------------------------------
            List<carboCircleElement> queue = new List<carboCircleElement>();

            foreach (carboCircleElement required in carboCircleProject.requiredData)
            {
                if (required == null)
                    continue;

                if (required.materialClass != "Steel" && required.materialClass != "Wood")
                    continue;

                //Screened here rather than silently dropped. A structural column usually
                //reports no length in Revit, and the old engine simply omitted every one of
                //them from the results - the user was never told why their columns vanished.
                if (required.length <= 0)
                {
                    matchedpairs.Add(noMatch(required,
                        "Revit reports no length for this member, so nothing can be cut to fit it. " +
                        "Give the family type a Cut Length parameter, or leave columns out of the import."));
                    continue;
                }

                if (!carboCircleMatchRules.hasUsableSection(required) ||
                    required.sectionConfidence == carboCircleMatchRules.ConfidenceUnmapped)
                {
                    matchedpairs.Add(noMatch(required, "Section not recognised" + rawName(required) +
                        ", so it cannot be compared with anything in stock."));
                    continue;
                }

                queue.Add(required);
            }

            //Longest and strongest first. LINQ OrderBy is a stable sort; List.Sort is not.
            queue = queue
                .OrderByDescending(r => r.length)
                .ThenByDescending(r => r.Wy)
                .ThenBy(r => r.GUID, StringComparer.Ordinal)
                .ThenBy(r => r.id)
                .ToList();

            //How many times each original member has been cut, for stable offcut ids.
            Dictionary<string, int> cutCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            //---------------------------------------------------------------------------
            // Allocation
            //---------------------------------------------------------------------------
            foreach (carboCircleElement required in queue)
            {
                carboCircleElement best = null;
                int bestRank = 0;
                double bestOver = 0;
                double bestWaste = 0;

                //The nearest thing to a match that was refused, so a failure can be explained
                //in terms of what was actually in the yard.
                carboCircleElement nearest = null;
                int nearestKind = carboCircleMatchRules.MissNone;
                double nearestAmount = 0;
                string nearestReason = "";

                foreach (carboCircleElement piece in pool)
                {
                    //Already claimed by an earlier requirement.
                    if (!string.IsNullOrEmpty(piece.matchGUID))
                        continue;

                    int rank;
                    string reason;
                    int missKind;
                    double missAmount;

                    if (carboCircleMatchRules.isAdmissible(required, piece, settings,
                                                           out rank, out reason, out missKind, out missAmount))
                    {
                        double over = carboCircleMatchRules.overProvision(required, piece);
                        double waste = carboCircleMatchRules.waste(required, piece);

                        if (best == null || isBetterChoice(rank, piece, over, waste,
                                                           bestRank, best, bestOver, bestWaste))
                        {
                            best = piece;
                            bestRank = rank;
                            bestOver = over;
                            bestWaste = waste;
                        }
                    }
                    else if (isNearerMiss(missKind, missAmount, nearestKind, nearestAmount))
                    {
                        nearest = piece;
                        nearestKind = missKind;
                        nearestAmount = missAmount;
                        nearestReason = reason;
                    }
                }

                if (best == null)
                {
                    matchedpairs.Add(noMatch(required,
                        explainFailure(required, nearest, nearestReason, nearestKind, settings)));
                    continue;
                }

                //-----------------------------------------------------------------------
                // Form the pair
                //-----------------------------------------------------------------------
                carboCirclePair pair = new carboCirclePair(required, best);

                pair.matchClass = carboCircleMatchRules.classify(best, bestRank);
                pair.match_Score = best.Wy > 0 ? 100.0 * required.Wy / best.Wy : 0;
                pair.used_netLength = required.length;

                double volumePerMetre = best.netLength > 0 ? best.netVolume / best.netLength : 0;
                pair.used_netVolume = volumePerMetre * required.length;

                //-----------------------------------------------------------------------
                // Cut it, and put what is left back in the pool
                //-----------------------------------------------------------------------
                string sourceKey = string.IsNullOrEmpty(best.sourceGUID) ? best.GUID : best.sourceGUID;
                int cutNumber = nextCutNumber(cutCounts, sourceKey);

                carboCircleElement offcut = pair.getOffcut(cutNumber, cutAllowanceMetres(best, settings));

                if (offcut != null)
                {
                    pair.offcut_netLength = offcut.netLength;

                    if (offcut.netLength * 1000 >= settings.minOffcutLength)
                    {
                        //Straight back into the same pool. Appended, so it is considered by
                        //every later requirement without disturbing the pieces already scanned.
                        pool.Add(offcut);
                    }
                    else
                    {
                        leftOvers.Add(withNote(offcut, "offcut too short to be worth reusing"));
                    }
                }

                pair.description = describe(pair, required, best, bestRank, settings);

                //Claim the piece, and record the claim on both sides.
                best.matchGUID = required.GUID;
                required.matchGUID = sourceKey;

                carboCircleElement minedOriginal;

                if (minedByGuid.TryGetValue(sourceKey, out minedOriginal) && minedOriginal != null)
                    minedOriginal.matchGUID = required.GUID;

                matchedpairs.Add(pair);
            }

            //---------------------------------------------------------------------------
            // What nobody wanted
            //---------------------------------------------------------------------------
            foreach (carboCircleElement piece in pool)
            {
                if (string.IsNullOrEmpty(piece.matchGUID))
                    leftOvers.Add(piece.Copy());
            }

            return matchedpairs;
        }

        //--------------------------------------------------------------------------------
        // Selection
        //--------------------------------------------------------------------------------

        /// <summary>
        /// Whether this candidate beats the best so far, on a lexicographic key.
        ///
        /// The order of the tests IS the policy:
        ///  1. section rank   - an exact section beats any substitute, always. This is why a
        ///                      100% match cannot be outvoted by a tighter-fitting substitute,
        ///                      which is exactly what the old numeric score allowed.
        ///  2. offcuts first  - use up the remnants before cutting into whole members, so whole
        ///                      stock stays whole for the requirements that need the length.
        ///  3. least over-provision - the tightest structural fit, so a heavy section is not
        ///                      spent on a light requirement.
        ///  4. least waste    - the shortest piece that still does the job.
        ///  5. GUID           - so the answer is the same every run.
        /// </summary>
        private static bool isBetterChoice(int rank, carboCircleElement piece, double over, double waste,
                                           int bestRank, carboCircleElement best, double bestOver, double bestWaste)
        {
            if (rank != bestRank)
                return rank < bestRank;

            if (piece.isOffcut != best.isOffcut)
                return piece.isOffcut;

            if (!nearlyEqual(over, bestOver))
                return over < bestOver;

            if (!nearlyEqual(waste, bestWaste))
                return waste < bestWaste;

            return string.CompareOrdinal(piece.GUID, best.GUID) < 0;
        }

        private static bool nearlyEqual(double a, double b)
        {
            return Math.Abs(a - b) <= 1e-9 * (1 + Math.Abs(b));
        }

        /// <summary>
        /// Whether this refusal is more worth reporting than the one held. Ordered by how
        /// actionable it is: being told a section was 3% too weak is useful, being told the
        /// material was different is not.
        /// </summary>
        private static bool isNearerMiss(int kind, double amount, int heldKind, double heldAmount)
        {
            if (kind == carboCircleMatchRules.MissNone)
                return false;

            if (heldKind == carboCircleMatchRules.MissNone)
                return true;

            if (kind != heldKind)
                return kind < heldKind;

            //Within one kind, the nearest one. Comparing only the kind kept whichever candidate
            //happened to be scanned first, and then called it "closest in stock" - which was
            //simply untrue, and the number offered as the threshold to change was the wrong
            //number.
            return Math.Abs(amount) < Math.Abs(heldAmount);
        }

        //--------------------------------------------------------------------------------
        // Words
        //--------------------------------------------------------------------------------

        /// <summary>
        /// One sentence per pair, always the same grammar: what kind of match, which section,
        /// how much of the piece was used, and anything the engineer should be wary of.
        /// </summary>
        private static string describe(carboCirclePair pair, carboCircleElement required,
                                       carboCircleElement stock, int rank, carboCircleSettings settings)
        {
            StringBuilder text = new StringBuilder();

            switch (pair.matchClass)
            {
                case carboCircleMatchRules.ClassExactSection:
                    text.Append("Exact section. " + stock.standardName + ", whole member. ");
                    break;

                case carboCircleMatchRules.ClassFromOffcut:
                    text.Append("From an offcut. " + stock.standardName);
                    text.Append(rank == 0 ? " (exact section)" : " (substitute)");
                    text.Append(", cut from " + carboCircleMatchRules.m(stock.netLength) +
                                " left over from " + sourceLabel(stock) + ". ");
                    break;

                case carboCircleMatchRules.ClassAdequateCrossFamily:
                    text.Append("Adequate substitute, different shape family. ");
                    text.Append(stock.standardName + " for " + required.standardName + ": ");
                    text.Append(compare(required, stock) + ". ");
                    break;

                default:
                    text.Append("Adequate substitute, same family. ");
                    text.Append(stock.standardName + " for " + required.standardName + ": ");
                    text.Append(compare(required, stock) + ". ");
                    break;
            }

            text.Append(carboCircleMatchRules.m(pair.used_netLength) + " used of " +
                        carboCircleMatchRules.m(stock.netLength) + " usable");

            if (pair.offcut_netLength > 0)
            {
                text.Append(", " + carboCircleMatchRules.m(pair.offcut_netLength) + " offcut");
                text.Append(pair.offcut_netLength * 1000 >= settings.minOffcutLength
                    ? " returned to stock." : " too short to reuse.");
            }
            else
            {
                text.Append(", no offcut.");
            }

            //Caveats. These are the things the tool genuinely does not know, said out loud.
            //
            //Every gate in carboCircleMatchRules is about bending and stiffness, because that is
            //all the catalogue supports. Anything else a substitution has to satisfy - grade,
            //shear, axial load, section classification, the connections at each end - is
            //invisible here, and a tool that stays quiet about that is claiming more than it
            //checked.
            if (required.sectionConfidence == carboCircleMatchRules.ConfidenceAssumed ||
                stock.sectionConfidence == carboCircleMatchRules.ConfidenceAssumed)
                text.Append(" Section identified by name similarity, not confirmed.");

            //Recorded when a gate could not run, rather than left to look like a gate that
            //passed. A required value of zero means the comparison was skipped, which is not
            //the same as the substitute being adequate on that axis.
            if (required.Wz <= 0)
                text.Append(" Minor-axis capacity not compared - the requirement has none recorded.");

            if (required.Iy <= 0 || required.Iz <= 0)
                text.Append(" Stiffness not fully compared - the requirement has none recorded.");

            if (pair.matchClass == carboCircleMatchRules.ClassAdequateCrossFamily)
                text.Append(" Different shape family - every connection on this member needs re-detailing.");

            if (rank == 0)
                text.Append(" " + carboCircleMatchRules.exactSectionCaveat());
            else
                text.Append(" " + carboCircleMatchRules.substitutionCaveat());

            return text.ToString();
        }

        /// <summary>How the substitute differs from what was asked for, in the terms that decided it.</summary>
        private static string compare(carboCircleElement required, carboCircleElement stock)
        {
            List<string> parts = new List<string>();

            double over = carboCircleMatchRules.overProvision(required, stock);
            parts.Add(over >= 0
                ? carboCircleMatchRules.pc(over) + " stronger"
                : carboCircleMatchRules.pc(-over) + " weaker");

            double deeper = stock.standardDepth - required.standardDepth;

            if (Math.Abs(deeper) >= 0.5)
                parts.Add(carboCircleMatchRules.mm(Math.Abs(deeper)) + (deeper > 0 ? " deeper" : " shallower"));

            double wider = stock.standardWidth - required.standardWidth;

            if (Math.Abs(wider) >= 0.5)
                parts.Add(carboCircleMatchRules.mm(Math.Abs(wider)) + (wider > 0 ? " wider" : " narrower"));

            return string.Join(", ", parts.ToArray());
        }

        /// <summary>
        /// Why nothing could serve this requirement, named in terms of the closest thing that
        /// was actually available and what the user could change.
        /// </summary>
        private static string explainFailure(carboCircleElement required, carboCircleElement nearest,
                                             string nearestReason, int nearestKind, carboCircleSettings settings)
        {
            if (nearest == null)
                return "No match. Nothing of the same material with a recognised section was left in stock.";

            string text = "No match. Closest in stock: " + nearest.standardName + " (" +
                          carboCircleMatchRules.m(nearest.netLength) + " usable) - " + nearestReason + ".";

            //The remedy, but only the one that actually applies. Naming a setting that would
            //not have changed the answer is worse than saying nothing: the user changes it,
            //nothing happens, and they stop believing the rest of the sentence. So each remedy
            //is tied to the reason that was actually binding.
            switch (nearestKind)
            {
                case carboCircleMatchRules.MissOverProvision:
                    text += " A strength tolerance above " +
                            carboCircleMatchRules.pc(carboCircleMatchRules.overProvision(required, nearest)) +
                            " would let it be considered.";
                    break;

                //"would let it be considered", not "would allow it". isAdmissible returns at
                //the first gate that fails, so the gates after this one were never evaluated -
                //widening this tolerance may simply reveal the next objection.
                case carboCircleMatchRules.MissTooDeep:
                    text += " A depth allowance above " +
                            carboCircleMatchRules.mm(nearest.standardDepth - required.standardDepth) +
                            " would let it be considered.";
                    break;

                case carboCircleMatchRules.MissTooWide:
                    text += " A width allowance above " +
                            carboCircleMatchRules.mm(nearest.standardWidth - required.standardWidth) +
                            " would let it be considered.";
                    break;

                case carboCircleMatchRules.MissFamily:
                    //Only worth offering when the setting is the thing standing in the way, and
                    //only for the one substitution the tool is willing to make.
                    if (!settings.allowCrossFamilySubstitution &&
                        carboCircleMatchRules.sectionFamily(required) == "I" &&
                        carboCircleMatchRules.sectionFamily(nearest) == "H")
                        text += " Allowing column sections to serve beams in the settings would let this " +
                                "be considered - the connections would all need re-detailing.";
                    break;

                case carboCircleMatchRules.MissCapacity:
                    text += " Nothing can be done about this one: a substitute has to be at least as " +
                            "strong and as stiff as the member it replaces.";
                    break;
            }

            return text;
        }

        /// <summary>
        /// What to call the member an offcut came out of. The PARENT, not the offcut: "left over
        /// from M12" rather than "left over from M12_OC1", which named the remnant as its own
        /// source.
        /// </summary>
        private static string sourceLabel(carboCircleElement stock)
        {
            string label = stock.humanId;

            if (!string.IsNullOrEmpty(label))
            {
                int cut = label.IndexOf("_OC", StringComparison.Ordinal);

                if (cut > 0)
                    label = label.Substring(0, cut);

                return label;
            }

            return string.IsNullOrEmpty(stock.sourceGUID) ? stock.GUID : stock.sourceGUID;
        }

        private static string rawName(carboCircleElement el)
        {
            return string.IsNullOrWhiteSpace(el.name) ? "" : " (\"" + el.name + "\")";
        }

        //--------------------------------------------------------------------------------
        // Small helpers
        //--------------------------------------------------------------------------------

        private static void resetMatchMarks(List<carboCircleElement> list)
        {
            if (list == null)
                return;

            foreach (carboCircleElement el in list)
            {
                if (el != null)
                    el.matchGUID = "";
            }
        }

        /// <summary>
        /// A requirement nothing could serve, as a real pair so the required schedule is
        /// complete in one grid. mined_Element is left default, so mined_id is 0 and every
        /// downstream consumer can tell there is no member behind it.
        /// </summary>
        private static carboCirclePair noMatch(carboCircleElement required, string reason)
        {
            carboCirclePair pair = new carboCirclePair();

            pair.required_element = required.Copy();
            pair.matchClass = carboCircleMatchRules.ClassNoMatch;
            pair.match_Score = 0;
            pair.description = reason;

            //There is no member behind this row, and the id has to say so. A fresh
            //carboCircleElement carries id = -999 from its constructor, which the grid printed
            //as if it were a Revit element id and which Select Pair then asked Revit to
            //highlight. Zero is the value every consumer already treats as "nothing here".
            pair.mined_Element.id = 0;
            pair.mined_Element.name = "";
            pair.mined_Element.standardName = "";

            return pair;
        }

        /// <summary>Tags a leftover with why it is a leftover, for the grid to show.</summary>
        private static carboCircleElement withNote(carboCircleElement el, string note)
        {
            carboCircleElement copy = el.Copy();

            copy.matchGUID = "";

            //Into grade, which is the one string field on the leftovers grid that carries
            //nothing useful for a mined member - it holds the Revit material class, which the
            //Material column already says. The note used to go into name, and only when name
            //was empty, which it never is for anything the importer produced: the reason was
            //written and then discarded on every single element.
            copy.grade = note;

            return copy;
        }

        /// <summary>
        /// Length lost preparing the new end a cut creates, in metres. One end, not two: the
        /// two-end allowance has already been taken off the whole member's usable length by
        /// correctMinedValues, and charging it again per cut would compound.
        /// </summary>
        private static double cutAllowanceMetres(carboCircleElement stock, carboCircleSettings settings)
        {
            double mm = stock.materialClass == "Wood"
                ? settings.timberCutoffLength
                : settings.cutoffbeamLength;

            return mm > 0 ? mm / 1000.0 : 0;
        }

        private static int nextCutNumber(Dictionary<string, int> counts, string sourceKey)
        {
            int n;

            counts.TryGetValue(sourceKey, out n);
            n = n + 1;
            counts[sourceKey] = n;

            return n;
        }


    }
}
