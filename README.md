# 小说抓取浏览器（NovelGrabber）

一个自带浏览器的 Android 应用：用内置 WebView 打开任意小说网站，翻到章节页后点「抓取本章 → TXT」，正文会被自动识别、清洗并**追加**保存到手机 `Downloads/书名.txt`，多章连续点即可合并成整本 TXT。

## 功能

- **内置浏览器**：地址栏、前进/后退、JS 支持，可正常浏览任意网站
- **一键抓取**：智能识别常见小说站正文容器（`#content`、`.showtxt` 等十余种选择器 + `<p>` 兜底）
- **正文清洗**：压缩空白、规范段落、自动过滤「广告 / 请记住本书 / 首发域名」等干扰行
- **追加保存**：按书名写入 `Downloads/书名.txt`（Android 10+ 走 MediaStore，无需存储权限）
- **查看成果**：点「打开 TXT 文件」用系统任意文本/阅读应用打开

## 如何编译成 APK

1. 电脑安装 [Android Studio](https://developer.android.com/studio)（含内置 SDK）
2. File → Open 打开本项目文件夹 `NovelGrabber`
3. 等待 Gradle 同步完成（首次会自动下载依赖）
4. 菜单 Build → Build Bundle(s)/APK(s) → **Build APK(s)**
5. APK 生成在 `app/build/outputs/apk/debug/app-debug.apk`，传到手机安装即可

> 修改包名/应用名：改 `app/build.gradle.kts` 中的 `applicationId` 与 `res/values/strings.xml` 中的 `app_name`。

## 使用流程

1. 打开 App → 在地址栏输入小说站网址 → Go
2. 在 `书名` 框填入书名（作为 TXT 文件名，只需填一次）
3. 翻到任意一章正文页 → 点「抓取本章 → TXT」
4. 浏览器内翻到下一章 → 再点一次抓取……全部章节按顺序追加进同一个 TXT
5. 随时点「打开 TXT 文件」查看已合并的内容

## 已知限制（合规提示）

- 仅用于抓取**有授权或免费公开**的内容；请尊重网站 robots 协议与版权，勿用于传播盗版
- 部分站点正文容器为动态加载或使用非常规命名，可能识别不到（会弹提示），此时可换源
- 分页章节（一章拆多页）需要每页各点一次
