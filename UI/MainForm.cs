using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        private readonly DataGridView _readerGrid = CreateGrid();
        private readonly TextBox _readerSerial = new TextBox();
        private readonly TextBox _readerDriver = new TextBox();
        private readonly ComboBox _readerEndpoint = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        private readonly Label _comPortStatus = new Label();
        private readonly NumericUpDown _readerPort = new NumericUpDown();
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
            Width = 1100;
            Height = 720;
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            LoadSettings();

            _runtime.StateChanged += OnRuntimeStateChanged;
            _logger.EntryWritten += OnLog;

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
                RowCount = 9
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
            var sync = new Button { Text = "Sync Reader Config", AutoSize = true };
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

            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(780, 0),
                Text = "Controller keeps only reader configuration, technical status and the raw RFID detection outbox. Business processing is handled by Edge."
            };
            layout.Controls.Add(new Label(), 0, 8);
            layout.Controls.Add(note, 1, 8);
            tab.Controls.Add(layout);
            return tab;
        }

        private TabPage BuildReadersTab()
        {
            var tab = new TabPage("Readers");
            _readerGrid.Dock = DockStyle.Fill;
            _readerGrid.AutoGenerateColumns = true;
            _readerGrid.SelectionChanged += delegate { LoadSelectedReaderConnection(); };

            var local = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                Padding = new Padding(10),
                ColumnCount = 6,
                RowCount = 4
            };
            local.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            local.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
            local.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            local.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
            local.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            local.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));

            _readerSerial.ReadOnly = true;
            _readerPort.Minimum = 0;
            _readerPort.Maximum = 65535;
            _readerPort.Width = 100;
            AddInlineField(local, 0, 0, "Serial", _readerSerial);
            AddInlineField(local, 2, 0, "Driver", _readerDriver);
            AddInlineField(local, 4, 0, "Port", _readerPort);
            AddInlineField(local, 0, 1, "Endpoint", _readerEndpoint);
            local.SetColumnSpan(_readerEndpoint, 3);

            var localButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            var refreshCom = new Button { Text = "Refresh COM", AutoSize = true };
            var saveLocal = new Button { Text = "Save Local Connection", AutoSize = true };
            refreshCom.Click += delegate { RefreshComPorts(true); };
            saveLocal.Click += delegate { SaveSelectedReaderConnection(); };
            localButtons.Controls.Add(refreshCom);
            localButtons.Controls.Add(saveLocal);
            local.Controls.Add(localButtons, 4, 1);
            local.SetColumnSpan(localButtons, 2);

            _comPortStatus.AutoSize = true;
            _comPortStatus.ForeColor = Color.DimGray;
            local.Controls.Add(_comPortStatus, 0, 2);
            local.SetColumnSpan(_comPortStatus, 6);

            var note = new Label
            {
                Text = "Readers are created by Edge configuration. Windows COM ports are shown in Endpoint for local binding. Empty CF-E718 endpoint uses SDK Auto COM discovery.",
                AutoSize = true,
                ForeColor = Color.DimGray
            };
            local.Controls.Add(note, 0, 3);
            local.SetColumnSpan(note, 6);

            tab.Controls.Add(_readerGrid);
            tab.Controls.Add(local);
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
            _logBox.Dock = DockStyle.Fill;
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Both;
            _logBox.WordWrap = false;
            _logBox.Font = new Font("Consolas", 9F);
            tab.Controls.Add(_logBox);
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

        private static void AddInlineField(TableLayoutPanel layout, int labelColumn, int row, string label, Control control)
        {
            control.Dock = DockStyle.Fill;
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, labelColumn, row);
            layout.Controls.Add(control, labelColumn + 1, row);
        }

        private void LoadSelectedReaderConnection()
        {
            try
            {
                if (_readerGrid.CurrentRow == null) return;
                var status = _readerGrid.CurrentRow.DataBoundItem as ReaderStatus;
                if (status == null || string.IsNullOrWhiteSpace(status.SerialNumber)) return;
                var config = _store.GetReaderConfigs().FirstOrDefault(x => string.Equals(x.SerialNumber, status.SerialNumber, StringComparison.OrdinalIgnoreCase));
                if (config == null) return;
                _readerSerial.Text = config.SerialNumber ?? string.Empty;
                _readerDriver.Text = config.DriverKey ?? string.Empty;
                _readerEndpoint.Text = !string.IsNullOrWhiteSpace(config.Endpoint)
                    ? config.Endpoint
                    : NormalizeDetectedEndpoint(status.Endpoint);
                _readerPort.Value = Math.Max(_readerPort.Minimum, Math.Min(_readerPort.Maximum, config.Port));
            }
            catch { }
        }

        private void SaveSelectedReaderConnection()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_readerSerial.Text))
                    throw new InvalidOperationException("Select a Reader first.");
                _store.UpdateLocalReaderConnection(
                    _readerSerial.Text,
                    _readerDriver.Text,
                    _readerEndpoint.Text,
                    Convert.ToInt32(_readerPort.Value));
                _readers.ReloadCachedConfiguration();
                MessageBox.Show("Local Reader connection saved.", "NSP Gatekeeper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "NSP Gatekeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void AddField(TableLayoutPanel layout, int row, string label, Control control)
        {
            control.Dock = DockStyle.Fill;
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            layout.Controls.Add(control, 1, row);
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
            _modeStatus.Text = _runtime.Mode + (string.IsNullOrWhiteSpace(_runtime.LaneCalibrationCode) ? string.Empty : " | " + _runtime.LaneCalibrationCode);
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

                var current = _readerEndpoint.Text;
                _readerEndpoint.BeginUpdate();
                try
                {
                    _readerEndpoint.Items.Clear();
                    foreach (var port in ports) _readerEndpoint.Items.Add(port);
                }
                finally
                {
                    _readerEndpoint.EndUpdate();
                }
                _readerEndpoint.Text = current;
                _comPortSignature = signature;
                _comPortStatus.Text = ports.Count == 0
                    ? "Windows COM ports: none detected"
                    : "Windows COM ports: " + string.Join(", ", ports);
                _comPortStatus.ForeColor = ports.Count == 0 ? Color.Firebrick : Color.DimGray;
            }
            catch (Exception ex)
            {
                _comPortStatus.Text = "Windows COM ports: detection failed";
                _comPortStatus.ForeColor = Color.Firebrick;
                if (_logger != null) _logger.Warn("reader-ui", "Could not enumerate Windows COM ports", ex.Message);
            }
        }

        private void RefreshReaderGrid()
        {
            var configs = _store.GetReaderConfigs()
                .Where(value => value != null && !string.IsNullOrWhiteSpace(value.SerialNumber))
                .OrderBy(value => value.SerialNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var statuses = _store.GetReaderStatuses()
                .Where(value => value != null && !string.IsNullOrWhiteSpace(value.SerialNumber))
                .ToDictionary(value => value.SerialNumber.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);

            var rows = new List<ReaderStatus>();
            foreach (var config in configs)
            {
                var serial = config.SerialNumber.Trim().ToUpperInvariant();
                ReaderStatus status;
                if (!statuses.TryGetValue(serial, out status))
                {
                    status = new ReaderStatus
                    {
                        DriverKey = config.DriverKey,
                        SerialNumber = serial,
                        Model = config.DriverKey,
                        Endpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? "AUTO-COM" : config.Endpoint,
                        Online = false,
                        Message = config.Enabled ? "waiting_for_runtime_status" : "disabled_or_no_ports",
                        PowerDbm = config.PowerDbm,
                        ReadIntervalMs = config.ReadIntervalMs,
                        Ports = config.PortNumbers(),
                        UpdatedAtUtc = DateTime.MinValue
                    };
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(status.Endpoint))
                        status.Endpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? "AUTO-COM" : config.Endpoint;
                }
                rows.Add(status);
            }

            var selectedSerial = _readerGrid.CurrentRow == null
                ? string.Empty
                : ((_readerGrid.CurrentRow.DataBoundItem as ReaderStatus)?.SerialNumber ?? string.Empty);
            var signature = string.Join("|", rows.Select(value =>
                (value.SerialNumber ?? string.Empty) + ";"
                + (value.Endpoint ?? string.Empty) + ";"
                + value.Online + ";"
                + (value.Message ?? string.Empty) + ";"
                + value.UpdatedAtUtc.Ticks));
            if (string.Equals(signature, _readerRowsSignature, StringComparison.Ordinal)) return;

            _readerGrid.DataSource = new BindingList<ReaderStatus>(rows);
            _readerRowsSignature = signature;
            if (!string.IsNullOrWhiteSpace(selectedSerial))
            {
                foreach (DataGridViewRow row in _readerGrid.Rows)
                {
                    var value = row.DataBoundItem as ReaderStatus;
                    if (value != null && string.Equals(value.SerialNumber, selectedSerial, StringComparison.OrdinalIgnoreCase))
                    {
                        row.Selected = true;
                        _readerGrid.CurrentCell = row.Cells.Count == 0 ? null : row.Cells[0];
                        break;
                    }
                }
            }
        }

        private static int ComPortNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) return int.MaxValue;
            int number;
            return int.TryParse(value.Substring(3), out number) ? number : int.MaxValue;
        }

        private static string NormalizeDetectedEndpoint(string value)
        {
            value = (value ?? string.Empty).Trim().ToUpperInvariant();
            return value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && value != "AUTO-COM" ? value : string.Empty;
        }

        private void OnRuntimeStateChanged()
        {
            if (IsDisposed) return;
            try { BeginInvoke(new Action(RefreshRuntimeView)); } catch { }
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
            catch { }
        }

        private void OnClosed(object sender, FormClosedEventArgs e)
        {
            _refreshTimer.Stop();
            _runtime.StateChanged -= OnRuntimeStateChanged;
            _logger.EntryWritten -= OnLog;
        }
    }
}
