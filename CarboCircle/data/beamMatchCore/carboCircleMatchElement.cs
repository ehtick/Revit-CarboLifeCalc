using Autodesk.Revit.DB;
using CarboLifeAPI;
using CarboCircle.data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CarboCircle
{
    [Serializable]
    public class carboCircleMatchElement
    {

        public Int64 required_id { get; set; }
        public string required_humanId { get; set; }

        public Int64 mined_id { get; set; }
        public string mined_humanId { get; set; }
        public string required_Name { get; set; }
        public string mined_Name { get; set; }

        //Matched To Material Name
        public double required_length { get; set; }
        public double required_volume { get; set; }
        public double mined_netLength { get; set; }
        public double mined_netVolume { get; set; }

        public bool isVolumeElement { get; set; }
        public bool isOffcut { get; set; }

        //The Below are taken from standardized Database
        public string required_standardName { get; set; }
        public string mined_standardName { get; set; }
        public double match_Score { get; set; }

        /// <summary>
        /// Which class of match this is: see carboCircleMatchRules. Stored, because it is the
        /// sort key that keeps the groups in priority order - 100% matches first, no-matches
        /// last - and because the row colour keys off it.
        /// </summary>
        public int matchRank { get; set; }

        /// <summary>Length actually cut out of stock for this requirement, in metres.</summary>
        public double used_netLength { get; set; }

        /// <summary>
        /// Length of the remnant this cut left, in metres. Carried from the engine rather than
        /// recomputed: usable minus required omits the cutting allowance, so anyone deriving it
        /// downstream got a different number from the one the engine put back into stock.
        /// </summary>
        public double offcut_netLength { get; set; }

        /// <summary>
        /// What the user reads, and what the grid groups on.
        ///
        /// Computed rather than stored on purpose: a get-only property has no backing field, so
        /// it changes neither the [Serializable] shape nor the positional csv, and there is no
        /// way for it to disagree with matchRank.
        /// </summary>
        public string matchCategory
        {
            get { return carboCircleMatchRules.classLabel(matchRank); }
        }

        public string description { get; set; }


        public carboCircleMatchElement(
        int requiredId = 0,
        string requiredHumanId = null,
        int minedId = 0,
        string minedHumanId = null,
        string requiredName = null,
        string minedName = null,
        double requiredLength = 0.0,
        double requiredVolume = 0.0,
        double minedNetLength = 0.0,
        double minedNetVolume = 0.0,
        double match_Score = 0.0,
        bool isOffcut = false,
        bool isVolumeElement = false,
        string requiredStandardName = null,
        string minedStandardName = null,         
        string description = null)
        {
            required_id = requiredId;
            required_humanId = requiredHumanId;
            mined_id = minedId;
            mined_humanId = minedHumanId;
            required_Name = requiredName;
            mined_Name = minedName;
            required_length = requiredLength;
            required_volume = requiredVolume;
            mined_netLength = minedNetLength;
            mined_netVolume = minedNetVolume;
            this.isOffcut = isOffcut;
            this.isVolumeElement = isVolumeElement;
            required_standardName = requiredStandardName;
            mined_standardName = minedStandardName;
            //Was accepted as a parameter and then dropped on the floor.
            this.match_Score = match_Score;
            this.description = description;
        }

        public carboCircleMatchElement Copy()
        {
            carboCircleMatchElement clone = new carboCircleMatchElement
            {
                required_id = this.required_id,
                required_humanId = this.required_humanId,
                mined_id = this.mined_id,
                mined_humanId = this.mined_humanId,
                required_Name = this.required_Name,
                mined_Name = this.mined_Name,
                required_length = this.required_length,
                required_volume = this.required_volume,
                mined_netLength = this.mined_netLength,
                mined_netVolume = this.mined_netVolume,
                isVolumeElement = this.isVolumeElement,
                isOffcut = this.isOffcut,
                required_standardName = this.required_standardName,
                mined_standardName = this.mined_standardName,
                match_Score = this.match_Score,
                matchRank = this.matchRank,
                used_netLength = this.used_netLength,
                offcut_netLength = this.offcut_netLength,
                description = this.description
            };

            return clone;
        }
    }
}

