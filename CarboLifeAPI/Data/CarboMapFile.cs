using CarboLifeAPI.Data.Superseded;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Serialization;

namespace CarboLifeAPI.Data
{
    [Serializable]
    public class CarboMapFile
    {
        public List<CarboMapElement> mappingTable { get; set; }

        public CarboMapFile()
        {
            mappingTable = new List<CarboMapElement>();
        }

        /// <summary>
        /// Writes the mapping table.
        ///
        /// This is the file the whole company shares, so it is written the careful way: the new
        /// content goes to a temporary file next to the target first and only replaces the real
        /// one once it is complete, and a lock held by a colleague is retried for a moment before
        /// giving up. Serialising straight onto the shared path would leave everyone with a
        /// half written mapping file if the network dropped mid-write.
        ///
        /// The old version returned void, skipped the write entirely when the file was locked and
        /// swallowed every exception, so a user whose colleague had the shared file open lost
        /// their mapping work with no message at all.
        /// </summary>
        /// <param name="path">Target file, empty uses the configured mapping file.</param>
        /// <returns>True when the file was written.</returns>
        public bool SaveToXml(string path = "")
        {
            string error;
            return SaveToXml(path, out error);
        }

        /// <summary>
        /// As SaveToXml, with the reason it failed so the caller can tell the user.
        /// </summary>
        /// <param name="path">Target file, empty uses the configured mapping file.</param>
        /// <param name="error">Empty on success, otherwise a sentence fit to show.</param>
        public bool SaveToXml(string path, out string error)
        {
            error = "";

            string myPath = string.IsNullOrEmpty(path) ? PathUtils.GetMappingFilePath() : path;

            if (string.IsNullOrEmpty(myPath))
            {
                error = "No mapping file location is configured.";
                return false;
            }

            //A colleague saving at the same moment holds the file for a fraction of a second.
            //Worth waiting out; a file someone has genuinely left open is not.
            const int attempts = 3;

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                if (File.Exists(myPath) && DataExportUtils.IsFileLocked(myPath))
                {
                    if (attempt < attempts)
                    {
                        System.Threading.Thread.Sleep(250);
                        continue;
                    }

                    error = "The mapping file is open or locked by another program:" + Environment.NewLine +
                            myPath + Environment.NewLine + Environment.NewLine +
                            "It may be open on a colleague's machine. Your mapping has NOT been saved.";
                    return false;
                }

                try
                {
                    string folder = Path.GetDirectoryName(myPath);
                    if (string.IsNullOrEmpty(folder) == false && Directory.Exists(folder) == false)
                        Directory.CreateDirectory(folder);

                    //Write beside the target, then swap, so a failure never truncates the shared file.
                    string tempPath = myPath + ".tmp";

                    XmlSerializer serializer = new XmlSerializer(typeof(CarboMapFile));
                    using (StreamWriter writer = new StreamWriter(tempPath, false, Encoding.UTF8))
                    {
                        serializer.Serialize(writer, this);
                    }

                    if (File.Exists(myPath))
                        File.Delete(myPath);

                    File.Move(tempPath, myPath);

                    return true;
                }
                catch (Exception ex)
                {
                    if (attempt < attempts)
                    {
                        System.Threading.Thread.Sleep(250);
                        continue;
                    }

                    error = "The mapping file could not be written:" + Environment.NewLine +
                            myPath + Environment.NewLine + Environment.NewLine +
                            ex.Message + Environment.NewLine + Environment.NewLine +
                            "Your mapping has NOT been saved.";
                    return false;
                }
            }

            return false;
        }

        public static CarboMapFile LoadFromXml()
        {
            string myPath = PathUtils.GetMappingFilePath();

            if (File.Exists(myPath))
            {
                try
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(CarboMapFile));
                    using (StreamReader reader = new StreamReader(myPath))
                    {
                        return (CarboMapFile)serializer.Deserialize(reader);
                    }
                }
                catch (Exception ex)
                {
                    //Console.WriteLine("Error during deserialization: " + ex.Message);
                    return null;
                }
            }
            else
            {
                return new CarboMapFile();
            }
        }

        public static CarboMapFile ExportTo(string Filepath)
        {
            string myPath = Filepath;

            if (!File.Exists(myPath))
            {
                try
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(CarboMapFile));
                    using (StreamReader reader = new StreamReader(myPath))
                    {
                        return (CarboMapFile)serializer.Deserialize(reader);
                    }
                }
                catch (Exception ex)
                {
                    //Console.WriteLine("Error during deserialization: " + ex.Message);
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        public void Merge(List<CarboMapElement> newMappingTable)
        {
            foreach (var newElement in newMappingTable)
            {
                var existingElement = mappingTable.Find(e =>
                    string.Equals(e.revitName, newElement.revitName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.category, newElement.category, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.templateName, newElement.templateName, StringComparison.OrdinalIgnoreCase)
                );

                if (existingElement != null)
                {
                    // Update the carboNAME of the matching element
                    existingElement.carboNAME = newElement.carboNAME;
                }
                else
                {
                    // Add new element to the mapping table
                    mappingTable.Add(newElement);
                }
            }

            CleanUp();
        }

        private void CleanUp()
        {
            // Remove duplicates based on revitName, category, and templateName
            mappingTable = mappingTable
                .GroupBy(e => new { e.revitName, e.category, e.templateName })
                .Select(g => g.First())
                .ToList();
        }
    }
}
