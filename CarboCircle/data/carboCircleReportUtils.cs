using CarboLifeAPI;
using CarboLifeAPI.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;

namespace CarboCircle.data
{
    /// <summary>
    /// Writes the CarboCircle reuse report as a single self-contained html file.
    ///
    /// The look comes from CarboLifeAPI.ReportBuilder: its getCSS() is included verbatim
    /// and the markup here uses the class names that stylesheet defines - doc-header,
    /// kpi-band, info-grid, data-table and so on. A CarboCircle report should be
    /// indistinguishable from a Carbo Life one apart from what it says.
    ///
    /// The previous version pulled in the same stylesheet and then ignored it, laying
    /// everything out with "border=1 cellpadding=0 width=1600" tables, so none of the
    /// styling reached the page.
    /// </summary>
    internal class carboCircleReportUtils
    {
        //--------------------------------------------------------------------------------
        // Export
        //--------------------------------------------------------------------------------

        /// <summary>
        /// Writes the report. Returns true on success; <paramref name="message"/> carries
        /// something worth showing the user either way.
        ///
        /// Reporting through a return value rather than a MessageBox of its own: this runs
        /// inside the Revit external event, and the caller already knows how to talk to the
        /// user from there.
        /// </summary>
        internal static bool ExportReport(carboCircleProject project, string reportImage, string reportPath, out string message)
        {
            //An empty path used to reach StreamWriter and come back as "Empty path name is
            //not legal", which tells the user nothing about which step failed.
            if (string.IsNullOrWhiteSpace(reportPath))
            {
                message = "No report file was chosen, so there was nothing to write to.";
                return false;
            }

            if (project == null)
            {
                message = "There is no project data to report on.";
                return false;
            }

            try
            {
                StringBuilder report = new StringBuilder();

                report.Append(writeDocumentHead(project));
                report.Append(writeProjectInfo(project));
                report.Append(writeModelImage(reportImage));
                report.Append(writeMatchTable(project));
                report.Append(writeVolumesTable(project));
                report.Append(writeLeftOverTable(project));
                report.Append(ReportBuilder.closeHTML());

                //UTF-8, matching the charset the document declares. The old code asked for
                //Windows-1252, which .NET Core moved into a provider that has to be
                //registered first - so on the Revit 2025 build this threw before writing a
                //single byte, and the report never appeared.
                using (StreamWriter sw = new StreamWriter(reportPath, false, new UTF8Encoding(false)))
                {
                    sw.Write(report.ToString());
                }

                message = "Report written to " + reportPath;
                return true;
            }
            catch (Exception ex)
            {
                message = "The report could not be written: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Opens a finished report in whatever the machine uses for html.
        /// </summary>
        internal static bool OpenReport(string reportPath, out string message)
        {
            message = "";

            if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
            {
                message = "The report file could not be found at " + reportPath + ".";
                return false;
            }

            try
            {
                //UseShellExecute has to be set explicitly. It defaults to true on .NET
                //Framework and false on .NET Core, and with it false Windows refuses to
                //start an html file at all: "The specified executable is not a valid
                //application for this OS platform".
                ProcessStartInfo start = new ProcessStartInfo(reportPath);
                start.UseShellExecute = true;

                Process.Start(start);
                return true;
            }
            catch (Exception ex)
            {
                message = "The report was written but could not be opened: " + ex.Message;
                return false;
            }
        }

        //--------------------------------------------------------------------------------
        // Document
        //--------------------------------------------------------------------------------

        private static string writeDocumentHead(carboCircleProject project)
        {
            string html = "";
            string exportDate = DateTime.Today.ToShortDateString();

            html += "<HTML><HEAD><META charset=\"utf-8\">";
            html += "<TITLE>CarboCircle : Reuse Assessment for: " + esc(project.ProjectName) + " </TITLE>" + Environment.NewLine;

            html += ReportBuilder.getCSS();

            html += "</HEAD><BODY>";

            //Document header, same shape as ReportBuilder.writeHeader: an eyebrow, the
            //project name as the document title, then a one-line meta strip.
            html += "<DIV class=\"doc-header\">" + Environment.NewLine;
            html += "<DIV class=\"eyebrow\">Material Reuse Assessment</DIV>" + Environment.NewLine;
            html += "<H1 class=\"doc-title\"><B>" + esc(project.ProjectName) + "</B></H1>" + Environment.NewLine;

            html += "<DIV class=\"doc-meta\">";

            if (!string.IsNullOrWhiteSpace(project.ProjectNumber))
                html += "<span>" + esc(project.ProjectNumber) + "</span>";

            if (!string.IsNullOrWhiteSpace(project.ProjectCategory))
                html += "<span>" + esc(project.ProjectCategory) + "</span>";

            html += "<span>" + exportDate + "</span>";
            html += "</DIV>" + Environment.NewLine;
            html += "</DIV>" + Environment.NewLine;

            //Headline figures.
            //Only pairs that actually found material count as substituted. A requirement
            //nothing could serve is carried as a pair so the schedule is complete on screen,
            //and counting those would report every requirement as a success.
            int matched = 0;
            int leftOver = 0;

            if (project.carboCircleMatchedPairs != null)
            {
                foreach (carboCirclePair counted in project.carboCircleMatchedPairs)
                {
                    if (counted == null)
                        continue;

                    if (counted.matchClass == carboCircleMatchRules.ClassNoMatch)
                        leftOver++;
                    else
                        matched++;
                }
            }
            double reusedLength = 0;
            double reusableVolume = 0;

            if (project.carboCircleMatchedPairs != null)
            {
                foreach (carboCirclePair pair in project.carboCircleMatchedPairs)
                {
                    if (pair != null && pair.matchClass != carboCircleMatchRules.ClassNoMatch)
                        reusedLength += pair.used_netLength;
                }
            }

            foreach (carboCircleElement cce in project.getCarboVolumeOpportunities())
            {
                //netVolume, not volume. The gross figure ignores the deconstruction loss the
                //user set, so the headline claimed the whole wall came back while the table
                //two sections below correctly showed a quarter of it lost.
                if (cce != null)
                    reusableVolume += cce.netVolume;
            }

            html += "<DIV class=\"kpi-band\">";
            html += getKpi("Members&nbsp;Substituted", matched.ToString(), "no.");
            html += getKpi("Member&nbsp;Length&nbsp;Reused", Fmt(reusedLength, 1), "m");
            html += getKpi("Volume&nbsp;Recoverable", Fmt(reusableVolume, 2), "m<SUP>3</SUP>");
            html += getKpi("Members&nbsp;Still&nbsp;New", leftOver.ToString(), "no.");
            html += "</DIV>" + Environment.NewLine;

            return html;
        }

        private static string writeProjectInfo(carboCircleProject project)
        {
            string html = "<H1><B>" + "Project Info" + "</B></H1>" + Environment.NewLine;

            html += "<DIV class=\"info-grid\">" + Environment.NewLine;

            html += getInfoItem("Name", esc(project.ProjectName));
            html += getInfoItem("Project Number", esc(project.ProjectNumber));
            html += getInfoItem("Description", esc(project.ProjectDescription));
            html += getInfoItem("Category", esc(project.ProjectCategory));
            html += getInfoItem("Mined Members", project.minedData.Count.ToString());
            html += getInfoItem("Mined Volumes", project.minedVolumes.Count.ToString());
            html += getInfoItem("Required Members", project.requiredData.Count.ToString());
            html += getInfoItem("Required Volumes", project.requiredVolumes.Count.ToString());
            html += getInfoItem("Export Date", DateTime.Today.ToShortDateString());

            html += "</DIV>" + Environment.NewLine;

            return html;
        }

        private static string writeModelImage(string reportImage)
        {
            if (string.IsNullOrEmpty(reportImage))
                return "";

            string html = "<H1><B>" + "Model" + "</B></H1>" + Environment.NewLine;

            html += "<DIV class=\"charts\">" + Environment.NewLine;
            html += "<DIV class=\"chart\">" + ReportBuilder.getImageTag(reportImage, 1000, 0, "Model") + "</DIV>" + Environment.NewLine;
            html += "</DIV>" + Environment.NewLine;

            return html;
        }

        //--------------------------------------------------------------------------------
        // Tables
        //--------------------------------------------------------------------------------

        /// <summary>
        /// The substitutions found: a proposed member and the existing one that can serve it.
        ///
        /// Column order is not arbitrary. The shared stylesheet right-aligns every column
        /// from the fourth onwards and lets the third wrap, so the text columns come first,
        /// the description sits third, and the numbers follow.
        /// </summary>
        private static string writeMatchTable(carboCircleProject project)
        {
            string html = "<H1><B>" + "Substituted Members" + "</B></H1>" + Environment.NewLine;

            html += "<H3>Each proposed member below can be served by an existing member taken from " +
                    "the same building, rather than by new material.</H3>" + Environment.NewLine;

            List<carboCircleMatchElement> matches = project.getCarboMatchesListSimplified();

            if (matches.Count == 0)
                return html + "<H3>No substitutions were found.</H3>" + Environment.NewLine;

            html += "<DIV class=\"table-wrap\"><TABLE class=\"data-table\" cellpadding=0 cellspacing=0>";

            html += "<TR class=\"hrow\">" + Environment.NewLine;
            html += cell("Proposed Member");
            html += cell("Substituted With");
            html += cell("Reason");
            html += cell("Proposed Id");
            html += cell("Existing Id");
            html += cell("Required Length");
            html += cell("Usable Length");
            html += cell("Offcut");
            html += cell("Wel Used");
            html += "</TR>" + Environment.NewLine;

            html += "<TR class=\"urow\">" + Environment.NewLine;
            html += cell("");
            html += cell("");
            html += cell("");
            html += cell("");
            html += cell("");
            html += cell("m");
            html += cell("m");
            html += cell("m");
            html += cell("");
            html += "</TR>" + Environment.NewLine;

            double totalRequired = 0;
            double totalOffcut = 0;
            int substituted = 0;

            //Grouped by match class, in the same priority order as the grid on screen, so the
            //report and the window tell the same story in the same sequence. The class heading
            //is what makes a long schedule readable: the 100% matches are read once and the
            //attention goes on the rows that need a decision.
            int[] classOrder = new int[]
            {
                carboCircleMatchRules.ClassExactSection,
                carboCircleMatchRules.ClassAdequateSameFamily,
                carboCircleMatchRules.ClassAdequateCrossFamily,
                carboCircleMatchRules.ClassFromOffcut,
                carboCircleMatchRules.ClassNoMatch
            };

            foreach (int matchClass in classOrder)
            {
                List<carboCircleMatchElement> inClass = new List<carboCircleMatchElement>();

                foreach (carboCircleMatchElement match in matches)
                {
                    if (match != null && match.matchRank == matchClass)
                        inClass.Add(match);
                }

                if (inClass.Count == 0)
                    continue;

                bool isNoMatch = matchClass == carboCircleMatchRules.ClassNoMatch;

                html += "<TR class=\"group\">" + Environment.NewLine;
                html += "<TD colspan=\"9\">" + esc(carboCircleMatchRules.classLabel(matchClass)) +
                        " (" + inClass.Count + ")</TD>" + Environment.NewLine;
                html += "</TR>" + Environment.NewLine;

                foreach (carboCircleMatchElement match in inClass)
                {
                    //The engine's own figure. Recomputing it as usable minus required drops the
                    //cutting allowance charged when the remnant was made, so the report
                    //contradicted both the engine and the sentence in its own Reason column.
                    double offcut = isNoMatch ? 0 : match.offcut_netLength;

                    totalRequired += match.required_length;
                    totalOffcut += offcut;

                    if (!isNoMatch)
                        substituted++;

                    html += "<TR>" + Environment.NewLine;
                    html += cell(esc(match.required_Name));
                    html += cell(isNoMatch ? "-" : esc(match.mined_Name));
                    html += cell(esc(match.description));
                    html += cell(match.required_id.ToString());
                    html += cell(isNoMatch ? "-" : match.mined_id.ToString());
                    html += cell(Fmt(match.required_length, 2));
                    html += cell(isNoMatch ? "-" : Fmt(match.mined_netLength, 2));
                    html += cell(offcut > 0 ? Fmt(offcut, 2) : "-");
                    html += cell(isNoMatch ? "-" : Fmt(match.match_Score, 0) + "%");
                    html += "</TR>" + Environment.NewLine;
                }
            }

            html += "<TR class=\"totals\">" + Environment.NewLine;
            html += cell(substituted + " of " + matches.Count +
                         plural(matches.Count, " member", " members") + " served from reuse");
            html += cell("");
            html += cell("");
            html += cell("");
            html += cell("");
            html += cell(Fmt(totalRequired, 2));
            html += cell("");
            html += cell(Fmt(totalOffcut, 2));
            html += cell("");
            html += "</TR>" + Environment.NewLine;

            html += "</TABLE></DIV>" + Environment.NewLine;

            return html;
        }

        /// <summary>
        /// Material that cannot come back as a member, but can come back as material.
        /// </summary>
        private static string writeVolumesTable(carboCircleProject project)
        {
            string html = "<H1><B>" + "Recoverable Material" + "</B></H1>" + Environment.NewLine;

            html += "<H3>These materials are being taken out of the existing building. They cannot be " +
                    "reused as members, but they can be processed and substituted for new material.</H3>" + Environment.NewLine;

            List<carboCircleElement> volumes = project.getCarboVolumeOpportunities();

            if (volumes.Count == 0)
                return html + "<H3>No recoverable material was identified.</H3>" + Environment.NewLine;

            html += "<DIV class=\"table-wrap\"><TABLE class=\"data-table\" cellpadding=0 cellspacing=0>";

            html += "<TR class=\"hrow\">" + Environment.NewLine;
            html += cell("Material");
            html += cell("Class");
            html += cell("Recovered From");
            html += cell("Volume");
            html += cell("Usable Volume");
            html += "</TR>" + Environment.NewLine;

            html += "<TR class=\"urow\">" + Environment.NewLine;
            html += cell("");
            html += cell("");
            html += cell("");
            html += cell("m<SUP>3</SUP>");
            html += cell("m<SUP>3</SUP>");
            html += "</TR>" + Environment.NewLine;

            double totalVolume = 0;
            double totalNet = 0;

            foreach (carboCircleElement cce in volumes)
            {
                if (cce == null)
                    continue;

                totalVolume += cce.volume;
                totalNet += cce.netVolume;

                html += "<TR>" + Environment.NewLine;
                html += cell(esc(cce.materialName));
                html += cell(esc(cce.materialClass));
                html += cell(esc(cce.name));
                html += cell(Fmt(cce.volume, 3));
                html += cell(Fmt(cce.netVolume, 3));
                html += "</TR>" + Environment.NewLine;
            }

            html += "<TR class=\"totals\">" + Environment.NewLine;
            html += cell("Total");
            html += cell("");
            html += cell("");
            html += cell(Fmt(totalVolume, 3));
            html += cell(Fmt(totalNet, 3));
            html += "</TR>" + Environment.NewLine;

            html += "</TABLE></DIV>" + Environment.NewLine;

            return html;
        }

        /// <summary>
        /// Mined members that found no taker on this project.
        /// </summary>
        private static string writeLeftOverTable(carboCircleProject project)
        {
            string html = "<H1><B>" + "Unmatched Members" + "</B></H1>" + Environment.NewLine;

            html += "<H3>These members are coming out of the existing building and could be reused, but " +
                    "nothing in the proposed design fits them. They are candidates for reuse elsewhere.</H3>" + Environment.NewLine;

            List<carboCircleElement> leftOvers = project.getLeftOverData();

            if (leftOvers.Count == 0)
                return html + "<H3>Every mined member found a use.</H3>" + Environment.NewLine;

            html += "<DIV class=\"table-wrap\"><TABLE class=\"data-table\" cellpadding=0 cellspacing=0>";

            html += "<TR class=\"hrow\">" + Environment.NewLine;
            html += cell("Member");
            html += cell("Material");
            html += cell("Section");
            html += cell("Id");
            html += cell("Model Length");
            html += cell("Usable Length");
            html += cell("Volume");
            html += "</TR>" + Environment.NewLine;

            html += "<TR class=\"urow\">" + Environment.NewLine;
            html += cell("");
            html += cell("");
            html += cell("");
            html += cell("");
            html += cell("m");
            html += cell("m");
            html += cell("m<SUP>3</SUP>");
            html += "</TR>" + Environment.NewLine;

            double totalLength = 0;
            double totalUsable = 0;

            foreach (carboCircleElement cce in leftOvers)
            {
                if (cce == null)
                    continue;

                totalLength += cce.length;
                totalUsable += cce.netLength;

                html += "<TR>" + Environment.NewLine;
                html += cell(esc(cce.name));
                html += cell(esc(cce.materialName));
                html += cell(esc(cce.standardName));
                html += cell(esc(cce.humanId));
                html += cell(Fmt(cce.length, 2));
                html += cell(Fmt(cce.netLength, 2));
                html += cell(Fmt(cce.volume, 3));
                html += "</TR>" + Environment.NewLine;
            }

            html += "<TR class=\"totals\">" + Environment.NewLine;
            html += cell("Total, " + leftOvers.Count + plural(leftOvers.Count, " member", " members"));
            html += cell("");
            html += cell("");
            html += cell("");
            html += cell(Fmt(totalLength, 2));
            html += cell(Fmt(totalUsable, 2));
            html += cell("");
            html += "</TR>" + Environment.NewLine;

            html += "</TABLE></DIV>" + Environment.NewLine;

            return html;
        }

        //--------------------------------------------------------------------------------
        // Helpers
        //--------------------------------------------------------------------------------

        private static string plural(int count, string one, string many)
        {
            return count == 1 ? one : many;
        }

        private static string cell(string content)
        {
            return "<TD>" + content + "</TD>" + Environment.NewLine;
        }

        private static string getKpi(string label, string value, string unit)
        {
            string html = "<DIV class=\"kpi\">";
            html += "<DIV class=\"kpi-label\">" + label + "</DIV>";
            html += "<DIV class=\"kpi-value\">" + value + "<span class=\"kpi-unit\">" + unit + "</span></DIV>";
            html += "</DIV>";

            return html;
        }

        private static string getInfoItem(string label, string value)
        {
            string html = "<DIV class=\"info-item\">";
            html += "<DIV class=\"info-label\">" + label + "</DIV>";
            html += "<DIV class=\"info-value\">" + value + "</DIV>";
            html += "</DIV>" + Environment.NewLine;

            return html;
        }

        /// <summary>
        /// Numbers with thousand separators, so the headline figures stay readable.
        /// </summary>
        private static string Fmt(double value, int decimals)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "-";

            return value.ToString("N" + decimals);
        }

        /// <summary>
        /// Makes a value safe to drop into markup.
        ///
        /// Revit names carry ampersands and angle brackets often enough to matter - a type
        /// called "Steel &amp; Timber Composite" used to break the rest of the document,
        /// because nothing on this path escaped anything.
        /// </summary>
        private static string esc(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }

        //--------------------------------------------------------------------------------
        // Image
        //--------------------------------------------------------------------------------

        internal static string getImageAsString(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    using (Image image = Image.FromFile(path))
                    {
                        using (MemoryStream m = new MemoryStream())
                        {
                            image.Save(m, image.RawFormat);
                            byte[] imageBytes = m.ToArray();

                            // Convert byte[] to Base64 String
                            return Convert.ToBase64String(imageBytes);
                        }
                    }
                }
            }
            catch
            {
                return "";
            }

            return "";
        }
    }
}
