using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using System.Reflection;
using System.ComponentModel;
using System.Text.Json;
using Serilog;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace SFRT_PlanningScript
{

    public static class Helpers
    {
        public static class SeriLog
        {
            public static void Initialize(string user = "RunFromLauncher")
            {
                var SessionTimeStart = DateTime.Now;
                var AssemblyLocation = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(AssemblyLocation))
                    AssemblyLocation = AppDomain.CurrentDomain.BaseDirectory;
                var AssemblyPath = Path.GetDirectoryName(AssemblyLocation);
                var directory = Path.Combine(AssemblyPath, @"Logs");
                var logpath = Path.Combine(directory, string.Format(@"log_{0}_{1}_{2}.txt", SessionTimeStart.ToString("dd-MMM-yyyy"), SessionTimeStart.ToString("hh-mm-ss"), user.Replace(@"\", @"_")));
                Log.Logger = new LoggerConfiguration().WriteTo.File(logpath, Serilog.Events.LogEventLevel.Information,
                    "{Timestamp:dd-MMM-yyy HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}").CreateLogger();
            }
            public static void LogInfo(string log_info)
            {
                Log.Information(log_info);

            }
            public static void LogWarning(string log_info)
            {
                Log.Warning(log_info);
            }
            public static void LogError(string log_info, Exception ex = null)
            {
                if (ex == null)
                    Log.Error(log_info);
                else
                    Log.Error(ex, log_info);
            }
            public static void LogFatal(string log_info, Exception ex)
            {
                Log.Fatal(ex, log_info);
            }
        }
    }

    // Machine config loader moved here to keep helper utilities together
    public class MachineEntry
    {
        public string MachineId { get; set; }
        public string DisplayName { get; set; }
        public List<string> Energies_MV { get; set; }
        public string Notes { get; set; }
    }

    public class MachineConfig
    {
        public string Description { get; set; }
        public List<MachineEntry> Machines { get; set; }
        public static MachineConfig Load(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path))
                return null;

            try
            {
                Helpers.SeriLog.LogInfo($"Attempting to read machines.config at {path}");
                var json = File.ReadAllText(path);
                // Hand-edited file: tolerate trailing commas and comments
                var options = new JsonSerializerOptions
                {
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };
                MachineConfig cfg = JsonSerializer.Deserialize<MachineConfig>(json, options);
                if (cfg != null && cfg.Machines != null)
                {
                    Helpers.SeriLog.LogInfo($"Loaded machines.config from {path}; machines={cfg.Machines.Count}");
                }
                return cfg;
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError($"Failed to read/parse machines.config at {path}", ex);
                return null;
            }
        }

        public static MachineConfig LoadFromDefaults()
        {
            // Search common locations for machines.config and return first successful load
            var cfgPath = string.Empty;
            try
            {
                // var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                // Helpers.SeriLog.LogInfo($"Checking for machines.config candidate in base directory: {baseDir}");
                // candidates.Add(Path.Combine(baseDir, "configs", "machines.config"));

                // var asmPath = Assembly.GetExecutingAssembly().Location;
                var asmPath = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(asmPath))
                {
                    var asmDir = Path.GetDirectoryName(asmPath);
                    if (!string.IsNullOrEmpty(asmDir))
                        cfgPath = Path.Combine(asmDir, "configs", "machines.config");
                    Helpers.SeriLog.LogInfo($"Added machines.config candidate from assembly directory: {asmDir}");
                }

                // Also try repository root
                // var parent = Directory.GetParent(baseDir);
                // if (parent != null)
                // {
                //     candidates.Add(Path.Combine(parent.FullName, "configs", "machines.config"));
                //     Helpers.SeriLog.LogInfo($"Added machines.config candidate from parent directory: {parent.FullName}");
                // }
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogWarning($"Error building machines.config candidate list: {ex.Message}");
            }

            // Helpers.SeriLog.LogInfo($"machines.config candidates: {string.Join(";", candidates)}");

            try
            {
                if (File.Exists(cfgPath))
                {
                    Helpers.SeriLog.LogInfo($"Found machines.config at {cfgPath}; attempting parse");
                    MachineConfig cfg = Load(cfgPath);
                    if (cfg != null)
                        return cfg;
                    else
                        Helpers.SeriLog.LogWarning($"Parsed machines.config at {cfgPath} returned null or invalid");
                }
                else
                {
                    Helpers.SeriLog.LogInfo($"machines.config not found at {cfgPath}");
                }
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError($"Failed to read/parse machines.config at {cfgPath}", ex);
            }


            // foreach (var c in candidates)
            // {
            //     try
            //     {
            //         if (File.Exists(c))
            //         {
            //             Helpers.SeriLog.LogInfo($"Found machines.config at {c}; attempting parse");
            //             var cfg = Load(c);
            //             if (cfg != null)
            //                 return cfg;
            //             else
            //                 Helpers.SeriLog.LogWarning($"Parsed machines.config at {c} returned null or invalid");
            //         }
            //         else
            //         {
            //             Helpers.SeriLog.LogInfo($"machines.config not found at {c}");
            //         }
            //     }
            //     catch (Exception ex)
            //     {
            //         Helpers.SeriLog.LogError($"Failed to read/parse machines.config at {c}", ex);
            //     }
            // }

            Helpers.SeriLog.LogWarning("No valid machines.config found; returning null and using defaults");
            return null;
        }
    }

    // Preset template configuration: controls which planning templates appear in the GUI.
    // A template named <Name> requires templates/<name>_objectives.csv and
    // templates/<name>_prescription.json next to the plugin DLL.
    public class TemplateEntry
    {
        public string Name { get; set; }
        public bool Enabled { get; set; } = true;
        public string Notes { get; set; }
    }

    public class TemplateConfig
    {
        public string Description { get; set; }
        public List<TemplateEntry> Templates { get; set; }

        public static TemplateConfig Load(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path))
                return null;

            try
            {
                Helpers.SeriLog.LogInfo($"Attempting to read templates.config at {path}");
                var json = File.ReadAllText(path);
                // Hand-edited file: tolerate trailing commas and comments
                var options = new JsonSerializerOptions
                {
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };
                TemplateConfig cfg = JsonSerializer.Deserialize<TemplateConfig>(json, options);
                if (cfg != null && cfg.Templates != null)
                {
                    Helpers.SeriLog.LogInfo($"Loaded templates.config from {path}; templates={cfg.Templates.Count}");
                }
                return cfg;
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError($"Failed to read/parse templates.config at {path}", ex);
                return null;
            }
        }

        public static TemplateConfig LoadFromDefaults()
        {
            try
            {
                var asmPath = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(asmPath))
                {
                    var asmDir = Path.GetDirectoryName(asmPath);
                    if (!string.IsNullOrEmpty(asmDir))
                    {
                        var cfgPath = Path.Combine(asmDir, "configs", "templates.config");
                        if (File.Exists(cfgPath))
                            return Load(cfgPath);
                        Helpers.SeriLog.LogInfo($"templates.config not found at {cfgPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError("Failed to locate templates.config", ex);
            }

            Helpers.SeriLog.LogWarning("No valid templates.config found; returning null and using defaults");
            return null;
        }
    }

    // Prescription configuration loader
    public class PrescriptionConfig
    {
        // values are in cGy
        public double PTVLow { get; set; } = 2000.0;
        [JsonIgnore]
        public bool HasPTVLow { get; private set; } = true;
        public double PTVLowPriority { get; set; } = 100.0;

        public double Peak_Upper { get; set; } = 7000.0;
        public double Peak_Lower { get; set; } = 6670.0;
        public bool EnablePeakUpperObjective { get; set; } = true;
        public bool EnablePeakLowerObjective { get; set; } = true;
        public bool EnablePeakLower95Objective { get; set; } = true;
        public double PeakUpperPriority { get; set; } = 100.0;
        public double PeakLowerPriority { get; set; } = 100.0;
        public double PeakLower95Priority { get; set; } = 130.0;
        public bool EnableRepresentativePeakObjective { get; set; } = false;
        public double RepresentativePeakDose { get; set; } = 2000.0;
        public double RepresentativePeakVolumePercent { get; set; } = 50.0;
        public double RepresentativePeakPriority { get; set; } = 0.0;

        public double TS_Peak_Ring1 { get; set; } = 6670.0;
        public double TS_Peak_Ring2 { get; set; } = 6000.0;
        public double TS_Peak_Ring3 { get; set; } = 5000.0;
        public double TS_Peak_Ring1Priority { get; set; } = 30.0;
        public double TS_Peak_Ring2Priority { get; set; } = 30.0;
        public double TS_Peak_Ring3Priority { get; set; } = 30.0;

        public double TS_Ring1 { get; set; } = 2000.0;
        public double TS_Ring2 { get; set; } = 1500.0;
        public double TS_Ring1Priority { get; set; } = 30.0;
        public double TS_Ring2Priority { get; set; } = 30.0;
        public bool CombineTuningRingShells { get; set; } = false;

        public double Valley_Upper { get; set; } = 2400.0;
        public double Valley_Lower { get; set; } = 1950.0;
        public double Valley_EUD { get; set; } = 2100.0;
        public bool EnableValleyObjectives { get; set; } = true;
        public double ValleyUpperPriority { get; set; } = 100.0;
        public double ValleyLowerPriority { get; set; } = 100.0;
        public double ValleyEudPriority { get; set; } = 70.0;

        public bool CreateBodyMinusSpheresStructure { get; set; } = false;
        public string BodyMinusSpheresStructureId { get; set; } = "BODY_Spheres";
        public bool EnableBodyMinusSpheresObjective { get; set; } = false;
        public double BodyMinusSpheresDose { get; set; } = 333.0;
        public double BodyMinusSpheresVolumePercent { get; set; } = 1.0;
        public double BodyMinusSpheresPriority { get; set; } = 10.0;

        public static PrescriptionConfig Load(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path))
                return null;

            try
            {
                Helpers.SeriLog.LogInfo($"Attempting to read prescription config at {path}");
                var json = File.ReadAllText(path);
                // Hand-edited file: tolerate trailing commas and comments
                var options = new JsonSerializerOptions
                {
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };
                PrescriptionConfig cfg = JsonSerializer.Deserialize<PrescriptionConfig>(json, options);
                if (cfg != null)
                {
                    var docOptions = new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    };
                    using (var doc = JsonDocument.Parse(json, docOptions))
                    {
                        cfg.HasPTVLow = doc.RootElement.TryGetProperty(nameof(PrescriptionConfig.PTVLow), out _);
                    }
                    Helpers.SeriLog.LogInfo($"Loaded prescription config from {path}");
                }
                return cfg;
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError($"Failed to read/parse prescription config at {path}", ex);
                return null;
            }
        }

        public static PrescriptionConfig LoadFromDefaults()
        {
            try
            {
                var asmPath = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(asmPath))
                {
                    var asmDir = Path.GetDirectoryName(asmPath);
                    var cfgPath = Path.Combine(asmDir, "templates", "washu_prescription.json");
                    Helpers.SeriLog.LogInfo($"Checking for prescription config at {cfgPath}");
                    if (File.Exists(cfgPath))
                    {
                        var cfg = Load(cfgPath);
                        if (cfg != null)
                            return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogWarning($"Error locating prescription config: {ex.Message}");
            }

            Helpers.SeriLog.LogWarning("No prescription config found; using built-in defaults");
            return null;
        }
    }

        // Objective template loader: return full paths to CSV templates located in the
        // runtime `templates` folder next to the executing assembly.
        public static class ObjectiveTemplateConfig
        {
            public static List<string> LoadFromDefaults()
            {
                var list = new List<string>();
                try
                {
                    var asmPath = Assembly.GetExecutingAssembly().Location;
                    if (string.IsNullOrEmpty(asmPath))
                        asmPath = AppDomain.CurrentDomain.BaseDirectory;

                    var asmDir = Path.GetDirectoryName(asmPath);
                    if (string.IsNullOrEmpty(asmDir))
                        asmDir = AppDomain.CurrentDomain.BaseDirectory;

                    var tplDir = Path.Combine(asmDir, "templates");
                    Helpers.SeriLog.LogInfo($"Looking for objective templates in {tplDir}");
                    if (Directory.Exists(tplDir))
                    {
                        var files = Directory.GetFiles(tplDir, "*.csv");
                        foreach (var f in files)
                            list.Add(f);
                        Helpers.SeriLog.LogInfo($"Found {list.Count} objective templates");
                    }
                    else
                    {
                        Helpers.SeriLog.LogWarning($"Templates directory not found: {tplDir}");
                    }
                }
                catch (Exception ex)
                {
                    Helpers.SeriLog.LogError("Error locating objective templates", ex);
                }

                return list;
            }
        }

    }
