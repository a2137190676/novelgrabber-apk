using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web;

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
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    private bool webReady;          // WebView2 是否可用
    private string pendingNav = "";
    private bool grabbing;
    private bool bookGrabbing;
    private bool stopRequested;
    private bool paused;
    private bool dark;

    private string pageTitle = "未命名章节";
    private readonly List<string> pageList = new();          // 单章模式各节
    private readonly HashSet<string> visitedUrls = new();

    private readonly List<KeyValuePair<string, string>> bookChapters = new();
    private int bookIdx;
    private readonly List<string> chapterPageList = new();   // 整本模式当前章各节
    private readonly HashSet<string> chapterVisited = new();

    private readonly List<KeyValuePair<string, string>> bookmarks = new();
    private readonly List<string> logs = new();
    private string saveDir = "";

    private static readonly string CfgDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovelGrabber");
    private static readonly string CfgPath = Path.Combine(CfgDir, "config.json");
    private static Dictionary<string, string> cfg = new();

    private const double TimeoutSec = 60000;
    private DateTime lastAction = DateTime.Now;

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
    private static readonly Regex RxNextHref = new(
        "<a[^>]*href=\"([^\"]+)\"[^>]*>\\s*(下一页|下页|next)\\s*</a>|<a[^>]*>\\s*(下一页|下页|next)\\s*</a[^>]*>\\s*|<a[^>]*href=\"([^\"]+)\"[^>]*title=\"下一页\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxChapter = new(
        "<a[^>]*href=\\\"([^\\\"]+)\\\"[^>]*>\\s*([^<]{2,60})\\s*</a>", RegexOptions.Compiled);

    public MainForm()
    {
        Text = "小说爬取 v3.1";
        Size = new Size(1100, 780);
        StartPosition = FormStartPosition.CenterScreen;

        LoadConfig();
        dark = GetCfg("night") == "1";
        uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        uiTimer.Tick += (s, e) => TimeoutWatch();
        uiTimer.Start();

        InitUi();
        ApplyTheme();
        InitWebView();
    }

    // ================= UI =================
    private void InitUi()
    {
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 60, FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8), WrapContents = false
        };
        btnBack = MkBtn("◀", 36); btnBack.Click += (s, e) => { try { if (webReady && webView.CanGoBack) webView.GoBack(); } catch { } };
        btnForward = MkBtn("▶", 36); btnForward.Click += (s, e) => { try { if (webReady && webView.CanGoForward) webView.GoForward(); } catch { } };
        txtAddress = new TextBox { Width = 420, Anchor = AnchorStyles.Left, Margin = new Padding(4, 10, 4, 10) };
        txtAddress.PlaceholderText = "输入小说网址";
        txtAddress.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Go(); e.SuppressKeyPress = true; } };
        btnGo = MkBtn("前往", 60); btnGo.Click += (s, e) => Go();
        btnStar = MkBtn("★ 收藏", 70); btnStar.Click += (s, e) => BookmarkCurrent();
        txtBookName = new TextBox { Width = 170, Anchor = AnchorStyles.Left, Margin = new Padding(4, 10, 4, 10) };
        txtBookName.PlaceholderText = "书名（TXT 文件名）";
        btnPickDir = MkBtn("目录", 70); btnPickDir.Click += (s, e) => PickDir();
        top.Controls.AddRange(new Control[] { btnBack, btnForward, txtAddress, btnGo, btnStar, txtBookName, btnPickDir });

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8), WrapContents = false
        };
        btnGrab = MkBtn("抓取整章", 110); btnGrab.Click += (s, e) => _ = StartGrab();
        btnBook = MkBtn("一键整本", 110); btnBook.Click += (s, e) => _ = BookButton();
        btnBookmarks = MkBtn("收藏夹", 88); btnBookmarks.Click += (s, e) => ShowBookmarks();
        btnOpenDir = MkBtn("小说夹", 88); btnOpenDir.Click += (s, e) => OpenNovelDir();
        btnPause = MkBtn("暂停", 76); btnPause.Click += (s, e) => TogglePause();
        btnTheme = MkBtn("主题", 76); btnTheme.Click += (s, e) => ToggleTheme();
        btnLog = MkBtn("日志", 76); btnLog.Click += (s, e) => ShowLogs();
        btnFinish = MkBtn("打开TXT", 96); btnFinish.Click += (s, e) => OpenTxt();
        bottom.Controls.AddRange(new Control[]
            { btnGrab, btnBook, btnBookmarks, btnOpenDir, btnPause, btnTheme, btnLog, btnFinish });

        webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };
        var placeholder = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Text = "正在初始化内置浏览器（WebView2）…\r\n如长时间停留在此处，将自动启用内置 HTTP 抓取模式（无需浏览器内核）"
        };
        webView.CoreWebView2InitializationCompleted += (s, e) =>
        {
            placeholder.Visible = false;
            try
            {
                if (webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.Settings.UserAgent =
                        webView.CoreWebView2.Settings.UserAgent.Replace("; wv", "");
                    webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    webView.CoreWebView2.Profile.PreferredColorScheme =
                        dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
                    // 站内新窗口一律在软件内打开，绝不跳外部浏览器
                    webView.CoreWebView2.NewWindowRequested += (s2, e2) =>
                    {
                        e2.Handled = true;
                        try { webView.CoreWebView2.Navigate(e2.Uri); } catch { }
                    };
                }
                webReady = true;
            }
            catch { webReady = false; }
            if (webReady && pendingNav.Length > 0)
            {
                try { Navigate(pendingNav); } catch { }
                pendingNav = "";
            }
        };

        Controls.Add(webView);
        Controls.Add(placeholder);
        Controls.Add(bottom);
        Controls.Add(top);
    }

    private static Button MkBtn(string text, int width) => new()
    {
        Text = text, Width = width, Height = 34, Margin = new Padding(4, 5, 4, 5)
    };

    private async void InitWebView()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(CfgDir, "WebView2"));
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(CfgDir, "WebView2"));
            await webView.EnsureCoreWebView2Async(env);
            Navigate(GetCfg("home", "https://www.qidian.com/"));
        }
        catch (Exception ex)
        {
            webReady = false;
            Status("[警告] 内置浏览器不可用，已切换 HTTP 抓取模式");
            AddLog($"WebView2 初始化失败，启用 HTTP 兜底：{ex.Message}", false);
            MessageBox.Show(
                "内置浏览器（WebView2）初始化失败，已自动切换为 HTTP 抓取模式：\n" +
                "· 抓取整章 / 一键整本 / 日志 等全部抓取功能均可正常使用\n" +
                "· 仅无法在软件内浏览动态网页\n\n" +
                "如需内嵌浏览效果，请安装 Microsoft Edge WebView2 Runtime（Win7 请用 109 版）。\n\n" +
                "详情：" + ex.Message,
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void Navigate(string url)
    {
        if (!url.StartsWith("http://") && !url.StartsWith("https://")) url = "https://" + url;
        txtAddress.Text = url;
        if (webReady)
        {
            try { webView.Source = new Uri(url); }
            catch (Exception ex) { Status("导航失败：" + ex.Message); }
        }
        else pendingNav = url;
    }

    private void Go() { if (txtAddress.Text.Trim().Length > 0) Navigate(txtAddress.Text.Trim()); }

    private string CurrentUrl()
    {
        if (webReady) return webView.Source?.ToString() ?? txtAddress.Text;
        return txtAddress.Text;
    }

    // ================= 超时保护（60000 秒） =================
    private void Touch() => lastAction = DateTime.Now;

    private void TimeoutWatch()
    {
        if (!(grabbing || bookGrabbing) || paused) return;
        if ((DateTime.Now - lastAction).TotalSeconds < TimeoutSec) return;
        Touch();
        if (bookGrabbing)
        {
            var title = bookIdx < bookChapters.Count ? bookChapters[bookIdx].Key : "?";
            AddLog($"整本超时跳过章节：《{BookName()}》{title} · {CurrentUrl()}", true);
            Status("[超时] 加载超时（60000s），跳过：" + title);
            OnBookChapterDone(title);
        }
        else if (grabbing)
        {
            AddLog($"单章抓取超时收尾：{pageTitle} · {CurrentUrl()}", true);
            Status("[超时] 等待超时（60000s），按已抓取内容收尾");
            FinalizeChapter();
        }
    }

    // ================= 页面获取（WebView 优先，HTTP 兜底） =================
    private async Task<string> GetPageText()
    {
        if (webReady)
        {
            try
            {
                var r = await webView.CoreWebView2.ExecuteScriptAsync(JsGrab);
                return Unwrap(r);
            }
            catch { }
        }
        return ExtractTextFromHtml(await FetchHtml(CurrentUrl()));
    }

    private async Task<string> GetNextPageUrl()
    {
        if (webReady)
        {
            try { return Unwrap(await webView.CoreWebView2.ExecuteScriptAsync(JsNextPage)); }
            catch { }
        }
        var html = await FetchHtml(CurrentUrl());
        var m = RxNextHref.Match(html);
        if (!m.Success) return "";
        var href = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[4].Value;
        return ResolveUrl(CurrentUrl(), HttpUtility.HtmlDecode(href));
    }

    private async Task<string> FetchHtml(string url)
    {
        if (!url.StartsWith("http://") && !url.StartsWith("https://")) return "";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");
            var resp = await Http.SendAsync(req);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var html = Encoding.UTF8.GetString(bytes);
            var lower = html[..Math.Min(html.Length, 2000)].ToLower();
            if (lower.Contains("charset=gb2312") || lower.Contains("charset=gbk"))
                html = Encoding.GetEncoding("GBK").GetString(bytes);
            return html;
        }
        catch (Exception ex) { AddLog($"HTTP 获取失败：{url} · {ex.Message}", true); return ""; }
    }

    private static string ExtractTextFromHtml(string html)
    {
        if (html.Length == 0) return "";
        html = Regex.Replace(html, "<script[\\s\\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<style[\\s\\S]*?</style>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "</p>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<[^>]+>", "");
        html = HttpUtility.HtmlDecode(html);
        var lines = html.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 15 && !AdLine.IsMatch(l));
        var text = string.Join("\n", lines);
        return text.Length > 150 ? text : "";
    }

    private static string ResolveUrl(string baseUrl, string href)
    {
        try { return new Uri(new Uri(baseUrl), href).ToString(); }
        catch { return href.StartsWith("http") ? href : ""; }
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

    // ================= 单章抓取 =================
    private async Task StartGrab()
    {
        if (bookGrabbing) { Info("整本下载进行中，请先完成或停止"); return; }
        if (grabbing) return;
        grabbing = true; stopRequested = false; paused = false;
        pageList.Clear(); visitedUrls.Clear();
        visitedUrls.Add(CurrentUrl().Split('#')[0]);
        pageTitle = (webReady ? webView.CoreWebView2?.DocumentTitle : null) ?? ("章节 " + DateTime.Now.ToString("MMdd-HHmmss"));
        Status("🔍 正在抓取：" + pageTitle);
        Touch();
        await ExtractChapterPage();
    }

    private async Task ExtractChapterPage()
    {
        var text = await GetPageText();
        if (!string.IsNullOrEmpty(text)) pageList.Add(text);
        Status($"📄 已抓取第 {pageList.Count} 页，正在检查下一页…");
        await FindNextChapterPage();
    }

    private async Task FindNextChapterPage()
    {
        while (paused && !stopRequested) await Task.Delay(400);
        if (stopRequested) { FinalizeChapter(); return; }
        var next = await GetNextPageUrl();
        var key = next.Split('#')[0];
        if (next.Length == 0 || visitedUrls.Contains(key)) { FinalizeChapter(); return; }
        visitedUrls.Add(key);
        Status("自动翻页 " + (visitedUrls.Count - 1) + " …");
        Touch();
        await GoTo(next);
        await ExtractChapterPage();
    }

    private async Task GoTo(string url)
    {
        if (webReady)
        {
            try
            {
                var tcs = new TaskCompletionSource<bool>();
                void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(true);
                webView.NavigationCompleted += Handler;
                try
                {
                    webView.Source = new Uri(url);
                    await tcs.Task.WaitAsync(TimeSpan.FromSeconds(TimeoutSec));
                }
                finally { webView.NavigationCompleted -= Handler; }
                await Task.Delay(600);
                return;
            }
            catch { Status("[超时] 页面加载缓慢/失败，尝试 HTTP 模式继续"); }
        }
        txtAddress.Text = url;
        await Task.Delay(100);
    }

    private void FinalizeChapter()
    {
        if (!grabbing) return;
        grabbing = false; stopRequested = false;
        if (pageList.Count == 0)
        {
            Status("❌ 未识别到正文，本站结构不兼容");
            Info("未识别到正文，请尝试其他章节或书源。\n（HTTP 兜底模式下部分 JS 动态站点无法解析）");
            return;
        }
        var raw = pageList.Count > 1
            ? string.Join("\n\n", pageList.Select((p, i) => $"──── {pageTitle} · 第 {i + 1} 节 ────\n\n{p}"))
            : pageList[0];
        Status($"正在保存…（共 {pageList.Count} 页）");
        AppendTxt(Clean(raw, pageTitle));
        Status($"整章抓取完成：{pageTitle}（{pageList.Count} 页）");
    }

    // ================= 整本抓取 =================
    private async Task BookButton()
    {
        if (bookGrabbing) { stopRequested = true; Status("⏹ 正在停止，完成当前章节后结束…"); return; }
        if (grabbing) { Info("请先等待当前章节抓取完成"); return; }

        var list = await ParseChapters();
        if (list.Count == 0)
        {
            Info("未在当前页面识别到章节列表，请先打开小说目录页再点「一键整本」。");
            return;
        }
        bookChapters.Clear(); bookChapters.AddRange(list);
        bookGrabbing = true; stopRequested = false; paused = false;
        btnBook.Text = "⏹ 停止";
        Status($"识别到 {bookChapters.Count} 章，开始整本下载");
        await LoadChapter(0);
    }

    private async Task<List<KeyValuePair<string, string>>> ParseChapters()
    {
        var list = new List<KeyValuePair<string, string>>();
        if (webReady)
        {
            try
            {
                var json = Unwrap(await webView.CoreWebView2.ExecuteScriptAsync(JsBookChapters));
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                    list.Add(new KeyValuePair<string, string>(
                        el.GetProperty("t").GetString() ?? "", el.GetProperty("u").GetString() ?? ""));
                if (list.Count > 0) return list;
            }
            catch { }
        }
        var baseUrl = CurrentUrl();
        var html = await FetchHtml(baseUrl);
        var seen = new HashSet<string>();
        foreach (Match m in RxChapter.Matches(html))
        {
            var href = ResolveUrl(baseUrl, HttpUtility.HtmlDecode(m.Groups[1].Value));
            var t = HttpUtility.HtmlDecode(m.Groups[2].Value).Trim();
            if (href.Length == 0 || t.Length < 2 || seen.Contains(href)) continue;
            if (!Regex.IsMatch(t, @"^(第.{1,12}(章|节|回|卷))|^\d{1,4}(\.\d+)?$|^Chapter", RegexOptions.IgnoreCase)) continue;
            seen.Add(href);
            list.Add(new KeyValuePair<string, string>(t, href));
            if (list.Count >= 9999) break;
        }
        return list;
    }

    private async Task LoadChapter(int idx)
    {
        if (stopRequested || idx >= bookChapters.Count)
        {
            FinishBook(Math.Min(idx, bookChapters.Count));
            return;
        }
        while (paused && !stopRequested) await Task.Delay(400);
        bookIdx = idx;
        var (title, url) = (bookChapters[idx].Key, bookChapters[idx].Value);
        chapterPageList.Clear(); chapterVisited.Clear();
        chapterVisited.Add(url.Split('#')[0]);
        Status($"整本进度 第 {idx + 1}/{bookChapters.Count} 章：{title}");
        Touch();
        await GoTo(url);
        await ExtractBookPage();
    }

    private async Task ExtractBookPage()
    {
        var text = await GetPageText();
        if (!string.IsNullOrEmpty(text)) chapterPageList.Add(text);
        await AfterBookPage();
    }

    private async Task AfterBookPage()
    {
        while (paused && !stopRequested) await Task.Delay(400);
        var (title, _) = bookChapters[Math.Min(bookIdx, bookChapters.Count - 1)];
        var next = await GetNextPageUrl();
        var key = next.Split('#')[0];
        if (stopRequested || next.Length == 0 || chapterVisited.Contains(key))
        {
            OnBookChapterDone(title);
            return;
        }
        chapterVisited.Add(key);
        Status($"第 {bookIdx + 1} 章翻页 {chapterVisited.Count - 1} …");
        Touch();
        await GoTo(next);
        await ExtractBookPage();
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
            _ = Task.Run(async () =>
            {
                await Task.Delay(800);
                await LoadChapter(bookIdx + 1);
            });
    }

    private void FinishBook(int done)
    {
        bookGrabbing = false; stopRequested = false; paused = false;
        btnBook.Text = "一键整本"; btnPause.Text = "暂停";
        Status($"🎉 整本下载完成：{done}/{bookChapters.Count} 章已保存");
        Info($"整本下载完成！共 {done} 章，保存在 {SaveDirPath()}");
    }

    // ================= 清洗 / 保存 =================
    private string Clean(string raw, string chapterTitle)
    {
        var lines = raw.Replace("\r", "")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !AdLine.IsMatch(l));
        return $"══════ {chapterTitle} ══════\n\n{string.Join("\n\n", lines)}\n\n";
    }

    private string BookName()
    {
        var n = txtBookName.Text.Trim();
        if (n.Length == 0) n = "小说";
        return n + ".txt";
    }

    private string SaveDirPath()
    {
        var dir = saveDir.Length > 0 ? saveDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    private void AppendTxt(string content)
    {
        try
        {
            var path = Path.Combine(SaveDirPath(), BookName());
            File.AppendAllText(path, content, new UTF8Encoding(false));
            Status("已追加到 " + path);
        }
        catch (Exception ex) { Status("❌ 写入失败：" + ex.Message); AddLog("写入失败：" + ex.Message, true); }
    }

    private void PickDir()
    {
        using var dlg = new FolderBrowserDialog { Description = "选择 TXT 保存文件夹" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            saveDir = dlg.SelectedPath;
            SetCfg("saveDir", saveDir);
            SaveConfig();
            Status("TXT 将保存到：" + saveDir);
        }
    }

    private void OpenNovelDir()
    {
        try { System.Diagnostics.Process.Start("explorer.exe", SaveDirPath()); }
        catch (Exception ex) { Info("无法打开目录：" + ex.Message); }
    }

    private void OpenTxt()
    {
        try
        {
            var path = Path.Combine(SaveDirPath(), BookName());
            if (!File.Exists(path)) { Info("尚未抓取任何内容：" + path); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Info(ex.Message); }
    }

    // ================= 收藏夹 / 日志 / 主题 / 暂停 =================
    private void BookmarkCurrent()
    {
        var url = CurrentUrl();
        if (url.Length == 0 || !url.StartsWith("http")) { Info("请先打开一个网页"); return; }
        var title = (webReady ? webView.CoreWebView2?.DocumentTitle : null) ?? url;
        if (bookmarks.Any(b => b.Value == url)) { Status("该页面已在收藏夹中"); return; }
        bookmarks.Insert(0, new KeyValuePair<string, string>(title.Length > 40 ? title[..40] : title, url));
        SetCfg("bookmarks", JsonSerializer.Serialize(bookmarks.Select(b => new[] { b.Key, b.Value }).ToList()));
        SaveConfig();
        Status("⭐ 已收藏：" + title);
    }

    private void ShowBookmarks()
    {
        if (bookmarks.Count == 0) { Info("收藏夹为空：浏览页面后点「★ 收藏」"); return; }
        var dlg = new Form
        {
            Text = "收藏夹（双击打开，选中后点删除）", Size = new Size(560, 460),
            StartPosition = FormStartPosition.CenterParent, BackColor = BackColor, ForeColor = ForeColor
        };
        var list = new ListBox { Dock = DockStyle.Fill };
        foreach (var b in bookmarks) list.Items.Add($"⭐ {b.Key}\n     {b.Value}");
        var del = new Button { Text = "删除选中", Dock = DockStyle.Bottom, Height = 36 };
        list.DoubleClick += (s, e) =>
        {
            if (list.SelectedIndex >= 0)
            {
                var b = bookmarks[list.SelectedIndex];
                txtAddress.Text = b.Value; Navigate(b.Value); dlg.Close();
            }
        };
        del.Click += (s, e) =>
        {
            if (list.SelectedIndex >= 0)
            {
                bookmarks.RemoveAt(list.SelectedIndex);
                SetCfg("bookmarks", JsonSerializer.Serialize(bookmarks.Select(b => new[] { b.Key, b.Value }).ToList()));
                SaveConfig();
                list.Items.RemoveAt(list.SelectedIndex);
            }
        };
        dlg.Controls.Add(list); dlg.Controls.Add(del);
        dlg.ShowDialog(this);
    }

    private void AddLog(string msg, bool isError)
    {
        var ts = DateTime.Now.ToString("MM-dd HH:mm:ss");
        logs.Insert(0, (isError ? "[!] " : "    ") + $" [{ts}] {msg}");
        if (logs.Count > 300) logs.RemoveAt(logs.Count - 1);
        SetCfg("logs", JsonSerializer.Serialize(logs));
        SaveConfig();
    }

    private void ShowLogs()
    {
        var dlg = new Form
        {
            Text = "爬取日志（[!] = 超时跳过）", Size = new Size(760, 520),
            StartPosition = FormStartPosition.CenterParent, BackColor = BackColor, ForeColor = ForeColor
        };
        var list = new ListBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 10) };
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.Items.AddRange(logs.ToArray());
        list.DrawItem += (s, e) =>
        {
            if (e.Index < 0 || e.Index >= logs.Count) return;
            bool isErr = logs[e.Index].StartsWith("[!]");
            e.DrawBackground();
            using var brush = new SolidBrush(isErr ? Color.Firebrick : ForeColor);
            e.Graphics.DrawString(logs[e.Index], e.Font ?? Font, brush, e.Bounds);
            e.DrawFocusRectangle();
        };
        var clear = new Button { Text = "清空日志", Dock = DockStyle.Bottom, Height = 36 };
        clear.Click += (s, e) => { logs.Clear(); SetCfg("logs", "[]"); SaveConfig(); list.Items.Clear(); dlg.Close(); };
        dlg.Controls.Add(list); dlg.Controls.Add(clear);
        dlg.ShowDialog(this);
    }

    private void TogglePause()
    {
        if (!(grabbing || bookGrabbing)) { Info("当前没有进行中的下载"); return; }
        paused = !paused;
        btnPause.Text = paused ? "继续" : "暂停";
        Status(paused ? "已暂停，点「继续」恢复" : "已继续下载");
    }

    private void ToggleTheme()
    {
        dark = !dark;
        SetCfg("night", dark ? "1" : "");
        SaveConfig();
        ApplyTheme();
        if (webReady)
        {
            try
            {
                webView.CoreWebView2.Profile.PreferredColorScheme =
                    dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
            }
            catch { }
        }
    }

    private void ApplyTheme()
    {
        Color bg = dark ? Color.FromArgb(17, 24, 39) : Color.FromArgb(242, 244, 247);
        Color card = dark ? Color.FromArgb(31, 41, 55) : Color.White;
        Color fg = dark ? Color.FromArgb(249, 250, 251) : Color.FromArgb(17, 24, 39);
        Color border = dark ? Color.FromArgb(55, 65, 81) : Color.FromArgb(229, 231, 235);
        BackColor = bg; ForeColor = fg;
        foreach (var c in new Control[] { btnBack, btnForward, btnGo, btnStar, btnPickDir, btnGrab, btnBook,
            btnBookmarks, btnOpenDir, btnPause, btnTheme, btnLog, btnFinish })
        {
            if (c is Button b)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = border;
                if (b == btnGrab || b == btnBook)
                {
                    b.BackColor = dark ? Color.FromArgb(59, 130, 246) : Color.FromArgb(37, 99, 235);
                    b.ForeColor = Color.White;
                    continue;
                }
            }
            c.BackColor = card; c.ForeColor = fg;
        }
        txtAddress.BackColor = card; txtAddress.ForeColor = fg;
        txtBookName.BackColor = card; txtBookName.ForeColor = fg;
        webView.DefaultBackgroundColor = bg;
        btnTheme.Text = dark ? "主题" : "主题";
    }

    private void Status(string s) { Text = "小说爬取 v3.0 — " + s; }

    private void Info(string msg) => MessageBox.Show(this, msg, "小说爬取", MessageBoxButtons.OK, MessageBoxIcon.Information);

    // ================= 配置 =================
    private static string GetCfg(string k, string def = "") => cfg.TryGetValue(k, out var v) ? v : def;
    private static void SetCfg(string k, string v) => cfg[k] = v;

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(CfgPath))
                cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CfgPath))
                       ?? new Dictionary<string, string>();
            saveDir = GetCfg("saveDir");
            var bm = GetCfg("bookmarks");
            if (bm.Length > 0)
            {
                var arr = JsonSerializer.Deserialize<List<string[]>>(bm) ?? new List<string[]>();
                foreach (var pair in arr)
                    if (pair.Length == 2) bookmarks.Add(new KeyValuePair<string, string>(pair[0], pair[1]));
            }
            var lg = GetCfg("logs");
            if (lg.Length > 0) logs.AddRange(JsonSerializer.Deserialize<List<string>>(lg) ?? new List<string>());
        }
        catch { cfg = new Dictionary<string, string>(); }
    }

    private void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(CfgDir);
            File.WriteAllText(CfgPath, JsonSerializer.Serialize(cfg));
        }
        catch { }
    }

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
