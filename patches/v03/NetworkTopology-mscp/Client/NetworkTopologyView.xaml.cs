using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetworkTopology.Models;
using NetworkTopology.Services;
using VideoOS.Platform.Client;

namespace NetworkTopology.Client
{
    public partial class NetworkTopologyView : ViewItemWpfUserControl
    {
        private List<CameraDevice> _cameras = new List<CameraDevice>();
        private Dictionary<Guid, CameraDevice> _camerasById = new Dictionary<Guid, CameraDevice>();
        private WorkspaceState _workspace = new WorkspaceState();
        private TopologyConfiguration _legacyConfig = new TopologyConfiguration();
        private TopologyBoard _activeBoard;

        private DispatcherTimer _probeTimer;
        private DispatcherTimer _cameraSearchTimer;
        private DispatcherTimer _switchSearchTimer;
        private bool _closed;
        private bool _cameraLoading;
        private bool _probing;
        private bool _refreshingBoardList;
        private int _cameraLoadGeneration;

        private Point _cameraListMouseDown;
        private Point _switchListMouseDown;

        private BoardNode _dragNode;
        private FrameworkElement _dragVisual;
        private Point _dragMouseStart;
        private Point _dragNodeStart;

        private string _selectedNodeId;
        private string _connectSourceNodeId;
        private Line _previewLine;
        private bool _connectByHandle;

        private readonly HashSet<Guid> _lldpInFlight = new HashSet<Guid>();
        private readonly SemaphoreSlim _lldpSemaphore = new SemaphoreSlim(4);

        private const double NodeWidth = 188;
        private const double NodeHeight = 72;

        public NetworkTopologyView()
        {
            InitializeComponent();
        }

        public override bool ShowToolbar => false;

        public override void Init()
        {
            base.Init();

            LoadLegacyConfiguration();
            _workspace = WorkspaceStoreService.Load();
            WorkspaceStoreService.ImportLegacySwitches(_workspace, _legacyConfig);
            EnsureActiveBoard();
            SaveWorkspace();

            RefreshBoardList();
            RefreshSwitchList();
            RenderBoard();

            _cameraSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _cameraSearchTimer.Tick += (s, e) =>
            {
                _cameraSearchTimer.Stop();
                RefreshCameraList();
            };

            _switchSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _switchSearchTimer.Tick += (s, e) =>
            {
                _switchSearchTimer.Stop();
                RefreshSwitchList();
            };

            _probeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _probeTimer.Tick += async (s, e) => await ProbeActiveBoardAsync();
            _probeTimer.Start();

            _ = RefreshCameraIndexAsync();
        }

        public override void Close()
        {
            _closed = true;

            if (_probeTimer != null)
            {
                _probeTimer.Stop();
                _probeTimer = null;
            }
            if (_cameraSearchTimer != null)
            {
                _cameraSearchTimer.Stop();
                _cameraSearchTimer = null;
            }
            if (_switchSearchTimer != null)
            {
                _switchSearchTimer.Stop();
                _switchSearchTimer = null;
            }

            try { SaveWorkspace(); } catch { }
            base.Close();
        }

        private void LoadLegacyConfiguration()
        {
            try
            {
                var pluginDir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var path = System.IO.Path.Combine(pluginDir ?? string.Empty, "topology.xml");
                _legacyConfig = TopologyConfigService.Load(path);
            }
            catch
            {
                _legacyConfig = new TopologyConfiguration();
            }
        }

        private void EnsureActiveBoard()
        {
            if (_workspace.Boards == null) _workspace.Boards = new List<TopologyBoard>();
            if (_workspace.Switches == null) _workspace.Switches = new List<SwitchNode>();

            if (_workspace.Boards.Count == 0)
            {
                var board = new TopologyBoard { Id = Guid.NewGuid().ToString("N"), Name = "Main" };
                _workspace.Boards.Add(board);
                _workspace.ActiveBoardId = board.Id;
            }

            _activeBoard = _workspace.Boards.FirstOrDefault(x =>
                string.Equals(x.Id, _workspace.ActiveBoardId, StringComparison.OrdinalIgnoreCase));

            if (_activeBoard == null)
            {
                _activeBoard = _workspace.Boards[0];
                _workspace.ActiveBoardId = _activeBoard.Id;
            }

            if (_activeBoard.Nodes == null) _activeBoard.Nodes = new List<BoardNode>();
            if (_activeBoard.Links == null) _activeBoard.Links = new List<BoardLink>();
        }

        private async Task RefreshCameraIndexAsync()
        {
            if (_cameraLoading || _closed) return;

            _cameraLoading = true;
            int generation = ++_cameraLoadGeneration;
            RefreshCamerasButton.IsEnabled = false;
            SummaryText.Text = "Loading camera index...";
            ModeText.Text = "Fast index: names first, hardware metadata in background";

            try
            {
                var index = await Task.Run(() => DeviceDiscoveryService.DiscoverCameraIndex());
                if (_closed || generation != _cameraLoadGeneration) return;

                ReplaceCameraList(index);
                RefreshCameraList();
                SyncBoardNodesFromDevices();
                RenderBoard();
                UpdateSummary();

                SummaryText.Text = _cameras.Count + " cameras ready";
                ModeText.Text = "Loading IP/model metadata in background...";

                _ = EnrichCameraMetadataAsync(generation, index);
            }
            catch (Exception ex)
            {
                SummaryText.Text = "Camera index failed: " + ex.Message;
            }
            finally
            {
                _cameraLoading = false;
                if (!_closed) RefreshCamerasButton.IsEnabled = true;
            }
        }

        private async Task EnrichCameraMetadataAsync(int generation, List<CameraDevice> index)
        {
            try
            {
                var enriched = await Task.Run(() => DeviceDiscoveryService.EnrichCamerasFromHardware(index));
                if (_closed || generation != _cameraLoadGeneration) return;

                ReplaceCameraList(enriched);
                RefreshCameraList();
                SyncBoardNodesFromDevices();
                RefreshSwitchList();
                RenderBoard();
                UpdateSummary();
                ModeText.Text = "Interactive boards  |  Ping only active-board devices  |  Axis LLDP auto-discovery on drop";

                if (AutoLldpCheckBox.IsChecked == true && _activeBoard != null)
                {
                    foreach (var node in _activeBoard.Nodes.Where(x => IsCameraNode(x)).ToList())
                    {
                        var camera = ResolveCamera(node);
                        if (camera != null && !HasAnyLink(_activeBoard, node.Id))
                            _ = TryAutoDiscoverSwitchAsync(_activeBoard.Id, node.Id, camera);
                    }
                }

                await ProbeActiveBoardAsync();
            }
            catch (Exception ex)
            {
                ModeText.Text = "Camera metadata enrichment failed: " + ex.Message;
            }
        }

        private void ReplaceCameraList(List<CameraDevice> cameras)
        {
            _cameras = cameras ?? new List<CameraDevice>();
            _camerasById = _cameras
                .Where(x => x != null)
                .GroupBy(x => x.CameraId)
                .ToDictionary(g => g.Key, g => g.First());
        }

        private async void RefreshCamerasButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshCameraIndexAsync();
        }

        private async void ProbeButton_Click(object sender, RoutedEventArgs e)
        {
            await ProbeActiveBoardAsync();
        }

        private async Task ProbeActiveBoardAsync()
        {
            if (_closed || _probing || _activeBoard == null) return;
            _probing = true;

            try
            {
                var tasks = new List<Task>();
                var seenCameras = new HashSet<Guid>();
                var seenSwitches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var node in _activeBoard.Nodes)
                {
                    if (IsCameraNode(node))
                    {
                        var camera = ResolveCamera(node);
                        if (camera != null && seenCameras.Add(camera.CameraId))
                            tasks.Add(PingService.ProbeAsync(camera));
                    }
                    else if (IsSwitchNode(node))
                    {
                        var sw = ResolveSwitch(node);
                        if (sw != null && seenSwitches.Add(sw.Id ?? string.Empty))
                            tasks.Add(PingService.ProbeAsync(sw));
                    }
                }

                if (tasks.Count > 0)
                    await Task.WhenAll(tasks);

                if (_closed) return;
                RenderBoard();
                UpdateSummary();
            }
            catch (Exception ex)
            {
                SummaryText.Text = "Probe failed: " + ex.Message;
            }
            finally
            {
                _probing = false;
            }
        }

        private void CameraSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_cameraSearchTimer == null) return;
            _cameraSearchTimer.Stop();
            _cameraSearchTimer.Start();
        }

        private void RefreshCameraList()
        {
            var query = (CameraSearchBox.Text ?? string.Empty).Trim();
            IEnumerable<CameraDevice> items = _cameras;

            if (!string.IsNullOrWhiteSpace(query))
            {
                items = items.Where(x =>
                    Contains(x.CameraName, query) ||
                    Contains(x.IpAddress, query) ||
                    Contains(x.Model, query) ||
                    Contains(x.RecordingServerName, query));
            }

            var filtered = items.ToList();
            CameraList.ItemsSource = filtered;
            CameraCountText.Text = filtered.Count == _cameras.Count
                ? _cameras.Count + " cameras"
                : filtered.Count + " of " + _cameras.Count + " cameras";
        }

        private void SwitchSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_switchSearchTimer == null) return;
            _switchSearchTimer.Stop();
            _switchSearchTimer.Start();
        }

        private void RefreshSwitchList()
        {
            if (_workspace == null || _workspace.Switches == null) return;
            var query = (SwitchSearchBox.Text ?? string.Empty).Trim();
            IEnumerable<SwitchNode> items = _workspace.Switches;

            if (!string.IsNullOrWhiteSpace(query))
                items = items.Where(x => Contains(x.Name, query) || Contains(x.IpAddress, query) || Contains(x.Description, query));

            SwitchList.ItemsSource = items
                .OrderBy(x => x.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static bool Contains(string value, string query)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void RefreshBoardList()
        {
            _refreshingBoardList = true;
            try
            {
                BoardList.ItemsSource = null;
                BoardList.ItemsSource = _workspace.Boards;
                BoardList.SelectedItem = _activeBoard;
                BoardNameBox.Text = _activeBoard == null ? string.Empty : _activeBoard.Name;
                ActiveBoardText.Text = _activeBoard == null ? "No board" : _activeBoard.Name;
            }
            finally
            {
                _refreshingBoardList = false;
            }
        }

        private void BoardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshingBoardList) return;
            var board = BoardList.SelectedItem as TopologyBoard;
            if (board == null) return;

            _activeBoard = board;
            _workspace.ActiveBoardId = board.Id;
            _selectedNodeId = null;
            CancelConnect();
            SaveWorkspace();
            BoardNameBox.Text = board.Name;
            ActiveBoardText.Text = board.Name;
            RenderBoard();
            UpdateSummary();
            _ = ProbeActiveBoardAsync();
        }

        private void NewBoardButton_Click(object sender, RoutedEventArgs e)
        {
            var board = new TopologyBoard
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Board " + (_workspace.Boards.Count + 1)
            };
            _workspace.Boards.Add(board);
            _activeBoard = board;
            _workspace.ActiveBoardId = board.Id;
            _selectedNodeId = null;
            SaveWorkspace();
            RefreshBoardList();
            RenderBoard();
            UpdateSummary();
        }

        private void DeleteBoardButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeBoard == null) return;
            if (_workspace.Boards.Count <= 1)
            {
                SummaryText.Text = "At least one board must remain";
                return;
            }

            _workspace.Boards.Remove(_activeBoard);
            _activeBoard = _workspace.Boards[0];
            _workspace.ActiveBoardId = _activeBoard.Id;
            _selectedNodeId = null;
            SaveWorkspace();
            RefreshBoardList();
            RenderBoard();
            UpdateSummary();
        }

        private void SaveBoardNameButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeBoard == null) return;
            var name = (BoardNameBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name)) return;
            _activeBoard.Name = name;
            SaveWorkspace();
            RefreshBoardList();
        }

        private void AddSwitchButton_Click(object sender, RoutedEventArgs e)
        {
            var name = (NewSwitchNameBox.Text ?? string.Empty).Trim();
            var ip = (NewSwitchIpBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = string.IsNullOrWhiteSpace(ip) ? "Switch " + (_workspace.Switches.Count + 1) : "Switch " + ip;

            var sw = new SwitchNode
            {
                Id = "sw-" + Guid.NewGuid().ToString("N"),
                Name = name,
                IpAddress = ip,
                Role = "Switch",
                Source = "Manual",
                AutoDiscovered = false,
                State = NodeState.Unknown
            };

            _workspace.Switches.Add(sw);
            NewSwitchNameBox.Text = string.Empty;
            NewSwitchIpBox.Text = string.Empty;
            SaveWorkspace();
            RefreshSwitchList();
            SwitchList.SelectedItem = sw;
        }

        private void AddSelectedCameraButton_Click(object sender, RoutedEventArgs e)
        {
            var camera = CameraList.SelectedItem as CameraDevice;
            if (camera == null) return;
            AddCameraToBoard(camera, NextPlacement());
        }

        private void AddSelectedSwitchButton_Click(object sender, RoutedEventArgs e)
        {
            var sw = SwitchList.SelectedItem as SwitchNode;
            if (sw == null) return;
            AddSwitchToBoard(sw, NextPlacement());
        }

        private Point NextPlacement()
        {
            int n = _activeBoard == null ? 0 : _activeBoard.Nodes.Count;
            int col = n % 5;
            int row = n / 5;
            return new Point(70 + col * 230, 70 + row * 120);
        }

        private void CameraList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _cameraListMouseDown = e.GetPosition(CameraList);
        }

        private void CameraList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(CameraList);
            if (Math.Abs(p.X - _cameraListMouseDown.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _cameraListMouseDown.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            var camera = CameraList.SelectedItem as CameraDevice;
            if (camera == null) return;

            var data = new DataObject();
            data.SetData("NetworkTopology.Camera", camera.CameraId.ToString());
            DragDrop.DoDragDrop(CameraList, data, DragDropEffects.Copy);
        }

        private void SwitchList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _switchListMouseDown = e.GetPosition(SwitchList);
        }

        private void SwitchList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(SwitchList);
            if (Math.Abs(p.X - _switchListMouseDown.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _switchListMouseDown.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            var sw = SwitchList.SelectedItem as SwitchNode;
            if (sw == null) return;

            var data = new DataObject();
            data.SetData("NetworkTopology.Switch", sw.Id);
            DragDrop.DoDragDrop(SwitchList, data, DragDropEffects.Copy);
        }

        private void CameraList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var camera = CameraList.SelectedItem as CameraDevice;
            if (camera != null) AddCameraToBoard(camera, NextPlacement());
        }

        private void SwitchList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var sw = SwitchList.SelectedItem as SwitchNode;
            if (sw != null) AddSwitchToBoard(sw, NextPlacement());
        }

        private void NodeCanvas_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("NetworkTopology.Camera") || e.Data.GetDataPresent("NetworkTopology.Switch"))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void NodeCanvas_Drop(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(NodeCanvas);

            if (e.Data.GetDataPresent("NetworkTopology.Camera"))
            {
                Guid id;
                if (Guid.TryParse(Convert.ToString(e.Data.GetData("NetworkTopology.Camera")), out id))
                {
                    CameraDevice camera;
                    if (_camerasById.TryGetValue(id, out camera))
                        AddCameraToBoard(camera, p);
                }
            }
            else if (e.Data.GetDataPresent("NetworkTopology.Switch"))
            {
                var id = Convert.ToString(e.Data.GetData("NetworkTopology.Switch"));
                var sw = _workspace.Switches.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (sw != null) AddSwitchToBoard(sw, p);
            }

            e.Handled = true;
        }

        private void AddCameraToBoard(CameraDevice camera, Point p)
        {
            if (_activeBoard == null || camera == null) return;
            var key = camera.CameraId.ToString();
            var existing = _activeBoard.Nodes.FirstOrDefault(x => IsCameraNode(x) && string.Equals(x.DeviceKey, key, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                SelectNode(existing.Id);
                return;
            }

            var node = new BoardNode
            {
                Id = "node-" + Guid.NewGuid().ToString("N"),
                Kind = "Camera",
                DeviceKey = key,
                DisplayName = camera.CameraName,
                IpAddress = camera.IpAddress,
                X = Clamp(p.X - NodeWidth / 2, 8, NodeCanvas.Width - NodeWidth - 8),
                Y = Clamp(p.Y - NodeHeight / 2, 8, NodeCanvas.Height - NodeHeight - 8)
            };

            _activeBoard.Nodes.Add(node);
            _selectedNodeId = node.Id;
            SaveWorkspace();
            RenderBoard();

            if (AutoLldpCheckBox.IsChecked == true)
                _ = TryAutoDiscoverSwitchAsync(_activeBoard.Id, node.Id, camera);

            _ = ProbeActiveBoardAsync();
        }

        private void AddSwitchToBoard(SwitchNode sw, Point p)
        {
            if (_activeBoard == null || sw == null) return;
            var existing = _activeBoard.Nodes.FirstOrDefault(x => IsSwitchNode(x) && string.Equals(x.DeviceKey, sw.Id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                SelectNode(existing.Id);
                return;
            }

            var node = new BoardNode
            {
                Id = "node-" + Guid.NewGuid().ToString("N"),
                Kind = "Switch",
                DeviceKey = sw.Id,
                DisplayName = sw.Name,
                IpAddress = sw.IpAddress,
                X = Clamp(p.X - NodeWidth / 2, 8, NodeCanvas.Width - NodeWidth - 8),
                Y = Clamp(p.Y - NodeHeight / 2, 8, NodeCanvas.Height - NodeHeight - 8)
            };

            _activeBoard.Nodes.Add(node);
            _selectedNodeId = node.Id;
            SaveWorkspace();
            RenderBoard();
            _ = ProbeActiveBoardAsync();
        }

        private async Task TryAutoDiscoverSwitchAsync(string boardId, string cameraNodeId, CameraDevice camera)
        {
            if (camera == null || _closed) return;
            if (string.IsNullOrWhiteSpace(camera.HardwarePath))
            {
                LldpStatusText.Text = "LLDP: waiting for XProtect hardware metadata for " + camera.CameraName;
                return;
            }
            if (!_lldpInFlight.Add(camera.CameraId)) return;

            await _lldpSemaphore.WaitAsync();
            try
            {
                if (_closed) return;
                if (_activeBoard != null && string.Equals(_activeBoard.Id, boardId, StringComparison.OrdinalIgnoreCase))
                    LldpStatusText.Text = "LLDP: asking " + camera.CameraName + " for its network neighbor...";

                var result = await AxisLldpDiscoveryService.TryDiscoverAsync(camera);
                if (_closed) return;

                var board = _workspace.Boards.FirstOrDefault(x => string.Equals(x.Id, boardId, StringComparison.OrdinalIgnoreCase));
                if (board == null) return;
                var cameraNode = board.Nodes.FirstOrDefault(x => string.Equals(x.Id, cameraNodeId, StringComparison.OrdinalIgnoreCase));
                if (cameraNode == null) return;

                if (!result.Success)
                {
                    if (_activeBoard != null && string.Equals(_activeBoard.Id, boardId, StringComparison.OrdinalIgnoreCase))
                        LldpStatusText.Text = "LLDP: " + result.Error;
                    return;
                }

                var sw = FindOrCreateDiscoveredSwitch(result, camera.CameraName);
                var switchNode = board.Nodes.FirstOrDefault(x => IsSwitchNode(x) && string.Equals(x.DeviceKey, sw.Id, StringComparison.OrdinalIgnoreCase));
                if (switchNode == null)
                {
                    switchNode = new BoardNode
                    {
                        Id = "node-" + Guid.NewGuid().ToString("N"),
                        Kind = "Switch",
                        DeviceKey = sw.Id,
                        DisplayName = sw.Name,
                        IpAddress = sw.IpAddress,
                        X = Clamp(cameraNode.X + 260, 8, NodeCanvas.Width - NodeWidth - 8),
                        Y = Clamp(cameraNode.Y, 8, NodeCanvas.Height - NodeHeight - 8)
                    };
                    board.Nodes.Add(switchNode);
                }

                CreateLinkInternal(board, cameraNode, switchNode, result.PortId, false);
                SaveWorkspace();
                RefreshSwitchList();

                if (_activeBoard != null && string.Equals(_activeBoard.Id, boardId, StringComparison.OrdinalIgnoreCase))
                {
                    LldpStatusText.Text = "LLDP: " + sw.Name +
                        (string.IsNullOrWhiteSpace(sw.IpAddress) ? "" : "  " + sw.IpAddress) +
                        (string.IsNullOrWhiteSpace(result.PortId) ? "" : "  port " + result.PortId);
                    RenderBoard();
                    await ProbeActiveBoardAsync();
                }
            }
            catch (Exception ex)
            {
                if (!_closed) LldpStatusText.Text = "LLDP discovery error: " + ex.Message;
            }
            finally
            {
                _lldpSemaphore.Release();
                _lldpInFlight.Remove(camera.CameraId);
            }
        }

        private SwitchNode FindOrCreateDiscoveredSwitch(SwitchDiscoveryResult result, string cameraName)
        {
            SwitchNode sw = null;

            if (!string.IsNullOrWhiteSpace(result.ManagementIp))
            {
                sw = _workspace.Switches.FirstOrDefault(x =>
                    string.Equals(x.IpAddress, result.ManagementIp, StringComparison.OrdinalIgnoreCase));
            }

            if (sw == null && !string.IsNullOrWhiteSpace(result.ChassisId))
            {
                sw = _workspace.Switches.FirstOrDefault(x =>
                    string.Equals(x.ChassisId, result.ChassisId, StringComparison.OrdinalIgnoreCase));
            }

            if (sw == null)
            {
                sw = new SwitchNode
                {
                    Id = "sw-" + Guid.NewGuid().ToString("N"),
                    Name = string.IsNullOrWhiteSpace(result.SwitchName) ? "Discovered switch" : result.SwitchName,
                    IpAddress = result.ManagementIp,
                    ChassisId = result.ChassisId,
                    Description = result.Description,
                    Role = "Switch",
                    Source = "Axis LLDP via " + cameraName,
                    AutoDiscovered = true,
                    State = NodeState.Unknown
                };
                _workspace.Switches.Add(sw);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(result.SwitchName)) sw.Name = result.SwitchName;
                if (!string.IsNullOrWhiteSpace(result.ManagementIp)) sw.IpAddress = result.ManagementIp;
                if (!string.IsNullOrWhiteSpace(result.ChassisId)) sw.ChassisId = result.ChassisId;
                if (!string.IsNullOrWhiteSpace(result.Description)) sw.Description = result.Description;
                sw.AutoDiscovered = true;
                if (string.IsNullOrWhiteSpace(sw.Source)) sw.Source = "Axis LLDP via " + cameraName;
            }

            return sw;
        }

        private void RenderBoard()
        {
            LinkCanvas.Children.Clear();
            NodeCanvas.Children.Clear();
            EnsureActiveBoard();

            ActiveBoardText.Text = _activeBoard.Name;
            DrawLinks();
            foreach (var node in _activeBoard.Nodes.ToList())
                AddNodeVisual(node);

            ShowSelectedDetails();
            UpdateSummary();
        }

        private void DrawLinks()
        {
            foreach (var link in _activeBoard.Links.ToList())
            {
                var from = _activeBoard.Nodes.FirstOrDefault(x => string.Equals(x.Id, link.FromNodeId, StringComparison.OrdinalIgnoreCase));
                var to = _activeBoard.Nodes.FirstOrDefault(x => string.Equals(x.Id, link.ToNodeId, StringComparison.OrdinalIgnoreCase));
                if (from == null || to == null) continue;

                var a = NodeCenter(from);
                var b = NodeCenter(to);
                var state = GetLinkState(from, to);

                LinkCanvas.Children.Add(new Line
                {
                    X1 = a.X,
                    Y1 = a.Y,
                    X2 = b.X,
                    Y2 = b.Y,
                    Stroke = StateBrush(state),
                    StrokeThickness = 2.5,
                    Opacity = 0.88
                });

                if (!string.IsNullOrWhiteSpace(link.Label))
                {
                    var label = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(220, 19, 23, 29)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(58, 67, 78)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(5, 2, 5, 2),
                        Child = new TextBlock
                        {
                            Text = link.Label,
                            Foreground = new SolidColorBrush(Color.FromRgb(201, 209, 219)),
                            FontSize = 9
                        }
                    };
                    Canvas.SetLeft(label, (a.X + b.X) / 2 - 20);
                    Canvas.SetTop(label, (a.Y + b.Y) / 2 - 12);
                    LinkCanvas.Children.Add(label);
                }
            }
        }

        private void AddNodeVisual(BoardNode node)
        {
            SyncNodeFromDevice(node);
            var state = GetNodeState(node);
            var stateBrush = StateBrush(state);
            bool selected = string.Equals(node.Id, _selectedNodeId, StringComparison.OrdinalIgnoreCase);

            var rootGrid = new Grid();
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });

            var stack = new StackPanel { Margin = new Thickness(10, 7, 3, 5) };
            stack.Children.Add(new TextBlock
            {
                Text = IsCameraNode(node) ? "CAMERA" : "SWITCH",
                Foreground = new SolidColorBrush(Color.FromRgb(125, 137, 152)),
                FontSize = 9
            });
            stack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(node.DisplayName) ? "Unnamed" : node.DisplayName,
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            stack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(node.IpAddress) ? "No management IP" : node.IpAddress,
                Foreground = new SolidColorBrush(Color.FromRgb(170, 180, 192)),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(stack, 0);
            rootGrid.Children.Add(stack);

            var handle = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = new SolidColorBrush(Color.FromRgb(111, 182, 255)),
                Stroke = Brushes.White,
                StrokeThickness = 1,
                Cursor = Cursors.Cross,
                ToolTip = "Drag this dot to another node to connect"
            };
            handle.Tag = node;
            handle.MouseLeftButtonDown += ConnectHandle_MouseLeftButtonDown;
            Grid.SetColumn(handle, 1);
            handle.HorizontalAlignment = HorizontalAlignment.Center;
            handle.VerticalAlignment = VerticalAlignment.Center;
            rootGrid.Children.Add(handle);

            var border = new Border
            {
                Width = NodeWidth,
                Height = NodeHeight,
                Background = selected
                    ? new SolidColorBrush(Color.FromRgb(42, 51, 63))
                    : new SolidColorBrush(Color.FromRgb(31, 36, 45)),
                BorderBrush = selected
                    ? new SolidColorBrush(Color.FromRgb(111, 182, 255))
                    : stateBrush,
                BorderThickness = new Thickness(selected ? 3 : 2),
                CornerRadius = new CornerRadius(7),
                Child = rootGrid,
                Cursor = Cursors.SizeAll,
                Tag = node
            };

            border.MouseLeftButtonDown += NodeBorder_MouseLeftButtonDown;
            border.MouseMove += NodeBorder_MouseMove;
            border.MouseLeftButtonUp += NodeBorder_MouseLeftButtonUp;

            Canvas.SetLeft(border, node.X);
            Canvas.SetTop(border, node.Y);
            NodeCanvas.Children.Add(border);
        }

        private void NodeBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var visual = sender as FrameworkElement;
            var node = visual == null ? null : visual.Tag as BoardNode;
            if (node == null) return;

            _selectedNodeId = node.Id;
            ShowSelectedDetails();

            if (ConnectModeButton.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(_connectSourceNodeId))
                {
                    _connectSourceNodeId = node.Id;
                    ModeText.Text = "Connect mode: select the destination node";
                }
                else
                {
                    var source = _activeBoard.Nodes.FirstOrDefault(x => string.Equals(x.Id, _connectSourceNodeId, StringComparison.OrdinalIgnoreCase));
                    if (source != null && !string.Equals(source.Id, node.Id, StringComparison.OrdinalIgnoreCase))
                        CreateLinkInternal(_activeBoard, source, node, string.Empty, true);
                    _connectSourceNodeId = null;
                    ModeText.Text = "Connect mode: select the first node";
                    RenderBoard();
                }
                e.Handled = true;
                return;
            }

            _dragNode = node;
            _dragVisual = visual;
            _dragMouseStart = e.GetPosition(NodeCanvas);
            _dragNodeStart = new Point(node.X, node.Y);
            visual.CaptureMouse();
            e.Handled = true;
        }

        private void NodeBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragNode == null || _dragVisual == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(NodeCanvas);
            var dx = p.X - _dragMouseStart.X;
            var dy = p.Y - _dragMouseStart.Y;

            _dragNode.X = Clamp(_dragNodeStart.X + dx, 5, NodeCanvas.Width - NodeWidth - 5);
            _dragNode.Y = Clamp(_dragNodeStart.Y + dy, 5, NodeCanvas.Height - NodeHeight - 5);
            Canvas.SetLeft(_dragVisual, _dragNode.X);
            Canvas.SetTop(_dragVisual, _dragNode.Y);
            DrawLinksOnly();
        }

        private void NodeBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragVisual != null)
                _dragVisual.ReleaseMouseCapture();

            if (_dragNode != null)
            {
                SaveWorkspace();
                RenderBoard();
            }

            _dragNode = null;
            _dragVisual = null;
            e.Handled = true;
        }

        private void ConnectHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var handle = sender as FrameworkElement;
            var node = handle == null ? null : handle.Tag as BoardNode;
            if (node == null) return;

            _selectedNodeId = node.Id;
            ShowSelectedDetails();
            StartConnectPreview(node);
            e.Handled = true;
        }

        private void StartConnectPreview(BoardNode source)
        {
            CancelConnectPreviewOnly();
            _connectSourceNodeId = source.Id;
            _connectByHandle = true;

            var center = NodeCenter(source);
            _previewLine = new Line
            {
                X1 = center.X,
                Y1 = center.Y,
                X2 = center.X,
                Y2 = center.Y,
                Stroke = new SolidColorBrush(Color.FromRgb(111, 182, 255)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Opacity = 0.9
            };
            LinkCanvas.Children.Add(_previewLine);
            NodeCanvas.CaptureMouse();
            ModeText.Text = "Connecting: drag to a destination node";
        }

        private void NodeCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_previewLine == null) return;
            var p = e.GetPosition(NodeCanvas);
            _previewLine.X2 = p.X;
            _previewLine.Y2 = p.Y;
        }

        private void NodeCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_previewLine == null || !_connectByHandle) return;

            var p = e.GetPosition(NodeCanvas);
            var hit = NodeCanvas.InputHitTest(p) as DependencyObject;
            var target = FindBoardNodeFromVisual(hit);
            var source = _activeBoard.Nodes.FirstOrDefault(x => string.Equals(x.Id, _connectSourceNodeId, StringComparison.OrdinalIgnoreCase));

            if (source != null && target != null && !string.Equals(source.Id, target.Id, StringComparison.OrdinalIgnoreCase))
                CreateLinkInternal(_activeBoard, source, target, string.Empty, true);

            CancelConnect();
            RenderBoard();
            e.Handled = true;
        }

        private BoardNode FindBoardNodeFromVisual(DependencyObject visual)
        {
            var current = visual;
            while (current != null)
            {
                var framework = current as FrameworkElement;
                var node = framework == null ? null : framework.Tag as BoardNode;
                if (node != null) return node;
                try { current = VisualTreeHelper.GetParent(current); }
                catch { return null; }
            }
            return null;
        }

        private void ConnectModeButton_Changed(object sender, RoutedEventArgs e)
        {
            CancelConnect();
            ModeText.Text = ConnectModeButton.IsChecked == true
                ? "Connect mode: select the first node, then the destination"
                : "Interactive boards  |  drag the blue dot to connect";
        }

        private void CreateLinkInternal(TopologyBoard board, BoardNode from, BoardNode to, string label, bool save)
        {
            if (board == null || from == null || to == null) return;
            if (string.Equals(from.Id, to.Id, StringComparison.OrdinalIgnoreCase)) return;

            if (IsCameraNode(from) && IsCameraNode(to))
            {
                SummaryText.Text = "Camera-to-camera links are not supported";
                return;
            }

            var existing = board.Links.FirstOrDefault(x =>
                (string.Equals(x.FromNodeId, from.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(x.ToNodeId, to.Id, StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(x.FromNodeId, to.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(x.ToNodeId, from.Id, StringComparison.OrdinalIgnoreCase)));

            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(label)) existing.Label = label;
                if (save) SaveWorkspace();
                return;
            }

            board.Links.Add(new BoardLink
            {
                Id = "link-" + Guid.NewGuid().ToString("N"),
                FromNodeId = from.Id,
                ToNodeId = to.Id,
                Label = label ?? string.Empty
            });

            if (save) SaveWorkspace();
        }

        private void RemoveLinksButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeBoard == null || string.IsNullOrWhiteSpace(_selectedNodeId)) return;
            _activeBoard.Links.RemoveAll(x =>
                string.Equals(x.FromNodeId, _selectedNodeId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.ToNodeId, _selectedNodeId, StringComparison.OrdinalIgnoreCase));
            SaveWorkspace();
            RenderBoard();
        }

        private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeBoard == null || string.IsNullOrWhiteSpace(_selectedNodeId)) return;
            _activeBoard.Nodes.RemoveAll(x => string.Equals(x.Id, _selectedNodeId, StringComparison.OrdinalIgnoreCase));
            _activeBoard.Links.RemoveAll(x =>
                string.Equals(x.FromNodeId, _selectedNodeId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.ToNodeId, _selectedNodeId, StringComparison.OrdinalIgnoreCase));
            _selectedNodeId = null;
            SaveWorkspace();
            RenderBoard();
        }

        private void SaveSwitchButton_Click(object sender, RoutedEventArgs e)
        {
            var node = GetSelectedNode();
            if (node == null || !IsSwitchNode(node)) return;
            var sw = ResolveSwitch(node);
            if (sw == null) return;

            var name = (EditSwitchNameBox.Text ?? string.Empty).Trim();
            var ip = (EditSwitchIpBox.Text ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(name)) sw.Name = name;
            sw.IpAddress = ip;

            foreach (var board in _workspace.Boards)
            {
                foreach (var boardNode in board.Nodes.Where(x => IsSwitchNode(x) && string.Equals(x.DeviceKey, sw.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    boardNode.DisplayName = sw.Name;
                    boardNode.IpAddress = sw.IpAddress;
                }
            }

            SaveWorkspace();
            RefreshSwitchList();
            RenderBoard();
            _ = ProbeActiveBoardAsync();
        }

        private void SelectNode(string nodeId)
        {
            _selectedNodeId = nodeId;
            RenderBoard();
        }

        private BoardNode GetSelectedNode()
        {
            return _activeBoard == null || string.IsNullOrWhiteSpace(_selectedNodeId)
                ? null
                : _activeBoard.Nodes.FirstOrDefault(x => string.Equals(x.Id, _selectedNodeId, StringComparison.OrdinalIgnoreCase));
        }

        private void ShowSelectedDetails()
        {
            var node = GetSelectedNode();
            if (node == null)
            {
                DetailsName.Text = "Select a device";
                DetailsType.Text = string.Empty;
                DetailsIp.Text = "-";
                DetailsStatus.Text = "-";
                DetailsStatus.Foreground = Brushes.White;
                DetailsModel.Text = "-";
                DetailsRecorder.Text = "-";
                DetailsPort.Text = "-";
                DetailsPath.Text = "-";
                SwitchEditorPanel.Visibility = Visibility.Collapsed;
                return;
            }

            if (IsCameraNode(node))
            {
                var camera = ResolveCamera(node);
                DetailsName.Text = camera == null ? node.DisplayName : camera.CameraName;
                DetailsType.Text = "Camera";
                DetailsIp.Text = camera == null ? Empty(node.IpAddress) : Empty(camera.IpAddress);
                DetailsStatus.Text = camera == null ? "Unknown" : StatusText(camera.State, camera.PingMs);
                DetailsStatus.Foreground = camera == null ? StateBrush(NodeState.Unknown) : StateBrush(camera.State);
                DetailsModel.Text = camera == null ? "-" : Empty(camera.Model);
                DetailsRecorder.Text = camera == null ? "-" : Empty(camera.RecordingServerName);
                DetailsPort.Text = BuildConnectionText(node);
                DetailsPath.Text = BuildBoardPath(node);
                SwitchEditorPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                var sw = ResolveSwitch(node);
                DetailsName.Text = sw == null ? node.DisplayName : sw.Name;
                DetailsType.Text = sw != null && sw.AutoDiscovered ? "Switch • LLDP discovered" : "Switch";
                DetailsIp.Text = sw == null ? Empty(node.IpAddress) : Empty(sw.IpAddress);
                DetailsStatus.Text = sw == null ? "Unknown" : StatusText(sw.State, sw.PingMs);
                DetailsStatus.Foreground = sw == null ? StateBrush(NodeState.Unknown) : StateBrush(sw.State);
                DetailsModel.Text = sw == null ? "-" : Empty(sw.Description);
                DetailsRecorder.Text = sw == null ? "-" : Empty(sw.Source);
                DetailsPort.Text = BuildConnectionText(node);
                DetailsPath.Text = BuildBoardPath(node);
                SwitchEditorPanel.Visibility = Visibility.Visible;
                EditSwitchNameBox.Text = sw == null ? node.DisplayName : sw.Name;
                EditSwitchIpBox.Text = sw == null ? node.IpAddress : sw.IpAddress;
            }
        }

        private string BuildConnectionText(BoardNode node)
        {
            if (_activeBoard == null || node == null) return "-";
            var links = _activeBoard.Links.Where(x =>
                string.Equals(x.FromNodeId, node.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.ToNodeId, node.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (links.Count == 0) return "No links";

            var parts = new List<string>();
            foreach (var link in links)
            {
                var otherId = string.Equals(link.FromNodeId, node.Id, StringComparison.OrdinalIgnoreCase) ? link.ToNodeId : link.FromNodeId;
                var other = _activeBoard.Nodes.FirstOrDefault(x => string.Equals(x.Id, otherId, StringComparison.OrdinalIgnoreCase));
                if (other == null) continue;
                parts.Add((string.IsNullOrWhiteSpace(link.Label) ? "" : link.Label + " → ") + other.DisplayName);
            }
            return parts.Count == 0 ? "No links" : string.Join("\n", parts);
        }

        private string BuildBoardPath(BoardNode start)
        {
            if (_activeBoard == null || start == null) return "-";
            var path = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            BuildPathRecursive(start, null, path, seen, 0);
            return path.Count == 0 ? start.DisplayName : string.Join("  →  ", path);
        }

        private void BuildPathRecursive(BoardNode node, string previousId, List<string> path, HashSet<string> seen, int depth)
        {
            if (node == null || depth > 12 || !seen.Add(node.Id)) return;
            path.Add(string.IsNullOrWhiteSpace(node.DisplayName) ? node.Kind : node.DisplayName);

            var nextIds = _activeBoard.Links
                .Where(x => string.Equals(x.FromNodeId, node.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(x.ToNodeId, node.Id, StringComparison.OrdinalIgnoreCase))
                .Select(x => string.Equals(x.FromNodeId, node.Id, StringComparison.OrdinalIgnoreCase) ? x.ToNodeId : x.FromNodeId)
                .Where(x => !string.Equals(x, previousId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var nextId in nextIds)
            {
                var next = _activeBoard.Nodes.FirstOrDefault(x => string.Equals(x.Id, nextId, StringComparison.OrdinalIgnoreCase));
                if (next != null && !seen.Contains(next.Id))
                {
                    BuildPathRecursive(next, node.Id, path, seen, depth + 1);
                    return;
                }
            }
        }

        private void SyncBoardNodesFromDevices()
        {
            if (_workspace == null) return;
            foreach (var board in _workspace.Boards)
                foreach (var node in board.Nodes)
                    SyncNodeFromDevice(node);
            SaveWorkspace();
        }

        private void SyncNodeFromDevice(BoardNode node)
        {
            if (node == null) return;
            if (IsCameraNode(node))
            {
                var camera = ResolveCamera(node);
                if (camera != null)
                {
                    node.DisplayName = camera.CameraName;
                    node.IpAddress = camera.IpAddress;
                }
            }
            else if (IsSwitchNode(node))
            {
                var sw = ResolveSwitch(node);
                if (sw != null)
                {
                    node.DisplayName = sw.Name;
                    node.IpAddress = sw.IpAddress;
                }
            }
        }

        private CameraDevice ResolveCamera(BoardNode node)
        {
            if (node == null || !IsCameraNode(node)) return null;
            Guid id;
            if (!Guid.TryParse(node.DeviceKey, out id)) return null;
            CameraDevice camera;
            return _camerasById.TryGetValue(id, out camera) ? camera : null;
        }

        private SwitchNode ResolveSwitch(BoardNode node)
        {
            if (node == null || !IsSwitchNode(node) || _workspace.Switches == null) return null;
            return _workspace.Switches.FirstOrDefault(x => string.Equals(x.Id, node.DeviceKey, StringComparison.OrdinalIgnoreCase));
        }

        private NodeState GetNodeState(BoardNode node)
        {
            if (IsCameraNode(node))
            {
                var camera = ResolveCamera(node);
                return camera == null ? NodeState.Unknown : camera.State;
            }
            var sw = ResolveSwitch(node);
            return sw == null ? NodeState.Unknown : sw.State;
        }

        private NodeState GetLinkState(BoardNode a, BoardNode b)
        {
            var sa = GetNodeState(a);
            var sb = GetNodeState(b);
            if (sa == NodeState.Offline || sb == NodeState.Offline) return NodeState.Offline;
            if (sa == NodeState.Online && sb == NodeState.Online) return NodeState.Online;
            return NodeState.Unknown;
        }

        private void DrawLinksOnly()
        {
            LinkCanvas.Children.Clear();
            DrawLinks();
            if (_previewLine != null)
            {
                var source = _activeBoard.Nodes.FirstOrDefault(x => string.Equals(x.Id, _connectSourceNodeId, StringComparison.OrdinalIgnoreCase));
                if (source != null)
                {
                    var center = NodeCenter(source);
                    _previewLine.X1 = center.X;
                    _previewLine.Y1 = center.Y;
                    LinkCanvas.Children.Add(_previewLine);
                }
            }
        }

        private void UpdateSummary()
        {
            if (_activeBoard == null)
            {
                SummaryText.Text = _cameras.Count + " cameras indexed";
                return;
            }

            int online = 0, offline = 0, unknown = 0;
            foreach (var node in _activeBoard.Nodes)
            {
                switch (GetNodeState(node))
                {
                    case NodeState.Online: online++; break;
                    case NodeState.Offline: offline++; break;
                    default: unknown++; break;
                }
            }

            SummaryText.Text = _cameras.Count + " cameras indexed  |  " +
                _activeBoard.Nodes.Count + " nodes / " + _activeBoard.Links.Count + " links  |  " +
                online + " online / " + offline + " offline" + (unknown > 0 ? " / " + unknown + " unknown" : string.Empty);
        }

        private void SaveWorkspace()
        {
            try { WorkspaceStoreService.Save(_workspace); }
            catch (Exception ex) { SummaryText.Text = "Save failed: " + ex.Message; }
        }

        private void CancelConnect()
        {
            CancelConnectPreviewOnly();
            _connectSourceNodeId = null;
            _connectByHandle = false;
            try { NodeCanvas.ReleaseMouseCapture(); } catch { }
        }

        private void CancelConnectPreviewOnly()
        {
            if (_previewLine != null)
            {
                LinkCanvas.Children.Remove(_previewLine);
                _previewLine = null;
            }
        }

        private bool HasAnyLink(TopologyBoard board, string nodeId)
        {
            return board != null && board.Links.Any(x =>
                string.Equals(x.FromNodeId, nodeId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.ToNodeId, nodeId, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsCameraNode(BoardNode node)
        {
            return node != null && string.Equals(node.Kind, "Camera", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSwitchNode(BoardNode node)
        {
            return node != null && string.Equals(node.Kind, "Switch", StringComparison.OrdinalIgnoreCase);
        }

        private static Point NodeCenter(BoardNode node)
        {
            return new Point(node.X + NodeWidth / 2, node.Y + NodeHeight / 2);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static string Empty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private static string StatusText(NodeState state, long ping)
        {
            return state == NodeState.Online && ping >= 0 ? "Online (" + ping + " ms)" : state.ToString();
        }

        private static Brush StateBrush(NodeState state)
        {
            switch (state)
            {
                case NodeState.Online: return new SolidColorBrush(Color.FromRgb(60, 190, 105));
                case NodeState.Offline: return new SolidColorBrush(Color.FromRgb(220, 75, 75));
                default: return new SolidColorBrush(Color.FromRgb(125, 136, 150));
            }
        }
    }
}
