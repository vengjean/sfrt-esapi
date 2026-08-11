using ESAPIScript;
using Prism.Mvvm;
using SFRT_PlanningScript.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VMS.TPS.Common.Model;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using static SFRT_PlanningScript.SphereDialogViewModel;


namespace SFRT_PlanningScript
{
    public class SpherePreviewData
    {
        public bool IsValid { get; set; }
        public string Message { get; set; }
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinY { get; set; }
        public double MaxY { get; set; }
        public double MinZ { get; set; }
        public double MaxZ { get; set; }
        public double SphereRadiusMm { get; set; }
        public int TotalSphereCount { get; set; }
        public int DisplayedSphereCount { get; set; }
        public List<double> TargetVertices { get; set; } = new List<double>();
        public List<int> TargetTriangleIndices { get; set; } = new List<int>();
        public List<PreviewStructureMesh> OarMeshes { get; set; } = new List<PreviewStructureMesh>();
        public List<VVector> SphereCenters { get; set; } = new List<VVector>();
    }

    public class PreviewStructureMesh
    {
        public string Id { get; set; }
        public List<double> Vertices { get; set; } = new List<double>();
        public List<int> TriangleIndices { get; set; } = new List<int>();
    }

    public class LatticePlacementResult
    {
        public double ShiftX { get; set; }
        public double ShiftY { get; set; }
        public double ShiftZ { get; set; }
        public double RotX { get; set; }
        public double RotY { get; set; }
        public double RotZ { get; set; }
        public double TotalRotX { get; set; }
        public double TotalRotY { get; set; }
        public double TotalRotZ { get; set; }
    }

    public class Model
    {
        private double mask_spacing = 1.0;
        private double resolution = 1.0;
        // Origin of the last-generated target mask (used when mask was generated in a rotated frame)
        private double mask_min_x = 0.0;
        private double mask_min_y = 0.0;
        private double mask_min_z = 0.0;
        private bool mask_origin_assigned = false;
        private EsapiWorker _ew;

        bool HighRes = false;
        private double Radius;
        private double LatticeSpacing;

        private double bodyMargin;

        private string BodyId;

        private OptimizationSetup Optimizer;

        private double OarMargin;

        private StructureSet _ss;

        public LatticePlacementResult LastLatticePlacement { get; private set; }

        public Model(EsapiWorker ew)
        {
            _ew = ew;
        }

        private static bool IsBodyLikeStructure(Structure structure, string selectedBodyId = null)
        {
            if (structure == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(selectedBodyId) && structure.Id.Equals(selectedBodyId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(structure.DicomType) &&
                (structure.DicomType.Equals("EXTERNAL", StringComparison.OrdinalIgnoreCase) ||
                 structure.DicomType.Equals("BODY", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return structure.Id.IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   structure.Id.IndexOf("external", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private List<string> FilterSelectedOars(IEnumerable<string> selectedOars)
        {
            if (selectedOars == null)
            {
                return new List<string>();
            }

            return selectedOars
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(id =>
                {
                    var structure = _ss?.Structures.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    return structure != null && !IsBodyLikeStructure(structure, BodyId);
                })
                .ToList();
        }

        private List<OarEntry> FilterSelectedOarEntries(IEnumerable<OarEntry> selectedOars)
        {
            if (selectedOars == null)
            {
                return new List<OarEntry>();
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<OarEntry>();
            foreach (var entry in selectedOars)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Name)) continue;
                if (!seen.Add(entry.Name)) continue;

                var structure = _ss?.Structures.FirstOrDefault(s => s.Id.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
                if (structure == null) continue;
                if (IsBodyLikeStructure(structure, BodyId)) continue;
                result.Add(entry);
            }
            return result;
        }

        public async Task<bool> InitializeModel()
        {
            await _ew.AsyncRunPlanContext((pat, ps) =>
            {
                pat.BeginModifications();
                _ss = ps.StructureSet;
            });
            return true;
        }

        public async Task<(string patientId, string patientName, string planId)> FetchPlanContext()
        {
            string patientId = "";
            string patientName = "";
            string planId = "";
            await _ew.AsyncRunPlanContext((pat, ps) =>
            {
                if (pat != null)
                {
                    patientId = pat.Id ?? "";
                    patientName = pat.Name ?? "";
                }
                if (ps != null)
                {
                    planId = ps.Id ?? "";
                }
            });
            return (patientId, patientName, planId);
        }

        public async Task<(ObservableCollection<string>, ObservableCollection<string>, List<string>, string)> FetchStructures()
        {
            ObservableCollection<string> oarStructures = new ObservableCollection<string>();
            ObservableCollection<string> allStructures = new ObservableCollection<string>();
            List<string> targetStructures = new List<string>();
            List<string> sfrtStructures = new List<string>() { "TS_", "zzz_" };
            List<string> skippable = new List<string>() { "PTV", "GTV", "CTV" };
            string defaultBodyId = "";

            await _ew.AsyncRunPlanContext((pat, ps) =>
            {
                var ss = ps.StructureSet;

                foreach (var i in ss.Structures)
                {
                    allStructures.Add(i.Id);

                    if (IsBodyLikeStructure(i))
                    {
                        continue;
                    }

                    if (sfrtStructures.Any(x => i.Id.Contains(x)))
                    {
                        continue;
                    }

                    // if dicom type is not PTV, GTV, CTV, or a target structure made in a previous run, add to OARs, else to targets
                    if (i.DicomType != "PTV" && i.DicomType != "GTV" && i.DicomType != "CTV" && !skippable.Any(x => i.Id.Contains(x)))
                    {
                        oarStructures.Add(i.Id);
                    }
                    else
                    {
                        targetStructures.Add(i.Id);
                    }
                }

                defaultBodyId = ss.Structures
                    .FirstOrDefault(x => IsBodyLikeStructure(x))?.Id ?? "";
            });

            oarStructures = new ObservableCollection<string>(
                oarStructures.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            return (oarStructures, allStructures, targetStructures, defaultBodyId);
        }

        private Structure CreateStructure(StructureSet structureSet, string structName, bool showMessage, bool makeHiRes, string structType = "PTV")
        {
            string msg = $"New structure ({structName}) created.";
            var prevStruct = structureSet.Structures.FirstOrDefault(x => x.Id == structName);
            if (prevStruct != null)
            {
                structureSet.RemoveStructure(prevStruct);
                msg += " Old structure overwritten.";
            }

            var structure = structureSet.AddStructure(structType, structName);

            // TEMPORARY -> Need to bring it back to highres

            if (makeHiRes)
            {
                structure.ConvertToHighResolution();
                msg += " Converted to Hi-Res";
            }

            if (showMessage) { MessageBox.Show(msg); }
            return structure;
        }

        private void RemoveCombinedTuningShells(StructureSet structureSet)
        {
            var combinedTuningShells = structureSet.Structures
                .Where(x => x.Id.Equals("TS_Peak_Ring_Inner", StringComparison.OrdinalIgnoreCase) ||
                            x.Id.Equals("TS_Peak_Ring_Middle", StringComparison.OrdinalIgnoreCase) ||
                            x.Id.Equals("TS_Peak_Ring_Outer", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var shell in combinedTuningShells)
            {
                structureSet.RemoveStructure(shell);
            }
        }

        private void AddContoursToMain(StructureSet structureSet, ref Structure PrimaryStructure, ref Structure SecondaryStructure)
        {
            if (PrimaryStructure == null || SecondaryStructure == null)
            {
                return;
            }
            // Promote the destination to HighRes if the source is HighRes so the
            // per-image-plane copy below doesn't throw on a resolution mismatch.
            if (SecondaryStructure.IsHighResolution && !PrimaryStructure.IsHighResolution)
            {
                try { PrimaryStructure.ConvertToHighResolution(); }
                catch { /* fall through; the per-plane copy may still succeed */ }
            }
            // Loop through each image plane
            for (int z = 0; z < structureSet.Image.ZSize; ++z)
            {
                var contours = SecondaryStructure.GetContoursOnImagePlane(z);
                foreach (var seg in contours)
                {
                    try { PrimaryStructure.AddContourOnImagePlane(seg, z); }
                    catch { /* skip planes that fail */ }
                }
            }
        }

        private void BuildSphere(Structure parentStruct, VVector centre, double r)//, Structure secondStructure = null)
        {
            double z_centre = centre.z;
            double min_z = z_centre - r;
            // Find the closest slice number to the minimum z value
            int min_z_idx = (int)Math.Floor((min_z - _ss.Image.Origin.z) / _ss.Image.ZRes);
            // Find the closest slice number to the maximum z value
            int max_z_idx = (int)Math.Ceiling((z_centre + r - _ss.Image.Origin.z) / _ss.Image.ZRes);

            // Make sure they are above 0 and below the max number of slices
            min_z_idx = Math.Max(min_z_idx, 0);
            max_z_idx = Math.Min(max_z_idx, _ss.Image.ZSize);
            if (min_z_idx == 0 || max_z_idx == _ss.Image.ZSize)
            {
                MessageBox.Show("Sphere is out of image bounds - ERROR");
            }

            for (int z = min_z_idx; z < max_z_idx; ++z)
            {
                double zCoord = z * _ss.Image.ZRes + _ss.Image.Origin.z;

                // For each slice find in plane radius
                var z_diff = Math.Abs(zCoord - centre.z);
                if (z_diff > r) // If we are out of range of the sphere continue
                {
                    continue;
                }

                // Otherwise make spheres
                var r_z = Math.Sqrt(Math.Pow(r, 2.0) - Math.Pow(z_diff, 2.0));
                var contour = CreateContour(centre, r_z, 15);
                parentStruct.AddContourOnImagePlane(contour, z);
            }
        }

        private List<double> Arange(double start, double stop, double step)
        {
            //log.Debug($"Arange with start stop step = {start} {stop} {step}\n");
            var retval = new List<double>();
            var currentval = start;
            while (currentval < stop)
            {
                retval.Add(currentval);
                currentval += step;
            }
            return retval;
        }

        private List<VVector> BuildGrid(List<double> xcoords, List<double> ycoords, List<double> zcoords)
        {
            var retval = new List<VVector>();
            foreach (var x in xcoords)
            {
                foreach (var y in ycoords)
                {
                    foreach (var z in zcoords)
                    {
                        var pt = new VVector(x, y, z);

                        retval.Add(pt);
                    }
                }
            }

            return retval;
        }

        private List<VVector> BuildHexGrid(double Xstart, double Xsize, double Ystart, double Ysize, double Zstart, double Zsize)
        {
            double A = LatticeSpacing;
            var retval = new List<VVector>();

            void CreateLayer(double zCoord, double x0, double y0)
            {
                // create planar hexagonal sphere packing grid
                var yeven = Arange(y0, y0 + Ysize, A);
                var xeven = Arange(x0, x0 + Xsize, A);
                foreach (var y in yeven)
                {
                    foreach (var x in xeven)
                    {
                        retval.Add(new VVector(x, y, zCoord));
                        retval.Add(new VVector(x + (A / 2.0), y + (A / 2.0), zCoord));
                    }
                }
            }

            foreach (var z in Arange(Zstart, Zstart + Zsize, A))
            {
                CreateLayer(z, Xstart, Ystart);
                CreateLayer(z + (A / 2.0), Xstart + (A / 2.0), Ystart);

            }

            return retval;
        }

        private List<VVector> CheckGridInContour(List<VVector> grid, Structure target)
        {
            List<VVector> correctGrid = new List<VVector>();
            for (int i = 0; i < grid.Count; i++)
            {
                if (target.IsPointInsideSegment(grid[i]))
                {
                    correctGrid.Add(grid[i]);
                }
            }
            return correctGrid;
        }

        private class ShiftCandidate
        {
            public int Index { get; set; }
            public VVector Shift { get; set; }
        }

        private VVector GetShiftPeriods()
        {
            if (LatticeSpacing <= 0.0)
            {
                double fallback = Radius > 0.0 ? Radius * 2.0 : 1.0;
                return new VVector(fallback, fallback, fallback);
            }

            return new VVector(LatticeSpacing * 0.5, LatticeSpacing, LatticeSpacing);
        }

        private static double ToCanonicalPhase(double value, double period)
        {
            if (period <= 0.0)
            {
                return value;
            }

            double phase = value % period;
            if (phase < 0.0)
            {
                phase += period;
            }
            return phase;
        }

        private static double ToSignedPhase(double value, double period)
        {
            if (period <= 0.0)
            {
                return value;
            }

            double phase = ToCanonicalPhase(value, period);
            if (phase > period * 0.5)
            {
                phase -= period;
            }
            return phase;
        }

        private VVector ToCanonicalShift(VVector signedShift)
        {
            var periods = GetShiftPeriods();
            return new VVector(
                ToCanonicalPhase(signedShift.x, periods.x),
                ToCanonicalPhase(signedShift.y, periods.y),
                ToCanonicalPhase(signedShift.z, periods.z));
        }

        private VVector ToSignedShift(VVector canonicalShift)
        {
            var periods = GetShiftPeriods();
            return new VVector(
                ToSignedPhase(canonicalShift.x, periods.x),
                ToSignedPhase(canonicalShift.y, periods.y),
                ToSignedPhase(canonicalShift.z, periods.z));
        }

        private List<ShiftCandidate> BuildShiftCandidates()
        {
            var candidates = new List<ShiftCandidate>();
            if (LatticeSpacing <= 0.0 || resolution <= 0.0)
            {
                candidates.Add(new ShiftCandidate { Index = 0, Shift = new VVector(0, 0, 0) });
                return candidates;
            }

            int idx = 0;
            const double epsilon = 1e-9;
            var periods = GetShiftPeriods();

            for (double z_shift = 0.0; z_shift < periods.z - epsilon; z_shift += resolution)
            {
                for (double y_shift = 0.0; y_shift < periods.y - epsilon; y_shift += resolution)
                {
                    for (double x_shift = 0.0; x_shift < periods.x - epsilon; x_shift += resolution)
                    {
                        candidates.Add(new ShiftCandidate
                        {
                            Index = idx++,
                            Shift = new VVector(x_shift, y_shift, z_shift)
                        });
                    }
                }
            }

            if (candidates.Count == 0)
            {
                candidates.Add(new ShiftCandidate { Index = 0, Shift = new VVector(0, 0, 0) });
            }

            return candidates;
        }

        private VVector ClampSignedShiftToBounds(VVector shift)
        {
            var periods = GetShiftPeriods();
            double xMax = periods.x * 0.5;
            double yMax = periods.y * 0.5;
            double zMax = periods.z * 0.5;
            return new VVector(
                Math.Max(-xMax, Math.Min(xMax, shift.x)),
                Math.Max(-yMax, Math.Min(yMax, shift.y)),
                Math.Max(-zMax, Math.Min(zMax, shift.z)));
        }

        private List<ShiftCandidate> BuildLocalShiftCandidates(VVector centreShift, double maxOffsetMm)
        {
            var candidates = new List<ShiftCandidate>();
            double step = resolution > 0.0 ? resolution : 1.0;
            double maxOffset = Math.Max(0.0, maxOffsetMm);
            int idx = 0;

            for (double dz = -maxOffset; dz <= maxOffset + 1e-9; dz += step)
            {
                for (double dy = -maxOffset; dy <= maxOffset + 1e-9; dy += step)
                {
                    for (double dx = -maxOffset; dx <= maxOffset + 1e-9; dx += step)
                    {
                        var signedShift = ClampSignedShiftToBounds(new VVector(centreShift.x + dx, centreShift.y + dy, centreShift.z + dz));
                        candidates.Add(new ShiftCandidate
                        {
                            Index = idx++,
                            Shift = ToCanonicalShift(signedShift)
                        });
                    }
                }
            }

            if (candidates.Count == 0)
            {
                candidates.Add(new ShiftCandidate { Index = 0, Shift = ToCanonicalShift(ClampSignedShiftToBounds(centreShift)) });
            }

            return candidates;
        }

        private VVector index_to_shift(int idx)
        {
            return index_to_shift(idx, BuildShiftCandidates());
        }

        private VVector index_to_shift(int idx, List<ShiftCandidate> shiftCandidates)
        {
            if (shiftCandidates == null || shiftCandidates.Count == 0)
            {
                return new VVector(0, 0, 0);
            }

            var candidate = shiftCandidates.FirstOrDefault(x => x.Index == idx);
            return candidate != null ? candidate.Shift : shiftCandidates[0].Shift;
        }

        private List<VVector> TranslateGrid(List<VVector> grid, VVector shift)
        {
            var retval = new List<VVector>(grid.Count);
            for (int i = 0; i < grid.Count; i++)
            {
                var p = grid[i];
                retval.Add(new VVector(p.x + shift.x, p.y + shift.y, p.z + shift.z));
            }
            return retval;
        }

        private List<VVector> CheckColdGrid(List<VVector> cold_grid, List<VVector> grid)
        {
            List<VVector> correctGrid = new List<VVector>();
            double max_distance = Math.Sqrt(3 * Math.Pow(0.5 * LatticeSpacing, 2)) + Radius * 0.5;
            for (int i = 0; i < cold_grid.Count; i++)
            {
                for (int j = 0; j < grid.Count; j++)
                {
                    if (VVector.Distance(grid[j], cold_grid[i]) < max_distance)
                    {
                        correctGrid.Add(cold_grid[i]);
                        break;
                    }
                }
                // Find at least one point within grid that is within 2*spacing of cold grid point
            }
            return correctGrid;
        }

        private List<VVector> CheckTemplateGrid(List<VVector> grid_template, List<VVector> grid)
        {
            List<VVector> correctGrid = new List<VVector>();
            double max_distance = Math.Sqrt(3 * Math.Pow(0.5 * LatticeSpacing, 2)) + Radius * 0.5;
            for (int i = 0; i < grid_template.Count; i++)
            {
                // Check if grid_template[i] is contained in grid
                if (grid.Contains(grid_template[i]))
                {
                    continue;
                }
                for (int j = 0; j < grid.Count; j++)
                {
                    if (VVector.Distance(grid[j], grid_template[i]) < max_distance)
                    {
                        correctGrid.Add(grid_template[i]);
                        break;
                    }
                }
                // Find at least one point within grid that is within 2*spacing of cold grid point
            }
            return correctGrid;
        }

        public void CreatePrv(List<OarEntry> SelectedOars)
        {
            CreatePrvStructure(SelectedOars, "zzz_SFRT_PRV", "zzz_temp_prv");
        }

        private Structure CreatePrvStructure(List<OarEntry> SelectedOars, string prvId, string tempPrvId)
        {
            var body = _ss.Structures.FirstOrDefault(x => x.Id == BodyId);
            if (body == null || !body.HasSegment)
            {
                return null;
            }

            // Match the PRV structure's resolution to the body. After production lattice
            // generation, the body or some OARs may have been promoted to HighRes; if the PRV
            // is created as low-res "Control" and we then try to subtract a HighRes OAR,
            // ESAPI throws.
            CreateStructure(_ss, prvId, false, body.IsHighResolution, "Control");
            var prv = _ss.Structures.FirstOrDefault(x => x.Id == prvId);
            if (prv == null)
            {
                return null;
            }
            // crop body_margin mm from body
            try
            {
                prv.SegmentVolume = body.SegmentVolume.Margin(-bodyMargin);
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogWarning($"PRV body-margin failed for '{prvId}': {ex.Message}");
                return null;
            }

            // Loop through SelectedOars and subtract contours to the new structure with margin.
            // Each OAR carries its own margin so consecutive related organs can use different
            // standoffs without re-typing a global default.
            foreach (var oarEntry in FilterSelectedOarEntries(SelectedOars))
            {
                var oar_contour = _ss.Structures.FirstOrDefault(x => x.Id == oarEntry.Name);
                // Check if oar_contour has contours
                if (oar_contour == null || !oar_contour.HasSegment)
                {
                    continue;
                }

                double margin = oarEntry.Margin;

                try
                {
                    if (oar_contour.IsHighResolution)
                    {
                        // Match temp_prv resolution to the OAR being copied so AddContourOnImagePlane
                        // does not throw "resolution mismatch" on a HighRes OAR.
                        CreateStructure(_ss, tempPrvId, false, true, "Control");
                        var temp_prv = _ss.Structures.FirstOrDefault(x => x.Id == tempPrvId);
                        if (temp_prv == null) continue;
                        try
                        {
                            AddContoursToMain(_ss, ref temp_prv, ref oar_contour);
                            prv.SegmentVolume = prv.SegmentVolume.Sub(temp_prv.SegmentVolume.Margin(margin));
                        }
                        finally
                        {
                            try { _ss.RemoveStructure(temp_prv); } catch { /* ignore */ }
                        }
                    }
                    else
                    {
                        prv.SegmentVolume = prv.SegmentVolume.Sub(oar_contour.SegmentVolume.Margin(margin));
                    }
                }
                catch (Exception ex)
                {
                    Helpers.SeriLog.LogWarning($"Skipping OAR '{oarEntry.Name}' in PRV build: {ex.Message}");
                }
            }

            // do prv = body - prv
            try
            {
                prv.SegmentVolume = body.SegmentVolume.Sub(prv.SegmentVolume);
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogWarning($"PRV body-subtract failed for '{prvId}': {ex.Message}");
            }
            return prv;
        }

        private bool[,,] GenerateTargetMask(Structure target)
        {
            return GenerateTargetMask(target, 0.0, new VVector(0, 0, 0));
        }

        // 2D rotation about Z axis only
        private bool[,,] GenerateTargetMask(Structure target, double angleDeg, VVector centre)
        {
            var bounds = target.MeshGeometry.Bounds;
            double min_x = bounds.X;
            double max_x = bounds.X + bounds.SizeX;

            double min_y = bounds.Y;
            double max_y = bounds.Y + bounds.SizeY;

            double min_z = bounds.Z;
            double max_z = bounds.Z + bounds.SizeZ;

            // If rotated mask requested, compute rotated bounds by rotating the 8 corners
            if (Math.Abs(angleDeg) > 1e-6)
            {
                double angle = angleDeg * Math.PI / 180.0;
                double c = Math.Cos(angle);
                double s = Math.Sin(angle);

                var corners = new List<VVector>();
                double[] xs = { min_x, max_x };
                double[] ys = { min_y, max_y };
                double[] zs = { min_z, max_z };
                foreach (var x in xs)
                {
                    foreach (var y in ys)
                    {
                        foreach (var z in zs)
                        {
                            // rotate point (x,y) about centre
                            double dx = x - centre.x;
                            double dy = y - centre.y;
                            double rx = dx * c - dy * s + centre.x;
                            double ry = dx * s + dy * c + centre.y;
                            corners.Add(new VVector(rx, ry, z));
                        }
                    }
                }
                min_x = corners.Min(p => p.x);
                max_x = corners.Max(p => p.x);
                min_y = corners.Min(p => p.y);
                max_y = corners.Max(p => p.y);
                min_z = corners.Min(p => p.z);
                max_z = corners.Max(p => p.z);
            }

            // Create a 3d boolean array spanning the target bounding box
            int num_voxels_x = Convert.ToInt32(Math.Abs(max_x - min_x) / mask_spacing) + 1;
            int num_voxels_y = Convert.ToInt32(Math.Abs(max_y - min_y) / mask_spacing) + 1;
            int num_voxels_z = Convert.ToInt32(Math.Abs(max_z - min_z) / mask_spacing) + 1;

            bool[,,] target_mask = new bool[num_voxels_x, num_voxels_y, num_voxels_z];

            double angleInv = -angleDeg * Math.PI / 180.0;
            double cInv = Math.Cos(angleInv);
            double sInv = Math.Sin(angleInv);

            for (int i = 0; i < num_voxels_x; i++)
            {
                for (int j = 0; j < num_voxels_y; j++)
                {
                    for (int k = 0; k < num_voxels_z; k++)
                    {
                        var sample = new VVector(min_x + i * mask_spacing, min_y + j * mask_spacing, min_z + k * mask_spacing);
                        // If rotation requested, transform sample back to original image coords
                        VVector testPt = sample;
                        if (Math.Abs(angleDeg) > 1e-6)
                        {
                            double dx = sample.x - centre.x;
                            double dy = sample.y - centre.y;
                            double ux = dx * cInv - dy * sInv + centre.x;
                            double uy = dx * sInv + dy * cInv + centre.y;
                            testPt = new VVector(ux, uy, sample.z);
                        }
                        target_mask[i, j, k] = target.IsPointInsideSegment(testPt);
                    }
                }
            }
            // record mask origin for callers (so rotated masks can be indexed correctly)
            mask_min_x = min_x;
            mask_min_y = min_y;
            mask_min_z = min_z;
            mask_origin_assigned = true;

            return target_mask;
        }

        // 3D rotation about all axes
        private bool[,,] GenerateTargetMask(Structure target, double rotXdeg, double rotYdeg, double rotZdeg, VVector centre)
        {
            var bounds = target.MeshGeometry.Bounds;
            double min_x = bounds.X;
            double max_x = bounds.X + bounds.SizeX;

            double min_y = bounds.Y;
            double max_y = bounds.Y + bounds.SizeY;

            double min_z = bounds.Z;
            double max_z = bounds.Z + bounds.SizeZ;

            // If rotated mask requested, compute rotated bounds by rotating the 8 corners
            bool rotated = Math.Abs(rotXdeg) > 1e-6 || Math.Abs(rotYdeg) > 1e-6 || Math.Abs(rotZdeg) > 1e-6;
            if (rotated)
            {
                var corners = new List<VVector>();
                double[] xs = { min_x, max_x };
                double[] ys = { min_y, max_y };
                double[] zs = { min_z, max_z };
                foreach (var x in xs)
                {
                    foreach (var y in ys)
                    {
                        foreach (var z in zs)
                        {
                            var p = new VVector(x, y, z);
                            // apply rotations X then Y then Z about centre
                            var r = RotatePointAroundX(p, centre, rotXdeg);
                            r = RotatePointAroundY(r, centre, rotYdeg);
                            r = RotatePointAroundZ(r, centre, rotZdeg);
                            corners.Add(r);
                        }
                    }
                }
                min_x = corners.Min(p => p.x);
                max_x = corners.Max(p => p.x);
                min_y = corners.Min(p => p.y);
                max_y = corners.Max(p => p.y);
                min_z = corners.Min(p => p.z);
                max_z = corners.Max(p => p.z);
            }

            // Create a 3d boolean array spanning the target bounding box
            int num_voxels_x = Convert.ToInt32(Math.Abs(max_x - min_x) / mask_spacing) + 1;
            int num_voxels_y = Convert.ToInt32(Math.Abs(max_y - min_y) / mask_spacing) + 1;
            int num_voxels_z = Convert.ToInt32(Math.Abs(max_z - min_z) / mask_spacing) + 1;

            bool[,,] target_mask = new bool[num_voxels_x, num_voxels_y, num_voxels_z];

            // For each sample, transform sample back to original image coords by applying inverse rotations
            double angleXInv = -rotXdeg;
            double angleYInv = -rotYdeg;
            double angleZInv = -rotZdeg;

            for (int i = 0; i < num_voxels_x; i++)
            {
                for (int j = 0; j < num_voxels_y; j++)
                {
                    for (int k = 0; k < num_voxels_z; k++)
                    {
                        var sample = new VVector(min_x + i * mask_spacing, min_y + j * mask_spacing, min_z + k * mask_spacing);
                        VVector testPt = sample;
                        if (rotated)
                        {
                            // apply inverse rotations in reverse order: Z_inv, Y_inv, X_inv
                            testPt = RotatePointAroundZ(testPt, centre, angleZInv);
                            testPt = RotatePointAroundY(testPt, centre, angleYInv);
                            testPt = RotatePointAroundX(testPt, centre, angleXInv);
                        }
                        target_mask[i, j, k] = target.IsPointInsideSegment(testPt);
                    }
                }
            }

            // record mask origin for callers (so rotated masks can be indexed correctly)
            mask_min_x = min_x;
            mask_min_y = min_y;
            mask_min_z = min_z;
            mask_origin_assigned = true;

            return target_mask;
        }

        private VVector RotatePointAroundX(VVector p, VVector centre, double angleDeg)
        {
            double angle = angleDeg * Math.PI / 180.0;
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);
            double dy = p.y - centre.y;
            double dz = p.z - centre.z;
            double ry = dy * c - dz * s + centre.y;
            double rz = dy * s + dz * c + centre.z;
            return new VVector(p.x, ry, rz);
        }

        private VVector RotatePointAroundY(VVector p, VVector centre, double angleDeg)
        {
            double angle = angleDeg * Math.PI / 180.0;
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);
            double dx = p.x - centre.x;
            double dz = p.z - centre.z;
            double rx = dx * c + dz * s + centre.x;
            double rz = -dx * s + dz * c + centre.z;
            return new VVector(rx, p.y, rz);
        }

        private VVector RotatePointAroundZ(VVector p, VVector centre, double angleDeg)
        {
            double angle = angleDeg * Math.PI / 180.0;
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);
            double dx = p.x - centre.x;
            double dy = p.y - centre.y;
            double rx = dx * c - dy * s + centre.x;
            double ry = dx * s + dy * c + centre.y;
            return new VVector(rx, ry, p.z);
        }

        private List<VVector> CheckGridInMask(List<VVector> grid, bool[,,] mask, Structure target)
        {
            var bounds = target.MeshGeometry.Bounds;
            double min_x = bounds.X - bounds.SizeX;
            double max_x = bounds.X + 2 * bounds.SizeX;
            double min_y = bounds.Y - bounds.SizeY;
            double max_y = bounds.Y + 2 * bounds.SizeY;
            double min_z = bounds.Z - bounds.SizeZ;
            double max_z = bounds.Z + 2 * bounds.SizeZ;
            // Spacing is 1 mm

            List<VVector> correctGrid = new List<VVector>();
            foreach (var pt in grid)
            {
                if (pt.x < min_x || pt.x > max_x || pt.y < min_y || pt.y > max_y || pt.z < min_z || pt.z > max_z)
                {
                    continue;
                }
                int x_idx = Convert.ToInt32(Math.Floor(pt.x - min_x));
                int y_idx = Convert.ToInt32(Math.Floor(pt.y - min_y));
                int z_idx = Convert.ToInt32(Math.Floor(pt.z - min_z));

                if (mask[x_idx, y_idx, z_idx])
                {
                    correctGrid.Add(pt);
                }
            }
            return correctGrid;
        }

        private List<int> SearchGrid(List<VVector> grid, bool[,,] mask, double min_x, double min_y, double min_z, out int max_count, int topK = 1, List<ShiftCandidate> shiftCandidates = null)
        {
            if (shiftCandidates == null)
            {
                shiftCandidates = BuildShiftCandidates();
            }

            int[] shift_count = new int[shiftCandidates.Count];
            System.Threading.Tasks.Parallel.For(0, shiftCandidates.Count, shift_idx =>
            {
                var count = 0;
                var shift = shiftCandidates[shift_idx].Shift;
                for (int i = 0; i < grid.Count; i++)
                {
                    var p = grid[i];
                    double sx = p.x + shift.x;
                    double sy = p.y + shift.y;
                    double sz = p.z + shift.z;

                    int x_idx = Convert.ToInt32(Math.Floor((sx - min_x) / mask_spacing));
                    int y_idx = Convert.ToInt32(Math.Floor((sy - min_y) / mask_spacing));
                    int z_idx = Convert.ToInt32(Math.Floor((sz - min_z) / mask_spacing));

                    if (x_idx >= 0 && x_idx < mask.GetLength(0) &&
                        y_idx >= 0 && y_idx < mask.GetLength(1) &&
                        z_idx >= 0 && z_idx < mask.GetLength(2) &&
                        mask[x_idx, y_idx, z_idx])
                    {
                        count++;
                    }
                }

                shift_count[shift_idx] = count;
            });

            max_count = shift_count.Length == 0 ? 0 : shift_count.Max();
            return Enumerable.Range(0, shift_count.Length)
                .OrderByDescending(i => shift_count[i])
                .ThenBy(i => i)
                .Take(Math.Max(1, topK))
                .Select(i => shiftCandidates[i].Index)
                .ToList();
        }

        private double ComputePCAAngle(bool[,,] mask, Structure target)
        {
            var bounds = target.MeshGeometry.Bounds;
            double min_x = bounds.X;
            double min_y = bounds.Y;

            long count = 0;
            double sum_x = 0, sum_y = 0;

            int nx = mask.GetLength(0);
            int ny = mask.GetLength(1);
            int nz = mask.GetLength(2);

            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    for (int k = 0; k < nz; k++)
                    {
                        if (!mask[i, j, k]) continue;
                        double x = min_x + i * mask_spacing;
                        double y = min_y + j * mask_spacing;
                        sum_x += x;
                        sum_y += y;
                        count++;
                    }
                }
            }

            if (count == 0) return 0.0;

            double mean_x = sum_x / count;
            double mean_y = sum_y / count;

            double sxx = 0, syy = 0, sxy = 0;
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    for (int k = 0; k < nz; k++)
                    {
                        if (!mask[i, j, k]) continue;
                        double x = min_x + i * mask_spacing;
                        double y = min_y + j * mask_spacing;
                        double dx = x - mean_x;
                        double dy = y - mean_y;
                        sxx += dx * dx;
                        syy += dy * dy;
                        sxy += dx * dy;
                    }
                }
            }

            // covariance matrix [sxx sxy; sxy syy]
            // principal eigenvector angle = 0.5 * atan2(2*sxy, sxx - syy)
            double angle = 0.5 * Math.Atan2(2.0 * sxy, sxx - syy);
            // convert to degrees
            return angle * 180.0 / Math.PI;
        }

        private (double angleXY, double angleXZ, double angleYZ) ComputePCAAngles(bool[,,] mask, Structure target)
        {
            var bounds = target.MeshGeometry.Bounds;
            double min_x = bounds.X;
            double min_y = bounds.Y;
            double min_z = bounds.Z;

            long count = 0;
            double sum_x = 0, sum_y = 0, sum_z = 0;

            int nx = mask.GetLength(0);
            int ny = mask.GetLength(1);
            int nz = mask.GetLength(2);

            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    for (int k = 0; k < nz; k++)
                    {
                        if (!mask[i, j, k]) continue;
                        double x = min_x + i * mask_spacing;
                        double y = min_y + j * mask_spacing;
                        double z = min_z + k * mask_spacing;
                        sum_x += x;
                        sum_y += y;
                        sum_z += z;
                        count++;
                    }
                }
            }

            if (count == 0) return (0.0, 0.0, 0.0);

            double mean_x = sum_x / count;
            double mean_y = sum_y / count;
            double mean_z = sum_z / count;

            // build covariance matrix
            double sxx = 0, syy = 0, szz = 0, sxy = 0, sxz = 0, syz = 0;
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    for (int k = 0; k < nz; k++)
                    {
                        if (!mask[i, j, k]) continue;
                        double x = min_x + i * mask_spacing;
                        double y = min_y + j * mask_spacing;
                        double z = min_z + k * mask_spacing;
                        double dx = x - mean_x;
                        double dy = y - mean_y;
                        double dz = z - mean_z;
                        sxx += dx * dx;
                        syy += dy * dy;
                        szz += dz * dz;
                        sxy += dx * dy;
                        sxz += dx * dz;
                        syz += dy * dz;
                    }
                }
            }

            double[,] C = new double[3, 3]
            {
                { sxx, sxy, sxz },
                { sxy, syy, syz },
                { sxz, syz, szz }
            };

            // Jacobi eigen-decomposition for symmetric 3x3
            JacobiEigenDecomposition(C, out double[] evals, out double[,] evecs);

            // pick principal eigenvector (largest eigenvalue)
            int maxi = 0;
            for (int i = 1; i < 3; i++) if (evals[i] > evals[maxi]) maxi = i;
            double vx = evecs[0, maxi];
            double vy = evecs[1, maxi];
            double vz = evecs[2, maxi];

            // Use absolute components to remove eigenvector sign ambiguity and
            // restrict reported rotation to the first quadrant [0,90).
            double avx = Math.Abs(vx);
            double avy = Math.Abs(vy);
            double avz = Math.Abs(vz);

            double angleXY = 0.0;
            double angleXZ = 0.0;
            double angleYZ = 0.0;
            if (avx > 1e-12 || avy > 1e-12)
            {
                angleXY = Math.Atan2(avy, avx) * 180.0 / Math.PI;
            }
            if (avx > 1e-12 || avz > 1e-12)
            {
                angleXZ = Math.Atan2(avz, avx) * 180.0 / Math.PI;
            }
            if (avy > 1e-12 || avz > 1e-12)
            {
                angleYZ = Math.Atan2(avz, avy) * 180.0 / Math.PI;
            }
            return (angleXY, angleXZ, angleYZ);
        }

        // Jacobi method for symmetric 3x3 eigen-decomposition
        private void JacobiEigenDecomposition(double[,] a, out double[] evals, out double[,] evecs)
        {
            evals = new double[3];
            evecs = new double[3, 3];
            // Copy
            double[,] v = new double[3, 3];
            double[,] d = new double[3, 3];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    d[i, j] = a[i, j];
                    v[i, j] = (i == j) ? 1.0 : 0.0;
                }
            }

            const int maxIter = 50;
            for (int iter = 0; iter < maxIter; iter++)
            {
                // find largest off-diagonal
                int p = 0, q = 1;
                double max = Math.Abs(d[0, 1]);
                if (Math.Abs(d[0, 2]) > max) { p = 0; q = 2; max = Math.Abs(d[0, 2]); }
                if (Math.Abs(d[1, 2]) > max) { p = 1; q = 2; max = Math.Abs(d[1, 2]); }
                if (max < 1e-12) break;

                double dp = d[p, p];
                double dq = d[q, q];
                double apq = d[p, q];
                double phi = 0.5 * Math.Atan2(2.0 * apq, dq - dp);
                double c = Math.Cos(phi);
                double s = Math.Sin(phi);

                // rotate
                for (int i = 0; i < 3; i++)
                {
                    double dip = d[i, p];
                    double diq = d[i, q];
                    d[i, p] = c * dip - s * diq;
                    d[p, i] = d[i, p];
                    d[i, q] = s * dip + c * diq;
                    d[q, i] = d[i, q];
                }

                // update diagonal
                double new_pp = c * c * dp - 2.0 * s * c * apq + s * s * dq;
                double new_qq = s * s * dp + 2.0 * s * c * apq + c * c * dq;
                d[p, p] = new_pp;
                d[q, q] = new_qq;
                d[p, q] = d[q, p] = 0.0;

                // update eigenvectors
                for (int i = 0; i < 3; i++)
                {
                    double vip = v[i, p];
                    double viq = v[i, q];
                    v[i, p] = c * vip - s * viq;
                    v[i, q] = s * vip + c * viq;
                }
            }

            for (int i = 0; i < 3; i++)
            {
                evals[i] = d[i, i];
                for (int j = 0; j < 3; j++) evecs[j, i] = v[j, i];
            }
        }

        private List<VVector> RotateGrid(List<VVector> grid, VVector centre, double angleDeg)
        {
            double angle = angleDeg * Math.PI / 180.0;
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);
            var retval = new List<VVector>(grid.Count);
            foreach (var p in grid)
            {
                double dx = p.x - centre.x;
                double dy = p.y - centre.y;
                double rx = dx * c - dy * s + centre.x;
                double ry = dx * s + dy * c + centre.y;
                retval.Add(new VVector(rx, ry, p.z));
            }
            return retval;
        }

        private List<VVector> RotateGridAroundX(List<VVector> grid, VVector centre, double angleDeg)
        {
            double angle = angleDeg * Math.PI / 180.0;
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);
            var retval = new List<VVector>(grid.Count);
            foreach (var p in grid)
            {
                double dy = p.y - centre.y;
                double dz = p.z - centre.z;
                double ry = dy * c - dz * s + centre.y;
                double rz = dy * s + dz * c + centre.z;
                retval.Add(new VVector(p.x, ry, rz));
            }
            return retval;
        }

        private List<VVector> RotateGridAroundY(List<VVector> grid, VVector centre, double angleDeg)
        {
            double angle = angleDeg * Math.PI / 180.0;
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);
            var retval = new List<VVector>(grid.Count);
            foreach (var p in grid)
            {
                double dx = p.x - centre.x;
                double dz = p.z - centre.z;
                double rx = dx * c + dz * s + centre.x;
                double rz = -dx * s + dz * c + centre.z;
                retval.Add(new VVector(rx, p.y, rz));
            }
            return retval;
        }

        public async Task BuildSpheres(LatticeParameters latticeParams, bool makeIndividual, bool alignGrid, IProgress<string> progress = null)
        {
            LastLatticePlacement = null;

            await _ew.AsyncRunPlanContext((pat, ps) =>
            {

                StructureSet structureSet = ps.StructureSet;
                _ss = structureSet;

                BodyId = latticeParams.BodyId;
                Radius = float.Parse(latticeParams.SphereSize, NumberStyles.Float, CultureInfo.InvariantCulture) * 10.0f / 2.0f;
                bodyMargin = latticeParams.BodyMargin;
                OarMargin = latticeParams.OarMargin;
                LatticeSpacing = latticeParams.LatticeSpacing;
                HighRes = latticeParams.HighRes;
                var selectedOarEntries = FilterSelectedOarEntries(latticeParams.OarStructures);
                var selectedOars = selectedOarEntries.Select(e => e.Name).ToList();

                // Start timer
                var sw = new Stopwatch();
                sw.Start();

                // Total lattice structure with all spheres
                Structure structMain = null;
                Structure structMain_cold = null;

                var target_name = latticeParams.TargetStructure;
                var target_initial = structureSet.Structures.Where(x => x.Id == target_name).First();
                Structure target = null;
                bool deleteAutoTarget = false;
                Structure target_initial_temp = null;

                var ptv_low_name = latticeParams.PTVLowStructure;
                var ptv_low = structureSet.Structures.FirstOrDefault(x => x.Id == ptv_low_name);
                progress?.Report("\nPreparing structures ...");

                CreateStructure(structureSet, "zzz_GTV_core", false, false);
                target = structureSet.Structures.FirstOrDefault(x => x.Id == "zzz_GTV_core");
                AddContoursToMain(structureSet, ref target, ref target_initial);
                if (target == null)
                {
                    return;
                }

                target.SegmentVolume = target.SegmentVolume.Margin(-5 - Radius);

                CreatePrv(selectedOarEntries);
                var prv = structureSet.Structures.FirstOrDefault(x => x.Id == "zzz_SFRT_PRV");

                target.SegmentVolume = target.SegmentVolume.Sub(prv);


                Structure eval_ptv = null;
                CreateStructure(structureSet, "zzz_EvalPTV", false, false);
                eval_ptv = structureSet.Structures.FirstOrDefault(x => x.Id == "zzz_EvalPTV");
                AddContoursToMain(structureSet, ref eval_ptv, ref ptv_low);
                // eval_ptv.SegmentVolume = eval_ptv.SegmentVolume.Sub(prv);

                foreach (var oar in selectedOars)
                {
                    progress?.Report($"\nAccounting for OAR structure: {oar}");
                    var oar_contour = structureSet.Structures.FirstOrDefault(x => x.Id == oar);
                    // Check if oar_contour has contours
                    if (!oar_contour.HasSegment)
                    {
                        continue;
                    }

                    if (oar_contour.IsHighResolution)
                    {
                        CreateStructure(structureSet, "zzz_temp_oar", false, false, "Control");
                        var temp_oar = structureSet.Structures.FirstOrDefault(x => x.Id == "zzz_temp_oar");
                        AddContoursToMain(structureSet, ref temp_oar, ref oar_contour);
                        eval_ptv.SegmentVolume = eval_ptv.SegmentVolume.Sub(temp_oar);
                        structureSet.RemoveStructure(temp_oar);
                    }
                    else
                    {
                        eval_ptv.SegmentVolume = eval_ptv.SegmentVolume.Sub(oar_contour);
                    }
                }

                if (HighRes)
                {
                    eval_ptv.ConvertToHighResolution();
                }


                // Generate a regular grid accross the dummy bounding box 
                var bounds = target.MeshGeometry.Bounds;

                // If alignGrid calculate z to snap to
                double z0 = bounds.Z;
                double zf = bounds.Z + bounds.SizeZ;
                if (alignGrid)
                {
                    // Snap z to nearest z slice
                    // where z slices = img.origin.z + (c * zres)
                    // x, y, z --> dropdown all equal
                    // z0 --> rounded to nearest grid slice
                    var zSlices = new List<double>();
                    var plane_idx = (bounds.Z - structureSet.Image.Origin.z) / structureSet.Image.ZRes;
                    int plane_int = (int)Math.Round(plane_idx);

                    z0 = structureSet.Image.Origin.z + (plane_int * structureSet.Image.ZRes);
                }
                sw.Stop();
                progress?.Report($"\nTime to prepare structures: {sw.ElapsedMilliseconds} ms");
                sw.Reset();
                sw.Start();
                progress?.Report("\nGenerating template lattice grid ...");

                // Get points that are not in the image
                List<VVector> grid = null;
                List<VVector> cold_grid = null;

                // We give extra padding of the hex grid to make sure that we account for spheres when we shift the grid
                var xmin = bounds.X - LatticeSpacing * 1.5;
                var ymin = bounds.Y - LatticeSpacing * 1.5;
                var zmin = bounds.Z - LatticeSpacing * 1.5;
                var xsize = bounds.SizeX + LatticeSpacing * 3.1;
                var ysize = bounds.SizeY + LatticeSpacing * 3.1;
                var zsize = bounds.SizeZ + LatticeSpacing * 3.1;
                // grid = BuildHexGrid(bounds.X + XShift, bounds.SizeX, bounds.Y + YShift, bounds.SizeY, z0, bounds.SizeZ);
                grid = BuildHexGrid(xmin, xsize, ymin, ysize, zmin, zsize);
                structMain = CreateStructure(structureSet, "PTV_Peak", false, HighRes);
                // cold_grid = BuildHexGrid(bounds.X + XShift - LatticeSpacing / 2, bounds.SizeX + LatticeSpacing / 2, bounds.Y + YShift, bounds.SizeY, z0, bounds.SizeZ);

                // put even more padding on the cold grid
                xmin -= LatticeSpacing * 1;
                ymin -= LatticeSpacing * 1;
                zmin -= LatticeSpacing * 1;
                xsize += LatticeSpacing * 2.1;
                ysize += LatticeSpacing * 2.1;
                zsize += LatticeSpacing * 2.1;
                cold_grid = BuildHexGrid(xmin - LatticeSpacing / 2, xsize + LatticeSpacing / 2, ymin, ysize, zmin, zsize);
                structMain_cold = CreateStructure(structureSet, "PTV_Valley", false, HighRes, "Control");

                int max_num_shifts = Convert.ToInt32(Math.Round(LatticeSpacing * 0.5 * LatticeSpacing * LatticeSpacing / resolution));

                double optimal_avg_dist = 999999999999999;

                var target_centroid = target.CenterPoint;

                sw.Stop();
                progress?.Report($"\nTime to generate grid: {sw.ElapsedMilliseconds} ms");
                sw.Reset();

                sw.Start();
                progress?.Report("\nGenerating target mask ...");

                bool[,,] target_mask = GenerateTargetMask(target);

                // List<VVector> full_search_grid = new List<VVector>();

                sw.Stop();
                progress?.Report($"\nTime to generate target mask: {sw.ElapsedMilliseconds} ms");
                // MessageBox.Show("Ready to search for optimal shift, Press OK to continue.");
                sw.Reset();
                sw.Start();
                progress?.Report("\nSearching for optimal rotation + shift ...");

                // Keep copies of the unrotated grids
                var grid_base = grid.ToList();
                var cold_base = cold_grid.ToList();
                // Preserve strict unrotated templates so we can always evaluate a no-rotation candidate.
                var grid_unrot = grid.ToList();
                var cold_unrot = cold_grid.ToList();

                // Compute PCA-based initial angles (degrees) from the initially-generated mask
                var pcaAngles = ComputePCAAngles(target_mask, target); // (angleXY, angleXZ, angleYZ)
                // Report PCA seed angles so user sees the orientation used to seed grid generation
                progress?.Report($"\nPCA seed angles (deg) XY,Z:{pcaAngles.angleXY:F2} XZ,Y:{pcaAngles.angleXZ:F2} YZ,X:{pcaAngles.angleYZ:F2}");
                // If UsePCA is enabled, build the base grids rotated by the PCA angles so
                // the lattice is generated on that orientation. When pre-rotated we
                // centre subsequent sweeps around 0 (offsets from the PCA baseline).
                // PCA seeding is tied to the rotation search: when the rotation toggle is
                // off, no PCA is applied either. PcaZOnly restricts the seed to the
                // in-plane (Z) angle so the lattice never tilts about X/Y.
                bool pcaApplied = latticeParams.UsePCA && latticeParams.EnableRotation && !latticeParams.UseManualTransform;
                double baselineRotZ = 0.0, baselineRotY = 0.0, baselineRotX = 0.0;
                if (pcaApplied)
                {
                    baselineRotZ = pcaAngles.angleXY;
                    if (!latticeParams.PcaZOnly)
                    {
                        baselineRotY = pcaAngles.angleXZ; // rotation about Y aligns XZ plane
                        baselineRotX = pcaAngles.angleYZ; // rotation about X aligns YZ plane
                    }

                    // Apply 3D rotation: X, then Y, then Z about the target centroid
                    grid_base = RotateGridAroundX(grid_base, target_centroid, baselineRotX);
                    grid_base = RotateGridAroundY(grid_base, target_centroid, baselineRotY);
                    grid_base = RotateGrid(grid_base, target_centroid, baselineRotZ);

                    cold_base = RotateGridAroundX(cold_base, target_centroid, baselineRotX);
                    cold_base = RotateGridAroundY(cold_base, target_centroid, baselineRotY);
                    cold_base = RotateGrid(cold_base, target_centroid, baselineRotZ);

                    progress?.Report("\nUsing PCA baseline rotation with fixed patient-coordinate target mask.");
                }
                // Respect the user's toggle: if UsePCA was false we centre sweeps around 0 degrees,
                // otherwise sweeps are offsets from the PCA-baseline so centre at 0.
                double centreAngle = 0.0;
                bool useManualTransform = latticeParams.UseManualTransform;
                var manualSignedShift = ClampSignedShiftToBounds(new VVector(latticeParams.XShift, latticeParams.YShift, latticeParams.ZShift));
                var manualShift = ToCanonicalShift(manualSignedShift);

                // Optional debug: draw small sphere markers of grid points for visual inspection
                void DrawDebugSpheres(string name, List<VVector> pts, double sphRadius, int maxPoints = 200)
                {
                    if (!latticeParams.DebugDrawGrids) return;
                    CreateStructure(structureSet, name, false, false, "Control");
                    var dbgStruct = structureSet.Structures.FirstOrDefault(x => x.Id == name);
                    if (dbgStruct == null) return;
                    int drawn = 0;
                    int stride = Math.Max(1, pts.Count / maxPoints);
                    for (int i = 0; i < pts.Count; i += stride)
                    {
                        if (drawn++ >= maxPoints) break;
                        BuildSphere(dbgStruct, pts[i], (float)Math.Min(sphRadius, 2.0));
                    }
                }

                // Draw original template grid (unrotated) and the PCA-baseline grid (grid_base)
                if (latticeParams.DebugDrawGrids)
                {
                    try
                    {
                        DrawDebugSpheres("DBG_Grid_Original", grid, Math.Max(0.5, Radius * 0.25), 300);
                        DrawDebugSpheres("DBG_Grid_PCA", grid_base, Math.Max(0.5, Radius * 0.25), 300);
                    }
                    catch (Exception ex)
                    {
                        progress?.Report($"\nDebug draw error: {ex.Message}");
                    }
                }

                // Coarse-to-fine search parameters (from latticeParams)
                double coarseRange = latticeParams.EnableRotation ? latticeParams.RotationCoarseRange : 0.0;
                double coarseStep = latticeParams.EnableRotation ? latticeParams.RotationCoarseStep : 360.0; // if rotation disabled, skip sweep
                double fineRange = latticeParams.EnableRotation ? latticeParams.RotationFineRange : 0.0;
                double fineStep = latticeParams.EnableRotation ? latticeParams.RotationFineStep : 360.0;

                double bestAngle = 0.0;
                int bestShiftIdx = 0;
                bool usedUnrotatedCandidate = false;
                var shiftCandidates = BuildShiftCandidates();

                void EvaluateExactShiftCandidates(List<VVector> candidateGrid, List<int> candidateShiftIndexes, int maskMaxCount, out int bestShift, out double bestAvg, out int bestCount)
                {
                    bestShift = candidateShiftIndexes.FirstOrDefault();
                    bestAvg = double.MaxValue;
                    bestCount = -1;

                    foreach (var idx in candidateShiftIndexes.Distinct())
                    {
                        var shift = index_to_shift(idx, shiftCandidates);
                        var grid_shifted = TranslateGrid(candidateGrid, shift);
                        var grid_in_target = CheckGridInContour(grid_shifted, target);
                        int hotCountExact = grid_in_target.Count;
                        if (hotCountExact == 0)
                        {
                            continue;
                        }

                        double avgDistExact = 0.0;
                        foreach (var pt in grid_in_target)
                        {
                            avgDistExact += VVector.Distance(pt, target_centroid);
                        }
                        avgDistExact /= hotCountExact;

                        if (hotCountExact > bestCount || (hotCountExact == bestCount && avgDistExact < bestAvg))
                        {
                            bestCount = hotCountExact;
                            bestAvg = avgDistExact;
                            bestShift = idx;
                        }
                    }

                    if (bestCount < 0)
                    {
                        bestCount = maskMaxCount;
                        bestAvg = double.MaxValue;
                    }
                }

                // Helper to evaluate a given rotated grid and return best shift idx, avg dist, and count
                int angleEvaluations = 0;
                void EvalAngle(double angleDeg, ref int outBestShift, ref double outBestAvg, ref int outBestCount)
                {
                    angleEvaluations++;
                    if (pcaApplied)
                    {
                        progress?.Report($"\nScoring rotation offset {angleDeg:F1} degrees from PCA baseline (absolute Z {baselineRotZ + angleDeg:F1} degrees) ...");
                    }
                    else
                    {
                        progress?.Report($"\nScoring rotation angle {angleDeg:F1} degrees ...");
                    }
                    var rot_grid = RotateGrid(grid_base, target_centroid, angleDeg);

                    // Get candidate shifts from fast mask-based search, but evaluate them by actual point-in-structure test
                    int angleMaxCount = 0;
                    double searchMinX = mask_origin_assigned ? mask_min_x : target.MeshGeometry.Bounds.X;
                    double searchMinY = mask_origin_assigned ? mask_min_y : target.MeshGeometry.Bounds.Y;
                    double searchMinZ = mask_origin_assigned ? mask_min_z : target.MeshGeometry.Bounds.Z;
                    var candidate_shifts = System.Threading.Tasks.Task.Run(() =>
                    {
                        int maskCount;
                        var shifts = SearchGrid(rot_grid, target_mask, searchMinX, searchMinY, searchMinZ, out maskCount, latticeParams.RotationTopShifts, shiftCandidates);
                        angleMaxCount = maskCount;
                        return shifts;
                    }).GetAwaiter().GetResult();

                    EvaluateExactShiftCandidates(rot_grid, candidate_shifts, angleMaxCount, out int exactBestShift, out double exactBestAvg, out int exactBestCount);
                    outBestShift = exactBestShift;
                    outBestAvg = exactBestAvg;
                    outBestCount = exactBestCount;
                }

                // Angle selection: maximize hot-count, tie-break by average centroid distance
                int globalBestCount = -1;
                double globalBestAvg = double.MaxValue;
                if (useManualTransform)
                {
                    bestAngle = 0.0;
                    bestShiftIdx = 0;
                    progress?.Report($"\nUsing preview transform for final lattice: signed shift ({manualSignedShift.x:F1}, {manualSignedShift.y:F1}, {manualSignedShift.z:F1}) mm, rotation (X,Y,Z) ({latticeParams.RotX:F1}, {latticeParams.RotY:F1}, {latticeParams.RotZ:F1}) degrees.");
                }
                else if (!latticeParams.EnableRotation)
                {
                    // Evaluate single angle only using the chosen centre angle
                    double a = centreAngle;
                    int count = 0;
                    double avg = double.MaxValue;
                    int shiftIdx = 0;
                    EvalAngle(a, ref shiftIdx, ref avg, ref count);
                    globalBestCount = count;
                    globalBestAvg = avg;
                    bestAngle = a;
                    bestShiftIdx = shiftIdx;
                }
                else
                {
                    // First evaluate the PCA/centre seed angle so its candidate shifts are considered
                    double seedAngle = centreAngle;
                    {
                        int count = 0;
                        double avg = double.MaxValue;
                        int shiftIdx = 0;
                        EvalAngle(seedAngle, ref shiftIdx, ref avg, ref count);
                        if (count > globalBestCount || (count == globalBestCount && avg < globalBestAvg))
                        {
                            globalBestCount = count;
                            globalBestAvg = avg;
                            bestAngle = seedAngle;
                            bestShiftIdx = shiftIdx;
                        }
                    }

                    // Coarse sweep
                    for (double a = centreAngle - coarseRange; a <= centreAngle + coarseRange; a += coarseStep)
                    {
                        int count = 0;
                        double avg = double.MaxValue;
                        int shiftIdx = 0;
                        EvalAngle(a, ref shiftIdx, ref avg, ref count);
                        if (count > globalBestCount || (count == globalBestCount && avg < globalBestAvg))
                        {
                            globalBestCount = count;
                            globalBestAvg = avg;
                            bestAngle = a;
                            bestShiftIdx = shiftIdx;
                        }
                    }

                    // Fine sweep around best coarse angle
                    double fineStart = bestAngle - fineRange;
                    double fineEnd = bestAngle + fineRange;
                    for (double a = fineStart; a <= fineEnd; a += fineStep)
                    {
                        int count = 0;
                        double avg = double.MaxValue;
                        int shiftIdx = 0;
                        EvalAngle(a, ref shiftIdx, ref avg, ref count);
                        if (count > globalBestCount || (count == globalBestCount && avg < globalBestAvg))
                        {
                            globalBestCount = count;
                            globalBestAvg = avg;
                            bestAngle = a;
                            bestShiftIdx = shiftIdx;
                        }
                    }

                    // Always evaluate a strict unrotated candidate (no PCA baseline, no angle offset)
                    // so rotation sweeps cannot perform worse than the original lattice orientation.
                    try
                    {
                        int unrotMaskCount = 0;
                        double unrotMinX = mask_origin_assigned ? mask_min_x : target.MeshGeometry.Bounds.X;
                        double unrotMinY = mask_origin_assigned ? mask_min_y : target.MeshGeometry.Bounds.Y;
                        double unrotMinZ = mask_origin_assigned ? mask_min_z : target.MeshGeometry.Bounds.Z;
                        var unrot_candidate_shifts = System.Threading.Tasks.Task.Run(() =>
                        {
                            int maskCount;
                            var shifts = SearchGrid(grid_unrot, target_mask, unrotMinX, unrotMinY, unrotMinZ, out maskCount, latticeParams.RotationTopShifts, shiftCandidates);
                            unrotMaskCount = maskCount;
                            return shifts;
                        }).GetAwaiter().GetResult();

                        EvaluateExactShiftCandidates(grid_unrot, unrot_candidate_shifts, unrotMaskCount, out int unrotShiftIdx, out double unrotExactAvg, out int unrotExactCount);

                        if (unrotExactCount > globalBestCount || (unrotExactCount == globalBestCount && unrotExactAvg < globalBestAvg))
                        {
                            globalBestCount = unrotExactCount;
                            globalBestAvg = unrotExactAvg;
                            bestAngle = 0.0;
                            bestShiftIdx = unrotShiftIdx;
                            usedUnrotatedCandidate = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        progress?.Report($"\nUnrotated candidate evaluation failed: {ex.Message}");
                    }
                }

                // record chosen metrics
                optimal_avg_dist = globalBestAvg;
                // bestShiftIdx is set above

                // Apply best rotation and shift
                var opt_shift = index_to_shift(bestShiftIdx, shiftCandidates);
                // We may have pre-rotated the baseline grid by PCA along X/Y/Z.
                // The search only rotates about Z (by bestAngle) on top of that baseline.
                double totalAppliedZ = baselineRotZ + bestAngle;
                double totalAppliedY = baselineRotY;
                double totalAppliedX = baselineRotX;

                if (useManualTransform)
                {
                    totalAppliedX = latticeParams.RotX;
                    totalAppliedY = latticeParams.RotY;
                    totalAppliedZ = latticeParams.RotZ;

                    grid = grid_unrot.ToList();
                    cold_grid = cold_unrot.ToList();

                    grid = RotateGridAroundX(grid, target_centroid, totalAppliedX);
                    grid = RotateGridAroundY(grid, target_centroid, totalAppliedY);
                    grid = RotateGrid(grid, target_centroid, totalAppliedZ);
                    grid = TranslateGrid(grid, manualShift);

                    cold_grid = RotateGridAroundX(cold_grid, target_centroid, totalAppliedX);
                    cold_grid = RotateGridAroundY(cold_grid, target_centroid, totalAppliedY);
                    cold_grid = RotateGrid(cold_grid, target_centroid, totalAppliedZ);
                    cold_grid = TranslateGrid(cold_grid, manualShift);
                }
                else if (usedUnrotatedCandidate)
                {
                    totalAppliedX = 0.0;
                    totalAppliedY = 0.0;
                    totalAppliedZ = 0.0;

                    grid = TranslateGrid(grid_unrot, opt_shift);
                    cold_grid = TranslateGrid(cold_unrot, opt_shift);
                }
                else
                {
                    var rotated_grid = RotateGrid(grid_base, target_centroid, bestAngle);
                    grid = TranslateGrid(rotated_grid, opt_shift);

                    var rotated_cold = RotateGrid(cold_base, target_centroid, bestAngle);
                    cold_grid = TranslateGrid(rotated_cold, opt_shift);
                }

                var appliedShift = useManualTransform ? manualShift : opt_shift;
                var appliedSignedShift = ToSignedShift(appliedShift);
                LastLatticePlacement = new LatticePlacementResult
                {
                    ShiftX = appliedSignedShift.x,
                    ShiftY = appliedSignedShift.y,
                    ShiftZ = appliedSignedShift.z,
                    RotX = totalAppliedX - baselineRotX,
                    RotY = totalAppliedY - baselineRotY,
                    RotZ = totalAppliedZ - baselineRotZ,
                    TotalRotX = totalAppliedX,
                    TotalRotY = totalAppliedY,
                    TotalRotZ = totalAppliedZ
                };

                // Debug: draw final applied grid points for visual inspection
                if (latticeParams.DebugDrawGrids)
                {
                    try
                    {
                        DrawDebugSpheres("DBG_Grid_Final", grid, Math.Max(0.5, Radius * 0.25), 400);
                        DrawDebugSpheres("DBG_Grid_Final_Cold", cold_grid, Math.Max(0.5, Radius * 0.25), 400);
                        progress?.Report($"\nDebug: created DBG_Grid_Final and DBG_Grid_Final_Cold structures ({grid.Count} hot, {cold_grid.Count} cold points)");
                    }
                    catch (Exception ex)
                    {
                        progress?.Report($"\nDebug draw final error: {ex.Message}");
                    }
                }

                sw.Stop();
                progress?.Report($"\nTime to find optimal rotation+shift: {sw.ElapsedMilliseconds} ms (angles tested: {angleEvaluations})");
                // Report chosen rotation details for debugging
                if (useManualTransform)
                {
                    progress?.Report($"\nApplied preview transform. Total applied rotations (X,Y,Z): {totalAppliedX:F2}, {totalAppliedY:F2}, {totalAppliedZ:F2}. Signed shift (X,Y,Z): {manualSignedShift.x:F2}, {manualSignedShift.y:F2}, {manualSignedShift.z:F2}");
                }
                else if (pcaApplied)
                {
                    // bestAngle is an offset about Z from the PCA baseline; report offset and per-axis total applied rotation
                    var optSignedShift = ToSignedShift(opt_shift);
                    if (usedUnrotatedCandidate)
                    {
                        progress?.Report($"\nSweep selected strict unrotated candidate. Total applied rotations (X,Y,Z): {totalAppliedX:F2}, {totalAppliedY:F2}, {totalAppliedZ:F2}. Best hot-count: {globalBestCount}, signed shift (X,Y,Z): ({optSignedShift.x:F2}, {optSignedShift.y:F2}, {optSignedShift.z:F2}) mm");
                    }
                    else
                    {
                        progress?.Report($"\nChosen rotation offset about Z: {bestAngle:F2} degrees. Total applied rotations (X,Y,Z): {totalAppliedX:F2}, {totalAppliedY:F2}, {totalAppliedZ:F2}. Best hot-count: {globalBestCount}, signed shift (X,Y,Z): ({optSignedShift.x:F2}, {optSignedShift.y:F2}, {optSignedShift.z:F2}) mm");
                    }
                }
                else
                {
                    // No PCA baseline used; bestAngle is the absolute rotation about Z
                    var optSignedShift = ToSignedShift(opt_shift);
                    progress?.Report($"\nChosen rotation angle about Z: {bestAngle:F2} degrees. Best hot-count: {globalBestCount}, signed shift (X,Y,Z): ({optSignedShift.x:F2}, {optSignedShift.y:F2}, {optSignedShift.z:F2}) mm");
                }
                sw.Reset();
                sw.Start();

                // Copy grid to grid_template
                var grid_template = grid.ToList();

                // Create new structure
                // Check later
                CreateStructure(structureSet, "zzz_extra", false, HighRes, "Control");
                var structTemplate = structureSet.Structures.FirstOrDefault(x => x.Id == "zzz_extra");


                // Check if grid points are within target
                grid = CheckGridInContour(grid, target);
                cold_grid = CheckGridInContour(cold_grid, eval_ptv);


                cold_grid = CheckColdGrid(cold_grid, grid);
                progress?.Report("\n Number of hot grid points: " + grid.Count);
                progress?.Report("\n Number of cold grid points: " + cold_grid.Count);

                grid_template = CheckTemplateGrid(grid_template, grid);
                grid_template = CheckGridInContour(grid_template, ptv_low);

                sw.Stop();
                progress?.Report($"\nTime to finalize grid: {sw.ElapsedMilliseconds} ms");
                sw.Reset();
                sw.Start();
                progress?.Report($"\nCreating hot spheres ...");
                var presForStructures = _prescriptionConfig;
                bool combineTuningRingShells = presForStructures != null &&
                    presForStructures.CombineTuningRingShells;
                bool createIndividualPeakStructures = latticeParams.IndividualTuningStructures || combineTuningRingShells;

                // Create all individual spheres
                // Create peaks: either grouped by slice (default) or individually per sphere
                Dictionary<int, int> SliceIdx = new Dictionary<int, int>();
                int idx_tracker = 0;
                var createdPeakNames = new List<string>();
                grid.Reverse();
                if (createIndividualPeakStructures)
                {
                    // create one structure per sphere
                    foreach (VVector ctr in grid)
                    {
                        string structure_name = "TS_Peak_" + idx_tracker.ToString();
                        idx_tracker++;
                        var ts_peak = CreateStructure(structureSet, structure_name, false, HighRes, "PTV");
                        BuildSphere(ts_peak, ctr, Radius);
                        createdPeakNames.Add(structure_name);
                    }
                }
                else
                {
                    // original behavior: group by z slice (row)
                    foreach (VVector ctr in grid)
                    {
                        int z_slice = (int)Math.Round((ctr.z - structureSet.Image.Origin.z) / structureSet.Image.ZRes);
                        if (SliceIdx.ContainsKey(z_slice))
                        {
                            string structure_name = "TS_Peak_" + SliceIdx[z_slice].ToString();
                            var ts_peak = structureSet.Structures.FirstOrDefault(x => x.Id == structure_name);
                            if (ts_peak == null)
                            {
                                ts_peak = CreateStructure(structureSet, structure_name, false, HighRes, "PTV");
                            }
                            BuildSphere(ts_peak, ctr, Radius);
                        }
                        else
                        {
                            SliceIdx.Add(z_slice, idx_tracker);
                            string structure_name = "TS_Peak_" + SliceIdx[z_slice].ToString();
                            idx_tracker++;
                            var ts_peak = CreateStructure(structureSet, structure_name, false, HighRes, "PTV");
                            BuildSphere(ts_peak, ctr, Radius);
                            createdPeakNames.Add(structure_name);
                        }
                    }
                }

                // OR all created peak structures into `structMain`
                foreach (var structure_name in createdPeakNames)
                {
                    var ts_peak = structureSet.Structures.FirstOrDefault(x => x.Id == structure_name);
                    if (ts_peak != null)
                    {
                        structMain.SegmentVolume = structMain.SegmentVolume.Or(ts_peak.SegmentVolume);
                    }
                }

                SliceIdx.Clear();
                idx_tracker = 0;
                cold_grid.Reverse();
                progress?.Report($"\nCreating cold spheres ...");
                var createdValleyNames = new List<string>();
                // Valleys are grouped by z-slice (rows) regardless of IndividualTuningStructures setting
                foreach (VVector ctr in cold_grid)
                {
                    int z_slice = (int)Math.Round((ctr.z - structureSet.Image.Origin.z) / structureSet.Image.ZRes);
                    if (SliceIdx.ContainsKey(z_slice))
                    {
                        string structure_name = "TS_Valley_" + SliceIdx[z_slice].ToString();
                        // If the slice already exists, just add to it
                        var ts_valley = structureSet.Structures.FirstOrDefault(x => x.Id == structure_name);
                        if (ts_valley == null)
                        {
                            ts_valley = CreateStructure(structureSet, structure_name, false, HighRes, "Control");
                        }
                        BuildSphere(ts_valley, ctr, Radius);
                        structMain_cold.SegmentVolume = structMain_cold.SegmentVolume.Or(ts_valley.SegmentVolume);
                        createdValleyNames.Add(structure_name);
                    }
                    else
                    {
                        SliceIdx.Add(z_slice, idx_tracker);
                        string structure_name = "TS_Valley_" + SliceIdx[z_slice].ToString();
                        idx_tracker++;
                        var ts_valley = CreateStructure(structureSet, structure_name, false, HighRes, "Control");
                        BuildSphere(ts_valley, ctr, Radius);
                        structMain_cold.SegmentVolume = structMain_cold.SegmentVolume.Or(ts_valley.SegmentVolume);
                        createdValleyNames.Add(structure_name);
                    }
                }

                progress?.Report($"\nCreating extra spheres ...");
                foreach (VVector ctr in grid_template)
                {
                    BuildSphere(structTemplate, ctr, Radius);
                }

                structMain_cold.SegmentVolume = structMain_cold.SegmentVolume.And(eval_ptv);

                // Delete the autogenerated target if it exists
                if (deleteAutoTarget)
                {
                    structureSet.RemoveStructure(target_initial_temp);
                }
                // structureSet.RemoveStructure(eval_ptv);
                // structureSet.RemoveStructure(target);


                progress?.Report($"\nCreating tuning structures ...");

                // Create PTV_Control
                CreateStructure(structureSet, "PTV_Control", false, HighRes, "Control");
                Structure ptv_control = structureSet.Structures.FirstOrDefault(x => x.Id == "PTV_Control");
                AddContoursToMain(structureSet, ref ptv_control, ref ptv_low);
                ptv_control.SegmentVolume = ptv_control.SegmentVolume.Sub(structMain.SegmentVolume.Margin(15));

                // Create TS_Peak_Ring structures. If individual tuning structures were created,
                // create rings for each peak individually; otherwise create grouped rings around `structMain`.
                if (combineTuningRingShells && createdPeakNames != null && createdPeakNames.Count > 0)
                {
                    var staleRingStructures = structureSet.Structures
                        .Where(x => x.Id.Equals("TS_Peak_Ring1", StringComparison.OrdinalIgnoreCase) ||
                                    x.Id.Equals("TS_Peak_Ring2", StringComparison.OrdinalIgnoreCase) ||
                                    x.Id.Equals("TS_Peak_Ring3", StringComparison.OrdinalIgnoreCase) ||
                                    x.Id.StartsWith("TS_Peak_Ring1_TS_Peak_", StringComparison.OrdinalIgnoreCase) ||
                                    x.Id.StartsWith("TS_Peak_Ring2_TS_Peak_", StringComparison.OrdinalIgnoreCase) ||
                                    x.Id.StartsWith("TS_Peak_Ring3_TS_Peak_", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var staleRing in staleRingStructures)
                    {
                        structureSet.RemoveStructure(staleRing);
                    }

                    var tuningShells = new[]
                    {
                        new { Id = "TS_Peak_Ring_Inner", ShellIndex = 1, OuterMargin = 5.0, InnerMargin = 0.0 },
                        new { Id = "TS_Peak_Ring_Middle", ShellIndex = 2, OuterMargin = 10.0, InnerMargin = 5.0 },
                        new { Id = "TS_Peak_Ring_Outer", ShellIndex = 3, OuterMargin = 15.0, InnerMargin = 10.0 }
                    };

                    foreach (var shell in tuningShells)
                    {
                        var tuningShell = CreateStructure(structureSet, shell.Id, false, HighRes, "Control");
                        int peakIndex = 0;
                        foreach (var peakName in createdPeakNames)
                        {
                            var peakStruct = structureSet.Structures.FirstOrDefault(x => x.Id == peakName);
                            if (peakStruct == null)
                            {
                                peakIndex++;
                                continue;
                            }

                            string tempRingName = $"zzzT{shell.ShellIndex}_{peakIndex}";
                            var tempRing = CreateStructure(structureSet, tempRingName, false, HighRes, "Control");
                            AddContoursToMain(structureSet, ref tempRing, ref peakStruct);
                            tempRing.SegmentVolume = tempRing.SegmentVolume.Margin(shell.OuterMargin);
                            tempRing.SegmentVolume = tempRing.SegmentVolume.Sub(peakStruct.SegmentVolume.Margin(shell.InnerMargin));

                            if (tempRing.HasSegment)
                            {
                                if (tuningShell.HasSegment)
                                {
                                    tuningShell.SegmentVolume = tuningShell.SegmentVolume.Or(tempRing.SegmentVolume);
                                }
                                else
                                {
                                    AddContoursToMain(structureSet, ref tuningShell, ref tempRing);
                                }
                            }

                            structureSet.RemoveStructure(tempRing);
                            peakIndex++;
                        }
                    }
                }
                else if (createIndividualPeakStructures && createdPeakNames != null && createdPeakNames.Count > 0)
                {
                    RemoveCombinedTuningShells(structureSet);
                    foreach (var peakName in createdPeakNames)
                    {
                        var peakStruct = structureSet.Structures.FirstOrDefault(x => x.Id == peakName);
                        if (peakStruct == null) continue;

                        // Ring1 (5 mm margin)
                        string ring1Name = $"TS_Peak_Ring1_{peakName}";
                        CreateStructure(structureSet, ring1Name, false, HighRes, "Control");
                        var ring1 = structureSet.Structures.FirstOrDefault(x => x.Id == ring1Name);
                        if (ring1 != null)
                        {
                            AddContoursToMain(structureSet, ref ring1, ref peakStruct);
                            ring1.SegmentVolume = ring1.SegmentVolume.Margin(5);
                            ring1.SegmentVolume = ring1.SegmentVolume.Sub(peakStruct.SegmentVolume.Margin(0));
                        }

                        // Ring2 (10 mm margin)
                        string ring2Name = $"TS_Peak_Ring2_{peakName}";
                        CreateStructure(structureSet, ring2Name, false, HighRes, "Control");
                        var ring2 = structureSet.Structures.FirstOrDefault(x => x.Id == ring2Name);
                        if (ring2 != null)
                        {
                            AddContoursToMain(structureSet, ref ring2, ref peakStruct);
                            ring2.SegmentVolume = ring2.SegmentVolume.Margin(10);
                            ring2.SegmentVolume = ring2.SegmentVolume.Sub(peakStruct.SegmentVolume.Margin(5));
                        }

                        // Ring3 (15 mm margin)
                        string ring3Name = $"TS_Peak_Ring3_{peakName}";
                        CreateStructure(structureSet, ring3Name, false, HighRes, "Control");
                        var ring3 = structureSet.Structures.FirstOrDefault(x => x.Id == ring3Name);
                        if (ring3 != null)
                        {
                            AddContoursToMain(structureSet, ref ring3, ref peakStruct);
                            ring3.SegmentVolume = ring3.SegmentVolume.Margin(15);
                            ring3.SegmentVolume = ring3.SegmentVolume.Sub(peakStruct.SegmentVolume.Margin(10));
                        }
                    }
                }
                else
                {
                    CreateStructure(structureSet, "TS_Ring2", false, false, "Control");
                    Structure ts_ring2 = structureSet.Structures.FirstOrDefault(x => x.Id == "TS_Ring2");
                    AddContoursToMain(structureSet, ref ts_ring2, ref ptv_low);
                    ts_ring2.SegmentVolume = ts_ring2.SegmentVolume.Margin(30);

                    CreateStructure(structureSet, "TS_Ring1", false, false, "Control");
                    Structure ts_ring1 = structureSet.Structures.FirstOrDefault(x => x.Id == "TS_Ring1");
                    AddContoursToMain(structureSet, ref ts_ring1, ref ptv_low);
                    ts_ring1.SegmentVolume = ts_ring1.SegmentVolume.Margin(15);

                    if (ptv_low.IsHighResolution)
                    {
                        CreateStructure(_ss, "zzz_temp_ptv_low", false, HighRes, "Control");
                        var temp_ptv_low = _ss.Structures.FirstOrDefault(x => x.Id == "zzz_temp_ptv_low");
                        AddContoursToMain(_ss, ref temp_ptv_low, ref ptv_low);

                        ts_ring2.SegmentVolume = ts_ring2.SegmentVolume.Sub(temp_ptv_low.SegmentVolume.Margin(15));
                        ts_ring1.SegmentVolume = ts_ring1.SegmentVolume.Sub(temp_ptv_low.SegmentVolume.Margin(5));

                        structureSet.RemoveStructure(temp_ptv_low);
                    }
                    else
                    {
                        ts_ring2.SegmentVolume = ts_ring2.SegmentVolume.Sub(ptv_low.SegmentVolume.Margin(15));
                        ts_ring1.SegmentVolume = ts_ring1.SegmentVolume.Sub(ptv_low.SegmentVolume.Margin(5));
                    }

                    var body = structureSet.Structures.FirstOrDefault(x => x.Id == BodyId);
                    ts_ring2.SegmentVolume = ts_ring2.SegmentVolume.And(body);
                    ts_ring1.SegmentVolume = ts_ring1.SegmentVolume.And(body);

                    // Ensure the ring structure exists before adding contours
                    CreateStructure(structureSet, "TS_Peak_Ring1", false, HighRes, "Control");
                    Structure ts_peak_ring1 = structureSet.Structures.FirstOrDefault(x => x.Id == "TS_Peak_Ring1");
                    AddContoursToMain(structureSet, ref ts_peak_ring1, ref structMain);
                    ts_peak_ring1.SegmentVolume = ts_peak_ring1.SegmentVolume.Margin(5);
                    ts_peak_ring1.SegmentVolume = ts_peak_ring1.SegmentVolume.Sub(structMain.SegmentVolume.Margin(0));

                    CreateStructure(structureSet, "TS_Peak_Ring2", false, HighRes, "Control");
                    Structure ts_peak_ring2 = structureSet.Structures.FirstOrDefault(x => x.Id == "TS_Peak_Ring2");
                    AddContoursToMain(structureSet, ref ts_peak_ring2, ref structMain);
                    ts_peak_ring2.SegmentVolume = ts_peak_ring2.SegmentVolume.Margin(10);
                    ts_peak_ring2.SegmentVolume = ts_peak_ring2.SegmentVolume.Sub(structMain.SegmentVolume.Margin(5));

                    CreateStructure(structureSet, "TS_Peak_Ring3", false, HighRes, "Control");
                    Structure ts_peak_ring3 = structureSet.Structures.FirstOrDefault(x => x.Id == "TS_Peak_Ring3");
                    AddContoursToMain(structureSet, ref ts_peak_ring3, ref structMain);
                    ts_peak_ring3.SegmentVolume = ts_peak_ring3.SegmentVolume.Margin(15);
                    ts_peak_ring3.SegmentVolume = ts_peak_ring3.SegmentVolume.Sub(structMain.SegmentVolume.Margin(10));
                }

                if (presForStructures != null && presForStructures.CreateBodyMinusSpheresStructure)
                {
                    var body = structureSet.Structures.FirstOrDefault(x => x.Id == BodyId);
                    if (body != null && body.HasSegment && structMain != null && structMain.HasSegment)
                    {
                        string bodySpheresId = string.IsNullOrWhiteSpace(presForStructures.BodyMinusSpheresStructureId)
                            ? "BODY_Spheres"
                            : presForStructures.BodyMinusSpheresStructureId;
                        CreateStructure(structureSet, bodySpheresId, false, HighRes, "Control");
                        var bodySpheres = structureSet.Structures.FirstOrDefault(x => x.Id == bodySpheresId);
                        if (bodySpheres != null)
                        {
                            AddContoursToMain(structureSet, ref bodySpheres, ref body);
                            bodySpheres.SegmentVolume = bodySpheres.SegmentVolume.Sub(structMain.SegmentVolume);

                            var ringStructures = combineTuningRingShells
                                ? structureSet.Structures
                                    .Where(x => x.Id.Equals("TS_Peak_Ring_Inner", StringComparison.OrdinalIgnoreCase) ||
                                                x.Id.Equals("TS_Peak_Ring_Middle", StringComparison.OrdinalIgnoreCase) ||
                                                x.Id.Equals("TS_Peak_Ring_Outer", StringComparison.OrdinalIgnoreCase))
                                    .ToList()
                                : structureSet.Structures
                                    .Where(x => x.Id.Equals("TS_Peak_Ring1", StringComparison.OrdinalIgnoreCase) ||
                                                x.Id.Equals("TS_Peak_Ring2", StringComparison.OrdinalIgnoreCase) ||
                                                x.Id.Equals("TS_Peak_Ring3", StringComparison.OrdinalIgnoreCase) ||
                                                x.Id.StartsWith("TS_Peak_Ring1_TS_Peak_", StringComparison.OrdinalIgnoreCase) ||
                                                x.Id.StartsWith("TS_Peak_Ring2_TS_Peak_", StringComparison.OrdinalIgnoreCase) ||
                                                x.Id.StartsWith("TS_Peak_Ring3_TS_Peak_", StringComparison.OrdinalIgnoreCase))
                                    .ToList();

                            foreach (var ringStructure in ringStructures)
                            {
                                if (ringStructure.HasSegment)
                                {
                                    bodySpheres.SegmentVolume = bodySpheres.SegmentVolume.Sub(ringStructure.SegmentVolume);
                                }
                            }
                        }
                    }
                }

                progress?.Report("\nLattice structure creation complete!");
                sw.Stop();
                progress?.Report($"\nTime to create spheres: {sw.ElapsedMilliseconds} ms");

            });
        }

        private VVector[] CreateContour(VVector centre, double radius, int nOfPoints)
        {
            VVector[] contour = new VVector[nOfPoints + 1];
            double angleIncrement = Math.PI * 2.0 / Convert.ToDouble(nOfPoints);
            for (int i = 0; i < nOfPoints; ++i)
            {
                double angle = Convert.ToDouble(i) * angleIncrement;
                double xDelta = radius * Math.Cos(angle);
                double yDelta = radius * Math.Sin(angle);
                VVector delta = new VVector(xDelta, yDelta, 0.0);
                contour[i] = centre + delta;
            }
            contour[nOfPoints] = contour[0];

            return contour;
        }


        private ExternalBeamMachineParameters BuildBeamParameters(string machineId, string energySelection)
        {
            string energy = string.IsNullOrEmpty(energySelection) ? "6X" : energySelection;
            int doseRate = 600;
            string primaryFluenceModeId = "";
            if (energy.Contains("FFF"))
            {
                primaryFluenceModeId = "FFF";
                energy = energy.Replace("-FFF", "");
                if (energy.Contains("6X"))
                {
                    doseRate = 1400;
                }
                else if (energy.Contains("10X"))
                {
                    doseRate = 2400;
                }
            }
            return new ExternalBeamMachineParameters(machineId, energy, doseRate, "ARC", primaryFluenceModeId);
        }

        /// <summary>
        /// ESAPI exposes no list of commissioned machines, so each candidate is probed by
        /// creating a throwaway arc beam with the same parameters SetupBeams uses and
        /// removing it again. A machine counts as available only if the beam could be
        /// created, which also verifies the arc technique and dose rate exist for it.
        /// </summary>
        public async Task<List<string>> ProbeAvailableMachines(List<(string MachineId, string Energy)> candidates)
        {
            var available = new List<string>();
            await _ew.AsyncRunPlanContext((pat, ps) =>
            {
                var plan = ps as ExternalPlanSetup;
                if (plan == null)
                {
                    Helpers.SeriLog.LogWarning("Machine probe skipped: plan is not an ExternalPlanSetup.");
                    return;
                }

                VVector isocentre = new VVector(0, 0, 0);
                if (ps.StructureSet?.Image != null)
                {
                    isocentre = ps.StructureSet.Image.UserOrigin;
                }
                var jawPositions = new VRect<double>(-50, -50, 50, 50);

                foreach (var candidate in candidates)
                {
                    Beam probe = null;
                    try
                    {
                        var beamParams = BuildBeamParameters(candidate.MachineId, candidate.Energy);
                        probe = plan.AddMLCArcBeam(
                            beamParams,
                            null,
                            jawPositions,
                            0.0,
                            181.0,
                            179.0,
                            VMS.TPS.Common.Model.Types.GantryDirection.Clockwise,
                            0.0,
                            isocentre);
                        available.Add(candidate.MachineId);
                        Helpers.SeriLog.LogInfo($"Machine probe: {candidate.MachineId} ({candidate.Energy}) is available.");
                    }
                    catch (Exception ex)
                    {
                        Helpers.SeriLog.LogInfo($"Machine probe: {candidate.MachineId} ({candidate.Energy}) not available: {ex.Message}");
                    }
                    finally
                    {
                        if (probe != null)
                        {
                            try
                            {
                                plan.RemoveBeam(probe);
                            }
                            catch (Exception ex)
                            {
                                Helpers.SeriLog.LogError($"Machine probe: failed to remove probe beam for {candidate.MachineId}", ex);
                            }
                        }
                    }
                }
            });
            return available;
        }

        public async Task SetupBeams(LatticeParameters latticeParams)
        {
            await _ew.AsyncRunPlanContext((pat, ps) =>
            {
                var plan = (ExternalPlanSetup)ps;
                StructureSet structureSet = ps.StructureSet;

                // Clean up all beams
                var all_beam = plan.Beams.ToList();

                foreach (var beam in all_beam)
                {
                    plan.RemoveBeam(beam);
                }

                var ptv_low = structureSet.Structures.FirstOrDefault(x => x.Id == latticeParams.PTVLowStructure);
                VVector isocentre = ptv_low.CenterPoint;
                // round isocentre to 1 decimal place
                isocentre = new VVector(Math.Round(isocentre.x, 0), Math.Round(isocentre.y, 0), Math.Round(isocentre.z, 0));

                ExternalBeamMachineParameters beamParams = BuildBeamParameters(latticeParams.MachineId, latticeParams.Energy);
                // make a list of 180 array of zeros
                IEnumerable<double> metersetWeights = Enumerable.Repeat(0.0, 180).ToArray();

                var collimatorAngle = new List<double> { 50.0, 85.0, 125.0 };
                var gantryAngle = new List<double> { 181.0, 179.0, 181.0 };
                var gantryStop = new List<double> { 179.0, 181.0, 179.0 };
                var gantryDirection = VMS.TPS.Common.Model.Types.GantryDirection.Clockwise;
                var couchAngle = new List<double> { 0.0, 0.0, 0.0 };

                if (latticeParams.CouchKick)
                {
                    collimatorAngle = new List<double> { 312.0, 50.0, 57.0 };
                    couchAngle = new List<double> { 350.0, 0.0, 10.0 };
                }

                var jawPositions = new VRect<double>(-100, -100, 100, 100);

                for (int i = 0; i < 3; i++)
                {
                    // beam = plan.AddVMATBeam(
                    // beamParams,
                    // metersetWeights,
                    // Math.Round(collimatorAngle[i], 1),
                    // gantryAngle[i],
                    // gantryStop[i],
                    // gantryDirection,
                    // couchAngle[i],
                    // isocentre);
                    // CloseModalWindows();
                    var beam = plan.AddMLCArcBeam(
                        beamParams,
                        null,
                        jawPositions,
                        Math.Round(collimatorAngle[i], 1),
                        gantryAngle[i],
                        gantryStop[i],
                        gantryDirection,
                        couchAngle[i],
                        isocentre);

                    if (gantryDirection == VMS.TPS.Common.Model.Types.GantryDirection.Clockwise)
                    {

                        beam.Id = (i + 1).ToString() + " CW";
                    }
                    else
                    {
                        beam.Id = (i + 1).ToString() + " CCW";
                    }


                    gantryDirection = gantryDirection == VMS.TPS.Common.Model.Types.GantryDirection.Clockwise
                        ? VMS.TPS.Common.Model.Types.GantryDirection.CounterClockwise
                        : VMS.TPS.Common.Model.Types.GantryDirection.Clockwise;

                    var fitmargin = new VMS.TPS.Common.Model.Types.FitToStructureMargins(5.0);
                    var meetingpoint = VMS.TPS.Common.Model.Types.OpenLeavesMeetingPoint.OpenLeavesMeetingPoint_Middle;
                    var closedMeetingPoint = VMS.TPS.Common.Model.Types.ClosedLeavesMeetingPoint.ClosedLeavesMeetingPoint_Center;
                    var jawfitting = VMS.TPS.Common.Model.Types.JawFitting.FitToStructure;
                    beam.FitMLCToStructure(fitmargin, ptv_low, false, jawfitting, meetingpoint, closedMeetingPoint);
                }
            });
        }

        public async Task SetupOptimizer(LatticeParameters latticeParams)
        {
            await _ew.AsyncRunPlanContext((pat, ps) =>
            {
                var plan = (ExternalPlanSetup)ps;
                StructureSet structureSet = ps.StructureSet;

                string axb_model = "AcurosXB_1811";
                plan.SetCalculationOption(axb_model, "CalculationGridSizeInCM", "0.25");
                plan.SetCalculationOption(axb_model, "UseGPU", "No");

                Optimizer = plan.OptimizationSetup;

                // Load prescription configuration (values in cGy). If not found, use built-in defaults.
                var pres = _prescriptionConfig ?? PrescriptionConfig.LoadFromDefaults() ?? new PrescriptionConfig();

                // Clean up the objectives:
                var all_objectives = Optimizer.Objectives.ToList();
                foreach (var objective in all_objectives)
                {
                    Optimizer.RemoveObjective(objective);
                }

                Optimizer.UseJawTracking = true;
                Optimizer.AddAutomaticSbrtNormalTissueObjective(80.0);

                var ptv_low_name = latticeParams.PTVLowStructure;
                var ptv_low = structureSet.Structures.FirstOrDefault(x => x.Id == ptv_low_name);
                if (pres.HasPTVLow && ptv_low != null)
                {
                    var doseObjective = new DoseValue(pres.PTVLow, "cGy");
                    Optimizer.AddPointObjective(ptv_low, OptimizationObjectiveOperator.Lower, doseObjective, 100.0, pres.PTVLowPriority);
                }
                else if (!pres.HasPTVLow)
                {
                    Helpers.SeriLog.LogInfo("SetupOptimizer: PTV low objective not configured; skipping point objective.");
                }
                else
                {
                    Helpers.SeriLog.LogWarning("SetupOptimizer: PTV low structure not found; skipping point objective.");
                }

                List<Structure> peak_structures = new List<Structure>();
                List<Structure> valley_structures = new List<Structure>();

                foreach (var struct_i in structureSet.Structures)
                {
                    if (struct_i.Id.Contains("TS_Peak_") && !struct_i.Id.Contains("Ring"))
                    {
                        peak_structures.Add(struct_i);
                    }
                    else if (struct_i.Id.Contains("TS_Valley_"))
                    {
                        valley_structures.Add(struct_i);
                    }
                }

                foreach (var peak_structure in peak_structures)
                {
                    AddPeakObjective(peak_structure, Optimizer, pres);
                }
                if (pres.EnableRepresentativePeakObjective && peak_structures.Count > 0)
                {
                    var representativeDose = new DoseValue(pres.RepresentativePeakDose, "cGy");
                    Optimizer.AddPointObjective(
                        peak_structures[0],
                        OptimizationObjectiveOperator.Lower,
                        representativeDose,
                        pres.RepresentativePeakVolumePercent,
                        pres.RepresentativePeakPriority);
                }
                foreach (var valley_structure in valley_structures)
                {
                    AddValleyObjective(valley_structure, Optimizer, pres);
                }

                // Add objectives to either grouped rings or individual per-peak rings.
                void AddPeakRingObjectives(string groupedId, string individualPrefix, string combinedId, double doseCgY, double priority)
                {
                    var ringStructures = pres.CombineTuningRingShells
                        ? structureSet.Structures
                            .Where(x => x.Id.Equals(combinedId, StringComparison.OrdinalIgnoreCase))
                            .ToList()
                        : structureSet.Structures
                            .Where(x => x.Id.Equals(groupedId, StringComparison.OrdinalIgnoreCase) ||
                                        x.Id.StartsWith(individualPrefix, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                    if (ringStructures.Count == 0)
                    {
                        string expectedId = pres.CombineTuningRingShells ? combinedId : groupedId;
                        Helpers.SeriLog.LogWarning($"SetupOptimizer: {expectedId} not found; skipping its objective.");
                        return;
                    }

                    var ringDoseObjective = new DoseValue(doseCgY, "cGy");
                    foreach (var ringStructure in ringStructures)
                    {
                        Optimizer.AddPointObjective(ringStructure, OptimizationObjectiveOperator.Upper, ringDoseObjective, 0.0, priority);
                    }
                }

                AddPeakRingObjectives("TS_Peak_Ring1", "TS_Peak_Ring1_TS_Peak_", "TS_Peak_Ring_Inner", pres.TS_Peak_Ring1, pres.TS_Peak_Ring1Priority);
                AddPeakRingObjectives("TS_Peak_Ring2", "TS_Peak_Ring2_TS_Peak_", "TS_Peak_Ring_Middle", pres.TS_Peak_Ring2, pres.TS_Peak_Ring2Priority);
                AddPeakRingObjectives("TS_Peak_Ring3", "TS_Peak_Ring3_TS_Peak_", "TS_Peak_Ring_Outer", pres.TS_Peak_Ring3, pres.TS_Peak_Ring3Priority);

                // Add TS_Ring objectives
                var tsRing1DoseObjective = new DoseValue(pres.TS_Ring1, "cGy");
                var ts_ring1 = structureSet.Structures.FirstOrDefault(x => x.Id == "TS_Ring1");
                if (ts_ring1 != null)
                {
                    Optimizer.AddPointObjective(ts_ring1, OptimizationObjectiveOperator.Upper, tsRing1DoseObjective, 0.0, pres.TS_Ring1Priority);
                }
                else
                {
                    Helpers.SeriLog.LogWarning("SetupOptimizer: TS_Ring1 not found; skipping its objective.");
                }

                var tsRing2DoseObjective = new DoseValue(pres.TS_Ring2, "cGy");
                var ts_ring2 = structureSet.Structures.FirstOrDefault(x => x.Id == "TS_Ring2");
                if (ts_ring2 != null)
                {
                    Optimizer.AddPointObjective(ts_ring2, OptimizationObjectiveOperator.Upper, tsRing2DoseObjective, 0.0, pres.TS_Ring2Priority);
                }
                else
                {
                    Helpers.SeriLog.LogWarning("SetupOptimizer: TS_Ring2 not found; skipping its objective.");
                }

                if (pres.EnableBodyMinusSpheresObjective)
                {
                    string bodySpheresId = string.IsNullOrWhiteSpace(pres.BodyMinusSpheresStructureId)
                        ? "BODY_Spheres"
                        : pres.BodyMinusSpheresStructureId;
                    var bodySpheres = structureSet.Structures.FirstOrDefault(x => x.Id.Equals(bodySpheresId, StringComparison.OrdinalIgnoreCase));
                    if (bodySpheres != null)
                    {
                        var bodySpheresDose = new DoseValue(pres.BodyMinusSpheresDose, "cGy");
                        Optimizer.AddPointObjective(
                            bodySpheres,
                            OptimizationObjectiveOperator.Upper,
                            bodySpheresDose,
                            pres.BodyMinusSpheresVolumePercent,
                            pres.BodyMinusSpheresPriority);
                    }
                    else
                    {
                        Helpers.SeriLog.LogWarning($"SetupOptimizer: {bodySpheresId} not found; skipping body-minus-spheres objective.");
                    }
                }

                // plan.OptimizeVMAT();
            });

            // I would do one round of optimization, then find all organs-at-risk that are within PTV bounding box +/- 5 cm in sup-inf
            // Apply a mean dose reduction
            // Apply the constraints that we have in CTP - 20%
            // 
        }

        private void PopulatePreviewBoundaryMesh(SpherePreviewData preview, Structure structure)
        {
            if (preview == null || structure == null || structure.MeshGeometry == null)
            {
                return;
            }

            var bounds = structure.MeshGeometry.Bounds;
            preview.MinX = bounds.X;
            preview.MaxX = bounds.X + bounds.SizeX;
            preview.MinY = bounds.Y;
            preview.MaxY = bounds.Y + bounds.SizeY;
            preview.MinZ = bounds.Z;
            preview.MaxZ = bounds.Z + bounds.SizeZ;

            preview.TargetVertices.Clear();
            preview.TargetTriangleIndices.Clear();

            CopyPreviewMesh(structure, preview.TargetVertices, preview.TargetTriangleIndices, 15000);
        }

        private PreviewStructureMesh CreatePreviewStructureMesh(Structure structure, int maxTriangles)
        {
            if (structure == null || !structure.HasSegment || structure.MeshGeometry == null)
            {
                return null;
            }

            var previewMesh = new PreviewStructureMesh { Id = structure.Id };
            CopyPreviewMesh(structure, previewMesh.Vertices, previewMesh.TriangleIndices, maxTriangles);
            if (previewMesh.Vertices.Count < 9 || previewMesh.TriangleIndices.Count < 3)
            {
                return null;
            }

            return previewMesh;
        }

        private void CopyPreviewMesh(Structure structure, List<double> vertices, List<int> triangleIndices, int maxTriangles)
        {
            if (structure == null || structure.MeshGeometry == null || vertices == null || triangleIndices == null)
            {
                return;
            }

            vertices.Clear();
            triangleIndices.Clear();

            var mesh = structure.MeshGeometry;
            if (mesh.Positions == null || mesh.Positions.Count == 0 ||
                mesh.TriangleIndices == null || mesh.TriangleIndices.Count < 3)
            {
                return;
            }

            // Keep mesh transfer bounded so preview generation remains responsive on large structures.
            int triCount = mesh.TriangleIndices.Count / 3;
            int triStride = Math.Max(1, (int)Math.Ceiling((double)triCount / Math.Max(1, maxTriangles)));

            var indexMap = new Dictionary<int, int>();
            for (int t = 0; t < triCount; t += triStride)
            {
                int baseIdx = t * 3;
                for (int c = 0; c < 3; c++)
                {
                    int oldIdx = mesh.TriangleIndices[baseIdx + c];
                    if (!indexMap.TryGetValue(oldIdx, out int newIdx))
                    {
                        var p = mesh.Positions[oldIdx];
                        newIdx = vertices.Count / 3;
                        vertices.Add(p.X);
                        vertices.Add(p.Y);
                        vertices.Add(p.Z);
                        indexMap[oldIdx] = newIdx;
                    }
                    triangleIndices.Add(newIdx);
                }
            }
        }


        public async Task<SpherePreviewData> BuildSpherePreview(LatticeParameters latticeParams, int maxSpheres = 350,
            double shiftX = 0.0, double shiftY = 0.0, double shiftZ = 0.0,
            double rotX = 0.0, double rotY = 0.0, double rotZ = 0.0,
            bool exact = false, bool optimizePlacement = false, bool useExistingCreatedCore = false)
        {
            var preview = new SpherePreviewData
            {
                IsValid = false,
                Message = "Preview not generated."
            };

            if (latticeParams == null || string.IsNullOrEmpty(latticeParams.TargetStructure))
            {
                preview.Message = "Target structure is not selected.";
                return preview;
            }

            await _ew.AsyncRunPlanContext((pat, ps) =>
            {
                var ss = ps.StructureSet;
                _ss = ss;
                var target = ss.Structures.FirstOrDefault(x => x.Id == latticeParams.TargetStructure);
                if (target == null || !target.HasSegment)
                {
                    preview.Message = "Target structure has no segment.";
                    return;
                }

                double sphereRadiusMm = latticeParams.Radius;
                if (sphereRadiusMm <= 0.0)
                {
                    if (TryParseInvariant(latticeParams.SphereSize, out var sphereDiameterCm) && sphereDiameterCm > 0.0)
                    {
                        sphereRadiusMm = sphereDiameterCm * 10.0 / 2.0;
                    }
                    else
                    {
                        sphereRadiusMm = 5.0;
                    }
                }

                double spacing = latticeParams.LatticeSpacing;
                if (spacing <= 0.0)
                {
                    spacing = sphereRadiusMm * 8.0;
                }

                var previousSpacing = LatticeSpacing;
                var previousRadius = Radius;
                var previousBodyId = BodyId;
                var previousBodyMargin = bodyMargin;
                var previousOarMargin = OarMargin;
                var previousHighRes = HighRes;

                LatticeSpacing = spacing;
                Radius = sphereRadiusMm;
                BodyId = latticeParams.BodyId;
                bodyMargin = latticeParams.BodyMargin;
                OarMargin = latticeParams.OarMargin;
                HighRes = latticeParams.HighRes;
                var selectedOars = FilterSelectedOarEntries(latticeParams.OarStructures);
                // Preview counts must match creation: sphere centres are accepted only in the contracted
                // GTV core after PRV subtraction, never directly in the selected source GTV.
                bool useTemporaryPreviewStructures = !useExistingCreatedCore;

                var previewStructureIds = new List<string>
                {
                    "zzz_preview_GTV_core",
                    "zzz_preview_SFRT_PRV",
                    "zzz_preview_temp_prv"
                };

                if (useTemporaryPreviewStructures)
                {
                    // Proactively remove any leftover preview structures so a partial run from a
                    // previous attempt cannot poison this one (HighRes/Control mismatch, stale
                    // segments, etc.).
                    foreach (var id in previewStructureIds)
                    {
                        var stale = ss.Structures.FirstOrDefault(x => x.Id == id);
                        if (stale != null)
                        {
                            try { ss.RemoveStructure(stale); } catch { /* ignore */ }
                        }
                    }
                }

                try
                {
                    Structure countTarget = target;
                    bool usingSafeCore = false;

                    if (useExistingCreatedCore)
                    {
                        var createdCore = ss.Structures.FirstOrDefault(x => x.Id.Equals("zzz_GTV_core", StringComparison.OrdinalIgnoreCase));
                        if (createdCore != null && createdCore.HasSegment && createdCore.MeshGeometry != null)
                        {
                            countTarget = createdCore;
                            usingSafeCore = true;
                        }
                        else
                        {
                            useTemporaryPreviewStructures = true;
                        }
                    }

                    var body = ss.Structures.FirstOrDefault(x => x.Id == latticeParams.BodyId);
                    if (useTemporaryPreviewStructures && (body == null || !body.HasSegment))
                    {
                        preview.TotalSphereCount = 0;
                        preview.DisplayedSphereCount = 0;
                        preview.SphereRadiusMm = sphereRadiusMm;
                        preview.Message = "No preview generated: selected body structure has no segment, so the safe core cannot be built.";
                        return;
                    }

                    if (useTemporaryPreviewStructures)
                    {
                        // Match resolution to the source target so AddContoursToMain doesn't
                        // explode when the target is HighRes (which it often is after a
                        // previous Create that promoted structures).
                        CreateStructure(ss, "zzz_preview_GTV_core", false, target.IsHighResolution);
                        var safeTarget = ss.Structures.FirstOrDefault(x => x.Id == "zzz_preview_GTV_core");
                        if (safeTarget == null)
                        {
                            preview.Message = "Failed to create preview safe-core structure.";
                            return;
                        }
                        try
                        {
                            AddContoursToMain(ss, ref safeTarget, ref target);
                            safeTarget.SegmentVolume = safeTarget.SegmentVolume.Margin(-5 - sphereRadiusMm);
                        }
                        catch (Exception ex)
                        {
                            preview.Message = $"Preview safe-core build failed: {ex.Message}";
                            return;
                        }

                        var prv = CreatePrvStructure(selectedOars, "zzz_preview_SFRT_PRV", "zzz_preview_temp_prv");
                        if (prv == null)
                        {
                            preview.TotalSphereCount = 0;
                            preview.DisplayedSphereCount = 0;
                            preview.SphereRadiusMm = sphereRadiusMm;
                            preview.Message = "No preview generated: preview PRV could not be built, so the safe core cannot be built.";
                            return;
                        }

                        if (prv.HasSegment)
                        {
                            try
                            {
                                safeTarget.SegmentVolume = safeTarget.SegmentVolume.Sub(prv);
                            }
                            catch (Exception ex)
                            {
                                Helpers.SeriLog.LogWarning($"Preview safe-core PRV subtract failed: {ex.Message}");
                                preview.TotalSphereCount = 0;
                                preview.DisplayedSphereCount = 0;
                                preview.SphereRadiusMm = sphereRadiusMm;
                                preview.Message = $"No preview generated: preview PRV subtraction failed, so the safe core cannot be trusted. {ex.Message}";
                                return;
                            }
                        }

                        if (!safeTarget.HasSegment)
                        {
                            preview.TotalSphereCount = 0;
                            preview.DisplayedSphereCount = 0;
                            preview.SphereRadiusMm = sphereRadiusMm;
                            preview.IsValid = true;
                            preview.Message = "No sphere centres were found because the safe core is empty after target contraction and PRV subtraction.";
                            return;
                        }

                        if (safeTarget.HasSegment)
                        {
                            countTarget = safeTarget;
                            usingSafeCore = true;
                        }
                    }

                    if (countTarget == null || !countTarget.HasSegment || countTarget.MeshGeometry == null)
                    {
                        preview.TotalSphereCount = 0;
                        preview.DisplayedSphereCount = 0;
                        preview.SphereRadiusMm = sphereRadiusMm;
                        preview.IsValid = true;
                        preview.Message = "No preview generated: target structure has no segment after PRV subtraction.";
                        return;
                    }
                    var countBounds = countTarget.MeshGeometry.Bounds;
                    PopulatePreviewBoundaryMesh(preview, countTarget);
                    preview.OarMeshes.Clear();
                    foreach (var oarEntry in selectedOars)
                    {
                        var oarStructure = ss.Structures.FirstOrDefault(x => x.Id.Equals(oarEntry.Name, StringComparison.OrdinalIgnoreCase));
                        var oarMesh = CreatePreviewStructureMesh(oarStructure, 5000);
                        if (oarMesh != null)
                        {
                            preview.OarMeshes.Add(oarMesh);
                        }
                    }

                    // Match production grid extents, then sample points that truly lie inside the target/core.
                    var xmin = countBounds.X - spacing * 1.5;
                    var ymin = countBounds.Y - spacing * 1.5;
                    var zmin = countBounds.Z - spacing * 1.5;
                    var xsize = countBounds.SizeX + spacing * 3.1;
                    var ysize = countBounds.SizeY + spacing * 3.1;
                    var zsize = countBounds.SizeZ + spacing * 3.1;

                    var rawGrid = BuildHexGrid(xmin, xsize, ymin, ysize, zmin, zsize);

                    var centre = countTarget.CenterPoint;
                    var gridForCheck = rawGrid;
                    bool usedOptimizedPlacement = optimizePlacement && !latticeParams.UseManualTransform;
                    int optimizedCount = -1;
                    VVector optimizedShift = new VVector(0, 0, 0);
                    double optimizedAngle = 0.0;

                    if (usedOptimizedPlacement)
                    {
                        var targetMask = GenerateTargetMask(countTarget);
                        var gridBase = rawGrid.ToList();
                        double baselineRotZ = 0.0;
                        double baselineRotY = 0.0;
                        double baselineRotX = 0.0;
                        if (latticeParams.UsePCA && latticeParams.EnableRotation)
                        {
                            var pcaAngles = ComputePCAAngles(targetMask, countTarget);
                            baselineRotZ = pcaAngles.angleXY;
                            if (!latticeParams.PcaZOnly)
                            {
                                baselineRotY = pcaAngles.angleXZ;
                                baselineRotX = pcaAngles.angleYZ;
                            }
                        }
                        double appliedBaseX = baselineRotX + rotX;
                        double appliedBaseY = baselineRotY + rotY;
                        double appliedBaseZ = baselineRotZ + rotZ;

                        gridBase = RotateGridAroundX(gridBase, centre, appliedBaseX);
                        gridBase = RotateGridAroundY(gridBase, centre, appliedBaseY);
                        gridBase = RotateGrid(gridBase, centre, appliedBaseZ);

                        var shiftCandidates = BuildLocalShiftCandidates(new VVector(shiftX, shiftY, shiftZ), 1.0);
                        int topShifts = shiftCandidates.Count;

                        void EvaluateExactShiftCandidates(List<VVector> candidateGrid, List<int> candidateShiftIndexes, int maskMaxCount, out int bestShift, out double bestAvg, out int bestCount)
                        {
                            bestShift = candidateShiftIndexes.FirstOrDefault();
                            bestAvg = double.MaxValue;
                            bestCount = -1;

                            foreach (var idx in candidateShiftIndexes.Distinct())
                            {
                                var shift = index_to_shift(idx, shiftCandidates);
                                var gridShifted = TranslateGrid(candidateGrid, shift);
                                var gridInTarget = CheckGridInContour(gridShifted, countTarget);
                                int hotCountExact = gridInTarget.Count;
                                if (hotCountExact == 0)
                                {
                                    continue;
                                }

                                double avgDistExact = 0.0;
                                foreach (var pt in gridInTarget)
                                {
                                    avgDistExact += VVector.Distance(pt, centre);
                                }
                                avgDistExact /= hotCountExact;

                                if (hotCountExact > bestCount || (hotCountExact == bestCount && avgDistExact < bestAvg))
                                {
                                    bestCount = hotCountExact;
                                    bestAvg = avgDistExact;
                                    bestShift = idx;
                                }
                            }

                            if (bestCount < 0)
                            {
                                bestCount = maskMaxCount;
                                bestAvg = double.MaxValue;
                            }
                        }

                        void EvalAngle(double angleDeg, List<VVector> candidateGrid, ref int outBestShift, ref double outBestAvg, ref int outBestCount)
                        {
                            var rotGrid = RotateGrid(candidateGrid, centre, angleDeg);
                            double searchMinX = mask_origin_assigned ? mask_min_x : countTarget.MeshGeometry.Bounds.X;
                            double searchMinY = mask_origin_assigned ? mask_min_y : countTarget.MeshGeometry.Bounds.Y;
                            double searchMinZ = mask_origin_assigned ? mask_min_z : countTarget.MeshGeometry.Bounds.Z;
                            int maskCount;
                            var candidateShifts = SearchGrid(rotGrid, targetMask, searchMinX, searchMinY, searchMinZ, out maskCount, topShifts, shiftCandidates);
                            EvaluateExactShiftCandidates(rotGrid, candidateShifts, maskCount, out int exactBestShift, out double exactBestAvg, out int exactBestCount);
                            outBestShift = exactBestShift;
                            outBestAvg = exactBestAvg;
                            outBestCount = exactBestCount;
                        }

                        int globalBestCount = -1;
                        double globalBestAvg = double.MaxValue;
                        double bestAngle = 0.0;
                        int bestShiftIdx = 0;

                        void ConsiderAngle(double angleDeg)
                        {
                            int count = 0;
                            double avg = double.MaxValue;
                            int shiftIdx = 0;
                            EvalAngle(angleDeg, gridBase, ref shiftIdx, ref avg, ref count);
                            if (count > globalBestCount || (count == globalBestCount && avg < globalBestAvg))
                            {
                                globalBestCount = count;
                                globalBestAvg = avg;
                                bestAngle = angleDeg;
                                bestShiftIdx = shiftIdx;
                            }
                        }

                        ConsiderAngle(0.0);

                        if (latticeParams.EnableRotation)
                        {
                            double fineRange = Math.Max(0.0, latticeParams.RotationFineRange);
                            double fineStep = latticeParams.RotationFineStep > 0.0 ? latticeParams.RotationFineStep : 360.0;
                            for (double a = -fineRange; a <= fineRange; a += fineStep)
                            {
                                ConsiderAngle(a);
                            }
                        }

                        var optShift = index_to_shift(bestShiftIdx, shiftCandidates);
                        gridForCheck = RotateGrid(gridBase, centre, bestAngle);
                        gridForCheck = TranslateGrid(gridForCheck, optShift);

                        optimizedCount = globalBestCount;
                        optimizedShift = ToSignedShift(optShift);
                        optimizedAngle = appliedBaseZ + bestAngle;
                    }
                    else
                    {
                        // Apply rotations (X then Y then Z) about the counted target/core centroid if requested.
                        double previewRotX = rotX;
                        double previewRotY = rotY;
                        double previewRotZ = rotZ;
                        if (latticeParams.UsePCA && latticeParams.EnableRotation && !latticeParams.UseManualTransform)
                        {
                            var pcaMask = GenerateTargetMask(countTarget);
                            var pcaAngles = ComputePCAAngles(pcaMask, countTarget);
                            previewRotZ += pcaAngles.angleXY;
                            if (!latticeParams.PcaZOnly)
                            {
                                previewRotY += pcaAngles.angleXZ;
                                previewRotX += pcaAngles.angleYZ;
                            }
                        }

                        bool rotated = Math.Abs(previewRotX) > 1e-6 || Math.Abs(previewRotY) > 1e-6 || Math.Abs(previewRotZ) > 1e-6;
                        if (rotated)
                        {
                            gridForCheck = RotateGridAroundX(gridForCheck, centre, previewRotX);
                            gridForCheck = RotateGridAroundY(gridForCheck, centre, previewRotY);
                            gridForCheck = RotateGrid(gridForCheck, centre, previewRotZ);
                        }

                        if (Math.Abs(shiftX) > 1e-9 || Math.Abs(shiftY) > 1e-9 || Math.Abs(shiftZ) > 1e-9)
                        {
                            var previewShift = ToCanonicalShift(ClampSignedShiftToBounds(new VVector(shiftX, shiftY, shiftZ)));
                            gridForCheck = TranslateGrid(gridForCheck, previewShift);
                        }
                    }

                    int checkStride = 1;
                    if (!exact && !usedOptimizedPlacement)
                    {
                        const int maxInsideChecks = 12000;
                        checkStride = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, gridForCheck.Count) / maxInsideChecks));
                    }

                    var insideGrid = new List<VVector>();
                    for (int i = 0; i < gridForCheck.Count; i += checkStride)
                    {
                        if (countTarget.IsPointInsideSegment(gridForCheck[i]))
                        {
                            insideGrid.Add(gridForCheck[i]);
                        }
                    }

                    // If exact, this is the true count; otherwise estimate by stride
                    preview.TotalSphereCount = exact ? insideGrid.Count : insideGrid.Count * checkStride;
                    preview.SphereRadiusMm = sphereRadiusMm;

                    if (insideGrid.Count == 0)
                    {
                        preview.IsValid = true;
                        preview.Message = usingSafeCore ? "No sphere centres were found inside the safe core." : "No sphere centres were found inside the target.";
                        return;
                    }

                    int safeMax = Math.Max(1, maxSpheres);
                    int stride = Math.Max(1, (int)Math.Ceiling((double)insideGrid.Count / safeMax));
                    for (int i = 0; i < insideGrid.Count; i += stride)
                    {
                        preview.SphereCenters.Add(insideGrid[i]);
                    }

                    preview.DisplayedSphereCount = preview.SphereCenters.Count;
                    preview.IsValid = true;
                    if (usedOptimizedPlacement)
                    {
                        string seedLabel = (latticeParams.UsePCA && latticeParams.EnableRotation) ? "PCA-seeded " : "";
                        preview.Message = $"{seedLabel}fine-search preview generated. Rotation Z={optimizedAngle:F2} deg, signed shift (X,Y,Z)=({optimizedShift.x:F2}, {optimizedShift.y:F2}, {optimizedShift.z:F2}) mm, hot spheres={optimizedCount}.";
                    }
                    else if (!usingSafeCore)
                    {
                        preview.Message = exact ? "Exact preview generated against the selected target. Safe core was not available." : "Preview generated against the selected target because the safe core was not available.";
                    }
                    else
                    {
                        preview.Message = exact ? "Exact preview generated against the same safe core used for creation." : (checkStride > 1 ? $"Preview generated against safe core using sampled contour checks (stride={checkStride})." : "Preview generated against safe core.");
                    }
                }
                finally
                {
                    LatticeSpacing = previousSpacing;
                    Radius = previousRadius;
                    BodyId = previousBodyId;
                    bodyMargin = previousBodyMargin;
                    OarMargin = previousOarMargin;
                    HighRes = previousHighRes;

                    if (useTemporaryPreviewStructures)
                    {
                        foreach (var id in previewStructureIds)
                        {
                            var previewStructure = ss.Structures.FirstOrDefault(x => x.Id == id);
                            if (previewStructure != null)
                            {
                                try { ss.RemoveStructure(previewStructure); } catch { /* ignore cleanup failures */ }
                            }
                        }
                    }
                }
            });

            return preview;
        }

        private void AddPeakObjective(Structure peak_structure, OptimizationSetup optimizer, PrescriptionConfig pres)
        {
            if (peak_structure == null)
            {
                return;
            }
            if (pres.EnablePeakUpperObjective)
            {
                var upperDose = new DoseValue(pres.Peak_Upper, "cGy");
                optimizer.AddPointObjective(peak_structure, OptimizationObjectiveOperator.Upper, upperDose, 0.0, pres.PeakUpperPriority);
            }
            if (pres.EnablePeakLowerObjective)
            {
                var lowerDose = new DoseValue(pres.Peak_Lower, "cGy");
                optimizer.AddPointObjective(peak_structure, OptimizationObjectiveOperator.Lower, lowerDose, 100.0, pres.PeakLowerPriority);
            }
            if (pres.EnablePeakLower95Objective)
            {
                var lowerDose2 = new DoseValue(pres.Peak_Lower, "cGy");
                optimizer.AddPointObjective(peak_structure, OptimizationObjectiveOperator.Lower, lowerDose2, 95.0, pres.PeakLower95Priority);
            }
        }

        private void AddValleyObjective(Structure valley_structure, OptimizationSetup optimizer, PrescriptionConfig pres)
        {
            if (valley_structure == null || !pres.EnableValleyObjectives)
            {
                return;
            }
            var upperDose = new DoseValue(pres.Valley_Upper, "cGy");
            optimizer.AddPointObjective(valley_structure, OptimizationObjectiveOperator.Upper, upperDose, 0.0, pres.ValleyUpperPriority);
            var lowerDose = new DoseValue(pres.Valley_Lower, "cGy");
            optimizer.AddPointObjective(valley_structure, OptimizationObjectiveOperator.Lower, lowerDose, 100.0, pres.ValleyLowerPriority);
            var eudDose = new DoseValue(pres.Valley_EUD, "cGy");
            optimizer.AddEUDObjective(valley_structure, OptimizationObjectiveOperator.Exact, eudDose, -2.0, pres.ValleyEudPriority);
        }

        // --- begin added: optimization parameter loader and storage ---
        private List<OptimizationParameter> _optimizationParameters = new List<OptimizationParameter>();
        private PrescriptionConfig _prescriptionConfig = null;

        public bool HasOptimizationParameters => _optimizationParameters != null && _optimizationParameters.Count > 0;

        // keep track of structures that received template objectives
        private List<string> OAR_list = new List<string>();

        private class OptimizationParameter
        {
            public string StructureType { get; set; }
            public List<string> Labels { get; set; } = new List<string>();
            public string ObjectiveType { get; set; } // e.g. "dvh" or "mean"
            public string ObjectiveOperator { get; set; } // e.g. "Upper", "Lower", "Exact"
            public string ObjectiveParameter { get; set; } // e.g. "Mean", "V2000", "D20%"
            public string ObjectiveValue { get; set; } // value in cGy or cc
            public double Priority { get; set; } = 30.0;
            public bool ApplyDoseReduction { get; set; } = true;
        }

        /// <summary>
        /// Load optimization parameters from CSV. Call this before SetupOptimizer().
        /// CSV header: StructureType,Labels,ObjectiveType,ObjectiveOperator,ObjectiveParameter,ObjectiveValue
        /// Labels are semicolon separated substrings used to match structure.Id
        /// </summary>
        public void LoadOptimizationParameters(string csvPath)
        {
            _optimizationParameters.Clear();
            if (!File.Exists(csvPath)) return;

            var lines = File.ReadAllLines(csvPath);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (i == 0 && line.ToLower().StartsWith("structuretype")) continue;

                // naive CSV split (no quoted fields). Good for simple templates.
                var parts = line.Split(new[] { ',' }, StringSplitOptions.None);
                if (parts.Length < 6) continue;

                var param = new OptimizationParameter
                {
                    StructureType = parts[0].Trim(),
                    Labels = parts[1].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList(),
                    ObjectiveType = parts[2].Trim().ToLower(),
                    ObjectiveOperator = parts[3].Trim(),
                    ObjectiveParameter = parts[4].Trim(),
                    ObjectiveValue = parts[5].Trim()
                };
                if (parts.Length >= 7 && TryParseInvariant(parts[6].Trim(), out var priority))
                {
                    param.Priority = priority;
                }
                if (parts.Length >= 8 && bool.TryParse(parts[7].Trim(), out var applyDoseReduction))
                {
                    param.ApplyDoseReduction = applyDoseReduction;
                }

                _optimizationParameters.Add(param);
            }
        }

        public bool LoadPrescriptionParameters(string jsonPath)
        {
            var cfg = PrescriptionConfig.Load(jsonPath);
            if (cfg == null)
            {
                return false;
            }

            _prescriptionConfig = cfg;
            return true;
        }

        /// <summary>
        /// Load the bundled CSV from the application's output folder (templates\washu_objectives.csv).
        /// Call this once at startup (before SetupOptimizer).
        /// </summary>
        public void LoadDefaultOptimizationParameters()
        {
            // runtime folder (bin\Debug|Release\netX\)
            var AssemblyLocation = Assembly.GetExecutingAssembly().Location;
            var baseDir = Path.GetDirectoryName(AssemblyLocation);
            // var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var cfgPath = Path.Combine(baseDir, "templates", "washu_objectives.csv");

            if (File.Exists(cfgPath))
            {
                LoadOptimizationParameters(cfgPath);
            }
            else
            {
                MessageBox.Show("Default optimization parameter template not found:\n" + cfgPath);
                // optional: silently ignore or throw/log; kept silent to avoid ESAPI context issues
            }

            var presPath = Path.Combine(baseDir, "templates", "washu_prescription.json");
            if (File.Exists(presPath))
            {
                LoadPrescriptionParameters(presPath);
            }
        }

        private OptimizationObjectiveOperator ParseOperatorOrDefault(string opStr, OptimizationObjectiveOperator defaultOp)
        {
            if (string.IsNullOrWhiteSpace(opStr)) return defaultOp;
            if (Enum.TryParse<OptimizationObjectiveOperator>(opStr, true, out var parsed))
            {
                return parsed;
            }
            return defaultOp;
        }

        // Template/objective numbers always use '.' as the decimal separator, so
        // parse them with InvariantCulture rather than the current thread culture
        // (which, on Citrix, is the per-user OS locale). Pairs with the culture
        // pin in Script.Execute. Mirrors the default NumberStyles of
        // double.TryParse(string, out double).
        private static bool TryParseInvariant(string s, out double value)
            => double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands,
                               CultureInfo.InvariantCulture, out value);

        // helper to apply loaded parameters to structures
        private void ApplyLoadedObjectives(OptimizationSetup optimizer, LatticeParameters latticeParams)
        {
            if (_optimizationParameters == null || _optimizationParameters.Count == 0) return;

            // clear previous tracking list
            OAR_list.Clear();

            foreach (var opt in _optimizationParameters)
            {
                foreach (var label in opt.Labels)
                {
                    List<Structure> matches;
                    if (label.Equals("__SELECTED_OARS__", StringComparison.OrdinalIgnoreCase))
                    {
                        var selectedOars = FilterSelectedOars(latticeParams?.OarStructureNames);
                        matches = selectedOars
                            .Select(id => _ss.Structures.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                            .Where(s => s != null)
                            .ToList();
                    }
                    else
                    {
                        matches = _ss.Structures
                            .Where(s => s.Id.Equals(label, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    }

                    foreach (var s in matches)
                    {
                        try
                        {
                            bool addedForStructure = false;

                            // decide operator default: Upper
                            var defaultOp = OptimizationObjectiveOperator.Upper;
                            var opToUse = ParseOperatorOrDefault(opt.ObjectiveOperator, defaultOp);
                            double weight = opt.Priority;


                            if (opt.ObjectiveType == "dvh")
                            {
                                // Parse if it's a volume or dose based objective
                                if (opt.ObjectiveParameter.StartsWith("d", StringComparison.OrdinalIgnoreCase))
                                {
                                    double doseDouble = 0;
                                    TryParseInvariant(opt.ObjectiveValue, out doseDouble);
                                    var dv = new DoseValue(doseDouble, "cGy");
                                    if (opt.ObjectiveParameter.EndsWith("cc"))
                                    {
                                        // e.g. D20cc
                                        double volCc = 0;
                                        var volStr = opt.ObjectiveParameter.Substring(1).Replace("cc", "");
                                        TryParseInvariant(volStr, out volCc);
                                        double volPercent = volCc / s.Volume * 100.0;
                                        optimizer.AddPointObjective(s, opToUse, dv, volPercent, weight);
                                        addedForStructure = true;
                                    }
                                    else
                                    {
                                        // e.g. D20%
                                        double volPercent = 0;
                                        var volStr = opt.ObjectiveParameter.Substring(1).Replace("%", "");
                                        TryParseInvariant(volStr, out volPercent);
                                        optimizer.AddPointObjective(s, opToUse, dv, volPercent, weight);
                                        addedForStructure = true;
                                    }
                                }
                                else if (opt.ObjectiveParameter.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Check if it's volume spared or volume treated
                                    if (opt.ObjectiveParameter.StartsWith("vs", StringComparison.OrdinalIgnoreCase))
                                    {
                                        string doseString = opt.ObjectiveParameter.Substring(2);
                                        double dosecGy = 0;
                                        TryParseInvariant(doseString, out dosecGy);
                                        var dv = new DoseValue(dosecGy, "cGy");

                                        if (opt.ObjectiveValue.EndsWith("cc"))
                                        {
                                            string volString = opt.ObjectiveValue.Substring(2).Replace("cc", "");
                                            double volCc = 0;
                                            TryParseInvariant(volString, out volCc);

                                            double structureVolume = s.Volume;
                                            double volSparedPercent = (1.0 - volCc / structureVolume) * 100.0;

                                            optimizer.AddPointObjective(s, opToUse, dv, volSparedPercent, weight);
                                            addedForStructure = true;
                                        }
                                        else
                                        {
                                            string volString = opt.ObjectiveValue.Substring(2).Replace("%", "");
                                            double volPercent = 0;
                                            TryParseInvariant(volString, out volPercent);
                                            double volSparedPercent = 100.0 - volPercent;
                                            optimizer.AddPointObjective(s, opToUse, dv, volSparedPercent, weight);
                                            addedForStructure = true;
                                        }
                                    }
                                    else
                                    {
                                        string doseString = opt.ObjectiveParameter.Substring(1);
                                        double dosecGy = 0;
                                        TryParseInvariant(doseString, out dosecGy);
                                        var dv = new DoseValue(dosecGy, "cGy");

                                        if (opt.ObjectiveValue.EndsWith("cc"))
                                        {
                                            var volStr = opt.ObjectiveValue.Replace("cc", "");
                                            double volCc = 0;
                                            TryParseInvariant(volStr, out volCc);
                                            double volPercent = volCc / s.Volume * 100.0;
                                            optimizer.AddPointObjective(s, opToUse, dv, volPercent, weight);
                                            addedForStructure = true;
                                        }
                                        else
                                        {
                                            var volStr = opt.ObjectiveValue.Replace("%", "");
                                            double volPercent = 0;
                                            TryParseInvariant(volStr, out volPercent);
                                            optimizer.AddPointObjective(s, opToUse, dv, volPercent, weight);
                                            addedForStructure = true;
                                        }

                                    }
                                }
                                else
                                {
                                    // raise exception
                                    throw new Exception("Unknown ObjectiveParameter format: " + opt.ObjectiveParameter);
                                }

                                // Use AddPointObjective with provided operator (Upper/Lower)
                                // optimizer.AddPointObjective(s, opToUse, dv, 0.0, weight);
                            }
                            else if (opt.ObjectiveType == "mean")
                            {
                                double doseDouble = 0;
                                TryParseInvariant(opt.ObjectiveValue, out doseDouble);
                                var dv = new DoseValue(doseDouble, "cGy");
                                // map mean to EUD-style objective; operator typically Exact but use provided if sensible
                                optimizer.AddEUDObjective(s, opToUse, dv, -1.0, weight);
                                addedForStructure = true;
                            }
                            else
                            {
                                // unknown objective type
                                throw new Exception("Unknown ObjectiveType: " + opt.ObjectiveType);
                            }

                            // track that we added at least one objective for this structure
                            if (addedForStructure && opt.ApplyDoseReduction && !OAR_list.Contains(s.Id, StringComparer.OrdinalIgnoreCase))
                            {
                                OAR_list.Add(s.Id);
                            }
                        }
                        catch (Exception)
                        {
                            // swallow to avoid crashing ESAPI context; optionally log error
                        }
                    }
                    if (matches.Count > 0)
                    {
                        // structure matched, no need to check other labels for this parameter
                        break;
                    }
                }
            }
            // After applying template objectives, reduce dose constraints by 5% for objectives associated with tracked OARs.
            // copy list as optimizer.Objectives may be a live collection
            var objectivesCopy = optimizer.Objectives.ToList();
            foreach (var obj in objectivesCopy)
            {
                try
                {
                    // get the structure id for this objective (best-effort via reflection)
                    string structId = null;
                    var structProp = obj.GetType().GetProperty("Structure");
                    if (structProp != null)
                    {
                        var structObj = structProp.GetValue(obj);
                        if (structObj != null)
                        {
                            var idProp = structObj.GetType().GetProperty("Id");
                            structId = idProp?.GetValue(structObj)?.ToString();
                        }
                    }
                    if (string.IsNullOrEmpty(structId))
                    {
                        var idProp2 = obj.GetType().GetProperty("StructureId") ?? obj.GetType().GetProperty("StructureName");
                        structId = idProp2?.GetValue(obj)?.ToString();
                    }

                    if (string.IsNullOrEmpty(structId)) continue;
                    if (!OAR_list.Contains(structId, StringComparer.OrdinalIgnoreCase)) continue;

                    // Gather a reference to the Structure object
                    Structure structRef = null;
                    if (structProp != null)
                    {
                        structRef = structProp.GetValue(obj) as Structure;
                    }
                    if (structRef == null)
                    {
                        structRef = _ss.Structures.FirstOrDefault(s => s.Id.Equals(structId, StringComparison.OrdinalIgnoreCase));
                    }
                    if (structRef == null) continue;

                    // Get operator (best-effort)
                    OptimizationObjectiveOperator op = OptimizationObjectiveOperator.Upper;
                    var opProp = obj.GetType().GetProperty("Operator") ?? obj.GetType().GetProperty("ObjectiveOperator");
                    if (opProp != null)
                    {
                        try
                        {
                            var opObj = opProp.GetValue(obj);
                            if (opObj != null) Enum.TryParse(opObj.ToString(), true, out op);
                        }
                        catch { }
                    }

                    // get volume and weight (fallbacks)
                    double volume = 0.0;
                    double weight = 30.0;
                    var volProp = obj.GetType().GetProperty("Volume") ?? obj.GetType().GetProperty("VolumeFraction");
                    if (volProp != null) TryParseInvariant(volProp.GetValue(obj)?.ToString(), out volume);
                    var wtProp = obj.GetType().GetProperty("Weight");
                    if (wtProp != null) TryParseInvariant(wtProp.GetValue(obj)?.ToString(), out weight);

                    // read existing dose value (best-effort)
                    double existingDoseValue = 0.0;
                    string existingDoseUnit = "cGy";
                    var doseProp = obj.GetType().GetProperty("Dose") ?? obj.GetType().GetProperty("DoseValue");
                    if (doseProp != null && doseProp.CanRead)
                    {
                        var doseObj = doseProp.GetValue(obj);
                        if (doseObj != null)
                        {
                            var dValProp = doseObj.GetType().GetProperty("Dose");
                            var dUnitProp = doseObj.GetType().GetProperty("Unit");
                            if (dValProp != null)
                            {
                                TryParseInvariant(dValProp.GetValue(doseObj)?.ToString(), out existingDoseValue);
                            }
                            if (dUnitProp != null)
                            {
                                existingDoseUnit = dUnitProp.GetValue(doseObj)?.ToString() ?? existingDoseUnit;
                            }
                        }
                    }

                    // remove and re-add with 5% lower dose if we have a dose value
                    if (existingDoseValue > 0)
                    {
                        var newDoseVal = new DoseValue(existingDoseValue * 0.95, existingDoseUnit);
                        try
                        {
                            optimizer.RemoveObjective(obj);
                            optimizer.AddPointObjective(structRef, op, newDoseVal, volume, weight);
                        }
                        catch
                        {
                            // swallow to avoid ESAPI context errors
                        }
                    }
                }
                catch
                {
                    // ignore individual objective failures
                }
            }
        }

        public async Task OptimizeLattice(IProgress<string> progress = null, LatticeParameters latticeParams = null)
        {
            if (latticeParams == null)
            {
                throw new ArgumentNullException(nameof(latticeParams), "Lattice parameters must be provided.");
            }

            await SetupOptimizer(latticeParams);
            await _ew.AsyncRunPlanContext((pat, ps) =>
            {
                // This is required to update the structure set reference after changes
                _ss = ps.StructureSet;
                progress?.Report("\nLattice optimization setup complete.");
                ApplyLoadedObjectives(Optimizer, latticeParams);
                progress?.Report("\nApplied template objectives.");
                // var plan = (ExternalPlanSetup)ps;
                // plan.OptimizeVMAT();
                // progress?.Report("\nOptimization complete. Test");
            });
        }
    }
}
