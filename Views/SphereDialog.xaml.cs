using SFRT_PlanningScript.Models;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using ESAPIScript;
using VMS.TPS.Common.Model.API;


namespace SFRT_PlanningScript.Views
{
    /// <summary>
    /// Interaction logic for SphereDialog.xaml
    /// </summary>
    /// 
    public partial class SphereDialog : UserControl
    {
        private readonly System.Windows.Threading.DispatcherTimer _previewDebounceTimer;
        private bool _isPreviewRunning = false;
        private readonly SphereDialogViewModel vm;
        private bool _isPreviewCameraDragging = false;
        private Point _lastPreviewCameraPoint;
        private MouseButton _previewCameraButton = MouseButton.Left;
        private Point3D _previewCameraTarget = new Point3D(0, 0, 0);
        private bool _hasPreviewCameraDefault = false;
        private Point3D _defaultPreviewCameraTarget;
        private Point3D _defaultPreviewCameraPosition;
        private Vector3D _defaultPreviewCameraLookDirection;
        private Vector3D _defaultPreviewCameraUpDirection;
        private double _defaultPreviewCameraFieldOfView;
        public TextBoxOutputter outputter;

        public SphereDialog(EsapiWorker EsapiWorker)
        {
            InitializeComponent();
            vm = new SphereDialogViewModel(EsapiWorker, this.Dispatcher);
            DataContext = vm;
            _previewDebounceTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _previewDebounceTimer.Tick += async (s, ev) =>
            {
                _previewDebounceTimer.Stop();
                await RunPreviewOnce();
            };
        }

        void TimerTick(object state)
        {
            var who = state as string;
            Console.WriteLine(who);
        }

        private void ToggleCircle(object sender, MouseButtonEventArgs e)
        {
            var selectedEllipse = (System.Windows.Shapes.Ellipse)sender;
            Circle selectedCircle = (Circle)selectedEllipse.DataContext;
            selectedCircle.Selected = !selectedCircle.Selected;
        }

        private async void CreateLattice(object sender, RoutedEventArgs e)
        {
            _previewDebounceTimer.Stop();
            bool created = await vm.CreateLattice();
            if (!created)
            {
                return;
            }

            await GenerateAndRenderPreview(allowAfterCreation: true);
        }

        private async void Optimize(object sender, RoutedEventArgs e)
        {
            try
            {
                bool completed = await vm.Optimize();
                if (!completed)
                {
                    return;
                }

                // After optimization completes, close any open progress windows or dialogs.
                CloseProgressWindows();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Optimization failed:\n{ex.Message}");
            }
        }

        private void CloseProgressWindows()
        {
            try
            {
                foreach (Window w in System.Windows.Application.Current.Windows)
                {
                    // look for any window that has a ProgressBar control
                    var hasProgress = false;
                    try
                    {
                        hasProgress = w.FindName("ProgressBar") != null;
                    }
                    catch { }

                    if (hasProgress)
                    {
                        w.Close();
                    }
                }
            }
            catch { }
        }

        private void AddRoi(object sender, RoutedEventArgs e)
        {
            // The picker is now a single-select ComboBox bound to SelectedOar.
            vm.AddRoi();
        }

        private void RemoveRoi(object sender, RoutedEventArgs e)
        {
            vm.RemoveRoi();
        }

        private void RemoveOarChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is SFRT_PlanningScript.OarEntry entry)
            {
                vm.RemoveRoi(entry);
            }
        }

        private void ShowPreview_Click(object sender, RoutedEventArgs e)
        {
            SetPreviewVisible(true);
        }

        private void HidePreview_Click(object sender, RoutedEventArgs e)
        {
            SetPreviewVisible(false);
        }

        private void SetPreviewVisible(bool visible)
        {
            if (PreviewColumn != null)
            {
                PreviewColumn.Width = visible ? new GridLength(460) : new GridLength(0);
            }
            if (PreviewPanel != null)
            {
                PreviewPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
            if (ShowPreviewBtn != null)
            {
                ShowPreviewBtn.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void OpenAdvancedDrawer_Click(object sender, RoutedEventArgs e)
        {
            if (AdvancedDrawerOverlay != null) AdvancedDrawerOverlay.Visibility = Visibility.Visible;
        }

        private void CloseAdvancedDrawer_Click(object sender, RoutedEventArgs e)
        {
            if (AdvancedDrawerOverlay != null) AdvancedDrawerOverlay.Visibility = Visibility.Collapsed;
        }

        private void ResetAdvancedDefaults_Click(object sender, RoutedEventArgs e)
        {
            vm.RotationCoarseRange = 30.0;
            vm.RotationCoarseStep = 5.0;
            vm.RotationFineRange = 5.0;
            vm.RotationFineStep = 1.0;
            vm.RotationTopShifts = 10;
        }

        private void OpenLogDrawer_Click(object sender, RoutedEventArgs e)
        {
            if (LogDrawerOverlay != null) LogDrawerOverlay.Visibility = Visibility.Visible;
            // Scroll the log to the bottom on open
            if (LogTextBox != null)
            {
                LogTextBox.CaretIndex = LogTextBox.Text?.Length ?? 0;
                LogTextBox.ScrollToEnd();
            }
        }

        private void CloseLogDrawer_Click(object sender, RoutedEventArgs e)
        {
            if (LogDrawerOverlay != null) LogDrawerOverlay.Visibility = Visibility.Collapsed;
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            if (vm != null) vm.Output = string.Empty;
        }

        private void PresetTemplate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (vm == null) return;
            int idx = vm.SelectedPresetTemplate;
            if (idx < 0 || idx >= vm.PresetTemplates.Count) return;
            vm.LoadPresetTemplate(idx, silent: true);
        }

        private async void Preview3D_Click(object sender, RoutedEventArgs e)
        {
            await GenerateAndRenderPreview(allowAfterCreation: true);
        }

        private async Task GenerateAndRenderPreview(bool allowAfterCreation = false)
        {
            PreviewStatsText.Text = "Generating preview ...";
            try
            {
                var preview = await vm.GeneratePreviewData(allowAfterCreation);
                if (preview == null || !preview.IsValid)
                {
                    PreviewStatsText.Text = preview?.Message ?? "Preview generation failed.";
                    return;
                }

                RenderPreview(preview);
                PreviewStatsText.Text = $"Showing {preview.DisplayedSphereCount}/{preview.TotalSphereCount} spheres.";
            }
            catch (Exception ex)
            {
                PreviewStatsText.Text = "Preview generation failed.";
                MessageBox.Show($"Unable to generate 3D preview: {ex.Message}");
            }
        }

        private void ShiftBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (sender is TextBox box)
            {
                var binding = box.GetBindingExpression(TextBox.TextProperty);
                binding?.UpdateSource();
                // Re-read the view-model value so a clamped or rejected entry is
                // shown as what was actually applied.
                binding?.UpdateTarget();
                box.SelectAll();
                e.Handled = true;
            }
        }

        private void ShiftBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            }
        }

        private void PreviewSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            // Debounce rapid slider events to avoid spamming preview generation
            try
            {
                PreviewStatsText.Text = "Updating preview...";
                _previewDebounceTimer.Stop();
                _previewDebounceTimer.Start();
            }
            catch { }
        }

        private async Task RunPreviewOnce()
        {
            if (_isPreviewRunning) return;
            _isPreviewRunning = true;
            try
            {
                var preview = await vm.GeneratePreviewData();
                if (preview == null || !preview.IsValid)
                {
                    PreviewStatsText.Text = preview?.Message ?? "Preview generation failed.";
                    return;
                }
                RenderPreview(preview);
                PreviewStatsText.Text = $"Showing {preview.DisplayedSphereCount}/{preview.TotalSphereCount} spheres.";
            }
            catch (Exception)
            {
                // ignore transient preview errors
                PreviewStatsText.Text = "Preview error.";
            }
            finally
            {
                _isPreviewRunning = false;
            }
        }

        private async void RefineGenerate_Click(object sender, RoutedEventArgs e)
        {
            var cts = new CancellationTokenSource();

            var progressWindow = new Window { Title = "Refine Preview", Width = 500, Height = 300 };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var tb = new System.Windows.Controls.TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
            Grid.SetRow(tb, 0);
            grid.Children.Add(tb);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelBtn = new Button { Content = "Cancel", Margin = new Thickness(6) };
            cancelBtn.Click += (s, ev) => { cts.Cancel(); cancelBtn.IsEnabled = false; };
            btnPanel.Children.Add(cancelBtn);
            Grid.SetRow(btnPanel, 1);
            grid.Children.Add(btnPanel);

            progressWindow.Content = grid;
            progressWindow.Show();

            var progress = new Progress<string>(s =>
            {
                tb.AppendText(s + "\n");
                tb.ScrollToEnd();
            });

            try
            {
                var preview = await vm.RefinePreview(cts.Token, progress);
                if (preview != null && preview.IsValid)
                {
                    RenderPreview(preview);
                    PreviewStatsText.Text = $"Showing {preview.DisplayedSphereCount}/{preview.TotalSphereCount} spheres.";
                }
                else
                {
                    PreviewStatsText.Text = preview?.Message ?? "Refined preview failed.";
                }

                if (!cts.IsCancellationRequested)
                    MessageBox.Show("Refined preview ready. Adjust manually if needed, then click Create to write structures.");
                else
                    MessageBox.Show("Refine Preview cancelled.");
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show("Refine Preview cancelled.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Refine Preview failed: " + ex.Message);
            }
            finally
            {
                progressWindow.Close();
            }
        }

        private void RenderPreview(SpherePreviewData preview)
        {
            PreviewSceneRoot.Children.Clear();
            PreviewSceneRoot.Children.Add(new AmbientLight(Color.FromRgb(80, 80, 80)));
            PreviewSceneRoot.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.5, -0.6, -1.0)));

            var sphereMesh = CreateUnitSphereMesh(8, 8);
            var sphereMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(190, 240, 120, 50)));
            foreach (var c in preview.SphereCenters)
            {
                var t = new Transform3DGroup();
                t.Children.Add(new ScaleTransform3D(preview.SphereRadiusMm, preview.SphereRadiusMm, preview.SphereRadiusMm));
                t.Children.Add(new TranslateTransform3D(c.x, c.y, c.z));
                PreviewSceneRoot.Children.Add(new GeometryModel3D
                {
                    Geometry = sphereMesh,
                    Material = sphereMaterial,
                    BackMaterial = sphereMaterial,
                    Transform = t
                });
            }

            var targetMesh = CreateTargetMesh(preview);
            var targetBrush = new SolidColorBrush(Color.FromRgb(70, 140, 220))
            {
                Opacity = 0.08
            };
            var targetMaterial = new DiffuseMaterial(targetBrush);
            var targetModel = new GeometryModel3D
            {
                Geometry = targetMesh,
                Material = targetMaterial,
                BackMaterial = targetMaterial
            };
            // Draw the transparent boundary after sphere markers so WPF depth sorting does not hide spheres inside it.
            PreviewSceneRoot.Children.Add(targetModel);

            var oarColors = new[]
            {
                Color.FromRgb(225, 80, 95),
                Color.FromRgb(245, 170, 65),
                Color.FromRgb(95, 200, 125),
                Color.FromRgb(130, 120, 235),
                Color.FromRgb(235, 105, 190)
            };
            for (int i = 0; preview.OarMeshes != null && i < preview.OarMeshes.Count; i++)
            {
                var oarMesh = CreatePreviewMesh(preview.OarMeshes[i].Vertices, preview.OarMeshes[i].TriangleIndices);
                if (oarMesh == null)
                {
                    continue;
                }

                var oarBrush = new SolidColorBrush(oarColors[i % oarColors.Length])
                {
                    Opacity = 0.16
                };
                var oarMaterial = new DiffuseMaterial(oarBrush);
                PreviewSceneRoot.Children.Add(new GeometryModel3D
                {
                    Geometry = oarMesh,
                    Material = oarMaterial,
                    BackMaterial = oarMaterial
                });
            }

            PositionCamera(preview);
        }

        private MeshGeometry3D CreateTargetMesh(SpherePreviewData preview)
        {
            if (preview.TargetVertices != null && preview.TargetTriangleIndices != null &&
                preview.TargetVertices.Count >= 9 && preview.TargetTriangleIndices.Count >= 3)
            {
                return CreatePreviewMesh(preview.TargetVertices, preview.TargetTriangleIndices);
            }

            return CreateBoxMesh(preview.MinX, preview.MaxX, preview.MinY, preview.MaxY, preview.MinZ, preview.MaxZ);
        }

        private MeshGeometry3D CreatePreviewMesh(System.Collections.Generic.List<double> vertices, System.Collections.Generic.List<int> triangleIndices)
        {
            if (vertices == null || triangleIndices == null || vertices.Count < 9 || triangleIndices.Count < 3)
            {
                return null;
            }

            var mesh = new MeshGeometry3D();
            for (int i = 0; i + 2 < vertices.Count; i += 3)
            {
                mesh.Positions.Add(new Point3D(vertices[i], vertices[i + 1], vertices[i + 2]));
            }
            for (int i = 0; i < triangleIndices.Count; i++)
            {
                mesh.TriangleIndices.Add(triangleIndices[i]);
            }
            return mesh;
        }

        private void PositionCamera(SpherePreviewData preview)
        {
            double cx = (preview.MinX + preview.MaxX) * 0.5;
            double cy = (preview.MinY + preview.MaxY) * 0.5;
            double cz = (preview.MinZ + preview.MaxZ) * 0.5;
            double sx = Math.Max(1.0, preview.MaxX - preview.MinX);
            double sy = Math.Max(1.0, preview.MaxY - preview.MinY);
            double sz = Math.Max(1.0, preview.MaxZ - preview.MinZ);
            double extent = Math.Max(sx, Math.Max(sy, sz));

            var position = new Point3D(cx + extent * 1.8, cy + extent * 1.6, cz + extent * 1.4);
            PreviewCamera.Position = position;
            PreviewCamera.LookDirection = new Vector3D(cx - position.X, cy - position.Y, cz - position.Z);
            PreviewCamera.UpDirection = new Vector3D(0, 0, 1);
            PreviewCamera.FieldOfView = 45;

            _previewCameraTarget = new Point3D(cx, cy, cz);
            _defaultPreviewCameraTarget = _previewCameraTarget;
            _defaultPreviewCameraPosition = PreviewCamera.Position;
            _defaultPreviewCameraLookDirection = PreviewCamera.LookDirection;
            _defaultPreviewCameraUpDirection = PreviewCamera.UpDirection;
            _defaultPreviewCameraFieldOfView = PreviewCamera.FieldOfView;
            _hasPreviewCameraDefault = true;
        }

        private void ResetGeometry_Click(object sender, RoutedEventArgs e)
        {
            ResetPreviewCamera();
        }

        private void ResetPreviewCamera()
        {
            if (!_hasPreviewCameraDefault)
            {
                return;
            }

            _previewCameraTarget = _defaultPreviewCameraTarget;
            PreviewCamera.Position = _defaultPreviewCameraPosition;
            PreviewCamera.LookDirection = _defaultPreviewCameraLookDirection;
            PreviewCamera.UpDirection = _defaultPreviewCameraUpDirection;
            PreviewCamera.FieldOfView = _defaultPreviewCameraFieldOfView;
        }

        private void PreviewViewport_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isPreviewCameraDragging = true;
            _lastPreviewCameraPoint = e.GetPosition(PreviewViewport);
            _previewCameraButton = e.ChangedButton;
            PreviewViewport.CaptureMouse();
            e.Handled = true;
        }

        private void PreviewViewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPreviewCameraDragging)
            {
                return;
            }

            var current = e.GetPosition(PreviewViewport);
            double dx = current.X - _lastPreviewCameraPoint.X;
            double dy = current.Y - _lastPreviewCameraPoint.Y;
            _lastPreviewCameraPoint = current;

            if (_previewCameraButton == MouseButton.Right || _previewCameraButton == MouseButton.Middle)
            {
                PanPreviewCamera(dx, dy);
            }
            else
            {
                RotatePreviewCamera(dx, dy);
            }

            e.Handled = true;
        }

        private void PreviewViewport_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isPreviewCameraDragging = false;
            PreviewViewport.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void PreviewViewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ZoomPreviewCamera(e.Delta > 0 ? 0.9 : 1.1);
            e.Handled = true;
        }

        private void RotatePreviewCamera(double dx, double dy)
        {
            var offset = PreviewCamera.Position - _previewCameraTarget;
            if (offset.Length < 1e-6)
            {
                return;
            }

            var up = PreviewCamera.UpDirection;
            if (up.Length < 1e-6)
            {
                up = new Vector3D(0, 0, 1);
            }

            offset = RotateVector(offset, new Vector3D(0, 0, 1), dx * 0.35);
            up = RotateVector(up, new Vector3D(0, 0, 1), dx * 0.35);

            var look = -offset;
            var right = Vector3D.CrossProduct(look, up);
            if (right.Length > 1e-6)
            {
                right.Normalize();
                offset = RotateVector(offset, right, dy * 0.35);
                up = RotateVector(up, right, dy * 0.35);
            }

            PreviewCamera.Position = _previewCameraTarget + offset;
            PreviewCamera.LookDirection = _previewCameraTarget - PreviewCamera.Position;
            PreviewCamera.UpDirection = up;
        }

        private void PanPreviewCamera(double dx, double dy)
        {
            var look = PreviewCamera.LookDirection;
            var up = PreviewCamera.UpDirection;
            if (look.Length < 1e-6 || up.Length < 1e-6)
            {
                return;
            }

            look.Normalize();
            up.Normalize();
            var right = Vector3D.CrossProduct(look, up);
            if (right.Length < 1e-6)
            {
                return;
            }
            right.Normalize();

            double distance = (PreviewCamera.Position - _previewCameraTarget).Length;
            double scale = Math.Max(0.1, distance * 0.002);
            var delta = (right * (-dx * scale)) + (up * (dy * scale));

            _previewCameraTarget += delta;
            PreviewCamera.Position += delta;
            PreviewCamera.LookDirection = _previewCameraTarget - PreviewCamera.Position;
        }

        private void ZoomPreviewCamera(double factor)
        {
            var offset = PreviewCamera.Position - _previewCameraTarget;
            if (offset.Length < 1e-6)
            {
                return;
            }

            PreviewCamera.Position = _previewCameraTarget + (offset * factor);
            PreviewCamera.LookDirection = _previewCameraTarget - PreviewCamera.Position;
        }

        private Vector3D RotateVector(Vector3D vector, Vector3D axis, double angleDegrees)
        {
            if (axis.Length < 1e-6)
            {
                return vector;
            }

            axis.Normalize();
            var rotation = Matrix3D.Identity;
            rotation.Rotate(new Quaternion(axis, angleDegrees));
            return rotation.Transform(vector);
        }

        private MeshGeometry3D CreateBoxMesh(double minX, double maxX, double minY, double maxY, double minZ, double maxZ)
        {
            var mesh = new MeshGeometry3D();
            var p = new Point3DCollection
            {
                new Point3D(minX, minY, minZ), // 0
                new Point3D(maxX, minY, minZ), // 1
                new Point3D(maxX, maxY, minZ), // 2
                new Point3D(minX, maxY, minZ), // 3
                new Point3D(minX, minY, maxZ), // 4
                new Point3D(maxX, minY, maxZ), // 5
                new Point3D(maxX, maxY, maxZ), // 6
                new Point3D(minX, maxY, maxZ)  // 7
            };
            mesh.Positions = p;

            AddTriangle(mesh, 0, 1, 2); AddTriangle(mesh, 0, 2, 3); // bottom
            AddTriangle(mesh, 4, 6, 5); AddTriangle(mesh, 4, 7, 6); // top
            AddTriangle(mesh, 0, 4, 5); AddTriangle(mesh, 0, 5, 1); // front
            AddTriangle(mesh, 1, 5, 6); AddTriangle(mesh, 1, 6, 2); // right
            AddTriangle(mesh, 2, 6, 7); AddTriangle(mesh, 2, 7, 3); // back
            AddTriangle(mesh, 3, 7, 4); AddTriangle(mesh, 3, 4, 0); // left

            return mesh;
        }

        private MeshGeometry3D CreateUnitSphereMesh(int latDiv, int lonDiv)
        {
            var mesh = new MeshGeometry3D();
            for (int lat = 0; lat <= latDiv; lat++)
            {
                double theta = Math.PI * lat / latDiv;
                double sinTheta = Math.Sin(theta);
                double cosTheta = Math.Cos(theta);

                for (int lon = 0; lon <= lonDiv; lon++)
                {
                    double phi = 2.0 * Math.PI * lon / lonDiv;
                    double x = sinTheta * Math.Cos(phi);
                    double y = sinTheta * Math.Sin(phi);
                    double z = cosTheta;
                    mesh.Positions.Add(new Point3D(x, y, z));
                }
            }

            int cols = lonDiv + 1;
            for (int lat = 0; lat < latDiv; lat++)
            {
                for (int lon = 0; lon < lonDiv; lon++)
                {
                    int a = lat * cols + lon;
                    int b = a + 1;
                    int c = a + cols;
                    int d = c + 1;

                    AddTriangle(mesh, a, c, b);
                    AddTriangle(mesh, b, c, d);
                }
            }

            return mesh;
        }

        private void AddTriangle(MeshGeometry3D mesh, int i0, int i1, int i2)
        {
            mesh.TriangleIndices.Add(i0);
            mesh.TriangleIndices.Add(i1);
            mesh.TriangleIndices.Add(i2);
        }




        private void Cancel(object sender, RoutedEventArgs e)
        {
            //this.Close();
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {

        }
    }
}
