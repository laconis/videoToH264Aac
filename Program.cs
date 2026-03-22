using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Program
{
    private static readonly string CrashLogPath = Path.Combine(AppContext.BaseDirectory, "crash.log");

    [STAThread]
    static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    CrashLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UI THREAD EXCEPTION{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { }

            MessageBox.Show(
                e.Exception.ToString(),
                "Erreur UI",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    CrashLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] APPDOMAIN EXCEPTION{Environment.NewLine}{e.ExceptionObject}{Environment.NewLine}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    CrashLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TASK EXCEPTION{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { }

            e.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly TextBox txtSource = new() { Width = 560 };
    private readonly TextBox txtOutput = new() { Width = 560 };
    private readonly TextBox txtFfmpeg = new() { Width = 560, Text = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") };
    private readonly TextBox txtFfprobe = new() { Width = 560, Text = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe") };

    private readonly ComboBox cmbMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly NumericUpDown nudCpuWorkers = new() { Minimum = 1, Maximum = 8, Value = 2, Width = 80 };
    private readonly NumericUpDown nudGpuWorkers = new() { Minimum = 0, Maximum = 2, Value = 1, Width = 80 };
    private readonly NumericUpDown nudMinOutputMb = new() { Minimum = 1, Maximum = 10240, Value = 1, Width = 100 };

    private readonly CheckBox chkDeleteSource = new() { Text = "Supprimer la source si sortie validée" };
    private readonly CheckBox chkSkipCompatible = new() { Text = "Skip si déjà H.264 + AAC", Checked = true };
    private readonly CheckBox chkMirrorTree = new() { Text = "Conserver l'arborescence", Checked = true };

    private readonly Button btnStart = new() { Text = "Démarrer", Width = 120, Height = 34 };
    private readonly Button btnStop = new() { Text = "Stop", Width = 120, Height = 34, Enabled = false };
    private readonly Button btnResume = new() { Text = "Reprendre", Width = 120, Height = 34 };

    private readonly Label lblStatus = new() { AutoSize = true, Text = "Prêt." };
    private readonly ProgressBar progressGlobal = new() { Width = 900, Height = 24 };

    private readonly ListView list = new();
    private readonly RichTextBox logBox = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        Font = new Font("Consolas", 9f)
    };

    private readonly FolderBrowserDialog folderDialog = new();
    private readonly OpenFileDialog exeDialog = new()
    {
        Filter = "Executable (*.exe)|*.exe|Tous les fichiers (*.*)|*.*"
    };

    private CancellationTokenSource? cts;
    private Task? runningTask;
    private bool closingRequested;
    private bool uiClosing;

    private readonly string statePath = Path.Combine(AppContext.BaseDirectory, "transcode_state.json");
    private readonly string appLogPath = Path.Combine(AppContext.BaseDirectory, "transcode_ui.log");
    private readonly string defaultsPath = Path.Combine(AppContext.BaseDirectory, "defaults.json");

    private readonly object uiLock = new();
    private readonly Dictionary<string, ListViewItem> listIndex = new(StringComparer.OrdinalIgnoreCase);

    public MainForm()
    {
        Text = "Mini HandBrake maison";
        Width = 1200;
        Height = 860;
        StartPosition = FormStartPosition.CenterScreen;

        cmbMode.Items.AddRange(new object[] { "auto", "cpu", "gpu" });
        cmbMode.SelectedIndex = 0;

        list.View = View.Details;
        list.FullRowSelect = true;
        list.GridLines = true;
        list.Height = 280;
        list.Columns.Add("Fichier", 420);
        list.Columns.Add("Moteur", 80);
        list.Columns.Add("État", 120);
        list.Columns.Add("Progression", 90);
        list.Columns.Add("ETA", 80);
        list.Columns.Add("Entrée", 90);
        list.Columns.Add("Sortie", 90);
        list.Columns.Add("Détails", 300);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            Padding = new Padding(10)
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddRow(top, "Source", txtSource, () => PickFolder(txtSource));
        AddRow(top, "Sortie", txtOutput, () => PickFolder(txtOutput));
        AddRow(top, "ffmpeg.exe", txtFfmpeg, () => PickFile(txtFfmpeg));
        AddRow(top, "ffprobe.exe", txtFfprobe, () => PickFile(txtFfprobe));

        int r = top.RowCount;
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = "Mode", AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
        top.Controls.Add(cmbMode, 1, r);

        var flow1 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        flow1.Controls.Add(new Label { Text = "CPU workers", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        flow1.Controls.Add(nudCpuWorkers);
        flow1.Controls.Add(new Label { Text = "GPU workers", AutoSize = true, Padding = new Padding(16, 8, 0, 0) });
        flow1.Controls.Add(nudGpuWorkers);
        flow1.Controls.Add(new Label { Text = "Taille min sortie (Mo)", AutoSize = true, Padding = new Padding(16, 8, 0, 0) });
        flow1.Controls.Add(nudMinOutputMb);
        top.Controls.Add(flow1, 2, r);
        top.SetColumnSpan(flow1, 2);
        top.RowCount++;

        r = top.RowCount;
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var flow2 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        flow2.Controls.Add(chkDeleteSource);
        flow2.Controls.Add(chkSkipCompatible);
        flow2.Controls.Add(chkMirrorTree);
        top.Controls.Add(flow2, 1, r);
        top.SetColumnSpan(flow2, 3);
        top.RowCount++;

        r = top.RowCount;
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var flow3 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        flow3.Controls.Add(btnStart);
        flow3.Controls.Add(btnStop);
        flow3.Controls.Add(btnResume);
        flow3.Controls.Add(lblStatus);
        top.Controls.Add(flow3, 1, r);
        top.SetColumnSpan(flow3, 3);
        top.RowCount++;

        var mid = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 330
        };
        list.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(list);
        split.Panel2.Controls.Add(logBox);
        mid.Controls.Add(split);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            Padding = new Padding(10, 5, 10, 5)
        };
        bottom.Controls.Add(progressGlobal);

        Controls.Add(mid);
        Controls.Add(bottom);
        Controls.Add(top);

        btnStart.Click += async (_, _) => await StartAsync(resumeExisting: false);
        btnResume.Click += async (_, _) => await StartAsync(resumeExisting: true);
        btnStop.Click += (_, _) =>
        {
            if (cts == null) return;
            btnStop.Enabled = false;
            lblStatus.Text = "Arrêt demandé...";
            cts.Cancel();
        };

        FormClosing += OnMainFormClosing;

        LoadSavedDefaults();
        LoadStatePreview();
    }

    private void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        uiClosing = true;

        if (runningTask != null && !runningTask.IsCompleted)
        {
            e.Cancel = true;
            closingRequested = true;
            lblStatus.Text = "Fermeture en cours, arrêt demandé...";
            btnStop.Enabled = false;
            cts?.Cancel();
        }
    }

    private void AddRow(TableLayoutPanel top, string label, Control control, Action browseAction)
    {
        int r = top.RowCount;
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        top.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 8, 0, 0)
        }, 0, r);

        top.Controls.Add(control, 1, r);

        var btn = new Button { Text = "...", Width = 36, Height = 28 };
        btn.Click += (_, _) => browseAction();
        top.Controls.Add(btn, 2, r);

        top.RowCount++;
    }

    private void PickFolder(TextBox target)
    {
        if (Directory.Exists(target.Text))
            folderDialog.SelectedPath = target.Text;

        if (folderDialog.ShowDialog(this) == DialogResult.OK)
            target.Text = folderDialog.SelectedPath;
    }

    private void PickFile(TextBox target)
    {
        if (File.Exists(target.Text))
            exeDialog.FileName = target.Text;

        if (exeDialog.ShowDialog(this) == DialogResult.OK)
            target.Text = exeDialog.FileName;
    }

    private async Task StartAsync(bool resumeExisting)
    {
        if (cts != null || (runningTask != null && !runningTask.IsCompleted))
            return;

        try
        {
            uiClosing = false;

            var options = ReadOptions();

            if (!File.Exists(options.FfmpegPath))
                throw new FileNotFoundException("ffmpeg.exe introuvable", options.FfmpegPath);

            if (!Directory.Exists(options.SourceDir))
                throw new DirectoryNotFoundException("Dossier source introuvable");

            Directory.CreateDirectory(options.OutputDir);
            Directory.CreateDirectory(Path.Combine(options.SourceDir, "failed"));

            SaveDefaults(options);

            btnStart.Enabled = false;
            btnResume.Enabled = false;
            btnStop.Enabled = true;

            progressGlobal.Value = 0;

            list.BeginUpdate();
            list.Items.Clear();
            list.EndUpdate();
            listIndex.Clear();

            lblStatus.Text = "Initialisation...";

            cts = new CancellationTokenSource();

            Log($"Démarrage. resume={resumeExisting}");

            var engine = new TranscodeEngine(options, statePath, appLogPath, ReportToUi, Log);
            runningTask = Task.Run(() => engine.RunAsync(resumeExisting, cts.Token), cts.Token);

            await runningTask;

            lblStatus.Text = "Terminé.";
        }
        catch (OperationCanceledException)
        {
            lblStatus.Text = "Arrêt demandé.";
            Log("Arrêt demandé par l'utilisateur.");
        }
        catch (Exception ex)
        {
            lblStatus.Text = "Erreur.";
            Log("ERREUR COMPLETE: " + ex);

            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] StartAsync EXCEPTION{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { }

            MessageBox.Show(this, ex.ToString(), "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            btnStart.Enabled = true;
            btnResume.Enabled = true;
            btnStop.Enabled = false;

            cts?.Dispose();
            cts = null;
            runningTask = null;

            if (closingRequested && !IsDisposed)
            {
                closingRequested = false;
                BeginInvoke(new Action(Close));
            }
        }
    }

    private AppOptions ReadOptions() => new()
    {
        SourceDir = txtSource.Text.Trim(),
        OutputDir = txtOutput.Text.Trim(),
        FfmpegPath = txtFfmpeg.Text.Trim(),
        FfprobePath = txtFfprobe.Text.Trim(),
        Mode = cmbMode.SelectedItem?.ToString() ?? "auto",
        CpuWorkers = (int)nudCpuWorkers.Value,
        GpuWorkers = (int)nudGpuWorkers.Value,
        DeleteSourceAfterSuccess = chkDeleteSource.Checked,
        SkipCompatible = chkSkipCompatible.Checked,
        MirrorTree = chkMirrorTree.Checked,
        MinOutputBytes = (long)nudMinOutputMb.Value * 1024 * 1024
    };

    private void ReportToUi(UiReport r)
    {
        if (uiClosing || IsDisposed)
            return;

        if (InvokeRequired)
        {
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke(new Action(() => ReportToUi(r)));
            }
            catch
            {
            }
            return;
        }

        if (uiClosing || IsDisposed)
            return;

        try
        {
            lblStatus.Text = r.GlobalStatus;
            progressGlobal.Value = Math.Max(0, Math.Min(100, r.GlobalPercent));

            list.BeginUpdate();

            foreach (var kv in r.Files)
            {
                string key = kv.Key;
                var state = kv.Value;

                if (!listIndex.TryGetValue(key, out var existing))
                {
                    existing = new ListViewItem(state.DisplayName) { Tag = key };
                    existing.SubItems.Add(state.Engine);
                    existing.SubItems.Add(state.Status);
                    existing.SubItems.Add(state.PercentText);
                    existing.SubItems.Add(state.EtaText);
                    existing.SubItems.Add(state.InputSizeText);
                    existing.SubItems.Add(state.OutputSizeText);
                    existing.SubItems.Add(state.Details);

                    list.Items.Add(existing);
                    listIndex[key] = existing;
                }
                else
                {
                    existing.Text = state.DisplayName;
                    existing.SubItems[1].Text = state.Engine;
                    existing.SubItems[2].Text = state.Status;
                    existing.SubItems[3].Text = state.PercentText;
                    existing.SubItems[4].Text = state.EtaText;
                    existing.SubItems[5].Text = state.InputSizeText;
                    existing.SubItems[6].Text = state.OutputSizeText;
                    existing.SubItems[7].Text = state.Details;
                }
            }

            list.EndUpdate();
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ReportToUi EXCEPTION{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { }
        }
    }

    private void Log(string message)
    {
        if (uiClosing || IsDisposed)
            return;

        if (InvokeRequired)
        {
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke(new Action(() => Log(message)));
            }
            catch
            {
            }
            return;
        }

        if (uiClosing || IsDisposed)
            return;

        try
        {
            lock (uiLock)
            {
                if (logBox.TextLength > 200_000)
                    logBox.Clear();

                logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                logBox.ScrollToCaret();
            }
        }
        catch
        {
        }
    }

    private void SaveDefaults(AppOptions options)
    {
        File.WriteAllText(
            defaultsPath,
            JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8
        );
    }

    private void LoadSavedDefaults()
    {
        if (!File.Exists(defaultsPath))
            return;

        try
        {
            var opt = JsonSerializer.Deserialize<AppOptions>(File.ReadAllText(defaultsPath, Encoding.UTF8));
            if (opt == null) return;

            txtSource.Text = opt.SourceDir;
            txtOutput.Text = opt.OutputDir;
            txtFfmpeg.Text = opt.FfmpegPath;
            txtFfprobe.Text = opt.FfprobePath;

            if (cmbMode.Items.Contains(opt.Mode))
                cmbMode.SelectedItem = opt.Mode;

            nudCpuWorkers.Value = Math.Clamp(opt.CpuWorkers, (int)nudCpuWorkers.Minimum, (int)nudCpuWorkers.Maximum);
            nudGpuWorkers.Value = Math.Clamp(opt.GpuWorkers, (int)nudGpuWorkers.Minimum, (int)nudGpuWorkers.Maximum);

            chkDeleteSource.Checked = opt.DeleteSourceAfterSuccess;
            chkSkipCompatible.Checked = opt.SkipCompatible;
            chkMirrorTree.Checked = opt.MirrorTree;

            long mb = opt.MinOutputBytes / (1024 * 1024);
            nudMinOutputMb.Value = Math.Clamp(mb, (long)nudMinOutputMb.Minimum, (long)nudMinOutputMb.Maximum);
        }
        catch
        {
        }
    }

    private void LoadStatePreview()
    {
        if (!File.Exists(statePath))
            return;

        try
        {
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(statePath, Encoding.UTF8));
            if (state != null)
                lblStatus.Text = $"État trouvé: {state.Items.Count} item(s). Reprise possible.";
        }
        catch
        {
        }
    }
}

internal sealed class TranscodeEngine
{
    private readonly AppOptions options;
    private readonly string statePath;
    private readonly string appLogPath;
    private readonly Action<UiReport> ui;
    private readonly Action<string> log;

    private readonly object stateLock = new();
    private readonly object metricsLock = new();
    private readonly object uiThrottleLock = new();
    private readonly object fileLogLock = new();

    private PersistedState state = new();
    private readonly Dictionary<string, FileUiState> fileUi = new(StringComparer.OrdinalIgnoreCase);

    private DateTime startedAt;
    private int totalCount;
    private int doneCount;
    private long totalInputBytes;
    private long doneInputBytes;
    private double encodedDurationSeconds;
    private bool nvencAvailable;
    private DateTime lastUiPush = DateTime.MinValue;

    public TranscodeEngine(AppOptions options, string statePath, string appLogPath, Action<UiReport> ui, Action<string> log)
    {
        this.options = options;
        this.statePath = statePath;
        this.appLogPath = appLogPath;
        this.ui = ui;
        this.log = log;
    }

    public async Task RunAsync(bool resumeExisting, CancellationToken ct)
    {
        startedAt = DateTime.Now;
        nvencAvailable = options.Mode != "cpu" && Tooling.CheckEncoderAvailable(options.FfmpegPath, "h264_nvenc");
        WriteAppLog($"NVENC dispo: {nvencAvailable}");

        state = resumeExisting && File.Exists(statePath)
            ? JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(statePath, Encoding.UTF8)) ?? new PersistedState()
            : new PersistedState();

        if (!resumeExisting)
            state = new PersistedState();

        WriteAppLog("Scan des fichiers...");
        var all = ScanFiles();

        totalCount = all.Count;
        totalInputBytes = all.Sum(x => x.SizeBytes);
        doneCount = all.Count(x => x.State == JobState.Done || x.State == JobState.Skipped);
        doneInputBytes = all.Where(x => x.State == JobState.Done || x.State == JobState.Skipped).Sum(x => x.SizeBytes);
        encodedDurationSeconds = all.Where(x => x.State == JobState.Done || x.State == JobState.Skipped).Sum(x => x.DurationSeconds);

        foreach (var f in all)
        {
            fileUi[f.SourcePath] = new FileUiState
            {
                DisplayName = Path.GetFileName(f.SourcePath),
                Engine = f.AssignedEngine ?? "-",
                Status = f.State.ToString(),
                PercentText = f.State is JobState.Done or JobState.Skipped ? "100%" : "0%",
                EtaText = "--",
                InputSizeText = Tooling.FormatBytes(f.SizeBytes),
                OutputSizeText = f.OutputPath != null && File.Exists(f.OutputPath)
                    ? Tooling.FormatBytes(new FileInfo(f.OutputPath).Length)
                    : "--",
                Details = f.Note ?? ""
            };
        }

        PushUi(force: true);

        var pending = all.Where(x => x.State is JobState.Pending or JobState.Failed or JobState.InProgress).ToList();

        foreach (var item in pending)
            item.State = JobState.Pending;

        SaveState();

        var cpuQueue = new ConcurrentQueue<JobItem>();
        var gpuQueue = new ConcurrentQueue<JobItem>();

        foreach (var item in pending)
        {
            bool preferGpu = options.Mode switch
            {
                "gpu" => nvencAvailable,
                "cpu" => false,
                _ => nvencAvailable
            };

            if (preferGpu && options.GpuWorkers > 0)
                gpuQueue.Enqueue(item);
            else
                cpuQueue.Enqueue(item);
        }

        var tasks = new List<Task>();

        for (int i = 0; i < options.CpuWorkers; i++)
        {
            int workerId = i + 1;
            tasks.Add(Task.Run(() => WorkerLoop(cpuQueue, gpuQueue, gpu: false, workerId, ct), ct));
        }

        for (int i = 0; i < options.GpuWorkers; i++)
        {
            int workerId = i + 1;
            tasks.Add(Task.Run(() => WorkerLoop(gpuQueue, cpuQueue, gpu: true, workerId, ct), ct));
        }

        await Task.WhenAll(tasks);

        SaveState();
        PushUi(force: true, final: true);
    }

    private List<JobItem> ScanFiles()
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".mpeg", ".mpg"
        };

        Directory.CreateDirectory(options.OutputDir);
        string failedDir = Path.Combine(options.SourceDir, "failed");

        var sourceFiles = Directory.EnumerateFiles(options.SourceDir, "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .Where(f => !Tooling.IsInsideDirectory(f, options.OutputDir))
            .Where(f => !Tooling.IsInsideDirectory(f, failedDir))
            .ToList();

        var result = new List<JobItem>();
        int scanned = 0;

        foreach (var path in sourceFiles)
        {
            scanned++;
            if (scanned % 25 == 0)
                WriteAppLog($"Scan: {scanned}/{sourceFiles.Count}");

            string relative = Path.GetRelativePath(options.SourceDir, path);

            var item = state.Items.FirstOrDefault(x => x.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                ?? new JobItem { SourcePath = path, RelativePath = relative };

            item.RelativePath = relative;
            item.SizeBytes = new FileInfo(path).Length;
            item.DurationSeconds = Tooling.GetDurationSeconds(options.FfprobePath, path);
            item.OutputPath = BuildOutputPath(path);
            item.OutputDir = Path.GetDirectoryName(item.OutputPath)!;
            Directory.CreateDirectory(item.OutputDir);

            if (options.SkipCompatible)
            {
                var codecs = Tooling.ProbeCodecs(options.FfprobePath, path);
                if (codecs.Video.Contains("h264", StringComparison.OrdinalIgnoreCase) &&
                    codecs.Audio.Contains("aac", StringComparison.OrdinalIgnoreCase))
                {
                    item.State = JobState.Skipped;
                    item.Note = "Déjà H.264 + AAC";
                    item.AssignedEngine = "SKIP";
                    result.Add(item);
                    continue;
                }
            }

            if ((item.State == JobState.Done || item.State == JobState.Skipped) &&
                Tooling.ValidateOutput(options, path, item.OutputPath, log, quickOnly: false))
            {
                result.Add(item);
                continue;
            }

            item.State = JobState.Pending;
            item.Note = "En attente";
            item.AssignedEngine = null;
            result.Add(item);
        }

        state.Items = result;
        SaveState();

        return result;
    }

    private string BuildOutputPath(string input)
    {
        string relative = options.MirrorTree
            ? Path.GetRelativePath(options.SourceDir, input)
            : Path.GetFileName(input);

        string withoutExt = Path.ChangeExtension(relative, null) ?? Path.GetFileNameWithoutExtension(relative);
        return Path.Combine(options.OutputDir, withoutExt + "_h264_aac.mp4");
    }

    private async Task WorkerLoop(
        ConcurrentQueue<JobItem> primary,
        ConcurrentQueue<JobItem> fallback,
        bool gpu,
        int workerId,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!primary.TryDequeue(out var item))
            {
                if (!gpu && fallback.TryDequeue(out var moved))
                {
                    item = moved;
                }
                else
                {
                    break;
                }
            }

            ct.ThrowIfCancellationRequested();

            if (item.State == JobState.Done || item.State == JobState.Skipped)
                continue;

            item.State = JobState.InProgress;
            item.AssignedEngine = gpu ? $"GPU#{workerId}" : $"CPU#{workerId}";
            item.Note = "Encodage";

            SaveState();

            UpdateFileUi(item.SourcePath, s =>
            {
                s.Engine = item.AssignedEngine!;
                s.Status = "InProgress";
                s.Details = "Encodage";
            });

            PushUi(force: true);

            bool usedGpu = gpu && nvencAvailable;
            bool ok = false;

            try
            {
                ok = await ConvertOneAsync(item, usedGpu, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ok = false;
                WriteAppLog($"Erreur conversion {item.RelativePath}: {ex}");
            }

            if (!ok && usedGpu && !ct.IsCancellationRequested)
            {
                WriteAppLog($"Fallback CPU pour {item.RelativePath}");

                item.AssignedEngine = "CPU*";
                UpdateFileUi(item.SourcePath, s =>
                {
                    s.Engine = item.AssignedEngine;
                    s.Details = "Fallback CPU";
                });

                SaveState();

                try
                {
                    ok = await ConvertOneAsync(item, useGpu: false, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ok = false;
                    WriteAppLog($"Erreur fallback CPU {item.RelativePath}: {ex}");
                }
            }

            ct.ThrowIfCancellationRequested();

            if (ok)
            {
                item.State = JobState.Done;
                item.Note = "OK";

                lock (metricsLock)
                {
                    doneCount++;
                    doneInputBytes += item.SizeBytes;
                    encodedDurationSeconds += item.DurationSeconds;
                }

                if (options.DeleteSourceAfterSuccess)
                {
                    if (Tooling.ValidateOutput(options, item.SourcePath, item.OutputPath, log, quickOnly: false))
                    {
                        try
                        {
                            File.Delete(item.SourcePath);
                            item.Note = "OK + source supprimée";
                        }
                        catch (Exception ex)
                        {
                            item.Note = "OK mais suppression source impossible: " + ex.Message;
                        }
                    }
                    else
                    {
                        item.Note = "Sortie douteuse, source conservée";
                    }
                }
            }
            else
            {
                item.State = JobState.Failed;
                item.Note = "Échec";

                string failedDir = Path.Combine(options.SourceDir, "failed");
                Directory.CreateDirectory(failedDir);

                string target = Tooling.GetUniquePath(Path.Combine(failedDir, Path.GetFileName(item.SourcePath)));

                try
                {
                    File.Move(item.SourcePath, target);
                    item.Note = "Déplacé vers failed";
                }
                catch (Exception ex)
                {
                    item.Note = "Échec + move failed impossible: " + ex.Message;
                }
            }

            SaveState();

            UpdateFileUi(item.SourcePath, s =>
            {
                s.Engine = item.AssignedEngine ?? "-";
                s.Status = item.State.ToString();
                s.PercentText = item.State is JobState.Done or JobState.Skipped ? "100%" : s.PercentText;
                s.EtaText = "--";
                s.OutputSizeText = File.Exists(item.OutputPath)
                    ? Tooling.FormatBytes(new FileInfo(item.OutputPath).Length)
                    : "--";
                s.Details = item.Note ?? "";
            });

            PushUi(force: true);
        }
    }

    private async Task<bool> ConvertOneAsync(JobItem item, bool useGpu, CancellationToken ct)
    {
        if (File.Exists(item.OutputPath))
        {
            try { File.Delete(item.OutputPath); } catch { }
        }

        string videoArgs = useGpu
            ? "-c:v h264_nvenc -preset p5 -cq 23 -b:v 0"
            : "-c:v libx264 -preset medium -crf 23";

        string audioArgs = "-c:a aac -b:a 192k";

        string args =
            $"-y -hide_banner -i \"{item.SourcePath}\" " +
            $"{videoArgs} {audioArgs} -pix_fmt yuv420p -movflags +faststart " +
            $"\"{item.OutputPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = options.FfmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        process.ErrorDataReceived += (_, e) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    ParseProgress(item, useGpu, e.Data);
            }
            catch
            {
            }
        };

        process.OutputDataReceived += (_, _) =>
        {
        };

        if (!process.Start())
            return false;

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        using var reg = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        });

        await process.WaitForExitAsync();

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        if (process.ExitCode != 0)
            return false;

        return Tooling.ValidateOutput(options, item.SourcePath, item.OutputPath, log, quickOnly: false);
    }

    private void ParseProgress(JobItem item, bool useGpu, string line)
    {
        if (!line.Contains("time="))
            return;

        string timeStr = Tooling.ExtractValue(line, @"time=(\d{2}:\d{2}:\d{2}(?:\.\d+)?)");
        string speedStr = Tooling.ExtractValue(line, @"speed=\s*([0-9\.]+x)");

        double current = Tooling.ParseFfmpegTimeToSeconds(timeStr);
        double pct = item.DurationSeconds > 0
            ? Math.Min(100.0, current / item.DurationSeconds * 100.0)
            : 0.0;

        TimeSpan eta = Tooling.EstimateEta(current, item.DurationSeconds);

        bool shouldPush = false;

        UpdateFileUi(item.SourcePath, s =>
        {
            s.Engine = useGpu ? "GPU" : "CPU";
            s.Status = "InProgress";
            s.PercentText = pct.ToString("F1", CultureInfo.InvariantCulture) + "%";
            s.EtaText = Tooling.FormatTs(eta);
            s.Details = $"time={timeStr} speed={speedStr}";

            if (File.Exists(item.OutputPath))
                s.OutputSizeText = Tooling.FormatBytes(new FileInfo(item.OutputPath).Length);

            var now = DateTime.UtcNow;
            if ((now - s.LastProgressUiUpdateUtc).TotalMilliseconds >= 1000)
            {
                s.LastProgressUiUpdateUtc = now;
                shouldPush = true;
            }
        });

        if (shouldPush)
            PushUi();
    }

    private void UpdateFileUi(string key, Action<FileUiState> update)
    {
        lock (fileUi)
        {
            if (!fileUi.TryGetValue(key, out var s))
            {
                s = new FileUiState { DisplayName = Path.GetFileName(key) };
                fileUi[key] = s;
            }

            update(s);
        }
    }

    private void PushUi(bool force = false, bool final = false)
    {
        lock (uiThrottleLock)
        {
            if (!force && !final)
            {
                var now = DateTime.UtcNow;
                if ((now - lastUiPush).TotalMilliseconds < 500)
                    return;

                lastUiPush = now;
            }
        }

        Dictionary<string, FileUiState> snapshot;
        lock (fileUi)
        {
            snapshot = fileUi.ToDictionary(k => k.Key, v => v.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }

        string status;
        int percent;

        lock (metricsLock)
        {
            percent = totalCount == 0
                ? 0
                : (int)Math.Min(100, Math.Round(doneCount * 100.0 / totalCount));

            var elapsed = DateTime.Now - startedAt;

            double avgSecPerSourceSec = encodedDurationSeconds > 0
                ? elapsed.TotalSeconds / Math.Max(1.0, encodedDurationSeconds)
                : 0.0;

            double remainSourceSec = Math.Max(
                0,
                state.Items.Where(x => x.State is JobState.Pending or JobState.InProgress or JobState.Failed)
                    .Sum(x => x.DurationSeconds)
            );

            int workers = Math.Max(1, options.CpuWorkers + options.GpuWorkers);

            var eta = avgSecPerSourceSec > 0
                ? TimeSpan.FromSeconds(remainSourceSec * avgSecPerSourceSec / workers)
                : TimeSpan.Zero;

            status = final
                ? $"Fini | {doneCount}/{totalCount} | temps {Tooling.FormatTs(elapsed)}"
                : $"{doneCount}/{totalCount} traités | entrée {Tooling.FormatBytes(doneInputBytes)}/{Tooling.FormatBytes(totalInputBytes)} | ETA {Tooling.FormatTs(eta)}";
        }

        ui(new UiReport
        {
            GlobalPercent = percent,
            GlobalStatus = status,
            Files = snapshot
        });
    }

    private void SaveState()
    {
        lock (stateLock)
        {
            File.WriteAllText(
                statePath,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8
            );
        }
    }

    private void WriteAppLog(string message)
    {
        try
        {
            lock (fileLogLock)
            {
                File.AppendAllText(
                    appLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                    Encoding.UTF8
                );
            }
        }
        catch
        {
        }

        log(message);
    }
}

internal static class Tooling
{
    public static bool CheckEncoderAvailable(string ffmpegPath, string encoderName)
    {
        if (!File.Exists(ffmpegPath))
            return false;

        string output = RunProcessCapture(ffmpegPath, "-hide_banner -encoders");
        return output.Contains(encoderName, StringComparison.OrdinalIgnoreCase);
    }

    public static (string Video, string Audio) ProbeCodecs(string ffprobePath, string file)
    {
        if (!File.Exists(ffprobePath))
            return ("", "");

        try
        {
            string video = RunProcessCapture(
                ffprobePath,
                $"-v error -select_streams v:0 -show_entries stream=codec_name -of default=nw=1:nk=1 \"{file}\""
            ).Trim();

            string audio = RunProcessCapture(
                ffprobePath,
                $"-v error -select_streams a:0 -show_entries stream=codec_name -of default=nw=1:nk=1 \"{file}\""
            ).Trim();

            return (video, audio);
        }
        catch
        {
            return ("", "");
        }
    }

    public static double GetDurationSeconds(string ffprobePath, string file)
    {
        if (!File.Exists(ffprobePath))
            return 0;

        try
        {
            string output = RunProcessCapture(
                ffprobePath,
                $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{file}\""
            ).Trim();

            if (double.TryParse(output, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                return d;
        }
        catch
        {
        }

        return 0;
    }

    public static bool ValidateOutput(AppOptions options, string input, string output, Action<string> log, bool quickOnly)
    {
        if (!File.Exists(output))
        {
            log($"Validation KO: sortie absente: {output}");
            return false;
        }

        var fi = new FileInfo(output);

        if (fi.Length <= 0 || fi.Length < options.MinOutputBytes)
        {
            log($"Validation KO: taille sortie trop petite: {fi.Length} bytes");
            return false;
        }

        bool opened = false;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                using var fs = File.Open(output, FileMode.Open, FileAccess.Read, FileShare.None);
                opened = true;
                break;
            }
            catch
            {
                Thread.Sleep(300);
            }
        }

        if (!opened)
        {
            log("Validation KO: sortie encore verrouillée ou illisible");
            return false;
        }

        if (quickOnly)
            return true;

        var codecs = ProbeCodecs(options.FfprobePath, output);

        if (!codecs.Video.Contains("h264", StringComparison.OrdinalIgnoreCase))
        {
            log("Validation KO: codec vidéo non h264");
            return false;
        }

        if (!codecs.Audio.Contains("aac", StringComparison.OrdinalIgnoreCase))
        {
            log("Validation KO: codec audio non aac");
            return false;
        }

        double inDur = GetDurationSeconds(options.FfprobePath, input);
        double outDur = GetDurationSeconds(options.FfprobePath, output);

        if (inDur > 0 && outDur > 0)
        {
            double ratio = outDur / inDur;
            if (ratio < 0.90 || ratio > 1.10)
            {
                log($"Validation KO: durée incohérente, ratio={ratio:F2}");
                return false;
            }
        }

        return true;
    }

    public static string RunProcessCapture(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }

    public static string ExtractValue(string input, string pattern)
    {
        var m = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "?";
    }

    public static double ParseFfmpegTimeToSeconds(string time)
    {
        if (string.IsNullOrWhiteSpace(time) || time == "?")
            return 0;

        if (TimeSpan.TryParse(time, CultureInfo.InvariantCulture, out var ts))
            return ts.TotalSeconds;

        return 0;
    }

    public static TimeSpan EstimateEta(double currentSec, double totalSec)
    {
        if (currentSec <= 0 || totalSec <= 0)
            return TimeSpan.Zero;

        double remain = totalSec - currentSec;
        if (remain <= 0)
            return TimeSpan.Zero;

        return TimeSpan.FromSeconds(remain);
    }

    public static string FormatTs(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";

        return $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    public static bool IsInsideDirectory(string filePath, string parentDirectory)
    {
        string fileFull = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string dirFull = Path.GetFullPath(parentDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fileFull.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || fileFull.Equals(dirFull, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetUniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        int i = 1;
        while (true)
        {
            string p = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(p))
                return p;
            i++;
        }
    }

    public static string FormatBytes(long value)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        double n = value;
        int i = 0;

        while (n >= 1024 && i < suffix.Length - 1)
        {
            n /= 1024;
            i++;
        }

        return n.ToString("0.##", CultureInfo.InvariantCulture) + " " + suffix[i];
    }
}

internal sealed class AppOptions
{
    public string SourceDir { get; set; } = "";
    public string OutputDir { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public string Mode { get; set; } = "auto";
    public int CpuWorkers { get; set; } = 2;
    public int GpuWorkers { get; set; } = 1;
    public bool DeleteSourceAfterSuccess { get; set; }
    public bool SkipCompatible { get; set; } = true;
    public bool MirrorTree { get; set; } = true;
    public long MinOutputBytes { get; set; } = 1024 * 1024;
}

internal enum JobState
{
    Pending,
    InProgress,
    Done,
    Failed,
    Skipped
}

internal sealed class JobItem
{
    public string SourcePath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string OutputDir { get; set; } = "";
    public JobState State { get; set; } = JobState.Pending;
    public string? Note { get; set; }
    public string? AssignedEngine { get; set; }
    public long SizeBytes { get; set; }
    public double DurationSeconds { get; set; }
}

internal sealed class PersistedState
{
    public List<JobItem> Items { get; set; } = new();
}

internal sealed class UiReport
{
    public int GlobalPercent { get; set; }
    public string GlobalStatus { get; set; } = "";
    public Dictionary<string, FileUiState> Files { get; set; } = new();
}

internal sealed class FileUiState
{
    public string DisplayName { get; set; } = "";
    public string Engine { get; set; } = "";
    public string Status { get; set; } = "";
    public string PercentText { get; set; } = "";
    public string EtaText { get; set; } = "";
    public string InputSizeText { get; set; } = "";
    public string OutputSizeText { get; set; } = "";
    public string Details { get; set; } = "";
    public DateTime LastProgressUiUpdateUtc { get; set; }

    public FileUiState Clone() => (FileUiState)MemberwiseClone();
}
