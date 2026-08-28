using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CarboLifeAPI.Data
{
    /// <summary>
    /// Where an answer came from. Higher tiers are strictly more trustworthy.
    /// </summary>
    public enum CarboMatchTier
    {
        Unmatched = 0,
        LowConfidence = 1,
        Fuzzy = 2,
        GradeFamily = 3,
        NormalisedName = 4,
        ExactName = 5,
        Alias = 6
    }

    /// <summary>
    /// What to return when nothing clears the low confidence threshold.
    /// </summary>
    public enum UnmatchedPolicy
    {
        ReturnBestEffort,
        ReturnSentinel
    }

    /// <summary>
    /// Immutable description of one lookup.
    /// </summary>
    public sealed class CarboLookup
    {
        /// <summary>The Revit material name, null and whitespace are handled.</summary>
        public string Name { get; set; }
        /// <summary>The Revit Material.MaterialClass, e.g. Concrete, Metal, Wood, Generic.</summary>
        public string MaterialClass { get; set; }
        /// <summary>Strength grade, e.g. "C32/40", "S355". Also extracted from Name.</summary>
        public string Grade { get; set; }
        /// <summary>The Revit ELEMENT category, e.g. "Floors". Form hint only, never a family.</summary>
        public string ElementCategory { get; set; }
        /// <summary>Database template name, used only for alias table scoping.</summary>
        public string TemplateName { get; set; }

        public CarboLookup()
        {
            Name = "";
            MaterialClass = "";
            Grade = "";
            ElementCategory = "";
            TemplateName = "";
        }
    }

    /// <summary>
    /// The outcome of one lookup.
    /// </summary>
    public sealed class CarboMatchResult
    {
        /// <summary>Never null. Always a fresh deep clone, never a live list element.</summary>
        public CarboMaterial Material { get; set; }
        /// <summary>0.000 to 1.000. Exactly 1.000 only for an alias or exact name hit.</summary>
        public double Confidence { get; set; }
        public CarboMatchTier Tier { get; set; }
        /// <summary>True when Confidence is at or above CarboMatchOptions.ReviewThreshold.</summary>
        public bool IsAcceptable { get; set; }
        /// <summary>Same as IsAcceptable, kept as a friendlier name for UI binding.</summary>
        public bool IsReliable { get { return IsAcceptable && Confidence >= CarboMatchOptions.AcceptThreshold; } }
        /// <summary>
        /// Machine readable code: Ok, AliasHit, AliasBroken, AliasAmbiguous, ExactName,
        /// NormalisedName, GradeAnchored, EmptyLookup, EmptyDatabase, ScoreBelowThreshold.
        /// </summary>
        public string Reason { get; set; }
        /// <summary>Human sentence for CarboGroup.Description and the review queue.</summary>
        public string Explanation { get; set; }
        public string RunnerUpName { get; set; }
        public double RunnerUpConfidence { get; set; }
        /// <summary>True if this is the hard sentinel, i.e. Material.Id == -1 and tier Unmatched.</summary>
        public bool IsUnmatched { get { return Tier == CarboMatchTier.Unmatched; } }

        public CarboMatchResult()
        {
            Material = new CarboMaterial();
            Confidence = 0;
            Tier = CarboMatchTier.Unmatched;
            IsAcceptable = false;
            Reason = "";
            Explanation = "";
            RunnerUpName = "";
            RunnerUpConfidence = 0;
        }
    }

    /// <summary>
    /// One scored candidate with its component breakdown, for diagnostics and mapping dialogs.
    /// </summary>
    public sealed class CarboMatchCandidate
    {
        public CarboMaterial Material { get; set; }
        public double Confidence { get; set; }
        public double Raw { get; set; }
        public double Coverage { get; set; }
        public double Precision { get; set; }
        public double SToken { get; set; }
        public double SEdit { get; set; }
        public double CategoryAgreement { get; set; }
        public double GradeAgreement { get; set; }
        public double SBrief { get; set; }
        public string Explanation { get; set; }
    }

    /// <summary>
    /// All tunables of the matcher. A static class, so nothing here is instance state and
    /// the .cxml schema {CarboMaterials, templateName} is untouched.
    /// </summary>
    public static class CarboMatchOptions
    {
        //Component weights, these sum to exactly 1.00
        public static double WToken = 0.40;
        public static double WCat = 0.25;
        public static double WGrade = 0.20;
        public static double WEdit = 0.07;
        public static double WBrief = 0.08;

        //Bonuses
        public static double BRegion = 0.03;
        public static double BForm = 0.03;

        //Penalties
        public static double PCatConflict = 0.20;
        public static double PGradeConflict = 0.35;
        public static double PVariant = 0.06;
        public static double PSpecialiser = 0.10;
        public static double PSpecialiserCap = 0.20;
        public static double PConceptConflict = 0.12;

        //Token sub weights, coverage dominates on purpose, a short Revit name must be
        //free to match a long precise EPD name without a length penalty.
        public static double TokCoverageWeight = 0.75;
        public static double TokPrecisionWeight = 0.25;

        //...but coverage over a ONE token lookup carries no information about which row is
        //meant: it saturates at 1.0 the instant that single word appears anywhere in the row,
        //so every one of Okobaudat's 214 mineral rows scores a perfect 1.0 for "Concrete".
        //Precision still discriminates there, so the weights are swapped for that case.
        public static double SingleTokenCoverageWeight = 0.40;
        public static double SingleTokenPrecisionWeight = 0.60;

        //Bands
        public static double AcceptThreshold = 0.55;
        public static double ReviewThreshold = 0.35;

        //Margin damping, a dead tie is reported honestly instead of confidently
        public static double MarginFull = 0.05;
        public static double MarginDampFloor = 0.70;

        //Per tier confidence mapping
        public static double Tier2Confidence = 0.97;
        public static double Tier4Span = 0.79;

        //Tier 3 is an ANCHOR, not a verdict. It restricts the candidate set to rows carrying
        //the requested grade and then scores them exactly like tier 4, adding this bonus. It
        //deliberately has no confidence FLOOR: a row that shares nothing with the lookup but
        //the grade token used to be asserted at ~0.89 purely because it was the only row left
        //in the anchored set.
        public static double Tier3GradeBonus = 0.15;

        //When the lookup carries no usable MaterialClass the family gate cannot fire, so the
        //grade token alone would choose the winner - which mapped "Paint C8/10" onto in-situ
        //concrete. In that case the winner must still show real name evidence.
        public static double Tier3MinTokenWhenFamilyUnknown = 0.35;

        //A family word in the lookup ("timber") is genuinely satisfied by a row whose Category
        //is that family, even when the word has migrated out of the row's Name - that is the
        //D1 fix. A SPECIFIC product word ("plywood") is not: an OSB row must not be able to
        //claim it. Product words therefore earn only a fraction of the weight by family alone.
        public static double FamilyCreditFactor = 0.50;

        //Confidence ceiling for a match resting entirely on tokens that carry no family and no
        //concept. Revit placeholder names - "Wall", "Ceiling", "Element", "Default Wall" - share
        //a word with some product name in a large database and the coverage ratio then saturates
        //at 1.0 on that single token. Sits just below ReviewThreshold so such a match is always
        //surfaced rather than silently applied.
        public static double NoDomainEvidenceCap = 0.34;

        //A lookup whose only content word is a family word - "Steel", "Concrete", "Timber" -
        //has named a FAMILY, not a material. Whichever row wins, it won on tie-breaks among
        //rows that are all equally entitled to the name, so it must never be asserted: on
        //IStructE 2.0 a bare "Steel" with S355 picks composite floor decking at 1.76 kgCO2e/kg
        //over structural open sections at 0.89. Sits just below AcceptThreshold.
        public static double BareFamilyWordCap = 0.54;

        //Hard cap on the number of memoised lookups, so a very large export cannot grow the
        //cache without bound. Oldest entries are dropped wholesale when the cap is hit.
        public static int MemoCap = 4000;

        //Skip the O(n*m) Levenshtein when the two names cannot be meaningfully similar
        public static double EditLengthGate = 0.60;

        public static int TieEpsilonMilli = 5;
        public static int BriefTokenCap = 8;
        public static int BriefLenCap = 60;

        public static UnmatchedPolicy Policy = UnmatchedPolicy.ReturnBestEffort;

        /// <summary>
        /// Region preference. This is a POLICY DEFAULT, not a fact, it reflects a UK authored
        /// tool. A non UK deployment should set this empty or to its own region.
        /// </summary>
        public static string[] PreferredRegionTokens = new string[] { "uk", "import" };
    }

    /// <summary>
    /// Opt in, buffered, single write match logging. Never throws.
    /// Nothing touches the filesystem during scoring, Record is a no-op when disabled.
    /// </summary>
    public static class CarboMatchDiagnostics
    {
        private const int MaxBufferedLines = 20000;

        /// <summary>Off by default. Turn on to collect a match log for one import.</summary>
        public static bool Enabled = false;

        private static readonly StringBuilder buffer = new StringBuilder();
        private static int lineCount = 0;
        private static bool truncated = false;

        /// <summary>The buffered log is written here, never to the install directory.</summary>
        public static string GetLogPath()
        {
            return Path.Combine(Path.GetTempPath(), "CarboLifeCalc", "matchlog.csv");
        }

        /// <summary>Clears the buffer, call at the start of an import.</summary>
        public static void Reset()
        {
            buffer.Length = 0;
            lineCount = 0;
            truncated = false;
        }

        /// <summary>Appends one line to the in memory buffer. No-op when disabled.</summary>
        public static void Record(string line)
        {
            if (Enabled == false)
                return;

            if (lineCount >= MaxBufferedLines)
            {
                if (truncated == false)
                {
                    buffer.AppendLine("--- log truncated at " + MaxBufferedLines.ToString(CultureInfo.InvariantCulture) + " lines ---");
                    truncated = true;
                }
                return;
            }

            buffer.AppendLine(line);
            lineCount++;
        }

        /// <summary>
        /// Writes the whole buffer in exactly one operation and empties it.
        /// Safe to call always, never throws, a read only install simply gets no log.
        /// </summary>
        public static void Flush()
        {
            if (buffer.Length == 0)
                return;

            try
            {
                string path = GetLogPath();
                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir) == false)
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, buffer.ToString());
            }
            catch
            {
                //A log is never worth killing an import for.
            }
            finally
            {
                Reset();
            }
        }
    }

    /// <summary>
    /// The material family tree. Families are compared through this tree rather than by
    /// literal string containment, which is what makes the matcher work across the English,
    /// German and Swedish category vocabularies in the shipped databases.
    /// </summary>
    internal static class CarboFamily
    {
        public const string UNKNOWN = "UNKNOWN";
        public const string METAL = "METAL";
        public const string STEEL = "STEEL";
        public const string REBAR = "REBAR";
        public const string STAINLESS = "STAINLESS";
        public const string ALUMINIUM = "ALUMINIUM";
        public const string METAL_OTHER = "METAL_OTHER";
        public const string MINERAL = "MINERAL";
        public const string CONCRETE = "CONCRETE";
        public const string CEMENTITIOUS = "CEMENTITIOUS";
        public const string MASONRY = "MASONRY";
        public const string STONE = "STONE";
        public const string TIMBER = "TIMBER";
        public const string GLASS = "GLASS";
        public const string PLASTIC = "PLASTIC";
        public const string INSULATION = "INSULATION";
        public const string COATING = "COATING";
        public const string BITUMEN = "BITUMEN";
        public const string BOARD = "BOARD";
        public const string EARTH = "EARTH";
        public const string SERVICES = "SERVICES";
        public const string COMPOSITE = "COMPOSITE";

        private static readonly Dictionary<string, string> Parent = BuildParents();
        private static readonly HashSet<string> HasChildren = BuildHasChildren();
        private static readonly HashSet<string> Coarse = new HashSet<string>(StringComparer.Ordinal)
        {
            METAL, MINERAL, GLASS, UNKNOWN
        };

        private static Dictionary<string, string> BuildParents()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            map[STEEL] = METAL;
            map[REBAR] = STEEL;
            map[STAINLESS] = STEEL;
            map[ALUMINIUM] = METAL;
            map[METAL_OTHER] = METAL;
            map[CONCRETE] = MINERAL;
            map[CEMENTITIOUS] = MINERAL;
            map[MASONRY] = MINERAL;
            map[STONE] = MINERAL;
            return map;
        }

        private static HashSet<string> BuildHasChildren()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in Parent)
                set.Add(kv.Value);
            return set;
        }

        public static bool IsCoarse(string family)
        {
            return Coarse.Contains(family);
        }

        public static bool HasAnyChildren(string family)
        {
            return HasChildren.Contains(family);
        }

        /// <summary>True when a equals b, or b is found walking a's parent chain.</summary>
        public static bool IsSameOrDescendant(string a, string b)
        {
            if (a == null || b == null)
                return false;

            string current = a;
            int guard = 0;
            while (current != null && guard < 12)
            {
                if (string.Equals(current, b, StringComparison.Ordinal))
                    return true;

                string parent;
                if (Parent.TryGetValue(current, out parent) == false)
                    return false;

                current = parent;
                guard++;
            }
            return false;
        }

        public static int Depth(string family)
        {
            int depth = 0;
            string current = family;
            string parent;
            while (current != null && Parent.TryGetValue(current, out parent) && depth < 12)
            {
                current = parent;
                depth++;
            }
            return depth;
        }

        /// <summary>
        /// Category is a strong soft signal, never a hard filter. The reward is twice the
        /// penalty by design, so a strong name match can always overturn a disagreement.
        /// </summary>
        public static double Agreement(string a, string b)
        {
            if (string.Equals(a, UNKNOWN, StringComparison.Ordinal) || string.Equals(b, UNKNOWN, StringComparison.Ordinal))
                return 0.0;

            if (string.Equals(a, b, StringComparison.Ordinal))
                return 1.0;

            if (IsSameOrDescendant(a, b) || IsSameOrDescendant(b, a))
                return 0.6;

            return -0.5;
        }
    }

    /// <summary>
    /// One scored token. Key is what is compared, Family is what allows a token to be
    /// satisfied by a row's CATEGORY even when the row's name no longer carries the word.
    /// </summary>
    internal sealed class CarboTok
    {
        public string Key;
        public double Weight;
        public string Family;

        //True when the token IS the family's own word (timber / holz / trae / virke), false
        //when it names a specific product inside that family (plywood / osb / chipboard).
        //Only a family word may be fully satisfied by a row's Category alone.
        public bool IsFamilyWord;
    }

    /// <summary>
    /// A normalised name with everything the scorer needs, computed once per row at index
    /// build time and once per lookup.
    /// </summary>
    internal sealed class CarboBag
    {
        public string Raw;
        public string Folded;
        public string NormName;
        public Dictionary<string, CarboTok> Tokens;
        public List<string> Grades;
        public HashSet<string> Specialisers;
        public bool HasPreferredRegion;
        public bool HasCompetingRegion;
        public int VariantRank;
        public double TotalWeight;

        public int ContentTokenCount { get { return Tokens.Count; } }

        public CarboBag()
        {
            Raw = "";
            Folded = "";
            NormName = "";
            Tokens = new Dictionary<string, CarboTok>(StringComparer.Ordinal);
            Grades = new List<string>();
            Specialisers = new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// One indexed database row.
    /// </summary>
    internal sealed class CarboRow
    {
        public int Index;
        public CarboMaterial Material;
        public string TrimmedName;
        public CarboBag Bag;
        public string CategoryKey;
        public string Family;
    }

    /// <summary>
    /// All static lookup tables. Static readonly members only, so nothing here is instance
    /// state and XmlSerializer cannot see any of it.
    /// </summary>
    internal static class CarboMatchTables
    {
        //--- TABLE PH, phrase substitution, applied longest pattern first ------------------
        //Multi word phrases must be resolved before tokenising, or "cast in place" degrades
        //into three useless tokens.
        private static readonly string[][] PhrasesRaw = new string[][]
        {
            new string[] { "cross laminated timber", " clt " },
            new string[] { "laminated veneer lumber", " lvl " },
            new string[] { "oriented strand board", " osb " },
            new string[] { "brettschichtholz", " glulam " },
            new string[] { "furnierschichtholz", " lvl " },
            new string[] { "reinforced concrete", " rc concrete " },
            new string[] { "cross-laminated", " clt " },
            new string[] { "glued laminated", " glulam " },
            new string[] { "glue laminated", " glulam " },
            new string[] { "post-tensioned", " pt " },
            new string[] { "post tensioned", " pt " },
            new string[] { "rammed earth", " rammedearth " },
            new string[] { "cast-in-place", " insitu " },
            new string[] { "cast in place", " insitu " },
            new string[] { "cast in situ", " insitu " },
            new string[] { "platsgjuten", " insitu " },
            new string[] { "korslimmat", " clt " },
            new string[] { "ready-mix", " insitu " },
            new string[] { "ready mix", " insitu " },
            new string[] { "readymix", " insitu " },
            new string[] { "ortbeton", " insitu " },
            new string[] { "in-situ", " insitu " },
            new string[] { "in situ", " insitu " }
        };

        public static readonly string[][] Phrases = PhrasesRaw.OrderByDescending(p => p[0].Length).ToArray();

        //--- TABLE A, Revit MaterialClass -> family ---------------------------------------
        //Generic and its siblings map to UNKNOWN and NOT to "Other". "Plywood" with a
        //MaterialClass of "Generic" is a real measured input, mapping Generic to Other would
        //drag it toward Plasterboard and Bitumen. UNKNOWN contributes exactly zero.
        public static readonly Dictionary<string, string> RevitClassToFamily = BuildRevitClassToFamily();

        private static Dictionary<string, string> BuildRevitClassToFamily()
        {
            Dictionary<string, string> m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            m["concrete"] = CarboFamily.CONCRETE;
            m["precast concrete"] = CarboFamily.CONCRETE;
            m["precastconcrete"] = CarboFamily.CONCRETE;
            m["metal"] = CarboFamily.METAL;
            m["steel"] = CarboFamily.STEEL;
            m["structural steel"] = CarboFamily.STEEL;
            m["iron"] = CarboFamily.STEEL;
            m["aluminium"] = CarboFamily.ALUMINIUM;
            m["aluminum"] = CarboFamily.ALUMINIUM;
            m["wood"] = CarboFamily.TIMBER;
            m["timber"] = CarboFamily.TIMBER;
            m["masonry"] = CarboFamily.MASONRY;
            m["brick"] = CarboFamily.MASONRY;
            m["blockwork"] = CarboFamily.MASONRY;
            m["block"] = CarboFamily.MASONRY;
            m["stone"] = CarboFamily.STONE;
            m["glass"] = CarboFamily.GLASS;
            m["glazing"] = CarboFamily.GLASS;
            m["plastic"] = CarboFamily.PLASTIC;
            m["plastics"] = CarboFamily.PLASTIC;
            m["polymer"] = CarboFamily.PLASTIC;
            m["insulation"] = CarboFamily.INSULATION;
            m["paint"] = CarboFamily.COATING;
            m["coating"] = CarboFamily.COATING;
            m["finishes"] = CarboFamily.COATING;
            m["gypsum"] = CarboFamily.BOARD;
            m["plaster"] = CarboFamily.BOARD;
            m["plasterboard"] = CarboFamily.BOARD;
            m["membrane"] = CarboFamily.BITUMEN;
            m["roofing"] = CarboFamily.BITUMEN;
            m["waterproofing"] = CarboFamily.BITUMEN;
            m["earth"] = CarboFamily.EARTH;
            m["soil"] = CarboFamily.EARTH;
            m["site"] = CarboFamily.EARTH;
            m["gravel"] = CarboFamily.EARTH;
            m["aggregate"] = CarboFamily.EARTH;
            m["ceramic"] = CarboFamily.MASONRY;
            m["tile"] = CarboFamily.MASONRY;
            m["generic"] = CarboFamily.UNKNOWN;
            m["unassigned"] = CarboFamily.UNKNOWN;
            m["miscellaneous"] = CarboFamily.UNKNOWN;
            m["misc"] = CarboFamily.UNKNOWN;
            m["default"] = CarboFamily.UNKNOWN;
            m["none"] = CarboFamily.UNKNOWN;
            m["unknown"] = CarboFamily.UNKNOWN;
            m["na"] = CarboFamily.UNKNOWN;
            m["n/a"] = CarboFamily.UNKNOWN;
            m["<by category>"] = CarboFamily.UNKNOWN;
            m[""] = CarboFamily.UNKNOWN;
            return m;
        }

        //--- TABLE B, CarboMaterial.Category -> family ------------------------------------
        //Keys are CategoryKeys, that is folded and lowercased. English "Other" MUST be
        //UNKNOWN and not a family, the IStructE "Other" bucket holds Bitumen, Asphalt,
        //Plasterboard, Rammed earth and Bamboo, so mapping it would manufacture false
        //agreement. Such a row's family is inferred from its NAME instead.
        public static readonly Dictionary<string, string> CategoryToFamily = BuildCategoryToFamily();

        private static Dictionary<string, string> BuildCategoryToFamily()
        {
            Dictionary<string, string> m = new Dictionary<string, string>(StringComparer.Ordinal);

            //English
            m["concrete"] = CarboFamily.CONCRETE;
            m["steel"] = CarboFamily.STEEL;
            m["timber"] = CarboFamily.TIMBER;
            m["aluminium"] = CarboFamily.ALUMINIUM;
            m["stone"] = CarboFamily.STONE;
            m["blocks and bricks"] = CarboFamily.MASONRY;
            m["bricks & block"] = CarboFamily.MASONRY;
            m["bricks and block"] = CarboFamily.MASONRY;
            m["other"] = CarboFamily.UNKNOWN;

            //German, Okobaudat
            m["mineralische baustoffe"] = CarboFamily.MINERAL;
            m["kunststoffe"] = CarboFamily.PLASTIC;
            m["metalle"] = CarboFamily.METAL;
            m["komponenten von fenstern und vorhangfassaden"] = CarboFamily.GLASS;
            m["beschichtungen"] = CarboFamily.COATING;
            m["dammstoffe"] = CarboFamily.INSULATION;
            m["daemmstoffe"] = CarboFamily.INSULATION;
            m["gebaudetechnik"] = CarboFamily.SERVICES;
            m["gebaeudetechnik"] = CarboFamily.SERVICES;
            m["komposite"] = CarboFamily.COMPOSITE;
            m["holz"] = CarboFamily.TIMBER;
            m["sonstige"] = CarboFamily.UNKNOWN;

            //Swedish, Boverkets
            m["betong"] = CarboFamily.CONCRETE;
            m["isolering"] = CarboFamily.INSULATION;
            m["fonster, dorrar och glas"] = CarboFamily.GLASS;
            m["murblock och tegel"] = CarboFamily.MASONRY;
            m["stal och andra metaller"] = CarboFamily.METAL;
            m["byggskivor"] = CarboFamily.BOARD;
            m["bruk och bindemedel"] = CarboFamily.CEMENTITIOUS;
            m["tatskikt"] = CarboFamily.BITUMEN;
            m["travaror"] = CarboFamily.TIMBER;
            m["farg och fog"] = CarboFamily.COATING;

            return m;
        }

        //--- TABLE C, family stems ---------------------------------------------------------
        //Substring within ONE token, longest match wins, minimum stem length 4. This is the
        //ONLY place substring matching is permitted, and it exists solely to crack Germanic
        //and Scandinavian compounds such as "Bewehrungsstahl" and "Armeringsstal".
        //Note "cast" is deliberately NOT a stem, so the Cast/Precast defect cannot reappear.
        public static readonly List<string[]> FamilyStems = BuildFamilyStems();

        private static List<string[]> BuildFamilyStems()
        {
            List<string[]> list = new List<string[]>();

            AddStems(list, CarboFamily.CONCRETE, "concrete", "concret", "beton", "betong");
            AddStems(list, CarboFamily.CEMENTITIOUS, "cement", "zement", "mortar", "morte", "screed", "estrich", "bruk");
            AddStems(list, CarboFamily.STEEL, "steel", "stahl", "stal");
            AddStems(list, CarboFamily.REBAR, "rebar", "reinforc", "bewehrung", "armering", "armerad", "slakarmer");
            AddStems(list, CarboFamily.STAINLESS, "stainless", "edelstahl", "rostfri");
            AddStems(list, CarboFamily.ALUMINIUM, "alumin");
            AddStems(list, CarboFamily.METAL_OTHER, "copper", "kupfer", "koppar", "zinc", "zink", "lead", "blei", "brass");
            AddStems(list, CarboFamily.TIMBER, "timber", "wood", "holz", "trae", "lumber", "glulam", "plywood", "sperrholz",
                                               "chipboard", "softwood", "hardwood", "joist", "studwork", "virke", "limtra", "osb", "mdf");
            AddStems(list, CarboFamily.MASONRY, "brick", "block", "tegel", "murblock", "ziegel", "masonry", "mursten", "mauerwerk");
            AddStems(list, CarboFamily.STONE, "stone", "granit", "limestone", "sandstone", "skiffer", "naturstein", "sten");
            AddStems(list, CarboFamily.GLASS, "glass", "glas", "fenster", "fonster");
            AddStems(list, CarboFamily.PLASTIC, "plastic", "kunststoff", "polystyr", "polyeth", "polyamid", "epdm");
            AddStems(list, CarboFamily.INSULATION, "insulat", "isoler", "dammstoff", "rockwool", "glasswool", "mineralull");
            AddStems(list, CarboFamily.COATING, "coating", "beschicht", "intumescent", "farg", "lack");
            AddStems(list, CarboFamily.BITUMEN, "bitumen", "asphalt", "asfalt");
            AddStems(list, CarboFamily.BOARD, "plasterboard", "gypsum", "gips", "byggskiv", "fibercement");

            //Longest stem first so the compound rule resolves to the most specific family.
            return list.OrderByDescending(s => s[0].Length).ToList();
        }

        private static void AddStems(List<string[]> list, string family, params string[] stems)
        {
            foreach (string s in stems)
                list.Add(new string[] { s, family });
        }

        //--- TABLE C1b, which family stems are the FAMILY WORD itself ----------------------
        //Table C above maps a stem to a family, but it mixes two very different kinds of word.
        //"timber", "holz", "trae" and "virke" are the SAME word in four languages. "plywood",
        //"osb" and "chipboard" are three DIFFERENT products that merely live in that family.
        //Collapsing both kinds onto the family's canonical concept made every sheet material
        //an identical token, so "Plywood" scored a dead heat against OSB and lost the tie on
        //name length. Only the stems listed here collapse; every other stem keeps its own key
        //and carries its family alongside it.
        public static readonly HashSet<string> FamilyWordStems = BuildFamilyWordStems();

        private static HashSet<string> BuildFamilyWordStems()
        {
            return new HashSet<string>(StringComparer.Ordinal)
            {
                //Concrete and cementitious. Mortar, screed, estrich and bruk are products.
                "concrete", "concret", "beton", "betong", "cement", "zement",
                //Ferrous. Copper, zinc, lead and brass are distinct metals, not synonyms.
                "steel", "stahl", "stal",
                "rebar", "reinforc", "bewehrung", "armering", "armerad", "slakarmer",
                "stainless", "edelstahl", "rostfri",
                "alumin",
                //Timber. Lumber, glulam, plywood, chipboard, softwood, hardwood, joist,
                //studwork, limtra, osb and mdf are products and must stay distinguishable.
                "timber", "wood", "holz", "trae", "virke",
                //Masonry and stone. Brick, block, tegel, ziegel and mursten are products, as
                //are granite, limestone, sandstone and slate.
                "masonry", "mauerwerk",
                "stone", "sten", "naturstein",
                //Glass. Fenster and fonster mean window, which is a component, not the material.
                "glass", "glas",
                "plastic", "kunststoff",
                "insulat", "isoler", "dammstoff",
                "coating", "beschicht",
                "bitumen", "asphalt", "asfalt",
                "gypsum", "gips"
            };
        }

        //--- TABLE C2, canonical concept per family ----------------------------------------
        //This is what makes the design multilingual for free, reinforcement / rebar /
        //bewehrungsstahl / armeringsstal all collapse onto the single key "rebar".
        public static readonly Dictionary<string, string> FamilyCanonicalConcept = BuildFamilyCanonical();

        private static Dictionary<string, string> BuildFamilyCanonical()
        {
            Dictionary<string, string> m = new Dictionary<string, string>(StringComparer.Ordinal);
            m[CarboFamily.CONCRETE] = "concrete";
            m[CarboFamily.CEMENTITIOUS] = "cement";
            m[CarboFamily.STEEL] = "steel";
            m[CarboFamily.REBAR] = "rebar";
            m[CarboFamily.STAINLESS] = "stainless";
            m[CarboFamily.ALUMINIUM] = "aluminium";
            m[CarboFamily.METAL_OTHER] = "metal";
            m[CarboFamily.METAL] = "metal";
            m[CarboFamily.MINERAL] = "mineral";
            m[CarboFamily.TIMBER] = "timber";
            m[CarboFamily.MASONRY] = "masonry";
            m[CarboFamily.STONE] = "stone";
            m[CarboFamily.GLASS] = "glass";
            m[CarboFamily.PLASTIC] = "plastic";
            m[CarboFamily.INSULATION] = "insulation";
            m[CarboFamily.COATING] = "coating";
            m[CarboFamily.BITUMEN] = "bitumen";
            m[CarboFamily.BOARD] = "board";
            m[CarboFamily.EARTH] = "earth";
            m[CarboFamily.SERVICES] = "services";
            m[CarboFamily.COMPOSITE] = "composite";
            return m;
        }

        //--- TABLE D, explicit concepts, exact whole token ---------------------------------
        //Deliberately small, rule 2.7b already unifies every family word across all three
        //languages without any entry here.
        public static readonly Dictionary<string, string> Concepts = BuildConcepts();

        private static Dictionary<string, string> BuildConcepts()
        {
            Dictionary<string, string> m = new Dictionary<string, string>(StringComparer.Ordinal);
            AddConcept(m, "insitu", "insitu", "cast", "poured", "fabriksbetong", "frischbeton");
            AddConcept(m, "precast", "precast", "prefab", "prefabricated", "prefabricerad", "fertigteil", "fertigteildecken");
            AddConcept(m, "concrete", "rc");
            AddConcept(m, "clt", "clt", "brettsperrholz");
            AddConcept(m, "glulam", "glulam", "limtra");
            AddConcept(m, "lvl", "lvl");
            AddConcept(m, "plywood", "plywood", "ply", "faner");
            AddConcept(m, "osb", "osb");
            AddConcept(m, "profiledsheet", "deck", "decking", "profiled", "trapezoidal", "trapezblech", "profilplat");
            AddConcept(m, "plate", "plate", "sheet", "blech", "plat");
            AddConcept(m, "section", "section", "profile", "profil", "beam", "column", "rolled", "balk", "pelare");
            AddConcept(m, "sawntimber", "lumber", "dimensional", "sawn", "softwood", "konstruktionsvirke", "regelvirke");
            AddConcept(m, "stud", "stud", "studwork", "regel", "lattreglar");
            AddConcept(m, "prestress", "pt", "strand", "spannbeton", "spannarmering", "forspand");
            AddConcept(m, "galvanised", "galvanised", "galvanized", "verzinkt", "galvaniserad");
            AddConcept(m, "stainless", "stainless", "edelstahl", "rostfri", "rostfritt");
            AddConcept(m, "certified", "fsc", "pefc");
            AddConcept(m, "hollow", "hollow", "halbjalklag");
            AddConcept(m, "air", "air", "void", "luft");
            return m;
        }

        private static void AddConcept(Dictionary<string, string> m, string conceptId, params string[] tokens)
        {
            foreach (string t in tokens)
                m[t] = conceptId;
        }

        //--- TABLE E, stop words ------------------------------------------------------------
        //Chosen from measured document frequency. NEVER applied to a token that resolved to a
        //family or to a concept, which is what guarantees concrete, steel, timber, beton,
        //betong, stal and holz keep full weight regardless of document frequency.
        public static readonly HashSet<string> StopWords = BuildStopWords();

        private static HashSet<string> BuildStopWords()
        {
            HashSet<string> s = new HashSet<string>(StringComparer.Ordinal);

            //English provenance and filler
            string[] en = new string[]
            {
                "uk", "global", "europe", "european", "eu", "china", "world", "import", "imported",
                "weighted", "average", "avg", "mean", "generic", "upper", "lower", "bound",
                "percentile", "80th", "20th", "epd", "typical", "general", "product", "data",
                "value", "and", "or", "the", "with", "without", "per", "from", "incl", "excl",
                "mix", "type", "class", "grade", "new", "standard", "default", "misc", "miscellaneous"
            };
            //German manufacturer noise, measured Okobaudat document frequency
            //holcim 78, werk 77, siloware 63, und 45, gmbh 43
            string[] de = new string[]
            {
                "und", "mit", "aus", "ohne", "von", "der", "die", "das", "gmbh", "kg", "co",
                "werk", "holcim", "siloware", "sackware", "gruppe", "durchschnittlicher",
                "durchschnitt", "produkt", "deutschland", "dicke"
            };
            //Swedish function words
            string[] sv = new string[]
            {
                "och", "utan", "med", "typ", "ospecificerat", "ospecificerad", "primar",
                "skrotbaserad", "alla", "sorter", "inkl", "exkl", "samt", "ovrigt"
            };

            foreach (string w in en) s.Add(w);
            foreach (string w in de) s.Add(w);
            foreach (string w in sv) s.Add(w);

            //Belt and braces, a stop word may never shadow a family stem or a concept.
            //Rule 2.6 already guarantees this by consulting C and D first, this simply makes
            //the tables provably disjoint.
            foreach (string[] stem in FamilyStems)
                s.Remove(stem[0]);
            foreach (KeyValuePair<string, string> kv in Concepts)
                s.Remove(kv.Key);

            return s;
        }

        //--- TABLE F, specialisers ----------------------------------------------------------
        //A specialiser NARROWS the material. Carrying one that was not asked for costs points.
        //"softwood" and "sawntimber" are NOT specialisers, softwood is the default for plywood
        //and dimensional lumber. fsc and pefc share the single id "certified" so a row reading
        //"100% FSC/PEFC" pays one penalty and not two.
        public static readonly List<string[]> Specialisers = BuildSpecialisers();

        private static List<string[]> BuildSpecialisers()
        {
            List<string[]> list = new List<string[]>();
            string[] plain = new string[]
            {
                "stainless", "rebar", "reused", "recycled", "galvanised", "precast", "prestress",
                "hollow", "lightweight", "aac", "dense", "toughened", "hardwood", "engineered",
                "glulam", "clt", "lvl", "certified", "fibre", "faser", "glasfaser",
                "klimatforbattrad", "rohr", "pipe", "schacht", "kanal", "tube", "pflasterstein",
                "paver", "estrich", "tile", "fliese", "dach", "roof"
            };
            foreach (string p in plain)
                list.Add(new string[] { p, p });

            list.Add(new string[] { "fsc", "certified" });
            list.Add(new string[] { "pefc", "certified" });
            list.Add(new string[] { "galvanized", "galvanised" });
            list.Add(new string[] { "verzinkt", "galvanised" });
            list.Add(new string[] { "rostfri", "stainless" });
            list.Add(new string[] { "edelstahl", "stainless" });
            list.Add(new string[] { "fertigteil", "precast" });
            list.Add(new string[] { "armerad", "rebar" });

            return list;
        }

        /// <summary>
        /// The set of specialiser ids. A token whose resolved KEY is one of these is a
        /// specialiser too, even when the word itself looks nothing like the id, which is how
        /// the Swedish "Spannarmering" (key "prestress") gets priced against a plain
        /// reinforcement lookup.
        /// </summary>
        public static readonly HashSet<string> SpecialiserIds = BuildSpecialiserIds();

        private static HashSet<string> BuildSpecialiserIds()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string[] entry in Specialisers)
                set.Add(entry[1]);
            return set;
        }

        //--- TABLE F2, implied specialisers -------------------------------------------------
        //Asserting the key excuses the values at zero cost, metal deck IS galvanised by
        //definition, so "Galvanised profiled sheet UK" must not be penalised for saying so.
        public static readonly Dictionary<string, string[]> ImpliedSpecialisers = BuildImplied();

        private static Dictionary<string, string[]> BuildImplied()
        {
            Dictionary<string, string[]> m = new Dictionary<string, string[]>(StringComparer.Ordinal);
            m["profiledsheet"] = new string[] { "galvanised" };
            m["precast"] = new string[] { "hollow" };
            m["clt"] = new string[] { "engineered" };
            m["glulam"] = new string[] { "engineered" };
            m["lvl"] = new string[] { "engineered" };
            return m;
        }

        //--- TABLE H, mutually exclusive concept pairs ---------------------------------------
        public static readonly string[][] ExclusivePairs = new string[][]
        {
            new string[] { "insitu", "precast" },
            new string[] { "sawntimber", "hardwood" },
            new string[] { "stainless", "galvanised" }
        };

        //--- TABLE J, Revit ELEMENT category -> form hint tokens ------------------------------
        //These earn the small B_FORM bonus only, they NEVER touch the family term. This is what
        //turns the wrong argument passed by DataExportUtils into a small piece of genuine
        //signal instead of poison.
        public static readonly Dictionary<string, string[]> ElementCategoryHints = BuildElementHints();

        private static Dictionary<string, string[]> BuildElementHints()
        {
            Dictionary<string, string[]> m = new Dictionary<string, string[]>(StringComparer.Ordinal);
            m["walls"] = new string[] { "block", "brick" };
            m["wall"] = new string[] { "block", "brick" };
            m["floors"] = new string[] { "slab", "profiledsheet", "flooring", "plate" };
            m["floor"] = new string[] { "slab", "profiledsheet", "flooring", "plate" };
            m["roofs"] = new string[] { "roof" };
            m["roof"] = new string[] { "roof" };
            m["ceilings"] = new string[] { "ceiling" };
            m["stairs"] = new string[] { "stair" };
            m["structural framing"] = new string[] { "section" };
            m["structural columns"] = new string[] { "section" };
            m["structural foundations"] = new string[] { "foundation", "footing", "pile" };
            m["pile"] = new string[] { "pile" };
            m["piles"] = new string[] { "pile" };
            m["structural rebar"] = new string[] { "rebar" };
            m["reinforcement"] = new string[] { "rebar" };
            m["generic models"] = new string[0];
            m["windows"] = new string[0];
            m["doors"] = new string[0];
            m["curtain panels"] = new string[0];
            m["parts"] = new string[0];
            return m;
        }
    }

    /// <summary>
    /// Normalisation, tokenisation and grade extraction. All pure static helpers.
    /// </summary>
    internal static class CarboMatchNorm
    {
        //--- Section 3.1, exactly three grade regexes, all compiled once ---------------------
        //Nothing speculative is shipped, a bare C-class, GL glulam, B-series rebar, D-series
        //hardwood, EN AW aluminium and K-grade regex all have ZERO true positives in the
        //shipped corpus and several have measured false positives.
        public static readonly Regex RxGradeConcrete = new Regex(
            @"(?<![A-Za-z0-9])[Cc]\s?(?<n>\d{1,3})\s?/\s?(?<d>\d{2,3})(?![0-9/])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static readonly Regex RxGradeSteel = new Regex(
            @"(?<![A-Za-z0-9])[Ss](?<n>235|275|355|420|435|450|460|500|690)(?:JR|J0|J2|K2|NL|ML|N|M|W|jr|j0|j2|k2|nl|ml|n|m|w)?(?![A-Za-z0-9])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static readonly Regex RxGradeBlank = new Regex(
            @"(?<![A-Za-z0-9])[Cc]\s?\d{1,3}\s?/\s?\d{2,3}(?![0-9/])|(?<![A-Za-z0-9])[Ss](?:235|275|355|420|435|450|460|500|690)(?:JR|J0|J2|K2|NL|ML|N|M|W|jr|j0|j2|k2|nl|ml|n|m|w)?(?![A-Za-z0-9])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex RxUnitToken = new Regex(
            @"^[0-9]+(mm2|m2|m3|mm|cm|mpa|kg|m|g)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public const string GradeFamilyConcrete = "C";
        public const string GradeFamilySteel = "S";

        //Concrete cylinder strengths in ladder order, index = position + 1.
        private static readonly int[] ConcreteLadder = new int[]
        { 8, 12, 16, 20, 25, 28, 30, 32, 35, 40, 45, 50, 55, 60, 70, 80, 90, 100 };

        private static readonly Dictionary<string, int> SteelLadder = BuildSteelLadder();

        private static Dictionary<string, int> BuildSteelLadder()
        {
            Dictionary<string, int> m = new Dictionary<string, int>(StringComparer.Ordinal);
            m["S235"] = 1; m["S275"] = 2; m["S355"] = 3; m["S420"] = 4; m["S435"] = 5;
            m["S450"] = 6; m["S460"] = 7; m["S500"] = 8; m["S690"] = 9;
            return m;
        }

        private static readonly string[] CompetingRegionTokens = new string[]
        { "global", "europe", "european", "eu", "china", "world" };

        /// <summary>Case folding: lowercase invariant, strip diacritics, fold the odd letters.</summary>
        public static string FoldCase(string p)
        {
            if (string.IsNullOrEmpty(p))
                return "";

            string f = p.ToLowerInvariant();

            try
            {
                string d = f.Normalize(NormalizationForm.FormD);
                StringBuilder sb = new StringBuilder(d.Length);
                foreach (char c in d)
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                        sb.Append(c);
                }
                f = sb.ToString().Normalize(NormalizationForm.FormC);
            }
            catch
            {
                //Keep f unfolded, a folding failure must never break a lookup.
            }

            //These do not decompose, so they are folded explicitly.
            f = f.Replace("ß", "ss");   //sharp s
            f = f.Replace("æ", "ae");   //ae ligature
            f = f.Replace("ø", "o");    //o slash
            f = f.Replace("đ", "d");    //d stroke

            return f;
        }

        /// <summary>Phrase substitution, ordinal ignore case, longest pattern first.</summary>
        public static string PhraseSubstitute(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "";

            string s = raw;
            foreach (string[] phrase in CarboMatchTables.Phrases)
                s = ReplaceOrdinalIgnoreCase(s, phrase[0], phrase[1]);

            return s;
        }

        private static string ReplaceOrdinalIgnoreCase(string source, string pattern, string replacement)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(pattern))
                return source;

            int at = source.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return source;

            StringBuilder sb = new StringBuilder(source.Length + 8);
            int pos = 0;
            while (at >= 0)
            {
                sb.Append(source, pos, at - pos);
                sb.Append(replacement);
                pos = at + pattern.Length;
                at = source.IndexOf(pattern, pos, StringComparison.OrdinalIgnoreCase);
            }
            sb.Append(source, pos, source.Length - pos);
            return sb.ToString();
        }

        public static string CollapseWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            StringBuilder sb = new StringBuilder(s.Length);
            bool lastWasSpace = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (lastWasSpace == false && sb.Length > 0)
                        sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>Whole token containment test on an already folded string.</summary>
        public static bool ContainsWholeToken(string folded, string token)
        {
            if (string.IsNullOrEmpty(folded) || string.IsNullOrEmpty(token))
                return false;

            int at = folded.IndexOf(token, StringComparison.Ordinal);
            while (at >= 0)
            {
                bool leftOk = (at == 0) || (char.IsLetterOrDigit(folded[at - 1]) == false);
                int after = at + token.Length;
                bool rightOk = (after >= folded.Length) || (char.IsLetterOrDigit(folded[after]) == false);

                if (leftOk && rightOk)
                    return true;

                at = folded.IndexOf(token, at + 1, StringComparison.Ordinal);
            }
            return false;
        }

        /// <summary>The CategoryKey, that is trim plus case folding plus whitespace collapse.</summary>
        public static string CategoryKey(string category)
        {
            return CollapseWhitespace(FoldCase((category ?? "").Trim()));
        }

        //--- Section 3.2, grade extraction ---------------------------------------------------
        /// <summary>
        /// Extracts canonical grade keys ("C:C32/40", "S:S355") from a string. Runs on the
        /// ORIGINAL case string, both regexes, results de-duplicated.
        /// </summary>
        public static void ExtractGradesInto(string source, List<string> target)
        {
            if (string.IsNullOrEmpty(source) || target == null)
                return;

            foreach (Match m in RxGradeConcrete.Matches(source))
            {
                string key = GradeFamilyConcrete + ":C" + m.Groups["n"].Value + "/" + m.Groups["d"].Value;
                if (target.Contains(key) == false)
                    target.Add(key);
            }

            foreach (Match m in RxGradeSteel.Matches(source))
            {
                string key = GradeFamilySteel + ":S" + m.Groups["n"].Value;
                if (target.Contains(key) == false)
                    target.Add(key);
            }
        }

        public static List<string> ExtractGrades(string source)
        {
            List<string> list = new List<string>();
            ExtractGradesInto(source, list);
            return list;
        }

        public static string GradeFamilyOf(string gradeKey)
        {
            if (string.IsNullOrEmpty(gradeKey))
                return "";
            int at = gradeKey.IndexOf(':');
            return at <= 0 ? "" : gradeKey.Substring(0, at);
        }

        /// <summary>Position on the strength ladder, used to grade the contradiction penalty.</summary>
        public static int LadderIndex(string gradeKey)
        {
            string family = GradeFamilyOf(gradeKey);
            string value = gradeKey.Substring(family.Length + 1);

            if (family == GradeFamilySteel)
            {
                int idx;
                if (SteelLadder.TryGetValue(value, out idx))
                    return idx;
                return 0;
            }

            //Concrete: parse the cylinder strength and snap to the nearest listed class.
            int slash = value.IndexOf('/');
            if (slash <= 1)
                return 0;

            int n;
            if (int.TryParse(value.Substring(1, slash - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) == false)
                return 0;

            int best = 1;
            int bestDelta = int.MaxValue;
            for (int i = 0; i < ConcreteLadder.Length; i++)
            {
                int delta = Math.Abs(ConcreteLadder[i] - n);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = i + 1;
                }
            }
            return best;
        }

        /// <summary>
        /// Section 3.4. Strictly neutral when either side is silent, which is the 93.5% case,
        /// so there is never a reward for merely carrying a grade.
        /// </summary>
        public static double GradeAgreement(List<string> lookupGrades, List<string> rowGrades)
        {
            if (lookupGrades == null || rowGrades == null || lookupGrades.Count == 0 || rowGrades.Count == 0)
                return 0.0;

            bool sharedFamily = false;
            int minSteps = int.MaxValue;

            foreach (string a in lookupGrades)
            {
                string fa = GradeFamilyOf(a);
                foreach (string b in rowGrades)
                {
                    if (GradeFamilyOf(b) != fa)
                        continue;

                    sharedFamily = true;

                    if (string.Equals(a, b, StringComparison.Ordinal))
                        return 1.0;

                    int ia = LadderIndex(a);
                    int ib = LadderIndex(b);
                    if (ia > 0 && ib > 0)
                    {
                        int steps = Math.Abs(ia - ib);
                        if (steps < minSteps)
                            minSteps = steps;
                    }
                }
            }

            //No family on both sides means the grade signal is irrelevant, not negative.
            if (sharedFamily == false)
                return 0.0;

            if (minSteps == int.MaxValue || minSteps <= 0)
                return 0.0;

            if (minSteps == 1)
                return -0.6;
            if (minSteps == 2)
                return -0.8;

            return -1.0;
        }

        //--- Section 1 + 2, the whole pipeline ------------------------------------------------
        /// <summary>
        /// Runs the full normalisation and tokenisation pipeline over one name.
        /// </summary>
        /// <param name="name">The material name, null safe.</param>
        /// <param name="extraGradeSource">Optional second source of grades, e.g. cm.Grade.</param>
        public static CarboBag Build(string name, string extraGradeSource)
        {
            CarboBag bag = new CarboBag();

            string raw = (name ?? "").Trim();
            bag.Raw = raw;

            //1.2 grades are extracted from the ORIGINAL case string, from BOTH sources.
            ExtractGradesInto(raw, bag.Grades);
            if (string.IsNullOrEmpty(extraGradeSource) == false)
                ExtractGradesInto(extraGradeSource.Trim(), bag.Grades);

            //1.3 and 1.4
            string p = PhraseSubstitute(raw);
            string f = FoldCase(p);
            bag.Folded = f;

            //Region and variant detection run BEFORE stop word removal, because the tokens
            //involved are themselves stop words.
            foreach (string t in CarboMatchOptions.PreferredRegionTokens)
            {
                if (ContainsWholeToken(f, t))
                {
                    bag.HasPreferredRegion = true;
                    break;
                }
            }
            foreach (string t in CompetingRegionTokens)
            {
                if (ContainsWholeToken(f, t))
                {
                    bag.HasCompetingRegion = true;
                    break;
                }
            }
            if (f.IndexOf("upper bound", StringComparison.Ordinal) >= 0 || f.IndexOf("80th percentile", StringComparison.Ordinal) >= 0)
                bag.VariantRank = 1;
            else if (f.IndexOf("lower bound", StringComparison.Ordinal) >= 0 || f.IndexOf("20th percentile", StringComparison.Ordinal) >= 0)
                bag.VariantRank = 2;

            //1.5 blank the grade spans so '/' never has to survive tokenisation, that is what
            //stops "c32/40" degrading into the useless tokens "c32" and "40".
            string n = RxGradeBlank.Replace(f, " ");

            //1.6
            bag.NormName = CollapseWhitespace(n);

            //1.7
            Tokenise(bag);

            return bag;
        }

        private static void Tokenise(CarboBag bag)
        {
            string s = bag.NormName;
            int i = 0;
            int len = s.Length;

            while (i < len)
            {
                if (char.IsLetterOrDigit(s[i]) == false)
                {
                    i++;
                    continue;
                }

                int start = i;
                while (i < len && char.IsLetterOrDigit(s[i]))
                    i++;

                string token = s.Substring(start, i - start);
                AddToken(bag, token);
            }

            double total = 0;
            foreach (KeyValuePair<string, CarboTok> kv in bag.Tokens)
                total += kv.Value.Weight;
            bag.TotalWeight = total;
        }

        private static void AddToken(CarboBag bag, string token)
        {
            //2.2a all digits
            bool allDigits = true;
            for (int k = 0; k < token.Length; k++)
            {
                if (char.IsDigit(token[k]) == false)
                {
                    allDigits = false;
                    break;
                }
            }
            if (allDigits)
                return;

            //2.2b dimension and unit tokens
            if (RxUnitToken.IsMatch(token))
                return;

            //2.2c very short tokens are noise, except the three that carry meaning here
            bool isShortKeeper = (token == "rc" || token == "pc" || token == "pt");
            if (token.Length <= 2 && isShortKeeper == false)
                return;

            //2.3 conservative plural stem, must not turn "glass" into "glas" or "porous" into "porou"
            string stem = token;
            if (stem.Length >= 5 && stem[stem.Length - 1] == 's'
                && stem.EndsWith("ss", StringComparison.Ordinal) == false
                && stem.EndsWith("us", StringComparison.Ordinal) == false)
            {
                stem = stem.Substring(0, stem.Length - 1);
            }

            //Specialisers are collected from every surviving token, including ones that are
            //about to be dropped as stop words.
            CollectSpecialisers(bag, stem);

            //2.4 family by longest stem inside the token, the only substring rule in the design
            string family = CarboFamily.UNKNOWN;
            string matchedStem = null;
            foreach (string[] entry in CarboMatchTables.FamilyStems)
            {
                if (stem.IndexOf(entry[0], StringComparison.Ordinal) >= 0)
                {
                    family = entry[1];
                    matchedStem = entry[0];
                    break;   //FamilyStems is sorted longest first
                }
            }

            //Whether the stem that matched is the family's own word rather than a product
            //inside it. Drives both the key below and how much a row's Category alone can
            //satisfy this token during scoring.
            bool isFamilyWord = (matchedStem != null && CarboMatchTables.FamilyWordStems.Contains(matchedStem));

            //2.5 explicit concept
            string concept = null;
            CarboMatchTables.Concepts.TryGetValue(stem, out concept);

            //2.6 stop word, ONLY when the token resolved to neither a family nor a concept
            bool hasFamily = (family != CarboFamily.UNKNOWN);
            if (hasFamily == false && concept == null && CarboMatchTables.StopWords.Contains(stem))
                return;

            //2.7 key
            string key;
            if (concept != null)
            {
                key = concept;
            }
            else if (hasFamily && isFamilyWord)
            {
                //Only a genuine family WORD collapses onto the canonical concept. That is what
                //unifies reinforcement / bewehrungsstahl / armeringsstal across languages.
                string canonical;
                if (CarboMatchTables.FamilyCanonicalConcept.TryGetValue(family, out canonical) == false)
                    canonical = stem;
                key = canonical;
            }
            else
            {
                key = stem;
            }

            //A token whose resolved key IS a specialiser id counts as one, even when the word
            //itself looks nothing like the id, e.g. "spannarmering" resolves to "prestress".
            if (CarboMatchTables.SpecialiserIds.Contains(key))
                bag.Specialisers.Add(key);

            //2.8 weight
            double weight;
            if (hasFamily)
                weight = 2.0;
            else if (concept != null)
                weight = 1.5;
            else if (stem.Length >= 4)
                weight = 1.0;
            else
                weight = 0.5;

            //2.9 deduplicate by key, keep the strongest
            CarboTok existing;
            if (bag.Tokens.TryGetValue(key, out existing))
            {
                if (weight > existing.Weight)
                {
                    existing.Weight = weight;
                    existing.Family = family;
                    existing.IsFamilyWord = isFamilyWord;
                }
                else if (existing.Family == CarboFamily.UNKNOWN && hasFamily)
                {
                    existing.Family = family;
                    existing.IsFamilyWord = isFamilyWord;
                }
                return;
            }

            CarboTok tok = new CarboTok();
            tok.Key = key;
            tok.Weight = weight;
            tok.Family = family;
            tok.IsFamilyWord = isFamilyWord;
            bag.Tokens[key] = tok;
        }

        private static void CollectSpecialisers(CarboBag bag, string stem)
        {
            foreach (string[] entry in CarboMatchTables.Specialisers)
            {
                string text = entry[0];
                bool hit = string.Equals(stem, text, StringComparison.Ordinal);
                if (hit == false && text.Length >= 4 && stem.IndexOf(text, StringComparison.Ordinal) >= 0)
                    hit = true;

                if (hit)
                    bag.Specialisers.Add(entry[1]);
            }
        }

        //--- Section 4, family resolution -------------------------------------------------------
        /// <summary>
        /// Section 4.4. Refines a coarse base family using the families borne by the name's
        /// tokens, but only along a single ancestor chain. Two incomparable candidates leave
        /// the base untouched, which is what keeps "Stainless Steel Rebar" at STEEL.
        /// </summary>
        public static string ResolveFamily(string baseFamily, CarboBag bag)
        {
            if (baseFamily == null)
                baseFamily = CarboFamily.UNKNOWN;

            bool isUnknown = string.Equals(baseFamily, CarboFamily.UNKNOWN, StringComparison.Ordinal);

            if (isUnknown == false && CarboFamily.IsCoarse(baseFamily) == false && CarboFamily.HasAnyChildren(baseFamily) == false)
                return baseFamily;

            List<string> candidates = new List<string>();
            foreach (KeyValuePair<string, CarboTok> kv in bag.Tokens)
            {
                string f = kv.Value.Family;
                if (f == CarboFamily.UNKNOWN)
                    continue;

                //An UNKNOWN base places no constraint at all, that is how "Bitumen" filed
                //under the English "Other" bucket still resolves to BITUMEN.
                if (isUnknown == false && CarboFamily.IsSameOrDescendant(f, baseFamily) == false)
                    continue;

                if (candidates.Contains(f) == false)
                    candidates.Add(f);
            }

            if (candidates.Count == 0)
                return baseFamily;

            //Most specific first
            string deepest = candidates[0];
            foreach (string c in candidates)
            {
                if (CarboFamily.Depth(c) > CarboFamily.Depth(deepest))
                    deepest = c;
            }

            //Every other candidate must be an ancestor of the deepest one
            foreach (string c in candidates)
            {
                if (CarboFamily.IsSameOrDescendant(deepest, c) == false)
                    return baseFamily;
            }

            return deepest;
        }

        /// <summary>Table B lookup, exact key then token boundary substring fallback.</summary>
        public static string FamilyFromCategoryKey(string categoryKey)
        {
            if (string.IsNullOrEmpty(categoryKey))
                return CarboFamily.UNKNOWN;

            string family;
            if (CarboMatchTables.CategoryToFamily.TryGetValue(categoryKey, out family))
                return family;

            //Recovers the one corrupt Okobaudat row whose Category leaked a thickness prefix.
            string best = CarboFamily.UNKNOWN;
            int bestLen = 0;
            foreach (KeyValuePair<string, string> kv in CarboMatchTables.CategoryToFamily)
            {
                if (kv.Key.Length <= bestLen)
                    continue;

                if (ContainsWholeToken(categoryKey, kv.Key))
                {
                    best = kv.Value;
                    bestLen = kv.Key.Length;
                }
            }
            return best;
        }

        /// <summary>Table A lookup, anything unrecognised is UNKNOWN and contributes zero.</summary>
        public static string FamilyFromRevitClass(string revitClass)
        {
            string key = (revitClass ?? "").Trim();
            if (key.Length == 0)
                return CarboFamily.UNKNOWN;

            string family;
            if (CarboMatchTables.RevitClassToFamily.TryGetValue(key, out family))
                return family;

            return CarboFamily.UNKNOWN;
        }

        public static double Clamp01(double v)
        {
            if (v < 0)
                return 0;
            if (v > 1)
                return 1;
            return v;
        }
    }

    /// <summary>
    /// The scored view of one candidate during a single lookup.
    /// </summary>
    internal sealed class CarboScored
    {
        public CarboRow Row;
        public double Raw;
        public int ScoreMilli;
        public double Coverage;
        public double Precision;
        public double SToken;
        public double SEdit;
        public double C;
        public double G;
        public double SBrief;
        public int UnrequestedSpecialisers;

        //False when every lookup token that matched was a plain word carrying no family and
        //no concept, i.e. the row and the lookup merely share an English word.
        public bool DomainMatched;
    }

    /// <summary>
    /// The material matching engine. Owns the index over a CarboDatabase's material list and
    /// runs the five tier cascade. Held by CarboDatabase in a PRIVATE field, so XmlSerializer
    /// never sees it and the .cxml schema is untouched.
    /// </summary>
    public sealed class CarboMaterialMatcher
    {
        private const string MemoSeparator = "|#|";

        private readonly CarboDatabase database;

        //Index state, all rebuilt whenever the material list changes in any way
        private List<CarboMaterial> idxList;
        private int idxCount;
        private int idxHash;
        private CarboRow[] rows;
        private Dictionary<string, int> exactIdx;
        private Dictionary<string, int> normIdx;
        private Dictionary<string, CarboMatchResult> memo;

        private CarboMapFile aliasTable;

        public CarboMaterialMatcher(CarboDatabase owner)
        {
            database = owner;
        }

        /// <summary>Injects the user's saved alias table so it can be consulted as Tier 0.</summary>
        public void SetAliasTable(CarboMapFile map)
        {
            aliasTable = map;

            //The natural integration order is to match first and inject the saved map second,
            //which is exactly the order that would otherwise leave every already-memoised name
            //answering from the pre-alias cascade and never reaching Tier 0 at all.
            Invalidate();
        }

        /// <summary>Forces the index to be rebuilt on the next lookup.</summary>
        public void Invalidate()
        {
            rows = null;
            idxList = null;
            idxCount = -1;
            idxHash = 0;
            if (memo != null)
                memo.Clear();
        }

        #region Index

        //There is no INotifyCollectionChanged and no version counter anywhere in the codebase,
        //and external code both mutates the list in place and rewrites Name/Category/Grade on
        //a live row with no event. So the index is validated on EVERY call against list
        //identity, Count and a content hash.
        private void EnsureIndex()
        {
            List<CarboMaterial> list = database != null ? database.CarboMaterialList : null;

            if (list == null)
            {
                if (rows == null || rows.Length != 0)
                    BuildIndex(null);
                return;
            }

            int h = ContentHash(list);

            if (rows != null && ReferenceEquals(idxList, list) && idxCount == list.Count && idxHash == h)
                return;

            BuildIndex(list);
            idxList = list;
            idxCount = list.Count;
            idxHash = h;
        }

        private static int ContentHash(List<CarboMaterial> list)
        {
            int h = 17;
            for (int i = 0; i < list.Count; i++)
            {
                CarboMaterial m = list[i];
                h = unchecked(h * 31 + (m == null || m.Name == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(m.Name)));
                h = unchecked(h * 31 + (m == null || m.Category == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(m.Category)));
                h = unchecked(h * 31 + (m == null || m.Grade == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(m.Grade)));

                //The memo hands back a CLONE, so an in place edit to a carbon factor would
                //otherwise be invisible for the rest of the session. MaterialEditor writes ECI
                //and Density straight onto the live row returned by GetExcactMatch, and the
                //old code re-read the live row on every call, so leaving these out would be a
                //regression rather than a mere cache subtlety.
                h = unchecked(h * 31 + (m == null ? 0 : m.ECI.GetHashCode()));
                h = unchecked(h * 31 + (m == null ? 0 : m.Density.GetHashCode()));
            }
            return h;
        }

        private void BuildIndex(List<CarboMaterial> list)
        {
            exactIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            normIdx = new Dictionary<string, int>(StringComparer.Ordinal);
            memo = new Dictionary<string, CarboMatchResult>(StringComparer.OrdinalIgnoreCase);

            HashSet<string> ambiguousNorm = new HashSet<string>(StringComparer.Ordinal);

            if (list == null || list.Count == 0)
            {
                rows = new CarboRow[0];
                return;
            }

            List<CarboRow> built = new List<CarboRow>(list.Count);

            for (int i = 0; i < list.Count; i++)
            {
                CarboMaterial cm = list[i];
                if (cm == null)
                    continue;

                string trimmedName = (cm.Name ?? "").Trim();
                if (trimmedName.Length == 0)
                    continue;   //A row with no name cannot be scored, but must not throw either.

                CarboRow row = new CarboRow();
                row.Index = built.Count;
                row.Material = cm;
                row.TrimmedName = trimmedName;
                row.Bag = CarboMatchNorm.Build(trimmedName, cm.Grade);
                row.CategoryKey = CarboMatchNorm.CategoryKey(cm.Category);

                string baseFamily = CarboMatchNorm.FamilyFromCategoryKey(row.CategoryKey);
                row.Family = CarboMatchNorm.ResolveFamily(baseFamily, row.Bag);

                built.Add(row);

                if (exactIdx.ContainsKey(trimmedName) == false)
                    exactIdx[trimmedName] = row.Index;

                if (row.Bag.NormName.Length > 0)
                {
                    if (normIdx.ContainsKey(row.Bag.NormName))
                        ambiguousNorm.Add(row.Bag.NormName);
                    else
                        normIdx[row.Bag.NormName] = row.Index;
                }
            }

            //The grade span is blanked before the normalised name is formed, so every row of a
            //strength ladder collapses onto one key - "insitu uk (25% ggbs)" covers C8/10
            //through C40/50, and Boverkets has two eleven way collisions. Keeping the first
            //would make the answer depend on file order AND would override an explicitly
            //supplied grade, because Tier 2 runs before the grade anchored tier. A key that
            //cannot identify one row is no key at all, so drop it and let the later tiers,
            //which do read the grade, decide.
            foreach (string dup in ambiguousNorm)
                normIdx.Remove(dup);

            rows = built.ToArray();
        }

        #endregion

        #region Public entry points

        /// <summary>
        /// Triages the legacy second argument. A value that parses as a grade goes to the grade
        /// slot, a Revit ELEMENT category goes to the form hint slot, anything else is treated
        /// as a Revit MaterialClass. An unrecognised value contributes exactly zero.
        /// </summary>
        public static CarboLookup BuildLookup(string materialToLookup, string revitMaterialCategory, string grade)
        {
            CarboLookup q = new CarboLookup();
            q.Name = materialToLookup ?? "";
            q.Grade = grade ?? "";
            q.MaterialClass = "";
            q.ElementCategory = "";

            string arg2 = (revitMaterialCategory ?? "").Trim();
            if (arg2.Length == 0)
                return q;

            //JsonExportUtils and CarboLifeRevitImport historically pass the GRADE here.
            if (CarboMatchNorm.ExtractGrades(arg2).Count > 0)
            {
                q.Grade = string.IsNullOrEmpty(q.Grade) ? arg2 : (q.Grade + " " + arg2);
                return q;
            }

            //DataExportUtils historically passes the Revit ELEMENT category here.
            if (CarboMatchTables.ElementCategoryHints.ContainsKey(CarboMatchNorm.CategoryKey(arg2)))
            {
                q.ElementCategory = arg2;
                return q;
            }

            q.MaterialClass = arg2;
            return q;
        }

        /// <summary>Runs the cascade. Never returns null and never throws on data.</summary>
        public CarboMatchResult FindMatch(CarboLookup query)
        {
            if (query == null)
                query = new CarboLookup();

            EnsureIndex();

            string rawName = (query.Name ?? "").Trim();

            //Case (a) of Section 9, nothing to look up.
            if (rawName.Length == 0)
                return Sentinel(rawName, "EmptyLookup", "No material name was supplied.");

            //Case (b), a genuinely empty database, reachable when a template fails to load.
            if (rows == null || rows.Length == 0)
                return Sentinel(rawName, "EmptyDatabase", "The material database is empty.");

            //TemplateName is part of the key because Tier 0 scopes the alias table by it, so
            //two templates can legitimately map the same Revit name to different materials.
            string memoKey = rawName + MemoSeparator + (query.MaterialClass ?? "") + MemoSeparator
                           + (query.Grade ?? "") + MemoSeparator + (query.ElementCategory ?? "")
                           + MemoSeparator + (query.TemplateName ?? "");

            CarboMatchResult cached;
            if (memo != null && memo.TryGetValue(memoKey, out cached))
                return CopyOf(cached);

            CarboMatchResult result = RunCascade(query, rawName);

            if (memo != null)
            {
                //A per-element export against a large user database can otherwise grow this
                //without bound, and every entry holds a deep cloned CarboMaterial.
                if (memo.Count >= CarboMatchOptions.MemoCap)
                    memo.Clear();

                memo[memoKey] = CopyOf(result);
            }

            return result;
        }

        /// <summary>Convenience overload applying the same argument 2 triage as the legacy method.</summary>
        public CarboMatchResult FindMatch(string name, string revitMaterialClass, string grade)
        {
            return FindMatch(BuildLookup(name, revitMaterialClass, grade));
        }

        /// <summary>Top N ranked candidates with their component breakdown. Each Material is a deep clone.</summary>
        public List<CarboMatchCandidate> RankCandidates(CarboLookup query, int take)
        {
            List<CarboMatchCandidate> output = new List<CarboMatchCandidate>();

            if (query == null || take <= 0)
                return output;

            EnsureIndex();

            string rawName = (query.Name ?? "").Trim();
            if (rawName.Length == 0 || rows == null || rows.Length == 0)
                return output;

            CarboBag lookupBag;
            string lookupFamily;
            HashSet<string> formHints;
            PrepareLookup(query, out lookupBag, out lookupFamily, out formHints);

            List<CarboScored> scored = ScoreAll(rows, lookupBag, lookupFamily, formHints);
            scored.Sort(new CarboScoredComparer(lookupBag));

            int count = Math.Min(take, scored.Count);
            for (int i = 0; i < count; i++)
            {
                CarboScored s = scored[i];
                CarboMatchCandidate cand = new CarboMatchCandidate();
                cand.Material = s.Row.Material.DeepClone();
                cand.Raw = s.Raw;
                cand.Confidence = CarboMatchNorm.Clamp01(CarboMatchOptions.Tier4Span * s.Raw);
                cand.Coverage = s.Coverage;
                cand.Precision = s.Precision;
                cand.SToken = s.SToken;
                cand.SEdit = s.SEdit;
                cand.CategoryAgreement = s.C;
                cand.GradeAgreement = s.G;
                cand.SBrief = s.SBrief;
                cand.Explanation = Describe(s, lookupBag);
                output.Add(cand);
            }

            return output;
        }

        #endregion

        #region The cascade

        private void PrepareLookup(CarboLookup query, out CarboBag bag, out string family, out HashSet<string> formHints)
        {
            bag = CarboMatchNorm.Build(query.Name, query.Grade);

            string baseFamily = CarboMatchNorm.FamilyFromRevitClass(query.MaterialClass);
            family = CarboMatchNorm.ResolveFamily(baseFamily, bag);

            formHints = new HashSet<string>(StringComparer.Ordinal);
            string ecKey = CarboMatchNorm.CategoryKey(query.ElementCategory);
            if (ecKey.Length > 0)
            {
                string[] hints;
                if (CarboMatchTables.ElementCategoryHints.TryGetValue(ecKey, out hints))
                {
                    foreach (string h in hints)
                        formHints.Add(h);
                }
            }
        }

        private CarboMatchResult RunCascade(CarboLookup query, string rawName)
        {
            //TIER 0, the user's own alias table, if one has been injected.
            CarboMatchResult alias = TryAlias(query, rawName);
            if (alias != null)
                return alias;

            //TIER 1, exact case insensitive name. This satisfies C6 and short circuits before
            //any scoring runs. Deliberately NOT delegated to GetExcactMatch, which used to be
            //ordinal case SENSITIVE and returns the live row.
            int hit;
            if (exactIdx.TryGetValue(rawName, out hit))
            {
                return Build(rows[hit], 1.0, CarboMatchTier.ExactName, "ExactName",
                             "Exact name match on '" + rows[hit].TrimmedName + "'.", null, 0);
            }

            CarboBag lookupBag;
            string lookupFamily;
            HashSet<string> formHints;
            PrepareLookup(query, out lookupBag, out lookupFamily, out formHints);

            //TIER 2, normalised name, catches accents, en dashes, "&" versus "and", casing.
            if (lookupBag.NormName.Length > 0 && normIdx.TryGetValue(lookupBag.NormName, out hit))
            {
                return Build(rows[hit], CarboMatchOptions.Tier2Confidence, CarboMatchTier.NormalisedName, "NormalisedName",
                             "Normalised name match on '" + rows[hit].TrimmedName + "'.", null, 0);
            }

            //TIER 3, grade anchored family hit. Fires only when the lookup carries a grade, and
            //falls through completely free when the candidate set is empty, which is what makes
            //it safe on the four shipped databases whose Grade field is 100% empty.
            if (lookupBag.Grades.Count > 0)
            {
                List<CarboRow> anchored = new List<CarboRow>();
                foreach (CarboRow r in rows)
                {
                    if (SharesExactGrade(lookupBag.Grades, r.Bag.Grades) == false)
                        continue;

                    if (lookupFamily != CarboFamily.UNKNOWN && CarboFamily.Agreement(lookupFamily, r.Family) < 0.6)
                        continue;

                    anchored.Add(r);
                }

                if (anchored.Count > 0)
                {
                    List<CarboScored> anchoredScored = ScoreAll(anchored.ToArray(), lookupBag, lookupFamily, formHints);
                    CarboScored runnerUp;
                    CarboScored winner = PickWinner(anchoredScored, lookupBag, out runnerUp);

                    //With no usable MaterialClass the family gate above could not fire, so the
                    //anchored set is whatever carries the grade token and nothing else. Require
                    //real name evidence before trusting it, otherwise "Paint C8/10" is answered
                    //with in-situ concrete purely because the concrete row carries C8/10.
                    bool gateOk = (lookupFamily != CarboFamily.UNKNOWN)
                               || (winner.SToken >= CarboMatchOptions.Tier3MinTokenWhenFamilyUnknown);

                    if (gateOk)
                    {
                        //Tier 3 anchors, it does not assert. The score is the ordinary tier 4
                        //score plus a bonus for carrying the requested grade - there is no floor,
                        //so a row sharing only the grade token cannot reach the accept band.
                        double aMargin = runnerUp != null ? winner.Raw - runnerUp.Raw : 1.0;
                        double aDamp = CarboMatchOptions.MarginDampFloor
                                     + (1.0 - CarboMatchOptions.MarginDampFloor)
                                       * Math.Min(1.0, aMargin / CarboMatchOptions.MarginFull);

                        double confidence = CarboMatchNorm.Clamp01(
                            CarboMatchOptions.Tier4Span * winner.Raw * aDamp + CarboMatchOptions.Tier3GradeBonus);

                        if (winner.DomainMatched == false)
                            confidence = Math.Min(confidence, CarboMatchOptions.NoDomainEvidenceCap);

                        //Carrying the requested grade does not turn "Steel" into a choice of
                        //steel product; it only narrows the family.
                        if (IsBareFamilyWord(lookupBag))
                            confidence = Math.Min(confidence, CarboMatchOptions.BareFamilyWordCap);

                        //An anchor that leaves several rows tied at the top has identified the
                        //grade, not the material. Never assert one of them; surface the choice.
                        int tied = CountTiedWith(anchoredScored, winner);
                        if (tied > 1)
                            confidence = Math.Min(confidence, CarboMatchOptions.AcceptThreshold - 0.01);

                        string explanation = "Matched on grade " + string.Join(", ", GradeValues(lookupBag.Grades).ToArray())
                                           + " within family " + PrettyFamily(winner.Row.Family) + ".";
                        if (tied > 1)
                            explanation += " " + tied.ToString(CultureInfo.InvariantCulture)
                                         + " rows carry that grade equally well.";

                        double runnerConfidence = 0;
                        if (runnerUp != null)
                            runnerConfidence = CarboMatchNorm.Clamp01(
                                CarboMatchOptions.Tier4Span * runnerUp.Raw * aDamp + CarboMatchOptions.Tier3GradeBonus);

                        LogCandidate(rawName, query, "GradeFamily", confidence, winner, runnerUp, lookupBag);

                        CarboMatchResult anchoredResult = Build(winner.Row, confidence, CarboMatchTier.GradeFamily,
                                                                "GradeAnchored", explanation, runnerUp, runnerConfidence);
                        if (confidence < CarboMatchOptions.ReviewThreshold)
                            anchoredResult.IsAcceptable = false;
                        return anchoredResult;
                    }
                }
            }

            //TIER 4, the scored fallback over every row.
            List<CarboScored> all = ScoreAll(rows, lookupBag, lookupFamily, formHints);
            if (all.Count == 0)
                return Sentinel(rawName, "EmptyDatabase", "No scorable materials in the database.");

            CarboScored runner;
            CarboScored best = PickWinner(all, lookupBag, out runner);

            double margin = 1.0;
            if (runner != null)
                margin = best.Raw - runner.Raw;

            double damp = CarboMatchOptions.MarginDampFloor
                        + (1.0 - CarboMatchOptions.MarginDampFloor) * Math.Min(1.0, margin / CarboMatchOptions.MarginFull);

            double conf = CarboMatchNorm.Clamp01(CarboMatchOptions.Tier4Span * best.Raw * damp);

            //Nothing with domain meaning matched, so the lookup is almost certainly a Revit
            //placeholder ("Wall", "Ceiling", "Element") that merely shares a word with a
            //product name. The coverage RATIO saturates at 1.0 on that single token, which is
            //what used to push these into the accept band against a large database.
            if (best.DomainMatched == false)
                conf = Math.Min(conf, CarboMatchOptions.NoDomainEvidenceCap);

            //"Steel" names a family, not a material - every row of that family is equally
            //entitled to the name, so the winner won on tie-breaks and must be reviewed.
            if (IsBareFamilyWord(lookupBag))
                conf = Math.Min(conf, CarboMatchOptions.BareFamilyWordCap);

            LogCandidate(rawName, query, "Fuzzy", conf, best, runner, lookupBag);

            //TIER 5, below the review threshold there is essentially no evidence.
            if (conf < CarboMatchOptions.ReviewThreshold)
            {
                string weakText = "No confident match for '" + rawName + "', best guess was '"
                                + best.Row.TrimmedName + "' (" + conf.ToString("0.00", CultureInfo.InvariantCulture) + ").";

                if (CarboMatchOptions.Policy == UnmatchedPolicy.ReturnSentinel)
                    return Sentinel(rawName, "ScoreBelowThreshold", weakText);

                //Default policy: best effort. Returning a zero ECI sentinel here would price
                //real structure at zero and under count the project total, which is the same
                //silent corruption one level up the aggregation.
                CarboMatchResult weak = Build(best.Row, conf, CarboMatchTier.LowConfidence, "ScoreBelowThreshold",
                                              weakText, runner, RunnerConfidence(runner, damp));
                weak.IsAcceptable = false;
                return weak;
            }

            string reason = "Ok";
            string text = Describe(best, lookupBag);
            if (conf < CarboMatchOptions.AcceptThreshold)
                text = "REVIEW: " + text;

            return Build(best.Row, conf, CarboMatchTier.Fuzzy, reason, text, runner, RunnerConfidence(runner, damp));
        }

        private static double RunnerConfidence(CarboScored runner, double damp)
        {
            if (runner == null)
                return 0;
            return CarboMatchNorm.Clamp01(CarboMatchOptions.Tier4Span * runner.Raw * damp);
        }

        private CarboMatchResult TryAlias(CarboLookup query, string rawName)
        {
            if (aliasTable == null || aliasTable.mappingTable == null || aliasTable.mappingTable.Count == 0)
                return null;

            string normLookup = CarboMatchNorm.Build(rawName, null).NormName;
            string catKey = CarboMatchNorm.CategoryKey(query.ElementCategory);
            string template = query.TemplateName;
            if (string.IsNullOrEmpty(template) && database != null)
                template = database.templateName;

            List<string> targets = new List<string>();
            List<string> looseTargets = new List<string>();

            foreach (CarboMapElement el in aliasTable.mappingTable)
            {
                if (el == null || string.IsNullOrEmpty(el.carboNAME))
                    continue;

                //A CSV loaded database has no templateName and must still receive aliases.
                if (string.IsNullOrEmpty(el.templateName) == false && string.IsNullOrEmpty(template) == false
                    && string.Equals(el.templateName, template, StringComparison.OrdinalIgnoreCase) == false)
                    continue;

                string elNorm = CarboMatchNorm.Build(el.revitName, null).NormName;
                if (string.Equals(elNorm, normLookup, StringComparison.Ordinal) == false)
                    continue;

                string elCat = CarboMatchNorm.CategoryKey(el.category);
                if (elCat.Length > 0 && catKey.Length > 0 && string.Equals(elCat, catKey, StringComparison.Ordinal))
                {
                    if (targets.Contains(el.carboNAME) == false)
                        targets.Add(el.carboNAME);
                }
                else if (elCat.Length == 0 || catKey.Length == 0)
                {
                    if (looseTargets.Contains(el.carboNAME) == false)
                        looseTargets.Add(el.carboNAME);
                }
            }

            if (targets.Count == 0)
                targets = looseTargets;

            if (targets.Count == 0)
                return null;

            if (targets.Count > 1)
                return null;   //Ambiguous, fall through to the cascade rather than guess.

            int hit;
            if (exactIdx.TryGetValue(targets[0].Trim(), out hit) == false)
            {
                //A saved map written against a different template points at names this database
                //does not have. Returning a zero ECI sentinel here would price real structure at
                //zero - the very D5 failure this redesign exists to remove - and, because Tier 0
                //runs first, it would do so even when the lookup name is an exact match for a
                //real row. Fall through to the rest of the cascade and let it answer; only an
                //explicit ReturnSentinel policy still hard stops.
                if (CarboMatchOptions.Policy == UnmatchedPolicy.ReturnSentinel)
                    return Sentinel(rawName, "AliasBroken",
                        "The saved mapping points at '" + targets[0] + "', which is not in this database.");

                return null;
            }

            return Build(rows[hit], 1.0, CarboMatchTier.Alias, "AliasHit",
                         "Applied the saved mapping to '" + rows[hit].TrimmedName + "'.", null, 0);
        }

        private static bool SharesExactGrade(List<string> a, List<string> b)
        {
            foreach (string x in a)
            {
                foreach (string y in b)
                {
                    if (string.Equals(x, y, StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        private static List<string> GradeValues(List<string> gradeKeys)
        {
            List<string> list = new List<string>();
            foreach (string k in gradeKeys)
            {
                int at = k.IndexOf(':');
                list.Add(at >= 0 ? k.Substring(at + 1) : k);
            }
            return list;
        }

        #endregion

        #region Scoring

        private List<CarboScored> ScoreAll(CarboRow[] candidates, CarboBag lookup, string lookupFamily, HashSet<string> formHints)
        {
            List<CarboScored> list = new List<CarboScored>(candidates.Length);
            foreach (CarboRow r in candidates)
                list.Add(Score(r, lookup, lookupFamily, formHints));
            return list;
        }

        private static CarboScored Score(CarboRow row, CarboBag lookup, string lookupFamily, HashSet<string> formHints)
        {
            CarboScored s = new CarboScored();
            s.Row = row;

            Dictionary<string, CarboTok> L = lookup.Tokens;
            Dictionary<string, CarboTok> D = row.Bag.Tokens;
            string RF = row.Family;

            //--- 5.1 S_token -----------------------------------------------------------------
            //Coverage rule (ii) is FAMILY CREDIT: a lookup family word is satisfied by a row
            //whose family is that family or MORE SPECIFIC. This is the root cause fix, the
            //family word has migrated out of many database Names and into the Category, which
            //is the one field the old code never read.
            double covMatched = 0;
            bool domainMatched = false;
            foreach (KeyValuePair<string, CarboTok> kv in L)
            {
                CarboTok t = kv.Value;
                bool literal = D.ContainsKey(t.Key);
                bool matched = literal;
                double credit = t.Weight;

                if (matched == false && t.Family != CarboFamily.UNKNOWN
                    && CarboFamily.IsSameOrDescendant(RF, t.Family))
                {
                    matched = true;

                    //A family WORD is genuinely satisfied by the Category alone. A specific
                    //product word is not - otherwise every timber row answers "Plywood"
                    //equally well and the tie falls to whichever name is shortest.
                    if (t.IsFamilyWord == false)
                        credit = t.Weight * CarboMatchOptions.FamilyCreditFactor;
                }

                if (matched)
                {
                    covMatched += credit;

                    //Did anything with actual domain meaning match, or is this purely a
                    //coincidental word overlap such as "Ceiling" against "suspended ceiling"?
                    if (t.Family != CarboFamily.UNKNOWN || t.Weight >= 1.5)
                        domainMatched = true;
                }
            }
            s.Coverage = lookup.TotalWeight > 0 ? covMatched / lookup.TotalWeight : 0;
            s.DomainMatched = domainMatched;

            //Precision rule (ii) is deliberately the mirror image: a row token earns credit
            //only when its family is the lookup's family or MORE GENERAL. A row token that
            //NARROWS earns nothing, narrowing is priced by the specialiser penalty instead.
            double preMatched = 0;
            foreach (KeyValuePair<string, CarboTok> kv in D)
            {
                CarboTok u = kv.Value;
                bool matched = L.ContainsKey(u.Key);
                if (matched == false && u.Family != CarboFamily.UNKNOWN)
                    matched = CarboFamily.IsSameOrDescendant(lookupFamily, u.Family);

                if (matched)
                    preMatched += u.Weight;
            }
            s.Precision = row.Bag.TotalWeight > 0 ? preMatched / row.Bag.TotalWeight : 0;

            //A single token lookup ("Concrete", "Steel", "M_Concrete C40/50") saturates coverage
            //on every row of that family, so leaning on coverage there just hands the decision
            //to the tie-break. Precision is the only component still carrying information.
            double covW = CarboMatchOptions.TokCoverageWeight;
            double preW = CarboMatchOptions.TokPrecisionWeight;
            if (L.Count <= 1)
            {
                covW = CarboMatchOptions.SingleTokenCoverageWeight;
                preW = CarboMatchOptions.SingleTokenPrecisionWeight;
            }

            s.SToken = covW * s.Coverage + preW * s.Precision;

            //--- 5.2 S_edit ------------------------------------------------------------------
            //Normalised, so it can no longer swing by 160 points. Both operands are folded and
            //grade blanked, which also removes the case sensitivity of CalcLevenshteinDistance.
            string a = lookup.NormName;
            string b = row.Bag.NormName;
            int la = a.Length;
            int lb = b.Length;
            if (la == 0 || lb == 0)
            {
                s.SEdit = 0;
            }
            else
            {
                int maxLen = Math.Max(la, lb);
                if (Math.Abs(la - lb) > CarboMatchOptions.EditLengthGate * maxLen)
                {
                    s.SEdit = 0;   //No meaningful edit similarity, and skips the O(n*m) call.
                }
                else
                {
                    int dist = Utils.CalcLevenshteinDistance(a, b);
                    s.SEdit = CarboMatchNorm.Clamp01(1.0 - (double)dist / maxLen);
                }
            }

            //--- 5.3 and 5.4 -----------------------------------------------------------------
            s.C = CarboFamily.Agreement(lookupFamily, RF);
            s.G = CarboMatchNorm.GradeAgreement(lookup.Grades, row.Bag.Grades);

            //--- 5.5 S_brief -----------------------------------------------------------------
            //Prefers the tighter, more representative row, this is what separates "Rebar UK"
            //from "Rebar UK BOF Production Upper Bound".
            double tokPart = 1.0 - (double)Math.Min(row.Bag.ContentTokenCount, CarboMatchOptions.BriefTokenCap) / CarboMatchOptions.BriefTokenCap;
            double lenPart = 1.0 - (double)Math.Min(b.Length, CarboMatchOptions.BriefLenCap) / CarboMatchOptions.BriefLenCap;
            s.SBrief = 0.5 * tokPart + 0.5 * lenPart;

            //--- Section 6, combination ------------------------------------------------------
            bool catActive = s.C > 0;
            bool gradeActive = s.G > 0;

            double num = CarboMatchOptions.WToken * s.SToken
                       + CarboMatchOptions.WEdit * s.SEdit
                       + CarboMatchOptions.WBrief * s.SBrief;
            double den = CarboMatchOptions.WToken + CarboMatchOptions.WEdit + CarboMatchOptions.WBrief;

            if (catActive)
            {
                num += CarboMatchOptions.WCat * s.C;
                den += CarboMatchOptions.WCat;
            }
            if (gradeActive)
            {
                num += CarboMatchOptions.WGrade * s.G;
                den += CarboMatchOptions.WGrade;
            }

            //Renormalising by the ACTIVE signal mass is essential, not cosmetic. Without it a
            //database with no grades would be permanently capped 0.20 below one with grades,
            //and a "Generic" MaterialClass would cost 0.25 of headroom. den is constant across
            //all candidates of one lookup, so the ranking is unaffected, only the scale is.
            double baseScore = den > 0 ? num / den : 0;

            //--- Bonuses ----------------------------------------------------------------------
            if (row.Bag.HasPreferredRegion && lookup.HasPreferredRegion == false && lookup.HasCompetingRegion == false)
                baseScore += CarboMatchOptions.BRegion;

            if (formHints != null && formHints.Count > 0)
            {
                foreach (string hint in formHints)
                {
                    if (D.ContainsKey(hint))
                    {
                        baseScore += CarboMatchOptions.BForm;
                        break;
                    }
                }
            }

            //--- Penalties --------------------------------------------------------------------
            if (s.C < 0)
                baseScore -= CarboMatchOptions.PCatConflict * Math.Abs(s.C);

            if (s.G < 0)
                baseScore -= CarboMatchOptions.PGradeConflict * Math.Abs(s.G);

            if (row.Bag.VariantRank > 0 && lookup.VariantRank == 0)
                baseScore -= CarboMatchOptions.PVariant;

            int unrequested = 0;
            foreach (string spec in row.Bag.Specialisers)
            {
                if (IsRequested(spec, L, lookup.Specialisers) == false)
                    unrequested++;
            }
            s.UnrequestedSpecialisers = unrequested;
            if (unrequested > 0)
                baseScore -= Math.Min(CarboMatchOptions.PSpecialiserCap, CarboMatchOptions.PSpecialiser * unrequested);

            //Asserting the opposite of what was asked is stronger than merely narrowing.
            foreach (string[] pair in CarboMatchTables.ExclusivePairs)
            {
                if ((L.ContainsKey(pair[0]) && D.ContainsKey(pair[1])) ||
                    (L.ContainsKey(pair[1]) && D.ContainsKey(pair[0])))
                {
                    baseScore -= CarboMatchOptions.PConceptConflict;
                    break;
                }
            }

            s.Raw = CarboMatchNorm.Clamp01(baseScore);
            s.ScoreMilli = (int)Math.Round(s.Raw * 1000.0);
            return s;
        }

        private static bool IsRequested(string specialiser, Dictionary<string, CarboTok> lookupTokens, HashSet<string> lookupSpecialisers)
        {
            if (lookupTokens.ContainsKey(specialiser))
                return true;

            if (lookupSpecialisers != null && lookupSpecialisers.Contains(specialiser))
                return true;

            //Table F2, asserting the key excuses the implied values at zero cost.
            foreach (KeyValuePair<string, CarboTok> kv in lookupTokens)
            {
                string[] implied;
                if (CarboMatchTables.ImpliedSpecialisers.TryGetValue(kv.Key, out implied) == false)
                    continue;

                foreach (string im in implied)
                {
                    if (string.Equals(im, specialiser, StringComparison.Ordinal))
                        return true;
                }
            }

            return false;
        }

        #endregion

        #region Tie break and winner selection

        /// <summary>
        /// Section 8. Collect a BAND of near ties, then apply a strict total order. This is what
        /// makes the outcome provably independent of the database row order, several shipped
        /// rows score exactly the same and are decided today by whichever the file lists first.
        /// </summary>
        /// <summary>
        /// True when the lookup's only content token is the family's own word, so it names a
        /// family rather than a material. A specific product word ("plywood", "rebar") is not
        /// a bare family word even though it also resolves to a family.
        /// </summary>
        private static bool IsBareFamilyWord(CarboBag lookup)
        {
            if (lookup == null || lookup.Tokens.Count != 1)
                return false;

            foreach (KeyValuePair<string, CarboTok> kv in lookup.Tokens)
                return kv.Value.IsFamilyWord;

            return false;
        }

        /// <summary>
        /// How many candidates sit inside the tie epsilon of the winner, the winner included.
        /// A count above one means the evidence identified a band, not a single material.
        /// </summary>
        private static int CountTiedWith(List<CarboScored> scored, CarboScored winner)
        {
            if (scored == null || winner == null)
                return 0;

            int n = 0;
            foreach (CarboScored s in scored)
            {
                if (Math.Abs(s.ScoreMilli - winner.ScoreMilli) <= CarboMatchOptions.TieEpsilonMilli)
                    n++;
            }
            return n;
        }

        private static CarboScored PickWinner(List<CarboScored> scored, CarboBag lookup, out CarboScored runnerUp)
        {
            runnerUp = null;
            if (scored.Count == 0)
                return null;

            int maxMilli = int.MinValue;
            foreach (CarboScored s in scored)
            {
                if (s.ScoreMilli > maxMilli)
                    maxMilli = s.ScoreMilli;
            }

            List<CarboScored> band = new List<CarboScored>();
            foreach (CarboScored s in scored)
            {
                if (s.ScoreMilli >= maxMilli - CarboMatchOptions.TieEpsilonMilli)
                    band.Add(s);
            }

            band.Sort(new CarboScoredComparer(lookup));
            CarboScored winner = band[0];

            //The runner up is the best candidate that is NOT a provenance variant of the winner,
            //otherwise "Softwood plywood UK/import weighted average" and "... Global" would
            //depress a perfectly identifiable plywood into REVIEW.
            double bestRunner = double.MinValue;
            foreach (CarboScored s in scored)
            {
                if (ReferenceEquals(s, winner))
                    continue;
                if (IsProvenanceEquivalent(winner.Row, s.Row))
                    continue;

                if (s.Raw > bestRunner)
                {
                    bestRunner = s.Raw;
                    runnerUp = s;
                }
            }

            return winner;
        }

        private static bool IsProvenanceEquivalent(CarboRow a, CarboRow b)
        {
            if (a.Family != b.Family)
                return false;

            if (a.Bag.Tokens.Count != b.Bag.Tokens.Count)
                return false;

            foreach (KeyValuePair<string, CarboTok> kv in a.Bag.Tokens)
            {
                if (b.Bag.Tokens.ContainsKey(kv.Key) == false)
                    return false;
            }

            if (a.Bag.Grades.Count != b.Bag.Grades.Count)
                return false;

            foreach (string g in a.Bag.Grades)
            {
                if (b.Bag.Grades.Contains(g) == false)
                    return false;
            }

            return true;
        }

        private sealed class CarboScoredComparer : IComparer<CarboScored>
        {
            private readonly CarboBag lookup;

            public CarboScoredComparer(CarboBag lookupBag)
            {
                lookup = lookupBag;
            }

            public int Compare(CarboScored x, CarboScored y)
            {
                int c = y.ScoreMilli.CompareTo(x.ScoreMilli);
                if (c != 0) return c;

                c = y.C.CompareTo(x.C);
                if (c != 0) return c;

                c = y.G.CompareTo(x.G);
                if (c != 0) return c;

                c = y.Coverage.CompareTo(x.Coverage);
                if (c != 0) return c;

                c = x.Row.Bag.ContentTokenCount.CompareTo(y.Row.Bag.ContentTokenCount);
                if (c != 0) return c;

                c = x.Row.Bag.NormName.Length.CompareTo(y.Row.Bag.NormName.Length);
                if (c != 0) return c;

                //When forced to pick a statistical bound, take the conservative one.
                c = x.Row.Bag.VariantRank.CompareTo(y.Row.Bag.VariantRank);
                if (c != 0) return c;

                c = x.UnrequestedSpecialisers.CompareTo(y.UnrequestedSpecialisers);
                if (c != 0) return c;

                c = (y.Row.Bag.HasPreferredRegion ? 1 : 0).CompareTo(x.Row.Bag.HasPreferredRegion ? 1 : 0);
                if (c != 0) return c;

                c = x.Row.Material.Id.CompareTo(y.Row.Material.Id);
                if (c != 0) return c;

                return string.CompareOrdinal(x.Row.Bag.NormName, y.Row.Bag.NormName);
            }
        }

        #endregion

        #region Result construction and diagnostics

        private CarboMatchResult Build(CarboRow row, double confidence, CarboMatchTier tier, string reason,
                                       string explanation, CarboScored runnerUp, double runnerUpConfidence)
        {
            CarboMatchResult r = new CarboMatchResult();
            r.Material = row.Material.DeepClone();
            r.Confidence = CarboMatchNorm.Clamp01(confidence);
            r.Tier = tier;
            r.Reason = reason;
            r.Explanation = explanation;
            r.IsAcceptable = r.Confidence >= CarboMatchOptions.ReviewThreshold;
            r.RunnerUpName = runnerUp != null ? runnerUp.Row.TrimmedName : "";
            r.RunnerUpConfidence = CarboMatchNorm.Clamp01(runnerUpConfidence);
            return r;
        }

        private static CarboMatchResult Sentinel(string lookupName, string reason, string explanation)
        {
            CarboMatchResult r = new CarboMatchResult();

            //The default constructor already sets Id = -1, Density = 500 and all ECI to 0, so
            //Id == -1 is a clean, non breaking, machine readable no-match sentinel. The name is
            //angle bracket free so it survives a .clcx round trip.
            CarboMaterial sentinel = new CarboMaterial();
            sentinel.Name = "(no match: " + (lookupName ?? "") + ")";
            sentinel.Category = "Unmatched";
            sentinel.Grade = "";

            r.Material = sentinel;
            r.Confidence = 0;
            r.Tier = CarboMatchTier.Unmatched;
            r.IsAcceptable = false;
            r.Reason = reason;
            r.Explanation = explanation;
            return r;
        }

        private static CarboMatchResult CopyOf(CarboMatchResult source)
        {
            CarboMatchResult r = new CarboMatchResult();
            r.Material = source.Material.DeepClone();
            r.Confidence = source.Confidence;
            r.Tier = source.Tier;
            r.IsAcceptable = source.IsAcceptable;
            r.Reason = source.Reason;
            r.Explanation = source.Explanation;
            r.RunnerUpName = source.RunnerUpName;
            r.RunnerUpConfidence = source.RunnerUpConfidence;
            return r;
        }

        private static string PrettyFamily(string family)
        {
            if (string.IsNullOrEmpty(family) || family == CarboFamily.UNKNOWN)
                return "unknown";

            string lower = family.ToLowerInvariant().Replace('_', ' ');
            return lower;
        }

        private static string Describe(CarboScored s, CarboBag lookup)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Matched '").Append(s.Row.TrimmedName).Append("'");
            sb.Append(" [name ").Append(s.SToken.ToString("0.00", CultureInfo.InvariantCulture));
            sb.Append(", family ").Append(PrettyFamily(s.Row.Family));
            if (s.G > 0)
                sb.Append(", grade agrees");
            else if (s.G < 0)
                sb.Append(", grade DISAGREES");
            sb.Append("]");
            return sb.ToString();
        }

        private static void LogCandidate(string rawName, CarboLookup query, string tier, double confidence,
                                         CarboScored best, CarboScored runner, CarboBag lookup)
        {
            if (CarboMatchDiagnostics.Enabled == false || best == null)
                return;

            StringBuilder sb = new StringBuilder();
            sb.Append(Csv(rawName)).Append(',');
            sb.Append(Csv(query.MaterialClass)).Append(',');
            sb.Append(Csv(query.Grade)).Append(',');
            sb.Append(tier).Append(',');
            sb.Append(confidence.ToString("0.000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Csv(best.Row.TrimmedName)).Append(',');
            sb.Append(best.Coverage.ToString("0.000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(best.Precision.ToString("0.000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(best.SToken.ToString("0.000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(best.SEdit.ToString("0.000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(best.C.ToString("0.00", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(best.G.ToString("0.00", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(best.SBrief.ToString("0.000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(best.Raw.ToString("0.000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(runner != null ? Csv(runner.Row.TrimmedName) : "").Append(',');
            sb.Append(runner != null ? runner.Raw.ToString("0.000", CultureInfo.InvariantCulture) : "");

            CarboMatchDiagnostics.Record(sb.ToString());
        }

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace(',', ' ');
        }

        #endregion
    }
}
