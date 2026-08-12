using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using NSPGatekeeper.Controller.Configuration;
using NSPGatekeeper.Controller.Domain;
using NSPGatekeeper.Controller.Infrastructure.Database;
using NSPGatekeeper.Controller.Infrastructure.Logging;
using NSPGatekeeper.Controller.Services;

namespace NSPGatekeeper.Controller.UI
{
    public sealed class MainForm : Form
    {
        private readonly AppSettings _settings;
        private readonly ControllerRuntime _runtime;
        private readonly ReaderManager _readers;
        private readonly LocalStore _store;
        private readonly FileLogger _logger;

        private readonly TextBox _serverUrl = new TextBox();
        private readonly TextBox _controllerCode = new TextBox();
        private readonly TextBox _clientId = new TextBox();
        private readonly TextBox _clientSecret = new TextBox();
        private readonly CheckBox _discovery = new CheckBox();
        private readonly Label _connectionStatus = new Label();
        private readonly Label _modeStatus = new Label();
        private readonly Label _laneCalibrationStatus = new Label();
        private readonly DataGridView _readerGrid = CreateGrid();
        private readonly Label _comPortStatus = new Label();
        private readonly DataGridView _laneCalibrationGrid = CreateGrid();
        private readonly TextBox _logBox = new TextBox();
        private readonly Timer _refreshTimer = new Timer();
        private string _comPortSignature = string.Empty;
        private string _readerRowsSignature = string.Empty;

        public MainForm(AppSettings settings, ControllerRuntime runtime, ReaderManager readers, LocalStore store, FileLogger logger)
        {
            _settings = settings;
            _runtime = runtime;
            _readers = readers;
            _store = store;
            _logger = logger;

            Text = "NSP Gatekeeper Controller";
            Width = 1420;
            Height = 760;
            MinimumSize = new Size(1100, 640);
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            LoadSettings();

            _runtime.StateChanged += OnRuntimeStateChanged;
            _logger.EntryWritten += OnLog;
            LoadRecentLogs();

            _refreshTimer.Interval = 2000;
            _refreshTimer.Tick += delegate { RefreshComPorts(false); RefreshRuntimeView(); };
            _refreshTimer.Start();
            Shown += delegate { RefreshComPorts(true); RefreshRuntimeView(); };
            FormClosed += OnClosed;
        }

        private void BuildUi()
        {
            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildControllerTab());
            tabs.TabPages.Add(BuildReadersTab());
            tabs.TabPages.Add(BuildLaneCalibrationTab());
            tabs.TabPages.Add(BuildLogsTab());
            Controls.Add(tabs);
        }

        private TabPage BuildControllerTab()
        {
            var tab = new TabPage("Controller");
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(20),
                ColumnCount = 2,
                RowCount = 11
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddField(layout, 0, "Server URL", _serverUrl);
            AddField(layout, 1, "Controller Code", _controllerCode);
            AddField(layout, 2, "Core API Client ID", _clientId);
            _clientSecret.UseSystemPasswordChar = true;
            AddField(layout, 3, "Core API Client Secret", _clientSecret);

            _discovery.Text = "Auto-discover only when configured Server URL cannot connect";
            _discovery.AutoSize = true;
            layout.Controls.Add(new Label { Text = "Zeroconf", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
            layout.Controls.Add(_discovery, 1, 4);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            var save = new Button { Text = "Save", AutoSize = true };
            var test = new Button { Text = "Test Connection", AutoSize = true };
            var sync = new Button { Text = "Sync Runtime Config", AutoSize = true };
            save.Click += delegate { SaveSettings(); };
            test.Click += async delegate { await RunUiAction(test, _runtime.TestConnectionOnce); };
            sync.Click += async delegate { await RunUiAction(sync, _runtime.PullReaderConfigOnce); };
            buttons.Controls.Add(save);
            buttons.Controls.Add(test);
            buttons.Controls.Add(sync);
            layout.Controls.Add(new Label(), 0, 5);
            layout.Controls.Add(buttons, 1, 5);

            _connectionStatus.AutoSize = true;
            _connectionStatus.Font = new Font(Font, FontStyle.Bold);
            layout.Controls.Add(new Label { Text = "Status", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
            layout.Controls.Add(_connectionStatus, 1, 6);

            _modeStatus.AutoSize = true;
            _modeStatus.Font = new Font(Font, FontStyle.Bold);
            layout.Controls.Add(new Label { Text = "Runtime Mode", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 7);
            layout.Controls.Add(_modeStatus, 1, 7);

            ConfigureRuntimeContextLabel(_laneCalibrationStatus);
            layout.Controls.Add(new Label { Text = "Lane Calibration", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 8);
            layout.Controls.Add(_laneCalibrationStatus, 1, 8);

            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(780, 0),
                Text = "Runtime context is reported by Edge. Controller only connects Readers, applies Reader-level technical settings and forwards raw RFID observations."
            };
            layout.Controls.Add(new Label(), 0, 9);
            layout.Controls.Add(note, 1, 9);
            tab.Controls.Add(layout);
            return tab;
        }

        private TabPage BuildReadersTab()
        {
            var tab = new TabPage("Readers");
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(8)
            };
            var refresh = new Button { Text = "Refresh", AutoSize = true };
            refresh.Click += delegate { RefreshComPorts(true); RefreshReaderGrid(); };

            _comPortStatus.AutoSize = true;
            _comPortStatus.ForeColor = Color.DimGray;
            _comPortStatus.Padding = new Padding(8, 6, 0, 0);
            toolbar.Controls.Add(refresh);
            toolbar.Controls.Add(_comPortStatus);

            _readerGrid.Dock = DockStyle.Fill;
            _readerGrid.AutoGenerateColumns = false;
            _readerGrid.Columns.Clear();
            _readerGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "DetectedSerialNumber",
                HeaderText = "Detected SDK Serial",
                FillWeight = 18
            });
            _readerGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Connection",
                HeaderText = "COM / Connection",
                FillWeight = 16
            });
            _readerGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "State",
                HeaderText = "Status",
                FillWeight = 12
            });
            _readerGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "ConfigSource",
                HeaderText = "Applied From",
                FillWeight = 12
            });
            _readerGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Power",
                HeaderText = "RF Power",
                FillWeight = 10
            });
            _readerGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "ReadInterval",
                HeaderText = "Read Interval",
                FillWeight = 11
            });
            _readerGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "TidStart",
                HeaderText = "TID Start",
                FillWeight = 8
            });
            _readerGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "TidLength",
                HeaderText = "TID Length",
                FillWeight = 8
            });
            _readerGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "AppliedAt",
                HeaderText = "Applied At",
                FillWeight = 15
            });
            _readerGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Detail",
                HeaderText = "Detail",
                FillWeight = 20
            });
            _readerGrid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs args)
            {
                if (args.RowIndex < 0 || args.RowIndex >= _readerGrid.Rows.Count) return;
                var row = _readerGrid.Rows[args.RowIndex].DataBoundItem as ReaderUiRow;
                if (row == null) return;
                _readerGrid.Rows[args.RowIndex].DefaultCellStyle.ForeColor = row.Online
                    ? Color.DarkGreen
                    : Color.Firebrick;
            };

            var note = new Label
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                Padding = new Padding(10, 6, 10, 8),
                ForeColor = Color.DimGray,
                Text = "Only physical Readers observed through the SDK are shown. Applied Configuration is last-confirmed after the SDK configuration call succeeds; offline Readers show the last confirmed values."
            };

            tab.Controls.Add(_readerGrid);
            tab.Controls.Add(note);
            tab.Controls.Add(toolbar);
            return tab;
        }

        private TabPage BuildLaneCalibrationTab()
        {
            var tab = new TabPage("Live RFID / Lane Calibration");
            _laneCalibrationGrid.Dock = DockStyle.Fill;
            _laneCalibrationGrid.AutoGenerateColumns = true;
            tab.Controls.Add(_laneCalibrationGrid);
            return tab;
        }

        private TabPage BuildLogsTab()
        {
            var tab = new TabPage("Logs");
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
            var openFolder = new Button { Text = "Open Log Folder", AutoSize = true };
            var clearView = new Button { Text = "Clear View", AutoSize = true };
            var location = new Label
            {
                Text = _logger == null ? string.Empty : _logger.DirectoryPath,
                AutoSize = true,
                ForeColor = Color.DimGray,
                Padding = new Padding(8, 6, 0, 0)
            };

            openFolder.Click += delegate
            {
                try
                {
                    Process.Start("explorer.exe", _logger.DirectoryPath);
                }
                catch (Exception ex)
                {
                    if (_logger != null) _logger.Error("log-ui", "Could not open log directory", ex, "path=" + _logger.DirectoryPath);
                    MessageBox.Show(ex.Message, "NSP Gatekeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            clearView.Click += delegate { _logBox.Clear(); };
            toolbar.Controls.Add(openFolder);
            toolbar.Controls.Add(clearView);
            toolbar.Controls.Add(location);

            _logBox.Dock = DockStyle.Fill;
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Both;
            _logBox.WordWrap = false;
            _logBox.Font = new Font("Consolas", 9F);
            tab.Controls.Add(_logBox);
            tab.Controls.Add(toolbar);
            return tab;
        }

        private static DataGridView CreateGrid()
        {
            return new DataGridView
            {
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window
            };
        }

        private static void AddField(TableLayoutPanel layout, int row, string label, Control control)
        {
            control.Dock = DockStyle.Fill;
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            layout.Controls.Add(control, 1, row);
        }

        private static void ConfigureRuntimeContextLabel(Label label)
        {
            label.AutoSize = true;
            label.MaximumSize = new Size(780, 0);
            label.ForeColor = Color.DimGray;
        }

        private void LoadSettings()
        {
            _serverUrl.Text = _settings.CoreApiBaseUrl ?? string.Empty;
            _controllerCode.Text = _settings.ControllerCode ?? string.Empty;
            _clientId.Text = _settings.CoreApiClientId ?? string.Empty;
            _clientSecret.Text = _settings.CoreApiClientSecret ?? string.Empty;
            _discovery.Checked = _settings.DiscoveryEnabled;
        }

        private void SaveSettings()
        {
            _settings.CoreApiBaseUrl = AppSettings.NormalizeBaseUrl(_serverUrl.Text);
            _settings.ControllerCode = (_controllerCode.Text ?? string.Empty).Trim();
            _settings.CoreApiClientId = (_clientId.Text ?? string.Empty).Trim();
            _settings.CoreApiClientSecret = _clientSecret.Text ?? string.Empty;
            _settings.DiscoveryEnabled = _discovery.Checked;
            _settings.SaveConnection();
            _runtime.ResetConnection();
            MessageBox.Show("Controller settings saved.", "NSP Gatekeeper", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async Task RunUiAction(Control source, Action action)
        {
            source.Enabled = false;
            try
            {
                await Task.Run(action);
                _serverUrl.Text = _settings.CoreApiBaseUrl ?? string.Empty;
                RefreshRuntimeView();
            }
            catch (Exception ex)
            {
                if (_logger != null) _logger.Error("ui-action", "UI action failed", ex, "control=" + (source == null ? "<unknown>" : source.Text));
                MessageBox.Show(ex.Message, "NSP Gatekeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                source.Enabled = true;
            }
        }

        private void RefreshRuntimeView()
        {
            if (InvokeRequired) { BeginInvoke(new Action(RefreshRuntimeView)); return; }
            _connectionStatus.Text = (_runtime.Running ? "Running" : "Stopped") + " | " + _runtime.ConnectionMessage;
            var runtimeContext = _runtime.RuntimeContext ?? new ControllerRuntimeContextSnapshot();
            _modeStatus.Text = runtimeContext.Mode ?? "Idle";
            _laneCalibrationStatus.Text = FormatLaneCalibration(runtimeContext.LaneCalibration);
            try
            {
                RefreshReaderGrid();
            }
            catch (Exception ex)
            {
                if (_logger != null) _logger.Warn("reader-ui", "Could not refresh Reader list", ex.Message);
            }

            var detections = _readers.GetRecentDetections()
                .OrderByDescending(x => x.DetectedAtUtc)
                .Select(x => new
                {
                    Time = x.DetectedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    Reader = x.SerialNumber,
                    Port = x.PortNo,
                    TID = x.Tid,
                    RSSI = x.RssiDbm
                }).ToList();
            _laneCalibrationGrid.DataSource = detections;
        }


        private static string FormatLaneCalibration(LaneCalibrationSessionConfig calibration)
        {
            if (calibration == null || !calibration.IsActiveForController
                || string.IsNullOrWhiteSpace(calibration.LaneCalibrationCode))
                return "None";
            var status = string.IsNullOrWhiteSpace(calibration.Status)
                ? "Running"
                : ToTitle(calibration.Status);
            return calibration.LaneCalibrationCode
                + " · " + status
                + " · R" + Math.Max(1, calibration.Revision)
                + " · Readers: " + (calibration.Readers == null ? 0 : calibration.Readers.Count);
        }

        private static string ToTitle(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;
            return char.ToUpperInvariant(value[0]) + value.Substring(1).ToLowerInvariant();
        }

        private void RefreshComPorts(bool force)
        {
            try
            {
                var ports = SerialPort.GetPortNames()
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(ComPortNumber)
                    .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var signature = string.Join("|", ports);
                if (!force && string.Equals(signature, _comPortSignature, StringComparison.Ordinal)) return;

                _comPortSignature = signature;
                _comPortStatus.Text = ports.Count == 0
                    ? "Windows COM ports: none detected"
                    : "Windows COM ports: " + string.Join(", ", ports);
                _comPortStatus.ForeColor = ports.Count == 0 ? Color.Firebrick : Color.DimGray;

                if (_logger != null)
                    _logger.Info(
                        "reader-ui",
                        "Windows COM port list changed",
                        "ports=" + (ports.Count == 0 ? "none" : string.Join(",", ports)));
            }
            catch (Exception ex)
            {
                _comPortStatus.Text = "Windows COM ports: detection failed";
                _comPortStatus.ForeColor = Color.Firebrick;
                if (_logger != null)
                    _logger.Warn("reader-ui", "Could not enumerate Windows COM ports", ex.Message);
            }
        }

        private void RefreshReaderGrid()
        {
            var rows = _store.GetReaderStatuses()
                .Where(status => status != null && !string.IsNullOrWhiteSpace(status.DetectedSdkSerialNumber))
                .GroupBy(
                    status => status.DetectedSdkSerialNumber.Trim().ToUpperInvariant(),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(status => status.UpdatedAtUtc).First())
                .OrderBy(status => status.DetectedSdkSerialNumber, StringComparer.OrdinalIgnoreCase)
                .Select(status => new ReaderUiRow
                {
                    DetectedSerialNumber = status.DetectedSdkSerialNumber.Trim().ToUpperInvariant(),
                    Connection = !string.IsNullOrWhiteSpace(status.DetectedEndpoint)
                        ? status.DetectedEndpoint
                        : (status.Endpoint ?? string.Empty),
                    State = status.Online ? "Online" : "Detected",
                    ConfigSource = status.ConfigurationApplied
                        ? (status.ConfigurationSource ?? "Applied")
                        : "Pending",
                    Power = status.ConfigurationApplied ? status.PowerDbm + " dBm" : "—",
                    ReadInterval = status.ConfigurationApplied ? status.ReadIntervalMs + " ms" : "—",
                    TidStart = status.ConfigurationApplied ? status.TidStartAddress.ToString() : "—",
                    TidLength = status.ConfigurationApplied ? status.TidLength.ToString() : "—",
                    AppliedAt = status.ConfigurationAppliedAtUtc.HasValue
                        ? status.ConfigurationAppliedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                        : "—",
                    Detail = status.Message ?? string.Empty,
                    Online = status.Online
                })
                .ToList();

            var signature = string.Join(
                "|",
                rows.Select(row =>
                    row.DetectedSerialNumber + ";"
                    + row.Connection + ";"
                    + row.State + ";"
                    + row.ConfigSource + ";"
                    + row.Power + ";"
                    + row.ReadInterval + ";"
                    + row.TidStart + ";"
                    + row.TidLength + ";"
                    + row.AppliedAt + ";"
                    + row.Detail));
            if (string.Equals(signature, _readerRowsSignature, StringComparison.Ordinal)) return;

            _readerGrid.DataSource = new BindingList<ReaderUiRow>(rows);
            _readerRowsSignature = signature;
        }

        private static int ComPortNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) return int.MaxValue;
            int number;
            return int.TryParse(value.Substring(3), out number) ? number : int.MaxValue;
        }


        private void LoadRecentLogs()
        {
            if (_logger == null) return;
            foreach (var entry in _logger.Snapshot(500))
                _logBox.AppendText(entry + Environment.NewLine);
        }

        private void OnRuntimeStateChanged()
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(RefreshRuntimeView));
            }
            catch (InvalidOperationException)
            {
                // The form handle can disappear during application shutdown.
            }
        }

        private void OnLog(LogEntry entry)
        {
            if (IsDisposed) return;
            Action append = delegate
            {
                _logBox.AppendText(entry + Environment.NewLine);
                if (_logBox.TextLength > 200000) _logBox.Text = _logBox.Text.Substring(_logBox.TextLength - 150000);
            };
            try
            {
                if (InvokeRequired) BeginInvoke(append); else append();
            }
            catch (InvalidOperationException)
            {
                // Ignore UI dispatch after the form handle has been destroyed.
            }
        }

        private sealed class ReaderUiRow
        {
            public string DetectedSerialNumber { get; set; }
            public string Connection { get; set; }
            public string State { get; set; }
            public string ConfigSource { get; set; }
            public string Power { get; set; }
            public string ReadInterval { get; set; }
            public string TidStart { get; set; }
            public string TidLength { get; set; }
            public string AppliedAt { get; set; }
            public string Detail { get; set; }
            public bool Online { get; set; }
        }

        private void OnClosed(object sender, FormClosedEventArgs e)
        {
            _refreshTimer.Stop();
            _runtime.StateChanged -= OnRuntimeStateChanged;
            _logger.EntryWritten -= OnLog;
        }
    }
}
