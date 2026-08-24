using Autodesk.Revit.DB;
using CarboLifeAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CarboCircle
{
    [Serializable]
    public class carboCircleElement
    {

        public Int64 id { get; set; }
        public string GUID { get; set; }
        public string humanId { get; set; }

        public string category { get; set; }
        public string name { get; set; }
        //Imported Material Name
        public string materialName { get; set; }
        public string materialClass { get; set; }

        //Matched To Material Name
        public double length { get; set; }
        public double volume { get; set; }
        public double netLength { get; set; }
        public double netVolume { get; set; }

        public string grade { get; set; }
        public int quality { get; set; }

        public bool isVolumeElement { get; set; }

        //The Below are taken from standardized Database
        public string standardName { get; set; }
        public double standardDepth { get; set; }
        public double standardWidth{ get; set; }
        public string standardCategory { get; set; }
        public double Iy { get; set; }
        public double Wy { get; set; }
        public double Iz { get; set; }
        public double Wz { get; set; }
        public string matchGUID { get; set; }
        public bool isOffcut { get; set; }

        /// <summary>
        /// Mass per metre from the section catalogue, kg/m. Zero when unknown - the catalogue
        /// leaves it blank on 18 rows, and it is never known for timber.
        ///
        /// Read so the importer can sanity-check its own name mapping: the modelled cross
        /// section, volume/length, times the density of steel should agree with this. When it
        /// disagrees by a wide margin the name was mapped to the wrong section.
        /// </summary>
        public double massPerMetre { get; set; }

        /// <summary>
        /// How much the section identity can be trusted.
        ///
        /// 0 Exact    - the Revit type name matched a catalogue designation outright.
        /// 1 Assumed  - it was mapped to the nearest name, and the mapping may be wrong.
        /// 2 Unmapped - there is no usable section: no name, no catalogue, or the mapping
        ///              failed its own mass check.
        ///
        /// Only an Exact section on BOTH sides may be called a 100% match. An Assumed section
        /// may still form an adequate substitution, where the numbers are what carry the
        /// argument rather than the name. An Unmapped section may not pair at all.
        ///
        /// Default 0 so a settings file or csv written before this existed behaves exactly as
        /// it does today: the safety comes from the importer setting it, not from the default.
        /// </summary>
        public int sectionConfidence { get; set; }

        /// <summary>
        /// The GUID of the original Revit member this piece was cut from, or "" if this IS an
        /// original.
        ///
        /// Inherited through every generation of cutting, so every piece that came out of one
        /// member shares one key. That is what lets the tool say "this 9.00 m beam served
        /// three requirements", and it is the key the matcher writes back through.
        /// </summary>
        public string sourceGUID { get; set; }

        /// <summary>
        /// What this element is, for grouping the leftovers grid. Computed, so nothing is added
        /// to the serialised shape or to the positional csv.
        /// </summary>
        public string sourceLabel
        {
            get
            {
                //An offcut only counts as reusable if it went back into stock. The engine tags
                //the ones it rejected as too short by writing the reason into grade, and
                //labelling those "reusable" put scrap and stock in the same group.
                if (!isOffcut)
                    return "Unused - not matched";

                return string.IsNullOrEmpty(grade) || grade.IndexOf("too short", StringComparison.OrdinalIgnoreCase) < 0
                    ? "Offcut - reusable"
                    : "Offcut - too short to reuse";
            }
        }

        public List<Int64> idList { get; set; }
        public carboCircleElement()
        {
            id = -999;
            humanId = "";
            name = "";
            materialName = "Other";
            grade = "";
            length = 0;
            quality = 1;
            category = "";
            materialClass = "";
            isVolumeElement = false;

            volume = 0;
            netVolume = 0;
            netLength = 0;
            standardName = "";
            standardCategory = "";
            standardDepth = 0;
            standardWidth = 0;
            Iy = 0;
            Wy = 0;
            Iz = 0;
            Wz = 0;

            GUID = "";
            matchGUID = "";
            isOffcut = false;
            massPerMetre = 0;
            sectionConfidence = 0;
            sourceGUID = "";

            idList = new List<Int64>();
         }


    public carboCircleElement Copy()
        {
            carboCircleElement clone = new carboCircleElement
            {
                id = this.id,
                humanId = this.humanId,
                name = this.name,
                materialName = this.materialName,
                grade = this.grade,

                length = this.length,
                netLength = this.netLength,
                netVolume = this.netVolume,
                quality = this.quality,
                category = this.category,

                materialClass = this.materialClass,
                isVolumeElement = this.isVolumeElement,
                volume = this.volume,
                standardName = this.standardName,
                standardCategory = this.standardCategory,
                standardDepth = this.standardDepth,
                standardWidth = this.standardWidth,
                Iy = this.Iy,
                Wy = this.Wy,
                Iz = this.Iz,
                Wz = this.Wz,

                GUID = this.GUID,
                matchGUID = this.matchGUID,
                isOffcut = this.isOffcut,

                //Every field above and below is copied by hand in this list. A field added to
                //the class and forgotten here is silently lost on every Copy(), and the
                //matcher copies constantly - the pool, the pairs and the leftovers are all
                //copies. carboCircleMatchRulesTests covers this.
                massPerMetre = this.massPerMetre,
                sectionConfidence = this.sectionConfidence,
                sourceGUID = this.sourceGUID
            };

            clone.idList = new List<Int64>();
            foreach (Int64 id in this.idList)
            {
                clone.idList.Add(id);
            }

            return clone;
        }
    }
}

