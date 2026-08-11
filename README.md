# SFRT_ESAPI

SFRT_ESAPI is a Varian Eclipse ESAPI script for spatially fractionated radiation therapy (SFRT) and lattice radiation therapy planning. It creates lattice peak and valley structures, tuning structures, VMAT beams, and optimization objectives from UI selections and local protocol templates.

> [!warning]
> This script is a planning aid and research/clinical workflow tool. It must be commissioned, validated, and approved locally before clinical use. The template doses and objectives in this repository are examples and may not match your institution's protocol.

## Who This Is For

This README is intended for new users and local maintainers who need to:

- Build and deploy the ESAPI script.
- Configure local machines and energies.
- Understand the WashU, Mayo, and Custom preset files.
- Adjust prescription/objective templates safely.
- Run the current preview, structure creation, and objective setup workflow.

## Requirements

- Varian Eclipse with ESAPI scripting enabled.
- Eclipse v18+ for the SABR/SBRT NTO behavior used by optimization setup.
- Windows build environment with Visual Studio/MSBuild and the .NET Framework 4.8 Developer Pack.
- Access to a non-clinical Eclipse environment for commissioning and testing.

The project targets .NET Framework 4.8. The cross-platform `dotnet` SDK alone is usually not enough unless the .NET Framework 4.8 reference assemblies are also available.

## Repository Layout

- `SFRT_PlanningScript.sln`: Visual Studio solution.
- `SFRT_PlanningScript.csproj`: Main script project.
- `ESAPIWorker/`: ESAPI dispatcher/context helpers.
- `Model/Model.cs`: Main geometry, structure creation, beam setup, preview, and optimization logic.
- `ViewModels/SphereDialogViewModel.cs`: UI state and workflow orchestration.
- `Views/SphereDialog.xaml`: Main WPF interface.
- `templates/`: Protocol objective and prescription templates.
- `configs/machines.config`: Local linac IDs, display names, and available energies.

Build output copies the runtime configuration files into:

- `bin/x64/Release/templates/`
- `bin/x64/Release/configs/`

These runtime copies are what Eclipse uses when the script is launched from the built `.esapi` assembly.

## Build And Deploy

1. Open `SFRT_PlanningScript.sln` in Visual Studio on a machine with .NET Framework 4.8 Developer Pack installed.
2. Edit `configs/machines.config` for your local treatment units.
3. Review and localize the files in `templates/`.
4. Build the solution in `Release|x64`.
5. Confirm the output folder contains:
   - `SFRT_PlanningScript.esapi.dll`
   - `templates/*.csv`
   - `templates/*.json`
   - `configs/machines.config`
6. Link or copy `SFRT_PlanningScript.esapi.dll` to your Eclipse scripts location according to your local ESAPI deployment process.
7. Test in a non-clinical patient/course before clinical use.

## Runtime Configuration Files

The script expects configuration files next to the built assembly:

```text
bin/x64/Release/
  SFRT_PlanningScript.esapi.dll
  configs/
    machines.config
  templates/
    washu_objectives.csv
    washu_prescription.json
    mayo_objectives.csv
    mayo_prescription.json
```

### `configs/machines.config`

This JSON file controls the machine dropdown and available energy dropdown.

Example entry:

```json
{
  "MachineId": "ACUnit1TB",
  "DisplayName": "Unit 1",
  "Energies_MV": ["6X", "6X-FFF", "10X", "10X-FFF", "15X"],
  "Notes": "HDMLCs"
}
```

Fields:

- `MachineId`: The Eclipse machine ID used when beams are created.
- `DisplayName`: Friendly UI label.
- `Energies_MV`: Energies shown when the machine is selected.
- `Notes`: Optional human-readable notes.

If the file is missing or invalid, the script falls back to built-in example machine IDs and energies. Sites should not rely on those fallback values clinically.

## Protocol Presets

The UI currently lists:

- `WashU`
- `Mayo`
- `Custom`

Each preset is loaded from two files:

```text
templates/{preset}_objectives.csv
templates/{preset}_prescription.json
```

For example, selecting Mayo loads:

```text
templates/mayo_objectives.csv
templates/mayo_prescription.json
```

If you want to use `Custom`, add:

```text
templates/custom_objectives.csv
templates/custom_prescription.json
```

The preset files must be present in the build output `templates/` folder. The project copies files from the repository `templates/` folder on build.

## Prescription JSON Templates

Prescription JSON files control built-in target, peak, valley, ring, and body-minus-spheres objectives. Dose values are in cGy.

Common keys:

- `PTVLow`: Adds a lower point objective to the selected PTV-low structure. If omitted, this objective is skipped.
- `PTVLowPriority`: Priority for the `PTVLow` objective.
- `Peak_Upper`: Upper objective dose for peak structures.
- `Peak_Lower`: Lower objective dose for peak structures.
- `EnablePeakUpperObjective`: Enables/disables peak upper objective.
- `EnablePeakLowerObjective`: Enables/disables peak lower objective.
- `EnablePeakLower95Objective`: Enables/disables a 95% lower peak objective.
- `PeakUpperPriority`, `PeakLowerPriority`, `PeakLower95Priority`: Peak objective priorities.
- `EnableRepresentativePeakObjective`: Adds one representative peak objective to the first peak structure.
- `RepresentativePeakDose`, `RepresentativePeakVolumePercent`, `RepresentativePeakPriority`: Representative peak objective settings.
- `TS_Peak_Ring1`, `TS_Peak_Ring2`, `TS_Peak_Ring3`: Tuning-ring objective doses.
- `TS_Peak_Ring1Priority`, `TS_Peak_Ring2Priority`, `TS_Peak_Ring3Priority`: Tuning-ring objective priorities.
- `TS_Ring1`, `TS_Ring2`: Legacy/global tuning ring doses used when matching structures exist.
- `EnableValleyObjectives`: Enables/disables valley objectives.
- `Valley_Upper`, `Valley_Lower`, `Valley_EUD`: Valley objective doses.
- `CreateBodyMinusSpheresStructure`: Creates a body-minus-spheres control structure.
- `BodyMinusSpheresStructureId`: Name for that structure, usually `BODY_Spheres`.
- `EnableBodyMinusSpheresObjective`: Adds an objective to `BodyMinusSpheresStructureId`.
- `BodyMinusSpheresDose`, `BodyMinusSpheresVolumePercent`, `BodyMinusSpheresPriority`: Body-minus-spheres objective settings.

### WashU Prescription

`washu_prescription.json` includes `PTVLow`, peak objectives, valley objectives, and grouped tuning-ring doses.

Current WashU behavior:

- Peak structures are grouped by slice/row unless the UI option for per-sphere tuning structures is enabled.
- Tuning rings are generated as grouped `TS_Peak_Ring1`, `TS_Peak_Ring2`, and `TS_Peak_Ring3` structures in the default grouped mode.
- Valley objectives are enabled by default through the prescription defaults/templates.

### Mayo Prescription

`mayo_prescription.json` intentionally omits `PTVLow`, so the PTV-low point objective is skipped.

Current Mayo behavior:

- Peak spheres stay individual as `TS_Peak_#`.
- Per-sphere temporary ring geometry is combined by shell.
- Combined tuning shells are named:
  - `TS_Peak_Ring_Inner`
  - `TS_Peak_Ring_Middle`
  - `TS_Peak_Ring_Outer`
- Temporary per-sphere ring helper structures are removed after the combined shells are created.
- `CombineTuningRingShells: true` controls this behavior.
- `BODY_Spheres` can be created and can receive an objective if enabled in the template.

## Objective CSV Templates

Objective CSV files add additional objectives after the built-in prescription objectives are set.

Header:

```csv
StructureType,Labels,ObjectiveType,ObjectiveOperator,ObjectiveParameter,ObjectiveValue,Priority,ApplyDoseReduction
```

Supported fields:

- `StructureType`: Informational/grouping label.
- `Labels`: Semicolon-separated structure IDs or aliases to match. The special label `__SELECTED_OARS__` applies the row to all OARs selected in the UI.
- `ObjectiveType`: `dvh` or `mean`.
- `ObjectiveOperator`: Usually `Upper`, `Lower`, or `Exact`.
- `ObjectiveParameter`: Examples include `D0`, `D20%`, `D2cc`, `V1800`, `V1800cc`, `VS1250`.
- `ObjectiveValue`: Dose in cGy for D-type objectives, or volume for V/VS-type objectives.
- `Priority`: Optional objective priority. If omitted, code defaults are used.
- `ApplyDoseReduction`: Optional `true`/`false`; used by the later dose-reduction workflow for selected OARs.

Examples:

```csv
OAR,__SELECTED_OARS__,dvh,Upper,D0,800,10,false
SpinalCord,SpinalCord;Cord;Spinal_Cord,dvh,Upper,D0,2800
Brainstem,Brainstem,mean,Upper,D0,3100
```

Notes:

- The CSV parser is simple and does not support quoted commas.
- Structure labels are matched case-insensitively against structure IDs.
- Rows that do not match any structures are skipped.

## Current Planning Workflow

1. Open a patient and plan in Eclipse.
2. Run the script.
3. Select a preset: `WashU`, `Mayo`, or `Custom`.
4. Select:
   - GTV/target structure.
   - PTV-low structure.
   - Body/external structure.
   - OARs and OAR margins.
   - Machine and energy.
   - Sphere diameter and lattice spacing.
5. Use the preview panel to inspect geometry.
6. Click `Create structures`.
7. Review created structures in Eclipse.
8. If needed, adjust shift/rotation sliders and run `Preview 3D` or `Refine preview`.
9. Click `Create structures` again to regenerate structures with updated preview parameters.
10. Click `Set Objectives`/optimize workflow to apply objectives.
11. Continue with normal Eclipse inverse planning and institutional QA.

## Preview Behavior

The preview panel is designed to reduce unnecessary ESAPI structure writes.

### Live Preview

Before structures are created, slider changes trigger an approximate live preview. This path is read-oriented and does not create `zzz_preview_*` helper structures.

Live preview displays:

- Target/core boundary.
- Hot sphere centers.
- Selected OAR meshes as translucent overlays.

After `Create structures` has completed, automatic slider-triggered live preview is paused to avoid repeated ESAPI calls while the structure set contains generated lattice structures.

### Preview 3D

`Preview 3D` is an explicit preview request. It is allowed before and after structure creation. After a successful `Create structures` run, the script automatically calls this explicit preview path once so the preview window refreshes.

### Refine Preview

`Refine preview` is the exact preview path. It may create temporary `zzz_preview_*` structures to match the structure-generation safe-core logic more closely. It is gated so it cannot run at the same time as structure creation.

Use `Refine preview` when you want to:

- Recompute preview from current shift/rotation values.
- Fine-tune after an initial structure creation.
- Then run `Create structures` again using the updated parameters.

## Generated Structures

Common structures include:

- `zzz_GTV_core`: Temporary safe core used during structure generation.
- `zzz_SFRT_PRV`: Temporary PRV structure.
- `PTV_Peak`: Union of peak spheres.
- `TS_Peak_#`: Peak sphere structures.
- `PTV_Valley`: Union of valley spheres.
- `TS_Valley_#`: Valley structures.
- `PTV_Control`: PTV control structure outside peak margins.
- `TS_Peak_Ring1`, `TS_Peak_Ring2`, `TS_Peak_Ring3`: Default grouped tuning rings.
- `TS_Peak_Ring_Inner`, `TS_Peak_Ring_Middle`, `TS_Peak_Ring_Outer`: Mayo combined tuning-ring shells.
- `BODY_Spheres`: Optional body-minus-spheres control structure.

The script overwrites generated structures with the same IDs when rerun.

## Validation And QA

Before clinical use:

- Validate structure generation on phantom and retrospective cases.
- Confirm lattice placement, sphere diameter, spacing, OAR avoidance, and generated tuning structures.
- Validate all prescription JSON values and objective CSV rows against local protocol.
- Check beam geometry, jaw fitting, dose calculation settings, and optimization settings.
- Perform end-to-end dosimetric QA according to local policy.

## Troubleshooting

### Preset Fails To Load

Confirm both files exist in the runtime `templates/` folder:

```text
{preset}_objectives.csv
{preset}_prescription.json
```

For `Custom`, create `custom_objectives.csv` and `custom_prescription.json`.

### Machine Or Energy List Is Wrong

Check the runtime copy of:

```text
configs/machines.config
```

The `MachineId` must match the Eclipse machine ID.

### Build Fails With .NET Framework Reference Assembly Error

Install the .NET Framework 4.8 Developer Pack/targeting pack and build from Visual Studio or MSBuild on Windows.

### Preview Does Not Auto-Update After Create

This is intentional for slider-triggered live preview. Use `Preview 3D` or `Refine preview` after structure creation. This avoids repeated ESAPI preview calls while generated structures exist.

### Objectives Are Missing

Check:

- The selected preset loaded successfully.
- The prescription JSON contains the objective keys you expect.
- The objective CSV labels match actual Eclipse structure IDs.
- `PTVLow` is present only if you want the PTV-low point objective.

## Quick Commissioning Example

Use a non-clinical patient or phantom CT with a simple target.

Suggested starting test:

- GTV: roughly spherical target, 4 to 8 cm diameter.
- Sphere diameter: 1.0 cm.
- Lattice spacing: 4 times sphere diameter.
- Body margin: 15 mm.
- OAR margins: 15 mm.
- Preset: start with `WashU` or a local `Custom` copy.

Test workflow:

1. Run the script.
2. Select target, PTV-low, body, machine, energy, and OARs.
3. Generate Preview 3D.
4. Create structures.
5. Inspect all generated structures in Eclipse.
6. Apply objectives.
7. Optimize and review dose.
8. Repeat using shifted/rotated lattice settings.

The included values are examples only. Replace them with locally validated clinical values before patient use.
