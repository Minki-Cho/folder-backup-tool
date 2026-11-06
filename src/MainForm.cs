using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DdalggakBackup
{
    public sealed class MainForm : Form
    {
        private static readonly Color Ink = Color.FromArgb(30, 45, 60);
        private static readonly Color Muted = Color.FromArgb(98, 111, 126);
        private static readonly Color Accent = Color.FromArgb(34, 97, 191);
        private static readonly Color Good = Color.FromArgb(30, 118, 82);
        private static readonly Color Caution = Color.FromArgb(151, 95, 19);
        private static readonly Color Bad = Color.FromArgb(182, 49, 49);

        private AppSettings _settings;
        private readonly bool _persistSettings;
        private string _settingsWarning;
        private ComboBox _profile, _mode;
        private TextBox _source, _backup, _name, _preview, _detailText;
        private GroupBox _pathGroup, _itemGroup, _nameGroup, _dropGroup;
        private Panel _scrollHost;
        private TableLayoutPanel _rootLayout;
        private ListBox _dropList;
        private Label _dropHint, _modeLabel;
        private Button _selectAll;
        private Label _date, _folderName, _nameError, _selectionCount, _state, _counts, _currentFile, _notice;
        private Button _start, _cancel, _openFolder, _openLog;
        private CheckBox[] _checks;
        private Label[] _sourceStates;
        private bool[] _available;
        private string[] _activeItems = (string[])Catalog.Items.Clone();
        private DataGridView _results;
        private ListView _details;
        private TabControl _tabs;
        private ProgressBar _progress;
        private readonly ToolTip _tips = new ToolTip();
        private readonly System.Windows.Forms.Timer _previewTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _clockTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _progressTimer = new System.Windows.Forms.Timer();
        private readonly object _progressLock = new object();
        private BackupProgress _latestProgress;
        private ApplyPhase? _latestApplyPhase;
        private CancellationTokenSource _cancellation;
        private BackupResult _lastResult;
        private ApplyResult _lastApplyResult;
        private bool _runningApply;
        private bool _busy, _closeRequested, _loading;
        private int _sourceGeneration, _previewGeneration;
        private DateTime _displayDay;
        private string _validName;
        public Task Initialization { get; private set; }
        private bool ApplyMode { get { return _mode != null && _mode.SelectedIndex == 1; } }

        public MainForm() : this(LoadSettings(), true, null) { }

        private static AppSettings LoadSettings()
        {
            string warning;
            AppSettings result = SettingsStore.Load(out warning);
            _loadWarning = warning;
            return result;
        }
        [ThreadStatic] private static string _loadWarning;

        internal MainForm(AppSettings settings, bool persistSettings, string warning)
        {
            _settings = settings;
            _persistSettings = persistSettings;
            _settingsWarning = warning ?? (persistSettings ? _loadWarning : null);
            _loadWarning = null;
            Initialization = Task.FromResult(0);
            _loading = true;
            Text = "딸깍 백업 · 적용";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("맑은 고딕", 9F, FontStyle.Regular, GraphicsUnit.Point);
            ForeColor = Ink;
            BackColor = Color.FromArgb(246, 248, 251);
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(1120, workingArea.Width - 60), Math.Min(870, workingArea.Height - 70));
            MinimumSize = new Size(1000, 660);
            BuildLayout();

            foreach (MachineProfile profile in _settings.Profiles) _profile.Items.Add(profile);
            _profile.SelectedIndex = Math.Max(0, Math.Min(_settings.SelectedProfile, _profile.Items.Count - 1));
            _name.Text = _settings.LastName ?? "";
            ShowProfile();
            _loading = false;
            UpdateModeLayout();
            UpdateNameAndSelection();

            _previewTimer.Interval = 280;
            _previewTimer.Tick += async delegate { _previewTimer.Stop(); await RefreshPreviewAsync(); };
            _clockTimer.Interval = 1000;
            _clockTimer.Tick += delegate
            {
                if (!_busy && DateTime.Now.Date != _displayDay) UpdateNameAndSelection();
            };
            _clockTimer.Start();
            _progressTimer.Interval = 100;
            _progressTimer.Tick += delegate { DrainProgress(); };
            FormClosing += OnClosing;
            FormClosed += delegate
            {
                _sourceGeneration++;
                _previewGeneration++;
                _previewTimer.Dispose();
                _clockTimer.Dispose();
                _progressTimer.Dispose();
                _tips.Dispose();
            };
        }

        protected override void OnShown(EventArgs e)
        {
            Initialization = RefreshSourceStatesAsync();
            base.OnShown(e);
            if (!String.IsNullOrEmpty(_settingsWarning))
            {
                _notice.Text = _settingsWarning;
                _notice.ForeColor = Caution;
                _tips.SetToolTip(_notice, _settingsWarning);
            }
        }

        private void BuildLayout()
        {
            Panel scrollHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            _scrollHost = scrollHost;
            Controls.Add(scrollHost);
            TableLayoutPanel root = new TableLayoutPanel();
            _rootLayout = root;
            root.Location = Point.Empty;
            root.Size = new Size(ClientSize.Width, Math.Max(710, ClientSize.Height - 105));
            root.Padding = new Padding(22, 16, 22, 0);
            root.ColumnCount = 1;
            root.RowCount = 6;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 176));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 119));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            scrollHost.Controls.Add(root);
            scrollHost.Resize += delegate
            {
                ResizeContent();
            };

            Panel heading = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            Label title = MakeLabel("딸깍 백업 · 적용", 23, Ink);
            title.Dock = DockStyle.None;
            title.Location = new Point(0, 0);
            title.Size = new Size(330, 37);
            title.Font = new Font(Font.FontFamily, 23, FontStyle.Bold);
            heading.Controls.Add(title);
            Label subtitle = MakeLabel("선택 항목 백업 또는 한 항목의 사전 백업 후 파일 적용", 9, Muted);
            subtitle.Dock = DockStyle.None;
            subtitle.Location = new Point(2, 40);
            subtitle.Size = new Size(670, 22);
            heading.Controls.Add(subtitle);
            FlowLayoutPanel modePanel = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 272, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            _modeLabel = MakeLabel("작업 방식 · 백업", 8.5F, Muted);
            _modeLabel.AutoSize = true;
            _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 258, AccessibleName = "작업 방식" };
            _mode.Items.AddRange(new object[] { "백업", "백업 후 적용 (항목 1개)" });
            _mode.SelectedIndex = 0;
            _mode.SelectedIndexChanged += delegate
            {
                if (_loading || _busy) return;
                ChangeAllChecks(false);
                UpdateModeLayout();
                UpdateNameAndSelection();
            };
            modePanel.Controls.Add(_modeLabel);
            modePanel.Controls.Add(_mode);
            heading.Controls.Add(modePanel);
            root.Controls.Add(heading, 0, 0);

            _pathGroup = MakeGroup("01   장비와 기본 경로");
            TableLayoutPanel paths = NewTable(3, 3);
            paths.Padding = new Padding(12, 6, 12, 5);
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 259));
            paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            paths.Controls.Add(MakeLabel("사용 장비", 9, Muted), 0, 0);
            _profile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name", Width = 200, Anchor = AnchorStyles.Left, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 21 };
            _profile.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                e.DrawBackground();
                int index = e.Index < 0 ? _profile.SelectedIndex : e.Index;
                if (index >= 0 && index < _profile.Items.Count)
                {
                    Rectangle bounds = e.Bounds;
                    bounds.X += 5;
                    TextRenderer.DrawText(e.Graphics, _profile.Items[index].ToString(), e.Font, bounds, e.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
                e.DrawFocusRectangle();
            };
            _profile.SelectedIndexChanged += async delegate
            {
                if (_loading || _busy) return;
                ShowProfile();
                SaveSettings(false);
                await RefreshSourceStatesAsync();
            };
            paths.Controls.Add(_profile, 1, 0);
            FlowLayoutPanel pathButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            Button editPaths = MakeButton("경로·항목 설정", 126);
            editPaths.Click += EditPaths;
            Button refresh = MakeButton("상태 새로고침", 112);
            refresh.Click += async delegate { await RefreshSourceStatesAsync(); };
            pathButtons.Controls.Add(editPaths);
            pathButtons.Controls.Add(refresh);
            paths.Controls.Add(pathButtons, 2, 0);
            paths.Controls.Add(MakeLabel("원본 기본 경로", 9, Muted), 0, 1);
            paths.Controls.Add(MakeLabel("백업 기본 경로", 9, Muted), 0, 2);
            _source = ReadOnlyBox();
            _backup = ReadOnlyBox();
            paths.Controls.Add(_source, 1, 1);
            paths.SetColumnSpan(_source, 2);
            paths.Controls.Add(_backup, 1, 2);
            paths.SetColumnSpan(_backup, 2);
            _pathGroup.Controls.Add(paths);
            root.Controls.Add(_pathGroup, 0, 1);

            TableLayoutPanel configure = NewTable(2, 1);
            configure.Margin = Padding.Empty;
            configure.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
            configure.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
            _itemGroup = MakeGroup("02   백업할 항목");
            _itemGroup.Margin = new Padding(0, 3, 9, 7);
            TableLayoutPanel items = NewTable(2, 4);
            items.Padding = new Padding(12, 3, 12, 4);
            items.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            items.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int r = 0; r < 3; r++) items.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            items.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _checks = new CheckBox[Catalog.Items.Length];
            _sourceStates = new Label[Catalog.Items.Length];
            _available = new bool[Catalog.Items.Length];
            for (int i = 0; i < Catalog.Items.Length; i++)
            {
                TableLayoutPanel item = NewTable(2, 1);
                item.Margin = new Padding(0, 0, 10, 0);
                item.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                item.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
                CheckBox check = new CheckBox { Text = "", Dock = DockStyle.Fill, Enabled = false, AutoSize = false, AutoEllipsis = true, Checked = false };
                check.CheckedChanged += delegate(object sender, EventArgs e) { OnItemSelectionChanged((CheckBox)sender); };
                Label status = MakeLabel("확인 대기", 8.5F, Muted);
                status.AutoEllipsis = true;
                _checks[i] = check;
                _sourceStates[i] = status;
                item.Controls.Add(check, 0, 0);
                item.Controls.Add(status, 1, 0);
                items.Controls.Add(item, i / 3, i % 3);
            }
            FlowLayoutPanel selectionButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            Button selectAll = MakeButton("전체 선택", 91);
            _selectAll = selectAll;
            selectAll.Height = 29;
            selectAll.Click += delegate { ChangeAllChecks(true); };
            Button clearAll = MakeButton("전체 해제", 91);
            clearAll.Height = 29;
            clearAll.Click += delegate { ChangeAllChecks(false); };
            _selectionCount = MakeLabel("0개 선택", 8.5F, Muted);
            _selectionCount.AutoSize = false;
            _selectionCount.Size = new Size(110, 29);
            _selectionCount.Margin = new Padding(10, 1, 0, 0);
            selectionButtons.Controls.Add(selectAll);
            selectionButtons.Controls.Add(clearAll);
            selectionButtons.Controls.Add(_selectionCount);
            items.Controls.Add(selectionButtons, 0, 3);
            items.SetColumnSpan(selectionButtons, 2);
            _itemGroup.Controls.Add(items);
            configure.Controls.Add(_itemGroup, 0, 0);

            _nameGroup = MakeGroup("03   백업 이름");
            _nameGroup.Margin = new Padding(0, 3, 0, 7);
            TableLayoutPanel namePanel = NewTable(1, 4);
            namePanel.Padding = new Padding(12, 4, 12, 4);
            namePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            namePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
            namePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
            namePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _date = MakeLabel("", 9, Muted);
            namePanel.Controls.Add(_date, 0, 0);
            _name = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), MaxLength = 32767 };
            _name.TextChanged += delegate { if (!_loading) UpdateNameAndSelection(); };
            _tips.SetToolTip(_name, "비워 두면 과검제어전으로 백업합니다. 원하는 이름을 직접 입력해도 됩니다.");
            namePanel.Controls.Add(_name, 0, 1);
            _folderName = MakeLabel("예: YYMMDD_과검제어전", 10, Accent);
            _folderName.AutoEllipsis = true;
            namePanel.Controls.Add(_folderName, 0, 2);
            _nameError = MakeLabel("빈 이름은 과검제어전 · 중복 시 _01, _02…", 8.5F, Muted);
            _nameError.AutoEllipsis = true;
            namePanel.Controls.Add(_nameError, 0, 3);
            _nameGroup.Controls.Add(namePanel);
            configure.Controls.Add(_nameGroup, 1, 0);
            root.Controls.Add(configure, 0, 2);

            _dropGroup = MakeGroup("04   적용할 파일·폴더 · 여기에 끌어다 놓으세요");
            TableLayoutPanel dropPanel = NewTable(2, 2);
            dropPanel.Padding = new Padding(12, 3, 12, 6);
            dropPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dropPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            dropPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            dropPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            _dropList = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false, AccessibleName = "적용할 파일과 폴더 목록" };
            FlowLayoutPanel dropButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            Button addFiles = MakeButton("파일 추가", 114);
            addFiles.Height = 26;
            addFiles.Click += delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog { Multiselect = true, CheckFileExists = true, Title = "적용할 파일 선택" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) AddDropInputs(dialog.FileNames);
            };
            Button addFolder = MakeButton("폴더 추가", 114);
            addFolder.Height = 26;
            addFolder.Click += delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "이 폴더 안의 내용물을 선택 항목에 적용합니다.", ShowNewFolderButton = false })
                    if (dialog.ShowDialog(this) == DialogResult.OK) AddDropInputs(new[] { dialog.SelectedPath });
            };
            Button clearInputs = MakeButton("입력 비우기", 114);
            clearInputs.Height = 26;
            clearInputs.Click += delegate { if (!_busy) { _dropList.Items.Clear(); UpdateNameAndSelection(); } };
            dropButtons.Controls.Add(addFiles);
            dropButtons.Controls.Add(addFolder);
            dropButtons.Controls.Add(clearInputs);
            _dropHint = MakeLabel("폴더는 내용물만, 파일은 그대로 적용 대상 바로 아래에 넣습니다.", 8.5F, Muted);
            _dropHint.AutoEllipsis = true;
            dropPanel.Controls.Add(_dropList, 0, 0);
            dropPanel.Controls.Add(dropButtons, 1, 0);
            dropPanel.Controls.Add(_dropHint, 0, 1);
            dropPanel.SetColumnSpan(_dropHint, 2);
            _dropGroup.Controls.Add(dropPanel);
            foreach (Control dropTarget in new Control[] { _dropGroup, dropPanel, _dropList, _dropHint })
            {
                dropTarget.AllowDrop = true;
                dropTarget.DragEnter += DropInputsEnter;
                dropTarget.DragDrop += DropInputs;
            }
            root.Controls.Add(_dropGroup, 0, 3);

            GroupBox previewGroup = MakeGroup("생성 예정 경로 · 중복 번호는 항목별로 적용되며 실행 시 최종 확정됩니다");
            _preview = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Muted, Margin = Padding.Empty };
            Panel previewPadding = new Panel { Dock = DockStyle.Fill, Padding = new Padding(13, 5, 9, 6) };
            previewPadding.Controls.Add(_preview);
            previewGroup.Controls.Add(previewPadding);
            root.Controls.Add(previewGroup, 0, 4);

            TableLayoutPanel output = NewTable(1, 2);
            output.Margin = new Padding(0, 2, 0, 0);
            output.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
            output.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            TableLayoutPanel progressPanel = NewTable(2, 3);
            progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 19));
            progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            _state = MakeLabel("대기 중", 11, Ink);
            _state.Font = new Font(Font.FontFamily, 11, FontStyle.Bold);
            _counts = MakeLabel("백업할 항목을 선택한 뒤 시작하세요. 빈 이름은 과검제어전을 사용합니다.", 9, Muted);
            progressPanel.Controls.Add(_state, 0, 0);
            progressPanel.Controls.Add(_counts, 1, 0);
            _progress = new ProgressBar { Dock = DockStyle.Fill, Height = 13, Margin = new Padding(0, 2, 0, 3), Maximum = 1000 };
            progressPanel.Controls.Add(_progress, 0, 1);
            progressPanel.SetColumnSpan(_progress, 2);
            _currentFile = MakeLabel("실행 결과와 상세 기록이 여기에 표시됩니다.", 8.5F, Muted);
            _currentFile.AutoEllipsis = true;
            progressPanel.Controls.Add(_currentFile, 0, 2);
            progressPanel.SetColumnSpan(_currentFile, 2);
            output.Controls.Add(progressPanel, 0, 0);

            _tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
            TabPage resultsTab = new TabPage("항목별 결과");
            resultsTab.Padding = new Padding(2);
            _results = new DataGridView
            {
                Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
                ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false, AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, EnableHeadersVisualStyles = false,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = Color.FromArgb(231, 235, 241)
            };
            _results.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(239, 243, 248);
            _results.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
            _results.ColumnHeadersDefaultCellStyle.SelectionBackColor = _results.ColumnHeadersDefaultCellStyle.BackColor;
            _results.ColumnHeadersHeight = 30;
            _results.RowTemplate.Height = 29;
            _results.DefaultCellStyle.SelectionBackColor = Color.FromArgb(226, 237, 253);
            _results.DefaultCellStyle.SelectionForeColor = Ink;
            AddResultColumn("항목", 105, false);
            AddResultColumn("결과", 89, false);
            AddResultColumn("성공", 57, false);
            AddResultColumn("실패", 57, false);
            AddResultColumn("제외", 57, false);
            AddResultColumn("경고", 57, false);
            AddResultColumn("실제 저장 위치", 200, true);
            _results.SelectionChanged += delegate { UpdateOpenButtons(); };
            _results.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) OpenSelectedFolder(); };
            resultsTab.Controls.Add(_results);
            TabPage detailTab = new TabPage("상세 기록");
            TableLayoutPanel detailPanel = NewTable(1, 2);
            detailPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            detailPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            _details = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true, BorderStyle = BorderStyle.None };
            _details.Columns.Add("구분", 65);
            _details.Columns.Add("항목", 95);
            _details.Columns.Add("경로", 340);
            _details.Columns.Add("원인 / 내용", 480);
            _detailText = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, Text = "실패·제외·경고를 선택하면 전체 경로와 원인을 볼 수 있습니다. 전체 기록은 로그 열기를 이용하세요." };
            _details.SelectedIndexChanged += delegate
            {
                if (_details.SelectedItems.Count == 0) return;
                DetailEntry d = (DetailEntry)_details.SelectedItems[0].Tag;
                _detailText.Text = (d.Path ?? "") + Environment.NewLine + d.Message;
            };
            detailPanel.Controls.Add(_details, 0, 0);
            detailPanel.Controls.Add(_detailText, 0, 1);
            detailTab.Controls.Add(detailPanel);
            _tabs.TabPages.Add(resultsTab);
            _tabs.TabPages.Add(detailTab);
            output.Controls.Add(_tabs, 0, 1);
            root.Controls.Add(output, 0, 5);

            TableLayoutPanel footer = NewTable(2, 2);
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            FlowLayoutPanel resultButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
            _openFolder = MakeButton("선택 결과 폴더 열기", 157);
            _openFolder.Enabled = false;
            _openFolder.Click += delegate { OpenSelectedFolder(); };
            _openLog = MakeButton("로그 열기", 95);
            _openLog.Enabled = false;
            _openLog.Click += delegate { OpenLog(); };
            resultButtons.Controls.Add(_openFolder);
            resultButtons.Controls.Add(_openLog);
            footer.Controls.Add(resultButtons, 0, 0);
            FlowLayoutPanel actionButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 9, 0, 0) };
            _start = MakeButton("선택 항목 백업", 190);
            _start.Height = 39;
            _start.BackColor = Accent;
            _start.ForeColor = Color.White;
            _start.FlatAppearance.BorderColor = Accent;
            _start.Font = new Font(Font.FontFamily, 10, FontStyle.Bold);
            _start.Click += StartBackup;
            _cancel = MakeButton("취소", 100);
            _cancel.Height = 39;
            _cancel.Enabled = false;
            _cancel.Click += delegate { RequestCancel(); };
            actionButtons.Controls.Add(_start);
            actionButtons.Controls.Add(_cancel);
            footer.Controls.Add(actionButtons, 1, 0);
            _notice = MakeLabel("실행마다 새 폴더에 전체 복사합니다. 같은 시점의 파일 세트가 필요하면 원본 기록을 멈춘 뒤 실행하세요.", 8.5F, Muted);
            _notice.AutoEllipsis = true;
            footer.Controls.Add(_notice, 0, 1);
            footer.SetColumnSpan(_notice, 2);
            Panel footerHost = new Panel { Dock = DockStyle.Bottom, Height = 105, Padding = new Padding(22, 0, 22, 14) };
            footerHost.Controls.Add(footer);
            Controls.Add(footerHost);
        }

        private static TableLayoutPanel NewTable(int columns, int rows)
        {
            return new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = Padding.Empty };
        }
        private GroupBox MakeGroup(string text)
        {
            return new GroupBox { Text = text, Dock = DockStyle.Fill, BackColor = Color.White, ForeColor = Ink, Margin = new Padding(0, 3, 0, 7), Padding = new Padding(3) };
        }
        private Label MakeLabel(string text, float size, Color color)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, size), ForeColor = color, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
        }
        private Button MakeButton(string text, int width)
        {
            Button button = new Button { Text = text, Width = width, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Ink, Margin = new Padding(0, 0, 6, 0), Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = Color.FromArgb(203, 212, 224);
            return button;
        }
        private TextBox ReadOnlyBox()
        {
            return new TextBox { ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(248, 250, 253), Margin = new Padding(0, 2, 0, 2) };
        }
        private void AddResultColumn(string title, int width, bool fill)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn { HeaderText = title, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (fill) { c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; c.MinimumWidth = 180; }
            _results.Columns.Add(c);
        }

        private void ResizeContent()
        {
            if (_scrollHost == null || _rootLayout == null) return;
            int minimum = ApplyMode ? 866 : 710;
            _rootLayout.Size = new Size(Math.Max(980, _scrollHost.ClientSize.Width -
                (_scrollHost.ClientSize.Height < minimum ? SystemInformation.VerticalScrollBarWidth : 0)),
                Math.Max(minimum, _scrollHost.ClientSize.Height));
        }

        private void UpdateModeLayout()
        {
            if (_dropGroup == null) return;
            _modeLabel.Text = ApplyMode ? "작업 방식 · 백업 후 적용" : "작업 방식 · 백업";
            _dropGroup.Visible = ApplyMode;
            _rootLayout.RowStyles[3].Height = ApplyMode ? 156 : 0;
            _itemGroup.Text = ApplyMode ? "02   적용할 항목 · 1개만 선택" : "02   백업할 항목";
            _nameGroup.Text = ApplyMode ? "03   적용 전 백업 이름" : "03   백업 이름";
            _selectAll.Visible = !ApplyMode;
            _start.Text = ApplyMode ? "선택 항목 적용" : "선택 항목 백업";
            _notice.Text = ApplyMode
                ? "선택한 항목을 먼저 완전히 백업한 뒤 적용합니다. 같은 이름의 파일만 덮어쓰며 다른 기존 파일은 유지합니다."
                : "실행마다 새 폴더에 전체 복사합니다. 같은 시점의 파일 세트가 필요하면 원본 기록을 멈춘 뒤 실행하세요.";
            _tips.SetToolTip(_notice, _notice.Text);
            ResizeContent();
            _scrollHost.AutoScrollPosition = Point.Empty;
        }

        private void OnItemSelectionChanged(CheckBox changed)
        {
            if (_loading) return;
            if (ApplyMode && changed.Checked)
            {
                _loading = true;
                try { foreach (CheckBox check in _checks) if (check != changed) check.Checked = false; }
                finally { _loading = false; }
            }
            UpdateNameAndSelection();
        }

        private void DropInputsEnter(object sender, DragEventArgs e)
        {
            e.Effect = !_busy && ApplyMode && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop) &&
                (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void DropInputs(object sender, DragEventArgs e)
        {
            if (_busy || !ApplyMode || e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null) AddDropInputs(paths);
        }

        private void AddDropInputs(string[] paths)
        {
            if (_busy || !ApplyMode) return;
            try
            {
                List<string> normalized = new List<string>();
                foreach (string path in paths)
                {
                    if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                        throw new ArgumentException("파일·폴더의 전체 경로가 필요합니다.");
                    normalized.Add(Path.GetFullPath(path));
                }
                foreach (string path in normalized)
                    if (!_dropList.Items.Cast<string>().Contains(path, StringComparer.OrdinalIgnoreCase)) _dropList.Items.Add(path);
                UpdateNameAndSelection();
            }
            catch (Exception error)
            {
                _dropHint.Text = "입력할 수 없는 경로: " + error.Message;
                _tips.SetToolTip(_dropHint, _dropHint.Text);
            }
        }

        private bool CanStart()
        {
            int selected = SelectedItems().Length;
            return !_busy && _validName != null && _profile.SelectedIndex >= 0 &&
                (ApplyMode ? selected == 1 && _dropList.Items.Count > 0 : selected > 0);
        }

        private void ShowProfile()
        {
            MachineProfile profile = _profile.SelectedItem as MachineProfile;
            _pathGroup.Text = "01   장비와 기본 경로" + (profile == null ? "" : " · " + profile.Name);
            _source.Text = profile == null ? "" : profile.SourceRoot;
            _backup.Text = profile == null ? "" : profile.BackupRoot;
            _activeItems = PathSafety.ValidateItemNames(profile == null ? null : profile.ItemNames);
            _sourceGeneration++;
            _previewGeneration++;
            for (int i = 0; i < _checks.Length; i++)
            {
                _available[i] = false;
                _checks[i].Text = _activeItems[i];
                _tips.SetToolTip(_checks[i], _activeItems[i]);
                _checks[i].Checked = false;
                _checks[i].Enabled = false;
                _sourceStates[i].Text = "확인 대기";
                _sourceStates[i].ForeColor = Muted;
            }
            if (!_loading) UpdateNameAndSelection();
        }

        private async Task RefreshSourceStatesAsync()
        {
            if (_busy || IsDisposed) return;
            int generation = ++_sourceGeneration;
            string source = _source.Text;
            string[] names = (string[])_activeItems.Clone();
            for (int i = 0; i < _checks.Length; i++)
            {
                _available[i] = false;
                _checks[i].Enabled = false;
                _sourceStates[i].Text = "확인 중…";
                _sourceStates[i].ForeColor = Muted;
            }
            UpdateNameAndSelection();
            Task[] tasks = new Task[names.Length];
            for (int index = 0; index < names.Length; index++)
            {
                int captured = index;
                tasks[index] = CheckOneSourceAsync(source, names[captured], captured, generation);
            }
            await Task.WhenAll(tasks);
        }

        private async Task CheckOneSourceAsync(string source, string itemName, int index, int generation)
        {
            SourceStatus status;
            try
            {
                status = await Task.Run(delegate { return PathSafety.CheckSource(source, itemName); });
            }
            catch (Exception error)
            {
                status = new SourceStatus { Available = false, Message = error.Message };
            }
            if (IsDisposed || Disposing || generation != _sourceGeneration) return;
            _available[index] = status.Available;
            _checks[index].Enabled = !_busy && status.Available;
            if (!status.Available) _checks[index].Checked = false;
            _sourceStates[index].Text = status.Available ? "사용 가능" : "접근 불가";
            _sourceStates[index].ForeColor = status.Available ? Good : Bad;
            string detail = itemName + Environment.NewLine + status.Message;
            _tips.SetToolTip(_sourceStates[index], detail);
            _tips.SetToolTip(_checks[index], detail);
            UpdateNameAndSelection();
        }

        private void ChangeAllChecks(bool value)
        {
            if (ApplyMode && value) return;
            _loading = true;
            for (int i = 0; i < _checks.Length; i++) _checks[i].Checked = value && _available[i];
            _loading = false;
            UpdateNameAndSelection();
        }

        private string[] SelectedItems()
        {
            List<string> result = new List<string>();
            for (int i = 0; i < _checks.Length; i++) if (_checks[i].Checked && _available[i]) result.Add(_activeItems[i]);
            return result.ToArray();
        }

        private void UpdateNameAndSelection()
        {
            if (_busy) return;
            _displayDay = DateTime.Now.Date;
            _date.Text = "오늘 날짜  " + _displayDay.ToString("yyyy-MM-dd") + "  ·  PC 현지 시간";
            _validName = null;
            try
            {
                _validName = PathSafety.ValidateName(_name.Text);
                _folderName.Text = PathSafety.FolderName(_validName, _displayDay);
                _nameError.Text = String.IsNullOrWhiteSpace(_name.Text)
                    ? "빈 이름은 과검제어전 · 중복 시 _01, _02…"
                    : "같은 이름이 있으면 _01, _02… 번호가 붙습니다.";
                _nameError.ForeColor = Muted;
            }
            catch (ArgumentException error)
            {
                _folderName.Text = "예: " + _displayDay.ToString("yyMMdd") + "_과검제어전";
                _nameError.Text = error.Message;
                _nameError.ForeColor = String.IsNullOrWhiteSpace(_name.Text) ? Muted : Bad;
            }
            _tips.SetToolTip(_nameError, _nameError.Text);
            _tips.SetToolTip(_folderName, _folderName.Text);
            int selected = SelectedItems().Length;
            _selectionCount.Text = selected + (ApplyMode ? " / 1개 선택" : "개 선택");
            _start.Enabled = CanStart();
            _dropHint.Text = "입력 " + _dropList.Items.Count + "개 · 폴더는 내용물만, 파일은 그대로 대상 바로 아래에 넣습니다.";
            _tips.SetToolTip(_dropHint, _dropHint.Text);
            _previewGeneration++;
            _previewTimer.Stop();
            if (selected == 0) _preview.Text = ApplyMode ? "적용할 항목을 1개 선택하세요. 선택한 항목을 먼저 백업합니다." : "백업할 항목을 선택하면 항목별 저장 경로가 표시됩니다.";
            else if (_validName == null) _preview.Text = "유효한 백업 이름을 입력하면 저장 경로가 표시됩니다.";
            else
            {
                _preview.Text = "항목별 경로와 중복 번호를 확인하는 중…";
                _previewTimer.Start();
            }
        }

        private async Task RefreshPreviewAsync()
        {
            if (_busy || _validName == null || IsDisposed) return;
            int generation = _previewGeneration;
            string[] selected = SelectedItems();
            string backupRoot = _backup.Text;
            string sourceRoot = _source.Text;
            bool apply = ApplyMode;
            string[] inputs = _dropList.Items.Cast<string>().ToArray();
            string folderName = PathSafety.FolderName(_validName, DateTime.Now);
            string text;
            try
            {
                text = await Task.Run(delegate
                {
                    List<string> paths = new List<string>();
                    foreach (string item in selected)
                    {
                        string backupPath = PathSafety.PreviewPath(backupRoot, item, folderName);
                        if (!apply) paths.Add(backupPath);
                        else
                        {
                            paths.Add("① 사전 백업: " + backupPath);
                            paths.Add("② 적용 대상: " + Path.Combine(sourceRoot, item));
                            paths.Add(inputs.Length == 0 ? "적용할 파일·폴더를 끌어다 놓으세요." : "입력 " + inputs.Length + "개 · 폴더 내용물은 대상 바로 아래로, 같은 파일명은 덮어쓰기");
                        }
                    }
                    return String.Join(Environment.NewLine, paths);
                });
            }
            catch (Exception error) { text = "저장 경로를 미리 확인할 수 없습니다: " + error.Message + Environment.NewLine + "경로 설정과 연결 상태를 확인하세요. 실행 전 경로를 다시 검사합니다."; }
            if (IsDisposed || Disposing || _busy || generation != _previewGeneration) return;
            _preview.Text = text;
        }

        private async void EditPaths(object sender, EventArgs e)
        {
            if (_busy) return;
            using (ProfileSettingsForm dialog = new ProfileSettingsForm(_settings.Profiles, _profile.SelectedIndex))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                ApplyProfiles(dialog.Profiles, _profile.SelectedIndex);
                SaveSettings(true);
                UpdateNameAndSelection();
                await RefreshSourceStatesAsync();
            }
        }

        private void ApplyProfiles(List<MachineProfile> profiles, int selected)
        {
            _settings.Profiles = profiles;
            _loading = true;
            _profile.Items.Clear();
            foreach (MachineProfile profile in _settings.Profiles) _profile.Items.Add(profile);
            _profile.SelectedIndex = Math.Max(0, Math.Min(selected, _profile.Items.Count - 1));
            ShowProfile();
            _loading = false;
            UpdateNameAndSelection();
        }

        private void SaveSettings(bool showError)
        {
            if (!_persistSettings) return;
            _settings.SelectedProfile = Math.Max(0, _profile.SelectedIndex);
            _settings.LastName = _name.Text;
            try { SettingsStore.Save(_settings); }
            catch (Exception error)
            {
                string message = "설정을 저장하지 못했습니다. 현재 입력은 이번 실행에서만 유지됩니다.\r\n" + error.Message;
                _notice.Text = message.Replace("\r\n", " ");
                _notice.ForeColor = Caution;
                _tips.SetToolTip(_notice, message);
                if (showError) MessageBox.Show(this, message, "설정 저장 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async void StartBackup(object sender, EventArgs e)
        {
            if (ApplyMode) { await StartApplyAsync(); return; }
            DateTime startedAt = DateTime.Now;
            if (_busy) return;
            string[] selected = SelectedItems();
            if (selected.Length == 0) return;
            string name;
            try { name = PathSafety.ValidateName(_name.Text); }
            catch (ArgumentException error)
            {
                _nameError.Text = error.Message;
                _nameError.ForeColor = Bad;
                _name.Focus();
                return;
            }
            BackupRequest request = new BackupRequest
            {
                SourceRoot = _source.Text, BackupRoot = _backup.Text, Name = name,
                Items = selected, ConfiguredItems = (string[])_activeItems.Clone(), StartedAt = startedAt
            };
            SaveSettings(false);
            _cancellation = new CancellationTokenSource();
            _lastResult = null;
            _lastApplyResult = null;
            _runningApply = false;
            _results.Rows.Clear();
            foreach (string item in selected) _results.Rows.Add(item, "미실행", "0", "0", "0", "0", "—");
            _details.Items.Clear();
            _detailText.Text = "실패·제외·경고의 전체 경로와 원인이 표시됩니다. 전체 파일 기록은 실행 로그에 저장됩니다.";
            _tabs.TabPages[1].Text = "상세 기록";
            _tabs.SelectedIndex = 0;
            _state.Text = "준비 중";
            _state.ForeColor = Accent;
            _counts.Text = "선택한 원본과 저장 위치를 검사하고 대상 목록을 확인합니다.";
            _currentFile.Text = "시작 시각  " + startedAt.ToString("yyyy-MM-dd HH:mm:ss");
            _preview.Text = "확정 기준: " + PathSafety.FolderName(name, startedAt) + Environment.NewLine + "생성된 폴더의 실제 위치는 아래 항목별 결과에 표시됩니다.";
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 28;
            SetBusy(true);
            _progressTimer.Start();
            BackupResult result;
            try
            {
                result = await Task.Run(delegate
                {
                    BackupEngine engine = new BackupEngine();
                    return engine.Run(request, CaptureProgress, _cancellation.Token);
                });
            }
            catch (Exception error)
            {
                result = new BackupResult
                {
                    State = BackupState.Failed, StartedAt = startedAt, FinishedAt = DateTime.Now,
                    Message = "예기치 않은 오류로 백업을 중단했습니다. 완성 여부는 실제 폴더와 로그를 확인하세요. " + error.Message
                };
                result.Details.Add(new DetailEntry { Time = DateTime.Now, Kind = "failure", Message = error.ToString() });
                foreach (string item in selected) result.Items.Add(new ItemResult { Item = item, State = BackupState.Pending });
            }
            _progressTimer.Stop();
            DrainProgress();
            _lastResult = result;
            ShowResult(result);
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(false);
            if (_closeRequested) { Close(); return; }
            // Preserve the actual destinations until the user changes the next request.
            _displayDay = DateTime.Now.Date;
            _date.Text = "오늘 날짜  " + _displayDay.ToString("yyyy-MM-dd") + "  ·  PC 현지 시간";
            _folderName.Text = PathSafety.FolderName(name, _displayDay);
            _tips.SetToolTip(_folderName, _folderName.Text);
        }

        private async Task StartApplyAsync()
        {
            if (!CanStart() || !ApplyMode) return;
            string[] selected = SelectedItems();
            if (selected.Length != 1) return;
            ApplyRequest request;
            try
            {
                request = new ApplyRequest
                {
                    SourceRoot = _source.Text, BackupRoot = _backup.Text,
                    Name = PathSafety.ValidateName(_name.Text), Item = selected[0],
                    ConfiguredItems = (string[])_activeItems.Clone(),
                    InputPaths = _dropList.Items.Cast<string>().ToArray(), StartedAt = DateTime.Now
                };
                string target = Path.Combine(PathSafety.Normalize(request.SourceRoot), request.Item);
                string message = "적용 대상: " + target + "\r\n\r\n" +
                    "입력 파일·폴더: " + request.InputPaths.Length + "개\r\n" +
                    "선택한 항목 전체를 먼저 백업합니다. 백업이 완전히 성공해야 적용을 시작합니다.\r\n" +
                    "드래그한 폴더는 내용물만 대상 바로 아래에 넣고, 파일은 그대로 넣습니다.\r\n" +
                    "같은 이름의 파일을 덮어쓰며 나머지 기존 파일은 유지합니다.\r\n" +
                    "적용 중 실패·취소되면 이미 적용된 파일은 남으며 사전 백업은 보존됩니다.\r\n\r\n" +
                    "대상 프로그램의 파일 기록을 멈춘 상태에서 진행하세요. 적용하시겠습니까?";
                if (MessageBox.Show(this, message, "사전 백업 후 적용", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            }
            catch (Exception error)
            {
                _nameError.Text = error.Message;
                _nameError.ForeColor = Bad;
                return;
            }
            SaveSettings(false);
            await ExecuteApplyAsync(request);
        }

        // Shared execution path. Interactive use always enters through StartApplyAsync's confirmation;
        // the local fixture smoke route calls this only with its own sandbox paths and logs.
        private async Task ExecuteApplyAsync(ApplyRequest request)
        {
            if (_busy) return;
            _runningApply = true;
            _lastResult = null;
            _lastApplyResult = null;
            _cancellation = new CancellationTokenSource();
            lock (_progressLock) { _latestProgress = null; _latestApplyPhase = null; }
            _results.Rows.Clear();
            _results.Rows.Add(request.Item + " · 사전 백업", "미실행", "0", "0", "0", "0", "—");
            _results.Rows.Add(request.Item + " · 적용", "미실행", "0", "0", "0", "0", "—");
            _details.Items.Clear();
            _detailText.Text = "사전 백업과 적용의 상태·경로를 각각 확인할 수 있습니다.";
            _tabs.TabPages[1].Text = "상세 기록";
            _tabs.SelectedIndex = 0;
            _state.Text = "적용 준비 중";
            _state.ForeColor = Accent;
            _counts.Text = "입력 파일·폴더를 확인합니다. 사전 백업 완료 후에만 적용합니다.";
            _currentFile.Text = "적용 대상  " + Path.Combine(request.SourceRoot, request.Item);
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 28;
            SetBusy(true);
            _progressTimer.Start();
            ApplyResult result;
            try
            {
                CancellationToken token = _cancellation.Token;
                result = await Task.Run(delegate { return new ApplyEngine().Run(request, CaptureApplyProgress, token); });
            }
            catch (Exception error)
            {
                result = new ApplyResult
                {
                    State = BackupState.Failed, StartedAt = request.StartedAt, FinishedAt = DateTime.Now,
                    TargetPath = Path.Combine(request.SourceRoot, request.Item),
                    Message = "예기치 않은 오류로 적용 작업이 중단되었습니다. 실제 대상과 백업·로그를 확인하세요. " + error.Message,
                    Application = new ItemResult { Item = request.Item, State = BackupState.Failed }
                };
                result.Application.Details.Add(new DetailEntry { Time = DateTime.Now, Item = request.Item, Kind = "failure", Message = error.ToString() });
            }
            finally
            {
                _progressTimer.Stop();
                DrainProgress();
                _cancellation.Dispose();
                _cancellation = null;
            }
            _lastApplyResult = result;
            ShowApplyResult(result, request.Item);
            _runningApply = false;
            SetBusy(false);
            _scrollHost.ScrollControlIntoView(_results);
            if (_closeRequested) { Close(); return; }
            _displayDay = DateTime.Now.Date;
            _date.Text = "오늘 날짜  " + _displayDay.ToString("yyyy-MM-dd") + "  ·  PC 현지 시간";
            _folderName.Text = PathSafety.FolderName(request.Name, _displayDay);
        }

        private void CaptureApplyProgress(ApplyProgress value)
        {
            if (value == null || value.Progress == null) return;
            lock (_progressLock)
            {
                _latestProgress = CopyProgress(value.Progress);
                _latestApplyPhase = value.Phase;
            }
        }

        private static BackupProgress CopyProgress(BackupProgress progress)
        {
            return new BackupProgress
            {
                State = progress.State, Item = progress.Item, CurrentFile = progress.CurrentFile,
                Message = progress.Message, TotalFiles = progress.TotalFiles, ProcessedFiles = progress.ProcessedFiles,
                SucceededFiles = progress.SucceededFiles, FailedFiles = progress.FailedFiles
            };
        }

        private static string ApplyPhaseText(ApplyPhase phase)
        {
            if (phase == ApplyPhase.BackingUp) return "사전 백업 중";
            if (phase == ApplyPhase.Applying) return "적용 중";
            return "적용 준비 중";
        }

        private void CaptureProgress(BackupProgress progress)
        {
            if (progress == null) return;
            lock (_progressLock) { _latestProgress = CopyProgress(progress); _latestApplyPhase = null; }
        }
        private void DrainProgress()
        {
            BackupProgress p;
            ApplyPhase? phase;
            lock (_progressLock) { p = _latestProgress; phase = _latestApplyPhase; _latestProgress = null; _latestApplyPhase = null; }
            if (p == null) return;
            _state.Text = _cancellation != null && _cancellation.IsCancellationRequested ? "취소 중" :
                (phase.HasValue ? ApplyPhaseText(phase.Value) : StateText(p.State));
            _counts.Text = String.Format("전체 {0:N0}개   ·   처리 {1:N0}개   ·   성공 {2:N0}개   ·   실패 {3:N0}개", p.TotalFiles, p.ProcessedFiles, p.SucceededFiles, p.FailedFiles);
            _currentFile.Text = String.IsNullOrEmpty(p.CurrentFile) ? (p.Message ?? "") : (p.Item ?? "") + "  ·  " + p.CurrentFile;
            _tips.SetToolTip(_currentFile, _currentFile.Text + Environment.NewLine + (p.Message ?? ""));
            if (p.State == BackupState.Preparing)
            {
                _progress.Style = ProgressBarStyle.Marquee;
                _progress.MarqueeAnimationSpeed = 28;
            }
            else
            {
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Value = p.TotalFiles == 0 ? 0 : Math.Max(0, Math.Min(1000, (int)(1000L * p.ProcessedFiles / p.TotalFiles)));
            }
            foreach (DataGridViewRow row in _results.Rows)
            {
                if ((!_runningApply && (string)row.Cells[0].Value == p.Item) ||
                    (_runningApply && phase.HasValue &&
                    ((phase.Value == ApplyPhase.BackingUp && row.Index == 0) || (phase.Value == ApplyPhase.Applying && row.Index == 1))))
                {
                    row.Cells[1].Value = phase.HasValue ? ApplyPhaseText(phase.Value) : StateText(p.State);
                    row.Cells[1].Style.ForeColor = Accent;
                }
            }
        }

        private void ShowResult(BackupResult result)
        {
            _openFolder.Text = "선택 결과 폴더 열기";
            _results.Columns[0].Width = 105;
            _results.Columns[6].HeaderText = "실제 저장 경로";
            _progress.Style = ProgressBarStyle.Continuous;
            int total = result.Items.Sum(item => item.TotalFiles);
            int processed = result.Items.Sum(item => item.ProcessedFiles);
            int success = result.Items.Sum(item => item.SucceededFiles);
            int failure = result.Items.Sum(item => item.FailedFiles);
            int excluded = result.Items.Sum(item => item.ExcludedEntries);
            int warnings = result.Items.Sum(item => item.Warnings);
            _progress.Value = total == 0 ? (result.State == BackupState.Complete ? 1000 : 0) : Math.Max(0, Math.Min(1000, (int)(1000L * processed / total)));
            _state.Text = StateText(result.State);
            _state.ForeColor = StateColor(result.State);
            _counts.Text = String.Format("처리 {0:N0} / {1:N0}개   ·   성공 {2:N0}   실패 {3:N0}   제외 {4:N0}   경고 {5:N0}", processed, total, success, failure, excluded, warnings);
            _currentFile.Text = result.Message ?? ("종료 시각  " + result.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            _tips.SetToolTip(_currentFile, _currentFile.Text);
            _results.Rows.Clear();
            foreach (ItemResult item in result.Items)
            {
                int index = _results.Rows.Add(item.Item, StateText(item.State), item.SucceededFiles.ToString("N0"), item.FailedFiles.ToString("N0"), item.ExcludedEntries.ToString("N0"), item.Warnings.ToString("N0"), String.IsNullOrEmpty(item.Destination) ? "—" : item.Destination);
                _results.Rows[index].Tag = item;
                _results.Rows[index].Cells[1].Style.ForeColor = StateColor(item.State);
                _results.Rows[index].Cells[6].ToolTipText = item.Destination;
            }
            List<DetailEntry> details = new List<DetailEntry>();
            if (result.Details != null) details.AddRange(result.Details);
            foreach (ItemResult item in result.Items) if (item.Details != null) details.AddRange(item.Details);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            _details.BeginUpdate();
            foreach (DetailEntry detail in details)
            {
                if (detail.Kind == "success") continue;
                string key = detail.Time.Ticks + "\0" + detail.Item + "\0" + detail.Kind + "\0" + detail.Path + "\0" + detail.Message;
                if (!seen.Add(key)) continue;
                ListViewItem row = new ListViewItem(new string[] { KindText(detail.Kind), detail.Item ?? "—", detail.Path ?? "", detail.Message ?? "" });
                row.Tag = detail;
                row.ToolTipText = (detail.Path ?? "") + Environment.NewLine + detail.Message;
                if (detail.Kind == "failure") row.ForeColor = Bad;
                else if (detail.Kind == "warning" || detail.Kind == "excluded") row.ForeColor = Caution;
                _details.Items.Add(row);
            }
            _details.EndUpdate();
            _tabs.TabPages[1].Text = "상세 기록 (" + _details.Items.Count.ToString("N0") + ")";
            if (_details.Items.Count == 0) _detailText.Text = "실패·제외·경고가 없습니다. 전체 파일 기록은 로그 열기에서 확인할 수 있습니다.";
            _preview.Text = String.Join(Environment.NewLine, result.Items.Select(item => item.Item + "  [" + StateText(item.State) + "]  " + (String.IsNullOrEmpty(item.Destination) ? "생성된 폴더 없음" : item.Destination)));
            UpdateOpenButtons();
        }

        private void ShowApplyResult(ApplyResult result, string itemName)
        {
            ItemResult backup = result.Backup == null ? null : result.Backup.Items.FirstOrDefault();
            backup = CopyResultItem(backup, itemName);
            if (backup.State == BackupState.Pending && result.Backup != null &&
                (result.Backup.State == BackupState.Failed || result.Backup.State == BackupState.Cancelled || result.Backup.State == BackupState.Partial))
                backup.State = result.Backup.State;
            ItemResult application = CopyResultItem(result.Application, itemName);
            if (String.IsNullOrEmpty(application.Destination)) application.Destination = result.TargetPath;
            BackupResult display = new BackupResult
            {
                State = result.State, StartedAt = result.StartedAt, FinishedAt = result.FinishedAt,
                Message = result.Message, LogPath = result.LogPath ?? (result.Backup == null ? null : result.Backup.LogPath)
            };
            display.Items.Add(backup);
            display.Items.Add(application);
            if (result.Backup != null && result.Backup.Details != null) display.Details.AddRange(result.Backup.Details);
            _lastResult = display;
            ShowResult(display);
            _results.Rows[0].Cells[0].Value = itemName + " · 사전 백업";
            _results.Rows[1].Cells[0].Value = itemName + " · 적용";
            _results.Columns[0].Width = 205;
            _results.Columns[6].HeaderText = "백업 보관 / 적용 대상 경로";
            _state.Text = "적용 " + StateText(result.State);
            _counts.Text = "사전 백업: " + StateText(backup.State) + "   ·   적용: " + StateText(application.State) +
                "   ·   적용 성공 " + application.SucceededFiles.ToString("N0") + " / 실패 " + application.FailedFiles.ToString("N0");
            _preview.Text = "① 사전 백업 [" + StateText(backup.State) + "]: " +
                (String.IsNullOrEmpty(backup.Destination) ? "생성된 백업 없음" : backup.Destination) + Environment.NewLine +
                "② 적용 대상 [" + StateText(application.State) + "]: " + (result.TargetPath ?? "확정되지 않음");
            _openFolder.Text = "선택 경로 열기";
            UpdateOpenButtons();
        }

        private static ItemResult CopyResultItem(ItemResult item, string name)
        {
            if (item == null) return new ItemResult { Item = name, State = BackupState.Pending };
            return new ItemResult
            {
                Item = item.Item, State = item.State, Destination = item.Destination,
                TotalFiles = item.TotalFiles, ProcessedFiles = item.ProcessedFiles,
                SucceededFiles = item.SucceededFiles, FailedFiles = item.FailedFiles,
                ExcludedEntries = item.ExcludedEntries, Warnings = item.Warnings,
                Details = item.Details == null ? new List<DetailEntry>() : new List<DetailEntry>(item.Details)
            };
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _pathGroup.Enabled = !busy;
            _itemGroup.Enabled = !busy;
            _nameGroup.Enabled = !busy;
            _mode.Enabled = !busy;
            _dropGroup.Enabled = !busy;
            _cancel.Enabled = busy;
            _start.Text = busy ? (_runningApply ? "백업 후 적용 중…" : "백업 진행 중…") :
                (ApplyMode ? "선택 항목 적용" : "선택 항목 백업");
            _start.Enabled = CanStart();
            _cancel.Text = "취소";
            if (busy)
            {
                _previewTimer.Stop();
                _previewGeneration++;
            }
            UpdateOpenButtons();
        }
        private void RequestCancel()
        {
            if (!_busy || _cancellation == null || _cancellation.IsCancellationRequested) return;
            _cancellation.Cancel();
            _cancel.Enabled = false;
            _cancel.Text = "취소 중…";
            _state.Text = "취소 중";
            _state.ForeColor = Caution;
            _currentFile.Text = "현재 파일을 안전하게 중단하고 결과를 정리하는 중입니다.";
        }
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (_busy)
            {
                e.Cancel = true;
                _closeRequested = true;
                RequestCancel();
                _currentFile.Text = "진행 중인 작업을 중단한 뒤 창을 닫습니다. 파일과 로그 정리가 끝날 때까지 기다려 주세요.";
                return;
            }
            SaveSettings(true);
        }

        private ItemResult SelectedResult()
        {
            return _results.SelectedRows.Count == 0 ? null : _results.SelectedRows[0].Tag as ItemResult;
        }
        private void UpdateOpenButtons()
        {
            if (_openFolder == null || _openLog == null) return;
            ItemResult item = SelectedResult();
            _openFolder.Enabled = !_busy && item != null && !String.IsNullOrEmpty(item.Destination);
            _openLog.Enabled = !_busy && _lastResult != null && !String.IsNullOrEmpty(_lastResult.LogPath);
        }
        private void OpenSelectedFolder()
        {
            ItemResult item = SelectedResult();
            if (_busy || item == null || String.IsNullOrEmpty(item.Destination)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", QuoteArgument(item.Destination)) { UseShellExecute = true }); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "결과 폴더 열기 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        private void OpenLog()
        {
            if (_lastResult == null || String.IsNullOrEmpty(_lastResult.LogPath)) return;
            try { Process.Start(new ProcessStartInfo("notepad.exe", QuoteArgument(_lastResult.LogPath)) { UseShellExecute = true }); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "로그 열기 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        private static string QuoteArgument(string path) { return "\"" + path.TrimEnd('\\') + "\""; }
        private static string StateText(BackupState state)
        {
            switch (state)
            {
                case BackupState.Preparing: return "준비 중";
                case BackupState.Copying: return "복사 중";
                case BackupState.Complete: return "완료";
                case BackupState.Partial: return "일부 실패";
                case BackupState.Failed: return "실패";
                case BackupState.Cancelled: return "취소";
                default: return "미실행";
            }
        }
        private static Color StateColor(BackupState state)
        {
            if (state == BackupState.Complete) return Good;
            if (state == BackupState.Failed) return Bad;
            if (state == BackupState.Partial || state == BackupState.Cancelled) return Caution;
            return Muted;
        }
        private static string KindText(string kind)
        {
            if (kind == "failure") return "실패";
            if (kind == "excluded") return "제외";
            if (kind == "warning") return "경고";
            return "안내";
        }

        internal async Task VerifyAndCaptureSmokeAsync(string fixtureRoot)
        {
            await Initialization;
            if (_checks.Length != 6 || _checks.Any(check => check.Checked)) throw new InvalidOperationException("Initial checkbox state is incorrect.");
            for (int i = 0; i < Catalog.Items.Length; i++)
                if (_checks[i].Text != Catalog.Items[i]) throw new InvalidOperationException("Catalog labels differ.");
            if (_profile.Items.Count != 2 || _profile.SelectedIndex != 0 || _profile.SelectedItem == null ||
                ((MachineProfile)_profile.SelectedItem).Name != _settings.Profiles[0].Name ||
                _profile.Text != _settings.Profiles[0].Name || _mode.Text != "백업" ||
                _busy || _lastResult != null || _results.Rows.Count != 0 || _start.Enabled || _cancel.Enabled)
                throw new InvalidOperationException("Unexpected initial state or automatic backup.");
            if (!String.Equals(_source.Text, Path.Combine(fixtureRoot, "source"), StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(_backup.Text, Path.Combine(fixtureRoot, "backup"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture profile paths were not restored.");
            if (_persistSettings) throw new InvalidOperationException("Smoke must not persist production settings.");
            int before = Directory.GetFileSystemEntries(_backup.Text).Length;
            CaptureSmoke(Path.Combine(fixtureRoot, "ui-smoke.png"));

            // Exercise the same editable controls and validation/commit method as Save.
            string[] originalSecond = (string[])_settings.Profiles[1].ItemNames.Clone();
            using (ProfileSettingsForm dialog = new ProfileSettingsForm(_settings.Profiles))
            {
                string validation;
                dialog.SetSmokeItemName(0, 4, "");
                if (dialog.TryCommit(out validation)) throw new InvalidOperationException("Blank item name was accepted.");
                dialog.SetSmokeItemName(0, 4, "5/BAD");
                if (dialog.TryCommit(out validation)) throw new InvalidOperationException("A path separator was accepted in an item name.");
                dialog.SetSmokeItemName(0, 4, _activeItems[0].ToLowerInvariant());
                if (dialog.TryCommit(out validation)) throw new InvalidOperationException("Case-insensitive duplicate item names were accepted.");
                dialog.SetSmokeItemName(0, 4, "5_TERRACE");
                dialog.SetSmokeItemName(0, 5, "6_SSIDE");
                if (!dialog.TryCommit(out validation)) throw new InvalidOperationException("Custom item settings failed validation: " + validation);
                dialog.Show(this);
                await Task.Delay(80);
                using (Bitmap capture = new Bitmap(dialog.Width, dialog.Height))
                {
                    dialog.DrawToBitmap(capture, new Rectangle(Point.Empty, dialog.Size));
                    capture.Save(Path.Combine(fixtureRoot, "ui-settings.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                if (_settings.Profiles[0].ItemNames[4] != Catalog.Items[4]) throw new InvalidOperationException("Dialog mutated active settings before applying them.");
                ApplyProfiles(dialog.Profiles, 0);
                dialog.Close();
            }
            string sandboxSettings = Path.Combine(fixtureRoot, "settings");
            SettingsStore.SaveTo(_settings, sandboxSettings);
            string loadWarning;
            AppSettings reloaded = SettingsStore.LoadFrom(sandboxSettings, out loadWarning);
            if (!String.IsNullOrEmpty(loadWarning) || reloaded.Profiles[0].ItemNames[4] != "5_TERRACE" ||
                reloaded.Profiles[0].ItemNames[5] != "6_SSIDE" || !reloaded.Profiles[1].ItemNames.SequenceEqual(originalSecond))
                throw new InvalidOperationException("Per-equipment names did not persist independently.");
            _settings = reloaded;
            ApplyProfiles(_settings.Profiles, 0);
            await RefreshSourceStatesAsync();
            _checks[4].Checked = true;
            ApplyProfiles(_settings.Profiles, 1);
            if (_checks.Any(check => check.Checked) || !_activeItems.SequenceEqual(originalSecond))
                throw new InvalidOperationException("Profile switching did not restore independent names and reset selection.");
            ApplyProfiles(_settings.Profiles, 0);
            await RefreshSourceStatesAsync();
            if (_checks.Any(check => check.Checked) || _checks[4].Text != "5_TERRACE" || _checks[5].Text != "6_SSIDE" || !_available[4] || !_available[5])
                throw new InvalidOperationException("Custom labels/source status failed; fixture needs source/5_TERRACE and source/6_SSIDE.");

            // Exercise ordinary selection and duplicate preview without executing a backup.
            _checks[4].Checked = true;
            _checks[5].Checked = true;
            _previewTimer.Stop();
            bool dispatched = false;
            BeginInvoke(new Action(delegate { dispatched = true; }));
            await RefreshPreviewAsync();
            await Task.Delay(80);
            if (!dispatched || !_start.Enabled || !_preview.Text.Contains("5_TERRACE") || !_preview.Text.Contains("6_SSIDE"))
                throw new InvalidOperationException("Asynchronous preview or input responsiveness failed.");
            if (_preview.Text.Contains("1_LEAD") || _preview.Text.Contains("6_SHSD")) throw new InvalidOperationException("Unselected or legacy items appeared in preview.");
            if (Directory.GetFileSystemEntries(_backup.Text).Length != before || _lastResult != null) throw new InvalidOperationException("Preview wrote backup content.");
            CaptureSmoke(Path.Combine(fixtureRoot, "ui-preview.png"));
            _name.Text = "bad/name";
            if (_start.Enabled) throw new InvalidOperationException("An invalid name enabled backup.");
            _name.Text = "   ";
            if (!_start.Enabled || _validName != "과검제어전") throw new InvalidOperationException("Whitespace did not enable the default name.");
            _name.Clear();
            _previewTimer.Stop();
            await RefreshPreviewAsync();
            if (!_start.Enabled || _validName != "과검제어전" || !_folderName.Text.EndsWith("_과검제어전", StringComparison.Ordinal) || !_preview.Text.Contains("_과검제어전"))
                throw new InvalidOperationException("Blank input did not show the default name and enable backup.");
            CaptureSmoke(Path.Combine(fixtureRoot, "ui-default.png"));
            BackupRequest request = new BackupRequest
            {
                SourceRoot = _source.Text, BackupRoot = _backup.Text, Name = _name.Text,
                Items = SelectedItems(), ConfiguredItems = (string[])_activeItems.Clone(), StartedAt = DateTime.Now,
                LogDirectory = Path.Combine(fixtureRoot, "logs")
            };
            SetBusy(true);
            if (_pathGroup.Enabled || _itemGroup.Enabled || _nameGroup.Enabled || _start.Enabled || !_cancel.Enabled)
                throw new InvalidOperationException("Busy controls are not locked or cancel is unavailable.");
            _progressTimer.Start();
            BackupResult result;
            try { result = await Task.Run(delegate { return new BackupEngine().Run(request, CaptureProgress, CancellationToken.None); }); }
            finally { _progressTimer.Stop(); SetBusy(false); }
            DrainProgress();
            _lastResult = result;
            ShowResult(result);
            SetBusy(false);
            if (result.State != BackupState.Complete || result.Items.Count != 2 || result.Items.Any(item => String.IsNullOrEmpty(item.Destination)))
                throw new InvalidOperationException("Fixture backup did not complete: " + result.Message);
            if (result.Items.Any(item => !Path.GetFileName(item.Destination).StartsWith(PathSafety.FolderName("", request.StartedAt), StringComparison.Ordinal)))
                throw new InvalidOperationException("Blank-name backup did not create default destinations.");
            if (!result.Items.Select(item => item.Item).SequenceEqual(new[] { "5_TERRACE", "6_SSIDE" }) ||
                result.Items.Any(item => Path.GetFileName(Path.GetDirectoryName(item.Destination)) != item.Item))
                throw new InvalidOperationException("Custom item folders were not used in actual backup results.");
            CaptureSmoke(Path.Combine(fixtureRoot, "ui-result.png"));
            await VerifyApplySmokeAsync(fixtureRoot);
            Size = new Size(1000, 700);
            await Task.Delay(80);
            Rectangle client = RectangleToScreen(ClientRectangle);
            Rectangle startBounds = _start.RectangleToScreen(_start.ClientRectangle);
            Rectangle cancelBounds = _cancel.RectangleToScreen(_cancel.ClientRectangle);
            if (!client.Contains(startBounds) || !client.Contains(cancelBounds))
                throw new InvalidOperationException("Pinned action buttons are clipped in the short window.");
            CaptureSmoke(Path.Combine(fixtureRoot, "ui-short.png"));
            File.WriteAllText(Path.Combine(fixtureRoot, "ui-smoke-report.txt"),
                "PASS: six default labels; unchecked initial state; no automatic backup; fixture-only restored paths; " +
                "dialog validates empty/unsafe/duplicate names; 5_TERRACE and 6_SSIDE edited through shared save path; " +
                "sandbox settings save/reload; independent per-equipment names and reset selection; dynamic source statuses; " +
                "responsive asynchronous preview for 5_TERRACE and 6_SSIDE; no preview writes; isolated explicit custom-item backup; " +
                "blank/whitespace uses 과검제어전 in preview and backup destinations; invalid name stays blocked; " +
                "actual item result paths; fixture-only logs; frozen busy inputs; cancel enabled while busy; " +
                "apply-mode single selection and backup-mode multi-selection; FileDrop input and rejected text drops; " +
                "apply readiness and frozen input controls; original contents backed up before folder-contents and file application; " +
                "unrelated files and unselected target unchanged; separate backup/application result rows; " +
                "pinned action buttons at 1000x700; no production settings writes." + Environment.NewLine,
                new System.Text.UTF8Encoding(false));
        }

        private async Task VerifyApplySmokeAsync(string fixtureRoot)
        {
            if (_persistSettings) throw new InvalidOperationException("Apply smoke cannot use production settings.");
            string root = PathSafety.Canonicalize(Path.GetFullPath(fixtureRoot));
            if (root.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(root))
                throw new InvalidOperationException("Apply smoke requires a local fixture.");
            // Create a fresh subtree: the overwrite smoke never changes supplied fixture files.
            string sandbox = Path.Combine(root, "apply-smoke-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string source = Path.Combine(sandbox, "source");
            string backup = Path.Combine(sandbox, "backup");
            string input = Path.Combine(sandbox, "Config");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(Path.Combine(input, "nested"));
            foreach (string item in _activeItems) Directory.CreateDirectory(Path.Combine(source, item));
            string target = Path.Combine(source, _activeItems[4]);
            string other = Path.Combine(source, _activeItems[5]);
            File.WriteAllText(Path.Combine(target, "replace.txt"), "before apply");
            File.WriteAllText(Path.Combine(target, "keep.txt"), "unrelated existing file");
            File.WriteAllText(Path.Combine(other, "replace.txt"), "unselected target");
            File.WriteAllText(Path.Combine(input, "replace.txt"), "after apply");
            File.WriteAllText(Path.Combine(input, "nested", "settings.ini"), "nested payload");
            string singleFile = Path.Combine(sandbox, "extra.txt");
            File.WriteAllText(singleFile, "single file payload");
            foreach (MachineProfile profile in _settings.Profiles)
            {
                profile.SourceRoot = source;
                profile.BackupRoot = backup;
            }
            ApplyProfiles(_settings.Profiles, 0);
            await RefreshSourceStatesAsync();
            DataObject payload = new DataObject(DataFormats.FileDrop, new[] { input, singleFile });
            DragEventArgs drag = new DragEventArgs(payload, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
            DropInputsEnter(_dropList, drag);
            if (drag.Effect != DragDropEffects.None) throw new InvalidOperationException("Backup mode accepted a file drop.");
            _mode.SelectedIndex = 1;
            if (_checks.Any(check => check.Checked) || _selectAll.Visible || !_dropGroup.Visible || _start.Enabled)
                throw new InvalidOperationException("Apply mode did not clear selection and require inputs.");
            _checks[4].Checked = true;
            _checks[5].Checked = true;
            if (SelectedItems().Length != 1 || !_checks[5].Checked || _checks[4].Checked)
                throw new InvalidOperationException("Apply mode allowed more than one target.");
            _mode.SelectedIndex = 0;
            if (_checks.Any(check => check.Checked)) throw new InvalidOperationException("Mode change retained target selection.");
            _checks[4].Checked = true;
            _checks[5].Checked = true;
            if (SelectedItems().Length != 2) throw new InvalidOperationException("Backup multi-selection regressed.");
            _mode.SelectedIndex = 1;
            _checks[4].Checked = true;
            if (_start.Enabled) throw new InvalidOperationException("Empty apply inputs enabled execution.");
            DragEventArgs textDrop = new DragEventArgs(new DataObject(DataFormats.Text, "not a file"), 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
            DropInputsEnter(_dropList, textDrop);
            DropInputs(_dropList, textDrop);
            if (textDrop.Effect != DragDropEffects.None || _dropList.Items.Count != 0)
                throw new InvalidOperationException("Non-file drop was accepted.");
            DropInputsEnter(_dropList, drag);
            DropInputs(_dropList, drag);
            if (drag.Effect != DragDropEffects.Copy || _dropList.Items.Count != 2 || !_start.Enabled)
                throw new InvalidOperationException("FileDrop did not populate apply inputs.");
            _name.Text = "bad/name";
            if (_start.Enabled) throw new InvalidOperationException("Invalid prior-backup name enabled apply.");
            _name.Clear();
            _previewTimer.Stop();
            await RefreshPreviewAsync();
            if (!_start.Enabled || !_preview.Text.Contains(target) || !_preview.Text.Contains("① 사전 백업"))
                throw new InvalidOperationException("Apply preview/readiness did not show target and prior backup.");
            if (_mode.Text != "백업 후 적용 (항목 1개)" || _profile.Text != _settings.Profiles[0].Name ||
                _modeLabel.Text != "작업 방식 · 백업 후 적용")
                throw new InvalidOperationException("Apply mode or profile display text is incorrect.");
            CaptureSmoke(Path.Combine(root, "ui-apply-ready.png"));
            SetBusy(true);
            DropInputsEnter(_dropList, drag);
            if (_mode.Enabled || _dropGroup.Enabled || _pathGroup.Enabled || _itemGroup.Enabled || _nameGroup.Enabled ||
                _start.Enabled || !_cancel.Enabled || drag.Effect != DragDropEffects.None)
                throw new InvalidOperationException("Busy apply inputs remained editable.");
            SetBusy(false);
            ApplyRequest request = new ApplyRequest
            {
                SourceRoot = source, BackupRoot = backup, Name = _name.Text, Item = SelectedItems()[0],
                ConfiguredItems = (string[])_activeItems.Clone(), InputPaths = _dropList.Items.Cast<string>().ToArray(),
                StartedAt = DateTime.Now, LogDirectory = Path.Combine(sandbox, "logs")
            };
            await ExecuteApplyAsync(request);
            ApplyResult result = _lastApplyResult;
            if (result == null || result.State != BackupState.Complete || result.Backup == null ||
                result.Backup.State != BackupState.Complete || result.Application == null || result.Application.State != BackupState.Complete)
                throw new InvalidOperationException("Fixture apply failed: " + (result == null ? "no result" : result.Message));
            string saved = result.Backup.Items.Single().Destination;
            if (File.ReadAllText(Path.Combine(saved, "replace.txt")) != "before apply" ||
                File.ReadAllText(Path.Combine(target, "replace.txt")) != "after apply" ||
                File.ReadAllText(Path.Combine(target, "keep.txt")) != "unrelated existing file" ||
                File.ReadAllText(Path.Combine(target, "extra.txt")) != "single file payload" ||
                File.ReadAllText(Path.Combine(target, "nested", "settings.ini")) != "nested payload" ||
                File.ReadAllText(Path.Combine(other, "replace.txt")) != "unselected target" || Directory.Exists(Path.Combine(target, "Config")))
                throw new InvalidOperationException("Prior backup, flattened folder content, or preservation checks failed.");
            if (_results.Rows.Count != 2 || !_results.Rows[0].Cells[0].Value.ToString().Contains("사전 백업") ||
                !_results.Rows[1].Cells[0].Value.ToString().Contains("적용") || _busy || !_start.Enabled || _cancel.Enabled)
                throw new InvalidOperationException("Apply completion UI does not separate backup and application results.");
            CaptureSmoke(Path.Combine(root, "ui-apply-result.png"));
        }

        private void CaptureSmoke(string path)
        {
            using (Bitmap capture = new Bitmap(Width, Height))
            {
                DrawToBitmap(capture, new Rectangle(Point.Empty, Size));
                capture.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }

    internal sealed class ProfileSettingsForm : Form
    {
        private readonly List<TextBox> _sources = new List<TextBox>();
        private readonly List<TextBox> _backups = new List<TextBox>();
        private readonly List<TextBox[]> _itemNames = new List<TextBox[]>();
        private readonly List<MachineProfile> _original;
        private readonly TabControl _equipmentTabs = new TabControl();
        private readonly ToolTip _fieldTips = new ToolTip();
        public List<MachineProfile> Profiles { get; private set; }

        public ProfileSettingsForm(List<MachineProfile> profiles, int selectedProfile = 0)
        {
            _original = profiles;
            Text = "장비별 경로·항목 설정";
            Font = new Font("맑은 고딕", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(850, 485);
            MinimumSize = new Size(740, 515);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(246, 248, 251);

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            Label explanation = new Label
            {
                Text = "장비별 기본 경로와 여섯 항목의 실제 폴더 이름을 설정합니다.\r\n항목 이름을 변경하면 해당 원본 폴더와 같은 이름의 백업 부모 폴더를 사용합니다.",
                Dock = DockStyle.Fill, ForeColor = Color.FromArgb(76, 91, 110), AutoSize = false
            };
            root.Controls.Add(explanation, 0, 0);
            _equipmentTabs.Dock = DockStyle.Fill;
            _equipmentTabs.Margin = new Padding(0, 0, 0, 12);
            root.Controls.Add(_equipmentTabs, 0, 1);
            for (int i = 0; i < profiles.Count; i++)
            {
                TabPage page = new TabPage(profiles[i].Name) { BackColor = Color.White, Padding = new Padding(12), AutoScroll = true };
                _equipmentTabs.TabPages.Add(page);
                TableLayoutPanel profilePanel = new TableLayoutPanel { Dock = DockStyle.Top, Height = 295, ColumnCount = 1, RowCount = 4 };
                profilePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
                profilePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
                profilePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 125));
                profilePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
                TableLayoutPanel fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 97));
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 77));
                fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                fields.Controls.Add(new Label { Text = "원본 기본 경로", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
                fields.Controls.Add(new Label { Text = "백업 기본 경로", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
                TextBox source = new TextBox { Text = profiles[i].SourceRoot, Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 6, 8, 2) };
                TextBox backup = new TextBox { Text = profiles[i].BackupRoot, Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 6, 8, 2) };
                _sources.Add(source);
                _backups.Add(backup);
                fields.Controls.Add(source, 1, 0);
                fields.Controls.Add(backup, 1, 1);
                Button browseSource = new Button { Text = "찾아보기", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 4) };
                Button browseBackup = new Button { Text = "찾아보기", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 4) };
                browseSource.Click += delegate { BrowseForFolder(source); };
                browseBackup.Click += delegate { BrowseForFolder(backup); };
                fields.Controls.Add(browseSource, 2, 0);
                fields.Controls.Add(browseBackup, 2, 1);
                profilePanel.Controls.Add(fields, 0, 0);
                profilePanel.Controls.Add(new Label { Text = "항목 폴더 이름  ·  숫자 접두사를 포함한 전체 이름", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(34, 97, 191) }, 0, 1);
                TableLayoutPanel names = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3, Margin = Padding.Empty };
                names.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
                names.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                names.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));
                names.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                for (int row = 0; row < 3; row++) names.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / 3));
                string[] configured = PathSafety.ValidateItemNames(profiles[i].ItemNames);
                TextBox[] nameFields = new TextBox[configured.Length];
                for (int slot = 0; slot < configured.Length; slot++)
                {
                    int column = slot < 3 ? 0 : 2;
                    int row = slot % 3;
                    names.Controls.Add(new Label { Text = "항목 " + (slot + 1), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(column == 0 ? 0 : 13, 0, 0, 0) }, column, row);
                    TextBox name = new TextBox { Text = configured[slot], Dock = DockStyle.Fill, MaxLength = 32767, Margin = new Padding(0, 8, 0, 0) };
                    name.TextChanged += delegate(object sender, EventArgs e) { TextBox field = (TextBox)sender; _fieldTips.SetToolTip(field, field.Text); };
                    _fieldTips.SetToolTip(name, name.Text);
                    names.Controls.Add(name, column + 1, row);
                    nameFields[slot] = name;
                }
                _itemNames.Add(nameFields);
                profilePanel.Controls.Add(names, 0, 2);
                profilePanel.Controls.Add(new Label
                {
                    Text = "예: 5_TERRACE, 6_SSIDE. 원본에 존재하는 폴더명을 정확히 입력하세요.\r\n빈 이름·중복 이름·경로 구분자는 사용할 수 없습니다. 실제 폴더의 이름은 변경하지 않습니다.",
                    Dock = DockStyle.Fill, ForeColor = Color.FromArgb(91, 104, 121), Padding = new Padding(0, 8, 0, 0)
                }, 0, 3);
                page.Controls.Add(profilePanel);
            }
            _equipmentTabs.SelectedIndex = Math.Max(0, Math.Min(selectedProfile, profiles.Count - 1));
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            Button save = new Button { Text = "저장", Width = 100, Height = 33, BackColor = Color.FromArgb(34, 97, 191), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            Button cancel = new Button { Text = "취소", Width = 100, Height = 33, DialogResult = DialogResult.Cancel };
            save.Click += Save;
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            root.Controls.Add(buttons, 0, 2);
            Controls.Add(root);
            AcceptButton = save;
            CancelButton = cancel;
            FormClosed += delegate { _fieldTips.Dispose(); };
        }

        private void BrowseForFolder(TextBox target)
        {
            using (FolderBrowserDialog browser = new FolderBrowserDialog())
            {
                browser.Description = "여섯 항목이 들어 있는 상위 폴더를 선택하세요.";
                browser.ShowNewFolderButton = false;
                // Avoid a synchronous network existence check before opening the picker.
                if (browser.ShowDialog(this) == DialogResult.OK) target.Text = browser.SelectedPath;
            }
        }

        private void Save(object sender, EventArgs e)
        {
            string error;
            if (TryCommit(out error))
            {
                DialogResult = DialogResult.OK;
                Close();
            }
            else MessageBox.Show(this, error, "설정을 확인하세요", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Both the Save button and fixture smoke use this exact validation/commit path.
        internal bool TryCommit(out string message)
        {
            message = null;
            List<MachineProfile> profiles = new List<MachineProfile>();
            for (int i = 0; i < _original.Count; i++)
            {
                string source;
                string backup;
                try { source = PathSafety.Normalize(_sources[i].Text); }
                catch (Exception error)
                {
                    message = _original[i].Name + " 원본 경로: " + error.Message;
                    _equipmentTabs.SelectedIndex = i;
                    _sources[i].Focus();
                    return false;
                }
                try { backup = PathSafety.Normalize(_backups[i].Text); }
                catch (Exception error)
                {
                    message = _original[i].Name + " 백업 경로: " + error.Message;
                    _equipmentTabs.SelectedIndex = i;
                    _backups[i].Focus();
                    return false;
                }
                string[] names = _itemNames[i].Select(field => field.Text).ToArray();
                for (int slot = 0; slot < names.Length; slot++)
                {
                    try { PathSafety.ValidateItemName(names[slot]); }
                    catch (ArgumentException error)
                    {
                        message = _original[i].Name + " 항목 " + (slot + 1) + ": " + error.Message;
                        _equipmentTabs.SelectedIndex = i;
                        _itemNames[i][slot].Focus();
                        return false;
                    }
                }
                try { names = PathSafety.ValidateItemNames(names); }
                catch (ArgumentException error)
                {
                    message = _original[i].Name + " 항목 폴더 이름: " + error.Message;
                    _equipmentTabs.SelectedIndex = i;
                    _itemNames[i][0].Focus();
                    return false;
                }
                profiles.Add(new MachineProfile { Name = _original[i].Name, SourceRoot = source, BackupRoot = backup, ItemNames = names });
            }
            Profiles = profiles;
            return true;
        }

        internal void SetSmokeItemName(int profile, int slot, string name)
        {
            _equipmentTabs.SelectedIndex = profile;
            _itemNames[profile][slot].Text = name;
        }
    }
}
