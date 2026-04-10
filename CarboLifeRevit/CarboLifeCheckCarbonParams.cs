using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace CarboLifeRevit
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class CheckCarbonParams : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            Document doc = uiApp.ActiveUIDocument.Document;

            // 1. Get the path of the current DLL and find the .txt file in the same folder
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            string assemblyDir = Path.GetDirectoryName(assemblyPath);
            string carbonFilePath = Path.Combine(assemblyDir, "CarbonSharedParams.txt");

            if (!File.Exists(carbonFilePath))
            {
                TaskDialog.Show("Error", "Shared Parameter file not found at: " + carbonFilePath);
                return Result.Failed;
            }

            // 2. User Confirmation
            TaskDialog mainDialog = new TaskDialog("CarboLife");
            mainDialog.MainInstruction = "Would you like to check if the default CarboLife shared parameters are applied in this file?";
            mainDialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;

            if (mainDialog.Show() != TaskDialogResult.Yes) return Result.Cancelled;

            // 3. Store original path to be a "good citizen"
            string originalPath = uiApp.Application.SharedParametersFilename;

            try
            {
                uiApp.Application.SharedParametersFilename = carbonFilePath;
                DefinitionFile defFile = uiApp.Application.OpenSharedParameterFile();

                if (defFile == null) return Result.Failed;

                // 4. Identify all model categories that allow parameters
                CategorySet catSet = uiApp.Application.Create.NewCategorySet();
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat.CategoryType == CategoryType.Model && cat.AllowsBoundParameters)
                    {
                        catSet.Insert(cat);
                    }
                }

                using (Transaction t = new Transaction(doc, "Add CarboLife Parameters"))
                {
                    t.Start();

                    foreach (DefinitionGroup group in defFile.Groups)
                    {
                        foreach (Definition def in group.Definitions)
                        {
                            // Check if the parameter is already bound to the project map
                            if (!IsParameterBound(doc, def.Name))
                            {
                                ElementBinding binding;
                                
                                // Check for the specific Instance Parameter by name
                                if (def.Name == "CLC_EmbodiedCarbon" || def.Name == "CLC_IsSubstructure")
                                {
                                    // Create Instance Binding
                                    binding = uiApp.Application.Create.NewInstanceBinding(catSet);
                                }
                                else
                                {
                                    // Create Type Binding for all other parameters
                                    binding = uiApp.Application.Create.NewTypeBinding(catSet);
                                }

                                // Insert the binding into the document under the "Data" group
                                // Note: PG_DATA is for Revit < 2024. Use GroupTypeId.Data for 2024+
                                doc.ParameterBindings.Insert(def, binding, GroupTypeId.Data);
                            }
                        }
                    }
                    t.Commit();
                }

                TaskDialog.Show("Success", "CarboLife parameters are now loaded and bound to model categories.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
            finally
            {
                // 6. Restore original user settings
                uiApp.Application.SharedParametersFilename = originalPath;
            }
        }

        // Helper to check if a parameter name is already in the BindingMap
        private bool IsParameterBound(Document doc, string name)
        {
            BindingMap map = doc.ParameterBindings;
            DefinitionBindingMapIterator it = map.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                if (it.Key.Name == name) return true;
            }
            return false;
        }
    }
}