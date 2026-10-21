using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NovelGrabberWin;

public class MainForm : Form
{
    private WebView2 webView = null!;
    private TextBox txtAddress = null!;
    private TextBox txtBookName = null!;
    private Button btnBack = null!, btnForward = null!, btnGo = null!, btnStar = null!;
    private Button btnGrab = null!, btnBook = null!, btnBookmarks = null!, btnOpenDir = null!;
    private Button btnPause = null!, btnTheme = null!, btnFinish = null!, btnLog = null!, btnPickDir = null!;
    private System.Windows.Forms.Timer uiTimer = null!;

    private bool grabbing;          // 单章模式
    private bool bookGrabbing;      // 整本模式
    private bool stopRequested;
    private bool paused;
    private bool dark;

    private string pageTitle = "未命名章节";
    private readonly List<string> pageList = new();          // 单章模式各节
    private readonly HashSet<string> visitedUrls = new();

    private readonly List<(string title, string url)> bookChapters = new();
    private int bookIdx;
    private readonly List<string> chapterPageList = new();   // 整本模式当前章各节
    private readonly HashSet<string> chapterVisited = new();
    private DateTime chapterStart;

    private readonly List<(string title, string url)> bookmarks = new();
    private readonly List<string> logs = new();
    private string saveDir = "";
    private string bookUriName = "";

    private static readonly string CfgPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovelGrabber", "config.json");

    // JS：提取正文（与安卓版一致）
    private const string JsGrab = """
        (function() {
            var selectors = ['#content','#chaptercontent','#BookText','#htmlContent','#txtContent',
                '#articlecontent','#nr1','#nr','#chapter','.content','.readcontent','.read-content',
                '.showtxt','.novel-content','.article-content','.txtnav'];
            for (var i = 0; i < selectors.length; i++) {
                var el = document.querySelector(selectors[i]);
                if (el) { var t = el.innerText.trim(); if (t.length > 150) return t; }
            }
            var ps = document.querySelectorAll('p'); var buf = '';
            for (var j = 0; j < ps.length; j++) { var t = ps[j].innerText.trim(); if (t.length > 20) buf += t + '\n'; }
            if (buf.length > 150) return buf; return '';
        })();
        """;

    // JS：查找章内“下一页”
    private const string JsNextPage = """
        (function() {
            var links = document.querySelectorAll('a');
            var here = location.href.split('#')[0];
            for (var i = 0; i < links.length; i++) {
                var a = links[i];
                var t = (a.innerText || a.textContent || '').replace(/\s+/g, '');
                var href = a.getAttribute('href') || '';
                if (!href || href.indexOf('javascript') === 0) continue;
                if (t.indexOf('下一页') < 0 && t.indexOf('下页') < 0 && t.toLowerCase() !== 'next') continue;
                if (t.indexOf('下一章') >= 0 || t.indexOf('下章') >= 0 || t.indexOf('下一节') >= 0) continue;
                var full = a.href;
                if (!full || full.split('#')[0] === here) continue;
                return full;
            }
            return '';
        })();
        """;

    // JS：目录页解析章节列表
    private const string JsBookChapters = """
        (function() {
            var seen = {}, out = [];
            var links = document.querySelectorAll('a');
            for (var i = 0; i < links.length; i++) {
                var a = links[i]; var t = (a.innerText || '').trim(); var href = a.href || '';
                if (!href || href.indexOf('http') !== 0) continue;
                if (t.length < 2 || t.length > 60) continue;
                if (!(/^(第.{1,12}(章|节|回|卷))/.test(t) || /^\d{1,4}(\.\d+)?$/.test(t) || /^Chapter/i.test(t))) continue;
                if (seen[href]) continue; seen[href] = 1;
                out.push({t: t, u: href});
                if (out.length >= 9999) break;
            }
            return JSON.stringify(out);
        })();
        """;

    private static readonly Regex AdLine = new("广告|章节错误|最新章节|请记住本书|首发域名|app阅读|点击下一页|继续阅读",
        RegexOptions.Compiled);

    public MainForm()
    {
        Text = "小说爬取 v2.7";
        Size = new Size(1100, 780);
        StartPosition = FormStartPosition.CenterScreen;

        LoadConfig();
        dark = Cfg.GetBool("night");
        uiTimer = new System.Windows.Forms.Timer { Interval = 500 };
        uiTimer.Tick += (s, e) => TimeoutWatch();
        uiTimer.Start();

        InitUi();
        ApplyTheme();
        InitWebView();
    }

    private void InitUi()
    {
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 96, FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8), WrapContents = false
        };
        btnBack = MkBtn("◀", 36); btnForward = MkBtn("▶", 36); btnGo = MkBtn("前往", 60);
        btnStar = MkBtn("★ 收藏", 64);
        txtAddress = new TextBox { Width = 430, Anchor = AnchorStyles.Left };
        txtAddress.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Go(); e.SuppressKeyPress = true; } };
        txtBookName = new TextBox { Width = 180 };
        txtBookName.PlaceholderText = "📖 书名（TXT 文件名）";
        btnPickDir = MkBtn("📁 目录", 64);
        btnPickDir.Click += (s, e) => PickDir();
        top.Controls.AddRange(new Control[] { btnBack, btnForward, txtAddress, btnGo, btnStar, txtBookName, btnPickDir });

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 96, FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8), WrapContents = false
        };
        btnGrab = MkBtn("⚡ 抓取整章", 110); btnGrab.Click += (s, e) => StartGrab();
        btnBook = MkBtn("📚 一键整本", 110); btnBook.Click += (s, e) => BookButton();
        btnBookmarks = MkBtn("📂 收藏夹", 88); btnBookmarks.Click += (s, e) => ShowBookmarks();
        btnOpenDir = MkBtn("📁 小说夹", 88); btnOpenDir.Click += (s, e) => OpenNovelDir();
        btnPause = MkBtn("⏸ 暂停", 76); btnPause.Click += (s, e) => TogglePause();
        btnTheme = MkBtn("🌙 主题", 76); btnTheme.Click += (s, e) => ToggleTheme();
        btnLog = MkBtn("📋 日志", 76); btnLog.Click += (s, e) => ShowLogs();
        btnFinish = MkBtn("💾 打开TXT", 96); btnFinish.Click += (s, e) => OpenTxt();
        bottom.Controls.AddRange(new Control[]
            { btnGrab, btnBook, btnBookmarks, btnOpenDir, btnPause, btnTheme, btnLog, btnFinish });

        webView = new WebView2 { Dock = DockStyle.Fill };
        webView.NavigationCompleted += WebView_NavigationCompleted;
        var placeholder = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Text = "WebView2 初始化中…（若长期停留请安装 WebView2 Runtime）"
        };
        webView.CoreWebView2InitializationCompleted += (s, e) =>
        {
            placeholder.Visible = false;
            if (webView.CoreWebView2 != null)
                webView.CoreWebView2.Settings.UserAgent =
                    webView.CoreWebView2.Settings.UserAgent.Replace("; wv", "");
        };

        Controls.Add(webView);
        Controls.Add(placeholder);
        Controls.Add(bottom);
        Controls.Add(top);
    }

    private static Button MkBtn(string text, int width) => new()
    {
        Text = text, Width = width, Height = 34, Margin = new Padding(4, 26, 4, 4)
    };

    private async void InitWebView()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync();
            await webView.EnsureCoreWebView2Async(env);
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Navigate(Cfg.Get("home", "https://www.qidian.com/"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "WebView2 初始化失败：" + ex.Message +
                "\n\n请在系统安装 Microsoft Edge WebView2 Runtime（Win7 请使用 109 及以下版本运行时）。",
                "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Navigate(string url)
    {
        if (!url.StartsWith("http://") && !url.StartsWith("https://")) url = "https://" + url;
        webView.Source = new Uri(url);
    }

    private void Go() { if (txtAddress.Text.Trim().Length > 0) Navigate(txtAddress.Text.Trim()); }

    // ================= 超时保护（60000 秒） =================
    private static readonly double TimeoutSec = 60000;
    private DateTime lastAction = DateTime.Now;

    private void Touch() => lastAction = DateTime.Now;

    private void TimeoutWatch()
    {
        if (!(grabbing || bookGrabbing) || paused) return;
        if ((DateTime.Now - lastAction).TotalSeconds < TimeoutSec) return;
        if (bookGrabbing)
        {
            var title = bookIdx < bookChapters.Count ? bookChapters[bookIdx].title : "?";
            AddLog($"整本超时跳过章节：《{BookName()}》{title}", true);
            Status("⏱️ 加载超时（60000s），跳过：" + title);
            OnBookChapterDone(title);
        }
        else if (grabbing)
        {
            AddLog($"单章抓取超时收尾：{pageTitle} · {webView.Source}", true);
            Status("⏱️ 等待超时（60000s），按已抓取内容收尾");
            FinalizeChapter();
        }
        Touch();
    }

    // ================= 单章抓取 =================
    private async void StartGrab()
    {
        if (bookGrabbing) { MessageBox.Show("整本下载进行中"); return; }
        if (grabbing) return;
        grabbing = true; stopRequested = false; paused = false;
        pageList.Clear(); visitedUrls.Clear();
        visitedUrls.Add(webView.Source.ToString().Split('#')[0]);
        pageTitle = webView.CoreWebView2?.DocumentTitle ?? "未命名章节";
        Status("🔍 正在抓取：" + pageTitle);
        Touch();
        await ExtractChapterPage();
    }

    private async Task ExtractChapterPage()
    {
        string text = "";
        try { text = await webView.CoreWebView2.ExecuteScriptAsync(JsGrab); } catch { }
        text = Unwrap(text);
        if (!string.IsNullOrEmpty(text)) pageList.Add(text);
        await FindNextChapterPage();
    }

    private async Task FindNextChapterPage()
    {
        if (stopRequested) { FinalizeChapter(); return; }
        if (paused) { await Task.Delay(400); await FindNextChapterPage(); return; }
        string next = "";
        try { next = Unwrap(await webView.CoreWebView2.ExecuteScriptAsync(JsNextPage)); } catch { }
        var key = next.Split('#')[0];
        if (next.Length == 0 || visitedUrls.Contains(key)) { FinalizeChapter(); return; }
        visitedUrls.Add(key);
        Status("➡️ 自动翻页 " + (visitedUrls.Count - 1) + " …");
        Touch();
        await webView.CoreWebView2.ExecuteScriptAsync($"location.href='{next.Replace("'", "\\'")}'");
    }

    private void FinalizeChapter()
    {
        if (!grabbing) return;
        grabbing = false; stopRequested = false;
        if (pageList.Count == 0)
        {
            Status("❌ 未识别到正文，本站结构不兼容");
            MessageBox.Show("未识别到正文，请尝试其他章节或书源");
            return;
        }
        var raw = pageList.Count > 1
            ? string.Join("\n\n", pageList.Select((p, i) => $"──── {pageTitle} · 第 {i + 1} 节 ────\n\n{p}"))
            : pageList[0];
        var content = Clean(raw, pageTitle);
        Status($"💾 正在保存…（共 {pageList.Count} 页）");
        AppendTxt(content);
        Status($"✅ 整章抓取完成：{pageTitle}（{pageList.Count} 页）");
    }

    // ================= 整本抓取 =================
    private async void BookButton()
    {
        if (bookGrabbing) { stopRequested = true; Status("⏹ 正在停止，完成当前章节后结束…"); return; }
        if (grabbing) { MessageBox.Show("请先等待当前章节抓取完成"); return; }

        string json = "";
        try { json = await webView.CoreWebView2.ExecuteScriptAsync(JsBookChapters); } catch { }
        bookChapters.Clear();
        try
        {
            using var doc = JsonDocument.Parse(Unwrap(json));
            foreach (var el in doc.RootElement.EnumerateArray())
                bookChapters.Add((el.GetProperty("t").GetString() ?? "", el.GetProperty("u").GetString() ?? ""));
        }
        catch { }
        if (bookChapters.Count == 0)
        {
            MessageBox.Show("未在当前页面识别到章节列表，请先打开小说目录页再点「一键整本」");
            return;
        }
        bookGrabbing = true; stopRequested = false; paused = false;
        btnBook.Text = "⏹ 停止";
        Status($"📚 识别到 {bookChapters.Count} 章，开始整本下载");
        LoadChapter(0);
    }

    private async void LoadChapter(int idx)
    {
        if (stopRequested || idx >= bookChapters.Count) { FinishBook(Math.Min(idx, bookChapters.Count)); return; }
        bookIdx = idx;
        var (title, url) = bookChapters[idx];
        chapterPageList.Clear(); chapterVisited.Clear();
        chapterVisited.Add(url.Split('#')[0]);
        Status($"📚 整本进度 第 {idx + 1}/{bookChapters.Count} 章：{title}");
        Touch();
        await webView.CoreWebView2.ExecuteScriptAsync($"location.href='{url.Replace("'", "\\'")}'");
    }

    private async void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        txtAddress.Text = webView.Source?.ToString() ?? "";
        if (!bookGrabbing && !grabbing) return;
        if (paused) return;
        await Task.Delay(600);
        if (bookGrabbing) await ExtractBookPage();
        else if (grabbing) { Touch(); await ExtractChapterPage(); }
    }

    private async Task ExtractBookPage()
    {
        if (paused) { await Task.Delay(400); await ExtractBookPage(); return; }
        string text = "";
        try { text = await webView.CoreWebView2.ExecuteScriptAsync(JsGrab); } catch { }
        text = Unwrap(text);
        if (!string.IsNullOrEmpty(text)) chapterPageList.Add(text);

        if (paused) { await Task.Delay(400); await AfterBookPage(); return; }
        await AfterBookPage();
    }

    private async Task AfterBookPage()
    {
        string next = "";
        try { next = Unwrap(await webView.CoreWebView2.ExecuteScriptAsync(JsNextPage)); } catch { }
        var (title, curUrl) = bookChapters[Math.Min(bookIdx, bookChapters.Count - 1)];
        var key = next.Split('#')[0];
        if (stopRequested || next.Length == 0 || chapterVisited.Contains(key))
        {
            OnBookChapterDone(title);
            return;
        }
        chapterVisited.Add(key);
        Status($"📚 第 {bookIdx + 1} 章翻页 {chapterVisited.Count - 1} …");
        Touch();
        await webView.CoreWebView2.ExecuteScriptAsync($"location.href='{next.Replace("'", "\\'")}'");
    }

    private void OnBookChapterDone(string title)
    {
        if (chapterPageList.Count > 0)
        {
            var raw = chapterPageList.Count > 1
                ? string.Join("\n\n", chapterPageList.Select((p, i) => $"──── {title} · 第 {i + 1} 节 ────\n\n{p}"))
                : chapterPageList[0];
            AppendTxt(Clean(raw, title));
        }
        else AddLog($"空章节跳过：《{BookName()}》{title}", false);

        if (stopRequested || bookIdx >= bookChapters.Count - 1)
            FinishBook(stopRequested ? bookIdx + 1 : bookChapters.Count);
        else
        {
            var wait = Task.Delay(800);
            wait.ContinueWith(t =>
            {
                while (paused && !stopRequested) Thread.Sleep(400);
                BeginInvoke(() => LoadChapter(bookIdx + 1));
            });
        }
    }

    private void FinishBook(int done)
    {
        bookGrabbing = false; stopRequested = false; paused = false;
        btnBook.Text = "📚 一键整本"; btnPause.Text = "⏸ 暂停";
        Status($"🎉 整本下载完成：{done}/{bookChapters.Count} 章已保存");
    }

    // ================= 清洗 / 保存 =================
    private string Clean(string raw, string chapterTitle)
    {
        var lines = raw.Replace("\r", "")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !AdLine.IsMatch(l));
        var body = string.Join("\n\n", lines);
        return $"══════ {chapterTitle} ══════\n\n{body}\n\n";
    }

    private string BookName()
    {
        var n = txtBookName.Text.Trim();
        if (n.Length == 0) n = "小说";
        return n + ".txt";
    }

    private void AppendTxt(string content)
    {
        try
        {
            var name = BookName();
            if (name != bookUriName) { /* 新书名从新文件继续追加 */ }
            string dir = saveDir.Length > 0 ? saveDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            File.AppendAllText(path, content, new UTF8Encoding(false));
            Status("✅ 已追加到 " + path);
        }
        catch (Exception ex) { Status("❌ 写入失败：" + ex.Message); }
    }

    private void PickDir()
    {
        using var dlg = new FolderBrowserDialog { Description = "选择 TXT 保存文件夹" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            saveDir = dlg.SelectedPath;
            Cfg.Set("saveDir", saveDir);
            SaveConfig();
            Status("✅ TXT 将保存到：" + saveDir);
        }
    }

    private void OpenNovelDir()
    {
        try
        {
            var dir = saveDir.Length > 0 ? saveDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch (Exception ex) { MessageBox.Show("无法打开目录：" + ex.Message); }
    }

    private void OpenTxt()
    {
        try
        {
            var name = BookName();
            string dir = saveDir.Length > 0 ? saveDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) { MessageBox.Show("尚未抓取任何内容：" + path); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    // ================= 收藏夹 / 日志 / 主题 / 暂停 =================
    private async void BookmarkCurrent()
    {
        var url = webView.Source?.ToString() ?? "";
        if (url.Length == 0) return;
        var title = webView.CoreWebView2?.DocumentTitle ?? url;
        if (bookmarks.Any(b => b.url == url)) { Status("该页面已在收藏夹中"); return; }
        bookmarks.Insert(0, (title.Length > 40 ? title[..40] : title, url));
        Cfg.Set("bookmarks", JsonSerializer.Serialize(bookmarks));
        SaveConfig();
        Status("⭐ 已收藏：" + title);
        await Task.CompletedTask;
    }

    private void ShowBookmarks()
    {
        if (bookmarks.Count == 0) { MessageBox.Show("收藏夹为空：浏览页面后点「★ 收藏」"); return; }
        var dlg = new Form
        {
            Text = "📂 收藏夹（点按打开，选中后点删除）", Size = new Size(560, 460),
            StartPosition = FormStartPosition.CenterParent
        };
        var list = new ListBox { Dock = DockStyle.Fill };
        foreach (var (t, u) in bookmarks) list.Items.Add($"⭐ {t}\n     {u}");
        var del = new Button { Text = "删除选中", Dock = DockStyle.Bottom, Height = 36 };
        list.DoubleClick += (s, e) =>
        {
            if (list.SelectedIndex >= 0)
            {
                var (t, u) = bookmarks[list.SelectedIndex];
                txtAddress.Text = u; Navigate(u); dlg.Close();
            }
        };
        del.Click += (s, e) =>
        {
            if (list.SelectedIndex >= 0)
            {
                bookmarks.RemoveAt(list.SelectedIndex);
                Cfg.Set("bookmarks", JsonSerializer.Serialize(bookmarks)); SaveConfig();
                list.Items.RemoveAt(list.SelectedIndex);
            }
        };
        dlg.Controls.Add(list); dlg.Controls.Add(del);
        dlg.ShowDialog(this);
    }

    private void AddLog(string msg, bool isError)
    {
        var ts = DateTime.Now.ToString("MM-dd HH:mm:ss");
        logs.Insert(0, (isError ? "🔴" : "⚪") + $" [{ts}] {msg}");
        if (logs.Count > 300) logs.RemoveAt(logs.Count - 1);
        Cfg.Set("logs", JsonSerializer.Serialize(logs));
        SaveConfig();
    }

    private void ShowLogs()
    {
        var dlg = new Form
        {
            Text = "📋 爬取日志（🔴 = 超时跳过）", Size = new Size(760, 520),
            StartPosition = FormStartPosition.CenterParent
        };
        var list = new ListBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 10) };
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.DrawItem += (s, e) =>
        {
            if (e.Index < 0) return;
            bool isErr = logs[e.Index].StartsWith("🔴");
            e.DrawBackground();
            using var brush = new SolidBrush(isErr ? Color.Firebrick : ForeColor);
            e.Graphics.DrawString(logs[e.Index], e.Font ?? Font, brush, e.Bounds);
            e.DrawFocusRectangle();
        };
        var clear = new Button { Text = "清空日志", Dock = DockStyle.Bottom, Height = 36 };
        clear.Click += (s, e) => { logs.Clear(); Cfg.Set("logs", "[]"); SaveConfig(); list.Items.Clear(); dlg.Close(); };
        foreach (var l in logs) list.Items.Add(l);
        dlg.Controls.Add(list); dlg.Controls.Add(clear);
        dlg.ShowDialog(this);
    }

    private void TogglePause()
    {
        if (!(grabbing || bookGrabbing)) { MessageBox.Show("当前没有进行中的下载"); return; }
        paused = !paused;
        btnPause.Text = paused ? "▶ 继续" : "⏸ 暂停";
        Status(paused ? "⏸ 已暂停，点「▶ 继续」恢复" : "▶ 已继续下载");
    }

    private void ToggleTheme()
    {
        dark = !dark;
        Cfg.Set("night", dark ? "1" : "");
        SaveConfig();
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        Color bg = dark ? Color.FromArgb(22, 33, 62) : Color.FromArgb(220, 232, 255);
        Color fg = dark ? Color.FromArgb(230, 238, 255) : Color.FromArgb(26, 43, 76);
        Color btnBg = dark ? Color.FromArgb(40, 54, 90) : Color.White;
        BackColor = bg; ForeColor = fg;
        foreach (var c in new Control[] { btnBack, btnForward, btnGo, btnStar, btnPickDir, btnGrab, btnBook,
            btnBookmarks, btnOpenDir, btnPause, btnTheme, btnLog, btnFinish })
        {
            if (c is Button b)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = dark ? Color.FromArgb(70, 120, 180) : Color.FromArgb(180, 200, 240);
            }
            c.BackColor = btnBg; c.ForeColor = fg;
        }
        txtAddress.BackColor = btnBg; txtAddress.ForeColor = fg;
        txtBookName.BackColor = btnBg; txtBookName.ForeColor = fg;
        webView.DefaultBackgroundColor = bg;
        btnTheme.Text = dark ? "☀️ 主题" : "🌙 主题";
    }

    private void Status(string s) { Text = "小说爬取 v2.7 — " + s; }

    // ================= 配置 =================
    private static class Cfg
    {
        public static string Get(string k, string def = "") => Data.TryGetValue(k, out var v) ? v : def;
        public static bool GetBool(string k) => Get(k) == "1";
        public static void Set(string k, string v) => Data[k] = v;
        public static Dictionary<string, string> Data = new();
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(CfgPath))
                Cfg.Data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CfgPath))
                           ?? new Dictionary<string, string>();
            saveDir = Cfg.Get("saveDir");
            var bm = Cfg.Get("bookmarks");
            if (bm.Length > 0)
                bookmarks.AddRange(JsonSerializer.Deserialize<List<(string, string)>>(bm)
                    ?.Select(x => (x.Item1, x.Item2)) ?? new List<(string, string)>());
            var lg = Cfg.Get("logs");
            if (lg.Length > 0)
                logs.AddRange(JsonSerializer.Deserialize<List<string>>(lg) ?? new List<string>());
        }
        catch { Cfg.Data = new Dictionary<string, string>(); }
    }

    private void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CfgPath)!);
            File.WriteAllText(CfgPath, JsonSerializer.Serialize(Cfg.Data));
        }
        catch { }
    }

    private static string Unwrap(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetString() ?? "";
        }
        catch { return json; }
    }

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
