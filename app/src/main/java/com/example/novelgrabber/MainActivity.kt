package com.example.novelgrabber

import android.annotation.SuppressLint
import android.app.AlertDialog
import android.app.DownloadManager
import android.content.Intent
import android.content.SharedPreferences
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.os.Handler
import android.os.Looper
import android.provider.DocumentsContract
import android.provider.MediaStore
import android.view.KeyEvent
import android.view.View
import android.view.inputmethod.EditorInfo
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.ImageButton
import android.widget.ListView
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatDelegate
import androidx.appcompat.app.AppCompatActivity
import androidx.documentfile.provider.DocumentFile
import java.io.OutputStream

class MainActivity : AppCompatActivity() {

    private lateinit var webView: WebView
    private lateinit var etAddress: EditText
    private lateinit var etBookName: EditText
    private lateinit var tvStatus: TextView
    private lateinit var progressBar: ProgressBar
    private lateinit var btnGrab: Button
    private lateinit var prefs: SharedPreferences

    private val handler = Handler(Looper.getMainLooper())
    private var bookUri: Uri? = null
    private var saveDirUri: Uri? = null

    /** 书签数据：list of "title|url" */
    private val bookmarks = ArrayList<Pair<String, String>>()

    /** 整本下载状态 */
    private var bookGrabbing = false
    private var bookUriName = ""
    private var paused = false
    private var stopRequested = false
    private val bookChapters = ArrayList<Pair<String, String>>()
    private var bookIdx = 0
    private val chapterPages = StringBuilder()
    private val chapterPageList = ArrayList<String>()
    private val chapterVisited = HashSet<String>()

    private val pickDirLauncher = registerForActivityResult(
        ActivityResultContracts.OpenDocumentTree()
    ) { uri: Uri? ->
        if (uri != null) {
            try {
                contentResolver.takePersistableUriPermission(
                    uri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                )
            } catch (_: Exception) {}
            saveDirUri = uri
            prefs.edit().putString("save_dir", uri.toString()).apply()
            val name = DocumentFile.fromTreeUri(this, uri)?.name ?: uri.lastPathSegment ?: "所选文件夹"
            toast("✅ TXT 将保存到：$name")
        }
    }

    /** 分页抓取状态机 */
    private var grabbing = false
    private val pageTexts = StringBuilder()
    private val pageList = ArrayList<String>()
    private val visitedUrls = HashSet<String>()
    private var pageTitle = ""

    companion object {
        private const val PAGE_LOAD_TIMEOUT_MS = 6000000L

        /** 提取当前页正文 */
        private const val JS_GRAB = """
            (function() {
                var selectors = [
                    '#content', '#chaptercontent', '#BookText', '#htmlContent',
                    '#txtContent', '#articlecontent', '#nr1', '#nr', '#chapter',
                    '.content', '.readcontent', '.read-content', '.showtxt',
                    '.novel-content', '.article-content', '.txtnav'
                ];
                for (var i = 0; i < selectors.length; i++) {
                    var el = document.querySelector(selectors[i]);
                    if (el) {
                        var text = el.innerText.trim();
                        if (text.length > 150) return text;
                    }
                }
                var ps = document.querySelectorAll('p');
                var buf = '';
                for (var j = 0; j < ps.length; j++) {
                    var t = ps[j].innerText.trim();
                    if (t.length > 20) buf += t + '\n';
                }
                if (buf.length > 150) return buf;
                return '';
            })();
        """

        /**
         * 查找本章内的「下一页」链接。
         * 规则：链接文字含“下一页/下页”，但不含“下一章/下章/下一节”；
         * 且目标地址不能等于当前地址（防止死循环），不能是 javascript:;
         */
        private const val JS_NEXT_PAGE = """
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
        """

        /**
         * 目录页解析：提取所有章节链接（第x章/节/回/卷、纯数字序号、Chapter n）
         */
        private const val JS_BOOK_CHAPTERS = """
            (function() {
                var seen = {}, out = [];
                var links = document.querySelectorAll('a');
                for (var i = 0; i < links.length; i++) {
                    var a = links[i];
                    var t = (a.innerText || '').trim();
                    var href = a.href || '';
                    if (!href || href.indexOf('http') !== 0) continue;
                    if (t.length < 2 || t.length > 60) continue;
                    if (!(/^(第.{1,12}(章|节|回|卷))/.test(t) || /^\\d{1,4}(\\.\\d+)?$/.test(t) || /^Chapter/i.test(t))) continue;
                    if (seen[href]) continue;
                    seen[href] = 1;
                    out.push({t: t, u: href});
                    if (out.length >= 9999) break;
                }
                return JSON.stringify(out);
            })();
        """
    }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        webView = findViewById(R.id.webView)
        etAddress = findViewById(R.id.etAddress)
        etBookName = findViewById(R.id.etBookName)
        tvStatus = findViewById(R.id.tvStatus)
        progressBar = findViewById(R.id.progressBar)
        btnGrab = findViewById(R.id.btnGrab)
        prefs = getSharedPreferences("novelgrabber", MODE_PRIVATE)

        // 应用上次选择的主题（浅色/深色）
        AppCompatDelegate.setDefaultNightMode(
            if (prefs.getBoolean("night", false)) AppCompatDelegate.MODE_NIGHT_YES
            else AppCompatDelegate.MODE_NIGHT_NO
        )

        // 恢复上次选择的保存目录
        prefs.getString("save_dir", null)?.let {
            try {
                saveDirUri = Uri.parse(it)
            } catch (_: Exception) {}
        }
        // 恢复书签
        loadBookmarks()

        setupWebView()
        setupButtons()
        etAddress.setText("https://www.qidian.com/")
    }

    @SuppressLint("SetJavaScriptEnabled")
    private fun setupWebView() {
        webView.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            userAgentString = userAgentString.replace("; wv", "")
            mixedContentMode = WebSettings.MIXED_CONTENT_ALWAYS_ALLOW
            useWideViewPort = true
            loadWithOverviewMode = true
            builtInZoomControls = true
            displayZoomControls = false
        }
        webView.webViewClient = object : WebViewClient() {
            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                val url = request.url.toString()
                if (url.startsWith("http://") || url.startsWith("https://")) return false
                try {
                    startActivity(Intent(Intent.ACTION_VIEW, request.url))
                } catch (_: Exception) {}
                return true
            }

            override fun onPageFinished(view: WebView, url: String) {
                etAddress.setText(url)
                if (bookGrabbing) {
                    // 整本模式：章节页/分页加载完成后提取
                    handler.postDelayed({ grabBookCurrentPage(url) }, 600)
                } else if (grabbing) {
                    // 单章模式：等页面渲染完再提取
                    handler.postDelayed({ extractCurrentPage(url) }, 600)
                }
            }
        }
        webView.webChromeClient = object : WebChromeClient() {
            override fun onProgressChanged(view: WebView?, newProgress: Int) {
                progressBar.progress = newProgress
                progressBar.visibility = if (newProgress < 100) View.VISIBLE else View.GONE
            }
        }
    }

    private fun setupButtons() {
        findViewById<ImageButton>(R.id.btnBack).setOnClickListener { if (webView.canGoBack()) webView.goBack() }
        findViewById<ImageButton>(R.id.btnForward).setOnClickListener { if (webView.canGoForward()) webView.goForward() }
        findViewById<ImageButton>(R.id.btnGo).setOnClickListener { go() }
        etAddress.setOnEditorActionListener { _, actionId, event ->
            if (actionId == EditorInfo.IME_ACTION_GO ||
                (event != null && event.keyCode == KeyEvent.KEYCODE_ENTER)) {
                go(); true
            } else false
        }
        btnGrab.setOnClickListener { startGrab() }
        findViewById<ImageButton>(R.id.btnFinish).setOnClickListener { openTxt() }

        // ★ 收藏当前页面
        findViewById<ImageButton>(R.id.btnStar).setOnClickListener {
            val url = webView.url ?: return@setOnClickListener
            val title = (webView.title ?: url).take(40)
            if (bookmarks.any { it.second == url }) {
                toast("该页面已在收藏夹中")
            } else {
                bookmarks.add(0, title to url)
                saveBookmarks()
                toast("⭐ 已收藏：$title")
            }
        }

        // 📂 打开收藏夹
        findViewById<ImageButton>(R.id.btnBookmarks).setOnClickListener { showBookmarks() }

        // 📁 选择 TXT 保存文件夹
        findViewById<ImageButton>(R.id.btnPickDir).setOnClickListener {
            pickDirLauncher.launch(null)
        }

        // 📂 打开小说所在文件夹
        findViewById<ImageButton>(R.id.btnOpenDir).setOnClickListener { openDownloadDir() }

        // ⏸/▶ 暂停 / 继续
        findViewById<ImageButton>(R.id.btnPause).setOnClickListener { togglePause() }

        // 🌙/☀️ 深色 / 浅色主题切换
        findViewById<ImageButton>(R.id.btnTheme).setOnClickListener {
            val night = prefs.getBoolean("night", false)
            prefs.edit().putBoolean("night", !night).apply()
            AppCompatDelegate.setDefaultNightMode(
                if (night) AppCompatDelegate.MODE_NIGHT_NO else AppCompatDelegate.MODE_NIGHT_YES
            )
        }

        // 📚 一键整本（再次点击=停止）
        findViewById<Button>(R.id.btnBook).setOnClickListener {
            if (bookGrabbing) {
                stopRequested = true
                setStatus("⏹ 正在停止，完成当前章节后结束…")
            } else {
                startBookGrab()
            }
        }
    }

    // ================= 整本自动下载 =================

    private fun startBookGrab() {
        if (grabbing) {
            toast("请先等待当前章节抓取完成")
            return
        }
        val bookName = etBookName.text.toString().trim().ifEmpty { "小说" }
        toast("📖 开始整本下载：$bookName（需停留在目录页）")

        webView.evaluateJavascript(JS_BOOK_CHAPTERS) { result ->
            runOnUiThread {
                val list = ArrayList<Pair<String, String>>()
                try {
                    val arr = org.json.JSONArray(unquoteJson(result?.trim() ?: "[]"))
                    for (i in 0 until arr.length()) {
                        val o = arr.getJSONObject(i)
                        list.add(o.getString("t") to o.getString("u"))
                    }
                } catch (_: Exception) {}
                if (list.isEmpty()) {
                    toast("❌ 未在当前页面识别到章节列表，请先打开小说目录页再点「一键整本」")
                    return@runOnUiThread
                }
                bookGrabbing = true
                stopRequested = false
                btnGrab.isEnabled = false
                (findViewById<Button>(R.id.btnBook)).text = "⏹ 停止"
                bookChapters.clear()
                bookChapters.addAll(list)
                setStatus("📚 识别到 ${list.size} 章，开始整本下载")
                loadChapter(0)
            }
        }
    }

    private fun loadChapter(idx: Int) {
        bookIdx = idx
        val (title, url) = bookChapters[idx]
        chapterPages.setLength(0)
        chapterPageList.clear()
        chapterVisited.clear()
        chapterVisited.add(url.substringBefore('#'))
        setStatus("📚 整本进度 第 ${idx + 1}/${bookChapters.size} 章：$title")
        webView.loadUrl(url)
        scheduleBookTimeout(title, url)
    }

    /** 每次页面加载/翻页后的超时保护 */
    private fun scheduleBookTimeout(title: String, url: String) {
        handler.removeCallbacksAndMessages(null)
        handler.postDelayed({
            if (bookGrabbing) {
                setStatus("⏱️ 加载超时，跳过：$title")
                onBookChapterDone(title)
            }
        }, 6000000)
    }

    private fun grabBookCurrentPage(url: String) {
        webView.evaluateJavascript(JS_GRAB) { result ->
            runOnUiThread {
                var text = result?.trim() ?: ""
                text = if (text == "\"\"" || text == "null") "" else unquoteJson(text)
                if (text.isNotEmpty()) {
                    chapterPageList.add(text)
                }
                // 章内自动翻页
                webView.evaluateJavascript(JS_NEXT_PAGE) { r2 ->
                    runOnUiThread {
                        resumeAware {
                            val next = (r2?.trim() ?: "").removeSurrounding("\"")
                            val (chTitle, curUrl) = bookChapters[bookIdx]
                            if (stopRequested || next.isEmpty() || next.substringBefore('#') in chapterVisited) {
                                onBookChapterDone(chTitle)
                            } else {
                                chapterVisited.add(next.substringBefore('#'))
                                setStatus("📚 第 ${bookIdx + 1} 章翻页 ${chapterVisited.size - 1} …")
                                webView.loadUrl(next)
                                scheduleBookTimeout(chTitle, next)
                            }
                        }
                    }
                }
            }
        }
    }

    private fun onBookChapterDone(title: String) {
        if (chapterPageList.isNotEmpty()) {
            val raw = if (chapterPageList.size > 1) {
                // 分页章节：每节写入详细小标题
                chapterPageList.mapIndexed { i, p -> "──── $title · 第 ${i + 1} 节 ────\n\n$p" }
                    .joinToString("\n\n")
            } else chapterPageList[0]
            appendToTxt(clean(raw, title))
        } else {
            setStatus("⚠️ 空章节，跳过：$title")
        }
        if (stopRequested || bookIdx >= bookChapters.size - 1) {
            val done = if (stopRequested) bookIdx + 1 else bookChapters.size
            finishBook(done)
        } else {
            handler.postDelayed({ resumeAware { loadChapter(bookIdx + 1) } }, 800)
        }
    }

    private fun finishBook(doneCount: Int) {
        bookGrabbing = false
        stopRequested = false
        paused = false
        (findViewById<ImageButton>(R.id.btnPause)).setImageResource(android.R.drawable.ic_media_pause)
        btnGrab.isEnabled = true
        (findViewById<Button>(R.id.btnBook)).text = "📚 一键整本"
        setStatus("🎉 整本下载完成：$doneCount/${bookChapters.size} 章已保存")
        toast("整本下载完成！共 $doneCount 章")
    }

    // ================= 收藏夹 =================

    private fun loadBookmarks() {
        bookmarks.clear()
        val json = prefs.getString("bookmarks", "[]") ?: "[]"
        try {
            val arr = org.json.JSONArray(json)
            for (i in 0 until arr.length()) {
                val o = arr.getJSONObject(i)
                bookmarks.add(o.getString("t") to o.getString("u"))
            }
        } catch (_: Exception) {}
    }

    private fun saveBookmarks() {
        val arr = org.json.JSONArray()
        for ((t, u) in bookmarks) arr.put(org.json.JSONObject().put("t", t).put("u", u))
        prefs.edit().putString("bookmarks", arr.toString()).apply()
    }

    private fun showBookmarks() {
        if (bookmarks.isEmpty()) {
            toast("收藏夹为空：浏览页面后点右上角 ★ 收藏")
            return
        }
        val titles = bookmarks.mapIndexed { i, (t, u) -> "⭐ $t\n     ${u.take(60)}" }.toTypedArray()
        val dialog = AlertDialog.Builder(this)
            .setTitle("📂 收藏夹（点按打开，长按删除）")
            .setItems(titles) { _, which ->
                val (t, u) = bookmarks[which]
                etAddress.setText(u)
                webView.loadUrl(u)
                toast("打开：$t")
            }
            .setPositiveButton("关闭", null)
            .create()
        dialog.listView.setOnItemLongClickListener { _, _, which, _ ->
            val removed = bookmarks.removeAt(which)
            saveBookmarks()
            dialog.dismiss()
            AlertDialog.Builder(this)
                .setMessage("已删除收藏：${removed.first}\n重新打开收藏夹查看。")
                .setPositiveButton("好", null).show()
            true
        }
        dialog.show()
    }

    // ================= 保存路径 =================

    /** 在所选目录中查找或创建 TXT 文件，返回其 Uri；未选目录返回 null */
    private fun fileInPickedDir(name: String): Pair<Uri, Boolean>? {
        val tree = saveDirUri ?: return null
        val dir = DocumentFile.fromTreeUri(this, tree) ?: return null
        val existing = dir.findFile(name)
        return if (existing != null && existing.canWrite()) {
            existing.uri to true
        } else {
            val created = dir.createFile("text/plain", name) ?: return null
            created.uri to false
        }
    }

    private fun go() {
        var url = etAddress.text.toString().trim()
        if (url.isEmpty()) return
        if (!url.startsWith("http://") && !url.startsWith("https://")) url = "https://$url"
        webView.loadUrl(url)
    }

    // ================= 抓取状态机 =================

    /** 开始抓取：从当前页面起，自动翻完全部分页 */
    private fun startGrab() {
        if (grabbing) return
        val bookName = etBookName.text.toString().trim().ifEmpty { "小说" }
        if (bookUri == null) bookUri = null // 每次换书名由 append 逻辑处理

        grabbing = true
        pageTexts.setLength(0)
        pageList.clear()
        visitedUrls.clear()
        pageTitle = webView.title ?: "未命名章节"
        visitedUrls.add(currentUrl())

        setStatus("🔍 正在抓取：$pageTitle")
        btnGrab.isEnabled = false

        extractCurrentPage(currentUrl())

        // 超时保护：如果 8 秒没动静就收尾
        handler.postDelayed({
            if (grabbing) {
                setStatus("⏱️ 等待超时，按已抓取内容收尾")
                finalizeChapter()
            }
        }, PAGE_LOAD_TIMEOUT_MS)
    }

    private fun currentUrl(): String = webView.url ?: ""

    private fun extractCurrentPage(url: String) {
        webView.evaluateJavascript(JS_GRAB) { result ->
            runOnUiThread {
                var text = result?.trim() ?: ""
                text = if (text == "\"\"" || text == "null") "" else unquoteJson(text)
                if (text.isNotEmpty()) {
                    pageList.add(text)
                    pageTexts.setLength(0)
                    pageTexts.append(pageList.joinToString("\n\n"))
                    setStatus("📄 已抓取第 ${visitedUrls.size} 页，正在检查下一页…")
                }
                resumeAware { findNextPage() }
            }
        }
    }

    private fun findNextPage() {
        webView.evaluateJavascript(JS_NEXT_PAGE) { result ->
            runOnUiThread {
                resumeAware {
                    val next = (result?.trim() ?: "").removeSurrounding("\"")
                    if (stopRequested || next.isEmpty()) {
                        finalizeChapter()
                    } else {
                        val key = next.substringBefore('#')
                        if (key in visitedUrls) {
                            finalizeChapter()
                        } else {
                            visitedUrls.add(key)
                            setStatus("➡️ 自动翻页 ${visitedUrls.size - 1} …")
                            webView.loadUrl(next)
                            // 重置超时保护
                            handler.removeCallbacksAndMessages(null)
                            handler.postDelayed({
                                if (grabbing) {
                                    setStatus("⏱️ 翻页超时，按已抓取内容收尾")
                                    finalizeChapter()
                                }
                            }, PAGE_LOAD_TIMEOUT_MS)
                        }
                    }
                }
            }
        }
    }

    /** 全部分页抓完：清洗并一次性写入 TXT */
    private fun finalizeChapter() {
        if (!grabbing) return
        grabbing = false
        stopRequested = false
        handler.removeCallbacksAndMessages(null)
        btnGrab.isEnabled = true

        if (pageList.isEmpty()) {
            setStatus("❌ 未识别到正文，本站结构不兼容")
            toast("未识别到正文，请尝试其他章节或书源")
            return
        }
        // 分页章节：每节写入详细小标题
        val raw = if (pageList.size > 1) {
            pageList.mapIndexed { i, p -> "──── $pageTitle · 第 ${i + 1} 节 ────\n\n$p" }
                .joinToString("\n\n")
        } else pageList[0]
        val pages = visitedUrls.size - 1
        val content = clean(raw, pageTitle)
        setStatus("💾 正在保存…（共 ${pages + 1} 页）")
        appendToTxt(content)
        setStatus("✅ 整章抓取完成：$pageTitle（${pages + 1} 页）")
    }

    // ================= 暂停 / 停止 / 目录 =================

    /** 暂停时挂起后续步骤，继续后恢复 */
    private fun resumeAware(cont: () -> Unit) {
        if (!paused || !(grabbing || bookGrabbing)) {
            cont()
            return
        }
        handler.postDelayed({ resumeAware(cont) }, 400)
    }

    private fun togglePause() {
        val btn = findViewById<ImageButton>(R.id.btnPause)
        if (!(grabbing || bookGrabbing)) {
            toast("当前没有进行中的下载")
            return
        }
        paused = !paused
        if (paused) {
            btn.setImageResource(android.R.drawable.ic_media_play)
            setStatus("⏸ 已暂停，点 ▶ 继续")
        } else {
            btn.setImageResource(android.R.drawable.ic_media_pause)
            setStatus("▶ 已继续下载")
        }
    }

    /** 打开下载目录：自选文件夹 → 文件管理器浏览；否则打开系统下载管理 */
    private fun openDownloadDir() {
        try {
            val dir = saveDirUri
            if (dir != null) {
                startActivity(Intent(Intent.ACTION_VIEW).apply {
                    setDataAndType(dir, DocumentsContract.Document.MIME_TYPE_DIR)
                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                })
            } else {
                startActivity(Intent(DownloadManager.ACTION_VIEW_DOWNLOADS))
            }
        } catch (e: Exception) {
            toast("无法打开目录：${e.message}")
        }
    }

    // ================= 清洗与保存 =================

    private fun clean(raw: String, chapterTitle: String): String {
        val body = raw
            .replace("\r", "")
            .replace("[ \\t\\u00a0]+".toRegex(), " ")
            .replace(" ?\\n ?".toRegex(), "\n")
            .split("\n")
            .map { it.trim() }
            .filter { it.isNotEmpty() }
            .filterNot {
                it.contains("广告") || it.contains("章节错误") || it.contains("最新章节") ||
                it.contains("请记住本书") || it.contains("首发域名") || it.contains("app阅读") ||
                it.contains("点击下一页", true) || it.contains("继续阅读")
            }
            .joinToString("\n\n")
            .replace("\\n{4,}".toRegex(), "\n\n\n")
        return "══════ $chapterTitle ══════\n\n$body\n\n"
    }

    private fun appendToTxt(content: String) {
        val name = (etBookName.text.toString().trim().ifEmpty { "小说" }) + ".txt"
        if (name != bookUriName) { bookUri = null; bookUriName = name } // 换书名后不写入旧文件
        try {
            // 优先使用用户自选目录（SAF）
            val picked = fileInPickedDir(name)
            if (picked != null) {
                val (uri, existed) = picked
                if (existed) {
                    contentResolver.openOutputStream(uri, "wa")?.use { write(it, content) }
                } else {
                    contentResolver.openOutputStream(uri)?.use { write(it, content) }
                }
                bookUri = uri
            } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                val resolver = contentResolver
                var uri = bookUri
                if (uri == null) {
                    uri = findExisting(name)
                    if (uri == null) {
                        val values = android.content.ContentValues().apply {
                            put(MediaStore.Downloads.DISPLAY_NAME, name)
                            put(MediaStore.Downloads.MIME_TYPE, "text/plain")
                            put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS)
                        }
                        uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
                    }
                    bookUri = uri
                }
                if (uri == null) { setStatus("❌ 创建文件失败"); return }
                resolver.openOutputStream(uri, "append")?.use { write(it, content) }
            } else {
                val dir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
                if (!dir.exists()) dir.mkdirs()
                val f = java.io.File(dir, name)
                bookUri = Uri.fromFile(f)
                java.io.FileOutputStream(f, true).use { write(it, content) }
            }
            val where = if (saveDirUri != null) "所选文件夹" else "Downloads"
            toast("✅ 已追加到 $where/$name")
        } catch (e: Exception) {
            setStatus("❌ 写入失败：${e.message}")
        }
    }

    private fun findExisting(name: String): Uri? {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return null
        contentResolver.query(
            MediaStore.Downloads.EXTERNAL_CONTENT_URI,
            arrayOf(MediaStore.MediaColumns._ID),
            "${MediaStore.MediaColumns.DISPLAY_NAME}=?",
            arrayOf(name), null
        )?.use { c ->
            if (c.moveToFirst()) {
                return Uri.parse("content://media/external/downloads/${c.getLong(0)}")
            }
        }
        return null
    }

    private fun write(os: OutputStream, content: String) {
        os.write(content.toByteArray(Charsets.UTF_8))
        os.flush()
    }

    private fun openTxt() {
        val name = (etBookName.text.toString().trim().ifEmpty { "小说" }) + ".txt"
        try {
            val uri: Uri
            val picked = fileInPickedDir(name)
            if (picked != null) {
                uri = picked.first
            } else {
                uri = findExisting(name)
                    ?: if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
                        bookUri ?: throw Exception("尚未抓取任何内容")
                    } else throw Exception("尚未抓取任何内容")
            }
            startActivity(Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, "text/plain")
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            })
        } catch (e: Exception) {
            toast(e.message ?: "无法打开")
        }
    }

    // ================= 工具 =================

    private fun setStatus(msg: String) {
        tvStatus.visibility = View.VISIBLE
        tvStatus.text = msg
    }

    private fun toast(msg: String) {
        Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()
    }

    private fun unquoteJson(s: String): String = try {
        org.json.JSONArray("[$s]").getString(0)
    } catch (_: Exception) {
        s
    }

    override fun onBackPressed() {
        if (webView.canGoBack()) webView.goBack() else super.onBackPressed()
    }

    override fun onDestroy() {
        handler.removeCallbacksAndMessages(null)
        super.onDestroy()
    }
}
