using SFRT_PlanningScript.Models;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using SW = System.Windows;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using ESAPIScript;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;


namespace SFRT_PlanningScript
{
    public class OarEntry : BindableBase
    {
        private string name;
        public string Name { get => name; set => SetProperty(ref name, value); }

        private double margin;
        public double Margin { get => margin; set => SetProperty(ref margin, value); }

        public OarEntry() { }
        public OarEntry(string name, double margin) { Name = name; Margin = margin; }
    }

    public class LatticeParameters
    {
        public float Radius { get; set; }
        public double XShift { get; set; }
        public double YShift { get; set; }
        public double ZShift { get; set; }
        public double RotX { get; set; }
        public double RotY { get; set; }
        public double RotZ { get; set; }
        public bool UseManualTransform { get; set; } = false;
        public float VThresh { get; set; }
        public string BodyId { get; set; }
        public string TargetStructure { get; set; }
        public string PTVLowStructure { get; set; }
        public List<OarEntry> OarStructures { get; set; }
        public IEnumerable<string> OarStructureNames =>
            OarStructures?.Select(o => o.Name) ?? Enumerable.Empty<string>();
        public double BodyMargin { get; set; }
        // Default/last-used OAR margin from the picker. Kept for legacy
        // callers; per-OAR margins live on each OarEntry above.
        public double OarMargin { get; set; }
        public bool CouchKick { get; set; }
        public string Energy { get; set; }
        public string MachineId { get; set; }
        public string SphereSize { get; set; }
        public double LatticeSpacing { get; set; }
        public bool HighRes { get; set; }
        // Rotation optimization controls. PCA seeding only applies while EnableRotation
        // is on; PcaZOnly restricts the PCA seed to the in-plane (Z) angle (no X/Y tilt).
        public bool EnableRotation { get; set; } = true;
        public bool UsePCA { get; set; } = true;
        public bool PcaZOnly { get; set; } = false;
        public double RotationCoarseRange { get; set; } = 15.0; // degrees
        public double RotationCoarseStep { get; set; } = 2.5; // degrees
        public double RotationFineRange { get; set; } = 2.5; // degrees
        public double RotationFineStep { get; set; } = 0.5; // degrees
        public int RotationTopShifts { get; set; } = 10;
        // If true, create tuning structures (TS_Peak_*, TS_Valley_*) individually per sphere.
        // Default false: keep grouping by row (slice) for fewer structures.
        public bool IndividualTuningStructures { get; set; } = false;
        // Optional: draw debug sphere markers for template grids in Eclipse
        public bool DebugDrawGrids { get; set; } = false;
    }
    public class SphereDialogViewModel : BindableBase
    {
        private LatticeParameters latticeParams;
        private Model _model;
        private EsapiWorker _ew = null;
        private System.Windows.Threading.Dispatcher _uiDispatcher = null;
        private readonly SemaphoreSlim _esapiOperationGate = new SemaphoreSlim(1, 1);
        private string output;
        public string Output
        {
            get { return output; }
            set { SetProperty(ref output, value); }
        }

        private string statusMessage = "Ready.";
        public string StatusMessage
        {
            get { return statusMessage; }
            set { SetProperty(ref statusMessage, value); }
        }

        private bool isBusy = false;
        public bool IsBusy
        {
            get { return isBusy; }
            set { SetProperty(ref isBusy, value); }
        }

        private bool latticeCreatedInSession = false;

        private string patientLabel = "Patient";
        public string PatientLabel
        {
            get => patientLabel;
            set => SetProperty(ref patientLabel, value);
        }

        private string planLabel = "Plan";
        public string PlanLabel
        {
            get => planLabel;
            set => SetProperty(ref planLabel, value);
        }

        public void LoadPresetTemplate(int presetIndex) => LoadPresetTemplate(presetIndex, silent: false);

        public void LoadPresetTemplate(int presetIndex, bool silent)
        {
            try
            {
                if (presetIndex < 0 || presetIndex >= PresetTemplates.Count)
                {
                    if (!silent)
                    {
                        MessageBox.Show("No planning preset selected.", "Template not loaded", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                string presetName = PresetTemplates[presetIndex];
                string objectiveFile = $"{presetName.ToLower()}_objectives.csv";
                string prescriptionFile = $"{presetName.ToLower()}_prescription.json";

                // Find the template files in the templates directory
                var templatesDir = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "templates");
                var objectivePath = Path.Combine(templatesDir, objectiveFile);
                var prescriptionPath = Path.Combine(templatesDir, prescriptionFile);

                var missing = new List<string>();
                if (!File.Exists(objectivePath))
                {
                    missing.Add(objectivePath);
                }

                if (!File.Exists(prescriptionPath))
                {
                    missing.Add(prescriptionPath);
                }

                if (missing.Count > 0)
                {
                    var message = $"The {presetName} preset is incomplete and was not loaded.\n\nMissing file(s):\n" +
                                  string.Join("\n", missing) +
                                  "\n\nAdd the missing files to the templates folder or choose another preset.";
                    if (!silent)
                    {
                        MessageBox.Show(message, "Template files missing", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    StatusMessage = $"{presetName} preset missing required template files.";
                    Helpers.SeriLog.LogWarning(message);
                    return;
                }

                _model.LoadOptimizationParameters(objectivePath);
                if (!_model.LoadPrescriptionParameters(prescriptionPath))
                {
                    if (!silent)
                    {
                        MessageBox.Show($"Prescription template could not be read:\n{prescriptionPath}", "Template load failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    StatusMessage = $"{presetName} prescription template could not be read.";
                    return;
                }

                if (presetName.Equals("Mayo", StringComparison.OrdinalIgnoreCase))
                {
                    IndividualTuningStructures = true;
                    UsePCA = true;
                    PcaZOnly = false;
                }
                else if (presetName.Equals("WashU", StringComparison.OrdinalIgnoreCase))
                {
                    IndividualTuningStructures = false;
                    // WashU protocol: PCA seeds only the in-plane (Z) angle for the rotation
                    // sweep; the lattice never tilts about X/Y. (PCA is skipped entirely
                    // while the rotation search is turned off.)
                    UsePCA = true;
                    PcaZOnly = true;
                }

                Helpers.SeriLog.LogInfo($"Loaded preset template '{presetName}': {objectivePath}, {prescriptionPath}");
                StatusMessage = $"{presetName} objective and prescription templates loaded.";
                if (!silent)
                {
                    MessageBox.Show($"Loaded {presetName} template successfully.");
                }
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError($"Error loading preset template", ex);
                if (!silent)
                {
                    MessageBox.Show($"Failed to load preset template:\n{ex.Message}");
                }
            }
        }

        private double xShift;
        public double XShift
        {
            get { return xShift; }
            set { SetProperty(ref xShift, value); }
        }

        private double yShift;
        public double YShift
        {
            get { return yShift; }
            set { SetProperty(ref yShift, value); }
        }

        private float radius;
        public float Radius
        {
            get { return radius; }
            set
            {
                if (SetProperty(ref radius, value))
                {
                    UpdatePreviewShiftLimits();
                }
            }
        }

        private ObservableCollection<string> oarStructures = new ObservableCollection<string>();

        public ObservableCollection<string> OarStructures
        {
            get { return oarStructures; }
            set { SetProperty(ref oarStructures, value); }
        }
        private List<string> targetStructures = new List<string>();
        public List<string> TargetStructures
        {
            get { return targetStructures; }
            set { SetProperty(ref targetStructures, value); }
        }

        private int selectedOar;
        public int SelectedOar
        {
            get { return selectedOar; }
            set { SetProperty(ref selectedOar, value); }
        }

        private ObservableCollection<OarEntry> selectedOars = new ObservableCollection<OarEntry>();
        public ObservableCollection<OarEntry> SelectedOars
        {
            get { return selectedOars; }
            set
            {
                SetProperty(ref selectedOars, value);
            }
        }

        private int selectedTableOar;
        public int SelectedTableOar
        {
            get { return selectedTableOar; }
            set { SetProperty(ref selectedTableOar, value); }
        }
        private int targetSelected;
        public int TargetSelected
        {
            get { return targetSelected; }
            set { SetProperty(ref targetSelected, value); }
        }

        private int ptvSelected;

        public int PtvSelected
        {
            get { return ptvSelected; }
            set { SetProperty(ref ptvSelected, value); }
        }

        private int bodySelected;
        public int BodySelected
        {
            get { return bodySelected; }
            set
            {
                SetProperty(ref bodySelected, value);
                if (allStructures != null && value >= 0 && value < allStructures.Count)
                {
                    BodyId = allStructures[value];
                }
            }
        }

        public ObservableCollection<string> EnergyList { get; set; } = new ObservableCollection<string>();

        // Preset templates; populated from configs/templates.config in LoadEsapiDataAsync
        public ObservableCollection<string> PresetTemplates { get; set; } = new ObservableCollection<string>();

        private int selectedPresetTemplate = -1;
        public int SelectedPresetTemplate
        {
            get { return selectedPresetTemplate; }
            set { SetProperty(ref selectedPresetTemplate, value); }
        }

        public ObservableCollection<string> SphereSize { get; set; } = new ObservableCollection<string>
        {
            "1.5",
            "1.0",
            "0.75",
            "0.5",
        };

        private int selectedSphereSize = 0;

        public int SelectedSphereSize
        {
            get { return selectedSphereSize; }
            set
            {
                if (SetProperty(ref selectedSphereSize, value))
                {
                    UpdateSphereDerivedDefaults(true);
                }
            }
        }

        private int selectedEnergy = 0;

        public int SelectedEnergy
        {
            get { return selectedEnergy; }
            set { SetProperty(ref selectedEnergy, value); }
        }
        public ObservableCollection<string> MachineIds
        { get; set; } = new ObservableCollection<string>();

        // Display names shown in the UI (aligned by index with MachineIds)
        public ObservableCollection<string> MachineDisplayNames { get; set; } = new ObservableCollection<string>();

        private MachineConfig _machineConfig = null;

        private int selectedMachineId = 0;
        public int SelectedMachineId
        {
            get { return selectedMachineId; }
            set
            {
                if (SetProperty(ref selectedMachineId, value))
                {
                    UpdateEnergiesFromSelectedMachine();
                }
            }
        }
        private ObservableCollection<string> allStructures = new ObservableCollection<string>();
        public ObservableCollection<string> AllStructures
        {
            get { return allStructures; }
            set { SetProperty(ref allStructures, value); }
        }
        private float vThresh;
        public float VThresh
        {
            get { return vThresh; }
            set { SetProperty(ref vThresh, value); }
        }

        private double latticeSpacing;
        public double LatticeSpacing
        {
            get { return latticeSpacing; }
            set
            {
                double coercedValue = CoerceLatticeSpacing(value);
                if (SetProperty(ref latticeSpacing, coercedValue))
                {
                    RaisePropertyChanged(nameof(LatticeSpacingCm));
                    UpdatePreviewShiftLimits();
                }
                else if (Math.Abs(value - coercedValue) > 1e-9)
                {
                    RaisePropertyChanged(nameof(LatticeSpacing));
                    RaisePropertyChanged(nameof(LatticeSpacingCm));
                }
            }
        }

        public double LatticeSpacingCm
        {
            get { return LatticeSpacing / 10.0; }
            set { LatticeSpacing = value * 10.0; }
        }

        private double previewShiftMinimum = -3.75;
        public double PreviewShiftMinimum
        {
            get { return previewShiftMinimum; }
            set { SetProperty(ref previewShiftMinimum, value); }
        }

        private double previewShiftMaximum = 3.75;
        public double PreviewShiftMaximum
        {
            get { return previewShiftMaximum; }
            set { SetProperty(ref previewShiftMaximum, value); }
        }

        private double previewShiftXMinimum = -3.75;
        public double PreviewShiftXMinimum
        {
            get { return previewShiftXMinimum; }
            set { SetProperty(ref previewShiftXMinimum, value); }
        }

        private double previewShiftXMaximum = 3.75;
        public double PreviewShiftXMaximum
        {
            get { return previewShiftXMaximum; }
            set { SetProperty(ref previewShiftXMaximum, value); }
        }

        private double previewShiftYMinimum = -3.75;
        public double PreviewShiftYMinimum
        {
            get { return previewShiftYMinimum; }
            set { SetProperty(ref previewShiftYMinimum, value); }
        }

        private double previewShiftYMaximum = 3.75;
        public double PreviewShiftYMaximum
        {
            get { return previewShiftYMaximum; }
            set { SetProperty(ref previewShiftYMaximum, value); }
        }

        private double previewShiftZMinimum = -3.75;
        public double PreviewShiftZMinimum
        {
            get { return previewShiftZMinimum; }
            set { SetProperty(ref previewShiftZMinimum, value); }
        }

        private double previewShiftZMaximum = 3.75;
        public double PreviewShiftZMaximum
        {
            get { return previewShiftZMaximum; }
            set { SetProperty(ref previewShiftZMaximum, value); }
        }

        // Live preview controls
        private double shiftX = 0.0;
        public double ShiftX
        {
            get => shiftX;
            set { SetProperty(ref shiftX, Clamp(value, PreviewShiftXMinimum, PreviewShiftXMaximum)); }
        }

        private double shiftY = 0.0;
        public double ShiftY
        {
            get => shiftY;
            set { SetProperty(ref shiftY, Clamp(value, PreviewShiftYMinimum, PreviewShiftYMaximum)); }
        }

        private double shiftZ = 0.0;
        public double ShiftZ
        {
            get => shiftZ;
            set { SetProperty(ref shiftZ, Clamp(value, PreviewShiftZMinimum, PreviewShiftZMaximum)); }
        }

        private double rotX = 0.0;
        public double RotX
        {
            get => rotX;
            set { SetProperty(ref rotX, Clamp(value, -45.0, 45.0)); }
        }

        private double rotY = 0.0;
        public double RotY
        {
            get => rotY;
            set { SetProperty(ref rotY, Clamp(value, -45.0, 45.0)); }
        }

        private double rotZ = 0.0;
        public double RotZ
        {
            get => rotZ;
            set { SetProperty(ref rotZ, Clamp(value, -45.0, 45.0)); }
        }

        private int hotSphereCount = 0;
        public int HotSphereCount
        {
            get => hotSphereCount;
            set { SetProperty(ref hotSphereCount, value); }
        }

        private bool enableRotation = true;
        public bool EnableRotation
        {
            get { return enableRotation; }
            set { SetProperty(ref enableRotation, value); }
        }

        private bool usePCA = true;
        public bool UsePCA
        {
            get { return usePCA; }
            set { SetProperty(ref usePCA, value); }
        }

        private bool pcaZOnly = false;
        public bool PcaZOnly
        {
            get { return pcaZOnly; }
            set { SetProperty(ref pcaZOnly, value); }
        }

        private double rotationCoarseRange = 30.0;
        public double RotationCoarseRange
        {
            get { return rotationCoarseRange; }
            set { SetProperty(ref rotationCoarseRange, value); }
        }

        private double rotationCoarseStep = 5.0;
        public double RotationCoarseStep
        {
            get { return rotationCoarseStep; }
            set { SetProperty(ref rotationCoarseStep, value); }
        }

        private double rotationFineRange = 5.0;
        public double RotationFineRange
        {
            get { return rotationFineRange; }
            set { SetProperty(ref rotationFineRange, value); }
        }

        private double rotationFineStep = 1.0;
        public double RotationFineStep
        {
            get { return rotationFineStep; }
            set { SetProperty(ref rotationFineStep, value); }
        }

        private int rotationTopShifts = 10;
        public int RotationTopShifts
        {
            get { return rotationTopShifts; }
            set { SetProperty(ref rotationTopShifts, value); }
        }

        private bool enableCreate = true;
        public bool EnableCreate
        {
            get { return enableCreate; }
            set { SetProperty(ref enableCreate, value); }
        }

        private bool enableOptimize = true;
        public bool EnableOptimize
        {
            get { return enableOptimize; }
            set { SetProperty(ref enableOptimize, value); }
        }

        private bool debugDrawGrids = false;
        public bool DebugDrawGrids
        {
            get { return debugDrawGrids; }
            set { SetProperty(ref debugDrawGrids, value); }
        }

        private bool individualTuningStructures = false;
        public bool IndividualTuningStructures
        {
            get { return individualTuningStructures; }
            set { SetProperty(ref individualTuningStructures, value); }
        }

        private bool useManualTransform = false;
        public bool UseManualTransform
        {
            get { return useManualTransform; }
            set { SetProperty(ref useManualTransform, value); }
        }

        private string BodyId;


        private double bodyMargin;
        public double BodyMargin
        {
            get { return bodyMargin; }
            set { SetProperty(ref bodyMargin, value); }
        }

        private double oarMargin;

        public double OarMargin
        {
            get { return oarMargin; }
            set { SetProperty(ref oarMargin, value); }
        }

        private bool couchKick = false;

        public bool CouchKick
        {
            get { return couchKick; }
            set { SetProperty(ref couchKick, value); }
        }

        private bool highRes = false;
        public bool HighRes
        {
            get { return highRes; }
            set { SetProperty(ref highRes, value); }
        }


        public SphereDialogViewModel(EsapiWorker ew = null, SW.Threading.Dispatcher uiDispatcher = null)
        {
            _ew = ew;
            _uiDispatcher = uiDispatcher ?? SW.Threading.Dispatcher.CurrentDispatcher;
            // Apply scalar UI defaults synchronously, before the View sets
            // DataContext, so the first binding pass sees real values. Using the
            // property setters (not the backing fields) means PropertyChanged
            // fires, which the old field-based init skipped — that was why the
            // Body margin showed 0 instead of 15 when ESAPI data arrived late.
            ApplyDefaultUiValues();
            _ = Initialize();
        }

        // Synchronous, dependency-free defaults. Must not touch _model/_ew or the
        // file system; it runs during construction on the UI thread.
        private void ApplyDefaultUiValues()
        {
            VThresh = 95;
            XShift = 0;
            YShift = 0;
            ShiftX = 0;
            ShiftY = 0;
            ShiftZ = 0;
            RotX = 0;
            RotY = 0;
            RotZ = 0;
            UpdateSphereDerivedDefaults();
            Output = " ";

            TargetSelected = -1;
            PtvSelected = -1;
            BodySelected = -1;

            BodyMargin = 15.0;
            OarMargin = 15.0;

            SelectedOar = -1;
            SelectedTableOar = -1;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }

        private double GetMinimumLatticeSpacing()
        {
            return Radius > 0.0f ? Radius * 4.0 : 0.0;
        }

        private double CoerceLatticeSpacing(double value)
        {
            double minimumSpacing = GetMinimumLatticeSpacing();
            if (minimumSpacing > 0.0 && value > 0.0 && value < minimumSpacing)
            {
                return minimumSpacing;
            }

            return value;
        }

        private void UpdateSphereDerivedDefaults(bool resetSpacing = false)
        {
            if (SphereSize == null || SelectedSphereSize < 0 || SelectedSphereSize >= SphereSize.Count)
            {
                return;
            }

            if (!double.TryParse(SphereSize[SelectedSphereSize], NumberStyles.Float, CultureInfo.InvariantCulture, out var sphereDiameterCm) || sphereDiameterCm <= 0.0)
            {
                return;
            }

            Radius = (float)(sphereDiameterCm * 10.0 / 2.0);
            if (resetSpacing || LatticeSpacing <= 0.0)
            {
                LatticeSpacing = sphereDiameterCm * 10.0 * 4.0; // default spacing is 4x diameter in mm
            }
            else if (LatticeSpacing < GetMinimumLatticeSpacing())
            {
                LatticeSpacing = GetMinimumLatticeSpacing();
            }

        }

        private void UpdatePreviewShiftLimits()
        {
            SetPreviewShiftWindow();
        }

        private void SetPreviewShiftWindow()
        {
            double yZPeriod = LatticeSpacing > 0.0 ? LatticeSpacing : (Radius > 0.0f ? Radius * 2.0 : 15.0);
            double xPeriod = yZPeriod * 0.5;
            double xMaxShift = xPeriod * 0.5;
            double yZMaxShift = yZPeriod * 0.5;

            PreviewShiftMinimum = -yZMaxShift;
            PreviewShiftMaximum = yZMaxShift;

            PreviewShiftXMinimum = -xMaxShift;
            PreviewShiftXMaximum = xMaxShift;
            PreviewShiftYMinimum = -yZMaxShift;
            PreviewShiftYMaximum = yZMaxShift;
            PreviewShiftZMinimum = -yZMaxShift;
            PreviewShiftZMaximum = yZMaxShift;

        }

        private void ApplyPreviewPlacement(LatticePlacementResult placement)
        {
            if (placement == null)
            {
                return;
            }

            SetPreviewShiftWindow();
            ShiftX = placement.ShiftX;
            ShiftY = placement.ShiftY;
            ShiftZ = placement.ShiftZ;
            RotX = placement.RotX;
            RotY = placement.RotY;
            RotZ = placement.RotZ;
        }

        private bool IsBodyLikeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            return id.Equals(BodyId, StringComparison.OrdinalIgnoreCase) ||
                   id.IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   id.IndexOf("external", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private List<OarEntry> GetSelectedOarsForPlanning()
        {
            if (SelectedOars == null) return new List<OarEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<OarEntry>();
            foreach (var o in SelectedOars)
            {
                if (o == null || string.IsNullOrWhiteSpace(o.Name)) continue;
                if (IsBodyLikeId(o.Name)) continue;
                if (!seen.Add(o.Name)) continue;
                result.Add(o);
            }
            return result;
        }

        private async Task Initialize()
        {
            try
            {
                _model = new Model(_ew);
                await _model.InitializeModel();
                await LoadEsapiDataAsync();
            }
            catch (Exception ex)
            {
                // Initialize() is intentionally fire-and-forget from the ctor, so
                // anything that escapes here would be an unobserved task exception
                // that silently leaves the VM half-initialized (empty structure
                // lists, broken "Add OAR", stale defaults). Surface it instead.
                Helpers.SeriLog.LogError("ViewModel initialization failed", ex);
                _uiDispatcher.Invoke(() =>
                {
                    StatusMessage = "Initialization failed: " + ex.Message;
                    MessageBox.Show(
                        "SFRT Planning could not finish loading plan data:\n\n" + ex.Message +
                        "\n\nDefault values are shown and some structure lists may be empty.",
                        "Initialization error", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            }
        }

        private async Task LoadEsapiDataAsync()
        {
            (OarStructures, AllStructures, TargetStructures, BodyId) = await _model.FetchStructures();
            BodySelected = AllStructures.IndexOf(BodyId);

            // Patient + plan labels shown in the header pills.
            try
            {
                var ctx = await _model.FetchPlanContext();
                PatientLabel = !string.IsNullOrWhiteSpace(ctx.patientId) ? $"Patient · {ctx.patientId}" : "Patient";
                PlanLabel = !string.IsNullOrWhiteSpace(ctx.planId) ? $"Plan · {ctx.planId}" : "Plan";
            }
            catch { /* leave defaults */ }

            // Load machines.config using helper search
            _machineConfig = MachineConfig.LoadFromDefaults();

            // Populate MachineIds and default energies from config if available
            if (_machineConfig != null && _machineConfig.Machines != null && _machineConfig.Machines.Count > 0)
            {
                var idsLog = string.Join(",", _machineConfig.Machines.Select(m => m.MachineId ?? m.DisplayName ?? "(unknown)"));
                Helpers.SeriLog.LogInfo($"Machine config loaded with {_machineConfig.Machines.Count} machines: {idsLog}");

                // ESAPI cannot enumerate commissioned machines, so probe each configured
                // machine by creating and removing a throwaway beam; only machines the
                // current institution can actually treat on stay in the dropdown.
                StatusMessage = "Checking machine availability...";
                int configuredCount = _machineConfig.Machines.Count;
                try
                {
                    var candidates = _machineConfig.Machines
                        .Select(m => (MachineId: m.MachineId ?? m.DisplayName ?? "(unknown)",
                                      Energy: (m.Energies_MV != null && m.Energies_MV.Count > 0) ? m.Energies_MV[0] : "6X"))
                        .ToList();
                    var availableIds = await _model.ProbeAvailableMachines(candidates);

                    if (availableIds.Count > 0)
                    {
                        _machineConfig.Machines = _machineConfig.Machines
                            .Where(m => availableIds.Contains(m.MachineId ?? m.DisplayName ?? "(unknown)"))
                            .ToList();
                        StatusMessage = $"{availableIds.Count} of {configuredCount} configured machines available for this patient.";
                    }
                    else
                    {
                        // Probe could not validate anything (e.g. uneditable plan); show the
                        // full configured list rather than an empty dropdown.
                        Helpers.SeriLog.LogWarning("Machine probe found no available machines; showing all configured machines.");
                        StatusMessage = "Machine availability could not be verified; showing all configured machines.";
                    }
                }
                catch (Exception ex)
                {
                    Helpers.SeriLog.LogError("Machine availability probe failed; showing all configured machines.", ex);
                    StatusMessage = "Machine availability could not be verified; showing all configured machines.";
                }

                _uiDispatcher.Invoke(() =>
                {
                    MachineIds.Clear();
                    MachineDisplayNames.Clear();
                    foreach (var m in _machineConfig.Machines)
                    {
                        var mid = m.MachineId ?? m.DisplayName ?? "(unknown)";
                        var disp = string.IsNullOrEmpty(m.DisplayName) ? mid : m.DisplayName;
                        MachineIds.Add(mid);
                        MachineDisplayNames.Add(disp);
                    }
                    SelectedMachineId = 0;
                });
                UpdateEnergiesFromSelectedMachine();
            }
            else
            {
                Helpers.SeriLog.LogWarning($"No machines.config found or it contains no machines; using fallback defaults.");

                var _machineIds = new List<string>
                {
                    "ACUnit1TB",
                    "ACUnit2TB",
                    "ACUnit3TB",
                    "ACUnit4TB"
                };
                _uiDispatcher.Invoke(() =>
                {
                    MachineIds.Clear();
                    MachineDisplayNames.Clear();
                    foreach (var id in _machineIds)
                    {
                        MachineIds.Add(id);
                        MachineDisplayNames.Add(id);
                    }
                });

                var _energyList = new List<string>
                {
                    "6X",
                    "10X",
                    "15X",
                    "6X-FFF",
                    "10X-FFF"
                };
                _uiDispatcher.Invoke(() =>
                {
                    EnergyList.Clear();
                    foreach (var energy in _energyList)
                    {
                        EnergyList.Add(energy);
                    }
                });
                SelectedMachineId = 0;

            }

            // Populate preset templates from configs/templates.config (enabled entries only).
            // Missing/unparsable config falls back to the built-in presets; a config that
            // disables everything is respected and leaves the dropdown empty.
            var templateConfig = TemplateConfig.LoadFromDefaults();
            List<string> presetNames;
            if (templateConfig?.Templates != null)
            {
                presetNames = templateConfig.Templates
                    .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Name))
                    .Select(t => t.Name)
                    .ToList();
                Helpers.SeriLog.LogInfo($"templates.config: {presetNames.Count} of {templateConfig.Templates.Count} templates enabled: {string.Join(",", presetNames)}");
            }
            else
            {
                Helpers.SeriLog.LogWarning("No templates.config found; falling back to WashU and Mayo presets.");
                presetNames = new List<string> { "WashU", "Mayo" };
            }

            _uiDispatcher.Invoke(() =>
            {
                PresetTemplates.Clear();
                foreach (var name in presetNames)
                {
                    PresetTemplates.Add(name);
                }
                if (PresetTemplates.Count > 0)
                {
                    SelectedPresetTemplate = 0;
                }
            });
        }

        private void UpdateEnergiesFromSelectedMachine()
        {
            try
            {
                var energiesToSet = new List<string>();

                if (_machineConfig == null || _machineConfig.Machines == null || _machineConfig.Machines.Count == 0)
                {
                    Helpers.SeriLog.LogWarning("UpdateEnergiesFromSelectedMachine called but no machine config available");
                    return;
                }

                var idx = SelectedMachineId;
                var unit = _machineConfig.Machines[idx];
                if (unit.Energies_MV != null && unit.Energies_MV.Count > 0)
                {
                    energiesToSet = unit.Energies_MV.ToList();
                    Helpers.SeriLog.LogInfo($"Loaded energies for machineId={unit.MachineId ?? unit.DisplayName}: {string.Join(",", energiesToSet)}");
                }
                else
                {
                    energiesToSet.Add("6X");
                    Helpers.SeriLog.LogWarning($"No Energies_MV defined for machineId={unit.MachineId ?? unit.DisplayName}; using fallback 6X");
                }

                _uiDispatcher.Invoke(() =>
                {
                    EnergyList.Clear();
                    foreach (var energy in energiesToSet)
                        EnergyList.Add(energy);
                    SelectedEnergy = 0;
                });
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError("Exception in UpdateEnergiesFromSelectedMachine", ex);
            }
        }

        private bool PreSpheres()
        {
            UpdateSphereDerivedDefaults();
            // Check vol thresh for spheres
            if (VThresh > 100 || VThresh < 0)
            {
                MessageBox.Show("Volume threshold must be between 0 and 100");
                return false;
            }

            // Check target
            if (targetSelected == -1)
            {
                MessageBox.Show("Must have target selected, cancelling operation.");
                return false;
            }

            if (Radius <= 0)
            {
                MessageBox.Show("Radius must be greater than zero.");
                return false;
            }

            if (LatticeSpacing <= 0)
            {
                MessageBox.Show("Lattice spacing must be greater than zero.");
                return false;
            }

            double minimumLatticeSpacing = GetMinimumLatticeSpacing();
            if (LatticeSpacing < minimumLatticeSpacing)
            {
                LatticeSpacing = minimumLatticeSpacing;
                MessageBox.Show($"Lattice spacing must be at least {minimumLatticeSpacing / 10.0:F2} cm (2 x sphere diameter) to leave room for valley spheres.");
                return false;
            }

            // Check that "BODY" structure exists
            if (bodySelected == -1)
            {
                MessageBox.Show("Please select a body structure.");
                return false;
            }

            return true;
        }

        public async Task<SpherePreviewData> GeneratePreviewData(bool allowAfterCreation = false)
        {
            if (latticeCreatedInSession && !allowAfterCreation)
            {
                return new SpherePreviewData
                {
                    IsValid = false,
                    Message = "Live preview is paused after structure creation. Use Refine Preview to update from the current shift and rotation."
                };
            }

            if (IsBusy)
            {
                return new SpherePreviewData
                {
                    IsValid = false,
                    Message = "Preview paused while another operation is running."
                };
            }

            if (!PreSpheres())
            {
                return new SpherePreviewData
                {
                    IsValid = false,
                    Message = "Preview cancelled due to invalid inputs."
                };
            }

            var previewParams = new LatticeParameters()
            {
                Radius = Radius,
                XShift = ShiftX,
                YShift = ShiftY,
                ZShift = ShiftZ,
                RotX = RotX,
                RotY = RotY,
                RotZ = RotZ,
                UseManualTransform = UseManualTransform,
                VThresh = VThresh,
                BodyId = BodyId,
                TargetStructure = TargetStructures[TargetSelected],
                PTVLowStructure = TargetStructures[PtvSelected],
                OarStructures = GetSelectedOarsForPlanning(),
                BodyMargin = BodyMargin,
                OarMargin = OarMargin,
                CouchKick = CouchKick,
                Energy = EnergyList[SelectedEnergy],
                MachineId = MachineIds[SelectedMachineId],
                SphereSize = SphereSize[SelectedSphereSize],
                LatticeSpacing = LatticeSpacing,
                HighRes = HighRes,
                EnableRotation = EnableRotation,
                UsePCA = UsePCA,
                PcaZOnly = PcaZOnly,
                RotationCoarseRange = RotationCoarseRange,
                RotationCoarseStep = RotationCoarseStep,
                RotationFineRange = RotationFineRange,
                RotationFineStep = RotationFineStep,
                RotationTopShifts = RotationTopShifts,
                DebugDrawGrids = DebugDrawGrids,
                IndividualTuningStructures = IndividualTuningStructures
            };

            if (!await _esapiOperationGate.WaitAsync(0))
            {
                return new SpherePreviewData
                {
                    IsValid = false,
                    Message = "Preview skipped while ESAPI is busy."
                };
            }

            Output += "Generating 3D preview...\n";
            try
            {
                bool useExistingCreatedCore = allowAfterCreation;
                var preview = await _model.BuildSpherePreview(previewParams, 350, ShiftX, ShiftY, ShiftZ, RotX, RotY, RotZ, false, false, useExistingCreatedCore);
                Output += preview.IsValid ? "3D preview ready.\n" : $"3D preview failed: {preview.Message}\n";
                // Update hot-count if available
                if (preview != null && preview.IsValid)
                {
                    HotSphereCount = preview.TotalSphereCount;
                }
                return preview;
            }
            finally
            {
                _esapiOperationGate.Release();
            }
        }

        public async Task<SpherePreviewData> RefinePreview(CancellationToken cancellationToken = default, IProgress<string> progress = null)
        {
            if (!PreSpheres())
            {
                return new SpherePreviewData
                {
                    IsValid = false,
                    Message = "Preview cancelled due to invalid inputs."
                };
            }

            var latticeParams = new LatticeParameters()
            {
                Radius = Radius,
                XShift = ShiftX,
                YShift = ShiftY,
                ZShift = ShiftZ,
                RotX = RotX,
                RotY = RotY,
                RotZ = RotZ,
                UseManualTransform = UseManualTransform,
                VThresh = VThresh,
                BodyId = BodyId,
                TargetStructure = TargetStructures[TargetSelected],
                PTVLowStructure = TargetStructures[PtvSelected],
                OarStructures = GetSelectedOarsForPlanning(),
                BodyMargin = BodyMargin,
                OarMargin = OarMargin,
                CouchKick = CouchKick,
                Energy = EnergyList[SelectedEnergy],
                MachineId = MachineIds[SelectedMachineId],
                SphereSize = SphereSize[SelectedSphereSize],
                LatticeSpacing = LatticeSpacing,
                HighRes = HighRes,
                EnableRotation = EnableRotation,
                UsePCA = UsePCA,
                PcaZOnly = PcaZOnly,
                RotationCoarseRange = RotationCoarseRange,
                RotationCoarseStep = RotationCoarseStep,
                RotationFineRange = RotationFineRange,
                RotationFineStep = RotationFineStep,
                RotationTopShifts = RotationTopShifts,
                DebugDrawGrids = DebugDrawGrids,
                IndividualTuningStructures = IndividualTuningStructures
            };

            EnableCreate = false;
            IsBusy = true;
            bool gateAcquired = false;
            try
            {
                await _esapiOperationGate.WaitAsync(cancellationToken);
                gateAcquired = true;
                bool runFineSearch = !latticeCreatedInSession && !UseManualTransform;
                StatusMessage = runFineSearch ? "Generating exact fine-search preview..." : "Generating exact current-shift preview...";
                var externalProgress = progress;
                progress = new Progress<string>(m =>
                {
                    Output += m + "\n";
                    var clean = m?.Trim();
                    if (!string.IsNullOrEmpty(clean))
                    {
                        StatusMessage = clean;
                    }
                    externalProgress?.Report(m);
                });

                cancellationToken.ThrowIfCancellationRequested();

                Output += runFineSearch ? "Generating exact fine-search preview...\n" : "Generating exact preview with current shifts and rotations...\n";
                var exactPreview = await _model.BuildSpherePreview(latticeParams, 350, ShiftX, ShiftY, ShiftZ, RotX, RotY, RotZ, true, runFineSearch, latticeCreatedInSession);
                cancellationToken.ThrowIfCancellationRequested();
                if (exactPreview == null || !exactPreview.IsValid)
                {
                    Output += "Exact preview failed: " + (exactPreview?.Message ?? "Unknown") + "\n";
                    StatusMessage = "Exact preview failed.";
                    return exactPreview ?? new SpherePreviewData
                    {
                        IsValid = false,
                        Message = "Exact preview failed."
                    };
                }
                HotSphereCount = exactPreview.TotalSphereCount;

                cancellationToken.ThrowIfCancellationRequested();

                Output += runFineSearch ? "Refined preview ready. Adjust sliders if needed, then click Create to write structures.\n" : "Current-shift preview ready.\n";
                StatusMessage = runFineSearch ? "Refined preview ready. Click Create when ready." : "Current-shift preview ready.";
                return exactPreview;
            }
            finally
            {
                EnableCreate = true;
                IsBusy = false;
                if (gateAcquired)
                {
                    _esapiOperationGate.Release();
                }
            }
        }

        public async Task<bool> CreateLattice()
        {

            if (!PreSpheres())
            {
                return false;
            }
            latticeParams = new LatticeParameters()
            {
                Radius = Radius,
                XShift = ShiftX,
                YShift = ShiftY,
                ZShift = ShiftZ,
                RotX = RotX,
                RotY = RotY,
                RotZ = RotZ,
                UseManualTransform = UseManualTransform,
                VThresh = VThresh,
                BodyId = BodyId,
                TargetStructure = TargetStructures[TargetSelected],
                PTVLowStructure = TargetStructures[PtvSelected],
                OarStructures = GetSelectedOarsForPlanning(),
                BodyMargin = BodyMargin,
                OarMargin = OarMargin,
                CouchKick = CouchKick,
                Energy = EnergyList[SelectedEnergy],
                MachineId = MachineIds[SelectedMachineId],
                SphereSize = SphereSize[SelectedSphereSize],
                LatticeSpacing = LatticeSpacing,
                HighRes = HighRes
                ,
                EnableRotation = EnableRotation
                ,
                UsePCA = UsePCA
                ,
                PcaZOnly = PcaZOnly
                ,
                RotationCoarseRange = RotationCoarseRange
                ,
                RotationCoarseStep = RotationCoarseStep
                ,
                RotationFineRange = RotationFineRange
                ,
                RotationFineStep = RotationFineStep
                ,
                RotationTopShifts = RotationTopShifts
                ,
                DebugDrawGrids = DebugDrawGrids
                ,
                IndividualTuningStructures = IndividualTuningStructures
            };

            EnableCreate = false;
            IsBusy = true;
            bool gateAcquired = false;
            try
            {
                await _esapiOperationGate.WaitAsync();
                gateAcquired = true;
                StatusMessage = "Lattice creation starting...";

                var progress = new Progress<string>(message =>
                {
                    Output += message + "\n";
                    var clean = message?.Trim();
                    if (!string.IsNullOrEmpty(clean))
                    {
                        StatusMessage = clean;
                    }
                });
                Output += "Lattice creation in progress... This might take a few minutes.";
                await _model.BuildSpheres(latticeParams, true, true, progress);
                ApplyPreviewPlacement(_model.LastLatticePlacement);
                StatusMessage = "Setting up beams...";
                Output += "Lattice creation complete. Setting up beams...\n";
                await _model.SetupBeams(latticeParams);
                Output += "Beams setup complete. Review and proceed to optimization.\n";
                StatusMessage = "Beams setup complete. Review and proceed to optimization.";
                latticeCreatedInSession = true;
                MessageBox.Show("Script execution complete.");
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = "Structure creation failed.";
                Output += $"Structure creation failed: {ex.Message}\n";
                Helpers.SeriLog.LogError("Structure creation failed", ex);
                MessageBox.Show($"Structure creation failed:\n{ex.Message}");
                return false;
            }
            finally
            {
                IsBusy = false;
                EnableCreate = true;
                if (gateAcquired)
                {
                    _esapiOperationGate.Release();
                }
            }
        }

        public async Task<bool> Optimize()
        {
            if (!PreSpheres())
            {
                return false;
            }

            try
            {
                latticeParams = new LatticeParameters()
                {
                    Radius = Radius,
                    XShift = ShiftX,
                    YShift = ShiftY,
                    ZShift = ShiftZ,
                    RotX = RotX,
                    RotY = RotY,
                    RotZ = RotZ,
                    UseManualTransform = UseManualTransform,
                    VThresh = VThresh,
                    BodyId = BodyId,
                    TargetStructure = TargetStructures[TargetSelected],
                    PTVLowStructure = TargetStructures[PtvSelected],
                    OarStructures = GetSelectedOarsForPlanning(),
                    BodyMargin = BodyMargin,
                    OarMargin = OarMargin,
                    CouchKick = CouchKick,
                    Energy = EnergyList[SelectedEnergy],
                    MachineId = MachineIds[SelectedMachineId],
                    SphereSize = SphereSize[SelectedSphereSize],
                    LatticeSpacing = LatticeSpacing,
                    HighRes = HighRes
                    ,
                    EnableRotation = EnableRotation
                    ,
                    UsePCA = UsePCA
                    ,
                    PcaZOnly = PcaZOnly
                    ,
                    RotationCoarseRange = RotationCoarseRange
                    ,
                    RotationCoarseStep = RotationCoarseStep
                    ,
                    RotationFineRange = RotationFineRange
                    ,
                    RotationFineStep = RotationFineStep
                    ,
                    RotationTopShifts = RotationTopShifts
                    ,
                    DebugDrawGrids = DebugDrawGrids
                    ,
                    IndividualTuningStructures = IndividualTuningStructures
                };

                EnableOptimize = false;
                IsBusy = true;
                StatusMessage = "Setting optimization objectives...";
                var progress = new Progress<string>(message =>
                {
                    Output += message + "\n";
                    var clean = message?.Trim();
                    if (!string.IsNullOrEmpty(clean))
                    {
                        StatusMessage = clean;
                    }
                });
                if (!_model.HasOptimizationParameters)
                {
                    _model.LoadDefaultOptimizationParameters();
                }
                Output += "Starting optimization...\n";
                await _model.OptimizeLattice(progress, latticeParams);
                Output += "Optimization objectives set.\n";
                StatusMessage = "Optimization objectives set.";
                return true;
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError("Optimization failed", ex);
                Output += $"Optimization failed: {ex.Message}\n";
                StatusMessage = "Optimization failed.";
                MessageBox.Show($"Optimization failed:\n{ex.Message}");
                return false;
            }
            finally
            {
                IsBusy = false;
                EnableOptimize = true;
            }
        }

        public void AddRois(IEnumerable<string> rois)
        {
            if (rois == null) return;
            var list = rois.Where(r => !string.IsNullOrEmpty(r)).ToList();
            foreach (var r in list)
            {
                if (OarStructures.Contains(r) &&
                    !SelectedOars.Any(o => string.Equals(o.Name, r, StringComparison.OrdinalIgnoreCase)))
                {
                    // Each new OAR row captures the picker's current margin
                    // value, which itself persists as the last-used default.
                    SelectedOars.Add(new OarEntry(r, OarMargin));
                    OarStructures.Remove(r);
                }
            }
            SelectedTableOar = -1;
        }

        public void AddRoi()
        {
            if (SelectedOar != -1 && SelectedOar < OarStructures.Count)
            {
                AddRois(new[] { OarStructures[SelectedOar] });
                SelectedOar = -1;
            }
        }
        public void RemoveRoi()
        {
            // Remove selected roi from list
            if (SelectedTableOar != -1 && SelectedTableOar < SelectedOars.Count)
            {
                var entry = SelectedOars[SelectedTableOar];
                if (entry?.Name != null) OarStructures.Add(entry.Name);
                SelectedOars.RemoveAt(SelectedTableOar);
                SelectedTableOar--;
            }
        }

        public void RemoveRoi(OarEntry entry)
        {
            if (entry == null) return;
            if (!string.IsNullOrEmpty(entry.Name)) OarStructures.Add(entry.Name);
            SelectedOars.Remove(entry);
            SelectedTableOar = -1;
        }

    }
}
