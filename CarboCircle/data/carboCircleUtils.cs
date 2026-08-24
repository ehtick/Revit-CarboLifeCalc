using CarboLifeAPI;
using CarboLifeAPI.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;

namespace CarboCircle.data
{
    internal class carboCircleUtils
    {
        /// <summary>
        /// A number for a csv cell, always in invariant culture.
        ///
        /// The whole file is comma separated, so a comma decimal separator does not merely look
        /// odd - it adds a field and shifts every column after it, and the reader is positional.
        /// </summary>
        private static string num(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "0";

            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        internal static void ExportDataToCSV(List<carboCircleElement> dataCombined, string path)
        {
            if (File.Exists(path) && DataExportUtils.IsFileLocked(path) == true)
                return;

            string fileString = "";

            //Create Headers;
            //
            //Positional, and read back by index, so a column may only ever be APPENDED - see
            //GetElementsFromCVSFile. The three at the end were added when the matcher started
            //depending on them; a file written before that simply stops short and the reader
            //leaves them at their defaults.
            fileString =
                "id, GUID, humanId, category, name, materialName, materialClass, length, " +
                "volume, netLength, netVolume, grade, quality, isVolumeElement, " +
                "standardName, standardDepth, standardWidth, standardCategory, Iy, Wy, " +
                "Iz, Wz, matchGUID, isOffcut, sectionConfidence, sourceGUID, massPerMetre" +
                Environment.NewLine;
            //Advanced
            foreach (carboCircleElement ccE in dataCombined)
            {
                try
                {
                    string resultString = "";

                    resultString += DataExportUtils.CVSFormat(ccE.id.ToString()) + ","; //1
                    resultString += DataExportUtils.CVSFormat(ccE.GUID) + ","; //2
                    resultString += DataExportUtils.CVSFormat(ccE.humanId) + ","; //3
                    resultString += DataExportUtils.CVSFormat(ccE.category) + ","; //3
                    resultString += DataExportUtils.CVSFormat(ccE.name) + ","; //3
                    resultString += DataExportUtils.CVSFormat(ccE.materialName) + ","; //3
                    resultString += DataExportUtils.CVSFormat(ccE.materialClass) + ","; //3
                    //Invariant culture, explicitly. These four were concatenated straight into
                    //the string, which formats in the current culture - so on a machine with a
                    //comma decimal separator every one of them emitted an extra comma into a
                    //comma-separated file and shifted every later column by one.
                    resultString += num(ccE.length) + ",";
                    resultString += num(ccE.volume) + ",";
                    resultString += num(ccE.netLength) + ",";
                    resultString += num(ccE.netVolume) + ",";

                    resultString += DataExportUtils.CVSFormat(ccE.grade) + ","; //3
                    resultString += DataExportUtils.CVSFormat(ccE.quality.ToString()) + ","; //3
                    resultString += DataExportUtils.CVSFormat(ccE.isVolumeElement.ToString()) + ","; //3

                    resultString += DataExportUtils.CVSFormat(ccE.standardName) + ",";
                    resultString += num(ccE.standardDepth) + ",";
                    resultString += num(ccE.standardWidth) + ",";
                    resultString += DataExportUtils.CVSFormat(ccE.standardCategory) + ",";
                    resultString += num(ccE.Iy) + ",";
                    resultString += num(ccE.Wy) + ",";
                    resultString += num(ccE.Iz) + ",";
                    //Wz, at last. This column has always been written with Wy in it, under a
                    //header saying Wz, and read back into Wy again - so Wz came home as zero on
                    //every round trip. That mattered little while nothing used it; the matcher
                    //now gates on minor-axis capacity, and a zero there quietly skips the gate.
                    resultString += num(ccE.Wz) + ",";
                    resultString += DataExportUtils.CVSFormat(ccE.matchGUID) + ",";
                    resultString += DataExportUtils.CVSFormat(ccE.isOffcut.ToString()) + ",";

                    //Appended for the matcher. sectionConfidence in particular: without it every
                    //imported element claims an exact section identity, and two rows sharing a
                    //name become a "100% match" on a name nobody confirmed.
                    resultString += ccE.sectionConfidence.ToString(CultureInfo.InvariantCulture) + ",";
                    resultString += DataExportUtils.CVSFormat(ccE.sourceGUID) + ",";
                    resultString += num(ccE.massPerMetre) + ",";

                    resultString += Environment.NewLine;

                    fileString += resultString;
                }
                catch (IOException ex)
                {
                   // Console.WriteLine("An error occurred while writing the file: " + ex.Message);
                }
            }

            DataExportUtils.WriteCVSFile(fileString, path);


        }
        internal static List<carboCircleMatchElement> getCarboMatchListSimplified(List<carboCirclePair> carboCircleMatchedPairs)
        {
            List<carboCircleMatchElement> result = new List<carboCircleMatchElement>();

            if(carboCircleMatchedPairs != null )
            {
                if(carboCircleMatchedPairs.Count > 0) 
                { 
                    foreach(carboCirclePair pair in carboCircleMatchedPairs)
                    {
                        if (pair == null || pair.required_element == null || pair.mined_Element == null)
                            continue;

                        carboCircleMatchElement ccme
                            = new carboCircleMatchElement();

                        //convert the pairs to simplified data
                        //required Element
                        ccme.required_id = pair.required_element.id;
                        ccme.required_humanId = pair.required_element.humanId;
                        ccme.required_Name = pair.required_element.name;
                        ccme.required_standardName = pair.required_element.standardName;
                        ccme.required_length = pair.required_element.length;
                        ccme.required_volume = pair.required_element.volume;

                        //mined elemnent
                        ccme.mined_id = pair.mined_Element.id;
                        ccme.mined_humanId = pair.mined_Element.humanId;
                        ccme.mined_Name = pair.mined_Element.name;
                        ccme.mined_standardName = pair.mined_Element.standardName;
                        ccme.mined_netLength = pair.mined_Element.netLength;

                        //What this requirement actually consumes, not the whole piece offered.
                        ccme.mined_netVolume = pair.used_netVolume;

                        //The six fields below were declared on this class from the start and
                        //never filled in. isOffcut is the reason the "from an offcut" category
                        //could not be shown at all.
                        ccme.isOffcut = pair.mined_Element.isOffcut;
                        ccme.isVolumeElement = pair.mined_Element.isVolumeElement;
                        ccme.matchRank = pair.matchClass;
                        ccme.used_netLength = pair.used_netLength;
                        ccme.offcut_netLength = pair.offcut_netLength;

                        ccme.match_Score = pair.match_Score;

                        ccme.description = pair.description;

                        result.Add(ccme);
                    }
                }
            }

            return result;


        }

        internal static List<carboCircleElement> GetElementsFromCVSFile(string importPath)
        {
            List<carboCircleElement> cmList = new List<carboCircleElement>();

            try
            {
                if (File.Exists(importPath) && DataExportUtils.IsFileLocked(importPath) == false)
                {
                    DataTable profileTable = Utils.LoadCSV(importPath);

                    foreach (DataRow dr in profileTable.Rows)
                    {
                        try
                        {
                            carboCircleElement cce = new carboCircleElement();
                            cce.id = Convert.ToInt32(Utils.ConvertMeToDouble(dr[0].ToString()));
                            cce.GUID = dr[1].ToString();
                            cce.humanId = dr[2].ToString();
                            cce.category = dr[3].ToString();
                            cce.name = dr[4].ToString();
                            cce.materialName = dr[5].ToString();
                            cce.materialClass = dr[6].ToString();

                            cce.length = Utils.ConvertMeToDouble(dr[7].ToString());
                            cce.volume = Utils.ConvertMeToDouble(dr[8].ToString());
                            cce.netLength = Utils.ConvertMeToDouble(dr[9].ToString());
                            cce.netVolume = Utils.ConvertMeToDouble(dr[10].ToString());

                            cce.grade = dr[11].ToString();
                            cce.quality = Convert.ToInt32(Utils.ConvertMeToDouble(dr[12].ToString()));
                            
                            bool parseOk = false;
                            bool isVolume = true;
                            bool isOffcut = true;

                            parseOk = Boolean.TryParse(dr[13].ToString(), out isVolume);
                            if(parseOk)
                                cce.isVolumeElement = isVolume;

                            cce.standardName = dr[14].ToString();
                            cce.standardDepth = Utils.ConvertMeToDouble(dr[15].ToString());
                            cce.standardWidth = Utils.ConvertMeToDouble(dr[16].ToString());
                            cce.standardCategory = dr[17].ToString();
                            cce.Iy = Utils.ConvertMeToDouble(dr[18].ToString());
                            cce.Wy = Utils.ConvertMeToDouble(dr[19].ToString());
                            cce.Iz = Utils.ConvertMeToDouble(dr[20].ToString());
                            //Was read into Wy a second time, so Wz was always zero.
                            cce.Wz = Utils.ConvertMeToDouble(dr[21].ToString());
                            cce.matchGUID = dr[22].ToString();

                            parseOk = Boolean.TryParse(dr[23].ToString(), out isOffcut);
                            if (parseOk)
                                cce.isOffcut = isOffcut;

                            //Appended columns. A file written before they existed is shorter, so
                            //each is guarded and simply keeps its default.
                            //
                            //sectionConfidence defaults to 0 = Exact on the class, which is right
                            //for an element the importer has just resolved and wrong for one
                            //arriving from a file. An older csv therefore lands on Assumed: the
                            //numbers may still earn a substitution, but the name alone will not
                            //be presented as a 100% match.
                            if (dr.Table.Columns.Count > 24)
                                cce.sectionConfidence = (int)Math.Round(Utils.ConvertMeToDouble(dr[24].ToString()));
                            else
                                cce.sectionConfidence = 1;

                            if (dr.Table.Columns.Count > 25)
                                cce.sourceGUID = dr[25].ToString();

                            if (dr.Table.Columns.Count > 26)
                                cce.massPerMetre = Utils.ConvertMeToDouble(dr[26].ToString());



                            cmList.Add(cce);
                        }
                        catch (Exception ex)
                        { }
                    }
                }
            }
            catch 
            {
                return null;
            }

            return cmList;



        }


        internal static CarboLifeAPI.Data.CarboProject convertToCarboLifeProject(carboCircleProject circleProject)
        {
            //Carbon values for the reused materials come from the file named in the
            //CarboCircle settings, falling back to the copy shipped in circledb.
            string databasepath = circleProject.settings.getMaterialDatabasePath();

            if (databasepath != null && File.Exists(databasepath))
            {
                CarboProject result = new CarboProject(databasepath);
                //Get Materials
                if (result != null)
                {
                    List<CarboElement> elements = new List<CarboElement>();

                    //Get all reused elements;
                    foreach (carboCirclePair ccp in circleProject.carboCircleMatchedPairs)
                    {
                        //A requirement nothing could serve is carried as a real pair so the
                        //schedule is complete on screen, but there is no reused material behind
                        //it and it must not reach the carbon calculation.
                        if (ccp == null || ccp.matchClass == carboCircleMatchRules.ClassNoMatch)
                            continue;

                        CarboElement carboElement = new CarboElement();
                        carboElement.Name = ccp.mined_Element.name;

                        //What this requirement consumed, not the whole piece it came out of.
                        //Taking the piece counted a 9 m beam in full against a 6 m requirement,
                        //and then counted its offcut again on the next match.
                        carboElement.Volume = ccp.used_netVolume;

                        carboElement.MaterialName = ccp.mined_Element.materialName;
                        carboElement.Id = ccp.mined_Element.id;
                        elements.Add(carboElement);
                    }

                    if (elements.Count > 0)
                    {
                        foreach (CarboElement ce in elements)
                            result.AddElement(ce);
                    }

                    result.Audit();
                    result.CreateGroups();
                    result.CalculateProject();
                }
                else
                {
                    return null;
                }
                return result;
            }
            else
            {
                return null;
            }
        }

    }
}