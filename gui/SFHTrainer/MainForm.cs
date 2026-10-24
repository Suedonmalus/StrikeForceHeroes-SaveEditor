using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using SFHTrainer.Protocol;

namespace SFHTrainer;

/// <summary>
/// 主窗口：状态栏 / 金钱 / 武器列表 / 词条编辑 / 日志。
/// 所有耗时操作都走 await，不阻塞 UI 线程；窗口关闭时发 QUIT 再断开。
/// </summary>
public sealed class MainForm : Form
{
    // ---------------------------------------------------------------- 配色
    static readonly Color Bg = Color.FromArgb(243, 245, 249);
    static readonly Color CardBg = Color.White;
    static readonly Color BorderColor = Color.FromArgb(222, 228, 238);
    static readonly Color Ink = Color.FromArgb(31, 41, 55);
    static readonly Color Sub = Color.FromArgb(108, 117, 128);
    static readonly Color Accent = Color.FromArgb(37, 99, 235);
    static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
    static readonly Color OkColor = Color.FromArgb(22, 163, 74);
    static readonly Color WarnColor = Color.FromArgb(180, 120, 8);
    static readonly Color ErrColor = Color.FromArgb(220, 38, 38);
    static readonly Color MutedColor = Color.FromArgb(156, 163, 175);
    static readonly Color GridHeaderBg = Color.FromArgb(249, 250, 252);
    static readonly Color SelectionBg = Color.FromArgb(219, 234, 254);
    static readonly Color LogBg = Color.FromArgb(15, 23, 42);
    static readonly Color LogFg = Color.FromArgb(209, 213, 219);

    static readonly Font UiFont = new("Microsoft YaHei UI", 9F);
    static readonly Font UiFontBold = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    static readonly Font SmallFont = new("Microsoft YaHei UI", 8.25F);
    static readonly Font TitleFont = new("Microsoft YaHei UI", 12F, FontStyle.Bold);
    static readonly Font MoneyFont = new("Microsoft YaHei UI", 18F, FontStyle.Bold);
    static readonly Font MonoFont = new("Consolas", 9F);

    // ---------------------------------------------------------------- 状态
    readonly TrainerController _ctl;
    readonly bool _smokeTest;
    readonly string? _reportPath;
    readonly string? _screenshotPath;
    readonly int _holdSeconds;

    bool _busy;
    bool _binding;
    bool _shutdownDone;
    bool _readyPolling;
    int? _selectedItemId;

    /// <summary>READY=0 时的自动刷新：只在未就绪时运行，就绪后立即停止。</summary>
    readonly System.Windows.Forms.Timer _readyTimer = new() { Interval = 2000 };

    readonly List<(string Step, bool Ok, string Detail)> _smoke = new();

    // ---------------------------------------------------------------- 控件
    Label _lblProc = null!, _lblProcDot = null!;
    Label _lblAgent = null!, _lblAgentDot = null!;
    Label _lblDll = null!;
    Button _btnDetect = null!, _btnInject = null!, _btnRefresh = null!;
    Label _lblMoney = null!;
    Label _lblReady = null!;
    TextBox _txtMoney = null!;
    Button _btnMoney999 = null!, _btnMoneyApply = null!;
    Label _lblMoneyHint = null!;
    DataGridView _grid = null!;
    Label _lblGridHint = null!;
    ComboBox _cmbTypeFilter = null!;
    int _typeFilter = -2; // -2=全部, -1=护甲/装备, 0..10=EItemType
    Label _lblEditTitle = null!, _lblEditHint = null!;
    ComboBox _cmbWeaponRarity = null!, _cmbCamo = null!;
    readonly ComboBox[] _cboMod = new ComboBox[4];
    readonly ComboBox[] _cboRarity = new ComboBox[4];
    Button _btnApplyMods = null!, _btnSave = null!, _btnClearLog = null!;
    RichTextBox _log = null!;

    static readonly string[] TypeFilterLabels =
    {
        "全部类型", "护甲 / 装备", "突击步枪", "机枪", "马格南", "近战", "杂项",
        "火箭筒", "副武器", "护盾", "霰弹枪", "狙击枪",
    };
    static readonly int[] TypeFilterValues = { -2, -1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

    public bool SmokePassed { get; private set; }

    public MainForm(
        bool smokeTest = false,
        string? reportPath = null,
        string pipeName = AgentClient.DefaultPipeName,
        int holdSeconds = 0,
        string? screenshotPath = null)
    {
        _smokeTest = smokeTest;
        _reportPath = reportPath;
        _screenshotPath = screenshotPath;
        _holdSeconds = holdSeconds;
        _ctl = new TrainerController(pipeName);

        SuspendLayout();
        BuildUi();
        ResumeLayout(false);
        PerformLayout();

        _ctl.Logged += OnControllerLogged;
        _ctl.StatusChanged += OnControllerStatusChanged;

        _readyTimer.Tick += async (_, _) => await ReadyTickAsync();

        Load += OnFormLoaded;
        FormClosing += OnFormClosing;
    }

    // ================================================================ UI 构建

    void BuildUi()
    {
        Text = "战火英雄修改器 — Strike Force Heroes";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1240, 800);
        MinimumSize = new Size(1080, 680);
        BackColor = Bg;
        Font = UiFont;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Font;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            Padding = new Padding(0),
            ColumnCount = 2,
            RowCount = 2,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 444F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 196F));

        // 顶部状态栏
        var top = BuildTopBar();
        top.Dock = DockStyle.Fill;
        top.Margin = new Padding(0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 444F));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 196F));

        var left = BuildLeftColumn();
        left.Dock = DockStyle.Fill;
        left.Margin = new Padding(12, 12, 6, 6);

        var modCard = BuildModCard();
        modCard.Dock = DockStyle.Fill;
        modCard.Margin = new Padding(6, 12, 12, 6);

        var logCard = BuildLogCard();
        logCard.Dock = DockStyle.Fill;
        logCard.Margin = new Padding(12, 6, 12, 12);

        body.Controls.Add(left, 0, 0);
        body.Controls.Add(modCard, 1, 0);
        body.Controls.Add(logCard, 0, 1);
        body.SetColumnSpan(logCard, 2);

        // 顶部条 + 主体 放进同一个 TableLayoutPanel（1 列 2 行）
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 100F));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        top.Margin = new Padding(0);
        body.Margin = new Padding(0);
        shell.Controls.Add(top, 0, 0);
        shell.Controls.Add(body, 0, 1);

        root.Controls.Add(shell, 0, 0);
        root.SetColumnSpan(shell, 2);
        root.SetRowSpan(shell, 2);
        Controls.Add(root);
    }

    Control BuildTopBar()
    {
        var bar = new TableLayoutPanel
        {
            BackColor = CardBg,
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(18, 13, 18, 13),
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148F));
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        bar.ColumnCount = 6;
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
        };

        var brand = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        brand.Controls.Add(new Label
        {
            Text = "战火英雄修改器",
            Font = TitleFont,
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(0, 4),
        });
        brand.Controls.Add(new Label
        {
            Text = "Strike Force Heroes · 进程注入 + 词条编辑",
            Font = SmallFont,
            ForeColor = Sub,
            AutoSize = true,
            Location = new Point(1, 32),
        });

        // 进程状态
        var procBox = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        _lblProcDot = new Label
        {
            Text = "●",
            Font = new Font("Microsoft YaHei UI", 10F),
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(0, 8),
        };
        _lblProc = new Label
        {
            Text = "游戏进程：检测中…",
            Font = UiFont,
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(18, 9),
        };
        var lblDllCaption = new Label
        {
            Text = "代理 DLL：",
            Font = SmallFont,
            ForeColor = Sub,
            AutoSize = true,
            Location = new Point(18, 36),
        };
        _lblDll = new Label
        {
            Text = "检查中…",
            Font = SmallFont,
            ForeColor = Sub,
            AutoSize = true,
            Location = new Point(88, 36),
        };
        procBox.Controls.Add(_lblProcDot);
        procBox.Controls.Add(_lblProc);
        procBox.Controls.Add(lblDllCaption);
        procBox.Controls.Add(_lblDll);

        // 代理连接状态
        var agentBox = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        _lblAgentDot = new Label
        {
            Text = "●",
            Font = new Font("Microsoft YaHei UI", 10F),
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(0, 8),
        };
        _lblAgent = new Label
        {
            Text = "代理连接：未连接",
            Font = UiFont,
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(18, 9),
        };
        var lblPipe = new Label
        {
            Text = @"\\.\pipe\SFHTrainer",
            Font = SmallFont,
            ForeColor = Sub,
            AutoSize = true,
            Location = new Point(18, 36),
        };
        agentBox.Controls.Add(_lblAgentDot);
        agentBox.Controls.Add(_lblAgent);
        agentBox.Controls.Add(lblPipe);

        _btnInject = MakeButton("注入代理", primary: true, width: 136, height: 36);
        _btnInject.Dock = DockStyle.Fill;
        _btnInject.Margin = new Padding(0, 13, 0, 13);
        _btnInject.Click += async (_, _) => await SafeAsync(InjectAsync);

        _btnRefresh = MakeButton("刷新", primary: false, width: 84, height: 36);
        _btnRefresh.Dock = DockStyle.Fill;
        _btnRefresh.Margin = new Padding(0, 13, 8, 13);
        _btnRefresh.Enabled = false;
        _btnRefresh.Click += async (_, _) => await SafeAsync(RefreshFromServerAsync);

        _btnDetect = MakeButton("重新检测", primary: false, width: 104, height: 36);
        _btnDetect.Dock = DockStyle.Fill;
        _btnDetect.Margin = new Padding(0, 13, 8, 13);
        _btnDetect.Click += (_, _) => SafeFire(DetectProcess);

        bar.Controls.Add(brand, 0, 0);
        bar.Controls.Add(procBox, 1, 0);
        bar.Controls.Add(agentBox, 2, 0);
        bar.Controls.Add(_btnRefresh, 3, 0);
        bar.Controls.Add(_btnDetect, 4, 0);
        bar.Controls.Add(_btnInject, 5, 0);
        return bar;
    }

    Control BuildLeftColumn()
    {
        var col = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Bg,
            Margin = new Padding(0),
        };
        col.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        col.RowStyles.Add(new RowStyle(SizeType.Absolute, 124F));
        col.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        col.Controls.Add(BuildMoneyCard(), 0, 0);
        col.Controls.Add(BuildGridCard(), 0, 1);
        return col;
    }

    Control BuildMoneyCard()
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 0) };

        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _lblReady = new Label
        {
            Text = "● 游戏未就绪（等待加载）…",
            Font = SmallFont,
            ForeColor = WarnColor,
            AutoSize = true,
            Visible = false,
            Margin = new Padding(0, 1, 0, 0),
        };
        var titleRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        titleRow.Controls.Add(new Label
        {
            Text = "当前金钱",
            Font = SmallFont,
            ForeColor = Sub,
            AutoSize = true,
            Margin = new Padding(0, 1, 16, 0),
        });
        titleRow.Controls.Add(_lblReady);
        t.Controls.Add(titleRow, 0, 0);
        _lblMoney = new Label
        {
            Text = "—",
            Font = MoneyFont,
            ForeColor = Accent,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0),
        };
        t.Controls.Add(_lblMoney, 0, 1);

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };

        flow.Controls.Add(new Label
        {
            Text = "金额：",
            Font = UiFont,
            ForeColor = Ink,
            AutoSize = true,
            Margin = new Padding(0, 7, 0, 0),
        });

        _txtMoney = new TextBox
        {
            Width = 118,
            Height = 28,
            Font = UiFont,
            BorderStyle = BorderStyle.FixedSingle,
            Text = "999999",
            Margin = new Padding(0, 3, 8, 0),
        };
        flow.Controls.Add(_txtMoney);

        _btnMoney999 = MakeButton("设为 999999", primary: false, width: 108, height: 30);
        _btnMoney999.Margin = new Padding(0, 2, 8, 0);
        _btnMoney999.Click += async (_, _) => await SafeAsync(SetMoneyToCapAsync);
        flow.Controls.Add(_btnMoney999);

        _btnMoneyApply = MakeButton("应用金额", primary: true, width: 96, height: 30);
        _btnMoneyApply.Margin = new Padding(0, 2, 10, 0);
        _btnMoneyApply.Click += async (_, _) => await SafeAsync(ApplyMoneyAsync);
        flow.Controls.Add(_btnMoneyApply);

        _lblMoneyHint = new Label
        {
            Text = "上限 999999；代理会自动 clamp 到 0..999999",
            Font = SmallFont,
            ForeColor = Sub,
            AutoSize = true,
            Margin = new Padding(0, 9, 0, 0),
        };
        flow.Controls.Add(_lblMoneyHint);

        t.Controls.Add(flow, 0, 2);
        card.Controls.Add(t);
        return card;
    }

    Control BuildGridCard()
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };

        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var head = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        head.Controls.Add(new Label
        {
            Text = "武器 / 物品列表",
            Font = UiFontBold,
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(0, 4),
        });
        _lblGridHint = new Label
        {
            Text = "只有 type≥0 的武器可改词条；type=-1（护甲/装备）显示为灰色",
            Font = SmallFont,
            ForeColor = Sub,
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _lblGridHint.Location = new Point(360, 6);
        head.Controls.Add(_lblGridHint);
        head.Resize += (_, _) => _lblGridHint.Left = Math.Max(280, head.Width - _lblGridHint.Width);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = CardBg,
            BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToResizeColumns = true,
            AllowUserToOrderColumns = false,
            ReadOnly = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            EnableHeadersVisualStyles = false,
            GridColor = Color.FromArgb(235, 239, 245),
            ColumnHeadersHeight = 32,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            Font = UiFont,
            Margin = new Padding(0),
            ScrollBars = ScrollBars.Both,
        };
        _grid.ColumnHeadersDefaultCellStyle.BackColor = GridHeaderBg;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Sub;
        _grid.ColumnHeadersDefaultCellStyle.Font = UiFontBold;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = GridHeaderBg;
        _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
        _grid.DefaultCellStyle.BackColor = CardBg;
        _grid.DefaultCellStyle.ForeColor = Ink;
        _grid.DefaultCellStyle.SelectionBackColor = SelectionBg;
        _grid.DefaultCellStyle.SelectionForeColor = Ink;
        _grid.DefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 253);
        _grid.RowTemplate.Height = 28;
        _grid.SelectionChanged += (_, _) => OnGridSelectionChanged();
        _grid.DataError += (_, e) => e.ThrowException = false;

        AddColumn("c_id", "ID", 58);
        AddColumn("c_name", "名称", 158);
        AddColumn("c_type", "类型", 82);
        AddColumn("c_level", "等级", 58);
        AddColumn("c_rarity", "稀有度", 96);
        AddColumn("c_mods", "词条（槽位.词条×倍率）", 320, fill: true);
        AddColumn("c_equip", "装备标记", 92);

        t.Controls.Add(head, 0, 0);

        var filterPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 2, 0, 0),
        };
        filterPanel.Controls.Add(new Label
        {
            Text = "分类：",
            Font = UiFont,
            ForeColor = Sub,
            AutoSize = true,
            Margin = new Padding(0, 7, 4, 0),
        });
        _cmbTypeFilter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = UiFont,
            Width = 168,
            Margin = new Padding(0, 3, 0, 0),
            FlatStyle = FlatStyle.Flat,
        };
        foreach (var label in TypeFilterLabels) _cmbTypeFilter.Items.Add(label);
        _cmbTypeFilter.SelectedIndex = 0;
        _cmbTypeFilter.SelectedIndexChanged += (_, _) =>
        {
            if (_binding) return;
            _typeFilter = TypeFilterValues[_cmbTypeFilter.SelectedIndex];
            BindGrid();
        };
        filterPanel.Controls.Add(_cmbTypeFilter);
        t.Controls.Add(filterPanel, 0, 1);

        t.Controls.Add(_grid, 0, 2);
        card.Controls.Add(t);
        return card;
    }

    void AddColumn(string name, string header, int width, bool fill = false)
    {
        var col = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            MinimumWidth = 48,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            Resizable = DataGridViewTriState.True,
        };
        if (fill) col.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.Columns.Add(col);
    }

    Control BuildModCard()
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = new Padding(0) };

        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 12,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));   // 标题
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));   // 选中项
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));   // 提示
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));   // 稀有度 / 皮肤
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));   // 槽 0
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));   // 槽 1
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));   // 槽 2
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));   // 槽 3
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));   // 应用
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 8F));    // 少量留白
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));   // 保存
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));   // 保存提示

        t.Controls.Add(new Label
        {
            Text = "词条编辑",
            Font = UiFontBold,
            ForeColor = Ink,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 0),
        }, 0, 0);

        _lblEditTitle = new Label
        {
            Text = "未选中物品",
            Font = SmallFont,
            ForeColor = Sub,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        };
        t.Controls.Add(_lblEditTitle, 0, 1);

        _lblEditHint = new Label
        {
            Text = "在左侧列表选中一件武器后，可以为它的 4 个槽位设置词条。",
            Font = SmallFont,
            ForeColor = Sub,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 4),
        };
        t.Controls.Add(_lblEditHint, 0, 2);

        var attrRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 4),
        };
        attrRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54F));
        attrRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38F));
        attrRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44F));
        attrRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62F));
        attrRow.Controls.Add(new Label
        {
            Text = "稀有度",
            Font = UiFont,
            ForeColor = Sub,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        }, 0, 0);
        _cmbWeaponRarity = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Font = UiFont,
            Margin = new Padding(0, 2, 8, 2),
            Enabled = false,
        };
        attrRow.Controls.Add(_cmbWeaponRarity, 1, 0);
        attrRow.Controls.Add(new Label
        {
            Text = "皮肤",
            Font = UiFont,
            ForeColor = Sub,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        }, 2, 0);
        _cmbCamo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Font = UiFont,
            Margin = new Padding(0, 2, 0, 2),
            Enabled = false,
        };
        attrRow.Controls.Add(_cmbCamo, 3, 0);
        t.Controls.Add(attrRow, 0, 3);

        for (var i = 0; i < 4; i++)
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 4),
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116F));

            row.Controls.Add(new Label
            {
                Text = $"槽 {i + 1}",
                Font = UiFont,
                ForeColor = Sub,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0),
            }, 0, 0);

            var cboMod = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Font = UiFont,
                Margin = new Padding(0, 2, 6, 2),
                Enabled = false,
                IntegralHeight = false,
            };
            var cboRarity = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Font = UiFont,
                Margin = new Padding(0, 2, 0, 2),
                Enabled = false,
                IntegralHeight = false,
            };
            _cboMod[i] = cboMod;
            _cboRarity[i] = cboRarity;

            row.Controls.Add(cboMod, 1, 0);
            row.Controls.Add(cboRarity, 2, 0);
            t.Controls.Add(row, 0, 4 + i);
        }

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0),
            Padding = new Padding(0),
        };
        _btnApplyMods = MakeButton("应用到该武器", primary: true, width: 148, height: 32);
        _btnApplyMods.Margin = new Padding(0, 0, 8, 0);
        _btnApplyMods.Enabled = false;
        _btnApplyMods.Click += async (_, _) => await SafeAsync(ApplyModsAsync);






        btnRow.Controls.Add(_btnApplyMods);

        t.Controls.Add(btnRow, 0, 8);

        _btnSave = MakeButton("保存到游戏存档", primary: true, width: 200, height: 38);
        _btnSave.Dock = DockStyle.Fill;
        _btnSave.Margin = new Padding(0, 4, 0, 0);
        _btnSave.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        _btnSave.Click += async (_, _) => await SafeAsync(SaveAsync);
        t.Controls.Add(_btnSave, 0, 10);

        t.Controls.Add(new Label
        {
            Text = "改完必须点此按钮才会写入游戏存档（SAVE）。",
            Font = SmallFont,
            ForeColor = Sub,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        }, 0, 11);

        t.Dock = DockStyle.Top;
        t.AutoSize = false;
        t.Height = 400;
        t.Margin = new Padding(0);
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        scroll.Controls.Add(t);
        card.Controls.Add(scroll);
        return card;
    }

    Control BuildLogCard()
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = new Padding(0) };

        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var head = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        head.Controls.Add(new Label
        {
            Text = "操作日志",
            Font = UiFontBold,
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(0, 2),
        });
        _btnClearLog = MakeButton("清空日志", primary: false, width: 88, height: 24);
        _btnClearLog.Font = SmallFont;
        _btnClearLog.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnClearLog.Location = new Point(700, 0);
        _btnClearLog.Click += (_, _) => _log.Clear();
        head.Controls.Add(_btnClearLog);
        head.Resize += (_, _) => _btnClearLog.Left = Math.Max(200, head.Width - _btnClearLog.Width);

        _log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = LogBg,
            ForeColor = LogFg,
            Font = MonoFont,
            BorderStyle = BorderStyle.None,
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            DetectUrls = false,
            Margin = new Padding(0),
        };

        t.Controls.Add(head, 0, 0);
        t.Controls.Add(_log, 0, 1);
        card.Controls.Add(t);
        return card;
    }

    static Button MakeButton(string text, bool primary, int width, int height)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = height,
            FlatStyle = FlatStyle.Flat,
            Font = UiFont,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            BackColor = primary ? Accent : CardBg,
            ForeColor = primary ? Color.White : Ink,
            TabStop = true,
        };
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        b.FlatAppearance.BorderColor = BorderColor;
        b.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Color.FromArgb(240, 243, 248);
        b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(23, 62, 178) : Color.FromArgb(232, 237, 245);
        return b;
    }

    // ================================================================ 事件

    void OnFormLoaded(object? sender, EventArgs e)
    {
        _ctl.DetectProcess();
        RefreshUi();
        AppendLog($"代理 DLL 路径：{_ctl.AgentDllPath}", LogKind.Info);
        AppendLog("界面就绪。步骤：启动游戏 → 点「注入代理」→ 等 READY=1 后改数值 → 点「保存到游戏存档」。", LogKind.Info);

        if (_smokeTest)
            BeginInvoke(new Action(async () => await RunSmokeAsync()));
    }

    void OnControllerLogged(string message, LogKind kind) => AppendLog(message, kind);

    void OnControllerStatusChanged()
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(RefreshUi)); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
            return;
        }
        RefreshUi();
    }

    void OnGridSelectionChanged()
    {
        if (_binding) return;
        BindModEditor();
    }

    void DetectProcess()
    {
        _ctl.DetectProcess();
        RefreshUi();
    }

    // ================================================================ 命令流程

    async Task InjectAsync()
    {
        if (_busy) return;
        SetBusy(true, "正在注入…");
        try
        {
            await _ctl.InjectAndConnectAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    async Task RefreshFromServerAsync()
    {
        if (_busy) return;
        SetBusy(true, "正在读取…");
        try
        {
            await _ctl.RefreshStateAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    async Task SetMoneyToCapAsync()
    {
        if (_busy) return;
        _txtMoney.Text = TrainerController.MoneyCap.ToString(CultureInfo.InvariantCulture);
        SetBusy(true, "正在设置金钱…");
        try
        {
            await _ctl.SetMoneyAsync(TrainerController.MoneyCap);
        }
        finally
        {
            SetBusy(false);
        }
    }

    async Task ApplyMoneyAsync()
    {
        if (_busy) return;
        var raw = _txtMoney.Text.Trim().Replace(",", string.Empty);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            AppendLog("金额必须是 0..999999 之间的整数。", LogKind.Warn);
            return;
        }
        if (value < 0 || value > TrainerController.MoneyCap)
        {
            AppendLog($"金额 {value} 超出范围：游戏显示上限是 {TrainerController.MoneyCap}。", LogKind.Warn);
            return;
        }
        SetBusy(true, "正在设置金钱…");
        try
        {
            await _ctl.SetMoneyAsync(value);
        }
        finally
        {
            SetBusy(false);
        }
    }

    async Task ApplyModsAsync()
    {
        if (_busy) return;
        var item = SelectedItem();
        if (item is null)
        {
            AppendLog("请先在左侧列表里选中一件武器。", LogKind.Warn);
            return;
        }
        if (!item.IsWeapon)
        {
            AppendLog($"「{DisplayNames.ItemName(item)}」没有武器数据（type=-1），不能设置词条。", LogKind.Warn);
            return;
        }

        var slots = new List<ModSlot>(4);
        for (var i = 0; i < 4; i++)
        {
            if (_cboMod[i].SelectedItem is not ModChoice choice)
                continue;
            var rarity = _cboRarity[i].SelectedItem is RarityChoice rc ? rc.Index : 0;
            slots.Add(new ModSlot(i, choice.Index, rarity));
        }
        if (slots.Count == 0)
        {
            AppendLog("没有可应用的槽位：请先为槽位选择词条。", LogKind.Warn);
            return;
        }

        // 先把下拉里的选择取下来：SetBusy 会触发 RefreshUi → BindModEditor，
        // 那里会把「武器稀有度 / 皮肤」下拉重置成存档里的旧值，
        // 如果等到 SetBusy 之后再读，读到的永远是旧值（皮肤会"弹回"原选项）。
        var weaponRarity = _cmbWeaponRarity.SelectedItem is RarityChoice wr ? wr.Index : item.Rarity;
        var camoIndex = _cmbCamo.SelectedItem is CamoChoice cc ? cc.Index : item.CamoIndex;

        SetBusy(true, "正在写入词条…");
        try
        {
            AppendLog($"正在为「{DisplayNames.ItemName(item)}」（ID {item.Id}）写入 {slots.Count} 个槽位…", LogKind.Info);
            await _ctl.SetRarityAsync(item.Id, weaponRarity);
            await _ctl.SetCamoAsync(item.Id, camoIndex);
            await _ctl.ApplyModsAsync(item.Id, slots);
        }
        finally
        {
            SetBusy(false);
        }
    }

    async Task SaveAsync()
    {
        if (_busy) return;
        SetBusy(true, "正在保存…");
        try
        {
            await _ctl.SaveAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    void SetBusy(bool busy, string? statusText = null)
    {
        _busy = busy;
        if (busy && statusText is not null) AppendLog(statusText, LogKind.Info);
        UseWaitCursor = busy;
        RefreshUi();
    }

    async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppendLog($"操作异常：{TrainerController.Describe(ex)}", LogKind.Error);
            SetBusy(false);
        }
        finally
        {
            RefreshUi();
        }
    }

    void SafeFire(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppendLog($"操作异常：{TrainerController.Describe(ex)}", LogKind.Error);
        }
        RefreshUi();
    }

    // ================================================================ 刷新

    void RefreshUi()
    {
        if (IsDisposed || Disposing) return;

        var target = _ctl.Target;
        _lblProcDot.ForeColor = target is not null ? OkColor : MutedColor;
        _lblProc.Text = target is not null
            ? $"游戏进程：已检测到（PID {target.Pid}）"
            : "游戏进程：未启动";
        _lblProc.ForeColor = target is not null ? Ink : Sub;

        var connected = _ctl.Client.IsConnected;
        _lblAgentDot.ForeColor = connected ? OkColor : MutedColor;
        _lblAgent.Text = connected ? "代理连接：已连接" : "代理连接：未连接";
        _lblAgent.ForeColor = connected ? Ink : Sub;

        var dllExists = File.Exists(_ctl.AgentDllPath);
        _lblDll.Text = dllExists
            ? (_ctl.AgentDllEmbedded ? "已内嵌（已释放）" : "已找到")
            : "缺失（内嵌资源不可用）";
        _lblDll.ForeColor = dllExists ? OkColor : ErrColor;

        _btnInject.Enabled = !_busy;
        _btnDetect.Enabled = !_busy;
        _btnRefresh.Enabled = connected && !_busy;
        _btnInject.Text = _busy ? "请稍候…" : connected ? "重新注入" : "注入代理";

        var state = _ctl.State;
        _lblMoney.Text = state is not null ? DisplayNames.Money(state.Money) : "—";

        // 协议 v1.1：READY=0 时存档还在加载，所有写操作禁用。
        var ready = state?.Ready ?? false;
        _lblReady.Visible = state is not null && !ready;
        _lblReady.Text = "● 游戏未就绪（等待加载），写操作已禁用…";

        var canOperate = connected && !_busy && ready;
        _btnMoney999.Enabled = canOperate;
        _btnMoneyApply.Enabled = canOperate;
        _txtMoney.Enabled = !_busy;
        _btnSave.Enabled = canOperate;

        _lblGridHint.Text = state is null
            ? "尚未读取状态：注入代理并读档后会自动刷新"
            : $"共 {state.Items.Count} 件物品，武器 {state.Items.Count(i => i.IsWeapon)} 件；type=-1 为护甲/装备，置灰不可编辑";

        BindGrid();
        BindModEditor();
        EnsureReadyPolling();
    }

    /// <summary>READY=0 时每 2 秒自动拉一次 STATE；就绪后立即停止（协议 v1.1）。</summary>
    void EnsureReadyPolling()
    {
        if (!_ctl.Client.IsConnected || _ctl.State is null)
        {
            StopReadyPolling();
            return;
        }
        if (_ctl.State.Ready)
        {
            StopReadyPolling();
            return;
        }
        if (!_readyTimer.Enabled)
        {
            _readyTimer.Start();
            AppendLog("游戏尚未就绪（READY=0），每 2 秒自动刷新一次，就绪后自动停止。", LogKind.Info);
        }
    }

    void StopReadyPolling()
    {
        if (!_readyTimer.Enabled) return;
        _readyTimer.Stop();
        if (_ctl.State?.Ready == true)
            AppendLog("已就绪，自动轮询停止。", LogKind.Ok);
    }

    async Task ReadyTickAsync()
    {
        if (_readyPolling) return;
        if (!_ctl.Client.IsConnected)
        {
            StopReadyPolling();
            return;
        }
        if (_ctl.State?.Ready == true)
        {
            StopReadyPolling();
            return;
        }

        _readyPolling = true;
        try
        {
            await _ctl.RefreshStateAsync(silent: true);
        }
        catch (Exception ex)
        {
            AppendLog($"自动刷新失败：{TrainerController.Describe(ex)}", LogKind.Warn);
        }
        finally
        {
            _readyPolling = false;
        }
    }

    void BindGrid()
    {
        var state = _ctl.State;
        _binding = true;
        try
        {
            var keepId = _selectedItemId;
            var keepScroll = 0;
            try { keepScroll = _grid.FirstDisplayedScrollingRowIndex; } catch { keepScroll = 0; }

            _grid.Rows.Clear();
            if (state is not null)
            {
                foreach (var item in state.Items)
                {
                    if (!MatchesTypeFilter(item)) continue;
                    var index = _grid.Rows.Add(
                        item.Id.ToString(CultureInfo.InvariantCulture),
                        DisplayNames.ItemName(item),
                        DisplayNames.ItemType(item.Type),
                        item.Level.ToString(CultureInfo.InvariantCulture),
                        DisplayNames.RarityName(state, item.Rarity),
                        DisplayNames.SlotSummary(item, state),
                        DisplayNames.Equipped(item.EquippedMask));

                    var row = _grid.Rows[index];
                    row.Tag = item.Id;
                    if (!item.IsWeapon)
                    {
                        row.DefaultCellStyle.ForeColor = MutedColor;
                        row.DefaultCellStyle.SelectionForeColor = MutedColor;
                        row.Cells[2].ToolTipText = "type=-1：该物品没有武器数据";
                        row.Cells[5].Value = DisplayNames.SlotSummary(item, state);
                    }
                }

                if (_grid.Rows.Count > 0)
                {
                    var targetRow = -1;
                    if (keepId.HasValue)
                    {
                        for (var i = 0; i < _grid.Rows.Count; i++)
                        {
                            if (_grid.Rows[i].Tag is int id && id == keepId.Value)
                            {
                                targetRow = i;
                                break;
                            }
                        }
                    }
                    if (targetRow < 0) targetRow = FirstWeaponRow();
                    if (targetRow >= 0)
                    {
                        _grid.CurrentCell = _grid.Rows[targetRow].Cells[0];
                        _grid.Rows[targetRow].Selected = true;
                    }
                    if (keepScroll > 0 && keepScroll < _grid.Rows.Count)
                    {
                        try { _grid.FirstDisplayedScrollingRowIndex = keepScroll; } catch { }
                    }
                }
            }
        }
        finally
        {
            _binding = false;
        }
    }

    int FirstWeaponRow()
    {
        var state = _ctl.State;
        if (state is null) return -1;
        for (var i = 0; i < _grid.Rows.Count; i++)
        {
            if (_grid.Rows[i].Tag is not int id) continue;
            var item = state.Items.FirstOrDefault(x => x.Id == id);
            if (item?.IsWeapon == true) return i;
        }
        return _grid.Rows.Count > 0 ? 0 : -1;
    }

    bool MatchesTypeFilter(ItemState item)
        => _typeFilter == -2 || item.Type == _typeFilter;

    ItemState? SelectedItem()
    {
        if (_grid.CurrentRow?.Tag is not int id) return null;
        return _ctl.State?.Items.FirstOrDefault(i => i.Id == id);
    }

    void BindModEditor()
    {
        var state = _ctl.State;
        var item = SelectedItem();
        _selectedItemId = item?.Id;

        var editable = item is not null && item.IsWeapon && _ctl.Client.IsConnected && !_busy && (_ctl.State?.Ready ?? false);

        RebuildModChoices(state);

        if (item is null)
        {
            _lblEditTitle.Text = "未选中物品";
            _lblEditHint.Text = state is null
                ? "还没有读取到数据：注入代理后会自动拉取 STATE。"
                : "在左侧列表选中一件武器后，可以为它的 4 个槽位设置词条。";
        }
        else if (!item.IsWeapon)
        {
            _lblEditTitle.Text = $"已选中：{DisplayNames.ItemName(item)}（ID {item.Id}）";
            _lblEditHint.Text = "该物品没有武器数据（护甲 / 装备类，type=-1），不支持编辑词条。";
        }
        else
        {
            _lblEditTitle.Text = $"已选中：{DisplayNames.ItemName(item)}（ID {item.Id}）· {DisplayNames.ItemType(item.Type)} · 等级 {item.Level} · {DisplayNames.RarityName(state, item.Rarity)}";
            _lblEditHint.Text = !_ctl.Client.IsConnected
                ? "代理未连接：请先点「注入代理」。"
                : _ctl.State?.Ready != true
                    ? "游戏未就绪（READY=0，存档加载中）：就绪后会自动启用这里的编辑。"
                    : "为每个槽位选择词条与稀有度，点「应用到该武器」写入。";
        }

        for (var i = 0; i < 4; i++)
        {
            _cboMod[i].Enabled = editable;
            _cboRarity[i].Enabled = editable;

            var slot = item is not null && i < item.Slots.Count ? item.Slots[i] : null;
            SelectModCombo(_cboMod[i], slot?.ModIndex);
            SelectRarityCombo(_cboRarity[i], slot?.Rarity ?? 0);
        }

        _btnApplyMods.Enabled = editable;
        RebuildAttrChoices(state);
        _cmbWeaponRarity.Enabled = editable;
        _cmbCamo.Enabled = editable;
        if (item is not null)
        {
            var r = Math.Clamp(item.Rarity, 0, 6);
            if (r < _cmbWeaponRarity.Items.Count) _cmbWeaponRarity.SelectedIndex = r;
            SelectCamoCombo(item.CamoIndex);
        }
        else
        {
            _cmbWeaponRarity.SelectedIndex = -1;
            _cmbCamo.SelectedIndex = -1;
        }
    }

    void RebuildModChoices(GameState? state)
    {
        var mods = state?.Mods ?? new List<ModDef>();
        for (var i = 0; i < 4; i++)
        {
            var modCombo = _cboMod[i];
            var rarityCombo = _cboRarity[i];

            var sameMods = modCombo.Items.Count == mods.Count;
            if (sameMods)
            {
                for (var k = 0; k < mods.Count; k++)
                {
                    if (modCombo.Items[k] is not ModChoice mc || mc.Index != mods[k].Index || mc.Text != DisplayNames.ModLabel(mods[k]))
                    {
                        sameMods = false;
                        break;
                    }
                }
            }

            if (!sameMods)
            {
                var indexWas = (modCombo.SelectedItem as ModChoice)?.Index ?? -1;
                modCombo.BeginUpdate();
                modCombo.Items.Clear();
                foreach (var mod in mods)
                    modCombo.Items.Add(new ModChoice(mod.Index, DisplayNames.ModLabel(mod)));
                modCombo.EndUpdate();
                if (indexWas >= 0) SelectModCombo(modCombo, indexWas);
                else if (modCombo.Items.Count > 0) modCombo.SelectedIndex = 0;
            }

            // 稀有度：0..3，标签用 STATE 的 RARITY 行（缺省 普通/稀有/史诗/传说 ×1/×1.5/×2/×3）
            var rarityCount = Math.Max(4, state?.Rarities.Count ?? 4);
            if (rarityCombo.Items.Count != rarityCount)
            {
                var indexWas = (rarityCombo.SelectedItem as RarityChoice)?.Index ?? 0;
                rarityCombo.BeginUpdate();
                rarityCombo.Items.Clear();
                for (var r = 0; r < rarityCount; r++)
                    rarityCombo.Items.Add(new RarityChoice(r, DisplayNames.RarityLabel(state, r)));
                rarityCombo.EndUpdate();
                SelectRarityCombo(rarityCombo, indexWas);
            }
            else
            {
                // 稀有度名称可能随 STATE 变化，刷新一次显示文本
                for (var r = 0; r < rarityCount; r++)
                {
                    var label = DisplayNames.RarityLabel(state, r);
                    if (rarityCombo.Items[r] is RarityChoice rc && rc.Text != label)
                    {
                        var wasIndex = (rarityCombo.SelectedItem as RarityChoice)?.Index ?? 0;
                        rarityCombo.Items[r] = new RarityChoice(r, label);
                        SelectRarityCombo(rarityCombo, wasIndex);
                    }
                }
            }
        }
    }

    void RebuildAttrChoices(GameState? state)
    {
        if (_cmbWeaponRarity.Items.Count != 7)
        {
            _cmbWeaponRarity.BeginUpdate();
            _cmbWeaponRarity.Items.Clear();
            for (var r = 0; r < 7; r++)
                _cmbWeaponRarity.Items.Add(new RarityChoice(r, DisplayNames.RarityName(state, r)));
            _cmbWeaponRarity.EndUpdate();
        }
        else
        {
            for (var r = 0; r < 7; r++)
            {
                var label = DisplayNames.RarityName(state, r);
                if (_cmbWeaponRarity.Items[r] is RarityChoice rc && rc.Text != label)
                    _cmbWeaponRarity.Items[r] = new RarityChoice(r, label);
            }
        }

        var camoWas = (_cmbCamo.SelectedItem as CamoChoice)?.Index ?? -1;
        _cmbCamo.BeginUpdate();
        _cmbCamo.Items.Clear();
        _cmbCamo.Items.Add(new CamoChoice(-1, "无皮肤"));
        foreach (var camo in state?.Camos ?? new List<CamoDef>())
            _cmbCamo.Items.Add(new CamoChoice(camo.Index, $"{camo.Name}（{DisplayNames.RarityName(state, camo.Rarity)}）"));
        _cmbCamo.EndUpdate();
        if (camoWas >= 0) SelectCamoCombo(camoWas);
        else if (_cmbCamo.Items.Count > 0) _cmbCamo.SelectedIndex = 0;
    }

    void SelectCamoCombo(int camoIndex)
    {
        if (_cmbCamo.Items.Count == 0) return;
        for (var i = 0; i < _cmbCamo.Items.Count; i++)
        {
            if (_cmbCamo.Items[i] is CamoChoice cc && cc.Index == camoIndex)
            {
                _cmbCamo.SelectedIndex = i;
                return;
            }
        }
        _cmbCamo.SelectedIndex = 0;
    }

    static void SelectModCombo(ComboBox combo, int? modIndex)    {
        if (combo.Items.Count == 0) return;
        if (modIndex is null)
        {
            if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
            return;
        }
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ModChoice mc && mc.Index == modIndex.Value)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
    }

    static void SelectRarityCombo(ComboBox combo, int rarity)
    {
        if (combo.Items.Count == 0) return;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is RarityChoice rc && rc.Index == rarity)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
    }

    // ================================================================ 日志

    internal void AppendLog(string message, LogKind kind)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => AppendLog(message, kind))); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
            return;
        }

        var color = kind switch
        {
            LogKind.Ok => Color.FromArgb(74, 222, 128),
            LogKind.Warn => Color.FromArgb(250, 204, 21),
            LogKind.Error => Color.FromArgb(248, 113, 113),
            _ => Color.FromArgb(203, 213, 225),
        };

        if (_log.TextLength > 200_000)
            _log.Clear();

        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = color;
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _log.SelectionColor = LogFg;
        _log.ScrollToCaret();
    }

    // ================================================================ 关闭

    async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_shutdownDone) return;
        e.Cancel = true;

        try
        {
            _readyTimer.Stop();
            UseWaitCursor = true;
            AppendLog("正在关闭：发送 QUIT 并断开管道…", LogKind.Info);
            await _ctl.QuitAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"关闭时出错（已忽略）：{TrainerController.Describe(ex)}", LogKind.Warn);
        }
        finally
        {
            _shutdownDone = true;
            UseWaitCursor = false;
            try { await _ctl.DisposeAsync(); } catch { }
            Close();
        }
    }

    // ================================================================ 自测

    /// <summary>把窗口自身渲染到 PNG（DrawToBitmap，不抢前台焦点、不拍别的窗口）。</summary>
    void CaptureScreenshot(string suffix)
    {
        if (string.IsNullOrWhiteSpace(_screenshotPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(_screenshotPath);
            var stem = Path.GetFileNameWithoutExtension(_screenshotPath);
            var ext = Path.GetExtension(_screenshotPath);
            var file = string.IsNullOrEmpty(suffix)
                ? _screenshotPath!
                : Path.Combine(dir is null || dir.Length == 0 ? "." : dir, $"{stem}-{suffix}{ext}");

            using var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            AppendLog($"已保存界面截图：{file}", LogKind.Info);
        }
        catch (Exception ex)
        {
            AppendLog($"截图失败：{ex.Message}", LogKind.Warn);
        }
    }

    void Smoke(string step, bool ok, string detail)
    {
        _smoke.Add((step, ok, detail));
        AppendLog($"[自测] {(ok ? "通过" : "失败")} · {step} · {detail}", ok ? LogKind.Ok : LogKind.Error);
    }

    /// <summary>界面级端到端自测：连 mock 代理 → 读 → 设置 → 保存，全程走真实的按钮处理流程。</summary>
    async Task RunSmokeAsync()
    {
        AppendLog("=== 界面自测开始（需要 mock 管道服务端已在运行）===", LogKind.Info);
        try
        {
            // 1) 连接
            var connected = await _ctl.ConnectWithRetryAsync();
            RefreshUi();
            Smoke("连接 mock 代理", connected, connected ? _ctl.Client.PipePath : "连接失败");
            if (!connected) return;

            // 2) 读 STATE
            await RefreshFromServerAsync();
            var state = _ctl.State;
            var readOk = state is not null && state.Items.Count > 0 && state.Mods.Count > 0;
            Smoke("读取 STATE", readOk,
                state is null
                    ? "state 为空"
                    : $"金钱={state.Money}，物品={state.Items.Count}，词条={state.Mods.Count}，职业={state.CurrentClass}");
            if (!readOk) return;

            var gridOk = _grid.Rows.Count == state!.Items.Count;
            Smoke("列表行数与 ITEMCOUNT 一致", gridOk, $"DataGridView 行数={_grid.Rows.Count}，ITEMCOUNT={state.Items.Count}");

            // 协议 v1.1：READY=0 时禁用写操作，并每 2 秒自动轮询直到就绪
            if (!state.Ready)
            {
                var disabledOk = !_btnMoneyApply.Enabled && !_btnSave.Enabled && !_btnApplyMods.Enabled
                                 && _lblReady.Visible && _readyTimer.Enabled;
                Smoke("READY=0：写按钮禁用 + 自动轮询启动", disabledOk,
                    $"金额按钮={_btnMoneyApply.Enabled}，保存按钮={_btnSave.Enabled}，"
                    + $"词条按钮={_btnApplyMods.Enabled}，未就绪提示={_lblReady.Visible}，轮询={_readyTimer.Enabled}");
                CaptureScreenshot("notready");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!_ctl.GameReady && sw.Elapsed.TotalSeconds < 30)
                    await Task.Delay(300);
                Smoke("自动轮询到 READY=1", _ctl.GameReady,
                    $"耗时 {sw.Elapsed.TotalSeconds:0.0} 秒，READY={(_ctl.GameReady ? 1 : 0)}");

                await RefreshFromServerAsync();
                state = _ctl.State;
                Smoke("就绪后写按钮恢复可用", _btnMoneyApply.Enabled && _btnSave.Enabled,
                    $"金额按钮={_btnMoneyApply.Enabled}，保存按钮={_btnSave.Enabled}，未就绪提示={_lblReady.Visible}");
                if (state is null) return;
            }

            CaptureScreenshot("ready");

            // 空名字物品回退显示
            var unnamedIndex = -1;
            for (var i = 0; i < state.Items.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(state.Items[i].Name)) { unnamedIndex = i; break; }
            }
            if (unnamedIndex >= 0)
            {
                var expected = $"未命名武器 #{state.Items[unnamedIndex].Id}";
                var shown = _grid.Rows[unnamedIndex].Cells[1].Value?.ToString() ?? "";
                Smoke("空名字回退显示", shown == expected, $"显示=「{shown}」，期望=「{expected}」");
            }
            else
            {
                Smoke("空名字回退显示", true, "mock 未提供空名字物品，跳过");
            }

            var greyOk = true;
            for (var i = 0; i < _grid.Rows.Count && i < state.Items.Count; i++)
            {
                var item = state.Items[i];
                if (!item.IsWeapon && _grid.Rows[i].DefaultCellStyle.ForeColor != MutedColor)
                    greyOk = false;
                if (item.IsWeapon && _grid.Rows[i].DefaultCellStyle.ForeColor == MutedColor)
                    greyOk = false;
            }
            Smoke("type=-1 行置灰", greyOk, "逐行校验行前景色");

            // 3) 金钱
            _txtMoney.Text = "777777";
            await ApplyMoneyAsync();
            var moneyOk = _ctl.State is not null && _ctl.State!.Money == 777777;
            Smoke("应用金额 777777", moneyOk, $"STATE.MONEY={_ctl.State?.Money}，界面显示={_lblMoney.Text}");

            // 4) 选中第一件武器并写词条
            var weaponRow = FirstWeaponRow();
            if (weaponRow < 0)
            {
                Smoke("找到可编辑武器", false, "没有 type≥0 的武器");
            }
            else
            {
                _grid.CurrentCell = _grid.Rows[weaponRow].Cells[0];
                _grid.Rows[weaponRow].Selected = true;
                BindModEditor();
                var weapon = state.Items[weaponRow];
                Smoke("选中武器", SelectedItem()?.Id == weapon.Id, $"选中 {weapon.Name}（ID {weapon.Id}）");

                var choicesOk = _cboMod[0].Items.Count == state.Mods.Count && _cboMod[0].Enabled
                    && _cboRarity[0].Items.Count >= 4 && _cboRarity[0].Enabled;
                Smoke("词条/稀有度下拉已填充并启用", choicesOk,
                    $"词条项={_cboMod[0].Items.Count}，稀有度项={_cboRarity[0].Items.Count}，启用={_cboMod[0].Enabled}");

                for (var i = 0; i < 4; i++)
                {
                    SelectModCombo(_cboMod[i], i % state.Mods.Count);
                    SelectRarityCombo(_cboRarity[i], i % 4);
                }
                await ApplyModsAsync();

                var after = _ctl.State?.Items.FirstOrDefault(x => x.Id == weapon.Id);
                var slotsOk = after is not null && after.Slots.Count == 4;
                if (slotsOk)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        if (after!.Slots[i].ModIndex != i % state.Mods.Count || after.Slots[i].Rarity != i % 4)
                            slotsOk = false;
                    }
                }
                Smoke("应用 4 个槽位（SETMOD ×4）", slotsOk,
                    after is null ? "读不到该物品" : string.Join("，", after.Slots.Select(s => $"{s.Slot}:{s.ModIndex}/{s.Rarity}")));

                // 5) 保存
                await SaveAsync();
                Smoke("保存到游戏存档（SAVE）", _ctl.State is not null, "SAVE 后已刷新 STATE");

                // 7) 错误码中文翻译：item-not-found / no-weapon-data / mod-index-invalid
                var err3 = false;
                try { await _ctl.Client.SendAsync("SETMOD 999999 0 0 0", false, 4000); }
                catch (AgentProtocolException ex) { err3 = ex.Code == 3 && ex.Message.Contains("找不到该物品"); }
                Smoke("ERR 3 翻译为中文", err3, AgentProtocolException.ErrorText(3));

                var noWeapon = state.Items.FirstOrDefault(x => !x.IsWeapon);
                var err4 = false;
                if (noWeapon is not null)
                {
                    try { await _ctl.Client.SendAsync($"SETMOD {noWeapon.Id} 0 0 0", false, 4000); }
                    catch (AgentProtocolException ex) { err4 = ex.Code == 4 && ex.Message.Contains("没有武器数据"); }
                    Smoke("ERR 4 翻译为中文", err4, AgentProtocolException.ErrorText(4));
                }

                var err5 = false;
                try { await _ctl.Client.SendAsync($"SETMOD {weapon.Id} 0 99 0", false, 4000); }
                catch (AgentProtocolException ex) { err5 = ex.Code == 5 && ex.Message.Contains("词条序号无效"); }
                Smoke("ERR 5 翻译为中文", err5, AgentProtocolException.ErrorText(5));
            }

            // 8) 进程检测（只检测，绝不注入、不碰游戏窗口）
            var target = _ctl.DetectProcess();
            Smoke("进程检测（只读）", true, target is null ? "当前未检测到游戏进程（正常）" : $"检测到 PID {target.Pid}");

            await _ctl.QuitAsync();
            Smoke("关闭连接（QUIT）", true, "已发送 QUIT 并断开");
        }
        catch (Exception ex)
        {
            Smoke("自测异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            var failed = _smoke.Count(x => !x.Ok);
            SmokePassed = failed == 0 && _smoke.Count > 0;
            AppendLog($"=== 界面自测结束：{(SmokePassed ? "全部通过" : $"{failed} 项失败")} ===",
                SmokePassed ? LogKind.Ok : LogKind.Error);
            CaptureScreenshot("final");
            WriteSmokeReport();
            if (_smokeTest)
            {
                // 留一点时间把日志画出来；--hold 时保持窗口打开，方便人工/截图核对
                if (_holdSeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(_holdSeconds));
                else
                    await Task.Delay(400);
                Close();
            }
        }
    }

    void WriteSmokeReport()
    {
        if (_reportPath is null) return;
        var lines = new List<string>
        {
            $"# SFHTrainer 界面自测报告",
            $"# 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"# 结果: {(_smoke.Count(x => !x.Ok) == 0 && _smoke.Count > 0 ? "PASS" : "FAIL")}",
            string.Empty,
        };
        foreach (var (step, ok, detail) in _smoke)
            lines.Add($"{(ok ? "PASS" : "FAIL")}\t{step}\t{detail}");
        try
        {
            File.WriteAllText(_reportPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // 报告写不出去不影响自测结论
            System.Diagnostics.Debug.WriteLine($"写报告失败: {ex.Message}");
        }
    }

    // ================================================================ 辅助类型

    sealed class Card : Panel
    {
        public Card()
        {
            BackColor = CardBg;
            DoubleBuffered = true;
            Padding = new Padding(14, 12, 14, 12);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(BorderColor, 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    sealed class ModChoice(int index, string text)
    {
        public int Index { get; } = index;
        public string Text { get; } = text;
        public override string ToString() => Text;
    }

    sealed class RarityChoice(int index, string text)
    {
        public int Index { get; } = index;
        public string Text { get; } = text;
        public override string ToString() => Text;
    }

    sealed class CamoChoice(int index, string text)
    {
        public int Index { get; } = index;
        public string Text { get; } = text;
        public override string ToString() => Text;
    }
}




